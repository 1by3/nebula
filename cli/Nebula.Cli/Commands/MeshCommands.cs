using Nebula.Cli.Cloud;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class StartCommand : Command
{
    public override string Name => "start";
    public override string Summary => "Run the mesh locally: SpacetimeDB, the control-plane module, orchestrator, gateway and workers";
    public override string Usage => "[--build] [--workers N] [--npcs N] [--bots N] [--open-ui]";
    public override string? Details => @"
Starts a local SpacetimeDB if none answers on the configured address, publishes the control-plane module with
fresh data, then launches the orchestrator from the last build; the orchestrator starts the gateway and the
workers. Join from the Editor (Play) or with the client build. Defaults come from nebula.json (mesh section).
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("build", false, "build first (nebula build)"),
        new OptionSpec("workers", true, "worker processes (default from nebula.json, 4)", "N"),
        new OptionSpec("npcs", true, "worker-simulated NPCs spread across the mesh (default 0)", "N"),
        new OptionSpec("bots", true, "headless bot clients that roam and shoot (default 0; each is a full client process)", "N"),
        new OptionSpec("open-ui", false, "open the mesh dashboard in the browser once it is up"),
        new OptionSpec("skip-publish", false, "do not re-publish the control-plane module"),
    };
    public override string[] Examples => new[] { "nebula start --open-ui", "nebula start --build --workers 2", "nebula start --workers 4 --npcs 128" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        if (args.Has("build"))
            UnityBuild.Build(ctx, project, new UnityBuild.Options(BuildTarget.Host, StopMesh: true));
        var mesh = project.File.Mesh;
        LocalMesh.Start(ctx, project, new LocalMesh.StartOptions(
            args.GetInt("workers", mesh.Workers), args.GetInt("npcs", mesh.Npcs), args.GetInt("bots", 0),
            args.Has("skip-publish"), args.Has("open-ui")));
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
    public override string Summary => "Show the mesh: workers, containers, players, bots, NPCs and recent events";
    public override string Usage => "[--cloud]";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("cloud", false, "the deployed mesh (Hetzner servers + the remote dashboard) instead of the local one"),
        new OptionSpec("json", false, "print the raw /api/state JSON"),
    };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        string url;
        if (args.Has("cloud"))
        {
            var hz = ctx.Config.Hetzner;
            if (hz == null || !hz.IsConfigured) throw new CliError("Hetzner is not configured", "nebula config hetzner");
            var mesh = new HetznerMesh(hz, project);
            var servers = mesh.Servers();
            mesh.PrintServers(servers);
            var orch = servers.FirstOrDefault(s => s["name"]?.ToString() == mesh.OrchestratorName);
            if (orch == null) { Ui.Warn("no orchestrator server; run `nebula deploy`"); return 1; }
            url = $"http://{HetznerMesh.PublicIp(orch)}:{mesh.DashboardPort}/";
        }
        else
        {
            url = $"http://localhost:{project.File.Mesh.DashboardPort}/";
        }
        Ui.Blank();
        var state = LocalMesh.FetchState(url);
        if (state == null)
        {
            Ui.Warn($"the dashboard at {url} is not answering" + (args.Has("cloud") ? "" : "; is the mesh running? (`nebula start`)"));
            return 1;
        }
        if (args.Has("json")) { Console.WriteLine(state.ToJsonString(CliConfig.Json)); return 0; }
        Ui.Info($"dashboard {url}");
        LocalMesh.PrintState(state);
        return 0;
    }
}

public sealed class LogsCommand : Command
{
    public override string Name => "logs";
    public override string Summary => "Show a role's log: orchestrator, gateway, w1..wN, bot1..";
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
