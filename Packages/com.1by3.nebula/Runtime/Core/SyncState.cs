using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Who receives a behavior's sync state (<see cref="NetworkBehaviour.SyncAudience"/>). An audience only restricts
    /// <b>clients</b>: every worker that holds a ghost of the entity receives every behavior's sync state whatever its
    /// audience, because game logic on a neighboring worker (a hit test, an interaction, the next owner after a
    /// handover) reads those copies. <see cref="Owner"/> and <see cref="Custom"/> therefore mean "the workers, plus these
    /// clients". The audience is configuration, not state: declare it on the behavior and do not change it after spawn.
    /// </summary>
    public enum SyncAudience : byte
    {
        /// <summary>Every client that has the entity in its interest set. The default, and what Nebula always did.</summary>
        Everyone = 0,
        /// <summary>Only the client that owns the entity (<c>NetworkIdentity.OwnerClientId</c>). No client at all for an unowned entity.</summary>
        Owner = 1,
        /// <summary>No client. Workers holding a ghost of the entity still receive it.</summary>
        WorkersOnly = 2,
        /// <summary>
        /// The clients the authority chooses with <c>NetworkBehaviour.IsInSyncAudience</c>, evaluated again every
        /// <c>NetworkBehaviour.SyncAudienceRefreshTicks</c> ticks and whenever the behavior calls
        /// <c>NetworkBehaviour.MarkSyncAudienceDirty</c>.
        /// </summary>
        Custom = 3,
    }

    /// <summary>
    /// Envelope shared by the per-tick sync stream (EntityState/GhostSyncState messages) and the spawn snapshot:
    /// <c>[count]{[behaviourIndex][flags][len:ushort][chunk]}</c>. Each chunk is bounded so a behaviour that reads too
    /// much or too little cannot corrupt its neighbours. Kept free of NetworkIdentity so the gateway can cache
    /// keyframes without instantiating anything.
    /// </summary>
    public static class SyncStateCodec
    {
        [Flags]
        public enum ChunkFlags : byte
        {
            None = 0,
            /// <summary>The chunk is a keyframe: everything the behaviour replicates, not just what changed.</summary>
            Full = 1,
            /// <summary>
            /// Bits 1 and 2 hold the behavior's <see cref="SyncAudience"/> (<see cref="AudienceMask"/>). Zero is
            /// <see cref="SyncAudience.Everyone"/>, so a chunk of an unrestricted behavior is byte for byte what it was
            /// before audiences existed. The gateway filters on these bits; workers and clients ignore them.
            /// </summary>
            AudienceOwner = 1 << 1,
            /// <summary>See <see cref="AudienceOwner"/>: the chunk is for workers only.</summary>
            AudienceWorkersOnly = 2 << 1,
            /// <summary>See <see cref="AudienceOwner"/>: the chunk is for the member set the authority sent (<see cref="SyncAudienceMsg"/>).</summary>
            AudienceCustom = 3 << 1,
            /// <summary>The two audience bits.</summary>
            AudienceMask = 3 << 1,
            /// <summary>
            /// Gateway to client, protocol 20 and later: this client left the behavior's audience. The chunk is empty.
            /// The client calls <c>NetworkBehaviour.OnSyncStateCleared</c> instead of <c>ReadSyncState</c>.
            /// </summary>
            Cleared = 1 << 3,
        }

        public delegate void ChunkVisitor(byte behaviourIndex, ChunkFlags flags, ArraySegment<byte> chunk);

        /// <summary>The audience a chunk's flags carry.</summary>
        public static SyncAudience AudienceOf(ChunkFlags flags) => (SyncAudience)(((byte)flags & (byte)ChunkFlags.AudienceMask) >> 1);

        /// <summary>The flag bits that carry <paramref name="audience"/>; none for <see cref="SyncAudience.Everyone"/>.</summary>
        public static ChunkFlags FlagsOf(SyncAudience audience) => (ChunkFlags)(((byte)audience & 3) << 1);

        /// <summary>Whether the flags name an audience that limits which clients receive the chunk (anything but <see cref="SyncAudience.Everyone"/>).</summary>
        public static bool IsRestricted(ChunkFlags flags) => (flags & ChunkFlags.AudienceMask) != 0;

        public static int BeginEnvelope(NetworkWriter w)
        {
            int at = w.Length;
            w.WriteByte(0);
            return at;
        }

        public static void EndEnvelope(NetworkWriter w, int countAt, byte count)
        {
            w.Buffer[countAt] = count;
        }

#if !NEBULA_SERVICE
        public static void WriteChunk(NetworkWriter w, byte behaviourIndex, ChunkFlags flags, NetworkBehaviour b, bool full)
        {
            w.WriteByte(behaviourIndex);
            w.WriteByte((byte)flags);
            int lenAt = w.ReserveUShort();
            int start = w.Length;
            b.WriteSyncState(w, full);
            w.PatchUShort(lenAt, (ushort)(w.Length - start));
        }

#endif
        public static void WriteRawChunk(NetworkWriter w, byte behaviourIndex, ChunkFlags flags, ArraySegment<byte> chunk)
        {
            w.WriteByte(behaviourIndex);
            w.WriteByte((byte)flags);
            w.WriteUShort((ushort)chunk.Count);
            w.WriteRaw(chunk);
        }

        public static void ReadEnvelope(NetworkReader r, ChunkVisitor visit)
        {
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                byte index = r.ReadByte();
                var flags = (ChunkFlags)r.ReadByte();
                var chunk = r.ReadSegment(r.ReadUShort());
                visit(index, flags, chunk);
            }
        }
    }

    /// <summary>
    /// The client member sets of an entity's <see cref="SyncAudience.Custom"/> behaviors, as the authority last
    /// evaluated them: <c>[count:byte]{[behaviourIndex:byte][n:ushort]{[clientId:ulong]}}</c>, each set sorted
    /// ascending. It rides the spawn (<see cref="EntitySpawnMsg.Audience"/>) and <see cref="SyncAudienceMsg"/>. The
    /// gateway filters Custom chunks by it and never forwards it to a client.
    /// </summary>
    public static class SyncAudienceCodec
    {
        /// <summary>
        /// The most clients one Custom behavior's audience may name. Past it the authority keeps the lowest session
        /// ids and logs a warning once per behavior; a client left out does not receive the state (fail closed). The
        /// whole set is sent on every change, 8 bytes a member, so the bound keeps one change to about 2 KB.
        /// </summary>
        public const int MaxMembers = 256;

        /// <summary>Write the member sets. Each set must already be sorted; at most <see cref="MaxMembers"/> of each is written.</summary>
        public static void Write(NetworkWriter w, IReadOnlyList<KeyValuePair<byte, ulong[]>> sets)
        {
            int count = Math.Min(sets?.Count ?? 0, 255);
            w.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                w.WriteByte(sets[i].Key);
                var members = sets[i].Value ?? Array.Empty<ulong>();
                int n = Math.Min(members.Length, MaxMembers);
                w.WriteUShort((ushort)n);
                for (int m = 0; m < n; m++) w.WriteULong(members[m]);
            }
        }

        /// <summary>Read a block written by <see cref="Write"/>. Null or empty reads as no sets. Each set comes back sorted.</summary>
        public static Dictionary<byte, ulong[]> Read(byte[] block)
        {
            var sets = new Dictionary<byte, ulong[]>();
            if (block == null || block.Length == 0) return sets;
            var r = new NetworkReader(block);
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                byte index = r.ReadByte();
                int n = r.ReadUShort();
                if (n > MaxMembers) throw new FormatException($"audience set of {n} members exceeds {MaxMembers}");
                var members = new ulong[n];
                for (int m = 0; m < n; m++) members[m] = r.ReadULong();
                Array.Sort(members);
                sets[index] = members;
            }
            return sets;
        }

        /// <summary>Whether a sorted member set contains <paramref name="clientId"/>.</summary>
        public static bool Contains(ulong[] sorted, ulong clientId) =>
            sorted != null && clientId != 0 && Array.BinarySearch(sorted, clientId) >= 0;
    }
}
