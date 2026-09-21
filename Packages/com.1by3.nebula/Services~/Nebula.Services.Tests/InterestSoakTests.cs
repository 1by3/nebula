using System.Text;
using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The claim interest management is actually making (design §11): what one client is sent depends on the local
/// density of the world and not on its size, and what one gateway caches and dials depends on its clients and
/// not on the world. These run the three world shapes of §11 at two sizes each and assert that the second size
/// costs about what the first did.
/// <para>
/// They are marked <c>Soak</c> because each runs a real mesh for a few seconds:
/// <c>dotnet test --filter TestCategory=Soak</c>. The results table they print is written to
/// <c>docs/interest-baseline/soak-results.md</c>, so a regression is a diff and not a memory.
/// </para>
/// </summary>
[TestFixture]
[Category("Soak")]
public class InterestSoakTests
{
    /// <summary>What one scenario cost, as the results table reports it.</summary>
    private readonly record struct Result(string Shape, int WorldEntities, int Replicas, double BytesPerSecond,
        int Cache, int Links, double EvalMs, int Gaps, int Duplicates, int Leaks);

    private static readonly List<Result> Results = new();
    private string _directory = "";

    /// <summary>Metres between entities, the same at both world sizes: the density is what the bound is in terms of.</summary>
    private const float Spacing = 16f;
    private const float CellSize = 128f;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-soak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
    }

    [TearDown]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [OneTimeTearDown]
    public void WriteTable()
    {
        if (Results.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("# Interest management soak baseline");
        sb.AppendLine();
        sb.AppendLine("Written by `InterestSoakTests` (`dotnet test --filter TestCategory=Soak` in `Services~`). Each");
        sb.AppendLine("row is one world shape of design §11 at one size, with the same local density. The point of the");
        sb.AppendLine("table is the *pairs*: within a shape, replicas, bytes/s, cache and links must stay flat as the");
        sb.AppendLine("world grows tenfold. Absolute numbers depend on the machine; the ratios do not.");
        sb.AppendLine();
        sb.AppendLine("| Shape | World entities | Replicas | Bytes/s to client | Gateway cache | Worker links | Eval ms | Gaps | Duplicates | Leaks |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in Results)
            sb.AppendLine($"| {r.Shape} | {r.WorldEntities} | {r.Replicas} | {r.BytesPerSecond:0} | {r.Cache} | {r.Links} | {r.EvalMs:0.00} | {r.Gaps} | {r.Duplicates} | {r.Leaks} |");
        string directory = Path.Combine(FindRepositoryRoot(), "docs", "interest-baseline");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "soak-results.md"), sb.ToString());
        TestContext.Out.WriteLine(sb.ToString());
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    // ------------------------------------------------------------------------------------------- worlds

    /// <summary>Write a manifest of <paramref name="cells"/> boxes on a line and load it.</summary>
    private void LoadLine(int cells, float cellSize)
    {
        var containers = new List<Container>();
        for (int i = 0; i < cells; i++)
            containers.Add(new Container
            {
                ContainerId = "c" + i, Index = (ushort)i, Size = new Vector3(cellSize, cellSize, cellSize),
                transform = new ContainerFrame { position = new Vector3(i * cellSize + cellSize / 2, 0, 0) },
            });
        string path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest { Containers = containers }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    /// <summary>
    /// Run one shape at one size: fill the world, let a stationary client settle, measure it, then walk a
    /// second client from one end to the other and check it saw everything on the way and kept nothing after.
    /// </summary>
    private Result RunShape(string shape, int entities, int workers, int containers, float containerSize)
    {
        LoadLine(containers, containerSize);
        float length = containers * containerSize;
        using var fleet = new Fleet(1, workers: workers,
            configure: c => { c.InterestMaxRadius = 200f; },
            world: f => { for (int i = 0; i < containers; i++) f.Assign("c" + i, "w" + (i % workers + 1)); });

        // A stationary client at the left end. Its pawn is placed in c0 whichever worker owns it.
        foreach (var worker in fleet.Workers) worker.PawnPlacement = _ => new Vector3(-containerSize / 2 + 4, 0, 0);
        var stationary = fleet.Connect(0, "stand");
        Assert.That(fleet.Run(() => stationary.Join == JoinState.Joined, seconds: 10), Is.True, "the stationary client joins");

        // Fill the line at a fixed spacing, so the same density is reached at both world sizes.
        var byWorker = new Dictionary<ulong, int>();
        for (int i = 0; i < entities; i++)
        {
            float x = (i + 0.5f) * length / entities;
            int cell = Math.Min(containers - 1, (int)(x / containerSize));
            var worker = fleet.Workers[cell % workers];
            ulong netId = (ulong)(100_000 + i);
            worker.Spawn(netId, new Vector3(x - cell * containerSize - containerSize / 2, 0, 0), new ContainerRef((ushort)cell));
            byWorker[netId] = cell % workers;
        }
        fleet.OnPump = () => { foreach (var worker in fleet.Workers) worker.PublishStates(); };
        fleet.RunFor(3.0);

        long bytesBefore = stationary.BytesIn;
        fleet.RunFor(2.0);
        double bytesPerSecond = (stationary.BytesIn - bytesBefore) / 2.0;
        int replicas = stationary.Replicas.Count;
        int cache = fleet.Gateways[0].CachedEntityCount;
        int links = fleet.Gateways[0].WorkerLinkCount;
        double evalMs = fleet.Gateways[0].LastStats.InterestEvalMsAvg;

        // A traveller: walk its pawn the length of the world and check, at every step, that everything inside
        // the enter radius has arrived and nothing beyond the exit radius plus the linger has stayed.
        var traveller = fleet.Connect(0, "walk");
        Assert.That(fleet.Run(() => traveller.Join == JoinState.Joined, seconds: 10), Is.True, "the traveller joins");
        ulong pawn = 0;
        foreach (var worker in fleet.Workers)
            foreach (var kv in worker.Pawns)
                if (kv.Key == traveller.Welcome!.Value.ClientId) pawn = kv.Value;
        Assert.That(pawn, Is.Not.Zero, "the traveller has a pawn to walk");
        var pawnWorker = fleet.Workers.First(w => w.Entities.ContainsKey(pawn));

        // Walk it at a believable speed (20 m/s: a vehicle) across several regions, container boundaries and,
        // in the sharded shapes, worker boundaries. Teleporting across the whole world instead would only
        // measure how long a set takes to settle after a jump no player can make.
        int gaps = 0, leaks = 0;
        const float enterRadius = 90f;    // inside InterestRadius (120) with room for a step of travel
        const float leakRadius = 240f;    // beyond InterestRadius + exit margin plus a linger's worth of travel
        const float step = 8f;            // metres per stop
        const double dwell = 0.4;         // seconds at each stop
        int steps = 40;
        for (int i = 1; i <= steps; i++)
        {
            float x = Math.Min(length - 1, 4 + i * step);
            int cell = Math.Min(containers - 1, (int)(x / containerSize));
            pawnWorker.Entities[pawn].Container = new ContainerRef((ushort)cell);
            pawnWorker.Move(pawn, new Vector3(x - cell * containerSize - containerSize / 2, 0, 0));
            fleet.RunFor(dwell);
            int stepGaps = 0;
            for (int e = 0; e < entities; e++)
            {
                float ex = (e + 0.5f) * length / entities;
                ulong netId = (ulong)(100_000 + e);
                float d = Math.Abs(ex - x);
                if (d <= enterRadius && !traveller.Replicas.Contains(netId)) { gaps++; stepGaps++; }
                else if (d > leakRadius && traveller.Replicas.Contains(netId)) leaks++;
            }
            if (stepGaps > 0) TestContext.Out.WriteLine($"    {shape}/{entities} step {i} x={x:0} gaps={stepGaps} replicas={traveller.Replicas.Count} cache={fleet.Gateways[0].CachedEntityCount}");
        }
        TestContext.Out.WriteLine($"  {shape}/{entities}: stationary replicas={replicas} bytes/s={bytesPerSecond:0} cache={cache} links={links}; traveller replicas={traveller.Replicas.Count} gaps={gaps} leaks={leaks} dup={traveller.DuplicateSpawns}");

        Results.Add(new Result(shape, entities, replicas, bytesPerSecond, cache, links, evalMs, gaps, traveller.DuplicateSpawns, leaks));
        Assert.That(gaps, Is.Zero, $"{shape}/{entities}: the traveller must never have a hole inside the enter radius");
        Assert.That(traveller.DuplicateSpawns, Is.Zero, $"{shape}/{entities}: no entity is spawned twice into a live replica");
        Assert.That(leaks, Is.Zero, $"{shape}/{entities}: nothing beyond the exit radius and the linger is kept");
        return Results[^1];
    }

    /// <summary>Within one shape, growing the world must not grow what one client, or the gateway, is made to carry.</summary>
    private static void AssertBounded(string shape, Result small, Result large)
    {
        Assert.That(large.Replicas, Is.LessThanOrEqualTo(Math.Max(8, small.Replicas * 2)),
            $"{shape}: a stationary client's replica count must follow the local density, not the size of the world ({small.Replicas} -> {large.Replicas})");
        Assert.That(large.BytesPerSecond, Is.LessThanOrEqualTo(Math.Max(4096, small.BytesPerSecond * 3)),
            $"{shape}: and so must its inbound bytes ({small.BytesPerSecond:0} -> {large.BytesPerSecond:0} B/s)");
        Assert.That(large.Cache, Is.LessThanOrEqualTo(Math.Max(64, small.Cache * 3)),
            $"{shape}: the gateway caches what its clients' subscriptions bring in ({small.Cache} -> {large.Cache})");
        Assert.That(large.Links, Is.LessThanOrEqualTo(Math.Max(2, small.Links + 1)),
            $"{shape}: and dials the workers its clients need ({small.Links} -> {large.Links})");
    }

    // ------------------------------------------------------------------------------------------- scenarios

    [Test]
    public void AGridOfCellContainersAcrossFourWorkersStaysBoundedAsTheWorldGrows()
    {
        // Shape (a) of §11: an infinite grid of cell containers. Growing the world means more cells and more
        // workers' worth of them, which is the shape interest management is meant to make free.
        var small = RunShape("grid (4 workers)", 2000, workers: 4, containers: 250, containerSize: CellSize);
        Cleanup(); Setup();
        var large = RunShape("grid (4 workers)", 20000, workers: 4, containers: 2500, containerSize: CellSize);
        AssertBounded("grid", small, large);
        Assert.That(large.Links, Is.LessThanOrEqualTo(4));
    }

    [Test]
    public void AFewZoneContainersStayBoundedAsTheWorldGrows()
    {
        // Shape (b): a handful of large zones. The container is no help in scoping here — the region filter is
        // the only thing between the client and the whole zone.
        var small = RunShape("zones (3 workers)", 2000, workers: 3, containers: 3, containerSize: 250 * CellSize / 3f);
        Cleanup(); Setup();
        var large = RunShape("zones (3 workers)", 20000, workers: 3, containers: 3, containerSize: 2500 * CellSize / 3f);
        AssertBounded("zones", small, large);
    }

    [Test]
    public void OneBigContainerOnOneWorkerStillBoundsTheClientAndTheCache()
    {
        // Shape (c): one container, one worker. Simulation does not scale here and the design says so, but the
        // client must still be culled and the gateway must still cache only the regions it subscribes — with
        // exactly one worker link, because there is exactly one worker.
        var small = RunShape("one container", 2000, workers: 1, containers: 1, containerSize: 250 * CellSize);
        Cleanup(); Setup();
        var large = RunShape("one container", 20000, workers: 1, containers: 1, containerSize: 2500 * CellSize);
        AssertBounded("one container", small, large);
        Assert.That(large.Links, Is.EqualTo(1), "one worker, one link");
        Assert.That(large.Cache, Is.LessThan(large.WorldEntities), "and the region filter still keeps the cache below the world");
    }
}
