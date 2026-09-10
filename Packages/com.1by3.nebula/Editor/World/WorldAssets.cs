using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nebula.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula.Editor
{
    /// <summary>Finds world definitions and manifests in the project and maps scenes back to cells.</summary>
    public static class WorldAssets
    {
        private static WorldDefinition[] _worlds;
        private static double _worldsScannedAt = -1;

        /// <summary>Every <see cref="WorldDefinition"/> in the project (cached for a second; call <see cref="Invalidate"/> after creating one).</summary>
        public static WorldDefinition[] AllWorlds()
        {
            if (_worlds == null || EditorApplication.timeSinceStartup - _worldsScannedAt > 1.0)
            {
                _worlds = AssetDatabase.FindAssets("t:WorldDefinition")
                    .Select(g => AssetDatabase.LoadAssetAtPath<WorldDefinition>(AssetDatabase.GUIDToAssetPath(g)))
                    .Where(w => w != null)
                    .ToArray();
                _worldsScannedAt = EditorApplication.timeSinceStartup;
            }
            return _worlds;
        }

        public static void Invalidate() => _worlds = null;

        public static WorldContainerManifest[] AllManifests()
        {
            return AssetDatabase.FindAssets("t:WorldContainerManifest")
                .Select(g => AssetDatabase.LoadAssetAtPath<WorldContainerManifest>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(m => m != null)
                .ToArray();
        }

        /// <summary>The manifest that targets <paramref name="world"/>, or null.</summary>
        public static WorldContainerManifest ManifestFor(WorldDefinition world)
        {
            return world == null ? null : AllManifests().FirstOrDefault(m => m.World == world);
        }

        /// <summary>Whether <paramref name="scene"/> is a cell scene of some world, and which cell.</summary>
        public static bool TryGetCellOf(Scene scene, out WorldDefinition world, out Vector3Int coord)
        {
            return TryGetCellOf(scene.path, out world, out coord);
        }

        public static bool TryGetCellOf(string scenePath, out WorldDefinition world, out Vector3Int coord)
        {
            world = null;
            coord = default;
            if (string.IsNullOrEmpty(scenePath)) return false;
            foreach (var w in AllWorlds())
            {
                foreach (var c in w.Cells)
                {
                    if (c.ScenePath == scenePath) { world = w; coord = c.Coord; return true; }
                }
            }
            return false;
        }

        public static Scene OpenSceneOf(WorldDefinition.CellEntry entry) => SceneManager.GetSceneByPath(entry.ScenePath);
        public static bool IsOpen(WorldDefinition.CellEntry entry) { var s = OpenSceneOf(entry); return s.IsValid() && s.isLoaded; }

        /// <summary>Create a world definition asset at <paramref name="assetPath"/>.</summary>
        public static WorldDefinition CreateWorld(string assetPath, string worldName, Vector3 cellSize)
        {
            var w = ScriptableObject.CreateInstance<WorldDefinition>();
            w.WorldName = worldName;
            w.CellSize = cellSize;
            w.SceneFolder = Path.GetDirectoryName(assetPath).Replace('\\', '/') + "/Cells";
            EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
            AssetDatabase.CreateAsset(w, assetPath);
            AssetDatabase.SaveAssets();
            Invalidate();
            return w;
        }

        public static WorldContainerManifest CreateManifest(string assetPath, WorldDefinition world)
        {
            var m = ScriptableObject.CreateInstance<WorldContainerManifest>();
            m.World = world;
            EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
            AssetDatabase.CreateAsset(m, assetPath);
            AssetDatabase.SaveAssets();
            return m;
        }

        /// <summary>Create every missing folder of an Assets/... path.</summary>
        public static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        /// <summary>Make sure every cell scene of <paramref name="world"/> is in the build settings (enabled). Returns how many were added.</summary>
        public static int AddCellScenesToBuildSettings(WorldDefinition world)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            int added = 0;
            foreach (var c in world.Cells)
            {
                if (string.IsNullOrEmpty(c.ScenePath)) continue;
                var existing = scenes.FirstOrDefault(s => s.path == c.ScenePath);
                if (existing == null) { scenes.Add(new EditorBuildSettingsScene(c.ScenePath, true)); added++; }
                else if (!existing.enabled) { existing.enabled = true; added++; }
            }
            if (added > 0) EditorBuildSettings.scenes = scenes.ToArray();
            return added;
        }

        public static void RemoveFromBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != scenePath).ToArray();
            if (scenes.Length != EditorBuildSettings.scenes.Length) EditorBuildSettings.scenes = scenes;
        }

        public static IEnumerable<WorldDefinition.CellEntry> CellsSorted(WorldDefinition world) => world.Cells.OrderBy(c => WorldGrid.Morton(c.Coord));
    }
}
