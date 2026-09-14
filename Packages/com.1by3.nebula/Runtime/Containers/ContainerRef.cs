using System;

namespace Nebula
{
    /// <summary>
    /// How a container is named on the wire. A static container (authored into the scene or baked into the world
    /// manifest) is its dense registry <see cref="Index"/>, identical in every process. A dynamic container (one a
    /// <see cref="DynamicContainer"/> entity carries: a ship, a train, a moving station) has no stable index, since it
    /// is created and destroyed at runtime by whichever worker spawns its carrier; it is named by the carrier's
    /// <see cref="NetId"/>, which is already mesh-unique and follows the entity through every handover. A runtime
    /// container (one the game registered while the mesh runs, see <see cref="ContainerRegistry.RegisterRuntime"/>)
    /// is named by the 64-bit id the game gave it. On the wire that is two bytes for a static container and ten for
    /// a dynamic or runtime one.
    /// </summary>
    public readonly struct ContainerRef : IEquatable<ContainerRef>
    {
        /// <summary><see cref="Index"/> of "no container".</summary>
        public const ushort NoneIndex = ushort.MaxValue;
        /// <summary><see cref="Index"/> that says "dynamic: look at <see cref="NetId"/>".</summary>
        public const ushort DynamicIndex = ushort.MaxValue - 1;
        /// <summary><see cref="Index"/> that says "runtime: look at <see cref="RuntimeId"/>".</summary>
        public const ushort RuntimeIndex = ushort.MaxValue - 2;
        /// <summary>Largest serialized size (a dynamic or runtime reference), for sizing fixed batches.</summary>
        public const int MaxWireSize = 2 + 8;

        public readonly ushort Index;
        /// <summary>The 64-bit payload: the carrier's net id for a dynamic reference, the game's id for a runtime one, 0 otherwise.</summary>
        public readonly ulong NetId;

        public ContainerRef(ushort index, ulong netId = 0)
        {
            Index = index;
            NetId = index == DynamicIndex || index == RuntimeIndex ? netId : 0;
        }

        public static readonly ContainerRef None = new ContainerRef(NoneIndex);

        public bool IsNone => Index == NoneIndex;
        public bool IsDynamic => Index == DynamicIndex;
        public bool IsRuntime => Index == RuntimeIndex;
        /// <summary>Names a container of the baked set (a dense index).</summary>
        public bool IsStatic => Index < RuntimeIndex;
        /// <summary>The game's id of the runtime container this names (0 for any other reference).</summary>
        public ulong RuntimeId => IsRuntime ? NetId : 0;
        /// <summary>
        /// The container may be unknown on this process for a while and show up later: a dynamic container arrives
        /// with its carrier, a runtime container with its lease. Messages naming one that has not arrived wait for
        /// it instead of being dropped.
        /// </summary>
        public bool MayArriveLater => Index == DynamicIndex || Index == RuntimeIndex;

        /// <summary>The reference for <paramref name="container"/> (<see cref="None"/> for null).</summary>
        public static ContainerRef Of(Container container)
        {
            if (container == null) return None;
            if (container.IsDynamic) return new ContainerRef(DynamicIndex, container.CarrierNetId);
            if (container.IsRuntime) return new ContainerRef(RuntimeIndex, container.RuntimeId);
            return new ContainerRef(container.Index);
        }

        /// <summary>A reference to the dynamic container carried by entity <paramref name="carrierNetId"/>.</summary>
        public static ContainerRef Dynamic(ulong carrierNetId) => new ContainerRef(DynamicIndex, carrierNetId);

        /// <summary>A reference to the runtime container the game registered as <paramref name="runtimeId"/>.</summary>
        public static ContainerRef Runtime(ulong runtimeId) => new ContainerRef(RuntimeIndex, runtimeId);

        /// <summary>The container this names in this process, or null when it is unknown here (see <see cref="ContainerRegistry.Resolve(ContainerRef)"/>).</summary>
        public Container Resolve() => ContainerRegistry.Resolve(this);

        public void Write(NetworkWriter w)
        {
            w.WriteUShort(Index);
            if (MayArriveLater) w.WriteULong(NetId);
        }

        public static ContainerRef Read(NetworkReader r)
        {
            ushort index = r.ReadUShort();
            return index == DynamicIndex || index == RuntimeIndex ? new ContainerRef(index, r.ReadULong()) : new ContainerRef(index);
        }

        public bool Equals(ContainerRef other) => Index == other.Index && NetId == other.NetId;
        public override bool Equals(object obj) => obj is ContainerRef other && Equals(other);
        public override int GetHashCode() => (Index * 397) ^ NetId.GetHashCode();
        public static bool operator ==(ContainerRef a, ContainerRef b) => a.Equals(b);
        public static bool operator !=(ContainerRef a, ContainerRef b) => !a.Equals(b);
        public override string ToString() => IsNone ? "none" : IsDynamic ? $"dynamic#{NetId}" : IsRuntime ? ContainerRegistry.RuntimeContainerId(NetId) : Index.ToString();
    }
}
