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
                Console.WriteLine($"nebula-{role} -nebula-service-manifest <nebula-services.json> [-nebula-token <secret>] [-logFile <path>]");
                Console.WriteLine("Orchestrator: -nebula-worker-exe <Unity player> -nebula-workers <count> -nebula-dashboard-port <port> [-nebula-database sqlite:<file>|postgres://...|memory] [-nebula-reset-persistence]");
                Console.WriteLine("Gateway: -nebula-gateway <advertised-address:port> -nebula-control-plane <orchestrator url>");
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
            NebulaDatabase database = null;
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
                    gateway.Initialize(config, control);
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
                    control.Tick(); persistence?.Tick(); orchestrator?.Tick(); gateway?.Tick();
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
                try { gateway?.Dispose(); orchestrator?.Dispose(); persistence?.Dispose(); control?.Dispose(); database?.Dispose(); }
                finally { Console.CancelKeyPress -= cancel; Console.SetOut(originalOut); Console.SetError(originalError); log?.Dispose(); }
            }
        }
        public static void ApplyOverrides(NebulaConfig c)
        {
            // Secrets may come from the environment (a systemd EnvironmentFile) instead of the command line.
            c.ControlPlaneUrl = CommandLine.Get("nebula-control-plane", c.ControlPlaneUrl);
            c.MeshToken = CommandLine.Get("nebula-token", Environment.GetEnvironmentVariable("NEBULA_MESH_TOKEN") is { Length: > 0 } token ? token : c.MeshToken);
            c.DatabaseUrl = CommandLine.Get("nebula-database", Environment.GetEnvironmentVariable("NEBULA_DATABASE_URL") is { Length: > 0 } db ? db : c.DatabaseUrl);
            c.WorkerCount = CommandLine.GetInt("nebula-workers", c.WorkerCount);
            c.WorkerExecutable = CommandLine.Get("nebula-worker-exe", c.WorkerExecutable);
            c.WorkerHost = CommandLine.Get("nebula-host", c.WorkerHost);
            c.BuildArtifactDir = CommandLine.Get("nebula-build-dir", c.BuildArtifactDir);
            c.WorkerAdvertiseAddress = ResolveAdvertise(CommandLine.Get("nebula-advertise", c.WorkerAdvertiseAddress));
            c.DashboardPort = Port("nebula-dashboard-port", c.DashboardPort, true);
            c.UseLocalControlPlane = CommandLine.GetBool("nebula-local-control-plane", c.UseLocalControlPlane);
            c.OrchestratorSpawnsGateway = CommandLine.GetBool("nebula-spawn-gateway", c.OrchestratorSpawnsGateway);
            c.PersistenceMode = CommandLine.Get("nebula-persistence-mode", c.PersistenceMode);
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
