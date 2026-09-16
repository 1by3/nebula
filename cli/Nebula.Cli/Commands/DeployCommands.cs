using Nebula.Cli.Cloud;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class DeployCommand : Command
{
    public override string Name => "deploy";
    public override string Summary => "Build and deploy the mesh to Nebula Cloud or your own Hetzner project";
    public override string Usage => "[--target hetzner|cloud] [--workers N] [--min N] [--max N] [--npcs N] [--open-ui] [--reset-persistence]";
    public override string? Details => @"
Two targets, chosen with --target or deploy.target in nebula.json.

--target cloud deploys to Nebula Cloud (log in first with `nebula cloud login`). The first run picks or creates the
organization, project and deployment (interactively, or from --org, --cloud-project, --deployment; --yes takes the
defaults) and records them in the cloud section of nebula.json. Every run then builds the Linux dedicated server,
uploads Builds/nebula-linux.tar.gz as a content-addressed artifact, creates an immutable release from it with the
service manifest, the Nebula and protocol versions and the git commit, and rolls the release out, printing the
operation's steps until it finishes. Ctrl-C leaves the rollout running; the next `nebula deploy` reattaches to it.
--release rolls out an existing release without building. --min/--max change the deployment's worker band first.
A rollout is refused when the wire protocol version changed, because connected players would be disconnected;
--allow-protocol-change accepts that.

--target hetzner deploys to your own Hetzner Cloud project (`nebula config hetzner` first):

Build the Linux dedicated server at Builds/nebula-linux.tar.gz. Create any missing SSH key, private network,
firewall, and orchestrator VM. Upload the build and restart the orchestrator service. The orchestrator hosts the
control plane, creates one VM per worker, and keeps the control plane and every saved entity in its database.

--workers N alone runs a fixed mesh of N VMs. --min/--max open an autoscaling band: the orchestrator starts at
--min, adds VMs while workers are busy and retires them into an idle pool when they are not (see
deploy.idlePoolSeconds in nebula.json; the default keeps a retired VM for the hour Hetzner bills). --min 0
allows scaling to zero; the first player then waits for a VM to boot.

The database is a SQLite file on the orchestrator VM (/opt/nebula/data/nebula.db) unless you point the mesh at a
PostgreSQL server with `nebula config database`, deploy.database in nebula.json, or NEBULA_DATABASE_URL. Saved
entities stay across deployments unless you pass --reset-persistence. Every deployment generates a new mesh token
that workers and the gateway present to the orchestrator.

Run this command again after you change game code. A deployment restarts the orchestrator and recreates its
workers.

A deploy target must be configured (`nebula cloud login` or `nebula config hetzner`).
";
    public override OptionSpec[] Options => new[]
    {
        CloudTarget.TargetOption,
        new OptionSpec("workers", true, "worker VMs to start with; on its own it fixes the count (min = max = N). Default from nebula.json, 4", "N"),
        WorkerBand.MinOption, WorkerBand.MaxOption,
        new OptionSpec("release", true, "cloud: roll out this existing release instead of building a new one", "rel_id"),
        new OptionSpec("label", true, "cloud: a label for the new release, e.g. v12", "text"),
        new OptionSpec("allow-protocol-change", false, "cloud: roll out even when the wire protocol version changed (players are disconnected)"),
        new OptionSpec("region", true, "cloud: region of a deployment created by this run", "id"),
        new OptionSpec("worker-size", true, "cloud: worker machine size (small, medium, large)", "size"),
        CloudTarget.OrgOption, CloudTarget.CloudProjectOption, CloudTarget.DeploymentOption,
        new OptionSpec("npcs", true, "set the game-defined 'npcs' mesh setting at startup (default from nebula.json)", "N"),
        new OptionSpec("open-ui", false, "open the Nebula Dashboard once it answers"),
        new OptionSpec("skip-build", false, "use the existing Builds/nebula-linux.tar.gz"),
        new OptionSpec("skip-publish", false, "do not publish the control-plane and persistence modules"),
        new OptionSpec("skip-upload", false, "only rewrite the service and restart (no build, no upload)"),
        new OptionSpec("reset-persistence", false, "delete every saved entity when the orchestrator starts"),
    };
    public override string[] Examples => new[] { "nebula deploy --target cloud", "nebula deploy --min 1 --max 8 --open-ui", "nebula deploy --release rel_01h", "nebula deploy --target hetzner --workers 4", "nebula deploy --skip-build" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        string target = CloudTarget.TargetOf(args, project);
        if (target == "cloud")
        {
            return CloudDeploy.Run(ctx, project, args, new CloudDeploy.Options(
                args.Get("release"), args.Has("skip-build") || args.Has("skip-upload"), args.Has("allow-protocol-change"), args.Get("label"),
                null, null, args.Get("worker-size"), args.Has("open-ui")));
        }
        if (target != "hetzner") throw new CliError($"deploy target '{target}' is not supported", "hetzner or cloud");

        // --- configuration gate -------------------------------------------------------------------------
        var hz = ctx.Config.Hetzner;
        if (hz == null || !hz.IsConfigured) throw new CliError("Hetzner is not configured", "nebula config hetzner", 2);
        string database = project.DeployDatabase(ctx.Config);
        DatabaseUrl parsed;
        try { parsed = DatabaseUrl.Parse(database, NebulaProject.DefaultDeployDatabase); }
        catch (ArgumentException e) { throw new CliError(e.Message, "nebula config database"); }
        if (parsed.Scheme is "file" or "memory") throw new CliError($"a deployed orchestrator needs sqlite: or postgres:, not '{parsed.Scheme}:'", "nebula config database");
        var band = WorkerBand.Resolve(args, project.File.Deploy.Workers, project.File.Deploy.MinWorkers, project.File.Deploy.MaxWorkers);
        int npcs = args.GetInt("npcs", project.File.Deploy.Npcs);
        bool skipUpload = args.Has("skip-upload");

        Ui.Title($"deploying {Path.GetFileName(project.Root)} to Hetzner: {band.Describe()}, database {parsed.Display}");

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
        string url = mesh.Deploy(orch, project.LinuxTarball, new HetznerMesh.DeployOptions(band.Start, band.Min, band.Max, project.File.Deploy.IdlePoolSeconds, npcs, database, token, args.Has("reset-persistence"), skipUpload, ctx.Verbose));

        string ip = HetznerMesh.PublicIp(orch);
        Ui.Blank();
        Ui.Ok("deployed");
        Ui.Info($"dashboard  {url}");
        Ui.Info($"gateway    {ip}:{mesh.GatewayPort}   (client: {project.File.Executable} -nebula-role client -nebula-gateway {ip}:{mesh.GatewayPort})");
        Ui.Info($"web        https://{ip}:{mesh.GatewayPort}/   (HTTPS a few seconds after start; the web build is served there when the tarball was packed after `nebula build --web`)");
        Ui.Info("status     nebula status --cloud     logs: nebula logs --cloud orchestrator|gateway|w1");
        Ui.Info("tear down  nebula destroy");
        if (args.Has("open-ui")) Platform.OpenBrowser(url);
        return 0;
    }
}

public sealed class DestroyCommand : Command
{
    public override string Name => "destroy";
    public override string Summary => "Delete the deployed mesh";
    public override string Usage => "[--target hetzner|cloud] [--all]";
    public override string? Details => @"
On Nebula Cloud: delete the deployment named in nebula.json with every machine, its database and every saved
entity. The command asks you to type the deployment's name unless you pass --yes, then follows the destroy
operation. The organization, project and releases stay.

On Hetzner: delete every server labelled with the mesh (orchestrator and workers). A SQLite database on the
orchestrator VM goes with it, and with it every saved entity; a PostgreSQL database is left untouched.
";
    public override OptionSpec[] Options => new[]
    {
        CloudTarget.TargetOption,
        new OptionSpec("all", false, "hetzner: also delete the firewalls, private network, and SSH key"),
        CloudTarget.OrgOption, CloudTarget.CloudProjectOption, CloudTarget.DeploymentOption,
    };
    public override string[] Examples => new[] { "nebula destroy", "nebula destroy --yes", "nebula destroy --target hetzner --all" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        if (CloudTarget.IsCloud(args, project)) return DestroyCloud(ctx, project, args);
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

    private static int DestroyCloud(Context ctx, NebulaProject project, ParsedArgs args)
    {
        var api = CloudApi.Require(ctx);
        var t = CloudTarget.Resolve(ctx, api, project, args, create: false);
        var dep = t.Deployment;
        if (dep.State == "destroyed") { Ui.Info($"{t.Describe} is already destroyed"); return 0; }
        Ui.Warn($"this deletes deployment {t.Describe} ({dep.Id}, {dep.State}, {dep.Region}): every machine, its database and every saved entity");
        if (!ctx.Yes)
        {
            string typed = Ui.Ask($"type the deployment name ({dep.Name}) to confirm");
            if (typed != dep.Name) { Ui.Fail("the name did not match; nothing was deleted"); return 1; }
        }
        var running = OperationFollower.FindRunning(api, dep.Id);
        CloudApi.Operation op;
        if (running is { Kind: "destroy" })
        {
            Ui.Warn($"destroy {running.Id} is already {running.State}; following it");
            op = running;
        }
        else
        {
            try { op = api.DeleteDeployment(dep.Id); }
            catch (CloudApiError e) when (CloudApi.RunningOperationOf(e) is { } other) { Ui.Warn($"operation {other.Id} is already running; following it"); op = api.GetOperation(other.Id); }
        }
        OperationFollower.Follow(api, op);
        Ui.Ok($"{t.Describe} destroyed");
        Ui.Info("nebula.json still names the deployment; the next `nebula deploy --target cloud` creates it again");
        return 0;
    }
}
