using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nebula
{
    /// <summary>
    /// <see cref="IPersistenceStore"/> for a worker: every save and load goes to the orchestrator, which owns the
    /// real store (<see cref="PersistenceHost"/> over the dashboard address, the same one the control plane uses).
    /// <para>
    /// Saves are coalesced per key and posted in batches from a sender thread a fraction of a second after they are
    /// issued; a batch the orchestrator cannot take is kept and retried, so nothing is lost while it is away.
    /// Loads run one request each on the thread pool, retried until they succeed, and their answers are handed back
    /// from <see cref="Tick"/> on the main thread like every other store. <see cref="IsConnected"/> and
    /// <see cref="KnownCount"/> come from a status probe every few seconds.
    /// </para>
    /// </summary>
    public sealed class RemotePersistenceStore : IPersistenceStore
    {
        /// <summary>Seconds saves are gathered before a batch is posted.</summary>
        public const float FlushIntervalSeconds = 0.25f;
        public const float StatusIntervalSeconds = 5f;
        public const float RetrySeconds = 1f;
        private const int MaxBatch = 256;

        private readonly string _baseUrl;
        private readonly string _token;
        private readonly HttpClient _http;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new object();
        private readonly Dictionary<string, PersistedEntityRecord> _pendingSaves = new Dictionary<string, PersistedEntityRecord>();
        private readonly List<string> _pendingDeletes = new List<string>();
        private bool _pendingClear;
        // Every write bumps _writeSeq; _flushedSeq trails it and catches up when a batch that emptied the queue is posted.
        private long _writeSeq, _flushedSeq;
        private readonly List<(long Seq, Action Done)> _barriers = new List<(long, Action)>();
        private readonly List<Action> _callbacks = new List<Action>();
        private readonly List<Action> _draining = new List<Action>();
        private Thread _sender;
        private volatile bool _running;
        private volatile bool _connected;
        private volatile int _knownCount = -1;
        private volatile string _error;
        private string _loggedError;
        private bool _loggedConnected;

        public RemotePersistenceStore(string url, string token = null)
        {
            _baseUrl = (url ?? "").TrimEnd('/');
            _token = string.IsNullOrEmpty(token) ? null : token;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        public string Url => _baseUrl;
        public bool IsConnected => _running && _connected;
        public string Backend => "remote";
        public int KnownCount => _knownCount;
        /// <summary>Saves and deletes waiting for the next batch.</summary>
        public int PendingWrites { get { lock (_gate) return _pendingSaves.Count + _pendingDeletes.Count; } }

        public void Connect()
        {
            if (_running) return;
            if (string.IsNullOrEmpty(_baseUrl))
            {
                NebulaLog.Error("persistence: no orchestrator address for the remote store (-nebula-control-plane <url>)");
                return;
            }
            _running = true;
            _sender = new Thread(SendLoop) { IsBackground = true, Name = "nebula-persistence-sender" };
            _sender.Start();
            NebulaLog.Info($"persistence: remote store at {_baseUrl}{PersistenceHost.Prefix}");
        }

        public void Tick()
        {
            string error = _error;
            if (error != _loggedError)
            {
                _loggedError = error;
                if (error != null) NebulaLog.Warn($"persistence: {_baseUrl} failed: {error}; writes are queued and loads retried");
            }
            if (_connected && !_loggedConnected)
            {
                _loggedConnected = true;
                NebulaLog.Info($"persistence: connected to {_baseUrl} ({_knownCount} record(s) on the orchestrator)");
            }
            lock (_gate)
            {
                if (_callbacks.Count == 0) return;
                _draining.AddRange(_callbacks);
                _callbacks.Clear();
            }
            for (int i = 0; i < _draining.Count; i++)
            {
                try { _draining[i](); }
                catch (Exception e) { NebulaLog.Error($"persistence callback: {e}"); }
            }
            _draining.Clear();
        }

        public void Save(PersistedEntityRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Key)) return;
            lock (_gate)
            {
                _writeSeq++;
                _pendingSaves[record.Key] = record.Clone();
                _pendingDeletes.Remove(record.Key);
                Monitor.PulseAll(_gate);
            }
        }

        public void Delete(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                _writeSeq++;
                _pendingSaves.Remove(key);
                if (!_pendingDeletes.Contains(key)) _pendingDeletes.Add(key);
                Monitor.PulseAll(_gate);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _pendingSaves.Clear();
                _pendingDeletes.Clear();
                _writeSeq++;
                _pendingClear = true;
                Monitor.PulseAll(_gate);
            }
        }

        public void WhenWritten(Action onWritten)
        {
            if (onWritten == null) return;
            lock (_gate)
            {
                if (_flushedSeq >= _writeSeq) _callbacks.Add(onWritten);
                else _barriers.Add((_writeSeq, onWritten));
            }
        }

        public void Load(string key, Action<PersistedEntityRecord> onLoaded)
        {
            if (onLoaded == null) return;
            Fetch($"{PersistenceHost.Prefix}/record?key={Uri.EscapeDataString(key ?? "")}", body =>
            {
                var record = PersistedRecordJson.ParseOne(body);
                Deliver(() => onLoaded(record));
            });
        }

        public void LoadContainer(string containerId, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded)
        {
            if (onLoaded == null) return;
            Fetch($"{PersistenceHost.Prefix}/container?id={Uri.EscapeDataString(containerId ?? "")}", body =>
            {
                var records = PersistedRecordJson.ParseList(body);
                Deliver(() => onLoaded(records));
            });
        }

        public void LoadCarried(string carrierKey, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded)
        {
            if (onLoaded == null) return;
            Fetch($"{PersistenceHost.Prefix}/carried?key={Uri.EscapeDataString(carrierKey ?? "")}", body =>
            {
                var records = PersistedRecordJson.ParseList(body);
                Deliver(() => onLoaded(records));
            });
        }

        /// <summary>Fetches every record and filters here: the predicate cannot travel. For tools, not the restore path.</summary>
        public void LoadWhere(Func<PersistedEntityRecord, bool> predicate, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded)
        {
            if (onLoaded == null) return;
            Fetch($"{PersistenceHost.Prefix}/all", body =>
            {
                var all = PersistedRecordJson.ParseList(body);
                var hits = new List<PersistedEntityRecord>();
                foreach (var r in all) if (predicate == null || predicate(r)) hits.Add(r);
                Deliver(() => onLoaded(hits));
            });
        }

        /// <summary>Counted on the orchestrator (<c>GET /api/store/count</c>): only the number travels, never the records.</summary>
        public void CountRecords(string scopeKey, string containerId, Action<int> onCounted)
        {
            if (onCounted == null) return;
            string path = $"{PersistenceHost.Prefix}/count?scope={Uri.EscapeDataString(scopeKey ?? "")}&container={Uri.EscapeDataString(containerId ?? "")}";
            Fetch(path, body =>
            {
                int count = (int)ControlPlaneJson.Num(body, "count");
                Deliver(() => onCounted(count));
            });
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;
            lock (_gate) Monitor.PulseAll(_gate);
            // Let the sender post the last checkpoints of a shutting-down worker.
            _sender?.Join(2000);
            try { _http.CancelPendingRequests(); } catch { }
            _http.Dispose();
            lock (_gate) _callbacks.Clear();
        }

        // ---------------------------------------------------------------------------------------- threads

        private void Deliver(Action callback)
        {
            lock (_gate) _callbacks.Add(callback);
        }

        /// <summary>GET <paramref name="path"/> on the thread pool, retrying until it succeeds, then parse and hand back.</summary>
        private void Fetch(string path, Action<Dictionary<string, object>> onBody)
        {
            Task.Run(async () =>
            {
                while (_running)
                {
                    try
                    {
                        using (var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path))
                        {
                            if (_token != null) req.Headers.TryAddWithoutValidation(ControlPlaneHost.TokenHeader, _token);
                            using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                            {
                                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Trim(text)}");
                                if (!PersistenceJson.TryParseObject(text, out var body, out string error)) throw new FormatException(error);
                                onBody(body);
                                _error = null;
                                return;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        if (!_running) return;
                        _error = $"load {path}: {e.Message}";
                        await Task.Delay(TimeSpan.FromSeconds(RetrySeconds)).ConfigureAwait(false);
                    }
                }
            });
        }

        private void SendLoop()
        {
            double nextStatus = 0;
            var saves = new List<PersistedEntityRecord>(MaxBatch);
            var deletes = new List<string>();
            while (_running)
            {
                double now = _clock.Elapsed.TotalSeconds;
                if (now >= nextStatus)
                {
                    nextStatus = now + StatusIntervalSeconds;
                    Probe();
                }
                bool clear;
                long takenSeq;
                lock (_gate)
                {
                    if (_pendingSaves.Count == 0 && _pendingDeletes.Count == 0 && !_pendingClear)
                    {
                        Monitor.Wait(_gate, TimeSpan.FromSeconds(Math.Max(0.05, nextStatus - _clock.Elapsed.TotalSeconds)));
                        continue;
                    }
                }
                // Gather a little longer so a checkpoint pass lands in one request.
                Thread.Sleep(TimeSpan.FromSeconds(FlushIntervalSeconds));
                saves.Clear();
                deletes.Clear();
                lock (_gate)
                {
                    clear = _pendingClear;
                    _pendingClear = false;
                    deletes.AddRange(_pendingDeletes);
                    _pendingDeletes.Clear();
                    foreach (var kv in _pendingSaves)
                    {
                        saves.Add(kv.Value);
                        if (saves.Count >= MaxBatch) break;
                    }
                    foreach (var r in saves) _pendingSaves.Remove(r.Key);
                    takenSeq = _pendingSaves.Count == 0 ? _writeSeq : _flushedSeq;
                }
                if (clear && !Post(PersistenceHost.Prefix + "/clear", "{}")) { lock (_gate) _pendingClear = true; Requeue(saves, deletes); Sleep(RetrySeconds); continue; }
                if (deletes.Count > 0)
                {
                    var sb = new StringBuilder("{\"keys\":[");
                    for (int i = 0; i < deletes.Count; i++) { if (i > 0) sb.Append(','); sb.Append(JsonWriter.Quote(deletes[i])); }
                    sb.Append("]}");
                    if (!Post(PersistenceHost.Prefix + "/delete", sb.ToString()))
                    {
                        Requeue(saves, deletes);
                        Sleep(RetrySeconds);
                        continue;
                    }
                }
                if (saves.Count > 0 && !Post(PersistenceHost.Prefix + "/save", PersistedRecordJson.WriteList(saves)))
                {
                    // Put them back unless a newer checkpoint of the same entity arrived meanwhile.
                    Requeue(saves, null);
                    Sleep(RetrySeconds);
                    continue;
                }
                lock (_gate)
                {
                    if (takenSeq > _flushedSeq) _flushedSeq = takenSeq;
                    for (int i = _barriers.Count - 1; i >= 0; i--)
                    {
                        if (_barriers[i].Seq > _flushedSeq) continue;
                        _callbacks.Add(_barriers[i].Done);
                        _barriers.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>Put a batch that was not delivered back, unless something newer for the same key arrived meanwhile.</summary>
        private void Requeue(List<PersistedEntityRecord> saves, List<string> deletes)
        {
            lock (_gate)
            {
                if (deletes != null) foreach (var k in deletes) if (!_pendingSaves.ContainsKey(k) && !_pendingDeletes.Contains(k)) _pendingDeletes.Add(k);
                foreach (var r in saves) if (!_pendingSaves.ContainsKey(r.Key) && !_pendingDeletes.Contains(r.Key)) _pendingSaves[r.Key] = r;
            }
        }

        private bool Post(string path, string body)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path))
                {
                    if (_token != null) req.Headers.TryAddWithoutValidation(ControlPlaneHost.TokenHeader, _token);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = _http.SendAsync(req).GetAwaiter().GetResult())
                    {
                        if (resp.IsSuccessStatusCode) { _error = null; return true; }
                        string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        _error = $"{path}: HTTP {(int)resp.StatusCode}: {Trim(text)}";
                        // A malformed or unauthorized batch does not get better with time; keeping it would block every later one.
                        return (int)resp.StatusCode == 400 || (int)resp.StatusCode == 401;
                    }
                }
            }
            catch (Exception e)
            {
                if (_running) _error = $"{path}: {e.Message}";
                return false;
            }
        }

        private void Probe()
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + PersistenceHost.Prefix + "/status"))
                {
                    if (_token != null) req.Headers.TryAddWithoutValidation(ControlPlaneHost.TokenHeader, _token);
                    using (var resp = _http.SendAsync(req).GetAwaiter().GetResult())
                    {
                        string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Trim(text)}");
                        if (!PersistenceJson.TryParseObject(text, out var body, out string error)) throw new FormatException(error);
                        _knownCount = (int)ControlPlaneJson.Num(body, "count");
                        _connected = ControlPlaneJson.Bool(body, "connected");
                        _error = null;
                    }
                }
            }
            catch (Exception e)
            {
                _connected = false;
                if (_running) _error = "status: " + e.Message;
            }
        }

        private void Sleep(float seconds)
        {
            lock (_gate) { if (_running) Monitor.Wait(_gate, TimeSpan.FromSeconds(seconds)); }
        }

        private static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 200 ? s.Substring(0, 200) + "..." : s;
    }
}
