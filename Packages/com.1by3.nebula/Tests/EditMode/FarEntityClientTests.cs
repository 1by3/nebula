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
