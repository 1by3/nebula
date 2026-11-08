using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The client half of the far relevance tier (NEB-388): <see cref="NebulaClient.FarEntities"/> fills from
    /// <see cref="MsgId.FarEntities"/>, updates, empties on a "gone", ignores entries for an entity it holds as a replica,
    /// and turns a marker into the replica when the entity's spawn arrives, with <see cref="FarEntityRemoval.Replicated"/>
    /// and no "left". Plus the entry's wire round trip at system distances.
    /// </summary>
    [Category("Conformance")]
    public sealed class FarEntityClientTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private NebulaClient _client;
        private IDictionary _entities;
        private readonly List<string> _events = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _events.Clear();
            ContainerRegistry.Rebuild();
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            var prefab = new GameObject("ship-prefab");
            _objects.Add(prefab);
            prefab.AddComponent<NetworkIdentity>();
            prefab.SetActive(false);
            NetworkPrefabs.Register(new List<GameObject> { prefab });
            var go = new GameObject("client");
            _objects.Add(go);
            _client = go.AddComponent<NebulaClient>();
            typeof(NebulaClient).GetProperty("Config").SetValue(_client, ScriptableObject.CreateInstance<NebulaConfig>());
            typeof(NebulaClient).GetProperty("ConnectionState").SetValue(_client, NebulaClient.State.InGame);
            _entities = (IDictionary)typeof(NebulaClient).GetField("_entities", Private).GetValue(_client);
            _client.FarEntityAdded += f => _events.Add("added " + f.NetId);
            _client.FarEntityUpdated += f => _events.Add("updated " + f.NetId);
            _client.FarEntityRemoved += (f, why) => _events.Add($"removed {f.NetId} {why}");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var e in _entities.Values) if (e is NetworkIdentity id && id != null) Object.DestroyImmediate(id.gameObject);
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            NetworkPrefabs.Register(new List<GameObject>());
            ContainerRegistry.Rebuild();
            NebulaRuntime.IsClient = false;
            NebulaRuntime.IsServer = true;
        }

        private static FarEntityEntry State(ulong netId, double x, double y, double z, uint epoch = 1) => new FarEntityEntry
        {
            NetId = netId, Epoch = epoch, Kind = FarEntryKind.State, PrefabId = 0, InterestGroup = 3, Radius = 100000f, UpdateRate = 1f,
            OwnerClientId = 99, X = x, Y = y, Z = z, Rotation = Quaternion.Euler(0f, 45f, 0f), Velocity = new Vector3(0f, 0f, 200f),
        };

        private void Receive(params FarEntityEntry[] entries)
        {
            var w = new NetworkWriter(256);
            FarEntitiesMsg.Write(w, entries, 0, entries.Length);
            var r = new NetworkReader(w.ToSegment());
            Assert.AreEqual(MsgId.FarEntities, (MsgId)r.ReadByte());
            typeof(NebulaClient).GetMethod("OnFarEntities", Private).Invoke(_client, new object[] { r });
        }

        private void Spawn(ulong netId) =>
            typeof(NebulaClient).GetMethod("OnEntitySpawn", Private).Invoke(_client, new object[]
            {
                new EntitySpawnMsg
                {
                    NetId = netId, PrefabId = 0, Epoch = 1, Container = ContainerRef.None, LocalPosition = Vector3.zero,
                    LocalRotation = Quaternion.identity, LocalScale = Vector3.one, Vars = System.Array.Empty<byte>(),
                    State = System.Array.Empty<byte>(), ViewSeq = 1,
                },
            });

        [Test]
        public void AFarEntityIsAddedUpdatedAndRemoved()
        {
            Receive(State(5, 50000.25, 12.5, -320000.125));
            Assert.IsTrue(_client.FarEntities.TryGetValue(5, out var far));
            Assert.AreEqual(50000.25, far.Position.X);
            Assert.AreEqual(-320000.125, far.Position.Z, "double on the wire: a third of a million metres out to the millimetre and better");
            Assert.AreEqual(99UL, far.OwnerClientId);
            Assert.AreEqual(3, far.InterestGroup);
            Assert.That(Quaternion.Angle(far.Rotation, Quaternion.Euler(0f, 45f, 0f)), Is.LessThan(0.2f));
            Assert.AreEqual(200f, far.Velocity.z, 0.5f);
            Assert.AreEqual(ContainerRegistry.ToFrame(far.Position, 0UL), far.WorldPosition);
            var ahead = far.PredictedWorldPosition(far.ReceivedAt + 1.0);
            Assert.AreEqual(far.WorldPosition.z + 200f, ahead.z, 1f, "extrapolated by its velocity between updates");

            Receive(State(5, 50100, 0, 0));
            Assert.AreEqual(50100.0, _client.FarEntities[5].Position.X);
            Receive(State(5, 1, 0, 0, epoch: 0));
            Assert.AreEqual(50100.0, _client.FarEntities[5].Position.X, "an older epoch is ignored");
            Receive(FarEntityEntry.GoneOf(5, 1));
            Assert.IsFalse(_client.FarEntities.ContainsKey(5));
            CollectionAssert.AreEqual(new[] { "added 5", "updated 5", "removed 5 Left" }, _events);
        }

        [Test]
        public void ASpawnTurnsTheMarkerIntoTheReplicaAndLaterFarEntriesAreIgnored()
        {
            Receive(State(6, 40000, 0, 0));
            Spawn(6);
            Assert.IsFalse(_client.FarEntities.ContainsKey(6), "the replica replaces the marker");
            Assert.IsTrue(_entities.Contains(6UL), "and the replica exists");
            Receive(State(6, 40001, 0, 0));
            Assert.IsFalse(_client.FarEntities.ContainsKey(6), "a far entry for a replica is ignored");
            CollectionAssert.AreEqual(new[] { "added 6", "removed 6 Replicated" }, _events);
        }

        private void ReceivePoses(params FarEntityEntry[] poses)
        {
            var w = new NetworkWriter(256);
            FarPosesMsg.Write(w, poses, 0, poses.Length);
            var r = new NetworkReader(w.ToSegment());
            Assert.AreEqual(MsgId.FarPoses, (MsgId)r.ReadByte());
            typeof(NebulaClient).GetMethod("OnFarPoses", Private).Invoke(_client, new object[] { r });
        }

        private void Despawn(ulong netId) =>
            typeof(NebulaClient).GetMethod("OnEntityDespawn", Private).Invoke(_client, new object[] { new EntityDespawnMsg { NetId = netId, Epoch = 1, ViewSeq = 1 } });

        private static FarEntityEntry Pose(ushort slot, double dx, double dy, double dz, bool upright = true) => new FarEntityEntry
        {
            Kind = FarEntryKind.Pose, Slot = slot, Upright = upright, X = dx, Y = dy, Z = dz,
            Rotation = Quaternion.Euler(0f, 90f, 0f), Velocity = new Vector3(1.5f, 0f, 0f),
        };

        [Test]
        public void CompactPosesMoveAMarkerFromItsAnchorBySlot()
        {
            var entry = State(9, 1000.5, 0, -2000.25);
            entry.Slot = 4;
            entry.FarClass = 3;
            entry.Rotation = Quaternion.Euler(0f, 0f, 30f); // standing on a tilted surface
            Receive(entry);
            var far = _client.FarEntities[9];
            Assert.AreEqual(3, far.FarClass);
            var upBefore = far.Rotation * Vector3.up;
            ReceivePoses(Pose(4, 12.34, -0.5, 7.0));
            Assert.AreEqual(1012.84, far.Position.X, 1e-6, "the anchor plus the offset");
            Assert.AreEqual(-1993.25, far.Position.Z, 1e-6);
            Assert.AreEqual(1.5f, far.Velocity.x);
            Assert.That(Vector3.Angle(far.Rotation * Vector3.up, upBefore), Is.LessThan(0.01f), "an upright body keeps the up axis of its last full entry");
            Assert.That(Vector3.Angle(far.Rotation * Vector3.forward, Vector3.ProjectOnPlane(Quaternion.Euler(0f, 90f, 0f) * Vector3.forward, upBefore)), Is.LessThan(1.5f), "and turns to face where the pose says");
            ReceivePoses(Pose(4, 20.0, 0, 0));
            Assert.AreEqual(1020.5, far.Position.X, 1e-6, "offsets are from the anchor, not from the last pose");
            ReceivePoses(Pose(5, 1, 1, 1));
            CollectionAssert.AreEqual(new[] { "added 9", "updated 9", "updated 9" }, _events, "a slot that names nothing is ignored");

            Spawn(9);
            ReceivePoses(Pose(4, 30.0, 0, 0));
            Assert.IsFalse(_client.FarEntities.ContainsKey(9), "a promoted marker's slot is let go");
        }

        [Test]
        public void AMarkerSentAheadOfTheDespawnTakesOverFromTheReplica()
        {
            Spawn(8);
            var replica = (NetworkIdentity)_entities[8UL];
            replica.transform.position = new Vector3(130f, 0f, 0f);
            bool markerAtDespawn = false;
            _client.EntityDespawned += e => { _events.Add("despawned " + e.NetId); markerAtDespawn = _client.FarEntities.ContainsKey(e.NetId); };

            var entry = State(8, 131.0, 0, 0);
            entry.FromReplica = true;
            entry.Slot = 2;
            entry.Velocity = new Vector3(1.5f, 0f, 0f);
            Receive(entry);
            Assert.IsFalse(_client.FarEntities.ContainsKey(8), "not while the replica is still here");
            Assert.IsEmpty(_events);

            Despawn(8);
            CollectionAssert.AreEqual(new[] { "added 8", "despawned 8" }, _events, "the marker is in before anyone hears the replica went");
            Assert.IsTrue(markerAtDespawn);
            var far = _client.FarEntities[8];
            Assert.IsTrue(far.FromReplica);
            Assert.AreEqual(130f, far.PredictedWorldPosition(far.HandoverAt).x, 0.01f, "drawn where the replica was");
            double later = far.HandoverAt + FarEntity.HandoverBlendSeconds;
            Assert.AreEqual(131f + 1.5f * (float)(later - far.ReceivedAt), far.PredictedWorldPosition(later).x, 0.01f, "and on its far pose once the blend is over");

            ReceivePoses(Pose(2, 1.0, 0, 0));
            Assert.AreEqual(132.0, far.Position.X, 1e-6, "its compact poses resolve by the slot the hand-over gave it");
        }

        [Test]
        public void TheMarkerGivesWayOnlyOnceTheReplicaIsThere()
        {
            Receive(State(10, 300, 0, 0));
            bool replicaThere = false;
            _client.FarEntityRemoved += (f, why) => replicaThere = _entities.Contains(f.NetId);
            Spawn(10);
            Assert.IsTrue(replicaThere, "a game handling Replicated finds the replica in Entities");

            var aged = State(11, 0, 0, 0);
            aged.Age = 0.5f;
            Receive(aged);
            var far = _client.FarEntities[11];
            Assert.AreEqual(Time.unscaledTimeAsDouble - 0.5, far.ReceivedAt, 0.05, "a stored pose is placed when it was taken, not when it arrived");
        }

        [Test]
        public void AFarEntryRoundTripsAndAGoneEntryIsShort()
        {
            var entry = State(7, 1.5e8, -2.25, 6.0e5);
            entry.InstanceId = 0xABCDEF;
            var w = new NetworkWriter(128);
            entry.Write(w);
            int stateBytes = w.Length;
            var back = FarEntityEntry.Read(new NetworkReader(w.ToSegment()));
            Assert.AreEqual(entry.X, back.X);
            Assert.AreEqual(entry.Y, back.Y);
            Assert.AreEqual(entry.Z, back.Z);
            Assert.AreEqual(0xABCDEFUL, back.InstanceId);
            Assert.AreEqual(99UL, back.OwnerClientId);
            Assert.AreEqual(FarEntityEntry.StateWireSize + 16, stateBytes, "owner and scope add 8 bytes each");
            w.Reset();
            FarEntityEntry.GoneOf(7, 2).Write(w);
            Assert.AreEqual(13, w.Length, "a gone is net id, epoch and kind");
        }
    }
}
