using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The geometry behind <see cref="NetworkIdentity.ExtentSource"/> (<c>docs/entity-extents.md</c>): the box around
    /// an entity's own colliders, and an extent's corners in the space its container's neighbours live in.
    /// </summary>
    internal static class EntityExtents
    {
        private static readonly List<Collider> Colliders = new List<Collider>(16);
        private static readonly Vector3[] Scratch = new Vector3[8];

        /// <summary>
        /// The box, in <paramref name="identity"/>'s local space, around every enabled, non-trigger collider on an active
        /// object under it whose nearest <see cref="NetworkIdentity"/> is this one (docs/entity-extents.md D2). Each
        /// collider is read from its own shape, not from <see cref="Collider.bounds"/>. False when no collider counts.
        /// </summary>
        public static bool TryComputeColliderExtent(NetworkIdentity identity, out Bounds localBounds)
        {
            localBounds = default;
            bool any = false;
            var root = identity.transform;
            var rootFromWorld = root.worldToLocalMatrix;
            Colliders.Clear();
            identity.GetComponentsInChildren(false, Colliders);
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < Colliders.Count; i++)
            {
                var c = Colliders[i];
                if (c == null || !c.enabled || c.isTrigger) continue;
                if (c.GetComponentInParent<NetworkIdentity>() != identity) continue; // a rider's, or a nested entity's
                int n = ShapeCorners(c, rootFromWorld, Scratch);
                for (int k = 0; k < n; k++)
                {
                    min = Vector3.Min(min, Scratch[k]);
                    max = Vector3.Max(max, Scratch[k]);
                }
                any |= n > 0;
            }
            Colliders.Clear();
            if (!any) return false;
            localBounds.SetMinMax(min, max);
            return true;
        }

        /// <summary>The corners of a box around the collider's shape, in the root's local space. Returns how many were written.</summary>
        private static int ShapeCorners(Collider c, Matrix4x4 rootFromWorld, Vector3[] corners)
        {
            var t = c.transform;
            var rootFromCollider = rootFromWorld * t.localToWorldMatrix;
            switch (c)
            {
                case BoxCollider box:
                    return BoxCorners(rootFromCollider, box.center, box.size * 0.5f, corners);
                case SphereCollider sphere:
                    return BoxCorners(rootFromCollider, sphere.center, RoundHalfExtents(t, sphere.radius, sphere.radius, -1), corners);
                case CapsuleCollider capsule:
                    return BoxCorners(rootFromCollider, capsule.center, RoundHalfExtents(t, capsule.radius, capsule.height * 0.5f, capsule.direction), corners);
                case CharacterController controller:
                    return BoxCorners(rootFromCollider, controller.center, RoundHalfExtents(t, controller.radius, controller.height * 0.5f, 1), corners);
                case MeshCollider mesh when mesh.sharedMesh != null:
                    var b = mesh.sharedMesh.bounds;
                    return BoxCorners(rootFromCollider, b.center, b.extents, corners);
                default:
                    // A terrain, a wheel, anything else: its world box is the best there is.
                    var w = c.bounds;
                    if (w.size == Vector3.zero) return 0;
                    return BoxCorners(rootFromWorld, w.center, w.extents, corners);
            }
        }

        /// <summary>
        /// Half extents, in the collider's local axes, of the box around a sphere or capsule. Unity scales a round
        /// shape's radius by the largest axis of the collider's scale, so the local half extent along an axis is the
        /// radius times that largest scale over the axis's own scale. <paramref name="axis"/> is the capsule's axis
        /// (0, 1, 2), or -1 for a sphere.
        /// </summary>
        private static Vector3 RoundHalfExtents(Transform t, float radius, float halfHeight, int axis)
        {
            var s = t.lossyScale;
            var a = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            float largest = Mathf.Max(a.x, Mathf.Max(a.y, a.z));
            var h = Vector3.zero;
            for (int i = 0; i < 3; i++)
            {
                float round = a[i] > 1e-6f ? radius * largest / a[i] : radius;
                h[i] = i == axis ? Mathf.Max(halfHeight, round) : round;
            }
            return h;
        }

        private static int BoxCorners(Matrix4x4 m, Vector3 center, Vector3 half, Vector3[] corners)
        {
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) != 0 ? half.x : -half.x, (i & 2) != 0 ? half.y : -half.y, (i & 4) != 0 ? half.z : -half.z);
                corners[i] = m.MultiplyPoint3x4(center + p);
            }
            return 8;
        }

        /// <summary>
        /// The eight corners of <paramref name="localBounds"/> through <paramref name="localToWorld"/> (the entity root's
        /// matrix): on a worker, in the simulation space the entity lives in.
        /// </summary>
        public static void Corners(Matrix4x4 localToWorld, Bounds localBounds, Vector3[] corners) =>
            BoxCorners(localToWorld, localBounds.center, localBounds.extents, corners);
    }
}
