using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="ContainerRegistry.NeighborsOf"/> stopped scanning every carried container for every query when
    /// the ghost band's cost became a problem at scale (design §6). These tests pin the result against the
    /// reference answer — the exhaustive scan the broad phase replaced — on random layouts, above and below the
    /// size at which the hash kicks in.
    /// </summary>
    public sealed class NeighborsOfTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.ResetForNewSession();
            ContainerRegistry.RuntimeBucketSize = 256f;
            ContainerRegistry.Rebuild();
        }

        private Container MakeStatic(string id, Vector3 position, Vector3 size)
        {
            var go = new GameObject(id);
            go.transform.position = position;
            var c = go.AddComponent<Container>();
            c.ContainerId = id;
            c.Size = size;
            c.Center = Vector3.zero;
            _objects.Add(go);
            return c;
        }

        private Container MakeCarried(string name, ulong netId, Vector3 position, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = name;
            box.Size = size;
            box.Center = Vector3.zero;
            go.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            identity.Initialize();
            identity.NetId = netId;
            identity.InvokeSpawn(); // NetworkIdentity registers the box it carries
            return box;
        }

        /// <summary>The answer the exhaustive scan gives: every carried box whose expanded bounds overlap.</summary>
        private static List<Container> Reference(Container container)
        {
            var bounds = container.WorldBounds;
            bounds.Expand(0.05f);
            var expected = new List<Container>();
            foreach (var d in ContainerRegistry.Dynamic)
            {
                if (d == container || d.InstanceId != container.InstanceId) continue;
                if (bounds.Intersects(d.WorldBounds)) expected.Add(d);
            }
            return expected;
        }

        private static List<Container> CarriedNeighbors(Container container)
        {
            var found = new List<Container>();
            ContainerRegistry.NeighborsOf(container, found);
            var carried = new List<Container>();
            foreach (var c in found) if (c.IsDynamic) carried.Add(c);
            return carried;
        }

        [TestCase(8, 20250920)]
        [TestCase(64, 7)]
        [TestCase(200, 4242)]
        public void TheBroadPhaseFindsExactlyWhatAFullScanWould(int carriers, int seed)
        {
            var random = new System.Random(seed);
            var statics = new List<Container>();
            for (int i = 0; i < 6; i++)
            {
                statics.Add(MakeStatic($"zone{i}", new Vector3(i * 300f, 0, 0), new Vector3(200, 60, 200)));
            }
            ContainerRegistry.Load(statics, gridded: false);

            for (int i = 0; i < carriers; i++)
            {
                var p = new Vector3(
                    (float)(random.NextDouble() * 2000 - 200),
                    (float)(random.NextDouble() * 40 - 20),
                    (float)(random.NextDouble() * 400 - 200));
                MakeCarried($"ship{i}", (ulong)(i + 1), p, new Vector3(10 + i % 7, 6, 20 + i % 5));
            }
            ContainerRegistry.RefreshCaches();

            for (int i = 0; i < statics.Count; i++)
                CollectionAssert.AreEquivalent(Reference(statics[i]), CarriedNeighbors(statics[i]), $"static container {i}");
            foreach (var d in new List<Container>(ContainerRegistry.Dynamic))
                CollectionAssert.AreEquivalent(Reference(d), CarriedNeighbors(d), $"carried container {d.ContainerId}");
        }

        [Test]
        public void ACarrierRegisteredAfterTheLastRefreshIsStillFound()
        {
            var zone = MakeStatic("zone", Vector3.zero, new Vector3(200, 60, 200));
            ContainerRegistry.Load(new List<Container> { zone }, gridded: false);
            for (int i = 0; i < 40; i++) MakeCarried($"filler{i}", (ulong)(100 + i), new Vector3(5000 + i * 50, 0, 0), Vector3.one * 4);
            ContainerRegistry.RefreshCaches();
            CollectionAssert.IsEmpty(CarriedNeighbors(zone));

            // Registration marks the broad phase dirty, so the next query sees the newcomer without a refresh.
            var late = MakeCarried("late", 999, new Vector3(10, 0, 10), new Vector3(10, 6, 10));
            CollectionAssert.Contains(CarriedNeighbors(zone), late);
        }

        /// <summary>
        /// A carried box as big as a planet (412 km, a planet's frame) spans tens of millions of hash buckets. Its
        /// neighbours used to be found by looking up every one of them, about 600 ms per call, which the ghost band
        /// pays per entity standing directly in the box every tick: a pilot who flew up out of the ground's chunks
        /// into the planet's frame stalled the worker until the control plane declared it dead. The query now visits
        /// only the buckets that hold something, and a box too big to hash is a candidate of every query.
        /// </summary>
        [TestCase(1)]
        [TestCase(40)] // above the size at which carried boxes are hashed
        public void APlanetSizedCarriedBoxFindsItsNeighboursWithoutVisitingEveryBucket(int ships)
        {
            ContainerRegistry.Rebuild();
            ContainerRegistry.RuntimeBucketSize = 1024f; // a 256 m chunk grid's
            var chunk = ContainerRegistry.RegisterRuntime(42UL, new Bounds(new Vector3(0f, 205000f, 0f), new Vector3(256f, 2400f, 256f)));
            var planet = MakeCarried("planet", 1, Vector3.zero, new Vector3(412000f, 412000f, 412000f));
            for (int i = 1; i < ships; i++) MakeCarried($"ship{i}", (ulong)(100 + i), new Vector3(i * 5000f, 300000f, 0f), new Vector3(20, 8, 40));
            var ship = MakeCarried("near", 99, new Vector3(0f, 206000f, 0f), new Vector3(20, 8, 40));
            ContainerRegistry.RefreshCaches();

            var found = new List<Container>();
            ContainerRegistry.NeighborsOf(planet, found); // warm up
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) ContainerRegistry.NeighborsOf(planet, found);
            watch.Stop();

            CollectionAssert.Contains(found, chunk, "the chunk inside the planet's box");
            CollectionAssert.Contains(found, ship, "the ship inside the planet's box");
            Assert.Less(watch.Elapsed.TotalMilliseconds / 10.0, 5.0, "one query over a planet-sized box");
            // The other way round, a small box still finds the planet it is inside.
            CollectionAssert.Contains(CarriedNeighbors(ship), planet);
        }
    }
}
