using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
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
    /// <para>
    /// Both threads send their requests with blocking socket calls, not through the thread pool, so the mirror and the
    /// heartbeats keep going when the game fills the pool with blocking work. When a write has waited longer than
    /// <see cref="StallWarningSeconds"/>, or no document has arrived for longer than
    /// <see cref="DisconnectAfterSeconds"/>, <see cref="Tick"/> logs a warning that includes the thread pool's state.
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
        /// <summary>Gateway session requests only. The reader and sender threads each have a blocking client.</summary>
        private readonly HttpClient _http;
        private readonly BlockingHttpClient _readHttp, _sendHttp;
        /// <summary>Local clock usable from every thread (Unity's Time is main-thread only).</summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new object();
        private readonly Queue<string> _writes = new Queue<string>();
        private readonly ControlPlaneJson.OpWriter _op = new ControlPlaneJson.OpWriter();
        private Thread _reader, _sender;
        private volatile bool _running;
        private ControlPlaneJson.Snapshot _incoming;
        private long _version = -1;
        /// <summary>Identity of the mirrored document, or an empty string until a document with an identity arrives.</summary>
        public string DocumentId { get; private set; } = "";
        private double _lastReadAt = double.NegativeInfinity;
        private DateTime _serverNow = DateTime.UtcNow;
        private double _serverNowAtLocal;
        private volatile string _readError, _writeError;
        private string _loggedReadError, _loggedWriteError;
        private bool _loggedConnected;
        /// <summary>When the sender took the batch it is sending from the queue (local clock), or NaN while it sends none.</summary>
        private double _sendingSince = double.NaN;
        /// <summary>When the read in flight started (local clock), or NaN while none is.</summary>
        private double _readingSince = double.NaN;
        private bool _warnedSendStall, _warnedReadStall;

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
            _readHttp = new BlockingHttpClient(TimeSpan.FromSeconds(LongPollSeconds + 10));
            _sendHttp = new BlockingHttpClient(TimeSpan.FromSeconds(LongPollSeconds + 10));
        }

        /// <summary>
        /// Seconds a write (a heartbeat, for example) may wait to reach the orchestrator before <see cref="Tick"/> logs a
        /// warning, once per stall. Set it to the orchestrator's worker timeout (<c>NebulaConfig.WorkerTimeoutSeconds</c>):
        /// after that long without a heartbeat, the orchestrator treats a worker or gateway as dead.
        /// </summary>
        public float StallWarningSeconds { get; set; } = 5f;

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
            CheckStalls();
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
            DocumentId = incoming.DocumentId ?? "";
            _workers.Clear(); _workers.AddRange(incoming.Workers);
            _leases.Clear(); _leases.AddRange(incoming.Leases);
            _gateways.Clear(); _gateways.AddRange(incoming.Gateways);
            _scopes.Clear(); _scopes.AddRange(incoming.Scopes);
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
            _readHttp.Abort();
            _sendHttp.Abort();
            try { _http.CancelPendingRequests(); } catch { }
            _http.Dispose();
        }

        /// <summary>
        /// Main thread: warn once when a write has waited longer than <see cref="StallWarningSeconds"/>, or when no
        /// document has arrived for longer than <see cref="DisconnectAfterSeconds"/>, and say when each recovers.
        /// </summary>
        private void CheckStalls()
        {
            double now = _clock.Elapsed.TotalSeconds;
            double sending = Volatile.Read(ref _sendingSince);
            if (!double.IsNaN(sending) && now - sending > StallWarningSeconds)
            {
                if (!_warnedSendStall)
                {
                    _warnedSendStall = true;
                    NebulaLog.Warn($"control plane: a write to {_baseUrl} has waited {now - sending:0.0} s and heartbeats queue behind it; the orchestrator treats this process as dead after {StallWarningSeconds:0.#} s without one. {PendingWrites} more writes queued. {ThreadPoolState()}");
                }
            }
            else if (_warnedSendStall && double.IsNaN(sending))
            {
                _warnedSendStall = false;
                NebulaLog.Info($"control plane: writes to {_baseUrl} are getting through again");
            }

            double reading = Volatile.Read(ref _readingSince);
            double lastRead = Volatile.Read(ref _lastReadAt);
            // Before the first document arrives, count from the start of the read in flight.
            double silentSince = double.IsNegativeInfinity(lastRead) ? reading : lastRead;
            if (_running && !double.IsNaN(silentSince) && now - silentSince > DisconnectAfterSeconds)
            {
                if (!_warnedReadStall)
                {
                    _warnedReadStall = true;
                    string inFlight = double.IsNaN(reading) ? "no read in flight" : $"the read in flight started {now - reading:0.0} s ago";
                    NebulaLog.Warn($"control plane: no document from {_baseUrl} for {now - silentSince:0.0} s ({inFlight}); the mesh keeps running on its last known topology. {ThreadPoolState()}");
                }
            }
            else if (_warnedReadStall && !double.IsNegativeInfinity(lastRead) && now - lastRead <= DisconnectAfterSeconds)
            {
                _warnedReadStall = false;
                NebulaLog.Info($"control plane: documents from {_baseUrl} are arriving again");
            }
        }

        /// <summary>How busy the thread pool is, for a stall warning. A game that fills the pool stalls every task that waits on it.</summary>
        internal static string ThreadPoolState()
        {
            ThreadPool.GetAvailableThreads(out int workers, out int io);
            ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
            string state = $"Thread pool: {maxWorkers - workers} of {maxWorkers} worker threads busy, {maxIo - io} of {maxIo} I/O threads busy";
#if NEBULA_SERVICE
            state += $", {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} work items waiting";
#endif
            return state + ".";
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
                .Arg("ghostCount", s.GhostCount).Arg("playerCount", s.PlayerCount).Arg("botCount", s.BotCount).Arg("serverDrivenCount", s.ServerDrivenCount).Arg("hasGlobalEntities", s.HasGlobalEntities)
                .Arg("oldestDirtySeconds", s.OldestDirtySeconds).End());

        public void UnregisterWorker(string workerId) => Enqueue(_op.Op(ControlPlaneJson.UnregisterWorker).Arg("workerId", workerId).End());
        public void RegisterGateway(string gatewayId, string address, ushort port, uint incarnation = 0) =>
            Enqueue(_op.Op(ControlPlaneJson.RegisterGateway).Arg("gatewayId", gatewayId).Arg("address", address).Arg("port", (long)port).Arg("incarnation", (long)incarnation).End());
        public void HeartbeatGateway(string gatewayId, in GatewayStats s) =>
            Enqueue(_op.Op(ControlPlaneJson.HeartbeatGateway).Arg("gatewayId", gatewayId)
                .Arg("pendingJoins", (long)s.PendingJoins).Arg("activeClients", (long)s.ActiveClients).Arg("joiningClients", (long)s.JoiningClients).Arg("reconnectingClients", (long)s.ReconnectingClients)
                .Arg("packetsIn", s.PacketsInPerSecond).Arg("packetsOut", s.PacketsOutPerSecond).Arg("bytesIn", s.BytesInPerSecond).Arg("bytesOut", s.BytesOutPerSecond)
                .Arg("workerBytesIn", s.WorkerBytesInPerSecond).Arg("workerBytesOut", s.WorkerBytesOutPerSecond).Arg("cpu", s.Cpu).Arg("memoryBytes", (long)s.MemoryBytes)
                .Arg("loopLagMs", s.LoopLagMs).Arg("workerConnections", (long)s.WorkerConnections).Arg("ready", s.Ready).Arg("draining", s.Draining)
                // The interest half of GatewayStats (design §12). ReadGatewayStats on the orchestrator has
                // always expected these keys; without them every interest field in /api/state reads zero for a
                // gateway that reaches the control plane over HTTP, which is every gateway in a real mesh.
                .Arg("interestSetAvg", s.InterestSetAvg).Arg("interestSetMax", (long)s.InterestSetMax)
                .Arg("cachedEntities", (long)s.CachedEntities).Arg("subscribedRegions", (long)s.SubscribedRegions)
                .Arg("workerLinks", (long)s.WorkerLinks).Arg("workerLinkReasons", s.WorkerLinkReasons ?? "")
                .Arg("spawnsPerSecond", s.SpawnsPerSecond).Arg("despawnsPerSecond", s.DespawnsPerSecond)
                .Arg("interestEvalMsAvg", s.InterestEvalMsAvg).Arg("interestEvalMsMax", s.InterestEvalMsMax)
                .Arg("interestEvalsPerSecond", s.InterestEvalsPerSecond)
                .Arg("bytesPerClientAvg", s.BytesPerClientAvg).Arg("bytesPerClientMax", s.BytesPerClientMax)
                .Arg("extensionErrors", (long)s.ExtensionErrors).End());
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
        public void SetContainerCapacity(string containerId, float saturation, CostComponent dominant, bool atCapacity, SaturationCause cause = SaturationCause.None) =>
            Enqueue(_op.Op(ControlPlaneJson.SetContainerCapacity).Arg("containerId", containerId).Arg("saturation", saturation)
                .Arg("dominant", ContainerCost.NameOf(dominant)).Arg("atCapacity", atCapacity).Arg("cause", SaturationReport.NameOf(cause)).End());
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
                    Volatile.Write(ref _readingSince, _clock.Elapsed.TotalSeconds);
                    var resp = _readHttp.Send("GET", url, _token, null);
                    Volatile.Write(ref _readingSince, double.NaN);
                    if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {resp.Status}: {Trim(resp.Body)}");
                    var snapshot = ControlPlaneJson.Parse(resp.Body);
                    since = snapshot.Version;
                    Volatile.Write(ref _lastReadAt, _clock.Elapsed.TotalSeconds);
                    lock (_gate) _incoming = snapshot;
                    _readError = null;
                }
                catch (Exception e)
                {
                    Volatile.Write(ref _readingSince, double.NaN);
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
                Volatile.Write(ref _sendingSince, _clock.Elapsed.TotalSeconds);
                while (true)
                {
                    try
                    {
                        var resp = _sendHttp.Send("POST", _baseUrl + ControlPlaneHost.Path, _token, body);
                        if (resp.IsSuccessStatusCode) { _writeError = null; break; }
                        // A rejected batch (bad request, wrong token) will not get better by retrying it.
                        if (resp.Status == 400 || resp.Status == 401) { _writeError = $"HTTP {resp.Status}: {Trim(resp.Body)} (batch dropped)"; break; }
                        throw new HttpRequestException($"HTTP {resp.Status}: {Trim(resp.Body)}");
                    }
                    catch (Exception e)
                    {
                        if (!_running) return;
                        _writeError = e.Message;
                        Sleep(RetrySeconds);
                    }
                }
                Volatile.Write(ref _sendingSince, double.NaN);
            }
        }

        private void Sleep(float seconds)
        {
            lock (_gate) { if (_running) Monitor.Wait(_gate, TimeSpan.FromSeconds(seconds)); }
        }

        private static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 200 ? s.Substring(0, 200) + "..." : s;
    }
}
