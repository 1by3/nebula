using System.Diagnostics;
using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Conformance scenario 44, the far relevance tier (NEB-388, <c>docs/interest-management.md</c> §16), end to end: a real
/// <see cref="NebulaGateway"/> over loopback UDP, a <see cref="FakeWorker"/> publishing with the real
/// <see cref="FarPublisher"/>, and clients that count what arrives. Two ships 50 km apart in one scope see each other's
/// pose at the far rate and nothing else; closing within the normal radius upgrades a far entity to a replica with no
/// "gone" and no despawn in between, and opening out again drops it back to the far tier; a third ship beyond its far
/// radius is never seen; a client's far tier is bounded by <c>InterestFarMaxEntities</c>, nearest first; and a client
/// of protocol 25 is never sent the tier.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceFarRelevanceTests
{
    private string _directory = "";
    private const float FarRadius = 100_000f;
    private const ulong ShipC = 7001, ShipD = 7002, ShipE = 7003;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-far-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        // One container for a whole system: 400 km a side, centred on the origin, so a local position is absolute.
        var path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "c0", Index = 0, Size = new(400_000, 400_000, 400_000), transform = new ContainerFrame { position = new(0, 0, 0) } } },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown] public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Each pump: world state for the near tier, and the far tier at its own rate on the worker's clock.</summary>
    private static void Drive(Fleet fleet, Action? also = null) => fleet.OnPump = () =>
    {
        fleet.Worker.PublishStates();
        fleet.Worker.PublishFar(Clock.Elapsed.TotalSeconds);
        also?.Invoke();
    };

    private static FakeClient Pilot(Fleet fleet, string name, Vector3 at, ushort version = 0)
    {
        fleet.Worker.PawnPlacement = _ => at;
        var client = fleet.Connect(0, name);
        if (version != 0) client.AnnounceVersion = version;
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined && client.Welcome != null && fleet.Worker.Pawns.ContainsKey(Id(client)), seconds: 10), Is.True, $"{name} gets a ship");
        var ship = fleet.Worker.Entities[fleet.Worker.Pawns[Id(client)]];
        ship.FarRadius = FarRadius;
        ship.PrefabId = 42;
        return client;
    }

    private static ulong Id(FakeClient client) => client.Welcome!.Value.ClientId;

    private static ulong ShipOf(Fleet fleet, FakeClient client) => fleet.Worker.Pawns[Id(client)];

    [Test]
    public void TwoShips50KmApartSeeEachOtherAtTheFarRateAndNothingElse()
    {
        using var fleet = new Fleet(gateways: 1);
        var a = Pilot(fleet, "a", new Vector3(0, 0, 0));
        var b = Pilot(fleet, "b", new Vector3(50_000, 0, 0));
        ulong shipA = ShipOf(fleet, a), shipB = ShipOf(fleet, b);
        // A third ship 160 km from a and 167 km from b: beyond its own far radius of both.
        fleet.Worker.Spawn(ShipC, new Vector3(0, 0, 160_000)).FarRadius = FarRadius;
        Drive(fleet, () =>
        {
            // Everything else about b's ship goes out every pump too; none of it may reach a, and none of c's anything.
            fleet.Worker.SendVars(shipB, new byte[] { 1 });
            fleet.Worker.SendRpc(shipB);
            fleet.Worker.SendSyncState(shipB, 0, new byte[] { 2 });
        });

        Assert.That(fleet.Run(() => a.Far.ContainsKey(shipB) && b.Far.ContainsKey(shipA), seconds: 10), Is.True, "each ship is in the other's far tier");
        Assert.That(fleet.Gateways[0].ClientHoldsFar(Id(a), shipB), Is.True);
        Assert.That(a.Far[shipB].X, Is.EqualTo(50_000.0), "the pose is absolute in the scope, in double");
        Assert.That(a.Far[shipB].PrefabId, Is.EqualTo((ushort)42), "with its prefab, for the marker");
        Assert.That(b.Far[shipA].OwnerClientId, Is.EqualTo(Id(a)), "and its owner, for targeting");

        int before = a.FarStatesOf[shipB];
        fleet.RunFor(5.0);
        int updates = a.FarStatesOf[shipB] - before;
        TestContext.WriteLine($"a heard b's ship {updates} times in 5 s; c: far {a.Far.ContainsKey(ShipC)}, replica {a.Replicas.Contains(ShipC)}");
        Assert.That(updates, Is.InRange(4, 7), "one update a second (FarUpdateRate 1)");
        Assert.That(a.Replicas, Does.Not.Contain(shipB), "a far entity is not a replica");
        Assert.That(a.HeardAbout, Does.Not.Contain(shipB), "no state, vars, sync state or RPC of a far entity");
        Assert.That(a.FarWhileReplicated, Is.Zero);
        Assert.That(a.Far.ContainsKey(ShipC) || b.Far.ContainsKey(ShipC) || a.Replicas.Contains(ShipC) || b.Replicas.Contains(ShipC), Is.False,
            "a ship beyond its far radius is not seen at all");
        Assert.That(fleet.Worker.FarSent.ContainsKey((fleet.Gateways[0].GatewayId, ShipC)), Is.False, "and its worker does not even send it to the gateway");
        Assert.That(a.LastError, Is.Empty);
    }

    [Test]
    public void ClosingWithinTheNormalRadiusUpgradesToAReplicaWithNoRespawnAndOpeningOutDropsBack()
    {
        using var fleet = new Fleet(gateways: 1);
        var a = Pilot(fleet, "a", new Vector3(0, 0, 0));
        var b = Pilot(fleet, "b", new Vector3(50_000, 0, 0));
        ulong shipB = ShipOf(fleet, b);
        Drive(fleet);
        Assert.That(fleet.Run(() => a.Far.ContainsKey(shipB), seconds: 10), Is.True);

        // b closes to 60 m from a, inside InterestRadius.
        fleet.Worker.Move(shipB, new Vector3(60, 0, 0));
        Assert.That(fleet.Run(() => a.Replicas.Contains(shipB), seconds: 10), Is.True, "within the normal radius it is a replica");
        Assert.That(a.FarPromoted, Does.Contain(shipB), "the marker turned into the replica");
        Assert.That(a.FarGone, Does.Not.Contain(shipB), "with no far 'gone' first");
        Assert.That(a.Despawned, Does.Not.Contain(shipB), "and no despawn");
        Assert.That(a.Far.ContainsKey(shipB), Is.False);
        Assert.That(fleet.Gateways[0].ClientHoldsFar(Id(a), shipB), Is.False, "the gateway holds it in one tier only");
        int far = a.FarStatesOf[shipB];
        fleet.RunFor(2.5);
        Assert.That(a.FarStatesOf[shipB], Is.EqualTo(far), "a replica gets no far entries");
        Assert.That(a.FarWhileReplicated, Is.Zero);
        Assert.That(a.StatesOf.ContainsKey(shipB), Is.True, "and full replication instead");

        // b opens out to 40 km again: the replica goes and the marker comes back.
        fleet.Worker.Move(shipB, new Vector3(40_000, 0, 0));
        Assert.That(fleet.Run(() => !a.Replicas.Contains(shipB) && a.Far.ContainsKey(shipB), seconds: 10), Is.True, "back to the far tier beyond the normal radius");
        Assert.That(a.Far[shipB].X, Is.EqualTo(40_000.0));
        Assert.That(a.LastError, Is.Empty);
    }

    [Test]
    public void AClientsFarTierIsBoundedNearestFirst()
    {
        using var fleet = new Fleet(gateways: 1, configure: c => c.InterestFarMaxEntities = 2);
        var a = Pilot(fleet, "a", new Vector3(0, 0, 0));
        fleet.Worker.Spawn(ShipC, new Vector3(30_000, 0, 0)).FarRadius = FarRadius;
        fleet.Worker.Spawn(ShipD, new Vector3(10_000, 0, 0)).FarRadius = FarRadius;
        fleet.Worker.Spawn(ShipE, new Vector3(20_000, 0, 0)).FarRadius = FarRadius;
        Drive(fleet);
        Assert.That(fleet.Run(() => a.Far.ContainsKey(ShipD) && a.Far.ContainsKey(ShipE), seconds: 10), Is.True, "the two nearest");
        fleet.RunFor(1.5);
        Assert.That(a.Far.ContainsKey(ShipC), Is.False, "the third is over the budget");
        Assert.That(fleet.Gateways[0].FarTierSize(Id(a)), Is.EqualTo(2));

        // The nearest leaves the radius: the next one in takes its place.
        fleet.Worker.Move(ShipD, new Vector3(0, 0, 150_000));
        Assert.That(fleet.Run(() => a.Far.ContainsKey(ShipC) && !a.Far.ContainsKey(ShipD), seconds: 10), Is.True, "nearest-first: c comes in as d goes");
        Assert.That(a.FarGone, Does.Contain(ShipD));

        // A despawn takes it out of the far tier at once.
        fleet.Worker.Despawn(ShipE);
        Assert.That(fleet.Run(() => !a.Far.ContainsKey(ShipE), seconds: 5), Is.True, "a despawned far entity is gone");
    }

    [Test]
    public void AProtocol25ClientIsNeverSentTheFarTier()
    {
        using var fleet = new Fleet(gateways: 1);
        var old = Pilot(fleet, "old", new Vector3(0, 0, 0), version: HelloMsg.MinProtocolVersion);
        Assert.That(old.Welcome!.Value.NegotiatedVersion, Is.EqualTo(HelloMsg.MinProtocolVersion));
        fleet.Worker.Spawn(ShipC, new Vector3(30_000, 0, 0)).FarRadius = FarRadius;
        var now = Pilot(fleet, "now", new Vector3(100, 0, 0));
        Drive(fleet);
        Assert.That(fleet.Run(() => now.Far.ContainsKey(ShipC), seconds: 10), Is.True, "a protocol-26 client gets it");
        fleet.RunFor(1.5);
        Assert.That(old.Far, Is.Empty, "a protocol-25 client does not");
        Assert.That(old.Wire.Exists(w => w.StartsWith("far")), Is.False);
        Assert.That(old.LastError, Is.Empty, "and is sent nothing it cannot read");
    }
}
