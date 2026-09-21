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

        /// <summary>A box focus, from its centre and full size.</summary>
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
    /// What a policy is told about the client it is deciding for. A snapshot: the gateway fills it once per
    /// evaluation, so a policy never walks live gateway state (and cannot keep a reference to it).
    /// </summary>
    public struct InterestClient
    {
        public ulong ClientId;
        /// <summary>The player's identity across sessions (<c>PlayerIdentity</c>).</summary>
        public string Identity;
        public string Name;
        public ulong PawnNetId;
        public bool HasPawn;
        /// <summary>Absolute position of the pawn, resolved through any carrier. Meaningless when <see cref="HasPawn"/> is false.</summary>
        public double PawnX, PawnY, PawnZ;
        /// <summary>The client's simulation scope; 0 is the public world.</summary>
        public ulong InstanceId;
        /// <summary>A byte the game sets per client (<c>gateway.SetClientTag</c>): team, faction, whatever the policy needs.</summary>
        public byte Team;
        /// <summary>A hint from the client survived validation (<see cref="FocusHintFilter"/>).</summary>
        public bool HasHint;
        public double HintX, HintY, HintZ;
        /// <summary>The policy accepted free foci for this client, so the hint was not clamped to the pawn.</summary>
        public bool FreeHint;
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
        /// <summary>The prefab's interest group: a game-defined bucket to filter on.</summary>
        public byte InterestGroup;
        public ulong InstanceId;
        /// <summary>The entity carrying this one (0 = none); the interest decision is made on the root carrier.</summary>
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

        public IReadOnlyList<InterestFocus> Foci => _foci;
        public IReadOnlyList<ulong> Entities => _entities;

        /// <summary>Empty the query for a new evaluation and apply the caps that are in force.</summary>
        public void Reset(in InterestSettings settings)
        {
            _foci.Clear();
            _entities.Clear();
            MaxFoci = Math.Max(1, settings.MaxFoci);
            MaxEntities = Math.Max(0, settings.MaxExplicitPerClient);
            Overflowed = false;
        }

        public bool AddFocus(in InterestFocus focus)
        {
            if (double.IsNaN(focus.X) || double.IsNaN(focus.Y) || double.IsNaN(focus.Z) ||
                double.IsInfinity(focus.X) || double.IsInfinity(focus.Y) || double.IsInfinity(focus.Z)) return false;
            if (_foci.Count >= MaxFoci) { Overflowed = true; return false; }
            _foci.Add(focus);
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

        private sealed class Combined : IInterestPolicy
        {
            private readonly IInterestPolicy _a, _b;
            public Combined(IInterestPolicy a, IInterestPolicy b) { _a = a; _b = b; }
            public void Collect(in InterestClient client, InterestQuery query) { _a.Collect(client, query); _b.Collect(client, query); }
            public bool Authorize(in InterestClient client, in InterestEntity entity) => _a.Authorize(client, entity) && _b.Authorize(client, entity);
        }
    }

    /// <summary>
    /// Validation of <see cref="ClientFocusHintMsg"/>, on the gateway, before a policy ever sees a hint. A hint
    /// is an input and never authority: a client that sends a point on the other side of the map, or a thousand
    /// points a second, must not be able to make the gateway stream it the world. Non-finite values are dropped,
    /// the rate is capped at <see cref="InterestSettings.HintMaxHz"/>, and the point is clamped to
    /// <see cref="InterestSettings.HintMaxDistance"/> from the pawn unless the policy allows free foci.
    /// </summary>
    public sealed class FocusHintFilter
    {
        private readonly Dictionary<ulong, double> _lastAccepted = new Dictionary<ulong, double>();

        public InterestSettings Settings = InterestSettings.Default;
        /// <summary>Hints dropped since the last reset, by reason. Reported in the gateway's stats.</summary>
        public long DroppedNonFinite, DroppedRate, Clamped, Accepted;

        /// <summary>
        /// Validate one hint. Returns false when it must be ignored entirely; otherwise the (possibly clamped)
        /// point comes back in the out parameters.
        /// </summary>
        public bool TryAccept(ulong clientId, double now, double x, double y, double z, bool hasPawn, double pawnX, double pawnY, double pawnZ,
            out double outX, out double outY, out double outZ, bool freeHint = false)
        {
            outX = pawnX; outY = pawnY; outZ = pawnZ;
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z) || double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z))
            {
                DroppedNonFinite++;
                return false;
            }
            double minInterval = Settings.HintMaxHz > 0 ? 1.0 / Settings.HintMaxHz : double.PositiveInfinity;
            if (_lastAccepted.TryGetValue(clientId, out double last) && now - last < minInterval)
            {
                DroppedRate++;
                return false;
            }
            _lastAccepted[clientId] = now;
            outX = x; outY = y; outZ = z;
            if (!freeHint && hasPawn)
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

        /// <summary>Drop a disconnected client's rate-limit state.</summary>
        public void Forget(ulong clientId) => _lastAccepted.Remove(clientId);

        public void Clear()
        {
            _lastAccepted.Clear();
            DroppedNonFinite = DroppedRate = Clamped = Accepted = 0;
        }
    }
}
