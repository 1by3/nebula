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
    }

    [Flags]
    public enum FarEntryFlags : byte
    {
        None = 0,
        /// <summary>An <see cref="FarEntityEntry.OwnerClientId"/> follows.</summary>
        HasOwner = 1,
        /// <summary>An <see cref="FarEntityEntry.InstanceId"/> follows (the entity is in a scope, not the public world).</summary>
        HasInstance = 2,
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

        /// <summary>Bytes a <see cref="FarEntryKind.State"/> entry with neither owner nor scope costs on the wire.</summary>
        public const int StateWireSize = 8 + 4 + 1 + 1 + 2 + 1 + 4 + 4 + 24 + 4 + 6;

        public static FarEntityEntry GoneOf(ulong netId, uint epoch) => new FarEntityEntry { NetId = netId, Epoch = epoch, Kind = FarEntryKind.Gone };

        public void Write(NetworkWriter w)
        {
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteByte((byte)Kind);
            if (Kind == FarEntryKind.Gone) return;
            var flags = FarEntryFlags.None;
            if (OwnerClientId != 0) flags |= FarEntryFlags.HasOwner;
            if (InstanceId != 0) flags |= FarEntryFlags.HasInstance;
            w.WriteByte((byte)flags);
            w.WriteUShort(PrefabId);
            w.WriteByte(InterestGroup);
            w.WriteFloat(Radius);
            w.WriteFloat(UpdateRate);
            if (OwnerClientId != 0) w.WriteULong(OwnerClientId);
            if (InstanceId != 0) w.WriteULong(InstanceId);
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

        public static void Write(NetworkWriter w, IReadOnlyList<FarEntityEntry> entries, int from, int count)
        {
            w.WriteByte((byte)MsgId.FarEntities);
            w.WriteUShort((ushort)count);
            for (int i = 0; i < count; i++) entries[from + i].Write(w);
        }

        /// <summary>Read the entries after the message id into <paramref name="into"/> (cleared first).</summary>
        public static void Read(NetworkReader r, List<FarEntityEntry> into)
        {
            into.Clear();
            int count = r.ReadUShort();
            for (int i = 0; i < count; i++) into.Add(FarEntityEntry.Read(r));
        }
    }
}
