using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// A unit of authority: a box volume with its own local coordinate space. Entities inside it are parented under
    /// its transform and replicated in its local space. A <b>static</b> container is authored into the scene (or
    /// baked into the world manifest) and leased to a worker by the orchestrator; a <b>dynamic</b> one is carried by
    /// an entity (<see cref="DynamicContainer"/>): it moves with the entity, exists wherever the entity does and is
    /// owned by whoever is authoritative for the entity. Either way, the contents never see the difference.
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
        [Tooltip("Stable id used by the control plane. Must be unique in the scene. On a DynamicContainer prefab it is a label: the runtime id is '<label>#<carrier net id>'.")]
        public string ContainerId = "container";
        [Tooltip("Box size in local space, centred on Center.")]
        public Vector3 Size = new Vector3(20f, 10f, 20f);
        public Vector3 Center = new Vector3(0f, 5f, 0f);

        /// <summary>
        /// Dense index assigned by <see cref="ContainerRegistry"/> (sorted by id, or manifest order in a partitioned
        /// world). Used on the wire. A dynamic container has <see cref="ContainerRef.DynamicIndex"/> and is named
        /// by its carrier instead (see <see cref="Ref"/>).
        /// </summary>
        public ushort Index { get; internal set; } = ushort.MaxValue;
        /// <summary>Partitioned worlds: the grid cell this container belongs to (see <see cref="WorldContainerManifest"/>).</summary>
        public Vector3Int Cell { get; internal set; }
        /// <summary>Partitioned worlds: this container spans its whole cell.</summary>
        public bool IsCell { get; internal set; }
        /// <summary>Static adjacency: containers whose boxes touch or overlap this one, computed when the registry loads. Dynamic containers are not in it; see <see cref="ContainerRegistry.NeighborsOf"/>.</summary>
        public List<Container> Neighbors { get; } = new List<Container>();

        /// <summary>Carried by an entity (<see cref="DynamicContainer"/>): created and destroyed with it, moves with it, owned by its authority.</summary>
        public bool IsDynamic { get; internal set; }
        /// <summary>Dynamic containers: the entity carrying this container. Null for static ones.</summary>
        public NetworkIdentity Carrier { get; internal set; }
        /// <summary>Dynamic containers: the carrier's net id, which names this container on the wire. 0 for static ones.</summary>
        public ulong CarrierNetId => Carrier != null ? Carrier.NetId : 0;
        /// <summary>How this container is named on the wire.</summary>
        public ContainerRef Ref => ContainerRef.Of(this);
        /// <summary>The entities currently inside this container on this process, kept by <see cref="NetworkIdentity"/> as they move.</summary>
        public List<NetworkIdentity> Entities { get; } = new List<NetworkIdentity>();
        /// <summary>Dynamic containers: the container the carrier itself is in (what encloses this one). Null for static containers and for a carrier in no container.</summary>
        public Container Enclosing => IsDynamic && Carrier != null ? Carrier.Container : null;

        private string _ownerWorkerId = "";
        private ushort _ownerWorkerIndex = ushort.MaxValue;
        private ulong _leaseEpoch;

        /// <summary>State string of the control-plane lease as last applied ("" when there is none). See <see cref="Nebula.LeaseState"/>.</summary>
        public string LeaseState { get; internal set; } = "";

        /// <summary>
        /// Dynamic containers: the orchestrator pinned this container to a worker of its own (lease state
        /// <see cref="Nebula.LeaseState.Pinned"/>), so it no longer follows its carrier. The pinned worker holds a
        /// permanent ghost of the carrier and simulates everything inside.
        /// </summary>
        public bool IsPinned => IsDynamic && LeaseState == Nebula.LeaseState.Pinned && !string.IsNullOrEmpty(_ownerWorkerId);

        /// <summary>
        /// Runtime: id of the worker currently authoritative for this container ("" if none). Static containers:
        /// from the control plane lease. Dynamic containers: the pinned worker when the lease is pinned, otherwise
        /// whoever is authoritative for the carrier, resolved through <see cref="ContainerRegistry.WorkerIdByIndex"/>
        /// (only workers know every peer's id; on a client it is "" unless the game supplies a resolver).
        /// </summary>
        public string OwnerWorkerId
        {
            get
            {
                if (!IsDynamic || IsPinned) return _ownerWorkerId;
                if (Carrier == null) return "";
                var resolve = ContainerRegistry.WorkerIdByIndex;
                return resolve != null ? resolve(Carrier.OwnerWorkerIndex) ?? "" : "";
            }
            internal set => _ownerWorkerId = value ?? "";
        }

        /// <summary>Index of the owning worker (<see cref="ushort.MaxValue"/> when unowned). Dynamic containers: the pinned worker's, or the carrier's <see cref="NetworkIdentity.OwnerWorkerIndex"/>.</summary>
        public ushort OwnerWorkerIndex
        {
            get => IsDynamic && !IsPinned ? (Carrier != null ? Carrier.OwnerWorkerIndex : ushort.MaxValue) : _ownerWorkerIndex;
            internal set => _ownerWorkerIndex = value;
        }

        /// <summary>Lease epoch from the control plane. Dynamic containers following their carrier: the carrier's authority epoch.</summary>
        public ulong LeaseEpoch
        {
            get => IsDynamic && !IsPinned ? (Carrier != null ? Carrier.Epoch : 0) : _leaseEpoch;
            internal set => _leaseEpoch = value;
        }

        // The transform maths every entity needs each tick - which box am I in, how far to the seam, container-local
        // pose for the wire - runs on matrices cached once per tick (see RefreshCache) instead of a native transform
        // call each. Static containers never move, so the cache is refreshed for form's sake; a dynamic container
        // moves every tick, so its cache is also refreshed on first use in every frame (the worker refreshes all of
        // them at the top of every tick; clients and the gateway lean on the per-frame refresh).
        private Matrix4x4 _worldToLocal = Matrix4x4.identity;
        private Matrix4x4 _localToWorld = Matrix4x4.identity;
        private Quaternion _rotation = Quaternion.identity;
        private Quaternion _inverseRotation = Quaternion.identity;
        private Bounds _worldBounds;
        private float _volume;
        private bool _cached;
        private int _cacheFrame = -1;

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
            _cacheFrame = Time.frameCount;
            if (IsDynamic) transform.hasChanged = false; // a carrier above keeps its own flag until it refreshes itself
        }

        private void EnsureCached()
        {
            if (!_cached || (IsDynamic && (_cacheFrame != Time.frameCount || FrameMoved))) RefreshCache();
        }

        /// <summary>
        /// A dynamic container's transform (or a carrier's above it) moved since the cache was taken. The carrier
        /// moves inside a tick, between the registry's refresh and the poses its contents report, so a per-tick
        /// cache alone would express those poses in a frame one tick behind the hull; the transform's own changed
        /// flag catches that at the cost of one native call per query.
        /// </summary>
        private bool FrameMoved
        {
            get
            {
                for (var c = this; c != null && c.IsDynamic; c = c.Enclosing)
                    if (c.transform.hasChanged) return true;
                return false;
            }
        }

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
        /// the outdoor area, the area around a ship) the seam is this container's own surface, so the answer is how
        /// far the point is from getting out - not "inside the enclosing box", which would put every entity in every
        /// building in the band permanently.
        /// </summary>
        public float DistanceToSeam(Vector3 worldPosition, Container neighbor)
        {
            if (neighbor.Encloses(this)) return -SignedDistance(worldPosition);
            return neighbor.SignedDistance(worldPosition);
        }

        /// <summary><paramref name="other"/>'s box lies entirely inside this one (nested containers). A dynamic container is enclosed by the container its carrier is in whatever the boxes say.</summary>
        public bool Encloses(Container other)
        {
            if (other == this) return false;
            if (other.IsDynamic && other.Enclosing == this) return true;
            var a = WorldBounds;
            a.Expand(0.5f); // authored boxes are allowed to touch the enclosing box's faces
            var b = other.WorldBounds;
            return a.Contains(b.min) && a.Contains(b.max) && Volume > other.Volume;
        }

        /// <summary>
        /// Whether the segment from <paramref name="a"/> to <paramref name="b"/> passes through the box grown by
        /// <paramref name="margin"/> on every side, and where along it (<paramref name="tEnter"/> and
        /// <paramref name="tExit"/> are fractions of the segment, clamped to [0, 1]). A segment starting inside the
        /// box enters at 0. Slab test in the container's local space, so rotated boxes are exact. The margin absorbs
        /// handover hysteresis and an entity's own radius when the caller asks "whose entities could this ray touch".
        /// </summary>
        public bool IntersectsSegment(Vector3 a, Vector3 b, float margin, out float tEnter, out float tExit)
        {
            EnsureCached();
            var la = _worldToLocal.MultiplyPoint3x4(a) - Center;
            var lb = _worldToLocal.MultiplyPoint3x4(b) - Center;
            var d = lb - la;
            var h = Size * 0.5f + Vector3.one * margin;
            float t0 = 0f, t1 = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = la[axis], dir = d[axis], e = h[axis];
                if (Mathf.Abs(dir) < 1e-7f)
                {
                    if (o < -e || o > e) { tEnter = tExit = 0f; return false; }
                    continue;
                }
                float inv = 1f / dir;
                float ta = (-e - o) * inv;
                float tb = (e - o) * inv;
                if (ta > tb) { float tmp = ta; ta = tb; tb = tmp; }
                if (ta > t0) t0 = ta;
                if (tb < t1) t1 = tb;
                if (t0 > t1) { tEnter = tExit = 0f; return false; }
            }
            tEnter = t0;
            tExit = t1;
            return true;
        }

        public Vector3 ToLocal(Vector3 world) { EnsureCached(); return _worldToLocal.MultiplyPoint3x4(world); }
        public Vector3 ToWorld(Vector3 local) { EnsureCached(); return _localToWorld.MultiplyPoint3x4(local); }

        public bool IsOwnedBy(string workerId) => !string.IsNullOrEmpty(workerId) && OwnerWorkerId == workerId;

        /// <summary>How many carriers this container sits inside, walking up through <see cref="Enclosing"/>: 0 for a static container, 1 for a ship in a static container, 2 for a shuttle inside that ship.</summary>
        public int NestingDepth
        {
            get
            {
                int depth = 0;
                for (var c = this; c != null && c.IsDynamic && depth < 16; c = c.Enclosing) depth++;
                return depth;
            }
        }

        public override string ToString() => $"{ContainerId}[{(IsDynamic ? Ref.ToString() : Index.ToString())}]->{(string.IsNullOrEmpty(OwnerWorkerId) ? "unassigned" : OwnerWorkerId)}";

        private void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = GetComponent<DynamicContainer>() != null ? new Color(1f, 0.7f, 0.2f, 0.35f) : new Color(0.2f, 0.8f, 1f, 0.35f);
            Gizmos.DrawWireCube(Center, Size);
        }
    }
}
