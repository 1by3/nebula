using System;

namespace Nebula
{
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
        }

        public delegate void ChunkVisitor(byte behaviourIndex, ChunkFlags flags, ArraySegment<byte> chunk);

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

        public static void WriteChunk(NetworkWriter w, byte behaviourIndex, ChunkFlags flags, NetworkBehaviour b, bool full)
        {
            w.WriteByte(behaviourIndex);
            w.WriteByte((byte)flags);
            int lenAt = w.ReserveUShort();
            int start = w.Length;
            b.WriteSyncState(w, full);
            w.PatchUShort(lenAt, (ushort)(w.Length - start));
        }

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
}
