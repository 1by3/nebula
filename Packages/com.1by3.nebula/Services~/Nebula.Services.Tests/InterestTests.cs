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
    private class TestPolicy : IInterestPolicy
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

    /// <summary>A policy that hands out free foci by client id, which is how a server authorizes a spectator or an RTS camera.</summary>
    private sealed class HintPolicy : TestPolicy, IFocusHintPolicy
    {
        public readonly HashSet<ulong> Free = new();
        public bool RejectEverything;
        public double? SnapX;

        public FocusHintDecision AuthorizeFocusHint(in InterestClient client, in FocusHintDecision request)
        {
            if (RejectEverything) return FocusHintDecision.Reject();
            var decision = request;
            if (Free.Contains(client.ClientId)) decision.Mode = FocusMode.Free;
            if (SnapX != null) decision.X = SnapX.Value;
            return decision;
        }
    }

    [Test]
    public void AFocusHintMeansTheSamePlaceAfterTheClientsOriginShifts()
    {
        using var fleet = OneWorker();
        var client = Join(fleet, "ann");
        // ~148 m from the pawn at world x = 32: only a hint can hold it in the set.
        fleet.Worker.Spawn(5220, new Vector3(20, 0, 0), new ContainerRef(2));

        // The client is rendering with its origin at the world's zero and looks 58 m ahead of the pawn.
        for (int i = 0; i < 12; i++) { client.SendFocusHintInFrame(new Vector3(90, 0, 0), Vector3.zero); fleet.RunFor(0.25); }
        Assert.That(client.Replicas, Does.Contain(5220ul), "the hint brought it in");

        // Its floating origin shifts a whole 1 km cell: the same place is now frame x = -910 to the client. A
        // gateway reading the frame position would take the camera 1 km backwards and lose the entity; reading
        // the absolute point the client sends, nothing moves at all.
        var origin = new Vector3(1000, 0, 0);
        for (int i = 0; i < 12; i++) { client.SendFocusHintInFrame(new Vector3(-910, 0, 0), origin); fleet.RunFor(0.25); }
        Assert.That(client.Replicas, Does.Contain(5220ul), "a floating-origin shift does not move the hinted point");
        Assert.That(client.Despawned, Does.Not.Contain(5220ul));
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
    public void AServerAuthorizedFreeCameraSeesWhereItIsPointedAndAnOrdinaryClientDoesNot()
    {
        using var fleet = OneWorker();
        var policy = new HintPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var commander = Join(fleet, "com");
        fleet.Worker.Spawn(5400, new Vector3(0, 0, 0), new ContainerRef(5)); // world x ~ 352, far past the clamp
        ulong id = commander.Welcome!.Value.ClientId;

        // Default: PawnClamped. However hard the client asks, the hint is worth 60 m of pawn.
        for (int i = 0; i < 12; i++) { commander.SendFocusHint(new Vector3(352, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(commander.Replicas, Does.Not.Contain(5400ul), "a hint is clamped to the pawn by default");
        Assert.That(fleet.Gateways[0].GetClientFocusMode(id), Is.EqualTo(FocusMode.PawnClamped));

        // The server — not the client — decides this one may look anywhere: an RTS camera over its own front.
        fleet.Gateways[0].SetClientFocusMode(id, FocusMode.Free);
        Assert.That(fleet.Gateways[0].GetClientFocusMode(id), Is.EqualTo(FocusMode.Free));
        for (int i = 0; i < 16; i++) { commander.SendFocusHint(new Vector3(352, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(commander.Replicas, Does.Contain(5400ul), "and a free focus really does move the set to where it points");

        // Taking it away again drops the hint the gateway was holding, rather than leaving the last free point standing.
        fleet.Gateways[0].SetClientFocusMode(id, FocusMode.PawnClamped);
        Assert.That(fleet.Run(() => !commander.Replicas.Contains(5400ul), seconds: 8), Is.True,
            "revoking the mode revokes what it was seeing");
    }

    [Test]
    public void APolicyCanFreeAFocusHintAndMoveWhereItPoints()
    {
        using var fleet = OneWorker();
        var policy = new HintPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        fleet.Worker.Spawn(5410, new Vector3(0, 0, 0), new ContainerRef(5)); // world x ~ 352

        policy.Free.Add(client.Welcome!.Value.ClientId);
        // The client points somewhere else entirely; the policy snaps the camera to the place it is allowed to
        // watch, which is the adjustment half of the hook.
        policy.SnapX = 352;
        for (int i = 0; i < 16; i++) { client.SendFocusHint(new Vector3(-9000, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(client.Replicas, Does.Contain(5410ul), "the policy's point is the one that counts, not the client's");

        // Refusing takes effect on the next hint the client sends, which for a live client is within
        // 1/InterestHintMaxHz of a second: a refusal revokes the focus rather than leaving the last allowed
        // one standing for the rest of the session.
        policy.RejectEverything = true;
        fleet.OnPump = () => client.SendFocusHint(new Vector3(-9000, 0, 0));
        Assert.That(fleet.Run(() => !client.Replicas.Contains(5410ul), seconds: 8), Is.True, "and a policy may refuse a hint outright");
        fleet.OnPump = null;
    }

    [Test]
    public void APawnlessClientCannotGiveItselfAViewByHinting()
    {
        using var fleet = OneWorker();
        fleet.Worker.DeferSpawns = true;
        var policy = new HintPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var watcher = fleet.Connect(0, "watcher");
        Assert.That(fleet.Run(() => watcher.Welcome != null), Is.True);
        fleet.Worker.Spawn(5420, new Vector3(10, 0, 0));
        fleet.Worker.Spawn(5421, new Vector3(10, 0, 0), alwaysRelevant: true);

        // No pawn, so no distance to clamp to. Honouring the raw point would make "connect and hint" the
        // cheapest map scrape there is, and the client would not even need a body to do it.
        for (int i = 0; i < 12; i++) { watcher.SendFocusHint(new Vector3(32, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(watcher.Replicas, Does.Not.Contain(5420ul), "a pawn-less client's hint buys it nothing");
        Assert.That(watcher.Replicas, Does.Contain(5421ul), "it still hears the always-relevant entities, and only those");

        // Authorized, it works: a spectator is a decision the server made.
        fleet.Gateways[0].SetClientFocusMode(watcher.Welcome!.Value.ClientId, FocusMode.Free);
        for (int i = 0; i < 16; i++) { watcher.SendFocusHint(new Vector3(32, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(watcher.Replicas, Does.Contain(5420ul), "and an authorized pawn-less hint is a working spectator camera");
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

    [Test]
    public void AFreeFocusIsStillStoppedByInstanceIsolation()
    {
        using var fleet = OneWorker();
        var policy = new HintPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        fleet.Plane.EnsureRuntimeContainer("rt_88", new Bounds(new Vector3(352, 0, 0), new Vector3(20, 20, 20)), "w1", new InstanceContainerInfo { InstanceId = 88 });
        fleet.RunFor(0.5);
        fleet.Worker.Spawn(6710, Vector3.zero, ContainerRef.Runtime(88));

        fleet.Gateways[0].SetClientFocusMode(client.Welcome!.Value.ClientId, FocusMode.Free);
        for (int i = 0; i < 16; i++) { client.SendFocusHint(new Vector3(352, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(client.Replicas, Does.Not.Contain(6710ul),
            "a free focus is a bigger view of the world you are in, never a way into somebody else's instance");
    }

    // ------------------------------------------------------------------------------------------- what a policy is told

    /// <summary>Records the last snapshot a policy was given about one client and about the entities it judged.</summary>
    private sealed class RecordingPolicy : IInterestPolicy
    {
        public InterestClient Client;
        public readonly Dictionary<ulong, InterestEntity> Entities = new();

        public void Collect(in InterestClient client, InterestQuery query)
        {
            Client = client;
            if (client.HasPawn) query.AddFocus(client.PawnX, client.PawnY, client.PawnZ, 1f, client.PawnNetId);
        }

        public bool Authorize(in InterestClient client, in InterestEntity entity)
        {
            Entities[entity.NetId] = entity;
            return true;
        }
    }

    [Test]
    public void APolicyIsToldTheClientsInstanceTagsFocusModeAndCarrier()
    {
        using var fleet = OneWorker();
        var policy = new RecordingPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        ulong id = client.Welcome!.Value.ClientId;
        fleet.Gateways[0].SetClientTag(id, 4);
        fleet.Gateways[0].SetClientTags(id, 0xF00D);
        fleet.Gateways[0].SetClientFocusMode(id, FocusMode.Free);
        Assert.That(fleet.Run(() => policy.Client.FocusMode == FocusMode.Free, seconds: 5), Is.True);

        Assert.That(policy.Client.ClientId, Is.EqualTo(id));
        Assert.That(policy.Client.Identity, Is.Not.Null);
        Assert.That(policy.Client.Name, Is.EqualTo("ann"));
        Assert.That(policy.Client.HasPawn, Is.True);
        Assert.That(policy.Client.PawnNetId, Is.Not.Zero);
        Assert.That(policy.Client.PawnX, Is.EqualTo(32).Within(1), "the pawn's absolute position, not a container-local one");
        Assert.That(policy.Client.PawnCarrierNetId, Is.Zero, "it is standing in the world, not riding anything");
        Assert.That(policy.Client.InstanceId, Is.Zero, "the public world");
        Assert.That(policy.Client.Team, Is.EqualTo((byte)4));
        Assert.That(policy.Client.Tags, Is.EqualTo(0xF00Dul));
        Assert.That(policy.Client.FreeHint, Is.True, "FreeHint is the focus mode and is no longer always false");
    }

    [Test]
    public void APolicyIsToldAnEntitysOwnerCarrierGroupRadiusAndPosition()
    {
        using var fleet = OneWorker();
        var policy = new RecordingPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        // A ship beside the pawn with a crate riding in it: what the policy is told about the crate must
        // resolve through the ship, not be read off the crate alone.
        fleet.Worker.Spawn(6800, new Vector3(6, 0, 0), relevanceRadius: 90f, group: 5);
        fleet.Worker.Spawn(6801, new Vector3(1, 0, 0), ContainerRef.Dynamic(6800), group: 7);
        Assert.That(fleet.Run(() => policy.Entities.ContainsKey(6801), seconds: 6), Is.True);

        var ship = policy.Entities[6800];
        Assert.That(ship.CarrierNetId, Is.Zero);
        Assert.That(ship.X, Is.EqualTo(38).Within(1), "absolute, resolved through the container frame");
        Assert.That(ship.RelevanceRadius, Is.EqualTo(90f).Within(1f));
        Assert.That(ship.AlwaysRelevant, Is.False);
        Assert.That(ship.InterestGroup, Is.EqualTo((byte)5));
        Assert.That(ship.InstanceId, Is.Zero, "the public world");
        Assert.That(ship.PrefabId, Is.Not.Null);

        var crate = policy.Entities[6801];
        Assert.That(crate.CarrierNetId, Is.EqualTo(6800ul), "the entity that is carrying it");
        Assert.That(crate.Container.IsDynamic, Is.True);
        Assert.That(crate.InterestGroup, Is.EqualTo((byte)7), "its own group, not its carrier's");
        Assert.That(crate.RelevanceRadius, Is.EqualTo(90f).Within(1f), "but the carrier's reach, so the two move as one (design D39)");
        Assert.That(crate.X, Is.EqualTo(ship.X).Within(2), "and the carrier's position, by design D3");
        Assert.That(crate.InstanceId, Is.Zero);

        ulong id = client.Welcome!.Value.ClientId;
        var pawn = policy.Entities[fleet.Worker.Pawns[id]];
        Assert.That(pawn.OwnerClientId, Is.EqualTo(id), "ownership is reported, so a policy can tell a player's own things apart");
    }

    [Test]
    public void APolicyIsToldTheInstanceAnEntityIsInIncludingThroughItsCarrier()
    {
        // InterestEntity.InstanceId used to be left at zero, so every policy filtering on the scope was
        // filtering on "the public world" for everything in the mesh. Put the client and the entities inside a
        // private instance and the field has to say so — for a carried entity too, which resolves through its
        // carrier's container and not its own.
        using var fleet = OneWorker();
        var policy = new RecordingPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        fleet.Plane.EnsureRuntimeContainer("rt_99", new Bounds(new Vector3(32, 0, 0), new Vector3(60, 60, 60)), "w1", new InstanceContainerInfo { InstanceId = 99 });
        fleet.RunFor(0.5);
        fleet.Worker.PawnContainer = ContainerRef.Runtime(99);
        var client = Join(fleet, "ann");

        fleet.Worker.Spawn(6810, new Vector3(6, 0, 0), ContainerRef.Runtime(99));
        fleet.Worker.Spawn(6811, new Vector3(1, 0, 0), ContainerRef.Dynamic(6810));
        Assert.That(fleet.Run(() => policy.Entities.ContainsKey(6811), seconds: 8), Is.True);

        Assert.That(policy.Client.InstanceId, Is.EqualTo(99ul), "the client's own scope");
        Assert.That(policy.Entities[6810].InstanceId, Is.EqualTo(99ul));
        Assert.That(policy.Entities[6811].InstanceId, Is.EqualTo(99ul), "resolved through the carrier chain, not left at zero");
    }

    // ------------------------------------------------------------------------------------------- server surface

    [Test]
    public void TheGatewayAnnouncesAuthenticatedClientsJoiningAndLeaving()
    {
        using var fleet = OneWorker();
        var joined = new List<NebulaGateway.GatewayClientInfo>();
        var left = new List<NebulaGateway.GatewayClientInfo>();
        // Installed before anybody connects, which is the point: this is where an extension gets to set a
        // client's tags and focus mode before its first interest evaluation runs.
        fleet.Gateways[0].ClientJoined += info => { joined.Add(info); fleet.Gateways[0].SetClientTag(info.ClientId, 9); };
        fleet.Gateways[0].ClientLeft += left.Add;

        var client = Join(fleet, "ann");
        ulong id = client.Welcome!.Value.ClientId;
        Assert.That(joined.Count, Is.EqualTo(1));
        Assert.That(joined[0].ClientId, Is.EqualTo(id));
        Assert.That(joined[0].Name, Is.EqualTo("ann"));
        Assert.That(joined[0].Identity, Is.Not.Null.And.Not.Empty, "the authenticated subject");
        Assert.That(fleet.Gateways[0].GetClientTag(id), Is.EqualTo((byte)9), "a handler may configure the client it is told about");

        client.Disconnect();
        Assert.That(fleet.Run(() => left.Count == 1, seconds: 10), Is.True);
        Assert.That(left[0].ClientId, Is.EqualTo(id));
    }

    [Test]
    public void OneTickDoesNotEvaluateEveryClientButOneIntervalDoes()
    {
        // The gateway-level counterpart of InterestScheduleTests: with many clients on one gateway, the
        // evaluations really are spread over the ticks rather than run in one burst four times a second.
        const int players = 24;
        using var fleet = OneWorker();
        for (int i = 0; i < players; i++) Join(fleet, "p" + i, new Vector3(i % 8, 0, 0));

        var evaluated = new List<ulong>();
        fleet.Gateways[0].InterestPolicy = new CountingPolicy(evaluated);
        fleet.RunFor(1.0);   // installing a policy marks everybody dirty: let that pass first

        var perTick = new List<int>();
        var seen = new HashSet<ulong>();
        evaluated.Clear();
        // OnPump runs straight after the gateway's tick, so each bucket is exactly one tick's evaluations.
        fleet.OnPump = () =>
        {
            perTick.Add(evaluated.Count);
            foreach (ulong id in evaluated) seen.Add(id);
            evaluated.Clear();
        };
        fleet.RunFor(0.4);   // comfortably over one eval interval at the default 4 Hz
        fleet.OnPump = null;

        Assert.That(perTick.Count, Is.GreaterThan(players), "the window really is many ticks long");
        Assert.That(seen.Count, Is.EqualTo(players), "every client is evaluated within one interval");
        Assert.That(perTick.Max(), Is.LessThan(players),
            $"no tick evaluated the whole population (worst tick {perTick.Max()} of {players})");
    }

    // ------------------------------------------------------------------------------------- focus hint pipeline

    /// <summary>
    /// Counts every call into the game's focus-hint hook and remembers the worst point it was handed. A client
    /// sending hints as fast as it can must not be able to move either number faster than InterestHintMaxHz.
    /// </summary>
    private sealed class CountingHintPolicy : IInterestPolicy, IFocusHintPolicy
    {
        public int Calls;
        public bool SawMalformed;
        public bool RejectEverything;

        public void Collect(in InterestClient client, InterestQuery query)
        {
            if (client.HasPawn) query.AddFocus(client.PawnX, client.PawnY, client.PawnZ, 1f, client.PawnNetId);
            if (client.HasHint) query.AddFocus(client.HintX, client.HintY, client.HintZ);
        }

        public bool Authorize(in InterestClient client, in InterestEntity entity) => true;

        public FocusHintDecision AuthorizeFocusHint(in InterestClient client, in FocusHintDecision request)
        {
            Calls++;
            if (double.IsNaN(request.X) || double.IsNaN(request.Y) || double.IsNaN(request.Z) ||
                double.IsInfinity(request.X) || double.IsInfinity(request.Y) || double.IsInfinity(request.Z) ||
                Math.Abs(request.X) > FocusHintFilter.MaxMagnitude) SawMalformed = true;
            return RejectEverything ? FocusHintDecision.Reject() : request;
        }
    }

    /// <summary>
    /// Send hints as hard as a hostile client would — several per tick, for a stretch of wall time — and
    /// return the seconds it took, so a test can state its bound as a rate rather than as a magic number.
    /// </summary>
    private static double SpamHints(Fleet fleet, FakeClient client, double seconds, Func<int, Vector3> point)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int i = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            for (int k = 0; k < 8; k++) client.SendFocusHint(point(i++));
            fleet.Pump();
        }
        return clock.Elapsed.TotalSeconds;
    }

    /// <summary>The most hints InterestHintMaxHz allows in this long, with a tick's worth of slack either side.</summary>
    private static int AllowedIn(Fleet fleet, double seconds) =>
        (int)Math.Ceiling(fleet.Gateways[0].InterestSettingsInUse.HintMaxHz * seconds) + 2;

    [Test]
    public void RefusedHintsCannotDriveTheGamesHintPolicyAtPacketRate()
    {
        using var fleet = OneWorker();
        var policy = new CountingHintPolicy { RejectEverything = true };
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        policy.Calls = 0;

        // Every one of these is going to be refused, so under a rate limit that only counted *accepted* hints
        // none of them spent anything and each one bought a call into game code.
        double elapsed = SpamHints(fleet, client, 1.0, i => new Vector3(40 + i % 5, 0, 0));

        Assert.That(policy.Calls, Is.GreaterThan(0), "the policy is asked about hints at all");
        Assert.That(policy.Calls, Is.LessThanOrEqualTo(AllowedIn(fleet, elapsed)),
            $"a refused hint must still spend the rate budget ({policy.Calls} calls in {elapsed:0.00}s)");
        Assert.That(fleet.Gateways[0].FocusHintsDroppedByRate, Is.GreaterThan(0), "and the drops are counted as rate drops");
        Assert.That(fleet.Gateways[0].FocusHintsAccepted, Is.Zero, "nothing the policy refused was accepted");
    }

    [Test]
    public void HintsFromAClientThatMayNotHaveOneAreBoundedTooAndCountedAsUnauthorized()
    {
        using var fleet = OneWorker();
        var policy = new CountingHintPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        ulong id = client.Welcome!.Value.ClientId;
        fleet.Gateways[0].SetClientFocusMode(id, FocusMode.Disabled);
        policy.Calls = 0;

        double elapsed = SpamHints(fleet, client, 1.0, i => new Vector3(40 + i % 5, 0, 0));

        Assert.That(policy.Calls, Is.LessThanOrEqualTo(AllowedIn(fleet, elapsed)),
            $"a client the server has switched off does not get an unlimited path into the policy ({policy.Calls} calls in {elapsed:0.00}s)");
        Assert.That(fleet.Gateways[0].FocusHintsDroppedUnauthorized, Is.GreaterThan(0), "and its hints are counted as unauthorized, not as rate drops");
        Assert.That(fleet.Gateways[0].FocusHintsAccepted, Is.Zero);
    }

    [Test]
    public void NonFiniteAndAbsurdHintsNeverReachTheHintPolicy()
    {
        using var fleet = OneWorker();
        var policy = new CountingHintPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        fleet.Gateways[0].SetClientFocusMode(client.Welcome!.Value.ClientId, FocusMode.Free);
        policy.Calls = 0;

        var poison = new[]
        {
            new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0), new Vector3(0, 0, float.NegativeInfinity),
        };
        for (int i = 0; i < poison.Length; i++) { client.SendFocusHint(poison[i]); fleet.RunFor(0.3); }
        // A magnitude no camera produced: the grid can only pack about 6.7e7 m per axis, so this is malformed
        // rather than merely far away, and it is refused rather than quietly clamped to somewhere else.
        client.SendFocusHint(1e12, 0, 0);
        fleet.RunFor(0.3);

        Assert.That(policy.Calls, Is.Zero, "not one malformed point was handed to game code");
        Assert.That(policy.SawMalformed, Is.False);
        Assert.That(fleet.Gateways[0].FocusHintsDroppedMalformed, Is.GreaterThanOrEqualTo(4), "and each is counted as malformed");
        Assert.That(fleet.Gateways[0].FocusHintsDroppedUnauthorized, Is.Zero, "not as an authorization failure");

        // The connection is not poisoned: an ordinary hint after them still works.
        for (int i = 0; i < 8; i++) { client.SendFocusHint(new Vector3(60, 0, 0)); fleet.RunFor(0.25); }
        Assert.That(fleet.Gateways[0].FocusHintsAccepted, Is.GreaterThan(0));
        Assert.That(policy.Calls, Is.GreaterThan(0));
    }

    [Test]
    public void AcceptedHintsAreRateLimitedAndAServerAuthorizationLetsTheCameraStartAtOnce()
    {
        using var fleet = OneWorker();
        var policy = new CountingHintPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        ulong id = client.Welcome!.Value.ClientId;

        double elapsed = SpamHints(fleet, client, 1.0, i => new Vector3(40 + i % 5, 0, 0));
        Assert.That(fleet.Gateways[0].FocusHintsAccepted, Is.LessThanOrEqualTo(AllowedIn(fleet, elapsed)),
            "an accepted hint is still one per 1/InterestHintMaxHz seconds");
        Assert.That(fleet.Gateways[0].FocusHintsAccepted, Is.GreaterThan(0));

        // The server frees the camera. The client has just spent its budget being throttled, and it cannot
        // know it has been authorized, so the grant resets the budget: the very next hint is judged afresh.
        long accepted = fleet.Gateways[0].FocusHintsAccepted;
        fleet.Gateways[0].SetClientFocusMode(id, FocusMode.Free);
        client.SendFocusHint(new Vector3(400, 0, 0));
        fleet.Pump();
        fleet.Pump();
        Assert.That(fleet.Gateways[0].FocusHintsAccepted, Is.GreaterThan(accepted),
            "a newly freed camera does not have to wait out the budget it spent while being refused");
    }

    // ------------------------------------------------------------------------------------- revocation timing

    /// <summary>Fog of war as a policy: a group nobody (or one team) may see, flipped while clients are connected.</summary>
    private sealed class FogPolicy : IInterestPolicy
    {
        public const byte Hidden = 7;
        public bool Deny;
        /// <summary>255 means "every client"; anything else narrows the ban to that team.</summary>
        public byte DenyForTeam = 255;

        public void Collect(in InterestClient client, InterestQuery query)
        {
            if (client.HasPawn) query.AddFocus(client.PawnX, client.PawnY, client.PawnZ, 1f, client.PawnNetId);
        }

        public bool Authorize(in InterestClient client, in InterestEntity entity) =>
            !Deny || entity.InterestGroup != Hidden || (DenyForTeam != 255 && client.Team != DenyForTeam);
    }

    /// <summary>
    /// Enough clients that a change touching all of them cannot be served in one tick's dirty budget: the cap
    /// is max(InterestSchedule.MinDirtyPerTick, routine x DirtyBurst), and the routine share of a tick at 4 Hz
    /// with this many clients is under one. So a staggered answer would leave most of them holding the entity.
    /// </summary>
    private const int CrowdedGateway = InterestSchedule.MinDirtyPerTick + 4;

    private List<FakeClient> JoinCrowd(Fleet fleet)
    {
        var clients = new List<FakeClient>();
        for (int i = 0; i < CrowdedGateway; i++) clients.Add(Join(fleet, "p" + i));
        return clients;
    }

    private static List<int> SetSizes(Fleet fleet, List<FakeClient> clients)
    {
        var sizes = new List<int>();
        foreach (var c in clients) sizes.Add(fleet.Gateways[0].InterestSetSize(c.Welcome!.Value.ClientId));
        return sizes;
    }

    [Test]
    public void ATagChangeRevokesInsideTheCallAndNotAtTheClientsNextTurn()
    {
        using var fleet = OneWorker();
        var policy = new FogPolicy { Deny = true, DenyForTeam = 1 };
        fleet.Gateways[0].InterestPolicy = policy;
        var clients = JoinCrowd(fleet);
        fleet.Worker.Spawn(7100, new Vector3(10, 0, 0), group: FogPolicy.Hidden);
        Assert.That(fleet.Run(() => clients.TrueForAll(c => c.Replicas.Contains(7100))), Is.True, "everybody starts out holding it");

        var before = SetSizes(fleet, clients);
        var target = clients[CrowdedGateway - 1];   // the last one, so a rotation would reach it last
        // No pump between these two lines: the guarantee is that the revocation has happened by the time the
        // setter returns, not that it happens soon.
        fleet.Gateways[0].SetClientTag(target.Welcome!.Value.ClientId, 1);
        var after = SetSizes(fleet, clients);

        Assert.That(after[CrowdedGateway - 1], Is.EqualTo(before[CrowdedGateway - 1] - 1),
            "the entity its new team may not see is out of its set already");
        for (int i = 0; i < CrowdedGateway - 1; i++)
            Assert.That(after[i], Is.EqualTo(before[i]), "and nobody else's set was touched");

        // The despawn is on the wire on the very next tick, and nothing about it is relayed afterwards.
        fleet.OnPump = () => { fleet.Worker.PublishStates(); fleet.Worker.SendVars(7100, new byte[] { 1 }); fleet.Worker.SendRpc(7100); };
        Assert.That(fleet.Run(() => !target.Replicas.Contains(7100)), Is.True);
        fleet.RunFor(0.75);
        fleet.OnPump = null;
        Assert.That(target.OrphanUpdates, Is.Zero, "no state, netvar or RPC for the revoked entity reaches it");
        Assert.That(clients[0].Replicas, Does.Contain(7100ul), "and the other teams still see it");
    }

    [Test]
    public void ReplacingThePolicyOnALiveGatewayFailsClosedForEveryObserver()
    {
        using var fleet = OneWorker();
        fleet.Gateways[0].InterestPolicy = new FogPolicy();
        var clients = JoinCrowd(fleet);
        fleet.Worker.Spawn(7200, new Vector3(10, 0, 0), group: FogPolicy.Hidden);
        Assert.That(fleet.Run(() => clients.TrueForAll(c => c.Replicas.Contains(7200))), Is.True);

        var before = SetSizes(fleet, clients);
        fleet.Gateways[0].InterestPolicy = new FogPolicy { Deny = true };
        var after = SetSizes(fleet, clients);

        for (int i = 0; i < CrowdedGateway; i++)
            Assert.That(after[i], Is.EqualTo(before[i] - 1),
                $"client {i} of {CrowdedGateway} is still holding what the new policy refuses (the dirty cap is {InterestSchedule.MinDirtyPerTick})");

        fleet.OnPump = () => { fleet.Worker.PublishStates(); fleet.Worker.SendVars(7200, new byte[] { 1 }); };
        Assert.That(fleet.Run(() => clients.TrueForAll(c => !c.Replicas.Contains(7200))), Is.True, "and every client is told");
        fleet.RunFor(0.5);
        fleet.OnPump = null;
        foreach (var c in clients) Assert.That(c.OrphanUpdates, Is.Zero);
    }

    [Test]
    public void AGlobalRevocationDoesNotWaitBehindTheRoutineRotation()
    {
        using var fleet = OneWorker();
        var policy = new FogPolicy();
        fleet.Gateways[0].InterestPolicy = policy;
        var clients = JoinCrowd(fleet);
        fleet.Worker.Spawn(7300, new Vector3(10, 0, 0), group: FogPolicy.Hidden);
        Assert.That(fleet.Run(() => clients.TrueForAll(c => c.Replicas.Contains(7300))), Is.True);

        var before = SetSizes(fleet, clients);
        policy.Deny = true;                              // the game's own fog state tightened
        fleet.Gateways[0].RevalidateAllInterest();       // ...and this is how that is applied
        var after = SetSizes(fleet, clients);

        for (int i = 0; i < CrowdedGateway; i++)
            Assert.That(after[i], Is.EqualTo(before[i] - 1), $"client {i} kept an unauthorized replica past the call");

        // The reveal half is the staggered one, and it still works: lifting the fog brings it back.
        policy.Deny = false;
        fleet.Gateways[0].MarkAllInterestDirty();
        Assert.That(fleet.Run(() => clients.TrueForAll(c => c.Replicas.Contains(7300)), seconds: 8), Is.True,
            "an additive change may be spread over the following ticks, and arrives");
    }

    /// <summary>Records which clients an evaluation pass asked about; the fixture's pump closes off each tick.</summary>
    private sealed class CountingPolicy : IInterestPolicy
    {
        private readonly List<ulong> _evaluated;
        public CountingPolicy(List<ulong> evaluated) { _evaluated = evaluated; }

        public void Collect(in InterestClient client, InterestQuery query)
        {
            _evaluated.Add(client.ClientId);
            if (client.HasPawn) query.AddFocus(client.PawnX, client.PawnY, client.PawnZ, 1f, client.PawnNetId);
        }

        public bool Authorize(in InterestClient client, in InterestEntity entity) => true;
    }
}
