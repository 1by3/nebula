using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>
    /// A client draws a turned instance (<see cref="InstancePoses"/>, <c>docs/scope-frames.md</c> D12) turned while cameras
    /// draw, and only then: from outside, each turned instance it draws is turned about its door so its interior stands
    /// behind the door the way the door stands; from inside one, everything else (the host world, its frames, the sky's
    /// sun) is turned back about the door, so the world outside stands beyond the open door. Simulation, prediction and
    /// every pose a game reads outside drawing stay in each space's own upright axes. Everything turned is put back
    /// exactly when drawing ends.
    /// </summary>
    internal static class InstanceViews
    {
        private struct Turned
        {
            public Transform Transform;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
        }

        private static readonly List<Turned> TurnedList = new List<Turned>();
        private static readonly HashSet<Transform> Keep = new HashSet<Transform>();
        private static readonly HashSet<Transform> KeepAncestors = new HashSet<Transform>();
        private static readonly List<GameObject> RootScratch = new List<GameObject>();

        /// <summary>The scope whose instance the local view is in; set by <see cref="InstanceScenes"/>. A seam for tests.</summary>
        internal static ulong View => InstanceScenes.View;

        /// <summary>Whether anything is turned right now.</summary>
        internal static bool Active => TurnedList.Count > 0;

        /// <summary>
        /// Turn what is drawn turned. <paramref name="offset"/> is how far the scene was moved back for drawing
        /// (<see cref="SceneRenderOrigin.Offset"/>): a door's scene position p is drawn at p - offset.
        /// </summary>
        internal static void Begin(IReadOnlyList<Camera> cameras, Vector3 offset)
        {
            if (TurnedList.Count > 0 || InstancePoses.Count == 0) return;
            ulong view = View;
            if (InstancePoses.TryGet(view, out var viewOrigin, out var viewRotation))
            {
                var pivot = Double3.From(ContainerRegistry.ToFrame(viewOrigin, view)) - Double3.From(offset);
                var back = Quaternion.Inverse(viewRotation);
                Keep.Clear();
                KeepAncestors.Clear();
                foreach (var c in ContainerRegistry.Runtime)
                    if (c != null && c.InstanceId == view && c.FixedParent == null) Hold(c.transform);
                if (cameras != null) for (int i = 0; i < cameras.Count; i++) if (cameras[i] != null) Hold(cameras[i].transform);
                for (int s = 0; s < SceneManager.sceneCount; s++)
                {
                    var scene = SceneManager.GetSceneAt(s);
                    if (!scene.IsValid() || !scene.isLoaded) continue;
                    RootScratch.Clear();
                    scene.GetRootGameObjects(RootScratch);
                    for (int i = 0; i < RootScratch.Count; i++) if (RootScratch[i] != null) TurnUnlessHeld(RootScratch[i].transform, pivot, back, 0);
                }
                RootScratch.Clear();
                return;
            }
            foreach (ulong id in InstancePoses.Instances)
            {
                if (id == view || !InstanceScenes.Drawn(id) || !InstancePoses.TryGet(id, out var origin, out var rotation)) continue;
                var pivot = Double3.From(ContainerRegistry.ToFrame(origin, id)) - Double3.From(offset);
                foreach (var c in ContainerRegistry.Runtime)
                    if (c != null && c.InstanceId == id && c.FixedParent == null) Turn(c.transform, pivot, rotation);
            }
        }

        /// <summary>Put back exactly everything <see cref="Begin"/> turned.</summary>
        internal static void End()
        {
            for (int i = TurnedList.Count - 1; i >= 0; i--)
            {
                var t = TurnedList[i];
                if (t.Transform == null) continue;
                t.Transform.localPosition = t.LocalPosition;
                t.Transform.localRotation = t.LocalRotation;
            }
            TurnedList.Clear();
        }

        private static void Hold(Transform t)
        {
            Keep.Add(t);
            for (var p = t.parent; p != null; p = p.parent) KeepAncestors.Add(p);
        }

        private static void TurnUnlessHeld(Transform t, Double3 pivot, Quaternion rotation, int depth)
        {
            if (Keep.Contains(t)) return;
            var canvas = depth == 0 ? t.GetComponent<Canvas>() : null;
            if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay) return;
            if (!KeepAncestors.Contains(t)) { Turn(t, pivot, rotation); return; }
            if (depth > 64) return;
            for (int i = 0; i < t.childCount; i++) TurnUnlessHeld(t.GetChild(i), pivot, rotation, depth + 1);
        }

        private static void Turn(Transform t, Double3 pivot, Quaternion rotation)
        {
            TurnedList.Add(new Turned { Transform = t, LocalPosition = t.localPosition, LocalRotation = t.localRotation });
            var p = Double3.From(t.position);
            t.SetPositionAndRotation((pivot + PhysicsFrames.Rotate(rotation, p - pivot)).ToVector3(), rotation * t.rotation);
        }
    }
}
