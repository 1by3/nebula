namespace Nebula.Cli.Core;

/// <summary>One option a command accepts. <see cref="HasValue"/> options take the next argument (or --name=value).</summary>
public sealed record OptionSpec(string Name, bool HasValue, string Help, string? ValueName = null, string? Short = null);

/// <summary>Base class for `nebula &lt;name&gt;` commands.</summary>
public abstract class Command
{
    public abstract string Name { get; }
    public virtual string[] Aliases => Array.Empty<string>();
    public abstract string Summary { get; }
    /// <summary>Argument synopsis after the command name, e.g. "[worker] [--linux]".</summary>
    public virtual string Usage => "";
    public virtual string? Details => null;
    public virtual OptionSpec[] Options => Array.Empty<OptionSpec>();
    public virtual string[] Examples => Array.Empty<string>();
    public abstract int Run(Context ctx, ParsedArgs args);
}

/// <summary>Global state for one invocation.</summary>
public sealed class Context
{
    public static bool VerboseEnabled;
    public bool Verbose;
    public bool Yes;
    public string? ProjectOverride;
    private CliConfig? _config;

    public CliConfig Config => _config ??= CliConfig.Load();

    public void SaveConfig() => Config.Save();

    /// <summary>The Nebula project (a Unity project with nebula.json) the command runs against.</summary>
    public NebulaProject RequireProject()
    {
        string where = Path.GetFullPath(ProjectOverride ?? Directory.GetCurrentDirectory());
        var p = NebulaProject.Find(where);
        if (p != null) return p;
        if (NebulaProject.FindUnityRoot(where) != null)
        {
            throw new CliError($"{where} is a Unity project but Nebula is not initialised in it",
                "run `nebula init` in the project folder first");
        }
        throw new CliError($"no Nebula project found at or above {where}",
            "run this inside a Unity project that has had `nebula init`, or pass --project <path>");
    }
}

/// <summary>A user-facing failure: printed as one line (plus a hint), no stack trace.</summary>
public sealed class CliError : Exception
{
    public string? Hint { get; }
    public int ExitCode { get; }
    public CliError(string message, string? hint = null, int exitCode = 1) : base(message)
    {
        Hint = hint;
        ExitCode = exitCode;
    }
}

/// <summary>Parsed command-line: positionals plus the options the command declared.</summary>
public sealed class ParsedArgs
{
    public static readonly ParsedArgs Empty = new();
    public List<string> Positional { get; } = new();
    private readonly Dictionary<string, string?> _options = new();

    public bool Has(string name) => _options.ContainsKey(name);
    public string? Get(string name) => _options.TryGetValue(name, out var v) ? v : null;
    public string Get(string name, string fallback) => Get(name) ?? fallback;

    public int GetInt(string name, int fallback)
    {
        var v = Get(name);
        if (v == null) return fallback;
        if (!int.TryParse(v, out int i)) throw new CliError($"--{name} expects a number, got '{v}'");
        return i;
    }

    public static ParsedArgs Parse(string[] argv, Command cmd)
    {
        var result = new ParsedArgs();
        var specs = cmd.Options;
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            if (a == "--")
            {
                result.Positional.AddRange(argv.Skip(i + 1));
                break;
            }
            if (a.StartsWith("-") && a.Length > 1)
            {
                string key = a.TrimStart('-');
                string? inline = null;
                int eq = key.IndexOf('=');
                if (eq >= 0) { inline = key.Substring(eq + 1); key = key.Substring(0, eq); }
                var spec = specs.FirstOrDefault(s => s.Name == key || s.Short == key);
                if (spec == null) throw new CliError($"unknown option '{a}' for `nebula {cmd.Name}`", $"nebula {cmd.Name} --help");
                if (spec.HasValue)
                {
                    string? value = inline;
                    if (value == null)
                    {
                        if (i + 1 >= argv.Length) throw new CliError($"--{spec.Name} needs a value");
                        value = argv[++i];
                    }
                    result._options[spec.Name] = value;
                }
                else
                {
                    if (inline != null) throw new CliError($"--{spec.Name} does not take a value");
                    result._options[spec.Name] = null;
                }
            }
            else
            {
                result.Positional.Add(a);
            }
        }
        return result;
    }
}
