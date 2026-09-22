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
        WarnAboutMissingGatewayExtension(buildDir);
    }

    public static void Require(string buildDir)
    {
        foreach (string file in new[] { Executable(buildDir, "orchestrator"), Executable(buildDir, "gateway"), Path.Combine(buildDir, ManifestName) })
            if (!File.Exists(file)) throw new CliError($"missing service artifact: {file}", "run `nebula build`, or `nebula build services` if the manifest has already been exported");
        WarnAboutMissingGatewayExtension(buildDir);
    }

    /// <summary>
    /// A game that configured a gateway extension has to put the assembly in the build folder itself (that folder
    /// is what ships and what deploys). Saying so here beats a gateway that exits on start-up, which is what
    /// happens next: a configured extension that cannot be loaded is deliberately fatal.
    /// </summary>
    public static void WarnAboutMissingGatewayExtension(string buildDir)
    {
        string configured;
        try
        {
            using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(buildDir, ManifestName)));
            if (!manifest.RootElement.TryGetProperty("Config", out var config) ||
                !config.TryGetProperty("GatewayExtension", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String) return;
            configured = value.GetString() ?? "";
        }
        catch { return; }
        if (configured.Length == 0 || Path.IsPathRooted(configured) || File.Exists(Path.Combine(buildDir, configured))) return;
        Ui.Warn($"NebulaConfig.GatewayExtension is '{configured}' but there is no such file in {buildDir}; " +
                "copy the built extension assembly there (it ships with the build and the deploy tarball) or the gateway will refuse to start");
    }
}
