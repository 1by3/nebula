using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// A pose in a scope: an absolute <see cref="Double3"/> position and a rotation in the scope's own axes. What
    /// <see cref="Portals.Map(ulong, ulong, PortalPose)"/> carries from one scope into another (<c>docs/portals.md</c> D39).
    /// </summary>
    public readonly struct PortalPose
    {
        /// <summary>The position, absolute in the scope.</summary>
        public Double3 Position { get; }
        /// <summary>The rotation, in the scope's axes.</summary>
        public Quaternion Rotation { get; }

        /// <summary>A pose.</summary>
        public PortalPose(Double3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }

        /// <inheritdoc/>
        public override string ToString() => $"{Position} {Rotation.eulerAngles}";
    }

    /// <summary>
    /// One link's maths, one way (<c>docs/portals.md</c> D4, D37): a point of <see cref="FromScope"/> is
    /// <c>ToOrigin + Turn × (p − FromOrigin)</c> in <see cref="ToScope"/>, applied about the faces and in double. It is never
    /// stored as <c>T + R × p</c>, whose rotation error multiplies the distance from the scope's origin instead of the distance from
    /// the face: metres at 10⁸ m.
    /// </summary>
    internal readonly struct PortalMap
    {
        public readonly ulong FromScope, ToScope;
        public readonly Double3 FromOrigin, ToOrigin;
        /// <summary>What turns a rotation or direction of the from scope's axes into the to scope's.</summary>
        public readonly Quaternion Turn;
        /// <summary>The portal this map belongs to (its derived id), or 0 when it is learned from an instance's pose alone.</summary>
        public readonly ulong PortalId;

        public PortalMap(ulong fromScope, ulong toScope, Double3 fromOrigin, Double3 toOrigin, Quaternion turn, ulong portalId = 0)
        {
            FromScope = fromScope;
            ToScope = toScope;
            FromOrigin = fromOrigin;
            ToOrigin = toOrigin;
            Turn = turn;
            PortalId = portalId;
        }

        /// <summary>The identity: nothing moves, nothing turns.</summary>
        public static PortalMap Identity(ulong scope) => new PortalMap(scope, scope, Double3.Zero, Double3.Zero, Quaternion.identity);

        /// <summary>Whether <see cref="Turn"/> is more than the pose epsilon from the identity.</summary>
        public bool IsTurned => Quaternion.Angle(Quaternion.identity, Turn) > NebulaWorker.PoseAngleEpsilon;

        /// <summary>A point of the from scope (absolute) in the to scope (absolute).</summary>
        public Double3 Point(Double3 p) => ToOrigin + PhysicsFrames.Rotate(Turn, p - FromOrigin);

        /// <summary>A direction or rotation-free vector of the from scope in the to scope.</summary>
        public Vector3 Direction(Vector3 v) => Turn * v;

        /// <summary>A rotation of the from scope's axes in the to scope's.</summary>
        public Quaternion Rotation(Quaternion q) => Turn * q;

        /// <summary>The same link the other way.</summary>
        public PortalMap Inverse => new PortalMap(ToScope, FromScope, ToOrigin, FromOrigin, Quaternion.Inverse(Turn), PortalId);
    }

    /// <summary>
    /// One side of a <see cref="Portal"/> (<c>docs/portals.md</c> D2): a scope, the container the face is fixed in, and a
    /// pose, an absolute <see cref="Double3"/> position in that scope and a rotation. The face's +z axis is the way from
    /// the near scope into the far one: in its axes, z &lt; 0 is the near side's region and z ≥ 0 the far side's.
    /// </summary>
    /// <remarks>
    /// As built, the pose is held in the scope's own absolute space (what <see cref="ContainerRegistry.ToAbsolutePrecise(Vector3, ulong)"/>
    /// gives, out of any physics frame the container is in) rather than in the container's local space, which is the form
    /// every crossing already compares in; the container is kept for ownership, lookup and the network gateway's rows.
    /// </remarks>
    public readonly struct PortalFace
    {
        /// <summary>The scope the face is in (<see cref="Container.InstanceId"/>; 0 is the public world).</summary>
        public ulong Scope { get; }
        /// <summary>The id of the container the face is fixed in; empty when it is not known yet.</summary>
        public string ContainerId { get; }
        /// <summary>The face's origin, absolute in <see cref="Scope"/>.</summary>
        public Double3 Origin { get; }
        /// <summary>The face's axes in <see cref="Scope"/>'s space, normalised and rounded to 10⁻⁶ (<c>docs/portals.md</c> D37).</summary>
        public Quaternion Rotation { get; }

        /// <summary>A face.</summary>
        public PortalFace(ulong scope, string containerId, Double3 origin, Quaternion rotation)
        {
            Scope = scope;
            ContainerId = containerId ?? "";
            Origin = origin;
            Rotation = rotation;
        }

        /// <summary>The way through, in the scope's axes.</summary>
        public Vector3 Forward => Rotation * Vector3.forward;

        /// <summary>A point of the scope (absolute) in the face's axes: the offset from the origin, turned back by the face's rotation, in double.</summary>
        public Double3 ToLocal(Double3 absolute) => PhysicsFrames.Rotate(Quaternion.Inverse(Rotation), absolute - Origin);

        /// <summary>A point in the face's axes as an absolute point of the scope.</summary>
        public Double3 ToAbsolute(Double3 local) => Origin + PhysicsFrames.Rotate(Rotation, local);

        /// <inheritdoc/>
        public override string ToString() => $"face {ContainerId} scope {Scope:x} at {Origin} facing {Forward}";
    }

    /// <summary>
    /// The opening of a portal, on its face's z = 0 plane (<c>docs/portals.md</c> D3): an oriented box centred on the
    /// face's origin. Any convex shape is a later <see cref="Kind"/> with the same contract, a cross-section test and a
    /// bounding box.
    /// </summary>
    [Serializable]
    public struct PortalAperture
    {
        /// <summary>The shape. <see cref="PortalApertureKind.Box"/> today.</summary>
        public PortalApertureKind Kind;
        /// <summary>Half the opening's width, along the face's x axis.</summary>
        public float HalfWidth;
        /// <summary>Half the opening's height, along the face's y axis.</summary>
        public float HalfHeight;

        /// <summary>A rectangular opening <c>2 × halfWidth</c> wide and <c>2 × halfHeight</c> high.</summary>
        public static PortalAperture Box(float halfWidth, float halfHeight) =>
            new PortalAperture { Kind = PortalApertureKind.Box, HalfWidth = halfWidth, HalfHeight = halfHeight };

        /// <summary>Whether the point (<paramref name="x"/>, <paramref name="y"/>) of the face's plane is in the opening, widened by <paramref name="margin"/> on every side.</summary>
        public bool Contains(double x, double y, float margin = 0f) =>
            Math.Abs(x) <= HalfWidth + margin && Math.Abs(y) <= HalfHeight + margin;

        /// <inheritdoc/>
        public override string ToString() => $"{Kind} {HalfWidth * 2:0.##} x {HalfHeight * 2:0.##} m";
    }

    /// <summary>
    /// One party's link through a <see cref="Portal"/> (<c>docs/portals.md</c> D8, D9): the far scope, the state the link
    /// is in and, when it is not open, what it waits for. Created by <see cref="Portal.Open"/>; the worker that owns the
    /// portal drives it Closed → Preparing → Open → Closing → Closed. Read it from the worker that owns the portal.
    /// </summary>
    public sealed class PortalLink
    {
        internal readonly HashSet<ulong> MemberSet = new HashSet<ulong>();
        internal readonly HashSet<ulong> WarmSet = new HashSet<ulong>();
        internal readonly HashSet<ulong> ObserverSet = new HashSet<ulong>();

        internal PortalLink(Portal portal, string party)
        {
            Portal = portal;
            Party = party ?? "";
            Id = PortalIds.Link(portal.PortalId, Party);
        }

        /// <summary>The portal this link goes through.</summary>
        public Portal Portal { get; }
        /// <summary>The party it is for: an opaque string the game chose (<c>docs/portals.md</c> D8).</summary>
        public string Party { get; }
        /// <summary>The link's id (<see cref="PortalIds.Link"/>).</summary>
        public ulong Id { get; }
        /// <summary>Where the link stands.</summary>
        public PortalState State { get; internal set; }
        /// <summary>Why the link is not open; <see cref="PortalWait.None"/> when it is.</summary>
        public PortalWait Wait { get; internal set; }
        /// <summary>The member client the link waits for while <see cref="Wait"/> is <see cref="PortalWait.ClientWarming"/>; else 0.</summary>
        public ulong WaitClient { get; internal set; }
        /// <summary>How ready the link is, from 0 to 1: something for a door's UI to show while it opens. 1 once open.</summary>
        public float Progress { get; internal set; }
        /// <summary>Why the far content failed to load, when it did; else null.</summary>
        public string Error { get; internal set; }
        /// <summary>Counts the link's openings (see <see cref="PortalLinkRecord.Generation"/>).</summary>
        public uint Generation { get; internal set; }
        /// <summary>The far scope (0 until it is known).</summary>
        public ulong FarScope { get; internal set; }
        /// <summary>The far scope's key.</summary>
        public string FarScopeKey { get; internal set; } = "";
        /// <summary>The far face. Its scope is <see cref="FarScope"/>; for a template far side it is the near face read in the instance's own upright space (<c>docs/portals.md</c> D5).</summary>
        public PortalFace FarFace { get; internal set; }
        /// <summary>The far container a crossing enters, once the far scope's rows are here; else null.</summary>
        public Container FarEntry { get; internal set; }
        internal string FarEntryId = "";
        internal ulong FarEntryRuntimeId;
        // Worker bookkeeping (Portal.Links.cs).
        internal bool NeedsActivation = true;
        internal bool Dirty = true;
        internal ulong ReadyEpoch;
        internal bool HandshakeSent, HandshakeDone;
        internal uint HandshakeRequest;
        internal float HandshakeAt;
        internal float ClosingSince;
        internal float NextStandingCheck;
        internal int PublishedHash;
        internal bool Published;
        internal byte PublishedProgress;
        internal string WarnedError;

        /// <summary>The near face.</summary>
        public PortalFace NearFace => Portal.NearFace;

        /// <summary>Whether the link is <see cref="PortalState.Open"/>.</summary>
        public bool IsOpen => State == PortalState.Open;
        /// <summary>The clients that must be warm before the link opens (<c>docs/portals.md</c> D13).</summary>
        public IReadOnlyCollection<ulong> Members => MemberSet;
        /// <summary>The clients that may look through the link (<c>docs/portals.md</c> D11).</summary>
        public IReadOnlyCollection<ulong> Observers => ObserverSet;
        /// <summary>Whether anyone may look through the link, not only its observers.</summary>
        public bool ObserverAnyone => Portal.ObserveAnyone;
        /// <summary>Whether <paramref name="clientId"/> is a member.</summary>
        public bool IsMember(ulong clientId) => MemberSet.Contains(clientId);
        /// <summary>Whether <paramref name="clientId"/> has said it holds the far window (<see cref="MsgId.PortalWarm"/>).</summary>
        public bool IsWarm(ulong clientId) => WarmSet.Contains(clientId);
        /// <summary>Whether <paramref name="clientId"/> may look through the link.</summary>
        public bool IsObserver(ulong clientId) => ObserverAnyone || ObserverSet.Contains(clientId);

        /// <summary>
        /// What turns a rotation or direction of the far scope's space into the near scope's: the near face's rotation times
        /// the far face's inverse (<c>docs/portals.md</c> D4). For a coincident portal it is the pure turn about the door.
        /// </summary>
        public Quaternion Turn => ToNearMap.Turn;

        /// <summary>
        /// A point of the far scope (absolute) in the near scope: <c>N.Origin + (N.Rotation × F.Rotation⁻¹) × (p − F.Origin)</c>,
        /// applied about the faces and in double (<c>docs/portals.md</c> D4, D37).
        /// </summary>
        public Double3 ToNear(Double3 farPoint) => ToNearMap.Point(farPoint);

        /// <summary>The inverse of <see cref="ToNear"/>: a point of the near scope in the far scope.</summary>
        public Double3 ToFar(Double3 nearPoint) => ToFarMap.Point(nearPoint);

        /// <summary>The link's maths from the far scope into the near one (<see cref="ToNear"/>), for the pose and velocity of whatever crosses.</summary>
        internal PortalMap ToNearMap
        {
            get
            {
                var near = NearFace;
                var far = FarFace;
                return new PortalMap(far.Scope, near.Scope, far.Origin, near.Origin, near.Rotation * Quaternion.Inverse(far.Rotation), Portal.PortalId);
            }
        }

        /// <summary>The link's maths from the near scope into the far one (<see cref="ToFar"/>).</summary>
        internal PortalMap ToFarMap => ToNearMap.Inverse;

        /// <inheritdoc/>
        public override string ToString() => $"link {Portal.Id}/{Party} {State}{(Wait != PortalWait.None ? " (" + Wait + ")" : "")}";
    }
}
