using Nebula.Cli.Cloud;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class StartCommand : Command
{
    public override string Name => "start";
    public override string Summary => "Start the orchestrator, gateway, and workers on this computer";
    public override string Usage => "[--build] [--workers N] [--min N] [--max N] [--npcs N] [--bots N] [--bot-args \"...\"] [--open-ui] [--reset-persistence] [--reset-sessions]";
    public override string? Details => @"
Start the orchestrator from the latest build. The orchestrator hosts the control plane on its dashboard port and
starts the gateway and workers. To join, enter Play mode in the Unity Editor or start the build with the client
role. Set defaults in the mesh section of nebula.json.

Only one local mesh can run at a time. When a mesh from this or another project is already running, list it and
offer to stop it first (--yes stops it without asking). The start fails when the gateway or dashboard port is still
held by another program.

The orchestrator keeps the control plane and every saved entity in one database, by default a SQLite file at
Library/Nebula/nebula.db (mesh.database in nebula.json changes it: `sqlite:<file>`, `postgres://...` or `memory`).
The control plane starts empty on every run because it only holds which processes are running; saved entities
stay across restarts unless you pass --reset-persistence.

--reset-sessions clears held player-session claims without deleting saved entities. Use it only after every
gateway of this mesh has stopped, to recover claims whose previous gateway cannot acknowledge disconnection.

--bots starts headless client processes with -nebula-bot; what they do is the game's code. --bot-args appends
its value to every one of those command lines unchanged, which is how a game selects a deterministic bot
behavior for a soak run. The example flag must be implemented by your game
(`nebula start --bots 4 --bot-args ""-mygame-bot-line""`).
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("build", false, "build first (nebula build)"),
        new OptionSpec("workers", true, "worker processes to start with; on its own it fixes the count (min = max = N). Default from nebula.json, 4", "N"),
        WorkerBand.MinOption, WorkerBand.MaxOption,
        new OptionSpec("npcs", true, "set the game-defined 'npcs' mesh setting at startup (default 0)", "N"),
        new OptionSpec("bots", true, "start headless clients with the bot flag; the game supplies their behavior (default 0)", "N"),
        new OptionSpec("bot-args", true, "extra command-line arguments appended to every bot client, e.g. --bot-args \"-mygame-bot-line\"; the game reads them itself", "ARGS"),
        new OptionSpec("open-ui", false, "open the Nebula Dashboard in the browser once it is up"),
        new OptionSpec("reset-persistence", false, "delete every saved entity when the orchestrator starts"),
        new OptionSpec("reset-sessions", false, "clear player-session claims; stop every gateway of this mesh first"),
    };
    public override string[] Examples => new[] { "nebula start --open-ui", "nebula start --build --workers 2", "nebula start --min 1 --max 4", "nebula start --reset-persistence", "nebula start --bots 3 --bot-args \"-mygame-bot-line\"" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        if (args.Has("build"))
            UnityBuild.Build(ctx, project, new UnityBuild.Options(BuildTarget.Host, StopMesh: true));
        var mesh = project.File.Mesh;
        var band = WorkerBand.Resolve(args, mesh.Workers, mesh.MinWorkers, mesh.MaxWorkers);
        LocalMesh.Start(ctx, project, new LocalMesh.StartOptions(
            band.Start, band.Min, band.Max, args.GetInt("npcs", mesh.Npcs), args.GetInt("bots", 0),
            args.Has("open-ui"), args.Has("reset-persistence"), args.Has("reset-sessions"), args.Get("bot-args")));
        return 0;
    }
}

/// <summary>The worker range `nebula start` and `nebula deploy` share: --workers N alone fixes the count, --min/--max open the band.</summary>
public readonly record struct WorkerBand(int Start, int Min, int Max)
{
    public static readonly OptionSpec MinOption = new("min", true, "autoscaling floor; the mesh starts here when --workers is not given, and 0 allows scaling to zero (default: --workers)", "N");
    public static readonly OptionSpec MaxOption = new("max", true, "autoscaling ceiling; autoscaling grows into the band from --min (default: --workers)", "N");

    public static WorkerBand Resolve(ParsedArgs args, int defaultWorkers, int? defaultMin, int? defaultMax)
    {
        // --workers alone means a fixed mesh (min = max = N), which is what it has always meant; --min/--max open the band.
        int workers = args.GetInt("workers", defaultWorkers);
        int min = args.GetInt("min", args.Has("workers") ? workers : defaultMin ?? workers);
        int max = args.GetInt("max", args.Has("workers") ? workers : defaultMax ?? workers);
        if (min < 0 || max < 1 || min > max) throw new CliError($"invalid worker range {min}..{max}", "0 <= --min <= --max and --max >= 1");
        // A band without an explicit count starts at the floor and lets autoscaling grow into the band; anything else
        // would start a mesh at `--max` and only ever shrink, which is not what `--min 1 --max 4` reads as.
        bool band = args.Has("min") || args.Has("max");
        int start = args.Has("workers") || !band ? Math.Clamp(workers, min, max) : min;
        return new WorkerBand(start, min, max);
    }

    public string Describe() => Min == Max ? $"{Max} worker(s)" : $"{Start} worker(s), autoscaling {Min}..{Max}";
}

public sealed class StopCommand : Command
{
    public override string Name => "stop";
    public override string Summary => "Stop the local mesh (every process of the build)";

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        LocalMesh.Stop(project);
        return 0;
    }
}

public sealed class StatusCommand : Command
{
    public override string Name => "status";
    public override string Summary => "Show workers, containers, entity counts, persistence, and recent events";
    public override string Usage => "[--cloud] [--target hetzner|cloud] [--json]";
    public override string? Details => @"
Without --cloud, show the local mesh from its dashboard. With --cloud, show the deployed mesh: on Nebula Cloud the
deployment's health, orchestrator, gateways (clients, traffic, CPU, loop lag, draining), workers and mesh totals; on
Hetzner the servers plus the orchestrator's dashboard state.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("cloud", false, "the deployed mesh (Nebula Cloud, or the Hetzner servers + their Nebula Dashboard) instead of the local one"),
        CloudTarget.TargetOption,
        new OptionSpec("json", false, "print the raw state JSON"),
        CloudTarget.OrgOption, CloudTarget.CloudProjectOption, CloudTarget.DeploymentOption,
    };
    public override string[] Examples => new[] { "nebula status", "nebula status --cloud", "nebula status --cloud --json" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        string url, gateway;
        if (args.Has("cloud") && CloudTarget.IsCloud(args, project))
        {
            var api = CloudApi.Require(ctx);
            var t = CloudTarget.Resolve(ctx, api, project, args, create: false);
            var (status, raw) = api.GetStatus(t.Deployment.Id);
            if (args.Has("json")) { Console.WriteLine(raw.ToJsonString(CliConfig.Json)); return 0; }
            Ui.Blank();
            CloudStatus.Print(status, project.File.Executable);
            return status.Health is "down" ? 1 : 0;
        }
        if (args.Has("cloud"))
        {
            var hz = ctx.Config.Hetzner;
            if (hz == null || !hz.IsConfigured) throw new CliError("Hetzner is not configured", "nebula config hetzner");
            var mesh = new HetznerMesh(hz, project);
            var servers = mesh.Servers();
            mesh.PrintServers(servers);
            var orch = servers.FirstOrDefault(s => s["name"]?.ToString() == mesh.OrchestratorName);
            if (orch == null) { Ui.Warn("no orchestrator server; run `nebula deploy`"); return 1; }
            string ip = HetznerMesh.PublicIp(orch);
            url = $"http://{ip}:{mesh.DashboardPort}/";
            gateway = $"{ip}:{mesh.GatewayPort}";
        }
        else
        {
            url = $"http://localhost:{project.File.Mesh.DashboardPort}/";
            gateway = $"127.0.0.1:{project.File.Mesh.GatewayPort}";
        }
        Ui.Blank();
        var state = LocalMesh.FetchState(url);
        if (state == null)
        {
            Ui.Warn($"the dashboard at {url} is not answering" + (args.Has("cloud") ? "" : "; is the mesh running? (`nebula start`)"));
            return 1;
        }
        if (args.Has("json")) { Console.WriteLine(state.ToJsonString(CliConfig.Json)); return 0; }
        // Prefer the address the orchestrator actually hands its gateway; older builds do not report it.
        if (state["gatewayAddress"]?.ToString() is { Length: > 0 } advertised) gateway = advertised;
        Ui.Info($"dashboard {url}");
        Ui.Info($"gateway   {gateway}   (client: {project.File.Executable} -nebula-role client -nebula-gateway {gateway})");
        LocalMesh.PrintState(state);
        return 0;
    }
}

public sealed class LogsCommand : Command
{
    public override string Name => "logs";
    public override string Summary => "Read the log for the orchestrator, gateway, a worker, or a bot client";
    public override string Usage => "[role] [--lines N] [--follow] [--cloud] [--instance x] [--since 10m]";
    public override string? Details => @"
Locally, the role is a log file next to the build: orchestrator, gateway, w1..wN, bot1... On Nebula Cloud the role
is orchestrator, gateway, worker or all; a worker or gateway name (w1, gw2) selects that instance, as does
--instance. --since takes a duration (10m, 2h) or an RFC 3339 time, and --follow streams new lines as they arrive.
On Hetzner the CLI reads the files over ssh (no --follow).
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("lines", true, "lines to show (default 60)", "N", "n"),
        new OptionSpec("follow", false, "keep printing as the log grows (local and Nebula Cloud)", null, "f"),
        new OptionSpec("cloud", false, "read the log of the deployed mesh"),
        new OptionSpec("instance", true, "cloud: one instance (w1, gw2) of the role", "name"),
        new OptionSpec("since", true, "cloud: only lines newer than a duration ago (10m, 2h) or a time (RFC 3339)", "when"),
        CloudTarget.TargetOption, CloudTarget.OrgOption, CloudTarget.CloudProjectOption, CloudTarget.DeploymentOption,
    };
    public override string[] Examples => new[] { "nebula logs", "nebula logs w2 -n 200", "nebula logs gateway --follow", "nebula logs --cloud w1", "nebula logs --cloud all --since 10m --follow" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        string role = args.Positional.Count > 0 ? args.Positional[0] : "orchestrator";
        int lines = args.GetInt("lines", 60);
        if (args.Has("cloud") && CloudTarget.IsCloud(args, project)) return CloudLogs(ctx, project, args, role, lines);
        if (args.Has("cloud"))
        {
            var hz = ctx.Config.Hetzner;
            if (hz == null || !hz.IsConfigured) throw new CliError("Hetzner is not configured", "nebula config hetzner");
            return new HetznerMesh(hz, project).Logs(role, lines);
        }

        string file = Path.Combine(project.HostLogsDir, role + ".log");
        if (!File.Exists(file))
        {
            var available = Directory.Exists(project.HostLogsDir)
                ? Directory.EnumerateFiles(project.HostLogsDir, "*.log").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToList()
                : new List<string?>();
            throw new CliError($"no log at {file}", available.Count > 0 ? "available: " + string.Join(", ", available) : "start the mesh first (`nebula start`)");
        }
        // Unity keeps its log open for writing, so read with sharing.
        long offset;
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs))
        {
            var all = new List<string>();
            string? l;
            while ((l = reader.ReadLine()) != null) all.Add(l);
            foreach (var line in all.TakeLast(lines)) Console.WriteLine(line);
            offset = fs.Length;
        }
        if (!args.Has("follow")) return 0;
        while (true)
        {
            Thread.Sleep(500);
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset) offset = 0;
            if (fs.Length == offset) continue;
            fs.Seek(offset, SeekOrigin.Begin);
            using var r = new StreamReader(fs);
            Console.Write(r.ReadToEnd());
            offset = fs.Length;
        }
    }

    private static int CloudLogs(Context ctx, NebulaProject project, ParsedArgs args, string role, int lines)
    {
        var api = CloudApi.Require(ctx);
        var t = CloudTarget.Resolve(ctx, api, project, args, create: false);
        string? instance = args.Get("instance");
        // w1 / gw2 name an instance; the role follows from the prefix.
        if (System.Text.RegularExpressions.Regex.IsMatch(role, "^w[0-9]+$")) { instance ??= role; role = "worker"; }
        else if (System.Text.RegularExpressions.Regex.IsMatch(role, "^gw[0-9]+$")) { instance ??= role; role = "gateway"; }
        if (role is not ("orchestrator" or "gateway" or "worker" or "all")) throw new CliError($"unknown role '{role}'", "orchestrator, gateway, worker, all, or an instance like w1 or gw1");
        DateTimeOffset? since = ParseSince(args.Get("since"));
        var page = api.Logs(t.Deployment.Id, role, instance, since, lines);
        string? lastAt = null;
        foreach (var l in page.Lines ?? new List<CloudApi.LogLine>()) { PrintLine(l); lastAt = l.At ?? lastAt; }
        if (!args.Has("follow")) return 0;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        api.StreamLogs(t.Deployment.Id, role, instance, l =>
        {
            // The stream may replay the tail the page already showed.
            if (lastAt != null && l.At != null && string.CompareOrdinal(l.At, lastAt) <= 0) return;
            PrintLine(l);
        }, cts.Token);
        return 0;
    }

    private static void PrintLine(CloudApi.LogLine l)
    {
        string time = l.At is { Length: >= 19 } t ? t.Substring(0, 19).Replace('T', ' ') : (l.At ?? "");
        string who = l.Instance is { Length: > 0 } i ? i : l.Role ?? "";
        Console.WriteLine($"{time} {who,-12} {(l.Level ?? "info").ToUpperInvariant(),-5} {l.Message}");
    }

    /// <summary>--since: 10m, 2h, 1d, 30s, or an RFC 3339 timestamp.</summary>
    public static DateTimeOffset? ParseSince(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(value.Trim(), @"^(\d+(?:\.\d+)?)\s*([smhd])$");
        if (m.Success)
        {
            double n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var span = m.Groups[2].Value switch { "s" => TimeSpan.FromSeconds(n), "m" => TimeSpan.FromMinutes(n), "h" => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n) };
            return DateTimeOffset.UtcNow - span;
        }
        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) return at;
        throw new CliError($"--since expects a duration like 10m or 2h, or an RFC 3339 time, not '{value}'");
    }
}
