using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="ContainerRegistry.Resolve"/> skips its search for an entity still alone in its static box (NEB-379):
    /// the answer must match the full search everywhere, with hysteresis, nested boxes, carriers and an owner change,
    /// and a stationary or settled entity must stop costing a search.
    /// </summary>
    public class ResolveShortcutTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        private Container MakeStatic(string id, Vector3 position, Vector3 size, string owner = "")
        {
            var go = new GameObject(id);
            go.transform.position = position;
            var c = go.AddComponent<Container>();
            c.ContainerId = id;
            c.Size = size;
            c.Center = new Vector3(0, size.y * 0.5f, 0);
            c.OwnerWorkerId = owner;
            _objects.Add(go);
            return c;
        }

        private NetworkIdentity MakeCarrier(string name, ulong netId, Vector3 position, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = name;
            box.Size = size;
            box.Center = new Vector3(0, size.y * 0.5f, 0);
            go.AddComponent<NetworkTransform>();
            identity.Initialize();
            identity.NetId = netId;
            identity.SetContainer(ContainerRegistry.Find(position, box));
            identity.InvokeSpawn();
            return identity;
        }

        [SetUp]
        public void SetUp()
        {
            // Two adjacent areas, a hut inside the first.
            MakeStatic("west", new Vector3(-100, 0, 0), new Vector3(200, 60, 200), "w1");
            MakeStatic("east", new Vector3(100, 0, 0), new Vector3(200, 60, 200), "w2");
            MakeStatic("hut", new Vector3(-150, 0, 0), new Vector3(10, 5, 10), "w1");
            ContainerRegistry.Rebuild();
            ContainerRegistry.DisableFastResolve = false;
        }

        [TearDown]
        public void TearDown()
        {
            ContainerRegistry.DisableFastResolve = false;
            foreach (var go in _objects) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
        }

        private static Container Reference(Vector3 p, Container current, float h, NetworkIdentity subject = null)
        {
            ContainerRegistry.DisableFastResolve = true;
            try { return ContainerRegistry.Resolve(p, current, h, subject); }
            finally { ContainerRegistry.DisableFastResolve = false; }
        }

        [Test]
        public void WalkingAcrossBoundariesAndIntoANestedBoxGivesTheSameAnswerAsTheFullSearch()
        {
            var west = ContainerRegistry.FindById("west");
            var east = ContainerRegistry.FindById("east");
            var hut = ContainerRegistry.FindById("hut");
            foreach (var start in new[] { west, east, hut })
            {
                foreach (float h in new[] { 0f, 0.35f, 2f })
                {
                    var fast = start; var slow = start;
                    for (float x = -170f; x <= 170f; x += 0.25f)
                    {
                        var p = new Vector3(x, 1f, 0.5f);
                        fast = ContainerRegistry.Resolve(p, fast, h);
                        slow = Reference(p, slow, h);
                        Assert.AreSame(slow, fast, $"x={x} h={h} from {start.ContainerId}");
                    }
                    for (float x = 170f; x >= -170f; x -= 0.25f)
                    {
                        var p = new Vector3(x, 1f, 0.5f);
                        fast = ContainerRegistry.Resolve(p, fast, h);
                        slow = Reference(p, slow, h);
                        Assert.AreSame(slow, fast, $"back x={x} h={h} from {start.ContainerId}");
                    }
                }
            }
        }

        [Test]
        public void ACarrierAndAnEntityEnteringAndLeavingItMatchTheFullSearch()
        {
            var west = ContainerRegistry.FindById("west");
            var ship = MakeCarrier("ship", 42, new Vector3(-60, 0, 0), new Vector3(10, 6, 20));
            var box = ship.Carried;
            var current = west;
            var slow = west;
            for (float x = -80f; x <= -40f; x += 0.1f)
            {
                var p = new Vector3(x, 1f, 0f);
                current = ContainerRegistry.Resolve(p, current, 0.35f);
                slow = Reference(p, slow, 0.35f);
                Assert.AreSame(slow, current, $"x={x}");
            }
            // Inside the carried box the shortcut must not hold the entity in the box around it.
            Assert.AreSame(west, ContainerRegistry.Resolve(new Vector3(-60, 1, 0), west, 0.35f, ship), "the carrier itself never enters its own box");
            Assert.AreSame(Reference(new Vector3(-60, 1, 0), west, 0.35f, ship), ContainerRegistry.Resolve(new Vector3(-60, 1, 0), west, 0.35f, ship));
            Assert.AreSame(box, ContainerRegistry.Resolve(new Vector3(-60, 1, 0), west, 0.35f));
            // The ship moves under a stationary entity: it is taken in, then let go when the ship leaves.
            ship.transform.position = new Vector3(60, 0, 0);
            ContainerRegistry.RefreshCaches();
            Assert.AreSame(west, ContainerRegistry.Resolve(new Vector3(-60, 1, 0), box, 0.35f));
        }

        [Test]
        public void AnOwnerChangeDoesNotMoveTheContainerTheShortcutReturns()
        {
            var west = ContainerRegistry.FindById("west");
            var p = new Vector3(-100, 1, 0);
            Assert.AreSame(west, ContainerRegistry.Resolve(p, west, 0.35f));
            west.OwnerWorkerId = "w9"; // the worker reads the owner off the returned container after Resolve
            Assert.AreSame(west, ContainerRegistry.Resolve(p, west, 0.35f));
            Assert.AreEqual("w9", ContainerRegistry.Resolve(p, west, 0.35f).OwnerWorkerId);
        }

        [Test]
        public void AStationaryEntityInItsBoxIsNotSearchedAndAnEntityNearAnotherBoxIs()
        {
            var west = ContainerRegistry.FindById("west");
            ContainerRegistry.FastResolves = ContainerRegistry.FullResolves = 0;
            for (int i = 0; i < 100; i++) ContainerRegistry.Resolve(new Vector3(-100, 1, 0), west, 0.35f);
            Assert.AreEqual(100, ContainerRegistry.FastResolves);
            Assert.AreEqual(0, ContainerRegistry.FullResolves);
            // Inside the hut, the search must run and find it.
            ContainerRegistry.FastResolves = ContainerRegistry.FullResolves = 0;
            Assert.AreSame(ContainerRegistry.FindById("hut"), ContainerRegistry.Resolve(new Vector3(-150, 1, 0), west, 0.35f));
            Assert.AreEqual(0, ContainerRegistry.FastResolves);
            Assert.AreEqual(1, ContainerRegistry.FullResolves);
        }
    }
}
