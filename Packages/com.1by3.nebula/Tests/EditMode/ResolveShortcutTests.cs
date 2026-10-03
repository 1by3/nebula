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

        [Test]
        public void ARotatedCarriedBoxThatReachesPastItsUnturnedBoundsStillTakesTheEntity()
        {
            // The box is 10 wide and 20 long; turned 90 degrees it is 20 wide along x, which its unturned world bounds
            // (10 along x) do not cover. A point 8 m from its centre along x is inside the real box.
            var west = ContainerRegistry.FindById("west");
            var ship = MakeCarrier("ship", 42, new Vector3(-60, 0, 0), new Vector3(10, 6, 20));
            ship.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            ContainerRegistry.RefreshCaches();
            var p = new Vector3(-52f, 1f, 0f);
            Assume.That(ship.Carried.Contains(p), Is.True);
            Assume.That(ship.Carried.WorldBounds.Contains(p), Is.False, "outside the unturned bounds");
            Assert.AreSame(Reference(p, west, 0.35f), ContainerRegistry.Resolve(p, west, 0.35f));
            Assert.AreSame(ship.Carried, ContainerRegistry.Resolve(p, west, 0.35f), "boarded through the part of the box past its bounds");
        }

        private Container[,] MakeChunkGrid(Vector3 origin, float size)
        {
            var grid = new Container[3, 3];
            for (int x = 0; x < 3; x++)
                for (int z = 0; z < 3; z++)
                    grid[x, z] = MakeStatic($"chunk{x}{z}", origin + new Vector3((x - 1) * size, 0, (z - 1) * size), new Vector3(size, 400, size));
            ContainerRegistry.Rebuild();
            return grid;
        }

        [Test]
        public void AnEntityWellInsideAGridChunkWithEightNeighboursTakesTheFastPath()
        {
            var grid = MakeChunkGrid(new Vector3(0, 0, 1000), 256f);
            var centre = ContainerRegistry.FindById("chunk11");
            Assume.That(centre.Neighbors.Count, Is.EqualTo(8));
            ContainerRegistry.FastResolves = ContainerRegistry.FullResolves = 0;
            Assert.AreSame(centre, ContainerRegistry.Resolve(new Vector3(40, 1, 1000 - 30), centre, 0.35f));
            Assert.AreEqual(1, ContainerRegistry.FastResolves, "an axis-aligned neighbour does not hold a point in the next chunk");
            Assert.AreEqual(0, ContainerRegistry.FullResolves);
        }

        [Test]
        public void AHundredStationaryEntitiesOverA3x3GridDoOnlyFastResolves()
        {
            MakeChunkGrid(new Vector3(0, 0, 1000), 256f);
            ContainerRegistry.FastResolves = ContainerRegistry.FullResolves = 0;
            int n = 0;
            for (int x = 0; x < 3; x++)
                for (int z = 0; z < 3; z++)
                {
                    var chunk = ContainerRegistry.FindById($"chunk{x}{z}");
                    for (int i = 0; i < 12 && n < 100; i++, n++)
                    {
                        // Inside the chunk, clear of its edges and of the hysteresis band.
                        var p = chunk.transform.position + new Vector3(-100 + 17 * i, 1f, 90 - 15 * i);
                        Assert.AreSame(chunk, ContainerRegistry.Resolve(p, chunk, 0.35f));
                    }
                }
            Assert.AreEqual(100, n);
            Assert.AreEqual(100, ContainerRegistry.FastResolves);
            Assert.AreEqual(0, ContainerRegistry.FullResolves);
        }
    }
}
