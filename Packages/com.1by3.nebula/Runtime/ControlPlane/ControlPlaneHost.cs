using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// The control plane as the orchestrator hosts it: a <see cref="LocalControlPlane"/> that is the source of
    /// truth, served to workers and gateways over the orchestrator's HTTP server and kept in an
    /// <see cref="IControlPlaneStorage"/> between runs.
    /// <list type="bullet">
    /// <item><c>GET /api/control-plane?since=&lt;version&gt;&amp;wait=&lt;seconds&gt;</c> answers with the current
    /// document (<see cref="ControlPlaneJson"/>) as soon as its version is above <c>since</c>, or when
    /// <c>wait</c> seconds pass. Subscribers (<see cref="RemoteControlPlane"/>) keep one such request open.</item>
    /// <item><c>POST /api/control-plane</c> with <c>{"ops":[...]}</c> applies writes in order on the main thread
    /// (through <see cref="TryHandle"/>, from the orchestrator's command pump), so the orchestrator sees them in the
    /// same tick it would see its own.</item>
    /// </list>
    /// With a mesh token every request carries it in the <c>X-Nebula-Token</c> header; without one the API is open,
    /// which is fine on a private network or a development machine. Reads are answered on the listener thread from
    /// the last published document, so the orchestrator's main thread never waits on a subscriber.
    /// </summary>
    public sealed class ControlPlaneHost : IControlPlane
    {
        public const string Path = "/api/control-plane";
        public const string TokenHeader = "X-Nebula-Token";
        /// <summary>Longest a subscriber may ask a read to wait for a change.</summary>
        public const int MaxWaitSeconds = 30;
        /// <summary>Seconds between writes to storage while the control plane keeps changing.</summary>
        public float SaveIntervalSeconds = 1f;

        private readonly LocalControlPlane _plane = new LocalControlPlane();
        private readonly IControlPlaneStorage _storage;
        private readonly string _token;
        private readonly bool _restore;
        private readonly object _gate = new object();
        private string _document = "{}";
        private long _documentVersion = -1;
        private long _published = -1;
        private bool _storageDirty;
        private double _nextSave;
        private int _saving;
        private string _storageError;

        /// <param name="storage">Where the document is kept between runs; null keeps it in memory only.</param>
        /// <param name="token">Shared secret subscribers must present; null or empty for none.</param>
        /// <param name="restore">Read the stored document at <see cref="Connect"/>. The orchestrator resets the plane at its first tick unless <c>-nebula-reset false</c>, so this only matters then.</param>
        public ControlPlaneHost(IControlPlaneStorage storage, string token, bool restore = true)
        {
            _storage = storage ?? new MemoryControlPlaneStorage();
            _token = string.IsNullOrEmpty(token) ? null : token;
            _restore = restore;
        }

        /// <summary>The state machine itself, for tests.</summary>
        public LocalControlPlane Plane => _plane;
        public string StorageBackend => _storage.Backend;
        public bool HasToken => _token != null;
        /// <summary>Version of the document subscribers currently receive.</summary>
        public long PublishedVersion => _documentVersion;

        // ---------------------------------------------------------------------------------------- IControlPlane

        public bool IsConnected => _plane.IsConnected;
        public DateTime Now => _plane.Now;
        public event Action Changed { add => _plane.Changed += value; remove => _plane.Changed -= value; }
        public IReadOnlyList<WorkerInfo> Workers => _plane.Workers;
        public IReadOnlyList<LeaseInfo> Leases => _plane.Leases;
        public IReadOnlyList<GatewayInfo> Gateways => _plane.Gateways;
        public IReadOnlyDictionary<string, string> Settings => _plane.Settings;

        public void Connect()
        {
            if (_restore)
            {
                try
                {
                    string stored = _storage.Load();
                    if (!string.IsNullOrEmpty(stored))
                    {
                        var snapshot = ControlPlaneJson.Parse(stored);
                        _plane.Import(snapshot);
                        NebulaLog.Info($"control plane: restored from {_storage.Backend} storage ({snapshot.Workers.Count} worker(s), {snapshot.Leases.Count} lease(s), {snapshot.Settings.Count} setting(s))");
                    }
                }
                catch (Exception e)
                {
                    NebulaLog.Warn($"control plane: could not restore from {_storage.Backend} storage: {e.Message}; starting empty");
                }
            }
            _plane.Connect();
            Publish();
            _storageDirty = false; // what we just loaded (or nothing) is already stored
            NebulaLog.Info($"control plane: hosted by this orchestrator, stored in {_storage.Backend}{(_token != null ? ", token required" : "")}");
        }

        public void Tick()
        {
            _plane.Tick();
            if (_plane.Version != _published) Publish();
            if (!_storageDirty) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextSave || Volatile.Read(ref _saving) != 0) return;
            _nextSave = now + SaveIntervalSeconds;
            _storageDirty = false;
            string json = _document;
            Interlocked.Exchange(ref _saving, 1);
            Task.Run(() =>
            {
                try
                {
                    _storage.Save(json);
                    _storageError = null;
                }
                catch (Exception e)
                {
                    if (_storageError != e.Message) NebulaLog.Warn($"control plane: saving to {_storage.Backend} storage failed: {e.Message}");
                    _storageError = e.Message;
                    _storageDirty = true; // try again on the next interval
                }
                finally { Interlocked.Exchange(ref _saving, 0); }
            });
        }

        public void Dispose()
        {
            if (_storageDirty)
            {
                try { _storage.Save(_document); } catch (Exception e) { NebulaLog.Warn($"control plane: final save failed: {e.Message}"); }
            }
            _plane.Dispose();
            lock (_gate) Monitor.PulseAll(_gate); // release waiting readers: the plane is no longer connected
            _storage.Dispose();
        }

        public void RegisterWorker(string workerId, uint workerIndex, string address, ushort port) => _plane.RegisterWorker(workerId, workerIndex, address, port);
        public void HeartbeatWorker(string workerId, string status, in WorkerStats stats) => _plane.HeartbeatWorker(workerId, status, stats);
        public void UnregisterWorker(string workerId) => _plane.UnregisterWorker(workerId);
        public void RegisterGateway(string gatewayId, string address, ushort port, uint incarnation = 0) => _plane.RegisterGateway(gatewayId, address, port, incarnation);
        public void HeartbeatGateway(string gatewayId, in GatewayStats stats) => _plane.HeartbeatGateway(gatewayId, stats);
        public void UnregisterGateway(string gatewayId) => _plane.UnregisterGateway(gatewayId);
        public void SetGatewayDraining(string gatewayId, bool draining) => _plane.SetGatewayDraining(gatewayId, draining);
        public void HeartbeatOrchestrator(string orchestratorId, uint desiredWorkers) => _plane.HeartbeatOrchestrator(orchestratorId, desiredWorkers);
        public void SetSetting(string key, string value) => _plane.SetSetting(key, value);
        public void EnsureContainer(string containerId) => _plane.EnsureContainer(containerId);
        public void EnsureRuntimeContainer(string containerId, Bounds bounds, string workerId, InstanceContainerInfo instance = null) => _plane.EnsureRuntimeContainer(containerId, bounds, workerId, instance);
        public void TouchContainer(string containerId) => _plane.TouchContainer(containerId);
        public void AssignContainer(string containerId, string workerId) => _plane.AssignContainer(containerId, workerId);
        public void PinContainer(string containerId, string workerId) => _plane.PinContainer(containerId, workerId);
        public void SetLeaseState(string containerId, string state) => _plane.SetLeaseState(containerId, state);
        public void SetContainerHint(string containerId, in ContainerHint hint) => _plane.SetContainerHint(containerId, hint);
        public void ReleaseContainer(string containerId) => _plane.ReleaseContainer(containerId);
        public void RemoveContainer(string containerId) => _plane.RemoveContainer(containerId);
        public void ResetControlPlane() => _plane.ResetControlPlane();

        // ---------------------------------------------------------------------------------------- HTTP

        /// <summary>Serve reads from <paramref name="http"/>. Writes go through <see cref="TryHandle"/> from the command pump.</summary>
        public void Attach(OrchestratorHttpServer http)
        {
            http.MapDirect("GET", Path, HandleRead);
        }

        /// <summary>
        /// Main thread: apply a batch of writes when <paramref name="req"/> is one. Returns false for any other
        /// request so the caller can handle it.
        /// </summary>
        public bool TryHandle(OrchestratorHttpServer.Request req, out OrchestratorHttpServer.Response response)
        {
            response = default;
            if (req.Method != "POST" || req.Path.TrimEnd('/') != Path) return false;
            if (!Authorized(req)) { response = OrchestratorHttpServer.Response.Error(401, "missing or wrong mesh token"); return true; }
            string reason = ControlPlaneJson.ApplyBatch(req.Body, _plane);
            if (reason != null) { response = OrchestratorHttpServer.Response.Error(400, reason); return true; }
            response = OrchestratorHttpServer.Response.Json(200, $"{{\"ok\":true,\"version\":{_plane.Version.ToString(CultureInfo.InvariantCulture)}}}");
            return true;
        }

        /// <summary>Listener thread: wait for a version above <c>since</c>, then answer with the document.</summary>
        private OrchestratorHttpServer.Response HandleRead(OrchestratorHttpServer.Request req)
        {
            if (!Authorized(req)) return OrchestratorHttpServer.Response.Error(401, "missing or wrong mesh token");
            long since = -1;
            string s = req.GetQuery("since");
            if (!string.IsNullOrEmpty(s)) long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out since);
            double wait = 0;
            string w = req.GetQuery("wait");
            if (!string.IsNullOrEmpty(w) && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out wait)) wait = Math.Min(Math.Max(wait, 0), MaxWaitSeconds);
            string document;
            lock (_gate)
            {
                var deadline = DateTime.UtcNow.AddSeconds(wait);
                while (_documentVersion <= since && _plane.IsConnected)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(_gate, remaining)) break;
                }
                document = _document;
            }
            // The document was written when it last changed; the clock in it is refreshed on the way out (the
            // parser keeps the last value of a repeated key), so a subscriber that waited the full interval is not
            // told the time was minutes ago.
            document = document.Substring(0, document.Length - 1) + ",\"now\":" + ControlPlaneJson.ToUnixMs(DateTime.UtcNow).ToString(CultureInfo.InvariantCulture) + "}";
            return OrchestratorHttpServer.Response.Json(200, document);
        }

        private bool Authorized(OrchestratorHttpServer.Request req) => _token == null || req.Token == _token;

        private void Publish()
        {
            string json = _plane.ToJson();
            _published = _plane.Version;
            lock (_gate)
            {
                _document = json;
                _documentVersion = _plane.Version;
                Monitor.PulseAll(_gate);
            }
            _storageDirty = true;
        }
    }
}
