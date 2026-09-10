using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace Nebula.Cli.Core;

/// <summary>Finding the Unity Editor that matches a project.</summary>
public static class UnityLocator
{
    /// <summary>Editor executable for <paramref name="version"/>, or a CliError explaining where it looked.</summary>
    public static string Find(CliConfig config, string version)
    {
        var tried = new List<string>();
        foreach (var c in Candidates(config, version))
        {
            if (File.Exists(c)) return c;
            tried.Add(c);
        }
        throw new CliError($"Unity {version} was not found",
            $"install it with Unity Hub, or point NEBULA_UNITY (or `nebula config unity`) at the editor. Looked in:\n  " + string.Join("\n  ", tried));
    }

    private static IEnumerable<string> Candidates(CliConfig config, string version)
    {
        var env = Environment.GetEnvironmentVariable("NEBULA_UNITY");
        if (!string.IsNullOrEmpty(env)) foreach (var c in Expand(env, version)) yield return c;
        if (!string.IsNullOrEmpty(config.Unity.Editor)) foreach (var c in Expand(config.Unity.Editor, version)) yield return c;
        foreach (var root in HubRoots())
            yield return EditorInHubRoot(root, version);
    }

    private static IEnumerable<string> Expand(string setting, string version)
    {
        // An executable, an editor install folder (…/6000.6.0f1), or a Hub root (…/Hub/Editor).
        if (File.Exists(setting)) { yield return setting; yield break; }
        yield return EditorInInstall(setting);
        yield return EditorInHubRoot(setting, version);
    }

    private static string EditorInHubRoot(string root, string version) => EditorInInstall(Path.Combine(root, version));

    public static string EditorInInstall(string install)
    {
        if (Platform.IsWindows) return Path.Combine(install, "Editor", "Unity.exe");
        if (Platform.IsMac) return Path.Combine(install, "Unity.app", "Contents", "MacOS", "Unity");
        return Path.Combine(install, "Editor", "Unity");
    }

    /// <summary>Folders that hold one subfolder per installed editor version.</summary>
    public static IEnumerable<string> HubRoots()
    {
        if (Platform.IsWindows) yield return @"C:\Program Files\Unity\Hub\Editor";
        else if (Platform.IsMac) yield return "/Applications/Unity/Hub/Editor";
        else yield return Path.Combine(Platform.Home, "Unity", "Hub", "Editor");

        // Unity Hub records a custom install location here.
        string hubConfig = Platform.IsWindows ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub")
            : Platform.IsMac ? Path.Combine(Platform.Home, "Library", "Application Support", "UnityHub")
            : Path.Combine(Platform.Home, ".config", "UnityHub");
        string f = Path.Combine(hubConfig, "secondaryInstallPath.json");
        string? secondary = null;
        try
        {
            if (File.Exists(f))
            {
                var text = File.ReadAllText(f).Trim();
                secondary = text.StartsWith("\"") ? JsonSerializer.Deserialize<string>(text) : text;
            }
        }
        catch { }
        if (!string.IsNullOrEmpty(secondary)) yield return secondary;
    }

    /// <summary>Data folder of an editor executable (holds PlaybackEngines).</summary>
    public static string DataDir(string editorExe)
    {
        string dir = Path.GetDirectoryName(editorExe)!;
        if (Platform.IsMac) return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dir))!)!, "PlaybackEngines");
        return Path.Combine(dir, "Data");
    }

    /// <summary>Does this editor have the Linux Dedicated Server module (what cloud workers are built with)?</summary>
    public static bool HasLinuxServerModule(string editorExe)
    {
        string engines = Platform.IsMac ? DataDir(editorExe) : Path.Combine(DataDir(editorExe), "PlaybackEngines");
        string variations = Path.Combine(engines, "LinuxStandaloneSupport", "Variations");
        return Directory.Exists(variations) && Directory.EnumerateDirectories(variations).Any(d => Path.GetFileName(d).Contains("server", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Is a Unity Editor process holding this project open?</summary>
    public static bool IsEditorOpen(string projectRoot)
    {
        string lockFile = Path.Combine(projectRoot, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile)) return false;
        if (Platform.IsWindows)
        {
            // The Editor keeps the lock file open with no sharing; a stale one from a crash opens fine.
            try
            {
                using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException) { return true; }
        }
        var ps = Shell.Capture("ps", new[] { "-eo", "args" });
        return ps.Ok && ps.Stdout.Split('\n').Any(l => l.Contains("Unity") && l.Contains(projectRoot));
    }
}

public enum BuildTarget { Host, Linux }

/// <summary>Batchmode player builds through Nebula's editor script (Packages/com.1by3.nebula/Editor/NebulaBuild.cs).</summary>
public static class UnityBuild
{
    public sealed record Options(BuildTarget Target, bool Scratch = false, bool Force = false, bool StopMesh = false);

    /// <summary>Relative "file:" package paths in the manifest are relative to Packages/; in a mirror they would point
    /// nowhere, so rewrite them to absolute paths next to the real project.</summary>
    private static void AbsolutizeFileDependencies(NebulaProject project, string mirroredManifest)
    {
        if (!File.Exists(mirroredManifest)) return;
        string text = File.ReadAllText(mirroredManifest);
        string rewritten = System.Text.RegularExpressions.Regex.Replace(text, "\"file:(\\.[^\"]*)\"", m =>
            "\"file:" + Path.GetFullPath(Path.Combine(project.PackagesDir, m.Groups[1].Value)).Replace('\\', '/') + "\"");
        if (rewritten != text) File.WriteAllText(mirroredManifest, rewritten);
    }

    /// <summary>Runs the build and returns the path of the executable (or tarball for Linux).</summary>
    public static string Build(Context ctx, NebulaProject project, Options options)
    {
        bool linux = options.Target == BuildTarget.Linux;
        string method = linux ? "Nebula.Editor.NebulaBuild.BuildLinuxServerBatch" : project.HostBuildMethod;
        string buildRel = linux ? Path.Combine("Builds", "Linux64") : Path.GetRelativePath(project.Root, project.HostBuildDir);
        string exe = linux ? project.LinuxExecutable : project.HostExecutable;
        string label = linux ? "Linux dedicated server" : project.HostBuildLabel;

        string unity = UnityLocator.Find(ctx.Config, project.UnityVersion);
        Ui.Step($"building the {label} with Unity {project.UnityVersion}");
        if (linux && !UnityLocator.HasLinuxServerModule(unity))
            throw new CliError("this Unity install has no Linux Dedicated Server module", "add 'Linux Dedicated Server Build Support' to the editor in Unity Hub");

        var scenes = project.EnabledScenes();
        if (scenes.Length == 0)
            throw new CliError("no scenes are enabled in Build Settings (ProjectSettings/EditorBuildSettings.asset)", "add your game scene in File > Build Profiles and re-run");
        Ui.Verbose("scenes: " + string.Join(", ", scenes));

        bool scratch = options.Scratch;
        if (UnityLocator.IsEditorOpen(project.Root) && !scratch && !options.Force)
        {
            Ui.Warn("the Unity Editor has this project open; building from a mirrored copy instead (--force builds in place)");
            scratch = true;
        }

        if (!linux)
        {
            var running = Shell.RunningUnder(project.HostBuildDir).ToList();
            if (running.Count > 0)
            {
                if (!options.StopMesh)
                    throw new CliError($"{running.Count} {project.File.Executable} process(es) are running and hold the previous build open", "run `nebula stop` first, or pass --stop-mesh");
                Ui.Info($"stopping {running.Count} running {project.File.Executable} process(es)");
                LocalMesh.Stop(project, stopSpacetime: false);
            }
        }

        string projectDir = project.Root;
        string? scratchDir = null;
        if (scratch)
        {
            scratchDir = Path.Combine(Platform.ScratchDir, Path.GetFileName(project.Root));
            Ui.Info($"mirroring the project to {scratchDir} (Library included; slow the first time)");
            var sw = Stopwatch.StartNew();
            FileSync.Skipped.Clear();
            foreach (var d in new[] { "Assets", "ProjectSettings", "Packages", "Library" })
            {
                string src = Path.Combine(project.Root, d);
                if (Directory.Exists(src)) FileSync.Mirror(src, Path.Combine(scratchDir, d));
            }
            // Locked files under Library are Unity caches it rebuilds (Bee build graphs, artifact DBs); anything
            // outside Library would make the build wrong, so stop there.
            var essential = FileSync.Skipped.Where(f => !f.StartsWith(Path.Combine(scratchDir, "Library"), StringComparison.OrdinalIgnoreCase)).ToList();
            if (essential.Count > 0)
                throw new CliError($"{essential.Count} project file(s) are held open by another process, e.g. {essential[0]}", "close whatever holds them and re-run");
            if (FileSync.Skipped.Count > 0)
                Ui.Warn($"{FileSync.Skipped.Count} locked Library cache file(s) were not mirrored (Unity regenerates them); --verbose lists them");
            AbsolutizeFileDependencies(project, Path.Combine(scratchDir, "Packages", "manifest.json"));
            string scratchLock = Path.Combine(scratchDir, "Temp", "UnityLockfile");
            if (File.Exists(scratchLock)) File.Delete(scratchLock);
            Ui.Verbose($"mirrored in {sw.Elapsed.TotalSeconds:F0}s");
            projectDir = scratchDir;
        }

        string log = Path.Combine(project.BuildsDir, linux ? "unity-build-linux.log" : "unity-build.log");
        Directory.CreateDirectory(project.BuildsDir);
        File.Delete(log);
        Ui.Info($"log: {log}");
        var args = new[] { "-batchmode", "-nographics", "-quit", "-projectPath", projectDir, "-executeMethod", method, "-logFile", log };
        var psi = new ProcessStartInfo(unity) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var timer = Stopwatch.StartNew();
        using var proc = Process.Start(psi) ?? throw new CliError("could not start Unity");
        using var tail = new LogTail(log, line => line.Contains("[nebula]") || line.Contains("error CS") || line.Contains("Build failed", StringComparison.OrdinalIgnoreCase));
        proc.WaitForExit();
        tail.Flush();
        if (proc.ExitCode != 0)
        {
            Ui.Fail($"Unity exited with {proc.ExitCode} after {timer.Elapsed.TotalSeconds:F0}s. Last lines of the log:");
            if (File.Exists(log)) foreach (var l in File.ReadLines(log).TakeLast(30)) Console.Error.WriteLine("    " + l);
            throw new CliError("build failed", $"the full log is at {log}");
        }

        if (scratchDir != null)
        {
            Ui.Info($"copying the build back into {buildRel}");
            FileSync.Mirror(Path.Combine(scratchDir, buildRel), Path.Combine(project.Root, buildRel), name => name == "Logs");
        }
        if (!File.Exists(exe)) throw new CliError($"the build reported success but {exe} is missing");
        Ui.Ok($"built {exe} in {timer.Elapsed.TotalSeconds:F0}s");

        if (linux)
        {
            Ui.Info($"packing {project.LinuxTarball}");
            PackTarball(project.LinuxBuildDir, project.LinuxTarball);
            Ui.Ok($"{Path.GetFileName(project.LinuxTarball)} ({new FileInfo(project.LinuxTarball).Length / 1024 / 1024} MB)");
            return project.LinuxTarball;
        }
        return exe;
    }

    /// <summary>One flat tarball of the Linux build (no logs), extracted into /opt/nebula on every VM.</summary>
    public static void PackTarball(string buildDir, string tarball)
    {
        File.Delete(tarball);
        using var file = File.Create(tarball);
        using var gz = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gz, TarEntryFormat.Gnu, leaveOpen: false);
        foreach (var f in Directory.EnumerateFiles(buildDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(buildDir, f).Replace('\\', '/');
            if (rel.StartsWith("Logs/") || rel.EndsWith(".log")) continue;
            var entry = new GnuTarEntry(TarEntryType.RegularFile, rel)
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                ModificationTime = File.GetLastWriteTimeUtc(f),
            };
            using var data = File.OpenRead(f);
            entry.DataStream = data;
            tar.WriteEntry(entry);
        }
    }

    /// <summary>Follows a log file while a process runs and prints the lines a filter selects.</summary>
    private sealed class LogTail : IDisposable
    {
        private readonly string _path;
        private readonly Func<string, bool> _filter;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        private long _offset;

        public LogTail(string path, Func<string, bool> filter)
        {
            _path = path;
            _filter = filter;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        private void Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                Read();
                Thread.Sleep(500);
            }
        }

        private void Read()
        {
            try
            {
                if (!File.Exists(_path)) return;
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length <= _offset) return;
                fs.Seek(_offset, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    if (_filter(line)) Ui.Info(line.Trim());
                _offset = fs.Length;
            }
            catch { }
        }

        public void Flush() { _cts.Cancel(); _thread.Join(2000); Read(); }
        public void Dispose() => _cts.Cancel();
    }
}
