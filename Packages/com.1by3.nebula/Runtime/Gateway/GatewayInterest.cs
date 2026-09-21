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
    /// Why a gateway holds a link to a worker (design §5). A link with no reason left is dropped after
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

        /// <summary>Resolved once per control-plane change: which workers a region's entities could live on.</summary>
        private readonly Dictionary<ulong, List<string>> _regionWorkers = new Dictionary<ulong, List<string>>();
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
        private readonly List<string> _containerIdScratch = new List<string>();
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
        private double _evalMsSum, _evalMsMax;
        private long _evalCount;
        private bool _subscriptionsDirty = true;

        /// <summary>
        /// The game's say in what each client hears about (design §7). Defaults to
        /// <see cref="DefaultInterestPolicy"/>: one focus at the pawn plus the client's validated hint. Compose
        /// several with <see cref="InterestPolicies.Combine"/>. Setting it re-evaluates every client at once.
        /// </summary>
        public IInterestPolicy InterestPolicy
        {
            get => _policy;
            set
            {
                _policy = value ?? DefaultInterestPolicy.Instance;
                foreach (var c in _clientsById.Values) c.InterestDirty = true;
            }
        }

        /// <summary>The resolved interest knobs this gateway runs on (clamped; see <see cref="InterestSettings.Validate"/>).</summary>
        public InterestSettings InterestSettingsInUse => _interest;
        /// <summary>The region grid both ends must agree on; it travels in every subscription message.</summary>
        public InterestGrid InterestGridInUse => _interestGrid;
        /// <summary>Entity records cached: what this gateway's clients' subscriptions bring in, not the world.</summary>
        public int CachedEntityCount => _entities.Count;
        /// <summary>Distinct regions subscribed across every worker link.</summary>
        public int SubscribedRegionCount => _subscribedRegions.Count;
        /// <summary>Worker links currently held, for a test or an operator that wants the number without the heartbeat.</summary>
        public int WorkerLinkCount => _links.Count;

        /// <summary>
        /// A byte the game attaches to a client for its policy to filter on (team, faction, party). It is server
        /// state: the client never sends it, which is what makes a team filter a security boundary rather than a
        /// suggestion. Changing it re-evaluates the client at once.
        /// </summary>
        public void SetClientTag(ulong clientId, byte team)
        {
            if (!_clientsById.TryGetValue(clientId, out var c) || c.Team == team) return;
            c.Team = team;
            c.InterestDirty = true;
        }

        /// <summary>What <see cref="SetClientTag"/> last set for this client (0 by default).</summary>
        public byte GetClientTag(ulong clientId) => _clientsById.TryGetValue(clientId, out var c) ? c.Team : (byte)0;

        /// <summary>
        /// Re-evaluate this client on the next tick rather than at its next scheduled evaluation. The game calls
        /// it when something its policy depends on changed (a party, a fog-of-war reveal); the gateway calls it
        /// when the pawn, instance, carrier or focus region changed.
        /// </summary>
        public void MarkInterestDirty(ulong clientId)
        {
            if (_clientsById.TryGetValue(clientId, out var c)) c.InterestDirty = true;
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
        /// space and a region key computed here never moves under a client (design §3).
        /// </summary>
        private void RefreshAbsolute(EntityRecord rec)
        {
            var world = WorldPosition(rec.Container, rec.LastSpawn.LocalPosition, 0);
            rec.AbsX = world.x;
            rec.AbsY = world.y;
            rec.AbsZ = world.z;
        }

        /// <summary>
        /// The entity the interest decision is made on: itself, or the outermost carrier it rides in. Carried
        /// entities inherit their root carrier's decision so a passenger cannot pop separately from its ship.
        /// </summary>
        private EntityRecord RootOf(EntityRecord rec)
        {
            for (int depth = 0; depth < 8 && rec.Container.IsDynamic; depth++)
            {
                if (!_entities.TryGetValue(rec.Container.NetId, out var carrier)) break;
                rec = carrier;
            }
            return rec;
        }

        private ulong RegionOf(EntityRecord rec)
        {
            var root = RootOf(rec);
            return _interestGrid.RegionOf(root.AbsX, root.AbsY, root.AbsZ);
        }

        // ------------------------------------------------------------------------------------------- the index

        /// <summary>
        /// Put a record where the scans will find it: the global list when its prefab is always relevant, the wide
        /// list when its own radius outreaches a region scan, otherwise the region its root carrier sits in.
        /// </summary>
        private void IndexEntity(EntityRecord rec)
        {
            RefreshAbsolute(rec);
            if (rec.AlwaysRelevant) { _index.AddGlobal(rec.NetId, rec); rec.Region = 0; rec.Placement = InterestPlacement.Global; }
            else if (rec.RelevanceRadius > _interest.Radius) { _index.AddWide(rec.NetId, rec); rec.Region = 0; rec.Placement = InterestPlacement.Wide; }
            else
            {
                ulong region = RegionOf(rec);
                _index.Add(rec.NetId, region, rec);
                rec.Region = region;
                rec.Placement = InterestPlacement.Region;
            }
            _index.SetCarrier(rec.NetId, rec.Container.IsDynamic ? rec.Container.NetId : 0);
        }

        /// <summary>Recompute the key only when the pose moved it: one multiply and floor per axis, then nothing.</summary>
        private void RebucketIfMoved(EntityRecord rec)
        {
            if (rec.Placement != InterestPlacement.Region) { RefreshAbsolute(rec); _index.SetValue(rec.NetId, rec); return; }
            RefreshAbsolute(rec);
            ulong region = RegionOf(rec);
            if (region == rec.Region) return;
            rec.Region = region;
            _index.Move(rec.NetId, region);
            OnEntityArrived(rec);
        }

        private void UnindexEntity(EntityRecord rec) => _index.Remove(rec.NetId);

        /// <summary>
        /// An entity spawned into, or rebucketed into, a region: test it against the clients whose subscribe discs
        /// cover that region and nobody else. This is the whole point of the region → clients map — without it
        /// every spawn in the world would cost one test per connected client.
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

        /// <summary>Drop the client from every entity that had it as an observer (it disconnected, or its session moved).</summary>
        private void ForgetClientInterest(ClientConn client)
        {
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
            _hintFilter.Forget(client.ClientId);
            _subscriptionsDirty = true;
        }

        // ------------------------------------------------------------------------------------------- evaluation

        /// <summary>
        /// Everything interest does once per tick: evaluate the clients whose turn it is (staggered across ticks
        /// so a hundred clients never all evaluate on the same one), then bring the worker subscriptions in line.
        /// </summary>
        private void TickInterest()
        {
            double now = InterestNow;
            int count = _clientsById.Count;
            if (count > 0)
            {
                // A client is evaluated at InterestEvalHz. Rather than keep a timer wheel, walk a cursor over the
                // clients each tick at the rate that gets through all of them in one eval interval.
                foreach (var client in _clientsById.Values)
                {
                    if (!client.Welcomed) continue;
                    if (!client.InterestDirty && now < client.NextInterestEval) continue;
                    EvaluateClient(client, now);
                }
            }
            if (now >= _nextSubscriptionAt || _subscriptionsDirty)
            {
                _nextSubscriptionAt = now + Math.Max(0.05, _interest.EvalInterval);
                _subscriptionsDirty = false;
                UpdateSubscriptions(now);
            }
        }

        private void EvaluateClient(ClientConn client, double now)
        {
            long started = Stopwatch.GetTimestamp();
            client.InterestDirty = false;
            client.NextInterestEval = now + _interest.EvalInterval;
            client.Interest ??= new ClientInterest<EntityRecord, InterestSource>(new InterestSource(this));
            client.Interest.Settings = _interest;
            client.Interest.Grid = _interestGrid;
            client.Query ??= new InterestQuery();

            var snapshot = SnapshotClient(client);
            client.Interest.Client = snapshot;
            client.Query.Reset(_interest);
            _policy.Collect(snapshot, client.Query);
            AddObservationWindows(client, snapshot, client.Query);

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
            };
            if (client.PawnNetId != 0 && _entities.TryGetValue(client.PawnNetId, out var pawn))
            {
                var root = RootOf(pawn);
                snapshot.HasPawn = true;
                snapshot.PawnX = root.AbsX; snapshot.PawnY = root.AbsY; snapshot.PawnZ = root.AbsZ;
                snapshot.InstanceId = ScopeContainer(pawn.Container)?.InstanceId ?? 0;
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
            if (client.PawnNetId == 0 || !_entities.TryGetValue(client.PawnNetId, out var pawn)) return;
            var view = ScopeContainer(pawn.Container)?.Instance;
            if (view == null || !view.ObservePublic) return;
            query.AddFocus(InterestFocus.Box(view.ObservationCenter.x, view.ObservationCenter.y, view.ObservationCenter.z,
                view.ObservationSize.x, view.ObservationSize.y, view.ObservationSize.z));
        }

        /// <summary>
        /// Authorization, in the order design §4 requires: the instance rules first (an unknown container is never
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
            client.NextRegions.Clear();
            for (int i = 0; i < _regionScratch.Count; i++)
            {
                ulong region = _regionScratch[i];
                if (!client.NextRegions.Add(region)) continue;
                if (client.Regions.Contains(region)) continue;
                changed = true;
                if (!_regionClients.TryGetValue(region, out var list)) _regionClients[region] = list = new List<ClientConn>(4);
                list.Add(client);
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
        /// containers nothing needs any more (design §8).
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

        /// <summary>A spawn carries the newest pose and the newest keyframe of every behaviour, plus a fresh view sequence.</summary>
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
        /// A client told us where it is looking. It is an input: non-finite values are dropped, the rate is
        /// capped, and the point is clamped to <see cref="InterestSettings.HintMaxDistance"/> of the pawn. A
        /// client cannot make the gateway stream it the world by claiming to look at it.
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
            client.HintGeneration = msg.Generation;

            bool hasPawn = false;
            double px = 0, py = 0, pz = 0;
            if (client.PawnNetId != 0 && _entities.TryGetValue(client.PawnNetId, out var pawn))
            {
                var root = RootOf(pawn);
                hasPawn = true; px = root.AbsX; py = root.AbsY; pz = root.AbsZ;
            }
            if (!_hintFilter.TryAccept(client.ClientId, InterestNow, msg.Position.x, msg.Position.y, msg.Position.z,
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
        /// Bring every worker link in line with what the clients need (design §5): resolve the needed regions to
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
            bool unreachablePawn = false;

            foreach (var client in _clientsById.Values)
            {
                if (!client.Welcomed) continue;
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
                        ulong key = _interestGrid.RegionOf(foci[i].X, foci[i].Y, foci[i].Z);
                        if (!_fociRegions.Contains(key)) _fociRegions.Add(key);
                        LinkNearbyOwners(foci[i]);
                    }
                    if (client.Query != null)
                    {
                        var extras = client.Query.Entities;
                        for (int i = 0; i < extras.Count; i++) if (!_explicitIds.Contains(extras[i])) _explicitIds.Add(extras[i]);
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
        /// The repair is design D5's mechanism turned on the pawn: subscribe it <b>by name</b> on every live
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
                if (!_explicitIds.Contains(client.PawnNetId)) _explicitIds.Add(client.PawnNetId);
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
            _interestGrid.BoundsOf(region, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ);
            float margin = Config.GhostBandMargin + Config.HandoverHysteresis;
            // A planar region is an infinite column; a finite box tall enough to hold any world is what a
            // container query can answer, and the exact per-entity test happens on the gateway anyway.
            if (double.IsInfinity(minY)) { minY = -10000; maxY = 10000; }
            var center = new Vector3((float)((minX + maxX) / 2), (float)((minY + maxY) / 2), (float)((minZ + maxZ) / 2));
            var size = new Vector3((float)(maxX - minX) + 2 * margin, (float)(maxY - minY) + 2 * margin, (float)(maxZ - minZ) + 2 * margin);
            ContainerRegistry.Overlapping(new Bounds(center, size), _containerScratch);
            for (int i = 0; i < _containerScratch.Count; i++)
            {
                string id = _containerScratch[i].OwnerWorkerId;
                if (!string.IsNullOrEmpty(id) && !owners.Contains(id)) owners.Add(id);
            }
            if (owners.Count == 0)
            {
                var nearest = ContainerRegistry.Find(center);
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
        /// </summary>
        private void EvictUnsubscribedRecords()
        {
            _evictScratch.Clear();
            foreach (var rec in _entities.Values)
            {
                if (rec.Placement != InterestPlacement.Region) continue;
                if (rec.OwnerClientId != 0 && _clientsById.ContainsKey(rec.OwnerClientId)) continue;
                if (_subscribedRegions.Contains(rec.Region)) continue;
                _evictScratch.Add(rec.NetId);
            }
            for (int i = 0; i < _evictScratch.Count; i++) ForgetEntity(_evictScratch[i]);
            _evictScratch.Clear();
        }

        /// <summary>Forget one record entirely: out of the index, out of every set, despawned from every observer.</summary>
        private void ForgetEntity(ulong netId)
        {
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
            if (!_entities.TryGetValue(msg.NetId, out var rec) || rec.OwnerWorkerIndex != w.Index) return;
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
        /// The containers this client is told about (design §8): the ones overlapping its <c>NearCells</c> window,
        /// its own instance, and the dynamic containers of entities in its set. Everything else is another part of
        /// the world and would be the unbounded lease table v16 broadcast to everybody.
        /// </summary>
        private void CollectNeededContainers(ClientConn client, List<ulong> entering)
        {
            _containerIdScratch.Clear();
            float cell = Config.ResolveWorldCellSize();
            float reach = cell > 0 ? _interest.NearCells(cell) * cell : _interest.ExitRadius;
            if (client.PawnNetId != 0 && _entities.TryGetValue(client.PawnNetId, out var pawn))
            {
                var root = RootOf(pawn);
                var box = new Bounds(new Vector3((float)root.AbsX, (float)root.AbsY, (float)root.AbsZ), new Vector3(2 * reach, 2 * reach, 2 * reach));
                ContainerRegistry.Overlapping(box, _containerScratch);
                for (int i = 0; i < _containerScratch.Count; i++) Add(_containerScratch[i].ContainerId);
                var scope = ScopeContainer(pawn.Container);
                if (scope != null) Add(scope.ContainerId);
            }
            // A spawn names its container; the client must already hold the lease row for it.
            foreach (ulong netId in client.Visible) AddOf(netId);
            if (entering != null) for (int i = 0; i < entering.Count; i++) AddOf(entering[i]);

            void AddOf(ulong netId)
            {
                if (!_entities.TryGetValue(netId, out var rec)) return;
                for (int depth = 0; depth < 8; depth++)
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
            void Add(string id)
            {
                if (!string.IsNullOrEmpty(id) && _ownershipById.ContainsKey(id) && !_containerIdScratch.Contains(id)) _containerIdScratch.Add(id);
            }
        }

        /// <summary>Upserts go out before the spawns that name them, on the same reliable batch.</summary>
        private void SendOwnershipUpserts(ClientConn client, List<ulong> entering)
        {
            CollectNeededContainers(client, entering);
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
            _removeScratch.Clear();
            foreach (string id in client.KnownContainers)
                if (!_containerIdScratch.Contains(id)) _removeScratch.Add(id);
            if (_removeScratch.Count == 0) return;
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

        /// <summary>The interest half of <see cref="GatewayStats"/> (design §12), and the counters start over.</summary>
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
