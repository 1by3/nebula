using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// The shape of a hosted chunk grid (<c>docs/container-tree.md</c> D22): which cell a point of the host belongs
    /// to, each cell's box, which cells surround a cell, and what id each cell's container gets. A cell is named by a
    /// <see cref="Vector3Int"/> the geometry gives meaning to; it is what part ids carry (<see cref="ChunkKeys.HostedPartId"/>)
    /// and what the allocator rings and leads in. Every coordinate here is the host's own.
    /// <para>
    /// <see cref="ChunkLattice"/> (planar or volumetric boxes) is the built-in geometry and the default. A game supplies
    /// another, such as a cube-sphere's faces, with <see cref="NebulaChunkedWorld.HostedGeometry"/> on every process.
    /// </para>
    /// </summary>
    public interface IChunkGridGeometry
    {
        /// <summary>
        /// The cell a point belongs to. Decided by the cell's own extent, not by which box holds the point: on a curved
        /// surface the boxes of neighbouring cells overlap.
        /// </summary>
        Vector3Int CellOf(Vector3 local);

        /// <summary>The cell's bounding box: the leased container's box, axis-aligned in the host's coordinates.</summary>
        Bounds BoundsOf(Vector3Int cell);

        /// <summary>The canonical name of a cell: drop or fold whatever the geometry does not use (a planar lattice's y).</summary>
        Vector3Int Normalize(Vector3Int cell);

        /// <summary>
        /// Append every cell within <paramref name="ring"/> steps of adjacency of <paramref name="cell"/>, itself
        /// included, to <paramref name="into"/>. Steps across a face edge count like any other.
        /// </summary>
        void Neighborhood(Vector3Int cell, int ring, List<Vector3Int> into);

        /// <summary>
        /// The 64-bit container id of a cell of the grid <paramref name="gridKey"/>. The default derivation is
        /// <see cref="ChunkKeys.RuntimeId"/>; a geometry that packs its own must still keep two grids' ids apart.
        /// </summary>
        ulong IdOf(string gridKey, Vector3Int cell);

        /// <summary>How far apart, in metres, the allocator samples a pawn's lead line so that it steps over no cell.</summary>
        float LeadStep { get; }
    }

    /// <summary>
    /// The built-in <see cref="IChunkGridGeometry"/>: a lattice of equal boxes, either one layer of columns centred on
    /// y = 0 (<see cref="Planar"/>) or a volumetric lattice, cell (x, y, z) spanning [x, x + 1) cells on each axis.
    /// </summary>
    public sealed class ChunkLattice : IChunkGridGeometry
    {
        public Vector3 CellSize { get; }
        public bool Planar { get; }

        public ChunkLattice(Vector3 cellSize, bool planar)
        {
            CellSize = cellSize;
            Planar = planar;
        }

        public Vector3Int CellOf(Vector3 local) => new Vector3Int(
            Mathf.FloorToInt(local.x / CellSize.x),
            Planar ? 0 : Mathf.FloorToInt(local.y / CellSize.y),
            Mathf.FloorToInt(local.z / CellSize.z));

        public Bounds BoundsOf(Vector3Int cell)
        {
            cell = Normalize(cell);
            return new Bounds(new Vector3(
                (float)((cell.x + 0.5) * CellSize.x),
                Planar ? 0f : (float)((cell.y + 0.5) * CellSize.y),
                (float)((cell.z + 0.5) * CellSize.z)), CellSize);
        }

        public Vector3Int Normalize(Vector3Int cell) => Planar ? new Vector3Int(cell.x, 0, cell.z) : cell;

        public void Neighborhood(Vector3Int cell, int ring, List<Vector3Int> into)
        {
            if (into == null) return;
            int yLow = Planar ? 0 : -ring, yHigh = Planar ? 0 : ring;
            int cy = Planar ? 0 : cell.y;
            for (int x = -ring; x <= ring; x++)
                for (int y = yLow; y <= yHigh; y++)
                    for (int z = -ring; z <= ring; z++)
                    {
                        var c = new Vector3Int(cell.x + x, cy + y, cell.z + z);
                        if (RuntimeGrid.IsValid(c)) into.Add(c);
                    }
        }

        public ulong IdOf(string gridKey, Vector3Int cell) => ChunkKeys.RuntimeId(gridKey, Normalize(cell));

        public float LeadStep => 0.5f * Mathf.Min(CellSize.x, Planar ? CellSize.z : Mathf.Min(CellSize.y, CellSize.z));
    }
}
