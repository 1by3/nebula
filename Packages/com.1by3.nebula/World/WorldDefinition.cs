using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// A world partitioned into a 3D grid of cells, each authored as its own additive scene. Cells are authored
    /// <b>cell-local</b>: everything in a cell scene sits around that scene's own origin, and at runtime the
    /// <see cref="WorldStreamer"/> places the cell at its grid position relative to the current
    /// <see cref="WorldOrigin"/>. Nothing ever needs an absolute world coordinate, so a world can be far larger
    /// than float precision allows.
    /// <para>
    /// Cell <c>(x, y, z)</c> is centred on <c>(x, y, z) * CellSize</c>: cell (0,0,0) covers ±CellSize/2 on every
    /// axis, which is exactly how a scene authored around the origin already looks. A flat world is a single row of
    /// cells on Y; a world that shards verticality uses more.
    /// </para>
    /// </summary>
    [CreateAssetMenu(menuName = "Nebula/World Definition", fileName = "World")]
    public sealed class WorldDefinition : ScriptableObject
    {
        [Serializable]
        public sealed class CellEntry
        {
            public Vector3Int Coord;
            [Tooltip("Project path of the cell's scene (Assets/.../Name.unity). The scene must be in the build settings.")]
            public string ScenePath = "";

            public string SceneName => SceneNameOf(ScenePath);
        }

        [Tooltip("Used to name cell scenes: <WorldName>_<x>_<y>_<z>.unity.")]
        public string WorldName = "World";
        [Tooltip("Size of one cell in metres. Adjustable per world; every cell of a world has the same size.")]
        public Vector3 CellSize = new Vector3(256f, 256f, 256f);
        [Tooltip("Editor: folder new cell scenes are created in.")]
        public string SceneFolder = "Assets/Scenes/World";
        public List<CellEntry> Cells = new List<CellEntry>();

        [NonSerialized] private Dictionary<Vector3Int, CellEntry> _byCoord;
        [NonSerialized] private int _indexedCount = -1;

        private Dictionary<Vector3Int, CellEntry> ByCoord
        {
            get
            {
                if (_byCoord == null || _indexedCount != Cells.Count)
                {
                    _byCoord = new Dictionary<Vector3Int, CellEntry>();
                    foreach (var c in Cells) _byCoord[c.Coord] = c;
                    _indexedCount = Cells.Count;
                }
                return _byCoord;
            }
        }

        /// <summary>Drop the coordinate index (call after editing <see cref="Cells"/> in place).</summary>
        public void Invalidate() { _byCoord = null; _indexedCount = -1; }

        private void OnValidate()
        {
            CellSize = new Vector3(Mathf.Max(1f, CellSize.x), Mathf.Max(1f, CellSize.y), Mathf.Max(1f, CellSize.z));
            Invalidate();
        }

        public bool HasCell(Vector3Int coord) => ByCoord.ContainsKey(coord);
        public bool TryGetCell(Vector3Int coord, out CellEntry entry) => ByCoord.TryGetValue(coord, out entry);
        public CellEntry GetCell(Vector3Int coord) => ByCoord.TryGetValue(coord, out var e) ? e : null;

        /// <summary>Add or replace the entry for <paramref name="coord"/>.</summary>
        public CellEntry SetCell(Vector3Int coord, string scenePath)
        {
            var e = GetCell(coord);
            if (e == null) { e = new CellEntry { Coord = coord }; Cells.Add(e); }
            e.ScenePath = scenePath;
            Invalidate();
            return e;
        }

        public bool RemoveCell(Vector3Int coord)
        {
            int n = Cells.RemoveAll(c => c.Coord == coord);
            Invalidate();
            return n > 0;
        }

        /// <summary>Grid coordinate of a position expressed in the frame whose origin cell is <paramref name="origin"/>.</summary>
        public Vector3Int CoordOf(Vector3 framePosition, Vector3Int origin)
        {
            return new Vector3Int(
                origin.x + Mathf.FloorToInt((framePosition.x + CellSize.x * 0.5f) / CellSize.x),
                origin.y + Mathf.FloorToInt((framePosition.y + CellSize.y * 0.5f) / CellSize.y),
                origin.z + Mathf.FloorToInt((framePosition.z + CellSize.z * 0.5f) / CellSize.z));
        }

        /// <summary>Where cell <paramref name="coord"/>'s centre sits in the frame whose origin cell is <paramref name="origin"/>.</summary>
        public Vector3 FrameOrigin(Vector3Int coord, Vector3Int origin)
        {
            var d = coord - origin;
            return new Vector3(d.x * CellSize.x, d.y * CellSize.y, d.z * CellSize.z);
        }

        public Bounds FrameBounds(Vector3Int coord, Vector3Int origin) => new Bounds(FrameOrigin(coord, origin), CellSize);

        /// <summary>Axis-aligned extent of the defined cells in grid coordinates (min/max inclusive). False when there are none.</summary>
        public bool TryGetExtent(out Vector3Int min, out Vector3Int max)
        {
            min = max = default;
            if (Cells.Count == 0) return false;
            min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
            max = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            foreach (var c in Cells)
            {
                min = Vector3Int.Min(min, c.Coord);
                max = Vector3Int.Max(max, c.Coord);
            }
            return true;
        }

        /// <summary>Scene name convention: <c>World_1_0_n2</c> for cell (1, 0, -2) of world "World".</summary>
        public string SceneNameFor(Vector3Int coord) => $"{WorldName}_{Fmt(coord.x)}_{Fmt(coord.y)}_{Fmt(coord.z)}";
        public string ScenePathFor(Vector3Int coord) => $"{SceneFolder.TrimEnd('/')}/{SceneNameFor(coord)}.unity";

        private static string Fmt(int v) => v < 0 ? "n" + (-v) : v.ToString();

        public static string SceneNameOf(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath)) return "";
            int slash = scenePath.LastIndexOf('/');
            string file = slash >= 0 ? scenePath.Substring(slash + 1) : scenePath;
            return file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 6) : file;
        }

        /// <summary>Parse a scene name produced by <see cref="SceneNameFor"/> back into a coordinate.</summary>
        public static bool TryParseSceneName(string sceneName, out string worldName, out Vector3Int coord)
        {
            worldName = "";
            coord = default;
            if (string.IsNullOrEmpty(sceneName)) return false;
            var parts = sceneName.Split('_');
            if (parts.Length < 4) return false;
            if (!TryParseAxis(parts[parts.Length - 3], out int x) || !TryParseAxis(parts[parts.Length - 2], out int y) || !TryParseAxis(parts[parts.Length - 1], out int z)) return false;
            worldName = string.Join("_", parts, 0, parts.Length - 3);
            coord = new Vector3Int(x, y, z);
            return true;
        }

        private static bool TryParseAxis(string s, out int v)
        {
            v = 0;
            if (string.IsNullOrEmpty(s)) return false;
            bool neg = s[0] == 'n';
            if (!int.TryParse(neg ? s.Substring(1) : s, out v)) return false;
            if (neg) v = -v;
            return true;
        }
    }
}
