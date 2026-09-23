using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The container graph of the loaded level: every static <see cref="Container"/>, indexed deterministically so
    /// all processes agree on indices, plus adjacency (adjacent = bounds touch or overlap), plus the dynamic
    /// containers currently carried by entities on this process, resolved by their carrier's net id, plus the
    /// runtime containers the game registered while the mesh runs, resolved by the id the game gave them.
    /// <para>Two ways to populate the static part: <see cref="Rebuild"/> scans the loaded scene and sorts by id
    /// (single-scene games), or <see cref="Load"/> takes an already ordered list built from a
    /// <see cref="WorldContainerManifest"/> (partitioned worlds), in which case lookups go through a grid of cells
    /// instead of a linear scan. Dynamic containers come and go with their carriers
    /// (<see cref="RegisterDynamic"/> / <see cref="UnregisterDynamic"/>, called by <see cref="DynamicContainer"/>).
    /// Runtime containers come and go with their control-plane lease (<see cref="RegisterRuntime"/> /
    /// <see cref="UnregisterRuntime"/>, called by every role from <see cref="SyncRuntime"/>); a world whose shape
    /// is decided at runtime (a chunked landscape) has no baked set at all and lives entirely in them.</para>
    /// </summary>
    public static class ContainerRegistry
    {
        private static readonly List<Container> Containers = new List<Container>();
        private static readonly Dictionary<string, Container> ById = new Dictionary<string, Container>();
        private static Dictionary<Vector3Int, List<Container>> _grid;
        private static readonly List<Container> Candidates = new List<Container>();
        private static readonly List<Container> DynamicList = new List<Container>();
        private static readonly Dictionary<ulong, Container> DynamicByNetId = new Dictionary<ulong, Container>();
        // Carried containers move every tick, so they cannot live in the static grid. They get a coarse hash of
        // their own instead, rebuilt whenever the caches are refreshed (once per tick on a worker) and whenever one
        // is registered or forgotten. It is a broad phase only: every candidate is still tested against the exact
        // box. Footprints and queries are padded by a whole bucket, so a carrier that moves between the rebuild and
        // the query is still found - at 256 m buckets that is far more than anything moves in a tick.
        private static readonly Dictionary<Vector3Int, List<Container>> DynamicHash = new Dictionary<Vector3Int, List<Container>>();
        private static readonly List<Container> DynamicCandidates = new List<Container>();
        private static readonly HashSet<Container> DynamicSeen = new HashSet<Container>();
        private static bool _dynamicHashDirty = true;
        /// <summary>Below this many carried containers the exact linear scan is cheaper than hashing them.</summary>
        private const int DynamicHashThreshold = 16;
        private static readonly List<NetworkIdentity> EntityScratch = new List<NetworkIdentity>();
        private struct PendingLease { public string WorkerId; public ushort WorkerIndex; public ulong Epoch; public string State; }
        /// <summary>Leases for dynamic or runtime containers that are not here yet, by container id; applied on registration.</summary>
        private static readonly Dictionary<string, PendingLease> PendingLeases = new Dictionary<string, PendingLease>();

        // Runtime containers: a flat list plus a coarse spatial hash, so a world of thousands of chunks still
        // resolves a point by visiting a handful of buckets. Buckets are keyed in the current frame; a box is
        // entered into every bucket it overlaps.
        private static readonly List<Container> RuntimeList = new List<Container>();
        private static readonly Dictionary<ulong, Container> RuntimeById = new Dictionary<ulong, Container>();
        private static readonly Dictionary<Vector3Int, List<Container>> RuntimeHash = new Dictionary<Vector3Int, List<Container>>();
        private static readonly List<Container> RuntimeCandidates = new List<Container>();
        private static readonly HashSet<Container> RuntimeSeen = new HashSet<Container>();
        private static readonly HashSet<ulong> RuntimeKeep = new HashSet<ulong>();
        private static readonly List<ulong> RuntimeScratchIds = new List<ulong>();
        private static Transform _runtimeRoot;

        /// <summary>The static containers, in wire order.</summary>
        public static IReadOnlyList<Container> All => Containers;
        public static int Count => Containers.Count;
        /// <summary>The dynamic containers present on this process (their carriers are resident here), in registration order.</summary>
        public static IReadOnlyList<Container> Dynamic => DynamicList;
        /// <summary>The runtime containers registered on this process, in registration order.</summary>
        public static IReadOnlyList<Container> Runtime => RuntimeList;
        /// <summary>A partitioned world's manifest is loaded: containers know their cell and lookups use the grid.</summary>
        public static bool IsGridded => _grid != null;
        /// <summary>
        /// Edge length, in meters, of the buckets runtime containers are hashed into. Set it before the first
        /// <see cref="RegisterRuntime"/> (a chunk size or a small multiple of it is right); changing it later rehashes.
        /// </summary>
        public static float RuntimeBucketSize
        {
            get => _runtimeBucketSize;
            set
            {
                value = Mathf.Max(1f, value);
                if (Mathf.Approximately(value, _runtimeBucketSize)) return;
                _runtimeBucketSize = value;
                RehashRuntime();
            }
        }
        private static float _runtimeBucketSize = 256f;

        public static event Action Rebuilt;
        /// <summary>Raised after a batch of leases was applied (the worker and client do this whenever the control plane changes).</summary>
        public static event Action LeasesChanged;
        /// <summary>A dynamic container became resolvable here (its carrier spawned). Messages that were waiting for it can be applied.</summary>
        public static event Action<Container> DynamicRegistered;
        /// <summary>A dynamic container is about to go (its carrier is despawning). Its contents are moved to the container around it right after.</summary>
        public static event Action<Container> DynamicUnregistering;
        /// <summary>A runtime container became resolvable here. The game loads whatever content belongs in that box (terrain, colliders, visuals).</summary>
        public static event Action<Container> RuntimeRegistered;
        /// <summary>A runtime container is about to go. The game unloads its content; entities still inside are moved to the container around them right after.</summary>
        public static event Action<Container> RuntimeUnregistering;

        /// <summary>
        /// How a worker index maps to a worker id, for the derived ownership of dynamic containers
        /// (<see cref="Container.OwnerWorkerId"/>). The worker installs one that knows its peers; elsewhere the
        /// default only recognises this process's own worker id.
        /// </summary>
        public static Func<ushort, string> WorkerIdByIndex = index => NebulaRuntime.IsServer && index == NebulaRuntime.LocalWorkerIndex ? NebulaRuntime.LocalWorkerId : "";

        /// <summary>
        /// Forget everything, subscribers included, without touching any object: a new play session is starting in
        /// an Editor that did not reload the domain, and whatever is still referenced here was destroyed with the
        /// previous one (see <see cref="NebulaStatics"/>).
        /// </summary>
        internal static void ResetForNewSession()
        {
            Containers.Clear();
            ById.Clear();
            _grid = null;
            DynamicList.Clear();
            DynamicByNetId.Clear();
            DynamicHash.Clear();
            _dynamicHashDirty = true;
            PendingLeases.Clear();
            RuntimeList.Clear();
            RuntimeById.Clear();
            RuntimeHash.Clear();
            _runtimeRoot = null;
            Rebuilt = null;
            LeasesChanged = null;
            DynamicRegistered = null;
            DynamicUnregistering = null;
            RuntimeRegistered = null;
            RuntimeUnregistering = null;
            RuntimeBoundsInFrame = null;
            WorkerIdByIndex = index => NebulaRuntime.IsServer && index == NebulaRuntime.LocalWorkerIndex ? NebulaRuntime.LocalWorkerId : "";
        }

        /// <summary>Scan the loaded scene(s) for static containers and index them by id. Containers carried by entities (<see cref="DynamicContainer"/>) and runtime containers are skipped.</summary>
        public static void Rebuild()
        {
            var found = UnityEngine.Object.FindObjectsByType<Container>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(c => c.GetComponent<DynamicContainer>() == null && !c.IsRuntime)
                .OrderBy(c => c.ContainerId, StringComparer.Ordinal)
                .ToList();
            Load(found, gridded: false);
        }

        /// <summary>
        /// Index <paramref name="ordered"/> as given (position = wire index). With <paramref name="gridded"/> every
        /// container must carry its <see cref="Container.Cell"/>; adjacency is then only tested between containers
        /// of neighboring cells and <see cref="Find"/> only visits the cells around the point. Dynamic and runtime
        /// containers registered earlier are forgotten: this is a boot-time operation.
        /// </summary>
        public static void Load(IList<Container> ordered, bool gridded)
        {
            UnregisterAllRuntime();
            Containers.Clear();
            ById.Clear();
            DynamicList.Clear();
            DynamicByNetId.Clear();
            DynamicHash.Clear();
            _dynamicHashDirty = true;
            PendingLeases.Clear();
            _grid = gridded ? new Dictionary<Vector3Int, List<Container>>() : null;
            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                if (ById.ContainsKey(c.ContainerId))
                    throw new InvalidOperationException($"Duplicate container id '{c.ContainerId}'");
                c.Index = (ushort)i;
                c.IsDynamic = false;
                c.IsRuntime = false;
                c.RuntimeId = 0;
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

        /// <summary>
        /// Every container whose box overlaps <paramref name="box"/>, appended to <paramref name="result"/>
        /// (cleared first). This is the query interest management uses to resolve a region to its owning workers,
        /// so it must never walk the whole world: a gridded world is answered from the cell grid, a
        /// runtime world from the spatial hash, and only the dynamic list — which is small by construction — is
        /// scanned linearly. An ungridded static set is scanned linearly too, which is bounded because an
        /// ungridded world is a handful of hand-placed containers.
        /// </summary>
        public static void Overlapping(Bounds box, List<Container> result, ulong instanceId = 0)
        {
            result.Clear();
            if (_grid != null)
            {
                var min = WorldOrigin.CellOf(box.min);
                var max = WorldOrigin.CellOf(box.max);
                for (int x = min.x - 1; x <= max.x + 1; x++)
                    for (int y = min.y - 1; y <= max.y + 1; y++)
                        for (int z = min.z - 1; z <= max.z + 1; z++)
                        {
                            if (!_grid.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                            for (int i = 0; i < list.Count; i++)
                                if (list[i].InstanceId == instanceId && box.Intersects(list[i].WorldBounds) && !result.Contains(list[i])) result.Add(list[i]);
                        }
            }
            else
            {
                for (int i = 0; i < Containers.Count; i++)
                    if (Containers[i].InstanceId == instanceId && box.Intersects(Containers[i].WorldBounds)) result.Add(Containers[i]);
            }
            if (RuntimeList.Count > 0)
            {
                CollectRuntimeIn(box, RuntimeCandidates);
                for (int i = 0; i < RuntimeCandidates.Count; i++)
                    if (RuntimeCandidates[i].InstanceId == instanceId && box.Intersects(RuntimeCandidates[i].WorldBounds)) result.Add(RuntimeCandidates[i]);
            }
            for (int i = 0; i < DynamicList.Count; i++)
                if (DynamicList[i].InstanceId == instanceId && box.Intersects(DynamicList[i].WorldBounds)) result.Add(DynamicList[i]);
        }

        /// <summary>Containers whose cell is <paramref name="cell"/> (the cell container and its nested ones). Empty when not gridded.</summary>
        public static IReadOnlyList<Container> InCell(Vector3Int cell)
        {
            return _grid != null && _grid.TryGetValue(cell, out var list) ? list : (IReadOnlyList<Container>)Array.Empty<Container>();
        }

        /// <summary>The static container with this wire index, or null (also null for <see cref="ContainerRef.DynamicIndex"/> and <see cref="ContainerRef.RuntimeIndex"/>: use <see cref="Resolve(ContainerRef)"/>).</summary>
        public static Container Get(ushort index) => index < Containers.Count ? Containers[index] : null;

        /// <summary>The container a wire reference names, static, dynamic or runtime, or null when it is not known on this process (a dynamic container whose carrier has not arrived yet, a runtime container whose lease has not).</summary>
        public static Container Resolve(ContainerRef r)
        {
            if (r.IsDynamic) return DynamicByNetId.TryGetValue(r.NetId, out var c) ? c : null;
            if (r.IsRuntime) return RuntimeById.TryGetValue(r.NetId, out var rt) ? rt : null;
            return Get(r.Index);
        }

        /// <summary>The dynamic container carried by entity <paramref name="carrierNetId"/>, or null.</summary>
        public static Container FindDynamic(ulong carrierNetId) => DynamicByNetId.TryGetValue(carrierNetId, out var c) ? c : null;

        /// <summary>Re-read every container's transform (the worker does this once per tick; see <see cref="Container.RefreshCache"/>).</summary>
        public static void RefreshCaches()
        {
            for (int i = 0; i < Containers.Count; i++) Containers[i].RefreshCache();
            for (int i = 0; i < RuntimeList.Count; i++) RuntimeList[i].RefreshCache();
            for (int i = 0; i < DynamicList.Count; i++) DynamicList[i].RefreshCache();
            _dynamicHashDirty = true; // the boxes just moved: the broad phase is rebuilt on the next query
        }

        /// <summary>The static or runtime container with this id, or the dynamic container currently registered under it (<c>label#netId</c>), or null.</summary>
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
            _dynamicHashDirty = true;
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
            _dynamicHashDirty = true;
            if (netId != 0 && DynamicByNetId.TryGetValue(netId, out var same) && same == container) DynamicByNetId.Remove(netId);
            DynamicUnregistering?.Invoke(container);
            EvacuateEntities(container);
            PendingLeases.Remove(container.ContainerId);
            container.IsDynamic = false;
            container.Carrier = null;
            container.Index = ushort.MaxValue;
            container.LeaseState = "";
            container.OwnerWorkerId = "";
            container.OwnerWorkerIndex = ushort.MaxValue;
        }

        /// <summary>Move every entity still inside <paramref name="container"/> to the container around it (ignoring the departing box).</summary>
        private static void EvacuateEntities(Container container)
        {
            EntityScratch.Clear();
            EntityScratch.AddRange(container.Entities);
            foreach (var e in EntityScratch)
            {
                if (e == null) continue;
                var outer = Find(e.transform.position, container, 0, e);
                if (outer == container) outer = null;
                e.SetContainer(outer);
            }
            container.Entities.Clear();
        }

        // ---------------------------------------------------------------------------------------- runtime containers

        /// <summary>Prefix of a runtime container's string id (<c>rt_&lt;id&gt;</c>), the form leases and persistence records use.</summary>
        public const string RuntimeIdPrefix = "rt_";

        /// <summary>The string id a runtime container registered as <paramref name="id"/> has: <c>rt_&lt;id&gt;</c>.</summary>
        public static string RuntimeContainerId(ulong id) => RuntimeIdPrefix + id.ToString(CultureInfo.InvariantCulture);

        /// <summary>Whether a container id names a runtime container (<c>rt_&lt;id&gt;</c>).</summary>
        public static bool IsRuntimeId(string id) => TryParseRuntimeId(id, out _);

        /// <summary>The 64-bit id a runtime container id (<c>rt_&lt;id&gt;</c>) names.</summary>
        public static bool TryParseRuntimeId(string id, out ulong runtimeId)
        {
            runtimeId = 0;
            return id != null && id.Length > RuntimeIdPrefix.Length && id.StartsWith(RuntimeIdPrefix, StringComparison.Ordinal)
                && ulong.TryParse(id.Substring(RuntimeIdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out runtimeId);
        }

        /// <summary>The runtime container registered as <paramref name="id"/> on this process, or null.</summary>
        public static Container GetRuntime(ulong id) => RuntimeById.TryGetValue(id, out var c) ? c : null;

        /// <summary>
        /// Optional mapping from a public runtime container's stable ID to bounds in the current origin frame.
        /// Called during registration and origin shifts. Calculate the center from precise coordinates before
        /// converting to floats. Origin shifts use only the returned center; container sizes remain unchanged.
        /// Clear this callback when its world unloads. It is reset when a new play session starts.
        /// </summary>
        public static Func<ulong, Bounds, Bounds> RuntimeBoundsInFrame { get; set; }

        /// <summary>
        /// Register a static box that is not in the baked set, named by a 64-bit id the game chose (a chunk
        /// coordinate hash, a plot number) and axis-aligned in this process's frame. Legal at any time after boot,
        /// on any role; every process that should resolve the container has to register it under the same id with
        /// the same box, which is what <see cref="SyncRuntime"/> does from the control-plane leases. Registering an
        /// id that is already here returns the existing container (its box is updated if it changed). Adjacency to
        /// baked and runtime containers whose boxes touch is computed at once, so ghosting and handover across the
        /// seam work like between baked containers.
        /// </summary>
        public static Container RegisterRuntime(ulong id, Bounds frameBounds, InstanceContainerInfo instance = null)
        {
            if (instance == null && RuntimeBoundsInFrame != null) frameBounds = RuntimeBoundsInFrame(id, frameBounds);
            if (RuntimeById.TryGetValue(id, out var existing))
            {
                if (existing.WorldBounds != frameBounds)
                {
                    RemoveFromHash(existing);
                    existing.transform.position = frameBounds.center;
                    existing.Size = frameBounds.size;
                    existing.RefreshCache();
                    AddToHash(existing);
                    RelinkRuntimeNeighbors(existing);
                }
                return existing;
            }
            if (_runtimeRoot == null)
            {
                var root = new GameObject("RuntimeContainers");
                if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(root);
                _runtimeRoot = root.transform;
            }
            var go = new GameObject(RuntimeContainerId(id));
            go.transform.SetParent(_runtimeRoot, false);
            go.transform.position = frameBounds.center;
            var c = go.AddComponent<Container>();
            c.ContainerId = RuntimeContainerId(id);
            c.Size = frameBounds.size;
            c.Center = Vector3.zero;
            c.IsRuntime = true;
            c.RuntimeId = id;
            c.Instance = instance?.Copy();
            c.Index = ContainerRef.RuntimeIndex;
            c.RefreshCache();
            RuntimeList.Add(c);
            RuntimeById[id] = c;
            ById[c.ContainerId] = c;
            AddToHash(c);
            RelinkRuntimeNeighbors(c);
            if (PendingLeases.TryGetValue(c.ContainerId, out var lease))
            {
                PendingLeases.Remove(c.ContainerId);
                ApplyLease(c.ContainerId, lease.WorkerId, lease.WorkerIndex, lease.Epoch, lease.State);
            }
            RuntimeRegistered?.Invoke(c);
            return c;
        }

        /// <summary>
        /// Forget a runtime container and destroy its object. Whatever is still inside is moved to the container
        /// around it first; the game normally retires a container only once it is empty, and persists or despawns
        /// the rest before calling this. Neighbours drop their adjacency to it.
        /// </summary>
        public static bool UnregisterRuntime(ulong id)
        {
            if (!RuntimeById.TryGetValue(id, out var c)) return false;
            // A removed lease cannot silently move private occupants into the public world. An entity whose object was
            // destroyed without a despawn is no occupant: counting it would keep the box registered forever.
            if (c != null) c.Entities.RemoveAll(e => e == null);
            if (c != null && c.InstanceId != 0 && c.Entities.Count > 0) return false;
            if (c == null)
            {
                // Its object is already gone (a scene unload took it): just forget it.
                RuntimeById.Remove(id);
                RuntimeList.Remove(c);
                RehashRuntime();
                return true;
            }
            RuntimeUnregistering?.Invoke(c);
            InstanceScenes.Release(c);
            RuntimeById.Remove(id);
            RuntimeList.Remove(c);
            ById.Remove(c.ContainerId);
            RemoveFromHash(c);
            foreach (var n in c.Neighbors) n.Neighbors.Remove(c);
            c.Neighbors.Clear();
            EvacuateEntities(c);
            // Nothing networked may go down with the box: an entity that is still parented here (a ghost, a scene
            // object moved by hand) would be destroyed with it and leave a dead reference in every list that holds it.
            EntityScratch.Clear();
            c.GetComponentsInChildren(true, EntityScratch);
            foreach (var e in EntityScratch) if (e != null && e.transform.parent != null && e.transform.IsChildOf(c.transform)) e.transform.SetParent(null, true);
            EntityScratch.Clear();
            PendingLeases.Remove(c.ContainerId);
            c.IsRuntime = false;
            c.Index = ushort.MaxValue;
            if (c.gameObject != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(c.gameObject);
                else UnityEngine.Object.DestroyImmediate(c.gameObject);
            }
            return true;
        }

        private static void UnregisterAllRuntime()
        {
            RuntimeScratchIds.Clear();
            RuntimeScratchIds.AddRange(RuntimeById.Keys);
            foreach (var id in RuntimeScratchIds) UnregisterRuntime(id);
            RuntimeScratchIds.Clear();
        }

        /// <summary>
        /// Mirror the runtime containers the control plane names: register every lease that carries a box and is
        /// not here yet, forget every runtime container whose lease is gone. Boxes on the row are absolute; they are
        /// brought into this process's frame. Workers, the gateway and the orchestrator call this whenever the
        /// control plane changes, before applying the leases; clients do the same from the ownership message.
        /// </summary>
        public static void SyncRuntime(IReadOnlyList<LeaseInfo> leases)
        {
            RuntimeKeep.Clear();
            for (int i = 0; i < leases.Count; i++)
            {
                var l = leases[i];
                if (!l.HasBounds || !TryParseRuntimeId(l.ContainerId, out ulong id)) continue;
                RuntimeKeep.Add(id);
                if (!RuntimeById.ContainsKey(id)) RegisterRuntime(id, ToFrame(new Bounds(l.BoundsCenter, l.BoundsSize), l.Instance?.InstanceId ?? 0UL), l.Instance);
            }
            PruneRuntime(RuntimeKeep);
            RuntimeKeep.Clear();
        }

        /// <summary>Forget every runtime container whose id is not in <paramref name="keep"/>.</summary>
        public static void PruneRuntime(HashSet<ulong> keep)
        {
            RuntimeScratchIds.Clear();
            foreach (var id in RuntimeById.Keys) if (!keep.Contains(id)) RuntimeScratchIds.Add(id);
            foreach (var id in RuntimeScratchIds) UnregisterRuntime(id);
            RuntimeScratchIds.Clear();
        }

        /// <summary>An absolute box (as leases and telemetry carry it) in the public world's frame.</summary>
        public static Bounds ToFrame(Bounds absolute) => ToFrame(absolute, 0UL);

        /// <summary>
        /// An absolute box in the frame of the scope that owns it. A scope with a frame of its own (a scoped chunk
        /// grid) has its own origin cell, so its boxes are brought in relative to <i>that</i> origin; every other
        /// scope, and the public world, use <see cref="WorldOrigin"/> exactly as before (<c>docs/scope-frames.md</c>).
        /// </summary>
        public static Bounds ToFrame(Bounds absolute, ulong instanceId)
        {
            if (ScopeFrames.HasFrame(instanceId)) return new Bounds(absolute.center + ScopeFrames.Of(instanceId).OriginOffset, absolute.size);
            var world = WorldOrigin.Definition;
            if (world == null) return absolute;
            var origin = world.FrameOrigin(Vector3Int.zero, WorldOrigin.Cell); // where absolute (0,0,0) sits in this frame
            return new Bounds(absolute.center + origin, absolute.size);
        }

        /// <summary>A box in the public world's frame as an absolute box: the inverse of <see cref="ToFrame(Bounds)"/>.</summary>
        public static Bounds ToAbsolute(Bounds frame) => ToAbsolute(frame, 0UL);

        /// <summary>A box in a scope's own frame as an absolute box: the inverse of <see cref="ToFrame(Bounds,ulong)"/>.</summary>
        public static Bounds ToAbsolute(Bounds frame, ulong instanceId)
        {
            if (ScopeFrames.HasFrame(instanceId)) return new Bounds(frame.center - ScopeFrames.Of(instanceId).OriginOffset, frame.size);
            var world = WorldOrigin.Definition;
            if (world == null) return frame;
            var origin = world.FrameOrigin(Vector3Int.zero, WorldOrigin.Cell);
            return new Bounds(frame.center - origin, frame.size);
        }

        /// <summary>The public world's floating origin moved: every container in the public frame moves with it.</summary>
        public static void ShiftRuntime(Vector3 delta) => ShiftRuntime(0UL, delta);

        /// <summary>
        /// One frame's origin moved by <paramref name="delta"/>: only the runtime containers of that frame move, and
        /// the runtime hash is rebuilt. <paramref name="frameId"/> is 0 for the public frame — which is where every
        /// container whose scope has no frame of its own still lives — and a scope's isolation id for a scope that
        /// owns its origin (<c>docs/scope-frames.md</c> D2).
        /// </summary>
        public static void ShiftRuntime(ulong frameId, Vector3 delta)
        {
            if (RuntimeList.Count == 0) return;
            bool moved = false;
            for (int i = 0; i < RuntimeList.Count; i++)
            {
                var c = RuntimeList[i];
                if (c == null || ScopeFrames.FrameIdOf(c.InstanceId) != frameId) continue;
                // Resolve scoped grids from the container's isolation id: the public grid can unpack any id,
                // including an ordinary instance interior's hash. Custom public bounds hooks still apply.
                var shifted = new Bounds(c.transform.position + delta, c.WorldBounds.size);
                var grid = c.InstanceId != 0 ? NebulaChunks.GridOf(c) : null;
                c.transform.position = grid != null ? grid.BoundsOfId(c.RuntimeId, shifted).center
                    : c.InstanceId == 0 && RuntimeBoundsInFrame != null
                        ? RuntimeBoundsInFrame(c.RuntimeId, shifted).center
                        : shifted.center;
                c.RefreshCache();
                moved = true;
            }
            if (moved) RehashRuntime();
        }

        private static Vector3Int BucketOf(Vector3 p)
        {
            float s = _runtimeBucketSize;
            return new Vector3Int(Mathf.FloorToInt(p.x / s), Mathf.FloorToInt(p.y / s), Mathf.FloorToInt(p.z / s));
        }

        private static void AddToHash(Container c)
        {
            var b = c.WorldBounds;
            var min = BucketOf(b.min);
            var max = BucketOf(b.max);
            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                    {
                        var key = new Vector3Int(x, y, z);
                        if (!RuntimeHash.TryGetValue(key, out var list)) RuntimeHash[key] = list = new List<Container>();
                        list.Add(c);
                    }
        }

        private static void RemoveFromHash(Container c)
        {
            var b = c.WorldBounds;
            var min = BucketOf(b.min);
            var max = BucketOf(b.max);
            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                    {
                        var key = new Vector3Int(x, y, z);
                        if (!RuntimeHash.TryGetValue(key, out var list)) continue;
                        list.Remove(c);
                        if (list.Count == 0) RuntimeHash.Remove(key);
                    }
        }

        private static void RehashRuntime()
        {
            RuntimeHash.Clear();
            for (int i = 0; i < RuntimeList.Count; i++) AddToHash(RuntimeList[i]);
        }

        /// <summary>
        /// Rebuild the carried-container broad phase from the cached boxes, each padded by one bucket so a carrier
        /// that moves between this rebuild and a query is still found. O(carried containers); called at most once
        /// per tick, and only when something changed.
        /// </summary>
        private static void RehashDynamic()
        {
            _dynamicHashDirty = false;
            foreach (var list in DynamicHash.Values) list.Clear();
            for (int i = 0; i < DynamicList.Count; i++)
            {
                var c = DynamicList[i];
                var b = c.WorldBounds;
                var min = BucketOf(b.min - Vector3.one * _runtimeBucketSize);
                var max = BucketOf(b.max + Vector3.one * _runtimeBucketSize);
                for (int x = min.x; x <= max.x; x++)
                    for (int y = min.y; y <= max.y; y++)
                        for (int z = min.z; z <= max.z; z++)
                        {
                            var key = new Vector3Int(x, y, z);
                            if (!DynamicHash.TryGetValue(key, out var list)) DynamicHash[key] = list = new List<Container>();
                            list.Add(c);
                        }
            }
        }

        /// <summary>
        /// Distinct carried containers that could overlap <paramref name="bounds"/>, into <paramref name="result"/>
        /// (cleared first). A broad phase: the caller still tests each candidate against the exact box. Below
        /// <see cref="DynamicHashThreshold"/> containers the whole list is returned, which is both cheaper and
        /// exactly what the pre-hash code did.
        /// </summary>
        private static void CollectDynamicIn(Bounds bounds, List<Container> result)
        {
            result.Clear();
            if (DynamicList.Count == 0) return;
            if (DynamicList.Count <= DynamicHashThreshold) { result.AddRange(DynamicList); return; }
            if (_dynamicHashDirty) RehashDynamic();
            DynamicSeen.Clear();
            var min = BucketOf(bounds.min);
            var max = BucketOf(bounds.max);
            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                    {
                        if (!DynamicHash.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                        for (int i = 0; i < list.Count; i++) if (DynamicSeen.Add(list[i])) result.Add(list[i]);
                    }
            DynamicSeen.Clear();
        }

        /// <summary>Distinct runtime containers hashed into any bucket <paramref name="bounds"/> overlaps, into <paramref name="result"/> (cleared first).</summary>
        private static void CollectRuntimeIn(Bounds bounds, List<Container> result)
        {
            result.Clear();
            if (RuntimeHash.Count == 0) return;
            RuntimeSeen.Clear();
            var min = BucketOf(bounds.min);
            var max = BucketOf(bounds.max);
            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                    {
                        if (!RuntimeHash.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                        for (int i = 0; i < list.Count; i++) if (RuntimeSeen.Add(list[i])) result.Add(list[i]);
                    }
            RuntimeSeen.Clear();
        }

        /// <summary>Runtime containers hashed into the bucket holding <paramref name="p"/> and the 26 around it.</summary>
        private static void CollectRuntimeAround(Vector3 p, List<Container> result)
        {
            float s = _runtimeBucketSize;
            CollectRuntimeIn(new Bounds(p, new Vector3(2f * s, 2f * s, 2f * s)), result);
        }

        /// <summary>Recompute the adjacency of a runtime container to every baked and runtime container whose box touches it, both ways.</summary>
        private static void RelinkRuntimeNeighbors(Container c)
        {
            foreach (var n in c.Neighbors) n.Neighbors.Remove(c);
            c.Neighbors.Clear();
            var a = c.WorldBounds;
            a.Expand(0.05f);
            if (_grid != null)
            {
                var min = WorldOrigin.CellOf(a.min);
                var max = WorldOrigin.CellOf(a.max);
                for (int x = min.x - 1; x <= max.x + 1; x++)
                    for (int y = min.y - 1; y <= max.y + 1; y++)
                        for (int z = min.z - 1; z <= max.z + 1; z++)
                        {
                            if (!_grid.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                            for (int i = 0; i < list.Count; i++) if (a.Intersects(list[i].WorldBounds)) Link(c, list[i]);
                        }
            }
            else
            {
                for (int i = 0; i < Containers.Count; i++) if (a.Intersects(Containers[i].WorldBounds)) Link(c, Containers[i]);
            }
            CollectRuntimeIn(a, RuntimeCandidates);
            for (int i = 0; i < RuntimeCandidates.Count; i++)
            {
                var o = RuntimeCandidates[i];
                if (o != c && a.Intersects(o.WorldBounds)) Link(c, o);
            }
        }

        private static void Link(Container a, Container b)
        {
            if (a.InstanceId != b.InstanceId) return;
            if (!a.Neighbors.Contains(b)) a.Neighbors.Add(b);
            if (!b.Neighbors.Contains(a)) b.Neighbors.Add(a);
        }

        /// <summary>
        /// Every container adjacent to <paramref name="container"/> right now, into <paramref name="result"/>
        /// (cleared first): its static <see cref="Container.Neighbors"/> (baked and runtime) plus every dynamic
        /// container whose box currently touches it, and, for a dynamic container, the container its carrier sits in
        /// and every container its box touches. This is what the ghost band walks; use it instead of
        /// <see cref="Container.Neighbors"/> wherever a vehicle could be parked next door.
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
                // Only the carriers whose broad-phase buckets reach this box: a world with hundreds of vehicles
                // used to cost every static container a full scan of all of them, every tick, per entity.
                CollectDynamicIn(bounds, DynamicCandidates);
                for (int i = 0; i < DynamicCandidates.Count; i++)
                {
                    var d = DynamicCandidates[i];
                    if (d != container && d.InstanceId == container.InstanceId && bounds.Intersects(d.WorldBounds)) result.Add(d);
                }
                return;
            }
            var enclosing = container.Enclosing;
            if (enclosing != null) result.Add(enclosing);
            if (_grid != null)
            {
                CollectAround(WorldOrigin.CellOf(bounds.center), Candidates);
                foreach (var c in Candidates) if (c != enclosing && c.InstanceId == container.InstanceId && bounds.Intersects(c.WorldBounds)) result.Add(c);
            }
            else
            {
                for (int i = 0; i < Containers.Count; i++)
                {
                    var c = Containers[i];
                    if (c != enclosing && c.InstanceId == container.InstanceId && bounds.Intersects(c.WorldBounds)) result.Add(c);
                }
            }
            if (RuntimeList.Count > 0)
            {
                CollectRuntimeIn(bounds, RuntimeCandidates);
                for (int i = 0; i < RuntimeCandidates.Count; i++)
                {
                    var c = RuntimeCandidates[i];
                    if (c != enclosing && c.InstanceId == container.InstanceId && bounds.Intersects(c.WorldBounds)) result.Add(c);
                }
            }
            CollectDynamicIn(bounds, DynamicCandidates);
            for (int i = 0; i < DynamicCandidates.Count; i++)
            {
                var d = DynamicCandidates[i];
                if (d == container || d == enclosing || d.InstanceId != container.InstanceId) continue;
                if (bounds.Intersects(d.WorldBounds)) result.Add(d);
            }
        }

        // ---------------------------------------------------------------------------------------- lookups

        /// <summary>
        /// The container whose volume holds the point; if none does, the nearest one. Containers may nest (an
        /// "outdoor" box enclosing per-building boxes, a ship inside the outdoor box): when several hold the point
        /// the smallest volume wins, so the most specific container is chosen. Pure geometry, so every process
        /// agrees. <paramref name="exclude"/> leaves one container out of the search.
        /// <para>
        /// <paramref name="subject"/> is the entity being placed, when there is one. The containers it carries,
        /// directly or through any chain of carriers (<see cref="Container.IsCarriedBy"/>), are skipped: its own
        /// box (its origin is inside it), and the box of a ship whose interior overlaps its own and which already
        /// rides inside it. The next smallest box holding the point wins instead, so two carriers whose interiors
        /// overlap end up one inside the other at most, never each inside the other.
        /// </para>
        /// </summary>
        public static Container Find(Vector3 worldPosition, Container exclude = null, ulong instanceId = 0, NetworkIdentity subject = null)
        {
            Container inside = null, nearest = null;
            float insideVolume = float.MaxValue, nearestDist = float.MaxValue;
            if (_grid != null)
            {
                CollectAround(WorldOrigin.CellOf(worldPosition), Candidates);
                FindAmong(Candidates, worldPosition, exclude, instanceId, null, ref inside, ref insideVolume, ref nearest, ref nearestDist);
            }
            else FindAmong(Containers, worldPosition, exclude, instanceId, null, ref inside, ref insideVolume, ref nearest, ref nearestDist);
            if (RuntimeList.Count > 0)
            {
                CollectRuntimeAround(worldPosition, RuntimeCandidates);
                FindAmong(RuntimeCandidates, worldPosition, exclude, instanceId, null, ref inside, ref insideVolume, ref nearest, ref nearestDist);
            }
            // Only a dynamic container can be carried by the subject, so only this list pays for the check.
            FindAmong(DynamicList, worldPosition, exclude, instanceId, subject, ref inside, ref insideVolume, ref nearest, ref nearestDist);
            if (inside == null && nearest == null)
            {
                // Nothing near the point: fall back to the whole set so a far-away point still gets its nearest box.
                if (_grid != null) FindAmong(Containers, worldPosition, exclude, instanceId, null, ref inside, ref insideVolume, ref nearest, ref nearestDist);
                if (RuntimeList.Count > 0) FindAmong(RuntimeList, worldPosition, exclude, instanceId, null, ref inside, ref insideVolume, ref nearest, ref nearestDist);
            }
            return inside ?? nearest;
        }

        private static void FindAmong(List<Container> list, Vector3 worldPosition, Container exclude, ulong instanceId, NetworkIdentity subject, ref Container inside, ref float insideVolume, ref Container nearest, ref float nearestDist)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == exclude || c.InstanceId != instanceId) continue;
                float d = c.SignedDistance(worldPosition);
                if (d <= 0f)
                {
                    float volume = c.Volume;
                    // The chain walk only for a box that would win: most candidates lose on volume first.
                    if (volume < insideVolume && (subject == null || !c.IsCarriedBy(subject)))
                    {
                        insideVolume = volume;
                        inside = c;
                    }
                }
                else if (d < nearestDist && (subject == null || !c.IsCarriedBy(subject)))
                {
                    nearestDist = d;
                    nearest = c;
                }
            }
        }

        /// <summary>
        /// The container the entity should belong to after applying hysteresis: it must be at least
        /// <paramref name="hysteresis"/> meters inside a different container, and more than
        /// <paramref name="hysteresis"/> meters outside its current one, before we consider it moved.
        /// <paramref name="subject"/> is the entity being placed, as in <see cref="Find"/>: no container it carries,
        /// directly or through a chain of carriers, is ever the answer. A current container that is one (only
        /// possible from corrupt state) is left at once, with no hysteresis.
        /// </summary>
        public static Container Resolve(Vector3 worldPosition, Container current, float hysteresis, NetworkIdentity subject = null)
        {
            if (current != null && subject != null && current.IsDynamic && current.IsCarriedBy(subject))
                return Find(worldPosition, current, current.InstanceId, subject);
            if (current == null) return Find(worldPosition, null, 0, subject);
            var candidate = Find(worldPosition, null, current.InstanceId, subject);
            if (candidate == null || candidate == current) return current;
            if (current.Contains(worldPosition))
            {
                // Still inside the current box but a nested (smaller) container now claims the point: enter it once
                // we are past the hysteresis band.
                return candidate.SignedDistance(worldPosition) <= -hysteresis ? candidate : current;
            }
            // Outside the current box: stay until clearly out of it, through any face. Depth inside a box ignores the
            // floor (see Container.SignedDistance), so entering a nested box through its floor - a pawn at the top of
            // a ship's ramp - has no band on that side; without this one on the way out, an entity standing at the
            // floor would flip every tick, and every flip is a handover when the box belongs to another worker.
            if (current.SignedDistance(worldPosition) <= hysteresis) return current;
            return candidate.SignedDistance(worldPosition) <= -hysteresis ? candidate : current;
        }

        /// <summary>
        /// Every container whose box (grown by <paramref name="margin"/>) the segment <paramref name="a"/>-<paramref name="b"/>
        /// passes through, appended to <paramref name="result"/>. Nested containers are all reported: an entity in a
        /// building is in the building's box and in the outdoor box around it. This is what a hitscan or a line of
        /// sight uses to find out which workers, besides itself, could own something along the ray. In a gridded
        /// world only the cells the segment's bounds touch are visited; runtime containers are found through their
        /// hash and dynamic containers are always tested.
        /// </summary>
        public static void Along(Vector3 a, Vector3 b, float margin, List<Container> result, ulong instanceId = 0)
        {
            for (int i = 0; i < DynamicList.Count; i++)
                if (DynamicList[i].InstanceId == instanceId && DynamicList[i].IntersectsSegment(a, b, margin, out _, out _)) result.Add(DynamicList[i]);
            if (RuntimeList.Count > 0)
            {
                var box = new Bounds();
                box.SetMinMax(Vector3.Min(a, b) - Vector3.one * margin, Vector3.Max(a, b) + Vector3.one * margin);
                CollectRuntimeIn(box, RuntimeCandidates);
                for (int i = 0; i < RuntimeCandidates.Count; i++)
                    if (RuntimeCandidates[i].InstanceId == instanceId && RuntimeCandidates[i].IntersectsSegment(a, b, margin, out _, out _)) result.Add(RuntimeCandidates[i]);
            }
            if (_grid == null)
            {
                for (int i = 0; i < Containers.Count; i++)
                    if (Containers[i].InstanceId == instanceId && Containers[i].IntersectsSegment(a, b, margin, out _, out _)) result.Add(Containers[i]);
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
                            if (list[i].InstanceId == instanceId && list[i].IntersectsSegment(a, b, margin, out _, out _)) result.Add(list[i]);
                    }
        }

        /// <summary>
        /// Record a control-plane lease on its container. A lease for a dynamic or runtime container that is not
        /// resident yet is kept and applied when it registers. <paramref name="state"/> is the lease state
        /// (<see cref="LeaseState"/>); for a dynamic container only <see cref="LeaseState.Pinned"/> changes who owns it.
        /// </summary>
        public static void ApplyLease(string containerId, string workerId, ushort workerIndex, ulong epoch, string state = LeaseState.Active)
        {
            var c = FindById(containerId);
            if (c == null)
            {
                if (IsDynamicId(containerId) || IsRuntimeId(containerId)) PendingLeases[containerId] = new PendingLease { WorkerId = workerId ?? "", WorkerIndex = workerIndex, Epoch = epoch, State = state ?? "" };
                return;
            }
            c.OwnerWorkerId = workerId ?? "";
            c.OwnerWorkerIndex = workerIndex;
            c.LeaseEpoch = epoch;
            c.LeaseState = state ?? "";
        }

        /// <summary>A lease row went away (a carrier despawned, a runtime container was retired): forget whatever was recorded for it.</summary>
        public static void ForgetLease(string containerId)
        {
            PendingLeases.Remove(containerId);
            var c = FindById(containerId);
            if (c == null || (!c.IsDynamic && !c.IsRuntime)) return;
            c.OwnerWorkerId = "";
            c.OwnerWorkerIndex = ushort.MaxValue;
            c.LeaseState = "";
        }

        /// <summary>Tell listeners (world streaming, overlays) that a batch of <see cref="ApplyLease"/> calls is complete.</summary>
        public static void NotifyLeasesChanged() => LeasesChanged?.Invoke();
    }
}
