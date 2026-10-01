using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-368: an <see cref="InstanceBoundary"/> that prepares crossings only on request
    /// (<see cref="InstanceBoundary.RequireRequest"/>, <see cref="InstanceBoundary.Request"/>), reports their readiness
    /// (<see cref="InstanceBoundary.TryGetPreparation"/>, <see cref="InstanceBoundary.CrossingReady"/>), and keeps an
    /// occupant at the face from leaving and entering on every tick (<see cref="InstanceBoundary.ExitMargin"/>); and
    /// with every setting at its default, the boundary behaves as before. Tier B: one real <see cref="NebulaWorker"/>
    /// on the <see cref="ConformanceMesh"/> with a <see cref="LocalControlPlane"/>; the owning client's
    /// acknowledgement is stood in for by marking preparations client-ready.
    /// </summary>
    public sealed class InstanceBoundaryRequestTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Owner = "player-a";

        private sealed class SlowContent : MonoBehaviour, IInstanceContentLoader
        {
            public InstanceContentState Current = InstanceContentState.Loading;
            public InstanceContentState State => Current;
            public float Progress => 0f;
        }

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private InstanceTemplate _template;
        private ushort _pawnPrefab;
        private uint _tick;
        private float _now;
        private InstanceBoundary _door;
        private NetworkIdentity _player;
        private Container _street;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<InstanceCrossingState> _ready = new List<InstanceCrossingState>();

        private ConformanceMesh.Worker W => _mesh[0];
        private static ulong Private => NebulaWorker.InstanceKey("vault/" + Owner);

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(1);
            _plane = new LocalControlPlane();
            _plane.Connect();
            AttachRegistered(W.Instance, _plane);
            _plane.RegisterWorker(W.Id, W.Index, "127.0.0.1", 7000);
            _plane.HeartbeatWorker(W.Id, WorkerStatus.Ready, default);
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
            _ready.Clear();

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
            _door.CrossingReady += (entity, state) => _ready.Add(state);
            typeof(InstanceBoundary).GetMethod("OnEnable", Hidden).Invoke(_door, null);
            _player = W.SpawnServerDriven(_pawnPrefab, _street, At(0f, -8f), Quaternion.identity);
            _player.OwnerClientId = 7;
            _player.OwnerIdentity = Owner;
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

        /// <summary>A point at the door's height plus a metre, <paramref name="x"/> and <paramref name="z"/> from its centre.</summary>
        private Vector3 At(float x, float z) => new Vector3(20f + x, 1f, 20f + z);

        private void Tick()
        {
            W.Tick(++_tick);
            ContainerRegistry.SyncRuntime(_plane.Leases);
            foreach (var lease in _plane.Leases)
                if (lease.WorkerId == W.Id) ContainerRegistry.ApplyLease(lease.ContainerId, W.Id, W.Index, lease.Epoch);
        }

        private Dictionary<uint, InstanceTransfer> Transfers =>
            (Dictionary<uint, InstanceTransfer>)typeof(NebulaWorker).GetField("_instanceTransfers", Hidden).GetValue(W.Instance);

        private void AcknowledgeClients()
        {
            foreach (var transfer in Transfers.Values) transfer.ClientReady = true;
        }

        private InstanceCrossingState State()
        {
            _door.TryGetPreparation(_player, out var state);
            return state;
        }

        private Container Hall => ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));

        /// <summary>Approach, prepare, acknowledge, and step inside.</summary>
        private void Enter()
        {
            Tick();
            Tick();
            AcknowledgeClients();
            _player.transform.position = At(0f, 1f);
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private), "entered");
        }

        // ------------------------------------------------------------------------------------------------ defaults

        [Test]
        public void WithDefaultsANearbyPlayerIsPreparedAndCrossesAsBefore()
        {
            Assert.That(_door.RequireRequest, Is.False);
            Assert.That(_door.ExitMargin, Is.EqualTo(0f));
            Tick();
            Assert.That(_plane.FindScope("vault/" + Owner), Is.Not.Null, "proximity alone prepares the instance");
            Tick();
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.PendingClient));
            AcknowledgeClients();
            _player.transform.position = At(0f, 1f);
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private));
            Assert.That(_player.Container, Is.SameAs(Hall));

            // Out at the face itself, once the way out is ready.
            _player.transform.position = At(0f, -4.5f);
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private), "held until the way out is ready");
            AcknowledgeClients();
            _player.transform.position = At(0f, -4.5f);
            Tick();
            Assert.That(_player.Container, Is.SameAs(_street), "left just past the face");
        }

        [Test]
        public void APlayerOutOfRangeHasNoPreparation()
        {
            _player.transform.position = At(0f, -40f);
            Tick();
            Assert.That(_door.TryGetPreparation(_player, out var state), Is.False);
            Assert.That(state.Status, Is.EqualTo(InstanceCrossingStatus.None));
            Assert.That(_plane.FindScope("vault/" + Owner), Is.Null);
        }

        // ------------------------------------------------------------------------------------------------ requests

        [Test]
        public void WithRequireRequestProximityAlonePreparesNothing()
        {
            _door.RequireRequest = true;
            Tick();
            Tick();
            Assert.That(_plane.FindScope("vault/" + Owner), Is.Null, "no instance is activated without a request");
            Assert.That(Transfers, Is.Empty);
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.NotRequested));

            _player.transform.position = At(0f, 1f);
            Tick();
            Assert.That(_player.Container, Is.SameAs(_street), "the face is closed without a request");
            Assert.That(_player.transform.position, Is.EqualTo(At(0f, -8f)), "held where it last stood");

            _door.Request(_player, 5f);
            Tick();
            Assert.That(_plane.FindScope("vault/" + Owner), Is.Not.Null, "the request prepares the instance");
            Tick();
            AcknowledgeClients();
            _player.transform.position = At(0f, 1f);
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private), "and the crossing commits under it");
        }

        [Test]
        public void ARequestExpires()
        {
            _door.RequireRequest = true;
            _door.Request(_player, 2f);
            Tick();
            Tick();
            AcknowledgeClients();
            Tick();
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.Ready));

            _now += 2.5f;
            Tick();
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.NotRequested), "expired: the prepared crossing is dropped");
            _player.transform.position = At(0f, 1f);
            Tick();
            Assert.That(_player.Container, Is.SameAs(_street), "and can no longer commit");

            _door.Request(_player, 2f);
            _door.Request(_player, 0f);
            Tick();
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.NotRequested), "a request of 0 seconds withdraws it");
        }

        [Test]
        public void ARequestPreparesBeyondThePreparationDistance()
        {
            _player.transform.position = At(0f, -40f);
            _door.Request(_player, 10f);
            Tick();
            Assert.That(_plane.FindScope("vault/" + Owner), Is.Not.Null, "prepared early, before the player is near");
            Tick();
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.PendingClient));

            _now += 11f;
            Tick();
            Assert.That(_door.TryGetPreparation(_player, out _), Is.False);
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.None), "once it expires, out of range is out of range again");
        }

        [Test]
        public void AnOccupantLeavesOnlyOnRequestWithRequireRequest()
        {
            Enter();
            _door.RequireRequest = true;
            _player.transform.position = At(0f, -6f);
            AcknowledgeClients();
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private), "no way out without a request");
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.NotRequested));
            Assert.That(State().Leaving, Is.True);

            _door.Request(_player, 5f);
            Tick();
            AcknowledgeClients();
            _player.transform.position = At(0f, -6f);
            Tick();
            Assert.That(_player.Container, Is.SameAs(_street));
        }

        // ------------------------------------------------------------------------------------------------ readiness

        [Test]
        public void ReadinessIsReportedThroughEachState()
        {
            Tick(); // the instance is activated; its entry part has not been leased here yet
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.NoDestination));
            Assert.That(State().Leaving, Is.False);

            var hall = Hall;
            hall.LeaseState = "";
            W.Tick(++_tick); // the part is here, but its lease is not active
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.DestinationNotLeased));
            ContainerRegistry.ApplyLease(hall.ContainerId, W.Id, W.Index, hall.LeaseEpoch);

            var loader = new GameObject("slow content").AddComponent<SlowContent>();
            loader.transform.SetParent(hall.transform, false);
            Tick();
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.PendingContent));

            loader.Current = InstanceContentState.Ready;
            Tick();
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.PendingClient));
            InstanceTransfer transfer = null;
            foreach (var t in Transfers.Values) transfer = t;
            transfer.WorkerReady = false; // as while another worker owning the destination has not answered
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.PendingWorker), "read live, between ticks");
            transfer.WorkerReady = true;

            AcknowledgeClients();
            Assert.That(_door.TryGetPreparation(_player, out var state), Is.True);
            Assert.That(state.Status, Is.EqualTo(InstanceCrossingStatus.Ready));
            Assert.That(state.IsReady, Is.True);
            Assert.That(state.Error, Is.Null);

            _door.CanEnter = (entity, key) => false;
            Tick();
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.Refused));
            _door.CanEnter = null;

            Tick();
            loader.Current = InstanceContentState.Failed;
            transfer.Error = "Destination content failed to load";
            Assert.That(State().Status, Is.EqualTo(InstanceCrossingStatus.Failed));
            Assert.That(State().Error, Is.EqualTo("Destination content failed to load"));
        }

        [Test]
        public void CrossingReadyIsRaisedOncePerCrossing()
        {
            Tick();
            Tick();
            Assert.That(_ready, Is.Empty);
            AcknowledgeClients();
            for (int i = 0; i < 5; i++) Tick(); // ready, but still outside: no commit
            Assert.That(_ready.Count, Is.EqualTo(1), "raised once, not on every tick it stays ready");
            Assert.That(_ready[0].Status, Is.EqualTo(InstanceCrossingStatus.Ready));
            Assert.That(_ready[0].Leaving, Is.False);

            _player.transform.position = At(0f, 1f);
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private));
            Assert.That(_ready.Count, Is.EqualTo(1), "committing raises nothing new");

            Tick(); // the way out is prepared
            AcknowledgeClients();
            Tick();
            Tick();
            Assert.That(_ready.Count, Is.EqualTo(2), "the way out is a new crossing");
            Assert.That(_ready[1].Leaving, Is.True);
        }

        // ------------------------------------------------------------------------------------------------ exit margin

        /// <summary>Stand alternately just inside the face and just outside it, acknowledging every preparation, and count scope changes.</summary>
        private int Oscillate(int ticks)
        {
            int flips = 0;
            ulong scope = _player.InstanceId;
            for (int i = 0; i < ticks; i++)
            {
                AcknowledgeClients();
                _player.transform.position = At(0f, i % 2 == 0 ? -4.5f : -3.6f);
                Tick();
                if (_player.InstanceId != scope) { flips++; scope = _player.InstanceId; }
            }
            return flips;
        }

        [Test]
        public void WithoutAnExitMarginAnOccupantAtTheFaceFlipsScope()
        {
            Enter();
            Assert.That(Oscillate(16), Is.GreaterThanOrEqualTo(2), "the behaviour the margin is for: out and back in");
        }

        [Test]
        public void AnExitMarginKeepsAnOccupantAtTheFaceInside()
        {
            _door.ExitMargin = 1f;
            Enter();
            Assert.That(Oscillate(16), Is.EqualTo(0), "never leaves while within the margin");
            Assert.That(_player.InstanceId, Is.EqualTo(Private));

            AcknowledgeClients();
            _player.transform.position = At(0f, -5.5f);
            Tick();
            Assert.That(_player.Container, Is.SameAs(_street), "and leaves once past it");
        }

        [Test]
        public void AnExitMarginDoesNotWidenTheEntrance()
        {
            _door.ExitMargin = 1f;
            Tick();
            Tick();
            AcknowledgeClients();
            _player.transform.position = At(0f, -4.5f); // within the margin, outside Interior
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(0UL), "entry is still Interior's face");
            _player.transform.position = At(0f, -3.6f);
            Tick();
            Assert.That(_player.InstanceId, Is.EqualTo(Private));
        }
    }
}
