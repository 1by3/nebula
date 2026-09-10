using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>Grid arithmetic shared by the streamer, the editor tooling and Nebula's container integration.</summary>
    public static class WorldGrid
    {
        private const int MortonBias = 1 << 20; // coordinates within ±2^20 cells interleave into 63 bits

        /// <summary>
        /// Z-order (Morton) key of a coordinate: sorting cells by this key puts spatial neighbours next to each
        /// other in one dimension, which is what makes contiguous runs of cells compact regions.
        /// </summary>
        public static ulong Morton(Vector3Int c)
        {
            ulong x = Spread((uint)(c.x + MortonBias));
            ulong y = Spread((uint)(c.y + MortonBias));
            ulong z = Spread((uint)(c.z + MortonBias));
            return x | (y << 1) | (z << 2);
        }

        private static ulong Spread(uint v)
        {
            ulong x = v & 0x1FFFFF; // 21 bits
            x = (x | (x << 32)) & 0x1F00000000FFFFUL;
            x = (x | (x << 16)) & 0x1F0000FF0000FFUL;
            x = (x | (x << 8)) & 0x100F00F00F00F00FUL;
            x = (x | (x << 4)) & 0x10C30C30C30C30C3UL;
            x = (x | (x << 2)) & 0x1249249249249249UL;
            return x;
        }

        /// <summary>Every coordinate within <paramref name="radius"/> cells of <paramref name="center"/> on every axis (a cube; radius 0 = just the centre).</summary>
        public static void Neighborhood(Vector3Int center, int radius, ICollection<Vector3Int> result)
        {
            for (int x = -radius; x <= radius; x++)
                for (int y = -radius; y <= radius; y++)
                    for (int z = -radius; z <= radius; z++)
                        result.Add(new Vector3Int(center.x + x, center.y + y, center.z + z));
        }

        /// <summary>Chebyshev distance between two coordinates (how many rings apart).</summary>
        public static int Rings(Vector3Int a, Vector3Int b)
        {
            return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Max(Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
        }

        /// <summary>The 26 coordinates that share a face, edge or corner with <paramref name="c"/>.</summary>
        public static bool AreAdjacent(Vector3Int a, Vector3Int b) => a != b && Rings(a, b) == 1;
    }
}
