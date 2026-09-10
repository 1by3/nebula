using Nebula.Cli.Commands;
using Nebula.Cli.Core;

namespace Nebula.Cli;

public static class Program
{
    public static readonly Command[] Commands =
    {
        new SetupCommand(),
        new InitCommand(),
        new BuildCommand(),
        new StartCommand(),
        new StopCommand(),
        new StatusCommand(),
        new LogsCommand(),
        new ConfigCommand(),
        new DeployCommand(),
        new DestroyCommand(),
        new VersionCommand(),
        new HelpCommand(),
    };

    public static int Main(string[] argv)
    {
        try
        {
            return Run(argv);
        }
        catch (CliError e)
        {
            Ui.Fail(e.Message);
            if (e.Hint != null) Ui.Hint(e.Hint);
            return e.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Ui.Warn("cancelled");
            return 130;
        }
        catch (Exception e)
        {
            Ui.Fail(e.Message);
            if (Context.VerboseEnabled) Console.Error.WriteLine(e);
            else Ui.Hint("re-run with --verbose for the stack trace");
            return 1;
        }
    }

    private static int Run(string[] argv)
    {
        // Global options can appear anywhere: `nebula --verbose start` or `nebula start --verbose`.
        var rest = new List<string>();
        var ctx = new Context();
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            switch (a)
            {
                case "--verbose": case "-v": ctx.Verbose = true; Context.VerboseEnabled = true; break;
                case "--yes": case "-y": ctx.Yes = true; break;
                case "--project":
                    if (i + 1 >= argv.Length) throw new CliError("--project needs a path");
                    ctx.ProjectOverride = argv[++i];
                    break;
                case "--version": case "-V": rest.Insert(0, "version"); break;
                default:
                    if (a.StartsWith("--project=")) ctx.ProjectOverride = a.Substring("--project=".Length);
                    else rest.Add(a);
                    break;
            }
        }

        if (rest.Count == 0) return new HelpCommand().Run(ctx, ParsedArgs.Empty);
        string name = rest[0];
        if (name is "--help" or "-h") return new HelpCommand().Run(ctx, ParsedArgs.Empty);

        var cmd = Find(name);
        if (cmd == null)
        {
            Ui.Fail($"unknown command '{name}'");
            Ui.Hint("`nebula --help` lists the commands");
            return 2;
        }
        var args = rest.Skip(1).ToArray();
        if (args.Contains("--help") || args.Contains("-h"))
        {
            HelpCommand.PrintCommandHelp(cmd);
            return 0;
        }
        var parsed = ParsedArgs.Parse(args, cmd);
        return cmd.Run(ctx, parsed);
    }

    public static Command? Find(string name) =>
        Commands.FirstOrDefault(c => c.Name == name || c.Aliases.Contains(name));
}
