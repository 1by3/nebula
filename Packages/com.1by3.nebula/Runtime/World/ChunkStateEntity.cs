using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// The entity that carries one chunk's <see cref="ChunkState"/> entries. Nebula spawns it from a built-in
    /// prefab (<see cref="PrefabId"/>) in the chunk's container the first time an object in the chunk gets an
    /// entry, and despawns it, deleting its record, when the last entry is gone. A game never adds this component
    /// or spawns the entity itself; it reads through <see cref="ChunkState"/> and writes through
    /// <see cref="ChunkStateService"/>.
    /// <para>
    /// The entries replicate as one NetworkVariable holding the whole map, sent again whenever it changes. The
    /// gateway keeps the latest copy for clients that arrive later. They are saved through
    /// <see cref="NetworkBehaviour.WritePersistentState"/>, so the entity's <see cref="PersistentEntity"/> record is
    /// the chunk's record in the store.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ChunkStateEntity : NetworkBehaviour
    {
        /// <summary>The built-in network prefab id of the chunk state entity. Reserved in <see cref="NetworkPrefabs"/>; no game list uses it.</summary>
        public const ushort PrefabId = NetworkPrefabs.ChunkStatePrefabId;

        /// <summary>Name of the built-in prefab, and of the prefab saved in the entity's records.</summary>
        public const string PrefabName = "NebulaChunkState";

        /// <summary>The map, as a NetworkVariable so it rides the spawn message, the gateway's cache, ghosts and handovers.</summary>
        private readonly EntriesVariable _entries = new EntriesVariable();

        private readonly SortedDictionary<ulong, ObjectState> _map = new SortedDictionary<ulong, ObjectState>();
        private readonly List<ulong> _scratchIds = new List<ulong>();
        private int _encodedBytes = ChunkStateCodec.HeaderBytes;
        /// <summary>Earliest expiry time of any entry, 0 when none expires.</summary>
        private long _nextExpiry;
        private bool _registered;

        /// <summary>
        /// When the map became empty on the worker that holds authority, on the service's clock; negative while it
        /// has entries. The service despawns the entity a moment later, so the emptying reaches clients first.
        /// </summary>
        internal float EmptySince = -1f;

        /// <summary>The id of the container (chunk) whose state this entity carries. Empty before it spawns.</summary>
        public string ContainerId { get; private set; } = "";

        /// <summary>How many entries the entity holds, including ones whose expiry time has passed but that are not removed yet.</summary>
        public int EntryCount => _map.Count;

        /// <summary>The encoded size of the entries, in bytes. At most <see cref="ChunkState.MaxEncodedBytes"/>.</summary>
        public int EncodedBytes => _encodedBytes;

        /// <summary>Whether the entity holds no entries.</summary>
        public bool IsEmpty => _map.Count == 0;

        // ------------------------------------------------------------------------------------------ reads

        internal bool TryGetLive(ulong objectId, long nowUnixMs, out ObjectState state)
        {
            if (_map.TryGetValue(objectId, out state) && state.IsLiveAt(nowUnixMs)) return true;
            state = default;
            return false;
        }

        internal int CountLive(long nowUnixMs)
        {
            if (_nextExpiry == 0 || nowUnixMs < _nextExpiry) return _map.Count;
            int n = 0;
            foreach (var kv in _map) if (kv.Value.IsLiveAt(nowUnixMs)) n++;
            return n;
        }

        internal void CollectLive(long nowUnixMs, List<KeyValuePair<ulong, ObjectState>> into)
        {
            foreach (var kv in _map) if (kv.Value.IsLiveAt(nowUnixMs)) into.Add(kv);
        }

        // ------------------------------------------------------------------------------------------ writes (authority)

        /// <summary>
        /// Whether setting <paramref name="objectId"/> to <paramref name="state"/> keeps the map within
        /// <see cref="ChunkState.MaxEncodedBytes"/>.
        /// </summary>
        internal bool Fits(ulong objectId, in ObjectState state)
        {
            int size = _encodedBytes + ChunkStateCodec.EntrySize(state);
            if (_map.TryGetValue(objectId, out var old)) size -= ChunkStateCodec.EntrySize(old);
            return size <= ChunkState.MaxEncodedBytes;
        }

        /// <summary>
        /// Put an entry in the map before the entity spawns, on the worker that creates it. No change is raised:
        /// the spawn raises one for every entry.
        /// </summary>
        internal void Seed(ulong objectId, in ObjectState state)
        {
            PutEntry(objectId, state);
            AfterAuthorityChange();
        }

        /// <summary>Set an entry on the authority, replicate it, schedule a checkpoint and raise the change. False when it would not fit.</summary>
        internal bool Set(ulong objectId, in ObjectState state)
        {
            if (!Fits(objectId, state)) return false;
            if (_map.TryGetValue(objectId, out var old) && old.Equals(state)) return true;
            PutEntry(objectId, state);
            AfterAuthorityChange();
            RaiseChange(objectId, ObjectStateChangeKind.Set, true, state);
            return true;
        }

        /// <summary>Remove an entry on the authority, replicate the removal, schedule a checkpoint and raise the change. False when there was none.</summary>
        internal bool Remove(ulong objectId)
        {
            if (!_map.TryGetValue(objectId, out var old)) return false;
            _map.Remove(objectId);
            _encodedBytes -= ChunkStateCodec.EntrySize(old);
            if (old.Expires && old.ExpiresAtUnixMs == _nextExpiry) RecomputeNextExpiry();
            AfterAuthorityChange();
            RaiseChange(objectId, ObjectStateChangeKind.Cleared, false, default);
            return true;
        }

        private void PutEntry(ulong objectId, in ObjectState state)
        {
            if (_map.TryGetValue(objectId, out var old)) _encodedBytes -= ChunkStateCodec.EntrySize(old);
            _map[objectId] = state;
            _encodedBytes += ChunkStateCodec.EntrySize(state);
            if (old.Expires && old.ExpiresAtUnixMs == _nextExpiry) RecomputeNextExpiry();
            if (state.Expires && (_nextExpiry == 0 || state.ExpiresAtUnixMs < _nextExpiry)) _nextExpiry = state.ExpiresAtUnixMs;
        }

        /// <summary>
        /// The map changed on the authority: send it to every copy, and ask for a checkpoint. An empty map's record
        /// is deleted rather than saved (<see cref="PersistentEntity.DiscardRecord"/>).
        /// </summary>
        private void AfterAuthorityChange()
        {
            _entries.Dirty = true;
            var identity = Identity;
            if (identity == null) return;
            identity.MarkVarsDirty();
            var pe = identity.Persistent;
            if (pe != null)
            {
                pe.DiscardRecord = _map.Count == 0;
                pe.MarkDirty();
            }
        }

        // ------------------------------------------------------------------------------------------ expiry

        /// <summary>
        /// Remove every entry whose expiry time has passed at <paramref name="nowUnixMs"/> and raise
        /// <see cref="ObjectStateChangeKind.Expired"/> for each. On the authority the removal is also replicated
        /// and saved. Cheap when nothing is due: one comparison.
        /// </summary>
        internal void PollExpiry(long nowUnixMs)
        {
            if (_nextExpiry == 0 || nowUnixMs < _nextExpiry) return;
            _scratchIds.Clear();
            foreach (var kv in _map) if (!kv.Value.IsLiveAt(nowUnixMs)) _scratchIds.Add(kv.Key);
            for (int i = 0; i < _scratchIds.Count; i++)
            {
                var id = _scratchIds[i];
                _encodedBytes -= ChunkStateCodec.EntrySize(_map[id]);
                _map.Remove(id);
            }
            RecomputeNextExpiry();
            if (_scratchIds.Count == 0) return;
            if (HasAuthority) AfterAuthorityChange();
            for (int i = 0; i < _scratchIds.Count; i++) RaiseChange(_scratchIds[i], ObjectStateChangeKind.Expired, false, default);
            _scratchIds.Clear();
        }

        private void RecomputeNextExpiry()
        {
            _nextExpiry = 0;
            foreach (var kv in _map)
            {
                long t = kv.Value.ExpiresAtUnixMs;
                if (t > 0 && (_nextExpiry == 0 || t < _nextExpiry)) _nextExpiry = t;
            }
        }

        /// <summary>Drop what has expired without raising anything: for a map that has just been loaded or received.</summary>
        private void PruneSilently(long nowUnixMs)
        {
            if (_nextExpiry == 0 || nowUnixMs < _nextExpiry) return;
            _scratchIds.Clear();
            foreach (var kv in _map) if (!kv.Value.IsLiveAt(nowUnixMs)) _scratchIds.Add(kv.Key);
            for (int i = 0; i < _scratchIds.Count; i++)
            {
                _encodedBytes -= ChunkStateCodec.EntrySize(_map[_scratchIds[i]]);
                _map.Remove(_scratchIds[i]);
            }
            _scratchIds.Clear();
            RecomputeNextExpiry();
        }

        private void Update()
        {
            // Only copies with something that expires pay for a clock read. The authority's service polls as well,
            // so an edit-mode worker (tests, tools) expires entries without a player loop.
            if (_nextExpiry == 0 || !IsSpawned) return;
            long now = ChunkState.NowUnixMs;
            if (now >= _nextExpiry) PollExpiry(now);
        }

        // ------------------------------------------------------------------------------------------ replication

        private void WriteEntries(NetworkWriter writer)
        {
            int lengthAt = writer.ReserveUShort();
            int start = writer.Length;
            ChunkStateCodec.WriteBody(writer, _map);
            writer.PatchUShort(lengthAt, (ushort)(writer.Length - start));
        }

        private static readonly SortedDictionary<ulong, ObjectState> Incoming = new SortedDictionary<ulong, ObjectState>();

        private void ReadEntries(NetworkReader reader)
        {
            var body = reader.ReadSegment(reader.ReadUShort());
            Incoming.Clear();
            if (!ChunkStateCodec.TryReadBody(body, Incoming, out string error))
            {
                NebulaLog.Warn($"chunk state of {(ContainerId != "" ? ContainerId : name)}: {error}; keeping the previous entries");
                Incoming.Clear();
                return;
            }
            long now = ChunkState.NowUnixMs;
            bool raise = _registered && ChunkState.HasListeners;
            if (raise)
            {
                // What left: cleared by a write, or expired on the authority before it expired here.
                _scratchIds.Clear();
                foreach (var kv in _map)
                    if (!Incoming.ContainsKey(kv.Key) && kv.Value.IsLiveAt(now)) _scratchIds.Add(kv.Key);
            }
            var previous = raise ? new Dictionary<ulong, ObjectState>(_map) : null;
            _map.Clear();
            _encodedBytes = ChunkStateCodec.HeaderBytes;
            foreach (var kv in Incoming)
            {
                _map[kv.Key] = kv.Value;
                _encodedBytes += ChunkStateCodec.EntrySize(kv.Value);
            }
            Incoming.Clear();
            RecomputeNextExpiry();
            PruneSilently(now);
            if (!raise) return;
            for (int i = 0; i < _scratchIds.Count; i++) RaiseChange(_scratchIds[i], ObjectStateChangeKind.Cleared, false, default);
            _scratchIds.Clear();
            foreach (var kv in _map)
            {
                if (previous.TryGetValue(kv.Key, out var old) && old.Equals(kv.Value) && old.IsLiveAt(now)) continue;
                RaiseChange(kv.Key, ObjectStateChangeKind.Set, true, kv.Value);
            }
        }

        // ------------------------------------------------------------------------------------------ persistence

        /// <inheritdoc/>
        public override void WritePersistentState(NetworkWriter writer)
        {
            if (_map.Count == 0) return;
            ChunkStateCodec.WriteBody(writer, _map);
        }

        /// <inheritdoc/>
        public override void ReadPersistentState(NetworkReader reader)
        {
            var body = reader.ReadSegment(reader.Remaining);
            Incoming.Clear();
            if (!ChunkStateCodec.TryReadBody(body, Incoming, out string error))
            {
                NebulaLog.Error($"chunk state record could not be read: {error}; it restores empty");
                Incoming.Clear();
                return;
            }
            _map.Clear();
            _encodedBytes = ChunkStateCodec.HeaderBytes;
            foreach (var kv in Incoming)
            {
                _map[kv.Key] = kv.Value;
                _encodedBytes += ChunkStateCodec.EntrySize(kv.Value);
            }
            Incoming.Clear();
            RecomputeNextExpiry();
            // Whatever expired while nobody was here is simply gone: the timer is the absolute time in the record.
            PruneSilently(ChunkState.NowUnixMs);
            var pe = Identity != null ? Identity.Persistent : null;
            if (pe != null) pe.DiscardRecord = _map.Count == 0;
        }

        // ------------------------------------------------------------------------------------------ lifecycle

        /// <inheritdoc/>
        public override void OnNetworkSpawn()
        {
            var identity = Identity;
            ContainerId = identity.Container != null ? identity.Container.ContainerId : "";
            // Bookkeeping, not simulation: it must not keep a scope "occupied" or count as load (docs/chunk-state.md D7).
            identity.ExcludeFromOccupancy = true;
            if (HasAuthority)
            {
                identity.RelevanceRadius = ChunkState.RelevanceRadiusFor(identity.Container);
                identity.SetCostWeight(0f);
            }
            PruneSilently(ChunkState.NowUnixMs);
            if (identity.Persistent != null && HasAuthority) identity.Persistent.DiscardRecord = _map.Count == 0;
            if (string.IsNullOrEmpty(ContainerId))
            {
                NebulaLog.Warn($"chunk state entity {identity} spawned outside any container; it cannot be read by chunk");
                return;
            }
            ChunkState.Register(this);
            _registered = true;
            if (!ChunkState.HasListeners) return;
            foreach (var kv in _map) RaiseChange(kv.Key, ObjectStateChangeKind.Set, true, kv.Value);
        }

        /// <inheritdoc/>
        public override void OnNetworkDespawn()
        {
            if (!_registered) return;
            // Unregistered first, so a handler that reads the chunk sees the state gone; raised while still
            // counted as registered, since the changes are this copy's.
            ChunkState.Unregister(this, ContainerId);
            if (ChunkState.HasListeners)
            {
                long now = ChunkState.NowUnixMs;
                foreach (var kv in _map)
                    if (kv.Value.IsLiveAt(now)) RaiseChange(kv.Key, ObjectStateChangeKind.Forgotten, false, default);
            }
            _registered = false;
        }

        /// <inheritdoc/>
        public override void OnGainedAuthority()
        {
            // The entity stands for its chunk: it never moves into a neighbouring or nested container, whatever
            // its position resolves to, and it follows the chunk's lease from worker to worker.
            if (Identity != null) Identity.ContainerPinned = true;
        }

        private void OnDestroy()
        {
            // Destroyed without a despawn (a scene torn down, the Editor leaving play mode).
            if (_registered) ChunkState.Unregister(this, ContainerId);
            _registered = false;
        }

        private void RaiseChange(ulong objectId, ObjectStateChangeKind kind, bool hasState, ObjectState state)
        {
            if (!_registered || !ChunkState.HasListeners) return;
            var container = Identity != null ? Identity.Container : null;
            ChunkState.Raise(new ObjectStateChange(container, ContainerId, objectId, kind, hasState, state, HasAuthority));
        }

        // ------------------------------------------------------------------------------------------ built-in prefab

        private static GameObject _template;

        /// <summary>
        /// The built-in prefab: an inactive, hidden object with a <see cref="NetworkIdentity"/>, a
        /// <see cref="PersistentEntity"/> and this component. Built once per domain, on first use, in every
        /// process, so every role instantiates <see cref="PrefabId"/> the same way without the game listing it.
        /// </summary>
        internal static GameObject Template
        {
            get
            {
                if (_template != null) return _template;
                var go = new GameObject(PrefabName) { hideFlags = HideFlags.HideAndDontSave };
                go.SetActive(false);
                var identity = go.AddComponent<NetworkIdentity>();
                identity.PrefabId = PrefabId;
                identity.CostWeight = 0f;
                var pe = go.AddComponent<PersistentEntity>();
                pe.PersistPose = true;
                pe.PersistVelocity = false;
                go.AddComponent<ChunkStateEntity>();
                _template = go;
                return go;
            }
        }

        /// <summary>The NetworkVariable the map replicates through. Written whole: the map is small and changes rarely.</summary>
        private sealed class EntriesVariable : NetworkVariableBase
        {
            public override void Write(NetworkWriter writer) => ((ChunkStateEntity)Owner).WriteEntries(writer);
            public override void Read(NetworkReader reader) => ((ChunkStateEntity)Owner).ReadEntries(reader);
        }
    }

    /// <summary>
    /// The encoding of a chunk's entries, shared by replication and the saved record:
    /// <code>
    /// [version:byte=1][count:ushort] { [objectId:ulong][flags:byte][value:uint]([expiresAtUnixMs:long])([payloadLength:byte][payload]) }
    /// </code>
    /// Entries are in object id order, so the same map always encodes to the same bytes. Flag 1 means the entry
    /// expires; flag 2 means it has a payload.
    /// </summary>
    internal static class ChunkStateCodec
    {
        public const byte Version = 1;
        public const int HeaderBytes = 3;
        private const byte FlagExpires = 1;
        private const byte FlagPayload = 2;

        public static int EntrySize(in ObjectState s) =>
            13 + (s.Expires ? 8 : 0) + (s.PayloadLength > 0 ? 1 + s.PayloadLength : 0);

        public static void WriteBody(NetworkWriter w, SortedDictionary<ulong, ObjectState> map)
        {
            w.WriteByte(Version);
            w.WriteUShort((ushort)Math.Min(map.Count, ushort.MaxValue));
            foreach (var kv in map)
            {
                var s = kv.Value;
                byte flags = 0;
                if (s.Expires) flags |= FlagExpires;
                if (s.PayloadLength > 0) flags |= FlagPayload;
                w.WriteULong(kv.Key);
                w.WriteByte(flags);
                w.WriteUInt(s.Value);
                if (s.Expires) w.WriteLong(s.ExpiresAtUnixMs);
                if (s.PayloadLength > 0)
                {
                    w.WriteByte((byte)s.PayloadLength);
                    w.WriteRaw(new ArraySegment<byte>(s.Payload));
                }
            }
        }

        public static bool TryReadBody(ArraySegment<byte> body, SortedDictionary<ulong, ObjectState> into, out string error)
        {
            error = null;
            if (body.Count == 0) return true;
            try
            {
                var r = new NetworkReader(body);
                byte version = r.ReadByte();
                if (version > Version)
                {
                    error = $"encoded by a newer build (version {version}, this build reads {Version})";
                    return false;
                }
                int count = r.ReadUShort();
                for (int i = 0; i < count; i++)
                {
                    ulong id = r.ReadULong();
                    byte flags = r.ReadByte();
                    uint value = r.ReadUInt();
                    long expires = (flags & FlagExpires) != 0 ? r.ReadLong() : 0;
                    byte[] payload = null;
                    if ((flags & FlagPayload) != 0)
                    {
                        int n = r.ReadByte();
                        var seg = r.ReadSegment(n);
                        payload = new byte[n];
                        Buffer.BlockCopy(seg.Array, seg.Offset, payload, 0, n);
                    }
                    into[id] = new ObjectState(value, expires, payload, noCopy: true);
                }
                return true;
            }
            catch (Exception e)
            {
                error = "malformed: " + e.Message;
                return false;
            }
        }
    }
}
