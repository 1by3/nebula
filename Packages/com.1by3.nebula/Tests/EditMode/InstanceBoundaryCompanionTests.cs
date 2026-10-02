using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-372: an <see cref="InstanceBoundary"/> crosses an entity together with its companions
    /// (<see cref="InstanceBoundary.ResolveCompanions"/>): a transfer is prepared for each, readiness covers the group,
    /// and the group commits as one, into a destination this worker owns or another worker owns. A companion that
    /// is refused or whose preparation failed is left behind and reported (<see cref="InstanceBoundary.CompanionLeftBehind"/>);
    /// one still pending holds the group for <see cref="InstanceBoundary.CompanionWaitSeconds"/>. Tier B: real
    /// <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/> with a <see cref="LocalControlPlane"/>; the owning
    /// client's acknowledgement is stood in for by marking preparations client-ready.
    /// </summary>
    public sealed class InstanceBoundaryCompanionTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Owner = "player-a";

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private InstanceTemplate _template;
        private ushort _pawnPrefab;
        private uint _tick;
        private float _now;
        private InstanceBoundary _door;
        private NetworkIdentity _player;
        private NetworkIdentity _crate;
        private Container _street;
        private bool _holding;
        /// <summary>When set, the instance's hall is leased to the second worker instead of the one that prepared it.</summary>
        private bool _hallOnSecondWorker;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<(NetworkIdentity leader, NetworkIdentity companion, InstanceCrossingState state)> _leftBehind =
            new List<(NetworkIdentity, NetworkIdentity, InstanceCrossingState)>();

        private ConformanceMesh.Worker W => _mesh[0];
        private ConformanceMesh.Worker Other => _mesh[1];
        private static ulong Private => NebulaWorker.InstanceKey("vault/" + Owner);

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            _plane = new LocalControlPlane();
            _plane.Connect();
            foreach (var worker in _mesh.Workers)
            {
                AttachRegistered(worker.Instance, _plane);
                _plane.RegisterWorker(worker.Id, worker.Index, "127.0.0.1", (ushort)(7000 + worker.Index));
                _plane.HeartbeatWorker(worker.Id, WorkerStatus.Ready, default);
            }
            _template = ScriptableObject.CreateInstance<InstanceTemplate>();
            _template.TemplateId = "vault";
            _template.Parts = new[] { new InstanceTemplate.Part { Id = "hall", Bounds = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f)) } };
            _template.ObservePublic = false;
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _objects.Add(pawn);
            _pawnPrefab = _mesh.RegisterPrefab(pawn);
            _tick = 0;
            _now = 100f;
            _holding = true;
            _hallOnSecondWorker = false;
            _leftBehind.Clear();

            _street = _mesh.AddStaticContainer("street", Vector3.zero, new Vector3(400f, 50f, 400f));
            _mesh.SetOwner(_street, W);
            var go = new GameObject("door");
            _objects.Add(go);
            go.transform.position = new Vector3(20f, 0f, 20f);
            _door = go.AddComponent<InstanceBoundary>();
            _door.Template = _template;
            _door.Interior = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f));
            _door.PreparationDistance = 8f;
            _door.ClockForTests = () => _now;
            _door.ResolveCompanions = (entity, companions) => { if (entity == _player && _holding) companions.Add(_crate); };
            _door.CompanionLeftBehind += (leader, companion, state) => _leftBehind.Add((leader, companion, state));
            typeof(InstanceBoundary).GetMethod("OnEnable", Hidden).Invoke(_door, null);

            _player = W.SpawnServerDriven(_pawnPrefab, _street, At(0f, -8f), Quaternion.identity);
            _player.OwnerClientId = 7;
            _player.OwnerIdentity = Owner;
            // A server-owned crate the player holds just in front of them.
            _crate = W.SpawnServerDriven(_pawnPrefab, _street, At(1f, -7f), Quaternion.identity);
        }

        [TearDown]
        public void TearDown()
        {
            if (_door != null) typeof(InstanceBoundary).GetMethod("OnDisable", Hidden).Invoke(_door, null);
            _mesh.Dispose();
            _plane.Dispose();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            Object.DestroyImmediate(_template);
            InstanceScenes.Reset();
        }

        private static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", Hidden).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", Hidden).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", Hidden).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", Hidden).SetValue(registration, plane.DocumentId ?? "");
        }

        /// <summary>A point a metre above the door's floor, <paramref name="x"/> and <paramref name="z"/> from its centre.</summary>
        private static Vector3 At(float x, float z) => new Vector3(20f + x, 1f, 20f + z);

        private void Tick()
        {
            W.Tick(++_tick);
            ApplyLeases();
            _mesh.Pump();
        }

        private void ApplyLeases()
        {
            ContainerRegistry.SyncRuntime(_plane.Leases);
            foreach (var lease in _plane.Leases)
                if (lease.WorkerId == W.Id) ContainerRegistry.ApplyLease(lease.ContainerId, W.Id, W.Index, lease.Epoch);
            var hall = Hall;
            if (_hallOnSecondWorker && hall != null) ContainerRegistry.ApplyLease(hall.ContainerId, Other.Id, Other.Index, 7);
        }

        private Dictionary<uint, InstanceTransfer> Transfers =>
            (Dictionary<uint, InstanceTransfer>)typeof(NebulaWorker).GetField("_instanceTransfers", Hidden).GetValue(W.Instance);

        private void AcknowledgeClients()
        {
            foreach (var transfer in Transfers.Values) transfer.ClientReady = true;
        }

        private InstanceTransfer TransferOf(NetworkIdentity entity)
        {
            foreach (var transfer in Transfers.Values)
                if ((NetworkIdentity)typeof(InstanceTransfer).GetField("Entity", Hidden).GetValue(transfer) == entity) return transfer;
            return null;
        }

        private InstanceCrossingState State(NetworkIdentity entity)
        {
            _door.TryGetPreparation(entity, out var state);
            return state;
        }

        private static Container Hall => ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));

        /// <summary>Walk the player, crate in hand, through the face: the crate goes ahead of them.</summary>
        private void StepIn()
        {
            _crate.transform.position = At(1f, 2f);
            _player.transform.position = At(0f, 1f);
        }

        /// <summary>Approach the door and get the crossing prepared and acknowledged.</summary>
        private void Prepare()
        {
            Tick();
            Tick();
            AcknowledgeClients();
            Tick();
        }

        // ------------------------------------------------------------------------------------------------ same worker

        [Test]
        public void ACompanionCrossesTogetherWithItsLeader()
        {
            ulong crateId = _crate.NetId;
            Prepare();
            Assert.That(TransferOf(_crate), Is.Not.Null, "the companion has a preparation of its own");
            Assert.That(State(_crate).Status, Is.EqualTo(InstanceCrossingStatus.Ready));
            Assert.That(State(_player).Status, Is.EqualTo(InstanceCrossingStatus.Ready), "and the group is ready");

            StepIn();
            var cratePosition = _crate.transform.position;
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private));
            Assert.That(_player.Container, Is.SameAs(Hall));
            Assert.That(_crate.InstanceId, Is.EqualTo(Private), "the crate crossed in the same tick");
            Assert.That(_crate.Container, Is.SameAs(Hall), "into the same container");
            Assert.That(_crate.NetId, Is.EqualTo(crateId), "as the same entity");
            Assert.That(_crate.HasAuthority, Is.True);
            Assert.That(_crate.transform.position, Is.EqualTo(cratePosition), "at the same absolute pose");
            Assert.That(_leftBehind, Is.Empty);

            // And back out together, from inside.
            Tick();
            AcknowledgeClients();
            Tick();
            _crate.transform.position = At(1f, -7f);
            _player.transform.position = At(0f, -6f);
            Tick();
            Assert.That(_player.Container, Is.SameAs(_street));
            Assert.That(_crate.Container, Is.SameAs(_street), "the way out takes the crate too");
            Assert.That(_crate.InstanceId, Is.EqualTo(0UL));
        }

        [Test]
        public void WithoutCompanionsTheEntityCrossesAlone()
        {
            _holding = false;
            Prepare();
            Assert.That(TransferOf(_crate), Is.Null, "nothing is prepared for an entity nobody names");
            StepIn();
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private));
            Assert.That(_crate.Container, Is.SameAs(_street), "a server-owned entity never crosses by itself");
        }

        [Test]
        public void ACompanionWorksWithRequestsAndTheExitMargin()
        {
            _door.RequireRequest = true;
            _door.ExitMargin = 1f;
            Prepare();
            Assert.That(TransferOf(_crate), Is.Null, "no request, nothing prepared for the group");
            _door.Request(_player, 30f);
            Prepare();
            StepIn();
            Tick();
            Assert.That(_crate.InstanceId, Is.EqualTo(Private), "the leader's request covers its companions");

            _door.Request(_player, 30f);
            Tick();
            AcknowledgeClients();
            _crate.transform.position = At(1f, -5.5f);
            _player.transform.position = At(0f, -4.5f); // within the margin
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private), "within the margin, still inside");
            Assert.That(_crate.InstanceId, Is.EqualTo(Private), "and the crate stays with them, though it is past the face");

            _crate.transform.position = At(1f, -6.5f);
            _player.transform.position = At(0f, -5.5f);
            Tick();
            Assert.That(_player.Container, Is.SameAs(_street));
            Assert.That(_crate.Container, Is.SameAs(_street));
        }

        // ------------------------------------------------------------------------------------------------ other worker

        [Test]
        public void TheGroupCrossesIntoADestinationAnotherWorkerOwns()
        {
            _hallOnSecondWorker = true;
            ulong playerId = _player.NetId, crateId = _crate.NetId;
            Tick(); // the instance is activated, and its hall leased to the other worker
            Assert.That(Hall, Is.Not.Null);
            Assert.That(Hall.OwnerWorkerId, Is.EqualTo(Other.Id));
            Tick(); // both are prepared: the other worker is asked and answers
            Assert.That(_mesh.DeliveredOf(MsgId.InstancePrepare, Other.Id).Count, Is.EqualTo(2), "the destination worker is asked for each member");
            AcknowledgeClients();
            Tick();
            Assert.That(State(_player).Status, Is.EqualTo(InstanceCrossingStatus.Ready));
            Assert.That(State(_crate).Status, Is.EqualTo(InstanceCrossingStatus.Ready));

            StepIn();
            Tick();
            Assert.That(_player.Container, Is.SameAs(Hall));
            Assert.That(_crate.Container, Is.SameAs(Hall), "committed together, into the other worker's container");
            Tick(); // each follows its container's lease to the other worker
            var player = Other.Find(playerId);
            var crate = Other.Find(crateId);
            Assert.That(player, Is.Not.Null.And.Property(nameof(NetworkIdentity.HasAuthority)).True, "the player arrived on the other worker");
            Assert.That(crate, Is.Not.Null.And.Property(nameof(NetworkIdentity.HasAuthority)).True, "and so did the crate, as the same entity");
            Assert.That(crate.InstanceId, Is.EqualTo(Private));
        }

        // ------------------------------------------------------------------------------------------------ left behind

        [Test]
        public void ARefusedCompanionIsLeftBehindAndReported()
        {
            _door.CanEnter = (entity, key) => entity != _crate;
            Prepare();
            Assert.That(State(_crate).Status, Is.EqualTo(InstanceCrossingStatus.Refused));
            Assert.That(State(_player).Status, Is.EqualTo(InstanceCrossingStatus.Ready), "a refused companion does not hold the group");

            StepIn();
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private), "the leader crosses alone");
            Assert.That(_crate.Container, Is.SameAs(_street), "the crate stays where it was");
            Assert.That(_leftBehind.Count, Is.EqualTo(1));
            Assert.That(_leftBehind[0].leader, Is.SameAs(_player));
            Assert.That(_leftBehind[0].companion, Is.SameAs(_crate));
            Assert.That(_leftBehind[0].state.Status, Is.EqualTo(InstanceCrossingStatus.Refused));
        }

        [Test]
        public void ACompanionWhosePreparationFailedIsLeftBehindWithTheError()
        {
            Prepare();
            TransferOf(_crate).Error = "Destination is at capacity";
            Assert.That(State(_crate).Status, Is.EqualTo(InstanceCrossingStatus.Failed));
            Assert.That(State(_player).IsReady, Is.True);

            StepIn();
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private));
            Assert.That(_crate.Container, Is.SameAs(_street));
            Assert.That(_leftBehind.Count, Is.EqualTo(1));
            Assert.That(_leftBehind[0].state.Status, Is.EqualTo(InstanceCrossingStatus.Failed));
            Assert.That(_leftBehind[0].state.Error, Is.EqualTo("Destination is at capacity"));
        }

        [Test]
        public void ACompanionThisWorkerDoesNotSimulateIsLeftBehind()
        {
            _crate.HasAuthority = false; // as when another worker simulates it
            Prepare();
            Assert.That(State(_crate).Status, Is.EqualTo(InstanceCrossingStatus.Failed));
            Assert.That(State(_crate).Error, Does.Contain("not simulated"));
            StepIn();
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private));
            Assert.That(_leftBehind.Count, Is.EqualTo(1));
            _crate.HasAuthority = true;
        }

        // ------------------------------------------------------------------------------------------------ pending

        /// <summary>Make the companion a pawn of another client, whose acknowledgement the test gives by hand.</summary>
        private void CompanionOfAnotherClient()
        {
            _crate.OwnerClientId = 8;
            _crate.OwnerIdentity = "player-b";
        }

        private void AcknowledgeLeaderOnly() => TransferOf(_player).ClientReady = true;

        [Test]
        public void APendingCompanionHoldsTheGroupUntilItIsReady()
        {
            CompanionOfAnotherClient();
            Tick();
            Tick();
            AcknowledgeLeaderOnly();
            Tick();
            Assert.That(State(_crate).Status, Is.EqualTo(InstanceCrossingStatus.PendingClient));
            Assert.That(State(_player).Status, Is.EqualTo(InstanceCrossingStatus.PendingClient), "readiness covers the group");

            StepIn();
            Tick();
            Assert.That(_player.Container, Is.SameAs(_street), "the leader waits at the face");
            Assert.That(_player.transform.position, Is.EqualTo(At(0f, -8f)), "held where it last stood");
            Assert.That(_crate.Container, Is.SameAs(_street), "and a companion never crosses on its own, though it is inside");

            TransferOf(_crate).ClientReady = true;
            StepIn();
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private));
            Assert.That(_crate.InstanceId, Is.EqualTo(Private));
            Assert.That(_leftBehind, Is.Empty);
        }

        [Test]
        public void APendingCompanionIsLeftBehindOnceTheWaitRunsOut()
        {
            CompanionOfAnotherClient();
            _door.CompanionWaitSeconds = 2f;
            Tick();
            Tick();
            AcknowledgeLeaderOnly();
            Tick();
            StepIn();
            Tick();
            Assert.That(_player.Container, Is.SameAs(_street), "still waiting");

            _now += 2.5f;
            StepIn();
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private), "the wait ran out: the leader crosses alone");
            Assert.That(_crate.InstanceId, Is.EqualTo(0UL));
            Assert.That(_leftBehind.Count, Is.EqualTo(1));
            Assert.That(_leftBehind[0].state.Status, Is.EqualTo(InstanceCrossingStatus.PendingClient));
        }
    }
}
