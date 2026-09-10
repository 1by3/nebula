using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class SetupCommand : Command
{
    public override string Name => "setup";
    public override string Summary => "Install the prerequisites for running Nebula locally";
    public override string Usage => "[--check]";
    public override string? Details => @"
Installs what is missing and reports what it finds:
  spacetime   the SpacetimeDB CLI (control-plane server + module publishing), via its official installer
  dotnet      the .NET 10 SDK the control-plane module is compiled with, via dotnet-install into ~/.dotnet
  git, ssh    needed by `nebula init` (fetching the SDK) and `nebula deploy` (reaching the VMs)
  Unity       reports the editors Unity Hub has installed and whether they have Linux Dedicated Server support
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("check", false, "only report; install nothing"),
    };
    public override string[] Examples => new[] { "nebula setup", "nebula setup --check", "nebula setup --yes" };

    public override int Run(Context ctx, ParsedArgs args)
    {
        bool checkOnly = args.Has("check");
        bool allOk = true;

        Ui.Step("SpacetimeDB CLI");
        var stVersion = SpacetimeCli.Version();
        if (stVersion != null) Ui.Ok($"{stVersion} at {SpacetimeCli.Path}");
        else if (checkOnly) { Ui.Warn("not installed"); allOk = false; }
        else
        {
            if (Ui.Confirm("install the SpacetimeDB CLI with its official installer?", true, ctx.Yes))
            {
                InstallSpacetime();
                stVersion = SpacetimeCli.Version();
                if (stVersion != null) Ui.Ok($"{stVersion}");
                else { Ui.Warn("the installer finished but `spacetime` is still not found; open a new terminal and re-run `nebula setup`"); allOk = false; }
            }
            else allOk = false;
        }

        Ui.Step(".NET SDK (control-plane module builds need 10.0 or newer)");
        var dotnet = FindDotnetSdk();
        if (dotnet != null) Ui.Ok(dotnet);
        else if (checkOnly) { Ui.Warn("no .NET 10 SDK found"); allOk = false; }
        else
        {
            if (Ui.Confirm("install the .NET 10 SDK into ~/.dotnet with dotnet-install?", true, ctx.Yes))
            {
                InstallDotnet();
                dotnet = FindDotnetSdk();
                if (dotnet != null) Ui.Ok(dotnet);
                else { Ui.Warn("dotnet-install finished but no .NET 10 SDK is visible"); allOk = false; }
            }
            else allOk = false;
        }

        Ui.Step("tools");
        foreach (var (tool, why) in new[] { ("git", "fetching the Nebula SDK for `nebula init`"), ("ssh", "`nebula deploy`"), ("scp", "`nebula deploy`") })
        {
            var p = Shell.Which(tool);
            if (p != null) Ui.Ok($"{tool}: {p}");
            else Ui.Warn($"{tool} is not installed (needed for {why})");
        }

        Ui.Step("Unity");
        int editors = 0;
        foreach (var root in UnityLocator.HubRoots().Distinct())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var install in Directory.EnumerateDirectories(root).OrderBy(d => d))
            {
                string exe = UnityLocator.EditorInInstall(install);
                if (!File.Exists(exe)) continue;
                editors++;
                bool linux = UnityLocator.HasLinuxServerModule(exe);
                Ui.Ok($"{Path.GetFileName(install)}  {(linux ? "with" : "WITHOUT")} Linux Dedicated Server support  ({exe})");
            }
        }
        if (editors == 0)
        {
            Ui.Warn("no Unity Hub editors found; install one with Unity Hub, or `nebula config unity` to point at an editor");
            Ui.Info("cloud deploys need the 'Linux Dedicated Server Build Support' module on the editor your project uses");
        }

        if (!checkOnly && allOk)
        {
            ctx.Config.SetupCompletedAt = DateTime.UtcNow.ToString("o");
            ctx.SaveConfig();
        }
        Ui.Blank();
        if (allOk) Ui.Ok("ready. Next: `cd <your Unity project>` and `nebula init`");
        else Ui.Warn("some prerequisites are missing (see above)");
        return allOk ? 0 : 1;
    }

    private static void InstallSpacetime()
    {
        Ui.Info("running the SpacetimeDB installer...");
        int code;
        if (Platform.IsWindows)
            code = Shell.Stream("powershell", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "iwr https://windows.spacetimedb.com -useb | iex" });
        else
            code = Shell.Stream("/bin/sh", new[] { "-c", "curl -sSf https://install.spacetimedb.com | sh" });
        if (code != 0) throw new CliError($"the SpacetimeDB installer exited with {code}", "install it by hand from https://spacetimedb.com/install and re-run `nebula setup`");
    }

    private static void InstallDotnet()
    {
        Ui.Info("running dotnet-install (channel 10.0)...");
        int code;
        if (Platform.IsWindows)
            code = Shell.Stream("powershell", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "& { $(irm https://dot.net/v1/dotnet-install.ps1 | Out-String | iex) }; dotnet-install.ps1 -Channel 10.0" });
        else
            code = Shell.Stream("/bin/sh", new[] { "-c", "curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0" });
        if (code != 0) throw new CliError($"dotnet-install exited with {code}", "install the .NET 10 SDK from https://dotnet.microsoft.com/download and re-run `nebula setup`");
        Ui.Info("the SDK is in ~/.dotnet; the CLI adds it to PATH for module builds. Add it to your own PATH to use `dotnet` directly.");
    }

    /// <summary>Description of a .NET SDK >= 10 on PATH or in ~/.dotnet, or null.</summary>
    private static string? FindDotnetSdk()
    {
        foreach (var exe in new[] { Shell.Which("dotnet"), Path.Combine(Platform.Home, ".dotnet", "dotnet" + Platform.ExeSuffix) })
        {
            if (exe == null || !File.Exists(exe)) continue;
            var r = Shell.Capture(exe, new[] { "--list-sdks" });
            if (!r.Ok) continue;
            var best = r.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
                .Select(l => (line: l, major: int.TryParse(l.Split('.')[0], out var m) ? m : 0))
                .Where(x => x.major >= 10).OrderByDescending(x => x.line).FirstOrDefault();
            if (best.line != null) return $"SDK {best.line.Split(' ')[0]} ({exe})";
        }
        return null;
    }
}
