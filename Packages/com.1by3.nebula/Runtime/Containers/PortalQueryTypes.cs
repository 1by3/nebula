using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>What a <see cref="PortalHit"/> is besides a collider (<c>docs/portals.md</c> D32, D33).</summary>
    [Flags]
    public enum PortalHitFlags
    {
        /// <summary>An ordinary hit on a collider of the scope in <see cref="PortalHit.Scope"/>.</summary>
        None = 0,
        /// <summary>
        /// The cast reached a portal's aperture and stopped there: the hit is on the plane of the cross-section, and <see cref="PortalHit.Collider"/> is the aperture
        /// collider. The aperture was closed for the asker (not open, or not the asker's party's link, or an observation-only portal), or, with
        /// <see cref="DepthLimit"/>, open but a second portal deep.
        /// </summary>
        Aperture = 1,
        /// <summary>
        /// The cast left what this process knows: the far scope is on another worker and only its band is here (ghost proxies of its bodies), and the cast
        /// went past the band without hitting anything there. It is not a miss: nothing is known beyond <see cref="PortalHit.PointInScope"/>. <see cref="PortalHit.Collider"/>
        /// and <see cref="PortalHit.Entity"/> are null.
        /// </summary>
        Truncated = 2,
        /// <summary>The hit is in the far scope of a link the cast went through (it is in <see cref="PortalHit.Scope"/>, not the scope the cast started in).</summary>
        ThroughPortal = 4,
        /// <summary>The hit is against the pose the entity had at the query's tick, not the one it has now.</summary>
        Rewound = 8,
        /// <summary>An <see cref="Aperture"/> hit at an open link: queries continue through one portal only (<c>docs/portals.md</c> D22).</summary>
        DepthLimit = 16,
    }

    /// <summary>
    /// Who is asking a portal query (<c>docs/portals.md</c> D12, D32): the entity whose own colliders the cast skips, and the party whose link it goes through.
    /// A face with several parties' links casts into the asker's party's far scope. With no party named, the entity's party is its <see cref="Portal.PartyOf"/>
    /// (by default its owner's identity); with neither, the face's one open link, when there is exactly one.
    /// </summary>
    public readonly struct PortalAsker
    {
        /// <summary>The asking entity, whose colliders a cast does not hit; null for none.</summary>
        public NetworkIdentity Entity { get; }
        /// <summary>The party named explicitly; null when it is to be derived from <see cref="Entity"/>.</summary>
        public string Party { get; }

        private PortalAsker(NetworkIdentity entity, string party)
        {
            Entity = entity;
            Party = party;
        }

        /// <summary>Whether a party was named.</summary>
        public bool HasParty => Party != null;

        /// <summary>An entity asking: its colliders are skipped and its party chooses the link.</summary>
        public static PortalAsker Of(NetworkIdentity entity) => new PortalAsker(entity, null);

        /// <summary>A party asking, whether or not an entity is behind it (a turret, a spell, a sound): <paramref name="entity"/>, when given, is the one whose colliders are skipped.</summary>
        public static PortalAsker OfParty(string party, NetworkIdentity entity = null) => new PortalAsker(entity, party ?? "");

        /// <summary>Nobody in particular: the face's one open link, when there is exactly one.</summary>
        public static PortalAsker None => default;
    }

    /// <summary>
    /// One thing a portal query found (<c>docs/portals.md</c> D32): the scope it is in, the collider and the entity, how far along the cast it is, and its point in both the
    /// asker's space and the hit scope's. Spaces are the physics scenes' own coordinates (the scope's scene, or its physics frame's simulation space), in float and small
    /// by construction: a point is a face-relative offset carried through the link about the faces, never a bare translation (D37).
    /// </summary>
    public readonly struct PortalHit
    {
        /// <summary>The scope the hit is in: the scope the cast started in, or the far scope of the link it went through.</summary>
        public ulong Scope { get; }
        /// <summary>The collider hit; the aperture collider for <see cref="PortalHitFlags.Aperture"/>; null for <see cref="PortalHitFlags.Truncated"/>. Never a portal proxy.</summary>
        public Collider Collider { get; }
        /// <summary>The entity the collider belongs to (the nearest <see cref="NetworkIdentity"/> above it), or null.</summary>
        public NetworkIdentity Entity { get; }
        /// <summary>How far along the cast the hit is, in meters, summed over the legs (zero for an overlap).</summary>
        public float Distance { get; }
        /// <summary>The point in the asker's space: the scene the cast started in, as if the link were not there (a straight continuation).</summary>
        public Vector3 PointInAskerSpace { get; }
        /// <summary>The point in <see cref="Scope"/>'s own space.</summary>
        public Vector3 PointInScope { get; }
        /// <summary>The surface normal in <see cref="Scope"/>'s space.</summary>
        public Vector3 Normal { get; }
        /// <summary>The surface normal in the asker's space.</summary>
        public Vector3 NormalInAskerSpace { get; }
        /// <summary>What kind of hit.</summary>
        public PortalHitFlags Flags { get; }
        /// <summary>The id of the link the cast went through, or the one whose aperture it met; 0 for a hit that touched no portal.</summary>
        public ulong LinkId { get; }

        /// <summary>True for a hit on an aperture plane.</summary>
        public bool IsAperture => (Flags & PortalHitFlags.Aperture) != 0;
        /// <summary>True when the cast left the band of a link whose far scope is on another worker.</summary>
        public bool IsTruncated => (Flags & PortalHitFlags.Truncated) != 0;
        /// <summary>True when the hit is in the far scope of a link.</summary>
        public bool IsThroughPortal => (Flags & PortalHitFlags.ThroughPortal) != 0;

        internal PortalHit(ulong scope, Collider collider, NetworkIdentity entity, float distance, Vector3 pointInAsker, Vector3 pointInScope,
            Vector3 normal, Vector3 normalInAsker, PortalHitFlags flags, ulong linkId)
        {
            Scope = scope;
            Collider = collider;
            Entity = entity;
            Distance = distance;
            PointInAskerSpace = pointInAsker;
            PointInScope = pointInScope;
            Normal = normal;
            NormalInAskerSpace = normalInAsker;
            Flags = flags;
            LinkId = linkId;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"{(Entity != null ? Entity.name : Collider != null ? Collider.name : "-")} scope {Scope:x} {Distance:0.###} m {Flags}";
    }

    /// <summary>
    /// Settings of a portal query (<c>docs/portals.md</c> D32, D33). Make one and keep it: a query takes it by reference and allocates nothing itself. The defaults are
    /// every layer but <see cref="NebulaConfig.PortalProxyLayer"/>, triggers ignored, the present, and a scope's own scene.
    /// </summary>
    public sealed class PortalQueryOptions
    {
        /// <summary>The layers to hit. <see cref="NebulaConfig.PortalProxyLayer"/> is always left out: a proxy is never hit.</summary>
        public int LayerMask = ~0;

        /// <summary>Whether triggers are hit.</summary>
        public QueryTriggerInteraction Triggers = QueryTriggerInteraction.Ignore;

        /// <summary>
        /// The server tick to look at (a lag-compensated query), or 0 for now. The far leg is tested against the pose every entity there had at this tick
        /// (<see cref="HistoryRewind"/>); an entity that crossed during the interval is found on the side it was on. Workers only: a client keeps no history.
        /// </summary>
        public uint Tick;

        /// <summary>
        /// Also rewind the scope the cast starts in. Leave it off when the game already rewinds that scope itself (with <see cref="HistoryRewind"/> or its own colliders),
        /// and set it to have Nebula do both legs.
        /// </summary>
        public bool RewindNear;

        /// <summary>
        /// The physics frame the cast starts in, when it starts on ground that is a carrier's (<c>docs/portals.md</c> D26): the frame's owner container, in whose
        /// simulation space the cast's points are. Null for a scope's own scene.
        /// </summary>
        public Container Space;

        internal int EffectiveMask => LayerMask & ~(1 << Portals.ProxyLayer);
    }
}
