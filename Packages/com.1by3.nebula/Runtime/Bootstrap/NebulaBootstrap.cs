using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>Lists the process roles that <see cref="NebulaBootstrap"/> can start.</summary>
    [Flags]
    public enum NebulaRoles
    {
        None = 0,
        Client = 1,
        Worker = 2,
        Gateway = 4,
        Orchestrator = 8,
    }

    /// <summary>
    /// Entry point for Unity workers and clients. Put one in the boot scene. Reads the role from the command line
    /// (<c>-nebula-role worker,gateway</c>) or from <see cref="EditorRole"/> when running in the Editor, loads the
    /// game scene, and starts the matching components. A process is a worker <i>or</i> a client, never both.
    /// The CLI launches standalone .NET orchestrator and gateway executables; Unity service roles remain available for compatibility and in-process tests.
    /// <list type="bullet">
    /// <item><c>-nebula-role client|worker|gateway|orchestrator</c> (comma separated)</item>
    /// <item><c>-nebula-worker-id w1 -nebula-worker-index 1 -nebula-port 7101</c></item>
    /// <item><c>-nebula-gateway 127.0.0.1:7000</c></item>
    /// <item><c>-nebula-control-plane http://10.0.1.2:7080/ -nebula-token secret</c> (worker, gateway: the orchestrator that hosts the control plane)</item>
    /// <item><c>-nebula-database file:saves|memory</c> (Unity orchestrator: where the control plane and saved entities are kept; the standalone orchestrator also takes <c>sqlite:</c> and <c>postgres://</c>)</item>
    /// <item><c>-nebula-persistence-mode auto|database|remote|local|memory|off -nebula-persistence-file saves/world.bin</c> (where persistent entities are stored)</item>
    /// <item><c>-nebula-workers 4 -nebula-dashboard-port 7080 -nebula-settings round-time=600</c> (orchestrator; settings are
    /// game-defined key/values seeded on the control plane, editable on the dashboard)</item>
    /// <item><c>-nebula-name Jesse</c> (client display name)</item>
    /// <item><c>-nebula-advertise 10.0.1.2|auto</c> (address this worker/gateway advertises to peers)</item>
    /// <item><c>-nebula-host process|hetzner|cloud -nebula-build-dir /opt/nebula/artifacts</c> (orchestrator: where workers run;
    /// cloud hosts also read <c>-nebula-cloud-location</c>, <c>-nebula-cloud-type</c>, <c>-nebula-cloud-image</c>,
    /// <c>-nebula-cloud-network</c>, <c>-nebula-cloud-sshkey</c>, <c>-nebula-cloud-firewall</c>, <c>-nebula-build-url</c>,
    /// and the provider token from <c>-nebula-cloud-token</c> or <c>HCLOUD_TOKEN</c>)</item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class NebulaBootstrap : MonoBehaviour
    {
        public static NebulaBootstrap Instance { get; private set; }

        /// <summary>
        /// Roles started when pressing Play in the Editor, in place of <c>-nebula-role</c>. It does not replace a build:
        /// the standalone orchestrator launches Unity workers and a .NET gateway, and a Unity process is never both a
        /// client and a worker, so whichever half the Editor does not run comes from a build. Client (the default) joins a
        /// mesh started with <c>nebula start</c>; Worker joins one started with <c>nebula start --workers 0</c>.
        /// </summary>
        [Tooltip("Role used when pressing Play in the Editor (builds read -nebula-role instead).")]
        public NebulaRoles EditorRole = NebulaRoles.Client;
        public NebulaConfig Config;

        public NebulaRoles Roles { get; private set; }
        public IControlPlane ControlPlane { get; private set; }
        /// <summary>
        /// Where persistent entities are stored, created for the worker and orchestrator roles from
        /// <see cref="NebulaConfig.PersistenceMode"/>. Null when persistence is off.
        /// </summary>
        public IPersistenceStore PersistenceStore { get; private set; }
        public NebulaWorker Worker { get; private set; }
        public NebulaGateway Gateway { get; private set; }
        public NebulaOrchestrator Orchestrator { get; private set; }
        public NebulaClient Client { get; private set; }

        private bool _started;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            if (Config == null) Config = NebulaConfig.Load();
            NebulaRuntime.Reset();
            Config = ApplyCommandLineOverrides(Config);
            NebulaRuntime.Config = Config;
            if (Config.WorldManifest != null && Config.RuntimeWorld != null)
                NebulaLog.Error("both WorldManifest and RuntimeWorld are assigned; using the baked WorldManifest");

            // A runtime world has no scene objects to wait for. Load its coordinate frame before ordinary game
            // scripts awake so they can construct RuntimeGrid helpers from NebulaWorld.Definition.
            if (Config.WorldManifest == null && Config.RuntimeWorld != null)
                NebulaWorld.LoadRuntime(Config.RuntimeWorld);

            Roles = ResolveRoles();
            if ((Roles & NebulaRoles.Client) != 0 && (Roles & NebulaRoles.Worker) != 0)
            {
                NebulaLog.Error("A process cannot be both a client and a worker; dropping the worker role");
                Roles &= ~NebulaRoles.Worker;
            }
            NebulaRuntime.IsServer = (Roles & NebulaRoles.Worker) != 0;
            NebulaRuntime.IsClient = (Roles & NebulaRoles.Client) != 0;
            NebulaLog.Verbose = CommandLine.GetBool("nebula-verbose", false);
            if (!Application.isEditor)
            {
                // Player logs: a stack trace per Info/Warning line is most of the bytes in a busy worker's log
                // (every handover logs one) and is written synchronously on the main thread. Errors keep theirs.
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
            }

            Application.targetFrameRate = NebulaRuntime.IsClient ? -1 : NetworkTime.TickRate;
            Time.fixedDeltaTime = NetworkTime.TickInterval;
            Physics.simulationMode = SimulationMode.FixedUpdate;

            NetworkPrefabs.Register(Config.NetworkPrefabs);
            NebulaLog.Info($"boot roles={Roles} scene={Config.GameScene} tickRate={NetworkTime.TickRate}");
        }

        private void Start()
        {
            var active = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(Config.GameScene) && active.name != Config.GameScene)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.LoadScene(Config.GameScene, LoadSceneMode.Single);
            }
            else
            {
                StartServices();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != Config.GameScene) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            StartServices();
        }

        private void StartServices()
        {
            if (_started) return;
            _started = true;
            LogConfigIssues();
            ConfigureWorld(Config);
            string worldKind = Config.WorldManifest != null ? " (partitioned world)" : Config.RuntimeWorld != null ? " (runtime world)" : "";
            NebulaLog.Info($"containers: {ContainerRegistry.Count}{worldKind}");

            bool needsControlPlane = (Roles & (NebulaRoles.Worker | NebulaRoles.Gateway | NebulaRoles.Orchestrator)) != 0;
            if (needsControlPlane)
            {
                if (Config.UseLocalControlPlane)
                {
                    ControlPlane = new LocalControlPlane();
                }
                else if ((Roles & NebulaRoles.Orchestrator) != 0)
                {
                    // This process hosts the control plane; workers and gateways reach it through the dashboard port.
                    ControlPlane = new ControlPlaneHost(CreateControlPlaneStorage(), Config.MeshToken, !CommandLine.GetBool("nebula-reset", true));
                }
                else
                {
                    ControlPlane = new RemoteControlPlane(Config.ControlPlaneUrl, Config.MeshToken);
                }
                ControlPlane.Connect();
            }

            bool needsPersistence = (Roles & (NebulaRoles.Worker | NebulaRoles.Orchestrator)) != 0;
            if (needsPersistence)
            {
                PersistenceStore = CreatePersistenceStore();
                PersistenceStore?.Connect();
                if (PersistenceStore != null && (Roles & NebulaRoles.Orchestrator) != 0 && CommandLine.GetBool("nebula-reset-persistence", false))
                {
                    NebulaLog.Warn("persistence: -nebula-reset-persistence: deleting every saved entity");
                    PersistenceStore.Clear();
                }
            }

            if ((Roles & NebulaRoles.Orchestrator) != 0)
            {
                Orchestrator = gameObject.AddComponent<NebulaOrchestrator>();
                Orchestrator.Persistence = PersistenceStore;
                Orchestrator.Initialize(Config, ControlPlane);
            }
            if ((Roles & NebulaRoles.Gateway) != 0)
            {
                Gateway = gameObject.AddComponent<NebulaGateway>();
                Gateway.Initialize(Config, ControlPlane);
            }
            if ((Roles & NebulaRoles.Worker) != 0)
            {
                Worker = gameObject.AddComponent<NebulaWorker>();
                Worker.Initialize(Config, ControlPlane, PersistenceStore);
            }
            if ((Roles & NebulaRoles.Client) != 0)
            {
                Client = gameObject.AddComponent<NebulaClient>();
                // Scripted clients (bots, an explicit -nebula-gateway, -nebula-connect) go straight in. Otherwise the
                // client waits for ConnectTo, from NebulaTitleScreen if the scene has one or from the game's own UI.
                bool autoConnect = CommandLine.Has("nebula-gateway") || CommandLine.Has("nebula-bot") || CommandLine.GetBool("nebula-connect", false);
                Client.Initialize(Config, autoConnect);
                // A headless bot's log is the only place a soak can read what one client actually holds, so bots
                // carry the probe by default; a player client takes -nebula-probe to turn it on (and a bot takes
                // -nebula-probe=false to turn it off). It costs one pass over the replica set per second.
                if (CommandLine.GetBool("nebula-probe", CommandLine.Has("nebula-bot"))) InterestProbe.Attach(Client);
            }
            if (NebulaWorld.IsActive)
            {
                WorldStreaming = gameObject.AddComponent<NebulaWorldStreaming>();
                WorldStreaming.Initialize(Config, Worker, Client);
            }
            else if (Config.ChunkedWorld)
            {
                // A runtime world has no authored cell scenes, so NebulaWorld.IsActive is false and the baked
                // streaming policy above never runs. The chunked driver is that policy for a procedural world:
                // grid, allocator, origin and content hooks, on every role, from configuration alone.
                ChunkedWorld = gameObject.AddComponent<NebulaChunkedWorld>();
                ChunkedWorld.Initialize(Config, Roles, Worker, Client);
            }
        }

        /// <summary>
        /// Report a configuration that does not hold together at the one moment somebody is certain to be reading:
        /// the first seconds of a role's log. The same check runs in the config inspector, the setup window and
        /// <c>nebula doctor</c>, but a mesh is usually started from a build where none of those were looked at.
        /// Repairs are already applied by <see cref="NebulaConfig.ToInterestSettings()"/>; this only says so.
        /// </summary>
        private void LogConfigIssues()
        {
            var issues = new List<ConfigIssue>();
            Config.Validate(issues);
            foreach (var issue in issues)
            {
                if (issue.Severity == ConfigSeverity.Error) NebulaLog.Error($"config {issue.Field}: {issue.Message}");
                else if (issue.Severity == ConfigSeverity.Warning) NebulaLog.Warn($"config {issue.Field}: {issue.Message}");
                else NebulaLog.Info($"config {issue.Field}: {issue.Message}");
            }
        }

        /// <summary>Apply the configured baked, runtime, or single-scene world before a role starts.</summary>
        internal static void ConfigureWorld(NebulaConfig config)
        {
            if (config.WorldManifest != null)
            {
                NebulaWorld.Load(config.WorldManifest);
                return;
            }
            if (config.RuntimeWorld != null)
            {
                if (NebulaWorld.Definition != config.RuntimeWorld) NebulaWorld.LoadRuntime(config.RuntimeWorld);
                // Authored scene containers do not move with a runtime floating origin. Runtime containers arrive
                // from control-plane leases, so start with an intentionally empty static registry.
                ContainerRegistry.Load(Array.Empty<Container>(), gridded: false);
                return;
            }
            ContainerRegistry.Rebuild();
        }

        /// <summary>Per-role cell streaming policy; null unless <see cref="NebulaConfig.WorldManifest"/> is set.</summary>
        public NebulaWorldStreaming WorldStreaming { get; private set; }

        /// <summary>Per-role chunked-world policy; null unless <see cref="NebulaConfig.ChunkedWorld"/> is on with a <see cref="NebulaConfig.RuntimeWorld"/>.</summary>
        public NebulaChunkedWorld ChunkedWorld { get; private set; }

        private void Update()
        {
            ControlPlane?.Tick();
            // The store's callbacks land here, on the main thread, once per frame.
            PersistenceStore?.Tick();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            ControlPlane?.Dispose();
            PersistenceStore?.Dispose();
            PersistenceStore = null;
            Instance = null;
        }

        /// <summary>
        /// The persistence store this process talks to, from <see cref="NebulaConfig.PersistenceMode"/>
        /// (<c>-nebula-persistence-mode</c>): <c>database</c> for the orchestrator's own store (from
        /// <see cref="NebulaConfig.DatabaseUrl"/>), <c>remote</c> for a worker that asks the orchestrator,
        /// <c>local</c> for a file next to the process, <c>memory</c>, <c>off</c>, and <c>auto</c> (the default):
        /// database on an orchestrator, remote on a worker of a mesh, local in a single-process run. Null when
        /// persistence is off.
        /// </summary>
        private IPersistenceStore CreatePersistenceStore()
        {
            string mode = (Config.PersistenceMode ?? "auto").Trim().ToLowerInvariant();
            bool orchestrator = (Roles & NebulaRoles.Orchestrator) != 0;
            if (mode == "" || mode == "auto") mode = Config.UseLocalControlPlane ? "local" : orchestrator ? "database" : "remote";
            switch (mode)
            {
                case "off":
                case "none":
                    NebulaLog.Info("persistence: off");
                    return null;
                case "memory":
                    return new LocalPersistenceStore();
                case "local":
                case "file":
                {
                    string path = Config.PersistenceLocalFile;
                    if (string.IsNullOrEmpty(path)) path = System.IO.Path.Combine(Application.persistentDataPath, "nebula-persistence.bin");
                    return new LocalPersistenceStore(path);
                }
                case "remote":
                    return new RemotePersistenceStore(Config.ControlPlaneUrl, Config.MeshToken);
                case "database":
                {
                    // A Unity orchestrator keeps its data in files; sqlite: and postgres: need the standalone orchestrator.
                    var url = DatabaseUrl.Parse(Config.DatabaseUrl, "file:" + DefaultDataDir);
                    switch (url.Scheme)
                    {
                        case "memory": return new LocalPersistenceStore();
                        case "file": return new LocalPersistenceStore(System.IO.Path.Combine(url.Target, "entities.bin"));
                        default:
                            NebulaLog.Error($"persistence: a Unity orchestrator cannot open '{url.Scheme}:' databases (use file: or memory here, or run the standalone orchestrator); persistence is off");
                            return null;
                    }
                }
                default:
                    NebulaLog.Warn($"unknown persistence mode '{mode}'; persistence is off");
                    return null;
            }
        }

        /// <summary>Folder a Unity orchestrator keeps its data in when <see cref="NebulaConfig.DatabaseUrl"/> is empty.</summary>
        private static string DefaultDataDir => System.IO.Path.Combine(Application.persistentDataPath, "nebula");

        /// <summary>Where a Unity orchestrator keeps the control plane between runs (see <see cref="NebulaConfig.DatabaseUrl"/>).</summary>
        private IControlPlaneStorage CreateControlPlaneStorage()
        {
            var url = DatabaseUrl.Parse(Config.DatabaseUrl, "file:" + DefaultDataDir);
            switch (url.Scheme)
            {
                case "memory": return new MemoryControlPlaneStorage();
                case "file": return new FileControlPlaneStorage(System.IO.Path.Combine(url.Target, "control-plane.json"));
                default:
                    NebulaLog.Error($"control plane: a Unity orchestrator cannot open '{url.Scheme}:' databases (use file: or memory here, or run the standalone orchestrator); keeping it in memory");
                    return new MemoryControlPlaneStorage();
            }
        }

        private NebulaRoles ResolveRoles()
        {
            string arg = CommandLine.Get("nebula-role");
            if (string.IsNullOrEmpty(arg))
            {
                return Application.isEditor ? EditorRole : NebulaRoles.Client;
            }
            var roles = NebulaRoles.None;
            foreach (var part in arg.Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (part.Trim().ToLowerInvariant())
                {
                    case "client": roles |= NebulaRoles.Client; break;
                    case "worker": case "sim": roles |= NebulaRoles.Worker; break;
                    case "gateway": roles |= NebulaRoles.Gateway; break;
                    case "orchestrator": roles |= NebulaRoles.Orchestrator; break;
                    case "services": roles |= NebulaRoles.Gateway | NebulaRoles.Orchestrator; break;
                    default: NebulaLog.Warn($"unknown role '{part}'"); break;
                }
            }
            return roles;
        }

        private static NebulaConfig ApplyCommandLineOverrides(NebulaConfig cfg)
        {
            // Never mutate the asset itself in the Editor; work on a copy.
            if (Application.isEditor) cfg = Instantiate(cfg);

            var gateway = CommandLine.Get("nebula-gateway");
            if (!string.IsNullOrEmpty(gateway))
            {
                var parts = gateway.Split(':');
                cfg.GatewayAddress = parts[0];
                if (parts.Length > 1 && ushort.TryParse(parts[1], out var p)) cfg.GatewayPort = p;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            // A web build served by the gateway connects back to where the page came from, unless the config names a
            // real gateway or the page passes ?nebula-gateway=address:port.
            else if (IsLoopback(cfg.GatewayAddress) && Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out var page) && (page.Scheme == "http" || page.Scheme == "https"))
            {
                cfg.GatewayAddress = page.Host;
                cfg.GatewayPort = (ushort)page.Port;
            }
#endif
            cfg.ControlPlaneUrl = CommandLine.Get("nebula-control-plane", cfg.ControlPlaneUrl);
            cfg.MeshToken = CommandLine.Get("nebula-token", cfg.MeshToken);
            cfg.AuthIssuers = CommandLine.Get("nebula-auth-issuers", cfg.AuthIssuers);
            cfg.AuthAudience = CommandLine.Get("nebula-auth-audience", cfg.AuthAudience);
            cfg.AuthAnonymous = CommandLine.GetBool("nebula-auth-anonymous", cfg.AuthAnonymous);
            cfg.AuthSigningKey = CommandLine.Get("nebula-auth-key", cfg.AuthSigningKey);
            cfg.SingleSessionPerPlayer = CommandLine.GetBool("nebula-single-session", cfg.SingleSessionPerPlayer);
            cfg.DatabaseUrl = CommandLine.Get("nebula-database", cfg.DatabaseUrl);
            cfg.WorkerCount = CommandLine.GetInt("nebula-workers", cfg.WorkerCount);
            cfg.MinWorkers = CommandLine.GetInt("nebula-min-workers", cfg.MinWorkers);
            cfg.MaxWorkers = CommandLine.GetInt("nebula-max-workers", cfg.MaxWorkers);
            cfg.AutoScale = CommandLine.GetBool("nebula-autoscale", cfg.AutoScale);
            cfg.IdlePoolSeconds = CommandLine.GetFloat("nebula-idle-pool", cfg.IdlePoolSeconds);
            cfg.WorkerExecutable = CommandLine.Get("nebula-worker-exe", cfg.WorkerExecutable);
            cfg.WorkerHost = CommandLine.Get("nebula-host", cfg.WorkerHost);
            cfg.BuildArtifactDir = CommandLine.Get("nebula-build-dir", cfg.BuildArtifactDir);
            // Address this node advertises to peers: workers register it, the orchestrator hands it to workers it launches.
            // 'auto' picks the first private (10/8, 172.16/12, 192.168/16) IPv4 of this machine.
            cfg.WorkerAdvertiseAddress = ResolveAdvertise(CommandLine.Get("nebula-advertise", cfg.WorkerAdvertiseAddress));
            cfg.DashboardPort = (ushort)CommandLine.GetInt("nebula-dashboard-port", cfg.DashboardPort);
            cfg.UseLocalControlPlane = CommandLine.GetBool("nebula-local-control-plane", cfg.UseLocalControlPlane);
            cfg.GameScene = CommandLine.Get("nebula-scene", cfg.GameScene);
            cfg.PersistenceMode = CommandLine.Get("nebula-persistence-mode", cfg.PersistenceMode);
            cfg.PersistenceLocalFile = CommandLine.Get("nebula-persistence-file", cfg.PersistenceLocalFile);
            cfg.PersistenceCheckpointSeconds = CommandLine.GetFloat("nebula-persistence-checkpoint", cfg.PersistenceCheckpointSeconds);
            cfg.ScopeIdleRetireSeconds = CommandLine.GetFloat("nebula-scope-idle-retire", cfg.ScopeIdleRetireSeconds);
            cfg.CapacitySaturation = CommandLine.GetFloat("nebula-capacity-saturation", cfg.CapacitySaturation);
            cfg.GhostBandMargin = CommandLine.GetFloat("nebula-ghost-band", cfg.GhostBandMargin);
            cfg.HandoverHysteresis = CommandLine.GetFloat("nebula-hysteresis", cfg.HandoverHysteresis);
            cfg.AuthorityCallMaxHops = CommandLine.GetInt("nebula-authority-call-hops", cfg.AuthorityCallMaxHops);
            cfg.StateHistoryTicks = CommandLine.GetInt("nebula-state-history", cfg.StateHistoryTicks);
            // Interest is the knob a load test or a soak run wants to sweep without rebuilding.
            cfg.InterestRadius = CommandLine.GetFloat("nebula-interest-radius", cfg.InterestRadius);
            cfg.InterestCellSize = CommandLine.GetFloat("nebula-interest-cell", cfg.InterestCellSize);
            return cfg;
        }

        private static bool IsLoopback(string address) =>
            string.IsNullOrEmpty(address) || address == "127.0.0.1" || address.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        private static string ResolveAdvertise(string value)
        {
            if (!string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)) return value;
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        var b = a.Address.GetAddressBytes();
                        bool isPrivate = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168);
                        if (isPrivate) return a.Address.ToString();
                    }
                }
            }
            catch (Exception e) { NebulaLog.Warn($"-nebula-advertise auto: {e.Message}"); }
            NebulaLog.Warn("-nebula-advertise auto: no private IPv4 found; advertising 127.0.0.1");
            return "127.0.0.1";
        }

        /// <summary>The prefab used for the boot scene's Nebula object when none exists (editor convenience).</summary>
        public static NebulaBootstrap EnsureExists()
        {
            if (Instance != null) return Instance;
            var existing = FindFirstObjectByType<NebulaBootstrap>();
            if (existing != null) return existing;
            var go = new GameObject("Nebula");
            return go.AddComponent<NebulaBootstrap>();
        }
    }
}
