using System.Diagnostics;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The failure half of the scale and failure suite (<c>docs/scale-suite.md</c>, scenarios S4–S7): a worker dies,
/// a gateway is lost without a drain, the control plane restarts under the mesh, and the whole mesh comes back
/// container by container. Each one measures a time against a stated threshold and writes its numbers to
/// <c>Logs/scale/synthetic-*.csv</c>.
/// <para>
/// <b>Synthetic layer (tier A).</b> The processes here are objects: killing a worker means dropping its socket
/// and its control-plane registration, killing a gateway means the process stops answering. That is exactly what
/// the rest of the mesh can observe of a real kill, so what these tests measure — how long the survivors take to
/// notice, what they do about it, and what a client sees meanwhile — is real. What it is <b>not</b> is the cost
/// of a Unity worker loading a scene back in, which is tier D (<c>Tools/scale-suite.ps1</c>).
/// </para>
/// </summary>
[TestFixture]
[Category("Scale")]
[Category("Soak")]
public class ScaleFailureTests
{
    private const int Cells = 4;
    private const int SceneryPerCell = 40;

    private ScaleWorld _world = null!;

    [SetUp] public void Setup() => _world = new ScaleWorld(Cells);
    [TearDown] public void Cleanup() => _world.Dispose();

    /// <summary>One piece of scenery as the mesh would have persisted it: id, where it is, and in what.</summary>
    private readonly record struct Record(ulong NetId, string ContainerId, Vector3 Local);

    private readonly Dictionary<string, List<Record>> _contents = new();

    /// <summary>Fill every cell with scenery and remember it, so a replacement worker can restore exactly it.</summary>
    private void Populate(Fleet fleet)
    {
        _contents.Clear();
        for (int i = 0; i < _world.Cells.Count; i++)
        {
            string id = _world.Cells[i];
            var worker = fleet.Workers[i % fleet.Workers.Count];
            var container = ContainerRef.Of(ContainerRegistry.FindById(id));
            var random = new Random(i * 7919);
            var records = new List<Record>();
            for (int n = 0; n < SceneryPerCell; n++)
            {
                var local = new Vector3((float)(random.NextDouble() - 0.5) * ScaleWorld.CellSize, 0,
                    (float)(random.NextDouble() - 0.5) * ScaleWorld.CellSize);
                ulong netId = 500_000UL + (ulong)i * 10_000UL + (ulong)n;
                worker.Spawn(netId, local, container);
                records.Add(new Record(netId, id, local));
            }
            _contents[id] = records;
        }
    }

    /// <summary>
    /// Restore a container onto a worker: take the lease and put back every record it held, under the same net
    /// ids. This stands in for what a real worker does when the orchestrator hands it an orphaned container — it
    /// reads the container's persisted records and spawns them (the store leg itself is covered by conformance
    /// scenarios 3 and 11; what is under test here is what the gateways and clients do while it happens).
    /// </summary>
    private void Restore(Fleet fleet, FakeWorker worker, string containerId)
    {
        fleet.Assign(containerId, worker.WorkerId);
        var container = ContainerRef.Of(ContainerRegistry.FindById(containerId));
        foreach (var record in _contents[containerId]) worker.Spawn(record.NetId, record.Local, container);
    }

    /// <summary>Which cell each client is watching. See <see cref="Watch"/>.</summary>
    private readonly Dictionary<FakeClient, int> _watching = new();

    /// <summary>
    /// Point every client's interest at the cell it was given, in world coordinates, the way a real client tells
    /// the gateway where its camera is looking. Without this the measurement would be circular: a client only
    /// sees a container it is near, so "did the container come back" would be answered by wherever the gateway
    /// happened to place that client's pawn after the failure rather than by the restore. A hint is a standing
    /// request, so it is re-sent after anything that could have reset the gateway's view of this client.
    /// </summary>
    private byte _hintGeneration;

    private void Watch(Fleet fleet)
    {
        _hintGeneration++;
        foreach (var kv in _watching)
        {
            if (kv.Key.Welcome == null) continue;
            // A hint is clamped to InterestSettings.HintMaxDistance from the pawn unless the server has decided
            // otherwise, and is refused outright while the client has no pawn at all — which is exactly the state
            // a worker kill leaves its players in. FocusMode.Free is that server-side decision (an observer
            // camera); it is the gateway's call to make, never the client's, so the fixture makes it as a game
            // would. Without it this scenario could not observe a container it does not stand in.
            foreach (var gw in fleet.Gateways) gw.SetClientFocusMode(kv.Key.Welcome.Value.ClientId, FocusMode.Free);
            var cell = ContainerRegistry.FindById(_world.Cells[kv.Value]);
            kv.Key.SendFocusHint(cell!.ToWorld(Vector3.zero), _hintGeneration);
        }
        fleet.RunFor(0.3); // the hint has to arrive and be evaluated before anything is read back
    }

    /// <summary>The clients watching one cell: who should be able to tell whether it is back.</summary>
    private List<FakeClient> Watchers(string containerId) =>
        _watching.Where(kv => _world.Cells[kv.Value] == containerId).Select(kv => kv.Key).ToList();

    private Fleet Build(int gateways, int workers, int clients, out List<FakeClient> connected)
    {
        var fleet = new Fleet(gateways, workers: workers, world: _ => { });
        _world.Deal(fleet);
        foreach (var w in fleet.Workers) w.SpawnIntoRequestedContainer = true;
        Populate(fleet);
        fleet.OnPump = () => { foreach (var w in fleet.Workers) w.PublishStates(); };
        connected = new List<FakeClient>();
        _watching.Clear();
        for (int i = 0; i < clients; i++)
        {
            var c = fleet.Connect(i % gateways, "p" + i);
            connected.Add(c);
            _watching[c] = i % _world.Cells.Count;
        }
        Assert.That(fleet.Run(() => fleet.Clients.All(c => c.Join == JoinState.Joined), seconds: 60), Is.True,
            "the mesh did not admit every client before the failure was injected");
        Watch(fleet);
        fleet.RunFor(2.0);
        foreach (string id in _world.Cells)
        {
            var ids = _contents[id].Select(r => r.NetId).ToHashSet();
            Assert.That(Watchers(id).Sum(c => c.Replicas.Count(ids.Contains)), Is.GreaterThan(0),
                id + " was not visible to the clients watching it, so nothing about it can be measured");
        }
        return fleet;
    }

    // --------------------------------------------------------------------------------------- S4: worker kill

    [Test]
    public void AWorkerKillOrphansItsContainersAndTheRestoreBringsThemBackWithNoDuplicateEntities()
    {
        var report = new ScaleReport("worker-kill",
            "containersOrphaned", "clients", "noticedAfterSeconds", "restoredAfterSeconds", "secondsPerContainer",
            "duplicateSpawns", "orphanUpdates", "pawnsAfter", "replicasRestored");

        using var fleet = Build(gateways: 2, workers: Cells, clients: 40, out var clients);
        var victim = fleet.Workers[Cells - 1];
        var survivor = fleet.Workers[0];
        string dyingCell = _world.Cells[Cells - 1];
        var lost = _contents[dyingCell].Select(r => r.NetId).ToHashSet();
        var watchers = Watchers(dyingCell);
        int heldBefore = watchers.Sum(c => c.Replicas.Count(lost.Contains));
        Assert.That(heldBefore, Is.GreaterThan(0), "no client could see the dying worker's entities, so nothing is being measured");

        var clock = Stopwatch.StartNew();
        var orphaned = fleet.KillWorker(victim);
        Assert.That(orphaned, Is.EquivalentTo(new[] { _world.Cells[Cells - 1] }), "the kill orphaned exactly the leases it held");

        // The gateways notice by losing the link and drop what that worker owned; nothing else is disturbed.
        Assert.That(fleet.Run(() => clients.All(c => c.Replicas.All(id => !lost.Contains(id))), seconds: 30), Is.True,
            "a client still holds a replica of an entity nobody owns");
        double noticed = clock.Elapsed.TotalSeconds;
        Assert.That(clients.All(c => !c.Disconnected), Is.True, "a worker death must not disconnect a client");

        foreach (string containerId in orphaned) Restore(fleet, survivor, containerId);
        Watch(fleet);
        Assert.That(fleet.Run(() => clients.All(c => c.Join == JoinState.Joined) &&
            watchers.Sum(c => c.Replicas.Count(lost.Contains)) >= heldBefore, seconds: 60), Is.True,
            $"the restored container's entities did not come back: {watchers.Sum(c => c.Replicas.Count(lost.Contains))}/{heldBefore}");
        double restored = clock.Elapsed.TotalSeconds;

        int duplicates = clients.Sum(c => c.DuplicateSpawns), orphanUpdates = clients.Sum(c => c.OrphanUpdates);
        int pawns = fleet.Workers.Sum(w => w.Pawns.Count);
        report.Row(orphaned.Count, clients.Count, noticed, restored, restored / Math.Max(1, orphaned.Count),
            duplicates, orphanUpdates, pawns, watchers.Sum(c => c.Replicas.Count(lost.Contains)));
        report.Note($"{orphaned.Count} container(s) orphaned, noticed in {noticed:0.00} s, whole again in {restored:0.00} s");
        report.Write();

        Assert.That(restored / Math.Max(1, orphaned.Count), Is.LessThan(ScaleThresholds.RestoreSecondsPerContainer),
            "the restore took longer than the per-container budget");
        Assert.That(duplicates, Is.Zero, "a client was given a second live view of an entity it already held");
        Assert.That(pawns, Is.EqualTo(clients.Count), "one pawn per client after the restore: no duplicate players");
        foreach (var c in clients)
            Assert.That(c.Replicas.Count, Is.EqualTo(c.Replicas.Distinct().Count()), "replicas are a set; a duplicate would be a bug in the fixture");
    }

    // -------------------------------------------------------------------------------------- S5: gateway loss

    [Test]
    public void AnAbruptGatewayStopLetsEverySessionBeReclaimedOnAnotherGateway()
    {
        var report = new ScaleReport("gateway-stop", "kind", "clients", "reclaimed", "reclaimRate", "reclaimSeconds", "sameSessionIds");

        using var fleet = Build(gateways: 2, workers: 2, clients: 24, out var clients);
        var onDoomed = clients.Where((_, i) => i % 2 == 0).ToList();
        var identities = onDoomed.ToDictionary(c => c, c => c.Welcome!.Value);

        // Stopped, not crashed: the socket closes and the coordinator's claims are released, but nobody was told
        // to drain and no client was given time to reconnect first.
        fleet.KillGateway(0, hard: false);
        Assert.That(fleet.Run(() => onDoomed.All(c => c.Disconnected), seconds: 20), Is.True, "the stopped gateway's clients were not dropped");

        var clock = Stopwatch.StartNew();
        var back = onDoomed.Select(c => fleet.Connect(1, "reclaim", identities[c].Token, identities[c].SessionToken)).ToList();
        bool all = fleet.Run(() => back.All(c => c.Join == JoinState.Joined), seconds: 30);
        double seconds = clock.Elapsed.TotalSeconds;

        int reclaimed = back.Count(c => c.Welcome is { Reclaimed: true });
        int same = back.Where(c => c.Welcome != null).Select((c, i) => c.Welcome!.Value.ClientId == identities[onDoomed[i]].ClientId).Count(x => x);
        report.Row("abrupt-stop", onDoomed.Count, reclaimed, (double)reclaimed / onDoomed.Count, seconds, same);
        report.Note($"{reclaimed}/{onDoomed.Count} sessions reclaimed on the surviving gateway in {seconds:0.00} s");
        report.Write();

        Assert.That(all, Is.True, "not every client of the stopped gateway got back in");
        Assert.That(reclaimed, Is.EqualTo(onDoomed.Count), "every reclaim kept the session it had");
        Assert.That(same, Is.EqualTo(onDoomed.Count), "a reclaim must return the same session id");
        Assert.That(seconds, Is.LessThan(ScaleThresholds.GatewayReclaimSeconds), "the reclaim took longer than the budget");
        Assert.That(clients.Except(onDoomed).All(c => !c.Disconnected), Is.True, "the surviving gateway's clients were untouched");
    }

    [Test]
    public void AGatewayThatIsKilledOutrightHasItsSessionsReclaimedOnceItsHeartbeatGoesStale()
    {
        // D7a closed (NEB-229): GatewaySessionDirectory now treats a gateway whose control-plane heartbeat has
        // gone stale (GatewayStaleAfterSeconds, LocalControlPlane) as gone, and evicts its claims for a waiting
        // claimant instead of holding them until something releases them (which a killed process never does).
        // The measured number below is what sets ScaleThresholds.HardKillGatewayReclaimSeconds
        // (docs/scale-suite.md D2/D6); see docs/gateway-fleet-audit.md finding 1.
        var report = new ScaleReport("gateway-kill", "kind", "clients", "reclaimed", "reclaimRate", "secondsWaited");

        using var fleet = Build(gateways: 2, workers: 2, clients: 8, out var clients);
        var onDoomed = clients.Where((_, i) => i % 2 == 0).ToList();
        var identities = onDoomed.ToDictionary(c => c, c => c.Welcome!.Value);

        // A killed process: it stops answering, and it never gets to release the session claims it holds.
        fleet.KillGateway(0, hard: true);

        var clock = Stopwatch.StartNew();
        var back = onDoomed.Select(c => fleet.Connect(1, "reclaim", identities[c].Token, identities[c].SessionToken)).ToList();
        bool settled = fleet.Run(() => back.All(c => c.Join == JoinState.Joined || c.Rejected != null), seconds: 40);
        double seconds = clock.Elapsed.TotalSeconds;

        int reclaimed = back.Count(c => c.Join == JoinState.Joined);
        report.Row("hard-kill", onDoomed.Count, reclaimed, (double)reclaimed / onDoomed.Count, seconds);
        report.Note($"a hard gateway kill now reclaims every session in {seconds:0.00} s once the owning gateway's " +
                    "control-plane heartbeat is stale (GatewaySessionDirectory / GatewayStaleAfterSeconds, D7a). " +
                    "ScaleThresholds.HardKillGatewayReclaimSeconds is set from this row.");
        report.Write();

        Assert.That(settled, Is.True, "the reclaim attempts neither completed nor were refused within 40 s");
        Assert.That(reclaimed, Is.EqualTo(onDoomed.Count), "a hard gateway kill must lose no session: D7a is closed");
        Assert.That(seconds, Is.LessThanOrEqualTo(ScaleThresholds.HardKillGatewayReclaimSeconds),
            $"hard-kill reclaim took {seconds:0.00} s, over the {ScaleThresholds.HardKillGatewayReclaimSeconds} s threshold derived in docs/scale-suite.md D2");
    }

    // --------------------------------------------------------------------------- S6: control-plane restart

    [Test]
    public void AControlPlaneRestartKeepsEveryLeaseAndDisconnectsNobody()
    {
        var report = new ScaleReport("control-plane-restart",
            "leasesBefore", "leasesAfter", "stallSeconds", "clients", "disconnected", "sessionsChanged", "duplicateSpawns");

        using var fleet = Build(gateways: 2, workers: Cells, clients: 24, out var clients);
        var sessionsBefore = clients.ToDictionary(c => c, c => c.Welcome!.Value.ClientId);
        var leasesBefore = fleet.Plane.Leases.ToDictionary(l => l.ContainerId, l => (l.WorkerId, l.State, l.Epoch));
        string snapshot = fleet.Plane.ToJson(); // what the orchestrator's storage holds when it goes down

        var clock = Stopwatch.StartNew();
        fleet.RestartControlPlane(snapshot);
        Assert.That(fleet.Run(() => fleet.Plane.Leases.Count == leasesBefore.Count, seconds: 20), Is.True,
            "the leases did not come back");
        double stall = clock.Elapsed.TotalSeconds;
        fleet.RunFor(2.0); // let the gateways re-register and re-read ownership

        var leasesAfter = fleet.Plane.Leases.ToDictionary(l => l.ContainerId, l => (l.WorkerId, l.State, l.Epoch));
        int changed = clients.Count(c => c.Welcome!.Value.ClientId != sessionsBefore[c]);
        report.Row(leasesBefore.Count, leasesAfter.Count, stall, clients.Count,
            clients.Count(c => c.Disconnected), changed, clients.Sum(c => c.DuplicateSpawns));
        report.Note($"{leasesAfter.Count} leases restored in {stall:0.000} s with no client disconnected");
        report.Write();

        Assert.That(leasesAfter, Is.EqualTo(leasesBefore), "every lease came back with the same owner, state and epoch");
        Assert.That(stall, Is.LessThan(ScaleThresholds.ControlPlaneStallSeconds), "the mesh was without leases for too long");
        Assert.That(clients.All(c => !c.Disconnected), Is.True, "a control-plane restart must not disconnect a client");
        Assert.That(changed, Is.Zero, "a control-plane restart must not change a session id");
        Assert.That(fleet.Run(() => fleet.Plane.Gateways.Count == 2, seconds: 20), Is.True,
            "both gateways re-registered after the restart");
    }

    [Test]
    public void ARestartThatCameBackEmptyReclaimsItsGatewaysButNotItsWorkers()
    {
        var report = new ScaleReport("control-plane-cold-restart", "workersKnown", "gatewaysKnown", "leases", "clientsDisconnected");

        using var fleet = Build(gateways: 1, workers: 2, clients: 8, out var clients);
        fleet.RestartControlPlane(null); // storage lost everything, or was reset
        fleet.RunFor(3.0);

        report.Row(fleet.Plane.Workers.Count, fleet.Plane.Gateways.Count, fleet.Plane.Leases.Count,
            clients.Count(c => c.Disconnected));
        report.Note("a gateway re-registers itself when the control plane forgets it (NebulaGateway.OnControlPlaneChanged); " +
                    "a worker does not — NebulaWorker registers once and never again — so a control plane that comes back " +
                    "with nothing has no workers and no leases until every worker process is restarted. Durable " +
                    "control-plane state is NEB-227; this row is what happens without it.");
        report.Write();

        Assert.That(fleet.Plane.Gateways.Count, Is.EqualTo(1), "the gateway put itself back on the control plane");
        Assert.That(fleet.Plane.Workers, Is.Empty,
            "a worker now re-registers after a cold control-plane restart — update docs/scale-suite.md D7b and this test");
        Assert.That(fleet.Plane.Leases, Is.Empty, "no worker means no leases");
        Assert.That(clients.All(c => !c.Disconnected), Is.True, "and still nobody was disconnected");
    }

    // -------------------------------------------------------------------------------- S7: whole-mesh restart

    [Test]
    public void AWholeMeshRestartBringsTheContainersBackOneAtATimeAndTheCurveIsRecorded()
    {
        var report = new ScaleReport("mesh-restart", "step", "containerId", "worker", "secondsFromRestartStart", "clientsHoldingItsEntities");

        using var fleet = Build(gateways: 2, workers: Cells, clients: 24, out var clients);
        var expected = _world.Cells.ToDictionary(id => id, id => _contents[id].Select(r => r.NetId).ToHashSet());


        // Every worker dies. The gateways and the clients stay up, which is what makes this a mesh restart and
        // not a client restart: the measurement is how long a connected player waits for the world to come back.
        foreach (var w in fleet.Workers.ToList()) fleet.KillWorker(w);
        Assert.That(fleet.Run(() => clients.All(c => c.Replicas.Count == 0), seconds: 30), Is.True,
            "the clients still hold entities although no worker is alive");
        Assert.That(clients.All(c => !c.Disconnected), Is.True, "a whole-worker-fleet loss must not disconnect a client");

        var clock = Stopwatch.StartNew();
        var curve = new List<(string Container, double Seconds)>();
        for (int i = 0; i < _world.Cells.Count; i++)
        {
            string id = _world.Cells[i];
            var worker = fleet.StartWorker();
            worker.SpawnIntoRequestedContainer = true;
            Restore(fleet, worker, id);
            Watch(fleet);
            var watchers = Watchers(id);
            Assert.That(fleet.Run(() => watchers.Sum(c => c.Replicas.Count(expected[id].Contains)) > 0, seconds: 45), Is.True,
                id + " did not come back to the clients watching it");
            double at = clock.Elapsed.TotalSeconds;
            curve.Add((id, at));
            report.Row(i + 1, id, worker.WorkerId, at, watchers.Count(c => c.Replicas.Any(expected[id].Contains)));
        }
        Assert.That(fleet.Run(() => clients.All(c => c.Join == JoinState.Joined), seconds: 60), Is.True,
            "not every player was given a pawn again once the mesh was back");
        report.Note($"{curve.Count} containers back in {curve[^1].Seconds:0.00} s ({curve[^1].Seconds / curve.Count:0.00} s each)");
        report.Write();

        ScaleBaseline.CompareCurve("mesh-restart", curve, ScaleThresholds.RestoreSecondsPerContainer);
        Assert.That(clients.All(c => !c.Disconnected), Is.True);
        Assert.That(fleet.Workers.Sum(w => w.Pawns.Count), Is.EqualTo(clients.Count), "one pawn per player after the restart");
    }
}
