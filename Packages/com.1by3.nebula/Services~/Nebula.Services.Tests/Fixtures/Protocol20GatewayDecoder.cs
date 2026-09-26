using System.Text;

namespace Nebula.ServiceTests;

// Frozen protocol-20 oracle: do not replace literals or readers with production protocol types.
// Protocol 20 changed no frame layout this replay reads: it added audience bits to sync chunk flags and a Cleared
// chunk, both inside sync payloads this oracle does not inspect, so this is the protocol-19 oracle with its version
// literals raised.
// Covers the public-container handshake, spawn and transform stream exercised by the replay.
// Instance containers, game-defined variable/sync payload contents, RPCs and other message IDs
// are not compatibility claims made by this fixture. Like the protocol-20 client, unknown
// message IDs and optional trailing bytes are ignored inside their bounded frame. Known but
// uncovered instance-container payloads fail explicitly instead of guessing their framing.
internal sealed class Protocol20GatewayDecoder
{
    internal ulong ClientId;
    internal string Identity = "";
    internal int Welcomes;
    internal bool Joined;
    internal ushort NegotiatedVersion;
    internal readonly Dictionary<ulong, (ulong Owner, string Identity)> Spawns = new();
    internal readonly HashSet<ulong> StateEntities = new();
    internal readonly Dictionary<ulong, (float X, float Y, float Z)> Positions = new();
    internal readonly HashSet<string> Containers = new();
    internal readonly HashSet<byte> UninspectedMessageIds = new();

    internal void Read(byte[] frame) => Read(frame, 0);

    private void Read(byte[] frame, int depth)
    {
        if (depth > 8) throw new InvalidDataException("protocol-20 batch nesting limit");
        using var r = new Cursor(frame);
        byte id = r.Byte();
        switch (id)
        {
            case 2: // Welcome
                ClientId = r.U64();
                if (ClientId == 0 || r.Byte() == 0) throw new InvalidDataException("invalid welcome identity/tick rate");
                r.U32();
                Identity = r.Text();
                if (Identity.Length == 0 || r.Text().Length == 0 || r.Text().Length == 0)
                    throw new InvalidDataException("missing handshake identity or tokens");
                r.Bool();
                NegotiatedVersion = r.Remaining == 0 ? (ushort)20 : r.U16();
                if (NegotiatedVersion != 20) throw new InvalidDataException("gateway did not negotiate protocol 20");
                Welcomes++;
                break;
            case 5: // JoinStatus
                byte state = r.Byte();
                if (state > 2) throw new InvalidDataException("unknown join state");
                Joined |= state == 2;
                r.U16();
                if (r.Remaining > 0) r.Byte();
                break;
            case 10: // EntitySpawn
                ulong netId = r.U64();
                r.U16();
                ulong owner = r.U64();
                r.Container(); r.U32(); r.U16();
                r.Floats(3 + 4 + 3 + 3); // position, quaternion, scale, velocity
                r.Byte(); r.U32();
                r.Blob(); r.Blob();
                string identity = r.Text();
                r.U16(); r.Byte(); r.Byte(); r.U16();
                if (r.Remaining > 0) r.U32(); // optional cohesion group
                if (r.Remaining > 0) r.U16(); // optional cost weight
                if (netId == 0) throw new InvalidDataException("zero spawn id");
                Spawns[netId] = (owner, identity);
                break;
            case 14: // WorldState: tick, worker, count, tightly packed entries
                r.U32(); r.U16();
                int entries = r.U16();
                for (int i = 0; i < entries; i++)
                {
                    ulong entity = r.U64();
                    r.U32(); r.Container();
                    ushort fields = r.U16();
                    var position = r.Axes(fields, 0);
                    if ((fields & 7) == 7) Positions[entity] = (position[0], position[1], position[2]);
                    if ((fields & 56) != 0)
                    {
                        if ((fields & 4096) == 0) r.Axes(fields, 3);
                        else if ((fields & 8192) != 0) r.U32();
                        else if ((fields & 2048) != 0) r.Bytes(8);
                        else r.Floats(4);
                    }
                    r.Axes(fields, 6);
                    if ((fields & 512) != 0) r.Bytes(6);
                    if (entity == 0) throw new InvalidDataException("zero snapshot entity id");
                    StateEntities.Add(entity);
                }
                break;
            case 16: // ContainerOwnership
                if (r.Bool()) Containers.Clear();
                int upserts = r.U16();
                for (int i = 0; i < upserts; i++)
                {
                    r.U16(); string container = r.Text();
                    r.U16(); r.Text(); r.U64(); r.Text();
                    if (r.Bool()) throw new InvalidDataException("instance containers are outside the protocol-20 replay coverage");
                    if (r.Bool()) r.Floats(6);
                    Containers.Add(container);
                }
                int removes = r.U16();
                for (int i = 0; i < removes; i++) Containers.Remove(r.Text());
                break;
            case 18: // Batch: count and individually bounded messages
                int count = r.U16();
                for (int i = 0; i < count; i++) Read(r.Bytes(r.U16()), depth + 1);
                break;
            default:
                UninspectedMessageIds.Add(id);
                break;
        }
        // Dispatch in the protocol-20 client discards the remainder of each bounded message.
        // Additive fields/new messages are safe; required fields and declared batch lengths above
        // must still be complete, and the caller asserts the expected handshake/state semantics.
    }

    private sealed class Cursor : IDisposable
    {
        private readonly MemoryStream stream;
        private readonly BinaryReader reader;
        internal Cursor(byte[] bytes) { stream = new MemoryStream(bytes, writable: false); reader = new BinaryReader(stream); }
        internal long Remaining => stream.Length - stream.Position;
        internal byte Byte() => reader.ReadByte();
        internal ushort U16() => reader.ReadUInt16();
        internal uint U32() => reader.ReadUInt32();
        internal ulong U64() => reader.ReadUInt64();
        internal bool Bool()
        {
            byte value = Byte();
            if (value > 1) throw new InvalidDataException("invalid protocol-20 boolean/flags");
            return value != 0;
        }
        internal byte[] Bytes(int length)
        {
            if (length > Remaining) throw new EndOfStreamException("truncated protocol-20 field");
            return reader.ReadBytes(length);
        }
        internal string Text()
        {
            int length = U16();
            return length == 0 ? "" : new UTF8Encoding(false, true).GetString(Bytes(length - 1));
        }
        internal void Blob() => Bytes(U16());
        internal void Container() { ushort index = U16(); if (index == 65534 || index == 65533) U64(); }
        internal void Floats(int count)
        {
            for (int i = 0; i < count; i++) Float();
        }
        private float Float()
        {
            float value = reader.ReadSingle();
            if (!float.IsFinite(value)) throw new InvalidDataException("non-finite protocol-20 transform");
            return value;
        }
        internal float[] Axes(ushort fields, int shift)
        {
            var values = new float[3];
            for (int i = 0; i < 3; i++)
                if ((fields & (1 << (shift + i))) != 0)
                {
                    values[i] = (fields & 2048) != 0 ? (float)BitConverter.UInt16BitsToHalf(U16()) : Float();
                    if (!float.IsFinite(values[i])) throw new InvalidDataException("non-finite protocol-20 transform");
                }
            return values;
        }
        public void Dispose() { reader.Dispose(); stream.Dispose(); }
    }
}
