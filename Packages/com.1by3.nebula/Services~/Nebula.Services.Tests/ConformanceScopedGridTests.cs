using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Conformance scenario 4 (<c>docs/conformance-suite.md</c>, design <c>docs/scoped-chunk-grids.md</c>): two chunk
/// grids whose coordinates overlap exactly are two worlds. Tier A — the real <see cref="NebulaGateway"/> on a real
/// socket over an in-process <see cref="LocalControlPlane"/>, two <see cref="FakeClient"/>s built from the real
/// client handshake, and a <see cref="FakeWorker"/> built from the real interest code. This tier is what can say
/// anything about what a <i>client</i> is told, which is where interest leakage would show. The worker-side half
/// of the scenario — that nothing ghosts across a scope boundary — is
/// <c>Tests/EditMode/ConformanceScopedGridTests.cs</c>, which needs real <c>NebulaWorker</c>s.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceScopedGridTests
{
    private const string Alpha = "world/alpha";
    private const string Beta = "world/beta";
    private static readonly Vector3Int Anchor = new(0, 0, 0);

    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-scoped-grid-" + Guid.NewGuid().ToString("N"));
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

    private static ChunkGridDefinition Definition() => new()
    {
        CellSize = new Vector3(64, 512, 64),
        Planar = true,
        Anchor = Anchor,
    };

    private static string ContainerIdOf(string scope) => ChunkKeys.ContainerId(scope, Anchor);
    private static ContainerRef RefOf(string scope) => ContainerRef.Runtime(ChunkKeys.RuntimeId(scope, Anchor));

    private static void Activate(Fleet fleet, string scope) => fleet.Plane.ActivateScope(new ScopeActivationRequest
    {
        ScopeKey = scope,
        Definition = Definition().ToScopeDefinition(),
        PreferredWorkerId = fleet.Worker.WorkerId,
        Requester = "conformance",
    });

    [Test]
    public void TwoScopesWithOverlappingChunkCoordinatesGetDistinctLeasesAndRecords()
    {
        using var fleet = new Fleet(gateways: 1);
        Activate(fleet, Alpha);
        Activate(fleet, Beta);

        string a = ContainerIdOf(Alpha), b = ContainerIdOf(Beta);
        Assert.That(a, Is.Not.EqualTo(b), "chunk (0,0,0) of two scopes is two container ids, so two lease rows and two persistence records");
        var leaseA = fleet.Plane.FindLease(a);
        var leaseB = fleet.Plane.FindLease(b);
        Assert.That(leaseA, Is.Not.Null);
        Assert.That(leaseB, Is.Not.Null);
        Assert.That(leaseA!.Instance!.InstanceId, Is.EqualTo(ScopeKeys.Hash(Alpha)));
        Assert.That(leaseB!.Instance!.InstanceId, Is.EqualTo(ScopeKeys.Hash(Beta)));
        Assert.That(leaseA.Instance.PartId, Is.EqualTo("c/0/0/0"), "the coordinate travels on the row, so every role can place the chunk");
        Assert.That(leaseA.BoundsCenter, Is.EqualTo(leaseB.BoundsCenter), "the two chunks really do occupy the same ground");

        // A grid is unbounded: activating one must not enumerate it.
        Assert.That(fleet.Plane.FindScope(Alpha)!.ContainerIds, Is.EqualTo(new[] { a }));
        Assert.That(fleet.Plane.IsScopeReady(Alpha), Is.True);
        Assert.That(fleet.Plane.IsScopeReady(Beta), Is.True);
    }

    [Test]
    public void TheGridDefinitionTravelsInTheScopeRowSoEveryRoleRebuildsTheSameGrid()
    {
        using var fleet = new Fleet(gateways: 1);
        Activate(fleet, Alpha);

        var scope = fleet.Plane.FindScope(Alpha);
        Assert.That(scope!.Definition.Kind, Is.EqualTo(ScopeKind.Grid));
        var read = ChunkGridDefinition.Of(scope);
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.CellSize, Is.EqualTo(new Vector3(64, 512, 64)));
        Assert.That(read.Planar, Is.True);
        Assert.That(read.NormalizedAnchor(), Is.EqualTo(Anchor));

        // A role with no control plane (a client) has only the container row, and that is enough.
        var inferred = ChunkGridDefinition.Infer(Anchor, new Bounds(scope.Definition.Parts[0].Center, scope.Definition.Parts[0].Size));
        Assert.That(inferred!.CellSize, Is.EqualTo(read.CellSize));
        Assert.That(inferred.Planar, Is.EqualTo(read.Planar));
    }

    [Test]
    public void AClientInOneScopeIsNeverToldAboutTheOtherScopesChunksOrEntities()
    {
        using var fleet = new Fleet(gateways: 1);
        Activate(fleet, Alpha);
        Activate(fleet, Beta);
        // Spawn the pawns ourselves so each lands in the scope its client was routed to.
        fleet.Worker.DeferSpawns = true;

        var clientA = fleet.Connect(0, "alpha-player", scope: Alpha);
        var clientB = fleet.Connect(0, "beta-player", scope: Beta);
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count >= 2), Is.True, "both clients were placed");

        foreach (var claim in fleet.Worker.Claims)
        {
            Assert.That(claim.Container, Is.EqualTo(claim.Name == "alpha-player" ? RefOf(Alpha) : RefOf(Beta)),
                "the key decided which world the player entered");
            fleet.Worker.Spawn(100 + claim.ClientId, Vector3.zero, claim.Container, owner: claim.ClientId);
        }

        // The chunk next door, leased on demand in both worlds, as an allocator would: it holds nothing, so the
        // only way a client hears of it at all is its own window — which is what must be scope-qualified.
        var next = new Vector3Int(1, 0, 0);
        foreach (string scope in new[] { Alpha, Beta })
            fleet.Plane.EnsureRuntimeContainer(ChunkKeys.ContainerId(scope, next), Definition().AbsoluteBoundsOf(next),
                fleet.Worker.WorkerId, new InstanceContainerInfo { InstanceId = ScopeKeys.Hash(scope), ScopeKey = scope, PartId = ChunkKeys.PartId(next) });

        // One neutral entity per scope, at the same place in both — the same chunk coordinate, the same metres.
        var inAlpha = fleet.Worker.Spawn(11, new Vector3(1, 0, 1), RefOf(Alpha));
        var inBeta = fleet.Worker.Spawn(22, new Vector3(1, 0, 1), RefOf(Beta));
        fleet.OnPump = () => fleet.Worker.PublishStates();
        Assert.That(fleet.Run(() => clientA.Replicas.Contains(inAlpha.NetId) && clientB.Replicas.Contains(inBeta.NetId)), Is.True,
            "each client is told about the entity standing in its own world");

        Assert.That(clientA.Replicas, Does.Not.Contain(inBeta.NetId), "and never about the other world's, at the same coordinate");
        Assert.That(clientB.Replicas, Does.Not.Contain(inAlpha.NetId));
        Assert.That(clientA.HeardAbout, Does.Not.Contain(inBeta.NetId), "not even a stray state entry");
        Assert.That(clientB.HeardAbout, Does.Not.Contain(inAlpha.NetId));

        Assert.That(clientA.Containers, Does.Contain(ContainerIdOf(Alpha)), "a client holds the row of the chunk it stands in");
        Assert.That(clientA.Containers, Does.Not.Contain(ContainerIdOf(Beta)), "and is never told that the other world's chunk exists");
        Assert.That(clientB.Containers, Does.Contain(ContainerIdOf(Beta)));
        Assert.That(clientB.Containers, Does.Not.Contain(ContainerIdOf(Alpha)));
        Assert.That(clientA.Containers, Does.Not.Contain("c0"), "nor about the public world, which this scope does not observe");

        // The empty chunk next door: only a window can have brought it, and only in the client's own scope.
        Assert.That(fleet.Run(() => clientA.Containers.Contains(ChunkKeys.ContainerId(Alpha, next))), Is.True,
            "a client is told about the empty terrain its own world's window covers");
        Assert.That(clientA.Containers, Does.Not.Contain(ChunkKeys.ContainerId(Beta, next)), "and never about the other world's, in the same window");
        Assert.That(clientB.Containers, Does.Not.Contain(ChunkKeys.ContainerId(Alpha, next)));
    }
}
