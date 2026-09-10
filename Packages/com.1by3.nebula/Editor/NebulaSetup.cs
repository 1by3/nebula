using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Nebula.Editor
{
    /// <summary>
    /// One-click setup from the Nebula menu. <b>Set Up Active Scene</b> turns the open scene into a working Nebula
    /// game scene: the <c>Resources/NebulaConfig</c> asset, a <c>Nebula</c> object with <see cref="NebulaBootstrap"/>
    /// and <see cref="NebulaDebugOverlay"/>, <see cref="NebulaConfig.GameScene"/>, the build settings (the scene is the
    /// boot scene), the project's <see cref="NebulaGameMode"/>, starter <see cref="Container"/> volumes around the
    /// level, and every networked prefab in <see cref="NebulaConfig.NetworkPrefabs"/>. Every step is idempotent, so
    /// running it again on a configured scene changes nothing and it doubles as a repair.
    /// </summary>
    public static class NebulaSetup
    {
        public const string ConfigAssetPath = "Assets/Resources/NebulaConfig.asset";
        public const string DocsUrl = "https://nebula.1by3.co/docs";

        /// <summary>A container footprint at least this long on an axis is split in two along it by the scene setup.</summary>
        public const float SplitThreshold = 40f;

        /// <summary>What a setup pass did, and what it could not do for you.</summary>
        public sealed class Report
        {
            public readonly List<string> Done = new List<string>();
            public readonly List<string> Warnings = new List<string>();

            public void Did(string line) => Done.Add(line);
            public void Warn(string line) => Warnings.Add(line);
        }

        // ---------------------------------------------------------------------------------------- menu

        [MenuItem("Nebula/Set Up Active Scene", priority = -100)]
        public static void SetUpActiveSceneMenu()
        {
            var report = SetUpActiveScene(createContainers: true);
            if (report != null) Show("Set Up Active Scene", report);
        }

        [MenuItem("Nebula/Select Config", priority = -80)]
        public static void SelectConfig()
        {
            var report = new Report();
            var config = EnsureConfig(report);
            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
            foreach (var line in report.Done) Debug.Log($"[nebula] {line}");
        }

        [MenuItem("Nebula/Documentation", priority = 200)]
        public static void OpenDocumentation() => Application.OpenURL(DocsUrl);

        [MenuItem("Nebula/Network Prefabs/Register All Network Prefabs", priority = 60)]
        public static void RegisterNetworkPrefabsMenu()
        {
            var report = new Report();
            RegisterNetworkPrefabs(EnsureConfig(report), report);
            Show("Register Network Prefabs", report);
        }

        [MenuItem("Nebula/Containers/Split Selected Container (2x2)", priority = 50)]
        public static void SplitSelectedMenu()
        {
            var created = new List<Object>();
            foreach (var c in SelectedContainers()) created.AddRange(Split(c, 2, 2));
            if (created.Count > 0) Selection.objects = created.ToArray();
        }

        [MenuItem("Nebula/Containers/Split Selected Container (2x2)", true)]
        private static bool SplitSelectedValidate() => SelectedContainers().Any();

        [MenuItem("GameObject/Nebula/Container", priority = 10)]
        public static void CreateContainerMenu(MenuCommand command)
        {
            var go = new GameObject("Container");
            var parent = command.context as GameObject;
            if (parent != null) GameObjectUtility.SetParentAndAlign(go, parent);
            else if (SceneView.lastActiveSceneView != null) go.transform.position = SceneView.lastActiveSceneView.pivot;
            var c = go.AddComponent<Container>();
            c.ContainerId = UniqueContainerId("container");
            go.name = c.ContainerId;
            Undo.RegisterCreatedObjectUndo(go, "Create Container");
            Selection.activeGameObject = go;
        }

        [MenuItem("GameObject/Nebula/Bootstrap", priority = 11)]
        public static void CreateBootstrapMenu()
        {
            var report = new Report();
            var bootstrap = EnsureBootstrap(SceneManager.GetActiveScene(), EnsureConfig(report), report);
            Selection.activeGameObject = bootstrap.gameObject;
        }

        // ---------------------------------------------------------------------------------------- scene setup

        /// <summary>
        /// Configure the active scene as the game scene (see the class summary). Returns null when the scene cannot be
        /// set up (a cell scene, or an untitled scene the user declined to save).
        /// </summary>
        public static Report SetUpActiveScene(bool createContainers)
        {
            var scene = SceneManager.GetActiveScene();
            if (WorldAssets.TryGetCellOf(scene, out var world, out var coord))
            {
                Dialog("Set Up Active Scene", $"'{scene.name}' is cell {coord} of world '{world.WorldName}'. Make the hub scene active and run the setup there.");
                return null;
            }
            if (string.IsNullOrEmpty(scene.path) && !SaveUntitled(scene)) return null;

            var report = new Report();
            var config = EnsureConfig(report);
            EnsureBootstrap(scene, config, report);
            bool isGameScene = ConfigureGameScene(scene, config, report);
            EnsureBuildSettings(scene.path, config, report);
            if (isGameScene)
            {
                EnsureGameMode(scene, report);
                if (createContainers)
                {
                    if (config.WorldManifest != null) report.Did("containers: the world manifest provides them (partitioned world)");
                    else EnsureContainers(scene, report);
                }
            }
            RegisterNetworkPrefabs(config, report);
            AssetDatabase.SaveAssetIfDirty(config);
            return report;
        }

        /// <summary>The config every role loads (<c>Resources/NebulaConfig</c>), created at <see cref="ConfigAssetPath"/> if missing.</summary>
        public static NebulaConfig EnsureConfig(Report report)
        {
            var config = FindConfig();
            if (config != null) return config;
            var stray = AssetDatabase.FindAssets("t:NebulaConfig").Select(AssetDatabase.GUIDToAssetPath).ToList();
            if (stray.Count > 0) report.Warn($"NebulaConfig asset(s) outside a Resources folder are never loaded at runtime: {string.Join(", ", stray)}");
            WorldAssets.EnsureFolder(Path.GetDirectoryName(ConfigAssetPath).Replace('\\', '/'));
            config = ScriptableObject.CreateInstance<NebulaConfig>();
            config.GameScene = "";
            AssetDatabase.CreateAsset(config, ConfigAssetPath);
            AssetDatabase.SaveAssets();
            report.Did($"created {ConfigAssetPath}");
            return config;
        }

        /// <summary>The asset <see cref="NebulaConfig.Load"/> returns at runtime, or null.</summary>
        public static NebulaConfig FindConfig() => Resources.Load<NebulaConfig>("NebulaConfig");

        /// <summary>The scene's <see cref="NebulaBootstrap"/> (created on a root <c>Nebula</c> object if missing), wired to <paramref name="config"/>, with a debug overlay.</summary>
        public static NebulaBootstrap EnsureBootstrap(Scene scene, NebulaConfig config, Report report)
        {
            var all = FindInScene<NebulaBootstrap>(scene);
            if (all.Count > 1) report.Warn($"{all.Count} NebulaBootstrap components in '{scene.name}'; only the first to wake survives");
            var bootstrap = all.FirstOrDefault();
            int before = report.Done.Count;
            if (bootstrap == null)
            {
                var go = scene.GetRootGameObjects().FirstOrDefault(r => r.name == "Nebula");
                if (go == null)
                {
                    go = new GameObject("Nebula");
                    if (go.scene != scene) SceneManager.MoveGameObjectToScene(go, scene);
                    Undo.RegisterCreatedObjectUndo(go, "Create Nebula bootstrap");
                }
                bootstrap = Undo.AddComponent<NebulaBootstrap>(go);
                report.Did($"added NebulaBootstrap on '{go.name}'");
            }
            if (bootstrap.transform.parent != null)
                report.Warn($"NebulaBootstrap on '{bootstrap.name}' is not a root object; DontDestroyOnLoad needs it at the root");
            if (bootstrap.Config != config)
            {
                Undo.RecordObject(bootstrap, "Assign NebulaConfig");
                bootstrap.Config = config;
                EditorUtility.SetDirty(bootstrap);
                report.Did("assigned NebulaConfig to the bootstrap");
            }
            if (bootstrap.GetComponent<NebulaDebugOverlay>() == null && FindInScene<NebulaDebugOverlay>(scene).Count == 0)
            {
                Undo.AddComponent<NebulaDebugOverlay>(bootstrap.gameObject);
                report.Did("added NebulaDebugOverlay (tick, RTT, container and worker readout)");
            }
            if (report.Done.Count > before) EditorSceneManager.MarkSceneDirty(scene);
            return bootstrap;
        }

        /// <summary>Point <see cref="NebulaConfig.GameScene"/> at <paramref name="scene"/>, asking before replacing another real scene. True if it is the game scene afterwards.</summary>
        private static bool ConfigureGameScene(Scene scene, NebulaConfig config, Report report)
        {
            if (config.GameScene == scene.name) return true;
            string current = config.GameScene;
            bool currentExists = !string.IsNullOrEmpty(current) && FindScenePath(current) != null;
            if (currentExists && !Confirm("Game scene",
                    $"NebulaConfig.GameScene is '{current}'. Every role loads that scene after boot.\n\nMake '{scene.name}' the game scene instead?",
                    $"Use '{scene.name}'", $"Keep '{current}'"))
            {
                report.Warn($"GameScene stays '{current}': '{scene.name}' acts as a boot scene and every role loads '{current}' after it (game mode and container checks skipped)");
                return false;
            }
            Undo.RecordObject(config, "Set GameScene");
            config.GameScene = scene.name;
            EditorUtility.SetDirty(config);
            report.Did($"NebulaConfig.GameScene = '{scene.name}'" + (string.IsNullOrEmpty(current) ? "" : $" (was '{current}')"));
            return true;
        }

        /// <summary>
        /// Put <paramref name="bootScenePath"/> in the build settings, enabled and first unless the first scene already
        /// carries a bootstrap, and make sure the game scene is in there too.
        /// </summary>
        private static void EnsureBuildSettings(string bootScenePath, NebulaConfig config, Report report)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            bool changed = false;
            var entry = scenes.FirstOrDefault(s => s.path == bootScenePath);
            if (entry == null)
            {
                entry = new EditorBuildSettingsScene(bootScenePath, true);
                scenes.Add(entry);
                changed = true;
                report.Did($"added {bootScenePath} to the build settings");
            }
            else if (!entry.enabled)
            {
                entry.enabled = true;
                changed = true;
                report.Did($"enabled {bootScenePath} in the build settings");
            }
            var first = scenes.FirstOrDefault(s => s.enabled);
            if (first != entry && (first == null || !SceneContains<NebulaBootstrap>(first.path)))
            {
                scenes.Remove(entry);
                scenes.Insert(0, entry);
                changed = true;
                report.Did($"made {bootScenePath} the first build scene (the one a player boots into)");
            }
            string gamePath = FindScenePath(config.GameScene);
            if (gamePath != null && gamePath != bootScenePath)
            {
                var game = scenes.FirstOrDefault(s => s.path == gamePath);
                if (game == null) { scenes.Add(new EditorBuildSettingsScene(gamePath, true)); changed = true; report.Did($"added game scene {gamePath} to the build settings"); }
                else if (!game.enabled) { game.enabled = true; changed = true; report.Did($"enabled game scene {gamePath} in the build settings"); }
            }
            if (changed) EditorBuildSettings.scenes = scenes.ToArray();
        }

        /// <summary>Add the project's game mode to the scene when there is exactly one concrete <see cref="NebulaGameMode"/> in the player assemblies.</summary>
        private static void EnsureGameMode(Scene scene, Report report)
        {
            var existing = FindInScene<NebulaGameMode>(scene);
            if (existing.Count > 1) report.Warn($"{existing.Count} game modes in '{scene.name}'; workers use whichever FindFirstObjectByType returns");
            if (existing.Count > 0) return;
            var types = GameModeTypes();
            if (types.Count == 0)
            {
                report.Warn("no NebulaGameMode in the project: subclass it (OnSpawnPlayer spawns the player), then run the setup again or add it to a 'Game' object. See the Spawning guide.");
                return;
            }
            if (types.Count > 1)
            {
                report.Warn($"several game modes to choose from ({string.Join(", ", types.Select(t => t.Name))}); add the right one to a 'Game' object");
                return;
            }
            var go = new GameObject("Game");
            if (go.scene != scene) SceneManager.MoveGameObjectToScene(go, scene);
            Undo.RegisterCreatedObjectUndo(go, "Add game mode");
            Undo.AddComponent(go, types[0]);
            EditorSceneManager.MarkSceneDirty(scene);
            report.Did($"added {types[0].Name} on 'Game' (assign its fields, e.g. the player prefab)");
        }

        /// <summary>Concrete <see cref="NebulaGameMode"/> types in player (non-editor, non-test) assemblies.</summary>
        public static List<Type> GameModeTypes()
        {
            var player = new HashSet<string>(CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Select(a => a.name));
            return TypeCache.GetTypesDerivedFrom<NebulaGameMode>()
                .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && player.Contains(t.Assembly.GetName().Name))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>No containers yet: author a starter grid around the level's geometry (one box, or 2 per axis on a large footprint).</summary>
        private static void EnsureContainers(Scene scene, Report report)
        {
            var containers = FindInScene<Container>(scene);
            if (containers.Count > 0)
            {
                foreach (var dup in containers.GroupBy(c => c.ContainerId).Where(g => g.Count() > 1))
                    report.Warn($"container id '{dup.Key}' is used {dup.Count()} times; ids must be unique");
                return;
            }
            var bounds = LevelBounds(scene);
            var size = new Vector3(Mathf.Max(20f, bounds.size.x + 4f), Mathf.Max(10f, bounds.size.y + 2f), Mathf.Max(20f, bounds.size.z + 4f));

            var root = scene.GetRootGameObjects().FirstOrDefault(r => r.name == "Containers");
            if (root == null)
            {
                root = new GameObject("Containers");
                if (root.scene != scene) SceneManager.MoveGameObjectToScene(root, scene);
                Undo.RegisterCreatedObjectUndo(root, "Create containers");
            }
            var go = new GameObject("area");
            Undo.RegisterCreatedObjectUndo(go, "Create containers");
            go.transform.SetParent(root.transform, false);
            go.transform.position = new Vector3(bounds.center.x, bounds.min.y - 1f, bounds.center.z);
            var box = go.AddComponent<Container>();
            box.ContainerId = UniqueContainerId(Slug(scene.name));
            go.name = box.ContainerId;
            box.Size = size;
            box.Center = new Vector3(0f, size.y * 0.5f, 0f);

            int nx = size.x >= SplitThreshold ? 2 : 1, nz = size.z >= SplitThreshold ? 2 : 1;
            int count = nx * nz > 1 ? Split(box, nx, nz).Count : 1;
            EditorSceneManager.MarkSceneDirty(scene);
            report.Did($"created {count} container(s) covering the level ({size.x:F0} x {size.z:F0} m) under 'Containers'" +
                       (count == 1 ? "; one worker simulates it all until you split it (Nebula > Containers)" : ""));
        }

        /// <summary>Union of the renderer and collider bounds in the scene; a 40 m square at the origin for an empty scene.</summary>
        public static Bounds LevelBounds(Scene scene)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.GetComponentInChildren<NebulaBootstrap>(true) != null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>())
                {
                    if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; }
                }
                foreach (var c in root.GetComponentsInChildren<Collider>())
                {
                    if (c.isTrigger) continue;
                    if (any) b.Encapsulate(c.bounds); else { b = c.bounds; any = true; }
                }
            }
            return any ? b : new Bounds(new Vector3(0f, 4f, 0f), new Vector3(40f, 8f, 40f));
        }

        // ---------------------------------------------------------------------------------------- containers

        /// <summary>
        /// Replace <paramref name="container"/> with <paramref name="nx"/> x <paramref name="nz"/> containers tiling its
        /// footprint (same parent, rotation, scale and height), ids <c>&lt;id&gt;-x-z</c>. Undoable. A container with
        /// children is left alone.
        /// </summary>
        public static List<Container> Split(Container container, int nx, int nz)
        {
            var result = new List<Container>();
            nx = Mathf.Max(1, nx);
            nz = Mathf.Max(1, nz);
            if (nx * nz == 1) { result.Add(container); return result; }
            var t = container.transform;
            if (t.childCount > 0)
            {
                Debug.LogWarning($"[nebula] container '{container.ContainerId}' has child objects; move them out before splitting it", container);
                return result;
            }
            var size = container.Size;
            var center = container.Center;
            for (int ix = 0; ix < nx; ix++)
            for (int iz = 0; iz < nz; iz++)
            {
                var offset = new Vector3(center.x - size.x * 0.5f + (ix + 0.5f) * size.x / nx, 0f, center.z - size.z * 0.5f + (iz + 0.5f) * size.z / nz);
                var go = new GameObject();
                Undo.RegisterCreatedObjectUndo(go, "Split container");
                if (go.scene != t.gameObject.scene) SceneManager.MoveGameObjectToScene(go, t.gameObject.scene);
                go.transform.SetParent(t.parent, false);
                go.transform.SetSiblingIndex(t.GetSiblingIndex() + result.Count + 1);
                go.transform.localPosition = t.localPosition + t.localRotation * Vector3.Scale(t.localScale, offset);
                go.transform.localRotation = t.localRotation;
                go.transform.localScale = t.localScale;
                var c = go.AddComponent<Container>();
                c.ContainerId = UniqueContainerId($"{container.ContainerId}-{ix}-{iz}", container);
                go.name = c.ContainerId;
                c.Size = new Vector3(size.x / nx, size.y, size.z / nz);
                c.Center = new Vector3(0f, center.y, 0f);
                result.Add(c);
            }
            var scene = t.gameObject.scene;
            Undo.DestroyObjectImmediate(container.gameObject);
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            return result;
        }

        private static IEnumerable<Container> SelectedContainers() => Selection.gameObjects.Select(g => g.GetComponent<Container>()).Where(c => c != null);

        /// <summary><paramref name="wanted"/>, or <c>wanted-2</c>, <c>wanted-3</c>... so no container in the open scenes (other than <paramref name="ignore"/>) has it.</summary>
        public static string UniqueContainerId(string wanted, Container ignore = null)
        {
            var used = new HashSet<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
                foreach (var c in FindInScene<Container>(SceneManager.GetSceneAt(i)))
                    if (c != ignore) used.Add(c.ContainerId);
            if (!used.Contains(wanted)) return wanted;
            for (int n = 2; ; n++) if (!used.Contains($"{wanted}-{n}")) return $"{wanted}-{n}";
        }

        private static string Slug(string name)
        {
            var s = new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            return string.IsNullOrEmpty(s) ? "area" : s;
        }

        // ---------------------------------------------------------------------------------------- network prefabs

        /// <summary>
        /// Append every prefab under Assets whose root has a <see cref="NetworkIdentity"/> and is not yet listed to
        /// <see cref="NebulaConfig.NetworkPrefabs"/>. Existing entries are never reordered or removed: the index is the
        /// prefab id on the wire. Returns how many were added.
        /// </summary>
        public static int RegisterNetworkPrefabs(NebulaConfig config, Report report)
        {
            var listed = new HashSet<GameObject>(config.NetworkPrefabs.Where(p => p != null));
            var missing = NetworkPrefabCandidates().Where(p => !listed.Contains(p)).ToList();
            if (missing.Count > 0)
            {
                Undo.RecordObject(config, "Register network prefabs");
                config.NetworkPrefabs.AddRange(missing);
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssetIfDirty(config);
                report.Did($"registered {missing.Count} network prefab(s): {string.Join(", ", missing.Select(p => p.name))}");
            }
            int nulls = config.NetworkPrefabs.Count(p => p == null);
            if (nulls > 0) report.Warn($"NetworkPrefabs has {nulls} empty slot(s) (kept, since indices are prefab ids)");
            foreach (var p in config.NetworkPrefabs.Where(p => p != null && p.GetComponent<NetworkIdentity>() == null).Distinct())
                report.Warn($"network prefab '{p.name}' has no NetworkIdentity on its root");
            return missing.Count;
        }

        /// <summary>Prefab assets under Assets whose root carries a <see cref="NetworkIdentity"/>.</summary>
        public static List<GameObject> NetworkPrefabCandidates()
        {
            string identityScript = ScriptPathOf(typeof(NetworkIdentity));
            var result = new List<GameObject>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // Dependencies come from the asset database's cache, which keeps big art libraries from being loaded one by one.
                if (identityScript != null && !AssetDatabase.GetDependencies(path, true).Contains(identityScript)) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent<NetworkIdentity>() != null) result.Add(prefab);
            }
            return result.OrderBy(p => AssetDatabase.GetAssetPath(p), StringComparer.Ordinal).ToList();
        }

        // ---------------------------------------------------------------------------------------- helpers

        /// <summary>Every <typeparamref name="T"/> in a loaded scene, inactive objects included.</summary>
        public static List<T> FindInScene<T>(Scene scene) where T : Component
        {
            var list = new List<T>();
            if (!scene.IsValid() || !scene.isLoaded) return list;
            foreach (var root in scene.GetRootGameObjects()) list.AddRange(root.GetComponentsInChildren<T>(true));
            return list;
        }

        /// <summary>Whether the scene at <paramref name="scenePath"/> uses a <typeparamref name="T"/> (read from the open scene, else from its file's dependencies).</summary>
        public static bool SceneContains<T>(string scenePath) where T : Component
        {
            var open = SceneManager.GetSceneByPath(scenePath);
            if (open.IsValid() && open.isLoaded) return FindInScene<T>(open).Count > 0;
            var deps = AssetDatabase.GetDependencies(scenePath, false);
            if (typeof(T) == typeof(NebulaGameMode)) return GameModeTypes().Select(ScriptPathOf).Any(p => p != null && deps.Contains(p));
            string script = ScriptPathOf(typeof(T));
            return script != null && deps.Contains(script);
        }

        /// <summary>Project path of the script defining <paramref name="type"/>, or null.</summary>
        public static string ScriptPathOf(Type type)
        {
            foreach (var script in MonoImporter.GetAllRuntimeMonoScripts())
                if (script != null && script.GetClass() == type) return AssetDatabase.GetAssetPath(script);
            return null;
        }

        /// <summary>Project path of the scene asset named <paramref name="sceneName"/> (build settings first), or null.</summary>
        public static string FindScenePath(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return null;
            var inBuild = EditorBuildSettings.scenes.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s.path) == sceneName);
            if (inBuild != null) return inBuild.path;
            return AssetDatabase.FindAssets($"t:Scene {sceneName}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == sceneName && p.StartsWith("Assets/"));
        }

        private static bool SaveUntitled(Scene scene)
        {
            if (Application.isBatchMode) return false;
            string path = EditorUtility.SaveFilePanelInProject("Save scene", "Game", "unity", "Nebula needs a saved scene: every role loads it by name.");
            return !string.IsNullOrEmpty(path) && EditorSceneManager.SaveScene(scene, path);
        }

        /// <summary>Log a report and summarise it in a dialog (the log only, in batchmode).</summary>
        public static void Show(string title, Report report)
        {
            foreach (var line in report.Done) Debug.Log($"[nebula] {title}: {line}");
            foreach (var line in report.Warnings) Debug.LogWarning($"[nebula] {title}: {line}");
            if (Application.isBatchMode) return;
            string body = report.Done.Count == 0 ? "Already set up; nothing changed." : string.Join("\n", report.Done.Select(d => "• " + d));
            if (report.Warnings.Count > 0) body += "\n\nNeeds your attention:\n" + string.Join("\n", report.Warnings.Select(w => "• " + w));
            EditorUtility.DisplayDialog(title, body, "OK");
        }

        internal static void Dialog(string title, string message)
        {
            if (Application.isBatchMode) Debug.LogWarning($"[nebula] {title}: {message}");
            else EditorUtility.DisplayDialog(title, message, "OK");
        }

        /// <summary>Ask the user; batchmode takes <paramref name="ok"/>.</summary>
        internal static bool Confirm(string title, string message, string ok, string cancel)
        {
            return Application.isBatchMode || EditorUtility.DisplayDialog(title, message, ok, cancel);
        }
    }
}
