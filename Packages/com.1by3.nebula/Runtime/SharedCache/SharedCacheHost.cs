using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Nebula
{
    /// <summary>
    /// Serves the orchestrator's <see cref="ISharedCacheStore"/> to workers (<see cref="RemoteSharedCacheClient"/>) over
    /// the orchestrator's HTTP server, beside <see cref="PersistenceHost"/>:
    /// <list type="bullet">
    /// <item><c>GET /cache/{key}</c>: the object as <c>application/octet-stream</c> with its SHA-256 in <c>X-Nebula-Checksum</c>; 404 when there is none</item>
    /// <item><c>PUT /cache/{key}</c>: the object as the body, with its SHA-256 in <c>X-Nebula-Checksum</c>. 201 when it was stored, 200 when the key was already stored. 400 for a bad key, a missing or wrong checksum, or an empty body; 413 over the size limit</item>
    /// </list>
    /// Requests carry the mesh token in <c>X-Nebula-Token</c> when the mesh has one. Unlike the persistence
    /// endpoints, a request is answered on the HTTP server's own thread and never waits for the orchestrator's main
    /// thread, so a worker that is filling a cold cache cannot hold up the control plane. To keep it from starving the
    /// control plane of the HTTP server's threads instead, at most <see cref="MaxConcurrent"/> requests run at once;
    /// the rest get 503 and the worker treats that as a miss.
    /// </summary>
    public sealed class SharedCacheHost
    {
        /// <summary>The path prefix the cache is served under.</summary>
        public const string Prefix = "/cache";
        /// <summary>The header that carries an object's checksum in both directions.</summary>
        public const string ChecksumHeader = "X-Nebula-Checksum";
        /// <summary>Seconds between sweeps that remove expired keys.</summary>
        public const float SweepIntervalSeconds = 3600f;
        /// <summary>Seconds after start before the first sweep.</summary>
        public const float FirstSweepSeconds = 60f;

        private readonly ISharedCacheStore _store;
        private readonly string _token;
        private readonly SharedCacheCounters _counters = new SharedCacheCounters();
        private int _inFlight;
        private double _nextSweep = -1;
        private int _sweeping;

        /// <param name="store">Where objects are kept. Owned by the caller.</param>
        /// <param name="token">The mesh token requests must present; null or empty for none.</param>
        /// <param name="maxObjectBytes">Largest object accepted, in bytes.</param>
        /// <param name="maxConcurrent">Most requests served at once.</param>
        /// <param name="expiryDays">Days an object may go unread before a sweep removes it; 0 or less keeps everything.</param>
        public SharedCacheHost(ISharedCacheStore store, string token, int maxObjectBytes = SharedCacheKey.DefaultMaxObjectBytes, int maxConcurrent = 8, int expiryDays = SharedCacheKey.DefaultExpiryDays)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _token = string.IsNullOrEmpty(token) ? null : token;
            MaxObjectBytes = maxObjectBytes > 0 ? maxObjectBytes : SharedCacheKey.DefaultMaxObjectBytes;
            MaxConcurrent = Math.Max(1, maxConcurrent);
            ExpiryDays = expiryDays;
        }

        public ISharedCacheStore Store => _store;

        /// <summary>Largest object accepted, in bytes.</summary>
        public int MaxObjectBytes { get; }

        /// <summary>Most requests served at once.</summary>
        public int MaxConcurrent { get; }

        /// <summary>Days an object may go unread before a sweep removes it; 0 or less keeps everything.</summary>
        public int ExpiryDays { get; }

        /// <summary>What this host has served since it started.</summary>
        public SharedCacheStats Stats => _counters.Snapshot();

        /// <summary>
        /// Handle <paramref name="req"/> when it addresses the cache. False for any other request. Safe to call from
        /// several threads: it blocks only on the store.
        /// </summary>
        public bool TryHandle(OrchestratorHttpServer.Request req, out OrchestratorHttpServer.Response response)
        {
            response = default;
            string path = req.Path.TrimEnd('/');
            if (!path.StartsWith(Prefix + "/", StringComparison.Ordinal)) return false;
            if (_token != null && req.Token != _token) { response = OrchestratorHttpServer.Response.Error(401, "missing or wrong mesh token"); return true; }
            string key = path.Substring(Prefix.Length + 1);
            if (!SharedCacheKey.TryValidate(key, out string invalid)) { response = OrchestratorHttpServer.Response.Error(400, invalid); return true; }
            bool get = req.Method == "GET", put = req.Method == "PUT";
            if (!get && !put) { response = OrchestratorHttpServer.Response.Error(405, "the cache answers GET and PUT"); return true; }
            if (Interlocked.Increment(ref _inFlight) > MaxConcurrent)
            {
                Interlocked.Decrement(ref _inFlight);
                response = OrchestratorHttpServer.Response.Error(503, $"the cache is serving {MaxConcurrent} requests already; retry later");
                return true;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                response = get ? Get(key, watch) : Put(key, req, watch);
            }
            catch (Exception e)
            {
                _counters.Error();
                NebulaLog.Warn($"shared cache: {req.Method} {key} failed: {e.GetBaseException().Message}");
                response = OrchestratorHttpServer.Response.Error(503, "the cache store could not complete the request");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
            return true;
        }

        private OrchestratorHttpServer.Response Get(string key, System.Diagnostics.Stopwatch watch)
        {
            if (!_store.TryGet(key, out byte[] bytes, out string checksum))
            {
                _counters.Miss();
                NebulaLog.Debugf($"shared cache: GET {key}: miss, {watch.ElapsedMilliseconds} ms");
                return OrchestratorHttpServer.Response.Error(404, "not cached");
            }
            _counters.Hit(bytes.Length);
            NebulaLog.Debugf($"shared cache: GET {key}: hit, {bytes.Length} bytes, {watch.ElapsedMilliseconds} ms");
            return OrchestratorHttpServer.Response.Binary(200, bytes, checksum);
        }

        private OrchestratorHttpServer.Response Put(string key, OrchestratorHttpServer.Request req, System.Diagnostics.Stopwatch watch)
        {
            byte[] bytes = req.BodyBytes;
            if (bytes == null || bytes.Length == 0) return OrchestratorHttpServer.Response.Error(400, "the object is empty");
            if (bytes.Length > MaxObjectBytes) return OrchestratorHttpServer.Response.Error(413, $"the object is {bytes.Length} bytes; the limit is {MaxObjectBytes}");
            if (string.IsNullOrEmpty(req.Checksum)) return OrchestratorHttpServer.Response.Error(400, "the " + ChecksumHeader + " header is required");
            string actual = SharedCacheKey.Checksum(bytes);
            if (!SharedCacheKey.SameChecksum(req.Checksum, actual))
                return OrchestratorHttpServer.Response.Error(400, "the object does not match its " + ChecksumHeader + " header");
            bool stored = _store.Put(key, bytes, actual);
            if (stored) _counters.Put(bytes.Length); else _counters.PutExisting();
            NebulaLog.Debugf($"shared cache: PUT {key}: {(stored ? "stored" : "already stored")}, {bytes.Length} bytes, {watch.ElapsedMilliseconds} ms");
            return OrchestratorHttpServer.Response.Json(stored ? 201 : 200, $"{{\"ok\":true,\"stored\":{(stored ? "true" : "false")}}}");
        }

        /// <summary>
        /// Main thread, every frame: start a sweep when one is due. The sweep runs on the thread pool, first
        /// <see cref="FirstSweepSeconds"/> after the first call and then every <see cref="SweepIntervalSeconds"/>.
        /// </summary>
        public void Tick(double nowSeconds)
        {
            if (ExpiryDays <= 0) return;
            if (_nextSweep < 0) { _nextSweep = nowSeconds + FirstSweepSeconds; return; }
            if (nowSeconds < _nextSweep) return;
            _nextSweep = nowSeconds + SweepIntervalSeconds;
            if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try { Sweep(); }
                catch (Exception e) { NebulaLog.Warn($"shared cache: sweep failed: {e.GetBaseException().Message}"); }
                finally { Interlocked.Exchange(ref _sweeping, 0); }
            });
        }

        /// <summary>
        /// Remove every object that has not been read for <see cref="ExpiryDays"/> days. Returns how many were removed.
        /// Called by <see cref="Tick"/>; call it directly to sweep at once.
        /// </summary>
        public int Sweep()
        {
            if (ExpiryDays <= 0) return 0;
            int removed = _store.RemoveUnreadSince(DateTime.UtcNow - TimeSpan.FromDays(ExpiryDays));
            if (removed > 0) NebulaLog.Info($"shared cache: removed {removed} object(s) not read for {ExpiryDays} day(s)");
            return removed;
        }

        /// <summary>Remove every object (the dashboard's and the dev reset's way to start over). Persisted entities are not touched.</summary>
        public int Clear()
        {
            int removed = _store.Clear();
            NebulaLog.Warn($"shared cache: cleared, {removed} object(s) removed");
            return removed;
        }

        /// <summary>The cache's size, as the dashboard and the status line report it.</summary>
        public string Describe() =>
            $"{_store.Backend}: {_store.Count().ToString(CultureInfo.InvariantCulture)} object(s), {_store.TotalBytes().ToString(CultureInfo.InvariantCulture)} bytes";
    }
}
