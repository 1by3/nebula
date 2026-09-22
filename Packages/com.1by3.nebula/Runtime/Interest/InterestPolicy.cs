using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// One place a client is interested in: a point (the usual case, its pawn) or a box (an <c>ObservePublic</c>
    /// window, an RTS selection). Positions are absolute world coordinates in double, like every other interest
    /// input, so a floating-origin shift changes nothing.
    /// </summary>
    public struct InterestFocus
    {
        public double X, Y, Z;
        /// <summary>Half extents; all zero for a point focus.</summary>
        public double HalfX, HalfY, HalfZ;
        /// <summary>Scales the radius used around this focus (0.5 = half as far). 1 for the usual focus.</summary>
        public float RadiusScale;
        /// <summary>The entity this focus follows, when it follows one (the pawn, an owned unit). 0 otherwise.</summary>
        public ulong SourceNetId;

        public bool IsBox => HalfX > 0 || HalfY > 0 || HalfZ > 0;

        public static InterestFocus Point(double x, double y, double z, float radiusScale = 1f, ulong sourceNetId = 0) =>
            new InterestFocus { X = x, Y = y, Z = z, RadiusScale = radiusScale <= 0 ? 1f : radiusScale, SourceNetId = sourceNetId };

        /// <summary>A box focus, from its center and full size.</summary>
        public static InterestFocus Box(double centerX, double centerY, double centerZ, double sizeX, double sizeY, double sizeZ, float radiusScale = 1f) =>
            new InterestFocus
            {
                X = centerX, Y = centerY, Z = centerZ,
                HalfX = Math.Abs(sizeX) / 2, HalfY = Math.Abs(sizeY) / 2, HalfZ = Math.Abs(sizeZ) / 2,
                RadiusScale = radiusScale <= 0 ? 1f : radiusScale,
            };

        /// <summary>Squared distance from this focus to a point; 0 inside a box focus.</summary>
        public double SqrDistanceTo(double x, double y, double z)
        {
            double dx = Math.Max(0, Math.Abs(x - X) - HalfX);
            double dy = Math.Max(0, Math.Abs(y - Y) - HalfY);
            double dz = Math.Max(0, Math.Abs(z - Z) - HalfZ);
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>The effective radius around this focus for an entity whose relevance radius is <paramref name="radius"/>.</summary>
        public double Scaled(double radius) => radius * (RadiusScale <= 0 ? 1f : RadiusScale);
    }

    /// <summary>
    /// How much of the world a client's own <see cref="ClientFocusHintMsg"/> may move its interest to. It is
    /// <b>server</b> state — set with <c>NebulaGateway.SetClientFocusMode</c> or by an
    /// <see cref="IFocusHintPolicy"/> — and never travels from the client, because a client that could widen its
    /// own view would be a client that can see through walls by asking politely. Instance isolation applies in
    /// every mode: a free focus is a bigger view of the world the client is already in, not a way out of it.
    /// </summary>
    public enum FocusMode : byte
    {
        /// <summary>
        /// The default. A hint is honored only from a client that has a pawn, and is clamped to
        /// <see cref="InterestSettings.HintMaxDistance"/> of it. A pawn-less client's hint is ignored, so
        /// connecting without a body is not a way to look anywhere in the world.
        /// </summary>
        PawnClamped = 0,
        /// <summary>
        /// The hint is taken as sent, anywhere in the world: an RTS or commander camera, a spectator, a replay
        /// viewer. A pawn-less client may hold this mode; the server is the one that decided so.
        /// </summary>
        Free = 1,
        /// <summary>Hints from this client are ignored altogether, pawn or no pawn.</summary>
        Disabled = 2,
    }

    /// <summary>
    /// What the server decided about one incoming focus hint: which mode to treat it in, and the point itself
    /// (a policy may move it — snap an RTS camera to the territory it is allowed to watch, say). A struct, so
    /// policy evaluation allocates no additional memory.
    /// </summary>
    public struct FocusHintDecision
    {
        /// <summary>How the point is treated. <see cref="FocusMode.Disabled"/> drops the hint entirely.</summary>
        public FocusMode Mode;
        /// <summary>The hinted point in absolute world coordinates, as the client sent it or as the policy moved it.</summary>
        public double X, Y, Z;

        /// <summary>Ignore this hint.</summary>
        public static FocusHintDecision Reject() => new FocusHintDecision { Mode = FocusMode.Disabled };

        /// <summary>Accept this point as sent, wherever it is (<see cref="FocusMode.Free"/>).</summary>
        public static FocusHintDecision Anywhere(double x, double y, double z) =>
            new FocusHintDecision { Mode = FocusMode.Free, X = x, Y = y, Z = z };

        /// <summary>Accept this point, clamped to <see cref="InterestSettings.HintMaxDistance"/> of the pawn.</summary>
        public static FocusHintDecision NearPawn(double x, double y, double z) =>
            new FocusHintDecision { Mode = FocusMode.PawnClamped, X = x, Y = y, Z = z };
    }

    /// <summary>
    /// The optional second half of <see cref="IInterestPolicy"/>: the game's say over one client's focus hint,
    /// asked on the gateway loop as the hint arrives and before anything is done with it. Implement it on the
    /// same object as the policy (the gateway looks for it there) when the per-client
    /// <c>NebulaGateway.SetClientFocusMode</c> setting is not enough — when whether a commander camera is
    /// allowed depends on where it is pointed, for instance.
    /// <para>
    /// It must not block or allocate: a client can send hints as fast as it likes, and only the gateway's rate
    /// limit stands in front of this.
    /// </para>
    /// </summary>
    public interface IFocusHintPolicy
    {
        /// <summary>
        /// Decide what to do with one hint. <paramref name="request"/> carries the raw point the client sent and
        /// the mode the server has set for this client; returning it unchanged keeps that decision. Return
        /// <see cref="FocusHintDecision.Reject"/> to ignore the hint.
        /// </summary>
        FocusHintDecision AuthorizeFocusHint(in InterestClient client, in FocusHintDecision request);
    }

    /// <summary>
    /// What a policy is told about the client it is deciding for. A snapshot: the gateway fills it once per
    /// evaluation, so a policy never walks live gateway state (and cannot keep a reference to it).
    /// </summary>
    public struct InterestClient
    {
        public ulong ClientId;
        /// <summary>The player's identity across sessions (<c>PlayerIdentity</c>): the authenticated subject, "" for none.</summary>
        public string Identity;
        public string Name;
        public ulong PawnNetId;
        public bool HasPawn;
        /// <summary>Absolute position of the pawn, resolved through any carrier. Meaningless when <see cref="HasPawn"/> is false.</summary>
        public double PawnX, PawnY, PawnZ;
        /// <summary>The outermost entity the pawn is riding in (a ship, a lift), or 0 when it stands in the world.</summary>
        public ulong PawnCarrierNetId;
        /// <summary>
        /// The client's simulation scope, resolved through the pawn's carrier chain; 0 is the public world, and
        /// so is a client with no pawn.
        /// </summary>
        public ulong InstanceId;
        /// <summary>A byte the game sets per client (<c>gateway.SetClientTag</c>): team, faction, whatever the policy needs.</summary>
        public byte Team;
        /// <summary>
        /// 64 free bits the game sets per client (<c>gateway.SetClientTags</c>): alliances, roles, subscriptions
        /// to a front. Server state, like <see cref="Team"/>; the client never sends it.
        /// </summary>
        public ulong Tags;
        /// <summary>A hint from the client survived validation (<see cref="FocusHintFilter"/>).</summary>
        public bool HasHint;
        /// <summary>The accepted hint in absolute world coordinates. Meaningless when <see cref="HasHint"/> is false.</summary>
        public double HintX, HintY, HintZ;
        /// <summary>What the server allows this client's hint to do (<c>gateway.SetClientFocusMode</c>).</summary>
        public FocusMode FocusMode;
        /// <summary>Shorthand for <see cref="FocusMode"/> being <see cref="Nebula.FocusMode.Free"/>: the hint was not clamped to the pawn.</summary>
        public bool FreeHint => FocusMode == Nebula.FocusMode.Free;
    }

    /// <summary>What a policy is told about the entity it is deciding on. A snapshot, like <see cref="InterestClient"/>.</summary>
    public struct InterestEntity
    {
        public ulong NetId;
        public ushort PrefabId;
        public ulong OwnerClientId;
        public ContainerRef Container;
        /// <summary>Absolute position, resolved through carriers.</summary>
        public double X, Y, Z;
        /// <summary>The prefab's relevance radius, 0 for the mesh default.</summary>
        public float RelevanceRadius;
        public bool AlwaysRelevant;
        /// <summary>
        /// The prefab's interest group: a game-defined bucket to filter on. Taken from the entity itself and
        /// not from its carrier, because a filter on "what kind of thing is this" is about the passenger, not
        /// about the ship it happens to be riding in.
        /// </summary>
        public byte InterestGroup;
        /// <summary>
        /// The entity's simulation scope, resolved through its carrier chain the same way authorization
        /// resolves it: 0 is the public world, anything else a private instance. An entity in a container the
        /// gateway cannot resolve reads 0 here and is refused by the instance rules before any policy sees it.
        /// </summary>
        public ulong InstanceId;
        /// <summary>The entity immediately carrying this one (0 = none); the interest decision is made on the root carrier.</summary>
        public ulong CarrierNetId;
    }

    /// <summary>
    /// What a policy fills in for one client: where it is looking, and any entities it must have whatever the
    /// distance. Reused between evaluations, so a policy that adds the same things every pass allocates nothing.
    /// </summary>
    public sealed class InterestQuery
    {
        private readonly List<InterestFocus> _foci = new List<InterestFocus>();
        private readonly List<ulong> _entities = new List<ulong>();

        /// <summary>Foci beyond this are dropped (<see cref="InterestSettings.MaxFoci"/>).</summary>
        public int MaxFoci { get; private set; } = InterestSettings.Default.MaxFoci;
        /// <summary>Explicit entities beyond this are dropped (<see cref="InterestSettings.MaxExplicitPerClient"/>).</summary>
        public int MaxEntities { get; private set; } = InterestSettings.Default.MaxExplicitPerClient;
        /// <summary>A policy asked for more than the caps allow; the gateway logs this once per client rather than growing the set.</summary>
        public bool Overflowed { get; private set; }
        /// <summary>
        /// A box focus was wider than <see cref="InterestSettings.MaxRadius"/> on some axis and was shrunk to it
        /// about its center. Everything downstream of a focus enumerates cells across it — the
        /// region subscription, the container window — so an unbounded box is an unbounded loop, and
        /// <c>MaxRadius</c> is already the mesh's answer to "how far may one thing reach".
        /// </summary>
        public bool BoxesClamped { get; private set; }

        public IReadOnlyList<InterestFocus> Foci => _foci;
        public IReadOnlyList<ulong> Entities => _entities;

        /// <summary>The widest half extent a box focus may have on one axis (<see cref="InterestSettings.MaxRadius"/>).</summary>
        public double MaxBoxHalf { get; private set; } = InterestSettings.Default.MaxRadius;

        /// <summary>Empty the query for a new evaluation and apply the caps that are in force.</summary>
        public void Reset(in InterestSettings settings)
        {
            _foci.Clear();
            _entities.Clear();
            MaxFoci = Math.Max(1, settings.MaxFoci);
            MaxEntities = Math.Max(0, settings.MaxExplicitPerClient);
            MaxBoxHalf = Math.Max(1, settings.MaxRadius);
            Overflowed = false;
            BoxesClamped = false;
        }

        public bool AddFocus(in InterestFocus focus)
        {
            if (double.IsNaN(focus.X) || double.IsNaN(focus.Y) || double.IsNaN(focus.Z) ||
                double.IsInfinity(focus.X) || double.IsInfinity(focus.Y) || double.IsInfinity(focus.Z)) return false;
            if (_foci.Count >= MaxFoci) { Overflowed = true; return false; }
            var clamped = focus;
            // A box is a range everything downstream walks cell by cell; a non-finite or enormous one is an
            // unbounded loop on the gateway's own thread, so it is shrunk about its center and reported.
            if (Clamp(ref clamped.HalfX) | Clamp(ref clamped.HalfY) | Clamp(ref clamped.HalfZ)) BoxesClamped = true;
            _foci.Add(clamped);
            return true;
        }

        private bool Clamp(ref double half)
        {
            if (double.IsNaN(half) || half < 0) { half = 0; return false; }
            if (half <= MaxBoxHalf) return false;
            half = MaxBoxHalf;
            return true;
        }

        public bool AddFocus(double x, double y, double z, float radiusScale = 1f, ulong sourceNetId = 0) =>
            AddFocus(InterestFocus.Point(x, y, z, radiusScale, sourceNetId));

        /// <summary>Always send this entity to the client (a party member, a quest target), wherever it is.</summary>
        public bool AddEntity(ulong netId)
        {
            if (netId == 0) return false;
            // A repeated id is the same subscription, so it must not consume another slot of the cap.
            if (_entities.Contains(netId)) return true;
            if (_entities.Count >= MaxEntities) { Overflowed = true; return false; }
            _entities.Add(netId);
            return true;
        }
    }

    /// <summary>
    /// The game's say in what a client hears about. Both methods are called by the gateway, per client, at
    /// <see cref="InterestSettings.EvalHz"/>; neither may block or allocate per call if the mesh is to stay quiet.
    /// </summary>
    public interface IInterestPolicy
    {
        /// <summary>Fill the foci and explicit entities for this client.</summary>
        void Collect(in InterestClient client, InterestQuery query);

        /// <summary>
        /// The security filter, asked before an entity may enter or stay in the set (team, fog of war). Returning
        /// false removes the entity at once, with no linger: this is a boundary, not a distance.
        /// </summary>
        bool Authorize(in InterestClient client, in InterestEntity entity);
    }

    /// <summary>
    /// What a mesh does without a game policy: one focus at the pawn, plus the client's validated hint as a
    /// second focus when it has sent one. A client with no pawn gets no focus, and so hears only about globally
    /// relevant entities; a game that wants spectators gives them a focus in its own policy.
    /// </summary>
    public sealed class DefaultInterestPolicy : IInterestPolicy
    {
        public static readonly DefaultInterestPolicy Instance = new DefaultInterestPolicy();

        public void Collect(in InterestClient client, InterestQuery query)
        {
            if (client.HasPawn) query.AddFocus(client.PawnX, client.PawnY, client.PawnZ, 1f, client.PawnNetId);
            if (client.HasHint) query.AddFocus(client.HintX, client.HintY, client.HintZ);
        }

        public bool Authorize(in InterestClient client, in InterestEntity entity) => true;
    }

    /// <summary>Combining policies, so a game can add one concern (parties) without rewriting another (fog of war).</summary>
    public static class InterestPolicies
    {
        /// <summary>
        /// Union of the foci and explicit entities, and the AND of the authorizations: a policy can only ever
        /// take an entity away from a client, never grant one another policy refused.
        /// </summary>
        public static IInterestPolicy Combine(IInterestPolicy a, IInterestPolicy b) =>
            a == null ? b : b == null ? a : new Combined(a, b);

        public static IInterestPolicy Combine(params IInterestPolicy[] policies)
        {
            IInterestPolicy result = null;
            if (policies != null) foreach (var policy in policies) result = Combine(result, policy);
            return result;
        }

        private sealed class Combined : IInterestPolicy, IFocusHintPolicy
        {
            private readonly IInterestPolicy _a, _b;
            public Combined(IInterestPolicy a, IInterestPolicy b) { _a = a; _b = b; }
            public void Collect(in InterestClient client, InterestQuery query) { _a.Collect(client, query); _b.Collect(client, query); }
            public bool Authorize(in InterestClient client, in InterestEntity entity) => _a.Authorize(client, entity) && _b.Authorize(client, entity);

            /// <summary>
            /// Focus-hint decisions chain rather than intersect: the second policy is asked about what the first
            /// decided, so the later one in the <see cref="Combine"/> order has the last word. A point is not a
            /// permission that can be ANDed, and pretending it is would silently drop one policy's adjustment.
            /// </summary>
            public FocusHintDecision AuthorizeFocusHint(in InterestClient client, in FocusHintDecision request)
            {
                var decision = _a is IFocusHintPolicy first ? first.AuthorizeFocusHint(client, request) : request;
                return _b is IFocusHintPolicy second ? second.AuthorizeFocusHint(client, decision) : decision;
            }
        }
    }

    /// <summary>
    /// Validation of <see cref="ClientFocusHintMsg"/>, on the gateway, before a policy ever sees a hint. A hint
    /// is an input and never authority: a client that sends a point on the other side of the map, or a thousand
    /// points a second, must not be able to make the gateway stream it the world. Malformed values are dropped
    /// (<see cref="Malformed"/>: NaN, infinity, or a magnitude beyond <see cref="MaxMagnitude"/>), the rate of
    /// <i>attempts</i> is capped at <see cref="InterestSettings.HintMaxHz"/> before any game code runs
    /// (<see cref="ThrottledAttempt"/>) and the rate of accepted hints with it, and what the point may then do is decided
    /// by the server's <see cref="FocusMode"/> for that client — clamped to
    /// <see cref="InterestSettings.HintMaxDistance"/> of the pawn by default, refused outright when there is no
    /// pawn to clamp to, and taken as sent only where the server said so.
    /// </summary>
    public sealed class FocusHintFilter
    {
        private readonly Dictionary<ulong, double> _lastAccepted = new Dictionary<ulong, double>();
        /// <summary>
        /// When each client last <i>attempted</i> a hint, accepted or not. Kept apart from
        /// <see cref="_lastAccepted"/> because the two answer different questions: the accepted clock is what
        /// paces a working camera, and the attempt clock is what stops a client that is being refused — no pawn,
        /// hints disabled, a policy saying no — from paying nothing for the attempt and so getting an unlimited
        /// path into game code.
        /// </summary>
        private readonly Dictionary<ulong, double> _lastAttempt = new Dictionary<ulong, double>();

        /// <summary>
        /// The largest absolute coordinate a hint may name, in meters. Anything beyond it is malformed rather
        /// than merely far away: the region grid can only pack <see cref="InterestGrid.MaxCoordinate"/> cells
        /// per axis (about 6.7e7 m at the default 64 m edge), so every point out here is the same clamped
        /// region, and a value of 1e300 is a number no camera produced. Rejecting rather than clamping is
        /// deliberate: clamping would invent a place the client never asked about and then stream it.
        /// </summary>
        public const double MaxMagnitude = 1e9;

        public InterestSettings Settings = InterestSettings.Default;
        /// <summary>Hints dropped since the last reset, by reason. Reported through the gateway's focus-hint counters.</summary>
        public long DroppedNonFinite, DroppedOutOfRange, DroppedRate, DroppedUnauthorized, Clamped, Accepted;

        /// <summary>Malformed hints of either kind: NaN or infinity, plus points beyond <see cref="MaxMagnitude"/>.</summary>
        public long DroppedMalformed => DroppedNonFinite + DroppedOutOfRange;

        /// <summary>
        /// Whether a point is a number the rest of the pipeline may touch. Counted as it is rejected, so the
        /// gateway can call this before anything else — before the client snapshot and before the game's
        /// <see cref="IFocusHintPolicy"/> — and no NaN ever reaches game code.
        /// </summary>
        public bool Malformed(double x, double y, double z)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z) ||
                double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z))
            {
                DroppedNonFinite++;
                return true;
            }
            if (Math.Abs(x) > MaxMagnitude || Math.Abs(y) > MaxMagnitude || Math.Abs(z) > MaxMagnitude)
            {
                DroppedOutOfRange++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Spend this client's <b>attempt</b> budget: true when a hint arriving now is over
        /// <see cref="InterestSettings.HintMaxHz"/> and must be dropped before any further work, false when it
        /// may proceed — and in that case the attempt is recorded, whatever becomes of the hint afterwards.
        /// <para>
        /// This is the gate that stands in front of the game's <see cref="IFocusHintPolicy"/>. <see cref="Throttled"/>
        /// only ever knew about hints that were <i>accepted</i>, so a client with no pawn, with hints disabled,
        /// or whose every hint a policy refuses spent nothing and could call into the policy once per packet.
        /// </para>
        /// </summary>
        public bool ThrottledAttempt(ulong clientId, double now)
        {
            double minInterval = Settings.HintMaxHz > 0 ? 1.0 / Settings.HintMaxHz : double.PositiveInfinity;
            if (_lastAttempt.TryGetValue(clientId, out double last) && now - last < minInterval)
            {
                DroppedRate++;
                return true;
            }
            _lastAttempt[clientId] = now;
            return false;
        }

        /// <summary>
        /// Forget both budgets for one client, so its next hint is judged afresh. The gateway calls it when the
        /// <b>server</b> changed what the client is allowed — a new <see cref="FocusMode"/>, new tags — because
        /// the client cannot know it has just been authorized, and making it wait out a budget it spent while
        /// being refused would leave a freshly granted commander camera dead for a fraction of a second. It
        /// opens nothing: the budget is reset by a server-side decision, never by anything a client sends.
        /// </summary>
        public void ResetBudget(ulong clientId)
        {
            _lastAttempt.Remove(clientId);
            _lastAccepted.Remove(clientId);
        }

        /// <summary>
        /// Whether a hint arriving now would be dropped because one was <i>accepted</i> too recently, without
        /// recording anything. <see cref="ThrottledAttempt"/> is the gate that protects extension code; this one
        /// paces the accepted hint itself and is what <see cref="TryAccept"/> checks.
        /// </summary>
        public bool Throttled(ulong clientId, double now)
        {
            double minInterval = Settings.HintMaxHz > 0 ? 1.0 / Settings.HintMaxHz : double.PositiveInfinity;
            if (!_lastAccepted.TryGetValue(clientId, out double last) || now - last >= minInterval) return false;
            DroppedRate++;
            return true;
        }

        /// <summary>
        /// Whether a hint is worth anything at all in this mode. A client with no pawn has nothing to clamp to,
        /// so the default mode has no safe answer for it: the hint is refused rather than granted the whole
        /// world. A spectator gets <see cref="FocusMode.Free"/> from the server, which is a decision somebody
        /// made, not one the client made by omitting a body.
        /// </summary>
        public bool Authorizes(FocusMode mode, bool hasPawn)
        {
            if (mode == FocusMode.Free || (mode != FocusMode.Disabled && hasPawn)) return true;
            DroppedUnauthorized++;
            return false;
        }

        /// <summary>
        /// Validate one hint under the mode the server decided for this client. Returns false when it must be
        /// ignored entirely; otherwise the (possibly clamped) absolute point comes back in the out parameters.
        /// </summary>
        public bool TryAccept(ulong clientId, double now, double x, double y, double z, FocusMode mode,
            bool hasPawn, double pawnX, double pawnY, double pawnZ, out double outX, out double outY, out double outZ)
        {
            outX = pawnX; outY = pawnY; outZ = pawnZ;
            if (Malformed(x, y, z)) return false;
            if (!Authorizes(mode, hasPawn)) return false;
            if (Throttled(clientId, now)) return false;
            _lastAccepted[clientId] = now;
            outX = x; outY = y; outZ = z;
            if (mode != FocusMode.Free)
            {
                double max = Math.Max(0, Settings.HintMaxDistance);
                double dx = x - pawnX, dy = y - pawnY, dz = z - pawnZ;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > max * max)
                {
                    double d = Math.Sqrt(d2);
                    double scale = d > 0 ? max / d : 0;
                    outX = pawnX + dx * scale;
                    outY = pawnY + dy * scale;
                    outZ = pawnZ + dz * scale;
                    Clamped++;
                }
            }
            Accepted++;
            return true;
        }

        /// <summary>Drop a disconnected client's rate-limit state (both budgets).</summary>
        public void Forget(ulong clientId)
        {
            _lastAccepted.Remove(clientId);
            _lastAttempt.Remove(clientId);
        }

        public void Clear()
        {
            _lastAccepted.Clear();
            _lastAttempt.Clear();
            DroppedNonFinite = DroppedOutOfRange = DroppedRate = DroppedUnauthorized = Clamped = Accepted = 0;
        }
    }
}
