using System.Collections.Generic;
using System.Text;
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
    /// The allocator's ring is <see cref="InterestSettings.NearCells"/> plus
    /// <see cref="NebulaConfig.ChunkedWorldServerRingMargin"/> (1 unless set), so a
    /// chunk is always leased — and its content built — before interest can put an entity standing on it into a
    /// client's set. Raising <c>InterestRadius</c> therefore widens the chunk ring by itself; nothing to keep in step.
    /// NearCells alone already holds every chunk interest can reach; the margin is the time to build one (a pawn
    /// crossing a chunk edge requests the new ring at that moment), so a margin of 0 is only as safe as
    /// <see cref="InterestSettings.ChunkLoadLeadSeconds"/> says.
    /// </para>
    /// <para>
    /// Besides its root grid, a scope can hold chunk grids hosted by containers with physics frames of their own
    /// (<see cref="ActivateHostedGrid(IControlPlane, string, string, string, ChunkGridDefinition)"/>,
    /// <c>docs/container-tree.md</c> D22): a planet's ground carried through a system's space, a station's decks.
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
        /// <summary>Scratch for <see cref="FollowOwnedCells"/>: the owned cells of every scoped grid, by grid.</summary>
        private readonly Dictionary<RuntimeGrid, HashSet<Vector3Int>> _ownedByGrid = new Dictionary<RuntimeGrid, HashSet<Vector3Int>>();
        /// <summary>One allocator per scoped grid this worker has joined; the public world's is <see cref="Allocator"/>.</summary>
        private readonly Dictionary<string, RuntimeGridAllocator> _scopedAllocators = new Dictionary<string, RuntimeGridAllocator>(System.StringComparer.Ordinal);
        private bool _leasesDirty;
        private int _originRing = 1;
        private NebulaRoles _roles;
        /// <summary>The hosted-grid settings rows as last applied on this worker, by setting key, so an unchanged row costs nothing.</summary>
        private readonly Dictionary<string, string> _hostedRows = new Dictionary<string, string>(System.StringComparer.Ordinal);
        private readonly List<string> _hostedScratch = new List<string>();
        private float _nextHostedSync;

        /// <summary>The scoped grids' allocators on a worker, by scope key. Empty on every other role.</summary>
        public IReadOnlyDictionary<string, RuntimeGridAllocator> ScopedAllocators => _scopedAllocators;

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
            // The runtime spatial hash wants buckets a small multiple of a chunk; deriving it here is one less
            // number for a game to guess, and a wrong guess only shows up as slow neighbour queries.
            ContainerRegistry.RuntimeBucketSize = Mathf.Max(Grid.CellSize.x, Grid.CellSize.z) * 4f;

            var settings = config.ToInterestSettings();
            int nearCells = settings.NearCells(Mathf.Max(Grid.CellSize.x, Grid.CellSize.z));
            Ring = settings.ChunkRing(Mathf.Max(Grid.CellSize.x, Grid.CellSize.z), config.ChunkedWorldServerRingMargin);
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

            _roles = roles;
            NebulaChunks.Activate(Grid, roles, IsHeadless(roles), Allocator);
            ContainerRegistry.RuntimeRegistered += OnRuntimeRegistered;
            NebulaLog.Info($"chunked world: cell {Grid.CellSize}{(Grid.Planar ? " (planar)" : "")}, near {nearCells} cell(s), allocator ring {Ring} (margin {Ring - nearCells}), retire after {config.ChunkRetireSeconds}s");
        }

        // ---------------------------------------------------------------------------------- scoped grids

        /// <summary>
        /// Bring a second (third, hundredth) chunk grid into being, namespaced by <paramref name="scopeKey"/>:
        /// an instanced open area, a second map, a per-party copy of a procedural region. One control-plane write
        /// (<see cref="IControlPlane.ActivateScope"/>) with <see cref="ScopeKind.Grid"/>; the definition travels in
        /// the scope row's payload, so every role builds the same grid from the row alone and none of them has to
        /// be told separately. Idempotent by key, exactly like every other activation: two callers naming the same
        /// key with the same definition get one world (<c>docs/scope-activation.md</c> §3).
        /// <para>
        /// Only the scope's <see cref="ChunkGridDefinition.Anchor"/> chunk is created — a grid is unbounded, so
        /// activation must not enumerate it. Every other chunk is leased on demand by the allocator of whichever
        /// worker a pawn of that scope is simulated on.
        /// </para>
        /// </summary>
        public static void ActivateGrid(IControlPlane controlPlane, string scopeKey, ChunkGridDefinition definition,
            string requester = "", string preferredWorkerId = "")
        {
            if (controlPlane == null || definition == null) return;
            if (string.IsNullOrEmpty(scopeKey))
            {
                NebulaLog.Warn("chunked world: a scoped grid needs a scope key (the public world's grid comes from NebulaConfig)");
                return;
            }
            controlPlane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = scopeKey,
                Definition = definition.ToScopeDefinition(),
                Requester = string.IsNullOrEmpty(requester) ? "chunked-world" : requester,
                PreferredWorkerId = preferredWorkerId ?? "",
            });
        }

        /// <summary>
        /// Make sure this process has a local grid for a scope whose row the control plane shows, and — on a worker
        /// — an allocator for it. Called for a scope the first time one of its containers turns up here, which is
        /// the one rule that works on every role: a worker gets the anchor dealt to it or an entity transferred
        /// into it, a client is told about a chunk of the scope it joined, a gateway mirrors the lease. A process
        /// that never touches a scope never pays for it.
        /// </summary>
        public RuntimeGrid EnsureLocalGrid(string scopeKey, ChunkGridDefinition definition)
        {
            var existing = NebulaChunks.GridFor(scopeKey);
            if (existing != null || definition == null || string.IsNullOrEmpty(scopeKey)) return existing;
            var grid = RuntimeGrid.From(definition, scopeKey);
            RuntimeGridAllocator allocator = null;
            if (Worker != null)
            {
                allocator = new RuntimeGridAllocator(Worker, grid)
                {
                    Ring = definition.Ring > 0 ? definition.Ring : Ring,
                    RetireAfterSeconds = definition.RetireSeconds > 0f ? definition.RetireSeconds : Mathf.Max(0f, Config.ChunkRetireSeconds),
                    LeadSeconds = Mathf.Max(0f, definition.LeadSeconds),
                };
                // The anchor is the scope's guaranteed spawn area and the container routing a client by key finds.
                // It is pinned rather than anchored: a ring around it on every worker that has ever seen the scope
                // would lease the same chunks everywhere, and a pin is exactly the one box that must not go.
                allocator.AddPin(definition.NormalizedAnchor());
            }
            NebulaChunks.Activate(grid, _roles, IsHeadless(_roles), allocator);
            if (allocator != null) _scopedAllocators[scopeKey] = allocator;
            NebulaLog.Info($"chunked world: grid for scope '{scopeKey}' active (cell {grid.CellSize}{(grid.Planar ? ", planar" : "")})");
            return grid;
        }

        // ---------------------------------------------------------------------------------- hosted grids

        /// <summary>
        /// Host a chunk grid in a container of a scope (<c>docs/container-tree.md</c> D22): chunks of
        /// <paramref name="definition"/>'s size, leased as children of <paramref name="hostContainerId"/> (a container
        /// with a physics frame of its own, usually a carrier's: a planet, a station), laid out in the host's own
        /// coordinates. A planar grid is one layer of columns centred on the host's y = 0, and its host's frame never
        /// shifts its origin vertically.
        /// <para>
        /// The grid is registered under <paramref name="gridKey"/>, its own key, which must not be any scope's: every
        /// role finds it with <see cref="NebulaChunks.GridFor"/>, <see cref="NebulaChunks.EnsureAt(Vector3Int, string, System.Action{Container})"/>
        /// and <see cref="NebulaChunks.AllocatorFor"/> by that key, and its chunk ids are <see cref="ChunkKeys.RuntimeId"/>
        /// of that key and the coordinate. Its chunks belong to <paramref name="scopeKey"/>: what stands in them is in that
        /// scope, and the scope's lifecycle retires them with the rest of it.
        /// </para>
        /// <para>
        /// One control-plane write, a mesh setting row (<see cref="IControlPlane.SetSetting"/>), which every worker
        /// applies: each builds the grid and an allocator that leases chunks around the pawns it simulates on or near the
        /// host (<see cref="ChunkGridDefinition.Reach"/>), ahead of them by <see cref="ChunkGridDefinition.LeadSeconds"/>.
        /// Clients and gateways learn the grid from the chunks' own rows. Calling it again with the same arguments
        /// changes nothing; a carrier that came back under a new container id (a restart) is hosted again by calling it
        /// with the new id, and chunk rows still naming the old one are replaced. Nothing is leased while the host is not
        /// in a worker's process. The anchor is not pinned: pin chunks that must stay with
        /// <see cref="RuntimeGridAllocator.AddPin"/>.
        /// </para>
        /// </summary>
        public static void ActivateHostedGrid(IControlPlane controlPlane, string scopeKey, string gridKey, string hostContainerId, ChunkGridDefinition definition)
        {
            if (controlPlane == null || definition == null) return;
            string problem = HostedGridSetting.Problem(scopeKey, gridKey, hostContainerId, definition);
            if (problem != null) { NebulaLog.Warn($"chunked world: hosted grid '{gridKey}' not activated: {problem}"); return; }
            string key = HostedGridSetting.KeyOf(gridKey);
            string row = HostedGridSetting.Write(scopeKey, hostContainerId, definition);
            if (controlPlane.GetSetting(key) == row) return;
            controlPlane.SetSetting(key, row);
        }

        /// <summary>
        /// The shape of hosted grids (<see cref="IChunkGridGeometry"/>), asked with each grid's key and definition when
        /// this process builds the grid. Null, or a null answer, gives the definition's <see cref="ChunkLattice"/>. Set it
        /// at boot on every process (workers, gateways that build grids, clients) for a grid whose cells are not a box
        /// lattice, such as a cube-sphere's faces.
        /// </summary>
        public static System.Func<string, ChunkGridDefinition, IChunkGridGeometry> HostedGeometry { get; set; }

        /// <summary>
        /// <see cref="ActivateHostedGrid(IControlPlane, string, string, string, ChunkGridDefinition)"/> for a host this
        /// process holds: its container id and its scope are read from it.
        /// </summary>
        public static void ActivateHostedGrid(IControlPlane controlPlane, Container host, string gridKey, ChunkGridDefinition definition)
        {
            if (host == null) return;
            ActivateHostedGrid(controlPlane, host.ScopeKey, gridKey, host.ContainerId, definition);
        }

        /// <summary>
        /// Stop hosting a grid: every worker forgets it and leases nothing more for it. Chunk rows already leased are
        /// not removed; they retire as usual once nobody wants them, or with their scope.
        /// </summary>
        public static void DeactivateHostedGrid(IControlPlane controlPlane, string gridKey)
        {
            if (controlPlane == null || string.IsNullOrEmpty(gridKey)) return;
            string key = HostedGridSetting.KeyOf(gridKey);
            if (string.IsNullOrEmpty(controlPlane.GetSetting(key))) return;
            controlPlane.SetSetting(key, "");
        }

        /// <summary>
        /// Make sure this process has a hosted grid (<see cref="ActivateHostedGrid(IControlPlane, string, string, string, ChunkGridDefinition)"/>)
        /// and, on a worker, its allocator. A worker calls it for every hosted-grid row the control plane shows; a
        /// grid already here with another host or another definition is replaced. Returns the grid.
        /// </summary>
        public RuntimeGrid EnsureHostedGrid(string scopeKey, string gridKey, string hostContainerId, ChunkGridDefinition definition)
        {
            if (definition == null || HostedGridSetting.Problem(scopeKey, gridKey, hostContainerId, definition) != null) return null;
            var existing = NebulaChunks.GridFor(gridKey);
            if (existing != null && existing.IsHosted && existing.HostContainerId == hostContainerId && existing.ScopeKey == scopeKey
                && existing.CellSize == definition.CellSize && (!(existing.Geometry is ChunkLattice) || existing.Planar == definition.Planar))
            {
                var current = NebulaChunks.AllocatorFor(gridKey);
                if (current != null) Configure(current, definition);
                return existing;
            }
            if (existing != null && !existing.IsHosted)
            {
                NebulaLog.Warn($"chunked world: '{gridKey}' is a scope's grid here; a hosted grid needs a key of its own");
                return null;
            }
            if (existing != null) DeactivateHosted(gridKey);
            var grid = RuntimeGrid.Hosted(definition, scopeKey, gridKey, hostContainerId, HostedGeometry?.Invoke(gridKey, definition));
            RuntimeGridAllocator allocator = null;
            if (Worker != null) Configure(allocator = new RuntimeGridAllocator(Worker, grid), definition);
            NebulaChunks.Activate(grid, _roles, IsHeadless(_roles), allocator);
            if (allocator != null) _scopedAllocators[gridKey] = allocator;
            NebulaLog.Info($"chunked world: grid '{gridKey}' hosted by {hostContainerId} in scope '{scopeKey}' (cell {grid.CellSize}{(grid.Planar ? ", planar" : "")})");
            return grid;
        }

        private void Configure(RuntimeGridAllocator allocator, ChunkGridDefinition definition)
        {
            allocator.Ring = definition.Ring > 0 ? definition.Ring : Ring;
            allocator.RetireAfterSeconds = definition.RetireSeconds > 0f ? definition.RetireSeconds : Mathf.Max(0f, Config != null ? Config.ChunkRetireSeconds : 60f);
            allocator.LeadSeconds = Mathf.Max(0f, definition.LeadSeconds);
            allocator.Reach = Mathf.Max(0f, definition.Reach);
        }

        private void DeactivateHosted(string gridKey)
        {
            _scopedAllocators.Remove(gridKey);
            NebulaChunks.Deactivate(gridKey);
        }

        /// <summary>
        /// A worker applies the hosted-grid rows of the control plane's settings: a grid for every row, none for a row
        /// that was cleared. Only rows that changed since the last pass do any work.
        /// </summary>
        private void SyncHostedGrids()
        {
            var plane = Worker != null ? Worker.ControlPlane : null;
            var settings = plane != null && plane.IsConnected ? plane.Settings : null;
            if (settings == null) return;
            foreach (var pair in settings)
            {
                if (!HostedGridSetting.IsKey(pair.Key)) continue;
                if (_hostedRows.TryGetValue(pair.Key, out var applied) && applied == pair.Value) continue;
                _hostedRows[pair.Key] = pair.Value;
                string gridKey = HostedGridSetting.GridKeyOf(pair.Key);
                if (HostedGridSetting.TryRead(pair.Value, out var scopeKey, out var host, out var definition)) EnsureHostedGrid(scopeKey, gridKey, host, definition);
                else if (NebulaChunks.GridFor(gridKey)?.IsHosted == true) DeactivateHosted(gridKey);
            }
            _hostedScratch.Clear();
            foreach (var key in _hostedRows.Keys) if (!settings.ContainsKey(key)) _hostedScratch.Add(key);
            foreach (var key in _hostedScratch)
            {
                _hostedRows.Remove(key);
                string gridKey = HostedGridSetting.GridKeyOf(key);
                if (NebulaChunks.GridFor(gridKey)?.IsHosted == true) DeactivateHosted(gridKey);
            }
            _hostedScratch.Clear();
        }

        /// <summary>
        /// A container of a scope turned up here: that scope's grid is this process's business now. The definition
        /// comes from the scope row when this role has a control plane (a worker, a gateway, the orchestrator) and
        /// is otherwise <see cref="ChunkGridDefinition.Infer"/>red from the chunk itself — a client has no control
        /// plane, only the container rows the gateway sent it, and a chunk's box and coordinate say exactly what
        /// grid it is a cell of. A chunk of a hosted grid names its grid in its part id, and its parent is the host.
        /// </summary>
        private void OnRuntimeRegistered(Container container)
        {
            if (container == null || container.InstanceId == 0) return;
            if (ChunkKeys.TryParseHostedPartId(container.Instance?.PartId, out var gridKey, out var hostedCoord))
            {
                OnHostedChunkRegistered(container, gridKey, hostedCoord);
                return;
            }
            string scopeKey = container.Instance?.ScopeKey;
            if (string.IsNullOrEmpty(scopeKey) || NebulaChunks.IsActiveFor(scopeKey)) return;
            if (!ChunkKeys.TryParsePartId(container.Instance?.PartId, out var coord)) return; // an instance's part, not a chunk
            var plane = Worker != null ? Worker.ControlPlane : null;
            var definition = ChunkGridDefinition.Of(plane != null ? plane.FindScope(scopeKey) : null)
                ?? ChunkGridDefinition.Infer(coord, ContainerRegistry.ToAbsolute(container.WorldBounds));
            if (definition != null) EnsureLocalGrid(scopeKey, definition);
        }

        private void OnHostedChunkRegistered(Container container, string gridKey, Vector3Int coord)
        {
            if (NebulaChunks.IsActiveFor(gridKey)) return;
            string scopeKey = container.Instance?.ScopeKey;
            var host = container.Parent;
            if (string.IsNullOrEmpty(scopeKey) || host == null) return;
            // A worker reads the definition from its row; anything else (and a worker that has not seen the row yet)
            // infers it from the chunk: the box is the cell, and a column is the one centred on its host's y = 0.
            var plane = Worker != null ? Worker.ControlPlane : null;
            if (plane != null && HostedGridSetting.TryRead(plane.GetSetting(HostedGridSetting.KeyOf(gridKey)), out var rowScope, out var rowHost, out var rowDefinition)
                && rowHost == host.ContainerId)
            {
                EnsureHostedGrid(rowScope, gridKey, rowHost, rowDefinition);
                return;
            }
            var local = container.transform.localPosition;
            EnsureHostedGrid(scopeKey, gridKey, host.ContainerId, new ChunkGridDefinition
            {
                CellSize = container.Size,
                Planar = local.y == 0f,
                Anchor = coord,
            });
        }

        /// <summary>Whether this process draws anything: workers and services never do, and a batchmode client (a bot) does not either.</summary>
        private static bool IsHeadless(NebulaRoles roles) =>
            (roles & NebulaRoles.Client) == 0 || Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        private void OnDestroy()
        {
            ContainerRegistry.LeasesChanged -= OnLeasesChanged;
            ContainerRegistry.RuntimeRegistered -= OnRuntimeRegistered;
        }

        private void OnLeasesChanged() => _leasesDirty = true;

        private void Update()
        {
            if (Grid == null) return;
            // Every scoped grid's allocator runs on the same clock as the public one: each keeps the ring around
            // the pawns of its own scope and retires only its own chunks.
            if (Worker != null && Time.unscaledTime >= _nextHostedSync)
            {
                _nextHostedSync = Time.unscaledTime + 0.5f;
                SyncHostedGrids();
            }
            if (_scopedAllocators.Count > 0)
                foreach (var allocator in _scopedAllocators.Values) allocator.Tick(Time.unscaledTime);
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
            // A client is in exactly one scope at a time, so it keeps exactly one origin — the frame of the scope
            // its pawn stands in, which for an unscoped game is the public world's and is the behaviour this had
            // before per-scope frames existed (docs/scope-frames.md D6).
            var grid = OriginGridOf(pawn, Grid);
            // The pawn is asked by entity, not by position: inside a runtime container its cell is the
            // container's, which is the answer that survives a pose that has not been reconciled yet.
            if (pawn != null && anchor == pawn.transform) grid.KeepOriginNear(pawn, _originRing);
            else grid.KeepOriginNear(grid.CoordOf(anchor.position), _originRing);
        }

        /// <summary>
        /// The grid whose origin a client follows: the grid of the chunk its pawn stands in, or
        /// <paramref name="fallback"/> (the public grid) when there is none. A pawn aboard a carrier stands in the chunk
        /// its carrier chain ends in, so a pilot keeps the origin of the world the ship flies over. A chunk of a hosted
        /// grid has no origin of its own (its host's frame does), so a pawn standing in one keeps the origin of its
        /// scope's root grid, where the host itself is.
        /// </summary>
        internal static RuntimeGrid OriginGridOf(NetworkIdentity pawn, RuntimeGrid fallback)
        {
            var grid = pawn != null ? NebulaChunks.GridHolding(pawn.Container) : null;
            if (grid != null && grid.IsHosted) grid = NebulaChunks.GridFor(grid.ScopeKey);
            return grid ?? fallback;
        }

        /// <summary>
        /// A worker has no pawn of its own to follow, so each scope's origin follows the centroid of the cells of
        /// <b>that scope</b> this worker leases — the same rule <see cref="NebulaWorldStreaming"/> applies to a
        /// baked world, once per origin frame. A worker holding two scopes keeps both precision-safe at the same
        /// time, and neither shift disturbs the other (<c>docs/scope-frames.md</c> D3). Recomputed only when the
        /// lease set changes, which is rare.
        /// </summary>
        private void FollowOwnedCells()
        {
            if (Worker == null || string.IsNullOrEmpty(Worker.WorkerId)) return;
            foreach (var cells in _ownedByGrid.Values) cells.Clear();
            _owned.Clear();
            var runtime = ContainerRegistry.Runtime;
            for (int i = 0; i < runtime.Count; i++)
            {
                var c = runtime[i];
                if (c == null || !c.IsOwnedBy(Worker.WorkerId)) continue;
                // Each cell counts towards its own grid's centroid. A runtime container that is not a chunk of any
                // grid this process has joined belongs to no frame here and is left out of every centroid.
                var grid = NebulaChunks.GridOf(c);
                // A hosted grid's origin is its host frame's, which the worker shifts itself (docs/container-tree.md D19).
                if (grid == null || grid.IsHosted || !grid.TryCoordOf(c.RuntimeId, out var cell)) continue;
                if (grid == Grid) { _owned.Add(cell); continue; }
                if (!_ownedByGrid.TryGetValue(grid, out var set)) _ownedByGrid[grid] = set = new HashSet<Vector3Int>();
                set.Add(cell);
            }
            if (_owned.Count > 0) Grid.KeepOriginNear(NebulaWorldStreaming.Centroid(_owned), _originRing);
            foreach (var pair in _ownedByGrid)
                if (pair.Value.Count > 0) pair.Key.KeepOriginNear(NebulaWorldStreaming.Centroid(pair.Value), _originRing);
        }
    }

    /// <summary>
    /// The mesh setting row a hosted grid travels in (<see cref="NebulaChunkedWorld.ActivateHostedGrid(IControlPlane, string, string, string, ChunkGridDefinition)"/>):
    /// key <c>nebula.hostedGrid/&lt;gridKey&gt;</c>, value <c>{"scope":…,"host":…,"grid":{…}}</c> with the grid as
    /// <see cref="ChunkGridDefinition.ToJson"/> writes it. An empty value is a grid no longer hosted.
    /// </summary>
    internal static class HostedGridSetting
    {
        public const string Prefix = "nebula.hostedGrid/";

        public static string KeyOf(string gridKey) => Prefix + gridKey;
        public static bool IsKey(string key) => key != null && key.Length > Prefix.Length && key.StartsWith(Prefix, System.StringComparison.Ordinal);
        public static string GridKeyOf(string key) => key.Substring(Prefix.Length);

        public static string Write(string scopeKey, string hostContainerId, ChunkGridDefinition definition)
        {
            var sb = new StringBuilder(256);
            var w = new JsonWriter(sb);
            w.BeginObject();
            w.Prop("scope", scopeKey);
            w.Prop("host", hostContainerId);
            w.Key("grid");
            w.Raw(definition.ToJson());
            w.EndObject();
            return sb.ToString();
        }

        public static bool TryRead(string value, out string scopeKey, out string hostContainerId, out ChunkGridDefinition definition)
        {
            scopeKey = hostContainerId = null;
            definition = null;
            if (string.IsNullOrEmpty(value) || !PersistenceJson.TryParseObject(value, out var o, out _)) return false;
            scopeKey = ControlPlaneJson.Str(o, "scope");
            hostContainerId = ControlPlaneJson.Str(o, "host");
            definition = o.TryGetValue("grid", out var g) ? ChunkGridDefinition.FromObject(g as Dictionary<string, object>) : null;
            return !string.IsNullOrEmpty(scopeKey) && !string.IsNullOrEmpty(hostContainerId) && definition != null;
        }

        /// <summary>Why a hosted grid cannot be activated as asked, or null.</summary>
        public static string Problem(string scopeKey, string gridKey, string hostContainerId, ChunkGridDefinition definition)
        {
            if (string.IsNullOrEmpty(scopeKey)) return "a hosted grid lives in a scope; the public world hosts none";
            if (string.IsNullOrEmpty(gridKey)) return "a hosted grid needs a key of its own";
            if (gridKey == scopeKey) return "a hosted grid's key cannot be its scope's";
            if (string.IsNullOrEmpty(hostContainerId)) return "a hosted grid needs the id of its host container";
            return definition?.Validate();
        }
    }
}
