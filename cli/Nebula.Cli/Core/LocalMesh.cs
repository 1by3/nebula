using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nebula.Cli.Core;

/// <summary>The local mesh: the standalone orchestrator (which hosts the control plane and keeps the world in a SQLite file), the gateway it starts, and Unity workers from the host build.</summary>
public static class LocalMesh
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public sealed class State
    {
        public string? StartedAt { get; set; }
        public int DashboardPort { get; set; }
        public int GatewayPort { get; set; }
        public string? Database { get; set; }
        public string? Executable { get; set; }
    }

    /// <param name="Workers">Workers to start with.</param>
    /// <param name="MinWorkers">Autoscaling floor; equal to <paramref name="Workers"/> for a fixed mesh.</param>
    /// <param name="MaxWorkers">Autoscaling ceiling; equal to <paramref name="Workers"/> for a fixed mesh.</param>
    public sealed record StartOptions(int Workers, int MinWorkers, int MaxWorkers, int Npcs, int Bots, bool OpenUi, bool ResetPersistence = false);

    public static void Start(Context ctx, NebulaProject project, StartOptions o)
    {
        var mesh = project.File.Mesh;
        string exe = project.HostExecutable;
        if (!File.Exists(exe))
            throw new CliError($"no build at {exe}", "run `nebula build` first, or `nebula start --build`");
        ServiceBuild.Require(project.HostBuildDir);
        string logs = project.HostLogsDir;
        StopRunningMeshes(ctx, mesh.GatewayPort, mesh.DashboardPort);
        Directory.CreateDirectory(logs);

        // --- database ------------------------------------------------------------------------------------
        string database = project.LocalDatabase;
        Ui.Step($"control plane and saved entities in {database}");
        if (database.StartsWith("sqlite:", StringComparison.OrdinalIgnoreCase))
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database.Substring("sqlite:".Length)))!);
        if (o.ResetPersistence) Ui.Warn("--reset-persistence: every saved entity is deleted when the orchestrator starts");

        // --- orchestrator (launches the gateway and the workers) ----------------------------------------------
        Ui.Step($"starting the orchestrator with {o.Workers} worker(s){(o.MinWorkers == o.MaxWorkers ? " (fixed)" : $", autoscaling between {o.MinWorkers} and {o.MaxWorkers}")} and the game-defined 'npcs' setting at {o.Npcs}");
        var orch = new List<string>
        {
            "-nebula-worker-exe", exe,
            "-nebula-service-manifest", Path.Combine(project.HostBuildDir, ServiceBuild.ManifestName),
            "-nebula-gateway", $"127.0.0.1:{mesh.GatewayPort}",
            "-nebula-workers", o.Workers.ToString(),
            "-nebula-min-workers", o.MinWorkers.ToString(),
            "-nebula-max-workers", o.MaxWorkers.ToString(),
            "-nebula-idle-pool", mesh.IdlePoolSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-nebula-settings", $"npcs={o.Npcs}",
            "-nebula-dashboard-port", mesh.DashboardPort.ToString(),
            "-nebula-database", database,
            "-logFile", Path.Combine(logs, "orchestrator.log"),
        };
        if (o.ResetPersistence) orch.Add("-nebula-reset-persistence");
        if (ctx.Verbose) orch.Add("-nebula-verbose");
        Shell.Detach(ServiceBuild.Executable(project.HostBuildDir, "orchestrator"), orch, project.HostBuildDir, null);

        for (int b = 1; b <= o.Bots; b++)
        {
            Shell.Detach(exe, new[]
            {
                "-batchmode", "-nographics", "-nebula-role", "client", "-nebula-bot", "-nebula-name", $"bot{b}",
                "-nebula-gateway", $"127.0.0.1:{mesh.GatewayPort}",
                "-logFile", Path.Combine(logs, $"bot{b}.log"),
            }, project.HostBuildDir, null);
        }
        if (o.Bots > 0) Ui.Info($"started {o.Bots} bot client(s)");

        Directory.CreateDirectory(project.CliStateDir);
        var state = new State
        {
            StartedAt = DateTime.UtcNow.ToString("o"),
            DashboardPort = mesh.DashboardPort,
            GatewayPort = mesh.GatewayPort,
            Database = database,
            Executable = exe,
        };
        File.WriteAllText(project.MeshStateFile, JsonSerializer.Serialize(state, CliConfig.Json));

        string dashboard = $"http://localhost:{mesh.DashboardPort}/";
        Ui.Info("waiting for the dashboard...");
        var snapshot = WaitForDashboard(dashboard, 60);
        if (snapshot == null)
        {
            Ui.Warn($"the dashboard at {dashboard} is not answering yet; check {Path.Combine(logs, "orchestrator.log")}");
        }
        else
        {
            Ui.Ok($"mesh is up: {Summary(snapshot)}");
        }
        Ui.Blank();
        Ui.Info($"dashboard   {dashboard}");
        Ui.Info($"gateway     127.0.0.1:{mesh.GatewayPort}  (press Play in the Editor, or run the client build)");
        if (File.Exists(project.WebIndex)) Ui.Info($"web client  http://127.0.0.1:{mesh.GatewayPort}/  (Builds/Web, served by the gateway)");
        Ui.Info($"logs        {logs}  (`nebula logs orchestrator|gateway|w1`)");
        Ui.Info("stop        nebula stop");
        if (o.OpenUi) Platform.OpenBrowser(dashboard);
    }

    public static void Stop(NebulaProject project)
    {
        var state = LoadState(project);
        int killed = Shell.KillProcesses(project.HostBuildDir);
        Ui.Ok(killed > 0 ? $"stopped {killed} {project.File.Executable} process(es)" : $"no {project.File.Executable} processes were running");
        if (state != null)
        {
            state.StartedAt = null;
            File.WriteAllText(project.MeshStateFile, JsonSerializer.Serialize(state, CliConfig.Json));
        }
    }

    /// <summary>The processes of one local mesh, grouped by the build folder they run from.</summary>
    public sealed record RunningMesh(string BuildDir, int Processes)
    {
        /// <summary>The project folder when the build sits at the usual Builds/&lt;platform&gt;, else the build folder.</summary>
        public string Project
        {
            get
            {
                var parent = Directory.GetParent(BuildDir);
                return parent != null && parent.Name == "Builds" && parent.Parent != null ? parent.Parent.FullName : BuildDir;
            }
        }
    }

    /// <summary>
    /// Every mesh running on this computer, from this project's build or another's. A process counts when it runs
    /// from a build folder holding the orchestrator or gateway log that <c>nebula start</c> writes there.
    /// </summary>
    public static List<RunningMesh> FindRunningMeshes()
    {
        var byDir = new Dictionary<string, int>(Platform.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                // Every Unity player starts a crash handler from its folder; it goes with the player, so do not count it.
                if (p.Id == self || p.ProcessName.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase)) continue;
                string? file = null;
                try { file = p.MainModule?.FileName; } catch { }
                if (file == null || BuildDirOf(file) is not { } dir) continue;
                byDir[dir] = byDir.GetValueOrDefault(dir) + 1;
            }
            finally { p.Dispose(); }
        }
        return byDir.Select(kv => new RunningMesh(kv.Key, kv.Value)).OrderBy(m => m.BuildDir).ToList();
    }

    /// <summary>The mesh build folder an executable runs from, or null when it is not part of a local mesh.</summary>
    internal static string? BuildDirOf(string executable)
    {
        string? dir = Path.GetDirectoryName(executable);
        if (dir == null) return null;
        // macOS players run from Builds/MacOS/<Name>.app/Contents/MacOS/<Name>; the logs sit next to the .app.
        var app = Directory.GetParent(dir)?.Parent;
        if (Path.GetFileName(dir) == "MacOS" && app != null && app.Name.EndsWith(".app", StringComparison.Ordinal) && app.Parent != null)
            dir = app.Parent.FullName;
        string logs = Path.Combine(dir, "Logs");
        return File.Exists(Path.Combine(logs, "orchestrator.log")) || File.Exists(Path.Combine(logs, "gateway.log")) ? dir : null;
    }

    /// <summary>
    /// A second mesh cannot bind the gateway, worker and dashboard ports, and republishing the control plane wipes
    /// the state the running one depends on. Offer to stop whatever is running, then make sure the ports are free.
    /// </summary>
    private static void StopRunningMeshes(Context ctx, int gatewayPort, int dashboardPort)
    {
        var running = FindRunningMeshes();
        if (running.Count > 0)
        {
            Ui.Warn(running.Count == 1 ? "a local mesh is already running:" : $"{running.Count} local meshes are already running:");
            foreach (var m in running) Ui.Info($"{m.Project}  ({m.Processes} process(es) from {m.BuildDir})");
            Ui.Info("only one mesh can hold the gateway, worker and dashboard ports");
            string stopIt = running.Count == 1 ? "stop it" : "stop them";
            if (Console.IsInputRedirected && !ctx.Yes)
                throw new CliError("another local mesh is running", $"pass --yes to {stopIt}, or run `nebula stop` in its project folder");
            if (!Ui.Confirm($"{stopIt} and start this project's mesh?", true, ctx.Yes))
                throw new CliError("another local mesh is running", "run `nebula stop` in its project folder, then start again");
            foreach (var m in running)
            {
                int killed = Shell.KillProcesses(m.BuildDir);
                Ui.Ok($"stopped {m.Project} ({killed} process(es))");
            }
            var dirs = running.Select(m => m.BuildDir).ToList();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && dirs.Any(d => Shell.RunningUnder(d).Any())) Thread.Sleep(250);
        }

        if (!UdpPortFree(gatewayPort))
            throw new CliError($"UDP port {gatewayPort} (the gateway) is in use by another program", "stop it, or change mesh.gatewayPort in nebula.json");
        if (!TcpPortFree(gatewayPort))
            Ui.Warn($"TCP port {gatewayPort} is in use by another program; the gateway will not accept web clients");
        if (!TcpPortFree(dashboardPort))
            throw new CliError($"TCP port {dashboardPort} (the dashboard) is in use by another program", "stop it, or change mesh.dashboardPort in nebula.json");
    }

    private static bool UdpPortFree(int port)
    {
        try { using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, port)); return true; }
        catch (SocketException) { return false; }
    }

    private static bool TcpPortFree(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); return true; }
        catch (SocketException) { return false; }
        finally { listener.Stop(); }
    }

    public static State? LoadState(NebulaProject project)
    {
        try
        {
            if (!File.Exists(project.MeshStateFile)) return null;
            return JsonSerializer.Deserialize<State>(File.ReadAllText(project.MeshStateFile), CliConfig.Json);
        }
        catch { return null; }
    }

    public static JsonNode? FetchState(string dashboardUrl)
    {
        try
        {
            var text = Http.GetStringAsync(dashboardUrl.TrimEnd('/') + "/api/state").GetAwaiter().GetResult();
            return JsonNode.Parse(text);
        }
        catch { return null; }
    }

    /// <summary>POST a JSON body to the orchestrator's dashboard API; null on success, else the error text.</summary>
    public static string? PostApi(string dashboardUrl, string path, object body)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var resp = Http.PostAsync(dashboardUrl.TrimEnd('/') + path, content).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode) return null;
            string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            try { text = JsonNode.Parse(text)?["error"]?.ToString() ?? text; } catch { }
            return $"{(int)resp.StatusCode} {text}".Trim();
        }
        catch (Exception e) { return e.Message; }
    }

    public static JsonNode? WaitForDashboard(string dashboardUrl, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var s = FetchState(dashboardUrl);
            // The HTTP server answers before the orchestrator has a mesh to describe, and that first snapshot has no
            // worker list: waiting for it keeps the summary line from printing blanks.
            if (s != null && s["desiredWorkers"] != null && s["workers"] != null) return s;
            Thread.Sleep(1000);
        }
        return null;
    }

    public static string Summary(JsonNode s)
    {
        var t = s["totals"];
        object live = (object?)t?["liveWorkers"] ?? s["workers"]?.AsArray().Count ?? 0;
        return $"{live} live worker(s), {t?["players"] ?? 0} player(s), {t?["bots"] ?? 0} bot(s), {t?["serverDriven"] ?? 0} NPC(s), desired {s["desiredWorkers"] ?? 0}";
    }

    /// <summary>Print the dashboard snapshot the way `nebula status` shows it.</summary>
    public static void PrintState(JsonNode s)
    {
        Ui.Info($"host={s["host"]} ready={s["hostReady"]} controlPlane={s["controlPlaneConnected"]}{(s["controlPlaneStorage"] is { } cps && cps.ToString().Length > 0 ? " stored in " + cps : "")} desired={s["desiredWorkers"]}");
        Ui.Info(Summary(s));
        // Scale to zero: say why there is no worker, and who is waiting for one.
        var sc = s["scale"];
        int pendingJoins = sc?["pendingJoins"] is { } pj && int.TryParse(pj.ToString(), out var pjv) ? pjv : 0;
        if (pendingJoins > 0)
            Ui.Warn($"world starting: {pendingJoins} client(s) waiting to join (about {s["hostBootSeconds"] ?? 0} s on this host)");
        else if (sc?["scaleToZero"] is { } z && z.GetValue<bool>())
            Ui.Info("scale to zero is on: an idle mesh keeps no workers, and the first player waits for one to boot");
        // Older builds have no persistence layer and report no "persistence" object at all.
        if (s["persistence"] is { } p)
            Ui.Info($"persistence mode={p["mode"]} backend={p["backend"]} connected={p["connected"]} entities={p["entities"] ?? 0}");
        Ui.Blank();
        var rows = new List<string[]>();
        foreach (var w in s["workers"]?.AsArray() ?? new JsonArray())
        {
            if (w == null) continue;
            rows.Add(new[]
            {
                w["id"]?.ToString() ?? "", w["state"]?.ToString() ?? "", w["address"]?.ToString() ?? "",
                string.Join(",", w["containers"]?.AsArray().Select(c => c?.ToString()) ?? Array.Empty<string>()),
                w["players"]?.ToString() ?? "0", w["bots"]?.ToString() ?? "0", w["serverDriven"]?.ToString() ?? "0",
                w["tickMs"]?.ToString() ?? "", w["authoritative"]?.ToString() ?? "", w["ghosts"]?.ToString() ?? "",
                w["heartbeatAgeSeconds"] is { } hb && double.TryParse(hb.ToString(), out var d) ? d.ToString("F1") : "",
            });
        }
        Ui.Table(new[] { "worker", "state", "address", "containers", "players", "bots", "npcs", "tick ms", "auth", "ghosts", "hb s" }, rows);
        var events = s["events"]?.AsArray();
        if (events is { Count: > 0 })
        {
            Ui.Blank();
            Ui.Info("recent events:");
            foreach (var e in events.Take(10))
            {
                if (e == null) continue;
                string time = e["time"]?.ToString() ?? "";
                if (time.Length >= 19) time = time.Substring(11, 8);
                Ui.Info($"  {time} [{e["level"]}] {e["message"]}");
            }
        }
    }
}
