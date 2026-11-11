using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Replicated maps at the gateway (NEB-335, <c>docs/replicated-collections.md</c> D6, D12): a real
/// <see cref="NebulaGateway"/> over loopback UDP, fed the map sections a worker writes, and clients that apply every
/// spawn and every <see cref="MsgId.EntityMaps"/> they are sent. The question each test asks is whether a client ends
/// up holding exactly the authority's contents, and whether it got there with one full copy and increments only: a
/// client present from the start, a late joiner, a new owner's announcement, a stale update. The worker half of the
/// contract is the EditMode suite's <c>ConformanceNetworkMapTests</c>.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceNetworkMapTests
{
    private string _directory = "";

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-map-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        var containers = new List<Container>();
        for (int i = 0; i < 4; i++)
            containers.Add(new Container
            {
                ContainerId = "c" + i, Index = (ushort)i, Size = new Vector3(64, 64, 64),
                transform = new ContainerFrame { position = new Vector3(i * 64 + 32, 0, 0) },
            });
        var path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest { Containers = containers }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private static Fleet Mesh() => new(1, workers: 1, world: f => { for (int i = 0; i < 4; i++) f.Assign("c" + i, "w1"); });

    private static FakeClient Join(Fleet fleet, string name)
    {
        var client = fleet.Connect(0, name);
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined), Is.True, $"{name} should get a pawn");
        return client;
    }

    private static byte[] Key(string k) { var w = new NetworkWriter(); w.WriteString(k); return w.ToArray(); }

    /// <summary>A section of one map (index 0) as a worker writes it: <c>null</c> as a value is a removal.</summary>
    private static byte[] Section(bool clear, params (string Key, string? Value)[] entries)
    {
        var w = new NetworkWriter();
        int countAt = NetworkMapCodec.BeginSection(w);
        int lengthAt = NetworkMapCodec.BeginMap(w, 0);
        int bodyAt = NetworkMapCodec.BeginBody(w, clear);
        foreach (var (key, value) in entries)
        {
            if (value == null) { NetworkMapCodec.WriteRemove(w, new ArraySegment<byte>(Key(key))); continue; }
            var v = new NetworkWriter();
            v.WriteString(value);
            NetworkMapCodec.WriteSet(w, new ArraySegment<byte>(Key(key)), v.ToSegment());
        }
        NetworkMapCodec.EndBody(w, bodyAt, entries.Length);
        NetworkMapCodec.EndMap(w, lengthAt);
        NetworkMapCodec.EndSection(w, countAt, 1);
        return w.ToArray();
    }

    private static byte[] Full(params (string Key, string Value)[] entries) =>
        Section(true, Array.ConvertAll(entries, e => (e.Key, (string?)e.Value)));

    /// <summary>What a client holds for one map, decoded: key to value.</summary>
    private static Dictionary<string, string> Held(FakeClient c, ulong netId)
    {
        var result = new Dictionary<string, string>();
        if (!c.MapsOf.TryGetValue(netId, out var cache)) return result;
        NetworkMapCodec.ReadSection(new ArraySegment<byte>(cache.Encode()), (index, body) =>
        {
            var entries = new List<NetworkMapCodec.Entry>();
            NetworkMapCodec.ReadBody(new NetworkReader(body), out _, entries);
            foreach (var e in entries) result[new NetworkReader(e.Key).ReadString()] = new NetworkReader(e.Value).ReadString();
        });
        return result;
    }

    private static bool Holds(FakeClient c, ulong netId, params (string Key, string Value)[] expected)
    {
        var held = Held(c, netId);
        if (held.Count != expected.Length) return false;
        foreach (var (k, v) in expected) if (!held.TryGetValue(k, out var got) || got != v) return false;
        return true;
    }

    private static int EntriesIn(byte[] section)
    {
        int n = 0;
        NetworkMapCodec.ReadSection(new ArraySegment<byte>(section), (index, body) =>
        {
            var entries = new List<NetworkMapCodec.Entry>();
            NetworkMapCodec.ReadBody(new NetworkReader(body), out _, entries);
            n += entries.Count;
        });
        return n;
    }

    [Test]
    public void ALateJoinerGetsTheCurrentContentsThenTheSameIncrements()
    {
        using var fleet = Mesh();
        var ann = Join(fleet, "ann");
        var crate = fleet.Worker.Spawn(7001, new Vector3(3, 0, 0), alwaysRelevant: true);
        crate.Maps = Full(("a", "1"), ("b", "2"));
        fleet.Worker.Reannounce(7001, newEpoch: false);
        Assert.That(fleet.Run(() => Holds(ann, 7001, ("a", "1"), ("b", "2"))), Is.True, "ann gets the full copy with the spawn");

        // An increment: one entry changed, one removed, one added. Only those three travel.
        fleet.Worker.SendMaps(7001, Section(false, ("b", "3"), ("a", null), ("c", "4")));
        Assert.That(fleet.Run(() => Holds(ann, 7001, ("b", "3"), ("c", "4"))), Is.True);
        Assert.That(EntriesIn(ann.MapDeltas[^1].Maps), Is.EqualTo(3), "ann was sent the three changed entries, not the map");

        // A late joiner: its spawn is built from the gateway's copy, which has the increment in it.
        var bob = Join(fleet, "bob");
        Assert.That(fleet.Run(() => bob.Replicas.Contains(7001)), Is.True);
        Assert.That(fleet.Run(() => Holds(bob, 7001, ("b", "3"), ("c", "4"))), Is.True, "the current contents, not the spawn-time ones");
        Assert.That(bob.SpawnMaps.Exists(s => s.NetId == 7001), Is.True, "in one full copy, with the spawn");
        Assert.That(bob.MapDeltas.Exists(d => d.NetId == 7001), Is.False, "and no increment from before it joined");

        // From now on both get the same increments.
        fleet.Worker.SendMaps(7001, Section(false, ("d", "5")));
        Assert.That(fleet.Run(() => Holds(ann, 7001, ("b", "3"), ("c", "4"), ("d", "5")) && Holds(bob, 7001, ("b", "3"), ("c", "4"), ("d", "5"))), Is.True);
        Assert.That(EntriesIn(bob.MapDeltas[^1].Maps), Is.EqualTo(1));

        // A clear is an increment too.
        fleet.Worker.SendMaps(7001, Section(true, ("z", "9")));
        Assert.That(fleet.Run(() => Holds(ann, 7001, ("z", "9")) && Holds(bob, 7001, ("z", "9"))), Is.True);
        var cid = Join(fleet, "cid");
        Assert.That(fleet.Run(() => Holds(cid, 7001, ("z", "9"))), Is.True, "a later joiner sees the map after the clear");
        Assert.That(ann.LastError + bob.LastError + cid.LastError, Is.Empty);
    }

    /// <summary>A full section of <paramref name="maps"/> maps, each with <paramref name="entries"/> entries of <paramref name="valueBytes"/> bytes.</summary>
    private static byte[] BigSection(bool clear, int maps, int entries, int valueBytes, char fill)
    {
        var w = new NetworkWriter();
        int countAt = NetworkMapCodec.BeginSection(w);
        for (int m = 0; m < maps; m++)
        {
            int lengthAt = NetworkMapCodec.BeginMap(w, (byte)m);
            int bodyAt = NetworkMapCodec.BeginBody(w, clear);
            for (int i = 0; i < entries; i++)
            {
                var v = new NetworkWriter();
                v.WriteString(new string(fill, valueBytes));
                NetworkMapCodec.WriteSet(w, new ArraySegment<byte>(Key($"m{m}k{i}")), v.ToSegment());
            }
            NetworkMapCodec.EndBody(w, bodyAt, entries);
            NetworkMapCodec.EndMap(w, lengthAt);
        }
        NetworkMapCodec.EndSection(w, countAt, (byte)maps);
        return w.ToArray();
    }

    /// <summary>
    /// NEB-502: a reliable message to a client that is longer than 65,535 bytes (here an entity whose maps together
    /// hold about 92 KB, each under its own cap) must reach it whole. The gateway's reliable batch frames each message
    /// with a 16-bit length, so such a message travels on its own, in order with the batches around it.
    /// </summary>
    [Test]
    public void MapsLargerThanABatchFrameReachAClientPresentAndALateJoiner()
    {
        using var fleet = Mesh();
        var ann = Join(fleet, "ann");
        var crate = fleet.Worker.Spawn(7003, new Vector3(3, 0, 0), alwaysRelevant: true);
        crate.Maps = BigSection(true, maps: 2, entries: 46, valueBytes: 1000, fill: 'x');
        Assert.That(crate.Maps.Length, Is.GreaterThan(ushort.MaxValue), "the section is longer than a 16-bit length can say");
        fleet.Worker.Reannounce(7003, newEpoch: false);
        Assert.That(fleet.Run(() => Held(ann, 7003).Count == 92), Is.True, "a client present gets the full copy with the spawn: " + ann.LastError);

        // What is queued before and after the long message still arrives in order: a small change, then every entry
        // again in one update (itself longer than a frame), then another small change.
        fleet.Worker.SendMaps(7003, Section(false, ("before", "1")));
        fleet.Worker.SendMaps(7003, BigSection(false, maps: 2, entries: 46, valueBytes: 1000, fill: 'y'));
        fleet.Worker.SendMaps(7003, Section(false, ("before", "2"), ("after", "3")));
        Assert.That(fleet.Run(() => Held(ann, 7003).Count == 94 && Held(ann, 7003)["before"] == "2"), Is.True, "the updates arrive in order: " + ann.LastError);
        Assert.That(Held(ann, 7003)["m1k45"], Is.EqualTo(new string('y', 1000)));

        var bob = Join(fleet, "bob");
        Assert.That(fleet.Run(() => bob.Replicas.Contains(7003)), Is.True, "the late joiner is sent the entity: " + bob.LastError);
        Assert.That(fleet.Run(() => Held(bob, 7003).Count == 94), Is.True, "with every entry of both maps: " + bob.LastError);
        Assert.That(Held(bob, 7003)["m0k0"], Is.EqualTo(new string('y', 1000)));
        Assert.That(Held(bob, 7003)["before"], Is.EqualTo("2"));

        // Both keep receiving increments afterwards.
        fleet.Worker.SendMaps(7003, Section(false, ("last", "4")));
        Assert.That(fleet.Run(() => Held(ann, 7003).ContainsKey("last") && Held(bob, 7003).ContainsKey("last")), Is.True);
        Assert.That(ann.LastError + bob.LastError, Is.Empty);
    }

    [Test]
    public void AStaleUpdateIsIgnoredAndANewOwnersSpawnReplacesTheCopy()
    {
        using var fleet = Mesh();
        var ann = Join(fleet, "ann");
        var crate = fleet.Worker.Spawn(7002, new Vector3(3, 0, 0), alwaysRelevant: true);
        crate.Maps = Full(("a", "1"));
        fleet.Worker.Reannounce(7002, newEpoch: true);
        Assert.That(fleet.Run(() => Holds(ann, 7002, ("a", "1"))), Is.True);

        // An update from an older epoch (the previous owner's, overtaken by the handover) changes nothing.
        fleet.Worker.SendMaps(7002, Section(false, ("a", "old")), epoch: crate.Epoch - 1);
        fleet.RunFor(0.3);
        Assert.That(Holds(ann, 7002, ("a", "1")), Is.True);
        Assert.That(ann.MapDeltas.Exists(d => d.NetId == 7002), Is.False, "the stale update is not relayed");

        // The new owner announces itself with its full copy: the gateway's copy and every observer's are replaced.
        crate.Maps = Full(("a", "2"), ("n", "1"));
        fleet.Worker.Reannounce(7002, newEpoch: true);
        Assert.That(fleet.Run(() => Holds(ann, 7002, ("a", "2"), ("n", "1"))), Is.True);
        var bob = Join(fleet, "bob");
        Assert.That(fleet.Run(() => Holds(bob, 7002, ("a", "2"), ("n", "1"))), Is.True, "a late joiner gets the new owner's copy");
    }
}
