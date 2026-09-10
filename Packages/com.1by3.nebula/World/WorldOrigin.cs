using System;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// The floating origin: which cell currently sits at Unity's (0,0,0). Every position a process handles is
    /// expressed in this frame (a "frame position"). When the frame moves, <see cref="Shifted"/> reports the delta
    /// to add to every frame position the streamer did not move itself (cached positions, history buffers,
    /// world-space particle systems...).
    /// </summary>
    public static class WorldOrigin
    {
        /// <summary>The cell at Unity's origin.</summary>
        public static Vector3Int Cell { get; private set; }
        public static WorldDefinition Definition { get; private set; }
        public static int ShiftCount { get; private set; }

        /// <summary>Raised after the streamer has moved the loaded cells: the delta to add to any other frame position.</summary>
        public static event Action<Vector3> Shifted;

        public static void Reset(WorldDefinition definition)
        {
            Definition = definition;
            Cell = Vector3Int.zero;
            ShiftCount = 0;
        }

        /// <summary>Delta every frame position gets when the origin moves from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static Vector3 ShiftDelta(WorldDefinition definition, Vector3Int from, Vector3Int to)
        {
            var d = from - to;
            return new Vector3(d.x * definition.CellSize.x, d.y * definition.CellSize.y, d.z * definition.CellSize.z);
        }

        internal static void Apply(Vector3Int newCell, Vector3 delta)
        {
            Cell = newCell;
            ShiftCount++;
            Shifted?.Invoke(delta);
        }

        /// <summary>Frame position of a point given as cell coordinate plus local offset from that cell's centre.</summary>
        public static Vector3 ToFrame(Vector3Int cell, Vector3 cellLocal)
        {
            return Definition != null ? Definition.FrameOrigin(cell, Cell) + cellLocal : cellLocal;
        }

        /// <summary>Which cell a frame position lies in.</summary>
        public static Vector3Int CellOf(Vector3 framePosition)
        {
            return Definition != null ? Definition.CoordOf(framePosition, Cell) : Vector3Int.zero;
        }
    }
}
