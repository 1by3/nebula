using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nebula;
using Nebula.ServicePrimitives;

namespace Nebula.ServiceTests;

/// <summary>
/// A worker as a gateway sees it, built out of the <b>real</b> interest code: a
/// <see cref="RegionSubscriptionReceiver"/> per gateway link, one <see cref="RegionPublisher"/> holding the
/// subscriber masks and one <see cref="InterestIndex{T}"/> of its authoritative entities. That matters: a fake
/// that answered every subscription with everything would let a gateway bug through, and a fake with its own
/// idea of the protocol would test the fake. It follows design §5/§6 exactly — it announces nothing on a
/// gateway's Hello, publishes only subscribed regions, spawns on subscribe, forgets on rebucket out, and always
/// publishes an owned entity to its owner's gateway.
/// </summary>
public sealed class FakeWorker : IDisposable
{
    /// <summary>One entity this worker is authoritative for.</summary>
    public sealed class Entity
    {
        public ulong NetId;
        public ulong OwnerClientId;
        public ContainerRef Container;
        public Vector3 Local;
        public uint Epoch = 1;
        public float RelevanceRadius;
        public bool AlwaysRelevant;
        public byte InterestGroup;
        public ulong Region;
        public InterestPlacement Placement;
    }

    private sealed class GatewayLink
    {
        public int PeerId;
        public string GatewayId = "";
        public int Bit;
        public RegionSubscriptionReceiver Receiver = new();
        /// <summary>Entity ids this link has already been sent because it named them; an id is announced once.</summary>
        public readonly HashSet<ulong> AnnouncedByName = new();
    }

    public readonly LiteNetTransport Transport;
    public readonly int Port;
    public readonly string WorkerId;
    public readonly ushort Index;

    public readonly List<SpawnPlayerMsg> Claims = new();
    public readonly List<DespawnPlayerMsg> Despawns = new();
    public readonly Dictionary<int, string> Gateways = new();
    public readonly List<string> Refused = new();
    public readonly List<ClientInputMsg> Inputs = new();
    /// <summary>Spawns, forgets and resyncs this worker has sent, by gateway id: what the subscription tests assert on.</summary>
    public readonly Dictionary<string, int> SpawnsSent = new(), ForgetsSent = new(), ResyncsSent = new();
    /// <summary>
    /// Every spawn and forget as (gateway id, net id), in order. The counters above say how much was published;
    /// these say to <b>whom</b>, which is the only way to test that a gateway on the other side of the world is
    /// not sent a crate at all — a client-side assertion cannot tell "never sent" from "sent and filtered".
    /// </summary>
    public readonly List<(string Gateway, ulong NetId)> SpawnLog = new(), ForgetLog = new();
    public bool DeferSpawns;
    /// <summary>Where a pawn is put, relative to its container. A test that wants players apart overrides it.</summary>
    public Func<ulong, Vector3> PawnPlacement = _ => Vector3.zero;
    /// <summary>The container pawns are spawned into.</summary>
    public ContainerRef PawnContainer = new(0);
    /// <summary>
    /// Put a pawn in the container the gateway's <see cref="SpawnPlayerMsg"/> named, rather than in
    /// <see cref="PawnContainer"/>. A real worker always does this; the fixture does not by default because the
    /// interest tests place pawns themselves. The scale suite needs it: a client that joined a scope must have its
    /// pawn inside that scope, or per-scope isolation cannot be measured at all (docs/scale-suite.md, D3).
    /// </summary>
    public bool SpawnIntoRequestedContainer;

    private readonly Dictionary<ulong, Entity> _entities = new();
    private readonly Dictionary<int, GatewayLink> _links = new();
    private readonly Dictionary<ulong, ulong> _pawns = new();
    private readonly Dictionary<ulong, string> _identities = new();
    /// <summary>
    /// Session id → the gateway key that speaks for it, the way a real worker's <c>PlayerSessions</c> holds it.
    /// It is carried across an authority transfer (<c>AuthorityTransferMsg.SessionGateway</c>), which is what
    /// lets the new owner publish a pawn to its client's gateway — <b>if</b> it has a link to that gateway.
    /// </summary>
    private readonly Dictionary<ulong, string> _sessionGateway = new();
    private readonly RegionPublisher _publisher = new();
    private readonly InterestIndex<Entity> _index = new();
    /// <summary>The real carrier-subtree bookkeeping (design D3), so this fake publishes a ship the way a worker does.</summary>
    private readonly CarriedTransition _carried = new();
    /// <summary>The real handoff bookkeeping (design D85/D87), so a handover here suppresses followers the way a worker's does.</summary>
    private readonly HandoverScope _handover = new();
    /// <summary>A newly subscribed region's snapshot and each entry's carrier depth, for the carrier-first ordering.</summary>
    private readonly List<Entity> _spawnOrder = new();
    private readonly List<int> _spawnDepth = new();
    private readonly string _meshToken;
    private readonly NetworkWriter _w = new();
    /// <summary>
    /// Net ids are minted per worker and must not collide between them: two workers handing out the same id
    /// would look to a gateway like one entity changing authority, which is a plausible bug to write and an
    /// impossible one to read back out of a soak result.
    /// </summary>
    private ulong _nextNetId;
    private uint _tick;

    public InterestGrid Grid = InterestGrid.Resolve(InterestSettings.Default);
    /// <summary>The interest settings this worker publishes with; must match the gateway's.</summary>
    public InterestSettings Settings = InterestSettings.Default;

    public FakeWorker(string meshToken, string workerId = "w1", ushort index = 1)
    {
        _meshToken = meshToken;
        WorkerId = workerId;
        Index = index;
        _nextNetId = 1000 + (ulong)Math.Max(0, index - 1) * 1000;
        Transport = new LiteNetTransport("fake-" + workerId);
        Transport.Listen(0);
        Port = Transport.LocalPort;
    }

    /// <summary>The distinct pawns handed out, by session. A duplicate player shows up here as two entries.</summary>
    public IReadOnlyDictionary<ulong, ulong> Pawns => _pawns;
    public IReadOnlyDictionary<ulong, Entity> Entities => _entities;
    /// <summary>Regions at least one gateway subscribes here: the worker's view of how scoped its gateways are.</summary>
    public int SubscribedRegions => _publisher.SubscribedRegions;
    public int LinkCount => _links.Count;
    /// <summary>The set one gateway holds, for the resync and delta tests.</summary>
    public RegionSubscriptionReceiver? ReceiverOf(string gatewayId)
    {
        foreach (var link in _links.Values) if (link.GatewayId == gatewayId) return link.Receiver;
        return null;
    }

    // ------------------------------------------------------------------------------------------- content

    /// <summary>Add an authoritative entity and publish it to whoever already subscribes where it sits.</summary>
    public Entity Spawn(ulong netId, Vector3 local, ContainerRef? container = null, ulong owner = 0, float relevanceRadius = 0, bool alwaysRelevant = false, byte group = 0)
    {
        var e = new Entity
        {
            NetId = netId, Local = local, Container = container ?? PawnContainer, OwnerClientId = owner,
            RelevanceRadius = relevanceRadius, AlwaysRelevant = alwaysRelevant, InterestGroup = group,
        };
        _entities[netId] = e;
        // Passengers may already name this id as their carrier: until it existed they sat on their own
        // placement, and indexing it re-seats the whole pending subtree into this entity's (design D70). That
        // is a publication, so it is captured before the placement and sent after this entity's own spawn -
        // carrier before its contents, exactly as a real worker's InterestAddAndAnnounce does it.
        bool pending = _index.HasCarried(netId);
        if (pending) _carried.Capture(_index, netId, _publisher, WideMaskOf);
        Place(e);
        ulong mask = MaskFor(e);
        for (int bit = 0; bit < RegionPublisher.MaxGateways; bit++) if ((mask & (1UL << bit)) != 0) SendSpawn(PeerOfBit(bit), e);
        if (pending) PublishCarried();
        return e;
    }

    /// <summary>
    /// Move an entity. Crossing a region edge is what makes a gateway hear of it, or forget it (design §6) — and
    /// everything riding in it moves and is published with it (design D3). A passenger is bucketed with its
    /// carrier, so moving one by itself changes nothing but its local position.
    /// </summary>
    public void Move(ulong netId, Vector3 local)
    {
        if (!_entities.TryGetValue(netId, out var e)) return;
        e.Local = local;
        if (e.Placement != InterestPlacement.Region) { _index.SetValue(netId, e); PublishOwned(e); return; }
        if (_index.CarrierOf(netId) != 0) { _index.SetValue(netId, e); return; }
        ulong to = RegionOf(e);
        if (to == e.Region) { _index.SetValue(netId, e); return; }
        _carried.Capture(_index, netId, _publisher, WideMaskOf);
        _index.Move(netId, to);
        PublishCarried();
    }

    /// <summary>
    /// Put an entity inside the dynamic container another entity carries: from now on it is bucketed with that
    /// ship and enters and leaves a gateway's set with it.
    /// </summary>
    public void Board(ulong netId, ulong carrierNetId, Vector3 local = default) =>
        Recarry(netId, ContainerRef.Dynamic(carrierNetId), carrierNetId, local);

    /// <summary>Take an entity back out of the ship it was riding in: it buckets by its own position again.</summary>
    public void Disembark(ulong netId, ContainerRef container, Vector3 local) => Recarry(netId, container, 0, local);

    private void Recarry(ulong netId, ContainerRef container, ulong carrierNetId, Vector3 local)
    {
        if (!_entities.TryGetValue(netId, out var e)) return;
        _carried.Capture(_index, netId, _publisher, WideMaskOf);
        e.Container = container;
        e.Local = local;
        _index.SetValue(netId, e);
        _index.SetCarrier(netId, carrierNetId);
        // Free again: its own position decides where it sits, and its own subtree comes along.
        if (carrierNetId == 0 && e.Placement == InterestPlacement.Region)
            _index.Move(netId, RegionOf(e));
        PublishCarried();
    }

    /// <summary>
    /// Publish what the last captured move did to the carrier and everything riding in it: spawns carrier first,
    /// forgets contents first, so a gateway never holds an entity whose container it has not been given.
    /// </summary>
    private void PublishCarried()
    {
        _carried.Resolve(_index, _publisher, WideMaskOf, _handover);
        // The region each of them now sits in is what Reaches() reads, so refresh it before anything is sent.
        for (int i = 0; i < _carried.Count; i++)
            if (_entities.TryGetValue(_carried[i].Id, out var moved)) SyncPlacement(moved);
        for (int i = 0; i < _carried.Count; i++)
        {
            var slot = _carried[i];
            if (slot.Before == slot.After || !_entities.TryGetValue(slot.Id, out var moved)) continue;
            ulong spawn = slot.After & ~slot.Before & ~StickyOf(moved);
            for (int bit = 0; bit < RegionPublisher.MaxGateways; bit++)
                if ((spawn & (1UL << bit)) != 0) SendSpawn(PeerOfBit(bit), moved);
        }
        for (int i = _carried.Count - 1; i >= 0; i--)
        {
            var slot = _carried[i];
            if (slot.Before == slot.After || !_entities.TryGetValue(slot.Id, out var moved)) continue;
            ulong forget = slot.Before & ~slot.After & ~StickyOf(moved);
            for (int bit = 0; bit < RegionPublisher.MaxGateways; bit++)
                if ((forget & (1UL << bit)) != 0) SendForget(PeerOfBit(bit), moved);
        }
        Transport.Flush();
    }

    /// <summary>Gateways that keep an entity wherever it sits: the one speaking for its owner, and any that named it.</summary>
    private ulong StickyOf(Entity e)
    {
        ulong mask = 0;
        foreach (var link in _links.Values)
            if (IsOwnersGateway(e, link.PeerId) || link.Receiver.Entities.Contains(e.NetId)) mask |= 1UL << link.Bit;
        return mask;
    }

    /// <summary>Read an entity's bucket back out of the index, which is the one that decides where it really is.</summary>
    private void SyncPlacement(Entity e)
    {
        if (!_index.TryGetPlacement(e.NetId, out var placement, out ulong region)) return;
        e.Placement = placement;
        e.Region = region;
    }

    /// <summary>
    /// Hand authority over an entity to another worker, the way <c>NebulaWorker.TransferAuthority</c> does: the
    /// gateways that follow it (its owner's session gateway, anyone who named it) are sent an
    /// <see cref="EntityRedirectMsg"/>, the entity leaves this worker's index without any despawn, and the new
    /// owner announces it — but only to the gateways it actually has a link to, because a worker cannot dial a
    /// gateway. Nothing at all reaches a gateway that is linked to neither worker.
    /// <para>
    /// A handoff is not a destruction (design D85). The real worker takes a carrier's contents with it, so
    /// leaving the index here says nothing about the passengers: they are still aboard and are handed over
    /// immediately afterwards, which is how a test drives the two halves. Publishing their own placement in
    /// between would expose an orphan that never existed. This fixture has no pinned interiors, so every
    /// passenger follows; the real worker's <c>InterestRemove</c> still orphans the ones that do not.
    /// </para>
    /// <para>
    /// The suppression itself is <b>not</b> reimplemented here: the shared <see cref="HandoverScope"/> the real
    /// worker uses records the followers and <see cref="CarriedTransition.Resolve{T}"/> collapses their slots,
    /// so a service test that handed a ship over would fail if that production code were removed (design D87).
    /// </para>
    /// </summary>
    public void HandOver(ulong netId, FakeWorker target)
    {
        using var frame = _handover.Begin();
        if (frame.IsOutermost) _handover.Collect(_index, netId);
        if (!_entities.Remove(netId, out var e)) throw new InvalidOperationException($"{WorkerId} does not own #{netId}");
        foreach (var link in _links.Values)
        {
            if (!IsOwnersGateway(e, link.PeerId) && !link.Receiver.Entities.Contains(netId)) continue;
            _w.Reset();
            new EntityRedirectMsg { NetId = netId, NewWorkerIndex = target.Index }.Write(_w);
            Transport.Send(link.PeerId, Delivery.ReliableOrdered, _w.ToSegment());
        }
        Transport.Flush();
        // Exactly the real worker's InterestRemove: capture the subtree, take the carrier out, publish what
        // that did to anything riding in it — which is nothing at all for the passengers that follow it.
        bool carried = _index.HasCarried(netId);
        if (carried) _carried.Capture(_index, netId, _publisher, WideMaskOf);
        _index.Remove(netId);
        if (carried) PublishCarried();
        if (_pawns.TryGetValue(e.OwnerClientId, out ulong pawn) && pawn == netId) _pawns.Remove(e.OwnerClientId);
        target.Receive(e, this);
    }

    /// <summary>The receiving half of <see cref="HandOver"/>: the session travels with the pawn, the epoch rises.</summary>
    private void Receive(Entity e, FakeWorker from)
    {
        e.Epoch++;
        _entities[e.NetId] = e;
        Place(e);
        if (e.OwnerClientId != 0)
        {
            _pawns[e.OwnerClientId] = e.NetId;
            if (from._sessionGateway.TryGetValue(e.OwnerClientId, out string? gateway)) _sessionGateway[e.OwnerClientId] = gateway;
            if (from._identities.TryGetValue(e.OwnerClientId, out string? identity)) _identities[e.OwnerClientId] = identity;
        }
        ulong mask = MaskFor(e);
        for (int bit = 0; bit < RegionPublisher.MaxGateways; bit++) if ((mask & (1UL << bit)) != 0) SendSpawn(PeerOfBit(bit), e);
        Transport.Flush();
    }

    /// <summary>
    /// Take an entity out of the world. Its own despawn is sent to everyone who could know it; what is
    /// published on top of that is what its leaving does to anything riding in it, because removing a carrier
    /// gives every surviving passenger its own placement back (design D70) and no other path would say so.
    /// </summary>
    public void Despawn(ulong netId)
    {
        if (!_entities.Remove(netId, out var e)) return;
        bool carried = _index.HasCarried(netId);
        if (carried) _carried.Capture(_index, netId, _publisher, WideMaskOf);
        _index.Remove(netId);
        ulong mask = MaskFor(e);
        for (int bit = 0; bit < RegionPublisher.MaxGateways; bit++)
        {
            if ((mask & (1UL << bit)) == 0) continue;
            _w.Reset();
            new EntityDespawnMsg { NetId = netId, Epoch = e.Epoch }.Write(_w, MsgId.EntityDespawn);
            Transport.Send(PeerOfBit(bit), Delivery.ReliableOrdered, _w.ToSegment());
        }
        // Whatever survives it has to be put down somewhere that still exists before its restored placement is
        // published, or the spawn names a carrier that is gone and every gateway fails ScopeContainer closed
        // (design D86). This is NebulaWorker.RemoveLocal's evacuation in the fixture's flat frame model.
        if (carried) Evacuate(e);
        // The removed carrier's own slot resolves to "gone" and publishes nothing, so this is only the
        // passengers it orphaned.
        if (carried) PublishCarried();
    }

    /// <summary>
    /// Put every direct passenger of a departing carrier down in the container the carrier itself sat in,
    /// keeping its absolute pose. Anything deeper keeps naming a carrier that is still there, so a surviving
    /// subtree still resolves top to bottom.
    /// <para>
    /// This one <b>is</b> the fixture's own, because the fixture's frames are flat and carry no rotation while
    /// <c>NebulaWorker.EvacuateCarried</c> reparents real transforms. It exists so the wire behaviour a gateway
    /// sees is the worker's; the production evacuation itself is regression-tested directly by
    /// <c>Nebula.Tests.WorkerHandoverTests</c> in the package's EditMode suite.
    /// </para>
    /// </summary>
    private void Evacuate(Entity carrier)
    {
        foreach (var inner in _entities.Values)
        {
            if (!inner.Container.IsDynamic || inner.Container.NetId != carrier.NetId) continue;
            // Frames here carry no rotation, so the carrier's own offset is the whole of the transform.
            inner.Local = carrier.Local + inner.Local;
            inner.Container = carrier.Container;
            _index.SetValue(inner.NetId, inner);
        }
    }

    /// <summary>One tick of world state per gateway, holding only the entities that gateway subscribes.</summary>
    public void PublishStates()
    {
        _tick++;
        foreach (var link in _links.Values)
        {
            _w.Reset();
            int slot = WorldStateMsg.Begin(_w, MsgId.WorldState, _tick, Index);
            ushort n = 0;
            foreach (var e in _entities.Values)
            {
                if (!Reaches(e, link)) continue;
                new EntityStateEntry
                {
                    NetId = e.NetId, Epoch = e.Epoch, Container = e.Container, Fields = TransformFields.Position | TransformFields.Location,
                    LocalPosition = e.Local, LocalRotation = Quaternion.identity, LocalScale = Vector3.one,
                }.Write(_w);
                n++;
            }
            WorldStateMsg.End(_w, slot, n);
            if (n > 0) Transport.Send(link.PeerId, Delivery.Sequenced, _w.ToSegment());
        }
        Transport.Flush();
    }

    /// <summary>A netvar update, filtered exactly as world state is: only gateways that subscribe where it sits.</summary>
    public void SendVars(ulong netId, byte[] vars)
    {
        Each(netId, (peer, e) =>
        {
            _w.Reset();
            new EntityVarsMsg { NetId = e.NetId, Epoch = e.Epoch, Vars = vars }.Write(_w, MsgId.EntityVars);
            Transport.Send(peer, Delivery.ReliableOrdered, _w.ToSegment());
        });
    }

    public void SendSyncState(ulong netId, byte behaviour, byte[] chunk)
    {
        var buffer = new NetworkWriter();
        int at = SyncStateCodec.BeginEnvelope(buffer);
        SyncStateCodec.WriteRawChunk(buffer, behaviour, SyncStateCodec.ChunkFlags.Full, new ArraySegment<byte>(chunk));
        SyncStateCodec.EndEnvelope(buffer, at, 1);
        byte[] chunks = buffer.ToArray();
        Each(netId, (peer, e) =>
        {
            _w.Reset();
            new EntitySyncMsg { NetId = e.NetId, Epoch = e.Epoch, Chunks = chunks, Reliable = true }.Write(_w, MsgId.EntityState);
            Transport.Send(peer, Delivery.ReliableOrdered, _w.ToSegment());
        });
    }

    public void SendRpc(ulong netId, float radius = 0f)
    {
        Each(netId, (peer, e) =>
        {
            _w.Reset();
            new EntityRpcMsg { NetId = e.NetId, Epoch = e.Epoch, BehaviourIndex = 0, MethodHash = 7, Radius = radius, Args = Array.Empty<byte>() }.Write(_w, MsgId.EntityRpc);
            Transport.Send(peer, Delivery.ReliableOrdered, _w.ToSegment());
        });
    }

    /// <summary>
    /// Put this gateway's set out of step behind its back: a valid Full snapshot from nobody, at a sequence the
    /// gateway has never sent. The gateway's next delta is then based on a sequence the worker no longer holds,
    /// which is exactly the condition <see cref="InterestResyncMsg"/> exists for.
    /// </summary>
    public void CorruptSubscription(string gatewayId)
    {
        foreach (var link in _links.Values)
        {
            if (link.GatewayId != gatewayId) continue;
            bool applied = link.Receiver.Apply(new InterestSubscribeMsg
            {
                Seq = link.Receiver.Seq + 500, BaseSeq = 0, Grid = Grid,
                Flags = InterestSubscribeFlags.Full | InterestSubscribeFlags.Commit,
                Add = new List<ulong> { 1 }, Remove = new List<ulong>(),
                FociRegions = new List<ulong>(), Entities = new List<ulong>(),
                SetCount = 1, SetHash = RegionSubscription.Mix(1),
            }, out _);
            if (!applied) throw new InvalidOperationException("could not desync the worker's set");
            _publisher.RemoveGateway(link.Bit);
            _publisher.AddGateway(link.Bit, link.Receiver);
        }
    }

    /// <summary>What a real worker sends a gateway whose session has moved to another gateway.</summary>
    public void EndSession(string gatewayId, ulong clientId, ulong generation, string reason)
    {
        foreach (var kv in Gateways)
        {
            if (kv.Value != gatewayId) continue;
            _w.Reset();
            new EndSessionMsg { ClientId = clientId, Generation = generation, Reason = reason }.Write(_w);
            Transport.Send(kv.Key, Delivery.ReliableOrdered, _w.ToSegment());
            Transport.Flush();
            return;
        }
        throw new InvalidOperationException("no link to gateway " + gatewayId);
    }

    // ------------------------------------------------------------------------------------------- transport

    public void Poll()
    {
        Transport.Poll(e =>
        {
            if (e.Type == TransportEvent.Kind.Disconnected) { DropLink(e.PeerId); return; }
            if (e.Type != TransportEvent.Kind.Data) return;
            var r = new NetworkReader(e.Data);
            Dispatch(e.PeerId, r);
        });
        Transport.Flush();
    }

    private void Dispatch(int peerId, NetworkReader r)
    {
        var id = (MsgId)r.ReadByte();
        switch (id)
        {
            case MsgId.Hello:
            {
                var hello = HelloMsg.Read(r);
                if (!MeshPeerAuth.Verify(_meshToken, hello.Role, hello.Id, hello.Incarnation, hello.Token, out string error))
                {
                    Refused.Add(hello.Id + ": " + error);
                    Transport.Disconnect(peerId);
                    return;
                }
                Gateways[peerId] = hello.Id;
                var link = new GatewayLink { PeerId = peerId, GatewayId = hello.Id, Bit = FreeBit() };
                link.Receiver.Grid = Grid;
                _links[peerId] = link;
                _publisher.AddGateway(link.Bit, link.Receiver);
                _w.Reset();
                new HelloMsg { Role = PeerRole.Worker, Id = WorkerId, Index = Index, Incarnation = 1, Token = MeshPeerAuth.Issue(_meshToken, PeerRole.Worker, WorkerId, 1) }.Write(_w);
                Transport.Send(peerId, Delivery.ReliableOrdered, _w.ToSegment());
                // Nothing spatial is announced here: a worker publishes only what a gateway subscribes (design
                // §5). The one exception a real worker makes in AddGatewayLink is an entity owned by a session
                // this gateway speaks for - it is always published to its own client's gateway, and this is the
                // first moment it can be, because workers cannot dial gateways.
                foreach (var owned in _entities.Values)
                    if (owned.OwnerClientId != 0 && IsOwnersGateway(owned, peerId)) SendSpawn(peerId, owned);
                break;
            }
            case MsgId.InterestSubscribe:
            {
                var msg = InterestSubscribeMsg.Read(r);
                if (!_links.TryGetValue(peerId, out var link)) return;
                if (!link.Receiver.Apply(msg, out string rejection))
                {
                    Bump(ResyncsSent, link.GatewayId);
                    _w.Reset();
                    new InterestResyncMsg { HaveSeq = link.Receiver.Seq }.Write(_w);
                    Transport.Send(peerId, Delivery.ReliableOrdered, _w.ToSegment());
                    return;
                }
                if (!msg.IsCommit) return;
                _publisher.ApplyChanges(link.Bit, link.Receiver);
                // A newly subscribed region: send a spawn for everything bucketed in it. Nothing is sent for a
                // region that was removed; the gateway drops its own cache there.
                var added = link.Receiver.Added;
                _spawnOrder.Clear();
                for (int i = 0; i < added.Count; i++)
                    foreach (var entry in _index.Region(added[i])) _spawnOrder.Add(entry.Value);
                SendSpawnsInCarrierOrder(peerId);
                foreach (var entry in _index.Global) SendSpawn(peerId, entry.Value);
                foreach (var entry in _index.Wide) if (Reaches(entry.Value, link)) SendSpawn(peerId, entry.Value);
                foreach (var e in _entities.Values) if (e.OwnerClientId != 0 && IsOwnersGateway(e, peerId)) SendSpawn(peerId, e);
                // An entity named by id: not spatial, so nothing else would ever announce it. This is what makes
                // an explicit subscription a recovery mechanism and not just a filter (design D5).
                var named = link.Receiver.Entities;
                for (int i = 0; i < named.Count; i++)
                    if (link.AnnouncedByName.Add(named[i]) && _entities.TryGetValue(named[i], out var byName) &&
                        !IsOwnersGateway(byName, peerId)) SendSpawn(peerId, byName);
                link.AnnouncedByName.RemoveWhere(id => !named.Contains(id));
                Transport.Flush();
                break;
            }
            case MsgId.SpawnPlayer:
            {
                var msg = SpawnPlayerMsg.Read(r);
                Claims.Add(msg);
                if (Gateways.TryGetValue(peerId, out string? claimant)) _sessionGateway[msg.ClientId] = claimant;
                if (DeferSpawns) break;
                _identities[msg.ClientId] = msg.Identity ?? "";
                if (!_pawns.TryGetValue(msg.ClientId, out ulong netId))
                {
                    netId = _nextNetId++;
                    _pawns[msg.ClientId] = netId;
                    var into = SpawnIntoRequestedContainer && !msg.Container.IsNone ? msg.Container : PawnContainer;
                    Spawn(netId, PawnPlacement(msg.ClientId), into, msg.ClientId);
                }
                else SendSpawn(peerId, _entities[netId]); // a reclaim: re-announce the pawn to its new gateway
                break;
            }
            case MsgId.DespawnPlayer: Despawns.Add(DespawnPlayerMsg.Read(r)); break;
            case MsgId.ClientInput: Inputs.Add(ClientInputMsg.Read(r)); break;
        }
    }

    private void DropLink(int peerId)
    {
        if (!_links.Remove(peerId, out var link)) return;
        _publisher.RemoveGateway(link.Bit);
        Gateways.Remove(peerId);
    }

    // ------------------------------------------------------------------------------------------- internals

    private int FreeBit()
    {
        for (int bit = 0; bit < RegionPublisher.MaxGateways; bit++)
        {
            bool used = false;
            foreach (var link in _links.Values) if (link.Bit == bit) { used = true; break; }
            if (!used) return bit;
        }
        throw new InvalidOperationException("out of gateway bits");
    }

    private int PeerOfBit(int bit)
    {
        foreach (var link in _links.Values) if (link.Bit == bit) return link.PeerId;
        return -1;
    }

    private Vector3 Abs(Entity e)
    {
        var c = ContainerRegistry.Resolve(e.Container);
        return c != null ? c.ToWorld(e.Local) : e.Local;
    }

    private void Place(Entity e)
    {
        var abs = Abs(e);
        if (e.AlwaysRelevant) { _index.AddGlobal(e.NetId, e); e.Placement = InterestPlacement.Global; }
        else if (e.RelevanceRadius > Settings.Radius) { _index.AddWide(e.NetId, e); e.Placement = InterestPlacement.Wide; }
        else
        {
            e.Region = RegionOf(e);
            _index.Add(e.NetId, e.Region, e);
            e.Placement = InterestPlacement.Region;
        }
        // An entity spawned inside a dynamic container rides in it from the start (design D3): the index buckets
        // it with the carrier, and that - not its own resolved position - is where it is.
        _index.SetCarrier(e.NetId, e.Container.IsDynamic ? e.Container.NetId : 0);
        SyncPlacement(e);
    }

    /// <summary>
    /// The scope an entity's region key is salted with (docs/scope-frames.md D7): its root carrier's, exactly as
    /// the gateway resolves it, so a crate in a ship in a scope is bucketed in that scope and not in the public
    /// world. Zero for the public world, and then the key is the plain packing, as it has always been.
    /// </summary>
    private ulong ScopeOf(Entity e)
    {
        var root = _entities.TryGetValue(_index.RootOf(e.NetId), out var r) ? r : e;
        return ContainerRegistry.Resolve(root.Container)?.InstanceId ?? 0;
    }

    /// <summary>The region key an entity is held under: its absolute position, packed, salted with its scope.</summary>
    private ulong RegionOf(Entity e)
    {
        var abs = Abs(e);
        return RegionKeys.Salt(Grid.RegionOf(abs.x, abs.y, abs.z), ScopeOf(e));
    }

    private bool IsOwnersGateway(Entity e, int peerId) =>
        e.OwnerClientId != 0 && _sessionGateway.TryGetValue(e.OwnerClientId, out string? gateway) &&
        Gateways.TryGetValue(peerId, out string? id) && id == gateway;

    /// <summary>Whether this gateway hears about the entity at all: its region, its foci for a wide one, global, or it owns it.</summary>
    private bool Reaches(Entity e, GatewayLink link)
    {
        if (IsOwnersGateway(e, link.PeerId)) return true;
        // An entity a gateway named in InterestSubscribe.Entities is sticky for it, wherever it sits (design D5).
        if (link.Receiver.Entities.Contains(e.NetId)) return true;
        // Placement, not the prefab flag: a carried entity is published exactly as its root carrier is, so an
        // always-relevant crate riding in an ordinary ship is bucketed with the ship (design D70).
        if (e.Placement == InterestPlacement.Global) return true;
        if (e.Placement == InterestPlacement.Wide) return ReachesWide(e, link);
        return link.Receiver.Contains(e.Region);
    }

    /// <summary>A wide entity is matched against foci directly, at the root carrier's position and radius.</summary>
    private bool ReachesWide(Entity e, GatewayLink link)
    {
        var subject = _entities.TryGetValue(_index.RootOf(e.NetId), out var root) ? root : e;
        var abs = Abs(subject);
        var foci = link.Receiver.FociRegions;
        ulong salt = RegionKeys.SaltOf(ScopeOf(subject));
        double r2 = (double)subject.RelevanceRadius * subject.RelevanceRadius;
        for (int i = 0; i < foci.Count; i++) if (Grid.SqrDistanceToRegion(foci[i] ^ salt, abs.x, abs.y, abs.z) <= r2) return true;
        return false;
    }

    /// <summary>The gateways a wide entity currently reaches, for <see cref="CarriedTransition"/>. Spatial only.</summary>
    private ulong WideMaskOf(ulong netId)
    {
        if (!_entities.TryGetValue(netId, out var e)) return 0;
        ulong mask = 0;
        foreach (var link in _links.Values) if (ReachesWide(e, link)) mask |= 1UL << link.Bit;
        return mask;
    }

    /// <summary>
    /// Send a newly subscribed region's snapshot shallowest carrier first, to any depth — the rule
    /// <c>NebulaWorker.SendSpawnsInCarrierOrder</c> follows (design D71). Depth comes from the index, which
    /// refuses cyclic links, so the walk goes as deep as the deepest entity in the snapshot and no entity is
    /// silently left out of it.
    /// </summary>
    private void SendSpawnsInCarrierOrder(int peerId)
    {
        int deepest = 0;
        _spawnDepth.Clear();
        for (int i = 0; i < _spawnOrder.Count; i++)
        {
            int d = _index.DepthOf(_spawnOrder[i].NetId);
            _spawnDepth.Add(d);
            if (d > deepest) deepest = d;
        }
        for (int depth = 0; depth <= deepest; depth++)
            for (int i = 0; i < _spawnOrder.Count; i++)
                if (_spawnDepth[i] == depth) SendSpawn(peerId, _spawnOrder[i]);
        _spawnDepth.Clear();
        _spawnOrder.Clear();
    }

    private ulong MaskFor(Entity e)
    {
        ulong mask = 0;
        foreach (var link in _links.Values) if (Reaches(e, link)) mask |= 1UL << link.Bit;
        return mask;
    }

    private void Each(ulong netId, Action<int, Entity> send)
    {
        if (!_entities.TryGetValue(netId, out var e)) return;
        foreach (var link in _links.Values) if (Reaches(e, link)) send(link.PeerId, e);
        Transport.Flush();
    }

    private void PublishOwned(Entity e)
    {
        foreach (var link in _links.Values) if (IsOwnersGateway(e, link.PeerId)) SendSpawn(link.PeerId, e);
    }

    private void SendSpawn(int peerId, Entity e)
    {
        if (peerId < 0) return;
        _w.Reset();
        new EntitySpawnMsg
        {
            NetId = e.NetId, OwnerClientId = e.OwnerClientId,
            OwnerIdentity = _identities.TryGetValue(e.OwnerClientId, out string identity) ? identity : "",
            Epoch = e.Epoch, Container = e.Container, LocalPosition = e.Local,
            LocalRotation = Quaternion.identity, LocalScale = Vector3.one,
            RelevanceRadius = e.RelevanceRadius,
            InterestFlags = e.AlwaysRelevant ? EntityInterestFlags.AlwaysRelevant : EntityInterestFlags.None,
            InterestGroup = e.InterestGroup,
        }.Write(_w, MsgId.EntitySpawn);
        Transport.Send(peerId, Delivery.ReliableOrdered, _w.ToSegment());
        if (_links.TryGetValue(peerId, out var link)) { Bump(SpawnsSent, link.GatewayId); SpawnLog.Add((link.GatewayId, e.NetId)); }
    }

    private void SendForget(int peerId, Entity e)
    {
        if (peerId < 0) return;
        _w.Reset();
        new EntityForgetMsg { NetId = e.NetId, Epoch = e.Epoch }.Write(_w);
        Transport.Send(peerId, Delivery.ReliableOrdered, _w.ToSegment());
        if (_links.TryGetValue(peerId, out var link)) { Bump(ForgetsSent, link.GatewayId); ForgetLog.Add((link.GatewayId, e.NetId)); }
    }

    private static void Bump(Dictionary<string, int> counter, string key) => counter[key] = counter.TryGetValue(key, out int n) ? n + 1 : 1;

    public void Dispose() => Transport.Dispose();
}

/// <summary>A client link: sends Hello on connect and collects what the gateway says (unpacking batches).</summary>
public sealed class FakeClient : IDisposable
{
    public readonly LiteNetTransport Transport = new("fake-client");
    public WelcomeMsg? Welcome;
    public JoinRejectedMsg? Rejected;
    public string? Replaced;
    public JoinState Join;
    /// <summary>Why the gateway says the join is held (<see cref="JoinHoldReason"/>).</summary>
    public JoinHoldReason JoinReason;
    public int DrainWithin = -1;
    public bool Disconnected;
    /// <summary>Every spawn ever received, in order (a re-entry appears twice; that is what the churn tests read).</summary>
    public readonly List<ulong> Spawned = new();
    public readonly List<ulong> Despawned = new();
    /// <summary>The replicas this client currently holds: spawns minus despawns, with the view-sequence guard applied.</summary>
    public readonly HashSet<ulong> Replicas = new();
    /// <summary>Entities this client was sent state, vars, sync state or an RPC for. A leak shows up here first.</summary>
    public readonly HashSet<ulong> HeardAbout = new();
    public readonly HashSet<string> Containers = new();
    /// <summary>
    /// Everything that arrived in the order it arrived, as <c>container+ c2</c>, <c>container- c2</c>,
    /// <c>spawn 6600</c>, <c>despawn 6600</c>. Design §8 is an ordering rule — a row before the spawn that
    /// names it, a row removed only after the entities in it are gone — and a set cannot be asked about order.
    /// </summary>
    public readonly List<string> Wire = new();
    /// <summary>Container rows ever upserted, counting repeats (a lease change legitimately re-sends rows).</summary>
    public int ContainerUpsertRows;
    /// <summary>
    /// Rows naming the same container twice inside <b>one</b> message. A lease refresh may re-send a row the
    /// client already holds, but one collection pass listing the same container twice is a missing dedupe
    /// between two foci that overlap.
    /// </summary>
    public int DuplicateContainerRows;
    public int VarsReceived, StatesReceived, SyncStatesReceived, RpcsReceived, DuplicateSpawns, OrphanUpdates;
    /// <summary>The last packet this client could not parse, if any. A test that loses messages looks here first.</summary>
    public string LastError = "";
    public long BytesIn;

    private readonly Dictionary<ulong, ushort> _viewSeq = new();
    private readonly HashSet<string> _inMessage = new();
    private readonly string _token, _session, _name, _scope;
    private readonly int _peer;

    /// <param name="scope">The simulation scope to join into (<see cref="HelloMsg.ScopeKey"/>); empty is the public world.</param>
    public FakeClient(int port, string name, string token = "", string session = "", string scope = "")
    {
        _name = name; _token = token; _session = session; _scope = scope;
        _peer = Transport.Connect("127.0.0.1", port);
    }

    public void SendInput()
    {
        var w = new NetworkWriter();
        new ClientInputMsg { Frames = new List<ClientInputMsg.Frame> { new() { Tick = 1, Payload = new byte[] { 1 } } } }.Write(w, MsgId.ClientInput);
        Transport.Send(_peer, Delivery.Sequenced, w.ToSegment());
    }

    /// <summary>
    /// Tell the gateway where the player is looking, in absolute world coordinates — what a real client sends
    /// after adding its floating origin to its frame position. It is a hint: the gateway decides what it is
    /// worth. <see cref="SendFocusHintInFrame"/> is the same thing said the way a shifted client would say it.
    /// </summary>
    public void SendFocusHint(Vector3 position, byte generation = 0) =>
        SendFocusHint(position.x, position.y, position.z, generation);

    /// <inheritdoc cref="SendFocusHint(Vector3,byte)"/>
    public void SendFocusHint(double x, double y, double z, byte generation = 0)
    {
        var w = new NetworkWriter();
        new ClientFocusHintMsg { X = x, Y = y, Z = z, Generation = generation }.Write(w);
        Transport.Send(_peer, Delivery.Sequenced, w.ToSegment());
        Transport.Flush();
    }

    /// <summary>
    /// A hint from a client whose floating origin sits at <paramref name="origin"/>: it holds the point as
    /// <paramref name="framePosition"/> in its own space and sends the sum, which is why a shift does not move
    /// what the gateway thinks it is watching.
    /// </summary>
    public void SendFocusHintInFrame(Vector3 framePosition, Vector3 origin, byte generation = 0) =>
        SendFocusHint(origin.x + (double)framePosition.x, origin.y + (double)framePosition.y, origin.z + (double)framePosition.z, generation);

    /// <summary>Withdraw the hint, as <c>NebulaClient.ClearFocusHint</c> does: reliably, under a new generation.</summary>
    public void ClearFocusHint(byte generation)
    {
        var w = new NetworkWriter();
        new ClientFocusHintMsg { Generation = generation, Clear = true }.Write(w);
        Transport.Send(_peer, Delivery.ReliableOrdered, w.ToSegment());
        Transport.Flush();
    }

    public void Poll()
    {
        Transport.Poll(e =>
        {
            if (e.Type == TransportEvent.Kind.Connected)
            {
                var w = new NetworkWriter();
                new HelloMsg { Role = PeerRole.Client, Id = _name, Token = _token, Session = _session, ScopeKey = _scope }.Write(w);
                Transport.Send(e.PeerId, Delivery.ReliableOrdered, w.ToSegment());
            }
            else if (e.Type == TransportEvent.Kind.Disconnected) Disconnected = true;
            else if (e.Type == TransportEvent.Kind.Data)
            {
                BytesIn += e.Data.Count;
                // A malformed message must fail the test loudly rather than silently cost the client a batch.
                try { Dispatch(new NetworkReader(e.Data)); }
                catch (Exception ex) { LastError = ex.ToString(); }
            }
        });
        Transport.Flush();
    }

    private void Dispatch(NetworkReader r)
    {
        var id = (MsgId)r.ReadByte();
        switch (id)
        {
            case MsgId.Batch:
            {
                int n = r.ReadUShort();
                for (int i = 0; i < n; i++) Dispatch(new NetworkReader(r.ReadSegment(r.ReadUShort())));
                break;
            }
            case MsgId.Welcome: Welcome = WelcomeMsg.Read(r); break;
            case MsgId.JoinRejected: Rejected = JoinRejectedMsg.Read(r); break;
            case MsgId.SessionReplaced: Replaced = SessionReplacedMsg.Read(r).Reason; break;
            case MsgId.JoinStatus: { var js = JoinStatusMsg.Read(r); Join = js.State; JoinReason = js.Reason; break; }
            case MsgId.GatewayDraining: DrainWithin = GatewayDrainingMsg.Read(r).ReconnectWithinSeconds; break;
            case MsgId.ContainerOwnership:
            {
                var update = ContainerOwnershipMsg.Read(r);
                if (update.Full) Containers.Clear();
                if (update.Upserts != null)
                {
                    _inMessage.Clear();
                    foreach (var e in update.Upserts)
                    {
                        ContainerUpsertRows++;
                        if (!_inMessage.Add(e.ContainerId)) DuplicateContainerRows++;
                        Containers.Add(e.ContainerId);
                        Wire.Add("container+ " + e.ContainerId);
                    }
                }
                if (update.Removes != null)
                    foreach (string removed in update.Removes)
                    {
                        Containers.Remove(removed);
                        Wire.Add("container- " + removed);
                    }
                break;
            }
            case MsgId.EntitySpawn:
            {
                var msg = EntitySpawnMsg.Read(r);
                Spawned.Add(msg.NetId);
                Wire.Add("spawn " + msg.NetId);
                // A spawn with no view sequence is a relayed in-place update (an authority transfer, a container
                // change) for an entity already in the set; only a new view of an entity we still hold would be
                // the gateway telling us the same thing twice.
                bool held = !Replicas.Add(msg.NetId);
                if (msg.ViewSeq != 0)
                {
                    if (held && _viewSeq.TryGetValue(msg.NetId, out ushort seen) && msg.ViewSeq > seen) DuplicateSpawns++;
                    _viewSeq[msg.NetId] = msg.ViewSeq;
                }
                break;
            }
            case MsgId.EntityDespawn:
            {
                var msg = EntityDespawnMsg.Read(r);
                // The same guard a real client applies (design D4): a despawn of a view we have left behind is
                // stale and must not kill the replica a newer spawn created.
                if (msg.ViewSeq != 0 && _viewSeq.TryGetValue(msg.NetId, out ushort current) && msg.ViewSeq < current) break;
                _viewSeq.Remove(msg.NetId);
                Despawned.Add(msg.NetId);
                Wire.Add("despawn " + msg.NetId);
                Replicas.Remove(msg.NetId);
                break;
            }
            case MsgId.WorldState:
            {
                WorldStateMsg.ReadHeader(r, out _, out _, out ushort count);
                for (int i = 0; i < count; i++) { var entry = EntityStateEntry.Read(r); Note(entry.NetId); StatesReceived++; }
                break;
            }
            case MsgId.EntityVars: { Note(EntityVarsMsg.Read(r).NetId); VarsReceived++; break; }
            case MsgId.EntityState: { Note(EntitySyncMsg.Read(r).NetId); SyncStatesReceived++; break; }
            case MsgId.EntityRpc: { Note(EntityRpcMsg.Read(r).NetId); RpcsReceived++; break; }
        }
    }

    private void Note(ulong netId)
    {
        HeardAbout.Add(netId);
        if (!Replicas.Contains(netId)) OrphanUpdates++;
    }

    public void Disconnect() => Transport.Disconnect(_peer);
    public void Dispose() => Transport.Dispose();
}

/// <summary>
/// A running mesh in one process: an in-memory control plane, one or more subscription-aware workers and one or
/// more real <see cref="NebulaGateway"/> instances on real UDP sockets. The gateways are the code under test;
/// everything else exists so they have something to be wrong about.
/// </summary>
public sealed class Fleet : IDisposable
{
    public readonly LocalControlPlane Plane = new();
    public readonly List<FakeWorker> Workers = new();
    public readonly List<NebulaGateway> Gateways = new();
    public readonly List<int> Ports = new();
    public readonly List<FakeClient> Clients = new();
    public readonly HashSet<NebulaGateway> PausedGateways = new();
    public readonly List<string> ContainerIds = new();
    /// <summary>Called once per pump, after the gateways tick: where a test drives its workers' content.</summary>
    public Action? OnPump;

    private readonly string _meshToken;
    private readonly float _reclaimSeconds;
    private readonly bool _singleSession;
    private readonly Action<NebulaConfig>? _configure;
    private InterestGrid _grid = InterestGrid.Resolve(InterestSettings.Default);
    private InterestSettings _settings = InterestSettings.Default;
    private int _gatewaySeq, _workerSeq;

    /// <summary>The single-worker mesh the fleet-safety tests use (one container, one worker).</summary>
    public FakeWorker Worker => Workers[0];

    public Fleet(int gateways, string meshToken = "", float reclaimSeconds = 30f, bool singleSession = true,
        int workers = 1, Action<NebulaConfig>? configure = null, Action<Fleet>? world = null)
    {
        _meshToken = meshToken;
        _reclaimSeconds = reclaimSeconds;
        _singleSession = singleSession;
        _configure = configure;
        Plane.Connect();
        for (int i = 0; i < workers; i++) StartWorker();
        if (world != null) world(this);
        else Assign("c0", Workers[0].WorkerId);
        for (int i = 0; i < gateways; i++) StartGateway();
    }

    // -------------------------------------------------------------------- growing the mesh, and losing parts of it
    //
    // The scale and failure suite (docs/scale-suite.md) needs a mesh whose pieces come and go while clients stay
    // connected. These are the only ways to do that here: a fixture that reached into the gateway or the control
    // plane to fake a loss would be testing the fake.

    /// <summary>
    /// Add a worker: a new process registering with the control plane. It owns nothing until a container is
    /// assigned to it, exactly as a launched worker does.
    /// </summary>
    public FakeWorker StartWorker(string? workerId = null)
    {
        ushort index = (ushort)(++_workerSeq);
        var worker = new FakeWorker(_meshToken, workerId ?? "w" + index, index) { Grid = _grid, Settings = _settings };
        Workers.Add(worker);
        Plane.RegisterWorker(worker.WorkerId, index, "127.0.0.1", (ushort)worker.Port);
        Plane.HeartbeatWorker(worker.WorkerId, WorkerStatus.Ready, new WorkerStats());
        return worker;
    }

    /// <summary>
    /// A worker process dies: its socket goes away (so every gateway link drops) and the control plane loses its
    /// registration, which orphans every lease it held. Nothing is handed over and nothing is drained — that is the
    /// point. The gateways keep running and their clients keep their connections.
    /// <para>Returns the containers left orphaned, which is what the restore has to place again.</para>
    /// </summary>
    public List<string> KillWorker(FakeWorker worker)
    {
        var orphaned = new List<string>();
        foreach (var lease in Plane.Leases) if (lease.WorkerId == worker.WorkerId) orphaned.Add(lease.ContainerId);
        Workers.Remove(worker);
        Plane.UnregisterWorker(worker.WorkerId);
        worker.Dispose();
        return orphaned;
    }

    /// <summary>Start another gateway process; returns its index in <see cref="Gateways"/>.</summary>
    public int StartGateway(Action<NebulaConfig>? configure = null)
    {
        using var reserve = new UdpClient(0);
        int port = ((IPEndPoint)reserve.Client.LocalEndPoint!).Port; reserve.Close();
        var config = new NebulaConfig
        {
            GatewayPort = (ushort)port, AuthSigningKey = "fleet-key", MeshToken = _meshToken, WebClients = false,
            SessionReclaimSeconds = _reclaimSeconds, SingleSessionPerPlayer = _singleSession, GatewayDrainReconnectSeconds = 7,
        };
        _configure?.Invoke(config);
        configure?.Invoke(config);
        var gw = new NebulaGateway();
        gw.Initialize(config, Plane, null, "gw" + (++_gatewaySeq));
        Gateways.Add(gw);
        Ports.Add(port);
        _grid = config.ToInterestGrid();
        _settings = config.ToInterestSettings();
        foreach (var worker in Workers) { worker.Grid = _grid; worker.Settings = _settings; }
        return Gateways.Count - 1;
    }

    /// <summary>
    /// Lose a gateway <b>without a drain</b>, in one of the two ways that actually happen, because they are
    /// different failures and the suite measures both (docs/scale-suite.md, D7a):
    /// <list type="bullet">
    /// <item><b>Hard</b> (<paramref name="hard"/> true): the process is gone as far as everyone else is concerned.
    /// It stops ticking, so it stops heartbeating, stops answering its clients and never releases the session
    /// claims it holds in the coordinator. Its socket is not closed from outside (nothing here can), so a client
    /// notices by transport timeout rather than by a reset; that is the only difference from a killed process and
    /// it shortens nothing.</item>
    /// <item><b>Abrupt stop</b> (<paramref name="hard"/> false): <see cref="NebulaGateway.Dispose"/> — the socket
    /// closes, the control-plane row goes and the session claims are released, but no drain was requested and no
    /// client was told to reconnect. A container being stopped, not a crash.</item>
    /// </list>
    /// The gateway leaves the pump either way; its port stays in <see cref="Ports"/> so indices stay stable.
    /// </summary>
    public void KillGateway(int index, bool hard = true)
    {
        var gw = Gateways[index];
        PausedGateways.Add(gw);
        if (!hard) { Gateways.Remove(gw); gw.Dispose(); }
    }

    /// <summary>
    /// The control plane restarts: an orchestrator process restart, or a database failover under it. Everything it
    /// holds goes, and what was durable comes back. <paramref name="snapshot"/> is a document taken earlier with
    /// <see cref="LocalControlPlane.ToJson"/>; null models a restart that came back with nothing at all.
    /// <para>
    /// The gateways and workers are untouched, which is the point: this measures what the rest of the mesh does
    /// while the control plane is away, and how long it takes to be whole again.
    /// </para>
    /// </summary>
    public void RestartControlPlane(string? snapshot)
    {
        Plane.ResetControlPlane();
        Plane.Tick();
        if (snapshot != null) Plane.Import(ControlPlaneJson.Parse(snapshot));
    }

    /// <summary>Give a container an owner. The container itself comes from the manifest the test loaded.</summary>
    public void Assign(string containerId, string workerId)
    {
        Plane.EnsureContainer(containerId);
        Plane.AssignContainer(containerId, workerId);
        if (!ContainerIds.Contains(containerId)) ContainerIds.Add(containerId);
    }

    public FakeClient Connect(int gateway, string name, string token = "", string session = "", string scope = "")
    {
        var c = new FakeClient(Ports[gateway], name, token, session, scope);
        Clients.Add(c);
        return c;
    }

    /// <summary>One pass over everything: control plane, gateways, workers, clients.</summary>
    public void Pump()
    {
        foreach (var worker in Workers) Plane.HeartbeatWorker(worker.WorkerId, WorkerStatus.Ready, new WorkerStats());
        Plane.Tick();
        foreach (var g in Gateways) if (!PausedGateways.Contains(g)) g.Tick();
        foreach (var worker in Workers) worker.Poll();
        foreach (var c in Clients) c.Poll();
        OnPump?.Invoke();
    }

    /// <summary>Pump everything until <paramref name="until"/> holds or the time is up; returns whether it held.</summary>
    public bool Run(Func<bool> until, double seconds = 5)
    {
        var clock = Stopwatch.StartNew();
        bool ok = false;
        while (clock.Elapsed.TotalSeconds < seconds && !(ok = until()))
        {
            Pump();
            Thread.Sleep(5);
        }
        return ok;
    }

    /// <summary>Pump for a fixed stretch of wall time (settling, soak steps).</summary>
    public void RunFor(double seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds) { Pump(); Thread.Sleep(2); }
    }

    public void Dispose()
    {
        foreach (var c in Clients) c.Dispose();
        foreach (var g in Gateways) g.Dispose();
        foreach (var w in Workers) w.Dispose();
        Plane.Dispose();
    }
}
