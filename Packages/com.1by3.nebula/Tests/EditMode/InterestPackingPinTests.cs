using NUnit.Framework;
using UnityEngine;
using Nebula.World;

namespace Nebula.Tests
{
    /// <summary>
    /// Region ids and runtime container ids must be the same numbers: in a chunked world a region <b>is</b> a
    /// chunk, and both are persisted. <see cref="InterestGrid"/> duplicates the packing because
    /// <see cref="RuntimeGrid"/> lives in a Unity-only assembly the standalone gateway does not compile, so this
    /// pins the two copies together. Neither may change; <c>RuntimeGridTests</c> pins the values themselves.
    /// </summary>
    public class InterestPackingPinTests
    {
        private static readonly Vector3Int[] Samples =
        {
            new Vector3Int(0, 0, 0),
            new Vector3Int(1, 2, 3),
            new Vector3Int(-1, -1, -1),
            new Vector3Int(-5, 0, 7),
            new Vector3Int(1048575, 1048575, 1048575),
            new Vector3Int(-1048576, -1048576, -1048576),
            new Vector3Int(123456, -654321, 42),
        };

        [Test]
        public void InterestPackingIsRuntimeGridPacking()
        {
            foreach (var coord in Samples)
                Assert.AreEqual(RuntimeGrid.PackId(coord), InterestGrid.PackRegion(coord.x, coord.y, coord.z), $"packing diverged at {coord}");
        }

        [Test]
        public void InterestUnpackingIsRuntimeGridUnpacking()
        {
            foreach (var coord in Samples)
            {
                ulong id = RuntimeGrid.PackId(coord);
                InterestGrid.UnpackRegion(id, out int x, out int y, out int z);
                Assert.AreEqual(RuntimeGrid.UnpackId(id), new Vector3Int(x, y, z));
            }
        }

        [Test]
        public void ARuntimeCellAndItsRegionAreTheSameIdWhenTheyAreTheSameSize()
        {
            var settings = InterestSettings.Default;
            settings.CellSize = 64;
            settings.Planar = false;
            var grid = InterestGrid.Resolve(settings, 64, cellsCentred: false);
            var runtime = new RuntimeGrid(64f);
            var coord = new Vector3Int(3, -2, 7);
            // The same absolute point, asked of both: a chunk world's region ids are its container ids.
            Assert.AreEqual(RuntimeGrid.PackId(coord), grid.RegionOf(3 * 64 + 1, -2 * 64 + 1, 7 * 64 + 1));
            Assert.AreEqual(new Vector3(64, 64, 64), runtime.CellSize);
        }
    }
}
