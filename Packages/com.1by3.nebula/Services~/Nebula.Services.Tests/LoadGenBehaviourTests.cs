using System.Numerics;
using System.Text.Json;
using Nebula.LoadGen;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// NEB-365: the loadgen's behaviour model. The behaviours run against a fake client that dead-reckons its own
/// movement, so what is checked is what a behaviour asks of its client (where it walks, what it presses, what it
/// sends), with no network. The wire side of the loadgen is unchanged and is covered by the scale suite.
/// </summary>
[TestFixture]
public class LoadGenBehaviourTests
{
    private sealed class FakeClient : IClientContext
    {
        public double Now;
        public Vector2 Move; public bool Sprint; public uint Actions;
        public Vector3 Position;
        public readonly List<(ulong Net, byte Behaviour, string Method, byte[] Args)> Rpcs = new();
        public readonly List<string> Travels = new();
        public readonly List<string> Errors = new();
        public int Counted; public double LeaveFor = double.NaN;
        public ScopeTravelHandler? Handler;

        public FakeClient(int index, int seed = 1) { Index = index; Rng = new Random(BehaviourMix.ClientSeed(seed, index)); }

        public int Index { get; }
        public string Name => "fake" + Index;
        public string BehaviourName => "fake";
        public double Time => Now;
        public Random Rng { get; }
        public bool Connected => true;
        public bool Joined { get; set; } = true;
        public ulong SessionId => 1;
        public ulong PawnNetId => 7;
        public ReplicaInfo? Pawn => null;
        public IReadOnlyCollection<ulong> Replicas => Array.Empty<ulong>();
        public bool TryGetReplica(ulong netId, out ReplicaInfo replica) { replica = null!; return false; }
        public Vector3 EstimatedPosition => Position;
        public void SetEstimatedPosition(Vector3 position) => Position = position;
        public void SetMove(Vector2 direction, bool sprint = false) { Move = direction; Sprint = sprint; }
        public void SetActions(uint bits) => Actions = bits;
        public void SetRawInput(byte[]? payload) { }
        public void SendServerRpc(ulong netId, byte behaviourIndex, uint methodHash, ReadOnlySpan<byte> args) => Rpcs.Add((netId, behaviourIndex, "0x" + methodHash.ToString("x"), args.ToArray()));
        public void SendServerRpc(ulong netId, byte behaviourIndex, string method, ReadOnlySpan<byte> args) => Rpcs.Add((netId, behaviourIndex, method, args.ToArray()));
        public bool RequestScopeTravel(string target)
        {
            if (Handler == null) { Errors.Add("no handler"); return false; }
            Travels.Add(target);
            return Handler(this, target);
        }
        public void SetFocusHint(double x, double y, double z) { }
        public void ClearFocusHint() { }
        public void Leave(double rejoinAfterSeconds) { LeaveFor = rejoinAfterSeconds; Joined = false; }
        public void CountAction() => Counted++;
        public void ReportError(string message) => Errors.Add(message);

        /// <summary>Run the behaviour at 30 Hz for <paramref name="seconds"/>, moving the pawn as the loadgen's dead reckoning would.</summary>
        public void Run(ILoadGenBehaviour b, double seconds, Action? each = null)
        {
            const double dt = 1.0 / 30;
            b.OnJoined(this);
            for (double t = 0; t < seconds; t += dt)
            {
                Now = t;
                b.Tick(this, dt);
                var m = Move.LengthSquared() > 1 ? Vector2.Normalize(Move) : Move;
                Position += new Vector3(m.X, 0, m.Y) * (float)(5 * (Sprint ? 1.6 : 1) * dt);
                each?.Invoke();
                if (!Joined) break;
            }
        }
    }

    private static BehaviourOptions Opts(params (string, string)[] kv) => new(kv.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)));

    // ---- mix -----------------------------------------------------------------------------------------------------

    [Test]
    public void AMixParsesNamesAndWeights()
    {
        var mix = BehaviourMix.Parse("wander:70, travel:20,idle");
        Assert.That(mix.Entries.Select(e => (e.Name, e.Weight)), Is.EqualTo(new[] { ("wander", 70.0), ("travel", 20.0), ("idle", 1.0) }));
        Assert.Throws<ArgumentException>(() => BehaviourMix.Parse(""));
        Assert.Throws<ArgumentException>(() => BehaviourMix.Parse("wander:x"));
        Assert.Throws<ArgumentException>(() => BehaviourMix.Parse("wander:0"));
    }

    [Test]
    public void AMixAssignsExactCountsAndTheSameSeedGivesTheSameClientsTheSameBehaviours()
    {
        var mix = BehaviourMix.Parse("wander:70,travel:20,idle:10");
        var a = mix.Assign(100, seed: 5);
        Assert.That(new[] { 0, 1, 2 }.Select(i => a.Count(x => x == i)), Is.EqualTo(new[] { 70, 20, 10 }));
        Assert.That(mix.Assign(100, 5), Is.EqualTo(a), "same seed, same assignment");
        Assert.That(mix.Assign(100, 6), Is.Not.EqualTo(a), "another seed shuffles");
        // Counts that do not divide evenly still add up to the client count.
        Assert.That(BehaviourMix.Parse("a:1,b:1,c:1").Assign(10, 1).Length, Is.EqualTo(10));
        Assert.That(BehaviourMix.Parse("a:1,b:0").Assign(7, 1), Is.All.EqualTo(0));
    }

    [Test]
    public void AProfileSetsTheMixOptionsAndPlugins()
    {
        var p = LoadProfile.Parse("""
            { "seed": 9, "maxMinutes": 2.5, "inputHz": 20, "plugins": ["x.dll"],  // a comment
              "mix": [ { "behaviour": "wander", "weight": 3, "options": { "radius": 40, "sprint-chance": "0.5", "loop": true } },
                       { "behaviour": "idle" } ] }
            """);
        Assert.That(p.Seed, Is.EqualTo(9));
        Assert.That(p.MaxMinutes, Is.EqualTo(2.5));
        Assert.That(p.InputHz, Is.EqualTo(20));
        Assert.That(p.Plugins, Is.EqualTo(new[] { "x.dll" }));
        Assert.That(p.Mix!.Entries.Select(e => e.Name), Is.EqualTo(new[] { "wander", "idle" }));
        Assert.That(p.Mix.Entries[0].Options.GetDouble("radius", 0), Is.EqualTo(40));
        Assert.That(p.Mix.Entries[0].Options.GetDouble("sprint-chance", 0), Is.EqualTo(0.5));
        Assert.That(p.Mix.Entries[0].Options.GetBool("loop", false), Is.True);
        Assert.Throws<ArgumentException>(() => LoadProfile.Parse("{ \"nope\": 1 }"));
        Assert.Throws<ArgumentException>(() => LoadProfile.Parse("{ \"mix\": [] }"));
    }

    // ---- built-in behaviours -------------------------------------------------------------------------------------

    [Test]
    public void WanderStaysInsideItsRadiusAndStopsAndSprintsSometimes()
    {
        var c = new FakeClient(3);
        var b = new BehaviourRegistry().Create("wander", Opts(("radius", "20")));
        float far = 0; int stopped = 0, sprinting = 0, samples = 0;
        c.Run(b, 600, () =>
        {
            far = Math.Max(far, c.Position.Length());
            samples++;
            if (c.Move == Vector2.Zero) stopped++;
            if (c.Sprint) sprinting++;
        });
        Assert.That(far, Is.LessThan(23), "a leg ends within about a step of the radius");
        Assert.That(far, Is.GreaterThan(5), "it does walk");
        Assert.That(stopped, Is.GreaterThan(samples / 20), "it stops");
        Assert.That(sprinting, Is.GreaterThan(0), "it sprints");
    }

    [Test]
    public void WanderRepeatsForTheSameSeedAndDiffersForAnother()
    {
        Vector3 Final(int seed)
        {
            var c = new FakeClient(0, seed);
            c.Run(new BuiltinBehaviours.Wander(Opts()), 120);
            return c.Position;
        }
        Assert.That(Final(4), Is.EqualTo(Final(4)));
        Assert.That(Final(4), Is.Not.EqualTo(Final(5)));
    }

    [Test]
    public void PathVisitsEachWaypointInOrderAndLoops()
    {
        var c = new FakeClient(0);
        var b = new BuiltinBehaviours.Path(Opts(("points", "10,0|10,10|0,10|0,0"), ("mode", "loop")));
        var reached = new List<int>();
        var corners = new[] { new Vector3(10, 0, 0), new Vector3(10, 0, 10), new Vector3(0, 0, 10), new Vector3(0, 0, 0) };
        int next = 0;
        c.Run(b, 60, () =>
        {
            if (Vector3.Distance(c.Position, corners[next]) < 1.6f) { reached.Add(next); next = (next + 1) % 4; }
        });
        Assert.That(reached.Count, Is.GreaterThanOrEqualTo(5), "a lap takes 8 s, so it laps");
        Assert.That(reached.Take(4), Is.EqualTo(new[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void PathModeOnceStopsAtTheEndAndABadPathIsAnError()
    {
        var c = new FakeClient(0);
        c.Run(new BuiltinBehaviours.Path(Opts(("points", "5,0|5,5"), ("mode", "once"))), 30);
        Assert.That(c.Move, Is.EqualTo(Vector2.Zero));
        Assert.That(Vector3.Distance(c.Position, new Vector3(5, 0, 5)), Is.LessThan(2));
        var bad = new FakeClient(0);
        bad.Run(new BuiltinBehaviours.Path(Opts()), 1);
        Assert.That(bad.Errors, Has.Some.Contains("points"));
    }

    [Test]
    public void BurstPressesTheActionBitOnARhythmAndCountsEachPress()
    {
        var c = new FakeClient(0);
        uint seen = 0;
        c.Run(new BuiltinBehaviours.Burst(Opts(("bit", "3"), ("every", "0.5"), ("hold", "0.1"), ("jitter", "0"))), 10, () => seen |= c.Actions);
        Assert.That(seen, Is.EqualTo(1u << 3));
        Assert.That(c.Counted, Is.InRange(19, 21), "10 s at one press every 0.5 s");
    }

    [Test]
    public void TravelAsksTheHandlerForEachTargetAndWithoutOneItIsAnError()
    {
        var c = new FakeClient(0) { Handler = (_, _) => true };
        c.Run(new BuiltinBehaviours.Travel(Opts(("every-minutes", "0.1"), ("targets", "a|b|c"), ("order", "cycle"))), 40);
        Assert.That(c.Travels.Count, Is.InRange(5, 8), "one every 6 s, the first at a random point inside the first 6 s");
        Assert.That(c.Travels.Take(3), Is.EqualTo(new[] { "a", "b", "c" }));
        var none = new FakeClient(0);
        none.Run(new BuiltinBehaviours.Travel(Opts(("every-minutes", "0.1"), ("targets", "a"))), 20);
        Assert.That(none.Errors, Is.Not.Empty);
    }

    [Test]
    public void RpcSendsAtTheConfiguredRateWithTheConfiguredPayload()
    {
        var c = new FakeClient(1);
        c.Run(new BuiltinBehaviours.RpcAtRate(Opts(("method", "Ping"), ("rate", "4"), ("args", "0aff"), ("behaviour", "2"))), 100);
        Assert.That(c.Rpcs.Count, Is.InRange(300, 500), "4 a second for 100 s, Poisson");
        Assert.That(c.Rpcs[0], Is.EqualTo((0UL, (byte)2, "Ping", new byte[] { 0x0a, 0xff })));
        var hashed = new FakeClient(1);
        hashed.Run(new BuiltinBehaviours.RpcAtRate(Opts(("method", "0xdeadbeef"), ("rate", "10"))), 5);
        Assert.That(hashed.Rpcs[0].Method, Is.EqualTo("0xdeadbeef"));
        var bad = new FakeClient(1);
        bad.Run(new BuiltinBehaviours.RpcAtRate(Opts()), 1);
        Assert.That(bad.Errors, Has.Some.Contains("method"));
    }

    [Test]
    public void ChurnLeavesAfterItsStayAndAsksToRejoin()
    {
        var c = new FakeClient(0);
        c.Run(new BuiltinBehaviours.Churn(Opts(("on", "20"), ("off", "5"), ("jitter", "0"))), 60);
        Assert.That(c.LeaveFor, Is.EqualTo(5).Within(0.001));
        Assert.That(c.Counted, Is.EqualTo(1));
    }

    [Test]
    public void ABehaviourThatDoesNothingSendsNoMoves()
    {
        var c = new FakeClient(0);
        c.Run(new BuiltinBehaviours.Idle(), 5);
        Assert.That(c.Move, Is.EqualTo(Vector2.Zero));
        Assert.That(c.Rpcs, Is.Empty);
    }

    // ---- registry, plugins, input --------------------------------------------------------------------------------

    [Test]
    public void TheRegistryKnowsTheBuiltinsAndRefusesAnUnknownName()
    {
        var r = new BehaviourRegistry();
        foreach (string name in new[] { "idle", "wander", "path", "burst", "travel", "rpc", "churn" }) Assert.That(r.Has(name), name);
        Assert.That(r.Has("WANDER"), "names are case-insensitive");
        var e = Assert.Throws<ArgumentException>(() => r.Create("fly", new BehaviourOptions()))!;
        Assert.That(e.Message, Does.Contain("wander"), "the error lists the known behaviours");
    }

    [Test]
    public void APluginLoadedFromAFileRegistersItsBehaviours()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Nebula.LoadGen.SamplePlugin.dll");
        Assert.That(File.Exists(path), "the sample plugin is built beside the tests");
        var r = new BehaviourRegistry();
        Assert.That(r.LoadPlugin(path), Is.EqualTo(1));
        Assert.That(r.Has("strafe"));
        var c = new FakeClient(0);
        var b = r.Create("strafe", Opts(("length", "10")));
        float maxX = 0; bool back = false;
        c.Run(b, 30, () => { maxX = Math.Max(maxX, c.Position.X); if (maxX > 8 && c.Position.X < 2) back = true; });
        Assert.That(maxX, Is.GreaterThan(8));
        Assert.That(back, "it turns around and walks back");
        Assert.That(c.Counted, Is.GreaterThan(0), "and presses a bit on each turn");
        Assert.Throws<FileNotFoundException>(() => r.LoadPlugin(Path.Combine(AppContext.BaseDirectory, "nope.dll")));
    }

    [Test]
    public void ARegisteredEncoderAndScopeTravelReplaceTheDefaults()
    {
        var r = new BehaviourRegistry();
        Assert.That(r.ScopeTravel, Is.Null);
        r.SetScopeTravel((_, _) => true);
        Assert.That(r.ScopeTravel, Is.Not.Null);
        var buf = new byte[256];
        int n = r.InputEncoder.Encode(new InputSnapshot { Tick = 0x1ff, ClientIndex = 2, Move = new Vector2(1, -1), Sprint = true, Actions = 5 }, buf);
        Assert.That(n, Is.EqualTo(16));
        Assert.That(buf[0], Is.EqualTo(0xff));
        Assert.That(buf[1], Is.EqualTo(2));
        Assert.That(buf[2], Is.EqualTo(1));
        Assert.That(BitConverter.ToSingle(buf, 4), Is.EqualTo(1f));
        Assert.That(BitConverter.ToSingle(buf, 8), Is.EqualTo(-1f));
        Assert.That(BitConverter.ToUInt32(buf, 12), Is.EqualTo(5u));
    }

    // ---- report rows ---------------------------------------------------------------------------------------------

    [Test]
    public void AReportRowIsOneValidJsonLineAndOneCsvLineWithTheHeadersColumns()
    {
        var row = new BehaviourReportRow { T = 12.5, Behaviour = "strafe,\"x\"", Clients = 10, Connected = 9, Joined = 8, InputsPerSec = 600, ActionsPerSec = 3.25, Actions = 40, Rpcs = 31, Errors = 2, RttP50 = 15.5, BytesInPerSec = 2048 };
        string json = row.ToJson();
        Assert.That(json, Does.Not.Contain("\n"));
        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("behaviour").GetString(), Is.EqualTo("strafe,\"x\""));
        Assert.That(doc.RootElement.GetProperty("actionsPerSec").GetDouble(), Is.EqualTo(3.25));
        Assert.That(doc.RootElement.GetProperty("errors").GetInt64(), Is.EqualTo(2));
        Assert.That(doc.RootElement.GetProperty("rpcs").GetInt64(), Is.EqualTo(31));
        var headerColumns = BehaviourReportRow.CsvHeader.Split(',');
        Assert.That(doc.RootElement.EnumerateObject().Select(p => p.Name), Is.EqualTo(headerColumns));
        string csv = new BehaviourReportRow { Behaviour = "wander", Clients = 3 }.ToCsv();
        Assert.That(csv.Split(',').Length, Is.EqualTo(headerColumns.Length));
        Assert.That(csv, Does.StartWith("0.0,wander,3,"));
    }
}
