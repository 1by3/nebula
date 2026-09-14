using Nebula.Cli.Cloud;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class DeployCommand : Command
{
    public override string Name => "deploy";
    public override string Summary => "Build and deploy the mesh to the configured cloud target";
    public override string Usage => "[--target hetzner] [--workers N] [--npcs N] [--open-ui] [--reset-persistence]";
    public override string? Details => @"
Build the Linux dedicated server at Builds/nebula-linux.tar.gz. Create any missing SSH key, private network,
firewall, and orchestrator VM. Upload the build and restart the orchestrator service. The orchestrator hosts the
control plane, creates one VM per worker, and keeps the control plane and every saved entity in its database.

The database is a SQLite file on the orchestrator VM (/opt/nebula/data/nebula.db) unless you point the mesh at a
PostgreSQL server with `nebula config database`, deploy.database in nebula.json, or NEBULA_DATABASE_URL. Saved
entities stay across deployments unless you pass --reset-persistence. Every deployment generates a new mesh token
that workers and the gateway present to the orchestrator.

Run this command again after you change game code. A deployment restarts the orchestrator and recreates its
workers.

A deploy target must be configured (`nebula config hetzner`).
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("target", true, "deploy target (default from nebula.json: hetzner)", "name"),
        new OptionSpec("workers", true, "worker VMs the orchestrator keeps running (default from nebula.json, 4)", "N"),
        new OptionSpec("npcs", true, "set the game-defined 'npcs' mesh setting at startup (default from nebula.json)", "N"),
        new OptionSpec("open-ui", false, "open the Nebula Dashboard once it answers"),
        new OptionSpec("skip-build", false, "use the existing Builds/nebula-linux.tar.gz"),
        new OptionSpec("skip-publish", false, "do not publish the control-plane and persistence modules"),
        new OptionSpec("skip-upload", false, "only rewrite the service and restart (no build, no upload)"),
        new OptionSpec("reset-persistence", false, "delete every saved entity when the orchestrator starts"),
    };
    public override string[] Examples => new[] { "nebula deploy", "nebula deploy --workers 4 --open-ui", "nebula deploy --skip-build" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        string target = args.Get("target") ?? project.File.Deploy.Target;
        if (target != "hetzner") throw new CliError($"deploy target '{target}' is not supported yet", "hetzner is the only target in this version");

        // --- configuration gate -------------------------------------------------------------------------
        var hz = ctx.Config.Hetzner;
        if (hz == null || !hz.IsConfigured) throw new CliError("Hetzner is not configured", "nebula config hetzner", 2);
        string database = project.DeployDatabase(ctx.Config);
        DatabaseUrl parsed;
        try { parsed = DatabaseUrl.Parse(database, NebulaProject.DefaultDeployDatabase); }
        catch (ArgumentException e) { throw new CliError(e.Message, "nebula config database"); }
        if (parsed.Scheme is "file" or "memory") throw new CliError($"a deployed orchestrator needs sqlite: or postgres:, not '{parsed.Scheme}:'", "nebula config database");
        int workers = args.GetInt("workers", project.File.Deploy.Workers);
        int npcs = args.GetInt("npcs", project.File.Deploy.Npcs);
        bool skipUpload = args.Has("skip-upload");

        Ui.Title($"deploying {Path.GetFileName(project.Root)} to Hetzner: {workers} worker(s), database {parsed.Display}");

        // --- build --------------------------------------------------------------------------------------
        if (!skipUpload && !args.Has("skip-build"))
            UnityBuild.Build(ctx, project, new UnityBuild.Options(BuildTarget.Linux));
        else if (!skipUpload && !File.Exists(project.LinuxTarball))
            throw new CliError($"no {project.LinuxTarball}", "drop --skip-build, or run `nebula build --linux`");

        // --- cloud -----------------------------------------------------------------------------------------
        var mesh = new HetznerMesh(hz, project);
        var orch = mesh.Provision();
        // A fresh shared secret per deployment: it never leaves the VMs and the CLI has no reason to keep it.
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string url = mesh.Deploy(orch, project.LinuxTarball, new HetznerMesh.DeployOptions(workers, npcs, database, token, args.Has("reset-persistence"), skipUpload, ctx.Verbose));

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
    public override string Summary => "Delete the mesh's cloud servers";
    public override string Usage => "[--all]";
    public override string? Details => @"
Delete every server labelled with the mesh (orchestrator and workers). A SQLite database on the orchestrator VM
goes with it, and with it every saved entity; a PostgreSQL database is left untouched.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("all", false, "also delete the firewalls, private network, and SSH key"),
    };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        var hz = ctx.Config.Hetzner;
        if (hz == null || !hz.IsConfigured) throw new CliError("Hetzner is not configured", "nebula config hetzner");
        string database = project.DeployDatabase(ctx.Config);

        var mesh = new HetznerMesh(hz, project);
        var servers = mesh.Servers();
        if (servers.Count > 0) mesh.PrintServers(servers);
        var what = new List<string> { $"{servers.Count} server(s) of mesh '{mesh.MeshName}'" };
        if (args.Has("all")) what.Add("its network, firewalls and ssh key");
        if (database.StartsWith("sqlite:", StringComparison.OrdinalIgnoreCase)) what.Add("the SQLite database on the orchestrator VM (every saved entity)");
        if (!Ui.Confirm($"delete {string.Join(", ", what)}?", false, ctx.Yes))
            return 1;
        mesh.Destroy(args.Has("all"));
        if (!database.StartsWith("sqlite:", StringComparison.OrdinalIgnoreCase)) Ui.Info($"the database at {DatabaseUrl.Parse(database, NebulaProject.DefaultDeployDatabase).Display} was left in place");
        Ui.Ok("done");
        return 0;
    }
}
