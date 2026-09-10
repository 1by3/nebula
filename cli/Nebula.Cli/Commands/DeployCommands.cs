using Nebula.Cli.Cloud;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class DeployCommand : Command
{
    public override string Name => "deploy";
    public override string Summary => "Build, publish the control plane and run the mesh on the configured deploy target";
    public override string Usage => "[--target hetzner] [--workers N] [--npcs N] [--open-ui]";
    public override string? Details => @"
Steps: build the Linux dedicated server (Builds/nebula-linux.tar.gz), publish the control-plane module to the
configured SpacetimeDB server, create the cloud resources that are missing (ssh key, private network,
firewalls, orchestrator VM), upload the build and (re)start the orchestrator service, which creates one VM per
worker. Re-run after every code change; a redeploy restarts the orchestrator, which recreates its workers.

Both a deploy target (`nebula config hetzner`) and Spacetime (`nebula config spacetime`) must be configured.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("target", true, "deploy target (default from nebula.json: hetzner)", "name"),
        new OptionSpec("workers", true, "worker VMs the orchestrator keeps running (default from nebula.json, 4)", "N"),
        new OptionSpec("npcs", true, "worker-simulated NPCs at start (default from nebula.json)", "N"),
        new OptionSpec("open-ui", false, "open the Nebula Dashboard once it answers"),
        new OptionSpec("skip-build", false, "use the existing Builds/nebula-linux.tar.gz"),
        new OptionSpec("skip-publish", false, "do not publish the control-plane module"),
        new OptionSpec("skip-upload", false, "only rewrite the service and restart (no build, no upload)"),
        new OptionSpec("reset-control-plane", false, "publish the module with --delete-data (wipes the control-plane tables)"),
    };
    public override string[] Examples => new[] { "nebula deploy", "nebula deploy --workers 4 --npcs 128 --open-ui", "nebula deploy --skip-build" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        string target = args.Get("target") ?? project.File.Deploy.Target;
        if (target != "hetzner") throw new CliError($"deploy target '{target}' is not supported yet", "hetzner is the only target in this version");

        // --- configuration gate -------------------------------------------------------------------------
        var missing = new List<string>();
        var hz = ctx.Config.Hetzner;
        if (hz == null || !hz.IsConfigured) missing.Add("Hetzner is not configured: nebula config hetzner");
        var st = ctx.Config.Spacetime;
        if (st == null || !st.IsConfigured) missing.Add("Spacetime is not configured: nebula config spacetime");
        if (missing.Count > 0)
        {
            foreach (var m in missing) Ui.Fail(m);
            throw new CliError("deploy needs both a deploy target and Spacetime configured", null, 2);
        }
        string database = project.DeployDatabase(ctx.Config);
        string spacetimeUri = st!.Server == "maincloud" ? "https://maincloud.spacetimedb.com" : st.Server;
        int workers = args.GetInt("workers", project.File.Deploy.Workers);
        int npcs = args.GetInt("npcs", project.File.Deploy.Npcs);
        bool skipUpload = args.Has("skip-upload");

        Ui.Title($"deploying {Path.GetFileName(project.Root)} to Hetzner: {workers} worker(s), control plane {st.Server}/{database}");

        // --- build --------------------------------------------------------------------------------------
        if (!skipUpload && !args.Has("skip-build"))
            UnityBuild.Build(ctx, project, new UnityBuild.Options(BuildTarget.Linux));
        else if (!skipUpload && !File.Exists(project.LinuxTarball))
            throw new CliError($"no {project.LinuxTarball}", "drop --skip-build, or run `nebula build --linux`");

        // --- control plane -------------------------------------------------------------------------------
        if (!args.Has("skip-publish"))
        {
            Ui.Step($"publishing the control-plane module to {st.Server} as '{database}'");
            if (st.Server == "maincloud" && !SpacetimeCli.IsLoggedIn(out _))
                throw new CliError("not logged in to SpacetimeDB Maincloud (the login token may have expired)", "nebula config spacetime");
            SpacetimeCli.Publish(project.ModuleDir, st.Server, database, args.Has("reset-control-plane"));
            Ui.Ok("module published");
        }

        // --- cloud -----------------------------------------------------------------------------------------
        var mesh = new HetznerMesh(hz!, project);
        var orch = mesh.Provision();
        string url = mesh.Deploy(orch, project.LinuxTarball, new HetznerMesh.DeployOptions(workers, npcs, spacetimeUri, database, skipUpload, ctx.Verbose));

        string ip = HetznerMesh.PublicIp(orch);
        Ui.Blank();
        Ui.Ok("deployed");
        Ui.Info($"dashboard  {url}");
        Ui.Info($"gateway    {ip}:{mesh.GatewayPort}   (client: {project.File.Executable} -nebula-role client -nebula-gateway {ip}:{mesh.GatewayPort})");
        Ui.Info("status     nebula status --cloud     logs: nebula logs --cloud orchestrator|gateway|w1");
        Ui.Info("tear down  nebula destroy");
        if (args.Has("open-ui")) Platform.OpenBrowser(url);
        return 0;
    }
}

public sealed class DestroyCommand : Command
{
    public override string Name => "destroy";
    public override string Summary => "Delete every cloud server of the mesh so nothing keeps billing";
    public override string Usage => "[--all]";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("all", false, "also delete the firewalls, private network and ssh key (they cost nothing to keep)"),
    };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        var hz = ctx.Config.Hetzner;
        if (hz == null || !hz.IsConfigured) throw new CliError("Hetzner is not configured", "nebula config hetzner");
        var mesh = new HetznerMesh(hz, project);
        var servers = mesh.Servers();
        if (servers.Count > 0) mesh.PrintServers(servers);
        if (!Ui.Confirm($"delete {servers.Count} server(s) of mesh '{mesh.MeshName}'{(args.Has("all") ? " and its network, firewalls and ssh key" : "")}?", false, ctx.Yes))
            return 1;
        mesh.Destroy(args.Has("all"));
        Ui.Ok("done");
        return 0;
    }
}
