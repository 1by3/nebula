using System;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// One side's window of a portal (<c>docs/portals.md</c> D3, D20): the cross-section of the aperture grown by the portal's
    /// <c>WindowMargin</c> sideways and extruded <c>WindowDepth</c> into that side, an oriented box in the face's axes. The near
    /// side's window is the part of the face's axes with z &lt; 0, the far side's the part with z ≥ 0 (D2). Pure geometry, in double,
    /// built from a <see cref="PortalRecord"/> alone so the network gateway, which runs no game code, can decide who may look through
    /// a link (D21) and a client can learn what it was sent.
    /// </summary>
    public readonly struct PortalWindow
    {
        /// <summary>The scope this window is in: the scope its face is in.</summary>
        public ulong Scope { get; }
        /// <summary>The face's origin, absolute in <see cref="Scope"/>.</summary>
        public Double3 Origin { get; }
        /// <summary>The face's axes in <see cref="Scope"/>.</summary>
        public Quaternion Rotation { get; }
        /// <summary>Half the window's width in the face's x axis: the aperture's plus the margin.</summary>
        public float HalfWidth { get; }
        /// <summary>Half the window's height in the face's y axis: the aperture's plus the margin.</summary>
        public float HalfHeight { get; }
        /// <summary>How far the window reaches into its side, in meters.</summary>
        public float Depth { get; }
        /// <summary>True for the near side, whose window is z ∈ [−<see cref="Depth"/>, 0] in the face's axes; false for the far side, z ∈ [0, <see cref="Depth"/>].</summary>
        public bool IsNearSide { get; }

        /// <summary>A window.</summary>
        public PortalWindow(ulong scope, Double3 origin, Quaternion rotation, float halfWidth, float halfHeight, float depth, bool isNearSide)
        {
            Scope = scope;
            Origin = origin;
            Rotation = rotation;
            HalfWidth = halfWidth;
            HalfHeight = halfHeight;
            Depth = depth;
            IsNearSide = isNearSide;
        }

        /// <summary>Whether the window has any volume. A portal whose depth is zero has no window and nobody looks through it.</summary>
        public bool IsEmpty => !(Depth > 0f) || !(HalfWidth > 0f) || !(HalfHeight > 0f);

        /// <summary>The face's +z axis in the scope's axes: the way from the near side into the far one.</summary>
        public Vector3 Forward => Rotation * new Vector3(0f, 0f, 1f);

        /// <summary>A point of the scope (absolute) in the face's axes, in double.</summary>
        public Double3 ToLocal(Double3 absolute) =>
            PortalGeometry.Rotate(new Quaternion(-Rotation.x, -Rotation.y, -Rotation.z, Rotation.w), absolute - Origin);

        /// <summary>A point in the face's axes as an absolute point of the scope.</summary>
        public Double3 ToAbsolute(Double3 local) => Origin + PortalGeometry.Rotate(Rotation, local);

        /// <summary>Whether an absolute point of <see cref="Scope"/> is inside the window. The box is closed: a point on its boundary is inside.</summary>
        public bool Contains(Double3 absolute)
        {
            if (IsEmpty) return false;
            var local = ToLocal(absolute);
            if (Math.Abs(local.X) > HalfWidth || Math.Abs(local.Y) > HalfHeight) return false;
            return IsNearSide ? local.Z <= 0.0 && local.Z >= -Depth : local.Z >= 0.0 && local.Z <= Depth;
        }

        /// <summary>The smallest axis-aligned box of <see cref="Scope"/>'s space that holds the window: what a region subscription and a container query are asked.</summary>
        public void Bounds(out Double3 min, out Double3 max)
        {
            double z0 = IsNearSide ? -Depth : 0.0, z1 = IsNearSide ? 0.0 : Depth;
            min = new Double3(double.MaxValue, double.MaxValue, double.MaxValue);
            max = new Double3(double.MinValue, double.MinValue, double.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var corner = ToAbsolute(new Double3((i & 1) == 0 ? -HalfWidth : HalfWidth, (i & 2) == 0 ? -HalfHeight : HalfHeight, (i & 4) == 0 ? z0 : z1));
                if (corner.X < min.X) min.X = corner.X;
                if (corner.Y < min.Y) min.Y = corner.Y;
                if (corner.Z < min.Z) min.Z = corner.Z;
                if (corner.X > max.X) max.X = corner.X;
                if (corner.Y > max.Y) max.Y = corner.Y;
                if (corner.Z > max.Z) max.Z = corner.Z;
            }
        }

        /// <inheritdoc/>
        public override string ToString() => $"window {(IsNearSide ? "near" : "far")} scope {Scope:x} at {Origin} {HalfWidth * 2:0.##} x {HalfHeight * 2:0.##} x {Depth:0.##} m";
    }

    /// <summary>The double-precision vector maths portals share between the worker, the network gateway and the client.</summary>
    internal static class PortalGeometry
    {
        /// <summary>
        /// <paramref name="v"/> turned by the float rotation <paramref name="q"/>, in double. The quaternion is normalised in double first: a
        /// float one is a few 1e-8 off unit length, which would scale 200 km by a centimetre.
        /// </summary>
        internal static Double3 Rotate(Quaternion q, Double3 v)
        {
            double qx = q.x, qy = q.y, qz = q.z, qw = q.w;
            double n = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (n > 0.0 && n != 1.0) { qx /= n; qy /= n; qz /= n; qw /= n; }
            // t = 2 (q x v); v' = v + w t + q x t
            double tx = 2.0 * (qy * v.Z - qz * v.Y);
            double ty = 2.0 * (qz * v.X - qx * v.Z);
            double tz = 2.0 * (qx * v.Y - qy * v.X);
            return new Double3(
                v.X + qw * tx + (qy * tz - qz * ty),
                v.Y + qw * ty + (qz * tx - qx * tz),
                v.Z + qw * tz + (qx * ty - qy * tx));
        }
    }
}
