using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Conformance scenario 16 (<c>docs/conformance-suite.md</c>, design <c>docs/scope-activation.md</c> §11): a crewed
/// ship flies out of one grid scope (a planet) into another (space). Tier A — the real <see cref="NebulaGateway"/>
/// on a real socket over an in-process <see cref="LocalControlPlane"/>, <see cref="FakeClient"/>s built from the
/// real client handshake and a <see cref="FakeWorker"/> built from the real interest code, because everything
/// that went wrong was a decision of the gateway's: which rows a client holds, which scope a rider is in, which
/// regions it subscribes and who is told what when a carrier changes scope. The worker's half — a group commit
/// keeps the crew in their seats — needs a real <c>NebulaWorker</c> and is
/// <c>Tests/EditMode/ConformanceCrossScopeCrewTests.cs</c> (tier B).
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceCrossScopeCarrierTests
{
    private const string Planet = "world/planet";
    private const string Space = "world/space";
    private const ulong Hull = 500, OnPlanet = 700, InSpace = 600;

    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-cross-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "c0", Index = 0, Size = new(64, 64, 64), transform = new ContainerFrame { position = new(32, 0, 32) } } },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown]
    public void Cleanup()
    {
        ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static ChunkGridDefinition PlanetGrid() => new() { CellSize = new Vector3(64, 512, 64), Planar = true };
    private static ChunkGridDefinition SpaceGrid() => new() { CellSize = new Vector3(64, 64, 64), Planar = false };
    private static ContainerRef Anchor(string scope) => ContainerRef.Runtime(ChunkKeys.RuntimeId(scope, new Vector3Int(0, 0, 0)));
    private static string AnchorId(string scope) => ChunkKeys.ContainerId(scope, new Vector3Int(0, 0, 0));

    /// <summary>
    /// Two grid scopes on one worker, a ship standing on the planet with the rider's pawn seated in
    /// it, an onlooker standing next to it, and one entity in each world. Returns once every client holds what it
    /// should and the gateway follows the ship by name.
    /// </summary>
    private sealed class Voyage : IDisposable
    {
        public readonly Fleet Fleet = new(gateways: 1);
        public FakeWorker Worker => Fleet.Worker;
        public readonly FakeClient Rider, Onlooker;
        public ulong RiderPawn, OnlookerPawn;

        public Voyage()
        {
            Activate(Planet, PlanetGrid());
            Activate(Space, SpaceGrid());
            Worker.DeferSpawns = true;
            Rider = Fleet.Connect(0, "rider", scope: Planet);
            Onlooker = Fleet.Connect(0, "onlooker", scope: Planet);
            Assert.That(Fleet.Run(() => Worker.Claims.Count >= 2), Is.True, "both clients were placed on the planet");
            Worker.Spawn(Hull, new Vector3(0, 2, 0), Anchor(Planet));
            foreach (var claim in Worker.Claims)
            {
                bool rider = claim.Name == "rider";
                ulong pawn = 100 + claim.ClientId;
                if (rider) { RiderPawn = pawn; Worker.Spawn(pawn, new Vector3(0, 1, 1), ContainerRef.Dynamic(Hull), owner: claim.ClientId); }
                else { OnlookerPawn = pawn; Worker.Spawn(pawn, new Vector3(4, 0, 4), Anchor(Planet), owner: claim.ClientId); }
            }
            Worker.Spawn(OnPlanet, new Vector3(-4, 0, -4), Anchor(Planet));
            Worker.Spawn(InSpace, new Vector3(2, 0, 2), Anchor(Space));
            Fleet.OnPump = () => Worker.PublishStates();
            Assert.That(Fleet.Run(() => Rider.Replicas.IsSupersetOf(new[] { Hull, RiderPawn, OnPlanet }) &&
                                        Onlooker.Replicas.IsSupersetOf(new[] { Hull, RiderPawn, OnPlanet })), Is.True,
                "on the planet both clients see the ship, its rider and the planet's entity");
            Assert.That(Fleet.Run(() => Worker.ReceiverOf("gw1")?.Entities.Contains(Hull) == true), Is.True,
                "the rider's gateway follows the ship its pawn is seated in by name (D12)");
        }

        private void Activate(string scope, ChunkGridDefinition grid) => Fleet.Plane.ActivateScope(new ScopeActivationRequest
        {
            ScopeKey = scope, Definition = grid.ToScopeDefinition(), PreferredWorkerId = "w1", Requester = "conformance",
        });

        public void Dispose() => Fleet.Dispose();
    }

    [Test]
    public void APreparationIntoAnotherScopeGivesTheRiderItsDestinationRowFirst()
    {
        using var v = new Voyage();
        var lease = v.Fleet.Plane.FindLease(AnchorId(Space))!;

        v.Worker.Prepare(v.RiderPawn, Anchor(Space), lease.Epoch);

        Assert.That(v.Fleet.Run(() => v.Worker.ReadyAnswers.Count == 1), Is.True, "the rider's client answered");
        Assert.That(v.Rider.Preparations[0].HadRow, Is.True, "it held the destination's row when it was asked (D14)");
        Assert.That(v.Worker.ReadyAnswers[0].Success, Is.True, "so it could prepare the destination and say so: the crossing becomes ready");
        Assert.That(v.Rider.Wire.IndexOf("container+ " + AnchorId(Space)), Is.LessThan(v.Rider.Wire.IndexOf("prepare " + v.RiderPawn)),
            "the row travels ahead of the request, on the same reliable stream");

        v.Fleet.RunFor(0.6);
        Assert.That(v.Rider.Containers, Does.Contain(AnchorId(Space)), "and stays pinned while the crossing can still commit, whatever the window says");
        Assert.That(v.Rider.HeardAbout, Does.Not.Contain(InSpace), "a preparation grants no visibility of the destination's entities");
        Assert.That(v.Onlooker.Wire, Does.Not.Contain("container+ " + AnchorId(Space)), "and nobody else is told the destination exists");
    }

    [Test]
    public void AShipCrossingScopesTakesItsRidersClientWithItAndLeavesTheOnlookerBehind()
    {
        using var v = new Voyage();

        // Only the hull is committed; its rider was never prepared. The gateway has to follow the ship (D12).
        v.Worker.MoveTo(Hull, Anchor(Space), new Vector3(0, 2, 0));

        Assert.That(v.Fleet.Run(() => v.Rider.Replicas.Contains(InSpace)), Is.True,
            "the rider's scope followed its ship: it now sees the entity standing in space");
        Assert.That(v.Rider.ContainerOf[Hull], Is.EqualTo(Anchor(Space)), "and holds the ship in the space chunk");
        Assert.That(v.Rider.Replicas, Does.Contain(Hull).And.Contain(v.RiderPawn), "with its own pawn still aboard");
        Assert.That(v.Rider.Despawned, Does.Not.Contain(Hull).And.Not.Contain(v.RiderPawn), "neither was ever taken away, not for a moment");
        Assert.That(v.Rider.UnresolvableNames, Is.Empty, "and every message naming a container came after that container's row (D15)");

        Assert.That(v.Fleet.Run(() => !v.Rider.Replicas.Contains(OnPlanet) && !v.Rider.Containers.Contains(AnchorId(Planet))), Is.True,
            "the planet it left goes: its entity at once, its rows after the linger (D17)");

        Assert.That(v.Fleet.Run(() => !v.Onlooker.Replicas.Contains(Hull) && !v.Onlooker.Replicas.Contains(v.RiderPawn)), Is.True,
            "the onlooker on the planet loses the ship and everyone aboard");
        Assert.That(v.Onlooker.Wire, Does.Not.Contain("container+ " + AnchorId(Space)), "and is never told which chunk of space it went to (D13)");
        Assert.That(v.Onlooker.HeardAbout, Does.Not.Contain(InSpace));
        Assert.That(v.Onlooker.Replicas, Does.Contain(OnPlanet), "while its own world is untouched");
    }

    [Test]
    public void ARiderReconnectingAfterTheCrossingFindsTheShipInItsNewScope()
    {
        using var v = new Voyage();
        v.Worker.MoveTo(Hull, Anchor(Space), new Vector3(0, 2, 0));
        Assert.That(v.Fleet.Run(() => v.Rider.Replicas.Contains(InSpace)), Is.True);
        string token = v.Rider.Welcome!.Value.Token, session = v.Rider.Welcome.Value.SessionToken;

        // The worker still holds the pawn (the reclaim grace) and re-announces it to whichever gateway claims it.
        v.Worker.AdoptPawn(v.Rider.Welcome.Value.ClientId, v.RiderPawn);
        v.Worker.DeferSpawns = false;
        v.Rider.Disconnect();
        v.Fleet.RunFor(0.5);
        var back = v.Fleet.Connect(0, "rider", token: token, session: session, scope: Space);

        Assert.That(v.Fleet.Run(() => back.Replicas.IsSupersetOf(new[] { Hull, v.RiderPawn, InSpace }), seconds: 10), Is.True,
            "the session comes back to its pawn, the pawn's ship is found by name wherever it is, and the client is in space");
        Assert.That(back.ContainerOf[Hull], Is.EqualTo(Anchor(Space)));
        Assert.That(back.HeardAbout, Does.Not.Contain(OnPlanet), "and it is not shown the world it left");
        Assert.That(back.UnresolvableNames, Is.Empty);
    }

    [Test]
    public void AnUpdateNamingAChunkTheGatewayHasNoRowForYetWaitsForTheRow()
    {
        using var v = new Voyage();
        // The worker leased the next chunk and flew into it before the lease reached the gateway's mirror.
        var next = new Vector3Int(1, 0, 0);
        var nextRef = ContainerRef.Runtime(ChunkKeys.RuntimeId(Planet, next));
        v.Worker.MoveTo(Hull, nextRef, new Vector3(0, 2, 0));
        v.Fleet.RunFor(0.5);

        Assert.That(v.Rider.Replicas, Does.Contain(Hull).And.Contain(v.RiderPawn), "nobody loses the ship while the gateway cannot describe where it is");
        Assert.That(v.Onlooker.Replicas, Does.Contain(Hull));
        Assert.That(v.Rider.ContainerOf[Hull], Is.EqualTo(Anchor(Planet)), "its updates are held rather than relayed into a container nobody knows");

        v.Fleet.Plane.EnsureRuntimeContainer(ChunkKeys.ContainerId(Planet, next), PlanetGrid().AbsoluteBoundsOf(next), "w1",
            new InstanceContainerInfo { InstanceId = ScopeKeys.Hash(Planet), ScopeKey = Planet, PartId = ChunkKeys.PartId(next) });

        Assert.That(v.Fleet.Run(() => v.Rider.ContainerOf[Hull] == nextRef && v.Onlooker.ContainerOf[Hull] == nextRef), Is.True,
            "the row arrives and the held updates follow it, in order");
        Assert.That(v.Rider.UnresolvableNames, Is.Empty, "every client had the row before the first message naming it (D15)");
        Assert.That(v.Onlooker.UnresolvableNames, Is.Empty);
        Assert.That(v.Rider.Despawned, Does.Not.Contain(Hull));
        Assert.That(v.Onlooker.Despawned, Does.Not.Contain(Hull), "the ship never left the onlooker's world, so it was never taken away");
    }
}
