using System.Collections.Generic;
using System.Linq;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="RuntimeGrid"/>: the opt-in helper for a procedural, unbounded grid of runtime containers. The id
    /// packing is pinned against known pairs because existing ids are already persisted and must stay stable.
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

        // Known persisted pairs for three signed 21-bit fields, x high.
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

        // ------------------------------------------------------------------ scoped id layout (NEB-239)

        // Known pairs for the scoped derivation: the first eight bytes of SHA-256("<scope>/c/<x>/<y>/<z>"), which
        // is ScopeKeys.ContainerId of the chunk's part id. Persisted, like the packing above, and just as fixed.
        [TestCase("world/alpha", 1, 0, -2, 7764605123837071696ul)]
        [TestCase("world/beta", 1, 0, -2, 4884411379653675628ul)]
        [TestCase("world/alpha", 0, 0, 0, 889697383440456149ul)]
        [TestCase("world/beta", 0, 0, 0, 17316095309333124950ul)]
        public void ScopedChunkIdsMatchKnownPairs(string scope, int x, int y, int z, ulong id)
        {
            var coord = new Vector3Int(x, y, z);
            var grid = new RuntimeGrid(Vector3.one * 64f, planar: false, scopeKey: scope);
            Assert.AreEqual(id, grid.IdOf(coord));
            Assert.AreEqual(ScopeKeys.ContainerId(scope, ChunkKeys.PartId(coord)), grid.ContainerIdOf(coord));
            Assert.IsTrue(grid.TryCoordOf(id, out var back));
            Assert.AreEqual(coord, back);
        }

        [Test]
        public void ThePublicScopeKeepsThePinnedPackingAndScopesDoNot()
        {
            var coord = new Vector3Int(3, 0, -1);
            var pub = new RuntimeGrid(Vector3.one * 64f);
            Assert.AreEqual("", pub.ScopeKey);
            Assert.AreEqual(0ul, pub.InstanceId);
            Assert.AreEqual(RuntimeGrid.PackId(coord), pub.IdOf(coord), "a world with no scope must keep resolving the ids it already persisted");
            Assert.AreEqual(ContainerRegistry.RuntimeContainerId(RuntimeGrid.PackId(coord)), pub.ContainerIdOf(coord));

            var scoped = new RuntimeGrid(Vector3.one * 64f, planar: false, scopeKey: "world/alpha");
            Assert.AreNotEqual(pub.IdOf(coord), scoped.IdOf(coord), "the same coordinate in a scope is a different container");
            Assert.AreEqual(ScopeKeys.Hash("world/alpha"), scoped.InstanceId);
        }

        [Test]
        public void TwoScopesNeverShareAChunkIdOrAContainerId()
        {
            var a = new RuntimeGrid(Vector3.one * 64f, planar: true, scopeKey: "world/alpha");
            var b = new RuntimeGrid(Vector3.one * 64f, planar: true, scopeKey: "world/beta");
            for (int x = -2; x <= 2; x++)
                for (int z = -2; z <= 2; z++)
                {
                    var coord = new Vector3Int(x, 0, z);
                    Assert.AreNotEqual(a.IdOf(coord), b.IdOf(coord), coord.ToString());
                    Assert.AreNotEqual(a.ContainerIdOf(coord), b.ContainerIdOf(coord), coord.ToString());
                }
        }

        [Test]
        public void AScopedGridPlacesAnIdItNeverNamedOnceItAdoptsThePartIdFromTheLeaseRow()
        {
            var grid = new RuntimeGrid(Vector3.one * 64f, planar: true, scopeKey: "world/alpha");
            ulong id = ChunkKeys.RuntimeId("world/alpha", new Vector3Int(4, 0, 5));
            Assert.IsFalse(grid.TryCoordOf(id, out _), "a hash cannot be unpacked; a client has not computed this one");

            Assert.IsTrue(grid.Adopt(id, ChunkKeys.PartId(new Vector3Int(4, 0, 5)), out var coord));
            Assert.AreEqual(new Vector3Int(4, 0, 5), coord);
            Assert.IsTrue(grid.TryCoordOf(id, out coord));
            Assert.AreEqual(new Vector3Int(4, 0, 5), coord);
            Assert.AreEqual(grid.BoundsOf(coord), grid.BoundsOfId(id, default));

            Assert.IsFalse(grid.Adopt(id, "interior", out _), "an instance's part is not a chunk");
            Assert.IsFalse(grid.Adopt(id, ChunkKeys.PartId(new Vector3Int(9, 0, 9)), out _), "and a part id that does not derive this id is refused");
        }

        [Test]
        public void AScopedGridAnswersOnlyForItsOwnIdsSoBoundsNeverCrossScopes()
        {
            var a = new RuntimeGrid(Vector3.one * 64f, planar: true, scopeKey: "world/alpha");
            var b = new RuntimeGrid(Vector3.one * 64f, planar: true, scopeKey: "world/beta");
            var coord = new Vector3Int(1, 0, 1);
            ulong other = b.IdOf(coord);
            var fallback = new Bounds(Vector3.one * 999f, Vector3.one);
            Assert.AreEqual(fallback, a.BoundsOfId(other, fallback), "a grid must not place another scope's chunk");
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
