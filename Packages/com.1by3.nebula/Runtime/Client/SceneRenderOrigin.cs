using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>
    /// A client's render origin for the whole scene (<c>docs/container-tree.md</c> D25): while cameras draw, the scene's
    /// top-level objects and the cameras are moved by -<see cref="Offset"/>, the camera's position snapped to
    /// <see cref="Step"/>, so everything near the camera is drawn within a few dozen metres of Unity's origin.
    /// <para>
    /// Unity keeps every object's world matrix in float, in scene coordinates. A scope's floating origin can leave the
    /// camera kilometres from Unity's origin (a game with large origin cells, by design up to half a cell), where a float
    /// is a millimetre or more apart: the camera and every object near it round to that grid on their own, and something
    /// moving with the camera (a held tool, a ship's cockpit) shakes against it by that much every frame. Moved near the
    /// origin for drawing, the sums stay small.
    /// </para>
    /// <para>
    /// The shift holds only while cameras draw (the render pipeline's context, or one built-in camera's cull to render)
    /// and every transform is put back exactly afterwards, so game code, physics, prediction and Nebula's own state never
    /// see it. Inside rendering callbacks (<c>OnWillRenderObject</c>, <c>beginCameraRendering</c>, a render feature)
    /// positions, <c>Camera.main.transform.position</c> included, are shifted by -<see cref="Offset"/>.
    /// </para>
    /// <para>
    /// What is moved: the root objects of every loaded scene (and of the don't-destroy-on-load scene) except screen-space
    /// overlay canvases, each physics frame's top level (placed from its pose in double), the cameras that draw, and any
    /// root added with <see cref="AddRoot"/>. What cannot be moved by a transform is not: world-space particles, trails and
    /// lines, statically batched meshes, light probes, and meshes a game draws itself with world matrices. Offset those in
    /// <see cref="Shifted"/>, or turn the scene render origin off (<see cref="Enabled"/>).
    /// </para>
    /// </summary>
    public static class SceneRenderOrigin
    {
        /// <summary>Whether a client moves the scene near its camera while cameras draw. On by default.</summary>
        public static bool Enabled = true;

        /// <summary>The grid (metres) the offset snaps to: the camera is drawn within half of this from Unity's origin.</summary>
        public static float Step = 64f;

        /// <summary>How far (metres) the camera may be from Unity's origin before the scene is moved: a float is 0.06 mm apart at 1 km.</summary>
        public static float Threshold = 1024f;

        /// <summary>
        /// How far the scene is moved back while cameras draw right now (zero outside drawing, and while the camera is
        /// within <see cref="Threshold"/>): a scene position p is drawn at p - Offset.
        /// </summary>
        public static Vector3 Offset { get; private set; }

        /// <summary>
        /// Raised once the scene and the cameras were moved by -offset for drawing: the place for a game to move what a
        /// transform does not carry (its own instanced matrices), or to place a camera it composes in double precisely at
        /// (its position - offset). Everything Nebula moved is put back after drawing whatever the handler does to it;
        /// what the handler moves itself, it puts back in <see cref="Restoring"/>.
        /// </summary>
        public static event Action<Vector3> Shifted;

        /// <summary>Raised as drawing ends, before Nebula puts back what it moved, with the offset that was used.</summary>
        public static event Action<Vector3> Restoring;

        private static readonly List<Transform> ExtraRoots = new List<Transform>();
        private static readonly List<GameObject> RootScratch = new List<GameObject>();
        private static readonly HashSet<Transform> MovedRoots = new HashSet<Transform>();
        private static readonly List<KeyValuePair<Transform, Vector3>> Moved = new List<KeyValuePair<Transform, Vector3>>();
        private static GameObject _persistentProbe;
        private static bool _shifted;

        /// <summary>Move <paramref name="root"/> with the scene too (an object in a scene Nebula does not enumerate). Idempotent.</summary>
        public static void AddRoot(Transform root)
        {
            if (root != null && !ExtraRoots.Contains(root)) ExtraRoots.Add(root);
        }

        /// <summary>Stop moving a root added with <see cref="AddRoot"/>.</summary>
        public static void RemoveRoot(Transform root) => ExtraRoots.Remove(root);

        /// <summary>The offset for a camera at <paramref name="camera"/>: zero within <see cref="Threshold"/>, else snapped to <see cref="Step"/>.</summary>
        public static Vector3 OffsetFor(Vector3 camera)
        {
            float threshold = Threshold;
            if (float.IsNaN(threshold) || float.IsInfinity(threshold) || camera.magnitude <= threshold) return Vector3.zero;
            float step = Mathf.Max(1f, Step);
            return new Vector3(Mathf.Round(camera.x / step) * step, Mathf.Round(camera.y / step) * step, Mathf.Round(camera.z / step) * step);
        }

        internal static void ResetForNewSession()
        {
            ExtraRoots.Clear();
            Moved.Clear();
            MovedRoots.Clear();
            Offset = Vector3.zero;
            _shifted = false;
            Shifted = null;
            Restoring = null;
        }

        /// <summary>The camera the offset follows: the main camera when it draws now, else the first that does.</summary>
        private static Camera Lead(IReadOnlyList<Camera> cameras)
        {
            if (cameras == null || cameras.Count == 0) return null;
            var main = Camera.main;
            for (int i = 0; i < cameras.Count; i++) if (cameras[i] == main && main != null) return main;
            for (int i = 0; i < cameras.Count; i++) if (cameras[i] != null) return cameras[i];
            return null;
        }

        /// <summary>
        /// Move the scene and <paramref name="cameras"/> for drawing. Called by <see cref="PhysicsFrames"/> as cameras start
        /// drawing, after the frames' children took their render origins. Returns the offset used (zero: nothing moved).
        /// </summary>
        internal static Vector3 Begin(IReadOnlyList<Camera> cameras, List<PhysicsFrame> frames, PhysicsFrame simulating)
        {
            if (_shifted || !Enabled) return Vector3.zero;
            var lead = Lead(cameras);
            if (lead == null) return Vector3.zero;
            var offset = OffsetFor(lead.transform.position);
            if (offset == Vector3.zero) return Vector3.zero;
            _shifted = true;
            Offset = offset;
            MovedRoots.Clear();
            // Each frame's top level from its pose in double, so the frame is drawn exactly about the new origin rather
            // than moved from a position that was already rounded kilometres out.
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                var pivot = frame.Pivot;
                if (pivot == null || frame == simulating || !frame._posed || pivot.parent != null) continue;
                Remember(pivot);
                pivot.position = (frame._pivotScene - offset).ToVector3();
            }
            for (int s = 0; s < SceneManager.sceneCount; s++) MoveRootsOf(SceneManager.GetSceneAt(s), offset);
            if (Application.isPlaying)
            {
                if (_persistentProbe == null)
                {
                    _persistentProbe = new GameObject("Nebula scene render origin probe") { hideFlags = HideFlags.HideAndDontSave };
                    UnityEngine.Object.DontDestroyOnLoad(_persistentProbe);
                }
                MoveRootsOf(_persistentProbe.scene, offset);
            }
            for (int i = 0; i < ExtraRoots.Count; i++) Move(ExtraRoots[i], offset);
            // A camera outside every moved root (the Editor's scene view, a camera in a scene of its own) moves itself.
            for (int i = 0; i < cameras.Count; i++)
            {
                var camera = cameras[i];
                if (camera == null || IsUnderMoved(camera.transform)) continue;
                Remember(camera.transform);
                camera.transform.position -= offset;
            }
            try { Shifted?.Invoke(offset); }
            catch (Exception e) { NebulaLog.Error($"SceneRenderOrigin.Shifted threw: {e}"); }
            return offset;
        }

        /// <summary>Put back exactly everything <see cref="Begin"/> moved.</summary>
        internal static void End()
        {
            if (!_shifted) return;
            try { Restoring?.Invoke(Offset); }
            catch (Exception e) { NebulaLog.Error($"SceneRenderOrigin.Restoring threw: {e}"); }
            for (int i = Moved.Count - 1; i >= 0; i--)
                if (Moved[i].Key != null) Moved[i].Key.localPosition = Moved[i].Value;
            Moved.Clear();
            MovedRoots.Clear();
            Offset = Vector3.zero;
            _shifted = false;
        }

        private static void MoveRootsOf(Scene scene, Vector3 offset)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            RootScratch.Clear();
            scene.GetRootGameObjects(RootScratch);
            for (int i = 0; i < RootScratch.Count; i++)
            {
                var go = RootScratch[i];
                if (go == null || go == _persistentProbe) continue;
                var canvas = go.GetComponent<Canvas>();
                if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay) continue;
                Move(go.transform, offset);
            }
            RootScratch.Clear();
        }

        private static void Move(Transform t, Vector3 offset)
        {
            if (t == null || MovedRoots.Contains(t) || IsUnderMoved(t)) return;
            Remember(t);
            t.position -= offset;
        }

        private static void Remember(Transform t)
        {
            MovedRoots.Add(t);
            Moved.Add(new KeyValuePair<Transform, Vector3>(t, t.localPosition));
        }

        private static bool IsUnderMoved(Transform t)
        {
            int hops = 0;
            for (var p = t; p != null && hops < 256; p = p.parent, hops++)
                if (MovedRoots.Contains(p)) return true;
            return false;
        }
    }
}
