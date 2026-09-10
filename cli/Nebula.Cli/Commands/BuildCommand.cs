using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class BuildCommand : Command
{
    public override string Name => "build";
    public override string Summary => "Build the game executable every role runs from (orchestrator, gateway, workers, client)";
    public override string Usage => "[worker|client] [--linux] [--scratch] [--stop-mesh]";
    public override string? Details => @"
Every Nebula role is the same player build started with a different -nebula-role, so `worker` and `client`
build the same thing; the words are accepted for readability. Without --linux the build is for this machine
(Builds/Win64, Builds/MacOS or Builds/Linux64), which is what `nebula start` runs. --linux builds the Linux
dedicated server into Builds/Linux64 and packs Builds/nebula-linux.tar.gz for `nebula deploy`.

If the Unity Editor has the project open, the build runs from a mirrored copy under ~/.nebula-cli/scratch
(the Editor holds an exclusive lock on the project). Builds/unity-build*.log has the full Unity output.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("linux", false, "build the Linux dedicated server and pack the deploy tarball"),
        new OptionSpec("scratch", false, "always build from a mirrored copy of the project"),
        new OptionSpec("force", false, "build in place even if the Editor seems to hold the project"),
        new OptionSpec("stop-mesh", false, "stop a running local mesh first (it holds the previous build open)"),
    };
    public override string[] Examples => new[] { "nebula build", "nebula build worker --linux", "nebula build --stop-mesh" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        var project = ctx.RequireProject();
        if (args.Positional.Count > 0 && args.Positional[0] is not ("worker" or "client"))
            throw new CliError($"unknown build target '{args.Positional[0]}'", "nebula build [worker|client] [--linux]");
        var target = args.Has("linux") ? BuildTarget.Linux : BuildTarget.Host;
        UnityBuild.Build(ctx, project, new UnityBuild.Options(target, args.Has("scratch"), args.Has("force"), args.Has("stop-mesh")));
        Ui.Info(target == BuildTarget.Linux ? "ship it: nebula deploy" : "run it: nebula start --open-ui");
        return 0;
    }
}
