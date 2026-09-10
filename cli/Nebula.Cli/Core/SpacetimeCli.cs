using System.Net.Http;

namespace Nebula.Cli.Core;

/// <summary>The `spacetime` command-line tool (control-plane server, module publishing, Maincloud login).</summary>
public static class SpacetimeCli
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Path of the spacetime executable, looking in the folders its installer uses when PATH is stale.</summary>
    public static string? Path
    {
        get
        {
            var extra = Platform.IsWindows
                ? new[] { System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpacetimeDB") }
                : new[] { System.IO.Path.Combine(Platform.Home, ".local", "bin"), "/usr/local/bin" };
            return Shell.Which("spacetime", extra);
        }
    }

    public static string Require() =>
        Path ?? throw new CliError("the spacetime CLI is not installed", "run `nebula setup`");

    public static string? Version()
    {
        var p = Path;
        if (p == null) return null;
        var r = Shell.Capture(p, new[] { "--version" });
        var line = r.Stdout.Split('\n').FirstOrDefault(l => l.Contains("tool version"));
        return line?.Trim();
    }

    /// <summary>Environment for spacetime subprocesses: dotnet from ~/.dotnet when it is not on PATH (module builds need it).</summary>
    public static Dictionary<string, string> Env()
    {
        var env = new Dictionary<string, string>();
        if (Shell.Which("dotnet") == null)
        {
            string local = System.IO.Path.Combine(Platform.Home, ".dotnet");
            if (Directory.Exists(local))
            {
                env["DOTNET_ROOT"] = local;
                env["PATH"] = local + System.IO.Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            }
        }
        return env;
    }

    public static bool Ping(string uri)
    {
        try
        {
            var resp = Http.GetAsync(uri.TrimEnd('/') + "/v1/ping").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public static bool WaitForPing(string uri, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (Ping(uri)) return true;
            Thread.Sleep(1000);
        }
        return false;
    }

    /// <summary>Start a local SpacetimeDB server detached, logging into <paramref name="logDir"/>.</summary>
    public static void StartLocal(string dataDir, string listen, string logDir)
    {
        Directory.CreateDirectory(dataDir);
        Shell.Detach(Require(), new[] { "start", "--data-dir", dataDir, "--listen-addr", listen }, null, System.IO.Path.Combine(logDir, "spacetimedb.log"));
    }

    public static void Publish(string moduleDir, string server, string database, bool deleteData)
    {
        if (!Directory.Exists(moduleDir)) throw new CliError($"control-plane module not found at {moduleDir}");
        var args = new List<string> { "publish", "-s", server, database, "-y" };
        if (deleteData) args.Add("--delete-data");
        int code = Shell.Stream(Require(), args, moduleDir, Env());
        if (code != 0) throw new CliError($"spacetime publish failed (exit {code})",
            "the module builds with the .NET 10 SDK and the NativeAOT-LLVM toolchain (downloaded on first build); `nebula setup` checks both");
    }

    /// <summary>Is there a login token for the given server? Runs `spacetime login show`.</summary>
    public static bool IsLoggedIn(out string identity)
    {
        identity = "";
        var p = Path;
        if (p == null) return false;
        var r = Shell.Capture(p, new[] { "login", "show" });
        if (!r.Ok) return false;
        var text = r.Output;
        if (text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)) return false;
        identity = text.Split('\n').FirstOrDefault(l => l.Contains("identity", StringComparison.OrdinalIgnoreCase))?.Trim() ?? text.Split('\n')[0].Trim();
        return true;
    }

    public static void Login()
    {
        int code = Shell.Stream(Require(), new[] { "login" });
        if (code != 0) throw new CliError("spacetime login failed");
    }
}
