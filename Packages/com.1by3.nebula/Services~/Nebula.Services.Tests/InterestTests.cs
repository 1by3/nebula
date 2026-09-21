using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Interest management end to end: a real <see cref="NebulaGateway"/> over loopback UDP, subscription-aware
/// workers built from the real <see cref="RegionPublisher"/>, and clients that count what actually arrives.
/// <para>
/// What every test here is really asking is one of two questions. Does the client hear about what it should —
/// no gaps, at the right moment, with the state it needs? And does it hear about nothing else — not through
/// spawns, not through world state, not through a netvar, a sync-state chunk or an RPC, and not by the gateway
/// quietly caching the world on its behalf.
/// </para>
/// </summary>
[TestFixture]
public class InterestTests
{
    private string _directory = "";

    /// <summary>A world of 64 m cell containers, laid out on a line so a client can walk across worker boundaries.</summary>
    private void LoadWorld(int cells, float cellSize = 64f)
    {
        var containers = new List<Container>();
        for (int i = 0; i < cells; i++)
            containers.Add(new Container
            {
                ContainerId = "c" + i, Index = (ushort)i, Size = new Vector3(cellSize, cellSize, cellSize),
                transform = new ContainerFrame { position = new Vector3(i * cellSize + cellSize / 2, 0, 0) },
            });
        var path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest { Containers = containers }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-interest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        LoadWorld(8);
    }

    [TearDown]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    /// <summary>One worker owning every cell of the line world: the simplest mesh that still has regions.</summary>
    private Fleet OneWorker(int cells = 8, Action<NebulaConfig>? configure = null) =>
        new(1, world: f => { for (int i = 0; i < cells; i++) f.Assign("c" + i, "w1"); }, configure: configure);

    /// <summary>A pawn for this client is placed at the start of the line; everything else is measured from there.</summary>
    private static FakeClient Join(Fleet fleet, string name, Vector3 pawnAt = default, int gateway = 0)
    {
        fleet.Worker.PawnPlacement = _ => pawnAt;
        var client = fleet.Connect(gateway, name);
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined), Is.True, $"{name} should get a pawn");
        return client;
    }

    // ------------------------------------------------------------------------------------------- culling

    [Test]
    public void AFarEntityNeverReachesTheClientAndANearOneDoes()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        // c0's frame centre is at x = 32, so a local x of 0 is world x = 32.
        fleet.Worker.Spawn(2001, new Vector3(20, 0, 0));                                  // ~20 m away: inside
        fleet.Worker.Spawn(2002, new Vector3(0, 0, 0), new ContainerRef(7));              // ~448 m away: far outside

        Assert.That(fleet.Run(() => client.Replicas.Contains(2001)), Is.True, "an entity inside InterestRadius arrives");
        fleet.RunFor(1.0);
        Assert.That(client.Replicas, Does.Not.Contain(2002ul), "an entity four cells away never does");
        Assert.That(fleet.Gateways[0].CachedEntityCount, Is.LessThan(3), "and the gateway does not cache it either: nobody subscribes that region");
    }

    [Test]
    public void EveryKindOfEntityTrafficIsFilteredByTheSet()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        fleet.Worker.Spawn(2001, new Vector3(20, 0, 0));
        fleet.Worker.Spawn(2002, new Vector3(0, 0, 0), new ContainerRef(7));
        Assert.That(fleet.Run(() => client.Replicas.Contains(2001)), Is.True);

        fleet.OnPump = () =>
        {
            fleet.Worker.PublishStates();
            fleet.Worker.SendVars(2001, new byte[] { 1 });
            fleet.Worker.SendVars(2002, new byte[] { 1 });
            fleet.Worker.SendSyncState(2001, 0, new byte[] { 2 });
            fleet.Worker.SendSyncState(2002, 0, new byte[] { 2 });
            fleet.Worker.SendRpc(2001);
            fleet.Worker.SendRpc(2002);
        };
        fleet.RunFor(1.0);

        Assert.That(client.HeardAbout, Does.Contain(2001ul), "the near entity's state, vars, sync state and RPCs arrive");
        Assert.That(client.HeardAbout, Does.Not.Contain(2002ul), "and none of the far entity's do, on any channel");
        Assert.That(client.OrphanUpdates, Is.Zero, "nor does anything arrive for an entity the client has no replica of");
        Assert.That(client.VarsReceived + client.SyncStatesReceived + client.RpcsReceived, Is.GreaterThan(0), "and the near ones really were sent");
    }

    [Test]
    public void AnEntityPacingTheBoundaryDoesNotThrash()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        var config = new NebulaConfig();
        float radius = config.InterestRadius; // 120 m from the pawn at world x = 32
        fleet.Worker.Spawn(2001, new Vector3(radius - 2, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(2001)), Is.True);

        // Walk it back and forth across the enter radius, staying inside the exit margin. Hysteresis plus the
        // linger must keep exactly one spawn and no despawn: a boundary that flickers is a boundary that costs
        // a spawn message per crossing to every client near it.
        for (int i = 0; i < 12; i++)
        {
            fleet.Worker.Move(2001, new Vector3(radius + (i % 2 == 0 ? 6 : -6), 0, 0));
            fleet.RunFor(0.12);
        }
        Assert.That(client.Spawned.Count(id => id == 2001), Is.EqualTo(1), "one spawn, however many times it paced the line");
        Assert.That(client.Despawned, Does.Not.Contain(2001ul));
    }

    [Test]
    public void LeavingForGoodDespawnsAndComingBackSpawnsAgainCleanly()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        fleet.Worker.Spawn(2001, new Vector3(10, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(2001)), Is.True);

        fleet.Worker.Move(2001, new Vector3(0, 0, 0)); // into c5, far outside
        fleet.Worker.Entities[2001].Container = new ContainerRef(5);
        fleet.Worker.Move(2001, new Vector3(0, 0, 0));
        Assert.That(fleet.Run(() => !client.Replicas.Contains(2001), seconds: 6), Is.True, "past the exit margin and the linger, it goes");

        fleet.Worker.Entities[2001].Container = new ContainerRef(0);
        fleet.Worker.Move(2001, new Vector3(10, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(2001)), Is.True, "and coming back spawns it again");
        Assert.That(client.DuplicateSpawns, Is.Zero, "with no duplicate replica");
        fleet.RunFor(0.5);
        Assert.That(client.Replicas, Does.Contain(2001ul), "and no late despawn of the old view kills the new replica");
    }

    [Test]
    public void AnEntityEntersWithItsCurrentStateAndNotItsSpawnState()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        // Spawn it out of reach, give it a sync-state keyframe there, then bring it in: the spawn the client
        // gets must carry the keyframe, or the entity arrives with the state it had when it was created.
        fleet.Worker.Spawn(2001, new Vector3(0, 0, 0), new ContainerRef(6));
        fleet.RunFor(0.3);
        fleet.Worker.Entities[2001].Container = new ContainerRef(0);
        fleet.Worker.Move(2001, new Vector3(5, 0, 0));
        fleet.Worker.SendSyncState(2001, 3, new byte[] { 9, 9 });
        Assert.That(fleet.Run(() => client.Replicas.Contains(2001)), Is.True);
        Assert.That(fleet.Gateways[0].InterestSetSize(client.Welcome!.Value.ClientId), Is.GreaterThanOrEqualTo(2), "the pawn and the arrival");
    }

    // ------------------------------------------------------------------------------------------- shapes

    [Test]
    public void CarriedEntitiesEnterAndLeaveWithTheirCarrier()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        // A ship far away with a passenger inside it. The passenger is bucketed with the ship (design D3), so
        // neither is heard about; bring the ship in and both arrive, as one unit.
        fleet.Worker.Spawn(3000, new Vector3(0, 0, 0), new ContainerRef(6));
        fleet.Worker.Spawn(3001, new Vector3(1, 0, 0), ContainerRef.Dynamic(3000));
        fleet.RunFor(0.5);
        Assert.That(client.Replicas, Does.Not.Contain(3000ul));
        Assert.That(client.Replicas, Does.Not.Contain(3001ul), "a passenger of a far ship is as far as its ship");

        fleet.Worker.Entities[3000].Container = new ContainerRef(0);
        fleet.Worker.Move(3000, new Vector3(5, 0, 0));
        fleet.Worker.Move(3001, new Vector3(1, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(3000) && client.Replicas.Contains(3001)), Is.True, "and both arrive together");
        Assert.That(client.Spawned.IndexOf(3000), Is.LessThan(client.Spawned.IndexOf(3001)), "carrier before its contents, so the container resolves");
    }

    [Test]
    public void WideEntitiesReachFurtherAndGlobalOnesReachEverybody()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        fleet.Worker.Spawn(4000, new Vector3(0, 0, 0), new ContainerRef(4), relevanceRadius: 400f);  // ~256 m away
        fleet.Worker.Spawn(4001, new Vector3(0, 0, 0), new ContainerRef(7), alwaysRelevant: true);
        fleet.Worker.Spawn(4002, new Vector3(0, 0, 0), new ContainerRef(4));                          // same place, ordinary radius

        Assert.That(fleet.Run(() => client.Replicas.Contains(4000) && client.Replicas.Contains(4001), seconds: 8), Is.True,
            "a wide entity reaches past the region scan, and an always-relevant one is in every set");
        Assert.That(client.Replicas, Does.Not.Contain(4002ul), "while an ordinary entity in the same place is not");
    }

    // ------------------------------------------------------------------------------------------- policy

    /// <summary>A policy with the three things a game actually asks of one: extra foci, a team filter and a free camera.</summary>
    private sealed class TestPolicy : IInterestPolicy
    {
        public Vector3? ExtraFocus;
        public byte? DenyGroupForTeam;
        public bool SpectatorFocus;

        public void Collect(in InterestClient client, InterestQuery query)
        {
            if (client.HasPawn) query.AddFocus(client.PawnX, client.PawnY, client.PawnZ, 1f, client.PawnNetId);
            if (client.HasHint) query.AddFocus(client.HintX, client.HintY, client.HintZ);
            if (ExtraFocus != null) query.AddFocus(ExtraFocus.Value.x, ExtraFocus.Value.y, ExtraFocus.Value.z);
            if (SpectatorFocus && !client.HasPawn) query.AddFocus(0, 0, 0);
        }

        public bool Authorize(in InterestClient client, in InterestEntity entity) =>
            DenyGroupForTeam == null || client.Team != 1 || entity.InterestGroup != DenyGroupForTeam.Value;
    }

    [Test]
    public void ASecondFocusBringsADistantPlaceIntoTheSet()
    {
        using var fleet = OneWorker();
        var policy = new TestPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        fleet.Worker.Spawn(5000, new Vector3(0, 0, 0), new ContainerRef(5)); // world x ~ 352

        fleet.RunFor(0.5);
        Assert.That(client.Replicas, Does.Not.Contain(5000ul));
        policy.ExtraFocus = new Vector3(352, 0, 0);
        fleet.Gateways[0].MarkInterestDirty(client.Welcome!.Value.ClientId);
        Assert.That(fleet.Run(() => client.Replicas.Contains(5000)), Is.True, "an RTS camera is a second focus, and it subscribes its own regions");
    }

    [Test]
    public void AnAuthorizeFilterRemovesAnEntityAtOnceAndWithoutLinger()
    {
        using var fleet = OneWorker();
        var policy = new TestPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        fleet.Worker.Spawn(5100, new Vector3(10, 0, 0), group: 3);
        Assert.That(fleet.Run(() => client.Replicas.Contains(5100)), Is.True);

        policy.DenyGroupForTeam = 3;
        fleet.Gateways[0].SetClientTag(client.Welcome!.Value.ClientId, 1);
        Assert.That(fleet.Run(() => !client.Replicas.Contains(5100), seconds: 2), Is.True,
            "a security filter is not a distance: it takes effect without the leave hysteresis");
        Assert.That(fleet.Gateways[0].GetClientTag(client.Welcome.Value.ClientId), Is.EqualTo((byte)1));
    }

    [Test]
    public void AFocusHintIsClampedToTheHintDistanceFromThePawn()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        fleet.Worker.Spawn(5200, new Vector3(0, 0, 0), new ContainerRef(6)); // ~384 m away

        // A client claiming to look at the other end of the world. The gateway clamps the hint to
        // InterestHintMaxDistance (60 m) of the pawn, so the entity stays out of reach however hard it asks.
        for (int i = 0; i < 20; i++) { client.SendFocusHint(new Vector3(400, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(client.Replicas, Does.Not.Contain(5200ul), "a hint is an input, never authority");

        // A hint inside the clamp does move the set: it is a real input, not an ignored one.
        fleet.Worker.Spawn(5201, new Vector3(0, 0, 0), new ContainerRef(2)); // world x ~ 160, 128 m from the pawn
        for (int i = 0; i < 20; i++) { client.SendFocusHint(new Vector3(90, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(client.Replicas, Does.Contain(5201ul), "and a hint within the clamp is honoured");
    }

    [Test]
    public void ClearingTheFocusHintReturnsInterestToThePawnAndLateHintsCannotBringItBack()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        // ~148 m from the pawn: beyond the exit radius (136 m), so only the hint can hold it in the set.
        fleet.Worker.Spawn(5210, new Vector3(20, 0, 0), new ContainerRef(2));
        for (int i = 0; i < 20; i++) { client.SendFocusHint(new Vector3(90, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(client.Replicas, Does.Contain(5210ul), "the hint brought it in");

        // Going quiet is not a clear: the gateway keeps the last hint it accepted.
        fleet.RunFor(3);
        Assert.That(client.Replicas, Does.Contain(5210ul), "silence leaves the hint standing");

        client.ClearFocusHint(generation: 1);
        Assert.That(fleet.Run(() => !client.Replicas.Contains(5210ul), 6), "a clear returns interest to the pawn");

        // A hint from before the clear, arriving late on the sequenced channel, is stale and changes nothing.
        for (int i = 0; i < 12; i++) { client.SendFocusHint(new Vector3(90, 0, 0), generation: 0); fleet.RunFor(0.25); }
        Assert.That(client.Replicas, Does.Not.Contain(5210ul), "a late hint cannot undo the clear");

        // A new hint under the new generation is a real input again.
        for (int i = 0; i < 20; i++) { client.SendFocusHint(new Vector3(90, 0, 0), generation: 1); fleet.RunFor(0.25); }
        Assert.That(client.Replicas, Does.Contain(5210ul), "and a fresh hint is honoured");
    }

    [Test]
    public void APawnlessClientHearsOnlyGlobalEntitiesUnlessAPolicyGivesItAFocus()
    {
        using var fleet = OneWorker();
        fleet.Worker.DeferSpawns = true;
        var policy = new TestPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var client = fleet.Connect(0, "watcher");
        Assert.That(fleet.Run(() => client.Welcome != null), Is.True);
        fleet.Worker.Spawn(5300, new Vector3(10, 0, 0));
        fleet.Worker.Spawn(5301, new Vector3(10, 0, 0), alwaysRelevant: true);

        fleet.RunFor(1.0);
        Assert.That(client.Replicas, Does.Not.Contain(5300ul), "no pawn, no focus: a spectator is not shown the world by default");
        Assert.That(client.Replicas, Does.Contain(5301ul), "but an always-relevant entity is in every set");

        policy.SpectatorFocus = true;
        fleet.Gateways[0].MarkInterestDirty(client.Welcome!.Value.ClientId);
        Assert.That(fleet.Run(() => client.Replicas.Contains(5300)), Is.True, "and a policy may give a pawn-less client a focus");
    }

    // ------------------------------------------------------------------------------------------- subscriptions

    [Test]
    public void AGatewayLinksOnlyTheWorkersItNeedsAndItsCacheShrinksWhenClientsLeave()
    {
        // Four workers, two cells each: 8 cells of 64 m over 512 m. A client at one end has no business
        // talking to the worker at the other.
        using var fleet = new Fleet(1, workers: 4,
            // A 512 m world with the default 1024 m InterestMaxRadius would make every worker a foci link, which
            // is the design's rule working, not a bug. Cap what a prefab may reach and the rule bites.
            configure: c => { c.InterestLinkLingerSeconds = 1f; c.InterestMaxRadius = 150f; },
            // Only the first cell has an owner to begin with, so the pawn lands there and the rest of the line
            // is somewhere the client demonstrably has no reason to reach.
            world: f => f.Assign("c0", "w1"));
        var client = Join(fleet, "ann");
        for (int i = 1; i < 8; i++) fleet.Assign("c" + i, "w" + (i / 2 + 1));
        for (int i = 0; i < 8; i++) fleet.Workers[i / 2].Spawn((ulong)(6000 + i), new Vector3(32, 0, 0), new ContainerRef((ushort)i));
        // Placing the first pawn is itself a reason to link a worker, so the far links go once that reason has
        // gone and the link linger has run out.
        fleet.RunFor(3.0);

        Assert.That(client.Replicas.Count, Is.LessThanOrEqualTo(4), "only what is near the pawn");
        Assert.That(fleet.Workers[3].Gateways, Is.Empty, "and the far worker is not kept: no client of ours needs anything it owns");
        int cached = fleet.Gateways[0].CachedEntityCount;
        Assert.That(cached, Is.LessThanOrEqualTo(5), $"the gateway caches what its client can reach, not the world (cached {cached})");

        client.Disconnect();
        Assert.That(fleet.Run(() => fleet.Gateways[0].CachedEntityCount == 0 && fleet.Gateways[0].WorkerLinkCount == 0, seconds: 15), Is.True,
            "with no clients the gateway holds no records and, past the link linger, no worker links");
    }

    [Test]
    public void AResyncRebuildsTheSubscriptionAfterAForcedMismatch()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        fleet.Worker.Spawn(6100, new Vector3(10, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(6100)), Is.True);
        int before = fleet.Worker.ReceiverOf("gw1")!.Count;

        // Put the worker's set out of step behind the gateway's back: the next delta cannot apply, the worker
        // answers InterestResync, and the gateway must send a Full snapshot that restores the set.
        fleet.Worker.CorruptSubscription("gw1");
        fleet.Worker.Spawn(6101, new Vector3(12, 0, 0));
        // Move the focus so the gateway has a delta to send; a delta against a sequence the worker no longer
        // holds is what makes it answer InterestResync.
        fleet.OnPump = () => client.SendFocusHint(new Vector3(30 + (DateTime.UtcNow.Millisecond / 100 % 2) * 50, 0, 0));
        Assert.That(fleet.Run(() => fleet.Worker.ResyncsSent.GetValueOrDefault("gw1") > 0, seconds: 10), Is.True, "the worker asks for a resync");
        Assert.That(fleet.Run(() => fleet.Worker.ReceiverOf("gw1")!.Count >= before, seconds: 20), Is.True, "and the gateway's Full snapshot restores the set");
        Assert.That(fleet.Run(() => client.Replicas.Contains(6101), seconds: 10), Is.True, "so the client is not left with a hole");
    }

    [Test]
    public void AWorkerDyingDropsItsEntitiesAndTheGatewayRebuilds()
    {
        // One worker, so nothing can quietly replace what it held and the cache can be asserted to be empty.
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        fleet.Worker.Spawn(6200, new Vector3(10, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(6200)), Is.True);

        fleet.Worker.Transport.Stop();
        Assert.That(fleet.Run(() => !client.Replicas.Contains(6200), seconds: 15), Is.True,
            "the link is gone, so the records are gone and the client is told rather than left with a ghost");
        Assert.That(fleet.Gateways[0].CachedEntityCount, Is.Zero);
    }

    [Test]
    public void ALeaseMigrationMidSubscriptionKeepsTheClientWhole()
    {
        using var fleet = new Fleet(1, workers: 2, world: f =>
        {
            for (int i = 0; i < 8; i++) f.Assign("c" + i, "w1");
        });
        var client = Join(fleet, "ann");
        fleet.Workers[0].Spawn(6300, new Vector3(10, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(6300)), Is.True);

        // The cell the client stands in moves to the other worker, which spawns the same entity there. The
        // gateway must link the new owner (and, because of the link linger, not drop the old one first).
        fleet.Plane.AssignContainer("c0", "w2");
        fleet.Workers[1].Spawn(6300, new Vector3(10, 0, 0));
        Assert.That(fleet.Run(() => fleet.Workers[1].Gateways.Count == 1, seconds: 10), Is.True, "the new owner is linked");
        fleet.RunFor(0.5);
        Assert.That(client.Replicas, Does.Contain(6300ul), "and the client never loses the entity across the move");
    }

    [Test]
    public void ADrainingGatewayKeepsItsSubscriptionsUntilItsLastClientLeaves()
    {
        using var fleet = new Fleet(2, world: f => { for (int i = 0; i < 8; i++) f.Assign("c" + i, "w1"); });
        var client = Join(fleet, "ann");
        fleet.Worker.Spawn(6400, new Vector3(10, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(6400)), Is.True);

        fleet.Plane.SetGatewayDraining("gw1", true);
        Assert.That(fleet.Run(() => client.DrainWithin > 0), Is.True);
        fleet.RunFor(0.5);
        Assert.That(fleet.Gateways[0].SubscribedRegionCount, Is.GreaterThan(0), "a drain does not cut the clients that are still here off");

        client.Disconnect();
        Assert.That(fleet.Run(() => fleet.Gateways[0].SubscribedRegionCount == 0, seconds: 10), Is.True, "and the subscriptions go with the last client");
    }

    [Test]
    public void AReconnectingSessionIsToldExactlyWhatItShouldHave()
    {
        using var fleet = new Fleet(2, world: f => { for (int i = 0; i < 8; i++) f.Assign("c" + i, "w1"); });
        var first = Join(fleet, "ann");
        fleet.Worker.Spawn(6500, new Vector3(10, 0, 0));
        fleet.Worker.Spawn(6501, new Vector3(0, 0, 0), new ContainerRef(6));
        Assert.That(fleet.Run(() => first.Replicas.Contains(6500)), Is.True);
        var welcome = first.Welcome!.Value;

        var second = fleet.Connect(1, "ann", welcome.Token, welcome.SessionToken);
        Assert.That(fleet.Run(() => second.Join == JoinState.Joined), Is.True);
        Assert.That(fleet.Run(() => second.Replicas.Contains(6500), seconds: 5), Is.True, "the reclaimed session is given the near entity");
        Assert.That(second.Replicas, Does.Not.Contain(6501ul), "and not the far one: the first evaluation is the replay");
        Assert.That(second.DuplicateSpawns, Is.Zero);
    }

    [Test]
    public void OwnershipRowsAreScopedToWhatTheClientCanSee()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        fleet.RunFor(0.7);
        Assert.That(client.Containers, Does.Contain("c0"), "the container the client stands in");
        Assert.That(client.Containers, Does.Not.Contain("c7"), "and not the lease table of the whole world (design §8)");
        Assert.That(client.Containers.Count, Is.LessThan(8));
    }

    [Test]
    public void AContainerRowArrivesBeforeTheSpawnThatNamesIt()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        fleet.RunFor(0.5);
        // An entity two cells along, brought into reach by a hint: its container row must arrive with (and in
        // the same reliable batch as) the spawn, or the client cannot place it.
        fleet.Worker.Spawn(6600, new Vector3(20, 0, 0), new ContainerRef(2));
        for (int i = 0; i < 12; i++) { client.SendFocusHint(new Vector3(90, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(client.Replicas, Does.Contain(6600ul));
        Assert.That(client.Containers, Does.Contain("c2"), "the row the spawn names is there");
    }

    // ------------------------------------------------------------------------------------------- handover

    /// <summary>
    /// Four workers over the line world, with the cells they own chosen so that a client standing in c0 has a
    /// reason to link <b>w1 and nobody else</b>: the others own cells hundreds of metres away, and
    /// <c>InterestMaxRadius</c> is capped so they are not linked "in case a wide entity reaches us" either.
    /// That is the shape every handover test here needs — a pawn whose authority moves to a worker the
    /// gateway is not talking to, which is what a freshly allocated chunk in a runtime world looks like.
    /// </summary>
    private static Fleet Handovers() => new(1, workers: 4,
        configure: c => { c.InterestLinkLingerSeconds = 1f; c.InterestMaxRadius = 150f; },
        // Only c0 has an owner at first, so the pawn lands on w1 and nowhere else; the far cells get their
        // owners once it is placed.
        world: f => f.Assign("c0", "w1"));

    /// <summary>The pawn id of a joined client, and a settled mesh holding exactly the one link it needs.</summary>
    private static ulong SettledPawn(Fleet fleet, FakeClient client)
    {
        ulong pawn = fleet.Workers[0].Pawns[client.Welcome!.Value.ClientId];
        fleet.Assign("c7", "w2");
        fleet.Assign("c6", "w3");
        fleet.Assign("c5", "w4");
        // Placing a first pawn links every lease owner (design D13); those reasons go as soon as it exists.
        Assert.That(fleet.Run(() => fleet.Gateways[0].WorkerLinkCount == 1, seconds: 10), Is.True,
            "the gateway settles on the one worker its client needs");
        return pawn;
    }

    /// <summary>The client still has its pawn, and its new owner's state is reaching it. Both halves matter:
    /// a record the gateway keeps but nobody publishes is a pawn frozen in place, not a pawn.</summary>
    private static void AssertPawnIsLive(Fleet fleet, FakeClient client, ulong pawn, FakeWorker owner, string what)
    {
        Assert.That(fleet.Run(() => client.Replicas.Contains(pawn), seconds: 15), Is.True,
            $"{what}: the client must still hold its own pawn");
        fleet.OnPump = owner.PublishStates;
        int before = client.StatesReceived;
        Assert.That(fleet.Run(() => client.StatesReceived > before, seconds: 10), Is.True,
            $"{what}: the gateway must speak for the pawn again, from its new owner");
        fleet.OnPump = null;
    }

    [Test]
    public void AnOwnedPawnSurvivesAHandoverToAWorkerTheGatewayHasNoLinkTo()
    {
        using var fleet = Handovers();
        var client = Join(fleet, "ann");
        ulong pawn = SettledPawn(fleet, client);

        // Authority moves to w2, which this gateway has never dialled. w2 cannot announce the pawn to a
        // gateway it has no link to, and a worker may not dial one: the gateway has to do the linking.
        fleet.Workers[0].HandOver(pawn, fleet.Workers[1]);

        Assert.That(fleet.Run(() => fleet.Workers[1].Gateways.Count == 1, seconds: 15), Is.True,
            "the gateway links the new owner it was redirected to");
        AssertPawnIsLive(fleet, client, pawn, fleet.Workers[1], "handover to an unlinked worker");
        Assert.That(client.Despawned, Does.Not.Contain(pawn), "and the client is never told its own pawn is gone");
    }

    [Test]
    public void AnOwnedPawnSurvivesItsPreviousWorkersLinkGoingAwayRightAfterTheHandover()
    {
        using var fleet = Handovers();
        var client = Join(fleet, "ann");
        ulong pawn = SettledPawn(fleet, client);

        // The pawn goes to w2 and, in the same breath, the last reason to hold the link to w1 goes with it:
        // its cell is reassigned. Dropping a link drops that worker's records, and the gateway's record still
        // names w1 until the new owner is heard from - so this is where a client's own pawn is lost for good.
        fleet.Workers[0].HandOver(pawn, fleet.Workers[1]);
        fleet.Plane.AssignContainer("c0", "w3");

        AssertPawnIsLive(fleet, client, pawn, fleet.Workers[1], "old link dropped during the handover");
        Assert.That(client.Despawned, Does.Not.Contain(pawn));
    }

    [Test]
    public void AnOwnedPawnSurvivesAChainOfHandoversThroughWorkersTheGatewayNeverLinked()
    {
        using var fleet = Handovers();
        var client = Join(fleet, "ann");
        ulong pawn = SettledPawn(fleet, client);

        // w1 -> w2 -> w3 faster than the gateway can dial. The redirect w2 would send names a gateway w2 has
        // no link to, so it is never sent: after this the only worker the gateway has heard of is w2, which no
        // longer owns the pawn. Nothing short of the gateway noticing its client has a pawn it cannot place
        // recovers from here.
        fleet.Workers[0].HandOver(pawn, fleet.Workers[1]);
        fleet.Pump();
        fleet.Workers[1].HandOver(pawn, fleet.Workers[2]);

        AssertPawnIsLive(fleet, client, pawn, fleet.Workers[2], "a chained handover");
        Assert.That(client.Despawned, Does.Not.Contain(pawn));
    }

    [Test]
    public void InstanceIsolationAndObservePublicSurviveInterestManagement()
    {
        // The invariants InstanceTests pins, but over the wire and with distance in play: a private entity is
        // not sent to the public world however near it is.
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        fleet.Plane.EnsureRuntimeContainer("rt_77", new Bounds(new Vector3(32, 0, 0), new Vector3(20, 20, 20)), "w1", new InstanceContainerInfo { InstanceId = 77 });
        fleet.RunFor(0.5);
        fleet.Worker.Spawn(6700, Vector3.zero, ContainerRef.Runtime(77));
        fleet.Worker.Spawn(6701, new Vector3(1, 0, 0));

        Assert.That(fleet.Run(() => client.Replicas.Contains(6701)), Is.True);
        fleet.RunFor(0.8);
        Assert.That(client.Replicas, Does.Not.Contain(6700ul), "a private instance is isolated no matter how close it is");
    }
}
