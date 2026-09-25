using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>Motion shared by scripted movement, prediction, physics, telemetry, and persistence.</summary>
    public sealed class NetworkMotionState
    {
        /// <summary>
        /// Linear velocity in the entity's own space: frame-local (relative to the ship) inside a physics frame
        /// (<see cref="NetworkIdentity.Space"/>), the scope's own space everywhere else. This is only streamed when a root
        /// NetworkTransform opts in. A change of space converts it (<see cref="PhysicsFrames.ConvertVelocity"/>).
        /// </summary>
        public Vector3 Velocity;
    }

    /// <summary>
    /// Marks a GameObject as a networked entity. Holds the entity's network identity (a 64-bit ID created by the
    /// worker that spawned it, never by a central allocator), its current container, its authority epoch and
    /// which worker/client owns it. Every <see cref="NetworkBehaviour"/> on the object hangs off this.
    /// Add <see cref="NetworkTransform"/> for ongoing transform replication and <see cref="NetworkRigidbody"/>
    /// for worker-simulated physics. An identity alone only synchronizes placement at spawn and container changes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkIdentity : MonoBehaviour
    {
        [Tooltip("Set automatically from the NebulaConfig prefab list at spawn time.")]
        public ushort PrefabId = ushort.MaxValue;
        [Tooltip("Non-zero for an entity authored into a scene (assigned when the scene is saved). The worker owning its container spawns it; clients and neighbours bind to their own copy of the scene object. Leave 0 on prefabs.")]
        public uint SceneId;

        [Header("Interest")]
        [Tooltip("How far clients hear about this entity, in metres. 0 uses NebulaConfig.InterestRadius. Raise it for something players should see from far away (a dropship, a boss); the gateway clamps it to InterestMaxRadius. A radius above the mesh default costs more to evaluate, so use it sparingly.")]
        public float RelevanceRadius = 0f;
        [Tooltip("Send this entity to every client whatever the distance. For the few objects a game cannot cull: a match timer, a world boss, a weather driver.")]
        public bool AlwaysRelevant;
        [Tooltip("A number your interest policy can filter on (team markers, quest objects). Nebula only carries it; 0 means no group.")]
        public byte InterestGroup;

        [Header("Cohesion")]
        [Tooltip("Entities sharing a non-zero cohesion group must be simulated by one worker: a handover of any member takes the others with it, and the planner treats their containers as one item. Set it here for a group that is authored (a ragdoll, a turret and its mount) or call JoinCohesionGroup at runtime. 0 means no group. See https://nebula.1by3.co/docs/guides/cohesion")]
        [SerializeField] private uint _cohesionGroup;

        [Header("Cost")]
        [Tooltip("What this entity costs to simulate, as a multiplier on its category weight in NebulaConfig.CostWeights. 1 (the default) leaves balancing exactly as it was; 8 on a raid boss and 0.25 on an ambient critter tell the planner and the scaler that they are not the same machine. NebulaCost.EntityWeight can override it at spawn; NetworkIdentity.SetCostWeight overrides both.")]
        public float CostWeight = 1f;

        /// <summary>
        /// The multiplier this entity actually carries right now (see <see cref="NebulaCost"/> for how it is
        /// chosen). It is computed at spawn, travels with the entity through ghosting and handover, and only
        /// changes when the game calls <see cref="SetCostWeight"/>: never per tick. Multiply it by the entity's
        /// category weight in <see cref="CostWeights"/> to get what the mesh thinks it costs.
        /// </summary>
        public float EffectiveCostWeight { get; private set; } = 1f;

        /// <summary>
        /// Say what this entity costs from now on, overruling both <see cref="CostWeight"/> and
        /// <see cref="NebulaCost.EntityWeight"/>. The value is clamped to [0, <see cref="NebulaCost.MaxWeight"/>]
        /// and travels with the entity: the worker it hands over to reports the same cost for it.
        /// </summary>
        public void SetCostWeight(float weight)
        {
            EffectiveCostWeight = NebulaCost.Clamp(weight);
            _costWeightPinned = true;
        }

        /// <summary>Re-ask <see cref="NebulaCost"/> for this entity's weight, unless the game pinned one with <see cref="SetCostWeight"/>.</summary>
        internal void RecomputeCostWeight()
        {
            if (_costWeightPinned) return;
            EffectiveCostWeight = NebulaCost.Evaluate(this);
        }

        /// <summary>
        /// Take the weight that arrived with the entity's state (a ghost spawn or a handover). It is pinned: the
        /// receiving worker does not re-ask the callback, so an entity reports the same cost wherever it is
        /// simulated (docs/cost-telemetry.md, D4).
        /// </summary>
        internal void ApplyCarriedCostWeight(float weight)
        {
            EffectiveCostWeight = NebulaCost.Clamp(weight);
            _costWeightPinned = true;
        }

        private bool _costWeightPinned;

        [Header("Ghost band")]
        [Tooltip("How the ghost band measures this entity. None (the default) measures from the entity's root position. Explicit measures from the Extent box. Colliders measures from a box around the entity's own colliders. Give a large entity (a building, a structure, a big ship) an extent so that every worker whose container its body comes near holds a ghost of it. See https://nebula.1by3.co/docs/concepts/containers-and-handover")]
        [SerializeField] private EntityExtentSource _extentSource;
        [Tooltip("The extent box in the entity's local space, used when the source is Explicit. The root's rotation and scale apply to it, as they do to a BoxCollider on the root.")]
        [SerializeField] private Bounds _extent = new Bounds(Vector3.zero, Vector3.one);

        /// <summary>
        /// With <see cref="EntityExtentSource.Colliders"/>, the box is computed again at least this often, in ticks
        /// of the worker that holds authority. Nothing tells Nebula that a collider deeper than a direct child
        /// changed; call <see cref="RefreshExtent"/> when a change must count sooner.
        /// </summary>
        public const int ColliderExtentRefreshTicks = 60;

        private Bounds _colliderExtent;
        private bool _hasColliderExtent;
        private bool _colliderExtentStale = true;
        private uint _colliderExtentTick;

        /// <summary>
        /// Where this entity's extent comes from (<c>docs/entity-extents.md</c>). With
        /// <see cref="EntityExtentSource.None"/>, the default, the ghost band measures from the entity's root
        /// position, as it always has. With an extent it measures from the extent's box, so the entity is ghosted to
        /// every worker whose container the box comes within <see cref="NebulaConfig.GhostBandMargin"/> of. An
        /// extent never changes which worker has authority: that still follows the root position.
        /// </summary>
        public EntityExtentSource ExtentSource => _extentSource;

        /// <summary>
        /// The extent was set at runtime (<see cref="SetExtent"/>, <see cref="UseColliderExtent"/>,
        /// <see cref="ClearExtent"/>, or received with a handover or a restore) rather than authored. Only such an
        /// extent is carried by a handover and saved by a <see cref="PersistentEntity"/>; an authored one comes with
        /// the prefab on every process.
        /// </summary>
        public bool ExtentChangedAtRuntime { get; private set; }

        /// <summary>
        /// The extent box in the entity's local space: the authored or set box for
        /// <see cref="EntityExtentSource.Explicit"/>, the box around the colliders for
        /// <see cref="EntityExtentSource.Colliders"/> (computed now if it never was), and an empty box at the origin
        /// when there is none. Use <see cref="TryGetExtent"/> to tell "no extent" apart.
        /// </summary>
        public Bounds Extent => TryGetExtent(out var box) ? box : default;

        /// <summary>
        /// The extent box in the entity's local space, if the entity has one: always for
        /// <see cref="EntityExtentSource.Explicit"/>, and for <see cref="EntityExtentSource.Colliders"/> when at least
        /// one collider counts (enabled, not a trigger, and not under another <see cref="NetworkIdentity"/>).
        /// </summary>
        public bool TryGetExtent(out Bounds localBounds)
        {
            switch (_extentSource)
            {
                case EntityExtentSource.Explicit:
                    localBounds = _extent;
                    return true;
                case EntityExtentSource.Colliders:
                    if (_colliderExtentStale) ComputeColliderExtent(_colliderExtentTick);
                    localBounds = _colliderExtent;
                    return _hasColliderExtent;
                default:
                    localBounds = default;
                    return false;
            }
        }

        /// <summary>
        /// Give the entity an explicit extent: a box in its local space (the root's rotation and scale apply to it).
        /// Call it on the authority when the entity's shape changes, for example after a structure is edited; the next
        /// tick's ghost band uses it. The box travels with a handover and is saved by a <see cref="PersistentEntity"/>.
        /// A negative size is taken as its absolute value.
        /// </summary>
        public void SetExtent(Bounds localBounds)
        {
            var size = localBounds.size;
            localBounds.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            _extentSource = EntityExtentSource.Explicit;
            _extent = localBounds;
            ChangedAtRuntime();
        }

        /// <summary>
        /// Measure the entity by its own colliders from now on: every enabled, non-trigger collider on an active
        /// object under it whose nearest <see cref="NetworkIdentity"/> is this one. The box is computed now and
        /// again every <see cref="ColliderExtentRefreshTicks"/> ticks, when a direct child is added or removed, and
        /// when <see cref="RefreshExtent"/> is called.
        /// </summary>
        public void UseColliderExtent()
        {
            _extentSource = EntityExtentSource.Colliders;
            _colliderExtentStale = true;
            ChangedAtRuntime();
        }

        /// <summary>Remove the entity's extent: the ghost band measures from its root position again, as for any entity.</summary>
        public void ClearExtent()
        {
            _extentSource = EntityExtentSource.None;
            ChangedAtRuntime();
        }

        /// <summary>
        /// Compute a <see cref="EntityExtentSource.Colliders"/> extent again now, after the game added, removed,
        /// resized or switched a collider. Does nothing for another source.
        /// </summary>
        public void RefreshExtent()
        {
            if (_extentSource == EntityExtentSource.Colliders) _colliderExtentStale = true;
        }

        private void ChangedAtRuntime()
        {
            ExtentChangedAtRuntime = true;
            Persistent?.MarkDirty();
        }

        /// <summary>
        /// The ghost band's read of the extent on the authority at <paramref name="tick"/>: a collider extent is
        /// computed again when it is stale or <see cref="ColliderExtentRefreshTicks"/> old.
        /// </summary>
        internal bool TryGetExtentForBand(uint tick, out Bounds localBounds)
        {
            if (_extentSource == EntityExtentSource.None) { localBounds = default; return false; }
            if (_extentSource == EntityExtentSource.Colliders && (_colliderExtentStale || tick - _colliderExtentTick >= ColliderExtentRefreshTicks))
                ComputeColliderExtent(tick);
            return TryGetExtent(out localBounds);
        }

        private void ComputeColliderExtent(uint tick)
        {
            _hasColliderExtent = EntityExtents.TryComputeColliderExtent(this, out _colliderExtent);
            _colliderExtentStale = false;
            _colliderExtentTick = tick;
        }

        /// <summary>
        /// Take an extent that arrived with the entity (a handover, a restore): the sender's runtime extent replaces
        /// the prefab's. It counts as set at runtime, so it travels on, but it does not mark the entity dirty.
        /// </summary>
        internal void ApplyCarriedExtent(EntityExtentSource source, Bounds localBounds)
        {
            _extentSource = source;
            if (source == EntityExtentSource.Explicit) _extent = localBounds;
            _colliderExtentStale = true;
            ExtentChangedAtRuntime = true;
        }

        // A direct child came or went: a Colliders extent is computed again at the next ghost band.
        private void OnTransformChildrenChanged() => _colliderExtentStale = true;

        /// <summary>Authored into a scene rather than spawned from a prefab: the object belongs to its scene and is bound, never instantiated or destroyed, by the network (see <see cref="SceneEntities"/>).</summary>
        public bool IsSceneEntity => SceneId != 0;

        public ulong NetId { get; internal set; }
        public ulong OwnerClientId { get; internal set; }
        /// <summary>
        /// The owning player's identity across sessions (<see cref="PlayerIdentity"/>): the same string every time
        /// that player connects, on every worker and client. Empty for entities no player owns. Key player records
        /// on it (for example a <see cref="PersistentEntity.Key"/>); <see cref="OwnerClientId"/> changes every session.
        /// </summary>
        public string OwnerIdentity { get; internal set; } = "";
        /// <summary>The owning client is a headless bot. Informational (dashboard/overlay); carried through handover.</summary>
        public bool OwnerIsBot { get; internal set; }
        /// <summary>
        /// No client owns this entity: whichever worker is authoritative drives it (a <see cref="PredictedBehaviour{TInput}"/>
        /// gets its input from <c>GatherServerInput</c>). What the game calls it (NPC, monster, vehicle) is up to the
        /// game; it costs no client process and no gateway connection. Carried through ghosting and handover.
        /// </summary>
        public bool IsServerDriven { get; internal set; }
        /// <summary>Monotonic authority epoch; bumped on every authority change. Stale-epoch messages are dropped everywhere.</summary>
        public uint Epoch { get; internal set; }
        /// <summary>
        /// The cohesion group this entity belongs to, or 0 for none: the game's promise that every member is
        /// simulated by one worker and moves between workers as a unit (<see cref="CohesionGroups"/>,
        /// <c>docs/cohesion-hints.md</c>). Authored in the inspector or set at runtime with
        /// <see cref="JoinCohesionGroup"/>; it travels with the entity in the spawn, ghost and handover messages,
        /// so every worker holding a copy knows it. The ids are the game's own - any non-zero number both sides of
        /// an interaction agree on.
        /// </summary>
        public uint CohesionGroup => _cohesionGroup;

        /// <summary>
        /// Put this entity in a cohesion group, leaving the one it was in. The authority's value is the one that
        /// counts: it is what a handover expands and what the worker reports to the orchestrator. Ghosts and client
        /// replicas learn the new value with the next spawn, ghost spawn or handover of the entity, so join before
        /// the interaction that needs the group rather than in the middle of it (<c>docs/cohesion-hints.md</c>, D3).
        /// </summary>
        /// <param name="group">A non-zero group id. 0 is <see cref="LeaveCohesionGroup"/>.</param>
        public void JoinCohesionGroup(uint group)
        {
            if (_cohesionGroup == group) return;
            CohesionGroups.Unregister(this, _cohesionGroup);
            _cohesionGroup = group;
            if (Initialized) CohesionGroups.Register(this);
        }

        /// <summary>Leave the cohesion group this entity is in, if any. Nothing else about the entity changes.</summary>
        public void LeaveCohesionGroup() => JoinCohesionGroup(0);
        public Container Container { get; internal set; }
        /// <summary>
        /// The entity is fixed to <see cref="Container"/> (<see cref="FrameAttachment"/>): its authority's tick does not
        /// re-resolve its container from its position, and no <see cref="InstanceBoundary"/> moves it. The owner check
        /// still runs, so it is handed to whichever worker owns the container. Local to the authority; a handover or a
        /// restore sets it again (<c>docs/frame-bodies.md</c> D8).
        /// </summary>
        internal bool ContainerPinned;
        /// <summary>
        /// Nebula's own bookkeeping entity (the carrier of a chunk's <see cref="Nebula.World.ChunkState"/>), not
        /// something the game simulates: worker telemetry leaves it out of a container's occupancy and cost, so it
        /// never keeps a scope from looking idle (docs/chunk-state.md D7).
        /// </summary>
        internal bool ExcludeFromOccupancy;
        /// <summary>The entity's simulation scope. Zero is the public world.</summary>
        public ulong InstanceId => Container != null ? Container.InstanceId : 0;
        /// <summary>
        /// The opaque scope key of the entity's simulation scope (<see cref="Container.ScopeKey"/> of the current
        /// container): <see cref="EntityLocation.PublicScope"/> (empty) in the public world or in no container, the
        /// instance key inside an instance. Never null.
        /// </summary>
        public string ScopeKey => Container != null ? Container.ScopeKey : EntityLocation.PublicScope;
        /// <summary>
        /// Where the entity durably is right now (<see cref="EntityLocation"/>): its scope key, its container's id
        /// and its pose in that container's local space (<see cref="LocalPosition"/>, <see cref="LocalRotation"/>).
        /// The same value on every process that holds the entity, and the value <see cref="PersistedEntityRecord.Location"/>
        /// saves for a persistent entity in a static or runtime container; it does not change on a handover.
        /// </summary>
        public EntityLocation Location => EntityLocation.Of(Container, LocalPosition, LocalRotation);
        /// <summary>Use this scene for raycasts and overlap tests to exclude entities in other instances.</summary>
        public PhysicsScene PhysicsScene => gameObject.scene.GetPhysicsScene();

        /// <summary>
        /// The physics frame this entity lives in (<see cref="Nebula.Container.InnerSpace"/> of its container), or null in
        /// its scope's own space. Inside a frame its transform reads frame-local coordinates in simulation space
        /// (<c>docs/container-tree.md</c> D11).
        /// </summary>
        public Container Space => Container != null ? Container.InnerSpace : null;

        /// <summary>A position in this entity's space as a position in its scope's own space (<see cref="PhysicsFrames.ToScope"/>).</summary>
        public Vector3 ToScope(Vector3 position) => PhysicsFrames.ToScope(position, Space);

        /// <summary>A position in the scope's own space (a part of a ship, a door, a quest giver) in this entity's space.</summary>
        public Vector3 FromScope(Vector3 position) => PhysicsFrames.FromScope(position, Space);

        /// <summary>
        /// Authority: put the entity at a pose given in its scope's own space (a respawn point, a warp target), leaving
        /// any physics frame it is in first. Writing such a pose straight onto the transform of an entity inside a
        /// frame would put it at those numbers in the frame's coordinates. When the target is inside a frame (a ship's
        /// interior), the entity lands in that frame at the matching frame-local pose. Outside frames this is a plain
        /// assignment; the next tick resolves the container as usual.
        /// </summary>
        public void PlaceInScope(Vector3 position, Quaternion rotation)
        {
            if (Space != null && !PhysicsFrames.RendersFrames)
            {
                var target = ContainerRegistry.Find(position, null, InstanceId, this);
                SetContainer(target);
            }
            SetScopePose(position, rotation);
        }

        /// <summary>
        /// Write a pose given in the scope's own space onto the transform, converted into the space the entity is in
        /// now (<see cref="Space"/>). Called after a container change, so a target inside a physics frame lands at
        /// the frame-local pose that matches it, not at the scope pose's numbers in the frame's coordinates.
        /// </summary>
        internal void SetScopePose(Vector3 position, Quaternion rotation)
        {
            var space = Space;
            if (PhysicsFrames.InSimulationPose(space))
            {
                position = PhysicsFrames.FromScope(position, space);
                rotation = PhysicsFrames.Convert(rotation, null, space);
            }
            transform.SetPositionAndRotation(position, rotation);
        }
        /// <summary>Wire index of the current container (<see cref="ContainerRef.DynamicIndex"/> inside a dynamic one, <see cref="ushort.MaxValue"/> in none). Prefer <see cref="ContainerRef"/>.</summary>
        public ushort ContainerIndex => Container != null ? Container.Index : ushort.MaxValue;
        /// <summary>How the current container is named on the wire (see <see cref="Nebula.ContainerRef"/>).</summary>
        public ContainerRef ContainerRef => ContainerRef.Of(Container);
        /// <summary>
        /// The container this entity carries: the <see cref="Nebula.Container"/> on its root, which registers when the
        /// entity spawns on this process and unregisters when it despawns (a ship's interior, a lift). Null when the root
        /// has none. Not to be confused with <see cref="Container"/>, the container this entity is in.
        /// </summary>
        public Container Carried => _carried;
        private Container _carried;
        // The carrier's Rigidbody while its box is registered, so Container.OfRigidbody finds the box from a hit collider.
        private Rigidbody _carriedBody;
        public bool IsSpawned { get; internal set; }
        /// <summary>Worker side: this process is authoritative. Client side: always false.</summary>
        public bool HasAuthority { get; internal set; }
        /// <summary>Client side: owned by the local client.</summary>
        public bool IsLocalPlayer { get; internal set; }
        /// <summary>Index of the worker currently authoritative (as last heard). Debug/overlay only.</summary>
        public ushort OwnerWorkerIndex { get; internal set; }
        /// <summary>Velocity as reported by the authority; used for extrapolation and carried through handover.</summary>
        [Obsolete("Use Motion.Velocity, NetworkTransform.Velocity, or NetworkRigidbody.SetVelocity instead.")]
        public Vector3 Velocity { get => Motion.Velocity; set => Motion.Velocity = value; }
        public NetworkMotionState Motion { get; } = new NetworkMotionState();
        public NetworkTransform RootTransform { get; private set; }
        internal EntityStateEntry ReplicationState;
        internal bool HasReplicationState;
        internal uint LastStateTick;
        internal bool HasStateTick;
        private ContainerRef _publishedContainer;
        private uint _publishedEpoch;
        private bool _publishedLocation;
        private EntityStateEntry _pendingState;
        private uint _pendingStateTick;
        private ushort _pendingStateWorker;
        private bool _hasPendingState;

        internal void ReplayPendingState()
        {
            if (!_hasPendingState) return;
            var entry = _pendingState;
            _hasPendingState = false;
            ReceiveState(_pendingStateTick, _pendingStateWorker, entry);
        }

        internal void PrepareReplication(uint tick)
        {
            HasReplicationState = RootTransform != null && RootTransform.isActiveAndEnabled && RootTransform.CaptureRoot(tick, out ReplicationState);
            if (!_publishedLocation || _publishedContainer != ContainerRef || _publishedEpoch != Epoch)
            {
                ReplicationState = EntityStateEntry.Snapshot(this);
                ReplicationState.Fields |= TransformFields.Location | TransformFields.Reliable;
                HasReplicationState = true;
                _publishedLocation = true;
                _publishedContainer = ContainerRef;
                _publishedEpoch = Epoch;
            }
        }

        internal bool ReceiveState(uint tick, ushort worker, in EntityStateEntry entry)
        {
            if (entry.NetId != NetId) return false;
            if (entry.Epoch < Epoch || (entry.Epoch == Epoch && HasStateTick && tick <= LastStateTick)) return false;
            var container = ContainerRegistry.Resolve(entry.Container);
            if (container == null && entry.Container.MayArriveLater)
            {
                if (!_hasPendingState || entry.Epoch > _pendingState.Epoch || (entry.Epoch == _pendingState.Epoch && tick > _pendingStateTick))
                { _pendingState = entry; _pendingStateTick = tick; _pendingStateWorker = worker; _hasPendingState = true; }
                return false;
            }
            Epoch = entry.Epoch;
            OwnerWorkerIndex = worker;
            LastStateTick = tick;
            HasStateTick = true;
            if (RootTransform != null && RootTransform.IsOwnerAuthoritative && RootTransform.IsOwner)
            {
                // The owner never consumes the root interpolation buffer. Adopt the worker's
                // container/epoch while preserving the more recent locally simulated world pose.
                SetContainer(container);
                if (NebulaRuntime.IsServer) RecordGhostState(tick, container);
                return true;
            }
            bool location = (entry.Fields & TransformFields.Location) != 0;
            // An interpolating root transform carries a container change through its buffer (see
            // NetworkTransform.ReceiveRoot); placing the object here as well would jump it to the newest sample.
            bool smoothed = RootTransform != null && RootTransform.Interpolate && (entry.Fields & TransformFields.Teleport) == 0;
            if ((location && !smoothed) || RootTransform == null)
            {
                SetContainer(container);
                if (!(IsLocalPlayer && Predicted != null) && !(RootTransform != null && RootTransform.IsSyncAuthority))
                {
                    SetLocalPose(container, entry.LocalPosition, entry.LocalRotation);
                    transform.localScale = entry.LocalScale;
                    Motion.Velocity = entry.Velocity;
                }
                Interpolator?.Clear();
            }
            RootTransform?.ReceiveRoot(tick, container, entry);
            // The stream says what the owner had at its tick, so the entry is tagged with that tick, not with ours.
            if (NebulaRuntime.IsServer) RecordGhostState(tick, container);
            return true;
        }

        public NetworkBehaviour[] Behaviours { get; private set; } = Array.Empty<NetworkBehaviour>();
        internal NetworkVariableBase[] AllVars = Array.Empty<NetworkVariableBase>();
        /// <summary>The <see cref="SyncHistoryAttribute"/> subset of <see cref="AllVars"/>, in the same order, snapshotted by <see cref="StateHistory"/>.</summary>
        internal NetworkVariableBase[] HistoryVars = Array.Empty<NetworkVariableBase>();
        internal bool VarsDirty;
        internal bool SyncDirty;
        internal bool Initialized;
        /// <summary>Behaviours that replicate through the sync channel (<see cref="NetworkBehaviour.HasSyncState"/>).</summary>
        internal NetworkBehaviour[] SyncBehaviours = Array.Empty<NetworkBehaviour>();

        /// <summary>
        /// Every this many ticks a dirty sync behavior writes a keyframe instead of a delta, and an unreliable one
        /// writes a keyframe whether it is dirty or not. Keyframes heal lost packets and are what the gateway hands to
        /// late joiners.
        /// </summary>
        public const uint SyncKeyframeInterval = 30;

        public PredictedBehaviourBase Predicted { get; private set; }
        public RemoteInterpolator Interpolator { get; internal set; }

        /// <summary>
        /// The <see cref="PersistentEntity"/> on this entity, or null when it is transient. Cached during
        /// initialization; see <see cref="NebulaPersistence"/> for checkpoint and restore behavior.
        /// </summary>
        public PersistentEntity Persistent { get; private set; }

        // ---- state history (worker side): what this entity looked like N ticks ago, for lag-compensated hit
        // tests and time-sensitive validation. A shooter aims at what its screen showed, which is interpolation
        // delay + transit + input lead in the past; a worker records every copy it holds per tick - its own after
        // the tick's simulation, a ghost when the owner's stream is applied - so game code can test a claim against
        // where the entity was at the claimed tick instead of where it is now. Design: docs/state-history.md.

        private StateHistory _history;

        /// <summary>
        /// The recorded ticks of this entity on this process, or null when nothing has been recorded (recording is
        /// off, this is a client, or the entity is too young). See <see cref="StateAt"/>.
        /// </summary>
        public StateHistory History => _history;

        /// <summary>The oldest tick <see cref="StateAt"/> can answer for, or 0 when nothing is recorded.</summary>
        public uint OldestAvailableTick => _history != null && _history.HasEntries ? _history.OldestAvailableTick : 0u;

        /// <summary>The newest tick <see cref="StateAt"/> can answer for, or 0 when nothing is recorded.</summary>
        public uint NewestAvailableTick => _history != null && _history.HasEntries ? _history.NewestAvailableTick : 0u;

        /// <summary>
        /// What this entity looked like at server tick <paramref name="tick"/>: its world pose, velocity, container,
        /// epoch and the <see cref="SyncHistoryAttribute"/> variables. Answered on the worker that has authority and
        /// on any worker holding a ghost of it; a ghost's newest tick is one behind the owner's (the tick the
        /// owner's stream was built on). Outside the recorded window the result's
        /// <see cref="HistoricalState.Available"/> is false and nothing is extrapolated.
        /// </summary>
        public HistoricalState StateAt(uint tick)
        {
            TryGetStateAt(tick, out var state);
            return state;
        }

        /// <summary>As <see cref="StateAt"/>, in the try-pattern.</summary>
        public bool TryGetStateAt(uint tick, out HistoricalState state)
        {
            if (_history != null) return _history.TryGetStateAt(tick, out state);
            state = default;
            return false;
        }

        /// <summary>
        /// The pose recorded for <paramref name="tick"/>, or the nearest one within
        /// <see cref="StateHistory.GapToleranceTicks"/>. False (with the entity's current pose) when nothing usable
        /// was recorded. A thin wrapper over <see cref="StateAt"/>.
        /// </summary>
        public bool TryGetPoseAt(uint tick, out Vector3 position, out Quaternion rotation)
        {
            if (TryGetStateAt(tick, out var state))
            {
                position = state.Position;
                rotation = state.Rotation;
                return true;
            }
            position = transform.position;
            rotation = transform.rotation;
            return false;
        }

        /// <summary>The ring for this entity, created on demand. Null when recording is off (window of 0 ticks).</summary>
        private StateHistory EnsureHistory()
        {
            int window = StateHistory.WindowTicks;
            if (window <= 0) return null;
            if (_history == null) _history = new StateHistory(window, HistoryVars);
            else if (_history.Capacity != Mathf.Min(window, StateHistory.MaxWindowTicks)) _history = new StateHistory(window, HistoryVars);
            return _history;
        }

        /// <summary>
        /// Record this tick from the authoritative copy, after its simulation. Called once per tick per
        /// authoritative entity by <see cref="NebulaWorker"/>.
        /// </summary>
        internal void RecordAuthoritativeState(uint tick)
        {
            var history = EnsureHistory();
            history?.Record(tick, transform.position, transform.rotation, Motion.Velocity, Container, Epoch, true);
        }

        /// <summary>
        /// Record the tick a replicated entry belongs to on a ghost holder, from the pose the stream just delivered
        /// rather than from the transform (an interpolating ghost has not moved there yet). <paramref name="tick"/>
        /// is the owner's tick, carried by <see cref="WorldStateMsg"/>.
        /// </summary>
        internal void RecordGhostState(uint tick, Container container)
        {
            var history = EnsureHistory();
            if (history == null) return;
            var localPosition = LocalPosition;
            var localRotation = LocalRotation;
            var velocity = Motion.Velocity;
            var frame = Container;
            if (Interpolator != null && Interpolator.HasSamples && Interpolator.LatestTick == tick)
            {
                frame = Interpolator.LatestContainer;
                localPosition = Interpolator.LatestLocalPosition;
                localRotation = Interpolator.LatestLocalRotation;
                velocity = Interpolator.LatestVelocity;
            }
            else if (container != null || Container == null)
            {
                frame = container;
                if (container != Container)
                {
                    localPosition = container != null ? container.ToLocal(transform.position) : transform.position;
                    localRotation = container != null ? container.InverseRotation * transform.rotation : transform.rotation;
                }
            }
            var position = frame != null ? frame.ToWorld(localPosition) : localPosition;
            var rotation = frame != null ? frame.Rotation * localRotation : localRotation;
            history.Record(tick, position, rotation, velocity, frame, Epoch, false);
        }

        public event Action<Container, Container> ContainerChanged;

        /// <summary>Every initialised identity in this process (worker and client alike), for frame-wide operations such as origin shifts.</summary>
        internal static readonly HashSet<NetworkIdentity> Live = new HashSet<NetworkIdentity>();

        /// <summary>
        /// The floating origin moved by <paramref name="delta"/>: entities under a container were moved with it, but
        /// cached frame positions (pose history, interpolation buffers, game-side state) must follow by hand.
        /// </summary>
        internal static void ShiftFrameAll(Vector3 delta) => ShiftFrameAll(0UL, delta);

        /// <summary>
        /// One origin frame moved: only the entities of that frame follow it. <paramref name="frameId"/> is 0 for
        /// the public frame, which is also where every entity whose scope has no frame of its own lives
        /// (<c>docs/scope-frames.md</c>).
        /// </summary>
        internal static void ShiftFrameAll(ulong frameId, Vector3 delta)
        {
            foreach (var e in Live)
                if (e != null && Nebula.World.ScopeFrames.FrameIdOf(e.InstanceId) == frameId) e.ShiftFrame(delta);
        }

        /// <summary>
        /// A physics frame's floating origin moved (<c>docs/container-tree.md</c> D19): the entities in that frame were
        /// moved with its root; their cached simulation-space positions follow here.
        /// </summary>
        internal static void ShiftFrameIn(Container frame, Vector3 delta)
        {
            foreach (var e in Live)
                if (e != null && e.Space == frame) e.ShiftFrame(delta);
        }

        internal void ShiftFrame(Vector3 delta)
        {
            // A scene entity was moved with its scene's roots by the streamer, a contained one with its container.
            if (Container == null && !IsSceneEntity) transform.position += delta;
            _history?.Shift(delta);
            Interpolator?.Shift(delta);
            for (int i = 0; i < Behaviours.Length; i++)
            {
                try { Behaviours[i].OnOriginShifted(delta); }
                catch (Exception ex) { NebulaLog.Error($"OnOriginShifted on {Behaviours[i].GetType().Name} of {name} threw: {ex.Message}"); }
            }
        }

        private void Awake()
        {
            // A prefab instance initialises when it is instantiated through NetworkPrefabs; a scene object has no
            // such moment, so it initialises here and announces itself. It stays unspawned until a worker spawns it
            // or a spawn for its id arrives.
            if (!IsSceneEntity || Initialized) return;
            Initialize();
            SceneEntities.Register(this);
        }

        private void OnDestroy()
        {
            // Destroyed without a despawn (scene torn down, play mode stopped): leave nothing of the carried box behind in
            // the registry. The Container on the same object may be destroyed first, so it is tested as a managed reference.
            if (!ReferenceEquals(_carried, null) && _carried.IsDynamic && _carried.Carrier == this) ContainerRegistry.UnregisterDynamic(_carried);
            Container.UnregisterBody(_carriedBody);
            _carriedBody = null;
            if (IsSceneEntity) SceneEntities.Unregister(this);
            CohesionGroups.Unregister(this, _cohesionGroup);
            Live.Remove(this);
        }

        /// <summary>
        /// A scene entity leaves the network (despawned, or its process lost track of it) without leaving its scene:
        /// back to the unspawned state it loaded in, keeping its last replicated values.
        /// </summary>
        internal void Unbind()
        {
            if (IsSpawned) InvokeDespawn();
            NetId = 0;
            Epoch = 0;
            OwnerClientId = 0;
            OwnerIdentity = "";
            OwnerIsBot = false;
            IsServerDriven = false;
            HasAuthority = false;
            IsLocalPlayer = false;
            EffectiveCostWeight = NebulaCost.Clamp(CostWeight);
            _costWeightPinned = false;
            OwnerWorkerIndex = 0;
            Motion.Velocity = Vector3.zero;
            HasStateTick = false;
            _publishedLocation = false;
            _hasPendingState = false;
            ContainerPinned = false;
            SetContainer(null, reparent: false);
            if (Interpolator != null)
            {
                Destroy(Interpolator);
                Interpolator = null;
            }
            _history = null;
            SyncAudienceGeneration = 0;
            AudienceChangedThisTick = false;
            foreach (var b in SyncBehaviours) ResetAudience(b);
            ClearDirty();
        }

        internal void Initialize()
        {
            if (Initialized) return;
            Initialized = true;
            Live.Add(this);
            CohesionGroups.Register(this);

            var found = GetComponentsInChildren<NetworkBehaviour>(true);
            // Deterministic order on every process: the same prefab yields the same component order.
            Behaviours = found;
            RootTransform = GetComponent<NetworkTransform>();
            _carried = GetComponent<Container>();
            var vars = new List<NetworkVariableBase>();
            for (int i = 0; i < Behaviours.Length; i++)
            {
                var b = Behaviours[i];
                b.Identity = this;
                b.BehaviourIndex = (byte)i;
                b.Vars = DiscoverVars(b);
                foreach (var v in b.Vars) vars.Add(v);
                if (b is PredictedBehaviourBase p)
                {
                    if (Predicted != null) NebulaLog.Warn($"{name}: more than one PredictedBehaviour; only the first drives input");
                    else Predicted = p;
                }
                if (b is PersistentEntity pe && Persistent == null) Persistent = pe;
            }
            AllVars = vars.ToArray();
            var history = new List<NetworkVariableBase>();
            foreach (var v in AllVars) if (v.SyncHistory) history.Add(v);
            HistoryVars = history.Count > 0 ? history.ToArray() : Array.Empty<NetworkVariableBase>();
            _history?.Rebind(HistoryVars);
            var sync = new List<NetworkBehaviour>();
            HasCustomAudience = false;
            HasRestrictedAudience = false;
            foreach (var b in Behaviours)
            {
                if (!b.HasSyncState) continue;
                sync.Add(b);
                // Read once: the audience is configuration, and every process must agree on it for the whole life
                // of the entity (docs/sync-audience.md D3).
                var audience = SyncAudience.Everyone;
                try { audience = b.SyncAudience; }
                catch (Exception ex) { NebulaLog.Error($"SyncAudience on {b.GetType().Name} of {name} threw: {ex.Message}; using Everyone"); }
                if (b == RootTransform && audience != SyncAudience.Everyone)
                {
                    // The root pose is the entity's place in the world: interest, carriers and every client's view
                    // of the entity are built on it, so it cannot be private.
                    NebulaLog.Warn($"{name}: the root NetworkTransform replicates to everyone; its SyncAudience {audience} is ignored");
                    audience = SyncAudience.Everyone;
                }
                b.Audience = audience;
                if (audience == SyncAudience.Custom) HasCustomAudience = true;
                if (audience != SyncAudience.Everyone) HasRestrictedAudience = true;
            }
            SyncBehaviours = sync.ToArray();
        }

        private static NetworkVariableBase[] DiscoverVars(NetworkBehaviour b)
        {
            var list = new List<(int token, NetworkVariableBase v, string name, bool persist, bool history)>();
            string typeName = b.GetType().Name;
            for (var t = b.GetType(); t != null && t != typeof(NetworkBehaviour); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(NetworkVariableBase).IsAssignableFrom(f.FieldType)) continue;
                    var v = (NetworkVariableBase)f.GetValue(b);
                    if (v == null)
                    {
                        v = (NetworkVariableBase)Activator.CreateInstance(f.FieldType);
                        f.SetValue(b, v);
                    }
                    // The name is what a persisted value is keyed by; it is cheap, so every variable gets one.
                    list.Add((f.MetadataToken, v, $"{typeName}.{f.Name}", f.IsDefined(typeof(PersistAttribute), true),
                        f.IsDefined(typeof(SyncHistoryAttribute), true)));
                }
            }
            var ordered = list.OrderBy(x => x.token).ToArray();
            var vars = new NetworkVariableBase[ordered.Length];
            for (int i = 0; i < ordered.Length; i++)
            {
                var v = ordered[i].v;
                v.Owner = b;
                v.Index = i;
                v.Name = ordered[i].name;
                v.Persist = ordered[i].persist;
                v.SyncHistory = ordered[i].history;
                vars[i] = v;
            }
            return vars;
        }

        internal void MarkVarsDirty() => VarsDirty = true;
        internal void MarkSyncDirty() => SyncDirty = true;
        public bool HasSyncState => SyncBehaviours.Length > 0;

        public void WriteVars(NetworkWriter writer)
        {
            for (int i = 0; i < AllVars.Length; i++) AllVars[i].Write(writer);
        }

        public void ReadVars(NetworkReader reader)
        {
            for (int i = 0; i < AllVars.Length; i++) AllVars[i].Read(reader);
        }

        /// <summary>Handover-only state from every behavior (see <see cref="NetworkBehaviour.WriteHandoverState"/>).</summary>
        public void WriteHandoverState(NetworkWriter writer)
        {
            writer.WriteByte((byte)Behaviours.Length);
            for (int i = 0; i < Behaviours.Length; i++)
            {
                int at = writer.ReserveUShort();
                int start = writer.Length;
                Behaviours[i].WriteHandoverState(writer);
                writer.PatchUShort(at, (ushort)(writer.Length - start));
            }
        }

        private static readonly NetworkReader ChunkReader = new NetworkReader(Array.Empty<byte>());

        /// <summary>
        /// Each behavior reads from a reader bounded to its own chunk, so one that reads too much throws (and is
        /// logged) instead of eating the next behavior's bytes, and one that reads too little leaves no residue.
        /// </summary>
        /// <summary>True while an incoming handover blob is applied: authority lands right after, so variable writes are the new owner's.</summary>
        public bool ReceivingHandover { get; private set; }

        public void ReadHandoverState(NetworkReader reader)
        {
            ReceivingHandover = true;
            try { ReadHandoverBehaviours(reader); }
            finally { ReceivingHandover = false; }
        }

        private void ReadHandoverBehaviours(NetworkReader reader)
        {
            int count = reader.ReadByte();
            for (int i = 0; i < count; i++)
            {
                var chunk = reader.ReadSegment(reader.ReadUShort());
                if (i >= Behaviours.Length) continue;
                ChunkReader.Set(chunk);
                try { Behaviours[i].ReadHandoverState(ChunkReader); }
                catch (Exception ex) { NebulaLog.Error($"ReadHandoverState on {Behaviours[i].GetType().Name} of {name} threw: {ex.Message}"); }
            }
        }

        public void ClearDirty()
        {
            VarsDirty = false;
            SyncDirty = false;
            for (int i = 0; i < AllVars.Length; i++) AllVars[i].Dirty = false;
            for (int i = 0; i < SyncBehaviours.Length; i++)
            {
                var b = SyncBehaviours[i];
                if (b.SyncDirty) b.OnSyncStateSent();
                b.SyncDirty = false;
                // Only now, after every destination got this tick's chunk, does the stream count as opened.
                if (b.SyncWrittenThisTick) { b.SyncEverSent = true; b.SyncWrittenThisTick = false; b.AudienceKeyframe = false; }
            }
            AudienceChangedThisTick = false;
        }

        // ---- sync channel ---------------------------------------------------------------------------------

        /// <summary>A keyframe from every sync behavior: what rides the spawn/handover message.</summary>
        public void WriteSyncSnapshot(NetworkWriter writer) => WriteSyncSnapshot(writer, forGateway: false);

        /// <summary>
        /// A keyframe from every sync behavior, each chunk flagged with its behavior's audience. For a gateway
        /// (<paramref name="forGateway"/>), <see cref="SyncAudience.WorkersOnly"/> behaviors are left out: no client
        /// may have them, so the gateway is not sent them at all.
        /// </summary>
        public void WriteSyncSnapshot(NetworkWriter writer, bool forGateway)
        {
            int at = SyncStateCodec.BeginEnvelope(writer);
            byte n = 0;
            for (int i = 0; i < SyncBehaviours.Length; i++)
            {
                var b = SyncBehaviours[i];
                if (b == RootTransform) continue; // root snapshots live in EntitySpawnMsg
                if (forGateway && b.Audience == SyncAudience.WorkersOnly) continue;
                SyncStateCodec.WriteChunk(writer, b.BehaviourIndex, SyncStateCodec.ChunkFlags.Full | SyncStateCodec.FlagsOf(b.Audience), b, true);
                n++;
            }
            SyncStateCodec.EndEnvelope(writer, at, n);
        }

        // ---- sync audience --------------------------------------------------------------------------------

        /// <summary>
        /// Raised every time the member set of one of this entity's <see cref="SyncAudience.Custom"/> behaviors
        /// changes on its authority. It travels with the entity through ghosting and handover, so it only ever goes up,
        /// and it stamps every sync message (<see cref="EntitySyncMsg.AudienceGeneration"/>) so a gateway can tell a
        /// chunk written under member sets it has not received yet (docs/sync-audience.md D6).
        /// </summary>
        public uint SyncAudienceGeneration { get; internal set; }

        /// <summary>At least one sync behavior has the <see cref="SyncAudience.Custom"/> audience.</summary>
        public bool HasCustomAudience { get; private set; }

        /// <summary>At least one sync behavior has an audience other than <see cref="SyncAudience.Everyone"/>.</summary>
        public bool HasRestrictedAudience { get; private set; }

        /// <summary>A Custom member set changed this tick: the gateways are sent a <see cref="SyncAudienceMsg"/> before the tick's chunks.</summary>
        internal bool AudienceChangedThisTick;

        /// <summary>The member sets of the Custom behaviors (<see cref="SyncAudienceCodec"/>).</summary>
        internal void WriteAudienceSets(NetworkWriter writer)
        {
            _audienceSets.Clear();
            for (int i = 0; i < SyncBehaviours.Length; i++)
            {
                var b = SyncBehaviours[i];
                if (b.Audience == SyncAudience.Custom) _audienceSets.Add(new KeyValuePair<byte, ulong[]>(b.BehaviourIndex, b.AudienceMembers));
            }
            SyncAudienceCodec.Write(writer, _audienceSets);
            _audienceSets.Clear();
        }

        private static readonly List<KeyValuePair<byte, ulong[]>> _audienceSets = new List<KeyValuePair<byte, ulong[]>>();

        /// <summary>
        /// Take the audience state that arrived with the entity (a ghost spawn, a handover): the generation, and the
        /// member sets its previous authority chose. A new authority starts from them, so if it reaches the same
        /// answer nothing changes for any client.
        /// </summary>
        internal void ApplyCarriedAudience(uint generation, byte[] sets)
        {
            if (generation > SyncAudienceGeneration) SyncAudienceGeneration = generation;
            if (!HasCustomAudience || sets == null || sets.Length == 0) return;
            Dictionary<byte, ulong[]> carried;
            try { carried = SyncAudienceCodec.Read(sets); }
            catch (Exception ex) { NebulaLog.Error($"{name}: unreadable audience sets ({ex.Message}); keeping the current ones"); return; }
            foreach (var kv in carried)
            {
                if (kv.Key >= Behaviours.Length) continue;
                var b = Behaviours[kv.Key];
                if (b.Audience == SyncAudience.Custom) b.AudienceMembers = kv.Value;
            }
        }

        /// <summary>
        /// Authority, once per tick before anything is written: evaluate every <see cref="SyncAudience.Custom"/>
        /// behavior that is due (marked dirty, or its <see cref="NetworkBehaviour.SyncAudienceRefreshTicks"/> ran out)
        /// against <paramref name="candidates"/>, the pawns this worker holds by session id. A behavior whose set
        /// changed writes a keyframe this tick, and the entity's generation goes up once. Returns whether anything
        /// changed. The cost is one predicate call per candidate per due behavior.
        /// </summary>
        internal bool EvaluateSyncAudiences(uint tick, Dictionary<ulong, NetworkIdentity> candidates, List<ulong> scratch)
        {
            if (!HasCustomAudience) return false;
            bool changed = false;
            for (int i = 0; i < SyncBehaviours.Length; i++)
            {
                var b = SyncBehaviours[i];
                if (b.Audience != SyncAudience.Custom) continue;
                uint refresh = 0;
                try { refresh = b.SyncAudienceRefreshTicks; }
                catch (Exception ex) { NebulaLog.Error($"SyncAudienceRefreshTicks on {b.GetType().Name} of {name} threw: {ex.Message}"); }
                if (!b.AudienceDirty && (refresh == 0 || tick < b.AudienceNextTick)) continue;
                b.AudienceDirty = false;
                b.AudienceNextTick = refresh > 0 ? tick + refresh : 0;
                scratch.Clear();
                if (candidates != null)
                {
                    foreach (var kv in candidates)
                    {
                        if (kv.Key == 0 || kv.Value == null) continue;
                        if (b.EvaluateAudienceMember(kv.Key, kv.Value)) scratch.Add(kv.Key);
                    }
                }
                scratch.Sort();
                if (scratch.Count > SyncAudienceCodec.MaxMembers)
                {
                    if (!b.AudienceCapWarned)
                    {
                        b.AudienceCapWarned = true;
                        NebulaLog.Warn($"{b.GetType().Name} of {name}: IsInSyncAudience chose {scratch.Count} clients, more than the {SyncAudienceCodec.MaxMembers} an audience can hold; the rest do not receive its state");
                    }
                    scratch.RemoveRange(SyncAudienceCodec.MaxMembers, scratch.Count - SyncAudienceCodec.MaxMembers);
                }
                if (SameMembers(b.AudienceMembers, scratch)) continue;
                b.AudienceMembers = scratch.ToArray();
                // Everyone who has the behaviour now, the new members included, gets a fresh keyframe this tick.
                b.AudienceKeyframe = true;
                b.SyncDirty = true;
                SyncDirty = true;
                changed = true;
            }
            if (!changed) return false;
            SyncAudienceGeneration++;
            AudienceChangedThisTick = true;
            return true;
        }

        private static bool SameMembers(ulong[] current, List<ulong> next)
        {
            if (current.Length != next.Count) return false;
            for (int i = 0; i < current.Length; i++) if (current[i] != next[i]) return false;
            return true;
        }

        /// <summary>
        /// Authority: the client <paramref name="clientId"/> came back (a reclaimed session). Every restricted behavior
        /// it is in the audience of writes a keyframe this tick, so the new connection starts from current state and
        /// not only from the gateway's newest cached keyframe.
        /// </summary>
        internal void ForceAudienceKeyframes(ulong clientId)
        {
            if (!HasRestrictedAudience || clientId == 0) return;
            for (int i = 0; i < SyncBehaviours.Length; i++)
            {
                var b = SyncBehaviours[i];
                bool member = b.Audience == SyncAudience.Owner ? OwnerClientId == clientId
                    : b.Audience == SyncAudience.Custom && SyncAudienceCodec.Contains(b.AudienceMembers, clientId);
                if (!member) continue;
                b.AudienceKeyframe = true;
                b.SyncDirty = true;
                SyncDirty = true;
            }
        }

        private static void ResetAudience(NetworkBehaviour b)
        {
            b.AudienceMembers = Array.Empty<ulong>();
            b.AudienceDirty = true;
            b.AudienceNextTick = 0;
            b.AudienceKeyframe = false;
            b.SyncCleared = false;
            b.SyncClearedTick = 0;
        }

        /// <summary>
        /// Authority, once per tick per delivery class and per destination: the chunks due this tick. A behavior is
        /// written when it is dirty, or (unreliable only) when a keyframe is due. The keyframe decision depends only
        /// on the tick and on state that <see cref="ClearDirty"/> advances, so every destination written in the same
        /// tick gets identical chunks: the first tick after gaining authority is a keyframe for all of them. Returns
        /// the number of chunks written; when zero the writer has been left untouched.
        /// </summary>
        public int WriteSyncState(NetworkWriter writer, uint tick, Delivery delivery) => WriteSyncState(writer, tick, delivery, forGateway: false);

        /// <summary>
        /// As <see cref="WriteSyncState(NetworkWriter, uint, Delivery)"/>, with each chunk flagged with its behavior's
        /// audience. For a gateway (<paramref name="forGateway"/>), <see cref="SyncAudience.WorkersOnly"/> behaviors
        /// are left out. A behavior whose audience just changed, or whose owner just came back, writes a keyframe.
        /// </summary>
        public int WriteSyncState(NetworkWriter writer, uint tick, Delivery delivery, bool forGateway)
        {
            int rewind = writer.Length;
            int at = SyncStateCodec.BeginEnvelope(writer);
            byte n = 0;
            bool keyframeTick = tick % SyncKeyframeInterval == 0;
            for (int i = 0; i < SyncBehaviours.Length; i++)
            {
                var b = SyncBehaviours[i];
                if (b == RootTransform) continue; // batched spatial stream, not opaque component chunks
                if (b.SyncDelivery != delivery) continue;
                if (forGateway && b.Audience == SyncAudience.WorkersOnly) continue;
                bool keyframe = keyframeTick || !b.SyncEverSent || b.AudienceKeyframe || (b is NetworkTransform && delivery == Delivery.ReliableOrdered);
                bool send = b.SyncDirty || (keyframe && delivery == Delivery.Sequenced);
                if (!send) continue;
                var flags = (keyframe ? SyncStateCodec.ChunkFlags.Full : SyncStateCodec.ChunkFlags.None) | SyncStateCodec.FlagsOf(b.Audience);
                SyncStateCodec.WriteChunk(writer, b.BehaviourIndex, flags, b, keyframe);
                b.SyncWrittenThisTick = true;
                n++;
            }
            if (n == 0) { writer.Rewind(rewind); return 0; }
            SyncStateCodec.EndEnvelope(writer, at, n);
            return n;
        }

        private static readonly NetworkReader SyncChunkReader = new NetworkReader(Array.Empty<byte>());

        /// <summary>
        /// The container the sync chunks currently being read were expressed in (world-space NetworkTransform values
        /// are container-local on the wire). Valid only inside <see cref="NetworkBehaviour.ReadSyncState"/>.
        /// </summary>
        public Container SyncContainer { get; internal set; }

        /// <summary>Non-authoritative copies: hand each chunk to its behavior. Chunks for unknown indices are skipped.</summary>
        public void ReadSyncState(NetworkReader reader, uint tick, Container container) => ReadSyncState(reader, tick, container, reliable: true);

        /// <summary>
        /// As <see cref="ReadSyncState(NetworkReader, uint, Container)"/>. <paramref name="reliable"/> says whether the
        /// chunks came on the reliable stream. A <see cref="SyncStateCodec.ChunkFlags.Cleared"/> chunk calls
        /// <see cref="NetworkBehaviour.OnSyncStateCleared"/> instead of <see cref="NetworkBehaviour.ReadSyncState"/>.
        /// After it, chunks of that behavior from sequenced packets no newer than the clear are dropped: they were
        /// sent before the client left the audience and overtook nothing but the notice.
        /// </summary>
        public void ReadSyncState(NetworkReader reader, uint tick, Container container, bool reliable)
        {
            SyncContainer = container;
            SyncStateCodec.ReadEnvelope(reader, (index, flags, chunk) =>
            {
                if (index >= Behaviours.Length) return;
                var b = Behaviours[index];
                if ((flags & SyncStateCodec.ChunkFlags.Cleared) != 0)
                {
                    b.SyncCleared = true;
                    b.SyncClearedTick = tick;
                    try { b.OnSyncStateCleared(); }
                    catch (Exception ex) { NebulaLog.Error($"OnSyncStateCleared on {b.GetType().Name} of {name} threw: {ex.Message}"); }
                    return;
                }
                bool full = (flags & SyncStateCodec.ChunkFlags.Full) != 0;
                if (b.SyncCleared)
                {
                    if (!reliable && tick <= b.SyncClearedTick) return;
                    if (full) b.SyncCleared = false;
                }
                SyncChunkReader.Set(chunk);
                try { b.ReadSyncState(SyncChunkReader, tick, full); }
                catch (Exception ex) { NebulaLog.Error($"ReadSyncState on {b.GetType().Name} of {name} threw: {ex.Message}"); }
            });
        }

        /// <summary>Present the tick <paramref name="renderTick"/> on every behavior (ghosts and remote client copies).</summary>
        public void RemoteTick(double renderTick)
        {
            ReplayPendingState();
            for (int i = 0; i < Behaviours.Length; i++)
            {
                try { Behaviours[i].RemoteTick(renderTick); }
                catch (Exception ex) { NebulaLog.Error($"RemoteTick on {Behaviours[i].GetType().Name} of {name} threw: {ex.Message}"); }
            }
        }

        /// <summary>A refused carrier cycle was already reported for this entity (cleared by the next placement that succeeds).</summary>
        private bool _carrierCycleWarned;

        /// <summary>
        /// Move the entity into <paramref name="container"/>. A dynamic container this entity carries, directly or
        /// through any chain of carriers (<see cref="Nebula.Container.IsCarriedBy"/>), is refused and the entity
        /// stays where it was: two ships whose interiors overlap may not each end up inside the other, and a ship
        /// may not sit in the hangar of a shuttle it is itself carrying. The resolver never proposes such a
        /// container; a refusal here means a placement came off the wire from a peer that decided differently.
        /// </summary>
        internal void SetContainer(Container container, bool reparent = true)
        {
            var previous = Container;
            if (previous == container) return;
            if (container != null && container.IsCarriedBy(this))
            {
                if (!_carrierCycleWarned)
                {
                    _carrierCycleWarned = true;
                    NebulaLog.Warn(container.Carrier == this
                        ? $"{this} cannot be inside the container it carries; ignored"
                        : $"{this} cannot be inside {container.ContainerId}: that container rides inside {this} itself, so the carriers would contain each other; ignored");
                }
                return;
            }
            _carrierCycleWarned = false;
            if (container != null && container.InstanceId != 0 && !InstanceScenes.Prepare(container))
                throw new InvalidOperationException("Instance content is unavailable: " + container.ContainerId);
            // Changing space (into or out of a physics frame, docs/container-tree.md §3): the pose and velocity are
            // converted through the frames' current poses. Only where frames are simulated at the identity pose (a
            // worker); a client renders every frame at its world pose, so a world pose there needs no conversion.
            var fromSpace = previous != null ? previous.InnerSpace : null;
            var toSpace = container != null ? container.InnerSpace : null;
            // A first placement (a spawn) takes the pose as given, in the space of the container it is placed in.
            bool convert = reparent && !IsSceneEntity && previous != null && fromSpace != toSpace && !PhysicsFrames.RendersFrames;
            Vector3 position = default;
            Quaternion rotation = default;
            if (convert)
            {
                var p = transform.position;
                position = PhysicsFrames.Convert(p, fromSpace, toSpace);
                rotation = PhysicsFrames.Convert(transform.rotation, fromSpace, toSpace);
                Motion.Velocity = PhysicsFrames.ConvertVelocity(Motion.Velocity, p, fromSpace, toSpace);
                var body = GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic)
                {
                    body.linearVelocity = PhysicsFrames.ConvertVelocity(body.linearVelocity, p, fromSpace, toSpace);
                    body.angularVelocity = PhysicsFrames.Convert(Quaternion.identity, fromSpace, toSpace) * body.angularVelocity;
                }
            }
            Container = container;
            if (previous != null) previous.Entities.Remove(this);
            if (container != null) container.Entities.Add(this);
            // A scene object stays in its scene's hierarchy (the streamer moves the scene, not the container).
            if (reparent && !IsSceneEntity)
            {
                // Under the container's content root: its physics frame's root when it has one, its transform otherwise.
                var root = container != null ? container.ContentRoot : null;
                if (root != null && gameObject.scene != root.gameObject.scene)
                {
                    transform.SetParent(null, true);
                    SceneManager.MoveGameObjectToScene(gameObject, root.gameObject.scene);
                }
                if (root != null) transform.SetParent(root, true);
                else if (previous != null && (previous.IsDynamic || previous.IsRuntime || fromSpace != null))
                {
                    // Out of a departing carrier, a retiring runtime box or a frame's scene, whose objects are about to go.
                    transform.SetParent(null, true);
                    var home = fromSpace != null ? fromSpace.gameObject.scene : default;
                    if (home.IsValid() && gameObject.scene != home) SceneManager.MoveGameObjectToScene(gameObject, home);
                }
                if (convert) transform.SetPositionAndRotation(position, rotation);
            }
            foreach (var b in Behaviours) b.OnContainerChanged(previous, container);
            ContainerChanged?.Invoke(previous, container);
            if (IsLocalPlayer) InstanceScenes.SetView(InstanceId);
        }

        internal void InvokeSpawn()
        {
            IsSpawned = true;
            // The carried box registers once the entity is placed in the container around it (SetContainer runs before
            // the spawn), and before any behaviour hears of the spawn, so a behaviour's OnNetworkSpawn finds it.
            RegisterCarried();
            foreach (var b in Behaviours) b.OnNetworkSpawn();
        }

        internal void InvokeDespawn()
        {
            // The carried box goes first, putting whatever rides in it down in the container around the entity, while
            // the entity is still here to be evacuated from.
            UnregisterCarried();
            foreach (var b in Behaviours) b.OnNetworkDespawn();
            IsSpawned = false;
        }

        private void RegisterCarried()
        {
            if (_carried == null) _carried = GetComponent<Container>();
            if (_carried == null) return;
            ContainerRegistry.RegisterDynamic(_carried, this);
            if (!_carried.IsDynamic) return; // refused (no net id yet)
            _carriedBody = GetComponent<Rigidbody>();
            Container.RegisterBody(_carriedBody, _carried);
        }

        private void UnregisterCarried()
        {
            Container.UnregisterBody(_carriedBody);
            _carriedBody = null;
            if (_carried != null && _carried.IsDynamic && _carried.Carrier == this) ContainerRegistry.UnregisterDynamic(_carried);
        }

        internal void SetAuthority(bool authority)
        {
            if (HasAuthority == authority) return;
            HasAuthority = authority;
            // A new authority (new epoch) opens its stream with keyframes so the gateway cache and ghosts restart clean.
            if (authority) foreach (var b in SyncBehaviours) { b.SyncEverSent = false; b.SyncWrittenThisTick = false; b.SyncDirty = true; SyncDirty = true; }
            // A new authority asks its own pawns: the answer can differ from the previous worker's (docs/sync-audience.md D7).
            if (authority) foreach (var b in SyncBehaviours) if (b.Audience == SyncAudience.Custom) b.AudienceDirty = true;
            if (authority) foreach (var b in Behaviours) b.OnGainedAuthority();
            else foreach (var b in Behaviours) b.OnLostAuthority();
        }

        /// <summary>
        /// Position in the current container's local space (what goes on the wire). An entity parented under its
        /// container reads its local transform directly, which is exact even when the container moved this tick.
        /// </summary>
        public Vector3 LocalPosition
        {
            get
            {
                if (Container == null) return transform.position;
                var root = Container.ContentRoot;
                if (transform.parent == root) return transform.localPosition;
                // Not parented (a scene entity): a framed container's contents are already in its local coordinates
                // in simulation space; anything else goes through the box's transform.
                return root != Container.transform ? root.InverseTransformPoint(transform.position) : Container.ToLocal(transform.position);
            }
        }

        public Quaternion LocalRotation
        {
            get
            {
                if (Container == null) return transform.rotation;
                var root = Container.ContentRoot;
                if (transform.parent == root) return transform.localRotation;
                return root != Container.transform ? Quaternion.Inverse(root.rotation) * transform.rotation : Container.InverseRotation * transform.rotation;
            }
        }

        /// <summary>
        /// Place the entity at a pose expressed in <paramref name="container"/>'s space (world space when null). An
        /// entity parented under the container's transform is moved through its local pose, so where it ends up
        /// this frame follows wherever the container is this frame, whichever of the two is updated first: this is
        /// what keeps passengers glued to a moving ship on every process.
        /// </summary>
        public void SetLocalPose(Container container, Vector3 localPosition, Quaternion localRotation)
        {
            if (container != null)
            {
                var t = transform;
                var root = container.ContentRoot;
                if (t.parent == root)
                {
                    t.localPosition = localPosition;
                    t.localRotation = localRotation;
                }
                else if (root != container.transform) t.SetPositionAndRotation(root.TransformPoint(localPosition), root.rotation * localRotation);
                else t.SetPositionAndRotation(container.ToWorld(localPosition), container.Rotation * localRotation);
            }
            else
            {
                transform.SetPositionAndRotation(localPosition, localRotation);
            }
        }

        public override string ToString()
        {
            string role = NebulaRuntime.IsServer ? (HasAuthority ? "auth" : "ghost") : (IsLocalPlayer ? "local" : "remote");
            string scene = IsSceneEntity ? $",scene {SceneId}" : "";
            return $"{name}#{NetId}(e{Epoch},{(Container != null ? Container.ContainerId : "-")},{role}{scene})";
        }
    }
}
