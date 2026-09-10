using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class HelpCommand : Command
{
    public override string Name => "help";
    public override string Summary => "Show help for the CLI or one command";
    public override string Usage => "[command] [--json]";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("json", false, "describe every command as JSON (the docs site generates its CLI reference from this)"),
    };

    public override int Run(Context ctx, ParsedArgs args)
    {
        if (args.Has("json"))
        {
            PrintJson();
            return 0;
        }
        if (args.Positional.Count > 0)
        {
            var cmd = Program.Find(args.Positional[0]);
            if (cmd == null) throw new CliError($"unknown command '{args.Positional[0]}'");
            PrintCommandHelp(cmd);
            return 0;
        }

        Ui.Title($"nebula {Platform.CliVersion} - dynamically meshed multiplayer servers for Unity");
        Ui.Blank();
        Console.WriteLine("usage: nebula <command> [options]");
        Ui.Blank();
        Console.WriteLine("getting started");
        PrintRow("setup", "install prerequisites (SpacetimeDB CLI, .NET SDK) and check Unity");
        PrintRow("init", "install Nebula into the Unity project in the current folder");
        Ui.Blank();
        Console.WriteLine("running locally");
        PrintRow("build", "build the player/worker executable with Unity");
        PrintRow("start", "run the mesh locally: SpacetimeDB, control plane, orchestrator, gateway, workers");
        PrintRow("stop", "stop the local mesh");
        PrintRow("status", "show the mesh (local, or --cloud)");
        PrintRow("logs", "show a role's log (orchestrator, gateway, w1, ...)");
        Ui.Blank();
        Console.WriteLine("deploying");
        PrintRow("config", "configure a deploy target (hetzner), Spacetime Maincloud, or the Unity editor path");
        PrintRow("deploy", "build, publish the control plane and run the mesh in the cloud");
        PrintRow("destroy", "delete the cloud mesh so nothing keeps billing");
        Ui.Blank();
        Console.WriteLine("other");
        PrintRow("version", "print the CLI version");
        PrintRow("help", "this text; `nebula <command> --help` for one command");
        Ui.Blank();
        Console.WriteLine("global options: --project <path>  --verbose/-v  --yes/-y");
        Ui.Blank();
        PrintNextStep(ctx);
        return 0;
    }

    private static void PrintRow(string name, string text) => Console.WriteLine($"  {name,-10} {text}");

    /// <summary>Every command with its usage, options and examples, in the order of the help text. Machine-readable, for the docs.</summary>
    private static void PrintJson()
    {
        var commands = Program.Commands.Select(c => new
        {
            name = c.Name,
            aliases = c.Aliases,
            summary = c.Summary,
            usage = c.Usage,
            details = c.Details?.Trim(),
            options = c.Options.Select(o => new { name = o.Name, @short = o.Short, hasValue = o.HasValue, valueName = o.ValueName, help = o.Help }),
            examples = c.Examples,
        });
        var doc = new
        {
            version = Platform.CliVersion,
            globalOptions = new[]
            {
                new { name = "project", @short = (string?)null, hasValue = true, valueName = (string?)"path", help = "run against another Nebula project instead of the current folder" },
                new { name = "verbose", @short = (string?)"v", hasValue = false, valueName = (string?)null, help = "print stack traces and the commands the CLI runs" },
                new { name = "yes", @short = (string?)"y", hasValue = false, valueName = (string?)null, help = "answer every confirmation with yes" },
            },
            commands,
        };
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(doc, Core.CliConfig.Json));
    }

    /// <summary>A one-line suggestion based on where the user is.</summary>
    private static void PrintNextStep(Context ctx)
    {
        try
        {
            string cwd = Directory.GetCurrentDirectory();
            if (SpacetimeCli.Path == null)
                Ui.Hint("SpacetimeDB is not installed yet: start with `nebula setup`");
            else if (NebulaProject.Find(cwd) is { } p)
                Ui.Hint(File.Exists(p.HostExecutable) ? $"project {Path.GetFileName(p.Root)}: `nebula start --open-ui` runs the mesh" : $"project {Path.GetFileName(p.Root)}: `nebula build` then `nebula start`");
            else if (NebulaProject.FindUnityRoot(cwd) != null)
                Ui.Hint("this is a Unity project without Nebula: `nebula init` installs it");
            else
                Ui.Hint("cd into a Unity project and run `nebula init`");
        }
        catch { }
    }

    public static void PrintCommandHelp(Command cmd)
    {
        Console.WriteLine($"nebula {cmd.Name} {cmd.Usage}".TrimEnd());
        Console.WriteLine("  " + cmd.Summary);
        if (cmd.Details != null)
        {
            Ui.Blank();
            foreach (var line in cmd.Details.Trim().Split('\n')) Console.WriteLine("  " + line.TrimEnd());
        }
        if (cmd.Options.Length > 0)
        {
            Ui.Blank();
            Console.WriteLine("options");
            foreach (var o in cmd.Options)
            {
                string flag = "--" + o.Name + (o.HasValue ? " <" + (o.ValueName ?? "value") + ">" : "");
                if (o.Short != null) flag = "-" + o.Short + ", " + flag;
                Console.WriteLine($"  {flag,-26} {o.Help}");
            }
        }
        if (cmd.Examples.Length > 0)
        {
            Ui.Blank();
            Console.WriteLine("examples");
            foreach (var e in cmd.Examples) Console.WriteLine("  " + e);
        }
    }
}

public sealed class VersionCommand : Command
{
    public override string Name => "version";
    public override string Summary => "Print the CLI version";

    public override int Run(Context ctx, ParsedArgs args)
    {
        Console.WriteLine($"nebula {Platform.CliVersion} ({Platform.Rid})");
        return 0;
    }
}
