using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula.Editor
{
    /// <summary>
    /// One-off upgrades of a project's assets to the current package. Each item says what it changed in the Console.
    /// </summary>
    public static class NebulaMigrate
    {
#pragma warning disable CS0618 // DynamicContainer is obsolete: removing it is the point
        /// <summary>
        /// Strip the obsolete <see cref="DynamicContainer"/> from every prefab in the project and every open scene. A
        /// <see cref="Container"/> on an entity's root is carried by the entity without it (docs/container-tree.md D1).
        /// Removing it renumbers the entity's behaviours, so rebuild every client and server afterwards. A component that
        /// another script requires (<c>[RequireComponent(typeof(DynamicContainer))]</c>) cannot be removed: take that
        /// attribute off the script first; the report names the object.
        /// </summary>
        [MenuItem("Nebula/Migrate/Remove DynamicContainer", priority = 200)]
        public static void RemoveDynamicContainerMenu()
        {
            var report = RemoveDynamicContainerEverywhere();
            foreach (var line in report.Blocked) Debug.LogWarning(line);
            Debug.Log($"[nebula] Remove DynamicContainer: {report.Removed} component(s) removed from {report.Prefabs} prefab(s) and {report.Scenes} open scene(s)" +
                      (report.Blocked.Count > 0 ? $"; {report.Blocked.Count} could not be removed (see the warnings above)" : ""));
        }

        /// <summary>What <see cref="RemoveDynamicContainerEverywhere"/> did.</summary>
        public sealed class Report
        {
            public int Removed, Prefabs, Scenes;
            public readonly List<string> Blocked = new List<string>();
        }

        /// <summary>
        /// Remove the component from the prefab assets under <paramref name="folders"/> (the whole project when null),
        /// then from the open scenes. Prefabs come first, so a scene instance loses the component with its prefab;
        /// only a component added on the instance itself is removed in the scene.
        /// </summary>
        public static Report RemoveDynamicContainerEverywhere(string[] folders = null)
        {
            var report = new Report();
            var guids = folders != null ? AssetDatabase.FindAssets("t:Prefab", folders) : AssetDatabase.FindAssets("t:Prefab");
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/")) continue; // packages are read-only
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null || asset.GetComponentsInChildren<DynamicContainer>(true).Length == 0) continue;
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    int removed = RemoveFrom(root, path, report.Blocked, inPrefabAsset: true);
                    if (removed > 0)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        report.Removed += removed;
                        report.Prefabs++;
                    }
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                int removed = 0;
                foreach (var go in scene.GetRootGameObjects()) removed += RemoveFrom(go, scene.path, report.Blocked, inPrefabAsset: false);
                if (removed == 0) continue;
                EditorSceneManager.MarkSceneDirty(scene);
                report.Removed += removed;
                report.Scenes++;
            }
            return report;
        }

        /// <summary>
        /// Remove every <see cref="DynamicContainer"/> under <paramref name="root"/> that can be removed here, and return
        /// how many were. In a scene, a component that comes from a prefab asset is left to the asset.
        /// </summary>
        public static int RemoveFrom(GameObject root, string where, List<string> blocked, bool inPrefabAsset)
        {
            int removed = 0;
            foreach (var component in root.GetComponentsInChildren<DynamicContainer>(true))
            {
                if (!inPrefabAsset && PrefabUtility.IsPartOfPrefabInstance(component) && !PrefabUtility.IsAddedComponentOverride(component)) continue;
                var go = component.gameObject;
                if (!CanRemove(component, out string dependent))
                {
                    blocked?.Add($"[nebula] {where}: '{go.name}' keeps its DynamicContainer because {dependent} requires it; remove [RequireComponent(typeof(DynamicContainer))] from that script and run this again");
                    continue;
                }
                // Prefab contents are a throwaway copy saved back to the asset; a scene edit can be undone.
                if (inPrefabAsset || Application.isPlaying) Object.DestroyImmediate(component);
                else Undo.DestroyObjectImmediate(component);
                removed++;
            }
            return removed;
        }

        private static bool CanRemove(Component component, out string dependent)
        {
            dependent = null;
            foreach (var other in component.GetComponents<Component>())
            {
                if (other == null || other == component) continue;
                foreach (RequireComponent req in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                {
                    if (req.m_Type0 == typeof(DynamicContainer) || req.m_Type1 == typeof(DynamicContainer) || req.m_Type2 == typeof(DynamicContainer))
                    {
                        dependent = other.GetType().Name;
                        return false;
                    }
                }
            }
            return true;
        }
#pragma warning restore CS0618
    }
}
