using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The instances this process holds whose own space is turned against their host scope's (NEB-426,
    /// <c>docs/scope-frames.md</c> D12): built upright in their own space and posed at a door that is not upright in the
    /// host (a door on a sphere). Learned from the instances' lease rows (<see cref="InstanceContainerInfo.HasPose"/>) when
    /// their first container is registered here, and forgotten with their last. A point p of the instance's space is
    /// <c>origin + rotation × (p − origin)</c> in its host's.
    /// </summary>
    public static class InstancePoses
    {
        private struct Pose
        {
            public Double3 Origin;
            public Quaternion Rotation;
        }

        private static readonly Dictionary<ulong, Pose> Poses = new Dictionary<ulong, Pose>();

        /// <summary>How many turned instances this process holds.</summary>
        public static int Count => Poses.Count;

        /// <summary>Every turned instance's id (the client's drawing pass walks them).</summary>
        public static IEnumerable<ulong> Instances => Poses.Keys;

        /// <summary>The pose of <paramref name="instanceId"/>, when it is turned against its host.</summary>
        public static bool TryGet(ulong instanceId, out Double3 origin, out Quaternion rotation)
        {
            if (instanceId != 0 && Poses.TryGetValue(instanceId, out var pose))
            {
                origin = pose.Origin;
                rotation = pose.Rotation;
                return true;
            }
            origin = Double3.Zero;
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>The instance's axes in its host's: identity for any scope that is not turned.</summary>
        public static Quaternion RotationOf(ulong instanceId) => TryGet(instanceId, out _, out var r) ? r : Quaternion.identity;

        /// <summary>
        /// What turns a rotation (or a direction) of scope <paramref name="from"/>'s space into scope <paramref name="to"/>'s,
        /// for an entity crossing between them through a door: identity unless either is a turned instance. A game keeping
        /// its own heading or velocity bookkeeping (a pawn's yaw) applies it when its entity changes scope.
        /// </summary>
        public static Quaternion Turn(ulong from, ulong to) => Quaternion.Inverse(RotationOf(to)) * RotationOf(from);

        /// <summary>A point of <paramref name="instanceId"/>'s own space in its host's (the identity for a scope that is not turned).</summary>
        public static Double3 ToHost(ulong instanceId, Double3 point)
        {
            if (!TryGet(instanceId, out var o, out var r)) return point;
            return o + PhysicsFrames.Rotate(r, point - o);
        }

        /// <summary>A point of the host's space in <paramref name="instanceId"/>'s own (the inverse of <see cref="ToHost"/>).</summary>
        public static Double3 FromHost(ulong instanceId, Double3 point)
        {
            if (!TryGet(instanceId, out var o, out var r)) return point;
            return o + PhysicsFrames.Rotate(Quaternion.Inverse(r), point - o);
        }

        internal static void Learn(InstanceContainerInfo info)
        {
            if (info == null || info.InstanceId == 0) return;
            if (!info.HasPose) { if (Poses.Remove(info.InstanceId)) DrawnScopes.MarkDirty(); return; }
            DrawnScopes.MarkDirty();
            Poses[info.InstanceId] = new Pose { Origin = info.PoseOrigin, Rotation = Normalized(info.PoseRotation) };
        }

        internal static void Forget(ulong instanceId) { if (Poses.Remove(instanceId)) DrawnScopes.MarkDirty(); }

        internal static void Clear() { Poses.Clear(); DrawnScopes.MarkDirty(); }

        private static Quaternion Normalized(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 1e-6f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }
    }
}
