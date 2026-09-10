using System.Collections.Generic;
using System.Linq;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public class WorldPartitionTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private WorldDefinition _world;

        private WorldDefinition MakeWorld(Vector3 cellSize, params Vector3Int[] cells)
        {
            var w = ScriptableObject.CreateInstance<WorldDefinition>();
            w.WorldName = "Test";
            w.CellSize = cellSize;
            foreach (var c in cells) w.SetCell(c, w.ScenePathFor(c));
            _objects.Add(w);
            return w;
        }

        private Container MakeContainer(string id, Vector3Int cell, Vector3 localPosition, Vector3 size, bool isCell, Transform parent)
        {
            var go = new GameObject(id);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            var c = go.AddComponent<Container>();
            c.ContainerId = id;
            c.Size = size;
            c.Center = isCell ? Vector3.zero : new Vector3(0, size.y * 0.5f, 0);
            c.Cell = cell;
            c.IsCell = isCell;
            _objects.Add(go);
            return c;
        }

        [SetUp]
        public void SetUp()
        {
            _world = MakeWorld(new Vector3(100, 100, 100), new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(0, 0, 1), new Vector3Int(5, 0, 5));
            WorldOrigin.Reset(_world);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
            WorldOrigin.Reset(null);
            ContainerRegistry.Rebuild();
        }

        // ---------------------------------------------------------------------------------------- grid maths

        [Test]
        public void CellsAreCentredOnTheirGridPosition()
        {
            Assert.AreEqual(new Vector3Int(0, 0, 0), _world.CoordOf(new Vector3(49.9f, 0, -49.9f), Vector3Int.zero));
            Assert.AreEqual(new Vector3Int(1, 0, 0), _world.CoordOf(new Vector3(50f, 0, 0), Vector3Int.zero));
            Assert.AreEqual(new Vector3Int(-1, 0, -1), _world.CoordOf(new Vector3(-50.1f, 0, -50.1f), Vector3Int.zero));
            Assert.AreEqual(new Vector3(100, 0, 0), _world.FrameOrigin(new Vector3Int(1, 0, 0), Vector3Int.zero));
            // With the origin moved to cell (5,0,5), that cell sits at Unity's origin and (0,0,0) is far away.
            Assert.AreEqual(new Vector3(-500, 0, -500), _world.FrameOrigin(Vector3Int.zero, new Vector3Int(5, 0, 5)));
            Assert.AreEqual(new Vector3Int(5, 0, 5), _world.CoordOf(Vector3.zero, new Vector3Int(5, 0, 5)));
        }

        [Test]
        public void SceneNamesRoundTripNegativeCoordinates()
        {
            var coord = new Vector3Int(3, -1, -12);
            string name = _world.SceneNameFor(coord);
            Assert.AreEqual("Test_3_n1_n12", name);
            Assert.IsTrue(WorldDefinition.TryParseSceneName(name, out var world, out var parsed));
            Assert.AreEqual("Test", world);
            Assert.AreEqual(coord, parsed);
            Assert.AreEqual("Test_3_n1_n12", WorldDefinition.SceneNameOf("Assets/Scenes/World/Test_3_n1_n12.unity"));
            Assert.IsFalse(WorldDefinition.TryParseSceneName("Corporation", out _, out _));
        }

        [Test]
        public void MortonOrderKeepsNeighboursTogether()
        {
            var a = WorldGrid.Morton(new Vector3Int(0, 0, 0));
            var b = WorldGrid.Morton(new Vector3Int(1, 0, 0));
            var far = WorldGrid.Morton(new Vector3Int(16, 0, 0));
            Assert.Less(a, b);
            Assert.Less(b, far);
            // Distinct coordinates get distinct keys, negatives included.
            var keys = new HashSet<ulong>();
            for (int x = -3; x <= 3; x++) for (int y = -3; y <= 3; y++) for (int z = -3; z <= 3; z++)
                Assert.IsTrue(keys.Add(WorldGrid.Morton(new Vector3Int(x, y, z))));
            // A 2x2x2 block sorts into one contiguous run of the 4x4x4 block containing it.
            var block = new List<Vector3Int>();
            WorldGrid.Neighborhood(new Vector3Int(1, 1, 1), 1, block);
            var sorted = block.OrderBy(WorldGrid.Morton).ToList();
            Assert.AreEqual(27, sorted.Count);
            Assert.AreEqual(1, WorldGrid.Rings(sorted[0], sorted[1]));
        }

        [Test]
        public void ShiftDeltaMovesEverythingTowardsTheNewOrigin()
        {
            var delta = WorldOrigin.ShiftDelta(_world, Vector3Int.zero, new Vector3Int(2, 0, -1));
            Assert.AreEqual(new Vector3(-200, 0, 100), delta);
        }

        // ---------------------------------------------------------------------------------------- streaming policy

        [Test]
        public void WantedSetIsTheAnchorCubesPlusRequiredLimitedToDefinedCells()
        {
            var wanted = new HashSet<Vector3Int>();
            var anchors = new List<(Vector3Int, int)> { (new Vector3Int(0, 0, 0), 1) };
            WorldStreamer.ComputeWanted(_world, anchors, new[] { new Vector3Int(5, 0, 5), new Vector3Int(9, 9, 9) }, wanted);
            CollectionAssert.AreEquivalent(new[] { new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(0, 0, 1), new Vector3Int(5, 0, 5) }, wanted);
            WorldStreamer.ComputeWanted(_world, new List<(Vector3Int, int)>(), null, wanted);
            Assert.AreEqual(0, wanted.Count);
        }

        [Test]
        public void WorkerCentroidIsTheRoundedAverageCell()
        {
            Assert.AreEqual(new Vector3Int(1, 0, 0), NebulaWorldStreaming.Centroid(new[] { new Vector3Int(0, 0, 0), new Vector3Int(2, 0, 0) }));
            Assert.AreEqual(Vector3Int.zero, NebulaWorldStreaming.Centroid(new Vector3Int[0]));
        }

        // ---------------------------------------------------------------------------------------- manifest-ordered registry

        private List<Container> BuildManifestContainers()
        {
            var root = new GameObject("world").transform;
            _objects.Add(root.gameObject);
            var cells = new Dictionary<Vector3Int, Container>();
            foreach (var coord in new[] { new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(0, 0, 1), new Vector3Int(5, 0, 5) })
            {
                var go = new GameObject(WorldContainerManifest.CellContainerId(coord));
                go.transform.SetParent(root, false);
                go.transform.position = WorldOrigin.ToFrame(coord, Vector3.zero);
                var c = go.AddComponent<Container>();
                c.ContainerId = go.name;
                c.Size = _world.CellSize;
                c.Center = Vector3.zero;
                c.Cell = coord;
                c.IsCell = true;
                cells[coord] = c;
            }
            var entries = new List<WorldContainerManifest.Entry>();
            foreach (var kv in cells) entries.Add(new WorldContainerManifest.Entry { Id = kv.Value.ContainerId, Cell = kv.Key, IsCell = true, Size = kv.Value.Size });
            // A building inside cell (1,0,0), 20 m from its centre.
            var building = MakeContainer("garage", new Vector3Int(1, 0, 0), new Vector3(20, -50, 0), new Vector3(10, 8, 10), false, cells[new Vector3Int(1, 0, 0)].transform);
            entries.Add(new WorldContainerManifest.Entry { Id = "garage", Cell = new Vector3Int(1, 0, 0), Size = building.Size });
            entries.Sort(WorldContainerManifest.Compare);
            var ordered = new List<Container>();
            foreach (var e in entries) ordered.Add(e.IsCell ? cells[e.Cell] : building);
            return ordered;
        }

        [Test]
        public void ManifestOrderIsTheWireIndexAndCellsPrecedeTheirNestedContainers()
        {
            var ordered = BuildManifestContainers();
            ContainerRegistry.Load(ordered, gridded: true);
            Assert.IsTrue(ContainerRegistry.IsGridded);
            Assert.AreEqual(5, ContainerRegistry.Count);
            for (int i = 0; i < ordered.Count; i++) Assert.AreSame(ordered[i], ContainerRegistry.Get((ushort)i));
            int cellIndex = ContainerRegistry.FindById("cell_1_0_0").Index;
            int garageIndex = ContainerRegistry.FindById("garage").Index;
            Assert.AreEqual(cellIndex + 1, garageIndex);
            Assert.AreEqual(0, ContainerRegistry.FindById("cell_0_0_0").Index);
            // Morton: (0,0,0) < (1,0,0) < (0,0,1) < (5,0,5).
            Assert.Less(ContainerRegistry.FindById("cell_1_0_0").Index, ContainerRegistry.FindById("cell_0_0_1").Index);
            Assert.AreEqual(4, ContainerRegistry.FindById("cell_5_0_5").Index);
        }

        [Test]
        public void GridLookupFindsCellsAndNestedBoxesAndAdjacencyOnlyCrossesNeighbouringCells()
        {
            ContainerRegistry.Load(BuildManifestContainers(), gridded: true);
            Assert.AreEqual("cell_0_0_0", ContainerRegistry.Find(new Vector3(10, 0, 10)).ContainerId);
            Assert.AreEqual("cell_1_0_0", ContainerRegistry.Find(new Vector3(90, 0, 0)).ContainerId);
            Assert.AreEqual("garage", ContainerRegistry.Find(new Vector3(120, -48, 0)).ContainerId);
            Assert.AreEqual("cell_5_0_5", ContainerRegistry.Find(new Vector3(500, 0, 500)).ContainerId);
            // Outside every defined cell: nearest wins, as in the linear registry.
            Assert.AreEqual("cell_0_0_1", ContainerRegistry.Find(new Vector3(0, 0, 160)).ContainerId);
            var origin = ContainerRegistry.FindById("cell_0_0_0");
            CollectionAssert.AreEquivalent(new[] { "cell_1_0_0", "cell_0_0_1" }, origin.Neighbors.Select(n => n.ContainerId));
            var east = ContainerRegistry.FindById("cell_1_0_0");
            CollectionAssert.Contains(east.Neighbors.Select(n => n.ContainerId).ToList(), "garage");
            Assert.AreEqual(0, ContainerRegistry.FindById("cell_5_0_5").Neighbors.Count);
            CollectionAssert.AreEquivalent(new[] { "cell_1_0_0", "garage" }, ContainerRegistry.InCell(new Vector3Int(1, 0, 0)).Select(c => c.ContainerId));
        }

        [Test]
        public void AssignmentKeepsManifestOrderSoWorkersGetContiguousRuns()
        {
            var ids = new List<string> { "cell_0_0_0", "cell_1_0_0", "cell_0_0_1", "cell_1_0_1", "z-first", "a-last" };
            var workers = new List<WorkerInfo>
            {
                new WorkerInfo { WorkerId = "w1", WorkerIndex = 1 },
                new WorkerInfo { WorkerId = "w2", WorkerIndex = 2 },
            };
            var kept = NebulaOrchestrator.ComputeAssignment(ids, workers, new List<LeaseInfo>(), keepOrder: true).ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.AreEqual("w1", kept["cell_0_0_0"]);
            Assert.AreEqual("w1", kept["cell_1_0_0"]);
            Assert.AreEqual("w1", kept["cell_0_0_1"]);
            Assert.AreEqual("w2", kept["cell_1_0_1"]);
            Assert.AreEqual("w2", kept["z-first"]);
            Assert.AreEqual("w2", kept["a-last"]);
            var sorted = NebulaOrchestrator.ComputeAssignment(ids, workers, new List<LeaseInfo>()).ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.AreEqual("w1", sorted["a-last"]);
            Assert.AreEqual("w2", sorted["z-first"]);
        }

        [Test]
        public void OriginShiftMovesInterpolatorAndPoseHistory()
        {
            var go = new GameObject("remote");
            _objects.Add(go);
            var interp = go.AddComponent<RemoteInterpolator>();
            interp.Push(10, new Vector3(1, 2, 3), Quaternion.identity, Vector3.zero);
            interp.Push(11, new Vector3(2, 2, 3), Quaternion.identity, Vector3.zero);
            interp.Shift(new Vector3(-100, 0, 0));
            Assert.AreEqual(new Vector3(-98, 2, 3), interp.LatestPosition);
            Assert.IsTrue(interp.Sample(10.5, out var pos, out _));
            Assert.AreEqual(new Vector3(-98.5f, 2, 3), pos);
        }
    }
}
