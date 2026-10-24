using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// An entity whose object is destroyed without a despawn (it went down with a destroyed parent) must not leave a
    /// zombie in a worker's entity table. Before, the worker's ghost loop called <see cref="NetworkIdentity.RemoteTick"/>
    /// on it, whose error handler read <c>name</c> from the destroyed object and threw out of the whole tick, before the
    /// purge that would have dropped it: every tick threw again (about 45,000 exceptions and a 42 MB log in one mesh run).
    /// </summary>
    public sealed class DestroyedGhostTests
    {
        private ConformanceMesh _mesh;
        private ushort _shipPrefab, _pawnPrefab;
        private Container _ground;

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(1);
            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var box = ship.AddComponent<Container>();
            box.ContainerId = "hull";
            box.Size = new Vector3(20f, 6f, 20f);
            box.Center = new Vector3(0f, 2f, 0f);
            ship.AddComponent<NetworkTransform>();
            _shipPrefab = _mesh.RegisterPrefab(ship);
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            pawn.AddComponent<NetworkTransform>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);
            _ground = _mesh.AddStaticContainer("ground", Vector3.zero, new Vector3(200f, 50f, 200f));
            _mesh.SetOwner(_ground, W);
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            ContainerRegistry.Rebuild();
        }

        [Test]
        public void AGhostDestroyedWithoutADespawnIsDroppedOnceAndTheTickKeepsRunning()
        {
            var ship = W.SpawnServerDriven(_shipPrefab, _ground, new Vector3(10f, 1f, 10f), Quaternion.identity);
            var ghost = W.SpawnServerDriven(_pawnPrefab, ship.Carried, new Vector3(10f, 2f, 10f), Quaternion.identity);
            ghost.HasAuthority = false; // a copy of an entity another worker owns
            ulong ghostId = ghost.NetId;
            Assume.That(ghost.transform.IsChildOf(ship.transform), "a rider is parented under its carrier");

            Object.DestroyImmediate(ghost.gameObject); // its object goes (with a parent, in the field) without a despawn
            Assume.That(ghost == null, "the ghost's object is destroyed");
            int dropped = 0, errors = 0;
            Application.LogCallback count = (text, _, type) =>
            {
                if (text.Contains("destroyed without a despawn")) dropped++;
                if (type == LogType.Error || type == LogType.Exception) errors++;
            };
            Application.logMessageReceived += count;
            LogAssert.ignoreFailingMessages = true; // the tick may log unrelated warnings; the counts below are the assertion

            try
            {
                Assert.DoesNotThrow(() => W.Tick(10));
                Assert.AreEqual(1, W.Instance.EntityCount, "only the ship is left");
                Assert.IsNull(W.Instance.Find(ghostId));

                Assert.DoesNotThrow(() => W.Tick(11));
                Assert.DoesNotThrow(() => W.Tick(12));
                Assert.AreEqual(1, dropped, "the zombie is reported once, not on every tick");
                Assert.AreEqual(0, errors);
            }
            finally
            {
                Application.logMessageReceived -= count;
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void ADeeperParentedEntityIsNotDestroyedWithItsCarrier()
        {
            var ship = W.SpawnServerDriven(_shipPrefab, _ground, new Vector3(10f, 1f, 10f), Quaternion.identity);
            var bystander = W.SpawnServerDriven(_pawnPrefab, _ground, new Vector3(12f, 1f, 12f), Quaternion.identity);
            // Parented under the carrier's object without being filed in its box, so nothing evacuates it.
            bystander.transform.SetParent(ship.transform, true);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"was still parented under"));

            W.Act(() => W.Instance.Despawn(ship));

            Assert.IsTrue(bystander != null, "the entity was lifted out before the carrier's object was destroyed");
            Assert.IsNull(bystander.transform.parent);
            Assert.AreSame(bystander, W.Instance.Find(bystander.NetId));
        }
    }
}
