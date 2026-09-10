using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nebula.Cli.Core;

namespace Nebula.Cli.Commands;

public sealed class InitCommand : Command
{
    public override string Name => "init";
    public override string Summary => "Install Nebula into the Unity project in the current folder";
    public override string Usage => "[--ref <tag|branch>] [--embed [--source <path>]] [--force]";
    public override string? Details => @"
Run inside a Unity project. It adds the Nebula package (com.1by3.nebula, fetched by Unity from the Nebula
repository on GitHub and pinned to this CLI's release) and the SpacetimeDB SDK to Packages/manifest.json, lists the
package in `testables` so its tests show in the Test Runner, creates Assets/Resources/NebulaConfig.asset with the
defaults and writes nebula.json at the project root.

--embed copies the package's sources into Packages/com.1by3.nebula instead, so you can read and modify the middleware
in place. The sources come from --source, $NEBULA_SOURCE, the sdkSource in the CLI config (set when the CLI was
installed from a checkout), or a shallow git clone of the repository into ~/.nebula-cli/sdk.

Re-run with --force to move an existing install to this CLI's release (or to re-copy an embedded package).
NebulaConfig.asset and nebula.json are always kept.
";
    public override OptionSpec[] Options => new[]
    {
        new OptionSpec("ref", true, "git tag or branch of the package to install (default: this CLI's release tag)", "ref"),
        new OptionSpec("embed", false, "copy the package sources into Packages/com.1by3.nebula instead of referencing the repository"),
        new OptionSpec("source", true, "with --embed: a checkout of the Nebula repository to copy the package from", "path"),
        new OptionSpec("force", false, "replace an existing Nebula package entry or embedded copy (NebulaConfig.asset and nebula.json are kept)"),
    };
    public override string[] Examples => new[] { "nebula init", "nebula init --ref main", "nebula init --embed --source C:\\Dev\\nebula", "nebula init --force" };

    /// <summary>The SpacetimeDB Unity SDK the package's runtime is built against. Unity cannot resolve git dependencies
    /// declared by a package, so the CLI adds it to the project manifest itself.</summary>
    public const string SpacetimeSdkPackage = "com.clockworklabs.spacetimedbsdk";
    public const string SpacetimeSdkVersion = "https://github.com/clockworklabs/com.clockworklabs.spacetimedbsdk.git#v2.10.0";

    /// <summary>GUID of Runtime/Core/NebulaConfig.cs.meta; the same in every copy of the package.</summary>
    private const string ConfigScriptGuid = "f8639aa5b9a960fe84b71286ec12a096";

    public override int Run(Context ctx, ParsedArgs args)
    {
        if (args.Has("source") && !args.Has("embed"))
            throw new CliError("--source only applies with --embed", "nebula init --embed --source <path>");

        string where = Path.GetFullPath(ctx.ProjectOverride ?? Directory.GetCurrentDirectory());
        string? root = NebulaProject.IsUnityProject(where) ? where : NebulaProject.FindUnityRoot(where);
        if (root == null)
            throw new CliError($"{where} is not a Unity project (no Assets/ and ProjectSettings/ProjectVersion.txt)", "cd into your Unity project and run `nebula init` there");
        if (root != where) Ui.Info($"using the enclosing Unity project {root}");

        // The Nebula repository is itself a Unity project with the package embedded; it only needs nebula.json.
        if (NebulaProject.IsNebulaRepository(root))
        {
            Ui.Info("this is the Nebula repository itself; only nebula.json is (re)written");
            WriteProjectFile(root);
            return 0;
        }

        string? targetVersion = NebulaProject.ReadUnityVersion(root);
        Ui.Step($"installing Nebula into {root} (Unity {targetVersion})");

        string embeddedDir = Path.Combine(root, "Packages", Platform.PackageName);
        bool embedded = Directory.Exists(Path.Combine(embeddedDir, "Runtime"));
        bool force = args.Has("force");

        if (args.Has("embed"))
        {
            if (embedded && !force)
                throw new CliError($"Packages/{Platform.PackageName} already exists in this project", "pass --force to overwrite it with the sources");
            string checkout = SdkSource.Resolve(ctx, args.Get("source"), args.Get("ref"));
            string? sourceVersion = NebulaProject.ReadUnityVersion(checkout);
            if (sourceVersion != null && targetVersion != null && sourceVersion != targetVersion)
                Ui.Warn($"Nebula is developed on Unity {sourceVersion}; this project uses {targetVersion}");
            int files = FileSync.Copy(SdkSource.PackageDir(checkout), embeddedDir, excludeDir: name => name is "bin" or "obj" or ".git");
            Ui.Ok($"Packages/{Platform.PackageName} ({files} files, embedded)");
            // An embedded package shadows a manifest entry of the same name; drop the entry so there is one source of truth.
            EditManifest(root, deps => deps.Remove(Platform.PackageName));
        }
        else
        {
            if (embedded)
                throw new CliError($"this project has Nebula embedded at Packages/{Platform.PackageName}", "run `nebula init --embed --force` to refresh it, or delete that folder to switch to the package reference");
            string gitRef = args.Get("ref") ?? SdkSource.DefaultRef();
            string url = Platform.PackageGitUrl(gitRef);
            EditManifest(root, deps =>
            {
                string? current = deps[Platform.PackageName]?.ToString();
                if (current == url) { Ui.Ok($"Packages/manifest.json already references {Platform.PackageName} ({gitRef})"); return; }
                if (current != null && !force)
                    throw new CliError($"Packages/manifest.json already references {Platform.PackageName}: {current}", $"pass --force to move it to {gitRef}");
                deps[Platform.PackageName] = url;
                Ui.Ok($"Packages/manifest.json: {Platform.PackageName} = {url}");
            });
        }

        WriteConfigAsset(root);
        WriteProjectFile(root);

        Ui.Blank();
        Ui.Ok("done. Next steps:");
        Ui.Info("1. open the project in Unity once so it resolves the Nebula and SpacetimeDB packages");
        Ui.Info("2. add your game scene to Build Settings and set it as GameScene in Assets/Resources/NebulaConfig.asset;");
        Ui.Info("   list every networked prefab in NetworkPrefabs and add a NebulaGameMode to the scene");
        Ui.Info("3. nebula build, then nebula start --open-ui");
        return 0;
    }

    private static void WriteProjectFile(string root)
    {
        string file = Path.Combine(root, NebulaProject.FileName);
        if (File.Exists(file))
        {
            var p = NebulaProject.Load(root);
            p.File.Nebula = Platform.CliVersion;
            p.Save();
            Ui.Ok($"{NebulaProject.FileName} updated");
            return;
        }
        var pf = new ProjectFile();
        pf.Deploy.Database = $"nebula-{Slug(Path.GetFileName(root))}";
        NebulaProject.Create(root, pf);
        Ui.Ok($"{NebulaProject.FileName} written");
    }

    private static string Slug(string s)
    {
        var slug = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 0 ? slug : "game";
    }

    /// <summary>Applies <paramref name="edit"/> to the manifest's dependencies, makes sure the SpacetimeDB SDK is
    /// listed, and adds the package to `testables`. Writes only when something changed.</summary>
    private static void EditManifest(string root, Action<JsonObject> edit)
    {
        string path = Path.Combine(root, "Packages", "manifest.json");
        if (!File.Exists(path)) throw new CliError($"{path} not found");
        var manifest = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new CliError($"{path} is not valid JSON");
        string before = manifest.ToJsonString();
        var deps = manifest["dependencies"]?.AsObject();
        if (deps == null) { deps = new JsonObject(); manifest["dependencies"] = deps; }

        edit(deps);

        if (deps[SpacetimeSdkPackage]?.ToString() != SpacetimeSdkVersion)
        {
            deps[SpacetimeSdkPackage] = SpacetimeSdkVersion;
            Ui.Ok($"Packages/manifest.json: {SpacetimeSdkPackage} = {SpacetimeSdkVersion}");
        }
        var testables = manifest["testables"]?.AsArray();
        if (testables == null) { testables = new JsonArray(); manifest["testables"] = testables; }
        if (!testables.Any(t => t?.ToString() == Platform.PackageName)) testables.Add(Platform.PackageName);

        if (manifest.ToJsonString() != before)
            File.WriteAllText(path, manifest.ToJsonString(CliConfig.Json) + "\n");
    }

    /// <summary>Assets/Resources/NebulaConfig.asset with the class defaults and no prefabs, unless the project has one.</summary>
    private static void WriteConfigAsset(string root)
    {
        string dir = Path.Combine(root, "Assets", "Resources");
        string asset = Path.Combine(dir, "NebulaConfig.asset");
        if (File.Exists(asset)) { Ui.Ok("Assets/Resources/NebulaConfig.asset kept"); return; }
        Directory.CreateDirectory(dir);
        File.WriteAllText(asset, DefaultConfigAsset.Replace("\r\n", "\n"));
        Ui.Ok("Assets/Resources/NebulaConfig.asset written (defaults)");
    }

    private const string DefaultConfigAsset = @"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 0}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: " + ConfigScriptGuid + @", type: 3}
  m_Name: NebulaConfig
  m_EditorClassIdentifier: Nebula.Runtime::Nebula.NebulaConfig
  GameScene:
  WorldManifest: {fileID: 0}
  ClientLoadRadiusCells: 1
  WorkerLoadRingCells: 1
  OriginShiftThresholdCells: 4
  GatewayAddress: 127.0.0.1
  GatewayPort: 7000
  WorkerBasePort: 7100
  WorkerAdvertiseAddress: 127.0.0.1
  SpacetimeUri: http://127.0.0.1:3000
  SpacetimeDatabase: nebula
  UseLocalControlPlane: 0
  WorkerCount: 4
  WorkerExecutable:
  WorkerHost: process
  BuildArtifactDir:
  OrchestratorSpawnsGateway: 1
  WorkerHeartbeatSeconds: 1
  WorkerTimeoutSeconds: 5
  DeadWorkerReplaceDelaySeconds: 8
  WorkerDrainTimeoutSeconds: 10
  DashboardPort: 7080
  GhostBandMargin: 4
  HandoverHysteresis: 0.35
  GhostLingerSeconds: 2
  InterestNearRadius: 30
  InterestFarRadius: 80
  InterestMidDivisor: 4
  InterestFarDivisor: 12
  InterpolationDelayTicks: 3
  InputLeadMarginTicks: 2
  InputLeadTargetTicks: 3
  InputLeadMaxAdjustTicks: 30
  NetworkPrefabs: []
";
}
