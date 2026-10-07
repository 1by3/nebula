using UnityEngine;

namespace Nebula
{
    /// <summary>Why an entity left <see cref="NebulaClient.FarEntities"/>.</summary>
    public enum FarEntityRemoval : byte
    {
        /// <summary>Out of its far radius (or the client's far budget), despawned, or the connection ended.</summary>
        Left = 0,
        /// <summary>
        /// It came within its normal relevance radius and the client now holds it as a replica (same net id, see
        /// <see cref="NebulaClient.EntitySpawned"/>): swap the marker for the real thing.
        /// </summary>
        Replicated = 1,
    }

    /// <summary>
    /// An entity of the far relevance tier as a client sees it (NEB-388, <c>docs/interest-management.md</c> §16): a large
    /// or important entity beyond its normal relevance radius, with no replica, no variables and no RPCs, only enough to
    /// draw a marker or an impostor and to target it. Updated at the entity's far rate (<see cref="UpdateRate"/>, 1 Hz by
    /// default); <see cref="PredictedWorldPosition"/> extrapolates between updates.
    /// <para>
    /// <see cref="Position"/> is absolute in the entity's scope and in double: the gateway sends it that way, so it is good
    /// to well under a millimetre at any distance a scope spans. <see cref="WorldPosition"/> is the same point in this
    /// client's Unity space (its floating origin taken off), which is what a marker is drawn at; it is in the scope's own
    /// space, outside every physics frame, so a ship standing on a turning planet is where the planet carries it.
    /// </para>
    /// </summary>
    public sealed class FarEntity
    {
        public ulong NetId { get; internal set; }
        public uint Epoch { get; internal set; }
        /// <summary>The entity's prefab, so a game can pick the marker or impostor to draw.</summary>
        public ushort PrefabId { get; internal set; }
        public ulong OwnerClientId { get; internal set; }
        public byte InterestGroup { get; internal set; }
        /// <summary>The isolation id of its scope (0: the public world).</summary>
        public ulong InstanceId { get; internal set; }
        /// <summary>Its far radius, metres.</summary>
        public float Radius { get; internal set; }
        /// <summary>Its far updates per second.</summary>
        public float UpdateRate { get; internal set; }
        /// <summary>Absolute position in its scope, metres, double precision.</summary>
        public Double3 Position { get; internal set; }
        /// <summary>Rotation in its scope's own space.</summary>
        public Quaternion Rotation { get; internal set; }
        /// <summary>Velocity in its scope's own space, m/s (half precision).</summary>
        public Vector3 Velocity { get; internal set; }
        /// <summary><see cref="Time.unscaledTimeAsDouble"/> when the newest update arrived.</summary>
        public double ReceivedAt { get; internal set; }

        /// <summary><see cref="Position"/> in this client's Unity space (its floating origin taken off, in double).</summary>
        public Vector3 WorldPosition => ContainerRegistry.ToFrame(Position, InstanceId);

        /// <summary>
        /// <see cref="WorldPosition"/> carried on by <see cref="Velocity"/> to <paramref name="now"/> (default: now), at most
        /// two update intervals ahead, so a marker moves smoothly between one-a-second updates.
        /// </summary>
        public Vector3 PredictedWorldPosition(double now = double.NaN)
        {
            if (double.IsNaN(now)) now = Time.unscaledTimeAsDouble;
            double ahead = now - ReceivedAt;
            double limit = 2.0 / FarRelevance.ClampRate(UpdateRate);
            if (ahead < 0) ahead = 0;
            if (ahead > limit) ahead = limit;
            var p = Position;
            return ContainerRegistry.ToFrame(new Double3(p.X + Velocity.x * ahead, p.Y + Velocity.y * ahead, p.Z + Velocity.z * ahead), InstanceId);
        }

        internal void Apply(in FarEntityEntry e, double now)
        {
            NetId = e.NetId;
            Epoch = e.Epoch;
            PrefabId = e.PrefabId;
            OwnerClientId = e.OwnerClientId;
            InterestGroup = e.InterestGroup;
            InstanceId = e.InstanceId;
            Radius = e.Radius;
            UpdateRate = e.UpdateRate;
            Position = new Double3(e.X, e.Y, e.Z);
            Rotation = e.Rotation;
            Velocity = e.Velocity;
            ReceivedAt = now;
        }

        public override string ToString() => $"far #{NetId} (prefab {PrefabId}) at ({Position.X:0.0}, {Position.Y:0.0}, {Position.Z:0.0})";
    }
}
