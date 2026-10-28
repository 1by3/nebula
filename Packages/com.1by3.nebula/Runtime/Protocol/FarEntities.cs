using System;
using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>What one <see cref="FarEntityEntry"/> says about its entity.</summary>
    public enum FarEntryKind : byte
    {
        /// <summary>Where the entity is now, and enough about it to draw a marker: its prefab, owner and reach.</summary>
        State = 0,
        /// <summary>The entity left the receiver's far tier: out of reach, despawned, or no longer far relevant.</summary>
        Gone = 1,
        /// <summary>
        /// Gateway only, never in a <see cref="FarEntitiesMsg"/>: a new pose for an entity the client already holds, queued
        /// to go as a compact <see cref="FarPosesMsg"/> entry (protocol 31, D111). <see cref="FarEntityEntry.X"/>..Z hold
        /// the offset from the anchor the client was last sent, and <see cref="FarEntityEntry.Slot"/> names the entity.
        /// </summary>
        Pose = 2,
    }

    [Flags]
    public enum FarEntryFlags : byte
    {
        None = 0,
        /// <summary>An <see cref="FarEntityEntry.OwnerClientId"/> follows.</summary>
        HasOwner = 1,
        /// <summary>An <see cref="FarEntityEntry.InstanceId"/> follows (the entity is in a scope, not the public world).</summary>
        HasInstance = 2,
        /// <summary>Protocol 31: a <see cref="FarEntityEntry.FarClass"/> byte follows (not 0).</summary>
        HasClass = 4,
        /// <summary>Protocol 31: a <see cref="FarEntityEntry.Slot"/> follows: the short name the client's compact poses use (gateway to client).</summary>
        HasSlot = 8,
        /// <summary>Protocol 31: the pose is <see cref="FarEntityEntry.Age"/> old, in milliseconds (u16): a gateway sending a stored pose says how stale it is.</summary>
        HasAge = 16,
        /// <summary>
        /// Protocol 31: the entity is leaving this client's replica set this instant, and this entry is its far marker
        /// (<see cref="FarEntityEntry.FromReplica"/>, D112). Sent just before the replica's despawn.
        /// </summary>
        FromReplica = 32,
    }

    /// <summary>
    /// One entity of the far relevance tier (<c>docs/interest-management.md</c> §16, NEB-388): a large or important
    /// entity seen beyond the normal interest radius as a pose and nothing else. No variables, no RPCs, no sync state,
    /// no container: the position is <b>absolute in the entity's scope, in double</b> (the space region keys and the
    /// gateway's own positions are in, converted out of every physics frame the entity stands in), so a receiver needs
    /// no container row and no frame to place it, and a coordinate 10<sup>6</sup> m out keeps sub-millimetre resolution
    /// on the wire. Rotation is in the same space (smallest-three, 4 bytes, about 0.1 degree), velocity in half floats (m/s).
    /// </summary>
    public struct FarEntityEntry
    {
        public ulong NetId;
        public uint Epoch;
        public FarEntryKind Kind;
        public ushort PrefabId;
        public byte InterestGroup;
        /// <summary>The far radius the entity declared (<see cref="NetworkIdentity.FarRelevanceRadius"/>), metres.</summary>
        public float Radius;
        /// <summary>The far updates per second it declared (<see cref="NetworkIdentity.FarUpdateRate"/>).</summary>
        public float UpdateRate;
        public ulong OwnerClientId;
        /// <summary>The isolation id of the entity's scope; 0 for the public world.</summary>
        public ulong InstanceId;
        /// <summary>Absolute position in the entity's scope, metres, in double.</summary>
        public double X, Y, Z;
        public Quaternion Rotation;
        public Vector3 Velocity;
        /// <summary>The entity's far class (<see cref="NetworkIdentity.FarClass"/>; 0 the default). Protocol 31.</summary>
        public byte FarClass;
        /// <summary>Gateway to client, protocol 31: the client's short name for the entity, which <see cref="FarPosesMsg"/> entries use.</summary>
        public ushort Slot;
        /// <summary>Seconds since the pose was taken, when a gateway sends one it stored (0: fresh). Protocol 31, millisecond resolution.</summary>
        public float Age;
        /// <summary>The replica the client holds is leaving its set, and this is its far marker (protocol 31, D112).</summary>
        public bool FromReplica;
        /// <summary><see cref="FarEntryKind.Pose"/> only: write the facing alone (an upright class, <see cref="Nebula.FarClass.Upright"/>).</summary>
        public bool Upright;

        /// <summary>The first protocol whose far entries carry a class, a slot, an age and the replica hand-over flag.</summary>
        public const ushort ClassesVersion = 31;

        /// <summary>Bytes a <see cref="FarEntryKind.State"/> entry with neither owner nor scope costs on the wire.</summary>
        public const int StateWireSize = 8 + 4 + 1 + 1 + 2 + 1 + 4 + 4 + 24 + 4 + 6;

        public static FarEntityEntry GoneOf(ulong netId, uint epoch) => new FarEntityEntry { NetId = netId, Epoch = epoch, Kind = FarEntryKind.Gone };

        /// <summary>Write at this build's protocol.</summary>
        public void Write(NetworkWriter w) => Write(w, HelloMsg.ProtocolVersion);

        /// <summary>
        /// Write for a receiver of <paramref name="version"/>: below <see cref="ClassesVersion"/> the class, slot, age and
        /// hand-over flag are left out, which is exactly the protocol-26 entry.
        /// </summary>
        public void Write(NetworkWriter w, ushort version)
        {
            if (Kind == FarEntryKind.Pose) throw new InvalidOperationException("a far pose goes in a FarPosesMsg");
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteByte((byte)Kind);
            if (Kind == FarEntryKind.Gone) return;
            var flags = FarEntryFlags.None;
            if (OwnerClientId != 0) flags |= FarEntryFlags.HasOwner;
            if (InstanceId != 0) flags |= FarEntryFlags.HasInstance;
            int ageMs = 0;
            if (version >= ClassesVersion)
            {
                if (FarClass != 0) flags |= FarEntryFlags.HasClass;
                if (Slot != 0) flags |= FarEntryFlags.HasSlot;
                ageMs = Age > 0f ? (int)Math.Min(ushort.MaxValue, Math.Round(Age * 1000.0)) : 0;
                if (ageMs > 0) flags |= FarEntryFlags.HasAge;
                if (FromReplica) flags |= FarEntryFlags.FromReplica;
            }
            w.WriteByte((byte)flags);
            w.WriteUShort(PrefabId);
            w.WriteByte(InterestGroup);
            w.WriteFloat(Radius);
            w.WriteFloat(UpdateRate);
            if (OwnerClientId != 0) w.WriteULong(OwnerClientId);
            if (InstanceId != 0) w.WriteULong(InstanceId);
            if ((flags & FarEntryFlags.HasClass) != 0) w.WriteByte(FarClass);
            if ((flags & FarEntryFlags.HasSlot) != 0) w.WriteUShort(Slot);
            if ((flags & FarEntryFlags.HasAge) != 0) w.WriteUShort((ushort)ageMs);
            w.WriteDouble(X);
            w.WriteDouble(Y);
            w.WriteDouble(Z);
            w.WriteCompressedQuaternion(Rotation);
            w.WriteHalf(Velocity.x);
            w.WriteHalf(Velocity.y);
            w.WriteHalf(Velocity.z);
        }

        public static FarEntityEntry Read(NetworkReader r)
        {
            var e = new FarEntityEntry { NetId = r.ReadULong(), Epoch = r.ReadUInt(), Kind = (FarEntryKind)r.ReadByte() };
            if (e.Kind == FarEntryKind.Gone) return e;
            if (e.Kind != FarEntryKind.State) throw new System.IO.InvalidDataException($"unknown far entry kind {(byte)e.Kind}");
            var flags = (FarEntryFlags)r.ReadByte();
            e.PrefabId = r.ReadUShort();
            e.InterestGroup = r.ReadByte();
            e.Radius = r.ReadFloat();
            e.UpdateRate = r.ReadFloat();
            if ((flags & FarEntryFlags.HasOwner) != 0) e.OwnerClientId = r.ReadULong();
            if ((flags & FarEntryFlags.HasInstance) != 0) e.InstanceId = r.ReadULong();
            if ((flags & FarEntryFlags.HasClass) != 0) e.FarClass = r.ReadByte();
            if ((flags & FarEntryFlags.HasSlot) != 0) e.Slot = r.ReadUShort();
            if ((flags & FarEntryFlags.HasAge) != 0) e.Age = r.ReadUShort() / 1000f;
            e.FromReplica = (flags & FarEntryFlags.FromReplica) != 0;
            e.X = r.ReadDouble();
            e.Y = r.ReadDouble();
            e.Z = r.ReadDouble();
            e.Rotation = r.ReadCompressedQuaternion();
            float vx = r.ReadHalf(), vy = r.ReadHalf(), vz = r.ReadHalf();
            e.Velocity = new Vector3(vx, vy, vz);
            return e;
        }

        /// <summary>Squared distance from an absolute point of the same scope, in double.</summary>
        public double SqrDistanceTo(double x, double y, double z)
        {
            double dx = X - x, dy = Y - y, dz = Z - z;
            return dx * dx + dy * dy + dz * dz;
        }
    }

    /// <summary>
    /// Worker -> gateway, and gateway -> client: a batch of far-tier entries (<see cref="MsgId.FarEntities"/>, protocol
    /// 26). <c>[id][count:ushort]{entry}</c>. A worker sends it at each entity's far rate to the gateways whose foci are
    /// within the entity's far radius; a gateway relays to each client the entries of the entities in that client's far
    /// tier, and only to clients that negotiated protocol 26 or later.
    /// </summary>
    public static class FarEntitiesMsg
    {
        /// <summary>Most entries in one message; a longer list is split.</summary>
        public const int MaxEntries = 64;

        public static void Write(NetworkWriter w, IReadOnlyList<FarEntityEntry> entries, int from, int count) =>
            Write(w, entries, from, count, HelloMsg.ProtocolVersion);

        /// <summary>Write for a receiver of <paramref name="version"/> (see <see cref="FarEntityEntry.Write(NetworkWriter, ushort)"/>).</summary>
        public static void Write(NetworkWriter w, IReadOnlyList<FarEntityEntry> entries, int from, int count, ushort version)
        {
            w.WriteByte((byte)MsgId.FarEntities);
            w.WriteUShort((ushort)count);
            for (int i = 0; i < count; i++) entries[from + i].Write(w, version);
        }

        /// <summary>Read the entries after the message id into <paramref name="into"/> (cleared first).</summary>
        public static void Read(NetworkReader r, List<FarEntityEntry> into)
        {
            into.Clear();
            int count = r.ReadUShort();
            for (int i = 0; i < count; i++) into.Add(FarEntityEntry.Read(r));
        }
    }

    /// <summary>One compact far update as read from a <see cref="FarPosesMsg"/>: a held entity's new pose, by slot.</summary>
    public struct FarPose
    {
        /// <summary>The slot the entity's last full entry named (<see cref="FarEntityEntry.Slot"/>).</summary>
        public ushort Slot;
        /// <summary>The pose carries only the way the entity faces (<see cref="Forward"/>); keep its up axis.</summary>
        public bool Upright;
        /// <summary>Offset from the position of the entity's last full entry, metres (1 cm steps).</summary>
        public double DX, DY, DZ;
        /// <summary>The full rotation (not <see cref="Upright"/>).</summary>
        public Quaternion Rotation;
        /// <summary>The way it faces, a unit vector in its scope's space (about a degree when <see cref="Upright"/>).</summary>
        public Vector3 Forward;
        public Vector3 Velocity;
    }

    /// <summary>
    /// Gateway -> client, protocol 31 (<c>docs/interest-management.md</c> D111): compact far updates for entities the
    /// client already holds in its far tier.
    /// <c>[id][count:u16]{u16 slot (bit 15: upright), 3 x s24 offset (1 cm, from the last full entry's position),
    /// rotation (u16 octahedral facing when upright, else u32 smallest-three), 3 x f16 velocity}</c>: 19 bytes for an
    /// upright body, 21 for anything else, against 59 to 78 for a full <see cref="FarEntityEntry"/>. Everything else about
    /// the entity (prefab, owner, class, radius, rate, scope, epoch) is what its last full entry said; the gateway sends a
    /// full entry again whenever any of that changes or the offset no longer fits (about 84 km on an axis).
    /// </summary>
    public static class FarPosesMsg
    {
        /// <summary>Most entries in one message; a longer list is split.</summary>
        public const int MaxEntries = 128;
        /// <summary>Metres one step of the offset stands for.</summary>
        public const double Resolution = 0.01;
        /// <summary>The largest offset on any axis a compact entry can carry, metres.</summary>
        public const double MaxOffset = 8388607 * Resolution;
        /// <summary>The highest slot (bit 15 of the slot carries the upright flag).</summary>
        public const ushort MaxSlot = 0x7FFF;
        private const ushort UprightBit = 0x8000;

        /// <summary>Bytes one entry costs.</summary>
        public static int EntrySize(bool upright) => 2 + 9 + (upright ? 2 : 4) + 6;

        /// <summary>Whether an offset fits a compact entry.</summary>
        public static bool Fits(double dx, double dy, double dz) =>
            Math.Abs(dx) <= MaxOffset && Math.Abs(dy) <= MaxOffset && Math.Abs(dz) <= MaxOffset;

        /// <summary>Write <paramref name="count"/> queued <see cref="FarEntryKind.Pose"/> entries from <paramref name="from"/>.</summary>
        public static void Write(NetworkWriter w, IReadOnlyList<FarEntityEntry> entries, int from, int count)
        {
            w.WriteByte((byte)MsgId.FarPoses);
            w.WriteUShort((ushort)count);
            for (int i = 0; i < count; i++)
            {
                var e = entries[from + i];
                if (e.Kind != FarEntryKind.Pose) throw new InvalidOperationException("only far poses go in a FarPosesMsg");
                WriteEntry(w, e.Slot, e.Upright, e.X, e.Y, e.Z, e.Rotation, e.Velocity);
            }
        }

        /// <summary>One entry: <paramref name="dx"/>..<paramref name="dz"/> are the offset from the client's anchor, metres.</summary>
        public static void WriteEntry(NetworkWriter w, ushort slot, bool upright, double dx, double dy, double dz, Quaternion rotation, Vector3 velocity)
        {
            w.WriteUShort((ushort)((slot & MaxSlot) | (upright ? UprightBit : 0)));
            WriteS24(w, dx);
            WriteS24(w, dy);
            WriteS24(w, dz);
            if (upright) w.WriteUShort(EncodeFacing(rotation * new Vector3(0f, 0f, 1f)));
            else w.WriteCompressedQuaternion(rotation);
            w.WriteHalf(velocity.x);
            w.WriteHalf(velocity.y);
            w.WriteHalf(velocity.z);
        }

        /// <summary>Read the entries after the message id into <paramref name="into"/> (cleared first).</summary>
        public static void Read(NetworkReader r, List<FarPose> into)
        {
            into.Clear();
            int count = r.ReadUShort();
            for (int i = 0; i < count; i++)
            {
                ushort raw = r.ReadUShort();
                var p = new FarPose { Slot = (ushort)(raw & MaxSlot), Upright = (raw & UprightBit) != 0 };
                p.DX = ReadS24(r);
                p.DY = ReadS24(r);
                p.DZ = ReadS24(r);
                if (p.Upright)
                {
                    p.Forward = DecodeFacing(r.ReadUShort());
                    p.Rotation = Quaternion.identity;
                }
                else
                {
                    p.Rotation = r.ReadCompressedQuaternion();
                    p.Forward = p.Rotation * new Vector3(0f, 0f, 1f);
                }
                float vx = r.ReadHalf(), vy = r.ReadHalf(), vz = r.ReadHalf();
                p.Velocity = new Vector3(vx, vy, vz);
                into.Add(p);
            }
        }

        private static void WriteS24(NetworkWriter w, double metres)
        {
            double steps = Math.Round(metres / Resolution);
            int v = (int)Math.Max(-8388608.0, Math.Min(8388607.0, steps));
            w.WriteByte((byte)v);
            w.WriteByte((byte)(v >> 8));
            w.WriteByte((byte)(v >> 16));
        }

        private static double ReadS24(NetworkReader r)
        {
            int v = r.ReadByte() | (r.ReadByte() << 8) | (r.ReadByte() << 16);
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            return v * Resolution;
        }

        /// <summary>A direction as 16 bits, octahedral (8 bits an axis, about a degree). A zero or non-finite one reads back as forward.</summary>
        public static ushort EncodeFacing(Vector3 d)
        {
            float l = Math.Abs(d.x) + Math.Abs(d.y) + Math.Abs(d.z);
            if (!(l > 1e-6f) || float.IsInfinity(l)) d = new Vector3(0f, 0f, 1f);
            else d = new Vector3(d.x / l, d.y / l, d.z / l);
            float x = d.x, y = d.y;
            if (d.z < 0f)
            {
                float ox = (1f - Math.Abs(y)) * (x >= 0f ? 1f : -1f);
                float oy = (1f - Math.Abs(x)) * (y >= 0f ? 1f : -1f);
                x = ox; y = oy;
            }
            // 254 steps, so 0 (an axis-aligned facing) is a code of its own (127).
            int qx = (int)Math.Round((x * 0.5f + 0.5f) * 254f);
            int qy = (int)Math.Round((y * 0.5f + 0.5f) * 254f);
            return (ushort)((Math.Max(0, Math.Min(254, qx)) << 8) | Math.Max(0, Math.Min(254, qy)));
        }

        /// <summary>The unit direction <see cref="EncodeFacing"/> wrote.</summary>
        public static Vector3 DecodeFacing(ushort bits)
        {
            float x = Math.Min(254, bits >> 8) / 254f * 2f - 1f;
            float y = Math.Min(254, bits & 0xFF) / 254f * 2f - 1f;
            float z = 1f - Math.Abs(x) - Math.Abs(y);
            if (z < 0f)
            {
                float ox = (1f - Math.Abs(y)) * (x >= 0f ? 1f : -1f);
                float oy = (1f - Math.Abs(x)) * (y >= 0f ? 1f : -1f);
                x = ox; y = oy;
            }
            float len = (float)Math.Sqrt(x * x + y * y + z * z);
            return len > 1e-6f ? new Vector3(x / len, y / len, z / len) : new Vector3(0f, 0f, 1f);
        }

#if !NEBULA_SERVICE
        /// <summary>
        /// The rotation of an upright body facing <paramref name="forward"/>, keeping <paramref name="up"/> (the up axis of
        /// its last full rotation). Falls back to <paramref name="fallback"/> when the facing is along the up axis.
        /// </summary>
        public static Quaternion UprightRotation(Vector3 forward, Vector3 up, Quaternion fallback)
        {
            var flat = forward - up * Vector3.Dot(forward, up);
            if (flat.sqrMagnitude < 1e-6f) return fallback;
            return Quaternion.LookRotation(flat, up);
        }
#endif
    }
}
