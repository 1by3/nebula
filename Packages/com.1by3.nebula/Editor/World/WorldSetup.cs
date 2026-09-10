using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nebula.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula.Editor
{
    /// <summary>
    /// Nebula &gt; Set Up World Partition: makes the active scene the hub of a partitioned world in one pass. Runs the
    /// scene setup, finds or creates the <see cref="WorldDefinition"/> and its <see cref="WorldContainerManifest"/>,
    /// moves the level's root objects into cell scenes by position (cameras, directional lights, volumes, UI, the
    /// game mode, the bootstrap and anything selected stay in the hub), clears static batching on the moved content,
    /// saves, bakes the manifest, points <see cref="NebulaConfig.WorldManifest"/> at it and opens the World window.
    /// Idempotent like the scene setup: on a hub that is already partitioned it only rebakes.
    /// </summary>
    public static class WorldSetup
    {
        [MenuItem("Nebula/Set Up World Partition", priority = -99)]
        [MenuItem("Nebula/World/Set Up World Partition", priority = 30)]
        public static void SetUpMenu()
        {
            var report = SetUp();
            if (report == null) return;
            NebulaSetup.Show("Set Up World Partition", report);
            if (!Application.isBatchMode) WorldEditorWindow.Open();
        }

        /// <summary>Partition the active scene (see the class summary). Null when the scene setup could not run.</summary>
        public static NebulaSetup.Report SetUp()
        {
            var scene = SceneManager.GetActiveScene();
            var report = NebulaSetup.SetUpActiveScene(createContainers: false);
            if (report == null) return null;
            var config = NebulaSetup.FindConfig();
            if (config.GameScene != scene.name)
            {
                report.Warn($"the hub of a partitioned world must be the game scene ('{config.GameScene}' is); world setup stopped");
                return report;
            }

            var world = PickWorld(scene, config, report);
            var manifest = PickManifest(world, config, report);

            var roots = scene.GetRootGameObjects();
            var selected = new HashSet<GameObject>(Selection.gameObjects);
            var keep = roots.Where(r => selected.Contains(r) || StaysInHub(r)).ToList();
            var movable = roots.Where(r => !keep.Contains(r) && r.GetComponent<WorldCell>() == null).ToList();
            if (movable.Count > 0)
            {
                string kept = keep.Count == 0 ? "nothing" : string.Join(", ", keep.Select(k => k.name));
                int choice = Application.isBatchMode ? 0 : EditorUtility.DisplayDialogComplex("Set Up World Partition",
                    $"Move {movable.Count} root object(s) of '{scene.name}' into the {world.CellSize.x:F0} m cells of world '{world.WorldName}' by position?\n\n" +
                    $"Staying in the hub: {kept}.\nSelect any other roots that should stay before running this.\n\n" +
                    "The hub and the cell scenes are saved afterwards.",
                    "Partition and save", "Cancel", "Skip");
                if (choice == 1)
                {
                    report.Warn("partitioning cancelled; the manifest was not baked");
                    return report;
                }
                if (choice == 0) Partition(scene, world, keep, movable, report);
            }

            if (world.Cells.Count == 0)
            {
                var cell = WorldAuthoring.CreateCell(world, Vector3Int.zero);
                report.Did($"created cell (0,0,0): {cell.path}");
            }
            // Creating a cell scene makes it the active scene; new objects belong in the hub unless the user says otherwise.
            SceneManager.SetActiveScene(scene);

            var bake = WorldBaker.Bake(manifest);
            report.Did($"baked {AssetDatabase.GetAssetPath(manifest)}: {bake}");
            foreach (var w in bake.Warnings) report.Warn($"bake: {w}");

            if (config.WorldManifest != manifest)
            {
                Undo.RecordObject(config, "Use world manifest");
                config.WorldManifest = manifest;
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssetIfDirty(config);
                report.Did("NebulaConfig.WorldManifest set: the partition is on");
            }

            var hubContainers = NebulaSetup.FindInScene<Container>(scene);
            if (hubContainers.Count > 0)
                report.Warn($"{hubContainers.Count} Container(s) left in the hub scene are ignored in a partitioned world; move them into a cell and rebake");
            return report;
        }

        private static void Partition(Scene scene, WorldDefinition world, List<GameObject> keep, List<GameObject> movable, NebulaSetup.Report report)
        {
            int cleared = ClearBatchingStatic(movable);
            int cells = movable.Select(r => WorldAuthoring.CellOfEditorPosition(world, r.transform.position)).Distinct().Count();
            int moved = WorldAuthoring.PartitionScene(world, scene, keep);
            report.Did($"moved {moved} root object(s) into {cells} cell(s)");
            if (cleared > 0) report.Did($"cleared Batching Static on {cleared} object(s) (static batching bakes positions, and cells move)");

            EditorSceneManager.SaveScene(scene);
            var open = world.Cells.Select(WorldAssets.OpenSceneOf).Where(s => s.IsValid() && s.isLoaded && s.isDirty).ToArray();
            if (open.Length > 0) EditorSceneManager.SaveScenes(open);
            report.Did($"saved '{scene.name}' and {open.Length} cell scene(s)");
        }

        private static WorldDefinition PickWorld(Scene scene, NebulaConfig config, NebulaSetup.Report report)
        {
            if (config.WorldManifest != null && config.WorldManifest.World != null) return config.WorldManifest.World;
            var worlds = WorldAssets.AllWorlds();
            var named = worlds.FirstOrDefault(w => w.WorldName == scene.name);
            if (named != null) return named;
            if (worlds.Length == 1) return worlds[0];
            string dir = Path.GetDirectoryName(scene.path).Replace('\\', '/') + "/World";
            WorldAssets.EnsureFolder(dir); // GenerateUniqueAssetPath returns "" for a folder that does not exist
            string path = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{scene.name}.asset");
            var world = WorldAssets.CreateWorld(path, scene.name, new Vector3(256f, 256f, 256f));
            report.Did($"created world definition {path} (256 m cells, scenes in {world.SceneFolder})");
            return world;
        }

        private static WorldContainerManifest PickManifest(WorldDefinition world, NebulaConfig config, NebulaSetup.Report report)
        {
            if (config.WorldManifest != null && config.WorldManifest.World == world) return config.WorldManifest;
            var existing = WorldAssets.ManifestFor(world);
            if (existing != null) return existing;
            string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(world)).Replace('\\', '/');
            WorldAssets.EnsureFolder(dir);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{world.WorldName}Containers.asset");
            var manifest = WorldAssets.CreateManifest(path, world);
            report.Did($"created container manifest {path}");
            return manifest;
        }

        /// <summary>
        /// Roots that belong in the hub rather than in a cell: Nebula's own objects, the game mode, cameras, UI,
        /// post-processing volumes and objects holding only directional lights.
        /// </summary>
        public static bool StaysInHub(GameObject root)
        {
            if (root.GetComponentInChildren<NebulaBootstrap>(true) != null) return true;
            if (root.GetComponentInChildren<NebulaGameMode>(true) != null) return true;
            if (root.GetComponentInChildren<WorldStreamer>(true) != null) return true;
            if (root.GetComponentInChildren<Camera>(true) != null) return true;
            if (root.GetComponentInChildren<Canvas>(true) != null) return true;
            foreach (var c in root.GetComponents<Component>())
            {
                if (c == null) continue;
                string type = c.GetType().Name;
                if (type == "Volume" || type == "EventSystem") return true;
            }
            var lights = root.GetComponentsInChildren<Light>(true);
            return lights.Length > 0 && lights.All(l => l.type == LightType.Directional) && root.GetComponentInChildren<Renderer>(true) == null;
        }

        /// <summary>Clear <see cref="StaticEditorFlags.BatchingStatic"/> under <paramref name="roots"/> (undoable). Returns how many objects changed.</summary>
        public static int ClearBatchingStatic(IEnumerable<GameObject> roots)
        {
            int cleared = 0;
            foreach (var root in roots)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var flags = GameObjectUtility.GetStaticEditorFlags(t.gameObject);
                    if ((flags & StaticEditorFlags.BatchingStatic) == 0) continue;
                    Undo.RecordObject(t.gameObject, "Clear Batching Static");
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags & ~StaticEditorFlags.BatchingStatic);
                    cleared++;
                }
            }
            return cleared;
        }
    }
}
