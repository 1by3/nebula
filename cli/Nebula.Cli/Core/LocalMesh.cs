using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nebula.Cli.Core;

/// <summary>The local mesh: SpacetimeDB + control-plane module + orchestrator (gateway and workers) from the host build.</summary>
public static class LocalMesh
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public sealed class State
    {
        public string? StartedAt { get; set; }
        public int DashboardPort { get; set; }
        public int GatewayPort { get; set; }
        public string? SpacetimeUri { get; set; }
        public bool SpacetimeStartedByCli { get; set; }
        public string? Executable { get; set; }
    }

    public sealed record StartOptions(int Workers, int Npcs, int Bots, bool SkipPublish, bool OpenUi);

    public static void Start(Context ctx, NebulaProject project, StartOptions o)
    {
        var mesh = project.File.Mesh;
        string exe = project.HostExecutable;
        if (!File.Exists(exe))
            throw new CliError($"no build at {exe}", "run `nebula build` first, or `nebula start --build`");
        string spacetime = SpacetimeCli.Require();
        string logs = project.HostLogsDir;
        Directory.CreateDirectory(logs);

        // --- SpacetimeDB ----------------------------------------------------------------------------
        bool startedSpacetime = false;
        Ui.Step($"control plane at {mesh.SpacetimeUri}");
        if (SpacetimeCli.Ping(mesh.SpacetimeUri))
        {
            Ui.Ok("SpacetimeDB is already running");
        }
        else
        {
            var uri = new Uri(mesh.SpacetimeUri);
            Ui.Info("starting SpacetimeDB...");
            SpacetimeCli.StartLocal(Path.Combine(project.TempDir, "spacetimedb"), $"{uri.Host}:{uri.Port}", logs);
            if (!SpacetimeCli.WaitForPing(mesh.SpacetimeUri, 30))
                throw new CliError($"SpacetimeDB did not answer on {mesh.SpacetimeUri} within 30s", $"see {Path.Combine(logs, "spacetimedb.log")}");
            startedSpacetime = true;
            Ui.Ok("SpacetimeDB started");
        }

        if (!o.SkipPublish)
        {
            Ui.Info($"publishing the control-plane module as '{mesh.Database}' (fresh data)");
            SpacetimeCli.Publish(project.ModuleDir, "local", mesh.Database, deleteData: true);
            Ui.Ok("module published");
        }

        // --- orchestrator (launches the gateway and the workers) ----------------------------------------------
        Ui.Step($"starting the orchestrator with {o.Workers} worker(s), {o.Npcs} NPC(s)");
        var orch = new List<string>
        {
            "-batchmode", "-nographics",
            "-nebula-role", "orchestrator",
            "-nebula-workers", o.Workers.ToString(),
            "-nebula-settings", $"npcs={o.Npcs}",
            "-nebula-dashboard-port", mesh.DashboardPort.ToString(),
            "-nebula-spacetime", mesh.SpacetimeUri,
            "-nebula-database", mesh.Database,
            "-logFile", Path.Combine(logs, "orchestrator.log"),
        };
        if (ctx.Verbose) orch.Add("-nebula-verbose");
        Shell.Detach(exe, orch, project.HostBuildDir, null);

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
            SpacetimeUri = mesh.SpacetimeUri,
            SpacetimeStartedByCli = startedSpacetime || (LoadState(project)?.SpacetimeStartedByCli ?? false),
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
        Ui.Info($"logs        {logs}  (`nebula logs orchestrator|gateway|w1`)");
        Ui.Info("stop        nebula stop");
        if (o.OpenUi) Platform.OpenBrowser(dashboard);
    }

    public static void Stop(NebulaProject project, bool stopSpacetime)
    {
        var state = LoadState(project);
        int killed = Shell.KillProcesses(project.HostBuildDir);
        Ui.Ok(killed > 0 ? $"stopped {killed} {project.File.Executable} process(es)" : $"no {project.File.Executable} processes were running");
        if (stopSpacetime || (state?.SpacetimeStartedByCli ?? false))
        {
            int s = Shell.KillProcesses(null, "spacetime", "spacetimedb-cli", "spacetimedb-standalone");
            if (s > 0) Ui.Ok($"stopped SpacetimeDB ({s} process(es))");
            if (state != null) state.SpacetimeStartedByCli = false;
        }
        if (state != null)
        {
            state.StartedAt = null;
            File.WriteAllText(project.MeshStateFile, JsonSerializer.Serialize(state, CliConfig.Json));
        }
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

    public static JsonNode? WaitForDashboard(string dashboardUrl, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var s = FetchState(dashboardUrl);
            if (s != null) return s;
            Thread.Sleep(1000);
        }
        return null;
    }

    public static string Summary(JsonNode s)
    {
        var t = s["totals"];
        return $"{t?["liveWorkers"] ?? s["workers"]?.AsArray().Count} live worker(s), {t?["players"] ?? 0} player(s), {t?["bots"] ?? 0} bot(s), {t?["serverDriven"] ?? 0} NPC(s), desired {s["desiredWorkers"]}";
    }

    /// <summary>Print the dashboard snapshot the way `nebula status` shows it.</summary>
    public static void PrintState(JsonNode s)
    {
        Ui.Info($"host={s["host"]} ready={s["hostReady"]} controlPlane={s["controlPlaneConnected"]} desired={s["desiredWorkers"]}");
        Ui.Info(Summary(s));
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
