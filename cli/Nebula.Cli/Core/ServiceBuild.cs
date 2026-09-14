namespace Nebula.Cli.Core;

public static class ServiceBuild
{
    public const string ManifestName = "nebula-services.json";
    public static string Executable(string buildDir, string role, bool linux = false) => Path.Combine(buildDir, "nebula-" + role + (linux ? "" : Platform.ExeSuffix));

    public static void Publish(NebulaProject project, bool linux)
    {
        string buildDir = linux ? project.LinuxBuildDir : project.HostBuildDir;
        if (!File.Exists(Path.Combine(buildDir, ManifestName)))
            throw new CliError("the exported service manifest is missing", "run `nebula build` to export the game's configuration and containers first");
        string dotnet = Shell.Which("dotnet", Path.Combine(Platform.Home, ".dotnet")) ?? throw new CliError("the .NET 10 SDK is required to build the services", "install the .NET 10 SDK and run `nebula build` again");
        string source = Path.Combine(project.PackageDir, "Services~");
        if (!Directory.Exists(source)) throw new CliError("this Nebula package has no standalone service sources", "update the project's Nebula package");
        string rid = linux ? "linux-x64" : Platform.Rid;
        foreach (string role in new[] { "Orchestrator", "Gateway" })
        {
            Ui.Step($"publishing the .NET {role.ToLowerInvariant()} ({rid})");
            Shell.StreamOrThrow(dotnet, new[]
            {
                "publish", Path.Combine(source, "Nebula." + role, "Nebula." + role + ".csproj"),
                "-c", "Release", "-r", rid, "--self-contained", "true", "-o", buildDir,
                "--artifacts-path", Path.Combine(project.TempDir, "nebula-services"), "--nologo",
            }, "service publish", project.Root);
        }
    }

    public static void Require(string buildDir)
    {
        foreach (string file in new[] { Executable(buildDir, "orchestrator"), Executable(buildDir, "gateway"), Path.Combine(buildDir, ManifestName) })
            if (!File.Exists(file)) throw new CliError($"missing service artifact: {file}", "run `nebula build`, or `nebula build services` if the manifest has already been exported");
    }
}
