using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-367: instance content that finishes loading after <see cref="InstanceScenes.Prepare"/> returns
    /// (<see cref="IInstanceContentLoader"/>) holds a crossing's preparation until it is ready, on the destination
    /// worker (its own and another worker's) and on the owning client, and content with no loaders is ready at once,
    /// as before. Tier B: real <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/> with a
    /// <see cref="LocalControlPlane"/>, and a <see cref="NebulaClient"/> over a transport that records what it sends.
    /// </summary>
    public sealed class InstanceContentLoaderTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Owner = "player-a";

        /// <summary>Content whose loading the test finishes by hand.</summary>
        private sealed class SlowContent : MonoBehaviour, IInstanceContentLoader
        {
            public InstanceContentState Current = InstanceContentState.Loading;
            public float Fraction;
            public int Reads;
            public InstanceContentState State { get { Reads++; return Current; } }
            public float Progress => Fraction;
        }

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private InstanceTemplate _template;
        private ushort _pawnPrefab;
        private uint _tick;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<InstanceBoundary> _boundaries = new List<InstanceBoundary>();

        private ConformanceMesh.Worker W => _mesh[0];

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
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var b in _boundaries)
                if (b != null) typeof(InstanceBoundary).GetMethod("OnDisable", Hidden).Invoke(b, null);
            _boundaries.Clear();
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

        private void Tick()
        {
            W.Tick(++_tick);
            ContainerRegistry.SyncRuntime(_plane.Leases);
            foreach (var lease in _plane.Leases)
                if (lease.WorkerId == W.Id) ContainerRegistry.ApplyLease(lease.ContainerId, W.Id, W.Index, lease.Epoch);
        }

        private Dictionary<uint, InstanceTransfer> Transfers(ConformanceMesh.Worker worker) =>
            (Dictionary<uint, InstanceTransfer>)typeof(NebulaWorker).GetField("_instanceTransfers", Hidden).GetValue(worker.Instance);

        /// <summary>Stand in for the owning client's gateway acknowledging every preparation in flight.</summary>
        private void AcknowledgeClients()
        {
            foreach (var transfer in Transfers(W).Values) transfer.ClientReady = true;
        }

        private InstanceTransfer OnlyTransfer()
        {
            var transfers = Transfers(W);
            Assert.That(transfers.Count, Is.EqualTo(1), "one crossing in preparation");
            foreach (var transfer in transfers.Values) return transfer;
            return null;
        }

        private SlowContent Loader(Container container, string name = "slow content")
        {
            var go = new GameObject(name);
            go.transform.SetParent(container.transform, false);
            return go.AddComponent<SlowContent>();
        }

        /// <summary>A street the worker owns with an entrance on it, and a player walking up to the door.</summary>
        private (InstanceBoundary door, NetworkIdentity player, Container street) Entrance()
        {
            var street = _mesh.AddStaticContainer("street", Vector3.zero, new Vector3(200f, 50f, 200f));
            _mesh.SetOwner(street, W);
            var go = new GameObject("door");
            _objects.Add(go);
            go.transform.position = new Vector3(20f, 0f, 20f);
            var boundary = go.AddComponent<InstanceBoundary>();
            boundary.Template = _template;
            boundary.Interior = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f));
            typeof(InstanceBoundary).GetMethod("OnEnable", Hidden).Invoke(boundary, null);
            _boundaries.Add(boundary);
            var player = W.SpawnServerDriven(_pawnPrefab, street, go.transform.position + new Vector3(0f, 1f, -8f), Quaternion.identity);
            player.OwnerClientId = 7;
            player.OwnerIdentity = Owner;
            return (boundary, player, street);
        }

        /// <summary>Walk up to the door: the instance is activated and its entry part leased here.</summary>
        private Container Approach()
        {
            Tick();
            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
            Assert.That(hall, Is.Not.Null, "the entry part is registered");
            return hall;
        }

        private void StepIn(InstanceBoundary door, NetworkIdentity player) =>
            player.transform.position = door.transform.position + new Vector3(0f, 1f, 1f);

        private static ulong Private => NebulaWorker.InstanceKey("vault/" + Owner);

        // ------------------------------------------------------------------------------------------------- worker

        [Test]
        public void ContentWithNoLoadersIsReadyAtOnceAsBefore()
        {
            var (door, player, _) = Entrance();
            var hall = Approach();
            Assert.That(InstanceScenes.ContentState(hall), Is.EqualTo(InstanceContentState.Ready));
            Assert.That(InstanceScenes.ContentProgress(hall), Is.EqualTo(1f));

            Tick(); // prepared
            Assert.That(OnlyTransfer().WorkerReady, Is.True, "the destination worker is ready in the tick it prepares, as before");
            AcknowledgeClients();
            StepIn(door, player);
            Tick();
            Assert.That(player.InstanceId, Is.EqualTo(Private), "committed on the first tick inside");
        }

        [Test]
        public void ASlowLoaderHoldsTheCrossingUntilItIsReady()
        {
            var (door, player, street) = Entrance();
            var hall = Approach();
            var loader = Loader(hall);

            Tick(); // prepared, but the content is still building
            var transfer = OnlyTransfer();
            Assert.That(transfer.WorkerReady, Is.False);
            Assert.That(InstanceScenes.ContentState(hall), Is.EqualTo(InstanceContentState.Loading));
            AcknowledgeClients();
            StepIn(door, player);
            for (int frame = 0; frame < 5; frame++)
            {
                StepIn(door, player);
                Tick();
                Assert.That(transfer.Ready, Is.False, $"not ready after {frame + 1} ticks of loading");
                Assert.That(player.Container, Is.SameAs(street), "no commit while the content loads");
                Assert.That(player.InstanceId, Is.EqualTo(0UL));
            }
            int reads = loader.Reads;
            Assert.That(reads, Is.GreaterThanOrEqualTo(5), "polled once a tick while pending");

            loader.Current = InstanceContentState.Ready;
            StepIn(door, player);
            Tick();
            Assert.That(transfer.Finished, Is.True, "ready and committed in the same tick");
            Assert.That(player.InstanceId, Is.EqualTo(Private));
            Assert.That(player.Container, Is.SameAs(hall));

            Tick();
            Tick();
            Assert.That(loader.Reads, Is.EqualTo(reads + 1), "no longer polled once nothing waits for it");
        }

        [Test]
        public void ALoaderThatNeverFinishesExpiresAtTheDeadline()
        {
            var (door, player, street) = Entrance();
            var hall = Approach();
            Loader(hall);
            Tick();
            var transfer = OnlyTransfer();
            AcknowledgeClients();
            StepIn(door, player);
            Tick();
            Assert.That(player.Container, Is.SameAs(street));

            transfer.Deadline = Time.unscaledTime - 1f;
            LogAssert.Expect(LogType.Warning, new Regex("expired while its content was still loading"));
            Tick();

            Assert.That(transfer.Finished, Is.True);
            Assert.That(transfer.Ready, Is.False);
            Assert.That(transfer.Error, Does.Contain("did not finish loading"));
            Assert.That(player.Container, Is.SameAs(street), "the player stays in the source");
            Assert.That(player.InstanceId, Is.EqualTo(0UL));
            Assert.That(Transfers(W).Count, Is.EqualTo(1), "the boundary prepares the crossing again");
            Assert.That(OnlyTransfer(), Is.Not.SameAs(transfer));
        }

        [Test]
        public void EveryLoaderMustBeReady()
        {
            var (door, player, street) = Entrance();
            var hall = Approach();
            var first = Loader(hall, "first");
            var second = Loader(hall, "second");
            first.Fraction = 0.5f;

            Tick();
            var transfer = OnlyTransfer();
            Assert.That(InstanceScenes.ContentProgress(hall), Is.EqualTo(0.25f).Within(1e-5f));
            AcknowledgeClients();

            first.Current = InstanceContentState.Ready;
            StepIn(door, player);
            Tick();
            Assert.That(transfer.Ready, Is.False, "one of two loaders is not enough");
            Assert.That(player.Container, Is.SameAs(street));
            Assert.That(InstanceScenes.ContentProgress(hall), Is.EqualTo(0.5f).Within(1e-5f));

            second.Current = InstanceContentState.Ready;
            StepIn(door, player);
            Tick();
            Assert.That(player.InstanceId, Is.EqualTo(Private));
        }

        [Test]
        public void AFailedLoaderRefusesTheCrossing()
        {
            var (door, player, street) = Entrance();
            var hall = Approach();
            var loader = Loader(hall);
            Tick();
            var transfer = OnlyTransfer();
            AcknowledgeClients();

            loader.Current = InstanceContentState.Failed;
            StepIn(door, player);
            Tick();
            Assert.That(transfer.Ready, Is.False);
            Assert.That(transfer.Error, Does.Contain("failed to load"));
            Assert.That(player.Container, Is.SameAs(street));
        }

        [Test]
        public void AnotherWorkersDestinationAnswersOnceItsContentIsReady()
        {
            var source = _mesh.AddStaticContainer("source", Vector3.zero, new Vector3(64f, 64f, 64f));
            _mesh.SetOwner(source, _mesh[0]);
            var destination = ContainerRegistry.RegisterRuntime(0xC0FFEEUL, new Bounds(new Vector3(500f, 0f, 0f), new Vector3(20f, 10f, 20f)),
                new InstanceContainerInfo { InstanceId = 0xBEEFUL });
            ContainerRegistry.ApplyLease(destination.ContainerId, _mesh[1].Id, _mesh[1].Index, 1);
            var loader = Loader(destination);
            var entity = _mesh[0].SpawnServerDriven(_pawnPrefab, source, Vector3.zero, Quaternion.identity);

            var transfer = _mesh[0].Instance.PrepareTransfer(entity, destination);
            _mesh.Pump();
            Assert.That(_mesh.DeliveredOf(MsgId.InstancePrepare).Count, Is.EqualTo(1), "the destination worker was asked");
            Assert.That(_mesh.DeliveredOf(MsgId.InstanceReady), Is.Empty, "and holds its answer while the content loads");
            _mesh[1].Tick(1);
            _mesh.Pump();
            Assert.That(_mesh.DeliveredOf(MsgId.InstanceReady), Is.Empty);
            Assert.That(transfer.Ready, Is.False);

            loader.Current = InstanceContentState.Ready;
            _mesh[1].Tick(2);
            _mesh.Pump();
            var answers = _mesh.DeliveredOf(MsgId.InstanceReady);
            Assert.That(answers.Count, Is.EqualTo(1));
            Assert.That(answers[0].Read(InstancePreparationMsg.Read).Success, Is.True);
            Assert.That(transfer.Ready, Is.True);
        }

        [Test]
        public void AnotherWorkersFailedContentIsReportedUnavailable()
        {
            var source = _mesh.AddStaticContainer("source", Vector3.zero, new Vector3(64f, 64f, 64f));
            _mesh.SetOwner(source, _mesh[0]);
            var destination = ContainerRegistry.RegisterRuntime(0xC0FFEEUL, new Bounds(new Vector3(500f, 0f, 0f), new Vector3(20f, 10f, 20f)),
                new InstanceContainerInfo { InstanceId = 0xBEEFUL });
            ContainerRegistry.ApplyLease(destination.ContainerId, _mesh[1].Id, _mesh[1].Index, 1);
            var loader = Loader(destination);
            var entity = _mesh[0].SpawnServerDriven(_pawnPrefab, source, Vector3.zero, Quaternion.identity);
            var transfer = _mesh[0].Instance.PrepareTransfer(entity, destination);
            _mesh.Pump();

            loader.Current = InstanceContentState.Failed;
            _mesh[1].Tick(1);
            _mesh.Pump();
            var answers = _mesh.DeliveredOf(MsgId.InstanceReady);
            Assert.That(answers.Count, Is.EqualTo(1));
            Assert.That(answers[0].Read(InstancePreparationMsg.Read).Success, Is.False);
            Assert.That(transfer.Error, Is.Not.Null);
        }

        // ------------------------------------------------------------------------------------------------- client

        /// <summary>A transport that records every message the client sends.</summary>
        private sealed class RecordingTransport : ITransport
        {
            public readonly List<byte[]> Sent = new List<byte[]>();
            public string Name => "recording";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { }
            public void StartClient() { }
            public int Connect(string host, int port) => 7;
            public void Disconnect(int peerId) { }
            public bool IsConnected(int peerId) => true;
            public int RoundTripMs(int peerId) => -1;
            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload) => Sent.Add(payload.ToArray());
            public void Poll(Action<TransportEvent> handler) { }
            public void Flush() { }
            public void Stop() { }
            public void Dispose() { }

            public List<InstancePreparationMsg> Answers()
            {
                var answers = new List<InstancePreparationMsg>();
                foreach (var bytes in Sent)
                {
                    if (bytes.Length == 0 || (MsgId)bytes[0] != MsgId.InstanceReady) continue;
                    var reader = new NetworkReader(new ArraySegment<byte>(bytes, 1, bytes.Length - 1));
                    answers.Add(InstancePreparationMsg.Read(reader));
                }
                return answers;
            }
        }

        private sealed class ClientRig : IDisposable
        {
            public readonly NebulaClient Client;
            public readonly RecordingTransport Transport = new RecordingTransport();
            public float Now = 100f;
            private readonly GameObject _go;
            private readonly NebulaConfig _config;

            public ClientRig()
            {
                _go = new GameObject("client");
                Client = _go.AddComponent<NebulaClient>();
                _config = ScriptableObject.CreateInstance<NebulaConfig>();
                typeof(NebulaClient).GetField("_transport", Hidden).SetValue(Client, Transport);
                typeof(NebulaClient).GetProperty("Config").SetValue(Client, _config);
                typeof(NebulaClient).GetField("_gatewayPeer", Hidden).SetValue(Client, 7);
                Client.ClockForTests = () => Now;
            }

            public void Prepare(Container destination, uint request) => Receive(w => new InstancePreparationMsg
            {
                RequestId = request, EntityId = 42, Destination = destination.Ref, SourceWorker = 1, LeaseEpoch = destination.LeaseEpoch,
            }.Write(w, MsgId.InstancePrepare));

            private void Receive(Action<NetworkWriter> write)
            {
                var writer = new NetworkWriter();
                write(writer);
                typeof(NebulaClient).GetMethod("Dispatch", Hidden).Invoke(Client, new object[] { new NetworkReader(writer.ToSegment()) });
            }

            public void Dispose()
            {
                typeof(NebulaClient).GetField("_transport", Hidden).SetValue(Client, null);
                Object.DestroyImmediate(_go);
                Object.DestroyImmediate(_config);
            }
        }

        private static Container ClientDestination()
        {
            var destination = ContainerRegistry.RegisterRuntime(0xD00DUL, new Bounds(new Vector3(500f, 0f, 0f), new Vector3(20f, 10f, 20f)),
                new InstanceContainerInfo { InstanceId = 0xBEEFUL });
            ContainerRegistry.ApplyLease(destination.ContainerId, "w1", 1, 3);
            return destination;
        }

        [Test]
        public void AClientWithNoLoadersAcknowledgesAtOnce()
        {
            using var rig = new ClientRig();
            var destination = ClientDestination();
            rig.Prepare(destination, 1);
            var answers = rig.Transport.Answers();
            Assert.That(answers.Count, Is.EqualTo(1), "acknowledged in the same frame, as before");
            Assert.That(answers[0].Success, Is.True);
            Assert.That(answers[0].RequestId, Is.EqualTo(1u));
        }

        [Test]
        public void AClientAcknowledgesOnceItsContentHasLoaded()
        {
            using var rig = new ClientRig();
            var destination = ClientDestination();
            var loader = Loader(destination);
            rig.Prepare(destination, 1);
            Assert.That(rig.Transport.Answers(), Is.Empty, "no acknowledgement while the content loads");
            for (int frame = 0; frame < 3; frame++)
            {
                rig.Client.TickInstancePreparations();
                Assert.That(rig.Transport.Answers(), Is.Empty);
            }

            loader.Current = InstanceContentState.Ready;
            rig.Client.TickInstancePreparations();
            var answers = rig.Transport.Answers();
            Assert.That(answers.Count, Is.EqualTo(1));
            Assert.That(answers[0].Success, Is.True);
            Assert.That(answers[0].LeaseEpoch, Is.EqualTo(destination.LeaseEpoch));

            rig.Client.TickInstancePreparations();
            Assert.That(rig.Transport.Answers().Count, Is.EqualTo(1), "acknowledged once");
        }

        [Test]
        public void AClientReportsFailedContentAsUnavailable()
        {
            using var rig = new ClientRig();
            var destination = ClientDestination();
            var loader = Loader(destination);
            rig.Prepare(destination, 1);
            loader.Current = InstanceContentState.Failed;
            rig.Client.TickInstancePreparations();
            var answers = rig.Transport.Answers();
            Assert.That(answers.Count, Is.EqualTo(1));
            Assert.That(answers[0].Success, Is.False);
        }

        [Test]
        public void AClientDropsAPreparationFromALinkItLeftOrAfterTheWaitLimit()
        {
            using var rig = new ClientRig();
            var destination = ClientDestination();
            var loader = Loader(destination);
            rig.Prepare(destination, 1);
            typeof(NebulaClient).GetField("_gatewayPeer", Hidden).SetValue(rig.Client, 8); // a new link
            loader.Current = InstanceContentState.Ready;
            rig.Client.TickInstancePreparations();
            Assert.That(rig.Transport.Answers(), Is.Empty, "the old link's worker has moved on");

            loader.Current = InstanceContentState.Loading;
            rig.Prepare(destination, 2);
            rig.Now += InstanceScenes.ContentWaitLimitSeconds + 1f;
            LogAssert.Expect(LogType.Warning, new Regex("stopped waiting for the content"));
            rig.Client.TickInstancePreparations();
            loader.Current = InstanceContentState.Ready;
            rig.Client.TickInstancePreparations();
            Assert.That(rig.Transport.Answers(), Is.Empty, "given up, and not answered later");
        }
    }
}
