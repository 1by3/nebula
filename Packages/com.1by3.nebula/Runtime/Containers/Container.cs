using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Defines a box-shaped authority area with its own local coordinate space. Nebula parents contained entities
    /// under its transform and replicates their local positions. The orchestrator assigns a static container from a
    /// scene or world manifest to a worker.
    /// <para>
    /// A container on the root of an entity (next to its <see cref="NetworkIdentity"/>) is carried by that entity:
    /// a ship's interior, a lift, a train car (<see cref="FrameMode"/> is <see cref="ContainerFrameMode.Entity"/>).
    /// It registers when the entity spawns on a process and unregisters when it despawns there, moves with the
    /// entity's root transform, and normally follows the entity's authoritative worker. Reach it through
    /// <see cref="NetworkIdentity.Carried"/>. The entity needs a <see cref="NetworkTransform"/> on its root; a
    /// container on a child object of an entity is not supported.
    /// </para>
    /// <para>
    /// Nebula uses <see cref="NebulaConfig.GhostBandMargin"/> to send nearby entities to a neighboring worker
    /// before they cross the boundary. It uses <see cref="NebulaConfig.HandoverHysteresis"/> to prevent an entity
    /// near a boundary from repeatedly changing workers.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Container : MonoBehaviour
    {
        [Tooltip("Stable id used by the control plane. Must be unique in the scene. On an entity's root it is a label: the runtime id is '<label>#<carrier net id>'.")]
        public string ContainerId = "container";
        [Tooltip("Box size in local space, centred on Center.")]
        public Vector3 Size = new Vector3(20f, 10f, 20f);
        public Vector3 Center = new Vector3(0f, 5f, 0f);

        [Tooltip("Who simulates what is inside. Auto: leased for a fixed box, inherited for a box an entity carries (what baked, runtime and carried containers always did). Leased: its own lease and owner, dealt by the planner. Inherited: whoever owns the parent container.")]
        public ContainerAuthority Authority = ContainerAuthority.Auto;
        [Tooltip("Give this container a physics scene of its own in which it stands still: everything inside is simulated in container-local coordinates, with the container's own up as gravity. Use it for ships, stations and planets; a small vehicle is cheaper without one.")]
        public bool OwnPhysicsFrame;
        [Tooltip("Frame only: the interior geometry (colliders, visuals; no NetworkIdentity) instantiated into the frame's physics scene. Empty: the carrier's static colliders are cloned into the frame.")]
        public GameObject FrameContent;
        [Tooltip("Frame only: WithCarrier publishes everything inside with the carrier (ships); OwnRegions buckets it in regions of the frame itself (planets).")]
        public FrameInterestMode FrameInterest = FrameInterestMode.WithCarrier;

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

        /// <summary>
        /// What the game tells the planner about this container (<see cref="ContainerHint"/>): the baked value, from
        /// the world manifest or the scene. The orchestrator prefers the control-plane row when one was set while
        /// the mesh runs (<see cref="IControlPlane.SetContainerHint"/>).
        /// </summary>
        public ContainerHint Hint = ContainerHint.Default;

        /// <summary>
        /// Registered as a carried container on this process: its entity has spawned here, and the box is named on the
        /// wire by the entity's net id. A container on an entity's root that has not spawned (a prefab asset, a copy not
        /// yet spawned) is <see cref="FrameMode"/> <see cref="ContainerFrameMode.Entity"/> but not yet dynamic.
        /// </summary>
        public bool IsDynamic { get; internal set; }

        /// <summary>
        /// Where the box is (<c>docs/container-tree.md</c> D1): <see cref="ContainerFrameMode.Entity"/> when the container
        /// sits on an entity's root, next to its <see cref="NetworkIdentity"/>, which then carries it; otherwise
        /// <see cref="ContainerFrameMode.Fixed"/>. It is read from the object, never authored: an entity's own box
        /// cannot be fixed.
        /// </summary>
        public ContainerFrameMode FrameMode => IsDynamic || IsOnEntity ? ContainerFrameMode.Entity : ContainerFrameMode.Fixed;

        /// <summary>Where the container comes from: baked, registered at runtime, or part of a prefab.</summary>
        public ContainerSource Source => FrameMode == ContainerFrameMode.Entity ? ContainerSource.Prefab : IsRuntime ? ContainerSource.Runtime : ContainerSource.Baked;

        /// <summary><see cref="Authority"/> with <see cref="ContainerAuthority.Auto"/> resolved: leased for a fixed frame, inherited for an entity frame.</summary>
        public ContainerAuthority ResolvedAuthority =>
            Authority != ContainerAuthority.Auto ? Authority : FrameMode == ContainerFrameMode.Entity ? ContainerAuthority.Inherited : ContainerAuthority.Leased;

        /// <summary>
        /// Whether a <see cref="NetworkIdentity"/> sits on this object. Looked up once while playing (components do not
        /// come and go on a spawned entity), every time in the Editor, where they do.
        /// </summary>
        internal bool IsOnEntity
        {
            get
            {
                if (_entityKnown) return _onEntity;
                bool on = GetComponent<NetworkIdentity>() != null;
                if (Application.isPlaying) { _onEntity = on; _entityKnown = true; }
                return on;
            }
        }

        /// <summary>Whether a container on <paramref name="go"/> would be carried by an entity: the same rule as <see cref="FrameMode"/>, for code that has only the object (baking, export).</summary>
        public static bool IsEntityObject(GameObject go) => go != null && go.GetComponent<NetworkIdentity>() != null;

        [NonSerialized] private bool _entityKnown, _onEntity;

        // ---------------------------------------------------------------------------------- carriers' bodies

        private static readonly Dictionary<Rigidbody, Container> ByBody = new Dictionary<Rigidbody, Container>();

        internal static void ResetForNewSession() => ByBody.Clear();

        /// <summary>
        /// The container carried by the entity that owns <paramref name="body"/>, or null. Game movement code that
        /// treats colliders without a Rigidbody as the level can use this to walk on a vehicle's floor as well.
        /// </summary>
        public static Container OfRigidbody(Rigidbody body) => body != null && ByBody.TryGetValue(body, out var c) ? c : null;

        /// <summary>Whether <paramref name="collider"/> belongs to the carrier of a registered container (the hull of a vehicle, the floor of a lift).</summary>
        public static bool IsCarrierGeometry(Collider collider) =>
            collider != null && collider.attachedRigidbody != null && ByBody.ContainsKey(collider.attachedRigidbody);

        internal static void RegisterBody(Rigidbody body, Container carried)
        {
            if (body != null && carried != null) ByBody[body] = carried;
        }

        internal static void UnregisterBody(Rigidbody body)
        {
            if (!ReferenceEquals(body, null)) ByBody.Remove(body);
        }

        /// <summary>
        /// Whether this container is simulated under a lease of its own right now. A fixed container that resolves to
        /// leased always is; a carried container is once its row is pinned to a worker (an authored-leased one pins
        /// itself when its carrier spawns). Either way it is not while <see cref="AuthorityDemoted"/>: a leased
        /// container under a moving parent without a physics frame of its own is simulated as inherited
        /// (<c>docs/container-tree.md</c> D7).
        /// </summary>
        public bool IsLeased => UsesOwnLease && !InMovingSpace;

        private bool UsesOwnLease => IsDynamic ? IsPinned : ResolvedAuthority == ContainerAuthority.Leased;

        /// <summary>
        /// The container would be leased, but sits under a moving parent that has no physics frame of its own, so
        /// its owner would simulate against a pose one replication delay old: it is simulated as inherited instead
        /// until it leaves (<c>docs/container-tree.md</c> D7).
        /// </summary>
        public bool AuthorityDemoted => UsesOwnLease && InMovingSpace;

        /// <summary>
        /// Some ancestor moves (it is carried by an entity) and no container between here and it has a physics frame
        /// of its own: the box is somewhere whose pose only the moving ancestor's authority knows exactly.
        /// </summary>
        public bool InMovingSpace
        {
            get
            {
                int hops = 0;
                for (var p = Parent; p != null; p = p.Parent)
                {
                    if (p.OwnPhysicsFrame) return false;
                    if (p.IsDynamic) return true;
                    if (++hops > ContainerRegistry.ChainBound) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// The container this one sits in, or null for a root of its scope (<c>docs/container-tree.md</c> D2). A baked
        /// container's parent is the smallest baked box of its scope enclosing it, a runtime container's is named on
        /// its lease row, and a carried container's is wherever its carrier stands, so it changes as the carrier moves.
        /// </summary>
        public Container Parent => IsDynamic ? (Carrier != null ? Carrier.Container : null) : FixedParent;

        /// <summary>Baked and runtime containers: the parent assigned at registration.</summary>
        internal Container FixedParent;
        internal readonly List<Container> FixedChildren = new List<Container>();

        /// <summary>
        /// The fixed containers whose parent this is (baked and runtime). Carried containers are not listed: they come
        /// and go with the entities standing in <see cref="Entities"/>, whose <see cref="NetworkIdentity.Carried"/> they are.
        /// </summary>
        public IReadOnlyList<Container> Children => FixedChildren;

        /// <summary>How many containers are above this one: 0 for a root of its scope. Resolution prefers the deepest box (D3).</summary>
        public int Depth
        {
            get
            {
                int depth = 0;
                for (var p = Parent; p != null && depth <= ContainerRegistry.ChainBound; p = p.Parent) depth++;
                return depth;
            }
        }

        /// <summary>
        /// The container whose physics frame this box lives in: the nearest ancestor with <see cref="OwnPhysicsFrame"/>,
        /// or null when the box is in its scope's own space (<c>docs/container-tree.md</c> §3). A framed container's own
        /// box lives in its parent's space; what it holds lives in its frame.
        /// </summary>
        public Container Space
        {
            get
            {
                int hops = 0;
                for (var p = Parent; p != null && hops <= ContainerRegistry.ChainBound; p = p.Parent, hops++)
                    if (p.OwnPhysicsFrame) return p;
                return null;
            }
        }

        /// <summary>The space what this container holds lives in: its own frame when it has one, otherwise its <see cref="Space"/>.</summary>
        public Container InnerSpace => OwnPhysicsFrame ? this : Space;

        /// <summary>
        /// The transform what this container holds is parented under: the root of its physics frame when it has one
        /// (<see cref="PhysicsFrame.Root"/>), otherwise its own transform. Entities and fixed child containers hang
        /// here, so their local pose is their container-local pose either way.
        /// </summary>
        public Transform ContentRoot => Frame != null && Frame.Root != null ? Frame.Root : transform;

        /// <summary>The physics frame this container owns (<see cref="OwnPhysicsFrame"/>) on this process, or null while it has none.</summary>
        public PhysicsFrame Frame { get; internal set; }
        /// <summary>
        /// Registered by the game while the mesh runs (<see cref="ContainerRegistry.RegisterRuntime"/>): a static box
        /// that is not in the baked set. Leased and owned like a baked container; named on the wire by
        /// <see cref="RuntimeId"/> (<see cref="Index"/> is <see cref="ContainerRef.RuntimeIndex"/>).
        /// </summary>
        public bool IsRuntime { get; internal set; }
        /// <summary>Runtime containers: the 64-bit id the game registered this container under. 0 otherwise.</summary>
        public ulong RuntimeId { get; internal set; }
        /// <summary>
        /// Private simulation scope, or zero for the public world. Carried containers follow their carrier, through
        /// every carrier above it. A carrier chain that loops back on itself has no scope and reads as zero.
        /// </summary>
        public ulong InstanceId => ScopeRoot?.Instance?.InstanceId ?? 0;
        /// <summary>
        /// The opaque scope key of the scope this container belongs to (<see cref="EntityLocation.ScopeKey"/>):
        /// <see cref="EntityLocation.PublicScope"/> (empty) for the public world, the instance key for an instance
        /// container. Carried containers follow their carrier, through every carrier above it. Never null.
        /// </summary>
        public string ScopeKey => ScopeRoot?.Instance?.ScopeKey ?? EntityLocation.PublicScope;

        /// <summary>
        /// The container this one's scope is read from: itself for a static or runtime container, and for a
        /// dynamic one the container at the bottom of its carrier chain (the ship's hangar, then the ship, then the
        /// static box the ship is in). Null when a carrier in the chain is in no container, or when the chain loops
        /// back on itself. A loop cannot be built through <see cref="NetworkIdentity"/>, which refuses it, so the
        /// bound only turns corrupt state into "no scope" instead of an endless walk: every link of a chain is a
        /// registered dynamic container, so a chain longer than the registry holds has revisited one.
        /// </summary>
        internal Container ScopeRoot
        {
            get
            {
                var c = this;
                for (int hops = 0; ; hops++)
                {
                    if (hops > ContainerRegistry.ChainBound) return null;
                    if (c.IsDynamic)
                    {
                        if (c.Carrier == null) return c;
                        c = c.Carrier.Container;
                        if (c == null) return null;
                    }
                    else if (c.FixedParent != null) c = c.FixedParent;
                    else return c;
                }
            }
        }

        /// <summary>
        /// Whether <paramref name="entity"/> carries this container, directly or through a chain of carriers: this
        /// is <paramref name="entity"/>'s own box, or a box riding somewhere inside it (a shuttle parked in its
        /// hangar, a crate's box in that shuttle). Putting <paramref name="entity"/> in such a container would make
        /// it ride inside itself; <see cref="ContainerRegistry.Find"/> skips these containers for the entity and
        /// the entity refuses to be placed in one. A chain longer than the registry holds has already looped and
        /// also answers true, so nothing is ever added to it.
        /// </summary>
        public bool IsCarriedBy(NetworkIdentity entity)
        {
            if (entity == null) return false;
            // The whole subtree of the entity's box, not only carriers inside carriers: a fixed room registered in a
            // ship's frame is as much the ship's as a shuttle in its hangar (docs/container-tree.md D4).
            int hops = 0;
            ulong netId = entity.NetId;
            for (var c = this; c != null; c = c.Parent)
            {
                // The same entity, not only the same object: another process's copy of it (a test mesh holds several
                // in one process) carries the same box.
                if (c.IsDynamic && (c.Carrier == entity || (netId != 0 && c.CarrierNetId == netId))) return true;
                if (++hops > ContainerRegistry.ChainBound) return true;
            }
            return false;
        }
        public InstanceContainerInfo Instance { get; internal set; }
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

        /// <summary>
        /// Append everything inside this container on this process at every carrier depth to <paramref name="into"/>:
        /// its own <see cref="Entities"/>, then what rides in each box one of them carries (the crew of a ship parked
        /// here, the passengers of a shuttle in that ship's hangar), and so on down. Breadth first, so a carrier is
        /// always listed before everything riding in it; walk the list backwards to visit riders before their
        /// carriers. With <paramref name="throughAuthoritativeCarriersOnly"/>, only boxes whose carrier this process
        /// is authoritative for are opened: what would leave with the carrier if this worker despawned it.
        /// <para>
        /// Bounded like every other carrier walk: a cycle cannot be built (see <see cref="IsCarriedBy"/>), and
        /// opening more boxes than the registry holds could only mean one, so the walk stops there.
        /// </para>
        /// </summary>
        internal void CollectContents(List<NetworkIdentity> into, bool throughAuthoritativeCarriersOnly)
        {
            int start = into.Count;
            int opened = 0, bound = ContainerRegistry.ChainBound;
            AddBox(this, into, ref opened, bound);
            for (int i = start; i < into.Count; i++)
            {
                var e = into[i];
                if (e == null || (throughAuthoritativeCarriersOnly && !e.HasAuthority)) continue;
                var carried = e.Carried;
                if (carried == null || !carried.IsDynamic || carried == this) continue;
                if (++opened > bound) break;
                AddBox(carried, into, ref opened, bound);
            }
        }

        /// <summary>
        /// A box's own entities, then those of its inherited fixed children at any depth: they are simulated by
        /// whoever simulates the box. A leased child is somebody else's and is not opened (docs/container-tree.md D6).
        /// </summary>
        private static void AddBox(Container box, List<NetworkIdentity> into, ref int opened, int bound)
        {
            into.AddRange(box.Entities);
            for (int c = 0; c < box.FixedChildren.Count; c++)
            {
                var child = box.FixedChildren[c];
                if (child == null || child.IsLeased) continue;
                if (++opened > bound) return;
                AddBox(child, into, ref opened, bound);
            }
        }

        private string _ownerWorkerId = "";
        private ushort _ownerWorkerIndex = ushort.MaxValue;
        private ulong _leaseEpoch;

        // Cost telemetry (docs/cost-telemetry.md). The counters live here rather than in a dictionary keyed by
        // container id because they are touched once per authoritative entity per tick, and a field add is free
        // where a string hash is not. ContainerCostMeter owns them: it is the only thing that reads or clears them.
        internal long CostSimTicks;
        internal long CostReplicationBytes;
        internal long CostGatewayBytes;
        internal bool CostTracked;

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
                if (IsLeased) return _ownerWorkerId;
                if (IsDynamic)
                {
                    if (Carrier == null) return "";
                    var resolve = ContainerRegistry.WorkerIdByIndex;
                    return resolve != null ? resolve(Carrier.OwnerWorkerIndex) ?? "" : "";
                }
                var parent = FixedParent;
                return parent != null ? parent.OwnerWorkerId : _ownerWorkerId;
            }
            internal set => _ownerWorkerId = value ?? "";
        }

        /// <summary>Index of the owning worker (<see cref="ushort.MaxValue"/> when unowned), derived as <see cref="OwnerWorkerId"/> is.</summary>
        public ushort OwnerWorkerIndex
        {
            get
            {
                if (IsLeased) return _ownerWorkerIndex;
                if (IsDynamic) return Carrier != null ? Carrier.OwnerWorkerIndex : ushort.MaxValue;
                var parent = FixedParent;
                return parent != null ? parent.OwnerWorkerIndex : _ownerWorkerIndex;
            }
            internal set => _ownerWorkerIndex = value;
        }

        /// <summary>Lease epoch from the control plane; derived as <see cref="OwnerWorkerId"/> is (a carried container following its carrier: the carrier's authority epoch).</summary>
        public ulong LeaseEpoch
        {
            get
            {
                if (IsLeased) return _leaseEpoch;
                if (IsDynamic) return Carrier != null ? Carrier.Epoch : 0;
                var parent = FixedParent;
                return parent != null ? parent.LeaseEpoch : _leaseEpoch;
            }
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
            if (!_cached || (MayMove && (_cacheFrame != Time.frameCount || FrameMoved))) RefreshCache();
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
                int hops = 0;
                for (var c = this; c != null && hops <= ContainerRegistry.ChainBound; c = c.Parent, hops++)
                    if (c.IsDynamic && c.transform.hasChanged) return true;
                return false;
            }
        }

        /// <summary>
        /// The box can move without being re-registered: it is carried, or some container above it is (a room fixed
        /// in a ship), or it lives inside a physics frame (whose root a client poses for rendering, D11).
        /// </summary>
        internal bool MayMove
        {
            get
            {
                if (IsDynamic) return true;
                int hops = 0;
                for (var p = FixedParent; p != null && hops <= ContainerRegistry.ChainBound; p = p.Parent, hops++)
                    if (p.IsDynamic || p.OwnPhysicsFrame) return true;
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
            return BoxDistance(_worldToLocal.MultiplyPoint3x4(worldPosition) - Center, Size);
        }

        /// <summary>
        /// <see cref="SignedDistance"/> for a point already in this container's local coordinates: the inner face of a
        /// container with a physics frame of its own, whose contents are in exactly those coordinates (<c>docs/container-tree.md</c> §3).
        /// </summary>
        public float SignedDistanceInner(Vector3 local) => BoxDistance(InnerLocal(local) - Center, Size);

        /// <summary><see cref="Contains"/> for a point in this container's local coordinates (the inner face of its frame).</summary>
        public bool ContainsInner(Vector3 local) => BoxDistance(InnerLocal(local) - Center, Size) <= 0f;

        /// <summary>A simulation-space point of this container's frame as a container-local one: the frame's origin (D19) comes off.</summary>
        private Vector3 InnerLocal(Vector3 position) => Frame != null ? Frame.SimulationToLocal(position) : position;

        /// <summary>Whether a point of <paramref name="space"/> is in this box: its inner face when the box owns the space.</summary>
        internal bool ContainsIn(Vector3 point, Container space) => space == this ? ContainsInner(point) : Contains(point);

        private static float BoxDistance(Vector3 local, Vector3 size)
        {
            var h = size * 0.5f;
            var d = new Vector3(Mathf.Abs(local.x) - h.x, Mathf.Abs(local.y) - h.y, Mathf.Abs(local.z) - h.z);
            if (d.x > 0f || d.y > 0f || d.z > 0f)
                return new Vector3(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f), Mathf.Max(d.z, 0f)).magnitude;
            return Mathf.Max(d.x, d.z);
        }

        /// <summary>
        /// How far a point inside this container is from its seam with <paramref name="neighbor"/>. For a sibling or
        /// an inner box that is the neighbor's surface; for a neighbor that encloses this one (a building inside
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
            if (other.Parent == this) return true;
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

        /// <summary>
        /// How many carriers this container sits inside, walking up through <see cref="Enclosing"/>: 0 for a
        /// static container, 1 for a ship in a static container, 2 for a shuttle inside that ship. There is no
        /// depth limit. This is what orders simulation and the ghost band, and two containers at
        /// different depths reported as equal would let a passenger tick before the ship it stands in. The walk
        /// cannot run away: every dynamic container is registered, so a chain longer than the registry has closed
        /// a cycle. That is the only bound; a cycle cannot be built in the first place (see <see cref="IsCarriedBy"/>).
        /// </summary>
        public int NestingDepth
        {
            get
            {
                // Carriers along the whole parent chain: a room fixed inside a ship ticks after the ship, like a
                // shuttle in its hangar does.
                int depth = 0, hops = 0, bound = ContainerRegistry.Dynamic.Count + 1;
                for (var c = this; c != null && hops <= ContainerRegistry.ChainBound && depth <= bound; c = c.Parent, hops++)
                    if (c.IsDynamic) depth++;
                return depth;
            }
        }

        public override string ToString() => $"{ContainerId}[{(IsDynamic || IsRuntime ? Ref.ToString() : Index.ToString())}]->{(string.IsNullOrEmpty(OwnerWorkerId) ? "unassigned" : OwnerWorkerId)}";

        /// <summary>A fresh random container id, e.g. "container-3f9a1c07".</summary>
        public static string NewContainerId() => "container-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// Set <see cref="Size"/> and <see cref="Center"/> to the local-space box around this object's enabled
        /// renderers and those of its children, falling back to their colliders when there are no renderers. Returns
        /// false and leaves the box unchanged when neither is found (an empty object keeps its authored box).
        /// </summary>
        public bool FitToBounds()
        {
            var worldToLocal = transform.worldToLocalMatrix;
            bool any = false;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;

            void Encapsulate(Bounds b, Matrix4x4 toLocal)
            {
                var c = b.center;
                var e = b.extents;
                for (int i = 0; i < 8; i++)
                {
                    var p = toLocal.MultiplyPoint3x4(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                any = true;
            }

            // Renderer.localBounds is in the renderer's own space, so a child rotated with the container stays tight.
            foreach (var r in GetComponentsInChildren<Renderer>())
                if (r.enabled) Encapsulate(r.localBounds, worldToLocal * r.localToWorldMatrix);
            if (!any)
                foreach (var col in GetComponentsInChildren<Collider>())
                    if (col.enabled) Encapsulate(col.bounds, worldToLocal);
            if (!any) return false;

            Center = (min + max) * 0.5f;
            Size = max - min;
            _cached = false;
            return true;
        }

        // Called by the Editor when the component is added (or reset from its context menu): give it a unique id and
        // wrap the box around whatever the object already looks like.
        private void Reset()
        {
            ContainerId = NewContainerId();
            FitToBounds();
        }

        private void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = FrameMode == ContainerFrameMode.Entity ? new Color(1f, 0.7f, 0.2f, 0.35f) : IsRuntime ? new Color(0.5f, 1f, 0.4f, 0.35f) : new Color(0.2f, 0.8f, 1f, 0.35f);
            Gizmos.DrawWireCube(Center, Size);
        }

        /// <summary>
        /// Where a container sits decides what it is (<see cref="FrameMode"/>); say so when the placement cannot work. A
        /// box on a child object of an entity is baked as a fixed container, so it would never move with the entity; a
        /// box on an entity's root needs a root <see cref="NetworkTransform"/>, because every process positions the
        /// contents from the carrier's replicated pose. The Nebula validator reports the same.
        /// </summary>
        /// <returns>Null when the placement works; otherwise the problem, with <paramref name="error"/> set when the container cannot work at all.</returns>
        public static string PlacementProblem(Container c, out bool error)
        {
            error = false;
            if (c == null) return null;
            if (c.GetComponent<NetworkIdentity>() != null)
            {
                if (c.GetComponent<NetworkTransform>() != null) return null;
                error = true;
                return $"Container '{c.ContainerId}' on entity '{c.name}' needs a NetworkTransform on the same object: every process positions what the entity carries from its replicated pose";
            }
            var entity = c.transform.parent != null ? c.transform.parent.GetComponentInParent<NetworkIdentity>(true) : null;
            if (entity == null) return null;
            return $"Container '{c.ContainerId}' sits on '{c.name}', a child of entity '{entity.name}': it is baked as a fixed container and will not move with the entity. Put it on the entity's root to have the entity carry it";
        }

#if UNITY_EDITOR
        [NonSerialized] private string _reportedProblem;

        private void OnValidate()
        {
            _entityKnown = false;
            _cached = false;
            // After the edit that triggered this is complete: adding a Container and then a NetworkTransform to an
            // entity (by hand or from a setup script) is one change, not an error followed by a fix.
            UnityEditor.EditorApplication.delayCall -= ReportPlacement;
            UnityEditor.EditorApplication.delayCall += ReportPlacement;
        }

        private void ReportPlacement()
        {
            if (this == null || Application.isPlaying || !IsAuthored()) return;
            string problem = PlacementProblem(this, out bool error);
            if (problem == _reportedProblem) return; // once per change, not on every edit
            _reportedProblem = problem;
            if (problem == null) return;
            if (error) Debug.LogError(problem, this);
            else Debug.LogWarning(problem, this);
        }

        // Something a person edits: a prefab asset, an object in the prefab editor, or one in a saved scene. Objects that
        // code builds in an unsaved scene (tests, tools) are checked by the validator when it runs, not on every change.
        private bool IsAuthored() =>
            UnityEditor.EditorUtility.IsPersistent(this)
            || UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject) != null
            || (gameObject.scene.IsValid() && !string.IsNullOrEmpty(gameObject.scene.path));
#endif
    }
}
