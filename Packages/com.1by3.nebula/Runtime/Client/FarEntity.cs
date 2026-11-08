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
        /// <summary>
        /// <see cref="Time.unscaledTimeAsDouble"/> when the newest pose was taken, as near as this client can tell: when it
        /// arrived, less the age the gateway reported for a pose it had stored (protocol 32).
        /// </summary>
        public double ReceivedAt { get; internal set; }
        /// <summary>Its far class (<see cref="NetworkIdentity.FarClass"/>, D110): pick what to draw by it. 0 from a gateway older than protocol 32.</summary>
        public byte FarClass { get; internal set; }
        /// <summary>The way it faces, a unit vector in its scope's own space (<see cref="Rotation"/> times forward).</summary>
        public Vector3 Forward { get; internal set; }
        /// <summary>
        /// It came from a replica this client held (D112): the replica left the set and this marker took over in the same
        /// message, so a game that draws both never shows neither. <see cref="PredictedWorldPosition"/> eases from where the
        /// replica was drawn to the far pose over <see cref="HandoverBlendSeconds"/>.
        /// </summary>
        public bool FromReplica { get; internal set; }
        /// <summary>Where the replica was drawn (this client's Unity space) when this marker took over from it.</summary>
        public Vector3 HandoverFrom { get; internal set; }
        /// <summary><see cref="Time.unscaledTimeAsDouble"/> when this marker took over from a replica (negative infinity when it did not).</summary>
        public double HandoverAt { get; internal set; } = double.NegativeInfinity;

        /// <summary>Seconds over which a marker that took over from a replica eases from the replica's last drawn spot onto its far pose.</summary>
        public const float HandoverBlendSeconds = 0.3f;

        /// <summary>The slot compact poses name it by (protocol 32), and the position they are offsets from.</summary>
        internal ushort Slot;
        internal Double3 Anchor;

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
            var target = ContainerRegistry.ToFrame(new Double3(p.X + Velocity.x * ahead, p.Y + Velocity.y * ahead, p.Z + Velocity.z * ahead), InstanceId);
            double since = now - HandoverAt;
            if (since >= 0 && since < HandoverBlendSeconds)
            {
                // Carry the replica's last drawn spot on by the same velocity, and ease from it onto the far pose.
                var from = HandoverFrom + Velocity * (float)since;
                float t = (float)(since / HandoverBlendSeconds);
                t = t * t * (3f - 2f * t);
                return Vector3.LerpUnclamped(from, target, t);
            }
            return target;
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
            Forward = e.Rotation * Vector3.forward;
            Velocity = e.Velocity;
            ReceivedAt = now - Mathf.Max(0f, e.Age);
            FarClass = e.FarClass;
            Slot = e.Slot;
            Anchor = Position;
        }

        /// <summary>A compact pose (protocol 32, D111): an offset from the anchor; an upright body keeps its up axis.</summary>
        internal void Apply(in FarPose p, double now)
        {
            Position = new Double3(Anchor.X + p.DX, Anchor.Y + p.DY, Anchor.Z + p.DZ);
            if (p.Upright) Rotation = FarPosesMsg.UprightRotation(p.Forward, Rotation * Vector3.up, Rotation);
            else Rotation = p.Rotation;
            Forward = p.Forward;
            Velocity = p.Velocity;
            ReceivedAt = now;
        }

        public override string ToString() => $"far #{NetId} (prefab {PrefabId}) at ({Position.X:0.0}, {Position.Y:0.0}, {Position.Z:0.0})";
    }
}
