using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Conformance scenario 12 (<c>docs/conformance-suite.md</c>, NEB-236): <b>a join or a transfer into a saturated
/// target is refused with a typed reason unless the admission hook admits it</b>, and nothing is ever split to
/// make room. Tier A: the real <see cref="NebulaGateway"/> on a real socket over an in-process
/// <see cref="LocalControlPlane"/>, with a <see cref="FakeWorker"/> to be spawned into. The test plays the
/// orchestrator's part - deriving the capacity signal from cost telemetry and publishing it on the lease rows,
/// which <c>NebulaOrchestrator.PublishCapacity</c> does on a live mesh; the derivation, the gateway's refusal, the
/// typed wire field and the hook are production code. The derivation itself is unit tested in both places by
/// <c>Tests/EditMode/CapacityAdmissionTests.cs</c>. Design record: <c>docs/capacity-admission.md</c>.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceCapacityAdmissionTests
{
    private const string Key = "station/alpha";
    private const string Part = "dock";
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-capacity-" + Guid.NewGuid().ToString("N"));
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
        NebulaAdmission.Reset();
        ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static ScopeDefinition Station() => new()
    {
        Kind = ScopeKind.Parts,
        ObservePublic = false,
        Parts = { new ScopePart { PartId = Part, Center = new(500, 0, 0), Size = new(40, 40, 40) } },
    };

    private static string ScopeContainerId => ScopeKeys.ContainerId(Key, Part);
    private static ContainerRef ScopeContainer => ContainerRef.Runtime(ScopeKeys.Hash(Key + "/" + Part));

    private static void Activate(Fleet fleet) => fleet.Plane.ActivateScope(new ScopeActivationRequest
    { ScopeKey = Key, Definition = Station(), PreferredWorkerId = fleet.Worker.WorkerId, Requester = "travel-service" });

    /// <summary>
    /// What <c>NebulaOrchestrator.PublishCapacity</c> does once a worker's telemetry says the container is hot:
    /// derive the reading from the cost row and put it on the lease.
    /// </summary>
    private static CapacityInfo Report(Fleet fleet, string containerId, float tickShareMs, float threshold = 0.9f)
    {
        var row = new ContainerCost { ContainerId = containerId, ScopeKey = Key, WorkerId = fleet.Worker.WorkerId, TickShareMs = tickShareMs };
        ContainerCost.Resolve(ref row, WorkerLoadTracker.TickPeriodMs, NebulaConfig.DefaultCostLinkBytesPerSec);
        var info = NebulaCapacity.Derive(row, threshold);
        fleet.Plane.SetContainerCapacity(containerId, info.Saturation, info.Dominant, info.AtCapacity);
        return info;
    }

    [Test]
    public void AJoinIntoASaturatedScopeIsRefusedWithATypedReasonAndNothingIsSplitToMakeRoom()
    {
        using var fleet = new Fleet(gateways: 1);
        Activate(fleet);
        Assert.That(fleet.Plane.IsScopeReady(Key), Is.True);

        var reading = Report(fleet, ScopeContainerId, tickShareMs: 16f);
        Assert.That(reading.AtCapacity, Is.True, "16 ms of a 16.7 ms tick is past the 0.9 threshold");
        Assert.That(fleet.Plane.ScopeCapacityOf(Key).AtCapacity, Is.True, "the whole scope is as full as its worst part");

        var late = fleet.Connect(0, "late", scope: Key);
        Assert.That(fleet.Run(() => late.Rejected != null, seconds: 10), Is.True, "the arrival gets an answer rather than waiting for ever");
        var rejected = late.Rejected!.Value;
        Assert.That(rejected.Code, Is.EqualTo(JoinRejectReason.AtCapacity), "and a typed one the travel service can act on");
        Assert.That(rejected.Saturation, Is.GreaterThan(0.9f).And.LessThanOrEqualTo(1.1f), "carrying how full the target was");
        Assert.That(rejected.Reason, Does.Contain("capacity"));
        Assert.That(fleet.Worker.Claims, Is.Empty, "nobody was placed, and no second copy of the station was made");
        Assert.That(fleet.Plane.FindScope(Key)!.ContainerIds.Count, Is.EqualTo(1), "the scope still has exactly the parts it was authored with");
    }

    [Test]
    public void TheAdmissionHookLetsOneParticularArrivalIntoAFullScope()
    {
        using var fleet = new Fleet(gateways: 1);
        Activate(fleet);
        Report(fleet, ScopeContainerId, tickShareMs: 16f);

        var seen = new List<AdmissionRequest>();
        NebulaAdmission.Decide = (in AdmissionRequest r) =>
        {
            lock (seen) seen.Add(r);
            return r.Name == "staff" ? AdmissionDecision.Admit() : AdmissionDecision.Reject("the station is full; you are in the queue");
        };

        var stranger = fleet.Connect(0, "tourist", scope: Key);
        Assert.That(fleet.Run(() => stranger.Rejected != null, seconds: 10), Is.True);
        Assert.That(stranger.Rejected!.Value.Reason, Is.EqualTo("the station is full; you are in the queue"), "the game's own sentence reaches the player");
        Assert.That(stranger.Rejected!.Value.Code, Is.EqualTo(JoinRejectReason.AtCapacity));

        var staff = fleet.Connect(0, "staff", scope: Key);
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count > 0, seconds: 10), Is.True, "the hook can override the default refusal");
        Assert.That(fleet.Worker.Claims[0].Container, Is.EqualTo(ScopeContainer));
        Assert.That(staff.Rejected, Is.Null);

        lock (seen)
        {
            Assert.That(seen, Is.Not.Empty);
            var request = seen[0];
            Assert.That(request.Kind, Is.EqualTo(AdmissionKind.Join));
            Assert.That(request.ScopeKey, Is.EqualTo(Key), "the hook is told which interaction domain is full");
            Assert.That(request.Capacity.AtCapacity, Is.True);
            Assert.That(request.Capacity.Dominant, Is.EqualTo(CostComponent.Simulation), "and what it is full of");
            Assert.That(request.ClientId, Is.Not.EqualTo(0UL));
        }
    }

    [Test]
    public void TheHookCanHoldTheJoinInsteadAndTheClientIsPlacedWhenRoomAppears()
    {
        using var fleet = new Fleet(gateways: 1);
        Activate(fleet);
        Report(fleet, ScopeContainerId, tickShareMs: 16f);
        NebulaAdmission.Decide = (in AdmissionRequest r) => AdmissionDecision.Hold();

        var queued = fleet.Connect(0, "queued", scope: Key);
        Assert.That(fleet.Run(() => queued.JoinReason == JoinHoldReason.AtCapacity, seconds: 10), Is.True,
            "a docking queue is a hold, not a refusal: the client is told why it is waiting");
        Assert.That(queued.Disconnected, Is.False);
        Assert.That(fleet.Worker.Claims, Is.Empty);

        // The rush is over: the worker reports a quiet container and the held join completes with no reconnect.
        Report(fleet, ScopeContainerId, tickShareMs: 2f);
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count > 0, seconds: 10), Is.True);
        Assert.That(queued.Disconnected, Is.False, "and it never had to reconnect");
    }

    [Test]
    public void AClientIsPlacedNormallyWhileTheTargetHasRoomAndTheHookIsNotEvenAsked()
    {
        using var fleet = new Fleet(gateways: 1);
        Activate(fleet);
        Report(fleet, ScopeContainerId, tickShareMs: 3f);

        int asked = 0;
        NebulaAdmission.Decide = (in AdmissionRequest r) => { Interlocked.Increment(ref asked); return AdmissionDecision.Admit(); };

        var ordinary = fleet.Connect(0, "ordinary", scope: Key);
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count > 0, seconds: 10), Is.True);
        Assert.That(ordinary.Rejected, Is.Null);
        Assert.That(Volatile.Read(ref asked), Is.EqualTo(0), "a join into a target with room does not pay for the hook");
    }

    [Test]
    public void APublicClientPrefersAContainerWithRoomOverOneAtCapacity()
    {
        using var fleet = new Fleet(gateways: 1, world: f =>
        {
            f.Assign("c0", f.Workers[0].WorkerId);
        });
        // c0 is the only public container and it is full: with nowhere else to go, the arrival is refused rather
        // than pushed into it.
        var row = new ContainerCost { ContainerId = "c0", ScopeKey = "", WorkerId = fleet.Worker.WorkerId, TickShareMs = 16f };
        ContainerCost.Resolve(ref row, WorkerLoadTracker.TickPeriodMs, NebulaConfig.DefaultCostLinkBytesPerSec);
        var info = NebulaCapacity.Derive(row, 0.9f);
        fleet.Plane.SetContainerCapacity("c0", info.Saturation, info.Dominant, info.AtCapacity);

        var local = fleet.Connect(0, "local");
        Assert.That(fleet.Run(() => local.Rejected != null, seconds: 10), Is.True);
        Assert.That(local.Rejected!.Value.Code, Is.EqualTo(JoinRejectReason.AtCapacity));

        // Room appears in the same container; the next arrival is placed there.
        fleet.Plane.SetContainerCapacity("c0", 0.1f, CostComponent.Simulation, false);
        var later = fleet.Connect(0, "later");
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count > 0, seconds: 10), Is.True);
        Assert.That(later.Rejected, Is.Null);
    }

    [Test]
    public void ACapacitySignalNobodyHasReportedAdmitsEverythingAsItAlwaysDid()
    {
        using var fleet = new Fleet(gateways: 1);
        Activate(fleet);
        // No cost row: the lease carries no reading at all.
        Assert.That(fleet.Plane.FindLease(ScopeContainerId)!.HasCapacity, Is.False);
        Assert.That(fleet.Plane.ScopeCapacityOf(Key).Known, Is.False);

        var client = fleet.Connect(0, "client", scope: Key);
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count > 0, seconds: 10), Is.True,
            "a mesh without cost telemetry behaves exactly as it did before capacity limits existed");
        Assert.That(client.Rejected, Is.Null);
    }
}
