using System.Text.RegularExpressions;

namespace Nebula.Cli.Cloud;

using Nebula.Cli.Core;

/// <summary>The organization, project and deployment a Nebula project deploys to, resolved to their ids.</summary>
public sealed record CloudTarget(CloudApi.Organization Organization, CloudApi.Project Project, CloudApi.Deployment Deployment)
{
    public static readonly OptionSpec OrgOption = new("org", true, "cloud: organization slug (default from nebula.json)", "slug");
    public static readonly OptionSpec CloudProjectOption = new("cloud-project", true, "cloud: project slug inside the organization (default from nebula.json)", "slug");
    public static readonly OptionSpec DeploymentOption = new("deployment", true, "cloud: deployment name inside the project (default from nebula.json)", "name");
    public static readonly OptionSpec TargetOption = new("target", true, "deploy target: hetzner or cloud (default from nebula.json deploy.target)", "name");

    public string Describe => $"{Organization.Slug}/{Project.Slug}/{Deployment.Name}";

    /// <summary>The target a command runs against: --target, else nebula.json deploy.target.</summary>
    public static string TargetOf(ParsedArgs args, NebulaProject project) => (args.Get("target") ?? project.File.Deploy.Target ?? "hetzner").ToLowerInvariant();

    /// <summary>True when the project (or --target) deploys to Nebula Cloud.</summary>
    public static bool IsCloud(ParsedArgs args, NebulaProject project) => TargetOf(args, project) == "cloud";

    /// <summary>What the API can create from: --region, --worker-size, --min/--max, and nebula.json defaults.</summary>
    public sealed record CreateDefaults(string? Region, string? WorkerSize, int? MinWorkers, int? MaxWorkers);

    /// <summary>
    /// Find the deployment named by the options or nebula.json's cloud section. With <paramref name="create"/>, a missing
    /// organization, project, or deployment is chosen from the ones you have or created (interactively, or with the
    /// defaults under --yes), and the choice is written back to nebula.json.
    /// </summary>
    public static CloudTarget Resolve(Context ctx, CloudApi api, NebulaProject project, ParsedArgs args, bool create, CreateDefaults? defaults = null)
    {
        var cloud = project.File.Cloud ?? new ProjectFile.CloudSettings();
        string? orgSlug = args.Get("org") ?? cloud.Organization;
        string? projectSlug = args.Get("cloud-project") ?? cloud.Project;
        string? deploymentName = args.Get("deployment") ?? cloud.Deployment;
        if (!create && (orgSlug == null || projectSlug == null || deploymentName == null))
            throw new CliError("this project has no Nebula Cloud deployment in nebula.json", "run `nebula deploy --target cloud` first, or pass --org, --cloud-project and --deployment");

        var me = api.GetMe();
        var org = PickOrganization(ctx, api, me, orgSlug, project, create);
        var prj = PickProject(ctx, api, org, projectSlug, project, create);
        var dep = PickDeployment(ctx, api, prj, deploymentName, project, args, create, defaults);

        // Only a deploy writes the choice back; status, logs and the like leave nebula.json alone.
        if (create && (cloud.Organization != org.Slug || cloud.Project != prj.Slug || cloud.Deployment != dep.Name || project.File.Deploy.Target != "cloud"))
        {
            project.File.Cloud = new ProjectFile.CloudSettings { Organization = org.Slug, Project = prj.Slug, Deployment = dep.Name };
            project.File.Deploy.Target = "cloud";
            project.Save();
            Ui.Ok($"nebula.json: cloud = {org.Slug}/{prj.Slug}/{dep.Name}, deploy.target = cloud");
        }
        return new CloudTarget(org, prj, dep);
    }

    private static CloudApi.Organization PickOrganization(Context ctx, CloudApi api, CloudApi.Me me, string? slug, NebulaProject project, bool create)
    {
        var orgs = me.Organizations.Select(m => m.Organization).ToList();
        if (slug != null)
        {
            var found = orgs.FirstOrDefault(o => o.Slug == slug || o.Id == slug);
            if (found != null) return found;
            if (!create) throw new CliError($"you are not a member of an organization '{slug}'", orgs.Count > 0 ? "yours: " + string.Join(", ", orgs.Select(o => o.Slug)) : "run `nebula cloud account`");
            if (!Ui.Confirm($"create organization '{slug}'?", true, ctx.Yes)) throw new OperationCanceledException();
            return api.CreateOrganization(slug, slug);
        }
        if (orgs.Count == 1) { Ui.Info($"organization {orgs[0].Slug} ({orgs[0].Name})"); return orgs[0]; }
        if (orgs.Count > 1)
        {
            if (ctx.Yes || Console.IsInputRedirected) throw new CliError("you belong to more than one organization", "pass --org <slug>: " + string.Join(", ", orgs.Select(o => o.Slug)));
            int i = Ui.Choose("which organization?", orgs.Select(o => $"{o.Slug}  ({o.Name})").ToList());
            return orgs[i];
        }
        if (!create) throw new CliError("you belong to no organization", "run `nebula deploy --target cloud` to create one");
        string name = ctx.Yes ? Path.GetFileName(project.Root) : Ui.Ask("organization name", Path.GetFileName(project.Root));
        string newSlug = ctx.Yes ? Slug(name) : Ui.Ask("organization slug", Slug(name));
        return api.CreateOrganization(name, newSlug);
    }

    private static CloudApi.Project PickProject(Context ctx, CloudApi api, CloudApi.Organization org, string? slug, NebulaProject project, bool create)
    {
        var projects = api.Projects(org.Id);
        if (slug != null)
        {
            var found = projects.FirstOrDefault(p => p.Slug == slug || p.Id == slug);
            if (found != null) return found;
            if (!create) throw new CliError($"organization {org.Slug} has no project '{slug}'", projects.Count > 0 ? "its projects: " + string.Join(", ", projects.Select(p => p.Slug)) : "run `nebula deploy --target cloud` to create it");
            if (!Ui.Confirm($"create project '{slug}' in {org.Slug}?", true, ctx.Yes)) throw new OperationCanceledException();
            return api.CreateProject(org.Id, slug, slug);
        }
        if (projects.Count == 1) { Ui.Info($"project {projects[0].Slug} ({projects[0].Name})"); return projects[0]; }
        if (projects.Count > 1)
        {
            if (ctx.Yes || Console.IsInputRedirected) throw new CliError($"organization {org.Slug} has more than one project", "pass --cloud-project <slug>: " + string.Join(", ", projects.Select(p => p.Slug)));
            var options = projects.Select(p => $"{p.Slug}  ({p.Name})").ToList();
            options.Add("create a new project");
            int i = Ui.Choose("which project?", options);
            if (i < projects.Count) return projects[i];
        }
        if (!create) throw new CliError($"organization {org.Slug} has no project", "run `nebula deploy --target cloud` to create one");
        string name = ctx.Yes ? Path.GetFileName(project.Root) : Ui.Ask("project name", Path.GetFileName(project.Root));
        string newSlug = ctx.Yes ? Slug(name) : Ui.Ask("project slug", Slug(name));
        return api.CreateProject(org.Id, name, newSlug);
    }

    private static CloudApi.Deployment PickDeployment(Context ctx, CloudApi api, CloudApi.Project prj, string? name, NebulaProject project, ParsedArgs args, bool create, CreateDefaults? defaults)
    {
        var deployments = api.Deployments(prj.Id).Where(d => d.State != "destroyed").ToList();
        if (name != null)
        {
            var found = deployments.FirstOrDefault(d => d.Name == name || d.Id == name);
            if (found != null) return found;
            if (!create) throw new CliError($"project {prj.Slug} has no deployment '{name}'", deployments.Count > 0 ? "its deployments: " + string.Join(", ", deployments.Select(d => d.Name)) : "run `nebula deploy --target cloud` to create it");
            if (!Ui.Confirm($"create deployment '{name}' in {prj.Slug}?", true, ctx.Yes)) throw new OperationCanceledException();
            return Create(ctx, api, prj, name, project, defaults);
        }
        if (deployments.Count == 1) { Ui.Info($"deployment {deployments[0].Name} ({deployments[0].State}, {deployments[0].Region})"); return deployments[0]; }
        if (deployments.Count > 1)
        {
            if (ctx.Yes || Console.IsInputRedirected) throw new CliError($"project {prj.Slug} has more than one deployment", "pass --deployment <name>: " + string.Join(", ", deployments.Select(d => d.Name)));
            var options = deployments.Select(d => $"{d.Name}  ({d.State}, {d.Region})").ToList();
            options.Add("create a new deployment");
            int i = Ui.Choose("which deployment?", options);
            if (i < deployments.Count) return deployments[i];
        }
        if (!create) throw new CliError($"project {prj.Slug} has no deployment", "run `nebula deploy --target cloud` to create one");
        string newName = ctx.Yes ? "production" : Ui.Ask("deployment name", "production");
        return Create(ctx, api, prj, newName, project, defaults);
    }

    private static CloudApi.Deployment Create(Context ctx, CloudApi api, CloudApi.Project prj, string name, NebulaProject project, CreateDefaults? defaults)
    {
        var catalog = api.GetCatalog();
        var regions = catalog.Regions?.Where(r => r.Available).ToList() ?? new List<CloudApi.Region>();
        string region = defaults?.Region ?? regions.FirstOrDefault()?.Id ?? throw new CliError("Nebula Cloud has no available region right now");
        if (regions.Count > 0 && regions.All(r => r.Id != region)) throw new CliError($"unknown region '{region}'", "available: " + string.Join(", ", regions.Select(r => r.Id)));
        if (defaults?.Region == null && regions.Count > 1 && !ctx.Yes && !Console.IsInputRedirected)
            region = regions[Ui.Choose("which region?", regions.Select(r => $"{r.Id}  ({r.Name})").ToList())].Id;
        var sizes = catalog.WorkerSizes?.Select(s => s.Id).ToList() ?? new List<string>();
        string workerSize = defaults?.WorkerSize ?? (sizes.Contains("small") ? "small" : sizes.FirstOrDefault() ?? "small");
        if (sizes.Count > 0 && !sizes.Contains(workerSize)) throw new CliError($"unknown worker size '{workerSize}'", "available: " + string.Join(", ", sizes));
        string gatewaySize = catalog.GatewaySizes?.FirstOrDefault()?.Id ?? "standard";
        var d = project.File.Deploy;
        int min = defaults?.MinWorkers ?? d.MinWorkers ?? d.Workers;
        int max = defaults?.MaxWorkers ?? d.MaxWorkers ?? d.Workers;
        if (min < 0 || max < 1 || min > max) throw new CliError($"invalid worker range {min}..{max}", "0 <= --min <= --max and --max >= 1");
        var settings = d.Npcs > 0 ? new Dictionary<string, string> { ["npcs"] = d.Npcs.ToString() } : null;
        Ui.Step($"creating deployment {name} in {prj.Slug}: region {region}, workers {workerSize} {min}..{max}, gateways {gatewaySize} 1..3");
        return api.CreateDeployment(prj.Id, new CloudApi.NewDeployment(name, region, workerSize, gatewaySize, min, max, 1, 3, settings));
    }

    /// <summary>A URL-safe slug from a folder or display name: lowercase, letters, digits and dashes.</summary>
    public static string Slug(string name)
    {
        string s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length > 0 ? s : "my-game";
    }
}
