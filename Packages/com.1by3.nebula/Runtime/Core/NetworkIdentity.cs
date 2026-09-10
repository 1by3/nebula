using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Marks a GameObject as a meshed entity. Holds the entity's network identity (a 64-bit id minted by the
    /// worker that spawned it, never by a central allocator), its current container, its authority epoch and
    /// which worker/client owns it. Every <see cref="NetworkBehaviour"/> on the object hangs off this.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkIdentity : MonoBehaviour
    {
        [Tooltip("Set automatically from the NebulaConfig prefab list at spawn time.")]
        public ushort PrefabId = ushort.MaxValue;
        [Tooltip("Non-zero for an entity authored into a scene (assigned when the scene is saved). The worker owning its container spawns it; clients and neighbours bind to their own copy of the scene object. Leave 0 on prefabs.")]
        public uint SceneId;

        /// <summary>Authored into a scene rather than spawned from a prefab: the object belongs to its scene and is bound, never instantiated or destroyed, by the network (see <see cref="SceneEntities"/>).</summary>
        public bool IsSceneEntity => SceneId != 0;

        public ulong NetId { get; internal set; }
        public uint OwnerClientId { get; internal set; }
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
        public Container Container { get; internal set; }
        public ushort ContainerIndex => Container != null ? Container.Index : ushort.MaxValue;
        public bool IsSpawned { get; internal set; }
        /// <summary>Worker side: this process is authoritative. Client side: always false.</summary>
        public bool HasAuthority { get; internal set; }
        /// <summary>Client side: owned by the local client.</summary>
        public bool IsLocalPlayer { get; internal set; }
        /// <summary>Index of the worker currently authoritative (as last heard). Debug/overlay only.</summary>
        public ushort OwnerWorkerIndex { get; internal set; }
        /// <summary>Velocity as reported by the authority; used for extrapolation and carried through handover.</summary>
        public Vector3 Velocity;

        public NetworkBehaviour[] Behaviours { get; private set; } = Array.Empty<NetworkBehaviour>();
        internal NetworkVariableBase[] AllVars = Array.Empty<NetworkVariableBase>();
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

        // ---- pose history (worker side): what this entity looked like N ticks ago, for lag-compensated hit tests.
        // A shooter aims at what its screen showed, which is interpolation delay + transit + input lead in the past;
        // the worker records every entity's pose per tick (authoritative and ghost alike) so game code can test a
        // shot against where the victim was at the shooter's aim tick instead of where it is now.

        /// <summary>Ticks of pose history kept per entity (about a second at 60 Hz).</summary>
        public const int PoseHistoryTicks = 64;

        private struct PoseSample { public uint Tick; public Vector3 Position; public Quaternion Rotation; public bool Valid; }
        private PoseSample[] _poseHistory;

        internal void RecordPose(uint tick)
        {
            if (_poseHistory == null) _poseHistory = new PoseSample[PoseHistoryTicks];
            ref var s = ref _poseHistory[tick % PoseHistoryTicks];
            s.Tick = tick;
            s.Position = transform.position;
            s.Rotation = transform.rotation;
            s.Valid = true;
        }

        /// <summary>
        /// The pose recorded for <paramref name="tick"/>, or the nearest later one within a few ticks (a ghost
        /// that arrived recently has a short history). False when nothing usable was recorded.
        /// </summary>
        public bool TryGetPoseAt(uint tick, out Vector3 position, out Quaternion rotation)
        {
            if (_poseHistory != null)
            {
                for (uint t = tick; t <= tick + 4; t++)
                {
                    ref var s = ref _poseHistory[t % PoseHistoryTicks];
                    if (s.Valid && s.Tick == t)
                    {
                        position = s.Position;
                        rotation = s.Rotation;
                        return true;
                    }
                }
            }
            position = transform.position;
            rotation = transform.rotation;
            return false;
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
            if (_poseHistory != null)
                for (int i = 0; i < _poseHistory.Length; i++) if (_poseHistory[i].Valid) _poseHistory[i].Position += delta;
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
            OwnerIsBot = false;
            IsServerDriven = false;
            HasAuthority = false;
            IsLocalPlayer = false;
            OwnerWorkerIndex = 0;
            Velocity = Vector3.zero;
            Container = null;
            if (Interpolator != null)
            {
                Destroy(Interpolator);
                Interpolator = null;
            }
            _poseHistory = null;
            ClearDirty();
        }

        internal void Initialize()
        {
            if (Initialized) return;
            Initialized = true;
            Live.Add(this);

            var found = GetComponentsInChildren<NetworkBehaviour>(true);
            // Deterministic order on every process: the same prefab yields the same component order.
            Behaviours = found;
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
            }
            AllVars = vars.ToArray();
            var sync = new List<NetworkBehaviour>();
            foreach (var b in Behaviours) if (b.HasSyncState) sync.Add(b);
            SyncBehaviours = sync.ToArray();
        }

        private static NetworkVariableBase[] DiscoverVars(NetworkBehaviour b)
        {
            var list = new List<(int token, NetworkVariableBase v)>();
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
                    list.Add((f.MetadataToken, v));
                }
            }
            var ordered = list.OrderBy(x => x.token).Select(x => x.v).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                ordered[i].Owner = b;
                ordered[i].Index = i;
            }
            return ordered;
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
        public void ReadHandoverState(NetworkReader reader)
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
                if (b.SyncDelivery != delivery) continue;
                bool keyframe = keyframeTick || !b.SyncEverSent;
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
        public Container SyncContainer { get; private set; }

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
            Container = container;
            // A scene object stays in its scene's hierarchy (the streamer moves the scene, not the container).
            if (reparent && container != null && !IsSceneEntity)
            {
                transform.SetParent(container.transform, true);
            }
            foreach (var b in Behaviours) b.OnContainerChanged(previous, container);
            ContainerChanged?.Invoke(previous, container);
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

        /// <summary>Position in the current container's local space (what goes on the wire).</summary>
        public Vector3 LocalPosition => Container != null ? Container.ToLocal(transform.position) : transform.position;
        public Quaternion LocalRotation => Container != null ? Container.InverseRotation * transform.rotation : transform.rotation;

        public void SetLocalPose(Container container, Vector3 localPosition, Quaternion localRotation)
        {
            if (container != null)
            {
                transform.SetPositionAndRotation(container.ToWorld(localPosition), container.Rotation * localRotation);
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
