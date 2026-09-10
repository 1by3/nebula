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
    /// Builds the <see cref="WorldContainerManifest"/>: one container per cell plus every <see cref="Container"/>
    /// authored inside the cell scenes, with cell-local poses, in wire order. Opens cell scenes that are not open
    /// (and closes them again) to read them.
    /// </summary>
    public static class WorldBaker
    {
        public sealed class Report
        {
            public int Cells;
            public int Containers;
            public readonly List<string> Warnings = new List<string>();
            public override string ToString() => $"{Cells} cells, {Containers} containers" + (Warnings.Count > 0 ? $", {Warnings.Count} warning(s)" : "");
        }

        public static Report Bake(WorldContainerManifest manifest)
        {
            var report = new Report();
            var world = manifest.World;
            if (world == null)
            {
                report.Warnings.Add("manifest has no WorldDefinition");
                return report;
            }
            var entries = new List<WorldContainerManifest.Entry>();
            var ids = new HashSet<string>();
            foreach (var cell in WorldAssets.CellsSorted(world))
            {
                report.Cells++;
                string cellId = WorldContainerManifest.CellContainerId(cell.Coord);
                ids.Add(cellId);
                entries.Add(new WorldContainerManifest.Entry
                {
                    Id = cellId, Cell = cell.Coord, IsCell = true, Size = world.CellSize, Center = Vector3.zero,
                });

                if (string.IsNullOrEmpty(cell.ScenePath) || !File.Exists(cell.ScenePath))
                {
                    report.Warnings.Add($"cell {cell.Coord}: scene '{cell.ScenePath}' is missing");
                    continue;
                }
                var scene = SceneManager.GetSceneByPath(cell.ScenePath);
                bool wasOpen = scene.IsValid() && scene.isLoaded;
                if (!wasOpen) scene = EditorSceneManager.OpenScene(cell.ScenePath, OpenSceneMode.Additive);
                try
                {
                    var root = WorldEditorPlacement.FindRoot(scene);
                    if (root == null) report.Warnings.Add($"cell {cell.Coord}: no WorldCell root object (content is assumed to be at its file position)");
                    var offset = root != null ? root.transform.position : Vector3.zero;
                    var half = world.CellSize * 0.5f;
                    foreach (var go in scene.GetRootGameObjects())
                    {
                        foreach (var c in go.GetComponentsInChildren<Container>(true))
                        {
                            if (!ids.Add(c.ContainerId))
                            {
                                report.Warnings.Add($"cell {cell.Coord}: duplicate container id '{c.ContainerId}' (skipped)");
                                continue;
                            }
                            var t = c.transform;
                            var local = t.position - offset;
                            var boxCenter = local + t.rotation * Vector3.Scale(c.Center, t.lossyScale);
                            if (Mathf.Abs(boxCenter.x) > half.x || Mathf.Abs(boxCenter.y) > half.y || Mathf.Abs(boxCenter.z) > half.z)
                                report.Warnings.Add($"cell {cell.Coord}: container '{c.ContainerId}' is centred outside its cell");
                            entries.Add(new WorldContainerManifest.Entry
                            {
                                Id = c.ContainerId, Cell = cell.Coord, IsCell = false,
                                LocalPosition = local, LocalRotation = t.rotation, LocalScale = t.lossyScale,
                                Size = c.Size, Center = c.Center,
                            });
                            report.Containers++;
                        }
                    }
                }
                finally
                {
                    if (!wasOpen) EditorSceneManager.CloseScene(scene, true);
                }
            }
            entries.Sort(WorldContainerManifest.Compare);
            manifest.Entries = entries;
            report.Containers += report.Cells;
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();
            int added = WorldAssets.AddCellScenesToBuildSettings(world);
            if (added > 0) Debug.Log($"[world] added {added} cell scene(s) to the build settings");
            foreach (var w in report.Warnings) Debug.LogWarning($"[world] bake: {w}");
            Debug.Log($"[world] baked {AssetDatabase.GetAssetPath(manifest)}: {report}");
            return report;
        }

        /// <summary>Bake the manifest of the config's world (the one <see cref="NebulaConfig.WorldManifest"/> points at).</summary>
        [MenuItem("Nebula/World/Bake Container Manifest", priority = 41)]
        public static void BakeConfigured()
        {
            var cfg = NebulaConfig.Load();
            var manifest = cfg.WorldManifest != null ? cfg.WorldManifest : WorldAssets.AllManifests().FirstOrDefault();
            if (manifest == null)
            {
                Debug.LogWarning("[world] no WorldContainerManifest in the project; create one from the World window");
                return;
            }
            Bake(manifest);
        }
    }
}
