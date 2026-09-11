using System;
using System.Collections.Generic;
using System.Linq;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The container graph of the loaded level: every static <see cref="Container"/>, indexed deterministically so
    /// all processes agree on indices, plus adjacency (adjacent = bounds touch or overlap), plus the dynamic
    /// containers currently carried by entities on this process, resolved by their carrier's net id.
    /// <para>Two ways to populate the static part: <see cref="Rebuild"/> scans the loaded scene and sorts by id
    /// (single-scene games), or <see cref="Load"/> takes an already ordered list built from a
    /// <see cref="WorldContainerManifest"/> (partitioned worlds), in which case lookups go through a grid of cells
    /// instead of a linear scan. Dynamic containers come and go with their carriers
    /// (<see cref="RegisterDynamic"/> / <see cref="UnregisterDynamic"/>, called by <see cref="DynamicContainer"/>).</para>
    /// </summary>
    public static class ContainerRegistry
    {
        private static readonly List<Container> Containers = new List<Container>();
        private static readonly Dictionary<string, Container> ById = new Dictionary<string, Container>();
        private static Dictionary<Vector3Int, List<Container>> _grid;
        private static readonly List<Container> Candidates = new List<Container>();
        private static readonly List<Container> DynamicList = new List<Container>();
        private static readonly Dictionary<ulong, Container> DynamicByNetId = new Dictionary<ulong, Container>();
        private static readonly List<NetworkIdentity> EntityScratch = new List<NetworkIdentity>();
        private struct PendingLease { public string WorkerId; public ushort WorkerIndex; public ulong Epoch; public string State; }
        /// <summary>Leases for dynamic containers whose carrier is not here yet, by container id; applied on registration.</summary>
        private static readonly Dictionary<string, PendingLease> PendingLeases = new Dictionary<string, PendingLease>();

        /// <summary>The static containers, in wire order.</summary>
        public static IReadOnlyList<Container> All => Containers;
        public static int Count => Containers.Count;
        /// <summary>The dynamic containers present on this process (their carriers are resident here), in registration order.</summary>
        public static IReadOnlyList<Container> Dynamic => DynamicList;
        /// <summary>A partitioned world's manifest is loaded: containers know their cell and lookups use the grid.</summary>
        public static bool IsGridded => _grid != null;
        public static event Action Rebuilt;
        /// <summary>Raised after a batch of leases was applied (the worker and client do this whenever the control plane changes).</summary>
        public static event Action LeasesChanged;
        /// <summary>A dynamic container became resolvable here (its carrier spawned). Messages that were waiting for it can be applied.</summary>
        public static event Action<Container> DynamicRegistered;
        /// <summary>A dynamic container is about to go (its carrier is despawning). Its contents are moved to the container around it right after.</summary>
        public static event Action<Container> DynamicUnregistering;

        /// <summary>
        /// How a worker index maps to a worker id, for the derived ownership of dynamic containers
        /// (<see cref="Container.OwnerWorkerId"/>). The worker installs one that knows its peers; elsewhere the
        /// default only recognises this process's own worker id.
        /// </summary>
        public static Func<ushort, string> WorkerIdByIndex = index => NebulaRuntime.IsServer && index == NebulaRuntime.LocalWorkerIndex ? NebulaRuntime.LocalWorkerId : "";

        /// <summary>Scan the loaded scene(s) for static containers and index them by id. Containers carried by entities (<see cref="DynamicContainer"/>) are skipped.</summary>
        public static void Rebuild()
        {
            var found = UnityEngine.Object.FindObjectsByType<Container>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(c => c.GetComponent<DynamicContainer>() == null)
                .OrderBy(c => c.ContainerId, StringComparer.Ordinal)
                .ToList();
            Load(found, gridded: false);
        }

        /// <summary>
        /// Index <paramref name="ordered"/> as given (position = wire index). With <paramref name="gridded"/> every
        /// container must carry its <see cref="Container.Cell"/>; adjacency is then only tested between containers
        /// of neighbouring cells and <see cref="Find"/> only visits the cells around the point. Dynamic containers
        /// registered earlier are forgotten: this is a boot-time operation.
        /// </summary>
        public static void Load(IList<Container> ordered, bool gridded)
        {
            Containers.Clear();
            ById.Clear();
            DynamicList.Clear();
            DynamicByNetId.Clear();
            PendingLeases.Clear();
            _grid = gridded ? new Dictionary<Vector3Int, List<Container>>() : null;
            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                if (ById.ContainsKey(c.ContainerId))
                    throw new InvalidOperationException($"Duplicate container id '{c.ContainerId}'");
                c.Index = (ushort)i;
                c.IsDynamic = false;
                c.Carrier = null;
                c.RefreshCache();
                c.Neighbors.Clear();
                Containers.Add(c);
                ById[c.ContainerId] = c;
                if (_grid != null)
                {
                    if (!_grid.TryGetValue(c.Cell, out var list)) _grid[c.Cell] = list = new List<Container>();
                    list.Add(c);
                }
            }
            const float touch = 0.05f;
            for (int i = 0; i < Containers.Count; i++)
            {
                var ci = Containers[i];
                var a = ci.WorldBounds;
                a.Expand(touch);
                if (_grid == null)
                {
                    for (int j = 0; j < Containers.Count; j++)
                    {
                        if (i == j) continue;
                        if (a.Intersects(Containers[j].WorldBounds)) ci.Neighbors.Add(Containers[j]);
                    }
                }
                else
                {
                    CollectAround(ci.Cell, Candidates);
                    foreach (var cj in Candidates)
                    {
                        if (cj == ci) continue;
                        if (a.Intersects(cj.WorldBounds)) ci.Neighbors.Add(cj);
                    }
                }
            }
            Rebuilt?.Invoke();
        }

        /// <summary>Containers of <paramref name="cell"/> and the 26 cells around it.</summary>
        private static void CollectAround(Vector3Int cell, List<Container> result)
        {
            result.Clear();
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        if (_grid.TryGetValue(new Vector3Int(cell.x + x, cell.y + y, cell.z + z), out var list)) result.AddRange(list);
        }

        /// <summary>Containers whose cell is <paramref name="cell"/> (the cell container and its nested ones). Empty when not gridded.</summary>
        public static IReadOnlyList<Container> InCell(Vector3Int cell)
        {
            return _grid != null && _grid.TryGetValue(cell, out var list) ? list : (IReadOnlyList<Container>)Array.Empty<Container>();
        }

        /// <summary>The static container with this wire index, or null (also null for <see cref="ContainerRef.DynamicIndex"/>: use <see cref="Resolve(ContainerRef)"/>).</summary>
        public static Container Get(ushort index) => index < Containers.Count ? Containers[index] : null;

        /// <summary>The container a wire reference names, static or dynamic, or null when it is not known on this process (a dynamic container whose carrier has not arrived yet).</summary>
        public static Container Resolve(ContainerRef r)
        {
            if (r.IsDynamic) return DynamicByNetId.TryGetValue(r.NetId, out var c) ? c : null;
            return Get(r.Index);
        }

        /// <summary>The dynamic container carried by entity <paramref name="carrierNetId"/>, or null.</summary>
        public static Container FindDynamic(ulong carrierNetId) => DynamicByNetId.TryGetValue(carrierNetId, out var c) ? c : null;

        /// <summary>Re-read every container's transform (the worker does this once per tick; see <see cref="Container.RefreshCache"/>).</summary>
        public static void RefreshCaches()
        {
            for (int i = 0; i < Containers.Count; i++) Containers[i].RefreshCache();
            for (int i = 0; i < DynamicList.Count; i++) DynamicList[i].RefreshCache();
        }

        /// <summary>The static container with this id, or the dynamic container currently registered under it (<c>label#netId</c>), or null.</summary>
        public static Container FindById(string id)
        {
            if (id == null) return null;
            if (ById.TryGetValue(id, out var c)) return c;
            for (int i = 0; i < DynamicList.Count; i++) if (DynamicList[i].ContainerId == id) return DynamicList[i];
            return null;
        }

        /// <summary>Whether a container id names a dynamic container (<c>label#netId</c>): static ids never contain '#'.</summary>
        public static bool IsDynamicId(string id) => id != null && id.IndexOf('#') >= 0;

        /// <summary>The carrier net id a dynamic container id names, or 0.</summary>
        public static ulong CarrierNetIdOf(string id)
        {
            int hash = id != null ? id.LastIndexOf('#') : -1;
            return hash >= 0 && ulong.TryParse(id.Substring(hash + 1), out var netId) ? netId : 0;
        }

        // ---------------------------------------------------------------------------------------- dynamic containers

        /// <summary>
        /// Make <paramref name="container"/> the dynamic container carried by <paramref name="carrier"/>, resolvable
        /// by the carrier's net id from now on. <see cref="DynamicContainer"/> calls this when its entity spawns on
        /// any process; game code does not normally need to. The authored <see cref="Container.ContainerId"/> becomes
        /// a label: the runtime id is <c>label#netId</c>, unique across the mesh.
        /// </summary>
        public static void RegisterDynamic(Container container, NetworkIdentity carrier)
        {
            if (container == null || carrier == null) throw new ArgumentNullException(container == null ? nameof(container) : nameof(carrier));
            if (carrier.NetId == 0) { NebulaLog.Error($"dynamic container on '{carrier.name}' registered before the entity has a net id; ignored"); return; }
            if (container.IsDynamic && container.Carrier == carrier && DynamicByNetId.TryGetValue(carrier.NetId, out var same) && same == container) return;
            if (DynamicByNetId.TryGetValue(carrier.NetId, out var existing) && existing != container)
            {
                NebulaLog.Warn($"entity #{carrier.NetId} already carries container '{existing.ContainerId}'; replacing it");
                UnregisterDynamic(existing);
            }
            string label = string.IsNullOrEmpty(container.ContainerId) || container.ContainerId == "container" ? carrier.name.Replace("(Clone)", "").Trim() : container.ContainerId;
            int hash = label.IndexOf('#');
            if (hash >= 0) label = label.Substring(0, hash);
            container.ContainerId = $"{label}#{carrier.NetId}";
            container.IsDynamic = true;
            container.Carrier = carrier;
            container.Index = ContainerRef.DynamicIndex;
            container.Neighbors.Clear();
            container.RefreshCache();
            DynamicList.Add(container);
            DynamicByNetId[carrier.NetId] = container;
            if (PendingLeases.TryGetValue(container.ContainerId, out var lease))
            {
                PendingLeases.Remove(container.ContainerId);
                ApplyLease(container.ContainerId, lease.WorkerId, lease.WorkerIndex, lease.Epoch, lease.State);
            }
            DynamicRegistered?.Invoke(container);
        }

        /// <summary>
        /// Forget a dynamic container. Whatever is still inside it is moved to the container around it (resolved
        /// from each entity's world position, ignoring the departing box) and re-parented, so a client destroying a
        /// despawned ship does not take the passengers' objects with it. The authority decides what actually happens
        /// to them on the next tick; despawn the contents first if they should not survive the carrier.
        /// </summary>
        public static void UnregisterDynamic(Container container)
        {
            if (container == null || !container.IsDynamic) return;
            ulong netId = container.CarrierNetId;
            if (!DynamicList.Remove(container)) return;
            if (netId != 0 && DynamicByNetId.TryGetValue(netId, out var same) && same == container) DynamicByNetId.Remove(netId);
            DynamicUnregistering?.Invoke(container);
            EntityScratch.Clear();
            EntityScratch.AddRange(container.Entities);
            foreach (var e in EntityScratch)
            {
                if (e == null) continue;
                var outer = Find(e.transform.position, container);
                if (outer == container) outer = null;
                e.SetContainer(outer);
            }
            container.Entities.Clear();
            PendingLeases.Remove(container.ContainerId);
            container.IsDynamic = false;
            container.Carrier = null;
            container.Index = ushort.MaxValue;
            container.LeaseState = "";
            container.OwnerWorkerId = "";
            container.OwnerWorkerIndex = ushort.MaxValue;
        }

        /// <summary>
        /// Every container adjacent to <paramref name="container"/> right now, into <paramref name="result"/>
        /// (cleared first): its static <see cref="Container.Neighbors"/> plus every dynamic container whose box
        /// currently touches it, and, for a dynamic container, the container its carrier sits in and every container
        /// its box touches. This is what the ghost band walks; use it instead of <see cref="Container.Neighbors"/>
        /// wherever a vehicle could be parked next door.
        /// </summary>
        public static void NeighborsOf(Container container, List<Container> result)
        {
            result.Clear();
            if (container == null) return;
            var bounds = container.WorldBounds;
            bounds.Expand(0.05f);
            if (!container.IsDynamic)
            {
                result.AddRange(container.Neighbors);
                for (int i = 0; i < DynamicList.Count; i++)
                {
                    var d = DynamicList[i];
                    if (d != container && bounds.Intersects(d.WorldBounds)) result.Add(d);
                }
                return;
            }
            var enclosing = container.Enclosing;
            if (enclosing != null) result.Add(enclosing);
            if (_grid != null)
            {
                CollectAround(WorldOrigin.CellOf(bounds.center), Candidates);
                foreach (var c in Candidates) if (c != enclosing && bounds.Intersects(c.WorldBounds)) result.Add(c);
            }
            else
            {
                for (int i = 0; i < Containers.Count; i++)
                {
                    var c = Containers[i];
                    if (c != enclosing && bounds.Intersects(c.WorldBounds)) result.Add(c);
                }
            }
            for (int i = 0; i < DynamicList.Count; i++)
            {
                var d = DynamicList[i];
                if (d == container || d == enclosing) continue;
                if (bounds.Intersects(d.WorldBounds)) result.Add(d);
            }
        }

        // ---------------------------------------------------------------------------------------- lookups

        /// <summary>
        /// The container whose volume holds the point; if none does, the nearest one. Containers may nest (an
        /// "outdoor" box enclosing per-building boxes, a ship inside the outdoor box): when several hold the point
        /// the smallest volume wins, so the most specific container is chosen. Pure geometry, so every process
        /// agrees. <paramref name="exclude"/> leaves one container out of the search: an entity carrying a container
        /// resolves its own position without it, or it would be found inside itself.
        /// </summary>
        public static Container Find(Vector3 worldPosition, Container exclude = null)
        {
            Container inside = null, nearest = null;
            float insideVolume = float.MaxValue, nearestDist = float.MaxValue;
            if (_grid != null)
            {
                CollectAround(WorldOrigin.CellOf(worldPosition), Candidates);
                FindAmong(Candidates, worldPosition, exclude, ref inside, ref insideVolume, ref nearest, ref nearestDist);
                FindAmong(DynamicList, worldPosition, exclude, ref inside, ref insideVolume, ref nearest, ref nearestDist);
                if (inside != null || nearest != null) return inside ?? nearest;
            }
            FindAmong(Containers, worldPosition, exclude, ref inside, ref insideVolume, ref nearest, ref nearestDist);
            if (_grid == null) FindAmong(DynamicList, worldPosition, exclude, ref inside, ref insideVolume, ref nearest, ref nearestDist);
            return inside ?? nearest;
        }

        private static void FindAmong(List<Container> list, Vector3 worldPosition, Container exclude, ref Container inside, ref float insideVolume, ref Container nearest, ref float nearestDist)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == exclude) continue;
                float d = c.SignedDistance(worldPosition);
                if (d <= 0f)
                {
                    float volume = c.Volume;
                    if (volume < insideVolume)
                    {
                        insideVolume = volume;
                        inside = c;
                    }
                }
                else if (d < nearestDist)
                {
                    nearestDist = d;
                    nearest = c;
                }
            }
        }

        /// <summary>
        /// The container the entity should belong to after applying hysteresis: it must be at least
        /// <paramref name="hysteresis"/> metres inside a different container before we consider it moved.
        /// <paramref name="exclude"/> as in <see cref="Find"/>.
        /// </summary>
        public static Container Resolve(Vector3 worldPosition, Container current, float hysteresis, Container exclude = null)
        {
            if (current == null) return Find(worldPosition, exclude);
            var candidate = Find(worldPosition, exclude);
            if (candidate == null || candidate == current) return current;
            if (current.Contains(worldPosition))
            {
                // Still inside the current box but a nested (smaller) container now claims the point: enter it once
                // we are past the hysteresis band. Leaving a nested box back into its enclosing box happens below
                // as soon as the nested box no longer contains us.
                return candidate.SignedDistance(worldPosition) <= -hysteresis ? candidate : current;
            }
            return candidate.SignedDistance(worldPosition) <= -hysteresis ? candidate : current;
        }

        /// <summary>
        /// Every container whose box (grown by <paramref name="margin"/>) the segment <paramref name="a"/>-<paramref name="b"/>
        /// passes through, appended to <paramref name="result"/>. Nested containers are all reported: an entity in a
        /// building is in the building's box and in the outdoor box around it. This is what a hitscan or a line of
        /// sight uses to find out which workers, besides itself, could own something along the ray. In a gridded
        /// world only the cells the segment's bounds touch are visited; dynamic containers are always tested.
        /// </summary>
        public static void Along(Vector3 a, Vector3 b, float margin, List<Container> result)
        {
            for (int i = 0; i < DynamicList.Count; i++)
                if (DynamicList[i].IntersectsSegment(a, b, margin, out _, out _)) result.Add(DynamicList[i]);
            if (_grid == null)
            {
                for (int i = 0; i < Containers.Count; i++)
                    if (Containers[i].IntersectsSegment(a, b, margin, out _, out _)) result.Add(Containers[i]);
                return;
            }
            var min = WorldOrigin.CellOf(Vector3.Min(a, b) - Vector3.one * margin);
            var max = WorldOrigin.CellOf(Vector3.Max(a, b) + Vector3.one * margin);
            for (int x = min.x - 1; x <= max.x + 1; x++)
                for (int y = min.y - 1; y <= max.y + 1; y++)
                    for (int z = min.z - 1; z <= max.z + 1; z++)
                    {
                        if (!_grid.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                        for (int i = 0; i < list.Count; i++)
                            if (list[i].IntersectsSegment(a, b, margin, out _, out _)) result.Add(list[i]);
                    }
        }

        /// <summary>
        /// Record a control-plane lease on its container. A lease for a dynamic container whose carrier is not
        /// resident yet is kept and applied when the carrier registers. <paramref name="state"/> is the lease state
        /// (<see cref="LeaseState"/>); for a dynamic container only <see cref="LeaseState.Pinned"/> changes who owns it.
        /// </summary>
        public static void ApplyLease(string containerId, string workerId, ushort workerIndex, ulong epoch, string state = LeaseState.Active)
        {
            var c = FindById(containerId);
            if (c == null)
            {
                if (IsDynamicId(containerId)) PendingLeases[containerId] = new PendingLease { WorkerId = workerId ?? "", WorkerIndex = workerIndex, Epoch = epoch, State = state ?? "" };
                return;
            }
            c.OwnerWorkerId = workerId ?? "";
            c.OwnerWorkerIndex = workerIndex;
            c.LeaseEpoch = epoch;
            c.LeaseState = state ?? "";
        }

        /// <summary>A lease row went away (a carrier despawned): forget whatever was recorded for it.</summary>
        public static void ForgetLease(string containerId)
        {
            PendingLeases.Remove(containerId);
            var c = FindById(containerId);
            if (c == null || !c.IsDynamic) return;
            c.OwnerWorkerId = "";
            c.OwnerWorkerIndex = ushort.MaxValue;
            c.LeaseState = "";
        }

        /// <summary>Tell listeners (world streaming, overlays) that a batch of <see cref="ApplyLease"/> calls is complete.</summary>
        public static void NotifyLeasesChanged() => LeasesChanged?.Invoke();
    }
}
