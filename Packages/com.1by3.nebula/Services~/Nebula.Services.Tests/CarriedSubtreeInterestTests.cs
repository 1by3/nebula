using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Design D3 over the wire, with the carrier as the <b>only</b> thing that is moved: a ship crossing a region
/// edge has to take its passengers with it in the publication as well as in the index. The regression these pin
/// is the one where only the carrier's subscriber-mask transition was sent — a gateway subscribing the
/// destination got a ship whose passengers never arrived, and one subscribing the origin kept them for ever.
/// <para>
/// Nothing here ever moves a passenger by hand. Everything a client is told about one must follow from the
/// carrier moving, from the passenger boarding it, or from the passenger getting off.
/// </para>
/// <para>
/// These are <b>fixture-level</b> end-to-end tests: they drive <see cref="FakeWorker"/>, which shares the real
/// interest core (<c>InterestIndex</c>, <c>CarriedTransition</c>, <c>RegionPublisher</c>, <c>HandoverScope</c>)
/// with the worker but not its Unity halves. The production implementations of the handoff bookkeeping and of
/// carrier-destruction evacuation are regression-tested directly by <c>Nebula.Tests.WorkerHandoverTests</c> and
/// <c>Nebula.Tests.HandoverScopeTests</c> in the package's EditMode suite.
/// </para>
/// </summary>
[TestFixture]
public class CarriedSubtreeInterestTests
{
    private string _directory = "";

    /// <summary>The same 64 m line world <see cref="InterestTests"/> uses, so distances read the same way.</summary>
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
        _directory = Path.Combine(Path.GetTempPath(), "nebula-carried-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        LoadWorld(8);
    }

    [TearDown]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private static Fleet Line(int gateways = 1) =>
        new(gateways, world: f => { for (int i = 0; i < 8; i++) f.Assign("c" + i, "w1"); });

    /// <summary>Join a client whose pawn stands in <paramref name="cell"/>, so its set covers that end of the line.</summary>
    private static FakeClient Join(Fleet fleet, string name, ushort cell = 0, int gateway = 0)
    {
        fleet.Worker.PawnContainer = new ContainerRef(cell);
        fleet.Worker.PawnPlacement = _ => Vector3.zero;
        var client = fleet.Connect(gateway, name);
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined), Is.True, $"{name} should get a pawn");
        return client;
    }

    /// <summary>A ship with a crate aboard and a cat in the crate: two levels of carrier over one entity.</summary>
    private static void SpawnShip(Fleet fleet, ushort cell, Vector3 at)
    {
        fleet.Worker.Spawn(7000, at, new ContainerRef(cell));
        fleet.Worker.Spawn(7001, Vector3.zero, ContainerRef.Dynamic(7000));
        fleet.Worker.Spawn(7002, Vector3.zero, ContainerRef.Dynamic(7001));
    }

    /// <summary>Sail the ship to another cell of the line. The passengers are not touched, and must not have to be.</summary>
    private static void SailTo(Fleet fleet, ushort cell, Vector3 at)
    {
        fleet.Worker.Entities[7000].Container = new ContainerRef(cell);
        fleet.Worker.Move(7000, at);
    }

    [Test]
    public void MovingOnlyTheCarrierCarriesItsPassengersIntoTheDestinationGatewaysSet()
    {
        using var fleet = Line(gateways: 2);
        var origin = Join(fleet, "ann", cell: 0);
        var destination = Join(fleet, "bob", cell: 6, gateway: 1);
        SpawnShip(fleet, 0, new Vector3(5, 0, 0));
        Assert.That(fleet.Run(() => origin.Replicas.Contains(7000) && origin.Replicas.Contains(7002)), Is.True,
            "the ship and everything in it start next to ann");
        Assert.That(destination.Replicas, Does.Not.Contain(7000ul));

        SailTo(fleet, 6, new Vector3(5, 0, 0));

        Assert.That(fleet.Run(() => destination.Replicas.Contains(7000) && destination.Replicas.Contains(7001) &&
                                    destination.Replicas.Contains(7002), seconds: 10), Is.True,
            "the destination gateway is sent the passengers too, not just the ship they ride in");
        Assert.That(destination.Spawned.IndexOf(7000), Is.LessThan(destination.Spawned.IndexOf(7001)),
            "carrier before its contents, so the container resolves");
        Assert.That(destination.Spawned.IndexOf(7001), Is.LessThan(destination.Spawned.IndexOf(7002)),
            "to any depth");
        Assert.That(destination.DuplicateSpawns, Is.Zero);
    }

    [Test]
    public void MovingOnlyTheCarrierMakesTheOriginGatewayForgetItsPassengersToo()
    {
        using var fleet = Line(gateways: 2);
        var origin = Join(fleet, "ann", cell: 0);
        Join(fleet, "bob", cell: 6, gateway: 1);
        SpawnShip(fleet, 0, new Vector3(5, 0, 0));
        Assert.That(fleet.Run(() => origin.Replicas.Contains(7002)), Is.True);

        SailTo(fleet, 6, new Vector3(5, 0, 0));

        Assert.That(fleet.Run(() => !origin.Replicas.Contains(7000) && !origin.Replicas.Contains(7001) &&
                                    !origin.Replicas.Contains(7002), seconds: 15), Is.True,
            "a passenger of a ship that sailed away is as gone as the ship: no stale replica is left behind");
        Assert.That(origin.Despawned.IndexOf(7002), Is.LessThan(origin.Despawned.IndexOf(7000)),
            "contents before their carrier on the way out");
        Assert.That(origin.OrphanUpdates, Is.Zero, "and nothing arrives for a replica it no longer holds");
    }

    [Test]
    public void BoardingAFarShipTakesThePassengerOutOfTheSetAndBoardingANearOneBringsItIn()
    {
        using var fleet = Line();
        var client = Join(fleet, "ann", cell: 0);
        fleet.Worker.Spawn(7100, new Vector3(0, 0, 0), new ContainerRef(6)); // a ship four cells away
        fleet.Worker.Spawn(7101, new Vector3(5, 0, 0), new ContainerRef(0)); // a crate at the client's feet
        Assert.That(fleet.Run(() => client.Replicas.Contains(7101)), Is.True);
        Assert.That(client.Replicas, Does.Not.Contain(7100ul), "the far ship is out of reach");

        // Boarding is the only thing that happens: the crate's own position is never touched again.
        fleet.Worker.Board(7101, 7100);
        Assert.That(fleet.Run(() => !client.Replicas.Contains(7101), seconds: 15), Is.True,
            "a crate loaded onto a far ship is bucketed with that ship, so it leaves the client's set");

        fleet.Worker.Spawn(7102, new Vector3(5, 0, 0), new ContainerRef(0)); // a ship at the client's feet
        fleet.Worker.Board(7101, 7102);
        Assert.That(fleet.Run(() => client.Replicas.Contains(7101), seconds: 10), Is.True,
            "and loading it onto a near one brings it back, with no move of its own");
    }

    [Test]
    public void ANestedCarrierBoardsAndDisembarksWithEverythingRidingInIt()
    {
        using var fleet = Line();
        var client = Join(fleet, "ann", cell: 0);
        fleet.Worker.Spawn(7200, new Vector3(0, 0, 0), new ContainerRef(6));  // the far ship
        fleet.Worker.Spawn(7201, new Vector3(5, 0, 0), new ContainerRef(0));  // a crate near the client
        fleet.Worker.Spawn(7202, Vector3.zero, ContainerRef.Dynamic(7201));   // riding in the crate
        Assert.That(fleet.Run(() => client.Replicas.Contains(7201) && client.Replicas.Contains(7202)), Is.True);

        // The crate boards the far ship. Its own passenger is never touched, and must go with it.
        fleet.Worker.Board(7201, 7200);
        Assert.That(fleet.Run(() => !client.Replicas.Contains(7201) && !client.Replicas.Contains(7202), seconds: 15), Is.True,
            "a carrier that boards another carrier takes its own subtree along");

        // And back off it, into the cell the client stands in.
        fleet.Worker.Disembark(7201, new ContainerRef(0), new Vector3(5, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(7201) && client.Replicas.Contains(7202), seconds: 10), Is.True,
            "disembarking republishes the whole subtree where it was put down");
        Assert.That(client.Spawned.LastIndexOf(7201), Is.LessThan(client.Spawned.LastIndexOf(7202)),
            "carrier before its contents on the way back in");
        Assert.That(client.DuplicateSpawns, Is.Zero);
    }

    [Test]
    public void DisembarkingIntoAFarCellLeavesTheShipBehindInTheSet()
    {
        using var fleet = Line();
        var client = Join(fleet, "ann", cell: 0);
        SpawnShip(fleet, 0, new Vector3(5, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(7000) && client.Replicas.Contains(7001)), Is.True);

        // The crate (with the cat in it) is put ashore four cells away while the ship stays where it is.
        fleet.Worker.Disembark(7001, new ContainerRef(6), new Vector3(0, 0, 0));
        Assert.That(fleet.Run(() => !client.Replicas.Contains(7001) && !client.Replicas.Contains(7002), seconds: 15), Is.True,
            "what got off the ship is where it was put down, and that is out of reach");
        Assert.That(client.Replicas, Does.Contain(7000ul), "while the ship itself never left");
    }

    // ----------------------------------------------------------------- a passenger is published as its carrier is

    /// <summary>
    /// How many spawns (or forgets) of one entity this worker has sent one gateway link. The client-side sets
    /// cannot answer design D70: an always-relevant crate that reaches a gateway on the other side of the world
    /// and is then filtered out per client costs that gateway the ingest, the cache row and the evaluation, and
    /// looks exactly like a crate that was never sent. These read what actually went down the link.
    /// </summary>
    private static int SpawnsOf(FakeWorker worker, string gateway, ulong netId) =>
        worker.SpawnLog.Count(e => e.Gateway == gateway && e.NetId == netId);

    private static int ForgetsOf(FakeWorker worker, string gateway, ulong netId) =>
        worker.ForgetLog.Count(e => e.Gateway == gateway && e.NetId == netId);

    [Test]
    public void AnAlwaysRelevantPassengerLeavesTheGatewaysItsShipIsNowhereNear()
    {
        using var fleet = Line(gateways: 2);
        var near = Join(fleet, "ann", cell: 0);
        var far = Join(fleet, "bob", cell: 6, gateway: 1);
        fleet.Worker.Spawn(7300, new Vector3(5, 0, 0), new ContainerRef(0));                        // an ordinary ship
        fleet.Worker.Spawn(7301, new Vector3(5, 0, 0), new ContainerRef(0), alwaysRelevant: true);  // a beacon crate
        Assert.That(fleet.Run(() => far.Replicas.Contains(7301)), Is.True, "always relevant: everyone has it to start with");
        Assert.That(SpawnsOf(fleet.Worker, "gw2", 7301), Is.GreaterThan(0));

        fleet.Worker.Board(7301, 7300);

        Assert.That(fleet.Run(() => !far.Replicas.Contains(7301), seconds: 15), Is.True,
            "aboard an ordinary ship it is bucketed with the ship, and the far gateway's clients lose it");
        Assert.That(ForgetsOf(fleet.Worker, "gw2", 7301), Is.GreaterThan(0),
            "the far gateway is told to forget it, so it drops the record rather than caching it for ever");
        int sent = SpawnsOf(fleet.Worker, "gw2", 7301);
        fleet.RunFor(1.0);
        Assert.That(SpawnsOf(fleet.Worker, "gw2", 7301), Is.EqualTo(sent),
            "and nothing re-sends it there because of the crate's own prefab settings");
        Assert.That(near.Replicas, Does.Contain(7301ul), "while the gateway the ship is at keeps it");

        // And the other direction: put ashore, it is its own always-relevant self again.
        fleet.Worker.Disembark(7301, new ContainerRef(0), new Vector3(5, 0, 0));
        Assert.That(fleet.Run(() => far.Replicas.Contains(7301), seconds: 10), Is.True,
            "off the ship it reaches every gateway again");
    }

    [Test]
    public void AWidePassengerIsBucketedWithTheOrdinaryShipItBoards()
    {
        using var fleet = Line(gateways: 2);
        Join(fleet, "ann", cell: 0);
        var far = Join(fleet, "bob", cell: 6, gateway: 1);
        fleet.Worker.Spawn(7400, new Vector3(5, 0, 0), new ContainerRef(0));                          // the ship
        fleet.Worker.Spawn(7401, new Vector3(5, 0, 0), new ContainerRef(0), relevanceRadius: 600f);   // a searchlight
        Assert.That(fleet.Run(() => far.Replicas.Contains(7401)), Is.True,
            "600 m of reach covers the whole line, so both gateways hear about it");

        fleet.Worker.Board(7401, 7400);

        Assert.That(fleet.Run(() => !far.Replicas.Contains(7401), seconds: 15), Is.True,
            "bolted to an ordinary ship it reaches exactly as far as the ship does");
        Assert.That(ForgetsOf(fleet.Worker, "gw2", 7401), Is.GreaterThan(0));
        int sent = SpawnsOf(fleet.Worker, "gw2", 7401);
        fleet.RunFor(1.0);
        Assert.That(SpawnsOf(fleet.Worker, "gw2", 7401), Is.EqualTo(sent),
            "and the wide evaluation does not keep re-matching it against far foci");
    }

    [Test]
    public void ANestedAlwaysRelevantPassengerInheritsTheOutermostCarrier()
    {
        using var fleet = Line(gateways: 2);
        Join(fleet, "ann", cell: 0);
        var far = Join(fleet, "bob", cell: 6, gateway: 1);
        fleet.Worker.Spawn(7500, new Vector3(5, 0, 0), new ContainerRef(0));                                    // ship
        fleet.Worker.Spawn(7501, Vector3.zero, ContainerRef.Dynamic(7500));                                     // crate aboard
        fleet.Worker.Spawn(7502, Vector3.zero, ContainerRef.Dynamic(7501), alwaysRelevant: true);               // beacon cat

        fleet.RunFor(1.5);
        Assert.That(SpawnsOf(fleet.Worker, "gw2", 7502), Is.Zero,
            "two levels down, the ship's placement is still the one that counts: the far gateway never sees it");
        Assert.That(far.Replicas, Does.Not.Contain(7502ul));

        // The ship sails to the far end: now the whole subtree arrives there, carrier first.
        fleet.Worker.Entities[7500].Container = new ContainerRef(6);
        fleet.Worker.Move(7500, new Vector3(5, 0, 0));
        Assert.That(fleet.Run(() => far.Replicas.Contains(7502), seconds: 10), Is.True);
        Assert.That(far.Spawned.IndexOf(7501), Is.LessThan(far.Spawned.IndexOf(7502)),
            "and it arrives after the crate it is sitting in");
    }

    // ----------------------------------------------------------------- the gateway's own cache follows the root

    /// <summary>The pawn of a client, as the worker handed it out.</summary>
    private static ulong PawnOf(Fleet fleet, FakeClient client) => fleet.Worker.Pawns[client.Welcome!.Value.ClientId];

    /// <summary>
    /// Walk a client's pawn to another cell of the line. Nothing is despawned and no worker link is dropped:
    /// the gateway simply stops subscribing where the pawn was, which is the one path on which a worker sends
    /// nothing at all and the gateway has to drop its own records (design §5).
    /// </summary>
    private static void WalkPawnTo(Fleet fleet, FakeClient client, ushort cell, Vector3 at)
    {
        ulong pawn = PawnOf(fleet, client);
        fleet.Worker.Entities[pawn].Container = new ContainerRef(cell);
        fleet.Worker.Move(pawn, at);
    }

    [Test]
    public void TheGatewayEvictsAnAlwaysRelevantPassengerWithTheShipItRidesIn()
    {
        using var fleet = Line();
        // World state has to flow, or the gateway never learns that the pawn walked or the crate got
        // off: a spawn is only re-sent when a subscriber mask changes, and neither of those changes one.
        fleet.OnPump = fleet.Worker.PublishStates;
        var client = Join(fleet, "ann", cell: 0);
        fleet.Worker.Spawn(7600, new Vector3(5, 0, 0), new ContainerRef(0));                      // an ordinary ship
        fleet.Worker.Spawn(7601, Vector3.zero, ContainerRef.Dynamic(7600), alwaysRelevant: true); // a beacon crate aboard
        var gateway = fleet.Gateways[0];
        Assert.That(fleet.Run(() => gateway.IsEntityCached(7601)), Is.True, "the gateway caches what its client is near");
        Assert.That(gateway.TryGetCachedPlacement(7601, out var placement, out ulong region), Is.True);
        Assert.That(placement, Is.EqualTo(InterestPlacement.Region),
            "an always-relevant crate in an ordinary ship is a region record in the ship's region, not a global one");
        Assert.That(gateway.TryGetCachedPlacement(7600, out _, out ulong shipRegion), Is.True);
        Assert.That(region, Is.EqualTo(shipRegion), "the ship's region, to the id");

        // The client walks to the far end. The worker sends nothing on unsubscribe, so the only thing that can
        // drop these records is the gateway's own eviction sweep.
        WalkPawnTo(fleet, client, 6, new Vector3(5, 0, 0));

        Assert.That(fleet.Run(() => !gateway.IsEntityCached(7600) && !gateway.IsEntityCached(7601), seconds: 20), Is.True,
            "the ship's region is nobody's any more, so the ship and the crate riding in it are both evicted");
    }

    [Test]
    public void TheGatewayEvictsAWidePassengerWithTheShipItRidesIn()
    {
        using var fleet = Line();
        // World state has to flow, or the gateway never learns that the pawn walked or the crate got
        // off: a spawn is only re-sent when a subscriber mask changes, and neither of those changes one.
        fleet.OnPump = fleet.Worker.PublishStates;
        var client = Join(fleet, "ann", cell: 0);
        fleet.Worker.Spawn(7700, new Vector3(5, 0, 0), new ContainerRef(0));                        // the ship
        fleet.Worker.Spawn(7701, Vector3.zero, ContainerRef.Dynamic(7700), relevanceRadius: 600f);  // a searchlight bolted on
        var gateway = fleet.Gateways[0];
        Assert.That(fleet.Run(() => gateway.IsEntityCached(7701)), Is.True);
        Assert.That(gateway.TryGetCachedPlacement(7701, out var placement, out _), Is.True);
        Assert.That(placement, Is.EqualTo(InterestPlacement.Region),
            "600 m of its own reach does not survive being bolted to an ordinary ship");

        WalkPawnTo(fleet, client, 6, new Vector3(5, 0, 0));

        Assert.That(fleet.Run(() => !gateway.IsEntityCached(7700) && !gateway.IsEntityCached(7701), seconds: 20), Is.True,
            "a wide passenger is evicted with its carrier rather than cached for the rest of the session");
    }

    [Test]
    public void APassengerPutAshoreGetsItsOwnPlacementBackOnTheGateway()
    {
        using var fleet = Line();
        // World state has to flow, or the gateway never learns that the pawn walked or the crate got
        // off: a spawn is only re-sent when a subscriber mask changes, and neither of those changes one.
        fleet.OnPump = fleet.Worker.PublishStates;
        var client = Join(fleet, "ann", cell: 0);
        fleet.Worker.Spawn(7800, new Vector3(5, 0, 0), new ContainerRef(0));                      // the ship
        fleet.Worker.Spawn(7801, Vector3.zero, ContainerRef.Dynamic(7800), alwaysRelevant: true); // the beacon crate
        var gateway = fleet.Gateways[0];
        Assert.That(fleet.Run(() => gateway.IsEntityCached(7801)), Is.True);

        // Ashore in the cell the client is standing in: it is its own always-relevant self again.
        fleet.Worker.Disembark(7801, new ContainerRef(0), new Vector3(5, 0, 0));
        Assert.That(fleet.Run(() => gateway.TryGetCachedPlacement(7801, out var p, out _) && p == InterestPlacement.Global,
            seconds: 10), Is.True, "off the ship the crate's own placement is restored on the gateway too");

        // And now that it really is global, walking away does not evict it: it is in everyone's set.
        WalkPawnTo(fleet, client, 6, new Vector3(5, 0, 0));
        Assert.That(fleet.Run(() => !gateway.IsEntityCached(7800), seconds: 20), Is.True, "the ship is out of reach");
        Assert.That(gateway.IsEntityCached(7801), Is.True, "while the crate ashore is always relevant on its own account");
        Assert.That(client.Replicas, Does.Contain(7801ul));
    }

    [Test]
    public void ACarrierThatArrivesAfterItsPassengersReseatsTheirCachedRecords()
    {
        using var fleet = Line();
        // World state has to flow, or the gateway never learns that the pawn walked or the crate got
        // off: a spawn is only re-sent when a subscriber mask changes, and neither of those changes one.
        fleet.OnPump = fleet.Worker.PublishStates;
        var client = Join(fleet, "ann", cell: 0);
        var gateway = fleet.Gateways[0];
        // The crate names a ship that does not exist yet, so the gateway caches it on its own placement first.
        fleet.Worker.Spawn(7901, Vector3.zero, ContainerRef.Dynamic(7900), alwaysRelevant: true);
        Assert.That(fleet.Run(() => gateway.IsEntityCached(7901)), Is.True);

        fleet.Worker.Spawn(7900, new Vector3(5, 0, 0), new ContainerRef(0));

        Assert.That(fleet.Run(() => gateway.TryGetCachedPlacement(7901, out var p, out _) && p == InterestPlacement.Region,
            seconds: 10), Is.True, "the ship arriving reseats the passenger the gateway was already holding");
        Assert.That(fleet.Run(() => client.Replicas.Contains(7901), seconds: 10), Is.True,
            "and it is offered to the clients of the region the ship is in");

        WalkPawnTo(fleet, client, 6, new Vector3(5, 0, 0));
        Assert.That(fleet.Run(() => !gateway.IsEntityCached(7901), seconds: 20), Is.True,
            "so it is evicted with the ship, not kept because of its own prefab settings");
    }

    // ----------------------------------------------------------------- the carrier arrives after its passengers

    /// <summary>
    /// These assert on the <b>gateway's</b> cache and on what the worker actually put down each link, not on a
    /// client's replica set — the distinction design D70 is about. A passenger whose carrier does not exist has
    /// no resolvable container, so no client may observe it in any case; what has to stop is the gateway on the
    /// other side of the world being sent it, caching it and evaluating it for ever.
    /// </summary>
    [Test]
    public void AnAlwaysRelevantPassengerLeavesTheFarGatewayWhenItsCarrierFinallyArrives()
    {
        using var fleet = Line(gateways: 2);
        var near = Join(fleet, "ann", cell: 0);
        Join(fleet, "bob", cell: 6, gateway: 1);
        NebulaGateway nearGateway = fleet.Gateways[0], farGateway = fleet.Gateways[1];
        // The crate names a ship that does not exist yet, so until it does the crate is what its own prefab
        // says: always relevant, and therefore sent to every gateway in the mesh.
        fleet.Worker.Spawn(8001, Vector3.zero, ContainerRef.Dynamic(8000), alwaysRelevant: true);
        Assert.That(fleet.Run(() => farGateway.IsEntityCached(8001) && nearGateway.IsEntityCached(8001), seconds: 10),
            Is.True, "with no carrier to sit in it is global on its own account, so every gateway is sent it");
        Assert.That(SpawnsOf(fleet.Worker, "gw2", 8001), Is.GreaterThan(0));

        // The ship turns up at ann's end of the line. Nothing about the crate is touched.
        fleet.Worker.Spawn(8000, new Vector3(5, 0, 0), new ContainerRef(0));

        Assert.That(fleet.Run(() => !farGateway.IsEntityCached(8001), seconds: 15), Is.True,
            "seated in the ship it is bucketed with the ship, and bob's gateway is told to drop it");
        Assert.That(ForgetsOf(fleet.Worker, "gw2", 8001), Is.GreaterThan(0),
            "the far gateway is told to forget it rather than caching it for the rest of the session");
        int sent = SpawnsOf(fleet.Worker, "gw2", 8001);
        fleet.RunFor(1.0);
        Assert.That(SpawnsOf(fleet.Worker, "gw2", 8001), Is.EqualTo(sent), "and nothing re-sends it there");
        Assert.That(nearGateway.IsEntityCached(8001), Is.True, "while the gateway the ship arrived at keeps it");
        Assert.That(nearGateway.TryGetCachedPlacement(8001, out var placement, out _), Is.True);
        Assert.That(placement, Is.EqualTo(InterestPlacement.Region), "as a region record in the ship's region");
        // And now that its container resolves, ann's client can be shown it.
        Assert.That(fleet.Run(() => near.Replicas.Contains(8001), seconds: 10), Is.True);
        Assert.That(near.Spawned.IndexOf(8000), Is.LessThan(near.Spawned.IndexOf(8001)),
            "carrier before its contents, so the container resolves");
    }

    [Test]
    public void AWholePendingSubtreeIsRepublishedWhenTheCarrierArrivesCarrierFirst()
    {
        using var fleet = Line(gateways: 2);
        var near = Join(fleet, "ann", cell: 0);
        Join(fleet, "bob", cell: 6, gateway: 1);
        NebulaGateway nearGateway = fleet.Gateways[0], farGateway = fleet.Gateways[1];
        // Two levels of passenger, both indexed before the ship they name: a beacon crate and a beacon cat in it.
        fleet.Worker.Spawn(8101, Vector3.zero, ContainerRef.Dynamic(8100), alwaysRelevant: true);
        fleet.Worker.Spawn(8102, Vector3.zero, ContainerRef.Dynamic(8101), alwaysRelevant: true);
        Assert.That(fleet.Run(() => farGateway.IsEntityCached(8101) && farGateway.IsEntityCached(8102), seconds: 10),
            Is.True);

        fleet.Worker.Spawn(8100, new Vector3(5, 0, 0), new ContainerRef(0));

        Assert.That(fleet.Run(() => !farGateway.IsEntityCached(8101) && !farGateway.IsEntityCached(8102), seconds: 15),
            Is.True, "the whole pending subtree is reseated with the ship, not only the crate linked to it directly");
        var forgotten = fleet.Worker.ForgetLog.Where(e => e.Gateway == "gw2").Select(e => e.NetId).ToList();
        Assert.That(forgotten.IndexOf(8102), Is.LessThan(forgotten.IndexOf(8101)),
            "contents before their carrier on the way out");
        Assert.That(fleet.Run(() => nearGateway.IsEntityCached(8100) && near.Replicas.Contains(8101) &&
                                    near.Replicas.Contains(8102), seconds: 10), Is.True);
        Assert.That(near.Spawned.LastIndexOf(8100), Is.LessThan(near.Spawned.LastIndexOf(8101)),
            "and carrier before its contents at the gateway that gains them");
        Assert.That(near.Spawned.LastIndexOf(8101), Is.LessThan(near.Spawned.LastIndexOf(8102)), "to any depth");
        Assert.That(near.OrphanUpdates, Is.Zero);
    }

    [Test]
    public void ACarrierArrivingDoesNotDisturbAPassengerAnExplicitSubscriberFollowsByName()
    {
        using var fleet = Line(gateways: 2);
        Join(fleet, "ann", cell: 0);
        Join(fleet, "bob", cell: 6, gateway: 1);
        var farGateway = fleet.Gateways[1];
        fleet.Worker.Spawn(8201, Vector3.zero, ContainerRef.Dynamic(8200), alwaysRelevant: true);
        Assert.That(fleet.Run(() => farGateway.IsEntityCached(8201), seconds: 10), Is.True);
        // bob's gateway names the crate: a party member, a quest target. It keeps it wherever it ends up.
        farGateway.InterestPolicy = new FollowPolicy(8201);
        Assert.That(fleet.Run(() => fleet.Worker.ReceiverOf("gw2") is { } r && r.Entities.Any(id => id == 8201),
            seconds: 10), Is.True, "the gateway asks the worker for it by name");

        fleet.Worker.Spawn(8200, new Vector3(5, 0, 0), new ContainerRef(0));
        fleet.RunFor(1.5);

        Assert.That(ForgetsOf(fleet.Worker, "gw2", 8201), Is.Zero,
            "a sticky subscriber is never told to forget the entity it asked for by name");
        Assert.That(farGateway.IsEntityCached(8201), Is.True);
    }

    /// <summary>A policy that adds one entity to every client's set by name, so it is sticky on that gateway.</summary>
    private sealed class FollowPolicy : IInterestPolicy
    {
        private readonly ulong _netId;
        public FollowPolicy(ulong netId) { _netId = netId; }
        public void Collect(in InterestClient client, InterestQuery query)
        {
            DefaultInterestPolicy.Instance.Collect(client, query);
            query.AddEntity(_netId);
        }
        public bool Authorize(in InterestClient client, in InterestEntity entity) => true;
    }

    // ----------------------------------------------------------------- the carrier is taken away

    [Test]
    public void AnAlwaysRelevantPassengerIsGlobalAgainWhenItsShipIsDespawned()
    {
        using var fleet = Line(gateways: 2);
        var near = Join(fleet, "ann", cell: 0);
        Join(fleet, "bob", cell: 6, gateway: 1);
        NebulaGateway nearGateway = fleet.Gateways[0], farGateway = fleet.Gateways[1];
        fleet.Worker.Spawn(8300, new Vector3(5, 0, 0), new ContainerRef(0));                      // an ordinary ship
        fleet.Worker.Spawn(8301, Vector3.zero, ContainerRef.Dynamic(8300), alwaysRelevant: true); // a beacon crate aboard
        Assert.That(fleet.Run(() => near.Replicas.Contains(8301), seconds: 10), Is.True);
        fleet.RunFor(1.0);
        Assert.That(SpawnsOf(fleet.Worker, "gw2", 8301), Is.Zero, "aboard an ordinary ship it is the ship's business only");
        Assert.That(farGateway.IsEntityCached(8301), Is.False);

        // The ship is destroyed. Nothing touches the crate, which survives it.
        fleet.Worker.Despawn(8300);

        Assert.That(fleet.Run(() => farGateway.IsEntityCached(8301), seconds: 15), Is.True,
            "orphaned, the crate is always relevant on its own account again and reaches every gateway");
        Assert.That(SpawnsOf(fleet.Worker, "gw2", 8301), Is.GreaterThan(0),
            "which is a spawn nothing else on the mesh would ever have sent");
        Assert.That(farGateway.TryGetCachedPlacement(8301, out var placement, out _), Is.True);
        Assert.That(placement, Is.EqualTo(InterestPlacement.Global), "on its own placement again");
        Assert.That(nearGateway.IsEntityCached(8301), Is.True, "and it never leaves the gateway that already had it");
        Assert.That(ForgetsOf(fleet.Worker, "gw1", 8301), Is.Zero, "which is told nothing, because nothing changed for it");
        // The two gateways hear of it over separate links, so the far one's spawn says nothing about the near one's despawn.
        Assert.That(fleet.Run(() => !nearGateway.IsEntityCached(8300), seconds: 10), Is.True, "while the ship itself is despawned");
    }

    [Test]
    public void AWidePassengerLeavesTheGatewayThatOnlyKnewItThroughTheDespawnedShip()
    {
        using var fleet = Line(gateways: 2);
        var near = Join(fleet, "ann", cell: 0);
        Join(fleet, "bob", cell: 6, gateway: 1);
        NebulaGateway nearGateway = fleet.Gateways[0], farGateway = fleet.Gateways[1];
        fleet.Worker.Spawn(8400, new Vector3(5, 0, 0), new ContainerRef(0)); // the ship, at ann's end
        // A searchlight bolted to it whose own frame position is at bob's end of the line: while it rides in the
        // ship none of that matters, and the moment the ship is gone its own reach is all there is.
        fleet.Worker.Spawn(8401, new Vector3(400, 0, 0), ContainerRef.Dynamic(8400), relevanceRadius: 130f);
        Assert.That(fleet.Run(() => near.Replicas.Contains(8401), seconds: 10), Is.True, "bucketed with the ship, at ann's end");
        fleet.RunFor(1.0);
        Assert.That(SpawnsOf(fleet.Worker, "gw2", 8401), Is.Zero);

        fleet.Worker.Despawn(8400);

        Assert.That(fleet.Run(() => !nearGateway.IsEntityCached(8401) && farGateway.IsEntityCached(8401), seconds: 15),
            Is.True, "its own reach is around its own position now, which is nowhere near ann");
        Assert.That(ForgetsOf(fleet.Worker, "gw1", 8401), Is.GreaterThan(0),
            "the gateway that only knew it through the ship is told to forget it");
        Assert.That(near.Replicas, Does.Not.Contain(8401ul), "and ann's client is not left holding a stale replica");
    }

    [Test]
    public void NothingAboutAnOrphanedPassengerReachesAGatewayBeforeItsSpawn()
    {
        using var fleet = Line(gateways: 2);
        var near = Join(fleet, "ann", cell: 0);
        var far = Join(fleet, "bob", cell: 6, gateway: 1);
        var farGateway = fleet.Gateways[1];
        fleet.Worker.Spawn(8500, new Vector3(5, 0, 0), new ContainerRef(0));
        fleet.Worker.Spawn(8501, Vector3.zero, ContainerRef.Dynamic(8500), alwaysRelevant: true);
        fleet.Worker.Spawn(8502, Vector3.zero, ContainerRef.Dynamic(8501), alwaysRelevant: true);
        // Everything the worker sends about a passenger, every pump, all the way through the despawn.
        fleet.OnPump = () =>
        {
            fleet.Worker.PublishStates();
            fleet.Worker.SendVars(8501, new byte[] { 1 });
            fleet.Worker.SendSyncState(8502, 0, new byte[] { 2 });
            fleet.Worker.SendRpc(8501);
        };
        fleet.RunFor(1.5);
        Assert.That(farGateway.IsEntityCached(8501), Is.False, "aboard the ship, bob's gateway has never heard of it");
        Assert.That(far.OrphanUpdates, Is.Zero);

        fleet.Worker.Despawn(8500);

        Assert.That(fleet.Run(() => farGateway.IsEntityCached(8501) && farGateway.IsEntityCached(8502), seconds: 15),
            Is.True, "the whole surviving subtree is republished on the placement it got back");
        var sent = fleet.Worker.SpawnLog.Where(e => e.Gateway == "gw2").Select(e => e.NetId).ToList();
        Assert.That(sent.IndexOf(8501), Is.LessThan(sent.IndexOf(8502)),
            "the surviving crate is sent before the cat riding in it");
        Assert.That(far.OrphanUpdates, Is.Zero,
            "and no state, netvar, sync state or RPC reached bob's client before a spawn that made it legible");
        // ann's client is not asserted on: it held the passengers while the ship was there and loses them to a
        // reliable despawn while unreliable state for them is still in flight, which is the ordinary crossing
        // of two channels and not something publication order can decide.
        Assert.That(far.LastError, Is.Empty);
    }

    /// <summary>
    /// A carrier handoff is not a carrier destruction. The real worker hands the carrier over first and then
    /// recursively hands over its contents on the same ordered stream. Removing the carrier from the old worker's
    /// interest index must not publish the passengers on their own placement in between: they are still aboard and
    /// are about to follow it to the new owner.
    /// </summary>
    [Test]
    public void ASubtreeHandoffDoesNotPublishAPassengerAsATemporaryOrphan()
    {
        using var fleet = new Fleet(2, workers: 2, world: f =>
        {
            for (int i = 0; i < 8; i++) f.Assign("c" + i, "w1");
        });
        var near = Join(fleet, "ann", cell: 0);
        Join(fleet, "bob", cell: 6, gateway: 1);
        var source = fleet.Workers[0];
        var target = fleet.Workers[1];
        var farGateway = fleet.Gateways[1];

        source.Spawn(8700, new Vector3(5, 0, 0), new ContainerRef(0));
        source.Spawn(8701, Vector3.zero, ContainerRef.Dynamic(8700), alwaysRelevant: true);
        Assert.That(fleet.Run(() => near.Replicas.Contains(8701), seconds: 10), Is.True);
        fleet.RunFor(0.5);
        Assert.That(farGateway.IsEntityCached(8701), Is.False,
            "aboard the ship, the passenger is scoped to the ship's region rather than globally");

        // Make the destination worker relevant at the ship's end while the far gateway stays linked to the old
        // worker for c6. Calling the two existing handoff halves in this order mirrors TransferAuthority: the
        // carrier leaves the old index, then its passenger follows immediately.
        fleet.Assign("c0", "w2");
        Assert.That(fleet.Run(() => target.Gateways.Values.Contains("gw1"), seconds: 10), Is.True,
            "the destination worker is linked where the ship is");
        source.HandOver(8700, target);
        source.HandOver(8701, target);

        fleet.RunFor(2.0);
        Assert.That(farGateway.IsEntityCached(8701), Is.False,
            "moving the whole subtree must not expose the passenger's own AlwaysRelevant placement between transfers");
    }

    /// <summary>
    /// The same handoff, with the two passenger kinds whose own placement reaches further than the ship does: a
    /// searchlight wide enough to cover the line, and a beacon two levels down. Neither may surface while the
    /// subtree is between owners, and both have to end up published by the new owner.
    /// </summary>
    [Test]
    public void ASubtreeHandoffKeepsAWideAndANestedPassengerScopedToTheCarrier()
    {
        using var fleet = new Fleet(2, workers: 2, world: f =>
        {
            for (int i = 0; i < 8; i++) f.Assign("c" + i, "w1");
        });
        var near = Join(fleet, "ann", cell: 0);
        Join(fleet, "bob", cell: 6, gateway: 1);
        var source = fleet.Workers[0];
        var target = fleet.Workers[1];
        var farGateway = fleet.Gateways[1];

        source.Spawn(8710, new Vector3(5, 0, 0), new ContainerRef(0));                                  // the ship
        source.Spawn(8711, Vector3.zero, ContainerRef.Dynamic(8710), relevanceRadius: 600f);            // a searchlight on it
        source.Spawn(8712, Vector3.zero, ContainerRef.Dynamic(8711), alwaysRelevant: true);             // a beacon in that
        Assert.That(fleet.Run(() => near.Replicas.Contains(8712), seconds: 10), Is.True);
        fleet.RunFor(0.5);
        Assert.That(farGateway.IsEntityCached(8711), Is.False, "600 m of its own reach does not survive being bolted on");
        Assert.That(farGateway.IsEntityCached(8712), Is.False, "nor does being always relevant two levels down");

        fleet.Assign("c0", "w2");
        Assert.That(fleet.Run(() => target.Gateways.Values.Contains("gw1"), seconds: 10), Is.True);
        // Carrier first, then its contents outwards in, exactly as TransferAuthority recurses.
        source.HandOver(8710, target);
        source.HandOver(8711, target);
        source.HandOver(8712, target);

        fleet.RunFor(2.0);
        Assert.That(farGateway.IsEntityCached(8711), Is.False, "a wide passenger does not surface between owners");
        Assert.That(farGateway.IsEntityCached(8712), Is.False, "and neither does a nested always-relevant one");
        Assert.That(SpawnsOf(source, "gw2", 8711) + SpawnsOf(source, "gw2", 8712), Is.Zero,
            "nothing about either was ever put down the far gateway's link");
        Assert.That(fleet.Run(() => near.Replicas.Contains(8711) && near.Replicas.Contains(8712), seconds: 10), Is.True,
            "while the new owner publishes the whole subtree where the ship actually is");
    }

    /// <summary>
    /// Restoring an orphan's effective placement is only useful when clients can observe the entity afterwards.
    /// The republished spawn must not retain a dynamic-container reference to the carrier that was just removed.
    /// </summary>
    [Test]
    public void AGlobalPassengerThatSurvivesItsCarrierIsObservableByNewlyEligibleClients()
    {
        using var fleet = Line(gateways: 2);
        var near = Join(fleet, "ann", cell: 0);
        var far = Join(fleet, "bob", cell: 6, gateway: 1);
        var farGateway = fleet.Gateways[1];
        fleet.Worker.Spawn(8800, new Vector3(5, 0, 0), new ContainerRef(0));
        fleet.Worker.Spawn(8801, Vector3.zero, ContainerRef.Dynamic(8800), alwaysRelevant: true);
        Assert.That(fleet.Run(() => near.Replicas.Contains(8801), seconds: 10), Is.True);
        Assert.That(far.Replicas, Does.Not.Contain(8801ul));

        fleet.Worker.Despawn(8800);

        Assert.That(fleet.Run(() => farGateway.IsEntityCached(8801), seconds: 10), Is.True,
            "the restored global placement republishes the survivor to the far gateway");
        Assert.That(fleet.Run(() => far.Replicas.Contains(8801), seconds: 2), Is.True,
            "a newly eligible client must be able to resolve and observe the surviving passenger");
    }

    /// <summary>
    /// The same, one level deeper: only the crate is put down where the ship was, the cat keeps naming the crate,
    /// and every spawn a newly eligible client is sent therefore names a container it has already been given.
    /// </summary>
    [Test]
    public void ANestedSurvivingSubtreeStaysResolvableAfterItsCarrierIsDestroyed()
    {
        using var fleet = Line(gateways: 2);
        var near = Join(fleet, "ann", cell: 0);
        var far = Join(fleet, "bob", cell: 6, gateway: 1);
        fleet.Worker.Spawn(8810, new Vector3(5, 0, 0), new ContainerRef(0));                      // the ship
        fleet.Worker.Spawn(8811, Vector3.zero, ContainerRef.Dynamic(8810), alwaysRelevant: true); // a beacon crate
        fleet.Worker.Spawn(8812, Vector3.zero, ContainerRef.Dynamic(8811), alwaysRelevant: true); // a beacon cat in it
        Assert.That(fleet.Run(() => near.Replicas.Contains(8812), seconds: 10), Is.True);
        Assert.That(far.Replicas, Does.Not.Contain(8811ul));

        fleet.Worker.Despawn(8810);

        Assert.That(fleet.Run(() => far.Replicas.Contains(8811) && far.Replicas.Contains(8812), seconds: 10), Is.True,
            "the whole surviving subtree can be observed, not merely cached");
        Assert.That(far.Spawned.IndexOf(8811), Is.LessThan(far.Spawned.IndexOf(8812)),
            "carrier before its contents, so the cat's container resolves when it arrives");
        Assert.That(far.OrphanUpdates, Is.Zero);
    }

    // ----------------------------------------------------------------- depth is bounded by the tree, not a constant

    /// <summary>
    /// Design D71 end to end: a chain far deeper than the eight levels the publication path used to stop at.
    /// A truncated snapshot leaves the bottom of the ship out of the gateway's subscription and out of every
    /// client's set, and nothing downstream can tell.
    /// </summary>
    [Test]
    public void ASubscriptionSnapshotHoldsAChainDeeperThanEightCarrierFirst()
    {
        const int depth = 12;
        using var fleet = Line();
        // World state has to flow, or the gateway never learns that the pawn walked to the other end.
        fleet.OnPump = fleet.Worker.PublishStates;
        var client = Join(fleet, "ann", cell: 0);
        // The whole chain at the far end of the line, out of the client's reach to start with.
        fleet.Worker.Spawn(8600, new Vector3(0, 0, 0), new ContainerRef(6));
        for (ulong i = 1; i < depth; i++) fleet.Worker.Spawn(8600 + i, Vector3.zero, ContainerRef.Dynamic(8600 + i - 1));
        fleet.RunFor(1.0);
        Assert.That(client.Replicas, Does.Not.Contain(8600ul));

        // Walking there is what makes the gateway subscribe the region and the worker send it the snapshot.
        WalkPawnTo(fleet, client, 6, new Vector3(0, 0, 0));

        Assert.That(fleet.Run(() => client.Replicas.Contains(8600 + depth - 1), seconds: 20), Is.True,
            "the deepest link of the chain arrives, twelve carriers down");
        for (ulong i = 0; i < depth; i++)
            Assert.That(client.Replicas, Does.Contain(8600 + i), $"#{8600 + i} is in the set");
        var sent = fleet.Worker.SpawnLog.Where(e => e.Gateway == "gw1" && e.NetId >= 8600 && e.NetId < 8600 + depth)
            .Select(e => e.NetId).ToList();
        for (ulong i = 1; i < depth; i++)
            Assert.That(sent.IndexOf(8600 + i - 1), Is.LessThan(sent.IndexOf(8600 + i)),
                $"the worker sent #{8600 + i - 1} before the #{8600 + i} riding in it");
        for (ulong i = 1; i < depth; i++)
            Assert.That(client.Spawned.IndexOf(8600 + i - 1), Is.LessThan(client.Spawned.IndexOf(8600 + i)),
                $"and so did the gateway, so the client can resolve every container it is named");
        Assert.That(client.OrphanUpdates, Is.Zero);
    }

    [Test]
    public void OnlyTheRootCarrierMovesBetweenGatewayRegions()
    {
        using var fleet = Line(gateways: 2);
        Join(fleet, "ann", cell: 0);
        Join(fleet, "bob", cell: 6, gateway: 1);
        SpawnShip(fleet, 0, new Vector3(5, 0, 0));
        fleet.RunFor(1.0);
        int spawns = fleet.Worker.SpawnLog.Count(e => e.NetId == 7001 || e.NetId == 7002);
        int forgets = fleet.Worker.ForgetLog.Count(e => e.NetId == 7001 || e.NetId == 7002);

        // A passenger's own position moving the length of the world publishes nothing: it is bucketed with the
        // ship, and the ship has not moved.
        fleet.Worker.Move(7001, new Vector3(400, 0, 0));
        fleet.Worker.Move(7002, new Vector3(400, 0, 0));
        fleet.RunFor(1.0);

        Assert.That(fleet.Worker.SpawnLog.Count(e => e.NetId == 7001 || e.NetId == 7002), Is.EqualTo(spawns),
            "a passenger cannot enter a gateway's set on its own");
        Assert.That(fleet.Worker.ForgetLog.Count(e => e.NetId == 7001 || e.NetId == 7002), Is.EqualTo(forgets),
            "nor leave one");
    }
}
