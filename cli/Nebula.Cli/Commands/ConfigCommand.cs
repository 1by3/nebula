using Nebula.Cli.Cloud;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class ConfigCommand : Command
{
    public override string Name => "config";
    public override string Summary => "Configure a deploy target, the deployed mesh's database, or the Unity editor path";
    public override string Usage => "<hetzner|database|unity|source|show>";
    public override string? Details => @"
  hetzner    API token, project, region and machine types for `nebula deploy --target hetzner`
  database   where a deployed orchestrator keeps the control plane and saved entities: a PostgreSQL URL, or SQLite on the VM
  unity      the Unity editor to build with, when it is not where Unity Hub puts it
  source     the Nebula checkout `nebula init --embed` copies the package from (--path; 'none' to clone on demand)
  show       print the current configuration (secrets masked)

Settings live in ~/.nebula-cli/config.json (private to you: it holds tokens and the database password). A database
URL in nebula.json (deploy.database) or NEBULA_DATABASE_URL in the environment takes precedence over it.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("token", true, "hetzner: API token (else prompted; HCLOUD_TOKEN in the environment always wins)", "token"),
        new OptionSpec("location", true, "hetzner: region (ash, hil, sin, nbg1, fsn1, hel1)", "name"),
        new OptionSpec("url", true, "database: postgres://user:password@host/db, sqlite:<file on the VM>, or 'default' for SQLite on the orchestrator VM", "url"),
        new OptionSpec("editor", true, "unity: path to the editor executable or the Hub's Editor folder", "path"),
        new OptionSpec("path", true, "source: a Nebula checkout for `nebula init` to copy from ('none' to clone on demand)", "path"),
    };
    public override string[] Examples => new[] { "nebula config hetzner", "nebula config database --url postgres://nebula:secret@db.example.com/nebula", "nebula config unity --editor \"C:\\Program Files\\Unity\\Hub\\Editor\"", "nebula config show" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        string what = args.Positional.Count > 0 ? args.Positional[0] : "show";
        return what switch
        {
            "hetzner" => Hetzner(ctx, args),
            "database" => Database(ctx, args),
            "unity" => Unity(ctx, args),
            "source" => Source(ctx, args),
            "show" => Show(ctx),
            _ => throw new CliError($"unknown config target '{what}'", "nebula config <hetzner|database|unity|source|show>"),
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
        var dbUrl = CliConfig.DatabaseSettings.ResolveUrl(c.Database);
        Ui.Info($"database    {(dbUrl != null ? DatabaseUrl.Parse(dbUrl, NebulaProject.DefaultDeployDatabase).Display : "SQLite on the orchestrator VM (default)")}{(Environment.GetEnvironmentVariable("NEBULA_DATABASE_URL") != null ? " (from NEBULA_DATABASE_URL)" : "")}");
        var p = NebulaProject.Find(ctx.ProjectOverride ?? Directory.GetCurrentDirectory());
        if (p != null)
        {
            Ui.Blank();
            Ui.Title(p.FilePath);
            Ui.Info($"mesh        workers={p.File.Mesh.Workers} npcs={p.File.Mesh.Npcs} dashboard={p.File.Mesh.DashboardPort} gateway={p.File.Mesh.GatewayPort} database={p.LocalDatabase}");
            Ui.Info($"deploy      target={p.File.Deploy.Target} mesh={p.File.Deploy.MeshName} database={DatabaseUrl.Parse(p.DeployDatabase(c), NebulaProject.DefaultDeployDatabase).Display} workers={p.File.Deploy.Workers}");
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
        Ui.Info("next: nebula deploy (SQLite on the orchestrator VM), or `nebula config database` first to use PostgreSQL");
        return 0;
    }

    private static int Database(Context ctx, ParsedArgs args)
    {
        var db = ctx.Config.Database ?? new CliConfig.DatabaseSettings();
        Ui.Title("Database of the deployed mesh");
        Ui.Info("The orchestrator keeps the control plane and every saved entity in one database. By default that is a");
        Ui.Info("SQLite file on the orchestrator VM, which disappears with the VM. For a mesh whose world must outlive");
        Ui.Info("its VMs, point it at a PostgreSQL server the orchestrator VM can reach.");
        Ui.Blank();

        string? url = args.Get("url") ?? Ui.Ask("database URL (postgres://user:password@host:5432/nebula, or 'default' for SQLite on the VM)", db.Url ?? "default");
        if (url.Length == 0 || url == "default") db.Url = null;
        else
        {
            DatabaseUrl parsed;
            try { parsed = DatabaseUrl.Parse(url, NebulaProject.DefaultDeployDatabase); }
            catch (ArgumentException e) { throw new CliError(e.Message); }
            if (parsed.Scheme is "file" or "memory") throw new CliError($"a deployed orchestrator needs sqlite: or postgres:, not '{parsed.Scheme}:'");
            if (parsed.Scheme == "sqlite" && !url.Contains('/')) throw new CliError("give the SQLite file an absolute path on the orchestrator VM, e.g. sqlite:/opt/nebula/data/nebula.db");
            db.Url = url;
        }
        db.ConfiguredAt = DateTime.UtcNow.ToString("o");
        ctx.Config.Database = db;
        ctx.SaveConfig();
        Ui.Ok($"saved to {Platform.ConfigPath}: {(db.Url != null ? DatabaseUrl.Parse(db.Url, NebulaProject.DefaultDeployDatabase).Display : "SQLite on the orchestrator VM")}");
        var p = NebulaProject.Find(ctx.ProjectOverride ?? Directory.GetCurrentDirectory());
        if (p?.File.Deploy.Database != null) Ui.Warn($"{p.FilePath} sets deploy.database = {p.File.Deploy.Database}, which takes precedence for this project");
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
