using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// A face's pose in a physics scene's own space, in float: what the geometry of a band is measured against (<c>docs/portals.md</c> D3, D26).
    /// A scene is anchored near what it holds, so a face-relative offset is small however far the scope's absolute origin is; the maths
    /// that must stay exact over distance, a crossing's, is in double (<see cref="PortalMap"/>) and does not use this.
    /// </summary>
    internal readonly struct PortalFaceFrame
    {
        public readonly Vector3 Origin;
        public readonly Quaternion Rotation;
        public readonly Quaternion Inverse;

        public PortalFaceFrame(Vector3 origin, Quaternion rotation)
        {
            Origin = origin;
            Rotation = rotation;
            Inverse = Quaternion.Inverse(rotation);
        }

        /// <summary>A point of the scene in the face's axes.</summary>
        public Vector3 ToLocal(Vector3 scenePoint) => Inverse * (scenePoint - Origin);

        /// <summary>A point in the face's axes as a point of the scene.</summary>
        public Vector3 ToScene(Vector3 localPoint) => Origin + Rotation * localPoint;
    }

    /// <summary>
    /// The band of a portal face (<c>docs/portals.md</c> D3): the aperture grown sideways by the band margin, reaching <see cref="Depth"/> to either
    /// side of the plane, a box in the face's axes. Pure geometry: it knows no scene, no entity and no worker.
    /// </summary>
    internal readonly struct PortalBand
    {
        public readonly float HalfWidth, HalfHeight, Depth;

        public PortalBand(float halfWidth, float halfHeight, float depth)
        {
            HalfWidth = halfWidth;
            HalfHeight = halfHeight;
            Depth = depth;
        }

        /// <summary>The band of an aperture grown sideways by <paramref name="margin"/>, <paramref name="depth"/> to either side of the plane.</summary>
        public PortalBand(PortalAperture aperture, float margin, float depth)
            : this(aperture.HalfWidth + margin, aperture.HalfHeight + margin, depth) { }

        /// <summary>Whether a point, in the face's axes, is in the band, grown on every side by <paramref name="grow"/>.</summary>
        public bool Contains(Vector3 local, float grow = 0f) =>
            Mathf.Abs(local.x) <= HalfWidth + grow && Mathf.Abs(local.y) <= HalfHeight + grow && Mathf.Abs(local.z) <= Depth + grow;

        /// <summary>Whether a box (<paramref name="min"/> to <paramref name="max"/>, axis-aligned in the face's axes) touches the band, grown on every side by <paramref name="grow"/>.</summary>
        public bool Overlaps(Vector3 min, Vector3 max, float grow = 0f) =>
            max.x >= -HalfWidth - grow && min.x <= HalfWidth + grow &&
            max.y >= -HalfHeight - grow && min.y <= HalfHeight + grow &&
            max.z >= -Depth - grow && min.z <= Depth + grow;

        /// <summary>
        /// Whether a box touches the half of the band on one side of the plane: <c>0 ≤ z ≤ Depth</c> for <paramref name="beyond"/>, <c>−Depth ≤ z ≤ 0</c>
        /// otherwise. What the throat copies (<c>docs/portals.md</c> D27): the far side's geometry in the part of the band that is its own side, and the near
        /// side's in the part that is its.
        /// </summary>
        public bool OverlapsHalf(Vector3 min, Vector3 max, bool beyond, float grow = 0f) =>
            max.x >= -HalfWidth - grow && min.x <= HalfWidth + grow &&
            max.y >= -HalfHeight - grow && min.y <= HalfHeight + grow &&
            (beyond ? max.z >= -grow && min.z <= Depth + grow : max.z >= -Depth - grow && min.z <= grow);

        /// <summary>The axis-aligned box, in the face's axes, around points of the scene.</summary>
        public static void LocalBounds(in PortalFaceFrame frame, Vector3[] scenePoints, int count, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < count; i++)
            {
                var l = frame.ToLocal(scenePoints[i]);
                min = Vector3.Min(min, l);
                max = Vector3.Max(max, l);
            }
        }
    }
}
