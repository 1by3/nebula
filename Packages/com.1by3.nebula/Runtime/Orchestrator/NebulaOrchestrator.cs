using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Nebula.Hosting;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>One line of the orchestrator's recent history, shown on the dashboard.</summary>
    public struct OrchestratorEvent
    {
        public DateTime Time;
        public string Level;
        public string Message;
    }

    /// <summary>
    /// Spins worker processes up and down and authoritatively assigns containers to them through the control plane.
    /// The CLI runs this loop in a standalone .NET executable. The Unity component remains available for compatibility.
    /// The number of workers is a live setting: <see cref="SetDesiredWorkers"/> (from the web dashboard, see
    /// <see cref="OrchestratorHttpServer"/>) launches new processes or retires existing ones on the fly.
    /// <para>
    /// Assignment is a pluggable <see cref="IAssignmentPolicy"/> (<see cref="Policy"/>). The baked policy deals
    /// containers as evenly as possible across live workers by count, sticky to their current owner so a rebalance
    /// moves as few containers as possible. The cost policy deals by the load workers report per container, along a
    /// space-filling curve, and is the default once the game registers runtime containers.
    /// </para><para>
    /// Removing a worker is graceful: it is excluded from the assignment set, so the next pass moves its containers
    /// to the survivors and the worker hands its entities over through the normal per-entity handover path. Once it
    /// holds no leases and reports no authoritative entities (or the drain timeout passes) the process is killed.
    /// A worker whose heartbeat stops is declared dead, its containers are reassigned immediately, and a replacement
    /// is launched after a short delay.
    /// </para><para>
    /// On a host where an instance costs money to keep and time to recreate (<see cref="IWorkerHost.SupportsParking"/>),
    /// a drained worker is parked into the idle pool instead of killed: it keeps its worker id, its index and its
    /// machine, receives no leases, and a later scale-out takes it back before anything new is launched. The host
    /// drops a parked instance by itself when keeping it stops being free.
    /// </para>
    /// </summary>
    public sealed class NebulaOrchestrator
#if !NEBULA_SERVICE
        : MonoBehaviour
#endif
    {
        private sealed class ManagedWorker
        {
            public string Id;
            public uint Index;
            /// <summary>The host's view of the instance; null when a launch failed outright.</summary>
            public IWorkerHandle Handle;
            public float LaunchedAt;
            public float RelaunchAt = -1f;
            public bool Retiring;
            public float RetireDeadline;
            /// <summary>In the idle pool: the instance is still there (and still paid for) but it is dealt nothing. See <see cref="IWorkerHost.Park"/>.</summary>
            public bool Parked;
            /// <summary>When it was parked; the most recently parked worker is the first one unparked.</summary>
            public float ParkedAt;
        }

        /// <summary>Hard cap on the worker cap: indices are 16-bit (the high bits of every net id).</summary>
        public const int MaxWorkersLimit = ushort.MaxValue;
        private const int MaxEvents = 200;
        /// <summary>The most workers this orchestrator will run (<see cref="NebulaConfig.MaxWorkers"/>).</summary>
        public int MaxWorkers => Mathf.Clamp(Config != null ? Config.MaxWorkers : 32, 1, MaxWorkersLimit);
        /// <summary>The fewest workers autoscaling will leave running (<see cref="NebulaConfig.MinWorkers"/>); 0 means the mesh may scale to zero.</summary>
        public int MinWorkers => Mathf.Clamp(Config != null ? Config.MinWorkers : 1, 0, MaxWorkers);
        /// <summary>How long the host is asked to keep a parked worker (<see cref="NebulaConfig.IdlePoolSeconds"/>); 0 leaves it to the host.</summary>
        private float IdlePoolSeconds => Config != null ? Mathf.Max(0f, Config.IdlePoolSeconds) : 0f;
        /// <summary>True when retiring a worker parks it instead of killing it.</summary>
        private bool CanPark => _host != null && _host.SupportsParking;
        /// <summary>Workers sitting in the idle pool, most recently parked first: the order they are taken back in.</summary>
        private IEnumerable<ManagedWorker> ParkedWorkers => _managed.Where(m => m.Parked).OrderByDescending(m => m.ParkedAt);
        /// <summary>The idle pool as <see cref="PickWorkerToUnpark"/> sees it.</summary>
        private IEnumerable<KeyValuePair<string, float>> ParkedByTime => _managed.Where(m => m.Parked).Select(m => new KeyValuePair<string, float>(m.Id, m.ParkedAt));
        /// <summary>How many workers are in the idle pool. They cost the host something but do no work and are not counted in <see cref="DesiredWorkers"/>.</summary>
        public int ParkedWorkerCount => _managed.Count(m => m.Parked);

        /// <summary>
        /// How containers are dealt to workers. Chosen from <see cref="NebulaConfig.AssignmentPolicy"/> (or
        /// <c>-nebula-assignment</c>) at <see cref="Initialize"/>; game code may replace it at any time. "auto" is
        /// the cost policy, for baked and runtime worlds alike; "baked" is the explicit opt-out that deals by count.
        /// </summary>
        public IAssignmentPolicy Policy { get; set; }
        private string _policyMode = "auto";
        private readonly BakedAssignmentPolicy _bakedPolicy = new BakedAssignmentPolicy();
        private CostBalancedAssignmentPolicy _costPolicy;
        private readonly AssignmentInput _assignmentInput = new AssignmentInput();
        private readonly Dictionary<string, ContainerLoad> _occupancy = new Dictionary<string, ContainerLoad>(StringComparer.Ordinal);
        /// <summary>
        /// The latest cost row of every container the mesh holds, one per lease (<see cref="ContainerCost"/>,
        /// docs/cost-telemetry.md): what it costs in simulation, in replication and in gateway relay. Refreshed
        /// once per pass from telemetry, read by the scaler and served at <c>GET /api/cost</c>.
        /// </summary>
        private readonly Dictionary<string, ContainerCost> _containerCost = new Dictionary<string, ContainerCost>(StringComparer.Ordinal);
        /// <summary>The cost row of one container, or all zeroes when no worker has reported it lately.</summary>
        public ContainerCost CostOf(string containerId) => containerId != null && _containerCost.TryGetValue(containerId, out var row) ? row : default;
        private readonly List<ContainerCost> _costRows = new List<ContainerCost>();
        /// <summary>
        /// What the mesh last published about how full each container is (docs/capacity-admission.md). Kept so a
        /// reading is only written to the control plane when it actually moved: the document is mirrored to every
        /// role and a per-pass rewrite of every lease would be a broadcast per pass.
        /// </summary>
        private readonly Dictionary<string, CapacityInfo> _capacity = new Dictionary<string, CapacityInfo>(StringComparer.Ordinal);
        /// <summary>How full a container is, as this orchestrator last derived it. Unknown when no worker has reported it lately.</summary>
        public CapacityInfo CapacityOf(string containerId) =>
            containerId != null && _capacity.TryGetValue(containerId, out var info) ? info : new CapacityInfo { ContainerId = containerId ?? "" };
        /// <summary>Per-worker interest summary for the state document, refreshed from telemetry on every build.</summary>
        private readonly Dictionary<string, MeshTelemetry.WorkerInterest> _interestByWorker = new Dictionary<string, MeshTelemetry.WorkerInterest>(StringComparer.Ordinal);
        /// <summary>Total container cost the mesh carries, per the cost policy, as of the last pass.</summary>
        public float TotalCost { get; private set; }

        /// <summary>The rolling tick-time window behind the scaling signal, sampled from every heartbeat this orchestrator sees.</summary>
        public WorkerLoadTracker Loads { get; } = new WorkerLoadTracker(() => Time.unscaledTime);
        private readonly WorkerScaler _scaler = new WorkerScaler();
        /// <summary>Reused every pass so the settle check allocates nothing (<see cref="WorkerScaler.IsInFlight"/>).</summary>
        private readonly List<WorkerScaler.MeshMember> _meshScratch = new List<WorkerScaler.MeshMember>();
        /// <summary>p90 utilization per eligible worker, as of the last pass.</summary>
        private readonly Dictionary<string, float> _utilization = new Dictionary<string, float>(StringComparer.Ordinal);
        /// <summary>That utilization spread over the containers, as of the last pass (<see cref="AssignmentInput.Utilization"/>).</summary>
        private readonly Dictionary<string, float> _containerUtilization = new Dictionary<string, float>(StringComparer.Ordinal);
        /// <summary>The last thing the policy said it could not honour, so it is logged once rather than every pass.</summary>
        private string _hintNote = "";
        /// <summary>The baked hints merged with the ones set while the mesh runs, rebuilt each pass (<see cref="AssignmentInput.Hints"/>).</summary>
        private readonly Dictionary<string, ContainerHint> _containerHints = new Dictionary<string, ContainerHint>(StringComparer.Ordinal);
        /// <summary>Containers under a live hold, with the seconds each has to run (<see cref="AssignmentInput.Holds"/>).</summary>
        private readonly Dictionary<string, float> _holds = new Dictionary<string, float>(StringComparer.Ordinal);
        /// <summary>The entity cohesion groups the workers report, rebuilt each pass (<see cref="AssignmentInput.Cohesion"/>).</summary>
        private readonly List<CohesionGroupInfo> _cohesion = new List<CohesionGroupInfo>();
        /// <summary>What the scaler decided last pass; shown on the dashboard as the scaling line.</summary>
        private ScaleDecision _scale = new ScaleDecision { BlockedBy = "", RetireWorkerId = "", Reason = "idle" };

        public NebulaConfig Config { get; private set; }
        public IControlPlane ControlPlane { get; private set; }
        public string OrchestratorId { get; private set; } = "orch1";
        public int DesiredWorkers { get; private set; }
        public int Rebalances { get; private set; }
        public IReadOnlyList<string> LastAssignmentLog => _log;
        /// <summary>The last pass's moves and why the planner made them (<c>docs/cohesion-rebalancing.md</c>).</summary>
        public IReadOnlyList<AssignmentMove> LastMoves => _moves;
        /// <summary>What the planner reported it could not relieve, and why (<see cref="SaturationReport"/>).</summary>
        public IReadOnlyList<SaturationReport> Saturated => _saturated;
        public IReadOnlyList<OrchestratorEvent> Events => _events;
        public string DashboardUrl => _http != null ? _http.Url : "";
        /// <summary>The World map's data: static container geometry and the latest telemetry each worker posted (see <see cref="WorkerTelemetry"/>).</summary>
        public MeshTelemetry Telemetry { get; } = new MeshTelemetry();
        /// <summary>Recent log lines of every process of the mesh, posted to and read from <c>/api/logs</c> (see <see cref="LogBuffer"/>).</summary>
        public LogBuffer Logs { get; } = new LogBuffer();

        /// <summary>
        /// The mesh's persistence store, so the dashboard can report how many entities are saved and wipe them
        /// (<c>POST /api/persistence/clear</c>). Set by <see cref="NebulaBootstrap"/> before <see cref="Initialize"/>;
        /// null when persistence is off. The orchestrator never writes records itself.
        /// </summary>
        public IPersistenceStore Persistence { get; set; }

        /// <summary>
        /// Decodes and rewrites persisted records for the dashboard's Persistence tab; created on the first request
        /// and dropped when <see cref="Persistence"/> is replaced. Null while persistence is off.
        /// </summary>
        private PersistenceEditor PersistenceTab
        {
            get
            {
                if (Persistence == null) return null;
                if (_persistenceEditor == null || !ReferenceEquals(_persistenceEditor.Store, Persistence)) _persistenceEditor = new PersistenceEditor(Persistence);
                return _persistenceEditor;
            }
        }

        private PersistenceEditor _persistenceEditor;

        /// <summary>Serves <see cref="Persistence"/> to workers (<see cref="PersistenceHost"/>); created on the first request, like the tab.</summary>
        private PersistenceHost StoreHost
        {
            get
            {
                if (Persistence == null) return null;
                if (_storeHost == null || !ReferenceEquals(_storeHost.Store, Persistence)) _storeHost = new PersistenceHost(Persistence, Config != null ? Config.MeshToken : null);
                return _storeHost;
            }
        }

        private PersistenceHost _storeHost;

        private readonly List<ManagedWorker> _managed = new List<ManagedWorker>();
        private readonly List<string> _log = new List<string>();
        /// <summary>
        /// The last pass's moves with the sentence that explains each one, when the policy in force explains itself
        /// (<see cref="IExplainsAssignment"/>). Kept until the next pass that moves something, so the dashboard's
        /// assignment card still says why the mesh looks the way it does (<c>docs/cohesion-rebalancing.md</c>).
        /// </summary>
        private readonly List<AssignmentMove> _moves = new List<AssignmentMove>();
        /// <summary>What the planner could not relieve last pass, hottest first (<see cref="SaturationReport"/>).</summary>
        private readonly List<SaturationReport> _saturated = new List<SaturationReport>();
        private readonly List<OrchestratorEvent> _events = new List<OrchestratorEvent>();
        /// <summary>Workers we have seen alive during this orchestrator's lifetime. Rows left behind by a previous run are never "declared dead", only reset.</summary>
        private readonly HashSet<string> _seenAlive = new HashSet<string>();
        /// <summary>Workers being drained: excluded from assignment, killed once empty. Keyed by id so unmanaged (externally started) workers can be retired too.</summary>
        private readonly Dictionary<string, float> _retiring = new Dictionary<string, float>();
        /// <summary>Recently retired ids -> time; their control-plane row may linger for a pass or two and must not be "declared dead".</summary>
        private readonly Dictionary<string, float> _retired = new Dictionary<string, float>();
        private readonly StringBuilder _json = new StringBuilder(8192);
        private Process _gatewayProcess;
        private OrchestratorHttpServer _http;
        /// <summary>Where workers run (child processes or cloud machines).</summary>
        private IWorkerHost _host;
        /// <summary>Always local: starts the gateway next to the orchestrator.</summary>
        private ProcessWorkerHost _local;
        private float _nextPass;
        private float _nextPublish;
        private bool _containersEnsured;
        private bool _gatewayLaunched;
        private bool _hostErrorLogged;
        /// <summary>Runtime containers appeared or vanished since the map's geometry was published.</summary>
        private bool _geometryDirty;

        public string HostName => _host != null ? _host.Name : "";

        public void Initialize(NebulaConfig config, IControlPlane controlPlane)
        {
            Config = config;
            ControlPlane = controlPlane;
            // WorkerCount is the count to start with; the autoscaling floor only applies when autoscaling is running.
            bool scaling = config.AutoScale && !config.UseLocalControlPlane;
            DesiredWorkers = Mathf.Clamp(config.WorkerCount, scaling ? MinWorkers : 0, MaxWorkers);
            Loads.WindowSeconds = Mathf.Max(1f, config.ScaleWindowSeconds);
            // Wired once: a per-pass assignment would allocate a delegate every half second for nothing.
            _scaler.CanRetire = MayRetire; // a worker holding a dedicated container is never the one to go
            _scaler.IsWarm = Loads.IsWarm;  // nor one whose window is still filling
            OrchestratorId = CommandLine.Get("nebula-orchestrator-id", "orch1");
            _costPolicy = new CostBalancedAssignmentPolicy { Weights = config.CostWeights, Threshold = config.CostRebalanceThreshold, MinGain = config.ScaleMinGain, SeamGraceMeters = config.SeamGraceMeters };
            _policyMode = CommandLine.Get("nebula-assignment", config.AssignmentPolicy ?? "auto").Trim().ToLowerInvariant();
            if (_policyMode != "auto" && _policyMode != "baked" && _policyMode != "cost")
            {
                Log("warn", $"unknown assignment policy '{_policyMode}'; using auto");
                _policyMode = "auto";
            }
            Policy = _policyMode == "baked" ? (IAssignmentPolicy)_bakedPolicy : _costPolicy;
            _local = new ProcessWorkerHost(config.WorkerExecutable, config.WorkerAdvertiseAddress);
            _host = CreateHost(config);
            Log("info", $"orchestrator {OrchestratorId}: desired workers = {DesiredWorkers}, host={_host.Name}, spawnGateway={config.OrchestratorSpawnsGateway}");
            // The two yardsticks a container's cost components are weighed against (docs/cost-telemetry.md).
            Telemetry.TickPeriodMs = WorkerLoadTracker.TickPeriodMs;
            Telemetry.LinkBytesPerSec = config.CostLinkBytesPerSec;
            Telemetry.CapacitySaturation = config.CapacitySaturation;
            Telemetry.PublishGeometry(MeshTelemetry.BuildGeometryJson(config));
            ContainerRegistry.RuntimeRegistered += OnRuntimeContainersChanged;
            ContainerRegistry.RuntimeUnregistering += OnRuntimeContainersChanged;
            StartDashboard();
            _host.Initialize(Log);
            if (!ReferenceEquals(_host, _local)) _local.Initialize(Log);
        }

        /// <summary>Pick the worker host from <see cref="NebulaConfig.WorkerHost"/> (<c>-nebula-host</c>). New providers register here.</summary>
        private IWorkerHost CreateHost(NebulaConfig config)
        {
            switch ((config.WorkerHost ?? "process").Trim().ToLowerInvariant())
            {
                case "hetzner":
                    return new HetznerWorkerHost(CloudHostSettings.FromCommandLine(OrchestratorId, config.WorkerAdvertiseAddress, config.DashboardPort));
                case "cloud":
                    // Managed hosting: workers come from a deployment-scoped resource API, not from a provider this process has credentials for.
                    return new CloudWorkerHost(CloudWorkerHostSettings.FromCommandLine());
                case "":
                case "process":
                case "local":
                    return _local;
                default:
                    Log("error", $"unknown worker host '{config.WorkerHost}'; using local processes");
                    return _local;
            }
        }

        private void StartDashboard()
        {
            if (Config.DashboardPort == 0) return;
            try
            {
                var page = Resources.Load<TextAsset>("NebulaDashboard");
                string bind = CommandLine.Get("nebula-dashboard-bind", "localhost");
                string artifacts = Config.BuildArtifactDir;
                if (string.IsNullOrEmpty(artifacts))
                {
                    try { artifacts = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName); } catch { artifacts = null; }
                }
                _http = new OrchestratorHttpServer(bind, Config.DashboardPort, page != null ? page.text : null, artifacts);
                var map = Resources.Load<TextAsset>("NebulaDashboardMap");
                if (map != null) _http.AddPage("/map", map.text);
                var persistence = Resources.Load<TextAsset>("NebulaDashboardPersistence");
                if (persistence != null) _http.AddPage("/persistence", persistence.text);
                // Telemetry and the map document are served on the listener thread: workers post several times a
                // second, and nothing here touches Unity.
                _http.MapDirect("POST", "/api/telemetry", req =>
                {
                    string error = Telemetry.Accept(req.Body, out bool detail);
                    return error != null
                        ? OrchestratorHttpServer.Response.Error(400, error)
                        : OrchestratorHttpServer.Response.Json(200, detail ? "{\"ok\":true,\"detail\":true}" : "{\"ok\":true,\"detail\":false}");
                });
                _http.MapDirect("GET", "/api/map", _ => OrchestratorHttpServer.Response.Json(200, Telemetry.BuildMapJson()));
                _http.MapDirect("GET", "/api/map/geometry", _ => OrchestratorHttpServer.Response.Json(200, Telemetry.GeometryJson));
                // Per-container cost rows (docs/cost-telemetry.md). Served straight off the telemetry store, like
                // the map, so a monitor polling it never waits on the orchestrator's frame.
                _http.MapDirect("GET", "/api/cost", _ => OrchestratorHttpServer.Response.Json(200, Telemetry.BuildCostJson()));
                // Recent log lines from every process of the mesh (see LogBuffer): posted by the machines, read by
                // whoever operates the mesh. With a mesh token both directions need it.
                _http.MapDirect("POST", "/api/logs", req =>
                {
                    if (!string.IsNullOrEmpty(Config.MeshToken) && req.Token != Config.MeshToken) return OrchestratorHttpServer.Response.Error(401, "mesh token required");
                    string error = Logs.Accept(req.Body, out int accepted);
                    return error != null ? OrchestratorHttpServer.Response.Error(400, error) : OrchestratorHttpServer.Response.Json(200, $"{{\"ok\":true,\"accepted\":{accepted}}}");
                });
                _http.MapDirect("GET", "/api/logs", req =>
                {
                    if (!string.IsNullOrEmpty(Config.MeshToken) && req.Token != Config.MeshToken) return OrchestratorHttpServer.Response.Error(401, "mesh token required");
                    long.TryParse(req.GetQuery("since"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long since);
                    int.TryParse(req.GetQuery("limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit);
                    return OrchestratorHttpServer.Response.Json(200, Logs.Query(since, req.GetQuery("role"), req.GetQuery("instance"), limit));
                });
                // The control plane this orchestrator hosts: reads are served on the listener thread, writes come
                // through the command pump (HandleCommand) so they land on the main thread.
                if (ControlPlane is ControlPlaneHost host) host.Attach(_http);
                _http.Start();
                Log("info", $"dashboard at {_http.Url} (bind {bind}); World map at {_http.Url}map; workers post telemetry to {TelemetryUrl()}; artifacts from {artifacts ?? "(none)"}");
                if (ControlPlane is ControlPlaneHost h) Log("info", $"control plane hosted at {ControlPlaneUrl()} (stored in {h.StorageBackend}{(h.HasToken ? ", token required" : "")})");
            }
            catch (Exception e)
            {
                Log("error", $"dashboard failed to start on port {Config.DashboardPort}: {e.Message}");
                _http?.Dispose();
                _http = null;
#if NEBULA_SERVICE
                throw;
#endif
            }
        }

#if NEBULA_SERVICE
        public void Tick()
#else
        private void Update()
#endif
        {
            // Dashboard commands run here so every mutation happens on the main thread; the response waits for us.
            _http?.Pump(HandleCommand);
            _host.Tick();
            if (!ReferenceEquals(_host, _local)) _local.Tick();

            if (!ControlPlane.IsConnected)
            {
                if (Time.unscaledTime >= _nextPublish) PublishState();
                return;
            }
            if (!_host.IsReady)
            {
                if (!string.IsNullOrEmpty(_host.InitializationError) && !_hostErrorLogged)
                {
                    _hostErrorLogged = true;
                    Log("error", $"worker host '{_host.Name}' unavailable: {_host.InitializationError}. No workers will be launched.");
                }
                if (Time.unscaledTime >= _nextPublish) PublishState();
                return;
            }
            if (!_containersEnsured)
            {
                _containersEnsured = true;
                if (CommandLine.GetBool("nebula-reset", true)) ControlPlane.ResetControlPlane();
                foreach (var c in ContainerRegistry.All) ControlPlane.EnsureContainer(c.ContainerId);
                SeedSettings();
                if (!Config.UseLocalControlPlane) LaunchGateway();
                _nextPass = Time.unscaledTime + 1f; // let the reset land before judging anybody
                return;
            }
            if (Time.unscaledTime < _nextPass) return;
            _nextPass = Time.unscaledTime + 0.5f;

            ControlPlane.HeartbeatOrchestrator(OrchestratorId, (uint)DesiredWorkers);
            ContainerRegistry.SyncRuntime(ControlPlane.Leases);
            ReapDeadWorkers();
            WakeForDemand();
            ReconcileDesiredCount();
            Rebalance();
            PublishCapacity();
            SweepScopes();
            FinishRetirements();
            RelaunchIfNeeded();
            PublishState();
        }

#if NEBULA_SERVICE
        public void Dispose()
#else
        private void OnDestroy()
#endif
        {
            ContainerRegistry.RuntimeRegistered -= OnRuntimeContainersChanged;
            ContainerRegistry.RuntimeUnregistering -= OnRuntimeContainersChanged;
            _http?.Dispose();
            foreach (var m in _managed) if (m.Handle != null) _host.Kill(m.Handle);
            _host?.Dispose();
            if (!ReferenceEquals(_host, _local)) _local?.Dispose();
            ProcessWorkerHost.KillProcess(_gatewayProcess);
        }

        // ---------------------------------------------------------------------------------------- public controls

        /// <summary>Set how many workers should be running. Extra workers are launched; surplus ones are drained and killed (highest index first).</summary>
        public void SetDesiredWorkers(int count)
        {
            count = Mathf.Clamp(count, 0, MaxWorkers);
            if (count == DesiredWorkers) return;
            Log("info", $"desired workers {DesiredWorkers} -> {count}");
            DesiredWorkers = count;
            _nextPass = 0f; // act on the next frame
        }

        public void AddWorker() => SetDesiredWorkers(DesiredWorkers + 1);

        /// <summary>
        /// Seed game-defined, mesh-wide settings from <c>-nebula-settings key=value,key=value</c>. Nebula stores
        /// the values without interpreting them. Game code reads them from the control plane on each worker.
        /// </summary>
        private void SeedSettings()
        {
            // Nebula's own row: the gateway reads it to tell a client held in JoinState.Starting how long a worker takes.
            ControlPlane.SetSetting(MeshSettings.BootSeconds, Mathf.RoundToInt(_host.TypicalBootSeconds).ToString());
            string spec = CommandLine.Get("nebula-settings", "");
            if (string.IsNullOrWhiteSpace(spec)) return;
            foreach (var part in spec.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) { Log("warn", $"-nebula-settings: ignoring '{part}' (expected key=value)"); continue; }
                string key = part.Substring(0, eq).Trim(), value = part.Substring(eq + 1).Trim();
                Log("info", $"setting {key} = {value}");
                ControlPlane.SetSetting(key, value);
            }
        }

        /// <summary>Set one mesh-wide setting (dashboard). Game code on the workers decides what it means.</summary>
        public bool SetSetting(string key, string value)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0 || key.Length > 64 || (value ?? "").Length > 1024) return false;
            Log("info", $"setting {key} = {value}");
            ControlPlane.SetSetting(key, value ?? "");
            return true;
        }

        /// <summary>Gracefully remove one worker: a specific one, or the highest-index one when <paramref name="workerId"/> is empty.</summary>
        public bool RemoveWorker(string workerId = null)
        {
            if (string.IsNullOrEmpty(workerId))
            {
                if (DesiredWorkers == 0) return false;
                SetDesiredWorkers(DesiredWorkers - 1);
                return true;
            }
            if (_retiring.ContainsKey(workerId)) return false;
            if (_managed.Any(m => m.Id == workerId && m.Parked)) return false; // already out of service
            bool known = _managed.Any(m => m.Id == workerId && !m.Retiring) || ControlPlane.FindWorker(workerId) != null;
            if (!known) return false;
            BeginRetire(workerId, "removed from dashboard");
            // A managed worker counted toward the desired total; lower it so the reconciler does not replace it at once.
            if (_managed.Any(x => x.Id == workerId)) DesiredWorkers = Mathf.Max(0, DesiredWorkers - 1);
            _nextPass = 0f;
            return true;
        }

        /// <summary>Hard-kill a managed worker process (simulates a crash). The reaper reassigns its containers and relaunches it.</summary>
        public bool KillWorker(string workerId)
        {
            var m = _managed.FirstOrDefault(x => x.Id == workerId);
            if (m == null || m.Handle == null) return false;
            if (m.Parked) return DeleteParkedWorker(workerId);
            Log("warn", $"killing worker {workerId} ({m.Handle.Describe}) to simulate a crash");
            _host.Kill(m.Handle);
            _nextPass = 0f;
            return true;
        }

        /// <summary>Run an assignment pass now instead of waiting for the next 500 ms tick.</summary>
        public void RequestRebalance()
        {
            Log("info", "rebalance requested");
            _nextPass = 0f;
        }

        // ---------------------------------------------------------------------------------------- processes

        private void LaunchGateway()
        {
            if (Config.OrchestratorSpawnsGateway && !_gatewayLaunched)
            {
                _gatewayLaunched = true;
                // The gateway registers the address clients use (-nebula-gateway on the orchestrator, e.g. its public IP).
                _gatewayProcess = _local.LaunchService("gateway", "-nebula-gateway-id gw1", "gateway", CommonArgs());
            }
        }

        /// <summary>Switches every launched role inherits from the orchestrator.</summary>
        private string CommonArgs()
        {
            string args = $"-nebula-control-plane {ControlPlaneUrl()} -nebula-gateway {Config.GatewayAddress}:{Config.GatewayPort} -nebula-telemetry {TelemetryUrl()}";
            if (!string.IsNullOrEmpty(Config.MeshToken)) args += $" -nebula-token {Config.MeshToken}";
            // The gateway decides who may join; it gets the same answer the orchestrator was configured with.
            if (!string.IsNullOrEmpty(Config.AuthIssuers)) args += $" -nebula-auth-issuers {Config.AuthIssuers.Replace(" ", ",")}";
            if (!string.IsNullOrEmpty(Config.AuthAudience)) args += $" -nebula-auth-audience {Config.AuthAudience}";
            if (!Config.AuthAnonymous) args += " -nebula-auth-anonymous false";
            if (!string.IsNullOrEmpty(Config.AuthSigningKey)) args += $" -nebula-auth-key {Config.AuthSigningKey}";
            if (!Config.SingleSessionPerPlayer) args += " -nebula-single-session false";
            // Client link encryption is the gateway's business, but its configuration arrives here (docs/transport-encryption.md).
            if (!Config.EncryptClients) args += " -nebula-encrypt-clients false";
            if (Config.RequireEncryption) args += " -nebula-require-encryption true";
            if (!string.IsNullOrEmpty(Config.EncryptionCertPath)) args += $" -nebula-encryption-cert {Config.EncryptionCertPath}";
            if (!string.IsNullOrEmpty(Config.EncryptionKeyPath)) args += $" -nebula-encryption-key {Config.EncryptionKeyPath}";
            if (!string.IsNullOrEmpty(Config.EncryptionSelfSignedPath)) args += $" -nebula-encryption-store {Config.EncryptionSelfSignedPath}";
            // Workers keep their persistent entities through this orchestrator's store, or not at all.
            args += $" -nebula-persistence-mode {(Persistence != null ? "remote" : "off")}";
            if (CommandLine.GetBool("nebula-verbose", false)) args += " -nebula-verbose";
            return args;
        }

        /// <summary>
        /// Where launched workers and gateways reach the control plane this orchestrator hosts: its dashboard at the
        /// address it advertises (<c>-nebula-advertise</c>), the same way worker VMs fetch the build.
        /// </summary>
        private string ControlPlaneUrl()
        {
            string host = string.IsNullOrEmpty(Config.WorkerAdvertiseAddress) ? "127.0.0.1" : Config.WorkerAdvertiseAddress;
            return $"http://{host}:{Config.DashboardPort}/";
        }

        /// <summary>
        /// Where launched workers post World map telemetry: <c>-nebula-telemetry</c> when given (<c>off</c> disables
        /// it), otherwise this orchestrator's dashboard at the address it advertises, which is how worker VMs already
        /// reach it for the build. <c>off</c> when the dashboard is disabled.
        /// </summary>
        private string TelemetryUrl()
        {
            if (Config.DashboardPort == 0) return "off";
            string url = CommandLine.Get("nebula-telemetry", "");
            if (!string.IsNullOrEmpty(url)) return url;
            string host = string.IsNullOrEmpty(Config.WorkerAdvertiseAddress) ? "127.0.0.1" : Config.WorkerAdvertiseAddress;
            return $"http://{host}:{Config.DashboardPort}/api/telemetry";
        }

        private void LaunchWorker(uint index)
        {
            string id = $"w{index}";
            var existing = _managed.FirstOrDefault(m => m.Index == index);
            if (existing == null)
            {
                existing = new ManagedWorker { Id = id, Index = index };
                _managed.Add(existing);
            }
            existing.RelaunchAt = -1f;
            existing.LaunchedAt = Time.unscaledTime;
            var spec = new WorkerLaunchSpec
            {
                WorkerId = id,
                Index = index,
                Port = (ushort)(Config.WorkerBasePort + index),
                CommonArgs = CommonArgs(),
            };
            var handle = _host.Launch(spec);
            existing.Handle = handle.State == WorkerHandleState.Failed ? null : handle;
            if (existing.Handle == null) Log("error", $"launching {id} on host '{_host.Name}' failed: {handle.Reason}");
            else Log("info", $"launched worker {id} on {_host.Name}: {handle.Describe}");
        }

        private static bool HandleGone(ManagedWorker m)
        {
            return m.Handle == null || m.Handle.State == WorkerHandleState.Exited || m.Handle.State == WorkerHandleState.Failed;
        }

        private void ReapDeadWorkers()
        {
            float now = Time.unscaledTime;
            // A gateway that stopped heartbeating for good (killed, or its machine is gone) leaves the fleet list, so
            // whoever reads /api/state sees the gateways that exist rather than every one that ever registered.
            foreach (var g in ControlPlane.Gateways.ToList())
            {
                if ((ControlPlane.Now - g.LastHeartbeat).TotalSeconds <= Config.WorkerTimeoutSeconds * 3) continue;
                Log("warn", $"gateway {g.GatewayId} missed heartbeats for {(ControlPlane.Now - g.LastHeartbeat).TotalSeconds:F0}s; removing it from the fleet");
                ControlPlane.UnregisterGateway(g.GatewayId);
            }
            // Forget retirements old enough that their control-plane row is certainly gone.
            foreach (var id in _retired.Where(kv => now - kv.Value > 15f).Select(kv => kv.Key).ToList()) _retired.Remove(id);

            foreach (var w in ControlPlane.Workers.ToList())
            {
                if (w.Status == WorkerStatus.Dead || _retired.ContainsKey(w.WorkerId)) continue;
                if (ControlPlane.IsWorkerAlive(w, Config.WorkerTimeoutSeconds))
                {
                    _seenAlive.Add(w.WorkerId);
                    continue;
                }
                // A row we never saw alive is a leftover from an earlier run (the reset removes it); not a death.
                if (!_seenAlive.Contains(w.WorkerId)) continue;
                _seenAlive.Remove(w.WorkerId);
                Log("warn", $"worker {w.WorkerId} missed heartbeats for {(ControlPlane.Now - w.LastHeartbeat).TotalSeconds:F1}s; declaring dead");
                ControlPlane.UnregisterWorker(w.WorkerId);
                Telemetry.Forget(w.WorkerId);
                Loads.Forget(w.WorkerId);
                var m = _managed.FirstOrDefault(x => x.Id == w.WorkerId);
                if (m != null) OnManagedWorkerDead(m);
                else _retiring.Remove(w.WorkerId);
            }
            // A managed instance the host reports gone is dead even if its last heartbeat is recent.
            foreach (var m in _managed.ToList())
            {
                if (m.Handle == null && m.RelaunchAt < 0f && !m.Retiring && !Config.UseLocalControlPlane)
                {
                    // Launch failed outright (no executable, cloud API error); keep trying at the replacement cadence.
                    m.RelaunchAt = Time.unscaledTime + Config.DeadWorkerReplaceDelaySeconds;
                    continue;
                }
                if (m.Handle != null && HandleGone(m) && m.RelaunchAt < 0f)
                {
                    Log("warn", $"worker {m.Id} instance gone ({m.Handle.Reason})");
                    ControlPlane.UnregisterWorker(m.Id);
                    _seenAlive.Remove(m.Id);
                    OnManagedWorkerDead(m);
                }
            }
        }

        private void OnManagedWorkerDead(ManagedWorker m)
        {
            string reason = m.Handle != null && !string.IsNullOrEmpty(m.Handle.Reason) ? m.Handle.Reason : "instance gone";
            if (m.Handle != null) _host.Kill(m.Handle);
            m.Handle = null;
            if (m.Parked)
            {
                // The host dropped it (the paid hour ended) or the machine vanished. Nothing was running on it.
                _managed.Remove(m);
                _retired[m.Id] = Time.unscaledTime;
                ControlPlane.UnregisterWorker(m.Id);
                Loads.Forget(m.Id);
                Telemetry.Forget(m.Id);
                Log("info", $"parked worker {m.Id} left the idle pool ({reason})");
                return;
            }
            if (m.Retiring)
            {
                // It was on its way out anyway; its leases are orphaned by the unregister and the next pass deals them out.
                _managed.Remove(m);
                _retiring.Remove(m.Id);
                _retired[m.Id] = Time.unscaledTime;
                Log("info", $"worker {m.Id} retired (process gone)");
            }
            else
            {
                m.RelaunchAt = Time.unscaledTime + Config.DeadWorkerReplaceDelaySeconds;
            }
        }

        private void RelaunchIfNeeded()
        {
            if (Config.UseLocalControlPlane) return;
            foreach (var m in _managed)
            {
                if (!m.Parked && m.RelaunchAt >= 0f && Time.unscaledTime >= m.RelaunchAt)
                {
                    Log("info", $"relaunching worker {m.Id}");
                    LaunchWorker(m.Index);
                }
            }
        }

        // ---------------------------------------------------------------------------------------- scaling

        /// <summary>Smallest positive index not in <paramref name="used"/> (indices double as ports and entity-id high bits, so they are reused).</summary>
        public static uint NextFreeIndex(IEnumerable<uint> used)
        {
            var taken = new HashSet<uint>(used);
            uint i = 1;
            while (taken.Contains(i)) i++;
            return i;
        }

        /// <summary>
        /// The workers an assignment pass may deal containers to: the live ones minus those draining, just retired,
        /// or sitting in the idle pool. A parked worker still heartbeats, so only this filter keeps leases off it.
        /// Pure function.
        /// </summary>
        public static List<WorkerInfo> EligibleWorkers(IEnumerable<WorkerInfo> live, ICollection<string> retiring, ICollection<string> retired, ICollection<string> parked)
        {
            return live.Where(w => !retiring.Contains(w.WorkerId) && !retired.Contains(w.WorkerId) && !parked.Contains(w.WorkerId)).ToList();
        }

        /// <summary>
        /// Which parked worker a scale-out takes back first: the most recently parked one. It is the warmest, and on
        /// a billed host it is the one with the most paid time left. Pure function; null when the pool is empty.
        /// </summary>
        public static string PickWorkerToUnpark(IEnumerable<KeyValuePair<string, float>> parkedAt)
        {
            string best = null;
            float bestTime = 0f;
            foreach (var kv in parkedAt)
            {
                if (best == null || kv.Value > bestTime) { best = kv.Key; bestTime = kv.Value; }
            }
            return best;
        }

        /// <summary>Which worker leaves first when scaling down: the highest index among those not already retiring.</summary>
        public static string PickWorkerToRetire(IEnumerable<KeyValuePair<string, uint>> candidates)
        {
            string best = null;
            uint bestIndex = 0;
            foreach (var kv in candidates)
            {
                if (best == null || kv.Value > bestIndex) { best = kv.Key; bestIndex = kv.Value; }
            }
            return best;
        }

        private void ReconcileDesiredCount()
        {
            if (Config.UseLocalControlPlane) return;
            var active = _managed.Where(m => !m.Retiring && !m.Parked).ToList();
            int guard = 0;
            while (active.Count < DesiredWorkers && guard++ < MaxWorkers)
            {
                // The idle pool first: a parked worker is already booted, already paid for, and keeps its id and index.
                if (!Unpark())
                {
                    // Parked workers keep their index, so NextFreeIndex never hands out one that is in the pool.
                    var used = _managed.Select(m => m.Index).Concat(ControlPlane.Workers.Select(w => w.WorkerIndex));
                    uint index = NextFreeIndex(used);
                    Log("info", $"scaling up: launching worker w{index}");
                    LaunchWorker(index);
                }
                active = _managed.Where(m => !m.Retiring && !m.Parked).ToList();
            }
            while (active.Count > DesiredWorkers)
            {
                string id = PickWorkerToRetire(active.Select(m => new KeyValuePair<string, uint>(m.Id, m.Index)));
                if (id == null) break;
                BeginRetire(id, "scaling down");
                active = _managed.Where(m => !m.Retiring && !m.Parked).ToList();
            }
        }

        private void BeginRetire(string workerId, string reason)
        {
            if (_retiring.ContainsKey(workerId)) return;
            _retiring[workerId] = Time.unscaledTime + Config.WorkerDrainTimeoutSeconds;
            var m = _managed.FirstOrDefault(x => x.Id == workerId);
            if (m != null)
            {
                m.Retiring = true;
                m.RetireDeadline = _retiring[workerId];
                m.RelaunchAt = -1f;
            }
            Log("info", $"retiring worker {workerId} ({reason}); draining its containers");
        }

        /// <summary>
        /// Hand a drained worker to the host's idle pool. It keeps its worker id, its index and its control-plane
        /// row (it goes on heartbeating), so unparking it is instant and no new worker can take its index meanwhile.
        /// False when the host refused, in which case the caller kills it as before.
        /// </summary>
        private bool Park(ManagedWorker m)
        {
            _host.Park(m.Handle, IdlePoolSeconds);
            if (m.Handle.State != WorkerHandleState.Parked) return false;
            m.Parked = true;
            m.Retiring = false;
            m.RelaunchAt = -1f;
            m.ParkedAt = Time.unscaledTime;
            float paid = m.Handle.ParkedSecondsRemaining;
            Log("info", $"worker {m.Id} parked ({m.Handle.Describe}){(paid >= 0f ? $"; paid for another {paid:F0}s" : "")}");
            return true;
        }

        /// <summary>
        /// Take the most recently parked worker back into service, if there is one. It is warm, so it starts taking
        /// containers on this very pass instead of after a boot. False when the idle pool is empty or the host has
        /// already dropped what was in it.
        /// </summary>
        private bool Unpark(string workerId = null)
        {
            var pool = ParkedByTime.Where(kv => workerId == null || kv.Key == workerId).ToList();
            while (pool.Count > 0)
            {
                string pick = PickWorkerToUnpark(pool);
                pool.RemoveAll(kv => kv.Key == pick);
                var m = _managed.First(x => x.Id == pick);
                if (m.Handle == null || !_host.Unpark(m.Handle))
                {
                    // The host let it go; forget it and let the caller launch something instead.
                    Log("info", $"worker {m.Id} is no longer in the idle pool; launching a new worker instead");
                    _managed.Remove(m);
                    _retired[m.Id] = Time.unscaledTime;
                    ControlPlane.UnregisterWorker(m.Id);
                    continue;
                }
                m.Parked = false;
                m.ParkedAt = 0f;
                _retired.Remove(m.Id);
                Log("info", $"scaling up: unparking worker {m.Id} from the idle pool ({m.Handle.Describe})");
                _nextPass = 0f;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Take one worker back out of the idle pool from the dashboard, raising the desired count to match. The
        /// ceiling is checked <i>before</i> unparking: raising a desired count that is already at
        /// <see cref="MaxWorkers"/> clamps it back to where it was, and the reconciler would retire the worker that
        /// had just been resumed on the very next pass. False when the pool is empty or the mesh is at its ceiling
        /// (<see cref="AtWorkerCeiling"/> tells the two apart for the HTTP answer).
        /// </summary>
        public bool UnparkWorker(string workerId)
        {
            if (AtWorkerCeiling) return false;
            if (!_managed.Any(m => m.Parked && (workerId == null || m.Id == workerId))) return false;
            if (!Unpark(workerId)) return false;
            DesiredWorkers = Mathf.Clamp(DesiredWorkers + 1, 0, MaxWorkers);
            return true;
        }

        /// <summary>The mesh is already driving towards as many workers as <see cref="MaxWorkers"/> allows, so nothing may be added or resumed.</summary>
        public bool AtWorkerCeiling => DesiredWorkers >= MaxWorkers;

        /// <summary>Drop a parked worker now instead of waiting for the host to let it go (the dashboard's "delete").</summary>
        public bool DeleteParkedWorker(string workerId)
        {
            var m = _managed.FirstOrDefault(x => x.Parked && x.Id == workerId);
            if (m == null) return false;
            Log("info", $"deleting parked worker {m.Id} ({(m.Handle != null ? m.Handle.Describe : "no instance")}) from the dashboard");
            if (m.Handle != null) _host.Kill(m.Handle);
            _managed.Remove(m);
            _retired[m.Id] = Time.unscaledTime;
            ControlPlane.UnregisterWorker(m.Id);
            Loads.Forget(m.Id);
            Telemetry.Forget(m.Id);
            _nextPass = 0f;
            return true;
        }

        private void FinishRetirements()
        {
            if (_retiring.Count == 0) return;
            float now = Time.unscaledTime;
            foreach (var id in _retiring.Keys.ToList())
            {
                bool holdsLease = ControlPlane.Leases.Any(l => l.WorkerId == id && (l.State == LeaseState.Active || l.State == LeaseState.Assigning));
                if (holdsLease) continue; // the assignment pass has not moved its containers yet
                var row = ControlPlane.FindWorker(id);
                bool drained = row == null || row.AuthoritativeCount == 0;
                bool timedOut = now >= _retiring[id];
                if (!drained && !timedOut) continue;
                var managed = _managed.FirstOrDefault(x => x.Id == id);
                bool willPark = managed != null && managed.Handle != null && CanPark && drained;
                Log(timedOut && !drained ? "warn" : "info",
                    timedOut && !drained
                        ? $"worker {id} still reports {row.AuthoritativeCount} authoritative entities after the drain timeout; {(willPark ? "parking" : "killing")} it anyway"
                        : $"worker {id} drained; {(willPark ? "parking it in the idle pool" : "shutting it down")}");
                if (!willPark)
                {
                    // A parked worker keeps its control-plane row and its heartbeat: it is idle, not gone.
                    ControlPlane.UnregisterWorker(id);
                    _seenAlive.Remove(id);
                    Telemetry.Forget(id);
                }
                Loads.Forget(id);
                var m = managed;
                bool parked = willPark && Park(m);
                if (m != null && !parked)
                {
                    if (m.Handle != null) _host.Kill(m.Handle);
                    _managed.Remove(m);
                }
                _retiring.Remove(id);
                if (!parked) _retired[id] = now;
            }
        }

        // ---------------------------------------------------------------------------------------- assignment

        /// <summary>
        /// Deal containers across live workers as evenly as possible, keeping existing assignments where they fit.
        /// Pure function of (containers, live workers, current leases); returns the changes to apply.
        /// </summary>
        /// <param name="keepOrder">Deal <paramref name="containerIds"/> in the order given instead of sorting by id.
        /// A partitioned world lists its containers in Morton order, so each worker's quota is a contiguous run of
        /// spatially neighbouring cells - a compact region it can load as a block.</param>
        public static List<KeyValuePair<string, string>> ComputeAssignment(IList<string> containerIds, IList<WorkerInfo> liveWorkers, IList<LeaseInfo> leases, bool keepOrder = false)
        {
            var changes = new List<KeyValuePair<string, string>>();
            if (liveWorkers.Count == 0 || containerIds.Count == 0) return changes;
            var workers = liveWorkers.OrderBy(w => w.WorkerIndex).ToList();
            var containers = keepOrder ? containerIds.ToList() : containerIds.OrderBy(c => c, StringComparer.Ordinal).ToList();
            int n = containers.Count, k = workers.Count;
            int baseQuota = n / k, extra = n % k;
            var quota = new Dictionary<string, int>();
            for (int i = 0; i < k; i++) quota[workers[i].WorkerId] = baseQuota + (i < extra ? 1 : 0);

            var alive = new HashSet<string>(workers.Select(w => w.WorkerId));
            var assignment = new Dictionary<string, string>();
            foreach (var c in containers)
            {
                var lease = leases.FirstOrDefault(l => l.ContainerId == c);
                string owner = lease != null && lease.State == LeaseState.Active && alive.Contains(lease.WorkerId) ? lease.WorkerId : "";
                assignment[c] = owner;
            }
            var count = workers.ToDictionary(w => w.WorkerId, w => 0);
            var unassigned = new List<string>();
            foreach (var c in containers)
            {
                string owner = assignment[c];
                if (owner == "") { unassigned.Add(c); continue; }
                if (count[owner] >= quota[owner]) { assignment[c] = ""; unassigned.Add(c); continue; }
                count[owner]++;
            }
            foreach (var c in unassigned)
            {
                foreach (var w in workers)
                {
                    if (count[w.WorkerId] < quota[w.WorkerId])
                    {
                        assignment[c] = w.WorkerId;
                        count[w.WorkerId]++;
                        break;
                    }
                }
            }
            foreach (var c in containers)
            {
                var lease = leases.FirstOrDefault(l => l.ContainerId == c);
                string current = lease != null && lease.State == LeaseState.Active ? lease.WorkerId : "";
                if (assignment[c] != current && assignment[c] != "") changes.Add(new KeyValuePair<string, string>(c, assignment[c]));
            }
            return changes;
        }

        private AssignmentInput BuildAssignmentInput(IList<WorkerInfo> eligible)
        {
            Telemetry.CopyOccupancy(_occupancy);
            Telemetry.CopyContainerCost(_containerCost);
            Loads.CopyUtilization(eligible, _utilization);
            WorkerLoadTracker.Attribute(_utilization, ControlPlane.Leases, _occupancy, Config.CostWeights, _containerUtilization);
            _assignmentInput.Utilization = _containerUtilization;
            _assignmentInput.Baked = ContainerRegistry.All;
            _assignmentInput.Runtime = ContainerRegistry.Runtime;
            _assignmentInput.Eligible = eligible;
            _assignmentInput.Leases = ControlPlane.Leases;
            _assignmentInput.Occupancy = _occupancy;
            _assignmentInput.Cost = _containerCost;
            BuildHints();
            _assignmentInput.Hints = _containerHints;
            // The cohesion hints the workers reported: holds as seconds still to run on this orchestrator's clock,
            // and the containers each entity cohesion group spans (docs/cohesion-hints.md).
            Telemetry.CopyHolds(_holds);
            Telemetry.CopyCohesion(_cohesion);
            _assignmentInput.Holds = _holds;
            _assignmentInput.Cohesion = _cohesion;
            _assignmentInput.KeepOrder = ContainerRegistry.IsGridded;
            return _assignmentInput;
        }

        /// <summary>
        /// Collect what the planner is told about each container: the baked hint the container was loaded with
        /// (manifest or scene), overridden by the hint on its control-plane row when one was set while the mesh runs.
        /// Only containers that actually carry a hint are entered, so a world nobody hinted costs an empty table.
        /// </summary>
        private void BuildHints()
        {
            _containerHints.Clear();
            for (int i = 0; i < ContainerRegistry.All.Count; i++)
            {
                var c = ContainerRegistry.All[i];
                if (!c.Hint.IsDefault) _containerHints[c.ContainerId] = c.Hint;
            }
            for (int i = 0; i < ContainerRegistry.Runtime.Count; i++)
            {
                var c = ContainerRegistry.Runtime[i];
                if (!c.Hint.IsDefault) _containerHints[c.ContainerId] = c.Hint;
            }
            var leases = ControlPlane.Leases;
            for (int i = 0; i < leases.Count; i++)
            {
                var l = leases[i];
                if (l.HasHint) _containerHints[l.ContainerId] = l.Hint; // the live value wins over the baked one
            }
        }

        /// <summary>Workers holding a <see cref="ContainerHint.Dedicated"/> container: never the one retired.</summary>
        private bool MayRetire(string workerId)
        {
            if (string.IsNullOrEmpty(workerId)) return true;
            var leases = ControlPlane.Leases;
            for (int i = 0; i < leases.Count; i++)
            {
                var l = leases[i];
                if (l.WorkerId != workerId || !LeaseState.IsOwning(l.State)) continue;
                if (_containerHints.TryGetValue(l.ContainerId, out var hint) && hint.Dedicated) return false;
            }
            return true;
        }

        /// <summary>The policy this pass runs: whatever the game installed, otherwise the cost policy ("auto"), or the baked one when that was chosen explicitly.</summary>
        private IAssignmentPolicy CurrentPolicy()
        {
            if (Policy != null && !ReferenceEquals(Policy, _bakedPolicy) && !ReferenceEquals(Policy, _costPolicy)) return Policy; // the game's own
            if (_policyMode == "auto") Policy = _costPolicy;
            return Policy;
        }

        /// <summary>Shown on the dashboard.</summary>
        public string PolicyName => Policy != null ? Policy.Name : "";

        /// <summary>
        /// Clients waiting to be placed: welcomed by a live gateway with nowhere to spawn yet
        /// (<see cref="GatewayInfo.PendingJoins"/>). Non-zero means somebody is looking at a "world starting" screen.
        /// </summary>
        public int PendingJoins
        {
            get
            {
                int n = 0;
                foreach (var g in ControlPlane.Gateways)
                {
                    if ((ControlPlane.Now - g.LastHeartbeat).TotalSeconds > Config.WorkerTimeoutSeconds) continue;
                    n += (int)g.PendingJoins;
                }
                return n;
            }
        }

        /// <summary>Anyone at all on the mesh: a player or bot on a worker, or a join the gateway is holding.</summary>
        private bool HasDemand => PendingJoins > 0 || ConnectedClients > 0;

        /// <summary>Clients (human or bot) that already own a pawn somewhere, as the workers report them.</summary>
        private int ConnectedClients
        {
            get
            {
                int n = 0;
                foreach (var w in ControlPlane.Workers)
                {
                    if (!ControlPlane.IsWorkerAlive(w, Config.WorkerTimeoutSeconds)) continue;
                    n += (int)(w.PlayerCount + w.BotCount);
                }
                return n;
            }
        }

        /// <summary>
        /// Scale to zero's other half: a mesh at zero workers has nobody to play on, so a join the gateway is
        /// holding raises the desired count at once - no scale-out hold, since the player is already waiting. The
        /// launch path (<see cref="ReconcileDesiredCount"/>) takes a parked worker back before launching a new one,
        /// so the wait is a restart rather than a boot whenever the idle pool still has something in it.
        /// </summary>
        private void WakeForDemand()
        {
            if (!Config.AutoScale || Config.UseLocalControlPlane) return;
            int floor = WorkerScaler.FloorWorkers(MinWorkers, HasDemand);
            if (DesiredWorkers >= floor) return;
            int pending = PendingJoins;
            Log("info", pending > 0
                ? $"waking the mesh: {pending} client(s) waiting to join and {DesiredWorkers} worker(s) running"
                : $"waking the mesh: {ConnectedClients} client(s) connected and {DesiredWorkers} worker(s) running");
            _scaler.Reset();
            SetDesiredWorkers(floor);
        }

        /// <summary>
        /// Grow or shrink the worker count from how busy the workers actually are
        /// (<see cref="NebulaConfig.AutoScale"/>). The decision itself lives in <see cref="WorkerScaler"/>, which
        /// asks the assignment policy what it would do with one worker more or one fewer instead of dividing an
        /// abstract cost by a count; the orchestrator only applies the answer. Launching and retiring go through the
        /// worker host like any other change to the desired count.
        /// </summary>
        private void AutoScale(AssignmentInput input, IAssignmentPolicy policy)
        {
            if (!Config.AutoScale || Config.UseLocalControlPlane)
            {
                _scaler.Reset();
                _scale = new ScaleDecision { BlockedBy = "", RetireWorkerId = "", Reason = Config.AutoScale ? "single process" : "autoscaling is off" };
                return;
            }
            var settings = new ScaleSettings
            {
                ScaleOutUtilization = Config.ScaleOutUtilization,
                ScaleInUtilization = Config.ScaleInUtilization,
                HoldSeconds = Config.ScaleHoldSeconds,
                MinGain = Config.ScaleMinGain,
                // While anyone is connected or waiting to join, one worker is the floor whatever MinWorkers says:
                // scale to zero is for an idle mesh, and the scaler must not fight WakeForDemand.
                MinWorkers = WorkerScaler.FloorWorkers(MinWorkers, HasDemand),
                MaxWorkers = MaxWorkers,
            };
            // A launch or a drain that has not landed yet makes the measurement meaningless: hold everything. A
            // parked worker is in the idle pool, not in service, so it is not part of the count being driven to.
            _meshScratch.Clear();
            foreach (var m in _managed) _meshScratch.Add(new WorkerScaler.MeshMember { Retiring = m.Retiring, Parked = m.Parked });
            bool inFlight = WorkerScaler.IsInFlight(_retiring.Count, _meshScratch, DesiredWorkers);
            _scale = _scaler.Evaluate(Time.unscaledTime, _utilization, input, policy, DesiredWorkers, settings, inFlight);
            switch (_scale.Action)
            {
                case ScaleAction.Grow:
                    Log("info", "autoscale: " + _scale.Reason);
                    SetDesiredWorkers(DesiredWorkers + 1);
                    break;
                case ScaleAction.Shrink:
                    Log("info", "autoscale: " + _scale.Reason);
                    if (!RemoveWorker(_scale.RetireWorkerId)) SetDesiredWorkers(DesiredWorkers - 1);
                    break;
            }
        }

        /// <summary>
        /// Create a runtime container (dashboard, tests, tooling): a lease row carrying <paramref name="bounds"/>
        /// (absolute coordinates), unassigned until the next pass deals it. The normal path is a worker asking for
        /// one next to its entities (<see cref="NebulaWorker.RequestRuntimeContainer"/>). Null on success, otherwise the reason.
        /// </summary>
        public string EnsureRuntimeContainer(ulong id, Bounds bounds)
        {
            if (bounds.size.x <= 0f || bounds.size.y <= 0f || bounds.size.z <= 0f) return "size must be positive on every axis";
            string containerId = ContainerRegistry.RuntimeContainerId(id);
            if (ControlPlane.FindLease(containerId) != null) return null;
            ControlPlane.EnsureRuntimeContainer(containerId, bounds, "");
            Log("info", $"runtime container {containerId} created at {bounds.center} size {bounds.size}");
            _nextPass = 0f;
            return null;
        }

        /// <summary>Delete a runtime container's lease row, so every process forgets the box. Null on success, otherwise the reason.</summary>
        public string RemoveRuntimeContainer(ulong id)
        {
            string containerId = ContainerRegistry.RuntimeContainerId(id);
            var lease = ControlPlane.FindLease(containerId);
            if (lease == null) return $"unknown runtime container '{containerId}'";
            if (!lease.HasBounds) return $"'{containerId}' is not a runtime container";
            ControlPlane.RemoveContainer(containerId);
            Log("info", $"runtime container {containerId} removed");
            return null;
        }

        /// <summary>
        /// Runtime containers (leases carrying a box) stay with the worker that asked for them; the game placed
        /// them next to the entities that need them. Only one whose owner is gone or retiring moves, to the eligible
        /// worker holding the fewest runtime containers. Pure function; returns the changes to apply.
        /// </summary>
        public static List<KeyValuePair<string, string>> ComputeRuntimeAssignment(IReadOnlyList<LeaseInfo> leases, IList<WorkerInfo> eligible)
        {
            var changes = new List<KeyValuePair<string, string>>();
            if (eligible.Count == 0) return changes;
            var ordered = eligible.OrderBy(w => w.WorkerIndex).ToList();
            var load = ordered.ToDictionary(w => w.WorkerId, w => 0);
            var orphans = new List<string>();
            for (int i = 0; i < leases.Count; i++)
            {
                var l = leases[i];
                if (!l.HasBounds) continue;
                if (l.State == LeaseState.Active && load.ContainsKey(l.WorkerId)) load[l.WorkerId]++;
                else orphans.Add(l.ContainerId);
            }
            orphans.Sort(StringComparer.Ordinal);
            foreach (var id in orphans)
            {
                string target = null;
                foreach (var w in ordered)
                    if (target == null || load[w.WorkerId] < load[target]) target = w.WorkerId;
                load[target]++;
                changes.Add(new KeyValuePair<string, string>(id, target));
            }
            return changes;
        }

        private void OnRuntimeContainersChanged(Container c) => _geometryDirty = true;

        private List<WorkerInfo> LiveWorkers() =>
            ControlPlane.Workers.Where(w => w.Status != WorkerStatus.Dead && ControlPlane.IsWorkerAlive(w, Config.WorkerTimeoutSeconds)).ToList();

        private void Rebalance()
        {
            var live = LiveWorkers();
            foreach (var w in live)
            {
                _seenAlive.Add(w.WorkerId);
                Loads.Sample(w.WorkerId, w.TickMs);
            }
            Loads.Expire();
            // Retiring workers stay alive for the drain but receive nothing new.
            var parkedIds = new HashSet<string>(_managed.Where(m => m.Parked).Select(m => m.Id), StringComparer.Ordinal);
            var eligible = EligibleWorkers(live, _retiring.Keys, _retired.Keys, parkedIds);
            var ids = ContainerRegistry.All.Select(c => c.ContainerId).ToList();
            if (eligible.Count == 0 && _retiring.Count > 0)
            {
                // Scaling to zero: there is nobody to hand over to, so the leases are released and the retirees drain by timeout.
                foreach (var l in ControlPlane.Leases.ToList())
                {
                    if (_retiring.ContainsKey(l.WorkerId) && l.State != LeaseState.Orphaned)
                    {
                        Log("warn", $"no eligible worker for {l.ContainerId}; releasing its lease");
                        ControlPlane.ReleaseContainer(l.ContainerId);
                    }
                }
                return;
            }
            // Carried containers (dynamic, id 'label#netId') follow their carrier by default and are not dealt. One
            // that was pinned to a worker that is gone or retiring falls back to following its carrier.
            foreach (var l in ControlPlane.Leases.ToList())
            {
                if (!ContainerRegistry.IsDynamicId(l.ContainerId) || l.State != LeaseState.Pinned) continue;
                if (eligible.Any(w => w.WorkerId == l.WorkerId)) continue;
                Log("info", $"unpinning {l.ContainerId} from {l.WorkerId} (not eligible); it follows its carrier again");
                ControlPlane.SetLeaseState(l.ContainerId, LeaseState.Active);
            }
            var input = BuildAssignmentInput(eligible);
            var policy = CurrentPolicy();
            TotalCost = _costPolicy.TotalCost(input);
            AutoScale(input, policy);
            var changes = policy.Compute(input);
            // A held container does not move, whichever policy computed the change (docs/cohesion-hints.md, D8).
            // The built-in policies already leave held items alone; this is the backstop for a game's own policy.
            int deferred = input.DropHeldChanges(changes);
            if (deferred > 0) Log("info", $"{deferred} move(s) deferred: their containers are held");
            // The cost policy reports what it could not honour (too few workers for the dedicated containers, a
            // group that does not fit one worker).
            string note = ReferenceEquals(policy, _costPolicy) ? _costPolicy.Note : "";
            if (note != _hintNote)
            {
                _hintNote = note;
                if (note != "") Log("warn", "hints: " + note);
            }
            // What the planner could not relieve, and why it may not split it (docs/cohesion-rebalancing.md). Kept
            // for the dashboard whether or not anything moved: saturation is exactly the case where nothing does.
            _saturated.Clear();
            if (policy is IExplainsAssignment reporter && reporter.Saturated != null) _saturated.AddRange(reporter.Saturated);
            if (changes.Count == 0) return;
            Rebalances++;
            _log.Clear();
            // The explanation the policy attached to each move, by container, so the log line and the dashboard say
            // which boundary the cut fell on and what constrained it.
            _moves.Clear();
            var why = new Dictionary<string, string>(StringComparer.Ordinal);
            if (policy is IExplainsAssignment explains && explains.Moves != null)
            {
                // A move whose container was held is dropped above, so only the moves actually applied are shown.
                var applied = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in changes) applied.Add(kv.Key);
                foreach (var move in explains.Moves) if (applied.Contains(move.ContainerId)) _moves.Add(move);
                for (int i = 0; i < _moves.Count; i++) why[_moves[i].ContainerId] = _moves[i].Reason ?? "";
            }
            foreach (var kv in changes)
            {
                ControlPlane.AssignContainer(kv.Key, kv.Value);
                string reason = why.TryGetValue(kv.Key, out string r) ? r : "";
                var line = $"assign {kv.Key} -> {kv.Value}" + (reason == "" ? "" : " (" + reason + ")");
                _log.Add(line);
                Log("info", line);
            }
        }

        // ---------------------------------------------------------------------------------------- dashboard

        // ---------------------------------------------------------------------------------------- scope lifecycle

        /// <summary>
        /// The scope state machine, one step per pass. The orchestrator is its single writer: it already aggregates
        /// leases and load, and a scope's parts are leases. Every step is a control-plane write, so every role sees
        /// the same sequence through the ordinary document. Design of record: <c>docs/scope-lifecycle.md</c>.
        /// </summary>
        private void SweepScopes()
        {
            var scopes = ControlPlane.Scopes;
            if (scopes == null || scopes.Count == 0) return;
            // The per-container counts the workers report. Rebalance refreshes these too, but it can return early
            // (nothing to plan), and a scope must not be judged on a stale reading of what is inside it.
            Telemetry.CopyOccupancy(_occupancy);
            for (int i = 0; i < scopes.Count; i++)
            {
                var scope = scopes[i];
                if (scope == null || string.IsNullOrEmpty(scope.ScopeKey)) continue;
                double elapsed = Math.Max(0.0, (ControlPlane.Now - scope.StateSince).TotalSeconds);
                switch (scope.State)
                {
                    case ScopeState.Active:
                        JudgeScope(scope);
                        break;
                    case ScopeState.Retiring:
                    {
                        // Every part checkpointed and emptied itself, or the deadline passed. Only now are the lease
                        // rows deleted: the checkpoint has to finish while the owner still holds the box.
                        if (ScopeLifecycle.NextState(scope, elapsed, out bool timedOut) == null) break;
                        if (timedOut) Log("warn", $"scope '{scope.ScopeKey}' did not finish checkpointing within {ScopeLifecycle.StepTimeoutSeconds:0} s; retiring it anyway");
                        int saved = scope.AckedCount(ScopePhase.Checkpointed);
                        int parts = scope.ContainerIds.Count;
                        for (int c = 0; c < scope.ContainerIds.Count; c++) ControlPlane.RemoveContainer(scope.ContainerIds[c]);
                        ControlPlane.SetScopeState(scope.ScopeKey, ScopeState.Retired);
                        Log("info", $"scope '{scope.ScopeKey}' retired: {parts} container(s) released, {saved} persistent entities checkpointed");
                        break;
                    }
                    case ScopeState.Restoring:
                    {
                        if (ScopeLifecycle.NextState(scope, elapsed, out bool timedOut) == null) break;
                        if (timedOut) Log("warn", $"scope '{scope.ScopeKey}' did not finish restoring within {ScopeLifecycle.StepTimeoutSeconds:0} s; admitting clients anyway");
                        int restored = scope.AckedCount(ScopePhase.Restored);
                        ControlPlane.SetScopeState(scope.ScopeKey, ScopeState.Active);
                        Log("info", $"scope '{scope.ScopeKey}' restored {restored} persistent entities and admits clients again");
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Derive the capacity signal from the cost rows and publish what changed onto the lease rows, so every
        /// gateway can answer "is this target at capacity" from the document it already mirrors, with no RPC
        /// (docs/capacity-admission.md). Only rows whose reading moved are written, and the write never stamps the
        /// lease's <c>UpdatedAt</c>: that is the idle clock the scope lifecycle retires on.
        /// </summary>
        private void PublishCapacity()
        {
            float threshold = Config.CapacitySaturation;
            Telemetry.CapacitySaturation = threshold;
            // Rebalance refreshes the cost rows too, but it can return early with nothing to plan, and a capacity
            // signal must not be derived from a stale reading of what the workers are carrying.
            Telemetry.CopyContainerCost(_containerCost);
            NebulaCapacity.Derive(_containerCost, threshold, _capacity);
            // And what the planner says it cannot relieve by moving anything (docs/cohesion-rebalancing.md): a
            // container held by a cohesion or affinity group, under a hold, without an authored boundary or asking
            // for a worker of its own is at capacity however cheap its own cost row looks, because no rebalance
            // is coming to save it.
            NebulaCapacity.Apply(_saturated, threshold, _capacity);
            var leases = ControlPlane.Leases;
            for (int i = 0; i < leases.Count; i++)
            {
                var lease = leases[i];
                string id = lease.ContainerId;
                if (string.IsNullOrEmpty(id)) continue;
                // Nothing reported for this container lately: the last published reading stands rather than being
                // cleared, so a worker that missed one telemetry post cannot open a full station.
                if (!_capacity.TryGetValue(id, out var info) || !info.Known) continue;
                bool moved = !lease.HasCapacity || lease.AtCapacity != info.AtCapacity || lease.Dominant != info.Dominant ||
                             lease.SaturationCause != info.Cause ||
                             Math.Abs(lease.Saturation - info.Saturation) >= CapacityPublishStep;
                if (!moved) continue;
                if (lease.HasCapacity && lease.AtCapacity != info.AtCapacity)
                    Log("info", info.AtCapacity
                        ? $"container {id} is at capacity ({info.DominantName} at {info.Saturation * 100f:0} % of its budget); joins and transfers into it go to the admission hook"
                        : $"container {id} is below capacity again ({info.DominantName} at {info.Saturation * 100f:0} %)");
                ControlPlane.SetContainerCapacity(id, info.Saturation, info.Dominant, info.AtCapacity, info.Cause);
            }
        }

        /// <summary>How far a saturation reading has to move before it is worth another control-plane document.</summary>
        private const float CapacityPublishStep = 0.02f;

        /// <summary>Ask the retire policy about one active scope (<see cref="ScopeLifecycle.ShouldRetire"/>).</summary>
        private void JudgeScope(ScopeInfo scope)
        {
            ScopeLifecycle.Occupancy(scope, _occupancy, out int entities, out int players);
            var context = new ScopeRetireContext
            {
                Scope = scope,
                IdleSeconds = ScopeLifecycle.IdleSeconds(ControlPlane, scope),
                Entities = entities,
                Players = players,
                RetireAfterSeconds = Config.ScopeIdleRetireSeconds,
            };
            bool retire;
            // The policy is the game's code. One that throws must not stop the sweep or retire the scope by accident.
            try { retire = (ScopeLifecycle.ShouldRetire ?? ScopeLifecycle.RetireWhenIdle)(context); }
            catch (Exception e)
            {
                Log("error", $"the scope retire policy threw for '{scope.ScopeKey}': {e.Message}; keeping the scope");
                return;
            }
            if (!retire) return;
            ControlPlane.SetScopeState(scope.ScopeKey, ScopeState.Retiring);
            Log("info", $"scope '{scope.ScopeKey}' has been idle for {context.IdleSeconds:0} s; retiring it");
        }

        private void Log(string level, string message)
        {
            switch (level)
            {
                case "warn": NebulaLog.Warn(message); break;
                case "error": NebulaLog.Error(message); break;
                default: NebulaLog.Info(message); break;
            }
            _events.Add(new OrchestratorEvent { Time = DateTime.UtcNow, Level = level, Message = message });
            if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
        }

        private OrchestratorHttpServer.Response HandleCommand(OrchestratorHttpServer.Request req)
        {
            string path = req.Path.TrimEnd('/');
            // Workers and gateways: control-plane writes and the persistence store.
            if (ControlPlane is ControlPlaneHost host && host.TryHandle(req, out var hosted)) return hosted;
            if (path.StartsWith(PersistenceHost.Prefix + "/", StringComparison.Ordinal))
            {
                var store = StoreHost;
                if (store == null) return OrchestratorHttpServer.Response.Error(409, "persistence is off");
                return store.TryHandle(req, out var answer) ? answer : OrchestratorHttpServer.Response.Error(404, "not found");
            }
            if (req.Method == "GET" && path == "/api/state")
            {
                return OrchestratorHttpServer.Response.Json(200, BuildStateJson());
            }
            if (req.Method == "GET" && path == "/api/persistence/records")
            {
                var editor = PersistenceTab;
                return editor == null ? OrchestratorHttpServer.Response.Error(409, "persistence is off") : editor.Records();
            }
            if (req.Method != "POST") return OrchestratorHttpServer.Response.Error(405, "method not allowed");
            switch (path)
            {
                case "/api/desired":
                {
                    if (!OrchestratorHttpServer.TryGetInt(req.Body, "desired", out int desired)) return OrchestratorHttpServer.Response.Error(400, "body must be {\"desired\": n}");
                    SetDesiredWorkers(desired);
                    break;
                }
                case "/api/settings":
                {
                    string key = OrchestratorHttpServer.GetString(req.Body, "key");
                    string value = OrchestratorHttpServer.GetString(req.Body, "value");
                    if (!SetSetting(key, value)) return OrchestratorHttpServer.Response.Error(400, "body must be {\"key\": \"name\", \"value\": \"text\"}");
                    break;
                }
                case "/api/scale/limits":
                {
                    // The autoscale band, live: min may be 0 (scale to zero), max is capped by the 16-bit index space.
                    if (!OrchestratorHttpServer.TryGetInt(req.Body, "min", out int min) || !OrchestratorHttpServer.TryGetInt(req.Body, "max", out int max))
                        return OrchestratorHttpServer.Response.Error(400, "body must be {\"min\": n, \"max\": n}");
                    if (max < 1 || max > MaxWorkersLimit || min < 0 || min > max) return OrchestratorHttpServer.Response.Error(400, $"0 <= min <= max and 1 <= max <= {MaxWorkersLimit}");
                    Config.MinWorkers = min;
                    Config.MaxWorkers = max;
                    Log("info", $"autoscale band set to {min}..{max} from the dashboard");
                    if (DesiredWorkers < min) SetDesiredWorkers(min);
                    else if (DesiredWorkers > max) SetDesiredWorkers(max);
                    break;
                }
                case "/api/workers/add":
                    if (AtWorkerCeiling) return OrchestratorHttpServer.Response.Error(409, $"at most {MaxWorkers} workers");
                    AddWorker();
                    break;
                case "/api/gateways/drain":
                {
                    // Whoever runs the gateway fleet takes a gateway out of service: it refuses new clients and asks
                    // its clients to reconnect (to another gateway, through a load balancer). {"id": "gw2", "draining": false} cancels.
                    string gatewayId = OrchestratorHttpServer.GetString(req.Body, "id");
                    if (string.IsNullOrEmpty(gatewayId)) return OrchestratorHttpServer.Response.Error(400, "body must be {\"id\": \"gateway id\", \"draining\": true|false}");
                    if (ControlPlane.FindGateway(gatewayId) == null) return OrchestratorHttpServer.Response.Error(404, $"no gateway '{gatewayId}'");
                    bool draining = !OrchestratorHttpServer.TryGetBool(req.Body, "draining", out bool flag) || flag;
                    ControlPlane.SetGatewayDraining(gatewayId, draining);
                    Log("info", draining ? $"gateway {gatewayId} asked to drain" : $"gateway {gatewayId} drain cancelled");
                    break;
                }
                case "/api/containers/ensure":
                {
                    if (!PersistenceJson.TryParseObject(req.Body, out var body, out string parseError)) return OrchestratorHttpServer.Response.Error(400, parseError);
                    if (!body.TryGetValue("id", out var idValue) || !ulong.TryParse(PersistenceJson.AsString(idValue), out ulong id)) return OrchestratorHttpServer.Response.Error(400, "body must be {\"id\": <unsigned 64-bit>, \"center\": [x, y, z], \"size\": [x, y, z]}");
                    if (!body.TryGetValue("center", out var centerValue) || !PersistenceJson.TryNumbers(centerValue, 3, out var c)) return OrchestratorHttpServer.Response.Error(400, "\"center\" must be [x, y, z]");
                    if (!body.TryGetValue("size", out var sizeValue) || !PersistenceJson.TryNumbers(sizeValue, 3, out var s)) return OrchestratorHttpServer.Response.Error(400, "\"size\" must be [x, y, z]");
                    string error = EnsureRuntimeContainer(id, new Bounds(new Vector3((float)c[0], (float)c[1], (float)c[2]), new Vector3((float)s[0], (float)s[1], (float)s[2])));
                    if (error != null) return OrchestratorHttpServer.Response.Error(400, error);
                    break;
                }
                case "/api/containers/remove":
                {
                    if (!ulong.TryParse(OrchestratorHttpServer.GetString(req.Body, "id"), out ulong id)) return OrchestratorHttpServer.Response.Error(400, "body must be {\"id\": <unsigned 64-bit>}");
                    string error = RemoveRuntimeContainer(id);
                    if (error != null) return OrchestratorHttpServer.Response.Error(404, error);
                    break;
                }
                case "/api/workers/remove":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "workerId");
                    if (!RemoveWorker(id)) return OrchestratorHttpServer.Response.Error(404, string.IsNullOrEmpty(id) ? "no workers to remove" : $"unknown or already retiring worker '{id}'");
                    break;
                }
                case "/api/workers/unpark":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "workerId");
                    // The same answer /api/workers/add gives: resuming a parked worker adds one to the mesh.
                    if (AtWorkerCeiling) return OrchestratorHttpServer.Response.Error(409, $"at most {MaxWorkers} workers");
                    if (!UnparkWorker(string.IsNullOrEmpty(id) ? null : id))
                        return OrchestratorHttpServer.Response.Error(404, string.IsNullOrEmpty(id) ? "the idle pool is empty" : $"worker '{id}' is not parked");
                    break;
                }
                case "/api/workers/delete-parked":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "workerId");
                    if (!DeleteParkedWorker(id)) return OrchestratorHttpServer.Response.Error(404, $"worker '{id}' is not parked");
                    break;
                }
                case "/api/workers/kill":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "workerId");
                    if (!KillWorker(id)) return OrchestratorHttpServer.Response.Error(404, $"no managed process for worker '{id}'");
                    break;
                }
                case "/api/rebalance":
                    RequestRebalance();
                    break;
                case "/api/persistence/clear":
                {
                    if (Persistence == null) return OrchestratorHttpServer.Response.Error(409, "persistence is off");
                    Persistence.Clear();
                    Log("warn", "persistence cleared from the dashboard: every saved entity is gone");
                    break;
                }
                // The Persistence tab answers with the record it wrote, so these return the editor's document as is.
                case "/api/persistence/record":
                {
                    var editor = PersistenceTab;
                    if (editor == null) return OrchestratorHttpServer.Response.Error(409, "persistence is off");
                    var response = editor.Update(req.Body);
                    if (response.Status == 200) Log("info", $"persisted record edited from the dashboard: {OrchestratorHttpServer.GetString(req.Body, "key")}");
                    return response;
                }
                case "/api/persistence/delete":
                {
                    var editor = PersistenceTab;
                    if (editor == null) return OrchestratorHttpServer.Response.Error(409, "persistence is off");
                    var response = editor.Delete(req.Body);
                    if (response.Status == 200) Log("warn", $"persisted record deleted from the dashboard: {OrchestratorHttpServer.GetString(req.Body, "key")}");
                    return response;
                }
                case "/api/persistence/duplicate":
                {
                    var editor = PersistenceTab;
                    if (editor == null) return OrchestratorHttpServer.Response.Error(409, "persistence is off");
                    var response = editor.Duplicate(req.Body);
                    if (response.Status == 200) Log("info", $"persisted record duplicated from the dashboard: {OrchestratorHttpServer.GetString(req.Body, "newKey")}");
                    return response;
                }
                case "/api/containers/hint":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "containerId");
                    if (string.IsNullOrEmpty(id)) return OrchestratorHttpServer.Response.Error(400, "body must be {\"containerId\": \"id\", \"hint\": \"x2,group=g,seam=0.5,dedicated\"}");
                    string error = SetContainerHint(id, ContainerHint.Parse(OrchestratorHttpServer.GetString(req.Body, "hint")));
                    if (error != null) return OrchestratorHttpServer.Response.Error(400, error);
                    break;
                }
                case "/api/containers/pin":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "containerId");
                    string workerId = OrchestratorHttpServer.GetString(req.Body, "workerId");
                    string error = PinContainer(id, workerId);
                    if (error != null) return OrchestratorHttpServer.Response.Error(400, error);
                    break;
                }
                case "/api/containers/unpin":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "containerId");
                    string error = UnpinContainer(id);
                    if (error != null) return OrchestratorHttpServer.Response.Error(400, error);
                    break;
                }
                default:
                    return OrchestratorHttpServer.Response.Error(404, "unknown endpoint");
            }
            PublishState();
            return OrchestratorHttpServer.Response.Json(200, $"{{\"ok\":true,\"desired\":{DesiredWorkers}}}");
        }

        /// <summary>
        /// Tell the planner something about a container while the mesh runs (<see cref="ContainerHint"/>): the value
        /// goes on its control-plane row, so it outlives this orchestrator and beats whatever was baked. Passing
        /// <see cref="ContainerHint.Default"/> clears it and lets the baked hint stand again. Null on success,
        /// otherwise the reason. The next pass acts on it.
        /// </summary>
        public string SetContainerHint(string containerId, in ContainerHint hint)
        {
            if (string.IsNullOrEmpty(containerId)) return "a container id is required";
            bool known = ContainerRegistry.FindById(containerId) != null || ControlPlane.FindLease(containerId) != null;
            if (!known) return $"unknown container '{containerId}'";
            ControlPlane.SetContainerHint(containerId, hint);
            Log("info", $"hint for {containerId}: {(hint.IsDefault ? "cleared" : hint.ToString())}");
            RequestRebalance();
            return null;
        }

        /// <summary>
        /// Give a carried container (a ship's interior) a worker of its own: the lease is assigned to
        /// <paramref name="workerId"/> and marked <see cref="LeaseState.Pinned"/>, so it stops following its carrier.
        /// The pinned worker receives a permanent ghost of the carrier and simulates whatever is inside. Null on
        /// success, otherwise the reason.
        /// </summary>
        public string PinContainer(string containerId, string workerId)
        {
            if (!ContainerRegistry.IsDynamicId(containerId)) return "only carried containers ('label#netId') can be pinned";
            if (ControlPlane.FindLease(containerId) == null) return $"unknown container '{containerId}'";
            var w = ControlPlane.FindWorker(workerId);
            if (w == null || !ControlPlane.IsWorkerAlive(w, Config.WorkerTimeoutSeconds) || _retiring.ContainsKey(workerId)) return $"worker '{workerId}' is not live";
            ControlPlane.PinContainer(containerId, workerId);
            Log("info", $"pinned {containerId} -> {workerId}");
            return null;
        }

        /// <summary>Let a pinned carried container follow its carrier again. Null on success, otherwise the reason.</summary>
        public string UnpinContainer(string containerId)
        {
            var lease = ControlPlane.FindLease(containerId);
            if (lease == null) return $"unknown container '{containerId}'";
            if (lease.State != LeaseState.Pinned) return $"'{containerId}' is not pinned";
            ControlPlane.SetLeaseState(containerId, LeaseState.Active);
            Log("info", $"unpinned {containerId}; it follows its carrier again");
            return null;
        }

        private void PublishState()
        {
            _nextPublish = Time.unscaledTime + 0.5f;
            if (_geometryDirty)
            {
                _geometryDirty = false;
                Telemetry.PublishGeometry(MeshTelemetry.BuildGeometryJson(Config));
            }
            _http?.PublishState(BuildStateJson());
        }

        private static void WriteContainerState(JsonWriter w, Container c, IReadOnlyList<LeaseInfo> leases, IReadOnlyList<WorkerInfo> workers, IReadOnlyDictionary<string, ContainerHint> hints)
        {
            var l = leases.FirstOrDefault(x => x.ContainerId == c.ContainerId);
            string owner = l != null && (l.State == LeaseState.Active || l.State == LeaseState.Draining || l.State == LeaseState.Assigning) ? l.WorkerId : "";
            var ownerRow = owner != "" ? workers.FirstOrDefault(r => r.WorkerId == owner) : null;
            w.BeginObject();
            w.Prop("id", c.ContainerId);
            w.Prop("worker", owner);
            w.Prop("workerIndex", ownerRow != null ? (int)ownerRow.WorkerIndex : -1);
            w.Prop("color", "#" + ColorUtility.ToHtmlStringRGB(NebulaDebugOverlay.ColorForWorker(ownerRow != null ? (ushort)ownerRow.WorkerIndex : ushort.MaxValue)));
            w.Prop("epoch", l != null ? l.Epoch : 0UL);
            w.Prop("state", l != null ? l.State : "missing");
            // The hint as the planner sees it this pass (baked, or the control-plane row when one was set).
            var hint = hints != null && hints.TryGetValue(c.ContainerId, out var h) ? h : c.Hint;
            w.Prop("hint", hint.IsDefault ? "" : hint.ToString());
            w.Prop("hintMultiplier", hint.EffectiveMultiplier);
            w.Prop("hintGroup", hint.Group);
            w.Prop("hintSeam", hint.EffectiveSeamCost);
            w.Prop("hintDedicated", hint.Dedicated);
            if (c.IsRuntime) w.Prop("runtime", true);
            else if (ContainerRegistry.IsGridded)
            {
                w.Prop("cell", $"{c.Cell.x},{c.Cell.y},{c.Cell.z}");
                w.Prop("isCell", c.IsCell);
            }
            w.EndObject();
        }

        /// <summary>
        /// The cohesion block of the state document (<c>docs/cohesion-hints.md</c>): the containers under a hold
        /// with the seconds each has to run, the entity cohesion groups the workers report with the containers they
        /// span, and the groups the planner had to keep whole although they do not fit one worker. Read from the
        /// last pass's snapshots, so the dashboard shows what the planner actually saw.
        /// </summary>
        private void WriteCohesionState(JsonWriter w)
        {
            w.Key("cohesion");
            w.BeginObject();
            w.Key("holds");
            w.BeginArray();
            foreach (var kv in _holds)
            {
                w.BeginObject();
                w.Prop("container", kv.Key);
                w.Prop("seconds", Math.Round(kv.Value, 1));
                w.Prop("worker", Telemetry.HolderOf(kv.Key));
                w.EndObject();
            }
            w.EndArray();
            w.Key("groups");
            w.BeginArray();
            for (int i = 0; i < _cohesion.Count; i++)
            {
                var group = _cohesion[i];
                w.BeginObject();
                w.Prop("group", (long)group.Group);
                w.Prop("members", group.Members);
                w.Key("containers");
                w.BeginArray();
                for (int c = 0; c < group.Containers.Count; c++) w.Value(group.Containers[c]);
                w.EndArray();
                w.Key("workers");
                w.BeginArray();
                for (int c = 0; c < group.Workers.Count; c++) w.Value(group.Workers[c]);
                w.EndArray();
                // More than one worker right now: a handover is in flight, or the group could not be moved as a unit.
                w.Prop("split", group.Workers.Count > 1);
                w.EndObject();
            }
            w.EndArray();
            w.Key("unsplittable");
            w.BeginArray();
            var oversize = _costPolicy != null ? _costPolicy.Unsplittable : null;
            if (oversize != null)
                for (int i = 0; i < oversize.Count; i++)
                {
                    var row = oversize[i];
                    w.BeginObject();
                    w.Prop("group", row.Group ?? "");
                    w.Prop("utilization", Math.Round(row.Utilization, 3));
                    w.Prop("limit", row.Limit);
                    w.Key("containers");
                    w.BeginArray();
                    if (row.Containers != null) for (int c = 0; c < row.Containers.Length; c++) w.Value(row.Containers[c]);
                    w.EndArray();
                    w.Prop("reason", row.ToString());
                    w.EndObject();
                }
            w.EndArray();
            w.EndObject();
        }

        /// <summary>
        /// The assignment block of the state document (<c>docs/cohesion-rebalancing.md</c>): the last pass's moves
        /// with the sentence that explains each one, and what the planner could not relieve with the constraint that
        /// stops it. Both are empty for a policy that does not explain itself.
        /// </summary>
        private void WriteAssignmentState(JsonWriter w)
        {
            w.Key("assignment");
            w.BeginObject();
            w.Prop("policy", PolicyName);
            w.Prop("rebalances", Rebalances);
            w.Key("moves");
            w.BeginArray();
            for (int i = 0; i < _moves.Count; i++)
            {
                w.BeginObject();
                w.Prop("container", _moves[i].ContainerId ?? "");
                w.Prop("from", _moves[i].From ?? "");
                w.Prop("to", _moves[i].To ?? "");
                w.Prop("reason", _moves[i].Reason ?? "");
                w.EndObject();
            }
            w.EndArray();
            w.Key("saturated");
            w.BeginArray();
            for (int i = 0; i < _saturated.Count; i++)
            {
                var row = _saturated[i];
                w.BeginObject();
                w.Prop("container", row.ContainerId ?? "");
                w.Prop("scope", row.ScopeKey ?? "");
                w.Prop("worker", row.WorkerId ?? "");
                w.Prop("utilization", Math.Round(row.Utilization, 3));
                w.Prop("cause", SaturationReport.NameOf(row.Cause));
                w.Prop("reason", row.Reason ?? "");
                w.Key("containers");
                w.BeginArray();
                if (row.Containers != null) for (int c = 0; c < row.Containers.Length; c++) w.Value(row.Containers[c]);
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        /// <summary>Everything the dashboard shows, as one JSON document.</summary>
        public string BuildStateJson()
        {
            var sb = _json;
            sb.Clear();
            var w = new JsonWriter(sb);
            float now = Time.unscaledTime;
            var leases = ControlPlane.Leases;
            var workers = ControlPlane.Workers;

            w.BeginObject();
            w.Prop("orchestratorId", OrchestratorId);
            w.Prop("serverTimeUtc", DateTime.UtcNow.ToString("o"));
            w.Prop("controlPlaneConnected", ControlPlane.IsConnected);
            w.Prop("localControlPlane", Config.UseLocalControlPlane);
            w.Prop("controlPlaneStorage", ControlPlane is ControlPlaneHost cph ? cph.StorageBackend : Config.UseLocalControlPlane ? "memory" : "");
            w.Prop("host", _host.Name);
            w.Prop("hostReady", _host.IsReady);
            w.Prop("hostError", _host.InitializationError ?? "");
            w.Prop("hostBootSeconds", _host.TypicalBootSeconds);
            w.Prop("hostParks", _host.SupportsParking);
            w.Prop("idlePoolSeconds", IdlePoolSeconds);
            // The address clients connect to (the one the spawned gateway registers).
            w.Prop("gatewayAddress", $"{Config.GatewayAddress}:{Config.GatewayPort}");
            w.Prop("desiredWorkers", DesiredWorkers);
            w.Prop("minWorkers", MinWorkers);
            w.Prop("maxWorkers", MaxWorkers);
            w.Prop("policy", PolicyName);
            w.Prop("totalCost", TotalCost);
            w.Prop("autoScale", Config.AutoScale);
            w.Prop("runtimeContainers", ContainerRegistry.Runtime.Count);
            w.Prop("rebalances", Rebalances);
            w.Prop("workerTimeoutSeconds", Config.WorkerTimeoutSeconds);

            // Scaling: what the scaler saw and what it decided, so the dashboard can explain why nothing is happening.
            w.Key("scale");
            w.BeginObject();
            w.Prop("enabled", Config.AutoScale && !Config.UseLocalControlPlane);
            w.Prop("action", _scale.Action == ScaleAction.Grow ? "grow" : _scale.Action == ScaleAction.Shrink ? "shrink" : "none");
            w.Prop("peak", _scale.Peak);
            w.Prop("mean", _scale.Mean);
            w.Prop("heldSeconds", _scale.HeldSeconds);
            w.Prop("holdSeconds", _scale.HoldSeconds);
            w.Prop("blockedBy", _scale.BlockedBy ?? "");
            // What the blocking container is mostly expensive in, so the dashboard can say which fix to reach for.
            w.Prop("blockedComponent", string.IsNullOrEmpty(_scale.BlockedBy) ? "" : ContainerCost.NameOf(_scale.BlockedComponent));
            w.Prop("blockedSaturation", _scale.BlockedSaturation);
            // Why the planner may not split the blocking container away (docs/cohesion-rebalancing.md).
            w.Prop("blockedCause", SaturationReport.NameOf(_scale.BlockedCause));
            w.Prop("blockedReason", _scale.BlockedReason ?? "");
            w.Prop("reason", _scale.Reason ?? "");
            w.Prop("outUtilization", Config.ScaleOutUtilization);
            w.Prop("inUtilization", Config.ScaleInUtilization);
            w.Prop("windowSeconds", Config.ScaleWindowSeconds);
            w.Prop("tickPeriodMs", WorkerLoadTracker.TickPeriodMs);
            w.Prop("hintNote", _hintNote ?? "");
            w.Prop("pendingJoins", PendingJoins);
            w.Prop("scaleToZero", Config.AutoScale && !Config.UseLocalControlPlane && MinWorkers == 0);
            w.EndObject();

            WriteCohesionState(w);
            WriteAssignmentState(w);

            // Workers: the union of control-plane rows and processes we manage (a freshly launched worker has no row yet).
            var ids = new List<string>();
            foreach (var m in _managed) if (!ids.Contains(m.Id)) ids.Add(m.Id);
            foreach (var r in workers) if (!ids.Contains(r.WorkerId)) ids.Add(r.WorkerId);
            uint totalPlayers = 0, totalBots = 0, totalServerDriven = 0, totalEntities = 0, totalAuth = 0;
            int liveCount = 0;
            // What interest management is doing per worker, from the telemetry documents (design §12).
            Telemetry.CopyInterest(_interestByWorker);
            w.Key("workers");
            w.BeginArray();
            foreach (var id in ids.OrderBy(IndexOf))
            {
                var row = workers.FirstOrDefault(r => r.WorkerId == id);
                var m = _managed.FirstOrDefault(x => x.Id == id);
                bool alive = row != null && ControlPlane.IsWorkerAlive(row, Config.WorkerTimeoutSeconds);
                uint index = row != null ? row.WorkerIndex : (m != null ? m.Index : 0);
                bool retiring = _retiring.ContainsKey(id);
                bool parked = m != null && m.Parked;
                string state;
                if (parked) state = "parked";
                else if (m != null && m.RelaunchAt >= 0f) state = "relaunching";
                else if (retiring) state = "draining";
                else if (!alive && m != null && m.Handle != null && !HandleGone(m)) state = "launching";
                else if (!alive) state = "dead";
                else state = row.Status; // starting | ready
                if (alive && !retiring && !parked) liveCount++;
                if (alive)
                {
                    totalPlayers += row.PlayerCount; totalBots += row.BotCount; totalServerDriven += row.ServerDrivenCount; totalEntities += row.EntityCount; totalAuth += row.AuthoritativeCount;
                }

                w.BeginObject();
                w.Prop("id", id);
                w.Prop("index", index);
                w.Prop("color", "#" + ColorUtility.ToHtmlStringRGB(NebulaDebugOverlay.ColorForWorker((ushort)index)));
                w.Prop("state", state);
                w.Prop("alive", alive);
                w.Prop("retiring", retiring);
                w.Prop("parked", parked);
                w.Prop("paidSecondsRemaining", parked && m.Handle != null ? m.Handle.ParkedSecondsRemaining : -1f);
                w.Prop("managed", m != null);
                w.Prop("instance", m != null && m.Handle != null ? m.Handle.Describe : "");
                if (m != null && m.Handle != null) _host.WriteHandleJson(m.Handle, w);
                w.Prop("address", row != null ? $"{row.Address}:{row.Port}" : (m != null && m.Handle != null && !string.IsNullOrEmpty(m.Handle.Address) ? $"{m.Handle.Address}:{Config.WorkerBasePort + index}" : ""));
                w.Prop("heartbeatAgeSeconds", row != null ? Math.Max(0.0, (ControlPlane.Now - row.LastHeartbeat).TotalSeconds) : -1.0);
                w.Prop("relaunchInSeconds", m != null && m.RelaunchAt >= 0f ? Math.Max(0f, m.RelaunchAt - now) : -1f);
                w.Prop("drainRemainingSeconds", retiring ? Math.Max(0f, _retiring[id] - now) : -1f);
                w.Prop("tickMs", row != null ? row.TickMs : 0f);
                w.Prop("oldestDirtySeconds", row != null ? row.OldestDirtySeconds : 0f);
                w.Prop("utilization", Loads.Utilization(id));
                w.Prop("tickCount", row != null ? row.TickCount : 0UL);
                w.Prop("entities", row != null ? row.EntityCount : 0U);
                w.Prop("authoritative", row != null ? row.AuthoritativeCount : 0U);
                w.Prop("ghosts", row != null ? row.GhostCount : 0U);
                w.Prop("players", row != null ? row.PlayerCount : 0U);
                w.Prop("bots", row != null ? row.BotCount : 0U);
                w.Prop("serverDriven", row != null ? row.ServerDrivenCount : 0U);
                // Interest management: what this worker sends versus what it holds, and whether it is asking for
                // the world to be partitioned. Absent (zeros) until its first telemetry document arrives.
                _interestByWorker.TryGetValue(id, out var interest);
                w.Key("interest");
                w.BeginObject();
                w.Prop("regions", interest.Regions);
                w.Prop("gateways", interest.Gateways);
                w.Prop("filterMs", interest.FilterMs);
                w.Prop("global", interest.Global || (row != null && row.HasGlobalEntities));
                w.Prop("entriesSent", interest.EntriesSent);
                w.Prop("entriesTotal", interest.EntriesTotal);
                w.Prop("bytesSent", interest.BytesSent);
                w.Prop("bytesUnfiltered", interest.BytesUnfiltered);
                w.Prop("warning", interest.Warning ?? "");
                w.EndObject();
                w.Key("containers");
                w.BeginArray();
                foreach (var l in leases.OrderBy(l => l.ContainerId, StringComparer.Ordinal))
                {
                    if (l.WorkerId == id && (l.State == LeaseState.Active || l.State == LeaseState.Draining || l.State == LeaseState.Assigning || l.State == LeaseState.Pinned)) w.Value(l.ContainerId);
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();

            // The idle pool: retired workers the host is still keeping. They hold nothing and are not counted in
            // desiredWorkers; the dashboard offers to take one back or to drop it early.
            w.Key("parked");
            w.BeginArray();
            foreach (var m in ParkedWorkers)
            {
                w.BeginObject();
                w.Prop("id", m.Id);
                w.Prop("index", m.Index);
                w.Prop("color", "#" + ColorUtility.ToHtmlStringRGB(NebulaDebugOverlay.ColorForWorker((ushort)m.Index)));
                w.Prop("instance", m.Handle != null ? m.Handle.Describe : "");
                w.Prop("parkedSeconds", Math.Max(0f, now - m.ParkedAt));
                w.Prop("paidSecondsRemaining", m.Handle != null ? m.Handle.ParkedSecondsRemaining : -1f);
                if (m.Handle != null) _host.WriteHandleJson(m.Handle, w);
                w.EndObject();
            }
            w.EndArray();

            w.Key("containers");
            w.BeginArray();
            foreach (var c in ContainerRegistry.All) WriteContainerState(w, c, leases, workers, _containerHints);
            foreach (var c in ContainerRegistry.Runtime) WriteContainerState(w, c, leases, workers, _containerHints);
            w.EndArray();

            // Cost telemetry: one row per container the mesh has heard about lately, heaviest first
            // (docs/cost-telemetry.md). The same rows are served on their own at GET /api/cost.
            w.Key("cost");
            w.BeginObject();
            w.Prop("tickPeriodMs", WorkerLoadTracker.TickPeriodMs);
            w.Prop("linkBytesPerSec", Config.CostLinkBytesPerSec);
            w.Prop("capacitySaturation", Config.CapacitySaturation);
            w.Key("containers");
            w.BeginArray();
            _costRows.Clear();
            foreach (var kv in _containerCost) _costRows.Add(kv.Value);
            _costRows.Sort((a, b) => b.TickShareMs != a.TickShareMs ? b.TickShareMs.CompareTo(a.TickShareMs) : string.CompareOrdinal(a.ContainerId, b.ContainerId));
            for (int i = 0; i < _costRows.Count; i++) ContainerCost.Write(w, _costRows[i], Config.CapacitySaturation);
            w.EndArray();
            w.EndObject();

            // Carried containers: leases workers create for the containers their entities carry (ships, lifts).
            // They follow their carrier unless pinned to a worker of their own.
            w.Key("carried");
            w.BeginArray();
            foreach (var l in leases.Where(x => ContainerRegistry.IsDynamicId(x.ContainerId)).OrderBy(x => x.ContainerId, StringComparer.Ordinal))
            {
                bool pinned = l.State == LeaseState.Pinned;
                string owner = LeaseState.IsOwning(l.State) ? l.WorkerId : "";
                var ownerRow = owner != "" ? workers.FirstOrDefault(r => r.WorkerId == owner) : null;
                w.BeginObject();
                w.Prop("id", l.ContainerId);
                w.Prop("carrierNetId", ContainerRegistry.CarrierNetIdOf(l.ContainerId));
                w.Prop("worker", owner);
                w.Prop("workerIndex", ownerRow != null ? (int)ownerRow.WorkerIndex : -1);
                w.Prop("color", "#" + ColorUtility.ToHtmlStringRGB(NebulaDebugOverlay.ColorForWorker(ownerRow != null ? (ushort)ownerRow.WorkerIndex : ushort.MaxValue)));
                w.Prop("epoch", l.Epoch);
                w.Prop("pinned", pinned);
                w.Prop("state", pinned ? "pinned" : (owner != "" ? "following carrier" : l.State));
                w.EndObject();
            }
            w.EndArray();

            // Scopes: the keyed worlds the mesh has been asked to bring into being, with their lifecycle state, what
            // is inside them and how long nothing has wanted them (docs/scope-lifecycle.md).
            w.Key("scopes");
            w.BeginArray();
            foreach (var scope in ControlPlane.Scopes.OrderBy(x => x.ScopeKey, StringComparer.Ordinal))
            {
                ScopeLifecycle.Occupancy(scope, _occupancy, out int scopeEntities, out int scopePlayers);
                w.BeginObject();
                w.Prop("key", scope.ScopeKey);
                w.Prop("state", scope.State ?? ScopeState.Active);
                w.Prop("requester", scope.Requester ?? "");
                w.Prop("parts", scope.ContainerIds.Count);
                w.Prop("ready", ControlPlane.IsScopeReady(scope, Config.WorkerTimeoutSeconds));
                w.Prop("entities", scopeEntities);
                w.Prop("players", scopePlayers);
                w.Prop("idleSeconds", ScopeLifecycle.IdleSeconds(ControlPlane, scope));
                w.Prop("stateSeconds", Math.Max(0.0, (ControlPlane.Now - scope.StateSince).TotalSeconds));
                w.Prop("ageSeconds", Math.Max(0.0, (ControlPlane.Now - scope.CreatedAt).TotalSeconds));
                w.Prop("acked", scope.Acks != null ? scope.Acks.Count : 0);
                // How full the scope is, as the whole mesh sees it: the worst of its parts, because a scope is one
                // interaction domain and cannot be split past its authored boundaries (docs/capacity-admission.md).
                var scopeCapacity = NebulaCapacity.OfScope(ControlPlane, scope.ScopeKey);
                w.Prop("capacityKnown", scopeCapacity.Known);
                w.Prop("saturation", Math.Round(scopeCapacity.Saturation, 4));
                w.Prop("dominant", scopeCapacity.Known ? scopeCapacity.DominantName : "");
                w.Prop("atCapacity", scopeCapacity.AtCapacity);
                w.Prop("capacityCause", SaturationReport.NameOf(scopeCapacity.Cause));
                w.Key("containers");
                w.BeginArray();
                foreach (var id in scope.ContainerIds) w.Value(id ?? "");
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
            w.Prop("scopeIdleRetireSeconds", Config.ScopeIdleRetireSeconds);
            w.Prop("capacitySaturation", Config.CapacitySaturation);

            w.Key("gateways");
            w.BeginArray();
            foreach (var g in ControlPlane.Gateways)
            {
                w.BeginObject();
                w.Prop("id", g.GatewayId);
                w.Prop("incarnation", g.Incarnation.ToString("x8"));
                w.Prop("address", $"{g.Address}:{g.Port}");
                w.Prop("heartbeatAgeSeconds", Math.Max(0.0, (ControlPlane.Now - g.LastHeartbeat).TotalSeconds));
                w.Prop("drainRequested", g.DrainRequested);
                ControlPlaneJson.WriteGatewayStats(w, g.Stats);
                w.EndObject();
            }
            w.EndArray();

            w.Key("totals");
            w.BeginObject();
            w.Prop("liveWorkers", liveCount);
            w.Prop("parkedWorkers", ParkedWorkerCount);
            w.Prop("players", totalPlayers);
            w.Prop("bots", totalBots);
            w.Prop("serverDriven", totalServerDriven);
            w.Prop("entities", totalEntities);
            w.Prop("authoritative", totalAuth);
            w.Prop("containers", ContainerRegistry.Count + ContainerRegistry.Runtime.Count);
            w.EndObject();

            w.Key("persistence");
            w.BeginObject();
            w.Prop("mode", Config.PersistenceMode ?? "auto");
            w.Prop("backend", Persistence != null ? Persistence.Backend : "off");
            w.Prop("connected", Persistence != null && Persistence.IsConnected);
            w.Prop("entities", Persistence != null ? Persistence.KnownCount : 0);
            w.EndObject();

            w.Key("settings");
            w.BeginObject();
            if (ControlPlane.Settings != null)
            {
                foreach (var kv in ControlPlane.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.Prop(kv.Key, kv.Value);
            }
            w.EndObject();

            w.Key("events");
            w.BeginArray();
            for (int i = _events.Count - 1; i >= 0 && i >= _events.Count - 60; i--)
            {
                var e = _events[i];
                w.BeginObject();
                w.Prop("time", e.Time.ToString("o"));
                w.Prop("level", e.Level);
                w.Prop("message", e.Message);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
            return sb.ToString();
        }

        private uint IndexOf(string workerId)
        {
            var m = _managed.FirstOrDefault(x => x.Id == workerId);
            if (m != null) return m.Index;
            var r = ControlPlane.FindWorker(workerId);
            return r != null ? r.WorkerIndex : uint.MaxValue;
        }
    }
}
