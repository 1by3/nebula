using System;

namespace Nebula
{
    /// <summary>
    /// How a container is named on the wire. A static container (authored into the scene or baked into the world
    /// manifest) is its dense registry <see cref="Index"/>, identical in every process. A dynamic container (one a
    /// <see cref="DynamicContainer"/> entity carries: a ship, a train, a moving station) has no stable index, since it
    /// is created and destroyed at runtime by whichever worker spawns its carrier; it is named by the carrier's
    /// <see cref="NetId"/>, which is already mesh-unique and follows the entity through every handover. On the wire
    /// that is two bytes for a static container and ten for a dynamic one.
    /// </summary>
    public readonly struct ContainerRef : IEquatable<ContainerRef>
    {
        /// <summary><see cref="Index"/> of "no container".</summary>
        public const ushort NoneIndex = ushort.MaxValue;
        /// <summary><see cref="Index"/> that says "dynamic: look at <see cref="NetId"/>".</summary>
        public const ushort DynamicIndex = ushort.MaxValue - 1;
        /// <summary>Largest serialized size (a dynamic reference), for sizing fixed batches.</summary>
        public const int MaxWireSize = 2 + 8;

        public readonly ushort Index;
        public readonly ulong NetId;

        public ContainerRef(ushort index, ulong netId = 0)
        {
            Index = index;
            NetId = index == DynamicIndex ? netId : 0;
        }

        public static readonly ContainerRef None = new ContainerRef(NoneIndex);

        public bool IsNone => Index == NoneIndex;
        public bool IsDynamic => Index == DynamicIndex;

        /// <summary>The reference for <paramref name="container"/> (<see cref="None"/> for null).</summary>
        public static ContainerRef Of(Container container)
        {
            if (container == null) return None;
            return container.IsDynamic ? new ContainerRef(DynamicIndex, container.CarrierNetId) : new ContainerRef(container.Index);
        }

        /// <summary>A reference to the dynamic container carried by entity <paramref name="carrierNetId"/>.</summary>
        public static ContainerRef Dynamic(ulong carrierNetId) => new ContainerRef(DynamicIndex, carrierNetId);

        /// <summary>The container this names in this process, or null when it is unknown here (see <see cref="ContainerRegistry.Resolve(ContainerRef)"/>).</summary>
        public Container Resolve() => ContainerRegistry.Resolve(this);

        public void Write(NetworkWriter w)
        {
            w.WriteUShort(Index);
            if (IsDynamic) w.WriteULong(NetId);
        }

        public static ContainerRef Read(NetworkReader r)
        {
            ushort index = r.ReadUShort();
            return index == DynamicIndex ? new ContainerRef(index, r.ReadULong()) : new ContainerRef(index);
        }

        public bool Equals(ContainerRef other) => Index == other.Index && NetId == other.NetId;
        public override bool Equals(object obj) => obj is ContainerRef other && Equals(other);
        public override int GetHashCode() => (Index * 397) ^ NetId.GetHashCode();
        public static bool operator ==(ContainerRef a, ContainerRef b) => a.Equals(b);
        public static bool operator !=(ContainerRef a, ContainerRef b) => !a.Equals(b);
        public override string ToString() => IsNone ? "none" : IsDynamic ? $"dynamic#{NetId}" : Index.ToString();
    }
}
