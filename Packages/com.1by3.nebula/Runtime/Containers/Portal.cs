using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>Why a portal did not let an entity through, or leave a companion behind (<c>docs/portals.md</c> D17).</summary>
    public enum PortalRefusal
    {
        /// <summary>Nothing refused it.</summary>
        None,
        /// <summary>The entity's party has no link, or its link is not <see cref="PortalState.Open"/>.</summary>
        LinkNotOpen,
        /// <summary><see cref="Portal.CanEnter"/> refused it, or it belongs to a client that is not a warm member of the link.</summary>
        NotAdmitted,
        /// <summary>It has no party and more than one link is open on this face (<c>docs/portals.md</c> D12).</summary>
        AmbiguousParty,
        /// <summary>This worker does not simulate it.</summary>
        NotSimulated,
        /// <summary>It is a scene entity, which cannot leave its authored scene, or it is pinned to its container.</summary>
        Pinned,
        /// <summary>It is in another scope than its leader.</summary>
        OtherScope,
        /// <summary>The container it would arrive in is not here yet, or no worker holds an active lease on it.</summary>
        NoDestination,
    }

    /// <summary>
    /// A way between two scopes: an opening in a wall, a hatch, a gate, a door to a room of its own (<c>docs/portals.md</c>).
    /// It stands in the near side's content, at a <see cref="NearFace"/> fixed in a container, and leads to a far side that is
    /// a fixed scope (<see cref="SetFixedFarSide"/>) or a template, one far scope per party (<see cref="FarTemplate"/>).
    /// <para>
    /// The game opens it for a party with <see cref="Open"/>, naming the clients that will cross (its members), and closes
    /// it with <see cref="Close"/>. Each party has its own <see cref="PortalLink"/> with a state and, while it is not open,
    /// the reason (<see cref="PortalLink.Wait"/>). A link is open once the far scope is active and its content loaded on the
    /// worker that owns it, and every member's client holds the far window and has said so: readiness is standing, held for
    /// as long as the link is open, so crossing loads nothing and holds nothing at the face. Everything runs on the worker
    /// that owns the portal's container.
    /// </para>
    /// <para>
    /// An entity crosses when it passes through the aperture, the opening on the face's z = 0 plane: anything simulated here (a
    /// player's pawn, a crate, a projectile, a vehicle with its passengers) goes through an open link on the tick its root ends
    /// <see cref="CrossDepth"/> beyond the plane, or, for a body that moves further than that in a step, on the tick before the step
    /// that would carry it through. It keeps its identity and carries its pose, its velocities and its history, turned by the link when
    /// the faces are turned against each other. One the link does not let through is stopped at the face (<see cref="Refused"/>).
    /// <see cref="ResolveCompanions"/> names entities that cross with it. The hooks <see cref="PartyOf"/>,
    /// <see cref="CanEnter"/> and <see cref="CanObserve"/> are the game's policy.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class Portal : MonoBehaviour
    {
        // ------------------------------------------------------------------------------------------ authoring

        /// <summary>
        /// The portal's id, unique within the container it stands in (<c>docs/portals.md</c> D6). Together with that container's id it
        /// derives <see cref="PortalId"/>, which every worker, network gateway and client computes the same. A second portal
        /// in the same container with the same id is ignored and logged. An empty id is the object's name.
        /// </summary>
        public string Id = "";

        /// <summary>
        /// A template far side (<c>docs/portals.md</c> D5): one far scope per party, whose key is
        /// <c>TemplateId + "/" + party</c>, prepared at the near face's origin and turned with it. Must be available on workers.
        /// </summary>
        public InstanceTemplate FarTemplate;

        /// <summary>The opening, centred on the near face's origin, on its z = 0 plane. Its half extents are measured in the face's axes.</summary>
        public PortalAperture Aperture = PortalAperture.Box(1f, 2f);

        /// <summary>
        /// Whether a template far scope turns with this face: its interior is built in the face's own axes (its forward is the way
        /// in) and stands behind the face however the face is turned. Off keeps the far scope in the near scope's axes.
        /// </summary>
        [Tooltip("Turn the far scope with this face: its interior is built in the face's own axes. Off keeps the near scope's axes.")]
        public bool TurnWithFace = true;

        /// <summary>The band is the aperture grown sideways by this many meters, <see cref="BandDepth"/> deep on either side of the face.</summary>
        public float BandMargin = 0.5f;
        /// <summary>A window is the aperture grown sideways by this many meters (<c>docs/portals.md</c> D3).</summary>
        public float WindowMargin = 8f;
        /// <summary>A window reaches this many meters into each side.</summary>
        public float WindowDepth = 48f;

        /// <summary>How deep the band reaches on each side of the face, in meters.</summary>
        public const float BandDepth = 2f;
        /// <summary>
        /// How far beyond the face's plane a root must end to cross, in meters, and how far short of it to cross back: the hysteresis
        /// that keeps an entity standing at the face from crossing and returning on every tick.
        /// </summary>
        public const float CrossDepth = 0.05f;
        /// <summary>How long a closing link waits for the band to empty before it closes anyway, in seconds.</summary>
        public const float CloseTimeoutSeconds = 5f;
        /// <summary>How far from the face, in meters, an entity is tracked for a crossing and its companions are asked for.</summary>
        public const float ApproachDistance = 8f;

        /// <summary>
        /// The face's origin is its position in absolute coordinates rounded to this many meters, so every worker and every
        /// origin frame names the same far scope, however the transform's float numbers came out.
        /// </summary>
        public const double OriginResolution = 0.001;

        /// <summary>Whether anyone may look through this portal's links, not only their observers (<c>docs/portals.md</c> D11).</summary>
        public bool ObserveAnyone;

        /// <summary>
        /// Server-side: the party an entity belongs to (<c>docs/portals.md</c> D11), an opaque string of the game's choosing, or
        /// null for none. When unset: the owner's identity (<see cref="NetworkIdentity.OwnerIdentity"/>) for an entity with an
        /// owner, null for a server-owned entity. An entity crosses through the link of its party; with no party it uses the face's
        /// one open link, when there is exactly one (<c>docs/portals.md</c> D12).
        /// </summary>
        public Func<NetworkIdentity, string> PartyOf;

        /// <summary>
        /// Server-side admission into a link's far scope (<c>docs/portals.md</c> D11). When unset: a server-owned entity is
        /// admitted, and an owned one when its client is a member of the link and warm. Leaving is not checked.
        /// </summary>
        public Func<NetworkIdentity, PortalLink, bool> CanEnter;

        /// <summary>
        /// Server-side: whether a client may look through a link (<c>docs/portals.md</c> D11), asked when it becomes a member.
        /// When unset every member may. The answer is published to the network gateways as the link's observer list, which they
        /// enforce without running game code.
        /// </summary>
        public Func<ulong, PortalLink, bool> CanObserve;

        /// <summary>
        /// Server-side: fill the list with the entities that must cross together with the one crossing, its companions: an item
        /// it holds, a pet, a cart it tows, an escort (<c>docs/portals.md</c> D14). Called on the worker that simulates the
        /// crossing entity, once a tick while it is near the face, with an empty list. A companion moves with its leader and does
        /// not cross on its own while it is one. The group commits in one step on the tick the leader crosses, each member keeping
        /// its identity and arriving at its own pose mapped through the link. A companion that cannot cross is left behind
        /// (<see cref="CompanionLeftBehind"/>); with the far side warm, a companion never waits for a preparation of its own.
        /// </summary>
        public Action<NetworkIdentity, List<NetworkIdentity>> ResolveCompanions;

        /// <summary>
        /// Raised on this worker when a crossing entity committed without one of its companions: the crossing entity, the
        /// companion, and why. The companion stays where it was. Handlers run inside the worker's tick and must not throw.
        /// </summary>
        public event Action<NetworkIdentity, NetworkIdentity, PortalRefusal> CompanionLeftBehind;

        /// <summary>
        /// Raised after an entity crossed through this portal, on the worker that committed it and on every client that holds the entity when its
        /// container change arrives: the entity, this portal, the scope it left and the scope it entered (so <c>to</c> is the far scope when it
        /// went in and the near scope when it came out). A game's own heading bookkeeping turns with <see cref="Portals.Turn"/><c>(from, to)</c>
        /// here. On a client the event is raised only where this component exists; <see cref="Portals.Crossed"/> is raised wherever the crossing is seen.
        /// Handlers run inside the worker's tick (or the client's frame) and must not throw.
        /// </summary>
        public event Action<NetworkIdentity, Portal, ulong, ulong> Crossed;

        /// <summary>
        /// Raised on this worker every time an entity reached the aperture and was not let through (<c>docs/portals.md</c> D17): the entity, the
        /// link it was refused by (null when it has no link here: its party never opened one, or the face has several parties' links open and
        /// nothing says whose it is, <see cref="PortalRefusal.AmbiguousParty"/>), and why. The entity has been stopped at the face already: its root put back
        /// <see cref="CrossDepth"/> short of the plane and the part of its velocity that pointed through removed, so a game can bounce, drop or
        /// destroy it. A body that keeps pushing against the aperture is refused on every tick it reaches it. Handlers run inside the worker's tick and must not throw.
        /// </summary>
        public event Action<NetworkIdentity, PortalLink, PortalRefusal> Refused;

        /// <summary>Raised on this worker when a link's state, wait, members or observers changed. Handlers run inside the worker's tick and must not throw.</summary>
        public event Action<PortalLink> LinkChanged;

        // ------------------------------------------------------------------------------------------ the far side

        private string _fixedScopeKey = "";
        private Double3 _fixedOrigin;
        private Quaternion _fixedRotation = Quaternion.identity;

        /// <summary>The scope key of a fixed far side; empty for a template far side (<see cref="SetFixedFarSide"/>).</summary>
        public string FixedFarScopeKey => _fixedScopeKey;
        /// <summary>Whether the far side is a fixed scope rather than a template.</summary>
        public bool HasFixedFarSide => !string.IsNullOrEmpty(_fixedScopeKey);

        /// <summary>
        /// Make the far side a scope that exists anyway (<c>docs/portals.md</c> D5): <paramref name="origin"/> and
        /// <paramref name="rotation"/> are the far face's pose in that scope, absolute. When the two faces' origins differ, the
        /// portal is an offset portal and the link carries a translation as well as a turn. The scope must be active before a
        /// link can open; Nebula never activates it.
        /// </summary>
        public void SetFixedFarSide(string scopeKey, Double3 origin, Quaternion rotation)
        {
            if (string.IsNullOrEmpty(scopeKey)) throw new ArgumentException("A fixed far side needs a scope key", nameof(scopeKey));
            _fixedScopeKey = scopeKey;
            _fixedOrigin = origin;
            _fixedRotation = Normalize(rotation);
            FarTemplate = null;
        }

        // ------------------------------------------------------------------------------------------ where it stands

        private Container _host;
        private bool _hostResolved;
        private Container _found;

        private void OnEnable()
        {
            _hostResolved = false;
            _found = null;
            Portals.Register(this);
        }

        private void OnDisable()
        {
            Portals.Unregister(this);
            DropAllState();
        }

        private void OnTransformParentChanged() { _hostResolved = false; _found = null; }

        /// <summary>
        /// The authored <see cref="Id"/>, or the object's name when it has none: the id a portal is derived and looked up by.
        /// </summary>
        public string AuthoredId => string.IsNullOrEmpty(Id) ? name : Id;

        /// <summary>
        /// The container the near face is fixed in: the nearest <see cref="Container"/> among the portal's parents (a scoped
        /// grid's chunk, an instance part, a carrier), or, for content in the shared scene, the container that holds its position.
        /// Null while none does.
        /// </summary>
        public Container NearContainer
        {
            get
            {
                // A destroyed container compares equal to null while the reference is still set: look again.
                if (!_hostResolved || (_host == null && !ReferenceEquals(_host, null)))
                {
                    _host = transform.parent != null ? transform.parent.GetComponentInParent<Container>(true) : null;
                    _hostResolved = true;
                }
                if (_host != null) return _host;
                if (_found == null || !_found) _found = ContainerRegistry.Find(transform.position, null, 0UL, null);
                return _found;
            }
        }

        /// <summary>The scope the near face is in: the scope of <see cref="NearContainer"/>'s ancestors, or 0, the public world, when it stands in none.</summary>
        public ulong NearScope
        {
            get
            {
                var _ = NearContainer; // resolves the parent container
                return _host != null ? _host.InstanceId : 0;
            }
        }

        /// <summary>The portal's derived id (<c>docs/portals.md</c> D6): the hash of its container's id and its <see cref="AuthoredId"/>.</summary>
        public ulong PortalId
        {
            get
            {
                var container = NearContainer;
                string containerId = container != null ? container.ContainerId : "";
                string id = AuthoredId;
                // Hashed again only when either part changed: this is read for every portal on every worker tick.
                if (!_idCached || _idContainer != containerId || _idAuthored != id)
                {
                    _idContainer = containerId;
                    _idAuthored = id;
                    _idValue = PortalIds.Of(containerId, id);
                    _idCached = true;
                }
                return _idValue;
            }
        }

        private bool _idCached;
        private string _idContainer = "", _idAuthored = "";
        private ulong _idValue;

        /// <summary>
        /// The near face: where this component stands, read out of any physics frame it is in, through its scope's origin frame, in
        /// double, with its origin rounded to <see cref="OriginResolution"/> and its rotation normalised and rounded to 10⁻⁶ so
        /// one door always prepares one definition (<c>docs/portals.md</c> D2, D37). Its +z axis, the transform's forward, is the way in.
        /// </summary>
        public PortalFace NearFace
        {
            get
            {
                var container = NearContainer;
                ulong scope = NearScope;
                var space = _host != null ? _host.InnerSpace : null;
                PhysicsFrames.PoseInSpace(transform, space, out var framePosition, out _, out _);
                var absolute = ContainerRegistry.ToAbsolutePrecise(PhysicsFrames.ConvertLocal(framePosition, space, null), scope);
                var origin = new Double3(Round(absolute.X), Round(absolute.Y), Round(absolute.Z));
                var rotation = space != null && PhysicsFrames.InSimulationPose(space) ? PhysicsFrames.Convert(transform.rotation, space, null) : transform.rotation;
                return new PortalFace(scope, container != null ? container.ContainerId : "", origin, Canonical(rotation));
            }
        }

        private static double Round(double v) => Math.Round(v / OriginResolution) * OriginResolution;

        private static float RoundQ(float v) => (float)(Math.Round(v * 1e6) / 1e6);

        /// <summary>A rotation as every process names it: normalised, in the hemisphere w ≥ 0, rounded, and the identity within the pose epsilon.</summary>
        internal static Quaternion Canonical(Quaternion q)
        {
            q = Normalize(q);
            if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            q = new Quaternion(RoundQ(q.x), RoundQ(q.y), RoundQ(q.z), RoundQ(q.w));
            return Quaternion.Angle(Quaternion.identity, q) <= NebulaWorker.PoseAngleEpsilon ? Quaternion.identity : q;
        }

        private static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 1e-6f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }

        /// <summary>
        /// The rotation a template far scope is prepared with (<c>docs/scope-frames.md</c> D12): the near face's, so an interior
        /// built upright in its own space stands behind a face that is not upright in its scope; the identity when
        /// <see cref="TurnWithFace"/> is off.
        /// </summary>
        public Quaternion PoseRotation => TurnWithFace ? NearFace.Rotation : Quaternion.identity;

        /// <summary>
        /// The far face a template far side gets (<c>docs/portals.md</c> D5): the near face's origin in the instance's own upright
        /// space, so the portal is coincident by construction. A face turned with the far scope has the identity rotation; one that is
        /// not keeps the near face's axes.
        /// </summary>
        internal PortalFace TemplateFarFace(ulong farScope, string farContainerId)
        {
            var near = NearFace;
            return new PortalFace(farScope, farContainerId, near.Origin, TurnWithFace ? Quaternion.identity : near.Rotation);
        }

        // ------------------------------------------------------------------------------------------ links

        private readonly Dictionary<string, PortalLink> _links = new Dictionary<string, PortalLink>();
        private readonly List<PortalLink> _linkScratch = new List<PortalLink>();
        internal Func<float> ClockForTests;
        internal float Now => ClockForTests != null ? ClockForTests() : Time.unscaledTime;
        /// <summary>Bumped when the portal's record changes, so the worker publishes it again.</summary>
        internal int Revision;

        /// <summary>The links this portal has, one per party that ever opened it, whatever their state.</summary>
        public IEnumerable<PortalLink> Links => _links.Values;

        /// <summary>How many parties have a link here.</summary>
        public int LinkCount => _links.Count;

        /// <summary>The party's link, or false when it was never opened (a party without a link is closed).</summary>
        public bool TryGetLink(string party, out PortalLink link) => _links.TryGetValue(party ?? "", out link);

        /// <summary>The state of a party's link: <see cref="PortalState.Closed"/> when it has none.</summary>
        public PortalState StateOf(string party) => _links.TryGetValue(party ?? "", out var link) ? link.State : PortalState.Closed;

        /// <summary>
        /// Open the portal for <paramref name="party"/>: the link goes to <see cref="PortalState.Preparing"/> and the worker that owns the
        /// portal prepares the far scope and its content, asks the members' clients to hold the far window, and turns the link
        /// <see cref="PortalState.Open"/> when every condition holds (<c>docs/portals.md</c> D10). Call it on that worker. Opening a link
        /// that is already live adds the members; one that is closing opens again.
        /// </summary>
        /// <param name="party">The party the far scope belongs to (D8). A fixed far side usually has one party, the empty string.</param>
        /// <param name="members">The client ids that will cross, and must be warm before the link opens. None is fine for a link only server-owned entities use.</param>
        /// <returns>The party's link.</returns>
        public PortalLink Open(string party, IEnumerable<ulong> members = null)
        {
            party ??= "";
            if (!_links.TryGetValue(party, out var link)) _links.Add(party, link = new PortalLink(this, party));
            switch (link.State)
            {
                case PortalState.Closed:
                    link.Generation++;
                    link.WarmSet.Clear();
                    link.NeedsActivation = true;
                    link.ReadyEpoch = 0;
                    link.HandshakeSent = false;
                    link.HandshakeDone = false;
                    link.Error = null;
                    link.Progress = 0f;
                    SetState(link, PortalState.Preparing, PortalWait.FarScopeActivating);
                    break;
                case PortalState.Closing:
                    SetState(link, PortalState.Preparing, PortalWait.None);
                    break;
            }
            if (members != null) foreach (var member in members) AddMemberTo(link, member);
            WarnIfNotHere();
            return link;
        }

        /// <summary>
        /// Close the portal for <paramref name="party"/>: the link goes to <see cref="PortalState.Closing"/>, waits for the band to be empty
        /// (at most <see cref="CloseTimeoutSeconds"/>), and then turns <see cref="PortalState.Closed"/>. False when it has no live link.
        /// </summary>
        public bool Close(string party)
        {
            if (!_links.TryGetValue(party ?? "", out var link) || link.State == PortalState.Closed || link.State == PortalState.Closing) return false;
            if (link.State == PortalState.Preparing)
            {
                // Nothing crossed through a link that never opened: it can close at once.
                FinishClose(link, PortalWait.None);
                return true;
            }
            link.ClosingSince = Now;
            SetState(link, PortalState.Closing, PortalWait.BandOccupied);
            return true;
        }

        /// <summary>
        /// Add a client to a live link's members (<c>docs/portals.md</c> D13). It warms on its own, without closing the link for the
        /// others: its wait is <see cref="PortalWait.ClientWarming"/>, and its own entities are refused at the aperture until it is warm.
        /// </summary>
        public bool AddMember(string party, ulong clientId) =>
            _links.TryGetValue(party ?? "", out var link) && link.State != PortalState.Closed && AddMemberTo(link, clientId);

        /// <summary>
        /// Let a client look through a party's link without being a member (<c>docs/portals.md</c> D11, D20): a bystander at a door, a friend
        /// watching a room. While the link is open and the client stands inside the near window it is sent the far window; when it stands in
        /// the far window it is sent the near one. It is not held to warm the far window, so the link never waits for it, and its own entities are
        /// not admitted by the default <see cref="CanEnter"/>. Members are observers already unless <see cref="CanObserve"/> says otherwise.
        /// </summary>
        /// <returns>True when the client was not an observer of a live link before.</returns>
        public bool AddObserver(string party, ulong clientId)
        {
            if (clientId == 0 || !_links.TryGetValue(party ?? "", out var link) || link.State == PortalState.Closed || !link.ObserverSet.Add(clientId)) return false;
            link.Dirty = true;
            return true;
        }

        /// <summary>Stop a client looking through a party's link. A member keeps its part in readiness; it just no longer sees through.</summary>
        public bool RemoveObserver(string party, ulong clientId)
        {
            if (!_links.TryGetValue(party ?? "", out var link) || !link.ObserverSet.Remove(clientId)) return false;
            link.Dirty = true;
            return true;
        }

        /// <summary>Remove a client from a link's members; it no longer holds the link back and no longer looks through it.</summary>
        public bool RemoveMember(string party, ulong clientId)
        {
            if (!_links.TryGetValue(party ?? "", out var link) || !link.MemberSet.Remove(clientId)) return false;
            link.WarmSet.Remove(clientId);
            link.ObserverSet.Remove(clientId);
            link.Dirty = true;
            return true;
        }

        private bool AddMemberTo(PortalLink link, ulong clientId)
        {
            if (clientId == 0 || !link.MemberSet.Add(clientId)) return false;
            link.Dirty = true;
            if (link.State == PortalState.Open) link.Wait = PortalWait.ClientWarming;
            ApplyObserver(link, clientId);
            return true;
        }

        private void ApplyObserver(PortalLink link, ulong clientId)
        {
            bool observes;
            try { observes = CanObserve == null || CanObserve(clientId, link); }
            catch (Exception e)
            {
                NebulaLog.Error($"Portal '{name}': a CanObserve handler threw: {e}");
                observes = false;
            }
            if (observes) link.ObserverSet.Add(clientId); else link.ObserverSet.Remove(clientId);
        }

        private void SetState(PortalLink link, PortalState state, PortalWait wait)
        {
            if (link.State == state && link.Wait == wait) return;
            link.State = state;
            link.Wait = wait;
            if (wait != PortalWait.ClientWarming) link.WaitClient = 0;
            link.Dirty = true;
            RaiseLinkChanged(link);
        }

        private void RaiseLinkChanged(PortalLink link)
        {
            var handler = LinkChanged;
            if (handler == null) return;
            try { handler(link); }
            catch (Exception e) { NebulaLog.Error($"Portal '{name}': a LinkChanged handler threw: {e}"); }
        }

        private void FinishClose(PortalLink link, PortalWait wait)
        {
            link.WarmSet.Clear();
            link.Progress = 0f;
            SetState(link, PortalState.Closed, wait);
            link.Dirty = true;
        }

        private bool _warnedNotHere;

        private void WarnIfNotHere()
        {
            if (_warnedNotHere) return;
            var near = NearContainer;
            if (near == null || string.IsNullOrEmpty(near.OwnerWorkerId) || string.IsNullOrEmpty(NebulaRuntime.LocalWorkerId) || near.OwnerWorkerId == NebulaRuntime.LocalWorkerId) return;
            _warnedNotHere = true;
            NebulaLog.Warn($"Portal '{AuthoredId}' was opened on worker '{NebulaRuntime.LocalWorkerId}', but its container {near.ContainerId} belongs to '{near.OwnerWorkerId}': call Open on the worker that owns the portal");
        }

        private void DropAllState()
        {
            foreach (var link in _links.Values) { link.State = PortalState.Closed; link.Wait = PortalWait.None; }
            _links.Clear();
            _crossings.Clear();
            _leaderOf.Clear();
        }

        // ------------------------------------------------------------------------------------------ gizmos

        private void OnDrawGizmosSelected()
        {
            var previous = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(Aperture.HalfWidth * 2f, Aperture.HalfHeight * 2f, 0f));
            Gizmos.DrawLine(Vector3.zero, Vector3.forward * 1.5f);
            Gizmos.color = new Color(0f, 1f, 1f, 0.35f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3((Aperture.HalfWidth + BandMargin) * 2f, (Aperture.HalfHeight + BandMargin) * 2f, BandDepth * 2f));
            Gizmos.matrix = previous;
        }
    }
}
