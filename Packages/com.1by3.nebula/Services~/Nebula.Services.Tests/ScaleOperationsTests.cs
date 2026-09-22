using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The two operational scenarios of the scale and failure suite (<c>docs/scale-suite.md</c>, D8 and D9):
/// autoscale and rebalance under holds, and a rolling upgrade across the compatibility window.
/// <para>
/// Both are fast and deterministic — the planner, the scaler and the version check are pure code, so nothing here
/// runs a mesh for seconds. They are tagged <c>Scale</c> but <b>not</b> <c>Soak</c>: they cost milliseconds and a
/// developer can run them on every change.
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

    // -------------------------------------------------------------------------- D8: autoscale and rebalance

    /// <summary>
    /// The scenario the issue asks for, in three parts on one world: a rebalance moves containers while nothing
    /// is held; the same rebalance moves <b>nothing that is held</b>, including the rest of a cohesion group whose
    /// one held member would otherwise be split off; and a container that carries a whole tick on its own is
    /// reported as the reason growing the mesh would not help, naming what it is expensive in.
    /// <para>
    /// <b>Extension point for NEB-235.</b> That issue adds explained moves (a reason per move) and a typed
    /// <c>Saturated</c> report in place of reading <see cref="ScaleDecision.Reason"/> as prose. When it lands, the
    /// three <c>ExtensionPoint</c> comments below are where the assertions become assertions on those types; the
    /// scenario itself — who moves, who does not, and what is reported — does not change.
    /// </para>
    /// </summary>
    [Test]
    public void ARebalanceMovesNothingThatIsHeldAndASaturatedContainerIsReportedWithItsReason()
    {
        var report = new ScaleReport("autoscale-rebalance",
            "phase", "workers", "movesPlanned", "heldContainers", "unsplittableGroups", "action", "blockedBy",
            "blockedComponent", "blockedSaturation", "reason");

        var containers = Row(4);
        var workers = Workers(2);
        var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w1"), ("c3", "w1")); // one worker holds everything
        var policy = new CostBalancedAssignmentPolicy();

        // ---- 1. a plain re-deal: the tail of the curve goes to the idle worker.
        var free = policy.Compute(Input(containers, workers, leases));
        report.Row("rebalance", workers.Count, free.Count, 0, policy.Unsplittable.Count, "-", "", "", 0.0, policy.Note);
        Assert.That(free.Select(m => m.Key), Does.Contain("c3"), "with nothing held the planner re-deals");
        // ExtensionPoint(NEB-235): each of these moves will carry its own explanation; assert that here.

        // ---- 2. the same re-deal with a hold on one member of a cohesion group.
        var holds = new Dictionary<string, float> { { "c2", 5f } };
        var cohesion = new List<CohesionGroupInfo> { NewGroup(7, "c2", "c3") };
        var held = policy.Compute(Input(containers, workers, leases, holds: holds, cohesion: cohesion));
        report.Row("rebalance-under-hold", workers.Count, held.Count, holds.Count, policy.Unsplittable.Count, "-", "", "", 0.0, policy.Note);
        Assert.That(held.Select(m => m.Key), Does.Not.Contain("c2"), "a held container is not moved");
        Assert.That(held.Select(m => m.Key), Does.Not.Contain("c3"),
            "moving the rest of the group while one member is held would be the split cohesion forbids");
        // ExtensionPoint(NEB-235): the re-deal along the boundary must still honour the hold; assert the reason
        // the planner gives for leaving the group where it is.

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
            decision.BlockedBy, decision.BlockedComponent.ToString(), decision.BlockedSaturation, decision.Reason);
        report.Note($"blocked by {decision.BlockedBy} ({decision.BlockedComponent}, {decision.BlockedSaturation:0.00} of its budget): {decision.Reason}");
        report.Write();

        Assert.That(decision.Action, Is.EqualTo(ScaleAction.None), "another worker cannot help, so the mesh does not grow");
        Assert.That(decision.BlockedBy, Is.EqualTo("c0"), "the report names the container that is saturated");
        Assert.That(decision.BlockedComponent, Is.EqualTo(CostComponent.Simulation), "and what it is expensive in (NEB-225)");
        Assert.That(decision.BlockedSaturation, Is.GreaterThan(0.5f), "and how much of that component's budget it uses");
        Assert.That(decision.Reason, Does.Contain("cannot be split"));
        Assert.That(decision.Reason, Does.Contain("mostly simulation"));
        // ExtensionPoint(NEB-235): replace the prose assertions above with the typed `Saturated` report when it
        // lands; `BlockedBy`/`BlockedComponent`/`BlockedSaturation` are already typed and stay as they are.
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

    // ------------------------------------------------------------------------------- D9: rolling upgrade

    /// <summary>
    /// <b>There is no compatibility window.</b> <c>NebulaGateway.DispatchClient</c> compares the peer's
    /// <c>HelloMsg.Version</c> to <see cref="HelloMsg.ProtocolVersion"/> for exact equality and disconnects on any
    /// difference — no minimum version, no negotiation, nothing that reads N-1. <c>HelloMsg.Write</c> does not even
    /// serialise the instance's <c>Version</c> field: it always writes the constant, so a peer of this build
    /// <i>cannot</i> announce an older protocol. A rolling upgrade across protocol versions is therefore not
    /// possible today, and what this scenario tests instead — as the issue's guidance says to — is that a mismatch
    /// is refused cleanly: the link is closed, no session is created and nothing on the gateway is left half-built.
    /// <para>
    /// The other half of a rolling upgrade <i>is</i> possible and is tested here too: replacing a gateway process
    /// while clients are connected, at the same protocol version, without anybody losing a session.
    /// </para>
    /// </summary>
    [Test]
    public void AProtocolMismatchIsRefusedCleanlyAndTheSameVersionRollsForward()
    {
        var report = new ScaleReport("rolling-upgrade", "case", "version", "admitted", "welcomed", "disconnected", "note");
        using var world = new ScaleWorld(1);
        using var fleet = new Fleet(gateways: 2, workers: 1, world: f => f.Assign("c0", f.Workers[0].WorkerId));
        fleet.Worker.SpawnIntoRequestedContainer = true;

        // A client of this build: the current version, admitted.
        var current = fleet.Connect(0, "current");
        Assert.That(fleet.Run(() => current.Join == JoinState.Joined), Is.True);
        report.Row("same version", HelloMsg.ProtocolVersion, 1, 1, 0, "admitted");

        // A client of the previous build. Its Hello has to be written by hand, because this build's writer cannot
        // produce one: that is the finding, not a shortcut.
        foreach (ushort version in new ushort[] { (ushort)(HelloMsg.ProtocolVersion - 1), (ushort)(HelloMsg.ProtocolVersion + 1) })
        {
            using var old = new RawClient(fleet.Ports[0], version, "old-build");
            bool closed = fleet.Run(() => old.Poll(), seconds: 15);
            report.Row(version < HelloMsg.ProtocolVersion ? "one behind" : "one ahead", version, 0, old.Welcomed ? 1 : 0, closed ? 1 : 0,
                "disconnected without a reason message");
            Assert.That(closed, Is.True, $"protocol {version} was not disconnected");
            Assert.That(old.Welcomed, Is.False, $"protocol {version} was welcomed");
        }
        Assert.That(current.Disconnected, Is.False, "refusing a mismatched peer must not disturb the peers of this version");
        Assert.That(fleet.Worker.Claims, Has.Count.EqualTo(1), "a refused peer never reaches a worker");

        // The half of a rolling upgrade that does work: a gateway process is replaced under a connected fleet.
        var welcome = current.Welcome!.Value;
        fleet.KillGateway(0, hard: false);
        Assert.That(fleet.Run(() => current.Disconnected, seconds: 20), Is.True);
        int replacement = fleet.StartGateway();
        var moved = fleet.Connect(replacement, "current", welcome.Token, welcome.SessionToken);
        Assert.That(fleet.Run(() => moved.Join == JoinState.Joined, seconds: 30), Is.True, "the client did not land on the new gateway");
        Assert.That(moved.Welcome!.Value.ClientId, Is.EqualTo(welcome.ClientId), "and it kept its session across the replacement");
        report.Row("gateway replaced", HelloMsg.ProtocolVersion, 1, 1, 0, "session kept across the replacement");
        report.Note("no compatibility window exists: HelloMsg.Write always writes the ProtocolVersion constant and the " +
                    "gateway requires exact equality, so a rolling upgrade may replace processes at one protocol version " +
                    "but may not span two. See docs/scale-suite.md D9.");
        report.Write();
    }
}

/// <summary>
/// A client that says whatever protocol version it is told to. <see cref="HelloMsg"/> cannot: its writer hard-codes
/// <see cref="HelloMsg.ProtocolVersion"/>, so the bytes are laid out by hand here, in the order
/// <c>HelloMsg.Write</c> lays them out. It reads nothing but the fact of being welcomed or closed.
/// </summary>
internal sealed class RawClient : IDisposable
{
    private readonly LiteNetTransport _transport = new("raw-client");
    private readonly int _peer;
    private readonly ushort _version;
    private readonly string _name;
    private bool _sent;

    public bool Welcomed, Closed;

    public RawClient(int port, ushort version, string name)
    {
        _version = version;
        _name = name;
        _peer = _transport.Connect("127.0.0.1", port);
    }

    /// <summary>Pump once; true once the gateway has closed the link.</summary>
    public bool Poll()
    {
        _transport.Poll(e =>
        {
            if (e.Type == TransportEvent.Kind.Connected && !_sent)
            {
                _sent = true;
                var w = new NetworkWriter();
                w.WriteByte((byte)MsgId.Hello);
                w.WriteUShort(_version);
                w.WriteByte((byte)PeerRole.Client);
                w.WriteString(_name);
                w.WriteUInt(0);            // index
                w.WriteByte(0);            // flags
                w.WriteString("");         // token
                w.WriteString("");         // session
                w.WriteUInt(0);            // incarnation
                w.WriteString("");         // scope key
                _transport.Send(e.PeerId, Delivery.ReliableOrdered, w.ToSegment());
            }
            else if (e.Type == TransportEvent.Kind.Disconnected) Closed = true;
            else if (e.Type == TransportEvent.Kind.Data && e.Data.Count > 0 && e.Data.Array![e.Data.Offset] == (byte)MsgId.Welcome) Welcomed = true;
        });
        _transport.Flush();
        return Closed;
    }

    public void Dispose() => _transport.Dispose();
}
