using System.Text.Json;
using Nebula.Cli.Cloud;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class EnvCommand : Command
{
    public override string Name => "env";
    public override string Summary => "Manage the environment variables your workers get";
    public override string Usage => "<ls|set|rm|pull|push> [KEY[=VALUE]|file] [--secret] [--deployment <name>] [--target hetzner|cloud]";
    public override string? Details => @"
Environment variables pass your game's own settings and secrets to its workers: service URLs, API keys, feature
flags. Every worker process gets them as ordinary environment variables, and NebulaEnv.Get reads them in code.
Changes apply when workers next start: the next `nebula deploy`, rollout or restart.

ls: list the variables with the deployments they apply to. Secret values are never shown.

set KEY=VALUE: create or replace a variable. `set KEY` without a value reads it from standard input, or asks for it
when you run the command in a terminal, so a secret stays out of your shell history:
`nebula env set GAME_API_KEY --secret < key.txt`.

rm KEY: remove a variable.

pull: print the non-secret variables as a dotenv file, or write them to --out. Secret values never leave Nebula
Cloud; each one appears as a comment. `nebula env pull --out .env.nebula` gives local runs the deployment's settings.

push FILE: upload every variable in a dotenv file at once. --secret-keys marks which of them are secret.

On Nebula Cloud (deploy.target cloud) the variables belong to the project in nebula.json's cloud section. Without
--deployment a variable applies to every deployment of the project; --deployment staging,production limits it to
those. The Cloud Dashboard edits the same variables.

On a self-hosted Hetzner deployment (deploy.target hetzner) the variables are kept in deploy.env in nebula.json.
That file is committed with the project, so secrets are refused there. `nebula deploy` writes them into the service
manifest on the orchestrator VM, which hands them to every worker VM it starts.

Local runs read a .env.nebula file at the project root instead (`nebula start` and the Editor). Keep it out of version
control; `nebula init` adds it to your .gitignore.

Names use letters, digits and _, and do not start with a digit. PORT and names starting with NEBULA_ are Nebula's own
and are refused. A value can be up to 32 KiB.
";
    public static readonly OptionSpec EnvDeploymentOption = new("deployment", true, "cloud: the deployments a variable applies to, comma-separated (default: every deployment of the project); ls and pull show what one deployment gets", "name");
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("secret", false, "set: the value is secret. It is never shown or downloaded again (cloud only)"),
        EnvDeploymentOption,
        new OptionSpec("out", true, "pull: write to this file instead of standard output", "file"),
        new OptionSpec("secret-keys", true, "push: the keys in the file whose values are secret, comma-separated", "K1,K2"),
        new OptionSpec("json", false, "ls: print the variables as JSON"),
        CloudTarget.TargetOption, CloudTarget.OrgOption, CloudTarget.CloudProjectOption,
    };
    public override string[] Examples => new[]
    {
        "nebula env ls",
        "nebula env set GAME_API_URL=https://api.example.com",
        "nebula env set GAME_API_KEY --secret < key.txt",
        "nebula env set FEATURE_TRADING=on --deployment staging",
        "nebula env rm FEATURE_TRADING --deployment staging",
        "nebula env pull --out .env.nebula",
        "nebula env push .env.production --deployment production --secret-keys GAME_API_KEY",
    };

    /// <summary>Tests feed standard input here; null reads the console.</summary>
    internal static TextReader? StdinOverride;

    public override int Run(Context ctx, ParsedArgs args)
    {
        string what = args.Positional.Count > 0 ? args.Positional[0] : "ls";
        var rest = args.Positional.Skip(1).ToList();
        var project = ctx.RequireProject();
        IEnvStore store = CloudTarget.TargetOf(args, project) switch
        {
            "cloud" => CloudEnvStore.Open(ctx, project, args),
            "hetzner" => new ProjectEnvStore(project, args),
            var t => throw new CliError($"deploy target '{t}' has no environment variables", "hetzner or cloud"),
        };
        return what switch
        {
            "ls" or "list" => List(store, args, rest),
            "set" or "add" => Set(ctx, store, args, rest),
            "rm" or "remove" or "unset" => Remove(store, rest),
            "pull" => Pull(store, args, rest),
            "push" => Push(store, args, rest),
            _ => throw new CliError($"unknown env command '{what}'", "nebula env <ls|set|rm|pull|push>"),
        };
    }

    // --- subcommands ----------------------------------------------------------------------------------------

    private static int List(IEnvStore store, ParsedArgs args, List<string> rest)
    {
        if (rest.Count > 0) throw new CliError("`nebula env ls` takes no arguments", "nebula env ls [--deployment <name>]");
        var vars = store.List().OrderBy(v => v.Key, StringComparer.Ordinal).ToList();
        if (args.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(vars, CloudApi.Json));
            return 0;
        }
        if (vars.Count == 0) { Ui.Info($"no environment variables for {store.Describe}"); return 0; }
        Ui.Info($"environment variables for {store.Describe}:");
        Ui.Table(new[] { "key", "value", "deployments", "updated" },
            vars.Select(v => new[] { v.Key, v.Secret ? "(secret)" : Shorten(v.Value ?? ""), store.ScopeOf(v), Updated(v) }));
        return 0;
    }

    private static int Set(Context ctx, IEnvStore store, ParsedArgs args, List<string> rest)
    {
        if (rest.Count != 1) throw new CliError("`nebula env set` takes one KEY=VALUE, or a KEY with the value on standard input", "nebula env set KEY=VALUE   or   nebula env set KEY --secret < value.txt");
        string arg = rest[0];
        int eq = arg.IndexOf('=');
        string key = eq >= 0 ? arg.Substring(0, eq) : arg;
        bool secret = args.Has("secret");
        CheckKey(key);
        string value;
        if (eq >= 0)
        {
            value = arg.Substring(eq + 1);
            if (secret) Ui.Warn("a secret given as KEY=VALUE is in your shell history; next time pass the value on standard input: nebula env set KEY --secret < file");
        }
        else value = ReadValue(key, secret);
        CheckValue(key, value);
        var saved = store.Set(key, value, secret);
        Ui.Ok($"{key} set{(saved.Secret ? " (secret)" : "")} for {store.ScopeOf(saved)} of {store.Describe}");
        Ui.Info(store.AppliesWhen);
        return 0;
    }

    private static int Remove(IEnvStore store, List<string> rest)
    {
        if (rest.Count != 1) throw new CliError("`nebula env rm` takes one KEY", "nebula env rm KEY [--deployment <name>]");
        string key = rest[0];
        if (!DotEnv.IsValidKey(key)) throw new CliError(DotEnv.KeyProblem(key)!);
        store.Remove(key);
        Ui.Ok($"{key} removed from {store.RemoveScope} of {store.Describe}");
        Ui.Info(store.AppliesWhen);
        return 0;
    }

    private static int Pull(IEnvStore store, ParsedArgs args, List<string> rest)
    {
        if (rest.Count > 0) throw new CliError("`nebula env pull` takes no arguments", "nebula env pull [--deployment <name>] [--out <file>]");
        var vars = store.List().OrderBy(v => v.Key, StringComparer.Ordinal).ToList();
        var plain = vars.Where(v => !v.Secret).Select(v => new KeyValuePair<string, string>(v.Key, v.Value ?? "")).ToList();
        var secrets = vars.Where(v => v.Secret).Select(v => v.Key).ToList();
        var header = new List<string> { $"Environment variables for {store.Describe}, from `nebula env pull`." };
        if (secrets.Count > 0) header.Add("Secret values are never downloaded. Set these yourself for local runs: " + string.Join(", ", secrets));
        string text = DotEnv.Write(plain, header);
        foreach (var s in secrets) text += $"# {s}=  (secret)\n";
        string? outFile = args.Get("out");
        if (outFile == null)
        {
            Console.Out.Write(text);
            return 0;
        }
        File.WriteAllText(outFile, text);
        Ui.Ok($"wrote {plain.Count} variable(s) to {outFile}{(secrets.Count > 0 ? $"; {secrets.Count} secret(s) left out" : "")}");
        return 0;
    }

    private static int Push(IEnvStore store, ParsedArgs args, List<string> rest)
    {
        if (rest.Count != 1) throw new CliError("`nebula env push` takes one dotenv file", "nebula env push <file> [--deployment <name>] [--secret-keys K1,K2]");
        string file = rest[0];
        if (!File.Exists(file)) throw new CliError($"no file {file}");
        var parsed = DotEnv.Parse(File.ReadAllText(file));
        if (parsed.Errors.Count > 0)
            throw new CliError($"{file} has lines that cannot be read:\n  " + string.Join("\n  ", parsed.Errors), "fix them and push again; nothing was uploaded");
        var vars = parsed.ToDictionary();
        if (vars.Count == 0) throw new CliError($"{file} has no variables");
        var problems = vars.Select(kv => DotEnv.KeyProblem(kv.Key) ?? DotEnv.ValueProblem(kv.Key, kv.Value)).Where(p => p != null).ToList();
        if (problems.Count > 0) throw new CliError(string.Join("\n  ", problems), "nothing was uploaded");
        var secretKeys = (args.Get("secret-keys") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var unknown = secretKeys.Where(k => !vars.ContainsKey(k)).ToList();
        if (unknown.Count > 0) throw new CliError($"--secret-keys names {string.Join(", ", unknown)}, which {(unknown.Count == 1 ? "is" : "are")} not in {file}", "nothing was uploaded");
        var saved = store.Push(vars.Select(kv => new CloudApi.EnvVarInput(kv.Key, kv.Value, secretKeys.Contains(kv.Key))).ToList());
        Ui.Ok($"pushed {saved.Count} variable(s){(secretKeys.Count > 0 ? $" ({secretKeys.Count} secret)" : "")} to {store.Describe}");
        Ui.Info(store.AppliesWhen);
        return 0;
    }

    // --- helpers --------------------------------------------------------------------------------------------

    private static void CheckKey(string key)
    {
        if (DotEnv.KeyProblem(key) is { } problem) throw new CliError(problem, key.Length > 0 && DotEnv.IsReserved(key) ? "pick another name for your variable" : null);
    }

    private static void CheckValue(string key, string value)
    {
        if (DotEnv.ValueProblem(key, value) is { } problem) throw new CliError(problem);
    }

    /// <summary>The value of `set KEY` from standard input, or a prompt in a terminal. One trailing line break is dropped.</summary>
    private static string ReadValue(string key, bool secret)
    {
        TextReader? input = StdinOverride ?? (Console.IsInputRedirected ? Console.In : null);
        if (input != null)
        {
            string text = input.ReadToEnd();
            if (text.EndsWith("\r\n")) text = text[..^2];
            else if (text.EndsWith('\n')) text = text[..^1];
            return text;
        }
        if (Ui.AssumeInteractive || !Console.IsInputRedirected)
            return secret ? Ui.AskSecret($"value of {key}") : Ui.Ask($"value of {key}");
        throw new CliError($"no value for {key}", "pass KEY=VALUE, or the value on standard input");
    }

    internal static string Scope(List<string>? deployments) =>
        deployments == null || deployments.Count == 0 ? "all deployments" : string.Join(", ", deployments);

    private static string Updated(CloudApi.EnvVar v)
    {
        string at = v.UpdatedAt is { Length: >= 16 } a ? a.Substring(0, 16).Replace('T', ' ') : v.UpdatedAt ?? "";
        return v.UpdatedBy is { Length: > 0 } by ? $"{at} by {by}".Trim() : at;
    }

    private static string Shorten(string value)
    {
        string one = value.Replace("\r", "").Replace('\n', ' ');
        return one.Length > 48 ? one.Substring(0, 45) + "..." : one;
    }

    // --- where the variables live -----------------------------------------------------------------------------

    private interface IEnvStore
    {
        string Describe { get; }
        string RemoveScope { get; }
        string AppliesWhen { get; }
        string ScopeOf(CloudApi.EnvVar v);
        List<CloudApi.EnvVar> List();
        CloudApi.EnvVar Set(string key, string value, bool secret);
        void Remove(string key);
        List<CloudApi.EnvVar> Push(List<CloudApi.EnvVarInput> vars);
    }

    /// <summary>Nebula Cloud: the project's variables through the Cloud API.</summary>
    private sealed class CloudEnvStore : IEnvStore
    {
        private readonly CloudApi _api;
        private readonly CloudApi.Organization _org;
        private readonly CloudApi.Project _project;
        private readonly List<string> _deployments;

        private CloudEnvStore(CloudApi api, CloudApi.Organization org, CloudApi.Project project, List<string> deployments)
        {
            _api = api;
            _org = org;
            _project = project;
            _deployments = deployments;
        }

        public static CloudEnvStore Open(Context ctx, NebulaProject project, ParsedArgs args)
        {
            var cloud = project.File.Cloud;
            string? orgSlug = args.Get("org") ?? cloud?.Organization;
            string? projectSlug = args.Get("cloud-project") ?? cloud?.Project;
            if (orgSlug == null || projectSlug == null)
                throw new CliError("this project has no Nebula Cloud project in nebula.json", "run `nebula deploy --target cloud` first, or pass --org and --cloud-project");
            var api = CloudApi.Require(ctx);
            var orgs = api.GetMe().Organizations.Select(m => m.Organization).ToList();
            var org = orgs.FirstOrDefault(o => o.Slug == orgSlug || o.Id == orgSlug)
                ?? throw new CliError($"you are not a member of an organization '{orgSlug}'", orgs.Count > 0 ? "yours: " + string.Join(", ", orgs.Select(o => o.Slug)) : "run `nebula cloud account`");
            var projects = api.Projects(org.Id);
            var prj = projects.FirstOrDefault(p => p.Slug == projectSlug || p.Id == projectSlug)
                ?? throw new CliError($"organization {org.Slug} has no project '{projectSlug}'", projects.Count > 0 ? "its projects: " + string.Join(", ", projects.Select(p => p.Slug)) : "run `nebula deploy --target cloud` to create it");
            var deployments = (args.Get("deployment") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
            if (deployments.Count > 0)
            {
                // A variable can be set up before its deployment exists, so an unknown name is only a warning.
                var known = api.Deployments(prj.Id).Where(d => d.State != "destroyed").Select(d => d.Name).ToHashSet();
                foreach (var d in deployments.Where(d => !known.Contains(d)))
                    Ui.Warn($"project {prj.Slug} has no deployment '{d}' yet{(known.Count > 0 ? $" (it has {string.Join(", ", known)})" : "")}");
            }
            return new CloudEnvStore(api, org, prj, deployments);
        }

        private string? OneDeployment(string what)
        {
            if (_deployments.Count > 1) throw new CliError($"{what} takes one --deployment, not a list");
            return _deployments.FirstOrDefault();
        }

        public string Describe => $"{_org.Slug}/{_project.Slug}" + (_deployments.Count > 0 ? $" ({string.Join(", ", _deployments)})" : "");
        public string RemoveScope => _deployments.Count > 0 ? string.Join(", ", _deployments) : "every deployment";
        public string AppliesWhen => "workers get the change when they next start: the next `nebula deploy`, rollout or restart";
        public string ScopeOf(CloudApi.EnvVar v) => Scope(v.Deployments);
        public List<CloudApi.EnvVar> List() => _api.EnvVars(_project.Id, OneDeployment("ls and pull"));
        public CloudApi.EnvVar Set(string key, string value, bool secret) => _api.SetEnvVar(_project.Id, key, value, secret, _deployments);
        public void Remove(string key)
        {
            if (_deployments.Count == 0) { _api.DeleteEnvVar(_project.Id, key, null); return; }
            foreach (var d in _deployments) _api.DeleteEnvVar(_project.Id, key, d);
        }
        public List<CloudApi.EnvVar> Push(List<CloudApi.EnvVarInput> vars) => _api.PushEnvVars(_project.Id, vars, _deployments);
    }

    /// <summary>A self-hosted deployment: deploy.env in nebula.json, written into the service manifest at deploy time.</summary>
    private sealed class ProjectEnvStore : IEnvStore
    {
        private readonly NebulaProject _project;

        public ProjectEnvStore(NebulaProject project, ParsedArgs args)
        {
            _project = project;
            if (args.Has("deployment"))
                throw new CliError("a self-hosted deployment has one set of variables; --deployment is for Nebula Cloud", "drop --deployment");
            if (args.Has("secret") || args.Has("secret-keys"))
                throw new CliError("self-hosted deployments keep their variables in nebula.json, which is committed with the project, so they cannot hold secrets",
                    "keep secrets out of deploy.env; see the Environment variables guide for self-hosted secrets, or deploy to Nebula Cloud");
        }

        private SortedDictionary<string, string> Env => _project.File.Deploy.Env ??= new SortedDictionary<string, string>(StringComparer.Ordinal);

        public string Describe => "the self-hosted deployment (deploy.env in nebula.json)";
        public string RemoveScope => "every worker";
        public string AppliesWhen => "workers get the change on the next `nebula deploy`";
        public string ScopeOf(CloudApi.EnvVar v) => "every worker";

        public List<CloudApi.EnvVar> List() =>
            (_project.File.Deploy.Env ?? new SortedDictionary<string, string>()).Select(kv => new CloudApi.EnvVar(kv.Key, false, kv.Value, new List<string>(), null, null)).ToList();

        public CloudApi.EnvVar Set(string key, string value, bool secret)
        {
            Env[key] = value;
            _project.Save();
            return new CloudApi.EnvVar(key, false, value, new List<string>(), null, null);
        }

        public void Remove(string key)
        {
            if (_project.File.Deploy.Env == null || !_project.File.Deploy.Env.Remove(key)) throw new CliError($"{key} is not set in deploy.env", "nebula env ls");
            if (_project.File.Deploy.Env.Count == 0) _project.File.Deploy.Env = null;
            _project.Save();
        }

        public List<CloudApi.EnvVar> Push(List<CloudApi.EnvVarInput> vars)
        {
            foreach (var v in vars) Env[v.Key] = v.Value;
            _project.Save();
            return vars.Select(v => new CloudApi.EnvVar(v.Key, false, v.Value, new List<string>(), null, null)).ToList();
        }
    }
}
