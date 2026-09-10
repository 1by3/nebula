using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula.World
{
    /// <summary>
    /// Loads and unloads cell scenes additively and keeps them placed relative to the floating origin. Which cells
    /// are wanted comes from two sources that are unioned every policy pass:
    /// <list type="bullet">
    /// <item><b>anchors</b> (<see cref="IWorldAnchor"/>): a position plus a radius in cells; the cube of cells around
    /// each anchor is loaded. A <see cref="WorldAnchor"/> component on the player is the usual one.</item>
    /// <item><b>required cells</b> (<see cref="SetRequired"/>): an explicit set, for callers that know exactly what
    /// they need (a server that simulates a region and wants its cells and a ring around them).</item>
    /// </list>
    /// Cells leaving the wanted set linger <see cref="LingerSeconds"/> before unloading so a player pacing on a
    /// boundary does not thrash. When <see cref="AutoShiftOrigin"/> is on and the primary anchor strays more than
    /// <see cref="ShiftThresholdCells"/> from the origin cell, the origin moves to the anchor's cell: every loaded
    /// cell's root objects are translated and <see cref="WorldOrigin.Shifted"/> tells everyone else.
    /// <para>Works on its own (no Nebula dependency); Nebula's integration drives it from leases and the local pawn.</para>
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class WorldStreamer : MonoBehaviour
    {
        public enum CellStatus { Loading, Loaded, Unloading }

        private sealed class CellState
        {
            public Vector3Int Coord;
            public CellStatus Status;
            public Scene Scene;
            public AsyncOperation Op;
            public float UnloadAt = -1f;
            public bool Wanted;
        }

        public static WorldStreamer Instance { get; private set; }

        public WorldDefinition Definition;
        [Tooltip("Cells stay loaded this long after nothing wants them any more.")]
        public float LingerSeconds = 3f;
        [Tooltip("How many cell scenes may be loading at once.")]
        public int MaxConcurrentLoads = 2;
        [Tooltip("How often the wanted set is recomputed.")]
        public float PolicyIntervalSeconds = 0.25f;
        [Tooltip("Move the floating origin to follow the primary anchor.")]
        public bool AutoShiftOrigin = true;
        [Tooltip("Shift once the primary anchor is more than this many cells from the origin cell on any axis.")]
        public int ShiftThresholdCells = 1;
        [Tooltip("Load nothing on the streamer's own initiative (a process that only needs the grid maths).")]
        public bool Passive;

        /// <summary>Anchor that the floating origin follows. Defaults to the first registered anchor.</summary>
        public IWorldAnchor PrimaryAnchor { get; set; }

        public event Action<Vector3Int, Scene> CellLoaded;
        public event Action<Vector3Int, Scene> CellUnloading;
        public event Action<Vector3Int> CellUnloaded;
        /// <summary>The origin moved by this delta (after the loaded cells were translated). Same as <see cref="WorldOrigin.Shifted"/>.</summary>
        public event Action<Vector3> OriginShifted;

        public int LoadedCount { get; private set; }
        public int LoadsInFlight { get; private set; }
        public bool HasPendingWork => _pendingOps > 0;

        private readonly List<IWorldAnchor> _anchors = new List<IWorldAnchor>();
        private readonly HashSet<Vector3Int> _required = new HashSet<Vector3Int>();
        private readonly Dictionary<Vector3Int, CellState> _cells = new Dictionary<Vector3Int, CellState>();
        private readonly HashSet<Vector3Int> _wanted = new HashSet<Vector3Int>();
        private readonly List<(Vector3Int cell, int radius)> _anchorCells = new List<(Vector3Int, int)>();
        private readonly List<Vector3Int> _scratch = new List<Vector3Int>();
        private readonly List<GameObject> _roots = new List<GameObject>();
        private float _nextPolicy;
        private int _pendingOps;
        private bool _dirty = true;

        public IEnumerable<Vector3Int> LoadedCells
        {
            get { foreach (var kv in _cells) if (kv.Value.Status == CellStatus.Loaded) yield return kv.Key; }
        }

        public bool IsLoaded(Vector3Int coord) => _cells.TryGetValue(coord, out var s) && s.Status == CellStatus.Loaded;
        public bool TryGetScene(Vector3Int coord, out Scene scene)
        {
            if (_cells.TryGetValue(coord, out var s) && s.Status == CellStatus.Loaded) { scene = s.Scene; return true; }
            scene = default;
            return false;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[world] a second WorldStreamer was created; destroying it");
                Destroy(this);
                return;
            }
            Instance = this;
            if (Definition != null && WorldOrigin.Definition != Definition) WorldOrigin.Reset(Definition);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------------------------------- inputs

        public void AddAnchor(IWorldAnchor anchor)
        {
            if (anchor == null || _anchors.Contains(anchor)) return;
            _anchors.Add(anchor);
            if (PrimaryAnchor == null) PrimaryAnchor = anchor;
            _dirty = true;
        }

        public void RemoveAnchor(IWorldAnchor anchor)
        {
            if (!_anchors.Remove(anchor)) return;
            if (ReferenceEquals(PrimaryAnchor, anchor)) PrimaryAnchor = _anchors.Count > 0 ? _anchors[0] : null;
            _dirty = true;
        }

        /// <summary>Replace the explicitly required set. Cells outside the definition are ignored.</summary>
        public void SetRequired(IEnumerable<Vector3Int> cells)
        {
            _required.Clear();
            if (cells != null) foreach (var c in cells) _required.Add(c);
            _dirty = true;
        }

        /// <summary>Recompute the wanted set on the next frame instead of waiting for the policy interval.</summary>
        public void Poke() => _dirty = true;

        /// <summary>Move the floating origin to <paramref name="cell"/> now (no-op if it already is there).</summary>
        public void ShiftOrigin(Vector3Int cell)
        {
            if (Definition == null || cell == WorldOrigin.Cell) return;
            var delta = WorldOrigin.ShiftDelta(Definition, WorldOrigin.Cell, cell);
            foreach (var kv in _cells)
            {
                var s = kv.Value;
                if (s.Status != CellStatus.Loaded || !s.Scene.IsValid()) continue;
                TranslateRoots(s.Scene, delta);
            }
            Physics.SyncTransforms();
            WorldOrigin.Apply(cell, delta);
            OriginShifted?.Invoke(delta);
        }

        // ---------------------------------------------------------------------------------------- policy

        private void Update()
        {
            if (Definition == null) return;
            if (AutoShiftOrigin && PrimaryAnchor != null && PrimaryAnchor.TryGetAnchor(out var primaryPos, out _))
            {
                var cell = Definition.CoordOf(primaryPos, WorldOrigin.Cell);
                if (WorldGrid.Rings(cell, WorldOrigin.Cell) > ShiftThresholdCells) ShiftOrigin(cell);
            }
            if (!_dirty && Time.unscaledTime < _nextPolicy) return;
            _dirty = false;
            _nextPolicy = Time.unscaledTime + PolicyIntervalSeconds;
            RunPolicy();
        }

        private void RunPolicy()
        {
            _anchorCells.Clear();
            if (!Passive)
            {
                for (int i = _anchors.Count - 1; i >= 0; i--)
                {
                    var a = _anchors[i];
                    if (a is UnityEngine.Object o && o == null) { _anchors.RemoveAt(i); continue; }
                    if (a.TryGetAnchor(out var pos, out int radius)) _anchorCells.Add((Definition.CoordOf(pos, WorldOrigin.Cell), radius));
                }
            }
            ComputeWanted(Definition, _anchorCells, _required, _wanted);

            float now = Time.unscaledTime;
            foreach (var coord in _wanted)
            {
                if (_cells.TryGetValue(coord, out var s)) { s.Wanted = true; s.UnloadAt = -1f; continue; }
                _cells[coord] = new CellState { Coord = coord, Wanted = true };
            }
            _scratch.Clear();
            foreach (var kv in _cells)
            {
                var s = kv.Value;
                if (_wanted.Contains(kv.Key)) continue;
                s.Wanted = false;
                if (s.Status == CellStatus.Unloading) continue;
                if (s.UnloadAt < 0f) s.UnloadAt = now + LingerSeconds;
                if (now >= s.UnloadAt && s.Status == CellStatus.Loaded) _scratch.Add(kv.Key);
            }
            foreach (var coord in _scratch) BeginUnload(_cells[coord]);

            // Start loads, nearest to the primary anchor first.
            if (LoadsInFlight < MaxConcurrentLoads)
            {
                _scratch.Clear();
                foreach (var kv in _cells) if (kv.Value.Wanted && kv.Value.Op == null && kv.Value.Status != CellStatus.Loaded && kv.Value.Status != CellStatus.Unloading) _scratch.Add(kv.Key);
                if (_scratch.Count > 1)
                {
                    var center = _anchorCells.Count > 0 ? _anchorCells[0].cell : WorldOrigin.Cell;
                    _scratch.Sort((a, b) => WorldGrid.Rings(a, center).CompareTo(WorldGrid.Rings(b, center)));
                }
                foreach (var coord in _scratch)
                {
                    if (LoadsInFlight >= MaxConcurrentLoads) break;
                    BeginLoad(_cells[coord]);
                }
            }
        }

        /// <summary>
        /// The set of cells to have loaded: the cube of <c>radius</c> cells around every anchor plus the required
        /// set, limited to cells the definition has. Pure, so it is testable without scenes.
        /// </summary>
        public static void ComputeWanted(WorldDefinition definition, IList<(Vector3Int cell, int radius)> anchors, IEnumerable<Vector3Int> required, HashSet<Vector3Int> result)
        {
            result.Clear();
            if (definition == null) return;
            var box = new List<Vector3Int>();
            for (int i = 0; i < anchors.Count; i++)
            {
                box.Clear();
                WorldGrid.Neighborhood(anchors[i].cell, Mathf.Max(0, anchors[i].radius), box);
                foreach (var c in box) if (definition.HasCell(c)) result.Add(c);
            }
            if (required != null) foreach (var c in required) if (definition.HasCell(c)) result.Add(c);
        }

        // ---------------------------------------------------------------------------------------- loading

        private void BeginLoad(CellState s)
        {
            var entry = Definition.GetCell(s.Coord);
            if (entry == null) { _cells.Remove(s.Coord); return; }
            var existing = SceneManager.GetSceneByPath(entry.ScenePath);
            if (existing.IsValid() && existing.isLoaded)
            {
                // Already open (the editor, or a game that loaded it by hand): adopt it as-is.
                s.Scene = existing;
                s.Status = CellStatus.Loaded;
                Place(s);
                return;
            }
            int buildIndex = SceneUtility.GetBuildIndexByScenePath(entry.ScenePath);
            if (buildIndex < 0)
            {
                Debug.LogError($"[world] cell {s.Coord}: scene '{entry.ScenePath}' is not in the build settings");
                _cells.Remove(s.Coord);
                return;
            }
            s.Status = CellStatus.Loading;
            s.Op = SceneManager.LoadSceneAsync(buildIndex, LoadSceneMode.Additive);
            if (s.Op == null) { _cells.Remove(s.Coord); return; }
            LoadsInFlight++;
            _pendingOps++;
            var coord = s.Coord;
            string path = entry.ScenePath;
            s.Op.completed += _ => OnLoaded(coord, path);
        }

        private void OnLoaded(Vector3Int coord, string scenePath)
        {
            LoadsInFlight--;
            _pendingOps--;
            if (!_cells.TryGetValue(coord, out var s)) return;
            s.Op = null;
            var scene = SceneManager.GetSceneByPath(scenePath);
            if (!scene.IsValid()) scene = SceneManager.GetSceneByName(WorldDefinition.SceneNameOf(scenePath));
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError($"[world] cell {coord}: scene '{scenePath}' did not load");
                _cells.Remove(coord);
                return;
            }
            s.Scene = scene;
            s.Status = CellStatus.Loaded;
            Place(s);
            if (!s.Wanted && s.UnloadAt < 0f) s.UnloadAt = Time.unscaledTime + LingerSeconds;
            _dirty = true;
        }

        private void Place(CellState s)
        {
            // The scene is authored around its own origin; put it where its cell is in the current frame. A scene
            // that was already open (the editor placed it) may carry a WorldCell root telling where it sits now.
            var target = Definition.FrameOrigin(s.Coord, WorldOrigin.Cell);
            var current = Vector3.zero;
            _roots.Clear();
            s.Scene.GetRootGameObjects(_roots);
            foreach (var root in _roots)
            {
                var cell = root.GetComponent<WorldCell>();
                if (cell != null) { current = root.transform.position; break; }
            }
            TranslateRoots(s.Scene, target - current);
            Physics.SyncTransforms();
            LoadedCount++;
            CellLoaded?.Invoke(s.Coord, s.Scene);
        }

        private void BeginUnload(CellState s)
        {
            if (!s.Scene.IsValid() || !s.Scene.isLoaded)
            {
                _cells.Remove(s.Coord);
                return;
            }
            s.Status = CellStatus.Unloading;
            LoadedCount--;
            CellUnloading?.Invoke(s.Coord, s.Scene);
            var op = SceneManager.UnloadSceneAsync(s.Scene, UnloadSceneOptions.None);
            var coord = s.Coord;
            if (op == null) { _cells.Remove(coord); CellUnloaded?.Invoke(coord); return; }
            _pendingOps++;
            op.completed += _ =>
            {
                _pendingOps--;
                if (_cells.TryGetValue(coord, out var cur) && cur == s) _cells.Remove(coord);
                CellUnloaded?.Invoke(coord);
                _dirty = true;
            };
        }

        private void TranslateRoots(Scene scene, Vector3 delta)
        {
            if (delta == Vector3.zero) return;
            _roots.Clear();
            scene.GetRootGameObjects(_roots);
            foreach (var root in _roots) root.transform.position += delta;
        }
    }

    /// <summary>Something the streamer keeps cells loaded around.</summary>
    public interface IWorldAnchor
    {
        /// <summary>Current frame position and load radius in cells; false when the anchor has nothing to say (no pawn yet).</summary>
        bool TryGetAnchor(out Vector3 framePosition, out int radiusCells);
    }

    /// <summary>Anchor component: keeps the cells around this transform loaded. Put it on the player or the camera.</summary>
    public sealed class WorldAnchor : MonoBehaviour, IWorldAnchor
    {
        [Tooltip("Cells around this object to keep loaded (1 = the 3x3x3 block).")]
        public int RadiusCells = 1;
        [Tooltip("The floating origin follows this anchor.")]
        public bool Primary;

        private void OnEnable()
        {
            var s = WorldStreamer.Instance;
            if (s == null) return;
            s.AddAnchor(this);
            if (Primary) s.PrimaryAnchor = this;
        }

        private void OnDisable()
        {
            var s = WorldStreamer.Instance;
            if (s != null) s.RemoveAnchor(this);
        }

        public bool TryGetAnchor(out Vector3 framePosition, out int radiusCells)
        {
            framePosition = transform.position;
            radiusCells = RadiusCells;
            return true;
        }
    }
}
