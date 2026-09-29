using System.Diagnostics;
using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// What one client is sent for a crowd of server-owned entities around it (docs/server-owned-entities.md §1): the
/// real gateway, a real client handshake over loopback, and a worker publishing walkers the way a Unity worker's
/// root <c>NetworkTransform</c> does (<see cref="FakeWorker.PublishWalkers"/>), paced at 60 ticks a second of wall
/// clock. The measurement is the client's inbound bytes per second once the spawns are over.
/// <para>
/// Tagged <c>Scale</c> and <c>Soak</c> (each scenario runs a mesh for seconds), so neither the conformance runner nor
/// an ordinary <c>TestCategory!=Soak</c> run picks it up. Writes <c>Logs/scale/synthetic-server-owned-bandwidth.csv</c>.
/// </para>
/// </summary>
[TestFixture]
[Category("Scale")]
[Category("Soak")]
public class ServerOwnedBandwidthTests
{
    private const float ContainerSize = 512f;
    private static ScaleReport? _report;
    private string _directory = "";

    [OneTimeSetUp]
    public void OpenReport() => _report = new ScaleReport("server-owned-bandwidth",
        "walking", "idle", "update_interval", "recovery", "tiers", "replicas", "bytes_per_s", "kbit_per_s", "entries_per_s", "messages_per_s");

    [OneTimeTearDown]
    public void WriteReport() => _report?.Write();

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-crowd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        string path = Path.Combine(_directory, "nebula-services.json");
        var container = new Container
        {
            ContainerId = "c0", Index = 0, Size = new Vector3(ContainerSize, ContainerSize, ContainerSize),
            transform = new ContainerFrame { position = new Vector3(ContainerSize / 2, 0, ContainerSize / 2) },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest { Containers = new List<Container> { container } }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    /// <summary>What one crowd scenario cost the client standing in the middle of it.</summary>
    public readonly record struct Result(int Replicas, double BytesPerSecond, double EntriesPerSecond, double MessagesPerSecond,
        double PlayerEntriesPerSecond = 0, double SyncMessagesPerSecond = 0)
    {
        public double KilobitsPerSecond => BytesPerSecond * 8 / 1000;
    }

    /// <summary>
    /// Stand a client in the middle of <paramref name="walking"/> walking and <paramref name="idle"/> standing
    /// server-owned entities, spread evenly over a disc of 110 m (inside the 120 m interest radius) and measure
    /// what it is sent over <paramref name="seconds"/>.
    /// </summary>
    /// <param name="light">
    /// Entities of a lighter tier, placed after the others: walking at <paramref name="lightInterval"/> with
    /// <paramref name="lightPriority"/>, or standing when <paramref name="lightWalking"/> is false.
    /// </param>
    public static Result RunCrowd(int walking, int idle, int updateInterval = 1, FakeWorker.RecoveryMode recovery = FakeWorker.RecoveryMode.EveryThirtyTicks,
        Action<NebulaConfig>? configure = null, double seconds = 4.0, int seed = 359,
        int light = 0, int lightInterval = 1, RelevancePriority lightPriority = RelevancePriority.Normal, bool lightWalking = true,
        RelevancePriority priority = RelevancePriority.Normal, TransformFields? fields = null,
        bool player = false, int swarms = 0, int swarmBytes = 200, bool swarmRated = false)
    {
        using var fleet = new Fleet(1, configure: configure);
        var worker = fleet.Worker;
        worker.Recovery = recovery;
        if (fields.HasValue) worker.WalkerFields = fields.Value;
        var centre = Vector3.zero;
        worker.PawnPlacement = _ => centre;
        var client = fleet.Connect(0, "watcher");
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined, seconds: 10), Is.True, "the client joins");

        var random = new Random(seed);
        int total = walking + idle + light;
        for (int i = 0; i < total; i++)
        {
            // Even over the disc's area, so the share in each distance tier is the share of its area.
            double r = 110.0 * Math.Sqrt(random.NextDouble());
            double a = random.NextDouble() * Math.PI * 2;
            var local = new Vector3((float)(r * Math.Cos(a)), 0, (float)(r * Math.Sin(a)));
            ulong netId = (ulong)(500_000 + i);
            bool isLight = i >= walking + idle;
            worker.Spawn(netId, local, priority: isLight ? lightPriority : priority);
            if (i < walking || (isLight && lightWalking))
            {
                double heading = random.NextDouble() * Math.PI * 2;
                var walker = worker.Walk(netId, new Vector3((float)Math.Cos(heading), 0, (float)Math.Sin(heading)) * 1.5f, radius: 110f,
                    updateInterval: isLight ? lightInterval : updateInterval);
                walker.Centre = centre;
            }
        }

        // Another player 40 m away: a Normal entity updated every tick, circling on the spot so it stays in the
        // middle band. Whatever the crowd's tiers, it should keep the global middle band's rate.
        const ulong PlayerId = 499_999;
        if (player)
        {
            var spot = new Vector3(40, 0, 0);
            worker.Spawn(PlayerId, spot, priority: RelevancePriority.Normal);
            worker.Walk(PlayerId, new Vector3(1.5f, 0, 0), radius: 2f).Centre = spot;
            total++;
        }

        // Swarms: Low walkers updated every 6 ticks, each carrying its members' state as a sync blob of
        // swarmBytes written whole on every update (SyncDistanceRating.WholeState when rated).
        var swarmIds = new List<ulong>();
        var blob = new byte[swarmBytes];
        for (int i = 0; i < swarms; i++)
        {
            double r = 110.0 * Math.Sqrt(random.NextDouble());
            double a = random.NextDouble() * Math.PI * 2;
            ulong netId = (ulong)(490_000 + i);
            worker.Spawn(netId, new Vector3((float)(r * Math.Cos(a)), 0, (float)(r * Math.Sin(a))), priority: RelevancePriority.Low);
            worker.Walk(netId, new Vector3(1.5f, 0, 0), radius: 110f, updateInterval: updateInterval).Centre = centre;
            swarmIds.Add(netId);
            total++;
        }
        var swarmFlags = SyncStateCodec.ChunkFlags.Full | (swarmRated ? SyncStateCodec.ChunkFlags.DistanceRated : SyncStateCodec.ChunkFlags.None);

        var clock = Stopwatch.StartNew();
        uint published = 0;
        void Step()
        {
            uint due = (uint)(clock.Elapsed.TotalSeconds * 60) + 1;
            if (due > published + 2) published = due - 2; // never more than two catch-up ticks, as a worker
            while (published < due)
            {
                worker.PublishWalkers(++published);
                foreach (var id in swarmIds)
                    if ((published + (uint)(id % (ulong)updateInterval)) % (uint)updateInterval == 0)
                        worker.SendSync(id, reliable: false, published, 0, (1, swarmFlags, blob));
            }
            fleet.Pump();
            Thread.Sleep(1);
        }

        // Settle: the spawns land and the interest set fills.
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed.TotalSeconds < 2.0 || client.Replicas.Count < total * 0.98) { Step(); if (settle.Elapsed.TotalSeconds > 15) break; }
        long bytes = client.BytesIn, entries = client.StatesReceived, messages = client.MessagesIn, syncs = client.SyncStatesReceived;
        client.StatesOf.TryGetValue(PlayerId, out int playerEntries);
        var window = Stopwatch.StartNew();
        while (window.Elapsed.TotalSeconds < seconds) Step();
        double s = window.Elapsed.TotalSeconds;
        client.StatesOf.TryGetValue(PlayerId, out int playerAfter);
        return new Result(client.Replicas.Count, (client.BytesIn - bytes) / s, (client.StatesReceived - entries) / s, (client.MessagesIn - messages) / s,
            (playerAfter - playerEntries) / s, (client.SyncStatesReceived - syncs) / s);
    }

    private static void Record(string label, int walking, int idle, int interval, object recovery, Result r)
    {
        _report?.Row(walking, idle, interval, recovery, label, r.Replicas, r.BytesPerSecond, r.KilobitsPerSecond, r.EntriesPerSecond, r.MessagesPerSecond);
        TestContext.Out.WriteLine($"[scale:synthetic] {label} walking={walking} idle={idle} interval={interval} recovery={recovery}: replicas={r.Replicas} {r.BytesPerSecond:0} B/s ({r.KilobitsPerSecond:0} kbit/s), {r.EntriesPerSecond:0} entries/s, {r.MessagesPerSecond:0} messages/s");
    }

    /// <summary>The numbers of docs/server-owned-entities.md §1, before anything of NEB-359: the default tiers, every tick, recovery every 30 ticks.</summary>
    [Test]
    public void BaselineCrowdBandwidth([Values(100, 200, 300)] int walking)
    {
        var r = RunCrowd(walking, 0);
        Record("default", walking, 0, 1, FakeWorker.RecoveryMode.EveryThirtyTicks, r);
        Assert.That(r.Replicas, Is.EqualTo(walking + 1), "the client holds every walker and its pawn");
    }

    /// <summary>
    /// 50 server-owned entities spawned in one frame next to a client: how many transport messages and bytes the
    /// client is sent for them. The gateway coalesces a client's reliable messages into <see cref="MsgId.Batch"/>
    /// packets of about 1,100 bytes, so this is a handful of messages, not fifty.
    /// </summary>
    [Test]
    public void BurstOfFiftySpawns()
    {
        using var fleet = new Fleet(1);
        var worker = fleet.Worker;
        var client = fleet.Connect(0, "watcher");
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined, seconds: 10), Is.True, "the client joins");
        fleet.RunFor(0.5);
        long messages = client.MessagesIn, bytes = client.BytesIn;
        client.Record = new();
        for (int i = 0; i < 50; i++) worker.Spawn((ulong)(600_000 + i), new Vector3(i % 10 * 3f, 0, i / 10 * 3f));
        Assert.That(fleet.Run(() => client.Replicas.Count >= 51, seconds: 5), Is.True, "all fifty arrive");
        fleet.RunFor(0.3);
        long m = client.MessagesIn - messages, b = client.BytesIn - bytes;
        // The messages that carried the spawns: reliable batches whose envelope holds at least one EntitySpawn.
        int carrying = 0, spawnsSeen = 0;
        foreach (var (outbound, data) in client.Record)
        {
            if (outbound || data.Length < 3 || data[0] != (byte)MsgId.Batch) continue;
            var r = new NetworkReader(data);
            r.ReadByte();
            int n = r.ReadUShort(), here = 0;
            for (int i = 0; i < n; i++)
            {
                var inner = r.ReadSegment(r.ReadUShort());
                if (inner.Count > 0 && inner.Array![inner.Offset] == (byte)MsgId.EntitySpawn) here++;
            }
            if (here > 0) { carrying++; spawnsSeen += here; }
        }
        _report?.Row(50, 0, 1, "spawn-burst", "default", client.Replicas.Count, (double)b, 0.0, (double)spawnsSeen, (double)carrying);
        TestContext.Out.WriteLine($"[scale:synthetic] spawn burst: 50 spawns reached the client in {carrying} transport messages ({m} messages and {b} bytes in all over the window)");
        Assert.That(spawnsSeen, Is.EqualTo(50), "every spawn reached the client");
        Assert.That(carrying, Is.LessThanOrEqualTo(6), "fifty spawns in one frame are coalesced into a few reliable batches per client, not fifty messages");
    }

    /// <summary>
    /// 200 walkers again, with what could already be changed before NEB-359: the gateway's distance tiers
    /// stretched (every 30th tick in the middle band, every 60th far, a 20 m near band), and, as a what-if, the
    /// recovery entry sent once an entity settles instead of every 30 ticks while it moves.
    /// </summary>
    [Test]
    public void WhatTheExistingKnobsBuy()
    {
        Record("default", 200, 0, 1, FakeWorker.RecoveryMode.OnceSettled, RunCrowd(200, 0, recovery: FakeWorker.RecoveryMode.OnceSettled));
        Action<NebulaConfig> stretched = c => { c.InterestNearRadius = 20f; c.InterestFarRadius = 60f; c.InterestMidDivisor = 30; c.InterestFarDivisor = 60; };
        Record("stretched", 200, 0, 1, FakeWorker.RecoveryMode.EveryThirtyTicks, RunCrowd(200, 0, configure: stretched));
        Record("stretched", 200, 0, 1, FakeWorker.RecoveryMode.OnceSettled, RunCrowd(200, 0, recovery: FakeWorker.RecoveryMode.OnceSettled, configure: stretched));
    }

    /// <summary>
    /// The same crowds with relevance tiers (docs/server-owned-entities.md §5): the recovery entry once settled, as
    /// <c>NetworkTransform</c> now sends it, active walkers updated 10 times a second
    /// (<see cref="NetworkIdentity.UpdateInterval"/> 6), and the lighter tier as <see cref="RelevancePriority.Background"/>
    /// updated twice a second, walking or standing. The last rows add the gateway's far tiers stretched as a game
    /// with a crowd would set them.
    /// </summary>
    [Test]
    public void WithRelevanceTiers()
    {
        const FakeWorker.RecoveryMode settled = FakeWorker.RecoveryMode.OnceSettled;
        var r = RunCrowd(200, 0, updateInterval: 6, recovery: settled);
        Record("default", 200, 0, 6, settled, r);

        r = RunCrowd(200, 0, updateInterval: 6, recovery: settled, light: 1000, lightInterval: 30, lightPriority: RelevancePriority.Background, lightWalking: false);
        Record("default +1000 background idle", 200, 0, 6, settled, r);
        Assert.That(r.Replicas, Is.EqualTo(1201));

        r = RunCrowd(200, 0, updateInterval: 6, recovery: settled, light: 1000, lightInterval: 30, lightPriority: RelevancePriority.Background, lightWalking: true);
        Record("default +1000 background walking", 200, 0, 6, settled, r);
        Assert.That(r.Replicas, Is.EqualTo(1201));

        // The gateway's tiers set for a crowd rather than a firefight: a 25 m near band, 3 updates a second to 60 m,
        // one beyond.
        Action<NebulaConfig> crowd = c => { c.InterestNearRadius = 25f; c.InterestFarRadius = 60f; c.InterestMidDivisor = 20; c.InterestFarDivisor = 60; };
        r = RunCrowd(200, 0, updateInterval: 6, recovery: settled, configure: crowd, light: 1000, lightInterval: 30, lightPriority: RelevancePriority.Background, lightWalking: true);
        Record("crowd tiers +1000 background walking", 200, 0, 6, settled, r);
        Assert.That(r.Replicas, Is.EqualTo(1201));

        // And entries a walker needs: yaw only, no velocity (32 bytes), then the same in half floats (24 bytes).
        var yaw = TransformFields.Position | TransformFields.RotationY;
        r = RunCrowd(200, 0, updateInterval: 6, recovery: settled, configure: crowd, light: 1000, lightInterval: 30, lightPriority: RelevancePriority.Background, lightWalking: true, fields: yaw);
        Record("crowd tiers, yaw only +1000 background walking", 200, 0, 6, settled, r);

        r = RunCrowd(200, 0, updateInterval: 6, recovery: settled, configure: crowd, light: 1000, lightInterval: 30, lightPriority: RelevancePriority.Background, lightWalking: true, fields: yaw | TransformFields.Half);
        Record("crowd tiers, yaw only, half +1000 background walking", 200, 0, 6, settled, r);

        // The configuration the sample ships (docs/server-owned-entities.md §5): two updates a second to 60 m, one
        // every two seconds beyond, and the background crowd updated once a second.
        Action<NebulaConfig> sample = c => { c.InterestNearRadius = 25f; c.InterestFarRadius = 60f; c.InterestMidDivisor = 30; c.InterestFarDivisor = 120; };
        r = RunCrowd(200, 0, updateInterval: 6, recovery: settled, configure: sample, light: 1000, lightInterval: 60, lightPriority: RelevancePriority.Background, lightWalking: true, fields: yaw | TransformFields.Half);
        Record("sample tiers, yaw only, half +1000 background walking", 200, 0, 6, settled, r);
        Assert.That(r.Replicas, Is.EqualTo(1201));
        Assert.That(r.KilobitsPerSecond, Is.LessThanOrEqualTo(100.0), "200 active and 1,000 light entities in view within 100 kbit/s");
    }

    private static void RecordWithPlayer(string label, int walking, int interval, Result r)
    {
        _report?.Row(walking, 1000, interval, "settled", label, r.Replicas, r.BytesPerSecond, r.KilobitsPerSecond, r.EntriesPerSecond, r.MessagesPerSecond);
        TestContext.Out.WriteLine($"[scale:synthetic] {label}: replicas={r.Replicas} {r.BytesPerSecond:0} B/s ({r.KilobitsPerSecond:0} kbit/s), {r.EntriesPerSecond:0} entries/s, " +
            $"player at 40 m {r.PlayerEntriesPerSecond:0.0} entries/s, {r.SyncMessagesPerSecond:0} sync messages/s");
    }

    /// <summary>The crowd tiers of docs/server-owned-entities.md D10, set for the crowd's priorities only.</summary>
    private static void CrowdPriorityTiers(NebulaConfig c)
    {
        c.InterestLowTiers = RelevanceTierBands.Of(nearRadius: 25f, farRadius: 60f, nearDivisor: 1, midDivisor: 30, farDivisor: 120);
        c.InterestBackgroundTiers = RelevanceTierBands.Of(nearRadius: 25f, farRadius: 60f, nearDivisor: 30, midDivisor: 0, farDivisor: 0);
    }

    /// <summary>
    /// Distance tiers per priority (docs/server-owned-entities.md D10): 200 active walkers at
    /// <see cref="RelevancePriority.Low"/> updated every 6 ticks and 1,000 light ones at
    /// <see cref="RelevancePriority.Background"/> every 60, with another player, a <see cref="RelevancePriority.Normal"/>
    /// entity updated every tick 40 m away. Global crowd tiers slow the player with the crowd; tiers for the crowd's
    /// priorities alone do not. The last rows add 20 swarms whose members travel as a 200-byte sync blob every 6 ticks,
    /// without and with distance-rated sync state (D11).
    /// </summary>
    [Test]
    public void WithPriorityTiers()
    {
        const FakeWorker.RecoveryMode settled = FakeWorker.RecoveryMode.OnceSettled;
        var yawHalf = TransformFields.Position | TransformFields.RotationY | TransformFields.Half;
        Result Run(Action<NebulaConfig>? configure, TransformFields? fields = null, int swarms = 0, bool swarmRated = false) =>
            RunCrowd(200, 0, updateInterval: 6, recovery: settled, configure: configure, priority: RelevancePriority.Low,
                light: 1000, lightInterval: 60, lightPriority: RelevancePriority.Background, lightWalking: true, fields: fields,
                player: true, swarms: swarms, swarmRated: swarmRated);

        var r = Run(null);
        RecordWithPlayer("default tiers, Low + Background", 200, 6, r);
        Assert.That(r.Replicas, Is.EqualTo(1202));
        Assert.That(r.PlayerEntriesPerSecond, Is.InRange(13.5, 16.5), "the default middle band: every 4th tick");

        Action<NebulaConfig> globalCrowd = c => { c.InterestNearRadius = 25f; c.InterestFarRadius = 60f; c.InterestMidDivisor = 30; c.InterestFarDivisor = 120; };
        r = Run(globalCrowd);
        RecordWithPlayer("global crowd tiers, Low + Background", 200, 6, r);
        Assert.That(r.PlayerEntriesPerSecond, Is.LessThan(3.0), "global crowd tiers slow the player 40 m away to 2 a second");

        r = Run(CrowdPriorityTiers);
        RecordWithPlayer("crowd tiers for Low and Background only", 200, 6, r);
        Assert.That(r.Replicas, Is.EqualTo(1202));
        Assert.That(r.PlayerEntriesPerSecond, Is.InRange(13.5, 16.5), "the player keeps the global middle band's rate");

        r = Run(CrowdPriorityTiers, yawHalf);
        RecordWithPlayer("crowd tiers for Low and Background only, yaw only, half", 200, 6, r);
        Assert.That(r.Replicas, Is.EqualTo(1202));
        Assert.That(r.PlayerEntriesPerSecond, Is.InRange(13.5, 16.5), "the player keeps the global middle band's rate");
        Assert.That(r.KilobitsPerSecond, Is.LessThanOrEqualTo(100.0), "200 active and 1,000 light entities in view within 100 kbit/s, a player unaffected");

        r = Run(CrowdPriorityTiers, yawHalf, swarms: 20, swarmRated: false);
        RecordWithPlayer("the same + 20 swarms, 200-byte sync blob every 6 ticks, unrated", 200, 6, r);
        var unrated = r;
        r = Run(CrowdPriorityTiers, yawHalf, swarms: 20, swarmRated: true);
        RecordWithPlayer("the same + 20 swarms, 200-byte sync blob every 6 ticks, distance-rated", 200, 6, r);
        Assert.That(r.SyncMessagesPerSecond, Is.LessThan(unrated.SyncMessagesPerSecond / 2), "rated sync state reaches far clients at their tier's rate");
    }

    /// <summary>Idle entities cost nothing once spawned: 200 walking among 1,000 standing still.</summary>
    [Test]
    public void BaselineCrowdWithIdleEntities()
    {
        var r = RunCrowd(200, 1000);
        Record("default", 200, 1000, 1, FakeWorker.RecoveryMode.EveryThirtyTicks, r);
        Assert.That(r.Replicas, Is.EqualTo(1201));
    }
}
