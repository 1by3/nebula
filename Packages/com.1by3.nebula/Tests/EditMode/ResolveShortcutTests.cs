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
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        /// <summary>A planet: a carried box with a physics frame of its own, and four runtime chunks of ground fixed in its frame.</summary>
        private NetworkIdentity MakePlanet(out List<Container> chunks)
        {
            PhysicsFrames.SceneFactory = () => UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(s);
            var go = new GameObject("planet");
            go.transform.position = new Vector3(0, 0, 30000f);
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = new Vector3(8000f, 1000f, 8000f);
            box.Center = new Vector3(0f, 100f, 0f);
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            go.AddComponent<NetworkTransform>();
            identity.Initialize();
            identity.NetId = 7;
            identity.SetContainer(ContainerRegistry.Find(go.transform.position, box));
            identity.InvokeSpawn();
            Assume.That(identity.Carried, Is.SameAs(box));
            Assume.That(box.Frame, Is.Not.Null, "the planet's frame");
            chunks = new List<Container>();
            for (int i = 0; i < 4; i++)
            {
                var chunk = ContainerRegistry.RegisterRuntime(2000UL + (ulong)i,
                    ContainerPlacement.Child(box.ContainerId, new Vector3(-3000f + 2000f * i, 100f, 0f), new Vector3(2000f, 1000f, 8000f), ContainerAuthority.Leased));
                Assert.IsNotNull(chunk);
                Assume.That(chunk.FixedInFrame, Is.True);
                chunks.Add(chunk);
            }
            return identity;
        }

        [Test]
        public void WalkingAcrossChunksFixedInAPlanetsFrameMatchesTheFullSearchAndTakesTheShortcut()
        {
            MakePlanet(out var chunks);
            Assume.That(chunks[1].Neighbors.Contains(chunks[0]) && chunks[1].Neighbors.Contains(chunks[2]), Is.True, "chunks of one frame are linked");
            foreach (float h in new[] { 0f, 0.35f, 2f })
            {
                var fast = chunks[0]; var slow = chunks[0];
                ContainerRegistry.ResetResolveCounters();
                int searched = 0; // full searches the shortcut path took (the reference always searches)
                // In the frame's own coordinates: across all four chunks and back.
                for (float x = -3900f; x <= 2900f; x += 4f)
                {
                    var p = new Vector3(x, 1f, 0.5f);
                    int before = ContainerRegistry.FullResolves;
                    fast = ContainerRegistry.Resolve(p, fast, h);
                    searched += ContainerRegistry.FullResolves - before;
                    slow = Reference(p, slow, h);
                    Assert.AreSame(slow, fast, $"x={x} h={h}");
                }
                for (float x = 2900f; x >= -3900f; x -= 4f)
                {
                    var p = new Vector3(x, 1f, 0.5f);
                    int before = ContainerRegistry.FullResolves;
                    fast = ContainerRegistry.Resolve(p, fast, h);
                    searched += ContainerRegistry.FullResolves - before;
                    slow = Reference(p, slow, h);
                    Assert.AreSame(slow, fast, $"back x={x} h={h}");
                }
                Assert.Greater(ContainerRegistry.FastResolves, searched * 10, $"h={h}: most steps are well inside a chunk and skip the search");
            }
        }

        [Test]
        public void OverlappingChunksOfAPlanetResolveByTheirClaimAndStillTakeTheShortcut()
        {
            // A curved grid's cells have boxes that overlap their neighbours' (D22): each claims the half of the overlap nearer its centre.
            MakePlanet(out var chunks);
            for (int i = 0; i < 4; i++)
                ContainerRegistry.RegisterRuntime(2000UL + (ulong)i,
                    ContainerPlacement.Child("planet", new Vector3(-3000f + 2000f * i, 100f, 0f), new Vector3(2600f, 1000f, 8000f), ContainerAuthority.Leased));
            ContainerRegistry.RuntimeClaims = (c, p) =>
            {
                float best = float.MaxValue; Container owner = null;
                foreach (var k in chunks) { float d = Mathf.Abs(p.x - k.transform.localPosition.x); if (d < best) { best = d; owner = k; } }
                return owner == c;
            };
            try
            {
                foreach (float h in new[] { 0f, 0.35f })
                {
                    var fast = chunks[0]; var slow = chunks[0];
                    ContainerRegistry.ResetResolveCounters();
                    int searched = 0;
                    for (float x = -3900f; x <= 2900f; x += 4f)
                    {
                        var p = new Vector3(x, 1f, 0.5f);
                        int before = ContainerRegistry.FullResolves;
                        fast = ContainerRegistry.Resolve(p, fast, h);
                        searched += ContainerRegistry.FullResolves - before;
                        slow = Reference(p, slow, h);
                        Assert.AreSame(slow, fast, $"x={x} h={h}");
                    }
                    Assert.Greater(ContainerRegistry.FastResolves, searched * 10, $"h={h}: a point in the overlap the current cell claims skips the search");
                }
            }
            finally { ContainerRegistry.RuntimeClaims = null; }
        }

        [Test]
        public void AStationaryEntityInAPlanetChunkIsNotSearched()
        {
            MakePlanet(out var chunks);
            ContainerRegistry.ResetResolveCounters();
            for (int i = 0; i < 100; i++) Assert.AreSame(chunks[1], ContainerRegistry.Resolve(new Vector3(-1000f, 1f, 0f), chunks[1], 0.35f));
            Assert.AreEqual(100, ContainerRegistry.FastResolves);
            Assert.AreEqual(0, ContainerRegistry.FullResolves);
            // Leaving the frame through its top takes the search, whatever chunk the entity is over.
            ContainerRegistry.ResetResolveCounters();
            ContainerRegistry.Resolve(new Vector3(-1000f, 650f, 0f), chunks[1], 0.35f);
            Assert.AreEqual(1, ContainerRegistry.FullResolves);
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
