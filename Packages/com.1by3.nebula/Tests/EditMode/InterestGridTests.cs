using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The region arithmetic every other part of interest management trusts: keys that do not move when the
    /// floating origin does, a grid that lines up with the world's cells, and collectors that find every region
    /// a focus can reach and no others.
    /// </summary>
    public class InterestGridTests
    {
        private static InterestSettings Settings(float cell = 64f, bool planar = true)
        {
            var s = InterestSettings.Default;
            s.CellSize = cell;
            s.Planar = planar;
            return s;
        }

        // ------------------------------------------------------------------------------------- far band

        private const double Mm = 1e9; // 1,000,000 km in metres

        [Test]
        public void PositionsInsideTheNearCubeKeepTheirKeys()
        {
            var grid = new InterestGrid(64, false);
            double edge = 64.0 * (1 << 20); // the near cube's face
            Assert.AreEqual(InterestGrid.PackRegion(InterestGrid.MaxCoordinate, 0, InterestGrid.MinCoordinate), grid.RegionOf(edge - 1, 0, -edge));
            Assert.IsFalse(InterestGrid.IsFar(grid.RegionOf(edge - 1, edge - 1, -edge)));
            Assert.IsTrue(InterestGrid.IsFar(grid.RegionOf(edge, 0, 0)));
            Assert.IsTrue(InterestGrid.IsFar(grid.RegionOf(0, 0, -edge - 0.001)));
        }

        [Test]
        public void RegionsAMillionKilometresOutAreDistinctOnEveryAxis()
        {
            foreach (bool planar in new[] { false, true })
            {
                var grid = new InterestGrid(64, planar);
                double far = 64.0 * InterestGrid.FarScale;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (planar && axis == 1) continue;
                    foreach (double sign in new[] { 1.0, -1.0 })
                    {
                        double[] p = { 0, 0, 0 };
                        p[axis] = sign * Mm;
                        ulong at = grid.RegionOf(p[0], p[1], p[2]);
                        Assert.IsTrue(InterestGrid.IsFar(at));
                        p[axis] = sign * (Mm + far);
                        ulong next = grid.RegionOf(p[0], p[1], p[2]);
                        p[axis] = sign * (Mm - far);
                        ulong prev = grid.RegionOf(p[0], p[1], p[2]);
                        p[axis] = sign * 1e12; // beyond even the far band: clamped
                        ulong clamped = grid.RegionOf(p[0], p[1], p[2]);
                        Assert.AreNotEqual(at, next, $"axis {axis} {sign}");
                        Assert.AreNotEqual(at, prev, $"axis {axis} {sign}");
                        Assert.AreNotEqual(at, clamped, $"axis {axis} {sign}");
                        p[axis] = sign * Mm;
                        Assert.AreEqual(0, grid.SqrDistanceToRegion(at, p[0], p[1], p[2]), $"axis {axis} {sign}");
                    }
                }
                // Points far apart off-axis are distinct too (the old packing clamped all of these together).
                Assert.AreNotEqual(grid.RegionOf(Mm, 0, Mm), grid.RegionOf(Mm, 0, -Mm));
                Assert.AreNotEqual(grid.RegionOf(112_957_000, 0, 0), grid.RegionOf(163_270_000, 0, 0));
            }
        }

        [Test]
        public void FarRegionsAreFarScaleEdgesWide()
        {
            var grid = new InterestGrid(64, false);
            ulong r = grid.RegionOf(Mm, 10, -Mm);
            grid.BoundsOf(r, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ);
            Assert.AreEqual(64.0 * InterestGrid.FarScale, maxX - minX, 1e-6);
            Assert.AreEqual(64.0 * InterestGrid.FarScale, maxY - minY, 1e-6);
            Assert.That(Mm, Is.InRange(minX, maxX));
            Assert.That(-Mm, Is.InRange(minZ, maxZ));
            grid.CenterOf(r, out double cx, out _, out _);
            Assert.AreEqual(r, grid.RegionOf(cx, 10, -Mm));
        }

        [Test]
        public void CollectorsSpanTheNearCubeFaceWithoutGapsOrOverlaps()
        {
            var grid = new InterestGrid(64, true);
            double face = 64.0 * (1 << 20);
            var disc = new List<ulong>();
            grid.CollectDisc(face, 0, 0, 5000, disc);
            Assert.AreEqual(disc.Count, new HashSet<ulong>(disc).Count);
            CollectionAssert.Contains(disc, grid.RegionOf(face - 1, 0, 0));
            CollectionAssert.Contains(disc, grid.RegionOf(face + 1, 0, 0));
            CollectionAssert.Contains(disc, grid.RegionOf(face + 4900, 0, 0));
            CollectionAssert.Contains(disc, grid.RegionOf(face - 4900, 0, 0));
            foreach (ulong r in disc)
                if (InterestGrid.IsFar(r))
                {
                    // Every far region collected lies outside the near cube.
                    grid.BoundsOf(r, out double minX, out _, out _, out _, out _, out _);
                    Assert.GreaterOrEqual(minX, face);
                }

            var box = new List<ulong>();
            grid.CollectBox(Mm - 100, 0, -100, Mm + 100, 0, 100, box);
            CollectionAssert.AreEquivalent(new[] { grid.RegionOf(Mm - 100, 0, -100), grid.RegionOf(Mm - 100, 0, 100) }, new HashSet<ulong>(box));
        }

        [Test]
        public void FarKeysSurviveTheScopeSalt()
        {
            var grid = new InterestGrid(64, false);
            ulong r = grid.RegionOf(-Mm, Mm, Mm);
            Assert.AreEqual(r, RegionKeys.Unsalt(RegionKeys.Salt(r, 42), 42));
            Assert.AreNotEqual(RegionKeys.Salt(r, 42), RegionKeys.Salt(r, 43));
        }

        [Test]
        public void KeysFollowTheCellAPositionFallsIn()
        {
            var grid = new InterestGrid(64, false);
            Assert.AreEqual(InterestGrid.PackRegion(0, 0, 0), grid.RegionOf(0, 0, 0));
            Assert.AreEqual(InterestGrid.PackRegion(0, 0, 0), grid.RegionOf(63.9, 63.9, 63.9));
            Assert.AreEqual(InterestGrid.PackRegion(1, 1, 1), grid.RegionOf(64, 64, 64));
        }

        [Test]
        public void NegativeCoordinatesFloorAwayFromZeroAndRoundTrip()
        {
            var grid = new InterestGrid(64, false);
            Assert.AreEqual(InterestGrid.PackRegion(-1, -1, -1), grid.RegionOf(-0.001, -0.001, -0.001));
            Assert.AreEqual(InterestGrid.PackRegion(-2, -1, -3), grid.RegionOf(-65, -64, -150));
            InterestGrid.UnpackRegion(InterestGrid.PackRegion(-2, 7, -1048576), out int x, out int y, out int z);
            Assert.AreEqual(-2, x);
            Assert.AreEqual(7, y);
            Assert.AreEqual(-1048576, z);
        }

        [Test]
        public void PlanarGridIgnoresHeightAndGivesColumns()
        {
            var grid = new InterestGrid(64);
            Assert.AreEqual(grid.RegionOf(10, -5000, 10), grid.RegionOf(10, 5000, 10));
            InterestGrid.UnpackRegion(grid.RegionOf(10, 5000, 10), out _, out int y, out _);
            Assert.AreEqual(0, y);
            grid.BoundsOf(grid.RegionOf(10, 5000, 10), out _, out double minY, out _, out _, out double maxY, out _);
            Assert.AreEqual(double.NegativeInfinity, minY);
            Assert.AreEqual(double.PositiveInfinity, maxY);
        }

        [Test]
        public void OffsetMovesRegionBoundaries()
        {
            var grid = new InterestGrid(64, 64, 64, -32, -32, -32, false);
            // With a -32 offset, 0 sits in the middle of region 0 and the boundary is at 32.
            Assert.AreEqual(InterestGrid.PackRegion(0, 0, 0), grid.RegionOf(0, 0, 0));
            Assert.AreEqual(InterestGrid.PackRegion(0, 0, 0), grid.RegionOf(31.9, 0, 0));
            Assert.AreEqual(InterestGrid.PackRegion(1, 0, 0), grid.RegionOf(32, 0, 0));
            grid.BoundsOf(InterestGrid.PackRegion(0, 0, 0), out double minX, out _, out _, out double maxX, out _, out _);
            Assert.AreEqual(-32, minX, 1e-9);
            Assert.AreEqual(32, maxX, 1e-9);
        }

        [Test]
        public void WithoutAWorldDefinitionTheEdgeIsTheConfiguredOne()
        {
            var grid = InterestGrid.Resolve(Settings(64));
            Assert.AreEqual(64, grid.EdgeX, 1e-9);
            Assert.AreEqual(0, grid.OffsetX, 1e-9);
            Assert.IsTrue(grid.Planar);
        }

        [Test]
        public void BakedCentredCellsSnapTheEdgeAndOffsetSoRegionsTileTheCell()
        {
            // A 2048 m baked cell with a 64 m region: 32 regions per axis, boundaries on the cell's edges.
            var grid = InterestGrid.Resolve(Settings(64), 2048, cellsCentred: true);
            Assert.AreEqual(64, grid.EdgeX, 1e-9);
            Assert.AreEqual(-1024, grid.OffsetX, 1e-9);
            // The cell centred on origin spans [-1024, 1024]; its lower corner must start a region.
            Assert.AreEqual(InterestGrid.PackRegion(0, 0, 0), grid.RegionOf(-1024, 0, -1024));
            Assert.AreEqual(InterestGrid.PackRegion(31, 0, 31), grid.RegionOf(1023.9, 0, 1023.9));
            Assert.AreEqual(InterestGrid.PackRegion(32, 0, 32), grid.RegionOf(1024, 0, 1024));
        }

        [Test]
        public void RuntimeCellsStartAtTheirCoordinateSoTheOffsetStaysZero()
        {
            var grid = InterestGrid.Resolve(Settings(64), 64, cellsCentred: false);
            Assert.AreEqual(64, grid.EdgeX, 1e-9);
            Assert.AreEqual(0, grid.OffsetX, 1e-9);
            // A 64 m chunk world gets regions that are exactly its chunks.
            Assert.AreEqual(InterestGrid.PackRegion(3, 0, -2), grid.RegionOf(3 * 64 + 1, 0, -2 * 64 + 1));
        }

        [Test]
        public void AnEdgeThatDoesNotDivideTheCellIsSnappedToOneThatDoes()
        {
            var grid = InterestGrid.Resolve(Settings(50), 128, cellsCentred: false);
            // 128 / 50 rounds to 3 divisions, so regions are 128/3 m and three of them tile a cell exactly.
            Assert.AreEqual(128.0 / 3, grid.EdgeX, 1e-9);
            Assert.AreEqual(InterestGrid.PackRegion(3, 0, 0), grid.RegionOf(128, 0, 0));
        }

        [Test]
        public void AKeyIsTheSameAbsolutePositionUnderAnyFloatingOrigin()
        {
            var grid = InterestGrid.Resolve(Settings(64), 256, cellsCentred: true);
            const double cell = 256;
            // The same entity: container cell (4, 0, -3), 20 m inside it, seen from two different origin cells.
            double absX = 4 * cell + 20, absZ = -3 * cell - 11;
            ulong fromOriginZero = grid.RegionOf(absX, 12, absZ);
            // A client whose origin is cell (4, 0, -3) holds the same entity at a frame position near zero; the
            // absolute position it reconstructs is identical, so the key must be too.
            double frameX = absX - 4 * cell, frameZ = absZ - -3 * cell;
            ulong fromShiftedOrigin = grid.RegionOf(frameX + 4 * cell, 12, frameZ + -3 * cell);
            Assert.AreEqual(fromOriginZero, fromShiftedOrigin);
        }

        [Test]
        public void DiscCollectsEveryRegionWithinReachAndNothingBeyondIt()
        {
            var grid = new InterestGrid(64);
            var regions = new List<ulong>();
            grid.CollectDisc(32, 0, 32, 10, regions);
            Assert.AreEqual(1, regions.Count, "a small disc well inside one region touches only it");
            Assert.AreEqual(grid.RegionOf(32, 0, 32), regions[0]);

            regions.Clear();
            grid.CollectDisc(0.5, 0, 0.5, 10, regions);
            Assert.AreEqual(4, regions.Count, "near a corner the disc reaches the three neighbours");
            CollectionAssert.Contains(regions, grid.RegionOf(-1, 0, -1));

            regions.Clear();
            grid.CollectDisc(32, 0, 32, 40, regions);
            // The corner regions are more than 40 m away from the centre, so they are left out.
            CollectionAssert.DoesNotContain(regions, grid.RegionOf(-32, 0, -32));
            Assert.AreEqual(5, regions.Count);
        }

        [Test]
        public void BoxCollectsEveryOverlappingRegion()
        {
            var grid = new InterestGrid(64);
            var regions = new List<ulong>();
            grid.CollectBox(-1, -100, -1, 65, 100, 1, regions);
            // x spans regions -1, 0 and 1; z spans -1 and 0.
            Assert.AreEqual(6, regions.Count);
            CollectionAssert.Contains(regions, grid.RegionOf(-1, 0, -1));
            CollectionAssert.Contains(regions, grid.RegionOf(64, 0, 0));
            CollectionAssert.DoesNotContain(regions, grid.RegionOf(0, 0, 64));
        }

        [Test]
        public void GridsAreComparedThroughTheFloatRoundTripTheMessageMakes()
        {
            var grid = InterestGrid.Resolve(Settings(64), 128, cellsCentred: true);
            var wire = new InterestSubscribeMsg { Grid = grid }.Grid;
            Assert.IsTrue(grid.Matches(wire), "an f32 round trip must not look like a different grid");
            Assert.IsFalse(grid.Matches(new InterestGrid(32)));
            Assert.IsFalse(new InterestGrid(64, true).Matches(new InterestGrid(64, false)));
        }
    }
}
