using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Conformance scenario 47 (NEB-401, <c>docs/container-tree.md</c> D18 and D22): what stands in a planet carrier's own
/// frame, between its hosted ground and the face of its box, stays in a client's set on a gateway of its own. A real
/// <see cref="NebulaGateway"/> over loopback UDP, out of every worker's process, so its registry holds no box for the
/// planet and no copy of the planet's hosted chunks: everything it knows about them comes from the lease rows and the
/// entity records. A <see cref="FakeWorker"/> stands in for the worker that owns the planet and its ground.
/// <para>
/// Before the fix the gateway resolved the scope of the planet's box through its registry, found nothing, and refused
/// everything in it: a ship climbing out of the ground was dropped from its own pilot's client the moment it entered
/// the planet's frame, while the worker went on simulating it.
/// </para>
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceCarrierFrameGatewayTests
{
    private string _directory = "";
    private const ulong Planet = 8001, Ship = 8002, Crate = 8003, Ground = 5001;
    private static readonly string PlanetBox = "planet#" + Planet;
    private static string GroundId => ContainerRegistry.RuntimeContainerId(Ground);

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-frame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        // The space the planet stands in: one static container 400 km a side, centred on the origin.
        var path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "c0", Index = 0, Size = new(400_000, 400_000, 400_000), transform = new ContainerFrame { position = new(0, 0, 0) } } },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown] public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    /// <summary>
    /// The mesh: w1 owns the space, the planet's box (a carried frame with regions of its own, as a worker's
    /// carried-lease pass writes its row) and one chunk of the planet's hosted ground, 512 m square and 1.2 km tall,
    /// fixed in the planet's box at its origin.
    /// </summary>
    private static Fleet Mesh() => new(gateways: 1, world: fleet =>
    {
        fleet.Assign("c0", fleet.Worker.WorkerId);
        fleet.Plane.EnsureContainer(PlanetBox, ownPhysicsFrame: true, frameInterest: FrameInterestMode.OwnRegions);
        fleet.Plane.AssignContainer(PlanetBox, fleet.Worker.WorkerId);
        fleet.Plane.EnsureRuntimeContainer(GroundId,
            new ContainerPlacement { ParentId = PlanetBox, Center = Double3.Zero, Size = new Vector3(512f, 1200f, 512f) }, fleet.Worker.WorkerId);
    });

    /// <summary>A pilot joins on the ground, in the hosted chunk, and is seated in a ship parked beside it.</summary>
    private static (FakeClient Pilot, ulong Pawn) Board(Fleet fleet)
    {
        // The planet 100 km out in space, always relevant (as a planet is), so every gateway holds its record.
        fleet.Worker.Spawn(Planet, new Vector3(100_000f, 0f, 0f), alwaysRelevant: true);
        fleet.Worker.PawnContainer = ContainerRef.Runtime(Ground);
        fleet.Worker.PawnPlacement = _ => new Vector3(0f, 1f, 0f);
        var pilot = fleet.Connect(0, "pilot");
        Assert.That(fleet.Run(() => pilot.Join == JoinState.Joined && pilot.Welcome != null && fleet.Worker.Pawns.ContainsKey(Id(pilot)), seconds: 10), Is.True, "the pilot joins");
        ulong pawn = fleet.Worker.Pawns[Id(pilot)];
        fleet.Worker.Spawn(Ship, new Vector3(10f, 2f, 0f), ContainerRef.Runtime(Ground), owner: Id(pilot), alwaysRelevant: true);
        fleet.Worker.Board(pawn, Ship, new Vector3(0f, 1f, 0f));
        fleet.OnPump = fleet.Worker.PublishStates;
        Assert.That(fleet.Run(() => pilot.Replicas.Contains(Ship) && pilot.Replicas.Contains(pawn) && pilot.Replicas.Contains(Planet), seconds: 10), Is.True,
            $"the pilot holds the planet, its pawn and the ship on the ground (holds {string.Join(", ", pilot.Replicas)})");
        return (pilot, pawn);
    }

    private static ulong Id(FakeClient client) => client.Welcome!.Value.ClientId;

    [Test]
    public void AnEntityStandingInAPlanetsOwnFrameOutsideEveryHostedChunkStaysInAClientsSet()
    {
        using var fleet = Mesh();
        var (pilot, pawn) = Board(fleet);
        // A crate in the planet's frame 5 km above its ground: over every hosted chunk, inside the planet's box.
        fleet.Worker.Spawn(Crate, new Vector3(0f, 5_000f, 0f), ContainerRef.Dynamic(Planet), alwaysRelevant: true);
        Assert.That(fleet.Run(() => pilot.Replicas.Contains(Crate), seconds: 10), Is.True, "what stands in the planet's own frame is in the pilot's set");
        fleet.RunFor(1.5);
        Assert.That(pilot.Replicas, Does.Contain(Crate), "and stays there");
        Assert.That(pilot.Despawned, Does.Not.Contain(Crate));
        Assert.That(pilot.LastError, Is.Empty);
    }

    [Test]
    public void AShipClimbingFromAHostedChunkThroughThePlanetsFrameIntoSpaceIsNeverDropped()
    {
        using var fleet = Mesh();
        var (pilot, pawn) = Board(fleet);

        // Up out of the ground into the planet's own frame, then on up through it, then out of the box into space.
        var climb = new (ContainerRef Container, Vector3 At, string Where)[]
        {
            (ContainerRef.Dynamic(Planet), new Vector3(10f, 700f, 0f), "just over the hosted chunk's top"),
            (ContainerRef.Dynamic(Planet), new Vector3(10f, 20_000f, 0f), "20 km up in the planet's frame"),
            (new ContainerRef(0), new Vector3(100_010f, 60_000f, 0f), "out of the planet's box, in space"),
        };
        foreach (var step in climb)
        {
            fleet.Worker.MoveTo(Ship, step.Container, step.At);
            Assert.That(fleet.Run(() => pilot.ContainerOf.TryGetValue(Ship, out var at) && at == step.Container, seconds: 10), Is.True,
                $"{step.Where}: the pilot is told the ship is in {step.Container}");
            fleet.RunFor(1.0);
            Assert.That(pilot.Replicas, Does.Contain(Ship), $"{step.Where}: the ship is still in its pilot's set");
            Assert.That(pilot.Replicas, Does.Contain(pawn), $"{step.Where}: and so is the pilot's own pawn");
            Assert.That(pilot.Despawned, Does.Not.Contain(Ship), $"{step.Where}: the ship was never dropped on the way");
            int states = pilot.StatesOf.TryGetValue(Ship, out int n) ? n : 0;
            fleet.RunFor(0.5);
            Assert.That(pilot.StatesOf.TryGetValue(Ship, out int m) ? m : 0, Is.GreaterThan(states), $"{step.Where}: its states keep coming");
        }
        Assert.That(pilot.LastError, Is.Empty);
    }

    [Test]
    public void AFrameWhoseCarrierTheGatewayHasNoRecordOfStillFailsClosed()
    {
        using var fleet = Mesh();
        var (pilot, _) = Board(fleet);
        // A crate filed in the box of a framed carrier nobody has spawned: its scope cannot be described, so it is
        // never shown, rather than falling back to the public world.
        const ulong Unknown = 8100;
        fleet.Plane.EnsureContainer("moon#" + Unknown, ownPhysicsFrame: true, frameInterest: FrameInterestMode.OwnRegions);
        fleet.Plane.AssignContainer("moon#" + Unknown, fleet.Worker.WorkerId);
        fleet.Worker.Spawn(Crate, new Vector3(0f, 10f, 0f), ContainerRef.Dynamic(Unknown), alwaysRelevant: true);
        fleet.RunFor(2.0);
        Assert.That(pilot.Replicas, Does.Not.Contain(Crate), "an unknown frame's contents never reach a client");
    }
}
