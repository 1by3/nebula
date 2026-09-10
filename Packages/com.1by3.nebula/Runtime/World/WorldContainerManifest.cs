using System;
using System.Collections.Generic;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The baked container tree of a partitioned world: one container per cell plus every hand-authored
    /// <see cref="Container"/> found inside the cell scenes, with cell-local poses. Every process instantiates the
    /// whole manifest at boot (thousands of empty transforms are cheap), so container indices are identical
    /// everywhere and never depend on which cell scenes happen to be loaded. Cell scenes stream content into
    /// those containers; the <see cref="Container"/> components authored in the scenes are stripped on load.
    /// <para>The entry order is the wire order: cells by Morton key, each followed by its nested containers, which
    /// is also the order the orchestrator deals contiguous runs of to workers. Rebake after moving or adding
    /// containers (Nebula &gt; World).</para>
    /// </summary>
    [CreateAssetMenu(menuName = "Nebula/World Container Manifest", fileName = "WorldContainers")]
    public sealed class WorldContainerManifest : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public string Id = "";
            public Vector3Int Cell;
            [Tooltip("The container that spans the whole cell (one per cell).")]
            public bool IsCell;
            [Tooltip("Pose relative to the cell centre (cell-local).")]
            public Vector3 LocalPosition;
            public Quaternion LocalRotation = Quaternion.identity;
            public Vector3 LocalScale = Vector3.one;
            public Vector3 Size;
            public Vector3 Center;
        }

        public WorldDefinition World;
        public List<Entry> Entries = new List<Entry>();

        /// <summary>Id of the container spanning cell <paramref name="coord"/>.</summary>
        public static string CellContainerId(Vector3Int coord) => $"cell_{Fmt(coord.x)}_{Fmt(coord.y)}_{Fmt(coord.z)}";
        private static string Fmt(int v) => v < 0 ? "n" + (-v) : v.ToString();

        /// <summary>Wire-order comparison: Morton key of the cell, cell container first, then larger boxes before smaller, then id.</summary>
        public static int Compare(Entry a, Entry b)
        {
            int c = WorldGrid.Morton(a.Cell).CompareTo(WorldGrid.Morton(b.Cell));
            if (c != 0) return c;
            if (a.IsCell != b.IsCell) return a.IsCell ? -1 : 1;
            float va = a.Size.x * a.Size.y * a.Size.z, vb = b.Size.x * b.Size.y * b.Size.z;
            c = vb.CompareTo(va);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Id, b.Id);
        }

        public void Sort() => Entries.Sort(Compare);
    }
}
