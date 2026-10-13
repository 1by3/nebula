using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Marks a collider Nebula made to carry one side of a portal into the other (<c>docs/portals.md</c> §6): a kinematic copy of a body
    /// that touches an open link's band (D26), a static copy of the other side's geometry in the throat (D27), or ghost held only as a
    /// proxy on a worker that does not simulate its scope (D29). It lives on <see cref="NebulaConfig.PortalProxyLayer"/>
    /// and names the entity it stands for, so a hit can be attributed and a query or a game's trigger can skip it
    /// (<see cref="Is"/>). The aperture colliders (D28) carry it too, with no authority.
    /// <para>
    /// A proxy is not an entity and is not replicated; it exists on the process that built it, in the physics scene of the side it was
    /// built for. A game must keep <see cref="NebulaConfig.PortalProxyLayer"/> free of its own objects and out of its own layer masks
    /// (raycasts that should not hit a copy of a crate on the far side of a wall), and let the layer collide with whatever bodies
    /// collide with.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PortalProxy : MonoBehaviour
    {
        /// <summary>
        /// The entity this copies: the authority's body, or, on a worker holding only a ghost of an entity across a portal, that ghost; null for
        /// a static copy of geometry and for an aperture collider.
        /// </summary>
        public NetworkIdentity Authority { get; internal set; }

        /// <summary>The id of the link (<see cref="PortalLink.Id"/>) the proxy belongs to.</summary>
        public ulong LinkId { get; internal set; }

        /// <summary>The collider this copies: the authority's or the other side's static one. Null for an aperture collider.</summary>
        public Collider Source { get; internal set; }

        /// <summary>True for a copy of static geometry (the throat, D27) and for an aperture collider (D28); false for a copy of a body.</summary>
        public bool IsStatic { get; internal set; }

        /// <summary>True for a link's aperture collider (D28), the thin box over the cross-section.</summary>
        public bool IsAperture { get; internal set; }

        /// <summary>Whether <paramref name="collider"/> is one of Nebula's portal proxies or aperture colliders.</summary>
        public static bool Is(Collider collider) => collider != null && collider.GetComponent<PortalProxy>() != null;

        /// <summary>The proxy component of <paramref name="collider"/>, or null.</summary>
        public static PortalProxy Of(Collider collider) => collider != null ? collider.GetComponent<PortalProxy>() : null;
    }
}
