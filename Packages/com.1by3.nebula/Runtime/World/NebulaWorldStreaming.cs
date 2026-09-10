using System.Collections.Generic;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Decides, per role, which cells this process keeps loaded and where the floating origin sits:
    /// <list type="bullet">
    /// <item><b>worker</b>: the cells of every container it leases plus <see cref="NebulaConfig.WorkerLoadRingCells"/>
    /// rings around them (seam physics and ghosts need the neighbours' geometry). The origin follows the centroid of
    /// the owned cells.</item>
    /// <item><b>client</b> (players and bots): <see cref="NebulaConfig.ClientLoadRadiusCells"/> around the local pawn,
    /// the origin follows the pawn. Before a pawn exists, the cells around the origin cell.</item>
    /// <item><b>gateway / orchestrator</b>: nothing. They only need the container maths, which the manifest provides.</item>
    /// </list>
    /// The origin shifts once the pawn/centroid is more than <see cref="NebulaConfig.OriginShiftThresholdCells"/>
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

        public void Initialize(NebulaConfig config, NebulaWorker worker, NebulaClient client)
        {
            Config = config;
            Worker = worker;
            Client = client;
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
                var pawn = Client.LocalPlayer;
                if (pawn != null)
                {
                    var cell = WorldOrigin.CellOf(pawn.transform.position);
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
            radiusCells = Mathf.Max(0, Config.ClientLoadRadiusCells);
            var pawn = Client != null ? Client.LocalPlayer : null;
            framePosition = pawn != null ? pawn.transform.position : Vector3.zero;
            return true;
        }
    }
}
