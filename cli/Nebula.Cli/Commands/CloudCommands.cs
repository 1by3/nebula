using System.Text.Json;
using Nebula.Cli.Cloud;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class CloudCommand : Command
{
    public override string Name => "cloud";
    public override string Summary => "Log in to Nebula Cloud, log out, or show your account";
    public override string Usage => "<login|logout|account> [--api <url>] [--json]";
    public override string? Details => @"
login: sign in with a device code. The CLI prints a code and opens the Cloud Dashboard, where you approve it.

logout: revoke the session and delete the stored credentials.

account: show who you are logged in as and the organizations you belong to, with your role in each.

Nebula Cloud is the hosted service for Nebula meshes. Once logged in, `nebula deploy --target cloud` builds the
project, uploads it as a release and rolls it out to a deployment; `nebula status --cloud`, `nebula logs --cloud`,
`nebula scale`, `nebula rollback`, `nebula destroy`, and `nebula dashboard` operate on it.

The tokens are stored in ~/.nebula-cli/config.json (private to you) and refreshed as they expire. NEBULA_CLOUD_API in
the environment points every command at another API host; --api stores one at login.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("api", true, "login: the Cloud API to use instead of the default (NEBULA_CLOUD_API in the environment always wins)", "url"),
        new OptionSpec("no-browser", false, "login: print the URL instead of opening a browser"),
        new OptionSpec("json", false, "account: print the /v1/me answer as JSON"),
    };
    public override string[] Examples => new[] { "nebula cloud login", "nebula cloud account", "nebula cloud logout" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        string what = args.Positional.Count > 0 ? args.Positional[0] : "account";
        return what switch
        {
            "login" => Login(ctx, args),
            "logout" => Logout(ctx),
            "account" or "whoami" => Account(ctx, args),
            _ => throw new CliError($"unknown cloud command '{what}'", "nebula cloud <login|logout|account>"),
        };
    }

    private static int Login(Context ctx, ParsedArgs args)
    {
        string apiUrl = CliConfig.CloudSettings.ResolveApiUrl(ctx.Config.Cloud, args.Get("api") is { } a ? CliConfig.CloudSettings.NormalizeApiUrl(a) : null);
        if (Environment.GetEnvironmentVariable("NEBULA_CLOUD_API") is { Length: > 0 } && args.Has("api")) Ui.Warn("NEBULA_CLOUD_API is set and takes precedence over --api");
        var api = CloudApi.Anonymous(apiUrl);
        Ui.Step($"logging in to Nebula Cloud ({apiUrl})");
        var start = api.StartDeviceLogin();
        string url = start.VerificationUrlComplete ?? start.VerificationUrl;
        Ui.Blank();
        Ui.Title($"    your code: {start.UserCode}");
        Ui.Info($"approve it at {url}");
        Ui.Blank();
        if (!args.Has("no-browser")) Platform.OpenBrowser(url);
        Ui.Info("waiting for approval (Ctrl-C to cancel)...");
        var tokens = api.WaitForDeviceToken(start);
        CloudApi.SaveSession(ctx.Config, apiUrl, tokens);
        Ui.Ok($"logged in as {tokens.User?.Email ?? tokens.User?.Name ?? tokens.User?.Id ?? "you"}");
        Ui.Hint("next: `nebula deploy --target cloud` inside a project, or `nebula cloud account`");
        return 0;
    }

    private static int Logout(Context ctx)
    {
        var cloud = ctx.Config.Cloud;
        if (cloud == null || !cloud.IsLoggedIn) { Ui.Info("not logged in"); return 0; }
        CloudApi.Require(ctx).Logout();
        cloud.AccessToken = null;
        cloud.RefreshToken = null;
        cloud.ExpiresAt = null;
        cloud.User = null;
        ctx.SaveConfig();
        Ui.Ok("logged out");
        return 0;
    }

    private static int Account(Context ctx, ParsedArgs args)
    {
        var api = CloudApi.Require(ctx);
        var me = api.GetMe();
        if (args.Has("json")) { Console.WriteLine(JsonSerializer.Serialize(me, CliConfig.Json)); return 0; }
        Ui.Info($"user   {me.User.Email ?? "-"}{(me.User.Name != null ? $" ({me.User.Name})" : "")}  {me.User.Id}");
        Ui.Info($"api    {api.BaseUrl}");
        Ui.Blank();
        if (me.Organizations.Count == 0) { Ui.Info("no organizations yet; `nebula deploy --target cloud` creates one"); return 0; }
        Ui.Table(new[] { "organization", "name", "role", "billing", "id" },
            me.Organizations.Select(m => new[] { m.Organization.Slug, m.Organization.Name, m.Role, m.Organization.BillingState ?? "", m.Organization.Id }));
        return 0;
    }
}

public sealed class DeploymentsCommand : Command
{
    public override string Name => "deployments";
    public override string Summary => "List the Nebula Cloud deployments of the project's organization";
    public override string Usage => "[--all] [--json]";
    public override string? Details => @"
Show every deployment of the organization the current project deploys to (nebula.json cloud.organization), or of
every organization you belong to with --all. Outside a project, --all is implied.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("all", false, "every organization you belong to"),
        new OptionSpec("json", false, "print the deployments as JSON"),
        CloudTarget.OrgOption,
    };
    public override string[] Examples => new[] { "nebula deployments", "nebula deployments --all", "nebula deployments --json" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var api = CloudApi.Require(ctx);
        var me = api.GetMe();
        string? orgSlug = args.Get("org");
        if (orgSlug == null && !args.Has("all"))
        {
            var project = NebulaProject.Find(Path.GetFullPath(ctx.ProjectOverride ?? Directory.GetCurrentDirectory()));
            orgSlug = project?.File.Cloud?.Organization;
        }
        var orgs = me.Organizations.Select(m => m.Organization).ToList();
        if (orgSlug != null)
        {
            orgs = orgs.Where(o => o.Slug == orgSlug || o.Id == orgSlug).ToList();
            if (orgs.Count == 0) throw new CliError($"you are not a member of an organization '{orgSlug}'", "nebula cloud account");
        }
        var rows = new List<(CloudApi.Organization Org, CloudApi.Project Project, CloudApi.Deployment Deployment)>();
        foreach (var org in orgs)
            foreach (var project in api.Projects(org.Id))
                foreach (var d in api.Deployments(project.Id))
                    rows.Add((org, project, d));
        if (args.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(rows.Select(r => new { organization = r.Org.Slug, project = r.Project.Slug, deployment = r.Deployment }), CliConfig.Json));
            return 0;
        }
        if (rows.Count == 0)
        {
            Ui.Info(orgs.Count == 0 ? "you belong to no organization yet" : "no deployments yet; `nebula deploy --target cloud` creates one");
            return 0;
        }
        Ui.Table(new[] { "deployment", "project", "organization", "state", "region", "workers", "release", "gateway", "id" },
            rows.Select(r => new[]
            {
                r.Deployment.Name, r.Project.Slug, r.Org.Slug, r.Deployment.State + (r.Deployment.SpendLimited ? " (spend limit)" : ""), r.Deployment.Region ?? "",
                $"{r.Deployment.WorkerSize} {r.Deployment.MinWorkers}..{r.Deployment.MaxWorkers}", r.Deployment.CurrentReleaseId ?? "-", r.Deployment.GatewayAddress ?? "-", r.Deployment.Id,
            }));
        return 0;
    }
}

public sealed class ScaleCommand : Command
{
    public override string Name => "scale";
    public override string Summary => "Change the worker range of the running mesh";
    public override string Usage => "--min N --max N [--gateways-min N --gateways-max N] [--target local|hetzner|cloud]";
    public override string? Details => @"
Set the autoscaling band while the mesh runs: the orchestrator starts workers up to --min and never runs more than
--max. --min 0 allows scaling to zero. On Nebula Cloud, --gateways-min and --gateways-max also bound the gateway
fleet. The change applies at once and does not restart anything.

For a local mesh (`nebula start`) or a Hetzner deployment the CLI sets the band through the orchestrator's dashboard
API; for a Nebula Cloud deployment through the Cloud API. Pass --target local to address the local mesh from a
project that deploys elsewhere.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("min", true, "fewest workers the orchestrator keeps running (0 allows scaling to zero)", "N"),
        new OptionSpec("max", true, "most workers the orchestrator may run", "N"),
        new OptionSpec("gateways-min", true, "cloud: fewest gateways", "N"),
        new OptionSpec("gateways-max", true, "cloud: most gateways", "N"),
        new OptionSpec("target", true, "local, hetzner or cloud (default: nebula.json deploy.target; the local mesh when no cloud flags apply)", "name"),
        CloudTarget.OrgOption, CloudTarget.CloudProjectOption, CloudTarget.DeploymentOption,
    };
    public override string[] Examples => new[] { "nebula scale --min 1 --max 8", "nebula scale --min 0 --max 4 --gateways-max 2", "nebula scale --target local --min 2 --max 2" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        int? min = args.Has("min") ? args.GetInt("min", 0) : null;
        int? max = args.Has("max") ? args.GetInt("max", 0) : null;
        int? gmin = args.Has("gateways-min") ? args.GetInt("gateways-min", 0) : null;
        int? gmax = args.Has("gateways-max") ? args.GetInt("gateways-max", 0) : null;
        if (min == null && max == null && gmin == null && gmax == null) throw new CliError("nothing to change", "pass --min and/or --max (and --gateways-min/--gateways-max on Nebula Cloud)");
        if (min != null && max != null && (min < 0 || max < 1 || min > max)) throw new CliError($"invalid worker range {min}..{max}", "0 <= --min <= --max and --max >= 1");

        string target = CloudTarget.TargetOf(args, project);
        if (target == "cloud")
        {
            var api = CloudApi.Require(ctx);
            var t = CloudTarget.Resolve(ctx, api, project, args, create: false);
            var d = api.Scale(t.Deployment.Id, min, max, gmin, gmax);
            Ui.Ok($"{t.Describe}: workers {d.MinWorkers}..{d.MaxWorkers}, gateways {d.MinGateways}..{d.MaxGateways}");
            return 0;
        }
        if (gmin != null || gmax != null) throw new CliError("gateway scaling is only available on Nebula Cloud", "drop --gateways-min/--gateways-max, or deploy with --target cloud");
        string url;
        if (target == "hetzner")
        {
            var hz = ctx.Config.Hetzner;
            if (hz == null || !hz.IsConfigured) throw new CliError("Hetzner is not configured", "nebula config hetzner");
            var mesh = new HetznerMesh(hz, project);
            var orch = mesh.Orchestrator() ?? throw new CliError("no orchestrator server", "nebula deploy");
            url = $"http://{HetznerMesh.PublicIp(orch)}:{mesh.DashboardPort}/";
        }
        else
        {
            url = $"http://127.0.0.1:{project.File.Mesh.DashboardPort}/";
        }
        var state = LocalMesh.FetchState(url) ?? throw new CliError($"the dashboard at {url} is not answering", target == "hetzner" ? "nebula status --cloud" : "is the mesh running? (`nebula start`)");
        int curMin = state["scale"]?["minWorkers"]?.GetValue<int>() ?? state["minWorkers"]?.GetValue<int>() ?? 0;
        int curMax = state["scale"]?["maxWorkers"]?.GetValue<int>() ?? state["maxWorkers"]?.GetValue<int>() ?? Math.Max(1, state["desiredWorkers"]?.GetValue<int>() ?? 1);
        int newMin = min ?? Math.Min(curMin, max ?? curMin), newMax = max ?? Math.Max(curMax, min ?? curMax);
        if (newMin < 0 || newMax < 1 || newMin > newMax) throw new CliError($"invalid worker range {newMin}..{newMax}", "0 <= --min <= --max and --max >= 1");
        string? error = LocalMesh.PostApi(url, "/api/scale/limits", new { min = newMin, max = newMax });
        if (error != null) throw new CliError($"the orchestrator refused the new band: {error}");
        Ui.Ok($"{(target == "hetzner" ? "hetzner" : "local")} mesh: workers {newMin}..{newMax}");
        return 0;
    }
}

public sealed class RollbackCommand : Command
{
    public override string Name => "rollback";
    public override string Summary => "Roll a Nebula Cloud deployment back to its previous release";
    public override string Usage => "[--release rel_id]";
    public override string? Details => @"
Roll out the release that ran before the current one (or the one named with --release) and follow the operation.
A rollback restarts the orchestrator and recreates the workers like a deploy does; players are disconnected while it
runs. Releases are immutable, so `nebula deploy --release rel_id` and `nebula rollback --release rel_id` do the same.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("release", true, "the release to return to (default: the previous one)", "rel_id"),
        CloudTarget.OrgOption, CloudTarget.CloudProjectOption, CloudTarget.DeploymentOption,
    };
    public override string[] Examples => new[] { "nebula rollback", "nebula rollback --release rel_01h..." };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        var api = CloudApi.Require(ctx);
        var t = CloudTarget.Resolve(ctx, api, project, args, create: false);
        var dep = t.Deployment;
        string? release = args.Get("release");
        if (release == null && dep.PreviousReleaseId == null) throw new CliError($"{dep.Name} has no previous release to roll back to", "pass --release <rel_id>; `nebula deploy --release` lists nothing, but the Cloud Dashboard does");
        Ui.Title($"rolling {t.Describe} back to {release ?? dep.PreviousReleaseId} (from {dep.CurrentReleaseId ?? "nothing"})");
        var running = OperationFollower.FindRunning(api, dep.Id);
        CloudApi.Operation op;
        if (running != null)
        {
            Ui.Warn($"operation {running.Id} ({running.Kind}) is already {running.State}; following it");
            op = running;
        }
        else
        {
            try { op = api.Rollback(dep.Id, release); }
            catch (CloudApiError e) when (CloudApi.RunningOperationOf(e) is { } other) { Ui.Warn($"operation {other.Id} is already running; following it"); op = api.GetOperation(other.Id); }
        }
        OperationFollower.Follow(api, op);
        dep = api.GetDeployment(dep.Id);
        Ui.Ok($"{dep.Name} is {dep.State} on release {dep.CurrentReleaseId}");
        if (dep.GatewayAddress != null) Ui.Info($"gateway    {dep.GatewayAddress}");
        return 0;
    }
}

public sealed class DashboardCommand : Command
{
    public override string Name => "dashboard";
    public override string Summary => "Open the Nebula Dashboard of the local mesh or the deployed one";
    public override string Usage => "[--target local|hetzner|cloud] [--no-open]";
    public override string? Details => @"
Print the dashboard URL and open it in the browser. For a Nebula Cloud deployment that is the Cloud Dashboard page
that embeds the mesh's Nebula Dashboard; for the local mesh it is http://127.0.0.1:7080/ (mesh.dashboardPort in
nebula.json); for a Hetzner deployment the orchestrator VM's dashboard port.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("target", true, "local, hetzner or cloud (default: nebula.json deploy.target when a deployment exists there, else local)", "name"),
        new OptionSpec("no-open", false, "only print the URL"),
        CloudTarget.OrgOption, CloudTarget.CloudProjectOption, CloudTarget.DeploymentOption,
    };
    public override string[] Examples => new[] { "nebula dashboard", "nebula dashboard --target local" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        string target = args.Get("target") ?? (project.File.Cloud?.IsComplete == true && project.File.Deploy.Target == "cloud" ? "cloud" : "local");
        string url;
        switch (target)
        {
            case "cloud":
            {
                var api = CloudApi.Require(ctx);
                var t = CloudTarget.Resolve(ctx, api, project, args, create: false);
                url = t.Deployment.DashboardUrl ?? throw new CliError($"{t.Describe} has no dashboard URL yet", "run `nebula deploy` first");
                break;
            }
            case "hetzner":
            {
                var hz = ctx.Config.Hetzner;
                if (hz == null || !hz.IsConfigured) throw new CliError("Hetzner is not configured", "nebula config hetzner");
                var mesh = new HetznerMesh(hz, project);
                var orch = mesh.Orchestrator() ?? throw new CliError("no orchestrator server", "nebula deploy");
                url = $"http://{HetznerMesh.PublicIp(orch)}:{mesh.DashboardPort}/";
                break;
            }
            case "local":
                url = $"http://127.0.0.1:{project.File.Mesh.DashboardPort}/";
                break;
            default:
                throw new CliError($"unknown target '{target}'", "local, hetzner or cloud");
        }
        Ui.Info($"dashboard  {url}");
        if (!args.Has("no-open")) Platform.OpenBrowser(url);
        return 0;
    }
}
