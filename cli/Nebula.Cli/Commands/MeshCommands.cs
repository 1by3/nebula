using Nebula.Cli.Cloud;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class StartCommand : Command
{
    public override string Name => "start";
    public override string Summary => "Start SpacetimeDB, the orchestrator, gateway, and workers on this computer";
    public override string Usage => "[--build] [--workers N] [--npcs N] [--bots N] [--open-ui] [--reset-persistence]";
    public override string? Details => @"
Start SpacetimeDB when the configured address is unavailable. Publish a new copy of the control-plane module,
publish the persistence module next to it, then start the orchestrator from the latest build. The orchestrator
starts the gateway and workers. To join, enter Play mode in the Unity Editor or start the build with the client
role. Set defaults in the mesh section of nebula.json.

The control-plane database is republished with fresh data every time, because it only holds which processes are
running. The persistence database (mesh.persistenceDatabase, `nebula-persist`) keeps its saved entities across
restarts unless you pass --reset-persistence.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("build", false, "build first (nebula build)"),
        new OptionSpec("workers", true, "worker processes (default from nebula.json, 4)", "N"),
        new OptionSpec("npcs", true, "set the game-defined 'npcs' mesh setting at startup (default 0)", "N"),
        new OptionSpec("bots", true, "start headless clients with the bot flag; the game supplies their behavior (default 0)", "N"),
        new OptionSpec("open-ui", false, "open the Nebula Dashboard in the browser once it is up"),
        new OptionSpec("skip-publish", false, "do not re-publish the control-plane and persistence modules"),
        new OptionSpec("reset-persistence", false, "publish the persistence module with --delete-data, deleting every saved entity"),
    };
    public override string[] Examples => new[] { "nebula start --open-ui", "nebula start --build --workers 2", "nebula start --workers 4 --skip-publish", "nebula start --reset-persistence" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        if (args.Has("build"))
            UnityBuild.Build(ctx, project, new UnityBuild.Options(BuildTarget.Host, StopMesh: true));
        var mesh = project.File.Mesh;
        LocalMesh.Start(ctx, project, new LocalMesh.StartOptions(
            args.GetInt("workers", mesh.Workers), args.GetInt("npcs", mesh.Npcs), args.GetInt("bots", 0),
            args.Has("skip-publish"), args.Has("open-ui"), args.Has("reset-persistence")));
        return 0;
    }
}

public sealed class StopCommand : Command
{
    public override string Name => "stop";
    public override string Summary => "Stop the local mesh (every process of the build, and SpacetimeDB if `nebula start` started it)";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("spacetime", false, "also stop SpacetimeDB even if it was already running before `nebula start`"),
    };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        LocalMesh.Stop(project, args.Has("spacetime"));
        return 0;
    }
}

public sealed class StatusCommand : Command
{
    public override string Name => "status";
    public override string Summary => "Show workers, containers, entity counts, persistence, and recent events";
    public override string Usage => "[--cloud]";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("cloud", false, "the deployed mesh (Hetzner servers + its Nebula Dashboard) instead of the local one"),
        new OptionSpec("json", false, "print the raw /api/state JSON"),
    };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        string url, gateway;
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
    public override string Usage => "[role] [--lines N] [--follow] [--cloud]";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("lines", true, "lines to show (default 60)", "N", "n"),
        new OptionSpec("follow", false, "keep printing as the log grows (local only)", null, "f"),
        new OptionSpec("cloud", false, "read the log from the deployed mesh over ssh"),
    };
    public override string[] Examples => new[] { "nebula logs", "nebula logs w2 -n 200", "nebula logs gateway --follow", "nebula logs --cloud w1" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        string role = args.Positional.Count > 0 ? args.Positional[0] : "orchestrator";
        int lines = args.GetInt("lines", 60);
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
}
