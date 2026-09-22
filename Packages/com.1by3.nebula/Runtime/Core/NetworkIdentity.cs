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
        /// <summary>World-space linear velocity. This is only streamed when a root NetworkTransform opts in.</summary>
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
        /// <summary>Wire index of the current container (<see cref="ContainerRef.DynamicIndex"/> inside a dynamic one, <see cref="ushort.MaxValue"/> in none). Prefer <see cref="ContainerRef"/>.</summary>
        public ushort ContainerIndex => Container != null ? Container.Index : ushort.MaxValue;
        /// <summary>How the current container is named on the wire (see <see cref="Nebula.ContainerRef"/>).</summary>
        public ContainerRef ContainerRef => ContainerRef.Of(Container);
        /// <summary>The dynamic container this entity carries (a <see cref="DynamicContainer"/> on its root), or null.</summary>
        public Container Carried => _carried != null ? _carried.Volume : null;
        private DynamicContainer _carried;
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
        /// Every this many ticks a dirty sync behaviour writes a keyframe instead of a delta, and an unreliable one
        /// writes a keyframe whether it is dirty or not. Keyframes heal lost packets and are what the gateway hands to
        /// late joiners.
        /// </summary>
        public const uint SyncKeyframeInterval = 30;

        public PredictedBehaviourBase Predicted { get; private set; }
        public RemoteInterpolator Interpolator { get; internal set; }

        /// <summary>
        /// The <see cref="PersistentEntity"/> on this entity, or null when it is transient. Cached by
        /// <see cref="Initialize"/>; everything about persistence hangs off it (see <see cref="NebulaPersistence"/>).
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
        internal static void ShiftFrameAll(Vector3 delta)
        {
            foreach (var e in Live) if (e != null) e.ShiftFrame(delta);
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
            OwnerWorkerIndex = 0;
            Motion.Velocity = Vector3.zero;
            HasStateTick = false;
            _publishedLocation = false;
            _hasPendingState = false;
            SetContainer(null, reparent: false);
            if (Interpolator != null)
            {
                Destroy(Interpolator);
                Interpolator = null;
            }
            _history = null;
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
            _carried = GetComponent<DynamicContainer>();
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
            foreach (var b in Behaviours) if (b.HasSyncState) sync.Add(b);
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

        /// <summary>Handover-only state from every behaviour (see <see cref="NetworkBehaviour.WriteHandoverState"/>).</summary>
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
        /// Each behaviour reads from a reader bounded to its own chunk, so one that reads too much throws (and is
        /// logged) instead of eating the next behaviour's bytes, and one that reads too little leaves no residue.
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
                if (b.SyncWrittenThisTick) { b.SyncEverSent = true; b.SyncWrittenThisTick = false; }
            }
        }

        // ---- sync channel ---------------------------------------------------------------------------------

        /// <summary>A keyframe from every sync behaviour: what rides the spawn/handover message.</summary>
        public void WriteSyncSnapshot(NetworkWriter writer)
        {
            int at = SyncStateCodec.BeginEnvelope(writer);
            byte n = 0;
            for (int i = 0; i < SyncBehaviours.Length; i++)
            {
                var b = SyncBehaviours[i];
                if (b == RootTransform) continue; // root snapshots live in EntitySpawnMsg
                SyncStateCodec.WriteChunk(writer, b.BehaviourIndex, SyncStateCodec.ChunkFlags.Full, b, true);
                n++;
            }
            SyncStateCodec.EndEnvelope(writer, at, n);
        }

        /// <summary>
        /// Authority, once per tick per delivery class and per destination: the chunks due this tick. A behaviour is
        /// written when it is dirty, or (unreliable only) when a keyframe is due. The keyframe decision depends only
        /// on the tick and on state that <see cref="ClearDirty"/> advances, so every destination written in the same
        /// tick gets identical chunks: the first tick after gaining authority is a keyframe for all of them. Returns
        /// the number of chunks written; when zero the writer has been left untouched.
        /// </summary>
        public int WriteSyncState(NetworkWriter writer, uint tick, Delivery delivery)
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
                bool keyframe = keyframeTick || !b.SyncEverSent || (b is NetworkTransform && delivery == Delivery.ReliableOrdered);
                bool send = b.SyncDirty || (keyframe && delivery == Delivery.Sequenced);
                if (!send) continue;
                SyncStateCodec.WriteChunk(writer, b.BehaviourIndex, keyframe ? SyncStateCodec.ChunkFlags.Full : SyncStateCodec.ChunkFlags.None, b, keyframe);
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

        /// <summary>Non-authoritative copies: hand each chunk to its behaviour. Chunks for unknown indices are skipped.</summary>
        public void ReadSyncState(NetworkReader reader, uint tick, Container container)
        {
            SyncContainer = container;
            SyncStateCodec.ReadEnvelope(reader, (index, flags, chunk) =>
            {
                if (index >= Behaviours.Length) return;
                var b = Behaviours[index];
                SyncChunkReader.Set(chunk);
                try { b.ReadSyncState(SyncChunkReader, tick, (flags & SyncStateCodec.ChunkFlags.Full) != 0); }
                catch (Exception ex) { NebulaLog.Error($"ReadSyncState on {b.GetType().Name} of {name} threw: {ex.Message}"); }
            });
        }

        /// <summary>Present the tick <paramref name="renderTick"/> on every behaviour (ghosts and remote client copies).</summary>
        public void RemoteTick(double renderTick)
        {
            ReplayPendingState();
            for (int i = 0; i < Behaviours.Length; i++)
            {
                try { Behaviours[i].RemoteTick(renderTick); }
                catch (Exception ex) { NebulaLog.Error($"RemoteTick on {Behaviours[i].GetType().Name} of {name} threw: {ex.Message}"); }
            }
        }

        internal void SetContainer(Container container, bool reparent = true)
        {
            var previous = Container;
            if (previous == container) return;
            if (container != null && container.Carrier == this)
            {
                NebulaLog.Warn($"{this} cannot be inside the container it carries; ignored");
                return;
            }
            if (container != null && container.InstanceId != 0 && !InstanceScenes.Prepare(container))
                throw new InvalidOperationException("Instance content is unavailable: " + container.ContainerId);
            Container = container;
            if (previous != null) previous.Entities.Remove(this);
            if (container != null) container.Entities.Add(this);
            // A scene object stays in its scene's hierarchy (the streamer moves the scene, not the container).
            if (reparent && !IsSceneEntity)
            {
                if (container != null && gameObject.scene != container.gameObject.scene)
                {
                    transform.SetParent(null, true);
                    SceneManager.MoveGameObjectToScene(gameObject, container.gameObject.scene);
                }
                if (container != null) transform.SetParent(container.transform, true);
                else if (previous != null && (previous.IsDynamic || previous.IsRuntime)) transform.SetParent(null, true); // out of a departing carrier or a retiring runtime box, whose object is about to be destroyed
            }
            foreach (var b in Behaviours) b.OnContainerChanged(previous, container);
            ContainerChanged?.Invoke(previous, container);
            if (IsLocalPlayer) InstanceScenes.SetView(InstanceId);
        }

        internal void InvokeSpawn()
        {
            IsSpawned = true;
            foreach (var b in Behaviours) b.OnNetworkSpawn();
        }

        internal void InvokeDespawn()
        {
            foreach (var b in Behaviours) b.OnNetworkDespawn();
            IsSpawned = false;
        }

        internal void SetAuthority(bool authority)
        {
            if (HasAuthority == authority) return;
            HasAuthority = authority;
            // A new authority (new epoch) opens its stream with keyframes so the gateway cache and ghosts restart clean.
            if (authority) foreach (var b in SyncBehaviours) { b.SyncEverSent = false; b.SyncWrittenThisTick = false; b.SyncDirty = true; SyncDirty = true; }
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
                return transform.parent == Container.transform ? transform.localPosition : Container.ToLocal(transform.position);
            }
        }

        public Quaternion LocalRotation
        {
            get
            {
                if (Container == null) return transform.rotation;
                return transform.parent == Container.transform ? transform.localRotation : Container.InverseRotation * transform.rotation;
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
                if (t.parent == container.transform)
                {
                    t.localPosition = localPosition;
                    t.localRotation = localRotation;
                }
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
