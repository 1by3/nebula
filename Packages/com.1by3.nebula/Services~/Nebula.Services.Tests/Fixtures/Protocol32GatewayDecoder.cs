// Frozen protocol-32 oracle: do not replace literals or readers with production protocol types.
// Protocol 32 (far classes) added optional class, slot, age and hand-over fields to a far entry, behind flag bits 4, 8, 16 and 32,
// and a message, FarPoses (53), of compact updates. This reader reads all of them. Protocol 33 (client requests) changed no frame
// layout a client of 32 reads: it added messages 54 to 56, which a gateway never sends to a client of 32 and which land in
// UninspectedMessageIds here, where the compatibility test asserts they are absent. Otherwise it is the protocol-31 oracle, below.
// Protocol 31 (NEB-484) added a trailing dropped-tick range to OwnerState, after its events section; this reader reads
// OwnerState in full, so a field of 33 after it fails here instead of being skipped. Otherwise it is the protocol-30 oracle, literals raised.
// Protocol 30 added a byte of transform extensions after each state entry's field mask: angular velocity (3 halves after
// the velocity) and 15-bit smallest-three rotations (6 bytes instead of 4); this reader reads both. Otherwise it is the
// protocol-29 oracle, below, with its version literals raised.
// Protocol 29 changed no frame layout this replay reads (it added a trailing events section to OwnerState, which a
// handshake and snapshot stream does not carry), so this is the protocol-28 oracle with its version literals raised.
// Protocols 21 to 28 changed no frame layout this replay reads: 21 added a message (EntityMaps, 38), which lands in
// UninspectedMessageIds, and a trailing maps field in the spawn body, which the spawn reader leaves unread; 22 and 23 added
// messages and fields the handshake and snapshot stream do not carry; 24 put priority bits in the spawn's interest flags,
// a byte this reader skips; 25 added a message a client sends (DriveInput) and a trailing driver field in the spawn body,
// left unread; 26 added FarEntities, a gateway message this reader skips; 27 added a trailing section of ContainerOwnership
// this reader stops before; 28 added three messages (Portals, PortalLinks and PortalWarm) a handshake and snapshot stream does
// not carry, and a trailing link id on the instance preparation messages. So this is the protocol-20 oracle with its version
// literals raised.
// Covers the public-container handshake, spawn and transform stream exercised by the replay.
// Instance containers, game-defined variable/sync payload contents, RPCs and other message IDs
// are not compatibility claims made by this fixture. Like the protocol-28 client, unknown
// message IDs and optional trailing bytes are ignored inside their bounded frame. Known but
// uncovered instance-container payloads fail explicitly instead of guessing their framing.
internal sealed class Protocol32GatewayDecoder
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
    /// <summary>Far entries as a client of 32 reads them, by entity: the newest of each.</summary>
    internal readonly Dictionary<ulong, (byte Flags, double X, double Y, double Z)> FarEntries = new();
    /// <summary>The dropped-tick range each owner state carried, when it carried one (NEB-484).</summary>
    internal readonly Dictionary<ulong, (uint From, uint To)> DroppedRanges = new();
    /// <summary>The slot each far entity was given (protocol 32).</summary>
    internal readonly Dictionary<ulong, ushort> FarSlots = new();
    /// <summary>Compact far poses read.</summary>
    internal int FarPosesRead;

    internal void Read(byte[] frame) => Read(frame, 0);

    private void Read(byte[] frame, int depth)
    {
        if (depth > 8) throw new InvalidDataException("protocol-32 batch nesting limit");
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
                NegotiatedVersion = r.Remaining == 0 ? (ushort)32 : r.U16();
                if (NegotiatedVersion != 32) throw new InvalidDataException("gateway did not negotiate protocol 32");
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
                    byte ext = r.Byte();
                    if (ext > 3) throw new InvalidDataException("unknown protocol-32 transform extension");
                    var position = r.Axes(fields, 0);
                    if ((fields & 7) == 7) Positions[entity] = (position[0], position[1], position[2]);
                    if ((fields & 56) != 0)
                    {
                        if ((fields & 4096) == 0) r.Axes(fields, 3);
                        else if ((fields & 8192) != 0) r.Bytes((ext & 2) != 0 ? 6 : 4);
                        else if ((fields & 2048) != 0) r.Bytes(8);
                        else r.Floats(4);
                    }
                    r.Axes(fields, 6);
                    if ((fields & 512) != 0) r.Bytes(6);
                    if ((ext & 1) != 0) r.Bytes(6);
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
                    if (r.Bool()) throw new InvalidDataException("instance containers are outside the protocol-25 replay coverage");
                    if (r.Bool()) r.Floats(6);
                    Containers.Add(container);
                }
                int removes = r.U16();
                for (int i = 0; i < removes; i++) Containers.Remove(r.Text());
                break;
            case 15: // OwnerState: NEB-484's range follows the events section, which it forces to be written
                ulong ownerState = r.U64();
                r.U32(); r.U32(); r.U32(); r.Byte(); r.U64();
                r.Container();
                r.Blob();
                if (r.Remaining > 0) r.Blob();
                if (r.Remaining >= 8) DroppedRanges[ownerState] = (r.U32(), r.U32());
                if (r.Remaining != 0) throw new InvalidDataException("bytes a protocol-32 client cannot read after an owner state");
                break;
            case 26: // FarEntities: a count and tightly packed entries
                int farEntries = r.U16();
                for (int i = 0; i < farEntries; i++)
                {
                    ulong far = r.U64();
                    r.U32();
                    byte kind = r.Byte();
                    if (kind == 1) { FarEntries.Remove(far); continue; }
                    if (kind != 0) throw new InvalidDataException("unknown protocol-32 far entry kind");
                    byte flags = r.Byte();
                    if ((flags & ~63) != 0) throw new InvalidDataException("far entry flag bits a protocol-32 client does not read: " + flags);
                    r.U16(); r.Byte(); r.U32(); r.U32();
                    if ((flags & 1) != 0) r.U64();
                    if ((flags & 2) != 0) r.U64();
                    if ((flags & 4) != 0) r.Byte(); // class
                    if ((flags & 8) != 0) FarSlots[far] = r.U16();
                    if ((flags & 16) != 0) r.U16(); // age, milliseconds
                    FarEntries[far] = (flags, r.F64(), r.F64(), r.F64());
                    r.Bytes(4 + 6); // the rotation, smallest-three, and three half-float velocities
                }
                if (r.Remaining != 0) throw new InvalidDataException("bytes a protocol-32 client cannot read after its far entries");
                break;
            case 53: // FarPoses: a count and compact entries, 19 bytes upright and 21 otherwise
                int poses = r.U16();
                for (int i = 0; i < poses; i++)
                {
                    ushort slot = r.U16();
                    r.Bytes(9);
                    r.Bytes((slot & 0x8000) != 0 ? 2 : 4);
                    r.Bytes(6);
                    FarPosesRead++;
                }
                if (r.Remaining != 0) throw new InvalidDataException("bytes a protocol-32 client cannot read after its far poses");
                break;
            case 18: // Batch: count and individually bounded messages
                int count = r.U16();
                for (int i = 0; i < count; i++) Read(r.Bytes(r.U16()), depth + 1);
                break;
            default:
                UninspectedMessageIds.Add(id);
                break;
        }
        // Dispatch in the protocol-25 client discards the remainder of each bounded message.
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
        internal double F64() => reader.ReadDouble();
        internal bool Bool()
        {
            byte value = Byte();
            if (value > 1) throw new InvalidDataException("invalid protocol-25 boolean/flags");
            return value != 0;
        }
        internal byte[] Bytes(int length)
        {
            if (length > Remaining) throw new EndOfStreamException("truncated protocol-25 field");
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
            if (!float.IsFinite(value)) throw new InvalidDataException("non-finite protocol-25 transform");
            return value;
        }
        internal float[] Axes(ushort fields, int shift)
        {
            var values = new float[3];
            for (int i = 0; i < 3; i++)
                if ((fields & (1 << (shift + i))) != 0)
                {
                    values[i] = (fields & 2048) != 0 ? (float)BitConverter.UInt16BitsToHalf(U16()) : Float();
                    if (!float.IsFinite(values[i])) throw new InvalidDataException("non-finite protocol-25 transform");
                }
            return values;
        }
        public void Dispose() { reader.Dispose(); stream.Dispose(); }
    }
}
