using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// Opt-in worker-side allocator for a game built on <see cref="RuntimeGrid"/>: keeps a ring of cells requested
    /// around every player-owned authoritative entity (and any fixed anchor coordinates, e.g. the origin),
    /// re-touches them every tick so a neighbor's owner does not retire them, and retires cells this worker owns
    /// once nobody has wanted them for a while and nothing occupies them. This packages the allocation policy that
    /// procedural-world games otherwise implement themselves; a game adopting <see cref="RuntimeGrid"/> can use it
    /// instead of writing the same logic again.
    /// <para>
    /// With <c>NebulaConfig.ChunkedWorld</c> enabled, <see cref="NebulaChunkedWorld"/> constructs and updates
    /// the allocator. For a custom allocation workflow, construct this class and call <see cref="Tick"/>
    /// from your worker's update loop.
    /// </para>
    /// </summary>
    public sealed class RuntimeGridAllocator
    {
        private readonly NebulaWorker worker;
        private readonly RuntimeGrid grid;
        private readonly List<Vector3Int> anchors = new List<Vector3Int>();
        private readonly HashSet<ulong> wanted = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> lastWanted = new Dictionary<ulong, float>();
        private readonly List<ulong> scratch = new List<ulong>();
        private readonly List<Vector3Int> ring = new List<Vector3Int>();
        private float next;

        /// <summary>Ring radius (Chebyshev distance in cells) requested around every anchor and owned entity.</summary>
        public int Ring { get; set; } = 1;
        /// <summary>How long an owned, unwanted, unoccupied cell sits idle before it is released.</summary>
        public float RetireAfterSeconds { get; set; } = 60f;
        /// <summary>How often <see cref="Tick"/> actually recomputes interest; calls between are no-ops.</summary>
        public float TickIntervalSeconds { get; set; } = 0.25f;

        public RuntimeGridAllocator(NebulaWorker worker, RuntimeGrid grid)
        {
            this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
            this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
        }

        /// <summary>The grid this allocator works in.</summary>
        public RuntimeGrid Grid => grid;

        /// <summary>Fixed coordinates kept wanted regardless of where entities are, e.g. the origin cell(s).</summary>
        public void AddAnchor(Vector3Int coord)
        {
            coord = grid.Normalize(coord);
            if (!anchors.Contains(coord)) anchors.Add(coord);
        }
        public void RemoveAnchor(Vector3Int coord) => anchors.Remove(grid.Normalize(coord));
        public void ClearAnchors() => anchors.Clear();

        /// <summary>The interest set as of the last <see cref="Tick"/>: every cell id currently requested.</summary>
        public IReadOnlyCollection<ulong> WantedIds => wanted;
        public bool IsWanted(Vector3Int coord) => wanted.Contains(RuntimeGrid.PackId(coord));
        public bool IsWanted(ulong id) => wanted.Contains(id);

        /// <summary>
        /// Recompute interest and touch/retire containers accordingly. Cheap to call every frame: the actual work
        /// only runs every <see cref="TickIntervalSeconds"/>. <paramref name="unscaledTime"/> is the caller's own
        /// clock (e.g. <c>Time.unscaledTime</c>); passed in rather than read here so this stays testable without
        /// a running player loop.
        /// </summary>
        public void Tick(float unscaledTime)
        {
            if (!worker.IsRegistered || unscaledTime < next) return;
            next = unscaledTime + TickIntervalSeconds;

            wanted.Clear();
            foreach (var a in anchors) AddRing(a);
            foreach (var entity in worker.Authoritative)
                if (entity != null && entity.OwnerClientId != 0) AddRing(grid.CoordOf(entity));

            foreach (var id in wanted)
            {
                lastWanted[id] = unscaledTime;
                // Touch existing foreign leases too: their owner must not retire our neighbours.
                worker.RequestRuntimeContainer(id, grid.BoundsOf(RuntimeGrid.UnpackId(id)));
            }

            scratch.Clear();
            foreach (var c in ContainerRegistry.Runtime)
            {
                if (!c.IsOwnedBy(worker.WorkerId) || wanted.Contains(c.RuntimeId)) continue;
                if (!lastWanted.TryGetValue(c.RuntimeId, out var last)) { lastWanted[c.RuntimeId] = unscaledTime; continue; }
                if (unscaledTime - last < RetireAfterSeconds || worker.RuntimeContainerIdleSeconds(c.RuntimeId) < RetireAfterSeconds) continue;
                bool occupied = false;
                foreach (var entity in c.Entities) if (entity != null && entity.OwnerClientId != 0) { occupied = true; break; }
                if (!occupied) scratch.Add(c.RuntimeId);
            }
            foreach (var id in scratch) if (worker.ReleaseRuntimeContainer(id)) lastWanted.Remove(id);

            scratch.Clear();
            foreach (var entry in lastWanted) if (!wanted.Contains(entry.Key) && ContainerRegistry.GetRuntime(entry.Key) == null) scratch.Add(entry.Key);
            foreach (var id in scratch) lastWanted.Remove(id);
        }

        private void AddRing(Vector3Int center)
        {
            // Through the grid, not the static helper: a planar grid must not want a layer of cells above and
            // below the one that exists, and the list overload keeps the policy tick allocation-free.
            ring.Clear();
            grid.Neighborhood(center, Ring, ring);
            for (int i = 0; i < ring.Count; i++) wanted.Add(RuntimeGrid.PackId(ring[i]));
        }

        /// <summary>
        /// Request the container at <paramref name="coord"/> and call <paramref name="onReady"/> once it is
        /// registered (immediately, if it already is). Event-driven, not polling: replaces the
        /// request-then-poll-every-0.1s loops a game otherwise writes by hand at every call site that needs a
        /// container to exist before acting on it. <see cref="ContainerRegistry.RuntimeRegistered"/> is a static,
        /// mesh-wide event, so this fires for a container any worker or the control plane registers, not only ones
        /// this allocator itself requested.
        /// </summary>
        public void EnsureContainer(Vector3Int coord, Action<Container> onReady)
        {
            if (onReady == null) return;
            coord = grid.Normalize(coord);
            ulong id = RuntimeGrid.PackId(coord);
            var existing = ContainerRegistry.GetRuntime(id);
            if (existing != null) { onReady(existing); return; }
            worker.RequestRuntimeContainer(id, grid.BoundsOf(coord));
            Action<Container> handler = null;
            handler = c =>
            {
                if (c.RuntimeId != id) return;
                ContainerRegistry.RuntimeRegistered -= handler;
                onReady(c);
            };
            ContainerRegistry.RuntimeRegistered += handler;
        }
    }
}
