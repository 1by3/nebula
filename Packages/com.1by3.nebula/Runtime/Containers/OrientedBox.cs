using System;

namespace Nebula
{
    /// <summary>
    /// A box turned by a rotation, tested without Unity types so the plain .NET test runner covers it. An
    /// <see cref="InstanceBoundary"/> uses it for its crossing volume: the box's local extents, turned with the
    /// boundary, so a doorway tilted by sloping or curved ground has a volume that follows it.
    /// </summary>
    internal static class OrientedBox
    {
        /// <summary>True when the rotation is exactly the identity, so the test can skip turning the offset.</summary>
        public static bool IsIdentity(float qx, float qy, float qz, float qw) => qx == 0f && qy == 0f && qz == 0f && qw == 1f;

        /// <summary>
        /// Whether a point lies in a box, edges included. <paramref name="px"/>.. is the point's offset from the box's
        /// anchor in the space the rotation is expressed in; <paramref name="qx"/>.. is the box's rotation in that space;
        /// <paramref name="cx"/>.. is the box's centre and <paramref name="hx"/>.. its half extents in the box's own
        /// axes, both relative to the anchor; <paramref name="margin"/> widens every side. The offset is turned by the
        /// inverse rotation into the box's own axes and compared there; with the identity rotation it is compared as is,
        /// exactly as an axis-aligned box.
        /// </summary>
        public static bool Contains(
            float px, float py, float pz,
            float qx, float qy, float qz, float qw,
            float cx, float cy, float cz,
            float hx, float hy, float hz,
            float margin)
        {
            if (!IsIdentity(qx, qy, qz, qw))
            {
                float n = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
                if (n > 0f)
                {
                    // Rotate by the conjugate: v' = v + 2w(u x v) + 2 u x (u x v), with u the negated vector part.
                    float ux = -qx / n, uy = -qy / n, uz = -qz / n, w = qw / n;
                    float tx = 2f * (uy * pz - uz * py);
                    float ty = 2f * (uz * px - ux * pz);
                    float tz = 2f * (ux * py - uy * px);
                    float rx = px + w * tx + (uy * tz - uz * ty);
                    float ry = py + w * ty + (uz * tx - ux * tz);
                    float rz = pz + w * tz + (ux * ty - uy * tx);
                    px = rx; py = ry; pz = rz;
                }
            }
            return Math.Abs(px - cx) <= hx + margin
                && Math.Abs(py - cy) <= hy + margin
                && Math.Abs(pz - cz) <= hz + margin;
        }
    }
}
