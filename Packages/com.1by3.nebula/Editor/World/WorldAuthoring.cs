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
    /// <summary>Create, open, close, delete and populate cell scenes. Everything the World window does goes through here.</summary>
    public static class WorldAuthoring
    {
        /// <summary>
        /// Create the cell scene for <paramref name="coord"/> (cell-local: an empty scene with a <see cref="WorldCell"/>
        /// root at the origin), register it in the definition and the build settings, and open it placed.
        /// </summary>
        public static Scene CreateCell(WorldDefinition world, Vector3Int coord)
        {
            var existing = world.GetCell(coord);
            if (existing != null && File.Exists(existing.ScenePath)) return OpenCell(world, coord);

            string path = world.ScenePathFor(coord);
            WorldAssets.EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var root = new GameObject($"Cell {coord.x} {coord.y} {coord.z}");
            SceneManager.MoveGameObjectToScene(root, scene);
            var cell = root.AddComponent<WorldCell>();
            cell.Coord = coord;
            cell.CellSize = world.CellSize;
            EditorSceneManager.SaveScene(scene, path);
            world.SetCell(coord, path);
            EditorUtility.SetDirty(world);
            AssetDatabase.SaveAssets();
            WorldAssets.Invalidate();
            WorldAssets.AddCellScenesToBuildSettings(world);
            WorldEditorPlacement.Place(scene, world, coord);
            return scene;
        }

        public static Scene OpenCell(WorldDefinition world, Vector3Int coord)
        {
            var entry = world.GetCell(coord);
            if (entry == null) return CreateCell(world, coord);
            var open = SceneManager.GetSceneByPath(entry.ScenePath);
            if (open.IsValid() && open.isLoaded) return open;
            if (!File.Exists(entry.ScenePath))
            {
                Debug.LogWarning($"[world] cell {coord}: scene '{entry.ScenePath}' is missing; recreating it");
                world.RemoveCell(coord);
                return CreateCell(world, coord);
            }
            // sceneOpened places it (WorldEditorPlacement).
            return EditorSceneManager.OpenScene(entry.ScenePath, OpenSceneMode.Additive);
        }

        /// <summary>Close the cell scene, asking to save first. False if the user cancelled.</summary>
        public static bool CloseCell(WorldDefinition world, Vector3Int coord)
        {
            var entry = world.GetCell(coord);
            if (entry == null) return true;
            var scene = SceneManager.GetSceneByPath(entry.ScenePath);
            if (!scene.IsValid() || !scene.isLoaded) return true;
            if (SceneManager.sceneCount == 1)
            {
                Debug.LogWarning("[world] cannot close the only open scene; open the hub scene first");
                return false;
            }
            if (scene.isDirty && !EditorSceneManager.SaveModifiedScenesIfUserWantsTo(new[] { scene })) return false;
            return EditorSceneManager.CloseScene(scene, true);
        }

        /// <summary>Forget the cell and delete its scene asset (after confirmation).</summary>
        public static bool DeleteCell(WorldDefinition world, Vector3Int coord)
        {
            var entry = world.GetCell(coord);
            if (entry == null) return false;
            if (!EditorUtility.DisplayDialog("Delete cell", $"Delete cell {coord} and its scene\n{entry.ScenePath}?", "Delete", "Cancel")) return false;
            var scene = SceneManager.GetSceneByPath(entry.ScenePath);
            if (scene.IsValid() && scene.isLoaded && SceneManager.sceneCount > 1) EditorSceneManager.CloseScene(scene, true);
            WorldAssets.RemoveFromBuildSettings(entry.ScenePath);
            if (File.Exists(entry.ScenePath)) AssetDatabase.DeleteAsset(entry.ScenePath);
            world.RemoveCell(coord);
            EditorUtility.SetDirty(world);
            AssetDatabase.SaveAssets();
            WorldAssets.Invalidate();
            return true;
        }

        /// <summary>
        /// Move <paramref name="roots"/> (root objects of any open scene, typically a single-scene level being
        /// partitioned) into the cell scene for <paramref name="coord"/>, creating it if needed. Objects keep their
        /// editor positions; because the cell scene is placed at its editor offset, that is exactly cell-local plus
        /// offset, and the save hook stores them cell-local.
        /// </summary>
        public static int MoveIntoCell(WorldDefinition world, Vector3Int coord, IEnumerable<GameObject> roots)
        {
            var scene = OpenCell(world, coord);
            int moved = 0;
            foreach (var go in roots.ToList())
            {
                if (go == null || go.scene == scene) continue;
                var root = go.transform.root.gameObject;
                if (root.GetComponent<WorldCell>() != null) continue;
                Undo.MoveGameObjectToScene(root, scene, "Move into cell");
                moved++;
            }
            if (moved > 0) EditorSceneManager.MarkSceneDirty(scene);
            return moved;
        }

        /// <summary>Which cell (of <paramref name="world"/>, given the current editor origin) a root object's position falls in.</summary>
        public static Vector3Int CellOfEditorPosition(WorldDefinition world, Vector3 editorPosition)
        {
            return world.CoordOf(editorPosition, WorldEditorPlacement.EditorOriginCell);
        }

        /// <summary>
        /// Partition every non-cell root object of <paramref name="source"/> into cells by position (objects that
        /// span several cells go to the one holding their pivot). Returns the number of objects moved.
        /// </summary>
        public static int PartitionScene(WorldDefinition world, Scene source, IEnumerable<GameObject> keepInSource)
        {
            var keep = new HashSet<GameObject>(keepInSource ?? Enumerable.Empty<GameObject>());
            var byCell = new Dictionary<Vector3Int, List<GameObject>>();
            foreach (var root in source.GetRootGameObjects())
            {
                if (keep.Contains(root) || root.GetComponent<WorldCell>() != null) continue;
                var coord = CellOfEditorPosition(world, root.transform.position);
                if (!byCell.TryGetValue(coord, out var list)) byCell[coord] = list = new List<GameObject>();
                list.Add(root);
            }
            int moved = 0;
            foreach (var kv in byCell) moved += MoveIntoCell(world, kv.Key, kv.Value);
            return moved;
        }
    }
}
