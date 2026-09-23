using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// A worker the orchestrator declares dead while its process is still running (NEB-256,
/// <c>docs/persistence-durability.md</c> D8–D11): it only missed heartbeats. Its containers are dealt to another
/// worker, which restores their entities from the last checkpoint, and for a moment two authoritative copies of the
/// same entity existed. The rules pinned here are the control-plane half of the fix: the worker fences itself on
/// the same liveness test the orchestrator applies (<see cref="WorkerRegistration.IsFenced"/>), it can tell a death
/// verdict from a control plane that came back empty (<see cref="WorkerRegistration.IsDeclaredDead"/>), and a
/// gateway drops a declared-dead worker even while its link is up, so a client is never shown the old copy beside
/// the restored one (<see cref="WorkerRoster"/>). The worker's persistence half (no saves while fenced, the restored
/// entity's epoch stamped into the store first, the lost containers dropped without a save) is the Unity conformance
/// scenario <c>Tests/EditMode/ConformanceDeclaredDeadWorkerTests.cs</c>.
/// </summary>
[TestFixture]
public class DeclaredDeadWorkerTests
{
    private const float Timeout = 5f;
    private DateTime _clock;

    private LocalControlPlane Plane()
    {
        _clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var plane = new LocalControlPlane { Clock = () => _clock };
        plane.Connect();
        return plane;
    }

    private static WorkerRegistration Registered(IControlPlane plane, string id = "w1", uint index = 1)
    {
        var registration = new WorkerRegistration { WorkerId = id, WorkerIndex = index, Address = "127.0.0.1", Port = 7000 };
        Assert.That(registration.Register(plane), Is.True);
        return registration;
    }

    // ------------------------------------------------------------------------------------------- the fence

    [Test]
    public void AWorkerIsFencedOnTheSameTestTheOrchestratorDeclaresItDeadWith()
    {
        using var plane = Plane();
        var registration = Registered(plane);
        Assert.That(registration.IsFenced(plane, Timeout), Is.False, "a fresh registration is alive");

        _clock = _clock.AddSeconds(Timeout - 0.5);
        Assert.That(registration.IsFenced(plane, Timeout), Is.False);
        Assert.That(plane.IsWorkerAlive(plane.FindWorker("w1"), Timeout), Is.True);

        // The heartbeats stop landing. At the moment the orchestrator would call it dead, the worker knows.
        _clock = _clock.AddSeconds(1);
        Assert.That(plane.IsWorkerAlive(plane.FindWorker("w1"), Timeout), Is.False, "the orchestrator's test");
        Assert.That(registration.IsFenced(plane, Timeout), Is.True, "the worker's own reading of its row agrees");
        Assert.That(registration.IsDeclaredDead(plane), Is.False, "stale is not dead: the row is still there");

        plane.HeartbeatWorker("w1", WorkerStatus.Ready, default);
        Assert.That(registration.IsFenced(plane, Timeout), Is.False, "the next heartbeat that lands lifts the fence");
    }

    [Test]
    public void AWorkerTheOrchestratorUnregisteredInTheSameDocumentWasDeclaredDead()
    {
        using var plane = Plane();
        var registration = Registered(plane);
        Assert.That(registration.IsFenced(plane, Timeout), Is.False); // sees its own row

        plane.UnregisterWorker("w1"); // what NebulaOrchestrator.ReapDeadWorkers does
        Assert.That(registration.IsDeclaredDead(plane), Is.True);
        Assert.That(registration.IsFenced(plane, Timeout), Is.True);
        Assert.That(registration.IsDeclaredDead(plane), Is.True, "it stays dead until its row is back, however often it asks");

        Assert.That(registration.RegisterAgainIfForgotten(plane), Is.True);
        Assert.That(registration.IsDeclaredDead(plane), Is.False);
        Assert.That(registration.IsFenced(plane, Timeout), Is.False, "registered again, with a fresh heartbeat");
    }

    [Test]
    public void AControlPlaneThatCameBackEmptyIsNotADeathVerdict()
    {
        using var plane = Plane();
        var registration = Registered(plane);
        Assert.That(registration.IsFenced(plane, Timeout), Is.False);
        string before = plane.DocumentId;

        plane.ResetControlPlane(); // a restart with no storage: a new document
        Assume.That(plane.DocumentId, Is.Not.EqualTo(before));
        Assert.That(plane.FindWorker("w1"), Is.Null);
        Assert.That(registration.IsDeclaredDead(plane), Is.False, "the reclaim path (control-plane-availability D1), not a death");
        Assert.That(registration.IsFenced(plane, Timeout), Is.False);
    }

    [Test]
    public void AWorkerThatHasNotSeenItsOwnRowYetIsNotFenced()
    {
        var registration = new WorkerRegistration { WorkerId = "w1", WorkerIndex = 1 };
        using var plane = Plane();
        Assert.That(registration.IsFenced(plane, Timeout), Is.False, "not registered");
        Assert.That(registration.IsFenced(null!, Timeout), Is.False, "no control plane");

        // A mirror that has not had the document with the new row in it yet.
        var mirror = new LocalControlPlane { Clock = () => _clock };
        mirror.Connect();
        Assert.That(registration.Register(mirror), Is.True);
        mirror.UnregisterWorker("w1");
        Assert.That(registration.IsFenced(mirror, Timeout), Is.False, "nothing is leased to a worker the document never listed");
        mirror.Dispose();
    }

    // ------------------------------------------------------------------------------------------- the roster

    [Test]
    public void TheRosterReportsARowRemovedFromTheSameDocumentOnce()
    {
        using var plane = Plane();
        Registered(plane, "w1", 1);
        Registered(plane, "w2", 2);
        var roster = new WorkerRoster();
        var dead = new List<string>();

        roster.Observe(plane, dead);
        Assert.That(dead, Is.Empty, "the first observation has nothing to compare with");

        plane.UnregisterWorker("w2");
        roster.Observe(plane, dead);
        Assert.That(dead, Is.EqualTo(new[] { "w2" }));

        dead.Clear();
        roster.Observe(plane, dead);
        Assert.That(dead, Is.Empty, "reported once");

        plane.ResetControlPlane();
        roster.Observe(plane, dead);
        Assert.That(dead, Is.Empty, "every row went with the document: a restart, not a verdict on w1");
    }

    // ------------------------------------------------------------------------------------------- the gateway

    private string _directory = "";

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-declared-dead-tests-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// The client half of the conformance scenario: kill a worker's heartbeats, not its process, and the gateway
    /// shows exactly one copy. The silenced worker keeps its socket and keeps publishing the entity; the survivor
    /// restores it under a new id, as <c>NebulaPersistence.Restore</c> does. Without the verdict the gateway would
    /// keep the old copy for as long as its link linger (or for good, while a client's pawn is on that worker).
    /// </summary>
    [Test]
    public void AGatewayDropsAWorkerDeclaredDeadWhileItsLinkIsStillUp()
    {
        using var fleet = new Fleet(1, workers: 2, world: f => { for (int i = 0; i < 4; i++) f.Assign("c" + i, "w1"); });
        var stale = fleet.Workers[0];
        var survivor = fleet.Workers[1];
        stale.PawnPlacement = _ => new Vector3(10, 0, 0);
        survivor.PawnPlacement = _ => new Vector3(10, 0, 0);
        var client = fleet.Connect(0, "ann");
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined), Is.True, "ann should get a pawn");
        const ulong oldCopy = 6500, restoredCopy = 6600;
        stale.Spawn(oldCopy, new Vector3(12, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(oldCopy)), Is.True);

        bool both = false;
        fleet.OnPump = () =>
        {
            if (client.Replicas.Contains(oldCopy) && client.Replicas.Contains(restoredCopy)) both = true;
            stale.Move(oldCopy, new Vector3(12, 0, 1)); // the old owner is still running, and still publishing
        };

        // Its heartbeats stop reaching the control plane; the orchestrator declares it dead and deals its
        // containers to the survivor, which restores the entity from its checkpoint.
        fleet.SilencedWorkers.Add(stale);
        fleet.Plane.UnregisterWorker(stale.WorkerId);
        for (int i = 0; i < 4; i++) fleet.Plane.AssignContainer("c" + i, survivor.WorkerId);
        Assert.That(fleet.Run(() => !client.Replicas.Contains(oldCopy), seconds: 5), Is.True,
            "the declared-dead worker's copy left the client even though its link was up");
        Assert.That(fleet.Run(() => stale.Gateways.Count == 0, seconds: 5), Is.True, "and the gateway closed the link");

        survivor.Spawn(restoredCopy, new Vector3(12, 0, 0));
        Assert.That(fleet.Run(() => client.Replicas.Contains(restoredCopy) && client.Join == JoinState.Joined, seconds: 10), Is.True,
            "the restored copy is shown, and the client is placed again on the survivor");
        fleet.RunFor(0.5);
        Assert.That(both, Is.False, "the client never held both copies");
        Assert.That(client.Replicas, Does.Not.Contain(oldCopy));
        Assert.That(client.Disconnected, Is.False, "a verdict on a worker disconnects no client");
        Assert.That(stale.Gateways, Is.Empty, "the gateway did not dial the declared-dead worker again");
    }
}
