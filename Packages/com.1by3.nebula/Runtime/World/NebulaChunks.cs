using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// What a game is told about one chunk of a turnkey chunked world (design <c>docs/interest-management.md</c> §10).
    /// Passed by <c>in</c> reference so a content callback that runs for every chunk entering and leaving a role's
    /// window costs no allocation.
    /// </summary>
    public readonly struct ChunkContext
    {
        /// <summary>Grid coordinate of the chunk. In a planar world y is always 0.</summary>
        public readonly Vector3Int Coord;
        /// <summary>The chunk's stable 64-bit id: <see cref="RuntimeGrid.PackId"/> of <see cref="Coord"/>, and the control-plane lease key.</summary>
        public readonly ulong Id;
        /// <summary>
        /// A deterministic seed for procedural content, derived from <see cref="Id"/> alone: every role, every
        /// process and every run generate the same chunk without replicating a byte of it.
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

        internal ChunkContext(Vector3Int coord, ulong id, Container container, Transform root, NebulaRoles role, bool headless)
        {
            Coord = coord;
            Id = id;
            Seed = NebulaChunks.SeedOf(id);
            Container = container;
            Root = root;
            Role = role;
            IsHeadless = headless;
        }

        /// <summary>Centre of the chunk in the current floating-origin frame.</summary>
        public Vector3 Center => Container != null ? Container.transform.position : Vector3.zero;
    }

    /// <summary>Handler for <see cref="NebulaChunks.Loaded"/> and <see cref="NebulaChunks.Unloading"/>.</summary>
    public delegate void ChunkHandler(in ChunkContext chunk);

    /// <summary>
    /// The content-streaming face of a turnkey chunked world: one place, on every role, where a game is told which
    /// chunks exist. <see cref="NebulaBootstrap"/> activates it from <see cref="NebulaConfig.ChunkedWorld"/> plus a
    /// <see cref="NebulaConfig.RuntimeWorld"/>; a game supplies content and nothing else.
    /// <para>
    /// Chunks are Nebula runtime containers, so "which chunks exist" means something slightly different per role and
    /// that is exactly the point: a worker sees the cells it and its neighbours lease, a client sees the containers
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
        /// <summary>The grid this world is built on, or null when the process is not running a chunked world.</summary>
        public static RuntimeGrid Grid { get; private set; }

        /// <summary>Whether a chunked world is wired up in this process.</summary>
        public static bool IsActive => Grid != null;

        /// <summary>The roles this process runs, as reported to every <see cref="ChunkContext"/>.</summary>
        public static NebulaRoles Role { get; private set; }

        /// <summary>Whether this process draws anything (see <see cref="ChunkContext.IsHeadless"/>).</summary>
        public static bool IsHeadless { get; private set; } = true;

        /// <summary>The worker-side allocator, when this process is a worker; null otherwise.</summary>
        public static RuntimeGridAllocator Allocator { get; private set; }

        /// <summary>Chunks resident in this process right now.</summary>
        public static IReadOnlyList<Container> All => ContainerRegistry.Runtime;

        /// <summary>How many chunks are resident in this process right now.</summary>
        public static int LoadedCount => ContainerRegistry.Runtime.Count;

        private static ChunkHandler _loaded;
        private static ChunkHandler _unloading;
        private static readonly Dictionary<ulong, Transform> Roots = new Dictionary<ulong, Transform>();

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
                    var ctx = ContextFor(c);
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

        /// <summary>The chunk coordinate a position in the current floating-origin frame falls in.</summary>
        public static Vector3Int CoordOf(Vector3 framePosition) => Grid != null ? Grid.CoordOf(framePosition) : Vector3Int.zero;

        /// <summary>The chunk holding <paramref name="framePosition"/> if it is resident in this process, else null.</summary>
        public static Container At(Vector3 framePosition) =>
            Grid == null ? null : ContainerRegistry.GetRuntime(RuntimeGrid.PackId(Grid.CoordOf(framePosition)));

        /// <summary>Whether the chunk holding <paramref name="framePosition"/> is resident in this process.</summary>
        public static bool IsLoadedAt(Vector3 framePosition) => At(framePosition) != null;

        /// <summary>The content root of a chunk (see <see cref="ChunkContext.Root"/>), or null if it is not resident.</summary>
        public static Transform RootOf(ulong id) => Roots.TryGetValue(id, out var t) ? t : null;

        /// <summary>
        /// Make sure the chunk holding <paramref name="framePosition"/> exists, then hand it over. The spawn-point
        /// helper: a game mode that wants to put a player or an object somewhere must not assume a container is
        /// there, and must not poll for one either. On a worker this requests the lease; on any role it waits for
        /// the registration event, and calls back at once when the chunk is already here.
        /// </summary>
        public static void EnsureAt(Vector3 framePosition, Action<Container> onReady)
        {
            if (onReady == null) return;
            if (Grid == null) { onReady(ContainerRegistry.Find(framePosition)); return; }
            EnsureAt(Grid.CoordOf(framePosition), onReady);
        }

        /// <summary><see cref="EnsureAt(Vector3,Action{Container})"/> by chunk coordinate.</summary>
        public static void EnsureAt(Vector3Int coord, Action<Container> onReady)
        {
            if (onReady == null || Grid == null) return;
            coord = Grid.Normalize(coord);
            if (Allocator != null) { Allocator.EnsureContainer(coord, onReady); return; }
            // No allocator (client, gateway): chunks arrive from the control plane, so only wait.
            ulong id = RuntimeGrid.PackId(coord);
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
        /// id, so neighbouring coordinates — whose ids differ in one low bit — produce unrelated seeds. Never
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

        /// <summary>The deterministic per-chunk seed of a coordinate.</summary>
        public static int SeedOf(Vector3Int coord) => SeedOf(RuntimeGrid.PackId(coord));

        /// <summary>
        /// Turn the facade on for this process. Called by <see cref="NebulaBootstrap"/>; a game never calls it.
        /// Idempotent, and safe to call after containers already exist (they are back-filled to the handlers that
        /// are already subscribed).
        /// </summary>
        internal static void Activate(RuntimeGrid grid, NebulaRoles role, bool headless, RuntimeGridAllocator allocator)
        {
            if (grid == null) return;
            if (Grid == null)
            {
                ContainerRegistry.RuntimeRegistered += OnRegistered;
                ContainerRegistry.RuntimeUnregistering += OnUnregistering;
            }
            Grid = grid;
            Role = role;
            IsHeadless = headless;
            Allocator = allocator;
            var snapshot = new List<Container>(ContainerRegistry.Runtime);
            for (int i = 0; i < snapshot.Count; i++) if (snapshot[i] != null) OnRegistered(snapshot[i]);
        }

        private static void OnRegistered(Container c)
        {
            if (c == null || !c.IsRuntime) return;
            var ctx = ContextFor(c);
            _loaded?.Invoke(in ctx);
        }

        private static void OnUnregistering(Container c)
        {
            if (c == null || !c.IsRuntime) return;
            if (_unloading != null)
            {
                var ctx = ContextFor(c);
                _unloading(in ctx);
            }
            Roots.Remove(c.RuntimeId);
        }

        private static ChunkContext ContextFor(Container c)
        {
            if (!Roots.TryGetValue(c.RuntimeId, out var root) || root == null)
            {
                var go = new GameObject("content");
                go.transform.SetParent(c.transform, false);
                root = go.transform;
                Roots[c.RuntimeId] = root;
            }
            return new ChunkContext(RuntimeGrid.UnpackId(c.RuntimeId), c.RuntimeId, c, root, Role, IsHeadless);
        }

        /// <summary>New play session without a domain reload: drop every subscriber and every reference to the previous session's objects.</summary>
        internal static void ResetForNewSession()
        {
            if (Grid != null)
            {
                ContainerRegistry.RuntimeRegistered -= OnRegistered;
                ContainerRegistry.RuntimeUnregistering -= OnUnregistering;
            }
            Grid = null;
            Allocator = null;
            Role = NebulaRoles.None;
            IsHeadless = true;
            _loaded = null;
            _unloading = null;
            Roots.Clear();
        }
    }
}
