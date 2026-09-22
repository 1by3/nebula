using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The two operational scenarios of the scale and failure suite (<c>docs/scale-suite.md</c>, S8 and S9):
/// autoscale and rebalance under holds, and a rolling upgrade across the compatibility window.
/// <para>
/// The autoscale scenarios are pure — the planner and the scaler are ordinary classes — so they cost
/// milliseconds and carry <c>Scale</c> only; a developer can run them on every change. The rolling-upgrade
/// scenario stands up real gateways on real sockets, so it carries <c>Soak</c> as well and stays out of the
/// default <c>TestCategory!=Soak</c> run.
/// </para>
/// </summary>
[TestFixture]
[Category("Scale")]
public class ScaleOperationsTests
{
    private const float Span = 64f;

    private static List<Container> Row(int count)
    {
        var containers = new List<Container>();
        for (int i = 0; i < count; i++)
            containers.Add(new Container
            {
                ContainerId = "c" + i,
                Index = (ushort)i,
                Size = new Vector3(Span, Span, Span),
                transform = new ContainerFrame { position = new Vector3((i + 0.5f) * Span, 0, 0) },
            });
        return containers;
    }

    private static List<WorkerInfo> Workers(int n) => Enumerable.Range(1, n)
        .Select(i => new WorkerInfo { WorkerId = "w" + i, WorkerIndex = (uint)i, Status = WorkerStatus.Ready }).ToList();

    private static List<LeaseInfo> Leases(params (string Container, string Worker)[] rows) => rows
        .Select(r => new LeaseInfo { ContainerId = r.Container, WorkerId = r.Worker, State = r.Worker.Length == 0 ? LeaseState.Orphaned : LeaseState.Active, Epoch = 1 })
        .ToList();

    private static AssignmentInput Input(List<Container> containers, List<WorkerInfo> workers, List<LeaseInfo> leases,
        Dictionary<string, float>? utilization = null, Dictionary<string, float>? holds = null,
        IReadOnlyList<CohesionGroupInfo>? cohesion = null, Dictionary<string, ContainerCost>? cost = null) => new()
        {
            Baked = containers,
            Runtime = new List<Container>(),
            Eligible = workers,
            Leases = leases,
            Occupancy = containers.ToDictionary(c => c.ContainerId, _ => new ContainerLoad { Players = 1 }),
            Utilization = utilization ?? new Dictionary<string, float>(),
            Hints = new Dictionary<string, ContainerHint>(),
            Cohesion = cohesion ?? new List<CohesionGroupInfo>(),
            Holds = holds ?? new Dictionary<string, float>(),
            Cost = cost,
        };

    // -------------------------------------------------------------------------- S8: autoscale and rebalance

    /// <summary>
    /// The scenario the issue asks for, in three parts on one world: a rebalance moves containers while nothing
    /// is held; the same rebalance moves <b>nothing that is held</b>, including the rest of a cohesion group whose
    /// one held member would otherwise be split off; and a container that carries a whole tick on its own is
    /// reported as the reason growing the mesh would not help, naming what it is expensive in.
    /// <para>
    /// Everything is asserted on the typed reports NEB-235 added (<see cref="AssignmentMove"/>,
    /// <see cref="SaturationReport"/>, <see cref="ScaleDecision.BlockedCause"/>) rather than on prose, so a reworded
    /// sentence does not fail the suite and a changed <i>decision</i> does.
    /// </para>
    /// </summary>
    [Test]
    public void ARebalanceMovesNothingThatIsHeldAndASaturatedContainerIsReportedWithItsReason()
    {
        var report = new ScaleReport("autoscale-rebalance",
            "phase", "workers", "movesPlanned", "heldContainers", "unsplittableGroups", "action", "blockedBy",
            "blockedComponent", "blockedSaturation", "blockedCause", "blockedReason", "explanation");

        var containers = Row(4);
        var workers = Workers(2);
        var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w1"), ("c3", "w1")); // one worker holds everything
        var policy = new CostBalancedAssignmentPolicy();

        // ---- 1. a plain re-deal: the tail of the curve goes to the idle worker.
        var free = policy.Compute(Input(containers, workers, leases));
        foreach (var move in policy.Moves)
            report.Row("rebalance", workers.Count, free.Count, 0, policy.Unsplittable.Count, "-", move.ContainerId,
                "", 0.0, "", "", move.Reason);
        Assert.That(free.Select(m => m.Key), Does.Contain("c3"), "with nothing held the planner re-deals");
        Assert.That(policy.Moves.Select(m => m.ContainerId), Is.EquivalentTo(free.Select(m => m.Key)),
            "every applied change is explained (NEB-235)");
        Assert.That(policy.Moves.All(m => m.Reason.Length > 0), Is.True, "a built-in policy explains every move it makes");
        Assert.That(policy.Moves.All(m => m.To.Length > 0), Is.True, "and names the worker it is handing to");

        // ---- 2. the same re-deal with a hold on one member of a cohesion group.
        var holds = new Dictionary<string, float> { { "c2", 5f } };
        var cohesion = new List<CohesionGroupInfo> { NewGroup(7, "c2", "c3") };
        var held = policy.Compute(Input(containers, workers, leases, holds: holds, cohesion: cohesion));
        foreach (var move in policy.Moves)
            report.Row("rebalance-under-hold", workers.Count, held.Count, holds.Count, policy.Unsplittable.Count, "-",
                move.ContainerId, "", 0.0, "", "", move.Reason);
        Assert.That(held.Select(m => m.Key), Does.Not.Contain("c2"), "a held container is not moved");
        Assert.That(held.Select(m => m.Key), Does.Not.Contain("c3"),
            "moving the rest of the group while one member is held would be the split cohesion forbids");
        Assert.That(policy.Moves.Select(m => m.ContainerId), Does.Not.Contain("c2"), "and no move is reported for it either");
        Assert.That(policy.Moves.Select(m => m.ContainerId), Does.Not.Contain("c3"));

        // ---- 3. a container that cannot be split, with a cost row saying what it is expensive in.
        var busy = new Dictionary<string, float> { { "c0", 0.95f }, { "c1", 0.02f }, { "c2", 0.02f }, { "c3", 0.02f } };
        var row = new ContainerCost { ContainerId = "c0", WorkerId = "w1", TickShareMs = 15.8f, BytesOutPerSec = 2048, GatewayBytesPerSec = 512 };
        ContainerCost.Resolve(ref row, tickPeriodMs: 16.67f, linkBytesPerSec: 12_500_000);
        var cost = new Dictionary<string, ContainerCost> { { "c0", row } };
        var input = Input(containers, workers, Leases(("c0", "w1"), ("c1", "w2"), ("c2", "w2"), ("c3", "w2")), busy, cost: cost);
        var scaler = new WorkerScaler();
        var settings = ScaleSettings.Default;
        settings.HoldSeconds = 1f;
        settings.MaxWorkers = 8;
        // w1 carries c0 alone and is over the line; w2 carries the other three and is nearly idle. The first pass
        // starts the hold clock, the second is taken after the hold has elapsed.
        var measured = new Dictionary<string, float> { { "w1", 0.95f }, { "w2", 0.06f } };
        scaler.Evaluate(0.0, measured, input, policy, workers.Count, settings, inFlight: false);
        var decision = scaler.Evaluate(settings.HoldSeconds + 0.1, measured, input, policy, workers.Count, settings, inFlight: false);

        report.Row("saturated", workers.Count, 0, 0, policy.Unsplittable.Count, decision.Action.ToString(),
            decision.BlockedBy, decision.BlockedComponent.ToString(), decision.BlockedSaturation,
            SaturationReport.NameOf(decision.BlockedCause), decision.BlockedReason, decision.Reason);
        var saturated = policy.Saturated.FirstOrDefault(r => r.ContainerId == "c0");
        report.Note($"blocked by {decision.BlockedBy}: cause {decision.BlockedCause}, component {decision.BlockedComponent} " +
                    $"at {decision.BlockedSaturation:0.00} of its budget - {decision.BlockedReason}");
        report.Write();

        Assert.That(decision.Action, Is.EqualTo(ScaleAction.None), "another worker cannot help, so the mesh does not grow");
        Assert.That(decision.BlockedBy, Is.EqualTo("c0"), "the decision names the container that is saturated");
        Assert.That(decision.BlockedComponent, Is.EqualTo(CostComponent.Simulation), "and what it is expensive in (NEB-225)");
        Assert.That(decision.BlockedSaturation, Is.GreaterThan(0.5f), "and how much of that component's budget it uses");
        Assert.That(decision.BlockedCause, Is.EqualTo(SaturationCause.NoBoundary),
            "one container carrying a whole tick with no authored boundary inside it is the NoBoundary cause (NEB-235)");
        Assert.That(decision.BlockedReason, Is.Not.Empty, "the typed cause comes with a sentence an operator can act on");
        Assert.That(policy.Saturated, Is.Not.Empty, "the planner itself reports what it could not relieve");
        Assert.That(saturated.ContainerId, Is.EqualTo("c0"));
        Assert.That(saturated.WorkerId, Is.EqualTo("w1"), "and which worker is carrying it");
        Assert.That(saturated.Cause, Is.EqualTo(decision.BlockedCause), "the scaler reports the planner's cause unchanged");
        Assert.That(saturated.ScopeKey, Is.Empty, "this world is the public one, so the row groups under the empty scope");
    }

    /// <summary>A cohesion group that does not fit one worker is reported rather than split, at scale-suite scale.</summary>
    [Test]
    public void AnOversizeCohesionGroupIsReportedInsteadOfBeingSplit()
    {
        var containers = Row(6);
        var workers = Workers(3);
        var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w2"), ("c3", "w2"), ("c4", "w3"), ("c5", "w3"));
        var utilization = new Dictionary<string, float> { { "c0", 0.7f }, { "c1", 0.05f }, { "c2", 0.05f }, { "c3", 0.05f }, { "c4", 0.05f }, { "c5", 0.8f } };
        var policy = new CostBalancedAssignmentPolicy();

        var owners = leases.ToDictionary(l => l.ContainerId, l => l.WorkerId);
        foreach (var change in policy.Compute(Input(containers, workers, leases, utilization,
            cohesion: new List<CohesionGroupInfo> { NewGroup(11, "c0", "c5") }))) owners[change.Key] = change.Value;

        Assert.That(owners["c0"], Is.EqualTo(owners["c5"]), "an oversize group is still never split");
        Assert.That(policy.Unsplittable, Has.Count.EqualTo(1), "and it is reported");
        Assert.That(policy.Unsplittable[0].Group, Is.EqualTo("cohesion 11"));
        Assert.That(policy.Unsplittable[0].Utilization, Is.GreaterThan(policy.Unsplittable[0].Limit));
    }

    private static CohesionGroupInfo NewGroup(uint group, params string[] containers)
    {
        var info = new CohesionGroupInfo { Group = group, Members = containers.Length };
        info.Containers.AddRange(containers);
        info.Workers.Add("w1");
        return info;
    }

    // ------------------------------------------------------------------------------- S9: rolling upgrade

    /// <summary>
    /// <b>The compatibility window, at its edges.</b> A gateway admits a client anywhere in
    /// <see cref="HelloMsg.MinProtocolVersion"/>..<see cref="HelloMsg.ProtocolVersion"/> (N-1 and N) and refuses
    /// anything outside it with <see cref="JoinRejectReason.ProtocolUnsupported"/> and its own range, so the
    /// client can tell "update required" from "this server has not been upgraded yet"
    /// (<c>docs/compatibility-policy.md</c>). The scenario walks all four edges: the minimum, the current
    /// version, one below the minimum and one above the current.
    /// <para>
    /// Today <c>MinProtocolVersion == ProtocolVersion == 18</c>, because 18 is the floor the policy starts from:
    /// the two admitted cases are the same version and "one below the minimum" is 17. The test is written
    /// against the constants, so bumping the protocol to 19 (min 18) turns the first row into a genuine N-1
    /// admission with nothing to edit here. The recorded-stream half of that guarantee is the conformance test
    /// <c>ConformanceProtocolCompatibilityTests</c>.
    /// </para>
    /// <para>
    /// The other half of a rolling upgrade — replacing processes under connected clients — is
    /// <c>RollingUpgradeTests</c> (S9a and S9b); this scenario keeps the version half and checks that a refused
    /// peer disturbs nobody.
    /// </para>
    /// </summary>
    [Test]
    [Category("Soak")]
    public void TheCompatibilityWindowAdmitsItsEdgesAndRefusesWhatIsOutsideItWithAReason()
    {
        var report = new ScaleReport("rolling-upgrade", "case", "version", "admitted", "welcomed", "refused", "code", "note");
        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 1, workers: 1, world: f => f.Assign("c0", f.Workers[0].WorkerId));
        fleet.Worker.SpawnIntoRequestedContainer = true;

        // Inside the window: the oldest version this gateway admits, and the one it speaks itself.
        foreach (ushort version in new[] { HelloMsg.MinProtocolVersion, HelloMsg.ProtocolVersion })
        {
            var client = fleet.Connect(0, "build-" + version);
            client.AnnounceVersion = version;
            bool joined = fleet.Run(() => client.Join == JoinState.Joined, seconds: 30);
            report.Row(version == HelloMsg.MinProtocolVersion ? "window minimum" : "current version", version,
                joined ? 1 : 0, client.Welcome != null ? 1 : 0, client.Rejected != null ? 1 : 0,
                client.Rejected?.Code ?? JoinRejectReason.None, "admitted and welcomed");
            Assert.That(joined, Is.True, $"protocol {version} is inside the window and was not admitted");
            Assert.That(client.Rejected, Is.Null);
            Assert.That(client.Welcome!.Value.NegotiatedVersion, Is.EqualTo(version),
                "the gateway records the negotiated version on the session and tells the client which it was");
        }

        // Outside it, in both directions: refused with a reason the client can act on, not a silent disconnect.
        foreach (ushort version in new[] { (ushort)(HelloMsg.MinProtocolVersion - 1), (ushort)(HelloMsg.ProtocolVersion + 1) })
        {
            var client = fleet.Connect(0, "build-" + version);
            client.AnnounceVersion = version;
            bool refused = fleet.Run(() => client.Rejected != null, seconds: 20);
            report.Row(version < HelloMsg.MinProtocolVersion ? "below the window" : "above the window", version,
                0, client.Welcome != null ? 1 : 0, refused ? 1 : 0, client.Rejected?.Code ?? JoinRejectReason.None,
                version < HelloMsg.MinProtocolVersion ? "the player must update" : "the server must be upgraded");
            Assert.That(refused, Is.True, $"protocol {version} was not refused with a message");
            Assert.That(client.Rejected!.Value.Code, Is.EqualTo(JoinRejectReason.ProtocolUnsupported));
            Assert.That(client.Rejected!.Value.Retry, Is.False, "retrying the same build would get the same answer");
            Assert.That(client.Rejected!.Value.SupportedMinVersion, Is.EqualTo(HelloMsg.MinProtocolVersion));
            Assert.That(client.Rejected!.Value.SupportedMaxVersion, Is.EqualTo(HelloMsg.ProtocolVersion));
            Assert.That(client.Welcome, Is.Null);
        }

        Assert.That(fleet.Clients[1].Disconnected, Is.False, "refusing a peer outside the window must not disturb the peers inside it");
        Assert.That(fleet.Worker.Claims, Has.Count.EqualTo(2), "a refused peer never reaches a worker");
        report.Note($"The window is {ProtocolCompatibility.WindowText()} (MinProtocolVersion..ProtocolVersion). " +
                    "Gateway-to-worker and worker-to-worker links are an exact match instead. Drain-and-replace " +
                    "is S9a and S9b in RollingUpgradeTests. See docs/compatibility-policy.md.");
        report.Write();
    }
}
