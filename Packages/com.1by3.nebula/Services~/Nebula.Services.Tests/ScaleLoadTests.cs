using System.Diagnostics;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The load half of the scale and failure suite (<c>docs/scale-suite.md</c>, scenarios D1–D3): sustained load,
/// a burst, and many scopes at once, with explicit bounds on what one client is sent.
/// <para>
/// <b>Synthetic layer (tier A).</b> Hundreds of real client handshakes through real
/// <see cref="NebulaGateway"/> instances on real loopback sockets, against <see cref="FakeWorker"/>s built out of
/// the production interest code. Nothing here is a Unity worker, so nothing here says anything about simulation
/// cost; what it does prove is what the gateways decide — how many replicas a client ends up holding, how many
/// bytes that costs, whether a scope leaks into another scope, and whether a burst of joins is admitted. The real
/// worker numbers are tier D and come from <c>Tools/scale-suite.ps1</c>; every artifact says which layer wrote it.
/// </para>
/// <para>
/// Tagged <c>Scale</c> so the conformance runner never picks them up, and <c>Soak</c> as well because each runs a
/// real mesh for several seconds — the default <c>TestCategory!=Soak</c> run is unaffected.
/// </para>
/// </summary>
[TestFixture]
[Category("Scale")]
[Category("Soak")]
public class ScaleLoadTests
{
    /// <summary>Entities of scenery per container: what a client's interest window is filtering.</summary>
    private const int SceneryPerCell = 120;
    private const int Cells = 4;
    private const int Gateways = 2;

    private ScaleWorld _world = null!;

    [SetUp] public void Setup() => _world = new ScaleWorld(Cells);
    [TearDown] public void Cleanup() => _world.Dispose();

    /// <summary>Scatter scenery through a container so an interest window has something to include and exclude.</summary>
    private static void Scenery(FakeWorker worker, string containerId, ulong firstNetId, int count)
    {
        var container = ContainerRegistry.FindById(containerId);
        Assert.That(container, Is.Not.Null, containerId + " is not in the manifest");
        var random = new Random(unchecked((int)firstNetId));
        for (int i = 0; i < count; i++)
        {
            float x = (float)(random.NextDouble() - 0.5) * ScaleWorld.CellSize;
            float z = (float)(random.NextDouble() - 0.5) * ScaleWorld.CellSize;
            worker.Spawn(firstNetId + (ulong)i, new Vector3(x, 0, z), ContainerRef.Of(container!));
        }
    }

    /// <summary>A mesh with one worker per cell and scenery in every cell, pawns placed where the gateway asks.</summary>
    private Fleet Build(int workers = Cells)
    {
        var fleet = new Fleet(Gateways, workers: workers, world: f => { });
        _world.Deal(fleet);
        for (int i = 0; i < fleet.Workers.Count; i++)
        {
            fleet.Workers[i].SpawnIntoRequestedContainer = true;
            fleet.Workers[i].PawnPlacement = id => new Vector3((id % 97) - 48f, 0, (id % 89) - 44f);
        }
        for (int i = 0; i < _world.Cells.Count; i++)
            Scenery(fleet.Workers[i % fleet.Workers.Count], _world.Cells[i], 500_000UL + (ulong)i * 10_000UL, SceneryPerCell);
        fleet.OnPump = () => { foreach (var w in fleet.Workers) w.PublishStates(); };
        return fleet;
    }

    // ------------------------------------------------------------------------------------- D1: sustained load

    [Test]
    public void SustainedLoadKeepsEveryClientInsideItsReplicaAndBandwidthBounds()
    {
        const int clients = 120;
        const double measureSeconds = 6.0;
        var report = new ScaleReport("sustained-load",
            "clients", "workers", "gateways", "scenery", "joined", "replicasMin", "replicasMax", "replicasMean",
            "bytesPerClientPerSecMax", "bytesPerClientPerSecMean", "gatewayLoopLagMsMax", "orphanUpdates", "duplicateSpawns");

        using var fleet = Build();
        for (int i = 0; i < clients; i++) fleet.Connect(i % Gateways, "load" + i);

        Assert.That(fleet.Run(() => fleet.Clients.All(c => c.Join == JoinState.Joined), seconds: 60), Is.True,
            "every client joined: " + fleet.Clients.Count(c => c.Join == JoinState.Joined) + "/" + clients);
        fleet.RunFor(1.5); // let the interest sets settle before anything is counted

        var before = fleet.Clients.ToDictionary(c => c, c => c.BytesIn);
        var clock = Stopwatch.StartNew();
        fleet.RunFor(measureSeconds);
        double seconds = clock.Elapsed.TotalSeconds;

        var replicas = fleet.Clients.Select(c => c.Replicas.Count).ToList();
        var rates = fleet.Clients.Select(c => (c.BytesIn - before[c]) / seconds).ToList();
        float lag = fleet.Plane.Gateways.Count > 0 ? fleet.Plane.Gateways.Max(g => g.Stats.LoopLagMs) : 0f;
        int orphans = fleet.Clients.Sum(c => c.OrphanUpdates), duplicates = fleet.Clients.Sum(c => c.DuplicateSpawns);

        report.Row(clients, fleet.Workers.Count, Gateways, SceneryPerCell * Cells, fleet.Clients.Count(c => c.Join == JoinState.Joined),
            replicas.Min(), replicas.Max(), replicas.Average(), rates.Max(), rates.Average(), lag, orphans, duplicates);
        report.Note($"{clients} clients, {rates.Average():0} B/s each on average, {replicas.Average():0} replicas each");
        report.Write();

        Assert.That(fleet.Clients.All(c => c.LastError.Length == 0), Is.True,
            "a client failed to parse a packet: " + fleet.Clients.First(c => c.LastError.Length > 0).LastError);
        Assert.That(replicas.Min(), Is.GreaterThan(0), "every client holds at least its own pawn");
        Assert.That(rates.Max(), Is.LessThan(ScaleThresholds.BytesPerClientPerSecond),
            "one client was sent more than the per-client bandwidth bound");
        Assert.That(duplicates, Is.Zero, "no client was sent a second view of an entity it already held");
        Assert.That(orphans, Is.Zero, "no client was sent state for an entity it does not hold");
    }

    // ---------------------------------------------------------------------------------------- D2: burst load

    [Test]
    public void ABurstOfJoinsIsAdmittedWithoutRejectingAnyoneOrLosingASession()
    {
        const int burst = 150;
        var report = new ScaleReport("burst-load", "clients", "joinedAfterSeconds", "rejected", "disconnected", "distinctSessions");

        using var fleet = Build();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < burst; i++) fleet.Connect(i % Gateways, "burst" + i); // all at once, no ramp

        bool all = fleet.Run(() => fleet.Clients.All(c => c.Join == JoinState.Joined), seconds: 90);
        double joinedAfter = clock.Elapsed.TotalSeconds;

        int rejected = fleet.Clients.Count(c => c.Rejected != null);
        int sessions = fleet.Clients.Where(c => c.Welcome != null).Select(c => c.Welcome!.Value.ClientId).Distinct().Count();
        report.Row(burst, joinedAfter, rejected, fleet.Clients.Count(c => c.Disconnected), sessions);
        report.Note($"the whole burst was admitted in {joinedAfter:0.0} s");
        report.Write();

        Assert.That(all, Is.True, "the burst was not fully admitted within 90 s");
        Assert.That(rejected, Is.Zero, "a client was refused during the burst");
        Assert.That(sessions, Is.EqualTo(burst), "every client got its own session");
        Assert.That(fleet.Workers.Sum(w => w.Pawns.Count), Is.EqualTo(burst), "one pawn each, no duplicates");
    }

    // --------------------------------------------------------------------------------------- D3: many scopes

    [Test]
    public void ManyScopesCarryTheirOwnLoadAndNeverShowEachOtherAnything()
    {
        const int scopes = 4;
        const int perScope = 20;
        const int publicClients = 20;
        var report = new ScaleReport("many-scopes", "scope", "clients", "replicasMean", "bytesPerClientPerSec", "foreignReplicas");

        using var fleet = Build();
        var keys = ScaleWorld.ActivateScopes(fleet, scopes);
        foreach (string key in keys) Assert.That(fleet.Plane.IsScopeReady(key), Is.True, key + " did not become ready");

        // Scenery inside each scope, on the worker that owns it: what a client of that scope should see, and what
        // a client of any other scope must never hear about.
        var sceneryOf = new Dictionary<string, List<ulong>>();
        for (int i = 0; i < scopes; i++)
        {
            var worker = fleet.Workers[i % fleet.Workers.Count];
            var container = ScaleWorld.RoomContainer(keys[i]);
            var ids = new List<ulong>();
            for (int n = 0; n < 12; n++)
            {
                ulong netId = 700_000UL + (ulong)i * 1_000UL + (ulong)n;
                worker.Spawn(netId, new Vector3(n - 6f, 0, 0), container);
                ids.Add(netId);
            }
            sceneryOf[keys[i]] = ids;
        }

        var byScope = new Dictionary<string, List<FakeClient>>();
        for (int i = 0; i < scopes; i++)
        {
            var list = new List<FakeClient>();
            for (int n = 0; n < perScope; n++) list.Add(fleet.Connect(n % Gateways, $"s{i}-{n}", scope: keys[i]));
            byScope[keys[i]] = list;
        }
        var publics = new List<FakeClient>();
        for (int n = 0; n < publicClients; n++) publics.Add(fleet.Connect(n % Gateways, "pub" + n));

        Assert.That(fleet.Run(() => fleet.Clients.All(c => c.Join == JoinState.Joined), seconds: 60), Is.True,
            "every scoped and public client joined");
        fleet.RunFor(1.5);

        var before = fleet.Clients.ToDictionary(c => c, c => c.BytesIn);
        var clock = Stopwatch.StartNew();
        fleet.RunFor(4.0);
        double seconds = clock.Elapsed.TotalSeconds;

        int leaks = 0;
        foreach (var kv in byScope)
        {
            var foreign = sceneryOf.Where(s => s.Key != kv.Key).SelectMany(s => s.Value).ToHashSet();
            int leaked = kv.Value.Sum(c => c.HeardAbout.Count(foreign.Contains));
            leaks += leaked;
            report.Row(kv.Key, kv.Value.Count, kv.Value.Average(c => c.Replicas.Count),
                kv.Value.Average(c => (c.BytesIn - before[c]) / seconds), leaked);
        }
        var publicForeign = sceneryOf.SelectMany(s => s.Value).ToHashSet();
        int publicLeaks = publics.Sum(c => c.HeardAbout.Count(publicForeign.Contains));
        leaks += publicLeaks;
        report.Row("(public)", publics.Count, publics.Average(c => c.Replicas.Count),
            publics.Average(c => (c.BytesIn - before[c]) / seconds), publicLeaks);
        report.Note($"{scopes} scopes of {perScope} clients plus {publicClients} public clients; {leaks} cross-scope leaks");
        report.Write();

        Assert.That(leaks, Is.Zero, "a client heard about an entity belonging to a scope it is not in");
        foreach (var kv in byScope)
            Assert.That(kv.Value.All(c => c.Replicas.Count > 0), Is.True, kv.Key + ": a client of this scope holds nothing at all");
        Assert.That(fleet.Clients.Max(c => (c.BytesIn - before[c]) / seconds), Is.LessThan(ScaleThresholds.BytesPerClientPerSecond));
    }
}
