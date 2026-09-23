using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// A carrier leaving a client's view must not take the replicas riding in it along. Riders are parented under
    /// the carrier, so destroying the carrier's object used to destroy theirs too while they stayed in the client's
    /// entity table: every later frame's <see cref="NetworkIdentity.RemoteTick"/> and the riders' own despawns then
    /// threw <c>NullReferenceException</c> on dead objects. Seen with fast ships whose passengers were other
    /// players.
    /// </summary>
    public sealed class ClientCarrierDespawnTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private NebulaClient _client;
        private IDictionary _entities;
        private Container _ground;

        [SetUp]
        public void SetUp()
        {
            ContainerRegistry.Rebuild();
            _ground = ContainerRegistry.RegisterRuntime(RuntimeGrid.PackId(Vector3Int.zero), new Bounds(new Vector3(32f, 32f, 32f), new Vector3(64f, 64f, 64f)));
            var go = new GameObject("client");
            _objects.Add(go);
            _client = go.AddComponent<NebulaClient>();
            _entities = (IDictionary)typeof(NebulaClient).GetField("_entities", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_client);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
        }

        private NetworkIdentity Replica(ulong netId, Container container, bool carrier)
        {
            var go = new GameObject((carrier ? "ship " : "rider ") + netId);
            _objects.Add(go);
            go.transform.position = new Vector3(30f, 10f, 30f);
            var entity = go.AddComponent<NetworkIdentity>();
            if (carrier)
            {
                var hull = go.AddComponent<Container>();
                hull.ContainerId = "hull" + netId;
                hull.Size = new Vector3(20f, 6f, 20f);
                hull.Center = new Vector3(0f, 2f, 0f);
                go.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            }
            entity.Initialize();
            entity.NetId = netId;
            entity.Epoch = 1;
            entity.SetContainer(container);
            entity.InvokeSpawn();
            _entities.Add(netId, entity);
            return entity;
        }

        private void Despawn(ulong netId) =>
            typeof(NebulaClient).GetMethod("OnEntityDespawn", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_client, new object[] { new EntityDespawnMsg { NetId = netId, Epoch = 1 } });

        [Test]
        public void ARiderSurvivesItsCarrierLeavingTheView()
        {
            var ship = Replica(1, _ground, carrier: true);
            Assume.That(ship.Carried, Is.Not.Null, "the ship registered its box");
            var rider = Replica(2, ship.Carried, carrier: false);
            Assume.That(rider.transform.IsChildOf(ship.transform), "riders are parented under their carrier");

            Despawn(1);

            Assert.IsTrue(rider != null, "the rider's object was not destroyed with the ship");
            Assert.AreSame(_ground, rider.Container, "the rider was put down where the ship was");
            Assert.IsTrue(ship == null, "the ship itself is gone");
            Assert.AreSame(rider, _client.Entities.Single(), "only the ship left the entity table");

            Despawn(2);
            Assert.AreEqual(0, _client.EntityCount, "the rider's own despawn still works");
        }

        [Test]
        public void AReplicaDestroyedWithoutADespawnIsDroppedInsteadOfThrowingEveryFrame()
        {
            var ship = Replica(1, _ground, carrier: false);
            Object.DestroyImmediate(ship.gameObject);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, $"{NebulaLog.Prefix} replica 1 was destroyed without a despawn; dropped from this client's entity table");

            typeof(NebulaClient).GetMethod("DropDestroyed", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_client, null);

            Assert.AreEqual(0, _client.EntityCount);
        }
    }
}
