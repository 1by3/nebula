using Nebula.Cli.Cloud;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class ConfigCommand : Command
{
    public override string Name => "config";
    public override string Summary => "Configure a deploy target, Spacetime Maincloud, or the Unity editor path";
    public override string Usage => "<hetzner|spacetime|unity|source|show>";
    public override string? Details => @"
  hetzner    API token, project, region and machine types for `nebula deploy --target hetzner`
  spacetime  log in to SpacetimeDB Maincloud (or another server) and pick the control-plane database name
  unity      the Unity editor to build with, when it is not where Unity Hub puts it
  source     the Nebula checkout `nebula init --embed` copies the package from (--path; 'none' to clone on demand)
  show       print the current configuration (secrets masked)

Settings live in ~/.nebula-cli/config.json. Inside a project, the database name and the mesh defaults are also
written to nebula.json so they travel with the project.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("token", true, "hetzner: API token (else prompted; HCLOUD_TOKEN in the environment always wins)", "token"),
        new OptionSpec("location", true, "hetzner: region (ash, hil, sin, nbg1, fsn1, hel1)", "name"),
        new OptionSpec("server", true, "spacetime: server nickname or URL (default maincloud)", "name"),
        new OptionSpec("database", true, "spacetime: control-plane database name", "name"),
        new OptionSpec("editor", true, "unity: path to the editor executable or the Hub's Editor folder", "path"),
        new OptionSpec("path", true, "source: a Nebula checkout for `nebula init` to copy from ('none' to clone on demand)", "path"),
    };
    public override string[] Examples => new[] { "nebula config hetzner", "nebula config spacetime", "nebula config unity --editor \"C:\\Program Files\\Unity\\Hub\\Editor\"", "nebula config show" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        string what = args.Positional.Count > 0 ? args.Positional[0] : "show";
        return what switch
        {
            "hetzner" => Hetzner(ctx, args),
            "spacetime" => Spacetime(ctx, args),
            "unity" => Unity(ctx, args),
            "source" => Source(ctx, args),
            "show" => Show(ctx),
            _ => throw new CliError($"unknown config target '{what}'", "nebula config <hetzner|spacetime|unity|source|show>"),
        };
    }

    private static int Show(Context ctx)
    {
        var c = ctx.Config;
        Ui.Title(Platform.ConfigPath);
        Ui.Info($"sdkSource   {c.SdkSource ?? "(clone on demand)"}");
        Ui.Info($"unity       {c.Unity.Editor ?? "(Unity Hub default)"}");
        Ui.Info($"setup       {c.SetupCompletedAt ?? "not run"}");
        if (c.Hetzner != null)
        {
            var t = c.Hetzner.ResolveToken();
            Ui.Info($"hetzner     project={c.Hetzner.Project} location={c.Hetzner.Location} worker={c.Hetzner.WorkerType} orchestrator={c.Hetzner.OrchestratorType} token={(t == null ? "none" : t.Length > 8 ? "..." + t[^4..] : "set")}{(Environment.GetEnvironmentVariable("HCLOUD_TOKEN") != null ? " (from HCLOUD_TOKEN)" : "")}");
        }
        else Ui.Info("hetzner     not configured");
        if (c.Spacetime != null) Ui.Info($"spacetime   server={c.Spacetime.Server} database={c.Spacetime.Database ?? "(per project)"}");
        else Ui.Info("spacetime   not configured");
        var p = NebulaProject.Find(ctx.ProjectOverride ?? Directory.GetCurrentDirectory());
        if (p != null)
        {
            Ui.Blank();
            Ui.Title(p.FilePath);
            Ui.Info($"mesh        workers={p.File.Mesh.Workers} npcs={p.File.Mesh.Npcs} dashboard={p.File.Mesh.DashboardPort} gateway={p.File.Mesh.GatewayPort} database={p.File.Mesh.Database}");
            Ui.Info($"deploy      target={p.File.Deploy.Target} mesh={p.File.Deploy.MeshName} database={p.DeployDatabase(c)} workers={p.File.Deploy.Workers}");
        }
        return 0;
    }

    private static int Hetzner(Context ctx, ParsedArgs args)
    {
        var hz = ctx.Config.Hetzner ?? new CliConfig.HetznerSettings();
        Ui.Title("Hetzner Cloud");
        Ui.Info("Nebula runs the orchestrator, dashboard and gateway on one VM and creates one VM per worker in a");
        Ui.Info("Hetzner Cloud project. You need a Read & Write API token: console.hetzner.cloud > project > Security > API tokens.");
        Ui.Blank();

        string? token = args.Get("token");
        if (token == null)
        {
            string current = hz.Token != null ? "keep the stored token" : Environment.GetEnvironmentVariable("HCLOUD_TOKEN") != null ? "use HCLOUD_TOKEN from the environment" : "";
            token = Ui.AskSecret("API token" + (current.Length > 0 ? $" (enter to {current})" : ""));
            if (token.Length == 0) token = hz.ResolveToken() ?? throw new CliError("a token is required");
        }
        Ui.Info("checking the token...");
        var api = new HcloudClient(token);
        var problem = api.Check();
        if (problem != null) throw new CliError(problem);
        Ui.Ok("token accepted");
        hz.Token = token;

        hz.Project = Ui.Ask("Hetzner project name (a label for your own reference; the token already selects the project)", hz.Project ?? "nebula");

        string? location = args.Get("location");
        if (location == null)
        {
            var locations = api.Locations();
            var known = locations.Where(l => l.zone.Length > 0).ToList();
            var labels = known.Select(l => $"{l.name,-5} {l.city} ({l.zone})").ToList();
            int def = Math.Max(0, known.FindIndex(l => l.name == hz.Location));
            location = known[Ui.Choose("deploy region:", labels, def)].name;
        }
        HetznerMesh.ZoneFor(location);
        hz.Location = location;
        hz.WorkerType = Ui.Ask("worker VM type (cpx21 = 3 shared vCPU/4 GB; ccx13 = 2 dedicated vCPU)", hz.WorkerType);
        hz.OrchestratorType = Ui.Ask("orchestrator VM type", hz.OrchestratorType);
        hz.ConfiguredAt = DateTime.UtcNow.ToString("o");
        ctx.Config.Hetzner = hz;
        ctx.SaveConfig();
        Ui.Ok($"saved to {Platform.ConfigPath}");

        var p = NebulaProject.Find(ctx.ProjectOverride ?? Directory.GetCurrentDirectory());
        if (p != null)
        {
            p.File.Deploy.Target = "hetzner";
            p.Save();
            Ui.Ok($"{p.FilePath}: deploy target = hetzner");
        }
        Ui.Info(ctx.Config.Spacetime?.IsConfigured == true ? "next: nebula deploy" : "next: nebula config spacetime, then nebula deploy");
        return 0;
    }

    private static int Spacetime(Context ctx, ParsedArgs args)
    {
        var st = ctx.Config.Spacetime ?? new CliConfig.SpacetimeSettings();
        Ui.Title("SpacetimeDB for deployment");
        Ui.Info("A deployed mesh keeps its control plane (workers, leases, gateways) in a SpacetimeDB database that the");
        Ui.Info("cloud VMs can reach. Maincloud (maincloud.spacetimedb.com) is the hosted option and needs a login.");
        Ui.Blank();
        SpacetimeCli.Require();

        string? server = args.Get("server");
        if (server == null)
        {
            int choice = Ui.Choose("server:", new[] { "maincloud (hosted by Clockwork Labs)", "another server nickname or URL known to the spacetime CLI" }, st.Server == "maincloud" ? 0 : 1);
            server = choice == 0 ? "maincloud" : Ui.Ask("server nickname or URL", st.Server == "maincloud" ? null : st.Server);
        }
        st.Server = server;

        if (server == "maincloud")
        {
            if (SpacetimeCli.IsLoggedIn(out var identity))
            {
                Ui.Ok($"logged in ({identity})");
                if (Ui.Confirm("log in again as a different user?", false, false)) SpacetimeCli.Login();
            }
            else
            {
                Ui.Info("opening the SpacetimeDB login in your browser (`spacetime login`)...");
                SpacetimeCli.Login();
                if (!SpacetimeCli.IsLoggedIn(out identity)) throw new CliError("still not logged in after `spacetime login`");
                Ui.Ok($"logged in ({identity})");
            }
        }

        var p = NebulaProject.Find(ctx.ProjectOverride ?? Directory.GetCurrentDirectory());
        string fallback = args.Get("database") ?? p?.DeployDatabase(ctx.Config) ?? st.Database ?? "nebula-game";
        string database = args.Get("database") ?? Ui.Ask("control-plane database name (unique on the server)", fallback);
        if (p != null)
        {
            p.File.Deploy.Database = database;
            p.Save();
            Ui.Ok($"{p.FilePath}: deploy database = {database}");
        }
        else st.Database = database;
        st.ConfiguredAt = DateTime.UtcNow.ToString("o");
        ctx.Config.Spacetime = st;
        ctx.SaveConfig();
        Ui.Ok($"saved to {Platform.ConfigPath}");
        Ui.Info(ctx.Config.Hetzner?.IsConfigured == true ? "next: nebula deploy" : "next: nebula config hetzner, then nebula deploy");
        return 0;
    }

    /// <summary>Where `nebula init --embed` copies the package from (set by the from-source installer).</summary>
    private static int Source(Context ctx, ParsedArgs args)
    {
        string? path = args.Get("path");
        if (path == null || path == "none" || path == "")
        {
            ctx.Config.SdkSource = null;
            ctx.SaveConfig();
            Ui.Ok("sdkSource cleared; `nebula init --embed` clones the repository on demand");
            return 0;
        }
        path = Path.GetFullPath(path);
        if (!NebulaProject.IsNebulaCheckout(path)) throw new CliError($"{path} has no {Platform.PackagePathInRepo}/Runtime", "point it at a checkout of the Nebula repository");
        ctx.Config.SdkSource = path;
        ctx.SaveConfig();
        Ui.Ok($"sdkSource = {path} (`nebula init --embed` copies the package from here)");
        return 0;
    }

    private static int Unity(Context ctx, ParsedArgs args)
    {
        string path = args.Get("editor") ?? Ui.Ask("Unity editor executable, install folder, or Hub 'Editor' folder", ctx.Config.Unity.Editor);
        path = Path.GetFullPath(path);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new CliError($"{path} does not exist");
        ctx.Config.Unity.Editor = path;
        ctx.SaveConfig();
        Ui.Ok($"unity editor = {path}");
        var p = NebulaProject.Find(ctx.ProjectOverride ?? Directory.GetCurrentDirectory());
        if (p != null)
        {
            try { Ui.Ok($"resolves to {UnityLocator.Find(ctx.Config, p.UnityVersion)} for this project (Unity {p.UnityVersion})"); }
            catch (CliError e) { Ui.Warn(e.Message); }
        }
        return 0;
    }
}
