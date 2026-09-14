using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class BuildCommand : Command
{
    public override string Name => "build";
    public override string Summary => "Build the Unity player and standalone .NET services";
    public override string Usage => "[worker|client|services] [--linux] [--scratch] [--stop-mesh]";
    public override string? Details => @"
The default build compiles the Unity player, exports configuration and container geometry, and publishes
self-contained .NET orchestrator and gateway executables into the same build folder. `worker` and `client`
build the same artifacts. `services` republishes only the .NET services using the last exported game data;
run a full build after changing scenes, containers, settings, or persistence fields. Service builds require
the .NET 10 SDK. Running the published services does not require a separate .NET installation.

Without --linux, builds target this machine. --linux builds the Linux dedicated server and .NET services
into Builds/Linux64 and packs Builds/nebula-linux.tar.gz for `nebula deploy`.
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
        if (args.Positional.Count > 0 && args.Positional[0] is not ("worker" or "client" or "services"))
            throw new CliError($"unknown build target '{args.Positional[0]}'", "nebula build [worker|client|services] [--linux]");
        var target = args.Has("linux") ? BuildTarget.Linux : BuildTarget.Host;
        if (args.Positional.FirstOrDefault() == "services")
        {
            if (target == BuildTarget.Linux && !File.Exists(project.LinuxExecutable))
                throw new CliError("the Linux worker build is missing", "run `nebula build --linux` before repacking the services");
            string directory = target == BuildTarget.Linux ? project.LinuxBuildDir : project.HostBuildDir;
            if (Shell.RunningUnder(directory).Any())
            {
                if (!args.Has("stop-mesh")) throw new CliError("the mesh holds the service executables open", "run `nebula stop` first, or pass --stop-mesh");
                if (directory != project.HostBuildDir) throw new CliError("stop processes running from the Linux build before publishing services");
                LocalMesh.Stop(project, stopSpacetime: false);
            }
            ServiceBuild.Publish(project, target == BuildTarget.Linux);
            if (target == BuildTarget.Linux) UnityBuild.PackTarball(project.LinuxBuildDir, project.LinuxTarball);
            return 0;
        }
        UnityBuild.Build(ctx, project, new UnityBuild.Options(target, args.Has("scratch"), args.Has("force"), args.Has("stop-mesh")));
        Ui.Info(target == BuildTarget.Linux ? "to deploy this build, run: nebula deploy" : "to start this build, run: nebula start --open-ui");
        return 0;
    }
}
