using System;

namespace Nebula
{
    /// <summary>
    /// Where a pair of bodies on opposite sides of a face collides (<c>docs/portals.md</c> D26). A body straddling the face has a proxy in the other
    /// side's scene; when two bodies, one native to each side, both have proxies and touch, each side would resolve the pair against the other's
    /// kinematic copy, an immovable mass, and the copy of a falling body would push the one below it out of its place. The pair collides in one scene
    /// only: the other side ignores the pair. This is the rule that picks the scene.
    /// </summary>
    internal static class PortalPairRule
    {
        /// <summary>Speeds closer than this (m/s) are a tie, settled by the bodies' ids.</summary>
        public const float SpeedTie = 0.05f;

        /// <summary>
        /// The scene the pair collides in: the near side's (true) or the far side's. A pair that already has a scene keeps it for as long as the two
        /// bodies touch, so a body that lands on another and comes to rest does not hand the pair to the other side. A new pair collides in the scene of the
        /// faster body, the one that arrived, which then stops against the other's copy, while the slower one is not moved by a copy of it; at equal speeds,
        /// in the scene of the body with the lower id.
        /// </summary>
        public static bool ResolvesNear(bool hasDecision, bool previouslyNear, float nearSpeed, float farSpeed, ulong nearId, ulong farId)
        {
            if (hasDecision) return previouslyNear;
            if (Math.Abs(nearSpeed - farSpeed) > SpeedTie) return nearSpeed > farSpeed;
            return nearId <= farId;
        }
    }
}
