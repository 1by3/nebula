using System;
using System.Collections.Generic;
using System.Diagnostics;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// Why a gateway holds a link to a worker. A link with no reason left is dropped after
    /// <see cref="InterestSettings.LinkLingerSeconds"/>; the reasons are reported in
    /// <see cref="GatewayStats.WorkerLinkReasons"/> because "why am I talking to 40 workers" is the first
    /// question anyone asks of a mesh that is not scoping.
    /// </summary>
    [Flags]
    public enum InterestLinkReason : byte
    {
        None = 0,
        /// <summary>It owns a container overlapping a region some client needs.</summary>
        Region = 1,
        /// <summary>It owns a container within <see cref="InterestSettings.MaxRadius"/> of a focus, so its wide entities could reach us.</summary>
        Foci = 2,
        /// <summary>Its heartbeat says it holds always-relevant entities.</summary>
        Global = 4,
        /// <summary>A policy asked for an entity by id and we do not know yet who owns it.</summary>
        Explicit = 8,
        /// <summary>A client of ours is waiting for a pawn and this worker owns somewhere to put it.</summary>
        Spawn = 16,
        /// <summary>It is authoritative for an entity one of our clients owns.</summary>
        Owned = 32,
    }

    public sealed partial class NebulaGateway
    {
        /// <summary>
        /// The gateway's end of one worker link. The subscription state machine is the shared
        /// <see cref="RegionSubscriptionSender"/>, so the messages a gateway sends and the set a worker keeps
        /// cannot drift apart over a bug in one of two implementations.
        /// </summary>
        private sealed class WorkerLink
        {
            public string WorkerId = "";
            public ushort Index = ushort.MaxValue;
            public RegionSubscriptionSender Sub;
            public InterestLinkReason Reasons;
            /// <summary>When the last reason went away; the link is dropped <see cref="InterestSettings.LinkLingerSeconds"/> later.</summary>
            public double UnneededSince = double.PositiveInfinity;
            /// <summary>The worker incarnation the subscription was built against; a change forces a Full snapshot.</summary>
            public uint Incarnation;
            public bool Linked;
        }

        /// <summary>
        /// The bridge between the gateway's entity records and the shared evaluation in
        /// <see cref="ClientInterest{T,TSource}"/>. A struct, so an evaluation of thousands of candidates makes no
        /// virtual call and no allocation.
        /// </summary>
        private readonly struct InterestSource : IInterestSource<EntityRecord>
        {
            private readonly NebulaGateway _gateway;
            public InterestSource(NebulaGateway gateway) { _gateway = gateway; }

            public void Describe(ulong id, in EntityRecord value, out InterestEntity entity)
            {
                var root = _gateway.RootOf(value);
                entity = new InterestEntity
                {
                    NetId = id,
                    PrefabId = value.LastSpawn.PrefabId,
                    OwnerClientId = value.OwnerClientId,
                    Container = value.Container,
                    // The decision is made on the root carrier: a ship and everything riding in it enter and
                    // leave as one unit (design D3).
                    X = root.AbsX, Y = root.AbsY, Z = root.AbsZ,
                    // Radius and always-relevance come from the root carrier too, not just the position: design
                    // D3 says a carrier and its contents enter and leave as one unit, and a passenger judged at
                    // its own (smaller or larger) radius would cross the boundary on its own tick instead.
                    RelevanceRadius = root.RelevanceRadius,
                    AlwaysRelevant = root.AlwaysRelevant,
                    InterestGroup = value.InterestGroup,
                    CarrierNetId = value.Container.IsDynamic ? value.Container.NetId : 0,
                    // The scope is resolved through the whole carrier chain, exactly as CanObserve resolves it:
                    // a crate in a ship in a private instance is in that instance, and a policy that filtered on
                    // a zero here would have been filtering on "the public world" for everything carried.
                    InstanceId = _gateway.ScopeContainer(value.Container)?.InstanceId ?? 0,
                };
            }

            public bool Authorize(in InterestClient client, in InterestEntity entity) => _gateway.AuthorizeEntity(client, entity);
        }

        // ------------------------------------------------------------------------------------------- state

        private InterestSettings _interest = InterestSettings.Default;
        private InterestGrid _interestGrid = InterestGrid.Resolve(InterestSettings.Default);
        private readonly InterestIndex<EntityRecord> _index = new InterestIndex<EntityRecord>();
        /// <summary>Region → the clients whose subscribe discs cover it, so an arriving entity is tested against those and nobody else.</summary>
        private readonly Dictionary<ulong, List<ClientConn>> _regionClients = new Dictionary<ulong, List<ClientConn>>();
        private readonly Dictionary<string, WorkerLink> _links = new Dictionary<string, WorkerLink>();
        private readonly FocusHintFilter _hintFilter = new FocusHintFilter();
        private IInterestPolicy _policy = DefaultInterestPolicy.Instance;
        /// <summary><see cref="_policy"/> when it also decides focus hints; resolved once per assignment, not per hint.</summary>
        private IFocusHintPolicy _hintPolicy;
        /// <summary>Whose turn it is to be evaluated: routine work spread over ticks, dirty clients first.</summary>
        private readonly InterestSchedule _schedule = new InterestSchedule();
        private readonly List<ulong> _dueClients = new List<ulong>();
        /// <summary>Cached so the per-tick dirty scan does not allocate a delegate.</summary>
        private Func<ulong, bool> _isClientDirty;
        private double _lastInterestTickAt = -1;

        /// <summary>Resolved once per control-plane change: which workers a region's entities could live on.</summary>
        private readonly Dictionary<ulong, List<string>> _regionWorkers = new Dictionary<ulong, List<string>>();
        /// <summary>
        /// Salted region key → the scope it belongs to (<see cref="RegionKeys"/>). A key is opaque, and resolving
        /// one to its owning workers needs both its coordinates and the scope to ask the registry in, so the pair
        /// is recorded where the key is made (<c>docs/scope-frames.md</c> D7). Entries are never removed: the map
        /// is bounded by the regions this gateway's clients have covered, exactly like <see cref="_regionWorkers"/>.
        /// </summary>
        private readonly Dictionary<ulong, ulong> _regionScope = new Dictionary<ulong, ulong>();
        /// <summary>Regions at least one link subscribes; a record outside every one of them is evicted.</summary>
        private readonly HashSet<ulong> _subscribedRegions = new HashSet<ulong>();
        private readonly Dictionary<string, ContainerOwnershipEntry> _ownershipById = new Dictionary<string, ContainerOwnershipEntry>();

        // Scratch. Interest runs several times a second over everything near every client, so none of it allocates.
        private readonly List<ulong> _entered = new List<ulong>();
        private readonly List<ulong> _leftIds = new List<ulong>();
        private readonly List<ulong> _regionScratch = new List<ulong>();
        private readonly List<ulong> _fociRegions = new List<ulong>();
        private readonly List<ulong> _explicitIds = new List<ulong>();
        private readonly List<ulong> _alwaysScratch = new List<ulong>();
        private readonly List<Container> _containerScratch = new List<Container>();
        private readonly List<InterestSubscribeMsg> _subOutbox = new List<InterestSubscribeMsg>();
        private readonly List<string> _linkScratch = new List<string>();
        private readonly List<ulong> _evictScratch = new List<ulong>();
        /// <summary>A carrier's subtree while its records' cached placement is brought in line with the index.</summary>
        private readonly List<ulong> _placementScratch = new List<ulong>();
        /// <summary>The same walk for the "who should hear about this now" pass; never the placement list, which may be in use.</summary>
        private readonly List<ulong> _arrivedScratch = new List<ulong>();
        /// <summary>Passengers of a carrier being taken out of the index, so their own placement can be read back.</summary>
        private readonly List<ulong> _orphanScratch = new List<ulong>();
        /// <summary>Entities already reported for a refused carrier link, so a cycle is logged once and not per tick.</summary>
        private readonly HashSet<ulong> _carrierCycleWarned = new HashSet<ulong>();
        /// <summary>The revocation pass has scratch of its own: it can run inside a tick, between evaluations.</summary>
        private readonly List<ulong> _revokeLeft = new List<ulong>();
        /// <summary>Never written to; <see cref="ApplyInterestChanges"/> wants an "entered" list and a revocation has none.</summary>
        private readonly List<ulong> _noEntered = new List<ulong>();
        private readonly List<ClientConn> _revalidateScratch = new List<ClientConn>();
        private bool _revalidating;
        private readonly List<string> _containerIdScratch = new List<string>();
        private readonly List<string> _preparedScratch = new List<string>();
        /// <summary>The same ids as a set: foci overlap, and a linear scan of a few hundred rows per focus is not free.</summary>
        private readonly HashSet<string> _containerIdSeen = new HashSet<string>();
        private readonly List<ContainerOwnershipEntry> _upsertScratch = new List<ContainerOwnershipEntry>();
        private readonly List<string> _removeScratch = new List<string>();
        private readonly NetworkWriter _interestWriter = new NetworkWriter(1024);

        /// <summary>
        /// Seconds the gateway spends asking every worker for a client's pawn by name before deciding it is
        /// really gone and starting the join over. Long enough for a dial, a Hello and a subscription round
        /// trip on a busy mesh; short enough that a player is not left watching an empty world.
        /// </summary>
        private const double PawnRecoverySeconds = 5;

        /// <summary>
        /// How long an owner that only an <see cref="EntityRedirectMsg"/> has named may stay unconfirmed before
        /// the gateway treats the pawn as lost and starts asking for it by name. A handover confirms itself in
        /// a round trip; anything slower than this is the chain that outran the dialling.
        /// </summary>
        private const double PawnHandoverGraceSeconds = 0.75;

        private double _nextSubscriptionAt;
        private long _spawnsSent, _despawnsSent;
        private long _containerBudgetHits;
        private double _evalMsSum, _evalMsMax;
        private long _evalCount;
        private bool _subscriptionsDirty = true;

        /// <summary>
        /// The game's say in what each client hears about. Defaults to
        /// <see cref="DefaultInterestPolicy"/>: one focus at the pawn plus the client's validated hint. Compose
        /// several with <see cref="InterestPolicies.Combine"/>. If the policy also implements
        /// <see cref="IFocusHintPolicy"/> it gets the say over focus hints too.
        /// <para>
        /// Install it before the gateway's first tick — a policy is a security filter, and one installed while
        /// clients are already connected has not been asked about what they can already see. Assigning it is
        /// the next best thing, and it fails closed: every client's current set is re-authorized against the
        /// new policy <b>synchronously, inside this assignment</b> (<see cref="RevalidateAllInterest"/>), so
        /// nothing the new policy refuses is relayed to anybody afterwards. What the new policy newly allows
        /// arrives at the clients' next evaluations, which are still spread over the following ticks.
        /// </para>
        /// </summary>
        public IInterestPolicy InterestPolicy
        {
            get => _policy;
            set
            {
                _policy = value ?? DefaultInterestPolicy.Instance;
                _hintPolicy = _policy as IFocusHintPolicy;
                RevalidateAllInterest();
            }
        }

        /// <summary>Focus hints from clients that survived validation and moved a set (<see cref="FocusHintFilter"/>).</summary>
        public long FocusHintsAccepted => _hintFilter.Accepted;
        /// <summary>Accepted hints that were pulled back to <see cref="InterestSettings.HintMaxDistance"/> of the pawn.</summary>
        public long FocusHintsClamped => _hintFilter.Clamped;
        /// <summary>
        /// Hints dropped for being over <see cref="InterestSettings.HintMaxHz"/>. Counted per <i>attempt</i>, so
        /// a client whose hints are all refused for some other reason still shows up here rather than nowhere.
        /// </summary>
        public long FocusHintsDroppedByRate => _hintFilter.DroppedRate;
        /// <summary>Hints dropped for being NaN, infinite, or further out than any world (<see cref="FocusHintFilter.MaxMagnitude"/>).</summary>
        public long FocusHintsDroppedMalformed => _hintFilter.DroppedMalformed;
        /// <summary>
        /// Hints dropped because this client may not have one: no pawn to clamp to in
        /// <see cref="FocusMode.PawnClamped"/>, <see cref="FocusMode.Disabled"/>, or an
        /// <see cref="IFocusHintPolicy"/> that refused it.
        /// </summary>
        public long FocusHintsDroppedUnauthorized => _hintFilter.DroppedUnauthorized;

        /// <summary>The resolved interest knobs this gateway runs on (clamped; see <see cref="InterestSettings.Validate"/>).</summary>
        public InterestSettings InterestSettingsInUse => _interest;
        /// <summary>The region grid both ends must agree on; it travels in every subscription message.</summary>
        public InterestGrid InterestGridInUse => _interestGrid;
        /// <summary>Entity records cached: what this gateway's clients' subscriptions bring in, not the world.</summary>
        public int CachedEntityCount => _entities.Count;

        /// <summary>
        /// Whether this gateway holds a record for one entity. The count above says how much is cached; this says
        /// <i>what</i>, which is the only way to tell "never sent here" from "sent and filtered out per client" —
        /// a distinction a client-side set cannot answer.
        /// </summary>
        public bool IsEntityCached(ulong netId) => _entities.ContainsKey(netId);

        /// <summary>
        /// Where this gateway currently has an entity bucketed: its <i>effective</i> placement, which for a
        /// carried entity is its root carrier's and not what its own prefab asked for. For tests
        /// and for the debug tooling; false when no record is held.
        /// </summary>
        public bool TryGetCachedPlacement(ulong netId, out InterestPlacement placement, out ulong region)
        {
            if (_entities.TryGetValue(netId, out var rec)) { placement = rec.Placement; region = rec.Region; return true; }
            placement = InterestPlacement.Region;
            region = 0;
            return false;
        }
        /// <summary>Distinct regions subscribed across every worker link.</summary>
        public int SubscribedRegionCount => _subscribedRegions.Count;
        /// <summary>Worker links currently held, for a test or an operator that wants the number without the heartbeat.</summary>
        public int WorkerLinkCount => _links.Count;

        /// <summary>
        /// Most container rows one of this gateway's clients may be told about in one evaluation
        /// (<see cref="InterestSettings.MaxContainerRows"/> at the world's cell size). Derived from the interest
        /// settings, not configured separately.
        /// </summary>
        public int ContainerRowCap => _interest.MaxContainerRows(Config.ResolveWorldCellSize());

        /// <summary>
        /// Evaluations in which a client's foci asked for more container rows than <see cref="ContainerRowCap"/>
        /// allows, so the far ones were withheld. A number that keeps climbing means a policy is handing out
        /// more or wider foci than the mesh is sized for; it is never a correctness problem, because the rows
        /// entities stand in are collected before the cap applies.
        /// </summary>
        public long ContainerBudgetHits => _containerBudgetHits;

        /// <summary>
        /// A byte the game attaches to a client for its policy to filter on (team, faction, party). It is server
        /// state: the client never sends it, which is what makes a team filter a security boundary rather than a
        /// suggestion. Changing it revokes at once and reveals shortly after: everything the policy no longer
        /// authorizes under the new tag is despawned inside this call (<see cref="RevalidateInterest"/>), and
        /// what the new tag newly allows arrives at the client's next evaluation.
        /// </summary>
        public void SetClientTag(ulong clientId, byte team)
        {
            if (!_clientsById.TryGetValue(clientId, out var c) || c.Team == team) return;
            c.Team = team;
            // The server has just changed what this client is allowed; its hint budget should not still be
            // spent on the refusals it collected under the old tag.
            _hintFilter.ResetBudget(clientId);
            RevalidateInterest(c);
        }

        /// <summary>What <see cref="SetClientTag"/> last set for this client (0 by default).</summary>
        public byte GetClientTag(ulong clientId) => _clientsById.TryGetValue(clientId, out var c) ? c.Team : (byte)0;

        /// <summary>
        /// Sixty-four more bits of the same thing (<see cref="InterestClient.Tags"/>): alliances a faction is in,
        /// roles a player holds, fronts a commander is watching — anything a policy wants to test that does not
        /// fit in <see cref="SetClientTag"/>'s single byte. Server state, like the tag, and with the same
        /// timing: what the new tags no longer authorize is despawned inside this call, and what they newly
        /// reveal arrives at the client's next evaluation.
        /// </summary>
        public void SetClientTags(ulong clientId, ulong tags)
        {
            if (!_clientsById.TryGetValue(clientId, out var c) || c.Tags == tags) return;
            c.Tags = tags;
            _hintFilter.ResetBudget(clientId);
            RevalidateInterest(c);
        }

        /// <summary>What <see cref="SetClientTags"/> last set for this client (0 by default).</summary>
        public ulong GetClientTags(ulong clientId) => _clientsById.TryGetValue(clientId, out var c) ? c.Tags : 0;

        /// <summary>
        /// How far this client's own focus hint may move its interest. The default is
        /// <see cref="FocusMode.PawnClamped"/>, so a hint is worth at most
        /// <see cref="InterestSettings.HintMaxDistance"/> of the pawn and a client with no pawn has no hint at
        /// all; <see cref="FocusMode.Free"/> is what gives a strategy camera or a spectator the run of the world.
        /// Only the server may set it — the client cannot ask for it, and cannot tell that it has it except by
        /// what it is sent.
        /// <para>
        /// Instance isolation is unaffected in every mode: a free focus is a wider view of the world the client
        /// is already in, never a way into another one.
        /// </para>
        /// Changing the mode drops the hint the gateway is holding, because a hint accepted under one mode is
        /// not an input the next one ever validated; the client's next hint (within 1/<see
        /// cref="InterestSettings.HintMaxHz"/> s) is judged afresh.
        /// </summary>
        public void SetClientFocusMode(ulong clientId, FocusMode mode)
        {
            if (!_clientsById.TryGetValue(clientId, out var c) || c.FocusMode == mode) return;
            c.FocusMode = mode;
            c.HasHint = false;
            // A client the server has just freed cannot know it, and would otherwise have to wait out the
            // attempt budget it spent while its hints were being refused before its camera answered.
            _hintFilter.ResetBudget(clientId);
            // Dropping the hint only narrows the foci, which is a distance and not a boundary: the entities it
            // was holding leave through the usual hysteresis at the next evaluation.
            c.InterestDirty = true;
        }

        /// <summary>What <see cref="SetClientFocusMode"/> last set for this client (<see cref="FocusMode.PawnClamped"/> by default).</summary>
        public FocusMode GetClientFocusMode(ulong clientId) =>
            _clientsById.TryGetValue(clientId, out var c) ? c.FocusMode : FocusMode.PawnClamped;

        /// <summary>
        /// Put this client at the front of the evaluation queue: a <b>reveal</b>, and additive work generally
        /// (a party joined, a unit was given to the player, a fog cell was uncovered). The gateway calls it
        /// itself when the pawn, instance, carrier, focus mode or focus region changed.
        /// <para>
        /// The timing is honest about what it is: dirty clients jump the routine rotation but are still
        /// bounded per tick (<see cref="InterestSchedule.MinDirtyPerTick"/>,
        /// <see cref="InterestSchedule.DirtyBurst"/>), so on a busy gateway a client may wait a few ticks for
        /// its turn. That is the right trade for showing something <i>more</i>, and the wrong one for taking
        /// something away: for a change that <b>tightens</b> what may be seen call
        /// <see cref="RevalidateInterest"/>, which revokes before this call would even have run.
        /// </para>
        /// </summary>
        public void MarkInterestDirty(ulong clientId)
        {
            if (_clientsById.TryGetValue(clientId, out var c)) c.InterestDirty = true;
        }

        /// <summary>
        /// The same for every client: a world-wide reveal. The evaluations are spread over the following ticks
        /// rather than all run on the next one — see <see cref="InterestSchedule"/> — so this is cheap to call
        /// on a live gateway, and for the same reason it is <b>not</b> how a revocation is applied. Use
        /// <see cref="RevalidateAllInterest"/> when a sweep can take visibility away.
        /// </summary>
        public void MarkAllInterestDirty()
        {
            foreach (var c in _clientsById.Values) c.InterestDirty = true;
        }

        /// <summary>
        /// Re-authorize everything this client can already see, <b>now</b>, and despawn whatever
        /// <see cref="IInterestPolicy.Authorize"/> no longer allows before any further traffic about it is
        /// relayed. This is the immediate half of <see cref="MarkInterestDirty"/> and the call to make whenever
        /// a change can take visibility away: a team or alliance change, a stealth roll, fog closing over a
        /// region, an access rule revoked.
        /// <para>
        /// It costs one authorization per entity the client already holds — no grid query, no candidate scan —
        /// and allocates nothing. It never <i>adds</i> anything: the client is also marked dirty, so what the
        /// same change reveals arrives at its next evaluation, within an eval interval.
        /// </para>
        /// <para>
        /// Safe to call from a policy callback: a nested call falls back to marking the client dirty rather
        /// than re-entering the pass.
        /// </para>
        /// </summary>
        public void RevalidateInterest(ulong clientId)
        {
            if (_clientsById.TryGetValue(clientId, out var c)) RevalidateInterest(c);
        }

        /// <summary>
        /// <see cref="RevalidateInterest"/> for every client: a world-wide tightening (a new policy, a war
        /// declared, a fog sweep that can close as well as open). Every unauthorized replica on this gateway is
        /// gone by the time the call returns.
        /// <para>
        /// The cost is one authorization per replica the gateway is currently serving — about what one round of
        /// evaluation costs, concentrated into this call instead of spread over an eval interval. That is
        /// affordable for the events this is for, and it is the reason the routine rotation is still staggered:
        /// this is not a per-tick path, and nothing calls it for ordinary movement or camera updates.
        /// </para>
        /// </summary>
        public void RevalidateAllInterest()
        {
            // The clients are copied out first: an authorization callback is game code, and a collection
            // modified under a foreach would turn a policy that disconnects somebody into an exception here.
            _revalidateScratch.Clear();
            foreach (var c in _clientsById.Values) _revalidateScratch.Add(c);
            for (int i = 0; i < _revalidateScratch.Count; i++) RevalidateInterest(_revalidateScratch[i]);
            _revalidateScratch.Clear();
        }

        private void RevalidateInterest(ClientConn client)
        {
            // Whatever else happens, the additive half is scheduled: a revocation pass adds nothing, and a
            // caller that tightened one rule and loosened another must still see the loosening.
            client.InterestDirty = true;
            if (!client.Welcomed || client.Interest == null || _revalidating) return;
            _revalidating = true;
            try
            {
                // The snapshot is retaken here and not reused: the tag, the mode or the pawn is exactly what
                // just changed, and authorizing against the stale one would answer the previous question.
                client.Interest.Client = SnapshotClient(client);
                _revokeLeft.Clear();
                client.Interest.Revalidate(_index, _revokeLeft);
                // ApplyInterestChanges keeps design §8's order: the despawns, then the container rows that
                // nothing needs any more.
                if (_revokeLeft.Count > 0) ApplyInterestChanges(client, _noEntered, _revokeLeft);
                _revokeLeft.Clear();
            }
            finally { _revalidating = false; }
        }

        /// <summary>
        /// A client the gateway has admitted, as the server-side surface reports it. A snapshot taken on the
        /// gateway loop: nothing in it is a live reference.
        /// </summary>
        public readonly struct GatewayClientInfo
        {
            /// <summary>The mesh-wide session id; the same across a reconnection with a session token.</summary>
            public readonly ulong ClientId;
            /// <summary>The authenticated subject (<c>PlayerIdentity</c>), "" when there is none.</summary>
            public readonly string Identity;
            public readonly string Name;
            /// <summary>The simulation scope the client's pawn is in; 0 is the public world (and a client with no pawn yet).</summary>
            public readonly ulong InstanceId;
            public readonly bool IsBot;
            /// <summary>The session was reclaimed (a reconnection), not started fresh.</summary>
            public readonly bool Reclaimed;

            public GatewayClientInfo(ulong clientId, string identity, string name, ulong instanceId, bool isBot, bool reclaimed)
            {
                ClientId = clientId; Identity = identity ?? ""; Name = name ?? "";
                InstanceId = instanceId; IsBot = isBot; Reclaimed = reclaimed;
            }
        }

        /// <summary>
        /// An authenticated client has been welcomed: it has a session id and an identity, and its interest is
        /// about to be evaluated for the first time. This is where a server extension gives it its tags and its
        /// focus mode, because both are read by the very first evaluation.
        /// <para>
        /// Raised on the gateway loop, synchronously, and the gateway is mid-welcome: a handler may call the
        /// <c>SetClient…</c>/<c>MarkInterestDirty</c> methods and read the gateway's own state, but it must not
        /// block, and it must not throw (an exception is caught and logged, never passed to the client).
        /// </para>
        /// </summary>
        public event Action<GatewayClientInfo> ClientJoined;

        /// <summary>
        /// A welcomed client is gone from this gateway: it disconnected, was rejected, or its session was taken
        /// over somewhere else (in which case another gateway raises its own <see cref="ClientJoined"/>). Raised
        /// on the gateway loop under the same rules as <see cref="ClientJoined"/>; by the time it runs the
        /// client's interest state has already been let go, so its id no longer resolves.
        /// </summary>
        public event Action<GatewayClientInfo> ClientLeft;

        private void RaiseClientEvent(Action<GatewayClientInfo> handler, ClientConn client, string what)
        {
            if (handler == null) return;
            ulong instance = 0;
            if (client.PawnNetId != 0 && _entities.TryGetValue(client.PawnNetId, out var pawn))
                instance = ScopeContainer(pawn.Container)?.InstanceId ?? 0;
            try { handler(new GatewayClientInfo(client.ClientId, client.Identity, client.Name, instance, client.IsBot, client.Reclaimed)); }
            catch (Exception e) { NebulaLog.Error($"a {what} handler threw: {e.Message}"); }
        }

        /// <summary>Entities in one client's set right now (-1 when there is no such client). For tests and tooling.</summary>
        public int InterestSetSize(ulong clientId) => _clientsById.TryGetValue(clientId, out var c) ? c.Visible.Count : -1;

        // ------------------------------------------------------------------------------------------- set-up

        private void InitializeInterest()
        {
            var issues = new List<ConfigIssue>();
            Config.Validate(issues);
            foreach (var issue in issues)
            {
                if (issue.Severity == ConfigSeverity.Error) NebulaLog.Error($"config: {issue.Field}: {issue.Message}");
                else if (issue.Severity == ConfigSeverity.Warning) NebulaLog.Warn($"config: {issue.Field}: {issue.Message}");
            }
            _interest = Config.ToInterestSettings();
            _interestGrid = Config.ToInterestGrid();
            _hintFilter.Settings = _interest;
            NebulaLog.Info($"interest: radius {_interest.Radius} m (+{_interest.ExitMargin} exit, +{_interest.SubscribeMargin} subscribe), {_interestGrid}, eval {_interest.EvalHz} Hz");
        }

        private double InterestNow => _clock.Elapsed.TotalSeconds;

        // ------------------------------------------------------------------------------------------- positions

        /// <summary>
        /// The record's absolute world position, cached on the record and refreshed only when a pose or container
        /// actually changes. The gateway never shifts its floating origin, so its world space <i>is</i> absolute
        /// space and a region key computed here never moves under a client.
        /// </summary>
        private void RefreshAbsolute(EntityRecord rec)
        {
            var world = WorldPosition(rec.Container, rec.LastSpawn.LocalPosition);
            rec.AbsX = world.x;
            rec.AbsY = world.y;
            rec.AbsZ = world.z;
        }

        /// <summary>
        /// The entity the interest decision is made on: itself, or the outermost carrier it rides in. Carried
        /// entities inherit their root carrier's decision so a passenger cannot pop separately from its ship.
        /// <para>
        /// The walk goes all the way up, however deep the chain. Stopping at a constant would make
        /// a deep passenger judged at its own position rather than its ship's, which is the pop D3 exists to
        /// prevent. Container references arrive on the wire rather than through the cycle-refusing index, so the
        /// bound is the number of records held — a chain longer than that has revisited one.
        /// </para>
        /// </summary>
        private EntityRecord RootOf(EntityRecord rec)
        {
            for (int hops = 0; hops <= _entities.Count && rec.Container.IsDynamic; hops++)
            {
                if (!_entities.TryGetValue(rec.Container.NetId, out var carrier) || carrier == rec) break;
                rec = carrier;
            }
            return rec;
        }

        /// <summary>
        /// The region key a record is bucketed under: its root carrier's absolute position, packed, and salted with
        /// the scope that carrier chain resolves to (<see cref="RegionKeys"/>). Two scopes may occupy exactly the
        /// same absolute coordinates once each has its own origin frame, so the salt is what keeps their regions —
        /// and therefore their subscriptions — apart (<c>docs/scope-frames.md</c> D7).
        /// </summary>
        private ulong RegionOf(EntityRecord rec)
        {
            var root = RootOf(rec);
            return RegionKeys.Salt(_interestGrid.RegionOf(root.AbsX, root.AbsY, root.AbsZ), InstanceOf(root));
        }

        /// <summary>The isolation id an entity's carrier chain resolves to; 0 for the public world.</summary>
        private ulong InstanceOf(EntityRecord rec) => ScopeContainer(rec.Container)?.InstanceId ?? 0UL;

        /// <summary>
        /// The scope a client's region keys are salted with: the one its pawn actually stands in, and the key it
        /// asked for in its <c>Hello</c> while it has no pawn yet, so a client's window is in its own world from
        /// the first evaluation rather than from the first spawn. While the pawn's carrier chain cannot be
        /// resolved it is the scope the client was last in (<see cref="ScopeOfClient"/>).
        /// </summary>
        private ulong InstanceOf(ClientConn client) => ScopeOfClient(client);

        // ------------------------------------------------------------------------------------------- the index

        /// <summary>
        /// Put a record where the scans will find it. The three lines below are the record's <b>own</b> placement —
        /// the global list when its prefab is always relevant, the wide list when its own radius outreaches a region
        /// scan, otherwise its region. Where it <i>actually</i> sits is the index's answer, which is read back by
        /// <see cref="SyncCarriedPlacement"/>: while an entity rides in something it sits exactly where its root
        /// carrier sits, and every gateway-side decision made from the cached placement has to agree.
        /// </summary>
        private void IndexEntity(EntityRecord rec)
        {
            RefreshAbsolute(rec);
            if (rec.AlwaysRelevant) _index.AddGlobal(rec.NetId, rec);
            else if (rec.RelevanceRadius > _interest.Radius) _index.AddWide(rec.NetId, rec);
            else _index.Add(rec.NetId, RegionOf(rec), rec);
            // The item is in the index, so the link can only be Linked, Detached, Unchanged or Cycle here.
            LinkCarrier(rec);
            SyncCarriedPlacement(rec);
        }

        /// <summary>
        /// The record's carrier changed — it boarded, it was put ashore, or the ship it rides in was itself moved
        /// into another one. Re-link it and read the whole subtree's placement back, because a link moves
        /// everything riding in the item as well as the item itself.
        /// </summary>
        private void RelinkCarrier(EntityRecord rec)
        {
            // A record with no index entry at all: put it back rather than leave a cached row no scan can find.
            // IndexEntity links the carrier itself, so there is nothing more to do here.
            if (LinkCarrier(rec) == CarrierLink.UnknownItem) { IndexEntity(rec); return; }
            SyncCarriedPlacement(rec);
        }

        /// <summary>
        /// Point the index at this record's carrier (0 when its container is not a dynamic one), reporting a
        /// refused link. A cycle ("this crate is inside itself") is a bug in whatever decided the parenting; the
        /// index keeps the link it had rather than accept a subtree it could not walk, and saying so once per
        /// entity is the only way it is ever noticed.
        /// </summary>
        private CarrierLink LinkCarrier(EntityRecord rec)
        {
            ulong carrier = rec.Container.IsDynamic ? rec.Container.NetId : 0;
            var result = _index.SetCarrier(rec.NetId, carrier);
            if (result == CarrierLink.Cycle)
            {
                if (_carrierCycleWarned.Add(rec.NetId))
                    NebulaLog.Error($"entity #{rec.NetId} cannot be carried by #{carrier}: that would put it inside itself. The link is refused and #{rec.NetId} keeps carrier #{_index.CarrierOf(rec.NetId)}; fix whatever parented these containers on the worker that published it.");
            }
            else _carrierCycleWarned.Remove(rec.NetId);
            return result;
        }

        /// <summary>
        /// Copy the index's answer onto one record. <see cref="EntityRecord.Placement"/> and
        /// <see cref="EntityRecord.Region"/> are a cache of where the entity sits, read by the eviction sweep and
        /// by the region → clients fan-out; taking them from the entity's own prefab flags instead would keep an
        /// always-relevant crate cached on a gateway its ship is nowhere near, and offer it to every client.
        /// </summary>
        private void ReadPlacement(EntityRecord rec)
        {
            if (!_index.TryGetPlacement(rec.NetId, out var placement, out ulong region)) return;
            rec.Placement = placement;
            rec.Region = region;
        }

        /// <summary>
        /// <see cref="ReadPlacement"/> for a record and everything riding in it, to any depth. Called after
        /// anything that can move a root: an index placement, a carrier link, a rebucket, a carrier arriving
        /// after its passengers. There is no cap: the whole subtree moves or none of it does.
        /// </summary>
        private void SyncCarriedPlacement(EntityRecord rec)
        {
            ReadPlacement(rec);
            if (!_index.HasCarried(rec.NetId)) return;
            _placementScratch.Clear();
            int count = _index.CollectCarried(rec.NetId, _placementScratch);
            for (int i = 0; i < count; i++)
                if (_entities.TryGetValue(_placementScratch[i], out var passenger)) ReadPlacement(passenger);
            _placementScratch.Clear();
        }

        /// <summary>
        /// <see cref="OnEntityArrived"/> for a record and everything riding in it: a ship that arrives somewhere
        /// new brings its passengers with it, and they are not offered to that region's clients by anything else
        /// until their own next state entry turns up.
        /// </summary>
        private void ConsiderCarried(EntityRecord rec)
        {
            // The ids are taken before anything is considered: an evaluation can evict a record, and the walk
            // must not be reading the index while that happens.
            _arrivedScratch.Clear();
            int count = _index.HasCarried(rec.NetId) ? _index.CollectCarried(rec.NetId, _arrivedScratch) : 0;
            OnEntityArrived(rec);
            for (int i = 0; i < count; i++)
                if (_entities.TryGetValue(_arrivedScratch[i], out var passenger)) OnEntityArrived(passenger);
            _arrivedScratch.Clear();
        }

        /// <summary>Recompute the key only when the pose moved it: one multiply and floor per axis, then nothing.</summary>
        private void RebucketIfMoved(EntityRecord rec)
        {
            RefreshAbsolute(rec);
            // Carried, or not bucketed by region at all: the index decides where it sits, and Move would refuse.
            // The placement is still read back, because the carrier's own move may have just reseated it.
            if (rec.Placement != InterestPlacement.Region || _index.CarrierOf(rec.NetId) != 0)
            {
                _index.SetValue(rec.NetId, rec);
                ReadPlacement(rec);
                return;
            }
            ulong region = RegionOf(rec);
            if (region == rec.Region) return;
            if (!_index.Move(rec.NetId, region)) { ReadPlacement(rec); return; }
            SyncCarriedPlacement(rec);
            ConsiderCarried(rec);
        }

        /// <summary>
        /// Take a record out of the index. Removing a carrier orphans its passengers, and the index puts each of
        /// them back on its own placement: their cached rows have to follow, or a passenger that outlives its
        /// ship would stay addressed to the region the ship took away with it.
        /// </summary>
        private void UnindexEntity(EntityRecord rec)
        {
            _orphanScratch.Clear();
            int orphans = _index.HasCarried(rec.NetId) ? _index.CollectCarried(rec.NetId, _orphanScratch) : 0;
            _index.Remove(rec.NetId);
            _carrierCycleWarned.Remove(rec.NetId);
            for (int i = 0; i < orphans; i++)
                if (_entities.TryGetValue(_orphanScratch[i], out var passenger)) ReadPlacement(passenger);
            _orphanScratch.Clear();
        }

        /// <summary>
        /// An entity spawned into, or rebucketed into, a region: test it against the clients whose subscribe discs
        /// cover that region and nobody else. This is the whole point of the region → clients map — without it
        /// every spawn in the world would cost one test per connected client.
        /// <para>
        /// The placement is the effective one, so a carried always-relevant or wide passenger costs the clients
        /// of its carrier's region and no others: its own prefab settings do not make every client a candidate
        /// for a crate in somebody else's ship.
        /// </para>
        /// </summary>
        private void OnEntityArrived(EntityRecord rec)
        {
            double now = InterestNow;
            if (rec.Placement != InterestPlacement.Region)
            {
                // Wide and global entities are not in any one region; every client is a candidate for them.
                foreach (var client in _clientsById.Values) ConsiderFor(client, rec, now);
                return;
            }
            // The clients that already hold a replica are re-tested wherever it went. A region key is per scope
            // (docs/scope-frames.md D7), so an entity that crossed from the public world into a scope — or between
            // two scopes — lands in a bucket whose clients are a different set entirely, and without this nothing
            // would tell the ones it left until their next scheduled evaluation. ConsiderOne is idempotent, so a
            // client in both lists is simply tested twice.
            for (int i = rec.Observers.Count - 1; i >= 0; i--) ConsiderFor(rec.Observers[i], rec, now);
            if (!_regionClients.TryGetValue(rec.Region, out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--) ConsiderFor(list[i], rec, now);
        }

        private void ConsiderFor(ClientConn client, EntityRecord rec, double now)
        {
            if (!client.Welcomed || client.Interest == null) return;
            _entered.Clear();
            _leftIds.Clear();
            client.Interest.ConsiderOne(_index, rec.NetId, now, _entered, _leftIds);
            ApplyInterestChanges(client, _entered, _leftIds);
        }

        // ------------------------------------------------------------------------------------------- observers

        private void AddObserver(EntityRecord rec, ClientConn client)
        {
            if (!rec.Observers.Contains(client)) rec.Observers.Add(client);
        }

        private void RemoveObserver(EntityRecord rec, ClientConn client) => rec.Observers.Remove(client);

        /// <summary>
        /// Drop the client from every entity that had it as an observer (it disconnected, or its session moved).
        /// Every path that removes a client goes through here, so it is also where the rotation lets go of it
        /// and where <see cref="ClientLeft"/> is raised.
        /// </summary>
        private void ForgetClientInterest(ClientConn client)
        {
            bool welcomed = client.Welcomed;
            // Only if the session id really is gone: a takeover removes the old link *after* the replacement has
            // claimed the same id, and taking that id out of the rotation would stop evaluating the new client.
            if (!_clientsById.ContainsKey(client.ClientId)) _schedule.Remove(client.ClientId);
            if (client.Interest != null)
            {
                foreach (ulong netId in client.Interest.Ids)
                    if (_entities.TryGetValue(netId, out var rec)) RemoveObserver(rec, client);
                client.Interest.Clear();
            }
            foreach (ulong region in client.Regions)
                if (_regionClients.TryGetValue(region, out var list) && list.Remove(client) && list.Count == 0) _regionClients.Remove(region);
            client.Regions.Clear();
            client.Visible.Clear();
            client.ViewSeq.Clear();
            client.KnownContainers.Clear();
            client.PreparedRows.Clear();
            client.RowsLeaving.Clear();
            _hintFilter.Forget(client.ClientId);
            _subscriptionsDirty = true;
            if (welcomed) RaiseClientEvent(ClientLeft, client, nameof(ClientLeft));
        }

        // ------------------------------------------------------------------------------------------- evaluation

        /// <summary>
        /// Everything interest does once per tick: evaluate the clients whose turn it is, then bring the worker
        /// subscriptions in line.
        /// <para>
        /// Whose turn it is comes from <see cref="InterestSchedule"/> and not from a per-client deadline. A
        /// deadline staggers nothing: clients evaluated together are given the same next deadline and stay in
        /// lockstep for the rest of the session, which is how a hundred-client gateway ends up doing all its
        /// interest work on one tick in four and nothing on the other three. The schedule spreads the routine
        /// share over the ticks of one interval and still lets a dirty client — a new pawn, another instance, a
        /// changed focus mode, a policy that said so — be evaluated on the next tick.
        /// </para>
        /// </summary>
        private void TickInterest()
        {
            double now = InterestNow;
            double dt = _lastInterestTickAt >= 0 ? now - _lastInterestTickAt : 0;
            _lastInterestTickAt = now;
            if (dt < 0) dt = 0;
            if (_schedule.Count > 0)
            {
                _isClientDirty ??= IsClientInterestDirty;
                _schedule.Collect(dt, _interest.EvalHz, _isClientDirty, _dueClients);
                for (int i = 0; i < _dueClients.Count; i++)
                {
                    // A client can leave between the slice being taken and being walked (a policy handler, an
                    // eviction): look it up rather than holding a reference to something already forgotten.
                    if (_clientsById.TryGetValue(_dueClients[i], out var client) && client.Welcomed) EvaluateClient(client, now);
                }
            }
            if (now >= _nextSubscriptionAt || _subscriptionsDirty)
            {
                _nextSubscriptionAt = now + Math.Max(0.05, _interest.EvalInterval);
                _subscriptionsDirty = false;
                UpdateSubscriptions(now);
            }
        }

        /// <summary>
        /// Say once per client that it ran into one of the interest limits. Once, because every one of them is
        /// re-tested four times a second and a log line per evaluation would bury the mesh's own messages.
        /// </summary>
        private void WarnOnce(ClientConn client, string what)
        {
            if (client.InterestLimitWarned) return;
            client.InterestLimitWarned = true;
            NebulaLog.Warn($"client {client.ClientId} '{client.Name}': {what}");
        }

        private bool IsClientInterestDirty(ulong clientId) =>
            _clientsById.TryGetValue(clientId, out var client) && client.Welcomed && client.InterestDirty;

        private void EvaluateClient(ClientConn client, double now)
        {
            long started = Stopwatch.GetTimestamp();
            client.InterestDirty = false;
            client.Interest ??= new ClientInterest<EntityRecord, InterestSource>(new InterestSource(this));
            client.Interest.Settings = _interest;
            client.Interest.Grid = _interestGrid;
            // Before the scan, not after it: the index is bucketed per scope, so a client whose salt was still the
            // public world's would find nothing in its own world on this pass (docs/scope-frames.md D7).
            client.Interest.ScopeSalt = RegionKeys.SaltOf(InstanceOf(client));
            client.Query ??= new InterestQuery();

            var snapshot = SnapshotClient(client);
            client.Interest.Client = snapshot;
            client.Query.Reset(_interest);
            _policy.Collect(snapshot, client.Query);
            AddObservationWindows(client, snapshot, client.Query);
            if (client.Query.Overflowed) WarnOnce(client, $"its policy asked for more than {_interest.MaxFoci} foci or {_interest.MaxExplicitPerClient} explicit entities; the rest are dropped.");
            if (client.Query.BoxesClamped) WarnOnce(client, $"a box focus was wider than InterestMaxRadius ({_interest.MaxRadius} m) and was shrunk to it about its center.");

            client.Interest.SetFoci(client.Query.Foci);
            _alwaysScratch.Clear();
            if (client.PawnNetId != 0) _alwaysScratch.Add(client.PawnNetId);
            // Everything this client owns is in its set at full rate, wherever it is: it is the thing the client's
            // own prediction reconciles against.
            foreach (var rec in _entities.Values) if (rec.OwnerClientId == client.ClientId) _alwaysScratch.Add(rec.NetId);
            var extras = client.Query.Entities;
            for (int i = 0; i < extras.Count; i++) _alwaysScratch.Add(extras[i]);
            client.Interest.SetAlways(_alwaysScratch);

            _entered.Clear();
            _leftIds.Clear();
            client.Interest.Evaluate(_index, now, _entered, _leftIds);
            UpdateClientRegions(client);
            ApplyInterestChanges(client, _entered, _leftIds);
            // What the client can *see* has been dealt with; what it must be able to *build* has not. A camera
            // over empty terrain changes no entity at all, so the container window is brought in line here and
            // not only when something entered or left the set (design D60).
            SyncOwnershipWindow(client);

            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            _evalMsSum += ms;
            _evalCount++;
            if (ms > _evalMsMax) _evalMsMax = ms;
        }

        /// <summary>The facts a policy is given about a client. A snapshot: a policy can never reach into live gateway state.</summary>
        private InterestClient SnapshotClient(ClientConn client)
        {
            var snapshot = new InterestClient
            {
                ClientId = client.ClientId,
                Identity = client.Identity,
                Name = client.Name,
                PawnNetId = client.PawnNetId,
                Team = client.Team,
                Tags = client.Tags,
                FocusMode = client.FocusMode,
            };
            if (client.PawnNetId != 0 && _entities.TryGetValue(client.PawnNetId, out var pawn))
            {
                var root = RootOf(pawn);
                snapshot.HasPawn = true;
                snapshot.PawnX = root.AbsX; snapshot.PawnY = root.AbsY; snapshot.PawnZ = root.AbsZ;
                // The scope comes from the pawn's own container chain, not from the root it resolved to: they
                // are the same container by construction, and asking the pawn keeps this line and CanObserve's
                // reading the same thing.
                snapshot.InstanceId = ScopeOfClient(client);
                snapshot.PawnCarrierNetId = root != pawn ? root.NetId : 0;
            }
            if (client.HasHint)
            {
                snapshot.HasHint = true;
                snapshot.HintX = client.HintX; snapshot.HintY = client.HintY; snapshot.HintZ = client.HintZ;
            }
            return snapshot;
        }

        /// <summary>
        /// An <c>ObservePublic</c> instance is a window onto the public world: while the client is inside it, the
        /// window's box is an extra focus, so the hallway outside is in the client's set without the client having
        /// to stand in it. Instance isolation itself is authorization (see <see cref="AuthorizeEntity"/>).
        /// </summary>
        private void AddObservationWindows(ClientConn client, in InterestClient snapshot, InterestQuery query)
        {
            if (client.Interest != null) client.Interest.ScanPublicToo = false;
            if (client.PawnNetId == 0 || !_entities.TryGetValue(client.PawnNetId, out var pawn)) return;
            var view = ScopeContainer(pawn.Container)?.Instance;
            if (view == null || !view.ObservePublic) return;
            // Entities seen through the window are bucketed in the public world, not in this client's scope
            // (docs/scope-frames.md D7); the window's box focus alone would scan the wrong buckets.
            if (client.Interest != null) client.Interest.ScanPublicToo = true;
            query.AddFocus(InterestFocus.Box(view.ObservationCenter.x, view.ObservationCenter.y, view.ObservationCenter.z,
                view.ObservationSize.x, view.ObservationSize.y, view.ObservationSize.z));
        }

        /// <summary>
        /// Authorization checks the instance rules first (an unknown container is never
        /// observable, a private instance is isolated), then the game's policy. A failure here removes the entity
        /// at once with no hysteresis, because this is a boundary and not a distance.
        /// </summary>
        private bool AuthorizeEntity(in InterestClient client, in InterestEntity entity)
        {
            if (!_clientsById.TryGetValue(client.ClientId, out var conn)) return false;
            if (!_entities.TryGetValue(entity.NetId, out var rec)) return false;
            return CanObserve(conn, rec) && _policy.Authorize(client, entity);
        }

        /// <summary>
        /// The regions this client's foci cover at the subscribe radius: what the gateway asks workers for, and
        /// the key of the region → clients map that makes an arriving entity cheap.
        /// </summary>
        /// <summary>One region key this client wants this pass, remembering which scope resolves it.</summary>
        private void Want(ClientConn client, ulong region, ulong instanceId, ref bool changed)
        {
            _regionScope[region] = instanceId;
            if (!client.NextRegions.Add(region)) return;
            if (client.Regions.Contains(region)) return;
            changed = true;
            if (!_regionClients.TryGetValue(region, out var list)) _regionClients[region] = list = new List<ClientConn>(4);
            list.Add(client);
        }

        private void UpdateClientRegions(ClientConn client)
        {
            _regionScratch.Clear();
            var foci = client.Interest.Foci;
            for (int i = 0; i < foci.Count; i++)
            {
                var focus = foci[i];
                double reach = focus.Scaled(_interest.SubscribeRadius);
                if (focus.IsBox)
                    _interestGrid.CollectBox(focus.X - focus.HalfX - reach, focus.Y - focus.HalfY - reach, focus.Z - focus.HalfZ - reach,
                        focus.X + focus.HalfX + reach, focus.Y + focus.HalfY + reach, focus.Z + focus.HalfZ + reach, _regionScratch);
                else _interestGrid.CollectDisc(focus.X, focus.Y, focus.Z, reach, _regionScratch);
            }
            bool changed = false;
            // A client is in one scope, so its window is salted with that scope: it asks its workers for the regions
            // of its own world and never for another's (docs/scope-frames.md D7). The one exception is a scope that
            // is an ObservePublic window onto the public world — what it may see through the window is bucketed in
            // the public world, so those keys are subscribed as well, and CanSee still decides what reaches it.
            ulong instance = InstanceOf(client);
            ulong salt = RegionKeys.SaltOf(instance);
            bool alsoPublic = salt != 0 && (client.Interest?.ScanPublicToo ?? false);
            client.NextRegions.Clear();
            for (int i = 0; i < _regionScratch.Count; i++)
            {
                Want(client, _regionScratch[i] ^ salt, instance, ref changed);
                if (alsoPublic) Want(client, _regionScratch[i], 0UL, ref changed);
            }
            foreach (ulong region in client.Regions)
            {
                if (client.NextRegions.Contains(region)) continue;
                changed = true;
                if (_regionClients.TryGetValue(region, out var list) && list.Remove(client) && list.Count == 0) _regionClients.Remove(region);
            }
            if (!changed) return;
            client.Regions.Clear();
            foreach (ulong region in client.NextRegions) client.Regions.Add(region);
            _subscriptionsDirty = true;
        }

        /// <summary>
        /// Turn what the evaluation decided into what goes on the wire, in the one order a client can act on:
        /// the containers a spawn will name, then the spawns (carriers first), then the despawns, then the
        /// containers nothing needs any more.
        /// </summary>
        private void ApplyInterestChanges(ClientConn client, List<ulong> entered, List<ulong> left)
        {
            if (entered.Count == 0 && left.Count == 0) return;
            if (entered.Count > 0) SendOwnershipUpserts(client, entered);
            for (int i = 0; i < entered.Count; i++)
            {
                if (!_entities.TryGetValue(entered[i], out var rec)) continue;
                client.Visible.Add(rec.NetId);
                AddObserver(rec, client);
                SendSpawn(client, rec);
            }
            for (int i = 0; i < left.Count; i++)
            {
                ulong netId = left[i];
                client.Visible.Remove(netId);
                uint epoch = 0;
                if (_entities.TryGetValue(netId, out var rec)) { RemoveObserver(rec, client); epoch = rec.Epoch; }
                SendDespawn(client, netId, epoch);
            }
            if (left.Count > 0) SendOwnershipRemoves(client);
        }

        /// <summary>A spawn carries the newest pose and the newest keyframe of every behavior, plus a fresh view sequence.</summary>
        private void SendSpawn(ClientConn client, EntityRecord rec)
        {
            rec.RefreshSpawnState(_scratch);
            ushort seq = client.ViewSeq.TryGetValue(rec.NetId, out ushort previous) ? (ushort)(previous + 1) : (ushort)1;
            client.ViewSeq[rec.NetId] = seq;
            var msg = rec.LastSpawn;
            msg.ViewSeq = seq;
            _interestWriter.Reset();
            msg.Write(_interestWriter, MsgId.EntitySpawn);
            AppendReliable(client, _interestWriter.ToSegment());
            _spawnsSent++;
        }

        private void SendDespawn(ClientConn client, ulong netId, uint epoch)
        {
            client.ViewSeq.TryGetValue(netId, out ushort seq);
            _interestWriter.Reset();
            new EntityDespawnMsg { NetId = netId, Epoch = epoch, ViewSeq = seq }.Write(_interestWriter, MsgId.EntityDespawn);
            AppendReliable(client, _interestWriter.ToSegment());
            _despawnsSent++;
        }

        /// <summary>Every client that had this entity is told it is gone; the record's observer list is the exact audience.</summary>
        private void DespawnFromObservers(EntityRecord rec)
        {
            for (int i = rec.Observers.Count - 1; i >= 0; i--)
            {
                var client = rec.Observers[i];
                client.Visible.Remove(rec.NetId);
                client.Interest?.Remove(rec.NetId, null);
                SendDespawn(client, rec.NetId, rec.Epoch);
            }
            rec.Observers.Clear();
        }

        // ------------------------------------------------------------------------------------------- focus hints

        /// <summary>
        /// A client told us where it is looking, in absolute world coordinates. It is an input: malformed
        /// values are dropped before anything reads them, the rate of attempts is capped before any game code
        /// runs, and what the point may then do is the server's decision and
        /// never the client's — the per-client <see cref="FocusMode"/> (<see cref="SetClientFocusMode"/>), which
        /// an <see cref="IFocusHintPolicy"/> may narrow or widen for this one hint. By default the point is
        /// clamped to <see cref="InterestSettings.HintMaxDistance"/> of the pawn, and a client with no pawn has
        /// its hint refused outright rather than being handed the world it has no body in. A client cannot make
        /// the gateway stream it the map by claiming to look at it.
        /// </summary>
        private void OnClientFocusHint(ClientConn client, in ClientFocusHintMsg msg)
        {
            // Hints are sequenced and the clear is reliable, so they can cross: anything from before the newest
            // generation is a late arrival and must not re-establish a focus the client already gave up.
            if (!client.HintGenerationKnown) { client.HintGenerationKnown = true; client.HintGeneration = (byte)(msg.Generation - (msg.Clear ? 1 : 0)); }
            if (ClientFocusHintMsg.IsStale(msg.Generation, client.HintGeneration)) return;
            if (msg.Clear)
            {
                // A clear for the generation we already hold was overtaken by a hint of that same generation:
                // the client has set a new focus since, and that one stands.
                if (msg.Generation == client.HintGeneration) return;
                client.HintGeneration = msg.Generation;
                // Narrowing interest is never an attack, so a clear bypasses the rate limit — dropping it
                // would leave the client paying for a view it no longer has.
                if (client.HasHint) { client.HasHint = false; client.InterestDirty = true; }
                return;
            }
            // The generation advances for every hint we saw, including the ones dropped below. A dropped hint
            // is still evidence of where the client is up to, and leaving the fence behind would let an older
            // hint arriving after it win — the exact thing the sequence exists to prevent.
            client.HintGeneration = msg.Generation;

            // Malformed first, before the snapshot and before any game code: a NaN that reached an
            // IFocusHintPolicy would be the gateway handing an extension a value it cannot defend against
            // (design D80).
            if (_hintFilter.Malformed(msg.X, msg.Y, msg.Z)) return;
            double now = InterestNow;
            // Then the rate limit, on the *attempt* and not on the acceptance. Everything after this line is
            // work a client could otherwise ask for as fast as it can send — the snapshot, and the game's own
            // hint policy — and a hint that is going to be refused anyway must pay for the asking, or a
            // pawn-less client would have an unlimited path into extension code.
            if (_hintFilter.ThrottledAttempt(client.ClientId, now)) return;

            bool hasPawn = false;
            double px = 0, py = 0, pz = 0;
            if (client.PawnNetId != 0 && _entities.TryGetValue(client.PawnNetId, out var pawn))
            {
                var root = RootOf(pawn);
                hasPawn = true; px = root.AbsX; py = root.AbsY; pz = root.AbsZ;
            }
            var decision = new FocusHintDecision { Mode = client.FocusMode, X = msg.X, Y = msg.Y, Z = msg.Z };
            if (_hintPolicy != null) decision = _hintPolicy.AuthorizeFocusHint(SnapshotClient(client), decision);
            if (!_hintFilter.Authorizes(decision.Mode, hasPawn))
            {
                // A refusal revokes. A policy that has just said no — or a free camera whose pawn has gone —
                // must not leave the last hint it was allowed standing for the rest of the session, and the
                // client is sending again within 1/HintMaxHz s if it is still allowed anything.
                if (client.HasHint) { client.HasHint = false; client.InterestDirty = true; }
                return;
            }
            if (!_hintFilter.TryAccept(client.ClientId, now, decision.X, decision.Y, decision.Z, decision.Mode,
                hasPawn, px, py, pz, out double x, out double y, out double z)) return;
            bool first = !client.HasHint;
            ulong before = first ? 0 : _interestGrid.RegionOf(client.HintX, client.HintY, client.HintZ);
            client.HasHint = true;
            client.HintX = x; client.HintY = y; client.HintZ = z;
            // Only a hint that crossed a region edge can change what the client should hear about right now;
            // the rest is answered by its next scheduled evaluation, which is what keeps a 5 Hz hint cheap.
            if (first || before != _interestGrid.RegionOf(x, y, z)) client.InterestDirty = true;
        }

        // ------------------------------------------------------------------------------------------- subscriptions

        /// <summary>
        /// Bring every worker link in line with what the clients need: resolve the needed regions to
        /// their owning workers, dial the workers that have a reason and let go of the ones that do not, send the
        /// subscription deltas, and evict records in regions nobody subscribes any more.
        /// </summary>
        private void UpdateSubscriptions(double now)
        {
            foreach (var link in _links.Values)
            {
                link.Reasons = InterestLinkReason.None;
                link.Sub.BeginPass();
            }
            _fociRegions.Clear();
            _explicitIds.Clear();
            _explicitSet.Clear();
            bool unreachablePawn = false;

            foreach (var client in _clientsById.Values)
            {
                if (!client.Welcomed) continue;
                // Every region id this client contributes is its own scope's (docs/scope-frames.md D7).
                ulong instance = InstanceOf(client);
                foreach (ulong region in client.Regions)
                {
                    var owners = WorkersForRegion(region);
                    for (int i = 0; i < owners.Count; i++) NeedRegion(owners[i], region);
                }
                if (client.Interest != null)
                {
                    var foci = client.Interest.Foci;
                    for (int i = 0; i < foci.Count; i++)
                    {
                        ulong key = RegionKeys.Salt(_interestGrid.RegionOf(foci[i].X, foci[i].Y, foci[i].Z), instance);
                        if (!_fociRegions.Contains(key)) _fociRegions.Add(key);
                        LinkNearbyOwners(foci[i]);
                    }
                    if (client.Query != null)
                    {
                        var extras = client.Query.Entities;
                        for (int i = 0; i < extras.Count; i++) AddExplicit(extras[i]);
                    }
                }
                // A client that owns an entity must hear about it wherever it is, so its worker is linked. The
                // worker is resolved through the control plane, not through the links we already hold: after a
                // handover the owner may be one we have never dialled, and this is where we dial it.
                if (client.PawnNetId != 0)
                {
                    bool placed = false;
                    if (_entities.TryGetValue(client.PawnNetId, out var pawn))
                    {
                        // The ship it rides in decides which world it is in, so it is followed wherever it goes.
                        FollowCarriers(pawn);
                        string ownerId = WorkerIdOfIndex(pawn.OwnerWorkerIndex);
                        // The link is held even while the owner is only a redirect's word, because dialling it
                        // is how the announcement that confirms it can arrive at all.
                        if (!string.IsNullOrEmpty(ownerId) && EnsureLink(ownerId) != null)
                        {
                            Reason(ownerId, InterestLinkReason.Owned);
                            placed = !pawn.OwnerUnconfirmed;
                        }
                    }
                    if (placed) { client.PawnLostSince = 0; client.PawnRecovering = false; }
                    else if (RecoverPawn(client, now)) unreachablePawn = true;
                }
                // Somewhere to put a client that has no pawn yet: without this the gateway would have no link to
                // ask for a spawn, and a first join into an empty mesh could never complete.
                if (client.PawnNetId == 0 && client.DisconnectAt == 0) LinkSpawnCandidates();
            }

            // An explicit id we cannot place yet: link every live worker until one of them answers (design D5).
            bool unresolved = unreachablePawn;
            for (int i = 0; i < _explicitIds.Count && !unresolved; i++) unresolved = !_entities.ContainsKey(_explicitIds[i]);
            foreach (var w in ControlPlane.Workers)
            {
                if (w.Status == WorkerStatus.Dead || !ControlPlane.IsWorkerAlive(w, Config.WorkerTimeoutSeconds)) continue;
                if (w.HasGlobalEntities) Reason(w.WorkerId, InterestLinkReason.Global);
                if (unresolved) Reason(w.WorkerId, InterestLinkReason.Explicit);
            }

            _subscribedRegions.Clear();
            _linkScratch.Clear();
            foreach (var link in _links.Values)
            {
                link.Sub.EndPass(now);
                link.Sub.Grid = _interestGrid;
                link.Sub.LingerSeconds = _interest.RegionLingerSeconds;
                link.Sub.ResyncSeconds = _interest.ResyncSeconds;
                link.Sub.SetFoci(_fociRegions);
                link.Sub.SetEntities(_explicitIds);
                foreach (ulong region in link.Sub) _subscribedRegions.Add(region);
                if (link.Reasons == InterestLinkReason.None)
                {
                    if (double.IsPositiveInfinity(link.UnneededSince)) link.UnneededSince = now;
                    if (now - link.UnneededSince >= _interest.LinkLingerSeconds) { _linkScratch.Add(link.WorkerId); continue; }
                }
                else link.UnneededSince = double.PositiveInfinity;
                FlushSubscription(link);
            }
            for (int i = 0; i < _linkScratch.Count; i++) DropLink(_linkScratch[i], "no client needs it any more");
            EvictUnsubscribedRecords();
        }

        /// <summary>
        /// A client whose pawn the gateway cannot place: there is no record for it, or the record names a worker
        /// that is gone, or only a redirect said who owns it and nobody has confirmed. Everything downstream of
        /// interest is derived from the pawn — the client's only focus, the link that keeps the pawn published,
        /// its own prediction — so sitting on this would leave the client connected and permanently pawn-less,
        /// which is exactly what it looks like in a chunked world under fast travel.
        /// <para>
        /// To repair this, subscribe to the pawn <b>by name</b> on every live
        /// worker (the caller links them all), so whichever worker holds it announces it and the record is
        /// rebuilt wherever it went. Only if nobody answers within <see cref="PawnRecoverySeconds"/> is the pawn
        /// really gone, and then the client starts the join over rather than staying a spectator for ever.
        /// </para>
        /// <returns>Whether every live worker should be linked for this client.</returns>
        /// </summary>
        private bool RecoverPawn(ClientConn client, double now)
        {
            if (client.DisconnectAt != 0) return false;
            if (client.PawnLostSince == 0) { client.PawnLostSince = now; return false; }
            // An ordinary handover is unconfirmed for a moment; only an episode that outlasts the grace is a
            // problem, and linking every worker for every crossing would be its own kind of bug.
            double waited = now - client.PawnLostSince;
            if (waited < PawnHandoverGraceSeconds) return false;
            if (!client.PawnRecovering)
            {
                client.PawnRecovering = true;
                NebulaLog.Warn($"client {client.ClientId} '{client.Name}': cannot place pawn #{client.PawnNetId} after {waited:0.00}s; asking every worker for it by name");
            }
            if (waited < PawnRecoverySeconds)
            {
                AddExplicit(client.PawnNetId);
                return true;
            }
            // Nobody owns it any more. Drop what we think we know and let the client be placed again; a stale
            // record here would keep the client out of its own world for the rest of the session.
            ulong lost = client.PawnNetId;
            NebulaLog.Warn($"client {client.ClientId} '{client.Name}': pawn #{lost} was not claimed by any worker; starting the join again");
            client.PawnNetId = 0;
            client.PawnLostSince = 0;
            client.PawnRecovering = false;
            client.InterestDirty = true;
            if (_entities.ContainsKey(lost)) ForgetEntity(lost);
            SendJoinStatus(client, JoinState.Starting);
            client.NextSpawnAttempt = 0;
            return false;
        }

        /// <summary>
        /// Which workers could hold an entity bucketed in this region: the owners of every container overlapping
        /// the region box grown by the meshing band, or the nearest container's owner when the region sits in the
        /// gaps between boxes (the rule <see cref="ContainerRegistry.Find"/> already uses for entities).
        /// </summary>
        private List<string> WorkersForRegion(ulong region)
        {
            if (_regionWorkers.TryGetValue(region, out var cached)) return cached;
            var owners = new List<string>(2);
            _regionScope.TryGetValue(region, out ulong instanceId);
            // The key is per scope; the arithmetic is on the plain packing, and the container query is asked in
            // the same scope, so a region of one world never resolves to the owner of another's box.
            _interestGrid.BoundsOf(RegionKeys.Unsalt(region, instanceId), out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ);
            float margin = Config.GhostBandMargin + Config.HandoverHysteresis;
            // A planar region is an infinite column; a finite box tall enough to hold any world is what a
            // container query can answer, and the exact per-entity test happens on the gateway anyway.
            if (double.IsInfinity(minY)) { minY = -10000; maxY = 10000; }
            var center = new Vector3((float)((minX + maxX) / 2), (float)((minY + maxY) / 2), (float)((minZ + maxZ) / 2));
            var size = new Vector3((float)(maxX - minX) + 2 * margin, (float)(maxY - minY) + 2 * margin, (float)(maxZ - minZ) + 2 * margin);
            ContainerRegistry.Overlapping(new Bounds(center, size), _containerScratch, instanceId);
            for (int i = 0; i < _containerScratch.Count; i++)
            {
                string id = _containerScratch[i].OwnerWorkerId;
                if (!string.IsNullOrEmpty(id) && !owners.Contains(id)) owners.Add(id);
            }
            if (owners.Count == 0)
            {
                var nearest = ContainerRegistry.Find(center, null, instanceId);
                if (nearest != null && !string.IsNullOrEmpty(nearest.OwnerWorkerId)) owners.Add(nearest.OwnerWorkerId);
            }
            _regionWorkers[region] = owners;
            return owners;
        }

        private void NeedRegion(string workerId, ulong region)
        {
            var link = EnsureLink(workerId);
            if (link == null) return;
            link.Reasons |= InterestLinkReason.Region;
            link.Sub.Need(region);
        }

        /// <summary>
        /// Owners of containers within <see cref="InterestSettings.MaxRadius"/> of a focus get the foci but no
        /// regions: that is all a worker needs to decide whether one of its wide entities reaches this gateway.
        /// </summary>
        private void LinkNearbyOwners(in InterestFocus focus)
        {
            float reach = _interest.MaxRadius;
            var box = new Bounds(new Vector3((float)focus.X, (float)focus.Y, (float)focus.Z), new Vector3(2 * reach, 2 * reach, 2 * reach));
            ContainerRegistry.Overlapping(box, _containerScratch);
            for (int i = 0; i < _containerScratch.Count; i++) Reason(_containerScratch[i].OwnerWorkerId, InterestLinkReason.Foci);
        }

        private void LinkSpawnCandidates()
        {
            for (int i = 0; i < ContainerRegistry.All.Count; i++) Reason(ContainerRegistry.All[i].OwnerWorkerId, InterestLinkReason.Spawn);
            for (int i = 0; i < ContainerRegistry.Runtime.Count; i++) Reason(ContainerRegistry.Runtime[i].OwnerWorkerId, InterestLinkReason.Spawn);
        }

        private void Reason(string workerId, InterestLinkReason reason)
        {
            var link = EnsureLink(workerId);
            if (link != null) link.Reasons |= reason;
        }

        /// <summary>Hold a link to this worker, dialling it if we are not already talking to it.</summary>
        private WorkerLink EnsureLink(string workerId)
        {
            if (string.IsNullOrEmpty(workerId)) return null;
            if (_links.TryGetValue(workerId, out var link)) return link;
            var info = ControlPlane.FindWorker(workerId);
            if (info == null || info.Status == WorkerStatus.Dead) return null;
            link = new WorkerLink { WorkerId = workerId, Index = (ushort)info.WorkerIndex, Sub = new RegionSubscriptionSender(() => InterestNow) };
            link.Sub.Grid = _interestGrid;
            _links[workerId] = link;
            if (!_workersById.ContainsKey(workerId) && !_dialing.Contains(workerId))
            {
                int peerId = _transport.Connect(info.Address, info.Port);
                _workersByPeer[peerId] = new WorkerConn { PeerId = peerId, WorkerId = workerId, Index = (ushort)info.WorkerIndex, Outbound = true };
                _dialing.Add(workerId);
                NebulaLog.Debugf($"dialing worker {workerId} at {info.Address}:{info.Port} (interest)");
            }
            return link;
        }

        private void DropLink(string workerId, string why)
        {
            if (!_links.TryGetValue(workerId, out var link)) return;
            // Belt and braces over the Owned reason: never let go of the worker that is authoritative for a
            // connected client's pawn. Its state is what that client's own prediction reconciles against.
            foreach (var rec in _entities.Values)
                if (rec.OwnerWorkerIndex == link.Index && IsLiveClientsPawn(rec)) { link.UnneededSince = double.PositiveInfinity; return; }
            _links.Remove(workerId);
            NebulaLog.Debugf($"dropping worker link {workerId}: {why}");
            if (_workersById.TryGetValue(workerId, out var conn))
            {
                // An empty Full snapshot tells the worker to stop sending before the link goes; a link that just
                // vanishes would leave the worker publishing into a socket for as long as its own timeout.
                link.Sub.Clear();
                FlushSubscription(link);
                _transport.Flush();
                _transport.Disconnect(conn.PeerId);
            }
            _dialing.Remove(workerId);
            DropWorkerRecords(link.Index);
        }

        private void FlushSubscription(WorkerLink link)
        {
            if (!_workersById.TryGetValue(link.WorkerId, out var conn) || !conn.Ready) return;
            if (!link.Linked)
            {
                link.Linked = true;
                link.Incarnation = conn.Incarnation;
                link.Sub.Reset();
            }
            else if (link.Incarnation != conn.Incarnation)
            {
                // The worker restarted behind the same id: it holds no set of ours, so the next message is Full.
                link.Incarnation = conn.Incarnation;
                link.Sub.Reset();
            }
            _subOutbox.Clear();
            if (!link.Sub.Build(_subOutbox, InterestNow)) return;
            for (int i = 0; i < _subOutbox.Count; i++)
            {
                _interestWriter.Reset();
                _subOutbox[i].Write(_interestWriter);
                Send(conn.PeerId, Delivery.ReliableOrdered, _interestWriter.ToSegment());
            }
        }

        /// <summary>
        /// A region nobody subscribes any more: the gateway drops its own records there, because the worker sends
        /// nothing on unsubscribe (it would be a per-gateway per-entity "known" set on the worker, which is
        /// exactly the memory interest management must not grow).
        /// <para>
        /// The placement read here is the <b>effective</b> one, so a carried passenger is evicted with its root
        /// carrier: an always-relevant crate riding in an ordinary ship is a region record in the ship's region,
        /// and leaving it out of this sweep because of its own prefab flag is how a gateway on the other side of
        /// the world ends up caching it for the rest of the session.
        /// </para>
        /// </summary>
        private void EvictUnsubscribedRecords()
        {
            _evictScratch.Clear();
            foreach (var rec in _entities.Values)
            {
                if (rec.Placement != InterestPlacement.Region) continue;
                if (rec.OwnerClientId != 0 && _clientsById.ContainsKey(rec.OwnerClientId)) continue;
                if (_subscribedRegions.Contains(rec.Region)) continue;
                // Followed by name (a policy's explicit entity, a pawn's carrier): the worker keeps publishing it
                // wherever it is, so its region being unsubscribed says nothing about whether we still want it.
                if (_explicitSet.Contains(rec.NetId)) continue;
                _evictScratch.Add(rec.NetId);
            }
            for (int i = 0; i < _evictScratch.Count; i++) ForgetEntity(_evictScratch[i]);
            _evictScratch.Clear();
        }

        /// <summary>Forget one record entirely: out of the index, out of every set, despawned from every observer.</summary>
        private void ForgetEntity(ulong netId)
        {
            _held.Remove(netId);
            if (!_entities.TryGetValue(netId, out var rec)) return;
            _entities.Remove(netId);
            UnindexEntity(rec);
            DespawnFromObservers(rec);
        }

        /// <summary>
        /// Let go of every record a worker we are no longer linked to gave us. A connected client's own pawn is
        /// not dropped: unlike a worker dying (<see cref="OnWorkerLost"/>) the pawn is alive, we simply stopped
        /// listening, and dropping it here is how a player ends up connected to a world it has no body in. The
        /// link is not dropped in that case either — see <see cref="DropLink"/>.
        /// </summary>
        private void DropWorkerRecords(ushort workerIndex)
        {
            _evictScratch.Clear();
            foreach (var rec in _entities.Values)
                if (rec.OwnerWorkerIndex == workerIndex && !IsLiveClientsPawn(rec)) _evictScratch.Add(rec.NetId);
            for (int i = 0; i < _evictScratch.Count; i++) ForgetEntity(_evictScratch[i]);
            _evictScratch.Clear();
        }

        // ------------------------------------------------------------------------------------------- worker messages

        private void OnInterestResync(WorkerConn w, in InterestResyncMsg msg)
        {
            if (!_links.TryGetValue(w.WorkerId, out var link)) return;
            NebulaLog.Warn($"worker {w.WorkerId} could not apply our subscription (it holds seq {msg.HaveSeq}); sending a full snapshot");
            link.Sub.OnResync(msg.HaveSeq);
            FlushSubscription(link);
        }

        /// <summary>An entity left every region we subscribe on that worker: drop it, and despawn it from whoever had it.</summary>
        private void OnEntityForget(WorkerConn w, in EntityForgetMsg msg)
        {
            // Held updates only (its spawn is waiting for a row): the worker has let go of it for us.
            if (!_entities.TryGetValue(msg.NetId, out var rec)) { _held.Remove(msg.NetId); return; }
            if (rec.OwnerWorkerIndex != w.Index) return;
            if (msg.Epoch < rec.Epoch) return;
            // A client's own pawn is sticky on the worker (design D22) and must never be forgotten here: a
            // worker that sends one anyway has lost track of the session, and obeying it would take a player's
            // body out of its own set. Anything else it owns is forgotten as usual - keeping a record nobody
            // publishes any more would freeze it, and a frozen record is its own kind of wrong answer.
            if (IsLiveClientsPawn(rec)) return;
            ForgetEntity(msg.NetId);
        }

        /// <summary>
        /// Whether this record is the pawn of a client still connected here. The gateway never lets go of one
        /// on its own initiative: it is the client's only focus and the thing its prediction reconciles
        /// against, and there is no way back from dropping it except starting the join over.
        /// </summary>
        private bool IsLiveClientsPawn(EntityRecord rec) =>
            rec.OwnerClientId != 0 && _clientsById.TryGetValue(rec.OwnerClientId, out var owner) &&
            owner.DisconnectAt == 0 && owner.PawnNetId == rec.NetId;

        /// <summary>
        /// An entity we follow (by id, or because we speak for its owner) moved to another worker: point the
        /// record at the new owner straight away and link it.
        /// <para>
        /// Following it at once matters for two reasons. Every owner-index check — the <see cref="InterestLinkReason.Owned"/>
        /// link reason, world state, <see cref="OnEntityForget"/>, <see cref="DropWorkerRecords"/> — is derived
        /// from the record, so a record still naming the previous owner both fails to hold the link that keeps
        /// the pawn and lets the previous owner's stale messages through. The new owner is marked unconfirmed
        /// until its own spawn arrives, because a redirect is a promise and not an announcement.
        /// </para>
        /// </summary>
        private void OnEntityRedirect(WorkerConn from, in EntityRedirectMsg msg)
        {
            if (_entities.TryGetValue(msg.NetId, out var rec) && rec.OwnerWorkerIndex == from.Index)
            {
                rec.OwnerWorkerIndex = msg.NewWorkerIndex;
                rec.OwnerUnconfirmed = true;
            }
            _subscriptionsDirty = true;
            string workerId = WorkerIdOfIndex(msg.NewWorkerIndex);
            if (!string.IsNullOrEmpty(workerId)) Reason(workerId, InterestLinkReason.Explicit);
        }

        /// <summary>
        /// The worker holding an index, whether or not this gateway has a link to it. Resolving through the
        /// control plane rather than through <c>_workersByIndex</c> is what lets the gateway <i>dial</i> the
        /// owner of an entity it must keep: the live-connection map only knows the workers it already talks to.
        /// </summary>
        private string WorkerIdOfIndex(ushort index)
        {
            if (_workersByIndex.TryGetValue(index, out var conn)) return conn.WorkerId;
            foreach (var w in ControlPlane.Workers)
                if (w.WorkerIndex == index && w.Status != WorkerStatus.Dead) return w.WorkerId;
            return null;
        }

        // ------------------------------------------------------------------------------------------- ownership

        /// <summary>
        /// The containers this client is told about: the ones overlapping the <c>NearCells</c>
        /// window of <b>every focus its last evaluation authorized</b> — its pawn, the focus hint the gateway
        /// accepted, and the point and box foci its policy added — plus its own instance and the dynamic
        /// containers of entities in its set. Everything else is another part of the world and would be the
        /// unbounded lease table v16 broadcast to everybody.
        /// <para>
        /// The pawn alone was enough for a shooter, where the camera <i>is</i> the pawn. A strategy camera is a
        /// focus somewhere else entirely, and a chunk it is looking at that happens to hold no entity is named
        /// by no spawn: the client was given no lease row for it and so could not build the terrain under its
        /// own camera. Every authorized focus now gets the same window, deduplicated where they overlap.
        /// </para>
        /// <para>
        /// A focus that was never authorized contributes nothing, because this reads the evaluated foci and not
        /// the raw hint: a refused hint is not among them, and a pawn-less client the server has not given
        /// <see cref="FocusMode.Free"/> has no foci at all. Only public containers and the client's own scope
        /// are collected, so no instance ever sees another's rows, and an id with no lease row is never sent.
        /// </para>
        /// <para>
        /// The rows entities in the set stand in are collected <i>first</i> and are never budgeted; the windows
        /// are, at <see cref="InterestSettings.MaxContainerRows"/> per client per evaluation. That order is what
        /// makes the budget safe: exhausting it can only ever withhold empty terrain, never the row a spawn
        /// names.
        /// </para>
        /// </summary>
        private void CollectNeededContainers(ClientConn client, List<ulong> entering)
        {
            _containerIdScratch.Clear();
            _containerIdSeen.Clear();
            float cell = Config.ResolveWorldCellSize();
            float reach = cell > 0 ? _interest.NearCells(cell) * cell : _interest.ExitRadius;
            int budget = _interest.MaxContainerRows(cell);
            bool truncated = false;

            // A spawn names its container; the client must already hold the lease row for it.
            foreach (ulong netId in client.Visible) AddOf(netId);
            if (entering != null) for (int i = 0; i < entering.Count; i++) AddOf(entering[i]);

            ulong pawnNetId = 0;
            // Which scope's rows this client may hold. A scoped grid (NEB-239) is a whole world of containers at
            // coordinates another scope also uses, so a window must be queried in the client's own scope and only
            // in the public world as well when its scope looks out at it. Both directions fail closed: a public
            // client is never told about a scope's chunks, and a scoped client is never told about the public
            // world's unless its scope observes it.
            ulong scopeInstance = 0;
            bool observePublic = true;
            if (client.PawnNetId != 0 && _entities.TryGetValue(client.PawnNetId, out var pawn))
            {
                var root = RootOf(pawn);
                pawnNetId = client.PawnNetId;
                var scope = ScopeContainer(pawn.Container);
                // An unresolvable carrier chain keeps the client in the scope it was last in, and a scope nobody
                // can describe does not look out at the public world (docs/scope-activation.md D16).
                scopeInstance = ScopeOfClient(client);
                observePublic = scopeInstance == 0 || (scope != null && scope.Instance != null && scope.Instance.ObservePublic);
                // The pawn's own window first: whatever a camera is doing, the player's body must be able to
                // stand on the ground, and this is the one focus that exists before any evaluation has run.
                AddWindow(root.AbsX, root.AbsY, root.AbsZ, 0, 0, 0, reach);
                if (scope != null) Add(scope.ContainerId);
            }
            var foci = client.Interest?.Foci;
            if (foci != null)
            {
                // Points before boxes: a point window is one near-window wide and is what a camera, a hint or an
                // owned unit is, while a box can legitimately be a district. If the budget runs out it runs out
                // on the expensive one, and the cheap ones are already in.
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < foci.Count; i++)
                    {
                        var focus = foci[i];
                        if (focus.IsBox != (pass == 1)) continue;
                        // The pawn's focus is the window above; a policy naming it again must not pay twice.
                        if (!focus.IsBox && focus.SourceNetId != 0 && focus.SourceNetId == pawnNetId) continue;
                        AddWindow(focus.X, focus.Y, focus.Z, focus.HalfX, focus.HalfY, focus.HalfZ, (float)focus.Scaled(reach));
                    }
            }
            // A destination a worker asked this client to prepare (D14): pinned for the life of the crossing, whatever
            // the window says, so the client can still resolve it when the commit names it.
            if (client.PreparedRows.Count > 0)
            {
                double now = InterestNow;
                _preparedScratch.Clear();
                foreach (var id in client.PreparedRows.Keys) _preparedScratch.Add(id);
                for (int i = 0; i < _preparedScratch.Count; i++)
                    if (IsPrepared(client, _preparedScratch[i], now)) Add(_preparedScratch[i]);
                _preparedScratch.Clear();
            }
            if (truncated)
            {
                _containerBudgetHits++;
                WarnOnce(client, $"its foci cover more than {_interest.MaxContainerRows(cell)} container rows; the far ones are withheld. Use fewer or smaller foci, or a larger world cell size.");
            }

            // Every container overlapping one focus's window, up to what is left of the budget.
            void AddWindow(double x, double y, double z, double halfX, double halfY, double halfZ, float window)
            {
                if (budget <= 0) { truncated = true; return; }
                // A box focus is already clamped to InterestMaxRadius per axis by InterestQuery (design D61), so
                // this query can never walk an unbounded range of cells.
                var size = new Vector3((float)(2 * (halfX + window)), (float)(2 * (halfY + window)), (float)(2 * (halfZ + window)));
                var box = new Bounds(new Vector3((float)x, (float)y, (float)z), size);
                if (scopeInstance != 0)
                {
                    ContainerRegistry.Overlapping(box, _containerScratch, scopeInstance);
                    for (int i = 0; i < _containerScratch.Count; i++)
                    {
                        if (budget <= 0) { truncated = true; return; }
                        if (Add(_containerScratch[i].ContainerId)) budget--;
                    }
                    if (!observePublic) return;
                }
                ContainerRegistry.Overlapping(box, _containerScratch);
                for (int i = 0; i < _containerScratch.Count; i++)
                {
                    if (budget <= 0) { truncated = true; return; }
                    if (Add(_containerScratch[i].ContainerId)) budget--;
                }
            }
            void AddOf(ulong netId)
            {
                if (!_entities.TryGetValue(netId, out var rec)) return;
                // Every container up the chain, however deep: the rows a client needs to build what it is being
                // sent are the whole chain's or they are not enough (design D71). Bounded by the records held,
                // which a chain can only exceed by revisiting one.
                for (int hops = 0; hops <= _entities.Count; hops++)
                {
                    if (rec.Container.IsDynamic)
                    {
                        if (!_entities.TryGetValue(rec.Container.NetId, out var carrier)) return;
                        foreach (var kv in _ownershipById)
                            if (ContainerRegistry.CarrierNetIdOf(kv.Key) == rec.Container.NetId) Add(kv.Key);
                        rec = carrier;
                        continue;
                    }
                    var c = ContainerRegistry.Resolve(rec.Container);
                    if (c != null) Add(c.ContainerId);
                    return;
                }
            }
            bool Add(string id)
            {
                if (string.IsNullOrEmpty(id) || !_ownershipById.ContainsKey(id) || !_containerIdSeen.Add(id)) return false;
                _containerIdScratch.Add(id);
                return true;
            }
        }

        /// <summary>
        /// Bring a client's container rows in line with its foci even though nothing entered or left its set
        /// Without this refresh, a strategy camera panning over empty terrain would never be told about the
        /// chunks it is looking at: <see cref="ApplyInterestChanges"/> is the only other place rows are sent,
        /// and it does nothing when no entity changed. Runs once per evaluation, on the evaluation's own foci,
        /// and sends nothing at all when the window has not moved.
        /// </summary>
        private void SyncOwnershipWindow(ClientConn client)
        {
            if (!client.Welcomed) return;
            CollectNeededContainers(client, null);
            // The same order §8 requires everywhere else: upserts, then the removes they may have replaced.
            FlushOwnershipUpserts(client);
            FlushOwnershipRemoves(client);
        }

        /// <summary>Upserts go out before the spawns that name them, on the same reliable batch.</summary>
        private void SendOwnershipUpserts(ClientConn client, List<ulong> entering)
        {
            CollectNeededContainers(client, entering);
            FlushOwnershipUpserts(client);
        }

        /// <summary>The rows in <see cref="_containerIdScratch"/> this client has not been sent yet.</summary>
        private void FlushOwnershipUpserts(ClientConn client)
        {
            _upsertScratch.Clear();
            for (int i = 0; i < _containerIdScratch.Count; i++)
            {
                string id = _containerIdScratch[i];
                if (!client.KnownContainers.Add(id)) continue;
                if (_ownershipById.TryGetValue(id, out var entry)) _upsertScratch.Add(entry);
            }
            if (_upsertScratch.Count == 0) return;
            _interestWriter.Reset();
            ContainerOwnershipMsg.Write(_interestWriter, _upsertScratch, false, null);
            AppendReliable(client, _interestWriter.ToSegment());
        }

        /// <summary>
        /// An entity that is already in a client's set has moved into a different container: make sure that
        /// client holds the container's lease row before the state entry naming it arrives.
        /// <para>
        /// <see cref="SendOwnershipUpserts"/> only runs for entities <i>entering</i> a set, which is enough in a
        /// baked world where an entity spends its life in one cell. In a chunked world a walking pawn changes
        /// container every few seconds without ever leaving anyone's set, and the observers were never told
        /// about the new chunk: they kept the replica, could not resolve its frame, and so froze it at its last
        /// known pose for ever. That shows up in <c>InterestProbe</c> as a permanent <c>orphans=1</c> and a
        /// <c>beyond</c> distance growing at exactly the observer's own walking speed.
        /// </para>
        /// </summary>
        private void SendOwnershipForContainerChange(EntityRecord rec)
        {
            if (rec.Observers.Count == 0) return;
            var container = ContainerRegistry.Resolve(rec.Container);
            string id = container != null ? container.ContainerId : null;
            if (string.IsNullOrEmpty(id) || !_ownershipById.TryGetValue(id, out var entry)) return;
            for (int i = rec.Observers.Count - 1; i >= 0; i--)
            {
                var client = rec.Observers[i];
                if (!client.Welcomed || !client.KnownContainers.Add(id)) continue;
                _upsertScratch.Clear();
                _upsertScratch.Add(entry);
                _interestWriter.Reset();
                ContainerOwnershipMsg.Write(_interestWriter, _upsertScratch, false, null);
                AppendReliable(client, _interestWriter.ToSegment());
            }
        }

        /// <summary>And removes only afterwards, once the entities that lived in them have been despawned.</summary>
        private void SendOwnershipRemoves(ClientConn client)
        {
            CollectNeededContainers(client, null);
            FlushOwnershipRemoves(client);
        }

        /// <summary>
        /// The rows this client holds that <see cref="_containerIdScratch"/> no longer asks for. A row whose lease is
        /// gone goes at once — the container is retired and whatever stood in it has been despawned. A row that only
        /// left the window lingers for <see cref="ContainerRowLingerSeconds"/> first: a replica that has just moved
        /// out of it (a fast ship, or a whole crew whose scope just changed) is still gliding out of that box
        /// through its interpolation buffer, and a client despawns whatever stands in a row it is told to drop
        /// (docs/scope-activation.md D17).
        /// </summary>
        private void FlushOwnershipRemoves(ClientConn client)
        {
            _removeScratch.Clear();
            double now = InterestNow;
            foreach (string id in client.KnownContainers)
            {
                if (_containerIdSeen.Contains(id)) { client.RowsLeaving.Remove(id); continue; }
                if (_ownershipById.ContainsKey(id))
                {
                    if (!client.RowsLeaving.TryGetValue(id, out double since)) { client.RowsLeaving[id] = now; continue; }
                    if (now - since < ContainerRowLingerSeconds) continue;
                }
                _removeScratch.Add(id);
            }
            if (_removeScratch.Count == 0) return;
            for (int i = 0; i < _removeScratch.Count; i++) client.RowsLeaving.Remove(_removeScratch[i]);
            for (int i = 0; i < _removeScratch.Count; i++) client.KnownContainers.Remove(_removeScratch[i]);
            _interestWriter.Reset();
            ContainerOwnershipMsg.Write(_interestWriter, Array.Empty<ContainerOwnershipEntry>(), false, _removeScratch);
            AppendReliable(client, _interestWriter.ToSegment());
        }

        /// <summary>A client that just joined gets a Full snapshot of the containers it needs, and nothing else.</summary>
        private void SendOwnershipFull(ClientConn client)
        {
            CollectNeededContainers(client, null);
            client.KnownContainers.Clear();
            client.RowsLeaving.Clear();
            _upsertScratch.Clear();
            for (int i = 0; i < _containerIdScratch.Count; i++)
            {
                client.KnownContainers.Add(_containerIdScratch[i]);
                if (_ownershipById.TryGetValue(_containerIdScratch[i], out var entry)) _upsertScratch.Add(entry);
            }
            _interestWriter.Reset();
            ContainerOwnershipMsg.Write(_interestWriter, _upsertScratch, true, null);
            AppendReliable(client, _interestWriter.ToSegment());
        }

        /// <summary>A lease changed: re-send the rows the clients that hold them already have.</summary>
        private void RefreshOwnership()
        {
            foreach (var client in _clientsById.Values)
            {
                if (!client.Welcomed) continue;
                _upsertScratch.Clear();
                foreach (string id in client.KnownContainers)
                    if (_ownershipById.TryGetValue(id, out var entry)) _upsertScratch.Add(entry);
                if (_upsertScratch.Count == 0) continue;
                _interestWriter.Reset();
                ContainerOwnershipMsg.Write(_interestWriter, _upsertScratch, false, null);
                AppendReliable(client, _interestWriter.ToSegment());
            }
        }

        // ------------------------------------------------------------------------------------------- stats

        /// <summary>The interest half of <see cref="GatewayStats"/>; the counters then start over.</summary>
        private void FillInterestStats(ref GatewayStats stats, double interval)
        {
            long sum = 0;
            uint max = 0, clients = 0;
            foreach (var c in _clientsById.Values)
            {
                if (!c.Welcomed) continue;
                clients++;
                sum += c.Visible.Count;
                if (c.Visible.Count > max) max = (uint)c.Visible.Count;
            }
            stats.InterestSetAvg = clients > 0 ? (float)sum / clients : 0f;
            stats.InterestSetMax = max;
            stats.CachedEntities = (uint)_entities.Count;
            stats.SubscribedRegions = (uint)_subscribedRegions.Count;
            stats.WorkerLinks = (uint)_links.Count;
            stats.WorkerLinkReasons = DescribeLinkReasons();
            stats.SpawnsPerSecond = (float)(_spawnsSent / interval);
            stats.DespawnsPerSecond = (float)(_despawnsSent / interval);
            stats.InterestEvalMsAvg = _evalCount > 0 ? (float)(_evalMsSum / _evalCount) : 0f;
            stats.InterestEvalMsMax = (float)_evalMsMax;
            // Clients x EvalHz when the rotation is keeping up. Well below it means evaluations are being
            // starved; well above it means something is marking clients dirty far more often than it should.
            stats.InterestEvalsPerSecond = interval > 0 ? (float)(_evalCount / interval) : 0f;
            stats.BytesPerClientAvg = clients > 0 ? (float)(_clientBytesOut / interval / clients) : 0f;
            stats.BytesPerClientMax = clients > 0 ? (float)(_maxClientBytesOut / interval) : 0f;
            _spawnsSent = _despawnsSent = 0;
            _evalMsSum = _evalMsMax = 0;
            _evalCount = 0;
        }

        private string DescribeLinkReasons()
        {
            int region = 0, foci = 0, global = 0, explicitId = 0, spawn = 0, owned = 0;
            foreach (var link in _links.Values)
            {
                if ((link.Reasons & InterestLinkReason.Region) != 0) region++;
                if ((link.Reasons & InterestLinkReason.Foci) != 0) foci++;
                if ((link.Reasons & InterestLinkReason.Global) != 0) global++;
                if ((link.Reasons & InterestLinkReason.Explicit) != 0) explicitId++;
                if ((link.Reasons & InterestLinkReason.Spawn) != 0) spawn++;
                if ((link.Reasons & InterestLinkReason.Owned) != 0) owned++;
            }
            return $"region={region},foci={foci},global={global},explicit={explicitId},spawn={spawn},owned={owned}";
        }
    }
}
