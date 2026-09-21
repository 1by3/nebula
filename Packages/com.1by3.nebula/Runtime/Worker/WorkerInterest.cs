using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The worker's half of interest management (design §5, §6): an index of the entities this worker owns keyed by
    /// region, one subscription set per gateway link, and the mask arithmetic that decides which gateways hear about
    /// each entity.
    /// <para>
    /// Before v17 a worker announced every authoritative entity to every gateway on <c>Hello</c> and streamed all of
    /// them every tick, so a gateway's ingest grew with the world rather than with its clients. Now a gateway is told
    /// only what it subscribed: the regions its clients' foci cover, the entities it named explicitly, the pawns of
    /// the sessions it speaks for, and the always-relevant ones. Everything here is keyed by <b>absolute</b> world
    /// position in double, so a floating-origin shift changes no key and never causes a mass rebucket.
    /// </para>
    /// </summary>
    public sealed partial class NebulaWorker
    {
        /// <summary>What a gateway link is told about one entity, accumulated once per tick in <see cref="_entityMask"/>.</summary>
        private readonly Dictionary<ulong, ulong> _entityMask = new Dictionary<ulong, ulong>();
        /// <summary>Authoritative entities by region (or in the wide / global lists). Keys are absolute; see <see cref="RegionOf"/>.</summary>
        private readonly InterestIndex<NetworkIdentity> _index = new InterestIndex<NetworkIdentity>();
        private readonly RegionPublisher _publisher = new RegionPublisher();
        /// <summary>Gateway peer per mask bit; null where the bit is free.</summary>
        private readonly Peer[] _gatewayBits = new Peer[RegionPublisher.MaxGateways];
        /// <summary>
        /// Gateways past <see cref="RegionPublisher.MaxGateways"/>: there is no bit left for them, so they cannot be
        /// filtered and receive everything (correct, but unfiltered). Loudly reported once per link.
        /// </summary>
        private readonly List<Peer> _unmaskedGateways = new List<Peer>();
        /// <summary>netId → gateways that named it in <c>InterestSubscribe.Entities</c>. Rebuilt on every committed apply.</summary>
        private readonly Dictionary<ulong, ulong> _explicitMask = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, ulong> _explicitPrevious = new Dictionary<ulong, ulong>();
        /// <summary>netId → gateways a wide entity currently reaches. Re-evaluated at <see cref="InterestSettings.EvalHz"/>, with the exit margin as hysteresis.</summary>
        private readonly Dictionary<ulong, ulong> _wideMask = new Dictionary<ulong, ulong>();
        private readonly List<ulong> _wideScratch = new List<ulong>();
        /// <summary>This tick's entities grouped by the exact set of gateways they go to, so one batch serves them all.</summary>
        private readonly Dictionary<ulong, List<NetworkIdentity>> _byMask = new Dictionary<ulong, List<NetworkIdentity>>();
        private readonly Stack<List<NetworkIdentity>> _maskListPool = new Stack<List<NetworkIdentity>>();
        private readonly List<ulong> _maskOrder = new List<ulong>();
        private readonly List<NetworkIdentity> _spawnOrder = new List<NetworkIdentity>();
        private readonly Dictionary<Container, int> _containerCounts = new Dictionary<Container, int>();

        private InterestSettings _interest = InterestSettings.Default;
        private InterestGrid _interestGrid;
        private Vector3Int _originCell;
        private Vector3 _originCellSize;
        private float _nextWideEval;
        private float _nextPartitionCheck;
        private float _nextPartitionLog;
        private bool _interestCountMismatchLogged;
        private readonly Stopwatch _filterWatch = new Stopwatch();
        private static readonly ProfileSection ProfInterest = NebulaProfiler.Section("interest");

        /// <summary>Seconds between two "partition this container" log lines, however often the condition holds.</summary>
        private const float PartitionWarnLogSeconds = 60f;

        // ------------------------------------------------------------------------------------------- public state

        /// <summary>The grid this worker makes region ids with; a gateway subscribing with a different one is rejected (design D2).</summary>
        public InterestGrid InterestGrid => _interestGrid;
        /// <summary>Regions at least one gateway subscribes here.</summary>
        public int SubscribedRegions => _publisher.SubscribedRegions;
        /// <summary>Authoritative entities that are in every client's set, so gateways must link this worker to hear them (design §5).</summary>
        public bool HasGlobalEntities => _index.GlobalCount > 0;
        /// <summary>State entries actually sent to gateways since start-up, and what an unfiltered worker would have sent.</summary>
        public long InterestEntriesSent { get; private set; }
        public long InterestEntriesTotal { get; private set; }
        /// <summary>Bytes of world state actually sent, and what the same tick would have cost sent to every gateway.</summary>
        public long InterestBytesSent { get; private set; }
        public long InterestBytesUnfiltered { get; private set; }
        /// <summary>Smoothed milliseconds per tick spent deciding who hears about what (design §11's second warning).</summary>
        public float InterestFilterMs { get; private set; }
        /// <summary>Non-empty while this worker wants the world partitioned; shown on the dashboard and logged once a minute.</summary>
        public string PartitionWarning { get; private set; } = "";

        /// <summary>Per gateway link, what interest management is doing for it. For telemetry and the debug overlay.</summary>
        public struct GatewayInterest
        {
            public string GatewayId;
            public int Regions;
            public int Foci;
            public int ExplicitEntities;
            public long EntriesSent;
            public long BytesSent;
        }

        /// <summary>Snapshot the per-gateway interest counters into <paramref name="result"/> (cleared first). Main thread.</summary>
        public void CopyGatewayInterest(List<GatewayInterest> result)
        {
            result.Clear();
            for (int i = 0; i < _gateways.Count; i++)
            {
                var g = _gateways[i];
                var receiver = g.Subscription;
                result.Add(new GatewayInterest
                {
                    GatewayId = g.Id,
                    Regions = receiver != null ? receiver.Count : 0,
                    Foci = receiver != null ? receiver.FociRegions.Count : 0,
                    ExplicitEntities = receiver != null ? receiver.Entities.Count : 0,
                    EntriesSent = g.EntriesSent,
                    BytesSent = g.BytesSent,
                });
            }
        }

        // ------------------------------------------------------------------------------------------- set-up

        /// <summary>Resolve the interest settings and the grid from the config. Called once, from <see cref="Initialize"/>.</summary>
        private void InitializeInterest()
        {
            _interest = Config.ToInterestSettings();
            _interestGrid = Config.ToInterestGrid();
            CacheOrigin();
            // The issues themselves are logged once by NebulaBootstrap; this line is what the worker actually runs on.
            NebulaLog.Info($"interest: radius {_interest.Radius} m (+{_interest.ExitMargin} exit), {_interestGrid}, eval {_interest.EvalHz} Hz");
        }

        // ------------------------------------------------------------------------------------------- keys

        /// <summary>
        /// Cache the floating origin for this tick. Keys are made from absolute coordinates, so the conversion has to
        /// use the same origin for every entity in a tick even if the streamer shifts in the middle of a frame.
        /// </summary>
        private void CacheOrigin()
        {
            var world = WorldOrigin.Definition;
            _originCell = world != null ? WorldOrigin.Cell : Vector3Int.zero;
            _originCellSize = world != null ? world.CellSize : Vector3.zero;
        }

        /// <summary>A frame position as absolute world coordinates, in double: the only input a region key is made from.</summary>
        private void ToAbsolute(Vector3 frame, out double x, out double y, out double z) =>
            ToAbsolute(_originCell, _originCellSize, frame, out x, out y, out z);

        /// <summary>
        /// Frame position plus floating origin as an absolute position, in double. Pure: this is the whole reason
        /// a region key does not move when the origin does, so it is worth being able to test on its own.
        /// </summary>
        internal static void ToAbsolute(Vector3Int originCell, Vector3 cellSize, Vector3 frame, out double x, out double y, out double z)
        {
            x = (double)originCell.x * cellSize.x + frame.x;
            y = (double)originCell.y * cellSize.y + frame.y;
            z = (double)originCell.z * cellSize.z + frame.z;
        }

        /// <summary>The region a frame position falls in for a given origin. Pure counterpart of <see cref="RegionOf"/>.</summary>
        internal static ulong RegionOfFrame(in InterestGrid grid, Vector3Int originCell, Vector3 cellSize, Vector3 frame)
        {
            ToAbsolute(originCell, cellSize, frame, out double x, out double y, out double z);
            return grid.RegionOf(x, y, z);
        }

        /// <summary>The region an entity belongs in, from its absolute position. Origin-shift invariant by construction.</summary>
        private ulong RegionOf(NetworkIdentity e) => RegionOfFrame(_interestGrid, _originCell, _originCellSize, e.transform.position);

        /// <summary>
        /// Where an entity belongs in the index and how far it reaches: always-relevant prefabs are global, a prefab
        /// whose own relevance radius exceeds the mesh radius is wide (a client's region scan would never find it),
        /// everything else is bucketed by region. Pure, so the decision is testable without a mesh.
        /// </summary>
        internal static InterestPlacement PlacementOf(bool alwaysRelevant, float relevanceRadius, in InterestSettings settings, out float radius)
        {
            radius = relevanceRadius > 0 ? Math.Min(relevanceRadius, settings.MaxRadius) : settings.Radius;
            if (alwaysRelevant) return InterestPlacement.Global;
            return radius > settings.Radius ? InterestPlacement.Wide : InterestPlacement.Region;
        }

        /// <summary>
        /// Which gateways hear about one entity: the ones subscribing its region (or reached by it, when it is wide,
        /// or every link, when it is global), plus the ones that named it explicitly, plus the gateway speaking for
        /// its owner. Pure: the four inputs are looked up once per tick and combined here.
        /// </summary>
        internal static ulong EffectiveMask(InterestPlacement placement, ulong regionMask, ulong wideMask, ulong linkedMask, ulong stickyMask)
        {
            ulong spatial = placement switch
            {
                InterestPlacement.Global => linkedMask,
                InterestPlacement.Wide => wideMask,
                _ => regionMask,
            };
            return spatial | stickyMask;
        }

        /// <summary>
        /// An entity moved from one region to another: which gateways have never heard of it and need a spawn, and
        /// which subscribed only the region it left and are told to forget it. <paramref name="stickyMask"/> is the
        /// set that keeps it regardless of region (its owner's gateway, explicit subscribers), which must never be
        /// sent a forget: the entity is still theirs. Pure.
        /// </summary>
        internal static void RebucketBits(ulong fromMask, ulong toMask, ulong stickyMask, out ulong spawn, out ulong forget)
        {
            spawn = toMask & ~fromMask & ~stickyMask;
            forget = fromMask & ~toMask & ~stickyMask;
        }

        // ------------------------------------------------------------------------------------------- index upkeep

        /// <summary>This worker gained authority over an entity: index it, and link it to its carrier (design D3).</summary>
        private void InterestAdd(NetworkIdentity e)
        {
            if (e == null || e.NetId == 0) return;
            CacheOrigin(); // a spawn can happen outside the tick, before this frame's origin was cached
            var placement = PlacementOf(e.AlwaysRelevant, e.RelevanceRadius, _interest, out _);
            // A carrier must stay a region entity. InterestIndex moves a carrier's subtree only when the carrier
            // is bucketed by region (design D3), so a dynamic container in the wide or global list would leave
            // everything riding in it bucketed wherever it was boarded, and passengers would enter and leave
            // clients' sets independently of the ship. Downgrade rather than obey, and say so once.
            if (placement != InterestPlacement.Region && IsCarrier(e))
            {
                if (!_carrierPlacementWarned.Contains(e.PrefabId))
                {
                    _carrierPlacementWarned.Add(e.PrefabId);
                    NebulaLog.Warn($"prefab {e.PrefabId} is a dynamic container with AlwaysRelevant or RelevanceRadius {e.RelevanceRadius} m (over InterestRadius {_interest.Radius} m). A carrier cannot be a wide or global entity without stranding what it carries, so it is bucketed by region instead. Raise InterestRadius if this ship needs to be seen further away.");
                }
                placement = InterestPlacement.Region;
            }
            switch (placement)
            {
                case InterestPlacement.Global: _index.AddGlobal(e.NetId, e); break;
                case InterestPlacement.Wide: _index.AddWide(e.NetId, e); break;
                default: _index.Add(e.NetId, RegionOf(e), e); break;
            }
            _index.SetCarrier(e.NetId, CarrierOf(e));
        }

        /// <summary>Whether this entity is itself a dynamic container other entities can ride in (a ship, a lift).</summary>
        private static bool IsCarrier(NetworkIdentity e)
        {
            var box = e.GetComponent<Container>();
            return box != null && box.IsDynamic;
        }

        /// <summary>Prefab ids already reported for an impossible carrier placement, so the warning is said once.</summary>
        private readonly HashSet<ushort> _carrierPlacementWarned = new HashSet<ushort>();

        /// <summary>The net id of the entity carrying this one (0 when it is not inside a dynamic container).</summary>
        private static ulong CarrierOf(NetworkIdentity e)
        {
            var c = e.Container;
            return c != null && c.IsDynamic && c.Carrier != null ? c.Carrier.NetId : 0;
        }

        /// <summary>Authority was lost or the entity is gone: it leaves the index (its despawn/forget is sent separately).</summary>
        private void InterestRemove(ulong netId)
        {
            _index.Remove(netId);
            _wideMask.Remove(netId);
            // It is not ours any more, so there is nothing to announce to a gateway that has yet to link here.
            if (_awaitedByGateway.Count > 0) foreach (var ids in _awaitedByGateway.Values) ids.Remove(netId);
        }

        /// <summary>
        /// Rebucket what moved, once per tick per authoritative entity and only when the key actually changed, and
        /// tell the gateways that gained or lost the entity as a result. A carried entity is never moved by itself:
        /// <see cref="InterestIndex{T}"/> moves a carrier's whole subtree with it, so a passenger cannot pop out of
        /// a client's set separately from the ship it rides in.
        /// </summary>
        private void UpdateInterestIndex()
        {
            // Every path that gains or loses authority maintains the index; this is the safety net. A stale entry
            // would be spawned to the next gateway that subscribes its region, so the index is rebuilt outright
            // rather than left to drift - it costs one pass over what this worker owns, and only when it happens.
            if (_index.Count != _authoritative.Count)
            {
                if (!_interestCountMismatchLogged)
                {
                    _interestCountMismatchLogged = true;
                    NebulaLog.Warn($"interest index holds {_index.Count} entities but this worker owns {_authoritative.Count}; rebuilding it");
                }
                _index.Clear();
                _wideMask.Clear();
                for (int i = 0; i < _authoritative.Count; i++) InterestAdd(_authoritative[i]);
                // Carrier links need every entity present, so they are re-applied after the whole set is back.
                for (int i = 0; i < _authoritative.Count; i++)
                {
                    var carried = _authoritative[i];
                    if (carried != null) _index.SetCarrier(carried.NetId, CarrierOf(carried));
                }
            }
            for (int i = 0; i < _authoritative.Count; i++)
            {
                var e = _authoritative[i];
                if (e == null) continue;
                if (!_index.TryGetPlacement(e.NetId, out var placement, out ulong from))
                {
                    InterestAdd(e);
                    continue;
                }
                // A carrier change is rare but real (a pawn boards a ship): re-link before deciding the key.
                ulong carrier = CarrierOf(e);
                if (_index.CarrierOf(e.NetId) != carrier) _index.SetCarrier(e.NetId, carrier);
                if (placement != InterestPlacement.Region || carrier != 0) continue;
                ulong to = RegionOf(e);
                if (to == from) continue;
                ulong sticky = StickyMask(e);
                RebucketBits(_publisher.MaskOf(from), _publisher.MaskOf(to), sticky, out ulong spawn, out ulong forget);
                _index.Move(e.NetId, to);
                if (spawn != 0) SendSpawnToMask(e, spawn);
                if (forget != 0) SendForgetToMask(e, forget);
            }
        }

        /// <summary>Gateways an entity reaches whatever region it is in: its owner's session gateway and any explicit subscriber.</summary>
        private ulong StickyMask(NetworkIdentity e)
        {
            ulong mask = OwnerBit(e);
            if (_explicitMask.Count > 0 && _explicitMask.TryGetValue(e.NetId, out ulong explicitBits)) mask |= explicitBits;
            return mask;
        }

        /// <summary>The bit of the gateway currently speaking for an entity's owner (0 for an unowned entity).</summary>
        private ulong OwnerBit(NetworkIdentity e)
        {
            if (e.OwnerClientId == 0 || !_sessions.TryGet(e.OwnerClientId, out var session)) return 0;
            var peer = GatewayByKey(session.Gateway);
            return peer != null ? RegionPublisher.Bit(peer.GatewayBit) : 0;
        }

        /// <summary>The gateway currently speaking for an entity's owner, or null (unowned, or its gateway is gone).</summary>
        private Peer SessionGatewayOf(NetworkIdentity e) =>
            e.OwnerClientId != 0 && _sessions.TryGet(e.OwnerClientId, out var session) ? GatewayByKey(session.Gateway) : null;

        /// <summary>The gateway speaking for a client, or null.</summary>
        private Peer SessionGatewayOf(ulong clientId) =>
            clientId != 0 && _sessions.TryGet(clientId, out var session) ? GatewayByKey(session.Gateway) : null;

        /// <summary>Count one state entry against every gateway in the mask, for the "entries sent vs total" stat of design §12.</summary>
        private void CountEntry(ulong mask)
        {
            InterestEntriesSent++;
            for (int bit = 0; bit < RegionPublisher.MaxGateways && mask != 0; bit++)
            {
                if ((mask & (1UL << bit)) == 0) continue;
                var peer = _gatewayBits[bit];
                if (peer != null) peer.EntriesSent++;
            }
            for (int i = 0; i < _unmaskedGateways.Count; i++) _unmaskedGateways[i].EntriesSent++;
        }

        private Peer GatewayByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < _gateways.Count; i++) if (_gateways[i].Key == key) return _gateways[i];
            return null;
        }

        // ------------------------------------------------------------------------------------------- gateway links

        /// <summary>
        /// A gateway link came up: give it a mask bit and a subscription set. Nothing is announced here beyond the
        /// entities that are relevant without a subscription — the always-relevant ones and the pawns of sessions it
        /// speaks for. The v16 behaviour of announcing every authoritative entity is exactly what interest
        /// management exists to remove.
        /// </summary>
        private void AddGatewayLink(Peer gateway)
        {
            gateway.GatewayBit = -1;
            for (int bit = 0; bit < RegionPublisher.MaxGateways; bit++)
            {
                if (_gatewayBits[bit] != null) continue;
                gateway.GatewayBit = bit;
                _gatewayBits[bit] = gateway;
                break;
            }
            if (gateway.GatewayBit < 0)
            {
                _unmaskedGateways.Add(gateway);
                NebulaLog.Error($"more than {RegionPublisher.MaxGateways} gateway links on worker {WorkerId}: gateway {gateway.Id} cannot be interest-filtered and will receive every authoritative entity. Split the mesh or raise RegionPublisher.MaxGateways.");
                return;
            }
            var receiver = new RegionSubscriptionReceiver { Grid = _interestGrid };
            receiver.Changed += OnSubscriptionChanged;
            gateway.Subscription = receiver;
            _publisher.AddGateway(gateway.GatewayBit, receiver);
            ulong bitMask = RegionPublisher.Bit(gateway.GatewayBit);
            foreach (var entry in _index.Global) SendSpawnToMask(entry.Value, bitMask);
            for (int i = 0; i < _authoritative.Count; i++)
            {
                var e = _authoritative[i];
                if (e != null && e.OwnerClientId != 0 && (OwnerBit(e) & bitMask) != 0) SendSpawnToMask(e, bitMask);
            }
            AnnounceAwaited(gateway);
        }

        /// <summary>A gateway link went away: its bit leaves every region and is free for the next link.</summary>
        private void RemoveGatewayLink(Peer gateway)
        {
            _unmaskedGateways.Remove(gateway);
            if (gateway.GatewayBit < 0) return;
            _publisher.RemoveGateway(gateway.GatewayBit);
            if (_gatewayBits[gateway.GatewayBit] == gateway) _gatewayBits[gateway.GatewayBit] = null;
            if (gateway.Subscription != null) gateway.Subscription.Changed -= OnSubscriptionChanged;
            gateway.Subscription = null;
            ulong bit = RegionPublisher.Bit(gateway.GatewayBit);
            gateway.GatewayBit = -1;
            RebuildExplicitMask(announce: false);
            _wideScratch.Clear();
            foreach (var kv in _wideMask) if ((kv.Value & bit) != 0) _wideScratch.Add(kv.Key);
            for (int i = 0; i < _wideScratch.Count; i++) _wideMask[_wideScratch[i]] &= ~bit;
            _wideScratch.Clear();
        }

        /// <summary>
        /// Apply a gateway's subscription update (design §5). A message built with a different grid, a delta against
        /// a sequence this worker does not hold, or a set that does not verify is refused loudly and answered with
        /// <see cref="InterestResyncMsg"/>: filtering with ids the gateway did not mean would show up as entities
        /// that never spawn rather than as an error.
        /// </summary>
        private void OnInterestSubscribe(Peer gateway, in InterestSubscribeMsg msg)
        {
            if (gateway.Role != PeerRole.Gateway) return;
            var receiver = gateway.Subscription;
            if (receiver == null)
            {
                NebulaLog.Warn($"interest subscription from gateway {gateway.Id} which has no link slot on {WorkerId}; ignored");
                return;
            }
            if (receiver.Apply(msg, out string rejection)) return;
            NebulaLog.Error($"gateway {gateway.Id}: {rejection}; asking for a full snapshot");
            _writer.Reset();
            new InterestResyncMsg { HaveSeq = receiver.Seq }.Write(_writer);
            Send(gateway, Delivery.ReliableOrdered);
        }

        /// <summary>
        /// A subscription was committed: fold the region change into the masks and send a spawn for everything
        /// already bucketed in the regions that were added (carriers before their contents, so an entity never
        /// arrives at a gateway before the container it rides in). Nothing is sent for removed regions: the gateway
        /// drops its own cache and despawns them from its clients (design §5).
        /// </summary>
        private void OnSubscriptionChanged(RegionSubscriptionReceiver receiver)
        {
            var gateway = GatewayOf(receiver);
            if (gateway == null || gateway.GatewayBit < 0) return;
            _publisher.ApplyChanges(gateway.GatewayBit, receiver);
            RebuildExplicitMask(announce: true);
            _nextWideEval = 0f; // new foci: re-match the wide list on the next publish rather than at the next interval
            var added = receiver.Added;
            if (added.Count == 0) return;
            ulong bit = RegionPublisher.Bit(gateway.GatewayBit);
            _spawnOrder.Clear();
            for (int i = 0; i < added.Count; i++)
            {
                var bucket = _index.Region(added[i]);
                foreach (var entry in bucket) if (entry.Value != null) _spawnOrder.Add(entry.Value);
            }
            SendSpawnsInCarrierOrder(bit);
        }

        private Peer GatewayOf(RegionSubscriptionReceiver receiver)
        {
            for (int i = 0; i < _gateways.Count; i++) if (_gateways[i].Subscription == receiver) return _gateways[i];
            return null;
        }

        /// <summary>
        /// Send <see cref="_spawnOrder"/> to one gateway shallowest container first. Nesting depth is the carrier
        /// order: a ship sits in a static container (depth 0) and its passengers in the ship's box (depth 1), so
        /// walking depths in order puts every carrier ahead of what it carries.
        /// </summary>
        private void SendSpawnsInCarrierOrder(ulong mask)
        {
            for (int depth = 0; depth <= MaxNestingDepth; depth++)
            {
                bool deeper = false;
                for (int i = 0; i < _spawnOrder.Count; i++)
                {
                    var e = _spawnOrder[i];
                    int d = e.Container != null ? e.Container.NestingDepth : 0;
                    if (d > depth) { deeper = true; continue; }
                    if (d < depth) continue;
                    SendSpawnToMask(e, mask);
                }
                if (!deeper) break;
            }
            _spawnOrder.Clear();
        }

        /// <summary>
        /// Recompute netId → explicit subscribers from every link's entity list. When <paramref name="announce"/>,
        /// a gateway that has just named an entity this worker owns is sent its spawn at once: an explicit
        /// subscription (a party member, a quest target) is not spatial and would otherwise wait for a rebucket.
        /// </summary>
        private void RebuildExplicitMask(bool announce)
        {
            _explicitPrevious.Clear();
            foreach (var kv in _explicitMask) _explicitPrevious[kv.Key] = kv.Value;
            _explicitMask.Clear();
            for (int i = 0; i < _gateways.Count; i++)
            {
                var g = _gateways[i];
                if (g.GatewayBit < 0 || g.Subscription == null) continue;
                ulong bit = RegionPublisher.Bit(g.GatewayBit);
                var ids = g.Subscription.Entities;
                for (int j = 0; j < ids.Count; j++)
                {
                    _explicitMask.TryGetValue(ids[j], out ulong mask);
                    _explicitMask[ids[j]] = mask | bit;
                }
            }
            if (!announce) return;
            foreach (var kv in _explicitMask)
            {
                _explicitPrevious.TryGetValue(kv.Key, out ulong before);
                ulong fresh = kv.Value & ~before;
                if (fresh == 0 || !_index.TryGetValue(kv.Key, out var e) || e == null) continue;
                SendSpawnToMask(e, fresh);
            }
        }

        // ------------------------------------------------------------------------------------------- wide entities

        /// <summary>
        /// Match the wide list against each gateway's foci at <see cref="InterestSettings.EvalHz"/> (design §6): a
        /// wide entity is too large for a region scan to find, so it is tested directly instead. The exit margin is
        /// the hysteresis — a gateway that already has it keeps it until the entity is a margin further away — so a
        /// dirigible hovering on the boundary is not spawned and forgotten four times a second.
        /// </summary>
        private void EvaluateWideEntities(float now)
        {
            if (now < _nextWideEval) return;
            _nextWideEval = now + _interest.EvalInterval;
            if (_index.WideCount == 0)
            {
                if (_wideMask.Count > 0) _wideMask.Clear();
                return;
            }
            foreach (var entry in _index.Wide)
            {
                var e = entry.Value;
                if (e == null) continue;
                PlacementOf(e.AlwaysRelevant, e.RelevanceRadius, _interest, out float radius);
                ToAbsolute(e.transform.position, out double x, out double y, out double z);
                _wideMask.TryGetValue(entry.Id, out ulong before);
                ulong enter = _publisher.WideMask(_interestGrid, x, y, z, radius);
                ulong stay = before == 0 ? 0 : _publisher.WideMask(_interestGrid, x, y, z, radius + _interest.ExitMargin);
                ulong after = enter | (before & stay);
                if (after == before) continue;
                _wideMask[entry.Id] = after;
                ulong sticky = StickyMask(e);
                RebucketBits(before, after, sticky, out ulong spawn, out ulong forget);
                if (spawn != 0) SendSpawnToMask(e, spawn);
                if (forget != 0) SendForgetToMask(e, forget);
            }
        }

        // ------------------------------------------------------------------------------------------- masks per tick

        /// <summary>
        /// Decide once per tick which gateways hear about each authoritative entity, and group the entities that go
        /// to exactly the same set so one serialization serves all of them (design §6). Everything downstream —
        /// world state, netvars, sync state, RPCs, despawns — reads <see cref="_entityMask"/> instead of iterating
        /// gateways, so the per-tick cost is O(entities + batches × subscribers) rather than O(entities × gateways).
        /// </summary>
        private void BuildPublishMasks()
        {
            _entityMask.Clear();
            ReleaseMaskLists();
            ulong linked = _publisher.LinkedMask;
            for (int i = 0; i < _authoritative.Count; i++)
            {
                var e = _authoritative[i];
                if (e == null) continue;
                if (!_index.TryGetPlacement(e.NetId, out var placement, out ulong region)) { placement = InterestPlacement.Region; region = 0; }
                ulong regionMask = placement == InterestPlacement.Region ? _publisher.MaskOf(region) : 0;
                _wideMask.TryGetValue(e.NetId, out ulong wide);
                ulong mask = EffectiveMask(placement, regionMask, wide, linked, StickyMask(e));
                _entityMask[e.NetId] = mask;
            }
        }

        /// <summary>The gateways that currently hear about an entity; 0 when nobody does.</summary>
        private ulong MaskOfEntity(ulong netId) => _entityMask.TryGetValue(netId, out ulong mask) ? mask : 0;

        /// <summary>
        /// Which gateways could know about an entity right now, computed rather than read from this tick's table:
        /// used outside the publish pass (a spawn, a despawn, a handover) where the table is stale or has no row
        /// for the entity yet.
        /// </summary>
        private ulong PublishMaskOf(NetworkIdentity e)
        {
            if (e == null) return 0;
            if (!_index.TryGetPlacement(e.NetId, out var placement, out ulong region)) { placement = InterestPlacement.Region; region = 0; }
            ulong regionMask = placement == InterestPlacement.Region ? _publisher.MaskOf(region) : 0;
            _wideMask.TryGetValue(e.NetId, out ulong wide);
            return EffectiveMask(placement, regionMask, wide, _publisher.LinkedMask, StickyMask(e));
        }

        /// <summary>Bucket this tick's dirty state entries by their mask; <paramref name="reliable"/> picks the channel.</summary>
        private void GroupDirtyByMask(bool reliable)
        {
            ReleaseMaskLists();
            for (int i = 0; i < _authoritative.Count; i++)
            {
                var e = _authoritative[i];
                if (e == null || !e.HasReplicationState || e.ReplicationState.Reliable != reliable) continue;
                ulong mask = MaskOfEntity(e.NetId);
                InterestEntriesTotal++;
                if (mask == 0 && _unmaskedGateways.Count == 0) continue;
                if (!_byMask.TryGetValue(mask, out var list))
                {
                    list = _maskListPool.Count > 0 ? _maskListPool.Pop() : new List<NetworkIdentity>(32);
                    _byMask[mask] = list;
                    _maskOrder.Add(mask);
                }
                list.Add(e);
            }
        }

        private void ReleaseMaskLists()
        {
            for (int i = 0; i < _maskOrder.Count; i++)
            {
                if (!_byMask.TryGetValue(_maskOrder[i], out var list)) continue;
                list.Clear();
                _maskListPool.Push(list);
            }
            _byMask.Clear();
            _maskOrder.Clear();
        }

        // ------------------------------------------------------------------------------------------- sending

        /// <summary>Send whatever is in <see cref="_writer"/> to every gateway in <paramref name="mask"/>, counting what it cost each of them.</summary>
        private void SendToMask(ulong mask, Delivery delivery)
        {
            int bytes = _writer.Length;
            // What the same message would have cost before v17: one copy to every gateway link.
            InterestBytesUnfiltered += (long)bytes * _gateways.Count;
            for (int bit = 0; bit < RegionPublisher.MaxGateways && mask != 0; bit++)
            {
                if ((mask & (1UL << bit)) == 0) continue;
                var peer = _gatewayBits[bit];
                if (peer == null) continue;
                peer.BytesSent += bytes;
                InterestBytesSent += bytes;
                Send(peer, delivery);
            }
            for (int i = 0; i < _unmaskedGateways.Count; i++)
            {
                _unmaskedGateways[i].BytesSent += bytes;
                InterestBytesSent += bytes;
                Send(_unmaskedGateways[i], delivery);
            }
        }

        private void SendSpawnToMask(NetworkIdentity e, ulong mask)
        {
            if (mask == 0 && _unmaskedGateways.Count == 0) return;
            _writer.Reset();
            EntitySpawnMsg.From(e, _scratch).Write(_writer, MsgId.EntitySpawn);
            SendToMask(mask, Delivery.ReliableOrdered);
        }

        /// <summary>
        /// Tell gateways to forget an entity that left every region they subscribe here (design §5). This is not a
        /// despawn: the entity is alive, it is simply no longer in anything this gateway asked for, and the gateway
        /// decides what its clients are told.
        /// </summary>
        private void SendForgetToMask(NetworkIdentity e, ulong mask)
        {
            if (mask == 0) return;
            _writer.Reset();
            new EntityForgetMsg { NetId = e.NetId, Epoch = e.Epoch }.Write(_writer);
            SendToMask(mask, Delivery.ReliableOrdered);
        }

        /// <summary>
        /// An entity this worker owns moved to another worker: the gateways that were following it by name (an
        /// explicit subscription) or because they speak for its owner are told where it went, so they can link the
        /// new owner instead of losing it (design §5).
        /// </summary>
        private void SendRedirect(NetworkIdentity e, ushort newWorkerIndex, ulong mask)
        {
            if (mask == 0) return;
            _writer.Reset();
            new EntityRedirectMsg { NetId = e.NetId, NewWorkerIndex = newWorkerIndex }.Write(_writer);
            SendToMask(mask, Delivery.ReliableOrdered);
        }

        /// <summary>The gateway keys that follow an entity by name or by session, for <see cref="AuthorityTransferMsg.InterestGateways"/>.</summary>
        private string[] InterestGatewayKeys(ulong mask)
        {
            if (mask == 0) return Array.Empty<string>();
            _scratchStrings.Clear();
            for (int bit = 0; bit < RegionPublisher.MaxGateways; bit++)
            {
                if ((mask & (1UL << bit)) == 0) continue;
                var peer = _gatewayBits[bit];
                if (peer != null && peer.Key.Length > 0) _scratchStrings.Add(peer.Key);
            }
            var keys = _scratchStrings.Count == 0 ? Array.Empty<string>() : _scratchStrings.ToArray();
            _scratchStrings.Clear();
            return keys;
        }

        /// <summary>
        /// This worker just took authority over an entity: announce it to the gateways that are linked here and
        /// actually want it (its region, an explicit subscription, its owner's session, or a global entity), plus
        /// the ones the previous owner said were following it. Announcing to every gateway is what v16 did.
        /// </summary>
        private void AnnounceToRelevantGateways(NetworkIdentity e, string[] followers)
        {
            ulong mask = PublishMaskOf(e);
            if (followers != null)
            {
                for (int i = 0; i < followers.Length; i++)
                {
                    var peer = GatewayByKey(followers[i]);
                    // A follower we have no link to cannot be told anything: gateways dial workers, never the
                    // other way round. Remember it instead and announce the moment that link arrives, which is
                    // what keeps a pawn alive across a handover onto a worker its own gateway has never used.
                    if (peer == null) Remember(followers[i], e.NetId);
                    else mask |= RegionPublisher.Bit(peer.GatewayBit);
                }
            }
            // The gateway speaking for the owner is the one that must never miss this, and it may be a gateway
            // we are not linked to either (the session travelled with the pawn, the link did not).
            if (e.OwnerClientId != 0 && _sessions.TryGet(e.OwnerClientId, out var session) &&
                !string.IsNullOrEmpty(session.Gateway) && GatewayByKey(session.Gateway) == null)
                Remember(session.Gateway, e.NetId);
            SendSpawnToMask(e, mask);

            void Remember(string key, ulong netId)
            {
                if (string.IsNullOrEmpty(key)) return;
                if (!_awaitedByGateway.TryGetValue(key, out var ids)) _awaitedByGateway[key] = ids = new List<ulong>(2);
                if (!ids.Contains(netId)) ids.Add(netId);
            }
        }

        /// <summary>
        /// Entities a gateway must be told about as soon as it links here, because it was following them when
        /// they arrived and we had nowhere to send the announcement. Keyed by gateway key, so a link that never
        /// comes costs one short list and nothing else.
        /// </summary>
        private readonly Dictionary<string, List<ulong>> _awaitedByGateway = new Dictionary<string, List<ulong>>();

        /// <summary>Flush what <see cref="AnnounceToRelevantGateways"/> could not send this gateway when it took authority.</summary>
        private void AnnounceAwaited(Peer gateway)
        {
            if (_awaitedByGateway.Count == 0 || gateway.Key.Length == 0) return;
            if (!_awaitedByGateway.Remove(gateway.Key, out var ids)) return;
            ulong bit = gateway.GatewayBit >= 0 ? RegionPublisher.Bit(gateway.GatewayBit) : 0;
            _spawnOrder.Clear();
            for (int i = 0; i < ids.Count; i++)
            {
                // Still ours, and not already sent by the owned-entity pass just above.
                if (!_index.TryGetValue(ids[i], out var e) || e == null || (OwnerBit(e) & bit) != 0) continue;
                _spawnOrder.Add(e);
            }
            if (_spawnOrder.Count == 0) return;
            SendSpawnsInCarrierOrder(bit);
        }

        // ------------------------------------------------------------------------------------------- warnings

        /// <summary>
        /// Design §11: interest management bounds who <b>hears</b> about an entity, not who simulates it. One
        /// container is still one worker's budget, so a container that outgrows a worker, or filtering that costs
        /// more than the game can afford per tick, is a call to partition the world — not something a bigger radius
        /// or a smaller cell will fix. Logged at most once a minute and raised as a telemetry flag.
        /// </summary>
        private void CheckPartitionWarning(float now)
        {
            if (now < _nextPartitionCheck) return;
            _nextPartitionCheck = now + _interest.EvalInterval;
            string warning = "";
            if (_interest.PartitionWarnEntities > 0)
            {
                _containerCounts.Clear();
                for (int i = 0; i < _authoritative.Count; i++)
                {
                    var c = _authoritative[i] != null ? _authoritative[i].Container : null;
                    if (c == null) continue;
                    _containerCounts.TryGetValue(c, out int count);
                    _containerCounts[c] = count + 1;
                }
                foreach (var kv in _containerCounts)
                {
                    if (kv.Value <= _interest.PartitionWarnEntities) continue;
                    warning = $"container '{kv.Key.ContainerId}' holds {kv.Value} authoritative entities (over PartitionWarnEntities={_interest.PartitionWarnEntities}). Interest management bounds who hears about entities, not who simulates them: partition this container.";
                    break;
                }
            }
            if (warning.Length == 0 && _interest.PartitionWarnFilterMs > 0 && InterestFilterMs > _interest.PartitionWarnFilterMs)
            {
                warning = $"per-gateway filtering costs {InterestFilterMs:0.00} ms per tick (over PartitionWarnFilterMs={_interest.PartitionWarnFilterMs}). Interest management bounds who hears about entities, not who simulates them: partition this container.";
            }
            PartitionWarning = warning;
            if (warning.Length == 0 || now < _nextPartitionLog) return;
            _nextPartitionLog = now + PartitionWarnLogSeconds;
            NebulaLog.Warn($"{WorkerId}: {warning}");
        }
    }
}
