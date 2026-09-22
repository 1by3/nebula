using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The gateway leg of conformance scenario 3 (<c>docs/conformance-suite.md</c>, NEB-240): while a scope is
/// retiring or restoring <b>a client is refused admission and told why</b>, and it is placed — with no reconnect —
/// the moment the restore completes. Tier A: the real <see cref="NebulaGateway"/> on a real socket over an
/// in-process <see cref="LocalControlPlane"/>, with a <see cref="FakeWorker"/> to be spawned into. The test plays
/// the orchestrator's part (<c>NebulaOrchestrator.SweepScopes</c>) and the workers' acknowledgements; the state
/// machine, the ordering and the admission rule are production code. The rest of the scenario is
/// <c>Tests/EditMode/ConformanceScopeLifecycleTests.cs</c> (the machine) and
/// <c>Tests/EditMode/ConformanceScopeCheckpointTests.cs</c> (the store).
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceScopeAdmissionTests
{
    private const string Key = "interior/raid-7";
    private const string Part = "interior";
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-scope-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "c0", Index = 0, Size = new(64, 64, 64), transform = new ContainerFrame { position = new(32, 0, 0) } } },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown]
    public void Cleanup()
    {
        ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static ScopeDefinition Room() => new()
    {
        Kind = ScopeKind.Parts,
        ObservePublic = false,
        Parts = { new ScopePart { PartId = Part, Center = new(500, 0, 0), Size = new(40, 40, 40) } },
    };

    private static string ScopeContainerId => ScopeKeys.ContainerId(Key, Part);
    private static ContainerRef ScopeContainer => ContainerRef.Runtime(ScopeKeys.Hash(Key + "/" + Part));

    private static void Activate(Fleet fleet) => fleet.Plane.ActivateScope(new ScopeActivationRequest
    { ScopeKey = Key, Definition = Room(), PreferredWorkerId = fleet.Worker.WorkerId, Requester = "matchmaker" });

    /// <summary>What the orchestrator's sweep does once every part has acknowledged its checkpoint.</summary>
    private static void FinishRetire(Fleet fleet)
    {
        fleet.Plane.AckScopePart(Key, ScopeContainerId, ScopePhase.Checkpointed, 2, fleet.Worker.WorkerId);
        fleet.Plane.RemoveContainer(ScopeContainerId);
        fleet.Plane.SetScopeState(Key, ScopeState.Retired);
    }

    [Test]
    public void AClientIsRefusedWhileTheScopeRetiresAndRestoresAndIsPlacedWhenTheRestoreCompletes()
    {
        using var fleet = new Fleet(gateways: 1);
        Activate(fleet);
        Assert.That(fleet.Plane.IsScopeReady(Key), Is.True);

        // Retiring: the lease row is still there and still owned, so nothing but the scope's state refuses this.
        fleet.Plane.SetScopeState(Key, ScopeState.Retiring);
        var early = fleet.Connect(0, "early", scope: Key);
        Assert.That(fleet.Run(() => early.JoinReason == JoinHoldReason.ScopeRetiring, seconds: 10), Is.True, "the client is told why it is waiting");
        Assert.That(fleet.Worker.Claims, Is.Empty, "a retiring scope admits nobody, even though its container still has an owner");
        Assert.That(early.Disconnected, Is.False, "the join is held, not refused");
        Assert.That(early.Join, Is.EqualTo(JoinState.Starting));

        FinishRetire(fleet);
        // The gateway retries a held join every few seconds, so the reason follows the row.
        Assert.That(fleet.Run(() => early.JoinReason == JoinHoldReason.ScopeNotReady, seconds: 10), Is.True, "a retired scope is one nobody has asked for yet");
        Assert.That(fleet.Worker.Claims, Is.Empty);

        // Re-activation. The lease row comes back with the same id and the same owner, and the gateway still holds:
        // the records have not been restored yet.
        Activate(fleet);
        Assert.That(fleet.Plane.FindScope(Key)!.State, Is.EqualTo(ScopeState.Restoring));
        Assert.That(fleet.Plane.IsScopeReady(Key), Is.True, "the leases are back and owned");
        Assert.That(fleet.Run(() => early.JoinReason == JoinHoldReason.ScopeRestoring, seconds: 10), Is.True);
        Assert.That(fleet.Worker.Claims, Is.Empty, "admission is refused until the restore completes");

        // The worker reports its part restored; the orchestrator's sweep turns that into Active.
        fleet.Plane.AckScopePart(Key, ScopeContainerId, ScopePhase.Restored, 2, fleet.Worker.WorkerId);
        var scope = fleet.Plane.FindScope(Key)!;
        Assert.That(ScopeLifecycle.NextState(scope, 1, out _), Is.EqualTo(ScopeState.Active));
        fleet.Plane.SetScopeState(Key, ScopeState.Active);

        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count > 0, seconds: 10), Is.True, "the held client is placed once the restore is done");
        Assert.That(fleet.Worker.Claims[0].Container, Is.EqualTo(ScopeContainer));
        Assert.That(early.Disconnected, Is.False, "and it never had to reconnect");
    }

    [Test]
    public void APublicClientIsUnaffectedByAScopeRetiring()
    {
        using var fleet = new Fleet(gateways: 1);
        Activate(fleet);
        fleet.Plane.SetScopeState(Key, ScopeState.Retiring);

        var local = fleet.Connect(0, "local");

        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count > 0), Is.True);
        Assert.That(fleet.Worker.Claims[0].Container, Is.EqualTo(new ContainerRef(0)), "the public world has no scope row and no lifecycle");
        Assert.That(local.Disconnected, Is.False);
    }
}
