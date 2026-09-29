using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Conformance scenario 35 (docs/conformance-suite.md, docs/server-owned-entities.md D3): the real gateway sends each
/// client an entity's transform updates at the rate the entity's distance band and relevance priority give, one per
/// rate window, and the windows compose with a worker that sends the entity only every few ticks. A background
/// entity beyond the near band is sent no unreliable update, stays in the client's set, and is sent where it came
/// to rest. A client's own pawn is sent every update.
/// <para>
/// Tick numbers, not wall time, decide the rates, so the worker here publishes every tick number in turn as fast as
/// the loop runs and the counts are per 60 ticks.
/// </para>
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceRelevanceTierTests
{
    private string _directory = "";

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-tiers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        var container = new Container
        {
            ContainerId = "c0", Index = 0, Size = new Vector3(512, 512, 512),
            transform = new ContainerFrame { position = new Vector3(256, 0, 256) },
        };
        string path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest { Containers = new List<Container> { container } }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private static void RunTicks(Fleet fleet, FakeWorker worker, ref uint tick, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            worker.PublishWalkers(++tick);
            fleet.Pump();
        }
        // What is still in flight on loopback arrives before anything is counted.
        for (int i = 0; i < 20; i++) { fleet.Pump(); Thread.Sleep(1); }
    }

    [Test]
    public void EachClientIsSentAnEntityAtTheRateItsDistanceAndPriorityGive()
    {
        using var fleet = new Fleet(1);
        var worker = fleet.Worker;
        worker.Recovery = FakeWorker.RecoveryMode.OnceSettled;
        var client = fleet.Connect(0, "watcher");
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined, seconds: 10), Is.True, "the client joins");
        ulong pawn = client.Welcome!.Value.ClientId;

        // Each entity walks a 2 m circle around its spot, so it never leaves its distance band.
        (ulong Id, float Distance, RelevancePriority Priority, int Interval)[] cast =
        {
            (1001, 50f, RelevancePriority.Normal, 1),     // middle band: InterestMidDivisor (4) -> 15 a second
            (1002, 50f, RelevancePriority.Normal, 6),     // the worker sends 10 a second; a 4-tick window takes them all
            (1003, 50f, RelevancePriority.High, 1),       // one tier better: every update
            (1004, 20f, RelevancePriority.Low, 1),        // one tier worse near: 15 a second
            (1005, 50f, RelevancePriority.Background, 1), // beyond the near band: nothing but where it comes to rest
            (1006, 20f, RelevancePriority.Background, 1), // inside it: the middle band's rate
            (1007, 100f, RelevancePriority.Normal, 30),   // far band (FarDivisor 12): the worker's 2 a second all get through
        };
        for (int i = 0; i < cast.Length; i++)
        {
            var (id, distance, priority, interval) = cast[i];
            double angle = i * Math.PI * 2 / cast.Length;
            var spot = new Vector3((float)(distance * Math.Cos(angle)), 0, (float)(distance * Math.Sin(angle)));
            worker.Spawn(id, spot, priority: priority);
            var walker = worker.Walk(id, new Vector3(1.5f, 0, 0), radius: 2f, updateInterval: interval);
            walker.Centre = spot;
        }
        uint tick = 0;
        Assert.That(fleet.Run(() => client.Replicas.Count >= cast.Length + 1, seconds: 10), Is.True, "every entity reaches the client's set");
        RunTicks(fleet, worker, ref tick, 60);
        client.StatesOf.Clear();
        client.ReliableStatesOf.Clear();

        const int Ticks = 600;
        RunTicks(fleet, worker, ref tick, Ticks);
        double PerSecond(ulong id) => (client.StatesOf.TryGetValue(id, out int n) ? n : 0) * 60.0 / Ticks;
        foreach (var (id, distance, priority, interval) in cast)
            TestContext.Out.WriteLine($"#{id} {priority} at {distance} m, interval {interval}: {PerSecond(id):0.0} entries a second");

        Assert.That(PerSecond(1001), Is.InRange(13.5, 16.5), "Normal, middle band: InterestMidDivisor");
        Assert.That(PerSecond(1002), Is.InRange(9.0, 10.5), "Normal, middle band, sent every 6th tick: every update gets through");
        Assert.That(PerSecond(1003), Is.InRange(56.0, 60.5), "High, middle band: every update");
        Assert.That(PerSecond(1004), Is.InRange(13.5, 16.5), "Low, near band: InterestMidDivisor");
        Assert.That(PerSecond(1005), Is.EqualTo(0.0), "Background beyond the near band: no updates");
        Assert.That(PerSecond(1006), Is.InRange(13.5, 16.5), "Background inside the near band: InterestMidDivisor");
        Assert.That(PerSecond(1007), Is.InRange(1.8, 2.1), "Normal, far band, sent every 30th tick: the 12-tick window takes each");
        Assert.That(client.Replicas, Does.Contain(1005ul), "a quiet entity is still in the client's set");
        Assert.That(client.ReliableStatesOf.Count, Is.Zero, "nothing that keeps moving sends a recovery entry");

        // The background entity stops: the client is told where it came to rest, once.
        worker.Walkers[1005].Velocity = Vector3.zero;
        client.StatesOf.Clear();
        RunTicks(fleet, worker, ref tick, 120);
        Assert.That(client.ReliableStatesOf.TryGetValue(1005, out int settles) ? settles : 0, Is.EqualTo(1), "one reliable entry once it settled");
        Assert.That(client.StatesOf[1005], Is.EqualTo(1), "and nothing else");
        Assert.That(client.Replicas, Does.Contain(1005ul));
    }

    [Test]
    public void AClientsOwnPawnIsSentEveryUpdateWhateverItsPriority()
    {
        using var fleet = new Fleet(1);
        var worker = fleet.Worker;
        var client = fleet.Connect(0, "pilot");
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined, seconds: 10), Is.True);
        ulong pawn = worker.Pawns[client.Welcome!.Value.ClientId];
        worker.Entities[pawn].Priority = RelevancePriority.Background;
        worker.Reannounce(pawn, newEpoch: false);
        worker.Walk(pawn, new Vector3(1.5f, 0, 0), radius: 2f);
        uint tick = 0;
        RunTicks(fleet, worker, ref tick, 60);
        client.StatesOf.Clear();
        RunTicks(fleet, worker, ref tick, 300);
        Assert.That(client.StatesOf.TryGetValue(pawn, out int n) ? n : 0, Is.InRange(290, 300), "the pawn is sent every tick even as Background");
    }
}
