using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Conformance scenario 38 (docs/conformance-suite.md, docs/server-owned-entities.md D10 and D11): a priority can
/// have distance tiers of its own, so a crowd at <see cref="RelevancePriority.Low"/> is slowed without slowing a
/// player at <see cref="RelevancePriority.Normal"/>; and a behaviour's sync state can follow the same tier as the
/// entity's transform, coalesced for far clients but never lost.
/// <para>
/// Tick numbers, not wall time, decide the rates, so the worker here publishes every tick number in turn as fast as
/// the loop runs and the counts are per 60 ticks.
/// </para>
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformancePriorityTierTests
{
    private string _directory = "";

    /// <summary>The crowd tiers a game with many server-owned characters sets for its Low and Background priorities.</summary>
    private static void CrowdTiers(NebulaConfig c)
    {
        c.InterestLowTiers = RelevanceTierBands.Of(nearRadius: 25f, farRadius: 60f, nearDivisor: 1, midDivisor: 30, farDivisor: 120);
        c.InterestBackgroundTiers = RelevanceTierBands.Of(nearRadius: 25f, farRadius: 60f, nearDivisor: 4, midDivisor: 60, farDivisor: 0);
    }

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-priority-tiers-" + Guid.NewGuid().ToString("N"));
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

    private static void RunTicks(Fleet fleet, FakeWorker worker, ref uint tick, int ticks, Action<uint>? each = null)
    {
        for (int i = 0; i < ticks; i++)
        {
            worker.PublishWalkers(++tick);
            each?.Invoke(tick);
            fleet.Pump();
        }
        // What is still in flight on loopback arrives before anything is counted.
        for (int i = 0; i < 20; i++) { fleet.Pump(); Thread.Sleep(1); }
    }

    private static Vector3 Spot(float distance, int i, int of)
    {
        double angle = i * Math.PI * 2 / of;
        return new Vector3((float)(distance * Math.Cos(angle)), 0, (float)(distance * Math.Sin(angle)));
    }

    [Test]
    public void EachPriorityIsSentByItsOwnTiersWhileNormalKeepsTheGlobalOnes()
    {
        using var fleet = new Fleet(1, configure: CrowdTiers);
        var worker = fleet.Worker;
        worker.Recovery = FakeWorker.RecoveryMode.OnceSettled;
        var client = fleet.Connect(0, "watcher");
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined, seconds: 10), Is.True, "the client joins");

        // Each entity walks a 2 m circle around its spot, so it never leaves its distance band.
        (ulong Id, float Distance, RelevancePriority Priority, string Why, double Min, double Max)[] cast =
        {
            (2001, 40f, RelevancePriority.Normal, "Normal at 40 m (a player): the global middle band, InterestMidDivisor 4", 13.5, 16.5),
            (2002, 40f, RelevancePriority.Low, "Low at 40 m: its own middle band, every 30th tick", 1.8, 2.2),
            (2003, 15f, RelevancePriority.Low, "Low at 15 m: its own near band, every update", 56.0, 60.5),
            (2004, 100f, RelevancePriority.Low, "Low at 100 m: its own far band, every 120th tick", 0.4, 0.6),
            (2005, 15f, RelevancePriority.Background, "Background at 15 m: its own near band, every 4th tick", 13.5, 16.5),
            (2006, 40f, RelevancePriority.Background, "Background at 40 m: every 60th tick", 0.9, 1.1),
            (2007, 100f, RelevancePriority.Background, "Background at 100 m: none", 0.0, 0.0),
            (2008, 50f, RelevancePriority.High, "High at 50 m, no override: the global tiers one better, every update", 56.0, 60.5),
        };
        for (int i = 0; i < cast.Length; i++)
        {
            var spot = Spot(cast[i].Distance, i, cast.Length);
            worker.Spawn(cast[i].Id, spot, priority: cast[i].Priority);
            worker.Walk(cast[i].Id, new Vector3(1.5f, 0, 0), radius: 2f).Centre = spot;
        }
        uint tick = 0;
        Assert.That(fleet.Run(() => client.Replicas.Count >= cast.Length + 1, seconds: 10), Is.True, "every entity reaches the client's set");
        RunTicks(fleet, worker, ref tick, 120);
        client.StatesOf.Clear();

        const int Ticks = 1200;
        RunTicks(fleet, worker, ref tick, Ticks);
        double PerSecond(ulong id) => (client.StatesOf.TryGetValue(id, out int n) ? n : 0) * 60.0 / Ticks;
        foreach (var c in cast) TestContext.Out.WriteLine($"#{c.Id} {c.Priority} at {c.Distance} m: {PerSecond(c.Id):0.00} entries a second");
        foreach (var c in cast) Assert.That(PerSecond(c.Id), Is.InRange(c.Min, c.Max), c.Why);
        Assert.That(client.Replicas, Does.Contain(2007ul), "a quiet entity is still in the client's set");
    }

    [Test]
    public void WithoutOverridesEveryPriorityKeepsTheShiftedGlobalTiers()
    {
        var settings = InterestSettings.Default;
        foreach (RelevancePriority p in Enum.GetValues(typeof(RelevancePriority)))
        {
            var t = settings.TiersFor(p);
            Assert.That(t.NearRadius, Is.EqualTo(settings.NearRadius), $"{p}: near radius");
            Assert.That(t.FarRadius, Is.EqualTo(settings.FarRadius), $"{p}: far radius");
            for (int band = RelevanceTiers.Near; band <= RelevanceTiers.Far; band++)
                Assert.That(t.DivisorOf(band), Is.EqualTo(RelevanceTiers.Divisor(p, band, settings.MidDivisor, settings.FarDivisor)), $"{p}, band {band}");
        }
    }

    // ------------------------------------------------------------------------------------------- sync state

    private const byte Rated = 1, Plain = 2;
    private const SyncStateCodec.ChunkFlags R = SyncStateCodec.ChunkFlags.DistanceRated, F = SyncStateCodec.ChunkFlags.Full;

    private static byte[] Stamp(uint tick) => BitConverter.GetBytes(tick);

    /// <summary>
    /// One tick of an entity's sync stream as a worker writes it for a rated behaviour that writes deltas
    /// (<see cref="SyncDistanceRating.Keyframes"/>) beside an unrated one: a delta every tick, a keyframe every 30.
    /// </summary>
    private static void SendDeltaStream(FakeWorker worker, ulong netId, uint tick)
    {
        bool keyframe = tick % 30 == 0; // NetworkIdentity.SyncKeyframeInterval
        var flags = keyframe ? F : SyncStateCodec.ChunkFlags.None;
        worker.SendSync(netId, reliable: false, tick, 0, (Rated, flags | R, Stamp(tick)), (Plain, flags, Stamp(tick)));
    }

    private static IEnumerable<(ulong NetId, byte Index, SyncStateCodec.ChunkFlags Flags, byte[] Bytes, string Via)> Chunks(FakeClient client, ulong netId, byte index) =>
        client.SyncChunks.Where(c => c.NetId == netId && c.Index == index && c.Via != "spawn");

    [Test]
    public void RatedSyncStateFollowsTheTierAndItsSettleKeyframeReachesEveryone()
    {
        using var fleet = new Fleet(1, configure: CrowdTiers);
        var worker = fleet.Worker;
        worker.Recovery = FakeWorker.RecoveryMode.OnceSettled;
        var client = fleet.Connect(0, "watcher");
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined, seconds: 10), Is.True);

        const ulong Near = 3001, Far = 3002, Whole = 3003, Quiet = 3004;
        (ulong Id, float Distance, RelevancePriority Priority)[] cast =
        {
            (Near, 15f, RelevancePriority.Low),        // every-update band: every chunk
            (Far, 40f, RelevancePriority.Low),         // every 30th tick: keyframes only, one a window
            (Whole, 40f, RelevancePriority.Low),       // every write whole: one a window of 30
            (Quiet, 100f, RelevancePriority.Background), // none: only the settle keyframe
        };
        for (int i = 0; i < cast.Length; i++)
        {
            var spot = Spot(cast[i].Distance, i, cast.Length);
            worker.Spawn(cast[i].Id, spot, priority: cast[i].Priority);
            worker.Walk(cast[i].Id, new Vector3(1.5f, 0, 0), radius: 2f).Centre = spot;
        }
        uint tick = 0;
        Assert.That(fleet.Run(() => client.Replicas.Count >= cast.Length + 1, seconds: 10), Is.True);

        void Stream(uint t)
        {
            SendDeltaStream(worker, Near, t);
            SendDeltaStream(worker, Far, t);
            SendDeltaStream(worker, Quiet, t);
            worker.SendSync(Whole, reliable: false, t, 0, (Rated, F | R, Stamp(t)));
        }
        RunTicks(fleet, worker, ref tick, 120, Stream);
        client.SyncChunks.Clear();

        const int Ticks = 1200;
        RunTicks(fleet, worker, ref tick, Ticks, Stream);
        double PerSecond(ulong id, byte index) => Chunks(client, id, index).Count() * 60.0 / Ticks;
        foreach (var c in cast)
            TestContext.Out.WriteLine($"#{c.Id} {c.Priority} at {c.Distance} m: rated {PerSecond(c.Id, Rated):0.00}/s, plain {PerSecond(c.Id, Plain):0.00}/s");

        Assert.That(PerSecond(Near, Rated), Is.InRange(58.0, 60.5), "near: every chunk of the rated behaviour");
        Assert.That(PerSecond(Near, Plain), Is.InRange(58.0, 60.5), "near: every chunk of the plain one");
        Assert.That(PerSecond(Far, Plain), Is.InRange(58.0, 60.5), "an unrated behaviour is sent every chunk however far");
        Assert.That(PerSecond(Far, Rated), Is.InRange(1.8, 2.2), "far, window 30, keyframes every 30: each keyframe and no delta");
        Assert.That(Chunks(client, Far, Rated).All(c => (c.Flags & F) != 0), Is.True, "a far client is sent keyframes only");
        Assert.That(PerSecond(Whole, Rated), Is.InRange(1.8, 2.2), "whole state every tick, window 30: one a window, as its transform");
        Assert.That(PerSecond(Quiet, Rated), Is.EqualTo(0.0), "no rate at all: nothing while it changes");
        Assert.That(PerSecond(Quiet, Plain), Is.InRange(58.0, 60.5));

        // The state stops changing: the worker sends one reliable settle keyframe, which every holder is given.
        client.SyncChunks.Clear();
        uint last = tick;
        RunTicks(fleet, worker, ref tick, 40, t =>
        {
            if (t == last + 31)
                foreach (var id in new[] { Far, Quiet, Whole })
                    worker.SendSync(id, reliable: true, t, 0, (Rated, F | R | SyncStateCodec.ChunkFlags.Settled, Stamp(999_999)));
        });
        foreach (var id in new[] { Far, Quiet, Whole })
        {
            var settle = Chunks(client, id, Rated).Where(c => c.Via == "reliable").ToList();
            Assert.That(settle, Has.Count.EqualTo(1), $"#{id}: the settle keyframe reaches the client");
            Assert.That(BitConverter.ToUInt32(settle[0].Bytes), Is.EqualTo(999_999u), $"#{id}: and it is the latest state");
        }
    }

    [Test]
    public void AClientThatComesCloserGetsDeltasAgainOnlyAfterItsNextKeyframe()
    {
        using var fleet = new Fleet(1, configure: CrowdTiers);
        var worker = fleet.Worker;
        worker.Recovery = FakeWorker.RecoveryMode.OnceSettled;
        var client = fleet.Connect(0, "watcher");
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined, seconds: 10), Is.True);

        const ulong Id = 3101;
        var far = new Vector3(40, 0, 0);
        worker.Spawn(Id, far, priority: RelevancePriority.Low);
        var walker = worker.Walk(Id, new Vector3(1.5f, 0, 0), radius: 2f);
        walker.Centre = far;
        uint tick = 0;
        Assert.That(fleet.Run(() => client.Replicas.Count >= 2, seconds: 10), Is.True);
        // Start the count just after a keyframe, so the client has missed deltas when it comes close.
        RunTicks(fleet, worker, ref tick, 125 - (int)tick, t => SendDeltaStream(worker, Id, t));
        Assert.That(tick % 30, Is.Not.EqualTo(0u));

        // It comes within the near band: the next entry moves it there.
        var near = new Vector3(10, 0, 0);
        worker.Move(Id, near);
        walker.Centre = near;
        client.SyncChunks.Clear();
        uint from = tick;
        RunTicks(fleet, worker, ref tick, 90, t => SendDeltaStream(worker, Id, t));

        var got = Chunks(client, Id, Rated).Select(c => (Tick: BitConverter.ToUInt32(c.Bytes), Full: (c.Flags & F) != 0)).ToList();
        Assert.That(got, Is.Not.Empty);
        uint firstKeyframe = got.First(g => g.Full).Tick;
        Assert.That(got.Where(g => g.Tick < firstKeyframe), Is.Empty, "no delta before the keyframe that catches the client up");
        Assert.That(firstKeyframe, Is.EqualTo((from / 30 + 1) * 30), "the first keyframe after it came close");
        uint lastTick = got.Max(g => g.Tick);
        Assert.That(got.Count(g => g.Tick > firstKeyframe), Is.EqualTo((int)(lastTick - firstKeyframe)), "then every chunk, deltas included");
    }
}
