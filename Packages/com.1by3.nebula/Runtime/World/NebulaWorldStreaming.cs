using System.Collections.Generic;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Decides, per role, which cells this process keeps loaded and where the floating origin sits:
    /// <list type="bullet">
    /// <item><b>worker</b>: the cells of every container it leases plus <see cref="NebulaConfig.WorkerLoadRingCells"/>
    /// rings around them so ghosts and boundary physics have the neighboring geometry. The origin follows the center of
    /// the owned cells.</item>
    /// <item><b>client</b> (players and bots): <see cref="NebulaConfig.ClientLoadRadiusCells"/> around its content
    /// anchor — the local pawn by default, or whatever <c>NebulaClient.SetContentAnchor</c> was given (a strategy
    /// camera) — and the origin follows the same anchor. Before either exists, the cells around the origin cell.</item>
    /// <item><b>gateway / orchestrator</b>: nothing. They only need the container maths, which the manifest provides.</item>
    /// </list>
    /// The origin shifts once the anchor/centroid is more than <see cref="NebulaConfig.OriginShiftThresholdCells"/>
    /// cells from the origin cell, so a shift is rare and never happens while pacing on a cell boundary.
    /// </summary>
    public sealed class NebulaWorldStreaming : MonoBehaviour, IWorldAnchor
    {
        public NebulaConfig Config { get; private set; }
        public NebulaWorker Worker { get; private set; }
        public NebulaClient Client { get; private set; }
        public int OwnedCellCount { get; private set; }
        public int RequiredCellCount { get; private set; }

        private readonly HashSet<Vector3Int> _owned = new HashSet<Vector3Int>();
        private readonly HashSet<Vector3Int> _required = new HashSet<Vector3Int>();
        private readonly List<Vector3Int> _scratch = new List<Vector3Int>();
        private bool _leasesDirty;
        private int _nearCells = 1;

        /// <summary>Cells of content a client keeps loaded: <see cref="InterestSettings.NearCells"/> of the world's cell size.</summary>
        public int NearCells => _nearCells;

        public void Initialize(NebulaConfig config, NebulaWorker worker, NebulaClient client)
        {
            Config = config;
            Worker = worker;
            Client = client;
            var cellSize = NebulaWorld.Definition != null ? Mathf.Max(NebulaWorld.Definition.CellSize.x, NebulaWorld.Definition.CellSize.z) : 0f;
            _nearCells = config.ToInterestSettings().NearCells(cellSize);
            var streamer = NebulaWorld.Streamer;
            if (worker != null)
            {
                ContainerRegistry.LeasesChanged += OnLeasesChanged;
                _leasesDirty = true;
            }
            else if (client != null)
            {
                streamer.AddAnchor(this);
                streamer.PrimaryAnchor = this;
            }
            else
            {
                streamer.Passive = true;
            }
        }

        private void OnDestroy()
        {
            ContainerRegistry.LeasesChanged -= OnLeasesChanged;
            if (NebulaWorld.Streamer != null) NebulaWorld.Streamer.RemoveAnchor(this);
        }

        private void OnLeasesChanged() => _leasesDirty = true;

        private void Update()
        {
            if (!NebulaWorld.IsActive) return;
            if (Worker != null)
            {
                if (_leasesDirty) { _leasesDirty = false; UpdateWorkerCells(); }
                return;
            }
            if (Client != null)
            {
                // The anchor, not the pawn: a strategy camera that has flown away from its pawn must take the
                // origin with it, or it ends up rendering the far side of the world in single-precision metres
                // from an origin nobody is near. NebulaClient.ActiveContentAnchor is the pawn until a game says
                // otherwise, so this is the same behaviour for everything that has not asked for the other.
                var anchor = Client.ActiveContentAnchor;
                if (anchor != null)
                {
                    var cell = WorldOrigin.CellOf(anchor.position);
                    if (WorldGrid.Rings(cell, WorldOrigin.Cell) > Config.OriginShiftThresholdCells) NebulaWorld.Streamer.ShiftOrigin(cell);
                }
            }
        }

        private void UpdateWorkerCells()
        {
            _owned.Clear();
            foreach (var c in ContainerRegistry.All)
                if (c.IsOwnedBy(Worker.WorkerId)) _owned.Add(c.Cell);
            OwnedCellCount = _owned.Count;

            if (_owned.Count > 0)
            {
                var centroid = Centroid(_owned);
                if (WorldGrid.Rings(centroid, WorldOrigin.Cell) > Config.OriginShiftThresholdCells) NebulaWorld.Streamer.ShiftOrigin(centroid);
            }

            _required.Clear();
            _scratch.Clear();
            foreach (var cell in _owned)
            {
                _scratch.Clear();
                WorldGrid.Neighborhood(cell, Mathf.Max(0, Config.WorkerLoadRingCells), _scratch);
                foreach (var c in _scratch) _required.Add(c);
            }
            RequiredCellCount = _required.Count;
            NebulaWorld.Streamer.SetRequired(_required);
        }

        /// <summary>Rounded average coordinate of a set of cells.</summary>
        public static Vector3Int Centroid(ICollection<Vector3Int> cells)
        {
            if (cells.Count == 0) return Vector3Int.zero;
            long x = 0, y = 0, z = 0;
            foreach (var c in cells) { x += c.x; y += c.y; z += c.z; }
            return new Vector3Int(Mathf.RoundToInt(x / (float)cells.Count), Mathf.RoundToInt(y / (float)cells.Count), Mathf.RoundToInt(z / (float)cells.Count));
        }

        bool IWorldAnchor.TryGetAnchor(out Vector3 framePosition, out int radiusCells)
        {
            // One notion of "near" (design §8): content must reach at least as far as interest, or a client is
            // spawned an entity standing on a cell it has not loaded. ClientLoadRadiusCells stays the floor, so a
            // game asking for more cells than interest needs still gets them.
            radiusCells = Mathf.Max(0, _nearCells);
            // Whatever the client anchors content to (the pawn by default, the active camera when a game has
            // called NebulaClient.SetContentAnchor). The streamer applies its own load/unload hysteresis around
            // whatever this returns, so a camera gets exactly the treatment a pawn does.
            var anchor = Client != null ? Client.ActiveContentAnchor : null;
            framePosition = anchor != null ? anchor.position : Vector3.zero;
            return true;
        }
    }
}
