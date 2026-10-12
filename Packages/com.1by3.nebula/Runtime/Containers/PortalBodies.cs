using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// What a process knows about the bodies near a portal's face (<c>docs/portals.md</c> D26): an entity's copyable colliders, how far its colliders
    /// and extent reach from its root, and whether any of it touches a band. Membership of a band is tested on the entity's extent when it has one
    /// (<c>docs/entity-extents.md</c>) and on its colliders' shapes otherwise, so a ship whose root is far from the face but whose hull is in the doorway
    /// is in the band; an entity with neither is a point at its root. It needs no physics scene: only transforms.
    /// </summary>
    internal sealed class PortalBodies
    {
        /// <summary>How many ticks a cached description of an entity's colliders is trusted. A collider added or removed shows up within this.</summary>
        public const uint RefreshTicks = 30;

        /// <summary>The colliders of one entity that can be copied, and how far the entity reaches.</summary>
        public sealed class Info
        {
            public NetworkIdentity Entity;
            public Collider[] Colliders = new Collider[4];
            public PortalShapeKey[] Keys = new PortalShapeKey[4];
            public Vector3[] Centers = new Vector3[4];
            public bool[] Upright = new bool[4];
            public int Count;
            /// <summary>The farthest any collider's or the extent's corner is from the entity's root, in scene space.</summary>
            public float Radius;
            public bool HasExtent;
            public Bounds Extent;
            public EntityExtentSource ExtentSource;
            /// <summary>The entity's body when it has a non-kinematic one, else null.</summary>
            public Rigidbody Body;
            public uint RefreshedAt;
            public uint Stamp;
            public bool Valid;
        }

        private readonly Dictionary<ulong, Info> _infos = new Dictionary<ulong, Info>();
        private readonly List<Collider> _scratch = new List<Collider>(16);
        private readonly Vector3[] _corners = new Vector3[8];
        private readonly List<ulong> _stale = new List<ulong>();

        /// <summary>How many entities this process has described.</summary>
        public int Cached => _infos.Count;

        /// <summary>How far, in meters, an entity nobody has described yet is assumed to reach from its root: what <see cref="MayTouch"/> allows before it looks at the entity.</summary>
        public const float UnknownReach = 32f;

        /// <summary>
        /// A cheap look at whether <paramref name="entity"/> can possibly touch a band, before anything about its colliders is read: its root is within the band grown by the
        /// entity's known reach (<see cref="UnknownReach"/> when nobody has described it yet). An entity with an extent is always asked about: its reach is its own.
        /// </summary>
        public bool MayTouch(NetworkIdentity entity, in PortalFaceFrame frame, in PortalBand band, float grow)
        {
            if (entity.ExtentSource != EntityExtentSource.None) return true;
            float reach = UnknownReach;
            if (_infos.TryGetValue(entity.NetId, out var info) && info.Valid && info.Entity == entity) reach = info.Radius;
            return band.Contains(frame.ToLocal(entity.ToScope(entity.transform.position)), reach + grow);
        }

        /// <summary>The description of <paramref name="entity"/>, made again when it is older than <see cref="RefreshTicks"/> or a collider it names was destroyed.</summary>
        public Info Get(NetworkIdentity entity, uint tick)
        {
            if (!_infos.TryGetValue(entity.NetId, out var info)) _infos.Add(entity.NetId, info = new Info { Entity = entity });
            bool refresh = !info.Valid || info.Entity != entity || tick - info.RefreshedAt >= RefreshTicks || tick < info.RefreshedAt || ExtentChanged(entity, info);
            if (!refresh)
                for (int i = 0; i < info.Count; i++)
                    if (info.Colliders[i] == null) { refresh = true; break; }
            if (refresh) Describe(entity, info, tick);
            info.Stamp = tick;
            return info;
        }

        /// <summary>The game set, changed or cleared the entity's extent since it was described: that takes effect at once, not at the next refresh.</summary>
        private static bool ExtentChanged(NetworkIdentity entity, Info info) =>
            entity.ExtentSource != info.ExtentSource || (info.ExtentSource == EntityExtentSource.Explicit && entity.Extent != info.Extent);

        /// <summary>Forget descriptions nobody asked for since <paramref name="tick"/> minus <paramref name="age"/>.</summary>
        public void Prune(uint tick, uint age)
        {
            _stale.Clear();
            foreach (var kv in _infos) if (tick - kv.Value.Stamp > age || kv.Value.Entity == null) _stale.Add(kv.Key);
            for (int i = 0; i < _stale.Count; i++) _infos.Remove(_stale[i]);
            _stale.Clear();
        }

        public void Clear() => _infos.Clear();

        private void Describe(NetworkIdentity entity, Info info, uint tick)
        {
            info.Entity = entity;
            info.Valid = true;
            info.RefreshedAt = tick;
            info.Count = 0;
            info.Radius = 0f;
            info.Body = null;
            var body = entity.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic) info.Body = body;
            var root = entity.ToScope(entity.transform.position);
            _scratch.Clear();
            entity.GetComponentsInChildren(false, _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                var c = _scratch[i];
                // A ghost held across a portal has its colliders off, so that it is no body here; they are still what its proxies are made from.
                if (c == null || (!c.enabled && !entity.IsPortalGhost) || c.isTrigger) continue;
                if (c.GetComponent<PortalProxy>() != null) continue;
                if (c.GetComponentInParent<NetworkIdentity>() != entity) continue; // a rider's, or a nested entity's
                if (!PortalShapes.TryDescribe(c, out var key, out var center, out bool upright)) continue;
                if (info.Count == info.Colliders.Length)
                {
                    int n = info.Count * 2;
                    System.Array.Resize(ref info.Colliders, n);
                    System.Array.Resize(ref info.Keys, n);
                    System.Array.Resize(ref info.Centers, n);
                    System.Array.Resize(ref info.Upright, n);
                }
                info.Colliders[info.Count] = c;
                info.Keys[info.Count] = key;
                info.Centers[info.Count] = center;
                info.Upright[info.Count] = upright;
                info.Count++;
                int corners = EntityExtents.ShapeCorners(c, Matrix4x4.identity, _corners);
                info.Radius = Mathf.Max(info.Radius, Reach(entity, root, corners));
            }
            _scratch.Clear();
            info.ExtentSource = entity.ExtentSource;
            info.HasExtent = entity.TryGetExtentForBand(tick, out var extent);
            if (info.HasExtent)
            {
                info.Extent = extent;
                EntityExtents.Corners(entity.transform.localToWorldMatrix, extent, _corners);
                info.Radius = Mathf.Max(info.Radius, Reach(entity, root, 8));
            }
        }

        private float Reach(NetworkIdentity entity, Vector3 root, int count)
        {
            float far = 0f;
            for (int i = 0; i < count; i++) far = Mathf.Max(far, (entity.ToScope(_corners[i]) - root).magnitude);
            return far;
        }

        /// <summary>
        /// Whether any part of the entity touches the band (<c>docs/portals.md</c> D26), the band grown on every side by <paramref name="grow"/>. When
        /// <paramref name="eligible"/> is given it is filled with the index (into <see cref="Info.Colliders"/>) of every collider whose own shape touches the band
        /// grown by <paramref name="eligibleGrow"/>: the ones a proxy is built from.
        /// </summary>
        public bool Touches(NetworkIdentity entity, Info info, uint tick, in PortalFaceFrame frame, in PortalBand band, float grow,
            List<int> eligible = null, float eligibleGrow = 0f)
        {
            eligible?.Clear();
            var root = entity.ToScope(entity.transform.position);
            var local = frame.ToLocal(root);
            if (!band.Contains(local, info.Radius + Mathf.Max(grow, eligibleGrow))) return false;
            bool touches = false;
            for (int i = 0; i < info.Count; i++)
            {
                var c = info.Colliders[i];
                if (c == null || (!c.enabled && !entity.IsPortalGhost)) continue;
                int n = EntityExtents.ShapeCorners(c, Matrix4x4.identity, _corners);
                ToScope(entity, n);
                PortalBand.LocalBounds(frame, _corners, n, out var min, out var max);
                if (!info.HasExtent && band.Overlaps(min, max, grow)) touches = true;
                if (eligible != null && band.Overlaps(min, max, eligibleGrow)) eligible.Add(i);
            }
            if (info.HasExtent)
            {
                EntityExtents.Corners(entity.transform.localToWorldMatrix, info.Extent, _corners);
                ToScope(entity, 8);
                PortalBand.LocalBounds(frame, _corners, 8, out var min, out var max);
                touches = band.Overlaps(min, max, grow);
            }
            else if (info.Count == 0) touches = band.Contains(local, grow);
            return touches;
        }

        private void ToScope(NetworkIdentity entity, int count)
        {
            var space = entity.Space;
            if (space == null || !PhysicsFrames.InSimulationPose(space)) return;
            for (int i = 0; i < count; i++) _corners[i] = PhysicsFrames.Convert(_corners[i], space, null);
        }

        /// <summary>
        /// A collider's pose for a proxy, in the scope's space: the centre of its shape and its rotation (the identity for a character controller, whose
        /// capsule does not turn).
        /// </summary>
        /// <param name="entity">The entity the collider belongs to.</param>
        /// <param name="collider">The collider.</param>
        /// <param name="centerLocal">The centre of its shape in its transform's space.</param>
        /// <param name="upright">The collider does not turn with its transform.</param>
        /// <param name="latestState">
        /// Pose it as if the entity's root stood at the latest pose its authority reported (the newest sample of its interpolator) rather than where it is drawn
        /// now: a client predicting against a replica uses the state the worker posed its own proxy from (<c>docs/portals.md</c> D25, section 18).
        /// </param>
        public static void PoseOf(NetworkIdentity entity, Collider collider, Vector3 centerLocal, bool upright, bool latestState, out Vector3 position, out Quaternion rotation)
        {
            var t = collider.transform;
            position = t.TransformPoint(centerLocal);
            rotation = upright ? Quaternion.identity : t.rotation;
            var interpolator = latestState ? entity.Interpolator : null;
            if (interpolator != null && interpolator.HasSamples)
            {
                // Where the collider stands relative to the root as drawn, carried to where the root stands in the newest sample.
                var root = entity.transform;
                var drawnInverse = Quaternion.Inverse(root.rotation);
                var latestRotation = interpolator.LatestRotation;
                position = interpolator.LatestPosition + latestRotation * (drawnInverse * (position - root.position));
                if (!upright) rotation = latestRotation * drawnInverse * rotation;
            }
            var space = entity.Space;
            if (space != null && PhysicsFrames.InSimulationPose(space))
            {
                position = PhysicsFrames.Convert(position, space, null);
                rotation = PhysicsFrames.Convert(rotation, space, null);
            }
        }
    }
}
