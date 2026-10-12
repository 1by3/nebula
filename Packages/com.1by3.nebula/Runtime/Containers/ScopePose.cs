using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Where a drawn scope stands against the <b>anchor</b>, the scope whose frame the local view's frame resolves to
    /// (<c>docs/portals.md</c> D40, <see cref="Portals.Anchor"/>): the composition, in double and about the faces, of the link
    /// poses along the portals from the anchor to the scope. A point of the scope's own upright space is
    /// <c>ToOrigin + Turn × (p − FromOrigin)</c> in the anchor's. Used to draw only: simulation, prediction and every pose a game reads
    /// stay in each scope's own space.
    /// </summary>
    public readonly struct ScopePose
    {
        /// <summary>The scope this is the pose of.</summary>
        public ulong Scope { get; }
        /// <summary>The anchor it is measured against.</summary>
        public ulong Anchor { get; }
        /// <summary>The point of the scope (absolute in the scope) the pose turns about.</summary>
        public Double3 FromOrigin { get; }
        /// <summary>The same point in the anchor (absolute in the anchor).</summary>
        public Double3 ToOrigin { get; }
        /// <summary>What turns a rotation or direction of the scope's axes into the anchor's.</summary>
        public Quaternion Turn { get; }

        /// <summary>A pose.</summary>
        public ScopePose(ulong scope, ulong anchor, Double3 fromOrigin, Double3 toOrigin, Quaternion turn)
        {
            Scope = scope;
            Anchor = anchor;
            FromOrigin = fromOrigin;
            ToOrigin = toOrigin;
            Turn = turn;
        }

        /// <summary>The pose of a scope that stands as the anchor does: nothing moves, nothing turns.</summary>
        public static ScopePose Identity(ulong scope, ulong anchor) =>
            new ScopePose(scope, anchor, Double3.Zero, Double3.Zero, Quaternion.identity);

        /// <summary>Whether drawing the scope needs no posing: it is not turned (within the pose epsilon) and not moved (within a millimetre).</summary>
        public bool IsIdentity =>
            Quaternion.Angle(Quaternion.identity, Turn) <= NebulaWorker.PoseAngleEpsilon &&
            System.Math.Abs(ToOrigin.X - FromOrigin.X) <= 1e-3 && System.Math.Abs(ToOrigin.Y - FromOrigin.Y) <= 1e-3 && System.Math.Abs(ToOrigin.Z - FromOrigin.Z) <= 1e-3;

        /// <summary>A point of the scope (absolute) in the anchor (absolute), about the faces and in double.</summary>
        public Double3 Point(Double3 absolute) => ToOrigin + PhysicsFrames.Rotate(Turn, absolute - FromOrigin);

        /// <summary>A rotation of the scope's axes in the anchor's.</summary>
        public Quaternion Rotation(Quaternion rotation) => Turn * rotation;

        /// <inheritdoc/>
        public override string ToString() => $"scope {Scope:x} in {Anchor:x}: {FromOrigin} to {ToOrigin} turned {Turn.eulerAngles}";

        internal static ScopePose From(in PortalMap map, ulong anchor) => new ScopePose(map.FromScope, anchor, map.FromOrigin, map.ToOrigin, map.Turn);
        internal PortalMap ToMap() => new PortalMap(Scope, Anchor, FromOrigin, ToOrigin, Turn);
    }
}
