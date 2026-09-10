using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula.Editor
{
    /// <summary>
    /// Gives every <see cref="NetworkIdentity"/> authored into a scene its <see cref="NetworkIdentity.SceneId"/>
    /// when the scene is saved: a random non-zero id, unique within the scene, that then stays with the object for
    /// good. Prefab assets keep 0 (they are spawned by prefab id); an instance placed in a scene gets its id as an
    /// override. Cell scenes of a partitioned world are separate files, so ids are made unique across every scene
    /// that is open at the time as well.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneEntityIds
    {
        private static readonly List<GameObject> Roots = new List<GameObject>();
        private static readonly HashSet<uint> InUse = new HashSet<uint>();

        static SceneEntityIds()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            if (Application.isPlaying) return;
            Assign(scene);
        }

        /// <summary>Assign ids to the identities in <paramref name="scene"/> that have none or share one. Returns how many changed.</summary>
        public static int Assign(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return 0;
            InUse.Clear();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var other = SceneManager.GetSceneAt(i);
                if (other == scene || !other.isLoaded) continue;
                foreach (var id in Identities(other)) if (id.SceneId != 0) InUse.Add(id.SceneId);
            }
            int changed = 0;
            foreach (var id in Identities(scene))
            {
                if (id.SceneId != 0 && InUse.Add(id.SceneId)) continue;
                uint fresh;
                do { fresh = (uint)Random.Range(1, int.MaxValue) ^ ((uint)Random.Range(0, 2) << 31); } while (fresh == 0 || !InUse.Add(fresh));
                Undo.RecordObject(id, "Assign scene entity id");
                id.SceneId = fresh;
                EditorUtility.SetDirty(id);
                PrefabUtility.RecordPrefabInstancePropertyModifications(id);
                changed++;
            }
            return changed;
        }

        private static IEnumerable<NetworkIdentity> Identities(Scene scene)
        {
            Roots.Clear();
            scene.GetRootGameObjects(Roots);
            foreach (var root in Roots)
                foreach (var id in root.GetComponentsInChildren<NetworkIdentity>(true))
                    yield return id;
        }
    }
}
