using System.Collections.Generic;
using Nebula.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula.Editor
{
    /// <summary>
    /// Keeps cell scenes usable in the editor even though they are authored cell-local. Whenever a cell scene is
    /// opened it is translated to where its cell sits relative to the <see cref="EditorOriginCell"/>, so several
    /// open cells line up like they will in the game; on save the translation is taken out again (and put back
    /// right after), so the file on disk always stays cell-local. The <see cref="WorldCell"/> root's position is
    /// the record of the applied translation, which makes this stateless across domain reloads.
    /// </summary>
    [InitializeOnLoad]
    public static class WorldEditorPlacement
    {
        private const string OriginKey = "Nebula.World.EditorOriginCell";
        private static readonly List<GameObject> Roots = new List<GameObject>();
        private static readonly Dictionary<string, Vector3> SavingOffsets = new Dictionary<string, Vector3>();

        static WorldEditorPlacement()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        /// <summary>The cell the editor shows at Unity's origin. Change it to author regions far from cell (0,0,0).</summary>
        public static Vector3Int EditorOriginCell
        {
            get
            {
                var s = SessionState.GetString(OriginKey, "0,0,0").Split(',');
                return s.Length == 3 && int.TryParse(s[0], out int x) && int.TryParse(s[1], out int y) && int.TryParse(s[2], out int z) ? new Vector3Int(x, y, z) : Vector3Int.zero;
            }
            set => SessionState.SetString(OriginKey, $"{value.x},{value.y},{value.z}");
        }

        /// <summary>Move the editor origin and re-place every open cell scene of <paramref name="world"/>.</summary>
        public static void SetEditorOrigin(WorldDefinition world, Vector3Int origin)
        {
            EditorOriginCell = origin;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (WorldAssets.TryGetCellOf(scene, out var w, out var coord) && (world == null || w == world)) Place(scene, w, coord);
            }
            SceneView.RepaintAll();
        }

        /// <summary>The WorldCell root of a cell scene, or null.</summary>
        public static WorldCell FindRoot(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            Roots.Clear();
            scene.GetRootGameObjects(Roots);
            foreach (var r in Roots)
            {
                var cell = r.GetComponent<WorldCell>();
                if (cell != null) return cell;
            }
            return null;
        }

        /// <summary>Where the open cell scene currently sits (the translation applied to its cell-local content).</summary>
        public static Vector3 CurrentOffset(Scene scene)
        {
            var root = FindRoot(scene);
            return root != null ? root.transform.position : Vector3.zero;
        }

        /// <summary>Translate the whole scene so its cell sits where the editor origin says it should.</summary>
        public static void Place(Scene scene, WorldDefinition world, Vector3Int coord)
        {
            var target = world.FrameOrigin(coord, EditorOriginCell);
            Translate(scene, target - CurrentOffset(scene));
        }

        public static void Translate(Scene scene, Vector3 delta)
        {
            if (delta == Vector3.zero || !scene.isLoaded) return;
            Roots.Clear();
            scene.GetRootGameObjects(Roots);
            foreach (var r in Roots) r.transform.position += delta;
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (Application.isPlaying) return;
            if (WorldAssets.TryGetCellOf(scene, out var world, out var coord)) Place(scene, world, coord);
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            if (Application.isPlaying) return;
            if (!WorldAssets.TryGetCellOf(scene, out _, out _)) return;
            var offset = CurrentOffset(scene);
            SavingOffsets[scene.path] = offset;
            Translate(scene, -offset); // the file is cell-local
        }

        private static void OnSceneSaved(Scene scene)
        {
            if (SavingOffsets.TryGetValue(scene.path, out var offset))
            {
                SavingOffsets.Remove(scene.path);
                Translate(scene, offset);
                ClearDirtiness(scene);
            }
        }

        private static System.Reflection.MethodInfo _clearDirtiness;
        private static bool _clearDirtinessLooked;

        /// <summary>Putting the translation back after a save dirties the scene again; the file is up to date, so clear the flag (internal API, best effort).</summary>
        private static void ClearDirtiness(Scene scene)
        {
            if (!_clearDirtinessLooked)
            {
                _clearDirtinessLooked = true;
                _clearDirtiness = typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            }
            try { _clearDirtiness?.Invoke(null, new object[] { scene }); }
            catch { /* the scene simply stays marked modified */ }
        }
    }
}
