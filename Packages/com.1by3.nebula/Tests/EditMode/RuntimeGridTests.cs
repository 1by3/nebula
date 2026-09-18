using System.Collections.Generic;
using System.Linq;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="RuntimeGrid"/>: the opt-in helper for a procedural, unbounded grid of runtime containers. The id
    /// packing is pinned against known pairs because it must stay bit-for-bit identical to Holospace's
    /// <c>WorldChunks.IdOf</c>/<c>CoordOf</c> (ids are already persisted).
    /// </summary>
    public sealed class RuntimeGridTests
    {
        private WorldDefinition definition;

        [SetUp]
        public void SetUp()
        {
            definition = ScriptableObject.CreateInstance<WorldDefinition>();
            definition.CellSize = Vector3.one * 512;
            WorldOrigin.Reset(definition);
        }

        [TearDown]
        public void TearDown()
        {
            WorldOrigin.Reset(null);
            Object.DestroyImmediate(definition);
        }

        // Known pairs, pinned against Holospace's WorldChunks.IdOf/CoordOf (three signed 21-bit fields, x high).
        [TestCase(0ul, 0, 0, 0)]
        [TestCase(2097151ul, 0, 0, -1)]
        [TestCase(9223367638808264704ul, -1, 0, 0)]
        [TestCase(9223367638810361855ul, -1, 0, -1)]
        [TestCase(9223372036854775807ul, -1, -1, -1)]
        public void PackIdMatchesKnownPairs(ulong id, int x, int y, int z)
        {
            var coord = new Vector3Int(x, y, z);
            Assert.AreEqual(id, RuntimeGrid.PackId(coord));
            Assert.AreEqual(coord, RuntimeGrid.UnpackId(id));
        }

        [Test]
        public void PackUnpackRoundTripsAcrossTheValidRange()
        {
            var coords = new[]
            {
                Vector3Int.zero,
                new Vector3Int(1, 2, 3),
                new Vector3Int(-1, -2, -3),
                new Vector3Int(RuntimeGrid.MinCoordinate, RuntimeGrid.MinCoordinate, RuntimeGrid.MinCoordinate),
                new Vector3Int(RuntimeGrid.MaxCoordinate, RuntimeGrid.MaxCoordinate, RuntimeGrid.MaxCoordinate),
                new Vector3Int(RuntimeGrid.MinCoordinate, RuntimeGrid.MaxCoordinate, 0),
            };
            foreach (var c in coords) Assert.AreEqual(c, RuntimeGrid.UnpackId(RuntimeGrid.PackId(c)), c.ToString());
        }

        [Test]
        public void IsValidRejectsCoordinatesOutsideThePackableRange()
        {
            Assert.IsTrue(RuntimeGrid.IsValid(new Vector3Int(RuntimeGrid.MinCoordinate, 0, RuntimeGrid.MaxCoordinate)));
            Assert.IsFalse(RuntimeGrid.IsValid(new Vector3Int(RuntimeGrid.MinCoordinate - 1, 0, 0)));
            Assert.IsFalse(RuntimeGrid.IsValid(new Vector3Int(0, RuntimeGrid.MaxCoordinate + 1, 0)));
        }

        [Test]
        public void PackIdThrowsOutsideTheValidRange()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => RuntimeGrid.PackId(new Vector3Int(RuntimeGrid.MaxCoordinate + 1, 0, 0)));
        }

        [Test]
        public void CoordOfMatchesTheCellAPositionFallsIn()
        {
            var grid = new RuntimeGrid(64f);
            Assert.AreEqual(Vector3Int.zero, grid.CoordOf(new Vector3(0, 0, 0)));
            Assert.AreEqual(Vector3Int.zero, grid.CoordOf(new Vector3(63.9f, 0, 0)));
            Assert.AreEqual(new Vector3Int(1, 0, 0), grid.CoordOf(new Vector3(64f, 0, 0)));
            Assert.AreEqual(new Vector3Int(-1, 0, 0), grid.CoordOf(new Vector3(-0.1f, 0, 0)));
        }

        [Test]
        public void CenterOfAndBoundsOfAgreeWithCoordOf()
        {
            var grid = new RuntimeGrid(64f);
            var coord = new Vector3Int(2, -1, 5);
            var center = grid.CenterOf(coord);
            Assert.AreEqual(coord, grid.CoordOf(center));
            var bounds = grid.BoundsOf(coord);
            Assert.AreEqual(center, bounds.center);
            Assert.AreEqual(new Vector3(64f, 64f, 64f), bounds.size);
        }

        [Test]
        public void CenterOfShiftsWithTheFloatingOrigin()
        {
            NebulaWorld.LoadRuntime(definition);
            try
            {
                var grid = new RuntimeGrid(64f);
                var before = grid.CenterOf(new Vector3Int(3, 0, 0));
                NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(3, 0, 0));
                var after = grid.CenterOf(new Vector3Int(3, 0, 0));
                // The origin sits on the cell's corner, so its centre is half a cell in.
                Assert.AreEqual(Vector3.one * 32f, after);
                Assert.AreNotEqual(before, after);
            }
            finally
            {
                ContainerRegistry.RuntimeBoundsInFrame = null;
                NebulaWorld.Unload();
            }
        }

        [Test]
        public void IsNearUsesChebyshevDistance()
        {
            Assert.IsTrue(RuntimeGrid.IsNear(Vector3Int.zero, new Vector3Int(1, 1, 1), 1));
            Assert.IsFalse(RuntimeGrid.IsNear(Vector3Int.zero, new Vector3Int(2, 0, 0), 1));
            Assert.IsTrue(RuntimeGrid.IsNear(Vector3Int.zero, new Vector3Int(2, 0, 0), 2));
        }

        [Test]
        public void NeighborhoodEnumeratesACubeOfTheRequestedRing()
        {
            var list = RuntimeGrid.Neighborhood(Vector3Int.zero, 1).ToList();
            Assert.AreEqual(27, list.Count);
            Assert.IsTrue(list.Contains(Vector3Int.zero));
            Assert.IsTrue(list.Contains(new Vector3Int(1, -1, 1)));
            Assert.IsFalse(list.Contains(new Vector3Int(2, 0, 0)));
        }

        [Test]
        public void NeighborhoodDropsCoordinatesOutsideThePackableRange()
        {
            var list = RuntimeGrid.Neighborhood(new Vector3Int(RuntimeGrid.MaxCoordinate, 0, 0), 1).ToList();
            Assert.IsTrue(list.All(RuntimeGrid.IsValid));
            Assert.IsTrue(list.Contains(new Vector3Int(RuntimeGrid.MaxCoordinate, 0, 0)));
            Assert.IsFalse(list.Contains(new Vector3Int(RuntimeGrid.MaxCoordinate + 1, 0, 0)));
        }

        [Test]
        public void BoundsOfIdMatchesUnpackingTheId()
        {
            var grid = new RuntimeGrid(64f);
            var coord = new Vector3Int(4, 0, -2);
            var id = RuntimeGrid.PackId(coord);
            Assert.AreEqual(grid.BoundsOf(coord), grid.BoundsOfId(id, default));
        }

        [Test]
        public void UseAsRuntimeBoundsWiresTheHookAndCanBeReplaced()
        {
            ContainerRegistry.RuntimeBoundsInFrame = null;
            var grid = new RuntimeGrid(64f);
            grid.UseAsRuntimeBounds();
            var coord = new Vector3Int(1, 2, 3);
            var id = RuntimeGrid.PackId(coord);
            Assert.AreEqual(grid.BoundsOf(coord), ContainerRegistry.RuntimeBoundsInFrame(id, default));
            ContainerRegistry.RuntimeBoundsInFrame = null;
        }

        [Test]
        public void NonUniformCellSizeIsRespectedPerAxis()
        {
            var grid = new RuntimeGrid(new Vector3(32f, 8f, 64f));
            var bounds = grid.BoundsOf(new Vector3Int(1, 1, 1));
            Assert.AreEqual(new Vector3(32f, 8f, 64f), bounds.size);
            Assert.AreEqual(new Vector3(48f, 12f, 96f), bounds.center);
        }

        [Test]
        public void ConstructorRejectsNonPositiveCellSize()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new RuntimeGrid(Vector3.zero));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new RuntimeGrid(new Vector3(1, -1, 1)));
        }
    }
}
