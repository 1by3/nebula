using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Carrier handoff and carrier destruction driven through a <b>real</b> <see cref="NebulaWorker"/>: its own
    /// <c>TransferAuthority</c>, <c>InterestRemove</c> and <c>RemoveLocal</c>, with a recording transport in
    /// place of a socket and one gateway link that subscribes no region at all, so anything it is sent is
    /// something the production code decided to send it.
    /// <para>
    /// These are the tests that protect the implementation rather than the idea: delete the follower
    /// suppression and <see cref="APassengerLeavingWithItsShipIsNeverAnnouncedAsAnOrphan"/> fails; delete the
    /// evacuation from <c>RemoveLocal</c> and
    /// <see cref="ASurvivorIsReparentedBeforeItsRestoredPlacementIsPublished"/> fails; drop the exception
    /// safety and <see cref="AThrowingHandoverCallbackLeavesNoPoisonedBookkeeping"/> fails. The service-level
    /// carrier tests in <c>Nebula.Services.Tests</c> cover the same design over the wire, but through the
    /// <c>FakeWorker</c> fixture.
    /// </para>
    /// </summary>
    public sealed class WorkerHandoverTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type PeerType = typeof(NebulaWorker).GetNestedType("Peer", BindingFlags.NonPublic);

        /// <summary>A transport that keeps what the worker handed it, so a test can read the wire.</summary>
        private sealed class RecordingTransport : ITransport
        {
            public readonly List<KeyValuePair<int, byte[]>> Sent = new List<KeyValuePair<int, byte[]>>();

            public string Name => "recording";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { }
            public void StartClient() { }
            public int Connect(string host, int port) => 0;
            public void Disconnect(int peerId) { }
            public bool IsConnected(int peerId) => true;
            public int RoundTripMs(int peerId) => 0;

            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
            {
                var copy = new byte[payload.Count];
                Array.Copy(payload.Array, payload.Offset, copy, 0, payload.Count);
                Sent.Add(new KeyValuePair<int, byte[]>(peerId, copy));
            }

            public void Poll(Action<TransportEvent> handler) { }
            public void Flush() { }
            public void Stop() { }
            public void Dispose() { }
        }

        private const int GatewayPeerId = 1;
        private const int TargetPeerId = 2;

        private readonly List<GameObject> _objects = new List<GameObject>();
        private NebulaWorker _worker;
        private RecordingTransport _transport;
        private object _target;

        // ------------------------------------------------------------------------------------------- fixture

        [SetUp]
        public void SetUp()
        {
            MakeStatic("handover-outdoor", Vector3.zero, new Vector3(400, 60, 400));
            ContainerRegistry.Rebuild();

            var host = new GameObject("worker");
            _objects.Add(host);
            _worker = host.AddComponent<NebulaWorker>();
            _transport = new RecordingTransport();
            SetField("_transport", _transport);
            SetField("_interestGrid", InterestGrid.Resolve(InterestSettings.Default));

            var receiver = new RegionSubscriptionReceiver();
            var gateway = MakePeer(GatewayPeerId, PeerRole.Gateway, "g1", 1, bit: 0, receiver);
            _target = MakePeer(TargetPeerId, PeerRole.Worker, "w2", 2, bit: -1, null);
            ((System.Collections.IList)GetField("_gateways")).Add(gateway);
            ((Array)GetField("_gatewayBits")).SetValue(gateway, 0);
            ((RegionPublisher)GetField("_publisher")).AddGateway(0, receiver);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
        }

        private static object MakePeer(int peerId, PeerRole role, string id, uint index, int bit, RegionSubscriptionReceiver receiver)
        {
            object peer = Activator.CreateInstance(PeerType, nonPublic: true);
            PeerType.GetField("PeerId").SetValue(peer, peerId);
            PeerType.GetField("Role").SetValue(peer, role);
            PeerType.GetField("Id").SetValue(peer, id);
            PeerType.GetField("Key").SetValue(peer, id + "#1");
            PeerType.GetField("Index").SetValue(peer, index);
            PeerType.GetField("HelloReceived").SetValue(peer, true);
            PeerType.GetField("GatewayBit").SetValue(peer, bit);
            if (receiver != null) PeerType.GetField("Subscription").SetValue(peer, receiver);
            return peer;
        }

        private object GetField(string name) => typeof(NebulaWorker).GetField(name, Flags).GetValue(_worker);
        private void SetField(string name, object value) => typeof(NebulaWorker).GetField(name, Flags).SetValue(_worker, value);
        private HandoverScope Handover => (HandoverScope)GetField("_handover");
        private int PooledContentLists => ((System.Collections.ICollection)GetField("_contentsPool")).Count;

        private void Invoke(string method, params object[] args)
        {
            try { typeof(NebulaWorker).GetMethod(method, Flags).Invoke(_worker, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }

        private Container MakeStatic(string id, Vector3 position, Vector3 size)
        {
            var go = new GameObject(id);
            _objects.Add(go);
            go.transform.position = position;
            var c = go.AddComponent<Container>();
            c.ContainerId = id;
            c.Size = size;
            c.Center = new Vector3(0, size.y * 0.5f, 0);
            return c;
        }

        /// <summary>
        /// A carrier this worker owns: identity, dynamic container, indexed and authoritative. A scene id keeps
        /// <c>RemoveLocal</c> on the unbind branch, because <c>Destroy</c> is not available in edit mode.
        /// </summary>
        private NetworkIdentity MakeCarrier(string name, ulong netId, Vector3 position, Container inside = null)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = name;
            box.Size = new Vector3(10, 6, 20);
            box.Center = new Vector3(0, 3, 0);
            go.AddComponent<DynamicContainer>();
            identity.SceneId = (uint)netId;
            identity.Initialize();
            identity.NetId = netId;
            identity.Epoch = 1;
            identity.SetContainer(inside ?? ContainerRegistry.Find(position, box));
            identity.InvokeSpawn(); // DynamicContainer.OnNetworkSpawn registers the box
            return Own(identity);
        }

        /// <summary>An always-relevant passenger riding in <paramref name="carrier"/>: global the moment it is orphaned.</summary>
        private NetworkIdentity MakePassenger(string name, ulong netId, NetworkIdentity carrier)
        {
            var go = new GameObject(name);
            go.transform.position = carrier.transform.position + Vector3.up;
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            identity.SceneId = (uint)netId;
            identity.AlwaysRelevant = true;
            identity.Initialize();
            identity.NetId = netId;
            identity.Epoch = 1;
            identity.SetContainer(carrier.Carried);
            identity.InvokeSpawn();
            return Own(identity);
        }

        private NetworkIdentity Own(NetworkIdentity identity)
        {
            identity.SetAuthority(true);
            ((System.Collections.IDictionary)GetField("_entities"))[identity.NetId] = identity;
            ((System.Collections.IList)GetField("_authoritative")).Add(identity);
            Invoke("InterestAdd", identity);
            return identity;
        }

        /// <summary>Every entity spawn the worker sent to one peer, decoded.</summary>
        private List<EntitySpawnMsg> SpawnsTo(int peerId)
        {
            var result = new List<EntitySpawnMsg>();
            for (int i = 0; i < _transport.Sent.Count; i++)
            {
                var sent = _transport.Sent[i];
                if (sent.Key != peerId || sent.Value.Length == 0 || sent.Value[0] != (byte)MsgId.EntitySpawn) continue;
                var reader = new NetworkReader(sent.Value);
                reader.ReadByte();
                result.Add(EntitySpawnMsg.Read(reader));
            }
            return result;
        }

        private static EntitySpawnMsg SpawnOf(List<EntitySpawnMsg> spawns, ulong netId)
        {
            for (int i = 0; i < spawns.Count; i++) if (spawns[i].NetId == netId) return spawns[i];
            Assert.Fail($"#{netId} was never spawned to that gateway");
            return default;
        }

        // ------------------------------------------------------------------------------------------- handoff

        [Test]
        public void APassengerLeavingWithItsShipIsNeverAnnouncedAsAnOrphan()
        {
            var ship = MakeCarrier("ship", 100, Vector3.zero);
            MakePassenger("crate", 101, ship);
            _transport.Sent.Clear();

            Invoke("TransferAuthority", ship, _target);

            Assert.IsEmpty(SpawnsTo(GatewayPeerId),
                "the crate is always relevant, so taking the ship out of the index would make it global again - " +
                "but it is leaving on the same stream and must not be announced to a gateway that then gets no forget");
            Assert.AreEqual(2, _worker.HandoversOut, "the ship and the crate both went");
            Assert.AreEqual(0, Handover.Depth);
            Assert.AreEqual(0, Handover.FollowerCount);
        }

        [Test]
        public void AThrowingHandoverCallbackLeavesNoPoisonedBookkeeping()
        {
            var ship = MakeCarrier("ship", 100, Vector3.zero);
            MakePassenger("crate", 101, ship);
            Action<NetworkIdentity, string> thrower = (e, to) =>
            {
                if (e.NetId == 101) throw new InvalidOperationException("a subscriber threw mid-handoff");
            };
            _worker.AuthorityHandedOff += thrower;

            var thrown = Assert.Throws<InvalidOperationException>(() => Invoke("TransferAuthority", ship, _target));
            Assert.AreEqual("a subscriber threw mid-handoff", thrown.Message, "the original failure is not swallowed");
            _worker.AuthorityHandedOff -= thrower;

            Assert.AreEqual(0, Handover.Depth, "a failed handoff must not leave the worker inside one");
            Assert.AreEqual(0, Handover.FollowerCount, "nor holding the followers it collected");
            Assert.GreaterOrEqual(PooledContentLists, 1, "the contents snapshot the failed recursion borrowed came back");
        }

        [Test]
        public void ALaterHandoffAfterAFailedOneStillSuppressesItsOwnFollowers()
        {
            // 1-3: a carrier-subtree handoff that throws part way through.
            var ship = MakeCarrier("ship", 100, Vector3.zero);
            MakePassenger("crate", 101, ship);
            Action<NetworkIdentity, string> thrower = (e, to) =>
            {
                if (e.NetId == 101) throw new InvalidOperationException("a subscriber threw mid-handoff");
            };
            _worker.AuthorityHandedOff += thrower;
            Assert.Throws<InvalidOperationException>(() => Invoke("TransferAuthority", ship, _target));
            _worker.AuthorityHandedOff -= thrower;

            // 4: an unrelated carrier subtree, handed over cleanly.
            var tug = MakeCarrier("tug", 200, new Vector3(60, 0, 0));
            MakePassenger("barrel", 201, tug);
            bool observed = false;
            Action<NetworkIdentity, string> watcher = (e, to) =>
            {
                if (e.NetId != 200) return;
                observed = true;
                Assert.IsTrue(Handover.Follows(201), "the second handoff collected its own passenger");
                Assert.IsFalse(Handover.Follows(101), "and nothing from the one that failed");
                Assert.AreEqual(1, Handover.FollowerCount);
            };
            _worker.AuthorityHandedOff += watcher;
            _transport.Sent.Clear();
            Invoke("TransferAuthority", tug, _target);
            _worker.AuthorityHandedOff -= watcher;

            // 5: and the barrel was never published to the gateway as an orphan in between.
            Assert.IsTrue(observed, "the tug's handoff ran");
            Assert.IsEmpty(SpawnsTo(GatewayPeerId));
            Assert.AreEqual(0, Handover.Depth);
            Assert.AreEqual(0, Handover.FollowerCount);
        }

        [Test]
        public void APinnedInteriorStaysBehindAndGetsItsOwnPlacementBack()
        {
            var ship = MakeCarrier("ship", 100, Vector3.zero);
            var crate = MakePassenger("crate", 101, ship);
            ship.Carried.LeaseState = Nebula.LeaseState.Pinned;
            ship.Carried.OwnerWorkerId = "w9";
            Assume.That(ship.Carried.IsPinned, Is.True, "the interior has to be pinned for this test to mean anything");
            _transport.Sent.Clear();

            Invoke("TransferAuthority", ship, _target);

            Assert.AreEqual(1, _worker.HandoversOut, "a pinned interior does not travel with the hull");
            var spawn = SpawnOf(SpawnsTo(GatewayPeerId), crate.NetId);
            Assert.AreEqual(crate.NetId, spawn.NetId, "the crate really was orphaned, so it is global again and announced");
        }

        // --------------------------------------------------------------------------------------- destruction

        [Test]
        public void ASurvivorIsReparentedBeforeItsRestoredPlacementIsPublished()
        {
            var ship = MakeCarrier("ship", 100, Vector3.zero);
            var crate = MakePassenger("crate", 101, ship);
            var around = ship.ContainerRef;
            Assume.That(crate.ContainerRef.IsDynamic, Is.True, "the crate starts out aboard");
            _transport.Sent.Clear();

            Invoke("RemoveLocal", ship);

            var spawn = SpawnOf(SpawnsTo(GatewayPeerId), crate.NetId);
            Assert.IsFalse(spawn.Container.IsDynamic,
                "the spawn the gateway is sent must not name the carrier that has just left: ScopeContainer fails " +
                "closed, so the gateway would cache a record no client could ever be shown (design D86)");
            Assert.AreEqual(around, spawn.Container, "it is put down in the container the ship itself sat in");
        }

        [Test]
        public void ANestedSurvivorKeepsNamingACarrierThatIsStillThere()
        {
            var ship = MakeCarrier("ship", 100, Vector3.zero);
            var hold = MakeCarrier("hold", 101, Vector3.zero, ship.Carried);
            var crate = MakePassenger("crate", 102, hold);
            var around = ship.ContainerRef;
            _transport.Sent.Clear();

            Invoke("RemoveLocal", ship);

            Assert.AreEqual(around, hold.ContainerRef, "the direct content is put down where the ship itself sat");
            Assert.AreEqual(ContainerRef.Dynamic(hold.NetId), crate.ContainerRef,
                "only the direct contents are put down; anything deeper names a carrier that is still there");
            Assert.IsNotNull(ContainerRegistry.Resolve(crate.ContainerRef), "so a nested survivor still resolves");
        }
    }
}
