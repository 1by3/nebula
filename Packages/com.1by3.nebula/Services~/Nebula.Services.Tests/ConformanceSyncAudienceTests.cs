using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;
using Flags = Nebula.SyncStateCodec.ChunkFlags;

namespace Nebula.ServiceTests;

/// <summary>
/// Sync audiences at the gateway (NEB-321, <c>docs/sync-audience.md</c>): a real <see cref="NebulaGateway"/> over
/// loopback UDP, fed exactly the chunks a worker writes, and clients that record every sync chunk that reaches them.
/// The question each test asks is whether a client can end up holding a restricted chunk's bytes it is not in the
/// audience of, on any path: a spawn, a delta, a keyframe on either stream, the cached keyframes of a late joiner, a
/// handover, a reclaimed session. And whether a client that joins an audience gets a keyframe at once, and one that
/// leaves is told to drop its copy. The worker half of the contract is the EditMode suite's
/// <c>ConformanceSyncAudienceTests</c>.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceSyncAudienceTests
{
    private const byte Public = 0, Private = 1, Hidden = 2, Chosen = 3;
    private static readonly Flags Owner = SyncStateCodec.FlagsOf(SyncAudience.Owner);
    private static readonly Flags WorkersOnly = SyncStateCodec.FlagsOf(SyncAudience.WorkersOnly);
    private static readonly Flags Custom = SyncStateCodec.FlagsOf(SyncAudience.Custom);

    private string _directory = "";

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-audience-tests-" + Guid.NewGuid().ToString("N"));
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

    private static Fleet Mesh(int workers = 1) =>
        new(1, workers: workers, world: f => { for (int i = 0; i < 4; i++) f.Assign("c" + i, "w1"); });

    private static FakeClient Join(Fleet fleet, string name, string token = "", string session = "")
    {
        var client = fleet.Connect(0, name, token, session);
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined), Is.True, $"{name} should get a pawn");
        return client;
    }

    private static ulong Id(FakeClient c) => c.Welcome!.Value.ClientId;
    private static byte[] B(byte behaviour, byte version) => new[] { behaviour, version };

    /// <summary>Whether a client was ever sent this behaviour's chunk for the entity (optionally a given version of it, or by a given path).</summary>
    private static bool Got(FakeClient c, ulong netId, byte index, byte? version = null, string? via = null) =>
        c.SyncChunks.Exists(s => s.NetId == netId && s.Index == index && (s.Flags & Flags.Cleared) == 0 &&
                                 (version == null || (s.Bytes.Length > 1 && s.Bytes[1] == version)) && (via == null || s.Via == via));

    private static bool GotCleared(FakeClient c, ulong netId, byte index) =>
        c.SyncChunks.Exists(s => s.NetId == netId && s.Index == index && (s.Flags & Flags.Cleared) != 0);

    private static void AssertNoLeaks(params FakeClient[] clients)
    {
        foreach (var c in clients)
        {
            Assert.That(c.AudienceLeaks, Is.Zero, "a client is never sent an audience generation or member sets");
            Assert.That(c.LastError, Is.Empty);
        }
    }

    // ------------------------------------------------------------------------------------------- Owner

    [Test]
    public void OwnerStateReachesTheOwnerAndNoOtherClientOnAnyPath()
    {
        using var fleet = Mesh();
        var ann = Join(fleet, "ann");
        var bob = Join(fleet, "bob");
        ulong pawn = fleet.Worker.Pawns[Id(ann)];
        Assert.That(fleet.Run(() => bob.Replicas.Contains(pawn)), Is.True, "bob stands next to ann and holds her pawn");

        // Spawn: the worker re-announces the pawn with a snapshot of a public and an owner-only behaviour.
        fleet.Worker.Entities[pawn].State = FakeWorker.Envelope((Public, Flags.Full, B(Public, 1)), (Private, Flags.Full | Owner, B(Private, 1)));
        fleet.Worker.Reannounce(pawn, newEpoch: false);
        Assert.That(fleet.Run(() => Got(ann, pawn, Private, 1, "spawn") && Got(bob, pawn, Public, 1, "spawn")), Is.True);

        // A reliable delta and a sequenced keyframe.
        fleet.Worker.SendSync(pawn, reliable: true, tick: 10, generation: 0, (Public, Flags.None, B(Public, 2)), (Private, Owner, B(Private, 2)));
        fleet.Worker.SendSync(pawn, reliable: false, tick: 30, generation: 0, (Private, Flags.Full | Owner, B(Private, 3)));
        Assert.That(fleet.Run(() => Got(ann, pawn, Private, 2, "reliable") && Got(ann, pawn, Private, 3, "sequenced") && Got(bob, pawn, Public, 2)), Is.True);

        // A late joiner's spawn comes from the gateway's cached keyframes.
        var cid = Join(fleet, "cid");
        Assert.That(fleet.Run(() => Got(cid, pawn, Public, 1, "spawn")), Is.True, "a late joiner starts from the cached public keyframe");
        fleet.RunFor(0.3);

        Assert.That(Got(bob, pawn, Private), Is.False, "bob never received a byte of ann's owner-only state");
        Assert.That(Got(cid, pawn, Private), Is.False, "nor did the late joiner, from the cache");
        Assert.That(ann.SyncChunks.FindAll(s => s.NetId == pawn && s.Index == Private).ConvertAll(s => s.Bytes[1]), Is.EqualTo(new byte[] { 1, 2, 3 }),
            "ann received every update, in order");
        AssertNoLeaks(ann, bob, cid);
    }

    [Test]
    public void AWorkersOnlyChunkNeverReachesAClient()
    {
        using var fleet = Mesh();
        var ann = Join(fleet, "ann");
        ulong pawn = fleet.Worker.Pawns[Id(ann)];
        // A worker never sends a gateway one; if one arrives anyway, in a spawn or in the stream, the gateway drops it
        // and keeps the rest.
        fleet.Worker.Entities[pawn].State = FakeWorker.Envelope((Public, Flags.Full, B(Public, 1)), (Hidden, Flags.Full | WorkersOnly, B(Hidden, 1)));
        fleet.Worker.Reannounce(pawn, newEpoch: false);
        Assert.That(fleet.Run(() => Got(ann, pawn, Public, 1, "spawn")), Is.True);
        fleet.Worker.SendSync(pawn, reliable: true, tick: 5, generation: 0, (Public, Flags.Full, B(Public, 2)), (Hidden, Flags.Full | WorkersOnly, B(Hidden, 2)));
        Assert.That(fleet.Run(() => Got(ann, pawn, Public, 2)), Is.True);
        var late = Join(fleet, "late");
        Assert.That(fleet.Run(() => Got(late, pawn, Public, null, "spawn")), Is.True, "a late joiner is sent the public keyframe");
        fleet.RunFor(0.3);
        Assert.That(Got(ann, pawn, Hidden) || Got(late, pawn, Hidden), Is.False, "not the owner, not a late joiner, nobody");
        AssertNoLeaks(ann, late);
    }

    [Test]
    public void AnOwnerChangeMovesTheAudience()
    {
        using var fleet = Mesh();
        var ann = Join(fleet, "ann");
        var bob = Join(fleet, "bob");
        // A container of ann's next to both pawns, with an owner-only inventory.
        var crate = fleet.Worker.Spawn(5001, new Vector3(2, 0, 0), owner: Id(ann));
        crate.State = FakeWorker.Envelope((Private, Flags.Full | Owner, B(Private, 1)));
        fleet.Worker.Reannounce(5001, newEpoch: false);
        Assert.That(fleet.Run(() => Got(ann, 5001, Private, 1) && bob.Replicas.Contains(5001)), Is.True);

        // Authority moves on with a new owner (a handover to a worker that now answers for bob).
        crate.OwnerClientId = Id(bob);
        crate.State = FakeWorker.Envelope((Private, Flags.Full | Owner, B(Private, 2)));
        fleet.Worker.Reannounce(5001);
        Assert.That(fleet.Run(() => Got(bob, 5001, Private, 2, "spawn") && GotCleared(ann, 5001, Private)), Is.True,
            "the new owner gets a keyframe with the spawn, the old one is told to drop its copy");

        fleet.Worker.SendSync(5001, reliable: true, tick: 40, generation: 0, (Private, Owner, B(Private, 3)));
        Assert.That(fleet.Run(() => Got(bob, 5001, Private, 3)), Is.True);
        fleet.RunFor(0.3);
        Assert.That(Got(ann, 5001, Private, 2) || Got(ann, 5001, Private, 3), Is.False, "nothing written for the new owner reaches the old one");
        AssertNoLeaks(ann, bob);
    }

    [Test]
    public void TheOwnerKeepsItsStateThroughAHandoverAndAReclaimedSession()
    {
        using var fleet = Mesh(workers: 2);
        var ann = Join(fleet, "ann");
        var bob = Join(fleet, "bob");
        var w1 = fleet.Workers[0];
        var w2 = fleet.Workers[1];
        ulong pawn = w1.Pawns[Id(ann)];
        w1.Entities[pawn].State = FakeWorker.Envelope((Public, Flags.Full, B(Public, 1)), (Private, Flags.Full | Owner, B(Private, 1)));
        w1.Reannounce(pawn, newEpoch: false);
        Assert.That(fleet.Run(() => Got(ann, pawn, Private, 1) && bob.Replicas.Contains(pawn)), Is.True);

        // Handover: the new owner announces the pawn with its own snapshot, and streams on.
        fleet.Assign("c0", "w2");
        Assert.That(fleet.Run(() => w2.Gateways.Count > 0, seconds: 10), Is.True, "the gateway links the new owner");
        w1.HandOver(pawn, w2);
        w2.Entities[pawn].State = FakeWorker.Envelope((Public, Flags.Full, B(Public, 2)), (Private, Flags.Full | Owner, B(Private, 2)));
        w2.Reannounce(pawn, newEpoch: false);
        Assert.That(fleet.Run(() => Got(ann, pawn, Private, 2)), Is.True, "the owner's state follows the pawn to its new worker");
        w2.SendSync(pawn, reliable: false, tick: 60, generation: 0, (Private, Flags.Full | Owner, B(Private, 3)));
        Assert.That(fleet.Run(() => Got(ann, pawn, Private, 3)), Is.True);

        // Ann's connection drops and she comes back with her session token: same session id, new connection.
        var welcome = ann.Welcome!.Value;
        ann.Disconnect();
        var back = Join(fleet, "ann", welcome.Token, welcome.SessionToken);
        Assert.That(Id(back), Is.EqualTo(welcome.ClientId), "the session was reclaimed");
        // Its spawn comes from the gateway's cache, or from the worker's re-announcement of the pawn for the reclaim,
        // whichever is first; either way it holds the owner keyframe.
        Assert.That(fleet.Run(() => Got(back, pawn, Private, null, "spawn")), Is.True, "the reclaimed session starts from an owner keyframe");
        w2.SendSync(pawn, reliable: true, tick: 70, generation: 0, (Private, Owner, B(Private, 4)));
        Assert.That(fleet.Run(() => Got(back, pawn, Private, 4)), Is.True, "and receives every update after it");

        fleet.RunFor(0.3);
        Assert.That(Got(bob, pawn, Private), Is.False, "bob never saw any of it: not across the handover, not across the reclaim");
        Assert.That(Got(bob, pawn, Public, 2), Is.True, "while the public behaviour reached him throughout");
        AssertNoLeaks(back, bob);
    }

    // ------------------------------------------------------------------------------------------- Custom

    [Test]
    public void ACustomAudienceAddsAndRemovesClientsAsItsAnswerChanges()
    {
        using var fleet = Mesh();
        var ann = Join(fleet, "ann");
        var bob = Join(fleet, "bob");
        fleet.Worker.Spawn(6001, new Vector3(3, 0, 0));
        Assert.That(fleet.Run(() => ann.Replicas.Contains(6001) && bob.Replicas.Contains(6001)), Is.True);

        // Before any member set arrives, a Custom chunk goes to nobody.
        fleet.Worker.SendSync(6001, reliable: true, tick: 1, generation: 0, (Chosen, Flags.Full | Custom, B(Chosen, 1)));
        fleet.RunFor(0.3);
        Assert.That(Got(ann, 6001, Chosen) || Got(bob, 6001, Chosen), Is.False, "fail closed: no set, no member");

        // Ann opens the container: she joins and gets the newest keyframe at once.
        fleet.Worker.SendAudience(6001, 1, (Chosen, new[] { Id(ann) }));
        Assert.That(fleet.Run(() => Got(ann, 6001, Chosen, 1, "reliable")), Is.True, "a joiner is sent the cached keyframe");
        fleet.Worker.SendSync(6001, reliable: true, tick: 2, generation: 1, (Chosen, Custom, B(Chosen, 2)));
        Assert.That(fleet.Run(() => Got(ann, 6001, Chosen, 2)), Is.True);

        // Ann closes it and Bob opens it: Ann is told to drop her copy, Bob gets the keyframe.
        fleet.Worker.SendAudience(6001, 2, (Chosen, new[] { Id(bob) }));
        Assert.That(fleet.Run(() => GotCleared(ann, 6001, Chosen) && Got(bob, 6001, Chosen, 1)), Is.True);

        // A keyframe written under generation 3 that overtakes its member sets goes to nobody until they arrive.
        fleet.Worker.SendSync(6001, reliable: false, tick: 30, generation: 3, (Chosen, Flags.Full | Custom, B(Chosen, 9)));
        fleet.RunFor(0.3);
        Assert.That(Got(ann, 6001, Chosen, 9) || Got(bob, 6001, Chosen, 9), Is.False, "held back: the sets that decide it are still on their way");
        fleet.Worker.SendAudience(6001, 3, (Chosen, new[] { Id(ann), Id(bob) }));
        Assert.That(fleet.Run(() => Got(ann, 6001, Chosen, 9, "reliable")), Is.True, "ann rejoins with the newest keyframe");

        // A late joiner outside the set starts from the cache without the Custom chunk.
        var cid = Join(fleet, "cid");
        Assert.That(fleet.Run(() => cid.Replicas.Contains(6001)), Is.True);
        fleet.RunFor(0.3);
        Assert.That(Got(cid, 6001, Chosen), Is.False);

        var annChosen = ann.SyncChunks.FindAll(s => s.NetId == 6001 && s.Index == Chosen);
        int clearedAt = annChosen.FindIndex(s => (s.Flags & Flags.Cleared) != 0);
        Assert.That(annChosen.Skip(clearedAt + 1).Select(s => s.Bytes[1]), Is.EqualTo(new byte[] { 9 }),
            "between leaving and rejoining, ann received nothing of it");
        AssertNoLeaks(ann, bob, cid);
    }

    [Test]
    public void AProtocol19ClientStopsReceivingButIsNotSentTheNotice()
    {
        using var fleet = Mesh();
        var ann = fleet.Connect(0, "ann");
        ann.AnnounceVersion = 19;
        Assert.That(fleet.Run(() => ann.Join == JoinState.Joined), Is.True);
        Assert.That(ann.Welcome!.Value.NegotiatedVersion, Is.EqualTo((ushort)19));
        fleet.Worker.Spawn(6002, new Vector3(3, 0, 0));
        Assert.That(fleet.Run(() => ann.Replicas.Contains(6002)), Is.True);
        fleet.Worker.SendSync(6002, reliable: true, tick: 1, generation: 0, (Chosen, Flags.Full | Custom, B(Chosen, 1)));
        fleet.Worker.SendAudience(6002, 1, (Chosen, new[] { Id(ann) }));
        Assert.That(fleet.Run(() => Got(ann, 6002, Chosen, 1)), Is.True);
        fleet.Worker.SendAudience(6002, 2, (Chosen, Array.Empty<ulong>()));
        fleet.Worker.SendSync(6002, reliable: true, tick: 2, generation: 2, (Chosen, Custom, B(Chosen, 2)));
        fleet.RunFor(0.5);
        Assert.That(GotCleared(ann, 6002, Chosen), Is.False, "a protocol-19 client cannot read the notice");
        Assert.That(Got(ann, 6002, Chosen, 2), Is.False, "but it is sent nothing more of the state");
        AssertNoLeaks(ann);
    }

    [Test]
    public void AnEntityWithoutRestrictedBehavioursIsRelayedAsBefore()
    {
        using var fleet = Mesh();
        var ann = Join(fleet, "ann");
        var bob = Join(fleet, "bob");
        fleet.Worker.Spawn(6003, new Vector3(3, 0, 0));
        Assert.That(fleet.Run(() => ann.Replicas.Contains(6003) && bob.Replicas.Contains(6003)), Is.True);
        fleet.Worker.SendSync(6003, reliable: false, tick: 1, generation: 0, (Public, Flags.Full, B(Public, 1)));
        fleet.Worker.SendSync(6003, reliable: true, tick: 2, generation: 0, (Public, Flags.None, B(Public, 2)));
        Assert.That(fleet.Run(() => Got(ann, 6003, Public, 2) && Got(bob, 6003, Public, 2)), Is.True);
        // The two streams are separate channels, so only the set is certain, not the order.
        Assert.That(ann.SyncChunks.FindAll(s => s.NetId == 6003).ConvertAll(s => s.Flags), Is.EquivalentTo(new[] { Flags.Full, Flags.None }),
            "the chunks arrive exactly as written");
        AssertNoLeaks(ann, bob);
    }
}
