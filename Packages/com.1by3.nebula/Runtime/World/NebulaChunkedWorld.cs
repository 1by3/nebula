using System.Collections.Generic;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The whole server and client side of an unbounded chunked world, wired from configuration alone
    /// (<see cref="NebulaConfig.ChunkedWorld"/> plus a <see cref="NebulaConfig.RuntimeWorld"/>).
    /// <see cref="NebulaBootstrap"/> adds one of these on every role, and
    /// the game writes no world-management code at all: chunks are containers, containers are leased, content
    /// arrives through <see cref="NebulaChunks"/>.
    /// <list type="bullet">
    /// <item><b>every role</b>: the <see cref="RuntimeGrid"/> from the world definition's cell size, installed as
    /// the container-bounds hook and as the spatial-hash bucket size, and <see cref="NebulaChunks"/> activated.</item>
    /// <item><b>worker</b>: a <see cref="RuntimeGridAllocator"/> keeping a ring of chunks leased around every pawn
    /// it simulates (and around the origin, so a first player always has somewhere to appear), retiring what
    /// nobody has wanted for <see cref="NebulaConfig.ChunkRetireSeconds"/>. The origin follows the centroid of the
    /// cells this worker leases.</item>
    /// <item><b>client</b>: the origin follows the content anchor — the local pawn by default, or whatever
    /// <c>NebulaClient.SetContentAnchor</c> was given (a strategy camera).</item>
    /// <item><b>gateway / orchestrator</b>: passive. They need the container arithmetic and nothing else.</item>
    /// </list>
    /// <para>
    /// The allocator's ring is <see cref="InterestSettings.NearCells"/> + 1, so a
    /// chunk is always leased — and its content built — before interest can put an entity standing on it into a
    /// client's set. Raising <c>InterestRadius</c> therefore widens the chunk ring by itself; nothing to keep in step.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class NebulaChunkedWorld : MonoBehaviour
    {
        public NebulaConfig Config { get; private set; }
        public NebulaWorker Worker { get; private set; }
        public NebulaClient Client { get; private set; }

        /// <summary>The grid every role shares. Also reachable as <see cref="NebulaChunks.Grid"/>.</summary>
        public RuntimeGrid Grid { get; private set; }

        /// <summary>The worker's chunk allocator; null on other roles.</summary>
        public RuntimeGridAllocator Allocator { get; private set; }

        /// <summary>Chebyshev ring of chunks the worker keeps leased around every pawn.</summary>
        public int Ring { get; private set; }

        private readonly HashSet<Vector3Int> _owned = new HashSet<Vector3Int>();
        private bool _leasesDirty;
        private int _originRing = 1;

        internal void Initialize(NebulaConfig config, NebulaRoles roles, NebulaWorker worker, NebulaClient client)
        {
            Config = config;
            Worker = worker;
            Client = client;

            var definition = NebulaWorld.Definition;
            if (definition == null)
            {
                NebulaLog.Error("ChunkedWorld is on but no RuntimeWorld is assigned; the chunk grid needs its cell size. Assign NebulaConfig.RuntimeWorld.");
                enabled = false;
                return;
            }

            Grid = new RuntimeGrid(definition.CellSize, config.ChunkPlanar);
            // Every role resolves a chunk id to the same box, in its own origin frame; without this the registry
            // would keep whatever box the lease row happened to be created with and drift on an origin shift.
            Grid.UseAsRuntimeBounds();
            // The runtime spatial hash wants buckets a small multiple of a chunk; deriving it here is one less
            // number for a game to guess, and a wrong guess only shows up as slow neighbour queries.
            ContainerRegistry.RuntimeBucketSize = Mathf.Max(Grid.CellSize.x, Grid.CellSize.z) * 4f;

            var settings = config.ToInterestSettings();
            int nearCells = settings.NearCells(Mathf.Max(Grid.CellSize.x, Grid.CellSize.z));
            Ring = nearCells + 1;
            _originRing = Mathf.Max(1, config.OriginShiftThresholdCells);

            if (worker != null)
            {
                Allocator = new RuntimeGridAllocator(worker, Grid)
                {
                    Ring = Ring,
                    RetireAfterSeconds = Mathf.Max(0f, config.ChunkRetireSeconds),
                };
                // Without a standing anchor an empty mesh has no container at all, and the gateway has nowhere to
                // place the first player. The origin chunk is the world's guaranteed spawn area.
                Allocator.AddAnchor(Vector3Int.zero);
                ContainerRegistry.LeasesChanged += OnLeasesChanged;
                _leasesDirty = true;
            }

            NebulaChunks.Activate(Grid, roles, IsHeadless(roles), Allocator);
            NebulaLog.Info($"chunked world: cell {Grid.CellSize}{(Grid.Planar ? " (planar)" : "")}, near {nearCells} cell(s), allocator ring {Ring}, retire after {config.ChunkRetireSeconds}s");
        }

        /// <summary>Whether this process draws anything: workers and services never do, and a batchmode client (a bot) does not either.</summary>
        private static bool IsHeadless(NebulaRoles roles) =>
            (roles & NebulaRoles.Client) == 0 || Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        private void OnDestroy()
        {
            ContainerRegistry.LeasesChanged -= OnLeasesChanged;
        }

        private void OnLeasesChanged() => _leasesDirty = true;

        private void Update()
        {
            if (Grid == null) return;
            if (Allocator != null)
            {
                Allocator.Tick(Time.unscaledTime);
                if (_leasesDirty) { _leasesDirty = false; FollowOwnedCells(); }
                return;
            }
            // A client keeps the origin on whatever it anchors content to — its own pawn by default, the active
            // camera once a game has called NebulaClient.SetContentAnchor — so the coordinates it is actually
            // rendering never lose float precision, however far the player or the camera travels. Shifts are
            // invisible: RuntimeGrid.ShiftOriginTo suspends CharacterControllers across the move and re-syncs
            // physics, and it does so for the predicted pawn whether or not the pawn is what moved the origin.
            if (Client == null) return;
            var pawn = Client.LocalPlayer;
            var anchor = Client.ActiveContentAnchor;
            if (anchor == null) return;
            // The pawn is asked by entity, not by position: inside a runtime container its cell is the
            // container's, which is the answer that survives a pose that has not been reconciled yet.
            if (pawn != null && anchor == pawn.transform) Grid.KeepOriginNear(pawn, _originRing);
            else Grid.KeepOriginNear(Grid.CoordOf(anchor.position), _originRing);
        }

        /// <summary>
        /// A worker has no pawn of its own to follow, so its origin follows the centroid of the cells it leases —
        /// the same rule <see cref="NebulaWorldStreaming"/> applies to a baked world. Recomputed only when the
        /// lease set changes, which is rare.
        /// </summary>
        private void FollowOwnedCells()
        {
            if (Worker == null || string.IsNullOrEmpty(Worker.WorkerId)) return;
            _owned.Clear();
            var runtime = ContainerRegistry.Runtime;
            for (int i = 0; i < runtime.Count; i++)
            {
                var c = runtime[i];
                if (c != null && c.IsOwnedBy(Worker.WorkerId)) _owned.Add(RuntimeGrid.UnpackId(c.RuntimeId));
            }
            if (_owned.Count == 0) return;
            Grid.KeepOriginNear(NebulaWorldStreaming.Centroid(_owned), _originRing);
        }
    }
}
