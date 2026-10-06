using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-397: <see cref="ChunkKeys.TryHostedCoordOf(PersistedEntityRecord, System.Func{string, IChunkGridGeometry}, out string, out Vector3Int)"/>
    /// verifies a record through its hosted grid's geometry, so a geometry that packs its own container ids round-trips,
    /// and a plain lattice still goes through the default hash.
    /// </summary>
    public sealed class HostedCoordOfGeometryTests
    {
        private const string SphereKey = "planet/sphere";
        private const string LatticeKey = "planet/flat";

        /// <summary>A cell is (face, i, j) with face 0..5; ids are packed, not hashed.</summary>
        private sealed class PackedGeometry : IChunkGridGeometry
        {
            public float LeadStep => 1f;
            public Vector3Int CellOf(Vector3 local) => default;
            public Bounds BoundsOf(Vector3Int cell) => new Bounds();
            public Vector3Int Normalize(Vector3Int cell) => cell;
            public void Neighborhood(Vector3Int cell, int ring, List<Vector3Int> into) { }
            public ulong IdOf(string gridKey, Vector3Int cell) =>
                0xABCD000000000000UL ^ ((ulong)cell.x << 40) ^ ((ulong)cell.y << 20) ^ (ulong)cell.z;
        }

        /// <summary>Only faces 0..5 exist: any other face is not a cell.</summary>
        private sealed class FaceCheckedGeometry : IChunkGridGeometry
        {
            private readonly PackedGeometry _inner = new PackedGeometry();
            public float LeadStep => 1f;
            public Vector3Int CellOf(Vector3 local) => default;
            public Bounds BoundsOf(Vector3Int cell) => new Bounds();
            public Vector3Int Normalize(Vector3Int cell) => cell.x < 0 || cell.x > 5 ? new Vector3Int(0, cell.y, cell.z) : cell;
            public void Neighborhood(Vector3Int cell, int ring, List<Vector3Int> into) { }
            public ulong IdOf(string gridKey, Vector3Int cell) => _inner.IdOf(gridKey, cell);
        }

        private static readonly PackedGeometry Packed = new PackedGeometry();

        private static IChunkGridGeometry Resolve(string key) => key == SphereKey ? Packed : null;

        private static PersistedEntityRecord RecordIn(string gridKey, Vector3Int cell, ulong id) => new PersistedEntityRecord
        {
            ContainerId = ScopeKeys.RuntimeIdPrefix + id,
            PartId = ChunkKeys.HostedPartId(gridKey, cell),
        };

        [Test]
        public void PackedGeometry_RoundTrips()
        {
            var cell = new Vector3Int(3, 17, 240);
            var record = RecordIn(SphereKey, cell, Packed.IdOf(SphereKey, cell));
            Assert.That(Packed.IdOf(SphereKey, cell), Is.Not.EqualTo(ChunkKeys.RuntimeId(SphereKey, cell)), "the test geometry's ids differ from the default hash");

            Assert.That(ChunkKeys.TryHostedCoordOf(record, Resolve, out var grid, out var coord), Is.True);
            Assert.AreEqual(SphereKey, grid);
            Assert.AreEqual(cell, coord);
            Assert.That(ChunkKeys.TryHostedCoordOf(record, out _, out _), Is.False, "the default formula alone cannot place it");
        }

        [Test]
        public void PackedGeometry_RejectsAMismatchedContainerId()
        {
            var cell = new Vector3Int(3, 17, 240);
            var record = RecordIn(SphereKey, cell, Packed.IdOf(SphereKey, cell) + 1);
            Assert.That(ChunkKeys.TryHostedCoordOf(record, Resolve, out var grid, out var coord), Is.False);
            Assert.IsNull(grid);
            Assert.AreEqual(default(Vector3Int), coord);

            var hashed = RecordIn(SphereKey, cell, ChunkKeys.RuntimeId(SphereKey, cell));
            Assert.That(ChunkKeys.TryHostedCoordOf(hashed, Resolve, out _, out _), Is.False, "a default-hash id is not the geometry's id");
        }

        [Test]
        public void Geometry_RejectsANonCanonicalCell()
        {
            var geometry = new FaceCheckedGeometry();
            var cell = new Vector3Int(9, 1, 2);
            var record = RecordIn(SphereKey, cell, geometry.IdOf(SphereKey, cell));
            Assert.That(ChunkKeys.TryHostedCoordOf(record, k => geometry, out _, out _), Is.False);
            var ok = new Vector3Int(5, 1, 2);
            Assert.That(ChunkKeys.TryHostedCoordOf(RecordIn(SphereKey, ok, geometry.IdOf(SphereKey, ok)), k => geometry, out _, out var coord), Is.True);
            Assert.AreEqual(ok, coord);
        }

        [Test]
        public void LatticeGrid_StillUsesTheDefaultFormula()
        {
            var cell = new Vector3Int(-4, 0, 7);
            var record = RecordIn(LatticeKey, cell, ChunkKeys.RuntimeId(LatticeKey, cell));
            Assert.That(ChunkKeys.TryHostedCoordOf(record, Resolve, out var grid, out var coord), Is.True, "the resolver knows no geometry for it");
            Assert.AreEqual(LatticeKey, grid);
            Assert.AreEqual(cell, coord);
            Assert.That(ChunkKeys.TryHostedCoordOf(record, out grid, out coord), Is.True, "and the original signature agrees");
            Assert.That(ChunkKeys.TryHostedCoordOf(record, k => new ChunkLattice(new Vector3(64f, 64f, 64f), false), out _, out _), Is.True, "a ChunkLattice geometry is the default formula");

            var wrong = RecordIn(LatticeKey, cell, ChunkKeys.RuntimeId(LatticeKey, cell) + 1);
            Assert.That(ChunkKeys.TryHostedCoordOf(wrong, Resolve, out _, out _), Is.False);
        }

        [Test]
        public void CarrierRecordsAndMissingRecordsAreRejected()
        {
            Assert.That(ChunkKeys.TryHostedCoordOf(null, Resolve, out _, out _), Is.False);
            var cell = new Vector3Int(1, 2, 3);
            var record = RecordIn(SphereKey, cell, Packed.IdOf(SphereKey, cell));
            record.CarrierKey = "ship/1";
            Assert.That(ChunkKeys.TryHostedCoordOf(record, Resolve, out _, out _), Is.False);
        }
    }
}
