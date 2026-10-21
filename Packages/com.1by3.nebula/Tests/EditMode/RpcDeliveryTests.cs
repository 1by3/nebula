using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The delivery class of a ClientRpc (NEB-477): <c>[ClientRpc(Delivery = Delivery.Sequenced)]</c> reaches the
    /// transport on the sequenced channel, the gateway forwards it on the same channel instead of batching it reliably,
    /// and under simulated loss (NEB-472) a sequenced call is dropped where a reliable one is retransmitted late.
    /// </summary>
    public sealed class RpcDeliveryTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type GatewayType = typeof(NebulaGateway);

        private sealed class Gun : NetworkBehaviour
        {
            public int Received;

            public void ShootAtOwner() => OwnerRpc(RpcHitProbable, 7);
            public void ShootAtAll() => ClientRpc(RpcHitProbable, 7);
            public void Reload() => OwnerRpc(RpcReloaded, 1);

            [ClientRpc(Delivery = Delivery.Sequenced)]
            private void RpcHitProbable(int n) { Received = n; }

            [ClientRpc]
            private void RpcReloaded(int n) { Received = n; }
        }

        private sealed class RecordingSink : IRpcSink
        {
            public readonly List<(uint Method, ulong Target, Delivery Delivery)> Calls = new List<(uint, ulong, Delivery)>();
            public void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius)
                => Calls.Add((methodHash, targetClientId, Delivery.ReliableOrdered));
            public void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius, Delivery delivery)
                => Calls.Add((methodHash, targetClientId, delivery));
            public void SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public void SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public ulong SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<AuthorityCallResult> onDone, float timeoutSeconds) => 0;
        }

        /// <summary>A sink written before delivery classes existed: it implements only the original members.</summary>
        private sealed class LegacySink : IRpcSink
        {
            public int ClientCalls;
            public void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius) => ClientCalls++;
            public void SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public void SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public ulong SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<AuthorityCallResult> onDone, float timeoutSeconds) => 0;
        }

        /// <summary>A transport that records what it is handed and the channel it was handed on, and replays queued incoming events.</summary>
        private sealed class RecordingTransport : ITransport
        {
            public readonly List<(int Peer, Delivery Delivery, byte[] Bytes)> Sent = new List<(int, Delivery, byte[])>();
            public readonly List<TransportEvent> Incoming = new List<TransportEvent>();
            public string Name => "recording";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { }
            public void StartClient() { }
            public int Connect(string host, int port) => 1;
            public void Disconnect(int peerId) { }
            public bool IsConnected(int peerId) => true;
            public int RoundTripMs(int peerId) => 0;
            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
            {
                var copy = new byte[payload.Count];
                Buffer.BlockCopy(payload.Array, payload.Offset, copy, 0, payload.Count);
                Sent.Add((peerId, delivery, copy));
            }
            public void Poll(Action<TransportEvent> handler)
            {
                var events = new List<TransportEvent>(Incoming);
                Incoming.Clear();
                foreach (var ev in events) handler(ev);
            }
            public int Flushes;
            public void Flush() { Flushes++; }
            public void Stop() { }
            public void Dispose() { }
        }

        private bool _wasServer, _wasClient;
        private IRpcSink _previousSink;
        private readonly List<GameObject> _created = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _wasServer = NebulaRuntime.IsServer;
            _wasClient = NebulaRuntime.IsClient;
            _previousSink = NebulaRuntime.RpcSink;
        }

        [TearDown]
        public void TearDown()
        {
            NebulaRuntime.IsServer = _wasServer;
            NebulaRuntime.IsClient = _wasClient;
            NebulaRuntime.RpcSink = _previousSink;
            foreach (var go in _created) if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
        }

        // ---- the attribute and the registry --------------------------------------------------------------------

        [Test]
        public void ClientRpcDefaultsToReliableOrderedAndTheAttributeCanAskForSequenced()
        {
            Assert.AreEqual(Delivery.ReliableOrdered, new ClientRpcAttribute().Delivery);
            Assert.AreEqual(Delivery.Sequenced, RpcRegistry.Require(typeof(Gun), "RpcHitProbable").Delivery);
            Assert.AreEqual(Delivery.ReliableOrdered, RpcRegistry.Require(typeof(Gun), "RpcReloaded").Delivery);
        }

        // ---- the behaviour hands the sink the delivery class ----------------------------------------------------

        private Gun SpawnedGun()
        {
            var go = new GameObject("rpc-delivery-gun");
            _created.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var gun = go.AddComponent<Gun>();
            identity.Initialize();
            identity.NetId = 0x4771;
            identity.IsSpawned = true;
            identity.HasAuthority = true;
            identity.OwnerClientId = 42;
            NebulaRuntime.IsServer = true;
            NebulaRuntime.IsClient = false;
            return gun;
        }

        [Test]
        public void AnOwnerRpcMarkedSequencedReachesTheSinkAsSequencedAndAnUnmarkedOneAsReliable()
        {
            var gun = SpawnedGun();
            var sink = new RecordingSink();
            NebulaRuntime.RpcSink = sink;

            gun.ShootAtOwner();
            gun.ShootAtAll();
            gun.Reload();

            Assert.AreEqual(3, sink.Calls.Count);
            Assert.AreEqual((RpcRegistry.Hash("RpcHitProbable"), 42UL, Delivery.Sequenced), sink.Calls[0], "owner RPC, sequenced");
            Assert.AreEqual((RpcRegistry.Hash("RpcHitProbable"), 0UL, Delivery.Sequenced), sink.Calls[1], "broadcast, sequenced");
            Assert.AreEqual((RpcRegistry.Hash("RpcReloaded"), 42UL, Delivery.ReliableOrdered), sink.Calls[2], "unmarked, reliable");
        }

        [Test]
        public void ASinkWithoutDeliveryClassesStillGetsEveryCallAndSendsItReliable()
        {
            var gun = SpawnedGun();
            var sink = new LegacySink();
            NebulaRuntime.RpcSink = sink;
            gun.ShootAtOwner();
            Assert.AreEqual(1, sink.ClientCalls);
        }

        // ---- the gateway forwards on the delivery class it received ---------------------------------------------

        private sealed class GatewayRig
        {
            public const ulong NetId = 5, ClientId = 42;
            public const int ClientPeer = 7;

            public GameObject Host;
            public NebulaGateway Gateway;
            public object Worker, Client;

            public static GatewayRig Make(ITransport transport)
            {
                var rig = new GatewayRig { Host = new GameObject("rpc-delivery-gateway") };
                rig.Gateway = rig.Host.AddComponent<NebulaGateway>();
                GatewayType.GetField("_transport", Flags).SetValue(rig.Gateway, transport);

                var connType = GatewayType.GetNestedType("WorkerConn", BindingFlags.NonPublic);
                rig.Worker = Activator.CreateInstance(connType, true);
                connType.GetField("WorkerId").SetValue(rig.Worker, "w1");
                connType.GetField("Index").SetValue(rig.Worker, (ushort)1);
                connType.GetField("Ready").SetValue(rig.Worker, true);

                var clientType = GatewayType.GetNestedType("ClientConn", BindingFlags.NonPublic);
                rig.Client = Activator.CreateInstance(clientType, true);
                clientType.GetField("ClientId").SetValue(rig.Client, ClientId);
                clientType.GetField("PeerId").SetValue(rig.Client, ClientPeer);
                clientType.GetField("Welcomed").SetValue(rig.Client, true);
                ((System.Collections.IDictionary)GatewayType.GetField("_clientsById", Flags).GetValue(rig.Gateway)).Add(ClientId, rig.Client);
                ((System.Collections.IDictionary)GatewayType.GetField("_clientsByPeer", Flags).GetValue(rig.Gateway)).Add(ClientPeer, rig.Client);
                ((HashSet<ulong>)clientType.GetField("Visible").GetValue(rig.Client)).Add(NetId);

                var recordType = GatewayType.GetNestedType("EntityRecord", BindingFlags.NonPublic);
                var record = Activator.CreateInstance(recordType, true);
                recordType.GetField("NetId").SetValue(record, NetId);
                recordType.GetField("Epoch").SetValue(record, 1u);
                recordType.GetField("OwnerWorkerIndex").SetValue(record, (ushort)1);
                recordType.GetField("OwnerClientId").SetValue(record, ClientId);
                ((System.Collections.IList)recordType.GetField("Observers").GetValue(record)).Add(rig.Client);
                ((System.Collections.IDictionary)GatewayType.GetField("_entities", Flags).GetValue(rig.Gateway)).Add(NetId, record);
                return rig;
            }

            /// <summary>Hand the gateway one owner RPC as a worker packet that arrived on the given channel.</summary>
            public void WorkerSends(Delivery arrivedOn)
            {
                GatewayType.GetField("_workerPacketSequenced", Flags).SetValue(Gateway, arrivedOn == Delivery.Sequenced);
                var msg = new EntityRpcMsg { NetId = NetId, Epoch = 1, BehaviourIndex = 0, MethodHash = 99, ClientId = ClientId, Args = new byte[] { 1, 2, 3 } };
                GatewayType.GetMethod("OnEntityRpc", Flags).Invoke(Gateway, new[] { Worker, (object)msg });
            }

            public void SetFlushRpcsImmediately(bool on)
            {
                var config = ScriptableObject.CreateInstance<NebulaConfig>();
                config.FlushRpcsImmediately = on;
                GatewayType.GetField("<Config>k__BackingField", Flags).SetValue(Gateway, config);
            }

            public int PendingReliable => Convert.ToInt32(Client.GetType().GetField("ReliableCount").GetValue(Client));

            public void FlushReliable() => GatewayType.GetMethod("FlushReliable", Flags).Invoke(Gateway, new[] { Client });
        }

        [Test]
        public void TheGatewayForwardsASequencedOwnerRpcSequencedAndNeverBatchesItReliably()
        {
            var transport = new RecordingTransport();
            var rig = GatewayRig.Make(transport);
            _created.Add(rig.Host);

            rig.WorkerSends(Delivery.Sequenced);

            Assert.AreEqual(1, transport.Sent.Count, "sent at once");
            Assert.AreEqual(GatewayRig.ClientPeer, transport.Sent[0].Peer);
            Assert.AreEqual(Delivery.Sequenced, transport.Sent[0].Delivery, "same delivery class");
            Assert.AreEqual((byte)MsgId.EntityRpc, transport.Sent[0].Bytes[0], "a bare EntityRpc, not a Batch");
            Assert.AreEqual(0, rig.PendingReliable, "nothing waits in the client's reliable batch");
        }

        [Test]
        public void WithImmediateFlushOffTheGatewayStillBatchesAReliableOwnerRpcWithTheClientsReliableTraffic()
        {
            var transport = new RecordingTransport();
            var rig = GatewayRig.Make(transport);
            _created.Add(rig.Host);
            rig.SetFlushRpcsImmediately(false);

            rig.WorkerSends(Delivery.ReliableOrdered);

            Assert.AreEqual(0, transport.Sent.Count, "held for the batch flush");
            Assert.AreEqual(1, rig.PendingReliable);
            rig.FlushReliable();
            Assert.AreEqual(1, transport.Sent.Count);
            Assert.AreEqual(Delivery.ReliableOrdered, transport.Sent[0].Delivery);
            Assert.AreEqual((byte)MsgId.Batch, transport.Sent[0].Bytes[0]);
        }

        [Test]
        public void ByDefaultTheGatewaySendsAReliableOwnerRpcAtOnceAndFlushesTheTransport()
        {
            var transport = new RecordingTransport();
            var rig = GatewayRig.Make(transport);
            _created.Add(rig.Host);

            rig.WorkerSends(Delivery.ReliableOrdered);

            Assert.AreEqual(1, transport.Sent.Count, "not held for the end of the loop (NEB-475)");
            Assert.AreEqual(Delivery.ReliableOrdered, transport.Sent[0].Delivery);
            Assert.AreEqual((byte)MsgId.Batch, transport.Sent[0].Bytes[0]);
            Assert.AreEqual(0, rig.PendingReliable);
            Assert.AreEqual(1, transport.Flushes, "pushed onto the wire");
        }

        [Test]
        public void ASequencedOwnerRpcIsFlushedAtOnceAndIsNotWhenImmediateFlushIsOff()
        {
            var transport = new RecordingTransport();
            var rig = GatewayRig.Make(transport);
            _created.Add(rig.Host);
            rig.WorkerSends(Delivery.Sequenced);
            Assert.AreEqual(1, transport.Flushes);

            rig.SetFlushRpcsImmediately(false);
            rig.WorkerSends(Delivery.Sequenced);
            Assert.AreEqual(2, transport.Sent.Count, "still sent at once");
            Assert.AreEqual(1, transport.Flushes, "but left to the loop's flush");
        }

        // ---- under simulated loss (NEB-472) ---------------------------------------------------------------------

        [Test]
        public void UnderSimulatedLossASequencedOwnerRpcIsDroppedWhereAReliableOneIsRetransmittedLate()
        {
            var inner = new RecordingTransport();
            double now = 0;
            var simulation = new NetworkSimulation(40f, 0f, 100f); // every packet lost once; 40 ms round trip
            var sim = new SimulatedNetworkTransport(inner, simulation, new System.Random(1), () => now);
            var rig = GatewayRig.Make(sim);
            _created.Add(rig.Host);

            rig.WorkerSends(Delivery.Sequenced);
            rig.WorkerSends(Delivery.ReliableOrdered);
            rig.FlushReliable();
            for (int i = 0; i < 30; i++) { now += 0.01; sim.Flush(); }

            Assert.AreEqual(1, sim.DroppedPackets, "the sequenced call was dropped, not retransmitted");
            Assert.AreEqual(1, inner.Sent.Count, "only the reliable batch was ever sent");
            Assert.AreEqual(Delivery.ReliableOrdered, inner.Sent[0].Delivery);
            Assert.AreEqual((byte)MsgId.Batch, inner.Sent[0].Bytes[0]);
        }

        [Test]
        public void ASequencedPacketKeepsItsChannelThroughTheSimulatedLinkWhenItIsDelayed()
        {
            var inner = new RecordingTransport();
            double now = 0;
            var sim = new SimulatedNetworkTransport(inner, new NetworkSimulation(20f, 0f, 0f), new System.Random(1), () => now);
            inner.Incoming.Add(new TransportEvent(TransportEvent.Kind.Data, 1, new ArraySegment<byte>(new byte[] { 1 }), true));
            inner.Incoming.Add(new TransportEvent(TransportEvent.Kind.Data, 1, new ArraySegment<byte>(new byte[] { 2 }), false));
            var seen = new List<(byte Id, bool Sequenced)>();
            sim.Poll(ev => seen.Add((ev.Data.Array[ev.Data.Offset], ev.Sequenced)));
            Assert.AreEqual(0, seen.Count, "held for half the latency");
            now = 0.02;
            sim.Poll(ev => seen.Add((ev.Data.Array[ev.Data.Offset], ev.Sequenced)));
            Assert.AreEqual(2, seen.Count);
            Assert.AreEqual((byte)1, seen[0].Id);
            Assert.IsTrue(seen[0].Sequenced, "the sequenced packet is still marked sequenced");
            Assert.AreEqual((byte)2, seen[1].Id);
            Assert.IsFalse(seen[1].Sequenced);
        }
    }
}
