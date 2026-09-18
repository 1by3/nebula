using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// <see cref="IControlPlane"/> for a worker or gateway: a mirror of the control plane the orchestrator hosts
    /// (<see cref="ControlPlaneHost"/>), reached over HTTP at the orchestrator's dashboard address.
    /// <para>
    /// A reader thread keeps one long-poll request open (<c>GET /api/control-plane?since=&lt;version&gt;</c>) and
    /// parses each document it gets; <see cref="Tick"/> swaps the parsed lists in on the main thread and raises
    /// <see cref="Changed"/>, so callers see the same threading as with any other control plane. Writes are queued
    /// and a sender thread posts them in order, batched when several are waiting; a write is never dropped while
    /// the orchestrator is unreachable, only delayed (up to <see cref="MaxQueuedWrites"/>, after which the oldest
    /// go). <see cref="Now"/> is the orchestrator's clock as of the last document plus the time since.
    /// </para>
    /// </summary>
    public sealed partial class RemoteControlPlane : IControlPlane
    {
        /// <summary>Seconds a read waits on the orchestrator for a change before it returns the unchanged document.</summary>
        public const int LongPollSeconds = 10;
        /// <summary>Seconds without a successful read after which the mirror reports itself disconnected.</summary>
        public const float DisconnectAfterSeconds = LongPollSeconds + 5f;
        public const float RetrySeconds = 1f;
        public const int MaxQueuedWrites = 10000;
        private const int MaxBatch = 64;

        private readonly string _baseUrl;
        private readonly string _token;
        private readonly HttpClient _http;
        /// <summary>Local clock usable from every thread (Unity's Time is main-thread only).</summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new object();
        private readonly Queue<string> _writes = new Queue<string>();
        private readonly ControlPlaneJson.OpWriter _op = new ControlPlaneJson.OpWriter();
        private Thread _reader, _sender;
        private volatile bool _running;
        private ControlPlaneJson.Snapshot _incoming;
        private long _version = -1;
        private double _lastReadAt = double.NegativeInfinity;
        private DateTime _serverNow = DateTime.UtcNow;
        private double _serverNowAtLocal;
        private volatile string _readError, _writeError;
        private string _loggedReadError, _loggedWriteError;
        private bool _loggedConnected;

        private readonly List<WorkerInfo> _workers = new List<WorkerInfo>();
        private readonly List<LeaseInfo> _leases = new List<LeaseInfo>();
        private readonly List<GatewayInfo> _gateways = new List<GatewayInfo>();
        private readonly Dictionary<string, string> _settings = new Dictionary<string, string>();

        /// <param name="url">The orchestrator's dashboard address, e.g. <c>http://10.0.1.2:7080/</c>.</param>
        /// <param name="token">Mesh token to present, or null.</param>
        public RemoteControlPlane(string url, string token = null)
        {
            _baseUrl = (url ?? "").TrimEnd('/');
            _token = string.IsNullOrEmpty(token) ? null : token;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(LongPollSeconds + 10) };
        }

        public string Url => _baseUrl;
        public bool IsConnected => _running && _clock.Elapsed.TotalSeconds - Volatile.Read(ref _lastReadAt) <= DisconnectAfterSeconds;
        public DateTime Now => _serverNow + TimeSpan.FromSeconds(_clock.Elapsed.TotalSeconds - _serverNowAtLocal);
        public event Action Changed;
        public IReadOnlyList<WorkerInfo> Workers => _workers;
        public IReadOnlyList<LeaseInfo> Leases => _leases;
        public IReadOnlyList<GatewayInfo> Gateways => _gateways;
        public IReadOnlyDictionary<string, string> Settings => _settings;
        /// <summary>Version of the document currently mirrored (-1 before the first).</summary>
        public long Version => _version;
        /// <summary>Writes waiting to be sent.</summary>
        public int PendingWrites { get { lock (_gate) return _writes.Count; } }

        public void Connect()
        {
            if (_running) return;
            if (string.IsNullOrEmpty(_baseUrl))
            {
                NebulaLog.Error("control plane: no orchestrator address (-nebula-control-plane <url>, or NebulaConfig.ControlPlaneUrl)");
                return;
            }
            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "nebula-control-plane-reader" };
            _sender = new Thread(SendLoop) { IsBackground = true, Name = "nebula-control-plane-sender" };
            _reader.Start();
            _sender.Start();
            NebulaLog.Info($"control plane: mirroring {_baseUrl}{ControlPlaneHost.Path}{(_token != null ? " (with mesh token)" : "")}");
        }

        public void Tick()
        {
            while (_sessionCallbacks.TryDequeue(out var sessionCallback)) sessionCallback();
            ControlPlaneJson.Snapshot incoming;
            lock (_gate)
            {
                incoming = _incoming;
                _incoming = null;
            }
            string readError = _readError, writeError = _writeError;
            if (readError != _loggedReadError)
            {
                _loggedReadError = readError;
                if (readError != null) NebulaLog.Warn($"control plane: reading {_baseUrl} failed: {readError}; mesh keeps running on its last known topology");
            }
            if (writeError != _loggedWriteError)
            {
                _loggedWriteError = writeError;
                if (writeError != null) NebulaLog.Warn($"control plane: writing to {_baseUrl} failed: {writeError}; writes are queued");
            }
            if (incoming == null) return;
            if (!_loggedConnected)
            {
                _loggedConnected = true;
                NebulaLog.Info($"control plane: connected to {_baseUrl} (version {incoming.Version})");
            }
            _serverNow = incoming.Now;
            _serverNowAtLocal = _clock.Elapsed.TotalSeconds;
            if (incoming.Version == _version) return; // an unchanged document after a full wait: only the clock moved
            _version = incoming.Version;
            _workers.Clear(); _workers.AddRange(incoming.Workers);
            _leases.Clear(); _leases.AddRange(incoming.Leases);
            _gateways.Clear(); _gateways.AddRange(incoming.Gateways);
            _settings.Clear();
            foreach (var kv in incoming.Settings) _settings[kv.Key] = kv.Value;
            Changed?.Invoke();
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;
            lock (_gate) Monitor.PulseAll(_gate);
            // Give the sender a moment to flush an Unregister that was queued right before this, then cut the
            // reader's long poll short.
            _sender?.Join(1000);
            try { _http.CancelPendingRequests(); } catch { }
            _http.Dispose();
        }

        // ---------------------------------------------------------------------------------------- writes

        private void Enqueue(string op)
        {
            lock (_gate)
            {
                if (_writes.Count >= MaxQueuedWrites) _writes.Dequeue();
                _writes.Enqueue(op);
                Monitor.PulseAll(_gate);
            }
        }

        public void RegisterWorker(string workerId, uint workerIndex, string address, ushort port) =>
            Enqueue(_op.Op(ControlPlaneJson.RegisterWorker).Arg("workerId", workerId).Arg("workerIndex", workerIndex).Arg("address", address).Arg("port", (long)port).End());

        public void HeartbeatWorker(string workerId, string status, in WorkerStats s) =>
            Enqueue(_op.Op(ControlPlaneJson.HeartbeatWorker).Arg("workerId", workerId).Arg("status", status)
                .Arg("tickCount", s.TickCount).Arg("tickMs", s.TickMs).Arg("entityCount", s.EntityCount).Arg("authoritativeCount", s.AuthoritativeCount)
                .Arg("ghostCount", s.GhostCount).Arg("playerCount", s.PlayerCount).Arg("botCount", s.BotCount).Arg("serverDrivenCount", s.ServerDrivenCount).End());

        public void UnregisterWorker(string workerId) => Enqueue(_op.Op(ControlPlaneJson.UnregisterWorker).Arg("workerId", workerId).End());
        public void RegisterGateway(string gatewayId, string address, ushort port, uint incarnation = 0) =>
            Enqueue(_op.Op(ControlPlaneJson.RegisterGateway).Arg("gatewayId", gatewayId).Arg("address", address).Arg("port", (long)port).Arg("incarnation", (long)incarnation).End());
        public void HeartbeatGateway(string gatewayId, in GatewayStats s) =>
            Enqueue(_op.Op(ControlPlaneJson.HeartbeatGateway).Arg("gatewayId", gatewayId)
                .Arg("pendingJoins", (long)s.PendingJoins).Arg("activeClients", (long)s.ActiveClients).Arg("joiningClients", (long)s.JoiningClients).Arg("reconnectingClients", (long)s.ReconnectingClients)
                .Arg("packetsIn", s.PacketsInPerSecond).Arg("packetsOut", s.PacketsOutPerSecond).Arg("bytesIn", s.BytesInPerSecond).Arg("bytesOut", s.BytesOutPerSecond)
                .Arg("workerBytesIn", s.WorkerBytesInPerSecond).Arg("workerBytesOut", s.WorkerBytesOutPerSecond).Arg("cpu", s.Cpu).Arg("memoryBytes", (long)s.MemoryBytes)
                .Arg("loopLagMs", s.LoopLagMs).Arg("workerConnections", (long)s.WorkerConnections).Arg("ready", s.Ready).Arg("draining", s.Draining).End());
        public void UnregisterGateway(string gatewayId) => Enqueue(_op.Op(ControlPlaneJson.UnregisterGateway).Arg("gatewayId", gatewayId).End());
        public void SetGatewayDraining(string gatewayId, bool draining) => Enqueue(_op.Op(ControlPlaneJson.SetGatewayDraining).Arg("gatewayId", gatewayId).Arg("draining", draining).End());
        public void HeartbeatOrchestrator(string orchestratorId, uint desiredWorkers) =>
            Enqueue(_op.Op(ControlPlaneJson.HeartbeatOrchestrator).Arg("orchestratorId", orchestratorId).Arg("desiredWorkers", desiredWorkers).End());
        public void SetSetting(string key, string value) => Enqueue(_op.Op(ControlPlaneJson.SetSetting).Arg("key", key).Arg("value", value ?? "").End());
        public void EnsureContainer(string containerId) => Enqueue(_op.Op(ControlPlaneJson.EnsureContainer).Arg("containerId", containerId).End());
        public void EnsureRuntimeContainer(string containerId, Bounds bounds, string workerId, InstanceContainerInfo instance = null) =>
            Enqueue(_op.Op(ControlPlaneJson.EnsureRuntimeContainer).Arg("containerId", containerId).Arg("workerId", workerId ?? "").Arg("center", bounds.center).Arg("size", bounds.size).Arg("instance", InstanceContainerInfo.Encode(instance)).End());
        public void TouchContainer(string containerId) => Enqueue(_op.Op(ControlPlaneJson.TouchContainer).Arg("containerId", containerId).End());
        public void AssignContainer(string containerId, string workerId) => Enqueue(_op.Op(ControlPlaneJson.AssignContainer).Arg("containerId", containerId).Arg("workerId", workerId).End());
        public void PinContainer(string containerId, string workerId) => Enqueue(_op.Op(ControlPlaneJson.PinContainer).Arg("containerId", containerId).Arg("workerId", workerId).End());
        public void SetLeaseState(string containerId, string state) => Enqueue(_op.Op(ControlPlaneJson.SetLeaseState).Arg("containerId", containerId).Arg("state", state).End());
        public void SetContainerHint(string containerId, in ContainerHint hint) => Enqueue(_op.Op(ControlPlaneJson.SetContainerHint).Arg("containerId", containerId).Arg("hint", hint.ToString()).End());
        public void ReleaseContainer(string containerId) => Enqueue(_op.Op(ControlPlaneJson.ReleaseContainer).Arg("containerId", containerId).End());
        public void RemoveContainer(string containerId) => Enqueue(_op.Op(ControlPlaneJson.RemoveContainer).Arg("containerId", containerId).End());
        public void ResetControlPlane() => Enqueue(_op.Op(ControlPlaneJson.ResetControlPlane).End());

        // ---------------------------------------------------------------------------------------- threads

        private void ReadLoop()
        {
            long since = -1;
            while (_running)
            {
                try
                {
                    string url = $"{_baseUrl}{ControlPlaneHost.Path}?since={since.ToString(CultureInfo.InvariantCulture)}&wait={LongPollSeconds}";
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (_token != null) req.Headers.TryAddWithoutValidation(ControlPlaneHost.TokenHeader, _token);
                        using (var resp = _http.SendAsync(req).GetAwaiter().GetResult())
                        {
                            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Trim(body)}");
                            var snapshot = ControlPlaneJson.Parse(body);
                            since = snapshot.Version;
                            Volatile.Write(ref _lastReadAt, _clock.Elapsed.TotalSeconds);
                            lock (_gate) _incoming = snapshot;
                            _readError = null;
                        }
                    }
                }
                catch (Exception e)
                {
                    if (!_running) return;
                    _readError = e is HttpRequestException || e is FormatException ? e.Message : e.GetType().Name + ": " + e.Message;
                    Sleep(RetrySeconds);
                }
            }
        }

        private void SendLoop()
        {
            var batch = new List<string>(MaxBatch);
            while (true)
            {
                batch.Clear();
                lock (_gate)
                {
                    while (_writes.Count == 0 && _running) Monitor.Wait(_gate);
                    if (_writes.Count == 0) return; // disposed with nothing left to send
                    while (_writes.Count > 0 && batch.Count < MaxBatch) batch.Add(_writes.Dequeue());
                }
                string body = ControlPlaneJson.WriteBatch(batch);
                while (true)
                {
                    try
                    {
                        using (var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + ControlPlaneHost.Path))
                        {
                            if (_token != null) req.Headers.TryAddWithoutValidation(ControlPlaneHost.TokenHeader, _token);
                            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                            using (var resp = _http.SendAsync(req).GetAwaiter().GetResult())
                            {
                                if (resp.IsSuccessStatusCode) { _writeError = null; break; }
                                string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                // A rejected batch (bad request, wrong token) will not get better by retrying it.
                                if ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 401) { _writeError = $"HTTP {(int)resp.StatusCode}: {Trim(text)} (batch dropped)"; break; }
                                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Trim(text)}");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        if (!_running) return;
                        _writeError = e.Message;
                        Sleep(RetrySeconds);
                    }
                }
            }
        }

        private void Sleep(float seconds)
        {
            lock (_gate) { if (_running) Monitor.Wait(_gate, TimeSpan.FromSeconds(seconds)); }
        }

        private static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 200 ? s.Substring(0, 200) + "..." : s;
    }
}
