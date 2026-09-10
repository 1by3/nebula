using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// A designer-authored unit of authority: a box volume with its own local coordinate space. Entities inside it
    /// are parented under its transform and replicated in its local space. Containers are static; which worker
    /// simulates each one is decided at runtime by the orchestrator.
    /// <para>
    /// There are no separate seam volumes in this prototype. Instead every container boundary carries an
    /// automatic ghost band (<see cref="NebulaConfig.GhostBandMargin"/>) and an entry hysteresis
    /// (<see cref="NebulaConfig.HandoverHysteresis"/>), which gives the same pre-warm-then-flip behaviour with
    /// nothing extra to author.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Container : MonoBehaviour
    {
        [Tooltip("Stable id used by the control plane. Must be unique in the scene.")]
        public string ContainerId = "container";
        [Tooltip("Box size in local space, centred on Center.")]
        public Vector3 Size = new Vector3(20f, 10f, 20f);
        public Vector3 Center = new Vector3(0f, 5f, 0f);

        /// <summary>Dense index assigned by <see cref="ContainerRegistry"/> (sorted by id, or manifest order in a partitioned world). Used on the wire.</summary>
        public ushort Index { get; internal set; } = ushort.MaxValue;
        /// <summary>Partitioned worlds: the grid cell this container belongs to (see <see cref="WorldContainerManifest"/>).</summary>
        public Vector3Int Cell { get; internal set; }
        /// <summary>Partitioned worlds: this container spans its whole cell.</summary>
        public bool IsCell { get; internal set; }
        public List<Container> Neighbors { get; } = new List<Container>();

        /// <summary>Runtime: id of the worker currently leasing this container ("" if none). Updated from the control plane.</summary>
        public string OwnerWorkerId { get; internal set; } = "";
        public ushort OwnerWorkerIndex { get; internal set; } = ushort.MaxValue;
        public ulong LeaseEpoch { get; internal set; }

        // Containers are static in this prototype (the tree is authored; only ownership moves), so the transform
        // maths every entity needs each tick - which box am I in, how far to the seam, container-local pose for the
        // wire - runs on matrices cached once per tick (see RefreshCache) instead of a native transform call each.
        private Matrix4x4 _worldToLocal = Matrix4x4.identity;
        private Matrix4x4 _localToWorld = Matrix4x4.identity;
        private Quaternion _rotation = Quaternion.identity;
        private Quaternion _inverseRotation = Quaternion.identity;
        private Bounds _worldBounds;
        private float _volume;
        private bool _cached;

        /// <summary>
        /// Re-read the transform. <see cref="ContainerRegistry.Rebuild"/> does this for every container and the worker
        /// repeats it once per tick, so a container that moves (a ship) is never more than a tick stale.
        /// </summary>
        public void RefreshCache()
        {
            var t = transform;
            _worldToLocal = t.worldToLocalMatrix;
            _localToWorld = t.localToWorldMatrix;
            _rotation = t.rotation;
            _inverseRotation = Quaternion.Inverse(_rotation);
            var center = _localToWorld.MultiplyPoint3x4(Center);
            var s = Vector3.Scale(Size, t.lossyScale);
            _worldBounds = new Bounds(center, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
            _volume = _worldBounds.size.x * _worldBounds.size.y * _worldBounds.size.z;
            _cached = true;
        }

        private void EnsureCached() { if (!_cached) RefreshCache(); }

        public Bounds WorldBounds { get { EnsureCached(); return _worldBounds; } }

        /// <summary>World-space volume of the box (nested containers: the smallest one holding a point wins).</summary>
        public float Volume { get { EnsureCached(); return _volume; } }

        /// <summary>World rotation of the container's frame (cached; see <see cref="RefreshCache"/>).</summary>
        public Quaternion Rotation { get { EnsureCached(); return _rotation; } }
        public Quaternion InverseRotation { get { EnsureCached(); return _inverseRotation; } }

        public bool Contains(Vector3 worldPosition)
        {
            EnsureCached();
            var local = _worldToLocal.MultiplyPoint3x4(worldPosition) - Center;
            var h = Size * 0.5f;
            return Mathf.Abs(local.x) <= h.x && Mathf.Abs(local.y) <= h.y && Mathf.Abs(local.z) <= h.z;
        }

        /// <summary>
        /// Signed distance to the box surface: positive outside (how far, in any direction), negative inside (how
        /// deep, measured to the nearest <i>wall</i> only). The floor and ceiling do not count as depth: pawns stand
        /// on the floor, which is the bottom face of the box, and treating it as a seam would keep every entity in
        /// a room permanently "about to leave" for the ghost band and never past the handover hysteresis.
        /// </summary>
        public float SignedDistance(Vector3 worldPosition)
        {
            EnsureCached();
            var local = _worldToLocal.MultiplyPoint3x4(worldPosition) - Center;
            var h = Size * 0.5f;
            var d = new Vector3(Mathf.Abs(local.x) - h.x, Mathf.Abs(local.y) - h.y, Mathf.Abs(local.z) - h.z);
            if (d.x > 0f || d.y > 0f || d.z > 0f)
                return new Vector3(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f), Mathf.Max(d.z, 0f)).magnitude;
            return Mathf.Max(d.x, d.z);
        }

        /// <summary>
        /// How far a point inside this container is from its seam with <paramref name="neighbor"/>. For a sibling or
        /// an inner box that is the neighbour's surface; for a neighbour that encloses this one (a building inside
        /// the outdoor area) the seam is this container's own surface, so the answer is how far the point is from
        /// getting out - not "inside the enclosing box", which would put every entity in every building in the
        /// band permanently.
        /// </summary>
        public float DistanceToSeam(Vector3 worldPosition, Container neighbor)
        {
            if (neighbor.Encloses(this)) return -SignedDistance(worldPosition);
            return neighbor.SignedDistance(worldPosition);
        }

        /// <summary><paramref name="other"/>'s box lies entirely inside this one (nested containers).</summary>
        public bool Encloses(Container other)
        {
            if (other == this) return false;
            var a = WorldBounds;
            a.Expand(0.5f); // authored boxes are allowed to touch the enclosing box's faces
            var b = other.WorldBounds;
            return a.Contains(b.min) && a.Contains(b.max) && Volume > other.Volume;
        }

        public Vector3 ToLocal(Vector3 world) { EnsureCached(); return _worldToLocal.MultiplyPoint3x4(world); }
        public Vector3 ToWorld(Vector3 local) { EnsureCached(); return _localToWorld.MultiplyPoint3x4(local); }

        public bool IsOwnedBy(string workerId) => !string.IsNullOrEmpty(workerId) && OwnerWorkerId == workerId;

        public override string ToString() => $"{ContainerId}[{Index}]->{(string.IsNullOrEmpty(OwnerWorkerId) ? "unassigned" : OwnerWorkerId)}";

        private void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.35f);
            Gizmos.DrawWireCube(Center, Size);
        }
    }
}
