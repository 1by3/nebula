using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nebula.Cli.Core;

/// <summary>nebula.json at the root of a Unity project: what `nebula init` wrote and what start/deploy read.</summary>
public sealed class ProjectFile
{
    public string Nebula { get; set; } = Platform.CliVersion;
    /// <summary>Base name of the player executable NebulaBuild produces (Nebula.exe / Nebula.x86_64 / Nebula.app).</summary>
    public string Executable { get; set; } = "Nebula";
    public MeshSettings Mesh { get; set; } = new();
    public DeploySettings Deploy { get; set; } = new();

    public sealed class MeshSettings
    {
        public int Workers { get; set; } = 4;
        /// <summary>The fewest workers autoscaling leaves running. Null = a fixed mesh of <see cref="Workers"/>; 0 allows scaling to zero.</summary>
        public int? MinWorkers { get; set; }
        /// <summary>The most workers the orchestrator may run. Null = a fixed mesh of <see cref="Workers"/>.</summary>
        public int? MaxWorkers { get; set; }
        public int Npcs { get; set; } = 0;
        /// <summary>How long a retired worker stays in the idle pool, ready to come back without a boot. 0 = the host decides (a local process is killed at once; a cloud VM is kept for the hour it is billed).</summary>
        public double IdlePoolSeconds { get; set; } = 0;
        public int DashboardPort { get; set; } = 7080;
        public int GatewayPort { get; set; } = 7000;
        /// <summary>Where the local orchestrator keeps the control plane and saved entities (sqlite:&lt;file&gt;, postgres://..., memory). Null = Library/Nebula/nebula.db.</summary>
        public string? Database { get; set; }
    }

    public sealed class DeploySettings
    {
        public string Target { get; set; } = "hetzner";
        /// <summary>Label every cloud resource of this mesh carries; one mesh per provider project.</summary>
        public string MeshName { get; set; } = "nebula";
        /// <summary>Where the deployed orchestrator keeps the control plane and saved entities: postgres://... or sqlite:&lt;file on the VM&gt;. Null = the CLI config (`nebula config database`), else SQLite on the orchestrator VM.</summary>
        public string? Database { get; set; }
        public int Workers { get; set; } = 4;
        public int Npcs { get; set; } = 0;
        public string? WorkerType { get; set; }
        public string? OrchestratorType { get; set; }
        public string? Location { get; set; }
    }
}

/// <summary>A Unity project with Nebula installed, and the paths the CLI needs inside it.</summary>
public sealed class NebulaProject
{
    public const string FileName = "nebula.json";

    public string Root { get; }
    public ProjectFile File { get; }
    public string FilePath => Path.Combine(Root, FileName);
    public string Assets => Path.Combine(Root, "Assets");
    public string PackagesDir => Path.Combine(Root, "Packages");
    public string ManifestPath => Path.Combine(PackagesDir, "manifest.json");
    /// <summary>Packages/com.1by3.nebula when the package is embedded in this project.</summary>
    public string EmbeddedPackageDir => Path.Combine(PackagesDir, Platform.PackageName);
    /// <summary>The Nebula package as Unity sees it: embedded, a file: reference, or the git checkout in Library/PackageCache.</summary>
    public string PackageDir => FindPackageDir() ?? throw new CliError("the Nebula package is not resolved in this project", "open the project in Unity once so it fetches com.1by3.nebula, or run `nebula init --embed`");
    public string BuildsDir => Path.Combine(Root, "Builds");
    public string LinuxBuildDir => Path.Combine(BuildsDir, "Linux64");
    public string LinuxExecutable => Path.Combine(LinuxBuildDir, File.Executable + ".x86_64");
    public string LinuxTarball => Path.Combine(BuildsDir, "nebula-linux.tar.gz");
    /// <summary>The web client build; the gateway serves it from here (a Web folder beside the host build folder).</summary>
    public string WebBuildDir => Path.Combine(BuildsDir, "Web");
    public string WebIndex => Path.Combine(WebBuildDir, "index.html");
    public string TempDir => Path.Combine(Root, "Temp");
    public string CliStateDir => Path.Combine(TempDir, "nebula-cli");
    public string MeshStateFile => Path.Combine(CliStateDir, "mesh.json");
    public string UnityVersion { get; }

    /// <summary>Build folder and executable of the build the local mesh runs on this OS.</summary>
    public string HostBuildDir => Path.Combine(BuildsDir, Platform.IsWindows ? "Win64" : Platform.IsMac ? "MacOS" : "Linux64");

    public string HostExecutable
    {
        get
        {
            if (Platform.IsWindows) return Path.Combine(HostBuildDir, File.Executable + ".exe");
            if (Platform.IsLinux) return Path.Combine(HostBuildDir, File.Executable + ".x86_64");
            // macOS: the binary inside the bundle is named after productName, so look it up.
            string macos = Path.Combine(HostBuildDir, File.Executable + ".app", "Contents", "MacOS");
            if (Directory.Exists(macos))
            {
                var bin = Directory.GetFiles(macos).FirstOrDefault();
                if (bin != null) return bin;
            }
            return Path.Combine(macos, File.Executable);
        }
    }

    public string HostLogsDir => Path.Combine(HostBuildDir, "Logs");
    public string HostBuildMethod => Platform.IsWindows ? "Nebula.Editor.NebulaBuild.BuildWindowsBatch"
        : Platform.IsMac ? "Nebula.Editor.NebulaBuild.BuildMacBatch"
        : "Nebula.Editor.NebulaBuild.BuildLinuxServerBatch";
    public string HostBuildLabel => Platform.IsWindows ? "Windows player" : Platform.IsMac ? "macOS player" : "Linux dedicated server";

    private NebulaProject(string root, ProjectFile file)
    {
        Root = root;
        File = file;
        UnityVersion = ReadUnityVersion(root) ?? "unknown";
    }

    public static bool IsUnityProject(string dir) =>
        Directory.Exists(Path.Combine(dir, "Assets")) && System.IO.File.Exists(Path.Combine(dir, "ProjectSettings", "ProjectVersion.txt"));

    /// <summary>True when <paramref name="dir"/> is a checkout of the Nebula repository (has the package with its runtime).</summary>
    public static bool IsNebulaCheckout(string dir) => Directory.Exists(Path.Combine(dir, "Packages", Platform.PackageName, "Runtime"));

    /// <summary>The Nebula repository itself: the package plus the CLI sources. Its own project needs no install.</summary>
    public static bool IsNebulaRepository(string dir) => IsNebulaCheckout(dir) && Directory.Exists(Path.Combine(dir, "cli", "Nebula.Cli"));

    public string? FindPackageDir()
    {
        if (Directory.Exists(Path.Combine(EmbeddedPackageDir, "Runtime"))) return EmbeddedPackageDir;
        // "file:<path>" dependencies are relative to the Packages folder (absolute paths work too).
        var m = Regex.Match(ReadManifest(), $@"""{Regex.Escape(Platform.PackageName)}""\s*:\s*""file:([^""]+)""");
        if (m.Success)
        {
            string p = Path.GetFullPath(Path.Combine(PackagesDir, m.Groups[1].Value));
            if (Directory.Exists(Path.Combine(p, "Runtime"))) return p;
        }
        string cache = Path.Combine(Root, "Library", "PackageCache");
        if (Directory.Exists(cache))
            return Directory.GetDirectories(cache, Platform.PackageName + "@*").OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Runtime")));
        return null;
    }

    private string ReadManifest() => System.IO.File.Exists(ManifestPath) ? System.IO.File.ReadAllText(ManifestPath) : "";

    /// <summary>Nearest enclosing Unity project root, or null.</summary>
    public static string? FindUnityRoot(string start)
    {
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (IsUnityProject(d.FullName)) return d.FullName;
        return null;
    }

    /// <summary>Nearest enclosing directory with nebula.json (that is also a Unity project), or null.</summary>
    public static NebulaProject? Find(string start)
    {
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
        {
            string f = Path.Combine(d.FullName, FileName);
            if (System.IO.File.Exists(f)) return Load(d.FullName);
        }
        return null;
    }

    public static NebulaProject Load(string root)
    {
        string f = Path.Combine(root, FileName);
        ProjectFile file;
        try { file = JsonSerializer.Deserialize<ProjectFile>(System.IO.File.ReadAllText(f), CliConfig.Json) ?? new ProjectFile(); }
        catch (JsonException e) { throw new CliError($"{f} is not valid JSON: {e.Message}"); }
        return new NebulaProject(root, file);
    }

    public static NebulaProject Create(string root, ProjectFile file)
    {
        var p = new NebulaProject(root, file);
        p.Save();
        return p;
    }

    public void Save() =>
        System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(File, CliConfig.Json) + Environment.NewLine);

    public static string? ReadUnityVersion(string root)
    {
        string f = Path.Combine(root, "ProjectSettings", "ProjectVersion.txt");
        if (!System.IO.File.Exists(f)) return null;
        var m = Regex.Match(System.IO.File.ReadAllText(f), @"m_EditorVersion:\s*(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Scenes enabled in Build Settings; NebulaBuild refuses to build without one.</summary>
    public string[] EnabledScenes()
    {
        string f = Path.Combine(Root, "ProjectSettings", "EditorBuildSettings.asset");
        if (!System.IO.File.Exists(f)) return Array.Empty<string>();
        var scenes = new List<string>();
        bool enabled = false;
        foreach (var raw in System.IO.File.ReadLines(f))
        {
            string line = raw.Trim();
            if (line.StartsWith("- enabled:")) enabled = line.EndsWith("1");
            else if (line.StartsWith("path:") && enabled) scenes.Add(line.Substring(5).Trim());
        }
        return scenes.ToArray();
    }

    /// <summary>Default database of a deployed mesh: a SQLite file on the orchestrator VM.</summary>
    public const string DefaultDeployDatabase = "sqlite:/opt/nebula/data/nebula.db";

    /// <summary>Database URL of the deployed mesh: NEBULA_DATABASE_URL, else nebula.json deploy.database, else the CLI config, else SQLite on the VM.</summary>
    public string DeployDatabase(CliConfig config) =>
        Environment.GetEnvironmentVariable("NEBULA_DATABASE_URL") is { Length: > 0 } env ? env
        : DatabaseOrNull(File.Deploy.Database, "deploy") ?? CliConfig.DatabaseSettings.ResolveUrl(config.Database) ?? DefaultDeployDatabase;

    /// <summary>Database URL of the local mesh: nebula.json mesh.database, else a SQLite file under Library (kept across builds).</summary>
    public string LocalDatabase => DatabaseOrNull(File.Mesh.Database, "mesh") ?? "sqlite:" + Path.Combine(Root, "Library", "Nebula", "nebula.db");

    /// <summary>
    /// Releases before 0.1.0-alpha.15 kept a SpacetimeDB database <i>name</i> under the same key. A value without a
    /// scheme is one of those: it is ignored, with a hint, so an old nebula.json keeps working.
    /// </summary>
    private string? DatabaseOrNull(string? value, string section)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains(':') || value.Equals("memory", StringComparison.OrdinalIgnoreCase)) return value;
        Ui.Warn($"{FilePath}: {section}.database = \"{value}\" is a database name from an older release and is ignored (use sqlite:<file> or postgres://...; delete the key to silence this)");
        return null;
    }
}
