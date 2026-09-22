using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Drain and replace, the procedure a rolling upgrade is made of (NEB-228,
/// <c>docs/compatibility-policy.md</c> D5; scale suite scenarios S9a and S9b). Nebula ships no upgrade command:
/// what it ships is a gateway that can be drained while its clients keep playing and a worker whose containers
/// can be handed to another process before it goes. These tests are the proof that both survive the traffic.
/// <list type="bullet">
/// <item><b>S9a</b>: a gateway is asked to drain, its clients move to another gateway of the fleet, the drained
/// process is stopped and a replacement is started. No session is lost and no client has to sign in again.</item>
/// <item><b>S9b</b>: a worker's containers and entities are handed to another worker, the drained worker leaves,
/// and a replacement joins. No entity is lost and no client is disconnected.</item>
/// </list>
/// Tier A of the scale suite: real gateways on real sockets, <see cref="FakeWorker"/>s and an in-process
/// <see cref="LocalControlPlane"/> (<c>docs/scale-suite.md</c> D1). The processes are objects rather than
/// operating-system processes, which is what these tests cannot prove — see <c>docs/compatibility-policy.md</c>
/// "what was verified".
/// </summary>
[TestFixture]
[Category("Scale")]
public class RollingUpgradeTests
{
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-rolling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new()
            {
                new Container { ContainerId = "c0", Index = 0, Size = new(64, 64, 64), transform = new ContainerFrame { position = new(32, 0, 0) } },
            },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown] public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    /// <summary>
    /// <b>S9a.</b> One gateway of a two-gateway fleet is drained and replaced under a connected client. The
    /// client is told to reconnect (<see cref="GatewayDrainingMsg"/>), presents its session token to the other
    /// gateway, and keeps the same session id, the same identity and the same pawn. The drained process is then
    /// stopped and a replacement started, and a client joins that one too: the fleet is whole again.
    /// </summary>
    [Test]
    public void AGatewayIsDrainedAndReplacedUnderConnectedClientsWithNoSessionLost()
    {
        var report = new ScaleReport("rolling-upgrade-gateway", "step", "clients", "sessions kept", "note");
        using var fleet = new Fleet(gateways: 2);
        fleet.Worker.SpawnIntoRequestedContainer = true;

        var client = fleet.Connect(0, "ann");
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined, seconds: 20), Is.True, "the client never joined");
        var before = client.Welcome!.Value;
        ulong pawn = fleet.Worker.Pawns[before.ClientId];
        report.Row("joined", 1, 1, $"session {before.ClientId:x}");

        // 1. Ask the gateway to drain, the way a deploy script does: a control-plane flag, not a signal.
        fleet.Plane.SetGatewayDraining("gw1", true);
        Assert.That(fleet.Run(() => client.DrainWithin > 0, seconds: 15), Is.True,
            "a draining gateway must tell its clients how long they have to reconnect");
        Assert.That(fleet.Gateways[0].Draining, Is.True);
        report.Row("drain requested", 1, 1, $"clients told to reconnect within {client.DrainWithin} s");

        // 2. The client reconnects through the other gateway with its session token, as a real client does when
        //    a load balancer hands its next connection to a different address.
        var moved = fleet.Connect(1, "ann", before.Token, before.SessionToken);
        Assert.That(fleet.Run(() => moved.Join == JoinState.Joined, seconds: 30), Is.True, "the client did not land on the second gateway");
        Assert.That(moved.Welcome!.Value.ClientId, Is.EqualTo(before.ClientId), "the session id must survive the move");
        Assert.That(moved.Welcome!.Value.Identity, Is.EqualTo(before.Identity), "and so must the player's identity");
        Assert.That(moved.Welcome!.Value.Reclaimed, Is.True, "the move must be a reclaim, not a new session");
        Assert.That(moved.Welcome!.Value.NegotiatedVersion, Is.EqualTo(HelloMsg.ProtocolVersion),
            "the replacement records the protocol it negotiated with the moved client");
        Assert.That(fleet.Worker.Pawns[before.ClientId], Is.EqualTo(pawn), "and the worker must still hold the same pawn");
        report.Row("moved to gw2", 1, 1, "same session id, same identity, same pawn");

        // 3. The drained process stops and a replacement takes its place.
        fleet.KillGateway(0, hard: false);
        int replacement = fleet.StartGateway();
        var fresh = fleet.Connect(replacement, "bob");
        Assert.That(fleet.Run(() => fresh.Join == JoinState.Joined, seconds: 30), Is.True, "the replacement gateway does not accept clients");
        Assert.That(moved.Disconnected, Is.False, "replacing a drained gateway must not disturb the clients on the others");
        Assert.That(moved.Rejected, Is.Null);
        report.Row("replaced", 2, 2, "the replacement accepts new clients; the moved client is undisturbed");
        report.Note("A rolling upgrade of a gateway fleet is: drain one, wait for its clients to move, stop it, " +
                    "start the new build, repeat. Nothing here needs a matching client build, because the client " +
                    "protocol window admits N and N-1 (docs/compatibility-policy.md).");
        report.Write();
    }

    /// <summary>
    /// <b>S9b.</b> A worker is drained and replaced: its entities are handed to another worker (the orchestrator
    /// does this by moving the lease and the worker by transferring authority; <see cref="FakeWorker.HandOver"/>
    /// is that transfer), the drained worker's process leaves, and a replacement registers and is given a
    /// container. The watching client keeps every replica it held, is never told its pawn is gone, and is never
    /// disconnected.
    /// </summary>
    [Test]
    public void AWorkerIsDrainedAndReplacedWithNoEntityLostAndNoClientDisconnected()
    {
        var report = new ScaleReport("rolling-upgrade-worker", "step", "entities", "replicas", "note");
        using var fleet = new Fleet(gateways: 1, workers: 2, world: f => f.Assign("c0", f.Workers[0].WorkerId));
        fleet.Worker.SpawnIntoRequestedContainer = true;
        var leaving = fleet.Workers[0];
        var staying = fleet.Workers[1];

        var client = fleet.Connect(0, "ann");
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined, seconds: 20), Is.True);
        ulong pawn = leaving.Pawns[client.Welcome!.Value.ClientId];
        leaving.Spawn(6600, new Vector3(20, 0, 0), new ContainerRef(0));
        leaving.Spawn(6601, new Vector3(24, 0, 0), new ContainerRef(0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(6600ul) && client.Replicas.Contains(6601ul) && client.Replicas.Contains(pawn), seconds: 20),
            Is.True, "the client never saw the world it is about to watch move");
        var held = new HashSet<ulong>(client.Replicas);
        report.Row("before the drain", leaving.Entities.Count, held.Count, "one worker owns everything");

        // 1. Drain: every entity this worker owns moves to the one taking over, and the lease follows. This is
        //    the order that matters - authority first, then the lease - because a lease moved under a live
        //    entity would leave the gateway asking a worker that no longer speaks for it.
        foreach (ulong netId in new List<ulong>(leaving.Entities.Keys)) leaving.HandOver(netId, staying);
        fleet.Plane.AssignContainer("c0", staying.WorkerId);
        Assert.That(fleet.Run(() => staying.Entities.Count == 3, seconds: 20), Is.True, "the entities did not reach the worker taking over");
        Assert.That(leaving.Entities, Is.Empty, "the drained worker still owns something");
        report.Row("drained", staying.Entities.Count, client.Replicas.Count, "authority and lease moved to the second worker");

        // 2. The drained process leaves. It owns nothing, so nothing is orphaned - that is what draining bought.
        var orphaned = fleet.KillWorker(leaving);
        Assert.That(orphaned, Is.Empty, "a drained worker must leave no orphaned container behind");

        // 3. A replacement joins, and the world can be dealt back to it.
        var replacement = fleet.StartWorker();
        fleet.Assign("c0", replacement.WorkerId);
        Assert.That(fleet.Run(() => fleet.Plane.Leases.Any(l => l.ContainerId == "c0" && l.WorkerId == replacement.WorkerId), seconds: 15), Is.True,
            "the replacement never got the container");

        Assert.That(client.Disconnected, Is.False, "no client may be disconnected by a worker being replaced");
        Assert.That(client.Despawned, Does.Not.Contain(pawn), "the client must never be told its own pawn is gone");
        foreach (ulong netId in held)
            Assert.That(client.Replicas, Does.Contain(netId), $"replica #{netId} was lost across the drain and replacement");
        report.Row("replaced", staying.Entities.Count, client.Replicas.Count, "no entity lost, no client disconnected");
        report.Note("A rolling upgrade of the worker tier is: hand a worker's entities and leases to its " +
                    "neighbours, let it exit, start the new build, deal containers back. Gateway-to-worker and " +
                    "worker-to-worker links require the exact same protocol, so the whole tier moves in one pass " +
                    "(docs/compatibility-policy.md D2).");
        report.Write();
    }
}
