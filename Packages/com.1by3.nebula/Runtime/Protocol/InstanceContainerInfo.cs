using System;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    public struct InstancePreparationMsg
    {
        public uint RequestId;
        public ulong EntityId;
        public ContainerRef Destination;
        public ushort SourceWorker;
        public ulong LeaseEpoch;
        public bool Success;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteUInt(RequestId);
            w.WriteULong(EntityId);
            Destination.Write(w);
            w.WriteUShort(SourceWorker);
            w.WriteULong(LeaseEpoch);
            w.WriteBool(Success);
        }

        public static InstancePreparationMsg Read(NetworkReader r) => new InstancePreparationMsg
        {
            RequestId = r.ReadUInt(), EntityId = r.ReadULong(), Destination = ContainerRef.Read(r),
            SourceWorker = r.ReadUShort(), LeaseEpoch = r.ReadULong(), Success = r.ReadBool()
        };
    }

    /// <summary>Identifies a private container's simulation scope, content, and optional outward view.</summary>
    [Serializable]
    public sealed class InstanceContainerInfo
    {
        /// <summary>The 64-bit simulation scope (<see cref="NebulaWorker.InstanceKey"/> of <see cref="ScopeKey"/>). Zero is the public world.</summary>
        public ulong InstanceId;
        public string ContentResource = "";
        public bool ObservePublic;
        public Vector3 ObservationCenter;
        public Vector3 ObservationSize;
        /// <summary>
        /// The opaque scope key the game chose for this instance (<c>TemplateId/key</c>, as passed to
        /// <see cref="NebulaWorker.PrepareInstance"/>), carried so every process can report it in
        /// <see cref="EntityLocation.ScopeKey"/> without inverting the hash. Nebula never parses it. Empty on a
        /// lease row written before the key was recorded.
        /// </summary>
        public string ScopeKey = "";

        public void Write(NetworkWriter writer)
        {
            writer.WriteULong(InstanceId);
            writer.WriteString(ContentResource ?? "");
            writer.WriteBool(ObservePublic);
            writer.WriteVector3(ObservationCenter);
            writer.WriteVector3(ObservationSize);
            writer.WriteString(ScopeKey ?? "");
        }

        public static InstanceContainerInfo Read(NetworkReader reader) => new InstanceContainerInfo
        {
            InstanceId = reader.ReadULong(), ContentResource = reader.ReadString(),
            ObservePublic = reader.ReadBool(), ObservationCenter = reader.ReadVector3(),
            ObservationSize = reader.ReadVector3(),
            // A stored lease row from before the key was recorded ends here (Decode); on the wire it is always present.
            ScopeKey = reader.Remaining > 0 ? reader.ReadString() : ""
        };

        internal static string Encode(InstanceContainerInfo info)
        {
            if (info == null) return "";
            var writer = new NetworkWriter();
            info.Write(writer);
            return Convert.ToBase64String(writer.ToArray());
        }

        internal static InstanceContainerInfo Decode(string value) => string.IsNullOrEmpty(value)
            ? null : Read(new NetworkReader(Convert.FromBase64String(value)));

        internal InstanceContainerInfo Copy() => Decode(Encode(this));
    }
}
