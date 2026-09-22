using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Nebula.Tls;
using Nebula.WebRtc;

namespace Nebula
{
    public static class ServiceHost
    {
        internal static string LogDirectory => Path.GetDirectoryName(Path.GetFullPath(CommandLine.Get("logFile", Path.Combine(AppContext.BaseDirectory, "Logs", "orchestrator.log"))));
        public static int Run(string role, CancellationToken cancellationToken = default)
        {
            if (CommandLine.Has("help"))
            {
                Console.WriteLine($"nebula-{role} -nebula-service-manifest <nebula-services.json> [-nebula-token <secret>] [-logFile <path>]");
                Console.WriteLine("Orchestrator: -nebula-worker-exe <Unity player> -nebula-workers <count> -nebula-dashboard-port <port> [-nebula-database sqlite:<file>|postgres://...|memory] [-nebula-reset-persistence]");
                Console.WriteLine("Gateway: -nebula-gateway <advertised-address:port> -nebula-control-plane <orchestrator url> [-nebula-web false] [-nebula-web-port <tcp>] [-nebula-webrtc-port <udp>] [-nebula-web-root <folder>]");
                Console.WriteLine("Gateway extension: [-nebula-gateway-extension <Game.Gateway.dll>] [-nebula-gateway-extension-type <Namespace.Class>] [-nebula-ext-<key> <value>]");
                return 0;
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            using var term = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
            NebulaOrchestrator orchestrator = null;
            NebulaGateway gateway = null;
            GatewayExtensionHost extension = null;
            IControlPlane control = null;
            IPersistenceStore persistence = null;
            NebulaDatabase database = null;
            GatewayHttpServer web = null;
            StreamWriter log = null;
            var originalOut = Console.Out;
            var originalError = Console.Error;
            try
            {
                string logPath = CommandLine.Get("logFile");
                if (!string.IsNullOrEmpty(logPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));
                    log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                    var synchronized = TextWriter.Synchronized(log);
                    Console.SetOut(synchronized); Console.SetError(synchronized);
                }
                NebulaLog.Verbose = CommandLine.GetBool("nebula-verbose", false);
                // Windows wakes a sleeping thread on its timer interrupt, 15.6 ms apart by default. A 60 Hz loop and the
                // transport's send thread both sleep between ticks, so without a finer timer every packet through this
                // process could wait up to two interrupts. Unity players request 1 ms themselves; a plain .NET process must ask.
                using var timer = FineTimer.Request();
                var manifest = ServiceManifest.Load(CommandLine.Get("nebula-service-manifest", Path.Combine(AppContext.BaseDirectory, "nebula-services.json")));
                var config = manifest.Config;
                ApplyOverrides(config);
                if (config.UseLocalControlPlane && !CommandLine.Has("nebula-local-control-plane"))
                    throw new InvalidOperationException("A standalone service cannot use an in-process control plane; disable UseLocalControlPlane in the exported configuration.");
                if (role == "orchestrator")
                {
                    if (config.WorkerHost == "process" && !config.UseLocalControlPlane && !File.Exists(config.WorkerExecutable))
                        throw new FileNotFoundException("Set -nebula-worker-exe to the Unity worker executable", config.WorkerExecutable);
                    if (config.DashboardPort == 0 && !config.UseLocalControlPlane)
                        throw new InvalidOperationException("The orchestrator hosts the control plane on its dashboard port; -nebula-dashboard-port cannot be 0");
                    // The orchestrator's database holds the control plane between runs and every saved entity.
                    var url = DatabaseUrl.Parse(config.DatabaseUrl, "sqlite:" + Path.Combine(AppContext.BaseDirectory, "nebula.db"));
                    if (url.Scheme == "sqlite" || url.Scheme == "postgres") database = NebulaDatabase.Open(url);
                    // Explicit local mode supports isolated service diagnostics; it cannot connect separate processes.
                    control = config.UseLocalControlPlane ? (IControlPlane)new LocalControlPlane() : new ControlPlaneHost(CreateControlPlaneStorage(url, database), config.MeshToken, !CommandLine.GetBool("nebula-reset", true));
                    control.Connect();
                    persistence = CreatePersistence(config, url, database);
                    persistence?.Connect();
                    if (persistence != null && CommandLine.GetBool("nebula-reset-persistence", false))
                    {
                        NebulaLog.Warn("persistence: -nebula-reset-persistence: deleting every saved entity");
                        persistence.Clear();
                    }
                    orchestrator = new NebulaOrchestrator { Persistence = persistence };
                    orchestrator.Initialize(config, control);
                }
                else if (role == "gateway")
                {
                    control = config.UseLocalControlPlane ? (IControlPlane)new LocalControlPlane() : new RemoteControlPlane(config.ControlPlaneUrl, config.MeshToken);
                    control.Connect();
                    gateway = new NebulaGateway();
                    gateway.Initialize(config, control, config.WebClients ? StartWebClients(config, out web) : null);
                    gateway.LoopPeriodSeconds = NetworkTime.TickInterval / 4;
                    // A load balancer needs a health check whether or not browsers are served: without web clients
                    // the HTTP server still answers /healthz on the gateway port (tcp) and nothing else.
                    if (web == null) web = StartHealthOnly(config);
                    if (web != null) web.Ready = () => gateway.IsReady;
                    // The game's own code, before the first tick: a policy installed here has been asked about
                    // every client, because none has been evaluated yet. Anything wrong with it stops the
                    // gateway rather than running it with a security filter the game thinks is in place.
                    extension = GatewayExtensionHost.TryLoad(gateway, config);
                }
                else throw new ArgumentException("Unknown service role: " + role);
                NebulaLog.Info($"standalone {role} started; {ContainerRegistry.Count} baked containers");
                // The orchestrator ticks at the simulation rate; the gateway is a relay, and a packet it holds until its
                // next loop is latency the client sees, so it polls and forwards several times per tick.
                double period = gateway != null ? NetworkTime.TickInterval / 4 : NetworkTime.TickInterval;
                var clock = Stopwatch.StartNew();
                double next = 0;
                // Loop telemetry: how regularly the service actually ticks (the OS timer decides, not the code).
                int loops = 0; double maxPeriod = 0, maxWork = 0, workSum = 0, lastStart = 0, nextReport = 5;
                while (!stop.IsCancellationRequested)
                {
                    double start = clock.Elapsed.TotalSeconds;
                    if (loops > 0 && start - lastStart > maxPeriod) maxPeriod = start - lastStart;
                    lastStart = start;
                    control.Tick(); persistence?.Tick(); orchestrator?.Tick(); extension?.Tick(start); gateway?.Tick();
                    double work = clock.Elapsed.TotalSeconds - start;
                    workSum += work; if (work > maxWork) maxWork = work;
                    loops++;
                    if (start >= nextReport)
                    {
                        NebulaLog.Info($"loop {loops / 5f:0} Hz, period max {maxPeriod * 1000:0.0} ms, tick avg {workSum / loops * 1000:0.00} ms max {maxWork * 1000:0.0} ms");
                        loops = 0; maxPeriod = maxWork = workSum = 0; nextReport = start + 5;
                    }
                    next += period;
                    double delay = next - clock.Elapsed.TotalSeconds;
                    if (delay > 0) stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(delay));
                    else if (delay < -1) next = clock.Elapsed.TotalSeconds;
                }
                return 0;
            }
            catch (Exception e) { NebulaLog.Error(e.ToString()); return 1; }
            finally
            {
                try { web?.Dispose(); extension?.Dispose(); gateway?.Dispose(); orchestrator?.Dispose(); persistence?.Dispose(); control?.Dispose(); database?.Dispose(); }
                finally { Console.CancelKeyPress -= cancel; Console.SetOut(originalOut); Console.SetError(originalError); log?.Dispose(); }
            }
        }
        public static void ApplyOverrides(NebulaConfig c)
        {
            // Secrets may come from the environment (a systemd EnvironmentFile) instead of the command line.
            c.ControlPlaneUrl = CommandLine.Get("nebula-control-plane", c.ControlPlaneUrl);
            c.MeshToken = CommandLine.Get("nebula-token", Environment.GetEnvironmentVariable("NEBULA_MESH_TOKEN") is { Length: > 0 } token ? token : c.MeshToken);
            c.DatabaseUrl = CommandLine.Get("nebula-database", Environment.GetEnvironmentVariable("NEBULA_DATABASE_URL") is { Length: > 0 } db ? db : c.DatabaseUrl);
            c.AuthIssuers = CommandLine.Get("nebula-auth-issuers", c.AuthIssuers);
            c.AuthAudience = CommandLine.Get("nebula-auth-audience", c.AuthAudience);
            c.AuthAnonymous = CommandLine.GetBool("nebula-auth-anonymous", c.AuthAnonymous);
            c.AuthSigningKey = CommandLine.Get("nebula-auth-key", Environment.GetEnvironmentVariable("NEBULA_AUTH_KEY") is { Length: > 0 } authKey ? authKey : c.AuthSigningKey);
            c.SingleSessionPerPlayer = CommandLine.GetBool("nebula-single-session", c.SingleSessionPerPlayer);
            c.WorkerCount = CommandLine.GetInt("nebula-workers", c.WorkerCount);
            c.MinWorkers = CommandLine.GetInt("nebula-min-workers", c.MinWorkers);
            c.MaxWorkers = CommandLine.GetInt("nebula-max-workers", c.MaxWorkers);
            c.AutoScale = CommandLine.GetBool("nebula-autoscale", c.AutoScale);
            c.IdlePoolSeconds = CommandLine.GetFloat("nebula-idle-pool", c.IdlePoolSeconds);
            c.WorkerExecutable = CommandLine.Get("nebula-worker-exe", c.WorkerExecutable);
            c.WorkerHost = CommandLine.Get("nebula-host", c.WorkerHost);
            c.BuildArtifactDir = CommandLine.Get("nebula-build-dir", c.BuildArtifactDir);
            c.WorkerAdvertiseAddress = ResolveAdvertise(CommandLine.Get("nebula-advertise", c.WorkerAdvertiseAddress));
            c.DashboardPort = Port("nebula-dashboard-port", c.DashboardPort, true);
            c.UseLocalControlPlane = CommandLine.GetBool("nebula-local-control-plane", c.UseLocalControlPlane);
            c.OrchestratorSpawnsGateway = CommandLine.GetBool("nebula-spawn-gateway", c.OrchestratorSpawnsGateway);
            c.ScopeIdleRetireSeconds = CommandLine.GetFloat("nebula-scope-idle-retire", c.ScopeIdleRetireSeconds);
            c.PersistenceMode = CommandLine.Get("nebula-persistence-mode", c.PersistenceMode);
            c.PersistenceLocalFile = CommandLine.Get("nebula-persistence-file", c.PersistenceLocalFile);
            c.GatewayExtension = CommandLine.Get("nebula-gateway-extension", c.GatewayExtension);
            c.GatewayExtensionType = CommandLine.Get("nebula-gateway-extension-type", c.GatewayExtensionType);
            c.GatewayExtensionOptions = CommandLine.Get("nebula-gateway-extension-options", c.GatewayExtensionOptions);
            c.WebClients = CommandLine.GetBool("nebula-web", c.WebClients);
            c.WebPort = Port("nebula-web-port", c.WebPort, true);
            c.WebRtcPort = Port("nebula-webrtc-port", c.WebRtcPort, true);
            var gateway = CommandLine.Get("nebula-gateway");
            if (!string.IsNullOrEmpty(gateway))
            {
                int colon = gateway.LastIndexOf(':');
                if (colon < 1 || !ushort.TryParse(gateway.Substring(colon + 1), out var port) || port == 0) throw new ArgumentException("-nebula-gateway requires address:port");
                c.GatewayAddress = gateway.Substring(0, colon); c.GatewayPort = port;
            }
            if (c.WorkerCount < 0 || c.MaxWorkers < 1 || c.MaxWorkers > NebulaOrchestrator.MaxWorkersLimit || c.WorkerBasePort + c.MaxWorkers > ushort.MaxValue)
                throw new ArgumentException("Invalid worker count or worker port range in service configuration");
            if (c.MinWorkers < 0 || c.MinWorkers > c.MaxWorkers)
                throw new ArgumentException("MinWorkers must be between 0 and MaxWorkers");
        }
        private static ushort Port(string key, ushort fallback, bool allowZero)
        {
            string text = CommandLine.Get(key);
            if (text == null) return fallback;
            if (!ushort.TryParse(text, out var port) || (!allowZero && port == 0)) throw new ArgumentException("Invalid port: " + key);
            return port;
        }
        private static string ResolveAdvertise(string value)
        {
            if (!string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)) return value;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                        { var b = a.Address.GetAddressBytes(); if (b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] < 32 || b[0] == 192 && b[1] == 168) return a.Address.ToString(); }
            return "127.0.0.1";
        }
        /// <summary>The orchestrator's store, from <c>-nebula-persistence-mode</c>: <c>auto</c> and <c>database</c> use the database the orchestrator was pointed at.</summary>
        private static IPersistenceStore CreatePersistence(NebulaConfig c, DatabaseUrl url, NebulaDatabase database)
        {
            string mode = (c.PersistenceMode ?? "auto").ToLowerInvariant();
            if (mode == "auto" || mode == "") mode = c.UseLocalControlPlane ? "local" : "database";
            return mode switch
            {
                "off" or "none" => null,
                "memory" => new LocalPersistenceStore(),
                "local" or "file" => new LocalPersistenceStore(string.IsNullOrEmpty(c.PersistenceLocalFile) ? Path.Combine(AppContext.BaseDirectory, "nebula-persistence.bin") : c.PersistenceLocalFile),
                "database" => url.Scheme switch
                {
                    "memory" => new LocalPersistenceStore(),
                    "file" => new LocalPersistenceStore(Path.Combine(url.Target, "entities.bin")),
                    _ => new SqlPersistenceStore(database),
                },
                "remote" => throw new ArgumentException("The orchestrator owns the store; 'remote' is for workers"),
                _ => throw new ArgumentException("Unknown persistence mode: " + mode)
            };
        }
        private static IControlPlaneStorage CreateControlPlaneStorage(DatabaseUrl url, NebulaDatabase database)
        {
            return url.Scheme switch
            {
                "memory" => new MemoryControlPlaneStorage(),
                "file" => new FileControlPlaneStorage(Path.Combine(url.Target, "control-plane.json")),
                _ => new SqlControlPlaneStorage(database),
            };
        }
        /// <summary>
        /// The gateway's side for web builds: WebRTC data channels on UDP (<see cref="NebulaConfig.WebRtcPort"/>) and an
        /// HTTP server for signaling and the web build (<see cref="NebulaConfig.WebPort"/>). A port that cannot be opened
        /// turns web clients off with a warning; UDP clients are not affected.
        /// </summary>
        /// <summary>Only <c>/healthz</c>, on the port web clients would use, for a gateway that serves no browsers.</summary>
        private static GatewayHttpServer StartHealthOnly(NebulaConfig c)
        {
            ushort tcpPort = c.WebPort != 0 ? c.WebPort : c.GatewayPort;
            try
            {
                var http = new GatewayHttpServer(tcpPort, null, null, null);
                http.Start();
                NebulaLog.Info($"health check at http://{c.GatewayAddress}:{tcpPort}{GatewayHttpServer.HealthPath}");
                return http;
            }
            catch (Exception e)
            {
                NebulaLog.Warn($"no health check endpoint (tcp/{tcpPort}): {e.GetBaseException().Message}");
                return null;
            }
        }

        private static WebRtcServerTransport StartWebClients(NebulaConfig c, out GatewayHttpServer http)
        {
            http = null;
            ushort udpPort = c.WebRtcPort != 0 ? c.WebRtcPort : (ushort)(c.GatewayPort + 1);
            ushort tcpPort = c.WebPort != 0 ? c.WebPort : c.GatewayPort;
            var rtc = new WebRtcServerTransport("gateway-web", c.GatewayAddress);
            WebCertificates certificates = null;
            try
            {
                rtc.Listen(udpPort);
                certificates = CreateWebCertificates(c);
                http = new GatewayHttpServer(tcpPort, rtc, FindWebRoot(), certificates);
                http.Start();
                certificates?.Start();
            }
            catch (Exception e)
            {
                NebulaLog.Warn($"web clients are off (tcp/{tcpPort}, udp/{udpPort}): {e.GetBaseException().Message}");
                try
                {
                    if (http != null) http.Dispose();
                    else certificates?.Dispose();
                }
                catch { }
                http = null;
                rtc.Dispose();
                return null;
            }
            string origin = $"{(http.Secure ? "https" : "http")}://{c.GatewayAddress}:{tcpPort}";
            NebulaLog.Info($"web clients: signaling at {origin}{GatewayHttpServer.SignalingPath}, WebRTC on udp/{udpPort}; " +
                (http.WebRoot != null ? $"serving the web build from {http.WebRoot} at {origin}/" : "no web build next to the gateway"));
            return rtc;
        }

        /// <summary>
        /// HTTPS for the web port, from <c>-nebula-web-tls</c>: <c>off</c> (plain HTTP, the default) or <c>acme</c>, a
        /// certificate for GatewayAddress from an ACME CA (Let's Encrypt unless <c>-nebula-acme-directory</c>), proven on
        /// port 80 and kept in <c>-nebula-web-tls-dir</c>. A page served over HTTPS can only post its offer to HTTPS.
        /// </summary>
        private static WebCertificates CreateWebCertificates(NebulaConfig c)
        {
            string mode = (CommandLine.Get("nebula-web-tls") ?? "off").Trim().ToLowerInvariant();
            if (mode == "" || mode == "off") return null;
            if (mode != "acme") throw new ArgumentException($"-nebula-web-tls {mode}: use off or acme");
            string identifier = c.GatewayAddress;
            if (string.IsNullOrEmpty(identifier) || identifier == "127.0.0.1" || identifier.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("-nebula-web-tls acme needs the gateway's public address in -nebula-gateway <address>:<port>");
            return new WebCertificates(identifier,
                CommandLine.Get("nebula-acme-directory", AcmeClient.LetsEncrypt),
                CommandLine.Get("nebula-web-tls-dir", Path.Combine(AppContext.BaseDirectory, "tls")),
                CommandLine.Get("nebula-acme-email"),
                CommandLine.Get("nebula-acme-profile", WebCertificates.DefaultProfile(identifier)));
        }

        /// <summary>The web build to serve: -nebula-web-root, else a Web folder next to the gateway or beside its folder (Builds/Web next to Builds/Win64).</summary>
        private static string FindWebRoot()
        {
            string configured = CommandLine.Get("nebula-web-root");
            if (!string.IsNullOrEmpty(configured))
            {
                if (File.Exists(Path.Combine(configured, "index.html"))) return Path.GetFullPath(configured);
                NebulaLog.Warn($"-nebula-web-root {configured} has no index.html; not serving a web build");
                return null;
            }
            foreach (string candidate in new[] { Path.Combine(AppContext.BaseDirectory, "Web"), Path.Combine(AppContext.BaseDirectory, "..", "Web") })
                if (File.Exists(Path.Combine(candidate, "index.html"))) return Path.GetFullPath(candidate);
            return null;
        }

        internal static Process LaunchGateway(string commonArgs, Action<string, string> log)
        {
            string exe = CommandLine.Get("nebula-gateway-exe", Path.Combine(AppContext.BaseDirectory, "nebula-gateway" + (OperatingSystem.IsWindows() ? ".exe" : "")));
            string logDir = LogDirectory;
            Directory.CreateDirectory(logDir);
            var args = $"{commonArgs} -nebula-service-manifest {Quote(ServiceManifest.PathOnDisk)} -logFile {Quote(Path.Combine(logDir, "gateway.log"))}";
            // Web client and gateway-extension switches given to the orchestrator are meant for the gateway it
            // starts: the orchestrator itself has no use for either, and a run started with
            // `-nebula-gateway-extension …` that quietly dropped it would leave the mesh without the game's policy.
            foreach (string key in new[] { "nebula-web", "nebula-web-port", "nebula-webrtc-port", "nebula-web-root", "nebula-web-tls", "nebula-web-tls-dir", "nebula-acme-directory", "nebula-acme-email", "nebula-acme-profile",
                "nebula-gateway-extension", "nebula-gateway-extension-type", "nebula-gateway-extension-options" })
                if (CommandLine.Get(key) is string value) args += $" -{key} {Quote(value)}";
            // Per-extension options are a family, not a fixed list: -nebula-ext-<key> is the game's own switch.
            foreach (string key in CommandLine.Keys)
                if (key.StartsWith("nebula-ext-", StringComparison.OrdinalIgnoreCase)) args += $" -{key} {Quote(CommandLine.Get(key) ?? "")}";
            var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory });
            log("info", $"launched gateway pid={p?.Id}");
            return p;
        }
        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
    /// <summary>Holds the Windows multimedia timer at 1 ms for the life of the process (a no-op elsewhere).</summary>
    internal sealed class FineTimer : IDisposable
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState state, uint size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [StructLayout(LayoutKind.Sequential)] private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
        private const int ProcessPowerThrottling = 4;
        private const uint PowerThrottlingIgnoreTimerResolution = 0x4;
        private readonly bool _held;
        private FineTimer(bool held) { _held = held; }
        public static FineTimer Request()
        {
            if (!OperatingSystem.IsWindows()) return new FineTimer(false);
            if (Environment.GetEnvironmentVariable("NEBULA_COARSE_TIMER") == "1") { NebulaLog.Warn("NEBULA_COARSE_TIMER=1: leaving the Windows timer at its default resolution (diagnostics)"); return new FineTimer(false); }
            try
            {
                // Windows 11 ignores a timer-resolution request from a process without a foreground window (a
                // service like this one) unless the process opts out of that power throttling first.
                var state = new PowerThrottlingState { Version = 1, ControlMask = PowerThrottlingIgnoreTimerResolution, StateMask = 0 };
                bool optedOut = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
                bool held = timeBeginPeriod(1) == 0;
                NebulaLog.Info($"timer resolution 1 ms {(held ? "requested" : "refused")}; background throttling opt-out {(optedOut ? "ok" : "unavailable")}");
                return new FineTimer(held);
            }
            catch (Exception e) { NebulaLog.Warn($"could not raise the timer resolution: {e.Message}"); return new FineTimer(false); }
        }
        public void Dispose() { if (_held) timeEndPeriod(1); }
    }
    public static class NebulaLog
    {
        public static bool Verbose;
        public static void Info(string text) => Console.WriteLine($"{DateTime.UtcNow:O} [nebula] {text}");
        public static void Warn(string text) => Info("warning: " + text);
        public static void Error(string text) => Console.Error.WriteLine($"{DateTime.UtcNow:O} [nebula] error: {text}");
        public static void Debugf(string text) { if (Verbose) Info(text); }
    }
    internal static class PersistentStateCodec { public const byte Version = 1; public const string BehaviourStateSuffix = "#state"; }
    internal static class NebulaDebugOverlay
    {
        private static readonly ServicePrimitives.Color[] Colors = { new(0.95f, 0.35f, 0.30f), new(0.30f, 0.80f, 0.40f), new(0.30f, 0.55f, 0.95f), new(0.95f, 0.80f, 0.25f), new(0.80f, 0.40f, 0.90f), new(0.30f, 0.85f, 0.85f), new(0.95f, 0.55f, 0.20f), new(0.70f, 0.70f, 0.70f) };
        public static ServicePrimitives.Color ColorForWorker(ushort index) => index == ushort.MaxValue ? new(0.35f, 0.35f, 0.35f) : Colors[index % Colors.Length];
    }
}
namespace LiteNetLib
{
    internal static class Trimming
    {
        public const System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes SerializerMemberTypes = System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All;
    }
}
