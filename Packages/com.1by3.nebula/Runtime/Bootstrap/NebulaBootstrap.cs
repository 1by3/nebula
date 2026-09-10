using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
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
    /// Entry point for every Nebula process. Put one in the boot scene. Reads the role from the command line
    /// (<c>-nebula-role worker,gateway</c>) or from <see cref="EditorRole"/> when running in the Editor, loads the
    /// game scene, and starts the matching services. A process is a worker <i>or</i> a client, never both.
    /// <list type="bullet">
    /// <item><c>-nebula-role client|worker|gateway|orchestrator</c> (comma separated)</item>
    /// <item><c>-nebula-worker-id w1 -nebula-worker-index 1 -nebula-port 7101</c></item>
    /// <item><c>-nebula-gateway 127.0.0.1:7000</c></item>
    /// <item><c>-nebula-spacetime http://127.0.0.1:3000 -nebula-database nebula</c></item>
    /// <item><c>-nebula-workers 4 -nebula-dashboard-port 7080 -nebula-settings npcs=64</c> (orchestrator; settings are
    /// game-defined key/values seeded on the control plane, editable on the dashboard)</item>
    /// <item><c>-nebula-name Jesse</c> (client display name)</item>
    /// <item><c>-nebula-advertise 10.0.1.2|auto</c> (address this worker/gateway advertises to peers)</item>
    /// <item><c>-nebula-host process|hetzner -nebula-build-dir /opt/nebula/artifacts</c> (orchestrator: where workers run;
    /// cloud hosts also read <c>-nebula-cloud-location/-type/-image/-network/-sshkey/-firewall</c>, <c>-nebula-build-url</c>
    /// and the provider token from <c>-nebula-cloud-token</c> or <c>HCLOUD_TOKEN</c>)</item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class NebulaBootstrap : MonoBehaviour
    {
        public static NebulaBootstrap Instance { get; private set; }

        [Tooltip("Role used when pressing Play in the Editor (builds read -nebula-role instead).")]
        public NebulaRoles EditorRole = NebulaRoles.Client;
        public NebulaConfig Config;

        public NebulaRoles Roles { get; private set; }
        public IControlPlane ControlPlane { get; private set; }
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
            if (Config.WorldManifest != null) NebulaWorld.Load(Config.WorldManifest);
            else ContainerRegistry.Rebuild();
            NebulaLog.Info($"containers: {ContainerRegistry.Count}{(NebulaWorld.IsActive ? " (partitioned world)" : "")}");

            bool needsControlPlane = (Roles & (NebulaRoles.Worker | NebulaRoles.Gateway | NebulaRoles.Orchestrator)) != 0;
            if (needsControlPlane)
            {
                if (Config.UseLocalControlPlane)
                {
                    ControlPlane = new LocalControlPlane();
                }
                else
                {
                    ControlPlane = new SpacetimeControlPlane(Config.SpacetimeUri, Config.SpacetimeDatabase);
                }
                ControlPlane.Connect();
            }

            if ((Roles & NebulaRoles.Orchestrator) != 0)
            {
                Orchestrator = gameObject.AddComponent<NebulaOrchestrator>();
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
                Worker.Initialize(Config, ControlPlane);
            }
            if ((Roles & NebulaRoles.Client) != 0)
            {
                Client = gameObject.AddComponent<NebulaClient>();
                // Scripted clients (bots, an explicit -nebula-gateway, -nebula-connect) go straight in; a human gets the
                // title screen to pick the gateway (local mesh or the Hetzner deployment) and a name.
                bool autoConnect = CommandLine.Has("nebula-gateway") || CommandLine.Has("nebula-bot") || CommandLine.GetBool("nebula-connect", false);
                Client.Initialize(Config, autoConnect);
                if (!autoConnect) gameObject.AddComponent<NebulaTitleScreen>().Client = Client;
            }
            if (NebulaWorld.IsActive)
            {
                WorldStreaming = gameObject.AddComponent<NebulaWorldStreaming>();
                WorldStreaming.Initialize(Config, Worker, Client);
            }
        }

        /// <summary>Per-role cell streaming policy; null unless <see cref="NebulaConfig.WorldManifest"/> is set.</summary>
        public NebulaWorldStreaming WorldStreaming { get; private set; }

        private void Update()
        {
            ControlPlane?.Tick();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            ControlPlane?.Dispose();
            Instance = null;
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
            cfg.SpacetimeUri = CommandLine.Get("nebula-spacetime", cfg.SpacetimeUri);
            cfg.SpacetimeDatabase = CommandLine.Get("nebula-database", cfg.SpacetimeDatabase);
            cfg.WorkerCount = CommandLine.GetInt("nebula-workers", cfg.WorkerCount);
            cfg.WorkerExecutable = CommandLine.Get("nebula-worker-exe", cfg.WorkerExecutable);
            cfg.WorkerHost = CommandLine.Get("nebula-host", cfg.WorkerHost);
            cfg.BuildArtifactDir = CommandLine.Get("nebula-build-dir", cfg.BuildArtifactDir);
            // Address this node advertises to peers: workers register it, the orchestrator hands it to workers it launches.
            // 'auto' picks the first private (10/8, 172.16/12, 192.168/16) IPv4 of this machine.
            cfg.WorkerAdvertiseAddress = ResolveAdvertise(CommandLine.Get("nebula-advertise", cfg.WorkerAdvertiseAddress));
            cfg.DashboardPort = (ushort)CommandLine.GetInt("nebula-dashboard-port", cfg.DashboardPort);
            cfg.UseLocalControlPlane = CommandLine.GetBool("nebula-local-control-plane", cfg.UseLocalControlPlane);
            cfg.GameScene = CommandLine.Get("nebula-scene", cfg.GameScene);
            cfg.GhostBandMargin = CommandLine.GetFloat("nebula-ghost-band", cfg.GhostBandMargin);
            cfg.HandoverHysteresis = CommandLine.GetFloat("nebula-hysteresis", cfg.HandoverHysteresis);
            return cfg;
        }

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
