using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    public class TransformReplicationTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private struct Input : INetworkInput
        {
            public void Serialize(NetworkWriter w) { }
            public void Deserialize(NetworkReader r) { }
        }
        private sealed class Predicted : PredictedBehaviour<Input>
        {
            protected override Input GatherInput() => default;
            protected override void Simulate(uint tick, in Input input, float dt) { }
        }

        [Test] public void PredictedOwnerKeepsItsPoseWhileObserversReceiveMovement()
        {
            var go = new GameObject("predicted-owner"); _objects.Add(go);
            go.AddComponent<Predicted>();
            var owner = go.GetComponent<NetworkIdentity>(); owner.Initialize(); owner.NetId = 17; owner.Epoch = 1;
            owner.IsLocalPlayer = true; owner.InvokeSpawn();
            Assert.IsNotNull(owner.RootTransform);
            owner.transform.position = Vector3.right * 20;
            var authority = Entity(); authority.transform.position = Vector3.right * 3;
            authority.RootTransform.CaptureRoot(5, out var entry);
            owner.ReceiveState(5, 2, entry); owner.RemoteTick(5);
            Assert.AreEqual(20, owner.transform.position.x, "only reconciliation may correct the predicted owner");
            var observer = Entity(true, false); observer.RootTransform.Interpolate = false;
            observer.ReceiveState(5, 2, entry);
            Assert.AreEqual(3, observer.transform.position.x);
        }

        [Test] public void CarrierValidationReportsDisabledAxesAndOwnerPhysics()
        {
            var go = new GameObject("carrier-validation"); _objects.Add(go);
            go.AddComponent<DynamicContainer>();
            var id = go.GetComponent<NetworkIdentity>();
            id.GetComponent<NetworkTransform>().SyncPositionX = false;
            var issues = new List<Nebula.Editor.NebulaValidator.Issue>();
            Nebula.Editor.NebulaValidator.CheckMotion(id, issues);
            Assert.IsTrue(issues.Exists(i => i.Severity == Nebula.Editor.NebulaValidator.Severity.Error));
            id.GetComponent<NetworkTransform>().SyncPositionX = true;
            issues.Clear(); Nebula.Editor.NebulaValidator.CheckMotion(id, issues);
            Assert.IsEmpty(issues);
            go.AddComponent<NetworkRigidbody>(); id.GetComponent<NetworkTransform>().Authority = AuthorityMode.Owner;
            Nebula.Editor.NebulaValidator.CheckMotion(id, issues);
            Assert.IsTrue(issues.Exists(i => i.Message.Contains("worker-authoritative")));
        }

        [Test] public void OwnerAuthoritativeTransformIgnoresItsLocationEcho()
        {
            NebulaRuntime.IsServer = false; NebulaRuntime.IsClient = true;
            var owner = Entity(true, false);
            owner.OwnerClientId = 1; owner.IsLocalPlayer = true; owner.RootTransform.Authority = AuthorityMode.Owner;
            owner.transform.position = Vector3.right * 20;
            var old = EntityStateEntry.Snapshot(owner); old.LocalPosition = Vector3.zero;
            old.Fields |= TransformFields.Location | TransformFields.Reliable;
            owner.ReceiveState(5, 2, old); owner.RemoteTick(5);
            Assert.AreEqual(20, owner.transform.position.x);
        }

        [Test] public void HandoverIncludesDisabledChildAxesAndWorldTeleportUsesWorldCoordinates()
        {
            var id = Entity();
            var c = ContainerRegistry.RegisterRuntime(90, new Bounds(Vector3.right * 100, Vector3.one * 20));
            id.SetContainer(c);
            id.RootTransform.Teleport(Vector3.right * 115, Quaternion.identity, Vector3.one);
            Assert.AreEqual(115, id.transform.position.x, 0.001f);
            var go = new GameObject("child"); go.transform.SetParent(id.transform, false);
            var nt = go.AddComponent<NetworkTransform>(); nt.SyncScaleX = false;
            go.transform.localPosition = new Vector3(1, 2, 3); go.transform.localScale = new Vector3(4, 5, 6);
            var w = new NetworkWriter(); nt.WriteHandoverState(w);
            go.transform.localPosition = Vector3.zero; go.transform.localScale = Vector3.one;
            nt.ReadHandoverState(new NetworkReader(w.ToSegment()));
            Assert.AreEqual(new Vector3(1, 2, 3), go.transform.localPosition);
            Assert.AreEqual(new Vector3(4, 5, 6), go.transform.localScale);
        }

        private sealed class CaptureTransport : ITransport
        {
            public readonly List<(Delivery delivery, byte[] bytes)> Sent = new List<(Delivery, byte[])>();
            public string Name => "test";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { } public void StartClient() { }
            public int Connect(string host, int port) => 1;
            public void Disconnect(int peerId) { } public bool IsConnected(int peerId) => true;
            public int RoundTripMs(int peerId) => 0;
            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
            { var bytes = new byte[payload.Count]; Array.Copy(payload.Array, payload.Offset, bytes, 0, bytes.Length); Sent.Add((delivery, bytes)); }
            public void Poll(Action<TransportEvent> handler) { } public void Stop() { } public void Dispose() { }
        }

        [Test] public void WorkerBatchesOnlyOptedInEntitiesWithinSequencedPacketBudget()
        {
            var go = new GameObject("worker-test"); _objects.Add(go);
            var worker = go.AddComponent<NebulaWorker>(); var transport = new CaptureTransport();
            var type = typeof(NebulaWorker); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            type.GetField("_transport", flags).SetValue(worker, transport);
            var authority = (IList)type.GetField("_authoritative", flags).GetValue(worker);
            var gateways = (IList)type.GetField("_gateways", flags).GetValue(worker);
            var peerType = type.GetNestedType("Peer", BindingFlags.NonPublic); var peer = Activator.CreateInstance(peerType, true);
            peerType.GetField("PeerId").SetValue(peer, 1); gateways.Add(peer);
            for (int i = 0; i < 60; i++)
            {
                var id = Entity(i % 2 == 0); id.NetId = (ulong)(i + 1); authority.Add(id);
                id.PrepareReplication(1); id.ClearDirty();
                id.transform.position = Vector3.right * 4; id.PrepareReplication(2);
            }
            type.GetMethod("PublishToGateways", flags).Invoke(worker, new object[] { 2u });
            int count = 0;
            foreach (var packet in transport.Sent)
            {
                Assert.AreEqual(Delivery.Sequenced, packet.delivery);
                Assert.LessOrEqual(packet.bytes.Length, NebulaWorker.StateBatchBytes);
                var r = new NetworkReader(packet.bytes); Assert.AreEqual((byte)MsgId.WorldState, r.ReadByte());
                WorldStateMsg.ReadHeader(r, out _, out _, out ushort n); count += n;
                for (int i = 0; i < n; i++) Assert.AreEqual(1, EntityStateEntry.Read(r).NetId % 2);
            }
            Assert.AreEqual(30, count);
            Assert.Less(transport.Sent.Count, 10, "updates are batched rather than one packet per entity");
        }

        [Test] public void StaticSceneEntityRetainsScaleWhenCellUnloadsAndReloads()
        {
            var go = new GameObject("scene-reload-client"); _objects.Add(go);
            var client = go.AddComponent<NebulaClient>();
            var type = typeof(NebulaClient);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var entity = Entity(false, false); entity.SceneId = 123456;
            entity.transform.position = new Vector3(8, 3, -5);
            entity.transform.localScale = new Vector3(2, 3, 4);
            var entities = (IDictionary)type.GetField("_entities", flags).GetValue(client);
            entities.Add(entity.NetId, entity);
            type.GetMethod("OnSceneEntityUnregistering", flags).Invoke(client, new object[] { entity });
            Assert.AreEqual(0, entity.NetId);
            var pending = (IDictionary)type.GetField("_pendingScene", flags).GetValue(client);
            var snapshot = (EntitySpawnMsg)pending[entity.SceneId];
            Assert.AreEqual(new Vector3(2, 3, 4), snapshot.LocalScale);

            // A freshly loaded scene copy starts with its authored transform, then binds to the held spawn.
            entity.transform.position = Vector3.zero;
            entity.transform.localScale = Vector3.one;
            SceneEntities.Register(entity);
            try
            {
                type.GetMethod("OnSceneEntityRegistered", flags).Invoke(client, new object[] { entity });
                Assert.IsNull(entity.RootTransform, "static scene objects must not require transform replication");
                Assert.IsTrue(entity.IsSpawned);
                Assert.AreEqual(new Vector3(8, 3, -5), entity.transform.position);
                Assert.AreEqual(new Vector3(2, 3, 4), entity.transform.localScale);
                Assert.IsTrue(entity.gameObject.activeInHierarchy);
            }
            finally { SceneEntities.Unregister(entity); }
        }

        private NetworkIdentity Entity(bool sync = true, bool authority = true)
        {
            var go = new GameObject("replication-test");
            _objects.Add(go);
            var id = go.AddComponent<NetworkIdentity>();
            if (sync) go.AddComponent<NetworkTransform>();
            id.Initialize();
            id.NetId = 17; id.Epoch = 1; id.HasAuthority = authority;
            id.InvokeSpawn();
            return id;
        }

        [SetUp] public void SetUp() { NebulaRuntime.Reset(); NebulaRuntime.IsServer = true; ContainerRegistry.Rebuild(); }
        [TearDown] public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear(); ContainerRegistry.Rebuild(); NebulaRuntime.Reset();
        }

        private static EntityStateEntry Wire(EntityStateEntry entry, out int bytes)
        {
            var w = new NetworkWriter(); entry.Write(w); bytes = w.Length;
            return EntityStateEntry.Read(new NetworkReader(w.ToSegment()));
        }

        [Test] public void BareIdentitySendsLocationOnceButNeverStreamsMotion()
        {
            var id = Entity(false);
            id.PrepareReplication(1);
            Assert.IsTrue(id.HasReplicationState);
            Assert.IsTrue(id.ReplicationState.Reliable);
            for (uint tick = 2; tick < 100; tick++)
            {
                id.transform.position = Vector3.right * tick;
                id.PrepareReplication(tick);
                Assert.IsFalse(id.HasReplicationState);
            }
        }

        [Test] public void OneAxisCostsOneFloatAndDisabledAxesStayLocal()
        {
            var a = Entity(); var b = Entity(true, false);
            a.RootTransform.SyncPositionY = a.RootTransform.SyncPositionZ = false;
            a.RootTransform.SyncRotAngleX = a.RootTransform.SyncRotAngleY = a.RootTransform.SyncRotAngleZ = false;
            a.RootTransform.SyncVelocity = false;
            b.RootTransform.Interpolate = false;
            b.transform.position = new Vector3(0, 7, 9);
            b.transform.localScale = Vector3.one * 3;
            a.transform.position = new Vector3(12, 25, 36);
            Assert.IsTrue(a.RootTransform.CaptureRoot(1, out var entry));
            entry = Wire(entry, out int bytes);
            Assert.AreEqual(8 + 4 + 2 + 2 + 4, bytes);
            Assert.IsTrue(b.ReceiveState(1, 2, entry));
            Assert.AreEqual(new Vector3(12, 7, 9), b.transform.position);
            Assert.AreEqual(Vector3.one * 3, b.transform.localScale);
            a.transform.position = new Vector3(12, 50, 100);
            Assert.IsFalse(a.RootTransform.CaptureRoot(2, out _), "disabled axes cannot trigger an update");
        }

        [Test] public void LostFinalUpdateGetsReliableRecoveryThenIdleCostsNothing()
        {
            var a = Entity(); var b = Entity(true, false);
            b.RootTransform.Interpolate = false;
            a.RootTransform.CaptureRoot(1, out var first); b.ReceiveState(1, 2, Wire(first, out _));
            a.transform.position = new Vector3(9, 0, 0);
            Assert.IsTrue(a.RootTransform.CaptureRoot(2, out _)); // drop this update
            Assert.IsFalse(a.RootTransform.CaptureRoot(3, out _));
            uint repairTick = 1 + NetworkIdentity.SyncKeyframeInterval;
            Assert.IsTrue(a.RootTransform.CaptureRoot(repairTick, out var repair));
            Assert.IsTrue(repair.Reliable, "also bypasses distance-tier tick filtering");
            b.ReceiveState(repairTick, 2, Wire(repair, out _));
            Assert.AreEqual(9, b.transform.position.x);
            for (uint t = repairTick + 1; t < repairTick + 100; t++) Assert.IsFalse(a.RootTransform.CaptureRoot(t, out _));
        }

        [Test] public void ScaleOnlyReplicationDoesNotMoveOrRotateTheRoot()
        {
            var a = Entity(); var b = Entity(true, false);
            var nt = a.RootTransform;
            nt.SyncPositionX = nt.SyncPositionY = nt.SyncPositionZ = false;
            nt.SyncRotAngleX = nt.SyncRotAngleY = nt.SyncRotAngleZ = false;
            nt.SyncScaleY = true;
            b.RootTransform.Interpolate = false;
            b.transform.position = Vector3.one * 7;
            b.transform.rotation = Quaternion.Euler(0, 45, 0);
            a.transform.localScale = new Vector3(5, 3, 6);
            nt.CaptureRoot(1, out var entry); b.ReceiveState(1, 2, Wire(entry, out _));
            Assert.AreEqual(Vector3.one * 7, b.transform.position);
            Assert.Less(Quaternion.Angle(Quaternion.Euler(0, 45, 0), b.transform.rotation), 0.001f);
            Assert.AreEqual(new Vector3(1, 3, 1), b.transform.localScale);
        }

        [Test] public void DisablingAllAxesStopsAnAlreadyOpenStream()
        {
            var a = Entity(); var b = Entity(true, false); b.RootTransform.Interpolate = false;
            a.RootTransform.CaptureRoot(1, out var first); b.ReceiveState(1, 2, first);
            var nt = a.RootTransform;
            nt.SyncPositionX = nt.SyncPositionY = nt.SyncPositionZ = false;
            nt.SyncRotAngleX = nt.SyncRotAngleY = nt.SyncRotAngleZ = false;
            Assert.IsTrue(nt.CaptureRoot(2, out var disabled));
            Assert.IsTrue(disabled.Reliable);
            Assert.AreEqual(TransformFields.None, disabled.Fields & TransformFields.Axes);
            b.ReceiveState(2, 2, disabled); b.transform.position = Vector3.one * 7;
            b.RemoteTick(3);
            Assert.AreEqual(Vector3.one * 7, b.transform.position);
            Assert.IsFalse(nt.CaptureRoot(100, out _));
        }

        [Test] public void ScaleUsesTheSameBufferedTickAsPosition()
        {
            var a = Entity(); var b = Entity(true, false);
            a.RootTransform.SyncScaleX = a.RootTransform.SyncScaleY = a.RootTransform.SyncScaleZ = true;
            a.RootTransform.CaptureRoot(10, out var first); b.ReceiveState(10, 2, first);
            a.transform.localScale = Vector3.one * 3;
            a.transform.position = Vector3.right * 4;
            a.RootTransform.CaptureRoot(12, out var next); b.ReceiveState(12, 2, next);
            b.RemoteTick(11);
            Assert.AreEqual(Vector3.one * 2, b.transform.localScale);
            Assert.AreEqual(Vector3.right * 2, b.transform.position);
        }

        [Test] public void TeleportAndNewEpochRejectOlderUnreliableSamples()
        {
            var a = Entity(); var b = Entity(true, false);
            a.RootTransform.Teleport(Vector3.right * 40, Quaternion.identity, Vector3.one);
            a.RootTransform.CaptureRoot(10, out var teleport);
            Assert.IsTrue(teleport.Reliable);
            b.ReceiveState(10, 2, Wire(teleport, out _));
            Assert.AreEqual(40, b.transform.position.x);
            var stale = teleport; stale.LocalPosition = Vector3.zero;
            Assert.IsFalse(b.ReceiveState(9, 2, stale));
            teleport.Epoch = 2;
            Assert.IsTrue(b.ReceiveState(11, 3, teleport));
            Assert.IsFalse(b.ReceiveState(12, 2, stale));
            Assert.AreEqual(3, b.OwnerWorkerIndex);
        }

        [Test] public void StaticLocationWaitsForContainerAndSurvivesReorderedDelivery()
        {
            var a = Entity(false); var b = Entity(false, false);
            var entry = EntityStateEntry.Snapshot(a);
            entry.Container = ContainerRef.Runtime(122);
            entry.LocalPosition = Vector3.right * 2;
            entry.Fields |= TransformFields.Location | TransformFields.Reliable;
            Assert.IsFalse(b.ReceiveState(5, 3, entry));
            var c = ContainerRegistry.RegisterRuntime(122, new Bounds(Vector3.right * 100, Vector3.one * 20));
            b.ReplayPendingState();
            Assert.AreSame(c, b.Container);
            Assert.AreEqual(2, b.LocalPosition.x);
            Assert.IsFalse(b.ReceiveState(4, 2, entry));
            Assert.AreEqual(3, b.OwnerWorkerIndex);
        }

        [Test] public void RootInterpolatesInMovingContainerFrame()
        {
            var a = Entity(); var b = Entity(true, false);
            var c = ContainerRegistry.RegisterRuntime(34, new Bounds(Vector3.zero, Vector3.one * 20));
            a.SetContainer(c); b.SetContainer(c);
            a.RootTransform.CaptureRoot(10, out var first); b.ReceiveState(10, 2, first);
            a.SetLocalPose(c, Vector3.right * 4, Quaternion.identity);
            a.RootTransform.CaptureRoot(12, out var next); b.ReceiveState(12, 2, next);
            c.transform.position += Vector3.right * 100;
            ContainerRegistry.RefreshCaches();
            b.RemoteTick(11);
            Assert.AreEqual(2, b.LocalPosition.x, 0.001f);
            Assert.AreEqual(c.ToWorld(Vector3.right * 2).x, b.transform.position.x, 0.001f);
        }

        [Test] public void PhysicsHandoverPreservesBothVelocitiesWhenPoseVelocityIsDisabled()
        {
            var go = new GameObject("physics-authority"); _objects.Add(go);
            var nr = go.AddComponent<NetworkRigidbody>();
            var id = go.GetComponent<NetworkIdentity>(); id.Initialize(); id.HasAuthority = true; id.InvokeSpawn();
            Assert.IsNotNull(id.RootTransform, "Rigidbody requires a visible transform component");
            id.RootTransform.SyncVelocity = false;
            nr.SetVelocity(new Vector3(1, 2, 3), new Vector3(4, 5, 6));
            var w = new NetworkWriter(); nr.WriteHandoverState(w);
            var copy = new GameObject("physics-copy"); _objects.Add(copy);
            var rb = copy.AddComponent<NetworkRigidbody>();
            var other = copy.GetComponent<NetworkIdentity>(); other.Initialize(); other.InvokeSpawn();
            Assert.IsTrue(rb.Body.isKinematic);
            rb.ReadHandoverState(new NetworkReader(w.ToSegment()));
            other.SetAuthority(true);
            Assert.IsFalse(rb.Body.isKinematic);
            Assert.AreEqual(new Vector3(1, 2, 3), rb.Body.linearVelocity);
            Assert.AreEqual(new Vector3(4, 5, 6), rb.Body.angularVelocity);
        }

        [Test] public void SpawnKeepsScaleWithoutTransformAndTransferKeepsGhostRecipients()
        {
            var a = Entity(false); a.transform.localScale = new Vector3(2, 3, 4);
            var msg = new AuthorityTransferMsg { Entity = EntitySpawnMsg.From(a, new NetworkWriter()), NewEpoch = 2, GhostWorkers = new[] { "w1", "w3" } };
            var w = new NetworkWriter(); msg.Write(w); var r = new NetworkReader(w.ToSegment()); r.ReadByte();
            var copy = AuthorityTransferMsg.Read(r);
            Assert.AreEqual(new Vector3(2, 3, 4), copy.Entity.LocalScale);
            CollectionAssert.AreEqual(msg.GhostWorkers, copy.GhostWorkers);
        }

        [Test] public void GatewayMergesMaskedPoseVelocityAndRejectsStaleTicks()
        {
            var a = Entity(); var go = new GameObject("gateway-test"); _objects.Add(go);
            var gateway = go.AddComponent<NebulaGateway>();
            var type = typeof(NebulaGateway);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var records = (IDictionary)type.GetField("_entities", flags).GetValue(gateway);
            var recordType = type.GetNestedType("EntityRecord", BindingFlags.NonPublic);
            var record = Activator.CreateInstance(recordType, true);
            recordType.GetField("Epoch").SetValue(record, 1u);
            recordType.GetField("OwnerWorkerIndex").SetValue(record, (ushort)2);
            var spawn = EntitySpawnMsg.From(a, new NetworkWriter()); spawn.LocalPosition = new Vector3(1, 2, 3);
            recordType.GetField("LastSpawn").SetValue(record, spawn);
            records.Add(a.NetId, record);
            var workerType = type.GetNestedType("WorkerConn", BindingFlags.NonPublic);
            var worker = Activator.CreateInstance(workerType, true); workerType.GetField("Index").SetValue(worker, (ushort)2);
            var entry = EntityStateEntry.Snapshot(a); entry.Fields = TransformFields.PositionX | TransformFields.Velocity;
            entry.LocalPosition = Vector3.right * 9; entry.Velocity = Vector3.forward * 7;
            void Send(uint tick)
            {
                var w = new NetworkWriter(); var slot = WorldStateMsg.Begin(w, MsgId.WorldState, tick, 2); entry.Write(w); WorldStateMsg.End(w, slot, 1);
                var r = new NetworkReader(w.ToSegment()); r.ReadByte();
                type.GetMethod("OnWorldState", flags).Invoke(gateway, new[] { worker, r });
            }
            Send(10); entry.LocalPosition = Vector3.zero; Send(9);
            var cached = (EntitySpawnMsg)recordType.GetField("LastSpawn").GetValue(record);
            Assert.AreEqual(new Vector3(9, 2, 3), cached.LocalPosition);
            Assert.AreEqual(Vector3.forward * 7, cached.Velocity);
            cached.LocalScale = new Vector3(2, 3, 4);
            cached.LocalRotation = Quaternion.Euler(0, 90, 0);
            recordType.GetField("LastSpawn").SetValue(record, cached);
            recordType.GetField("Container").SetValue(record, ContainerRef.None);
            var world = (Vector3)type.GetMethod("WorldPosition", flags).Invoke(gateway, new object[] { ContainerRef.Dynamic(a.NetId), Vector3.forward, 0 });
            Assert.Less(Vector3.Distance(new Vector3(13, 2, 3), world), 0.001f, "gateway applies carrier scale as well as rotation");
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                type.GetField("<Config>k__BackingField", flags).SetValue(gateway, config);
                var clientType = type.GetNestedType("ClientConn", BindingFlags.NonPublic);
                var client = Activator.CreateInstance(clientType, true);
                clientType.GetField("Welcomed").SetValue(client, true);
                ((IDictionary)type.GetField("_clientsById", flags).GetValue(gateway)).Add(1u, client);
                Send(11); // (11 + netId % 12) % 12 != 0: a spectator would miss this update
                Assert.AreEqual(-1, clientType.GetField("PendingSlot").GetValue(client));
                entry.Fields |= TransformFields.Reliable;
                Send(13); // also outside the distance slot, but recovery must arrive anyway
                Assert.AreEqual((ushort)1, clientType.GetField("ReliableCount").GetValue(client));
            }
            finally { Object.DestroyImmediate(config); }
        }

        [Test] public void RegisteredPrefabsHaveTheirRequiredTransformAndValidMotionSettings()
        {
            var config = Resources.Load<NebulaConfig>("NebulaConfig");
            if (config == null) return;
            foreach (var prefab in config.NetworkPrefabs)
            {
                if (prefab == null) continue;
                Assert.AreEqual(0, UnityEditor.GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab), prefab.name);
                foreach (var id in prefab.GetComponentsInChildren<NetworkIdentity>(true))
                {
                    var issues = new List<Nebula.Editor.NebulaValidator.Issue>();
                    Nebula.Editor.NebulaValidator.CheckMotion(id, issues);
                    foreach (var issue in issues) Assert.AreNotEqual(Nebula.Editor.NebulaValidator.Severity.Error, issue.Severity, issue.Message);
                }
            }
        }
    }
}
