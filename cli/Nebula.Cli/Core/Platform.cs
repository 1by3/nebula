using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nebula.Cli.Core;

/// <summary>Host OS facts and the few places the CLI keeps its own files.</summary>
public static class Platform
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public static string OsName => IsWindows ? "windows" : IsMac ? "macos" : "linux";

    /// <summary>.NET runtime identifier of this machine (win-x64, osx-arm64, ...), used to name release archives.</summary>
    public static string Rid
    {
        get
        {
            string os = IsWindows ? "win" : IsMac ? "osx" : "linux";
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                var a => a.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }
    }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>~/.nebula-cli (NEBULA_CLI_HOME overrides it, handy for tests).</summary>
    public static string CliHome =>
        Environment.GetEnvironmentVariable("NEBULA_CLI_HOME") is { Length: > 0 } h ? h : Path.Combine(Home, ".nebula-cli");

    public static string ConfigPath => Path.Combine(CliHome, "config.json");
    public static string SdkDir => Path.Combine(CliHome, "sdk");
    public static string ScratchDir => Path.Combine(CliHome, "scratch");
    public static string ExeSuffix => IsWindows ? ".exe" : "";

    public static string CliVersion =>
        typeof(Platform).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>The repository the SDK is cloned from when no local source is configured (NEBULA_REPO_URL overrides it).</summary>
    public static string RepoUrl =>
        Environment.GetEnvironmentVariable("NEBULA_REPO_URL") is { Length: > 0 } u ? u : DefaultRepoUrl;

    public const string DefaultRepoUrl = "https://github.com/1by3/nebula.git";

    /// <summary>The Unity package the middleware ships as, and where it lives inside the repository.</summary>
    public const string PackageName = "com.1by3.nebula";
    public const string PackagePathInRepo = "Packages/" + PackageName;

    /// <summary>Git URL Unity resolves the package from: the repository, the package folder inside it and a tag.</summary>
    public static string PackageGitUrl(string gitRef) => $"{RepoUrl}?path=/{PackagePathInRepo}#{gitRef}";

    /// <summary>Open a URL in the user's default browser.</summary>
    public static void OpenBrowser(string url)
    {
        try
        {
            if (IsWindows) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (IsMac) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch (Exception e)
        {
            Ui.Warn($"could not open a browser ({e.Message}); open {url} yourself");
        }
    }

    /// <summary>Set 0600 on files that hold secrets (no-op on Windows, where the profile folder is already private).</summary>
    public static void MakePrivate(string path)
    {
        if (IsWindows) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }
}
