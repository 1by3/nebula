namespace Nebula.Cli.Core;

/// <summary>
/// A checkout of the Nebula repository, for commands that need the package sources on disk (`nebula init --embed`).
/// Resolution order: --source, the NEBULA_SOURCE environment variable, the sdkSource in the CLI config (set by the
/// from-source installer), and finally a shallow clone of the repository under ~/.nebula-cli/sdk.
/// </summary>
public static class SdkSource
{
    public static string Resolve(Context ctx, string? explicitSource, string? gitRef)
    {
        foreach (var (candidate, origin) in new[]
                 {
                     (explicitSource, "--source"),
                     (Environment.GetEnvironmentVariable("NEBULA_SOURCE"), "NEBULA_SOURCE"),
                     (ctx.Config.SdkSource, "sdkSource in " + Platform.ConfigPath),
                 })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            string full = Path.GetFullPath(candidate);
            if (!NebulaProject.IsNebulaCheckout(full))
                throw new CliError($"{full} ({origin}) does not contain {Platform.PackagePathInRepo}/Runtime", "point it at a checkout of the Nebula repository");
            Ui.Verbose($"Nebula source: {full} ({origin})");
            return full;
        }
        return Clone(gitRef ?? DefaultRef());
    }

    /// <summary>The package folder inside a checkout.</summary>
    public static string PackageDir(string checkout) => Path.Combine(checkout, "Packages", Platform.PackageName);

    private static string Clone(string gitRef)
    {
        string git = Shell.Which("git") ?? throw new CliError("git is required to fetch the Nebula sources", "install git, or pass --source <path to a Nebula checkout>");
        string dir = Path.Combine(Platform.SdkDir, "nebula");
        if (Directory.Exists(Path.Combine(dir, ".git")))
        {
            Ui.Step($"updating the Nebula sources in {dir} ({gitRef})");
            var fetch = Shell.Capture(git, new[] { "fetch", "--depth", "1", "origin", gitRef }, dir);
            if (!fetch.Ok) throw new CliError($"git fetch failed: {fetch.Output}");
            var co = Shell.Capture(git, new[] { "checkout", "--force", "FETCH_HEAD" }, dir);
            if (!co.Ok) throw new CliError($"git checkout failed: {co.Output}");
        }
        else
        {
            Ui.Step($"cloning {Platform.RepoUrl} ({gitRef}) into {dir}");
            Directory.CreateDirectory(Platform.SdkDir);
            var clone = Shell.Capture(git, new[] { "clone", "--depth", "1", "--branch", gitRef, Platform.RepoUrl, dir });
            if (!clone.Ok)
            {
                // Tags for this CLI version may not exist yet; fall back to the default branch.
                if (gitRef != "main")
                {
                    Ui.Warn($"no '{gitRef}' in the repository; using main");
                    clone = Shell.Capture(git, new[] { "clone", "--depth", "1", "--branch", "main", Platform.RepoUrl, dir });
                }
                if (!clone.Ok) throw new CliError($"git clone failed: {clone.Output}");
            }
        }
        if (!NebulaProject.IsNebulaCheckout(dir)) throw new CliError($"{dir} has no {Platform.PackagePathInRepo}/Runtime after checkout");
        return dir;
    }

    /// <summary>
    /// The git ref `nebula init` pins the package to: the tag matching this CLI's version (v0.1.0), so a CLI and the
    /// package it installs always come from the same release. NEBULA_SDK_REF overrides it (development builds use main).
    /// </summary>
    public static string DefaultRef()
    {
        var env = Environment.GetEnvironmentVariable("NEBULA_SDK_REF");
        if (!string.IsNullOrEmpty(env)) return env;
        return "v" + Platform.CliVersion;
    }
}
