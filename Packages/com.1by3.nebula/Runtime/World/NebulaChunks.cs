using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// What a game is told about one chunk of a chunked world.
    /// Passed by <c>in</c> reference so a content callback that runs for every chunk entering and leaving a role's
    /// window costs no allocation.
    /// </summary>
    public readonly struct ChunkContext
    {
        /// <summary>Grid coordinate of the chunk. In a planar world y is always 0.</summary>
        public readonly Vector3Int Coord;
        /// <summary>The chunk's stable 64-bit id (<see cref="RuntimeGrid.IdOf"/>), and the control-plane lease key.</summary>
        public readonly ulong Id;
        /// <summary>
        /// A deterministic seed for procedural content, derived from <see cref="Id"/> alone: every role, every
        /// process and every run generate the same chunk without replicating a byte of it. Two scopes' chunks at
        /// the same coordinate have different ids, so they also have different seeds — a scoped copy of a world is
        /// a different world, not the same one twice.
        /// </summary>
        public readonly int Seed;
        /// <summary>The Nebula runtime container this chunk is. Entities are spawned into it; it is leased by one worker.</summary>
        public readonly Container Container;
        /// <summary>
        /// Where the game parents its content. A child of <see cref="Container"/>, destroyed with the chunk, so a
        /// game that only parents under it never has to clean up in <see cref="NebulaChunks.Unloading"/>. It also
        /// keeps content out of <c>Container.Entities</c>' way: Nebula detaches networked objects when a chunk
        /// retires, and unnetworked content should simply go with it.
        /// </summary>
        public readonly Transform Root;
        /// <summary>The roles this process runs. Content that only matters to a viewer checks <see cref="IsHeadless"/> instead.</summary>
        public readonly NebulaRoles Role;
        /// <summary>No one is looking: strip renderers and keep colliders. True on workers and on batchmode clients (bots).</summary>
        public readonly bool IsHeadless;
        /// <summary>
        /// The scope this chunk belongs to: <c>""</c> for the public world, otherwise the key the grid was
        /// activated under. Content that is per-world (a seed offset, a biome table) keys off this.
        /// </summary>
        public readonly string ScopeKey;
        /// <summary>The grid this chunk is a cell of.</summary>
        public readonly RuntimeGrid Grid;

        internal ChunkContext(Vector3Int coord, ulong id, Container container, Transform root, NebulaRoles role, bool headless, RuntimeGrid grid)
        {
            Coord = coord;
            Id = id;
            Seed = NebulaChunks.SeedOf(id);
            Container = container;
            Root = root;
            Role = role;
            IsHeadless = headless;
            Grid = grid;
            ScopeKey = grid != null ? grid.ScopeKey : "";
        }

        /// <summary>Centre of the chunk in the current floating-origin frame.</summary>
        public Vector3 Center => Container != null ? Container.transform.position : Vector3.zero;
    }

    /// <summary>Handler for <see cref="NebulaChunks.Loaded"/> and <see cref="NebulaChunks.Unloading"/>.</summary>
    public delegate void ChunkHandler(in ChunkContext chunk);

    /// <summary>
    /// Reports the runtime chunks available on each role so your game can load their content.
    /// <see cref="NebulaBootstrap"/> activates the public world's grid when
    /// <see cref="NebulaConfig.ChunkedWorld"/> and <see cref="NebulaConfig.RuntimeWorld"/> are configured.
    /// <para>
    /// Each scope, a separately identified simulation area, has its own grid. <see cref="Grid"/> returns
    /// the public world's grid. Activate additional grids with <see cref="NebulaChunkedWorld.ActivateGrid"/>
    /// and retrieve them with <see cref="GridFor"/>. Each grid has its own cell size, planar setting,
    /// allocator, container assignments, and persistence identifiers.
    /// </para>
    /// <para>
    /// Chunks are Nebula runtime containers, so "which chunks exist" means something slightly different per role and
    /// that is exactly the point: a worker sees the cells it and its neighbors lease, a client sees the containers
    /// the gateway told it about (its interest window), and both learn about them through the same two events. A
    /// game's callback therefore never needs to know which role it is running on, nor to poll.
    /// </para>
    /// <para>
    /// <see cref="Loaded"/> back-fills: subscribing after chunks already exist raises the event for each of them at
    /// once, so a handler added from <c>Start</c> on a late-loaded scene object cannot miss a chunk and no game has
    /// to write the "and now walk the existing ones" loop that every hand-rolled chunk loader begins with.
    /// </para>
    /// </summary>
    public static class NebulaChunks
    {
        private static readonly Dictionary<string, RuntimeGrid> GridsByScope = new Dictionary<string, RuntimeGrid>(StringComparer.Ordinal);
        private static readonly Dictionary<ulong, RuntimeGrid> GridsByInstance = new Dictionary<ulong, RuntimeGrid>();
        private static readonly Dictionary<string, RuntimeGridAllocator> AllocatorsByScope = new Dictionary<string, RuntimeGridAllocator>(StringComparer.Ordinal);
        private static bool _hooked;

        /// <summary>The public world's grid, or null when this process runs no public chunked world.</summary>
        public static RuntimeGrid Grid { get; private set; }

        /// <summary>Whether any chunked world is wired up in this process.</summary>
        public static bool IsActive => GridsByScope.Count > 0;

        /// <summary>The roles this process runs, as reported to every <see cref="ChunkContext"/>.</summary>
        public static NebulaRoles Role { get; private set; }

        /// <summary>Whether this process draws anything (see <see cref="ChunkContext.IsHeadless"/>).</summary>
        public static bool IsHeadless { get; private set; } = true;

        /// <summary>The public world's worker-side allocator, when this process is a worker; null otherwise.</summary>
        public static RuntimeGridAllocator Allocator { get; private set; }

        /// <summary>Every grid active in this process, public world first if it has one.</summary>
        public static IEnumerable<RuntimeGrid> Grids => GridsByScope.Values;

        /// <summary>The scope keys with a grid in this process (<c>""</c> is the public world).</summary>
        public static IEnumerable<string> ActiveScopeKeys => GridsByScope.Keys;

        /// <summary>The grid of a scope (<c>""</c> for the public world), or null when that scope has no grid here.</summary>
        public static RuntimeGrid GridFor(string scopeKey) =>
            GridsByScope.TryGetValue(scopeKey ?? "", out var g) ? g : null;

        /// <summary>The worker-side allocator of a scope's grid, or null (not a worker, or no such grid).</summary>
        public static RuntimeGridAllocator AllocatorFor(string scopeKey) =>
            AllocatorsByScope.TryGetValue(scopeKey ?? "", out var a) ? a : null;

        /// <summary>Whether a scope has a grid in this process.</summary>
        public static bool IsActiveFor(string scopeKey) => GridsByScope.ContainsKey(scopeKey ?? "");

        /// <summary>Chunks resident in this process right now, across every scope.</summary>
        public static IReadOnlyList<Container> All => ContainerRegistry.Runtime;

        /// <summary>How many chunks are resident in this process right now, across every scope.</summary>
        public static int LoadedCount => ContainerRegistry.Runtime.Count;

        private static ChunkHandler _loaded;
        private static ChunkHandler _unloading;
        private static readonly Dictionary<ulong, Transform> Roots = new Dictionary<ulong, Transform>();

        // -------------------------------------------------------------------------------- which scope is this?

        /// <summary>
        /// The grid a container belongs to, or null when it is not a chunk of any grid here. The central
        /// "which scope does this container belong to" query: a dictionary lookup on the container's isolation id,
        /// which every role already carries on the container (<see cref="Container.InstanceId"/>).
        /// </summary>
        public static RuntimeGrid GridOf(Container container)
        {
            if (container == null || !container.IsRuntime) return null;
            if (!GridsByInstance.TryGetValue(container.InstanceId, out var grid)) return null;
            return grid.TryCoordOf(container.RuntimeId, out _) || Adopt(grid, container) ? grid : null;
        }

        /// <summary>The scope key a container belongs to (<c>""</c> for the public world and for anything not in a grid).</summary>
        public static string ScopeOf(Container container) => GridOf(container)?.ScopeKey ?? "";

        /// <summary>
        /// The grid that named a chunk id, or null when no grid here has placed it. Scoped grids are asked first
        /// and answer only for ids they actually named; the public grid is asked last because every 64-bit value
        /// unpacks to one of its coordinates.
        /// </summary>
        public static RuntimeGrid GridOf(ulong id)
        {
            foreach (var grid in GridsByScope.Values)
                if (!grid.IsPublic && grid.TryCoordOf(id, out _)) return grid;
            return Grid != null ? Grid : null;
        }

        private static bool Adopt(RuntimeGrid grid, Container container) =>
            grid.Adopt(container.RuntimeId, container.Instance?.PartId, out _);

        /// <summary>
        /// The bounds hook (<see cref="ContainerRegistry.RuntimeBoundsInFrame"/>) for every grid at once: the box a
        /// chunk id has in this process's frame, whichever scope's grid named it, and the caller's own box for an
        /// id no grid here knows. Installed by <see cref="Activate"/>; a game with one grid and no scopes can keep
        /// using <see cref="RuntimeGrid.UseAsRuntimeBounds"/> instead.
        /// </summary>
        public static Bounds BoundsOfId(ulong id, Bounds fallback)
        {
            var grid = GridOf(id);
            return grid != null ? grid.BoundsOfId(id, fallback) : fallback;
        }

        /// <summary>
        /// A chunk now exists in this process: build its content. Subscribing back-fills every chunk that is
        /// already here, so subscription order never decides what a game sees.
        /// </summary>
        public static event ChunkHandler Loaded
        {
            add
            {
                if (value == null) return;
                _loaded += value;
                if (!IsActive) return;
                // Copy first: a handler that requests or releases containers — or subscribes another handler —
                // would otherwise mutate the list we are walking. Subscribing is rare, so a list per call is the
                // right trade against a shared buffer that re-entrancy could clobber.
                var snapshot = new List<Container>(ContainerRegistry.Runtime);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    var c = snapshot[i];
                    if (c == null) continue;
                    var grid = GridOf(c);
                    if (grid == null) continue;
                    var ctx = ContextFor(c, grid);
                    value(in ctx);
                }
            }
            remove => _loaded -= value;
        }

        /// <summary>
        /// A chunk is about to go: its container object, and everything under <see cref="ChunkContext.Root"/>, is
        /// destroyed right after. Only a game holding references outside the chunk has anything to do here.
        /// </summary>
        public static event ChunkHandler Unloading
        {
            add { if (value != null) _unloading += value; }
            remove => _unloading -= value;
        }

        /// <summary>The chunk coordinate a position in the current floating-origin frame falls in, in the public world.</summary>
        public static Vector3Int CoordOf(Vector3 framePosition) => CoordOf(framePosition, "");

        /// <summary>The chunk coordinate a position falls in, in a given scope's grid.</summary>
        public static Vector3Int CoordOf(Vector3 framePosition, string scopeKey)
        {
            var grid = GridFor(scopeKey);
            return grid != null ? grid.CoordOf(framePosition) : Vector3Int.zero;
        }

        /// <summary>The public-world chunk holding <paramref name="framePosition"/> if it is resident in this process, else null.</summary>
        public static Container At(Vector3 framePosition) => At(framePosition, "");

        /// <summary>The chunk of <paramref name="scopeKey"/>'s grid holding <paramref name="framePosition"/>, or null.</summary>
        public static Container At(Vector3 framePosition, string scopeKey)
        {
            var grid = GridFor(scopeKey);
            return grid == null ? null : ContainerRegistry.GetRuntime(grid.IdOf(grid.CoordOf(framePosition)));
        }

        /// <summary>Whether the public-world chunk holding <paramref name="framePosition"/> is resident in this process.</summary>
        public static bool IsLoadedAt(Vector3 framePosition) => At(framePosition) != null;

        /// <summary>The content root of a chunk (see <see cref="ChunkContext.Root"/>), or null if it is not resident.</summary>
        public static Transform RootOf(ulong id) => Roots.TryGetValue(id, out var t) ? t : null;

        /// <summary>
        /// Make sure the chunk holding <paramref name="framePosition"/> exists, then hand it over. The spawn-point
        /// helper: a game mode that wants to put a player or an object somewhere must not assume a container is
        /// there, and must not poll for one either. On a worker this requests the lease; on any role it waits for
        /// the registration event, and calls back at once when the chunk is already here.
        /// </summary>
        public static void EnsureAt(Vector3 framePosition, Action<Container> onReady) => EnsureAt(framePosition, "", onReady);

        /// <summary><see cref="EnsureAt(Vector3,Action{Container})"/> in a given scope's grid.</summary>
        public static void EnsureAt(Vector3 framePosition, string scopeKey, Action<Container> onReady)
        {
            if (onReady == null) return;
            var grid = GridFor(scopeKey);
            if (grid == null) { if (string.IsNullOrEmpty(scopeKey)) onReady(ContainerRegistry.Find(framePosition)); return; }
            EnsureAt(grid.CoordOf(framePosition), scopeKey, onReady);
        }

        /// <summary><see cref="EnsureAt(Vector3,Action{Container})"/> by chunk coordinate, in the public world.</summary>
        public static void EnsureAt(Vector3Int coord, Action<Container> onReady) => EnsureAt(coord, "", onReady);

        /// <summary><see cref="EnsureAt(Vector3,Action{Container})"/> by chunk coordinate, in a given scope's grid.</summary>
        public static void EnsureAt(Vector3Int coord, string scopeKey, Action<Container> onReady)
        {
            if (onReady == null) return;
            var grid = GridFor(scopeKey);
            if (grid == null) return;
            coord = grid.Normalize(coord);
            var allocator = AllocatorFor(scopeKey);
            if (allocator != null) { allocator.EnsureContainer(coord, onReady); return; }
            // No allocator (client, gateway): chunks arrive from the control plane, so only wait.
            ulong id = grid.IdOf(coord);
            var existing = ContainerRegistry.GetRuntime(id);
            if (existing != null) { onReady(existing); return; }
            Action<Container> handler = null;
            handler = c =>
            {
                if (c.RuntimeId != id) return;
                ContainerRegistry.RuntimeRegistered -= handler;
                onReady(c);
            };
            ContainerRegistry.RuntimeRegistered += handler;
        }

        /// <summary>
        /// The deterministic per-chunk seed (<see cref="ChunkContext.Seed"/>). A 64-bit finalizer mix of the packed
        /// id, so neighboring coordinates — whose ids differ in one low bit — produce unrelated seeds. Never
        /// zero-sensitive: chunk (0,0,0) gets a seed like any other.
        /// </summary>
        public static int SeedOf(ulong id)
        {
            // splitmix64's finalizer: cheap, and well distributed for the sequential-ish ids a grid produces.
            ulong z = id + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)(z ^ (z >> 32));
        }

        /// <summary>The deterministic per-chunk seed of a public-world coordinate.</summary>
        public static int SeedOf(Vector3Int coord) => SeedOf(RuntimeGrid.PackId(coord));

        /// <summary>The deterministic per-chunk seed of a coordinate in a scope's grid.</summary>
        public static int SeedOf(Vector3Int coord, string scopeKey)
        {
            var grid = GridFor(scopeKey);
            return grid != null ? SeedOf(grid.IdOf(coord)) : SeedOf(ChunkKeys.RuntimeId(scopeKey ?? "", coord));
        }

        /// <summary>
        /// Turn a scope's grid on for this process. Called by <see cref="NebulaChunkedWorld"/>; a game never calls
        /// it. Idempotent per scope, and safe to call after containers already exist (they are back-filled to the
        /// handlers that are already subscribed). Activating a second grid never disturbs the first.
        /// </summary>
        internal static void Activate(RuntimeGrid grid, NebulaRoles role, bool headless, RuntimeGridAllocator allocator)
        {
            if (grid == null) return;
            if (!_hooked)
            {
                ContainerRegistry.RuntimeRegistered += OnRegistered;
                ContainerRegistry.RuntimeUnregistering += OnUnregistering;
                _hooked = true;
            }
            string scope = grid.ScopeKey;
            bool newlyActive = !GridsByScope.ContainsKey(scope);
            GridsByScope[scope] = grid;
            GridsByInstance[grid.InstanceId] = grid;
            // A scoped grid owns its floating origin from the moment it exists here: registering the frame up
            // front is what keeps its containers out of the public world's origin shifts (docs/scope-frames.md D2).
            _ = grid.Frame;
            if (grid.IsPublic) { Grid = grid; Allocator = allocator; }
            if (allocator != null) AllocatorsByScope[scope] = allocator;
            else AllocatorsByScope.Remove(scope);
            Role = role;
            IsHeadless = headless;
            // Every grid resolves its own ids; one hook for all of them, so a second scope cannot take the first's.
            ContainerRegistry.RuntimeBoundsInFrame = BoundsOfId;
            // Containers of this scope that turned up before the grid did were placed in the public frame, because
            // nothing here could say otherwise yet. A zero-delta shift of the new frame re-asks the bounds hook for
            // every one of them, so each is recomputed from its coordinate in its own frame (docs/scope-frames.md D5).
            if (!grid.IsPublic) ContainerRegistry.ShiftRuntime(grid.InstanceId, Vector3.zero);
            if (!newlyActive) return;
            var snapshot = new List<Container>(ContainerRegistry.Runtime);
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i] != null && GridOf(snapshot[i]) == grid) OnRegistered(snapshot[i]);
        }

        /// <summary>
        /// Forget a scope's grid here (its scope was retired, or this role left it). Leases and containers are the
        /// control plane's; this only stops reporting its chunks as content.
        /// </summary>
        internal static void Deactivate(string scopeKey)
        {
            var grid = GridFor(scopeKey);
            if (grid == null) return;
            GridsByScope.Remove(grid.ScopeKey);
            GridsByInstance.Remove(grid.InstanceId);
            AllocatorsByScope.Remove(grid.ScopeKey);
            ScopeFrames.Remove(grid.ScopeKey);
            if (grid.IsPublic) { Grid = null; Allocator = null; }
        }

        private static void OnRegistered(Container c)
        {
            if (c == null || !c.IsRuntime) return;
            var grid = GridOf(c);
            if (grid == null) return;
            var ctx = ContextFor(c, grid);
            _loaded?.Invoke(in ctx);
        }

        private static void OnUnregistering(Container c)
        {
            if (c == null || !c.IsRuntime) return;
            var grid = GridOf(c);
            if (grid != null && _unloading != null)
            {
                var ctx = ContextFor(c, grid);
                _unloading(in ctx);
            }
            Roots.Remove(c.RuntimeId);
        }

        private static ChunkContext ContextFor(Container c, RuntimeGrid grid)
        {
            if (!Roots.TryGetValue(c.RuntimeId, out var root) || root == null)
            {
                var go = new GameObject("content");
                go.transform.SetParent(c.transform, false);
                root = go.transform;
                Roots[c.RuntimeId] = root;
            }
            grid.TryCoordOf(c.RuntimeId, out var coord);
            return new ChunkContext(coord, c.RuntimeId, c, root, Role, IsHeadless, grid);
        }

        /// <summary>New play session without a domain reload: drop every subscriber and every reference to the previous session's objects.</summary>
        internal static void ResetForNewSession()
        {
            if (_hooked)
            {
                ContainerRegistry.RuntimeRegistered -= OnRegistered;
                ContainerRegistry.RuntimeUnregistering -= OnUnregistering;
                _hooked = false;
            }
            Grid = null;
            Allocator = null;
            GridsByScope.Clear();
            GridsByInstance.Clear();
            AllocatorsByScope.Clear();
            Role = NebulaRoles.None;
            IsHeadless = true;
            _loaded = null;
            _unloading = null;
            Roots.Clear();
        }
    }
}
