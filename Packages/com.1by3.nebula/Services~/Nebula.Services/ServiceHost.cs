using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nebula
{
    public static class ServiceHost
    {
        internal static string LogDirectory => Path.GetDirectoryName(Path.GetFullPath(CommandLine.Get("logFile", Path.Combine(AppContext.BaseDirectory, "Logs", "orchestrator.log"))));
        public static int Run(string role, CancellationToken cancellationToken = default)
        {
            if (CommandLine.Has("help"))
            {
                Console.WriteLine($"nebula-{role} -nebula-service-manifest <nebula-services.json> [-nebula-spacetime <url>] [-nebula-database <name>] [-logFile <path>]");
                Console.WriteLine("Orchestrator: -nebula-worker-exe <Unity player> -nebula-workers <count> -nebula-dashboard-port <port>");
                Console.WriteLine("Gateway: -nebula-gateway <advertised-address:port>");
                return 0;
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            using var term = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
            NebulaOrchestrator orchestrator = null;
            NebulaGateway gateway = null;
            IControlPlane control = null;
            IPersistenceStore persistence = null;
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
                var manifest = ServiceManifest.Load(CommandLine.Get("nebula-service-manifest", Path.Combine(AppContext.BaseDirectory, "nebula-services.json")));
                var config = manifest.Config;
                ApplyOverrides(config);
                if (config.UseLocalControlPlane && !CommandLine.Has("nebula-local-control-plane"))
                    throw new InvalidOperationException("A standalone mesh requires SpacetimeDB; disable UseLocalControlPlane in the exported configuration.");
                // Explicit local mode supports isolated service diagnostics; it cannot connect separate processes.
                control = config.UseLocalControlPlane ? (IControlPlane)new LocalControlPlane() : new SpacetimeControlPlane(config.SpacetimeUri, config.SpacetimeDatabase);
                control.Connect();
                if (role == "orchestrator")
                {
                    if (config.WorkerHost == "process" && !config.UseLocalControlPlane && !File.Exists(config.WorkerExecutable))
                        throw new FileNotFoundException("Set -nebula-worker-exe to the Unity worker executable", config.WorkerExecutable);
                    persistence = CreatePersistence(config);
                    persistence?.Connect();
                    orchestrator = new NebulaOrchestrator { Persistence = persistence };
                    orchestrator.Initialize(config, control);
                }
                else if (role == "gateway") { gateway = new NebulaGateway(); gateway.Initialize(config, control); }
                else throw new ArgumentException("Unknown service role: " + role);
                NebulaLog.Info($"standalone {role} started; {ContainerRegistry.Count} baked containers");
                var clock = Stopwatch.StartNew();
                double next = 0;
                while (!stop.IsCancellationRequested)
                {
                    control.Tick(); persistence?.Tick(); orchestrator?.Tick(); gateway?.Tick();
                    next += NetworkTime.TickInterval;
                    double delay = next - clock.Elapsed.TotalSeconds;
                    if (delay > 0) stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(delay));
                    else if (delay < -1) next = clock.Elapsed.TotalSeconds;
                }
                return 0;
            }
            catch (Exception e) { NebulaLog.Error(e.ToString()); return 1; }
            finally
            {
                try { gateway?.Dispose(); orchestrator?.Dispose(); persistence?.Dispose(); control?.Dispose(); }
                finally { Console.CancelKeyPress -= cancel; Console.SetOut(originalOut); Console.SetError(originalError); log?.Dispose(); }
            }
        }
        public static void ApplyOverrides(NebulaConfig c)
        {
            c.SpacetimeUri = CommandLine.Get("nebula-spacetime", c.SpacetimeUri);
            c.SpacetimeDatabase = CommandLine.Get("nebula-database", c.SpacetimeDatabase);
            c.WorkerCount = CommandLine.GetInt("nebula-workers", c.WorkerCount);
            c.WorkerExecutable = CommandLine.Get("nebula-worker-exe", c.WorkerExecutable);
            c.WorkerHost = CommandLine.Get("nebula-host", c.WorkerHost);
            c.BuildArtifactDir = CommandLine.Get("nebula-build-dir", c.BuildArtifactDir);
            c.WorkerAdvertiseAddress = ResolveAdvertise(CommandLine.Get("nebula-advertise", c.WorkerAdvertiseAddress));
            c.DashboardPort = Port("nebula-dashboard-port", c.DashboardPort, true);
            c.UseLocalControlPlane = CommandLine.GetBool("nebula-local-control-plane", c.UseLocalControlPlane);
            c.OrchestratorSpawnsGateway = CommandLine.GetBool("nebula-spawn-gateway", c.OrchestratorSpawnsGateway);
            c.PersistenceMode = CommandLine.Get("nebula-persistence-mode", c.PersistenceMode);
            c.PersistenceUri = CommandLine.Get("nebula-persistence", c.PersistenceUri);
            c.PersistenceDatabase = CommandLine.Get("nebula-persistence-database", c.PersistenceDatabase);
            c.PersistenceLocalFile = CommandLine.Get("nebula-persistence-file", c.PersistenceLocalFile);
            var gateway = CommandLine.Get("nebula-gateway");
            if (!string.IsNullOrEmpty(gateway))
            {
                int colon = gateway.LastIndexOf(':');
                if (colon < 1 || !ushort.TryParse(gateway.Substring(colon + 1), out var port) || port == 0) throw new ArgumentException("-nebula-gateway requires address:port");
                c.GatewayAddress = gateway.Substring(0, colon); c.GatewayPort = port;
            }
            if (c.WorkerCount < 0 || c.MaxWorkers < 1 || c.MaxWorkers > NebulaOrchestrator.MaxWorkersLimit || c.WorkerBasePort + c.MaxWorkers > ushort.MaxValue)
                throw new ArgumentException("Invalid worker count or worker port range in service configuration");
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
        private static IPersistenceStore CreatePersistence(NebulaConfig c)
        {
            string mode = (c.PersistenceMode ?? "auto").ToLowerInvariant();
            if (mode == "auto" || mode == "") mode = c.UseLocalControlPlane ? "local" : "spacetime";
            return mode switch
            {
                "off" or "none" => null,
                "memory" => new LocalPersistenceStore(),
                "local" or "file" => new LocalPersistenceStore(string.IsNullOrEmpty(c.PersistenceLocalFile) ? Path.Combine(AppContext.BaseDirectory, "nebula-persistence.bin") : c.PersistenceLocalFile),
                "spacetime" => new SpacetimePersistenceStore(string.IsNullOrEmpty(c.PersistenceUri) ? c.SpacetimeUri : c.PersistenceUri, c.PersistenceDatabase),
                _ => throw new ArgumentException("Unknown persistence mode: " + mode)
            };
        }
        internal static Process LaunchGateway(string commonArgs, Action<string, string> log)
        {
            string exe = CommandLine.Get("nebula-gateway-exe", Path.Combine(AppContext.BaseDirectory, "nebula-gateway" + (OperatingSystem.IsWindows() ? ".exe" : "")));
            string logDir = LogDirectory;
            Directory.CreateDirectory(logDir);
            var args = $"{commonArgs} -nebula-service-manifest {Quote(ServiceManifest.PathOnDisk)} -logFile {Quote(Path.Combine(logDir, "gateway.log"))}";
            var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory });
            log("info", $"launched gateway pid={p?.Id}");
            return p;
        }
        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
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
