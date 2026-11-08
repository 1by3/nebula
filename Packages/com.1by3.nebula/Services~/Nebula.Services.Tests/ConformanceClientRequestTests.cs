using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Client requests (<c>docs/client-requests.md</c>, protocol 33), the pure rules: the token bucket, the gateway's admission order,
/// a worker's routing decision, the client's reply tracker, and the wire forms. Tier A: no sockets, no engine.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ClientRequestRuleTests
{
    [Test]
    public void ABucketAllowsABurstThenTheRate()
    {
        var bucket = new RequestBucket();
        for (int i = 0; i < 3; i++) Assert.That(bucket.TryTake(0, rate: 2, burst: 3), Is.True, $"burst take {i}");
        Assert.That(bucket.TryTake(0, 2, 3), Is.False, "the burst is spent");
        Assert.That(bucket.TryTake(0.4, 2, 3), Is.False, "0.8 of a token has come back");
        Assert.That(bucket.TryTake(0.5, 2, 3), Is.True, "a whole one has");
        Assert.That(bucket.TryTake(0.5, 2, 3), Is.False);
        Assert.That(bucket.TryTake(100, 2, 3), Is.True, "after a long wait the bucket is full again, not overfull");
        Assert.That(bucket.TryTake(100, 2, 3), Is.True);
        Assert.That(bucket.TryTake(100, 2, 3), Is.True);
        Assert.That(bucket.TryTake(100, 2, 3), Is.False, "full is the burst, no more");
    }

    [Test]
    public void ARateOfZeroIsNoLimit()
    {
        var bucket = new RequestBucket();
        for (int i = 0; i < 1000; i++) Assert.That(bucket.TryTake(0, 0, 1), Is.True);
    }

    private static ClientRequestGate Gate(int maxBytes = 100, float clientRate = 1, int clientBurst = 2, float entityRate = 1, int entityBurst = 3) =>
        new(maxBytes, clientRate, clientBurst, entityRate, entityBurst);

    [Test]
    public void TheGateChecksSizeThenExistenceThenInterestThenTheRates()
    {
        var gate = Gate();
        var sender = new RequestBucket();
        Assert.That(gate.Admit(101, false, false, ref sender, 1, 0), Is.EqualTo(ClientRequestOutcome.RejectedTooLarge), "size is checked before anything else");
        Assert.That(gate.Admit(10, false, false, ref sender, 1, 0), Is.EqualTo(ClientRequestOutcome.RejectedUnknownEntity));
        Assert.That(gate.Admit(10, true, false, ref sender, 1, 0), Is.EqualTo(ClientRequestOutcome.RejectedNotInInterest));
        Assert.That(sender.Started, Is.False, "a request that was never going to be delivered spends no token");
        Assert.That(gate.TrackedEntities, Is.EqualTo(0), "and creates no bucket for an entity the sender cannot see");
        Assert.That(gate.Admit(100, true, true, ref sender, 1, 0), Is.EqualTo(ClientRequestOutcome.Accepted), "exactly the limit is allowed");
        Assert.That(gate.Admit(10, true, true, ref sender, 1, 0), Is.EqualTo(ClientRequestOutcome.Accepted));
        Assert.That(gate.Admit(10, true, true, ref sender, 1, 0), Is.EqualTo(ClientRequestOutcome.RejectedClientRateLimit));
        Assert.That(gate.Admitted, Is.EqualTo(2));
        Assert.That(gate.Refused[(int)ClientRequestOutcome.RejectedClientRateLimit], Is.EqualTo(1));
    }

    [Test]
    public void AnEntityLimitIsSharedByEverySenderAndAClientLimitByEveryEntity()
    {
        var gate = Gate(clientRate: 1, clientBurst: 10, entityRate: 1, entityBurst: 3);
        RequestBucket a = default, b = default;
        for (int i = 0; i < 2; i++) Assert.That(gate.Admit(1, true, true, ref a, 7, 0), Is.EqualTo(ClientRequestOutcome.Accepted));
        Assert.That(gate.Admit(1, true, true, ref b, 7, 0), Is.EqualTo(ClientRequestOutcome.Accepted), "the third request to the entity, from another sender");
        Assert.That(gate.Admit(1, true, true, ref b, 7, 0), Is.EqualTo(ClientRequestOutcome.RejectedEntityRateLimit), "a fourth, from anybody, is over the entity's burst");
        Assert.That(gate.Admit(1, true, true, ref b, 8, 0), Is.EqualTo(ClientRequestOutcome.Accepted), "another entity is not affected");

        var tight = Gate(clientRate: 1, clientBurst: 2, entityRate: 100, entityBurst: 100);
        RequestBucket c = default;
        Assert.That(tight.Admit(1, true, true, ref c, 1, 0), Is.EqualTo(ClientRequestOutcome.Accepted));
        Assert.That(tight.Admit(1, true, true, ref c, 2, 0), Is.EqualTo(ClientRequestOutcome.Accepted));
        Assert.That(tight.Admit(1, true, true, ref c, 3, 0), Is.EqualTo(ClientRequestOutcome.RejectedClientRateLimit), "spreading calls over entities does not get past the client's limit");
    }

    [Test]
    public void AnEntityRefusedForItsRateStillSpentTheSendersToken()
    {
        var gate = Gate(clientRate: 1, clientBurst: 5, entityRate: 1, entityBurst: 1);
        RequestBucket a = default;
        Assert.That(gate.Admit(1, true, true, ref a, 7, 0), Is.EqualTo(ClientRequestOutcome.Accepted));
        for (int i = 0; i < 4; i++) Assert.That(gate.Admit(1, true, true, ref a, 7, 0), Is.EqualTo(ClientRequestOutcome.RejectedEntityRateLimit));
        Assert.That(gate.Admit(1, true, true, ref a, 9, 0), Is.EqualTo(ClientRequestOutcome.RejectedClientRateLimit), "hammering one busy entity is not free");
    }

    [Test]
    public void AnIdleEntityBucketIsForgotten()
    {
        var gate = Gate(entityRate: 10, entityBurst: 5);
        RequestBucket a = default;
        gate.Admit(1, true, true, ref a, 7, 0);
        Assert.That(gate.TrackedEntities, Is.EqualTo(1));
        gate.Sweep(5, idleSeconds: 30);
        Assert.That(gate.TrackedEntities, Is.EqualTo(1), "not idle long enough");
        gate.Sweep(60, idleSeconds: 30);
        Assert.That(gate.TrackedEntities, Is.EqualTo(0));
    }

    [Test]
    public void ALimitOfZeroTurnsTheLimitOff()
    {
        var gate = Gate(clientRate: 0, entityRate: 0);
        RequestBucket a = default;
        for (int i = 0; i < 500; i++) Assert.That(gate.Admit(1, true, true, ref a, 7, 0), Is.EqualTo(ClientRequestOutcome.Accepted));
        Assert.That(gate.TrackedEntities, Is.EqualTo(0));
    }

    [Test]
    public void AWorkerAppliesWhatItOwnsForwardsWhatMovedAndRejectsTheRest()
    {
        Assert.That(ClientRequestRouting.Decide(false, false, false, 0, 3).Outcome, Is.EqualTo(AuthorityCallOutcome.RejectedUnknownEntity));
        Assert.That(ClientRequestRouting.Decide(true, true, false, 0, 3).Action, Is.EqualTo(AuthorityCallAction.Apply));
        Assert.That(ClientRequestRouting.Decide(true, true, true, 9, 3).Action, Is.EqualTo(AuthorityCallAction.Apply), "authority applies whatever the hops");
        Assert.That(ClientRequestRouting.Decide(true, false, true, 0, 3).Action, Is.EqualTo(AuthorityCallAction.Forward));
        Assert.That(ClientRequestRouting.Decide(true, false, true, 2, 3).Action, Is.EqualTo(AuthorityCallAction.Forward));
        Assert.That(ClientRequestRouting.Decide(true, false, true, 3, 3).Outcome, Is.EqualTo(AuthorityCallOutcome.RejectedHopLimit));
        Assert.That(ClientRequestRouting.Decide(true, false, false, 0, 3).Outcome, Is.EqualTo(AuthorityCallOutcome.RejectedUnreachable));
        Assert.That(ClientRequestRouting.OutcomeOf(AuthorityCallOutcome.RejectedHopLimit), Is.EqualTo(ClientRequestOutcome.RejectedHopLimit));
        Assert.That(ClientRequestRouting.OutcomeOf(AuthorityCallOutcome.RejectedUnknownEntity), Is.EqualTo(ClientRequestOutcome.RejectedUnknownEntity));
    }

    [Test]
    public void EveryRequestSettlesOnceWhateverHappens()
    {
        var tracker = new ClientRequestTracker();
        var results = new List<ClientRequestResult>();
        uint a = tracker.Mint(), b = tracker.Mint(), c = tracker.Mint();
        Assert.That(new[] { a, b, c }, Is.Unique);
        Assert.That(a, Is.Not.EqualTo(0u));
        tracker.Track(a, 10, results.Add);
        tracker.Track(b, 5, results.Add);
        tracker.Track(c, 100, results.Add);

        Assert.That(tracker.Complete(a, new ClientRequestResult(a, ClientRequestOutcome.Accepted, 42)), Is.True);
        Assert.That(tracker.Complete(a, new ClientRequestResult(a, ClientRequestOutcome.Accepted)), Is.False, "a second reply is late, not a second callback");
        Assert.That(tracker.LateReplies, Is.EqualTo(1));
        tracker.Expire(6);
        Assert.That(results.Select(r => r.Outcome), Is.EqualTo(new[] { ClientRequestOutcome.Accepted, ClientRequestOutcome.TimedOut }));
        Assert.That(results[0].Code, Is.EqualTo((ushort)42));
        Assert.That(tracker.Complete(b, new ClientRequestResult(b, ClientRequestOutcome.Accepted)), Is.False, "a reply after the timeout is late");
        tracker.FailAll(ClientRequestOutcome.Disconnected);
        Assert.That(results.Last().Outcome, Is.EqualTo(ClientRequestOutcome.Disconnected));
        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(tracker.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void TheMessagesRoundTripAndTheStampIsNotInTheClientsMessage()
    {
        var w = new NetworkWriter();
        new ClientRequestMsg { CallId = 77, Flags = ClientRequestFlags.WantsReply, NetId = 123456789012, BehaviourIndex = 3, MethodHash = 0xDEADBEEF, Args = new byte[] { 1, 2, 3 } }.Write(w);
        var r = new NetworkReader(w.ToSegment());
        Assert.That((MsgId)r.ReadByte(), Is.EqualTo(MsgId.ClientRequest));
        var read = ClientRequestMsg.Read(r);
        Assert.That((read.CallId, read.WantsReply, read.NetId, read.BehaviourIndex, read.MethodHash), Is.EqualTo((77u, true, 123456789012ul, (byte)3, 0xDEADBEEF)));
        Assert.That(read.Args, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(r.Remaining, Is.EqualTo(0), "the client's message carries no sender field the gateway could be tempted to believe");

        var claims = new Dictionary<string, string> { ["role"] = "builder", ["tier"] = "2" };
        w = new NetworkWriter();
        new ClientRequestRoutedMsg
        {
            ClientId = 9, Identity = "oidc|abc", Claims = claims, PawnNetId = 44, HasPawnPosition = true, PawnPosition = new Vector3(1, 2, 3),
            CallId = 77, Flags = ClientRequestFlags.WantsReply, Hops = 1, NetId = 5, BehaviourIndex = 2, MethodHash = 6, Args = new byte[] { 9 },
        }.Write(w);
        r = new NetworkReader(w.ToSegment());
        Assert.That((MsgId)r.ReadByte(), Is.EqualTo(MsgId.ClientRequestRouted));
        var routed = ClientRequestRoutedMsg.Read(r);
        Assert.That((routed.ClientId, routed.Identity, routed.PawnNetId, routed.HasPawnPosition, routed.PawnPosition), Is.EqualTo((9ul, "oidc|abc", 44ul, true, new Vector3(1, 2, 3))));
        Assert.That(routed.Claims, Is.EquivalentTo(claims));
        Assert.That((routed.CallId, routed.Hops, routed.NetId, routed.BehaviourIndex, routed.MethodHash, routed.WantsReply), Is.EqualTo((77u, (byte)1, 5ul, (byte)2, 6u, true)));
        Assert.That(r.Remaining, Is.EqualTo(0));

        w = new NetworkWriter();
        new ClientRequestRoutedMsg { ClientId = 1, Claims = null, Args = null }.Write(w);
        r = new NetworkReader(w.ToSegment()); r.ReadByte();
        var bare = ClientRequestRoutedMsg.Read(r);
        Assert.That(bare.Claims, Is.Empty, "an anonymous player has no claims, never a null");
        Assert.That(bare.HasPawnPosition, Is.False);

        w = new NetworkWriter();
        new ClientRequestReplyMsg { ClientId = 9, CallId = 77, Outcome = ClientRequestOutcome.RejectedEntityRateLimit, Code = 513 }.Write(w);
        r = new NetworkReader(w.ToSegment());
        Assert.That((MsgId)r.ReadByte(), Is.EqualTo(MsgId.ClientRequestReply));
        var reply = ClientRequestReplyMsg.Read(r);
        Assert.That(reply.ToResult().Outcome, Is.EqualTo(ClientRequestOutcome.RejectedEntityRateLimit));
        Assert.That((reply.CallId, reply.Code), Is.EqualTo((77u, (ushort)513)));
    }

    [Test]
    public void TheWindowIs32To33AndTheMessageIdsFollowFarPoses()
    {
        Assert.That(HelloMsg.ProtocolVersion, Is.EqualTo((ushort)33));
        Assert.That(HelloMsg.MinProtocolVersion, Is.EqualTo((ushort)32), "additive: a client of 32 still joins");
        Assert.That(HelloMsg.ClientRequestVersion, Is.EqualTo((ushort)33));
        Assert.That(((int)MsgId.FarPoses, (int)MsgId.ClientRequest, (int)MsgId.ClientRequestRouted, (int)MsgId.ClientRequestReply), Is.EqualTo((53, 54, 55, 56)));
        Assert.That(ProtocolCompatibility.ClientProtocolAccepted(31), Is.False);
    }
}

/// <summary>
/// <b>Conformance: client requests across a worker seam</b> (<c>docs/client-requests.md</c>, FND-50). The gateway half, tier A: the
/// real <see cref="NebulaGateway"/> over loopback UDP, two <see cref="FakeWorker"/>s and real clients. A client whose pawn is on
/// the first worker sends a request to an entity the second worker owns: it reaches the second worker alone, stamped with the sender
/// the gateway knows (id, identity, pawn and where it was), and the worker's reply reaches the client. A request that is oversize,
/// for an unknown entity, for an entity outside the sender's interest, or over a rate limit is dropped at the gateway with its
/// reason, which the client is told when it asked. The worker half (the handler receiving the stamp, the reply, the hop on a handover)
/// is <c>ConformanceClientRequestWorkerTests</c> in the EditMode suite, on real workers.
/// </summary>
[TestFixture]
[TestFixture]
[Category("Conformance")]
public class ConformanceClientRequestGatewayTests
{
    private const ulong Target = 7001, Far = 7002;
    private ScaleWorld _world = null!;

    [SetUp] public void Setup() => _world = new ScaleWorld(3);
    [TearDown] public void Cleanup() => _world.Dispose();

    // The pawns stand in c0 at local 60. The control plane decides which worker a joining player lands on, so a scenario asks which
    // one it was and puts the entity on the other: that is the seam. The entity at c1's local -60 is 8 m from such a pawn.
    private Fleet Mesh(Action<NebulaConfig>? configure = null)
    {
        var fleet = new Fleet(1, workers: 2, configure: configure, world: _world.Deal);
        foreach (var w in fleet.Workers) w.PawnPlacement = _ => new Vector3(60, 0, 0);
        return fleet;
    }

    private static (FakeClient Client, ulong Id, FakeWorker Home, FakeWorker Other) Join(Fleet fleet, string name)
    {
        var client = fleet.Connect(0, name);
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined && client.Welcome != null, seconds: 15), Is.True, name + " joins");
        ulong id = client.Welcome!.Value.ClientId;
        Assert.That(fleet.Run(() => fleet.Workers.Any(w => w.Pawns.ContainsKey(id)), seconds: 10), Is.True, name + " has a pawn");
        var home = fleet.Workers.Single(w => w.Pawns.ContainsKey(id));
        return (client, id, home, fleet.Workers.Single(w => w != home));
    }

    [Test]
    public void ARequestForAnEntityOnTheOtherWorkerIsStampedRoutedAndAnswered()
    {
        using var fleet = Mesh();
        var (client, id, home, other) = Join(fleet, "builder");
        other.Spawn(Target, new Vector3(-60, 0, 0), new ContainerRef(1));
        Assert.That(fleet.Run(() => client.Replicas.Contains(Target), seconds: 10), Is.True, "the client holds a replica of an entity the other worker owns");

        client.SendClientRequest(Target, callId: 9, args: new byte[] { 1, 2, 3 }, methodHash: 0xABCD, behaviour: 2);
        Assert.That(fleet.Run(() => other.ClientRequests.Count > 0, seconds: 10), Is.True, "the owner of the entity gets it");
        fleet.RunFor(0.3);
        Assert.That(home.ClientRequests, Is.Empty, "the worker that holds the pawn does not");
        Assert.That(other.ClientRequests, Has.Count.EqualTo(1));
        var routed = other.ClientRequests[0];
        var request = routed.Request;
        Assert.That(request.ClientId, Is.EqualTo(id), "stamped with the sender's id");
        Assert.That(request.Identity, Is.EqualTo(client.Welcome!.Value.Identity).And.Not.Empty, "and the identity the gateway verified");
        Assert.That(request.PawnNetId, Is.EqualTo(home.Pawns[id]), "and the pawn, which lives on the other worker");
        Assert.That(request.HasPawnPosition, Is.True);
        Assert.That(request.PawnPosition.x, Is.EqualTo(124).Within(1.0), "where the pawn stands, in absolute space (c0's centre is at x=64, the pawn at local 60)");
        Assert.That((request.NetId, request.CallId, request.BehaviourIndex, request.MethodHash, request.Hops), Is.EqualTo((Target, 9u, (byte)2, 0xABCDu, (byte)0)));
        Assert.That(request.Args, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(request.WantsReply, Is.True);

        other.ReplyToClientRequest(routed, ClientRequestOutcome.Accepted, code: 7);
        Assert.That(fleet.Run(() => client.RequestReplies.Count > 0, seconds: 10), Is.True, "the reply reaches the client");
        var reply = client.RequestReplies[0];
        Assert.That((reply.CallId, reply.Outcome, reply.Code), Is.EqualTo((9u, ClientRequestOutcome.Accepted, (ushort)7)));
        Assert.That(fleet.Gateways[0].ClientRequestsRouted, Is.EqualTo(1));
    }

    [Test]
    public void ARequestWithNoReplyWantedGetsNoReplyEvenWhenDropped()
    {
        using var fleet = Mesh();
        var (client, _, _, _) = Join(fleet, "quiet");
        client.SendClientRequest(123456, callId: 0);
        fleet.RunFor(0.5);
        Assert.That(client.RequestReplies, Is.Empty);
        Assert.That(fleet.Gateways[0].RequestGate.Refused[(int)ClientRequestOutcome.RejectedUnknownEntity], Is.EqualTo(1), "it was dropped, and counted");
    }

    [Test]
    public void UnknownOversizeAndOutOfInterestRequestsAreDroppedWithTheirReasons()
    {
        using var fleet = Mesh();
        var (a, _, _, other) = Join(fleet, "a");
        other.Spawn(Target, new Vector3(-60, 0, 0), new ContainerRef(1));
        // The far entity is known to the gateway because a second client stands next to it, but A is 200 m away from it.
        foreach (var w in fleet.Workers) w.PawnPlacement = _ => new Vector3(260, 0, 0);
        var (b, _, _, _) = Join(fleet, "b");
        other.Spawn(Far, new Vector3(-62, 0, 0), new ContainerRef(2));
        Assert.That(fleet.Run(() => a.Replicas.Contains(Target) && b.Replicas.Contains(Far), seconds: 10), Is.True);
        Assert.That(a.Replicas.Contains(Far), Is.False, "A cannot see the far entity, B can");

        a.SendClientRequest(555555, callId: 1);
        a.SendClientRequest(Target, callId: 2, args: new byte[2000]);
        a.SendClientRequest(Far, callId: 3);
        Assert.That(fleet.Run(() => a.RequestReplies.Count == 3, seconds: 10), Is.True, "each of A's requests is answered");
        var outcomes = a.RequestReplies.ToDictionary(r => r.CallId, r => r.Outcome);
        Assert.That(outcomes[1], Is.EqualTo(ClientRequestOutcome.RejectedUnknownEntity));
        Assert.That(outcomes[2], Is.EqualTo(ClientRequestOutcome.RejectedTooLarge));
        Assert.That(outcomes[3], Is.EqualTo(ClientRequestOutcome.RejectedNotInInterest), "the entity exists, and the sender does not hold it");
        fleet.RunFor(0.3);
        Assert.That(fleet.Workers.SelectMany(w => w.ClientRequests), Is.Empty, "no worker saw any of them");

        // B, who can see the far entity, may send to it.
        b.SendClientRequest(Far, callId: 4);
        Assert.That(fleet.Run(() => other.ClientRequests.Count > 0, seconds: 10), Is.True);
    }

    [Test]
    public void ClientsAreRateLimitedEachAndAnEntityAcrossThemAll()
    {
        using var fleet = Mesh(c => { c.ClientRequestBurst = 3; c.ClientRequestsPerSecond = 0.01f; c.EntityRequestBurst = 4; c.EntityRequestsPerSecond = 0.01f; });
        var (a, _, _, other) = Join(fleet, "a");
        var (b, _, _, _) = Join(fleet, "b");
        other.Spawn(Target, new Vector3(-60, 0, 0), new ContainerRef(1));
        Assert.That(fleet.Run(() => a.Replicas.Contains(Target) && b.Replicas.Contains(Target), seconds: 10), Is.True);

        // A spends its burst of 3 on the entity (3 of the entity's 4), and the fourth is over its own limit.
        for (uint i = 1; i <= 4; i++) a.SendClientRequest(Target, callId: i);
        Assert.That(fleet.Run(() => a.RequestReplies.Count == 1, seconds: 10), Is.True);
        Assert.That(a.RequestReplies[0].Outcome, Is.EqualTo(ClientRequestOutcome.RejectedClientRateLimit), "a client over its rate hears why");
        // B has its own bucket: one more fits in the entity's 4, the next is over the ENTITY's limit, though B has tokens left.
        b.SendClientRequest(Target, callId: 1);
        b.SendClientRequest(Target, callId: 2);
        Assert.That(fleet.Run(() => b.RequestReplies.Count == 1, seconds: 10), Is.True);
        Assert.That(b.RequestReplies[0].Outcome, Is.EqualTo(ClientRequestOutcome.RejectedEntityRateLimit));
        Assert.That(b.RequestReplies[0].CallId, Is.EqualTo(2u));
        Assert.That(fleet.Run(() => other.ClientRequests.Count == 4, seconds: 10), Is.True, "three from A and one from B were routed");
        fleet.RunFor(0.3);
        Assert.That(other.ClientRequests, Has.Count.EqualTo(4));
    }

    [Test]
    public void ARequestFollowsTheEntityToItsNewWorker()
    {
        using var fleet = Mesh();
        var (client, _, home, other) = Join(fleet, "follower");
        other.Spawn(Target, new Vector3(-60, 0, 0), new ContainerRef(1));
        Assert.That(fleet.Run(() => client.Replicas.Contains(Target), seconds: 10), Is.True);

        other.HandOver(Target, home);
        fleet.RunFor(0.5); // the gateway hears of the new owner
        client.SendClientRequest(Target, callId: 1);
        Assert.That(fleet.Run(() => home.ClientRequests.Count + other.ClientRequests.Count > 0, seconds: 10), Is.True);
        fleet.RunFor(0.3);
        Assert.That(home.ClientRequests.Count + other.ClientRequests.Count, Is.EqualTo(1), "delivered once");
        Assert.That(home.ClientRequests.Count, Is.EqualTo(1), "to the worker that owns it now");
    }

    [Test]
    public void AClientOfProtocol32StillJoinsAndAProtocol31ClientIsRefused()
    {
        using var fleet = Mesh();
        var old = fleet.Connect(0, "protocol-32");
        old.AnnounceVersion = 32;
        var older = fleet.Connect(0, "protocol-31");
        older.AnnounceVersion = 31;
        Assert.That(fleet.Run(() => old.Join == JoinState.Joined && older.Rejected != null, seconds: 15), Is.True);
        Assert.That(old.Welcome!.Value.NegotiatedVersion, Is.EqualTo((ushort)32));
        Assert.That(older.Rejected!.Value.Code, Is.EqualTo(JoinRejectReason.ProtocolUnsupported));
        Assert.That(older.Rejected!.Value.SupportedMinVersion, Is.EqualTo((ushort)32));
    }
}
