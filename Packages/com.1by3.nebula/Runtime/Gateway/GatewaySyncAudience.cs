using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Sync audiences, seen from the gateway (<c>docs/sync-audience.md</c>). A worker flags each sync chunk with its
    /// behaviour's <see cref="SyncAudience"/> and sends the member sets of Custom behaviours alongside
    /// (<see cref="SyncAudienceMsg"/>, and in the spawn). The gateway is where a restricted chunk meets the individual
    /// clients, so every path a chunk can take to a client is filtered here:
    /// <list type="bullet">
    /// <item>per-tick deltas and keyframes (<see cref="BroadcastSyncFiltered"/>);</item>
    /// <item>a spawn relayed to the clients that already hold the entity (<see cref="BroadcastSpawnFiltered"/>);</item>
    /// <item>the cached keyframes a late joiner's spawn is built from (<see cref="CachedStateFor"/>);</item>
    /// <item>and the keyframe a client gets the moment it joins an audience (<see cref="SendAudienceChanges"/>).</item>
    /// </list>
    /// A client that leaves an audience is sent a <see cref="SyncStateCodec.ChunkFlags.Cleared"/> chunk. An entity with
    /// no restricted behaviour never reaches any of this: its chunks are relayed exactly as before.
    /// </summary>
    public sealed partial class NebulaGateway
    {
        private struct SyncChunk
        {
            public byte Index;
            public SyncStateCodec.ChunkFlags Flags;
            public ArraySegment<byte> Bytes;
        }

        private readonly List<SyncChunk> _syncChunks = new List<SyncChunk>();
        private readonly NetworkWriter _syncEnvelope = new NetworkWriter(1024);
        private readonly NetworkWriter _syncPublic = new NetworkWriter(1024);
        private readonly NetworkWriter _syncMessage = new NetworkWriter(1024);
        private readonly List<byte> _audienceIndices = new List<byte>();

        // ------------------------------------------------------------------------------------------- membership

        /// <summary>Whether <paramref name="clientId"/> is in a behaviour's audience, given who owns the entity and the Custom sets.</summary>
        private static bool IsAudienceMember(SyncAudience audience, byte index, ulong clientId, ulong owner, Dictionary<byte, ulong[]> sets)
        {
            switch (audience)
            {
                case SyncAudience.Everyone: return true;
                case SyncAudience.Owner: return owner != 0 && clientId == owner;
                case SyncAudience.Custom: return sets != null && sets.TryGetValue(index, out var members) && SyncAudienceCodec.Contains(members, clientId);
                default: return false; // WorkersOnly: no client, ever
            }
        }

        /// <summary>
        /// Whether <paramref name="client"/> may be sent a chunk flagged <paramref name="flags"/>. A Custom chunk
        /// written under a newer audience generation than the sets this gateway holds is sent to nobody: the sets that
        /// decide it are still on their way, and the old ones may name a client that has since left (D6).
        /// </summary>
        private static bool ReceivesChunk(EntityRecord rec, ClientConn client, byte index, SyncStateCodec.ChunkFlags flags, uint generation)
        {
            var audience = SyncStateCodec.AudienceOf(flags);
            if (audience == SyncAudience.Everyone) return true;
            if (audience == SyncAudience.Custom && generation > rec.AudienceGeneration) return false;
            return IsAudienceMember(audience, index, client.ClientId, rec.OwnerClientId, rec.AudienceSets);
        }

        /// <summary>Take the audience state a worker's spawn carries: its generation and its Custom member sets.</summary>
        private void ApplySpawnAudience(EntityRecord rec, in EntitySpawnMsg msg)
        {
            rec.AudienceGeneration = msg.AudienceGeneration;
            if (msg.Audience == null || msg.Audience.Length == 0)
            {
                // No sets: nobody is in any Custom audience. Kept as null so an entity without one costs nothing.
                rec.AudienceSets = null;
                return;
            }
            rec.AudienceSets = ReadAudienceSets(rec, msg.Audience);
        }

        private static Dictionary<byte, ulong[]> ReadAudienceSets(EntityRecord rec, byte[] block)
        {
            Dictionary<byte, ulong[]> sets;
            try { sets = SyncAudienceCodec.Read(block); }
            catch (Exception ex)
            {
                // Fail closed: an unreadable set admits nobody.
                NebulaLog.Warn($"entity #{rec.NetId}: unreadable audience sets ({ex.Message}); its Custom state goes to no client until the next update");
                return new Dictionary<byte, ulong[]>();
            }
            if (sets.Count > 0 && rec.Audiences == null) rec.Audiences = new Dictionary<byte, SyncAudience>();
            foreach (var index in sets.Keys) rec.Audiences[index] = SyncAudience.Custom;
            return sets;
        }

        // ------------------------------------------------------------------------------------------- the per-tick stream

        /// <summary>
        /// Split a worker's sync message into its chunks (kept in <see cref="_syncChunks"/>, pointing into the
        /// message), cache every keyframe with its audience, and say whether any chunk is restricted.
        /// </summary>
        private bool ParseSyncChunks(EntityRecord rec, in EntitySyncMsg msg, out bool rated, out bool ratedKeyframe)
        {
            _syncChunks.Clear();
            bool restricted = false;
            rated = false;
            ratedKeyframe = false;
            if (msg.Chunks == null || msg.Chunks.Length == 0) return false;
            _reader.Set(new ArraySegment<byte>(msg.Chunks));
            int count = _reader.ReadByte();
            for (int i = 0; i < count; i++)
            {
                byte index = _reader.ReadByte();
                var flags = (SyncStateCodec.ChunkFlags)_reader.ReadByte();
                var chunk = _reader.ReadSegment(_reader.ReadUShort());
                if ((flags & SyncStateCodec.ChunkFlags.Full) != 0) rec.StoreKeyframe(index, chunk, flags, msg.AudienceGeneration, msg.Tick);
                if (SyncStateCodec.IsRestricted(flags))
                {
                    restricted = true;
                    rec.NoteAudience(index, flags);
                }
                if (IsRated(index, flags))
                {
                    rated = true;
                    rec.RatedBehaviours |= 1UL << index;
                    if ((flags & (SyncStateCodec.ChunkFlags.Full | SyncStateCodec.ChunkFlags.Settled)) == SyncStateCodec.ChunkFlags.Full) ratedKeyframe = true;
                }
                _syncChunks.Add(new SyncChunk { Index = index, Flags = flags, Bytes = chunk });
            }
            return restricted;
        }

        /// <summary>
        /// Whether a chunk follows the entity's distance tier (D11). Only the first 64 behaviours of an entity can be:
        /// a client's missed deltas are kept as one bit per behaviour. A chunk of a later one is sent as if unrated.
        /// </summary>
        private static bool IsRated(byte index, SyncStateCodec.ChunkFlags flags) =>
            index < 64 && (flags & SyncStateCodec.ChunkFlags.DistanceRated) != 0;

        private readonly List<bool> _syncInclude = new List<bool>();

        /// <summary>
        /// Decide, for one client, which chunks of <see cref="_syncChunks"/> it is sent (into <see cref="_syncInclude"/>)
        /// and update which rated behaviours it is behind on. Returns how many it gets; <paramref name="custom"/> says
        /// whether that set differs from "every unrestricted chunk" (it has a restricted one, or a rated one was
        /// withheld), so the client needs a message of its own.
        /// <para>
        /// A rated chunk (docs/server-owned-entities.md D11): a settle keyframe goes to everyone. In the client's
        /// every-update band (divisor 1, and always for its own pawn) it gets every chunk, except deltas of a behaviour
        /// it is behind on, until a keyframe catches it up. Further away it gets only keyframes, and only the one that
        /// opens a new window of its rate (<see cref="RelevanceTiers.StartsWindow"/>, measured from the previous rated
        /// keyframe message); everything withheld leaves it behind.
        /// </para>
        /// </summary>
        private int SelectSyncChunks(EntityRecord rec, ClientConn client, in EntitySyncMsg msg, bool rated, out bool custom)
        {
            custom = false;
            _syncInclude.Clear();
            int divisor = 1;
            bool windowOpen = false;
            ulong behind = 0;
            if (rated)
            {
                divisor = DivisorFor(rec, client);
                int stream = msg.Reliable ? 0 : 1;
                windowOpen = divisor != 1 && RelevanceTiers.StartsWindow(msg.Tick, rec.HasRatedKeyframeTick[stream], rec.RatedKeyframeTick[stream], rec.NetId, divisor);
                if (client.SyncBehind != null) client.SyncBehind.TryGetValue(rec.NetId, out behind);
            }
            ulong before = behind;
            int count = 0;
            for (int i = 0; i < _syncChunks.Count; i++)
            {
                var chunk = _syncChunks[i];
                bool restricted = SyncStateCodec.IsRestricted(chunk.Flags);
                bool include = !restricted || ReceivesChunk(rec, client, chunk.Index, chunk.Flags, msg.AudienceGeneration);
                if (include && rated && IsRated(chunk.Index, chunk.Flags))
                {
                    ulong bit = 1UL << chunk.Index;
                    bool full = (chunk.Flags & SyncStateCodec.ChunkFlags.Full) != 0;
                    bool settled = (chunk.Flags & SyncStateCodec.ChunkFlags.Settled) != 0;
                    bool send = settled
                        || (full && (divisor == 1 || windowOpen))
                        || (divisor == 1 && (behind & bit) == 0);
                    if (send)
                    {
                        if (full) behind &= ~bit;
                    }
                    else
                    {
                        include = false;
                        behind |= bit;
                        custom = true;
                    }
                }
                if (include && restricted) custom = true;
                _syncInclude.Add(include);
                if (include) count++;
            }
            if (behind != before)
            {
                if (behind == 0) client.SyncBehind.Remove(rec.NetId);
                else (client.SyncBehind ??= new Dictionary<ulong, ulong>())[rec.NetId] = behind;
            }
            return count;
        }

        /// <summary>
        /// A client is about to be sent this entity's spawn, built from the cached keyframes: it is behind on every
        /// rated behaviour whose keyframe is older than the newest sync state relayed, since the deltas after it are
        /// not in the spawn. Anything it was behind on before is forgotten.
        /// </summary>
        private void ResetSyncBehind(EntityRecord rec, ClientConn client)
        {
            client.SyncBehind?.Remove(rec.NetId);
            if (rec.RatedBehaviours == 0 || rec.SyncKeyframes == null) return;
            ulong behind = 0;
            foreach (var kv in rec.SyncKeyframes)
                if (kv.Key < 64 && (rec.RatedBehaviours & (1UL << kv.Key)) != 0 && kv.Value.Tick < rec.LastSyncTick) behind |= 1UL << kv.Key;
            if (behind != 0) (client.SyncBehind ??= new Dictionary<ulong, ulong>())[rec.NetId] = behind;
        }

        /// <summary>
        /// Relay a sync message some of whose chunks are restricted or distance-rated: each observer gets the chunks
        /// it may have, at the rate its distance gives (<see cref="SelectSyncChunks"/>), and nothing else. Observers
        /// that get exactly the unrestricted ones share one message, built once; the others get one each.
        /// </summary>
        private void BroadcastSyncFiltered(EntityRecord rec, in EntitySyncMsg msg, bool rated)
        {
            var header = msg;
            header.AudienceGeneration = 0; // never to a client
            ArraySegment<byte> publicMessage = default;
            bool publicBuilt = false;
            for (int o = rec.Observers.Count - 1; o >= 0; o--)
            {
                var client = rec.Observers[o];
                if (!client.Welcomed) continue;
                int count = SelectSyncChunks(rec, client, msg, rated, out bool custom);
                if (count == 0) continue;
                ArraySegment<byte> segment;
                if (!custom)
                {
                    if (!publicBuilt)
                    {
                        publicBuilt = true;
                        publicMessage = WriteSyncFor(_syncPublic, header, null);
                    }
                    segment = publicMessage;
                }
                else segment = WriteSyncFor(_syncMessage, header, _syncInclude);
                if (segment.Count == 0) continue;
                if (msg.Delivery == Delivery.ReliableOrdered) AppendReliable(client, segment);
                else Send(client.PeerId, msg.Delivery, segment);
            }
        }

        /// <summary>
        /// Write an EntityState message holding the chunks of <see cref="_syncChunks"/> that <paramref name="include"/>
        /// selects, or every unrestricted one when it is null. Returns an empty segment when nothing qualifies.
        /// </summary>
        private ArraySegment<byte> WriteSyncFor(NetworkWriter into, in EntitySyncMsg header, List<bool> include)
        {
            _syncEnvelope.Reset();
            int at = SyncStateCodec.BeginEnvelope(_syncEnvelope);
            byte n = 0;
            for (int i = 0; i < _syncChunks.Count; i++)
            {
                var chunk = _syncChunks[i];
                if (include != null ? !include[i] : SyncStateCodec.IsRestricted(chunk.Flags)) continue;
                SyncStateCodec.WriteRawChunk(_syncEnvelope, chunk.Index, chunk.Flags, chunk.Bytes);
                n++;
            }
            if (n == 0) return default;
            SyncStateCodec.EndEnvelope(_syncEnvelope, at, n);
            into.Reset();
            WriteSyncMessage(into, header, _syncEnvelope.ToSegment());
            return into.ToSegment();
        }

        /// <summary>An EntityState message with <paramref name="chunks"/> as its envelope, the rest of it from <paramref name="header"/>.</summary>
        private static void WriteSyncMessage(NetworkWriter w, in EntitySyncMsg header, ArraySegment<byte> chunks)
        {
            w.WriteByte((byte)MsgId.EntityState);
            w.WriteULong(header.NetId);
            w.WriteUInt(header.Epoch);
            w.WriteUInt(header.Tick);
            header.Container.Write(w);
            w.WriteBool(header.Reliable);
            w.WriteBytes(chunks);
        }

        // ------------------------------------------------------------------------------------------- spawns

        /// <summary>
        /// The spawn state for one client: the newest cached keyframe of every behaviour it may have. What a late
        /// joiner, a new subscriber and a reconnected session start from.
        /// </summary>
        private byte[] CachedStateFor(EntityRecord rec, ClientConn client)
        {
            if (rec.SyncKeyframes == null) return rec.LastSpawn.State ?? Array.Empty<byte>();
            _syncEnvelope.Reset();
            int at = SyncStateCodec.BeginEnvelope(_syncEnvelope);
            byte n = 0;
            foreach (var kv in rec.SyncKeyframes)
            {
                var keyframe = kv.Value;
                if (!ReceivesChunk(rec, client, kv.Key, keyframe.Flags, keyframe.Generation)) continue;
                SyncStateCodec.WriteRawChunk(_syncEnvelope, kv.Key, SyncStateCodec.ChunkFlags.Full | keyframe.Flags, new ArraySegment<byte>(keyframe.Bytes));
                n++;
            }
            SyncStateCodec.EndEnvelope(_syncEnvelope, at, n);
            return _syncEnvelope.ToArray();
        }

        /// <summary>
        /// A worker's spawn for an entity some observers already hold (a handover, a container change): each gets it
        /// with the snapshot chunks it may have. The spawn is fresh from the worker, so it is the snapshot that is
        /// filtered, not the cache.
        /// </summary>
        private void BroadcastSpawnFiltered(EntityRecord rec, in EntitySpawnMsg msg)
        {
            var spawn = msg.ForClient();
            for (int o = rec.Observers.Count - 1; o >= 0; o--)
            {
                var client = rec.Observers[o];
                if (!client.Welcomed) continue;
                spawn.State = FilterEnvelope(rec, client, msg.State, msg.AudienceGeneration);
                _writer.Reset();
                spawn.Write(_writer, MsgId.EntitySpawn);
                AppendReliable(client, _writer.ToSegment());
            }
        }

        /// <summary>A copy of <paramref name="state"/> holding only the chunks <paramref name="client"/> may have.</summary>
        private byte[] FilterEnvelope(EntityRecord rec, ClientConn client, byte[] state, uint generation)
        {
            if (state == null || state.Length == 0) return state;
            var r = new NetworkReader(state);
            _syncEnvelope.Reset();
            int at = SyncStateCodec.BeginEnvelope(_syncEnvelope);
            byte n = 0;
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                byte index = r.ReadByte();
                var flags = (SyncStateCodec.ChunkFlags)r.ReadByte();
                var chunk = r.ReadSegment(r.ReadUShort());
                if (!ReceivesChunk(rec, client, index, flags, generation)) continue;
                SyncStateCodec.WriteRawChunk(_syncEnvelope, index, flags, chunk);
                n++;
            }
            SyncStateCodec.EndEnvelope(_syncEnvelope, at, n);
            return _syncEnvelope.ToArray();
        }

        // ------------------------------------------------------------------------------------------- joining and leaving

        /// <summary>
        /// A worker says the member sets of an entity's Custom behaviours changed. Applied whole; the clients that
        /// joined a set get its newest keyframe and the ones that left are told to drop their copy.
        /// </summary>
        private void OnSyncAudience(WorkerConn w, SyncAudienceMsg msg)
        {
            if (HoldBehind(msg.NetId, msg.Epoch, new HeldUpdate { Kind = HeldKind.Audience, Audience = msg, Worker = w.Index })) return;
            if (!_entities.TryGetValue(msg.NetId, out var rec) || msg.Epoch < rec.Epoch || rec.OwnerWorkerIndex != w.Index) return;
            // Sets are sent whole, so an older one arriving late has nothing to add.
            if (msg.Generation < rec.AudienceGeneration) return;
            var previous = rec.AudienceSets;
            rec.AudienceSets = ReadAudienceSets(rec, msg.Sets);
            rec.AudienceGeneration = msg.Generation;
            SendAudienceChanges(rec, rec.OwnerClientId, previous, sendJoins: true);
        }

        /// <summary>
        /// Compare every observer's membership of every restricted behaviour before (<paramref name="previousOwner"/>,
        /// <paramref name="previousSets"/>) and now. A client that left is sent one reliable message of
        /// <see cref="SyncStateCodec.ChunkFlags.Cleared"/> chunks. With <paramref name="sendJoins"/>, a client that joined
        /// is sent the behaviour's newest cached keyframe; a spawn carries that itself, so it passes false.
        /// </summary>
        private void SendAudienceChanges(EntityRecord rec, ulong previousOwner, Dictionary<byte, ulong[]> previousSets, bool sendJoins)
        {
            if (rec.Audiences == null || rec.Observers.Count == 0) return;
            _audienceIndices.Clear();
            foreach (var kv in rec.Audiences)
                if (kv.Value == SyncAudience.Owner || kv.Value == SyncAudience.Custom) _audienceIndices.Add(kv.Key);
            if (_audienceIndices.Count == 0) return;
            var header = new EntitySyncMsg { NetId = rec.NetId, Epoch = rec.Epoch, Tick = rec.LastSyncTick, Container = rec.Container, Reliable = true };
            for (int o = rec.Observers.Count - 1; o >= 0; o--)
            {
                var client = rec.Observers[o];
                if (!client.Welcomed) continue;
                _syncEnvelope.Reset();
                int at = SyncStateCodec.BeginEnvelope(_syncEnvelope);
                byte cleared = 0;
                for (int i = 0; i < _audienceIndices.Count; i++)
                {
                    byte index = _audienceIndices[i];
                    var audience = rec.Audiences[index];
                    bool was = IsAudienceMember(audience, index, client.ClientId, previousOwner, previousSets);
                    bool now = IsAudienceMember(audience, index, client.ClientId, rec.OwnerClientId, rec.AudienceSets);
                    if (was && !now)
                    {
                        SyncStateCodec.WriteRawChunk(_syncEnvelope, index, SyncStateCodec.ChunkFlags.Cleared | SyncStateCodec.FlagsOf(audience), default);
                        cleared++;
                    }
                    else if (!was && now && sendJoins) SendJoinKeyframe(rec, client, index);
                }
                if (cleared == 0) continue;
                SyncStateCodec.EndEnvelope(_syncEnvelope, at, cleared);
                _syncMessage.Reset();
                WriteSyncMessage(_syncMessage, header, _syncEnvelope.ToSegment());
                AppendReliable(client, _syncMessage.ToSegment());
            }
        }

        /// <summary>A client just joined a behaviour's audience: send it the newest keyframe held for it, reliably.</summary>
        private void SendJoinKeyframe(EntityRecord rec, ClientConn client, byte index)
        {
            if (rec.SyncKeyframes == null || !rec.SyncKeyframes.TryGetValue(index, out var keyframe)) return;
            if (!ReceivesChunk(rec, client, index, keyframe.Flags, keyframe.Generation)) return;
            var writer = new NetworkWriter(keyframe.Bytes.Length + 8);
            int at = SyncStateCodec.BeginEnvelope(writer);
            SyncStateCodec.WriteRawChunk(writer, index, SyncStateCodec.ChunkFlags.Full | keyframe.Flags, new ArraySegment<byte>(keyframe.Bytes));
            SyncStateCodec.EndEnvelope(writer, at, 1);
            var header = new EntitySyncMsg { NetId = rec.NetId, Epoch = rec.Epoch, Tick = keyframe.Tick, Container = rec.Container, Reliable = true };
            _syncMessage.Reset();
            WriteSyncMessage(_syncMessage, header, writer.ToSegment());
            AppendReliable(client, _syncMessage.ToSegment());
        }
    }
}
