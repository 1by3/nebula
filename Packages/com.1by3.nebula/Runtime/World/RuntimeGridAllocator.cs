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
    /// A cell counts as occupied while a client owns an entity anywhere inside it, including one riding in a
    /// vehicle's <see cref="DynamicContainer"/> (a pilot in a ship parked in the cell, or a passenger in a shuttle
    /// inside that ship). A cell is also kept while an authoritative entity still assigned to it stands in another
    /// cell that this allocator wants, or whose lease row any worker touched within
    /// <see cref="RetireAfterSeconds"/>. This happens when a fast vehicle leaves every leased cell behind: the
    /// vehicle stays in the last cell it left until a cell is registered where it is.
    /// </para>
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
        private readonly List<Vector3Int> pins = new List<Vector3Int>();
        private readonly HashSet<ulong> wanted = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> lastWanted = new Dictionary<ulong, float>();
        private readonly List<ulong> scratch = new List<ulong>();
        private readonly List<Vector3Int> ring = new List<Vector3Int>();
        private readonly List<NetworkIdentity> contents = new List<NetworkIdentity>();
        /// <summary>The scope blob each chunk's lease row is born with; null (and never built) for the public world.</summary>
        private readonly Dictionary<ulong, InstanceContainerInfo> instances;
        private float next;
        private bool hasSeenScope;
        private string observedDefinition;
        private bool definitionChanged;
        private bool warnedDefinitionChanged;

        /// <summary>Ring radius (Chebyshev distance in cells) requested around every anchor and owned entity.</summary>
        public int Ring { get; set; } = 1;
        /// <summary>
        /// How long an owned, unwanted, unoccupied cell sits idle before it is released. Also how recently another
        /// cell's lease row must have been touched for an entity standing in that cell to keep its own cell leased.
        /// </summary>
        public float RetireAfterSeconds { get; set; } = 60f;
        /// <summary>How often <see cref="Tick"/> actually recomputes interest; calls between are no-ops.</summary>
        public float TickIntervalSeconds { get; set; } = 0.25f;

        public RuntimeGridAllocator(NebulaWorker worker, RuntimeGrid grid)
        {
            this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
            this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
            if (!grid.IsPublic) instances = new Dictionary<ulong, InstanceContainerInfo>();
            ReadScope();
        }

        /// <summary>The grid this allocator works in.</summary>
        public RuntimeGrid Grid => grid;

        /// <summary>The scope this allocator leases chunks in (<c>""</c> for the public world).</summary>
        public string ScopeKey => grid.ScopeKey;

        /// <summary>
        /// What a scoped chunk's lease row is born carrying, cached per chunk. The public world's chunks carry
        /// nothing, exactly as before scoped grids existed, so their rows are byte for byte what they always were.
        /// </summary>
        private InstanceContainerInfo InstanceOf(ulong id, Vector3Int coord)
        {
            if (instances == null) return null;
            if (instances.TryGetValue(id, out var info)) return info;
            info = new InstanceContainerInfo
            {
                InstanceId = grid.InstanceId,
                ScopeKey = grid.ScopeKey,
                PartId = ChunkKeys.PartId(grid.Normalize(coord)),
            };
            instances[id] = info;
            return info;
        }

        /// <summary>Fixed coordinates kept wanted regardless of where entities are, e.g. the origin cell(s).</summary>
        public void AddAnchor(Vector3Int coord)
        {
            coord = grid.Normalize(coord);
            if (!anchors.Contains(coord)) anchors.Add(coord);
        }
        public void RemoveAnchor(Vector3Int coord) => anchors.Remove(grid.Normalize(coord));
        public void ClearAnchors() => anchors.Clear();

        /// <summary>
        /// Keep one chunk wanted without a ring around it: a scope's anchor chunk, which must not be retired
        /// because it is where a client routed by key arrives, but which does not need its neighbours leased on
        /// every worker that has ever seen the scope.
        /// </summary>
        public void AddPin(Vector3Int coord)
        {
            coord = grid.Normalize(coord);
            if (!pins.Contains(coord)) pins.Add(coord);
        }
        public void RemovePin(Vector3Int coord) => pins.Remove(grid.Normalize(coord));

        /// <summary>The interest set as of the last <see cref="Tick"/>: every cell id currently requested.</summary>
        public IReadOnlyCollection<ulong> WantedIds => wanted;
        public bool IsWanted(Vector3Int coord) => wanted.Contains(grid.IdOf(coord));
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
            var scope = ReadScope();
            if (definitionChanged) return;
            bool retiring = scope?.State == ScopeState.Retiring;
            if (MayAllocate(scope))
            {
                foreach (var a in anchors) AddRing(a);
                for (int i = 0; i < pins.Count; i++) wanted.Add(grid.IdOf(pins[i]));
                foreach (var entity in worker.Authoritative)
                    // Only this grid's own pawns: a player standing in another scope must not drag this world's
                    // chunks into being at the coordinate he happens to occupy over there.
                    if (entity != null && entity.OwnerClientId != 0 && entity.InstanceId == grid.InstanceId) AddRing(grid.CoordOf(entity));
            }

            foreach (var id in wanted)
            {
                lastWanted[id] = unscaledTime;
                if (!grid.TryCoordOf(id, out var coord)) continue;
                // Touch existing foreign leases too: their owner must not retire our neighbours.
                worker.RequestRuntimeContainer(id, grid.BoundsOf(coord), InstanceOf(id, coord));
            }

            scratch.Clear();
            foreach (var c in ContainerRegistry.Runtime)
            {
                // Only this grid's chunks: another scope's allocator owns its own, and retiring a box that is not
                // ours would empty somebody else's world.
                if (!grid.Owns(c)) continue;
                // The lifecycle owns the checkpoint barrier for its parts. Other idle chunks may drain normally.
                if (retiring && scope.ContainerIds.Contains(c.ContainerId)) continue;
                if (!c.IsOwnedBy(worker.WorkerId) || wanted.Contains(c.RuntimeId)) continue;
                if (!lastWanted.TryGetValue(c.RuntimeId, out var last)) { lastWanted[c.RuntimeId] = unscaledTime; continue; }
                if (unscaledTime - last < RetireAfterSeconds || worker.RuntimeContainerIdleSeconds(c.RuntimeId) < RetireAfterSeconds) continue;
                if (!MustKeep(c)) scratch.Add(c.RuntimeId);
            }
            foreach (var id in scratch) if (worker.ReleaseRuntimeContainer(id)) { lastWanted.Remove(id); instances?.Remove(id); }

            scratch.Clear();
            foreach (var entry in lastWanted) if (!wanted.Contains(entry.Key) && ContainerRegistry.GetRuntime(entry.Key) == null) scratch.Add(entry.Key);
            foreach (var id in scratch) { lastWanted.Remove(id); instances?.Remove(id); }
        }

        /// <summary>
        /// Whether releasing <paramref name="chunk"/>, an owned chunk nobody has wanted for long enough, would still
        /// unload something that must stay (<c>docs/dynamic-worlds.md</c>, "Retiring a chunk under a carrier"):
        /// <list type="bullet">
        /// <item>A client owns something in it at any carrier depth: a player standing in it, or the pilot of a ship
        /// parked in it, or a passenger of a shuttle in that ship's hangar. A rider is not in the chunk's own
        /// <see cref="Container.Entities"/>, so looking only there retired the ship under its crew.</item>
        /// <item>An authoritative entity filed under it stands in live space somewhere else. Container resolution
        /// keeps an entity in the nearest box when no box holds it yet, so a ship that outran the leased chunks is
        /// still filed under a chunk it left kilometres ago. Where it actually is decides: a cell this allocator wants,
        /// or one whose lease row somebody touched within <see cref="RetireAfterSeconds"/>, is being loaded, and the
        /// entity moves into it as soon as it is registered; releasing the old chunk first would unload it from the
        /// middle of the loaded world. Content standing in space nobody wants is unloaded with the chunk as usual.</item>
        /// </list>
        /// </summary>
        private bool MustKeep(Container chunk)
        {
            contents.Clear();
            chunk.CollectContents(contents, throughAuthoritativeCarriersOnly: false);
            bool keep = false;
            for (int i = 0; i < contents.Count && !keep; i++)
            {
                var e = contents[i];
                if (e == null) continue;
                if (e.OwnerClientId != 0) keep = true;
                else if (e.HasAuthority && e.Container == chunk && StandsInLiveSpaceOutside(e, chunk)) keep = true;
            }
            contents.Clear();
            return keep;
        }

        private bool StandsInLiveSpaceOutside(NetworkIdentity entity, Container chunk)
        {
            if (!grid.TryCoordOf(chunk.RuntimeId, out var own)) return false;
            var at = grid.Normalize(grid.CoordOf(entity));
            // Inside its own box it stands in this chunk, which nobody wants: that is ordinary unloading.
            if (at == grid.Normalize(own) || !RuntimeGrid.IsValid(at)) return false;
            ulong id = grid.IdOf(at);
            return wanted.Contains(id) || worker.RuntimeContainerIdleSeconds(id) < RetireAfterSeconds;
        }

        private void AddRing(Vector3Int center)
        {
            // Through the grid, not the static helper: a planar grid must not want a layer of cells above and
            // below the one that exists, and the list overload keeps the policy tick allocation-free.
            ring.Clear();
            grid.Neighborhood(center, Ring, ring);
            for (int i = 0; i < ring.Count; i++) wanted.Add(grid.IdOf(ring[i]));
        }

        private ScopeInfo ReadScope()
        {
            var scope = grid.IsPublic ? null : worker.ControlPlane?.FindScope(grid.ScopeKey);
            if (scope != null)
            {
                string definition = ScopeJson.WriteDefinition(scope.Definition);
                if (!hasSeenScope) observedDefinition = definition;
                hasSeenScope = true;
                // Removing a scope releases its key for different content, but this allocator still owns the
                // original grid arithmetic and pins. Do not create or retire leases using that stale layout.
                definitionChanged = !string.Equals(observedDefinition, definition, StringComparison.Ordinal);
                if (definitionChanged && !warnedDefinitionChanged)
                {
                    warnedDefinitionChanged = true;
                    NebulaLog.Warn($"chunk allocator for scope '{grid.ScopeKey}' stopped because its definition changed; restart the worker or use a new scope key to load the new grid definition");
                }
            }
            return scope;
        }

        private bool MayAllocate(ScopeInfo scope) =>
            !definitionChanged && (scope != null || !hasSeenScope) && scope?.State != ScopeState.Retiring && scope?.State != ScopeState.Retired;

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
            if (!MayAllocate(ReadScope())) return;
            coord = grid.Normalize(coord);
            ulong id = grid.IdOf(coord);
            var existing = ContainerRegistry.GetRuntime(id);
            if (existing != null) { onReady(existing); return; }
            worker.RequestRuntimeContainer(id, grid.BoundsOf(coord), InstanceOf(id, coord));
            Action<Container> handler = null;
            handler = c =>
            {
                if (c.RuntimeId != id) return;
                ContainerRegistry.RuntimeRegistered -= handler;
                if (!MayAllocate(ReadScope())) return;
                onReady(c);
            };
            ContainerRegistry.RuntimeRegistered += handler;
        }
    }
}
