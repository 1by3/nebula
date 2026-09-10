using System;
using System.Collections.Generic;
using System.Linq;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The container graph of the loaded level: every <see cref="Container"/>, indexed deterministically so all
    /// processes agree on indices, plus adjacency. Adjacent = bounds touch or overlap.
    /// <para>Two ways to populate it: <see cref="Rebuild"/> scans the loaded scene and sorts by id (single-scene
    /// games), or <see cref="Load"/> takes an already ordered list built from a <see cref="WorldContainerManifest"/>
    /// (partitioned worlds), in which case lookups go through a grid of cells instead of a linear scan.</para>
    /// </summary>
    public static class ContainerRegistry
    {
        private static readonly List<Container> Containers = new List<Container>();
        private static readonly Dictionary<string, Container> ById = new Dictionary<string, Container>();
        private static Dictionary<Vector3Int, List<Container>> _grid;
        private static readonly List<Container> Candidates = new List<Container>();

        public static IReadOnlyList<Container> All => Containers;
        public static int Count => Containers.Count;
        /// <summary>A partitioned world's manifest is loaded: containers know their cell and lookups use the grid.</summary>
        public static bool IsGridded => _grid != null;
        public static event Action Rebuilt;
        /// <summary>Raised after a batch of leases was applied (the worker and client do this whenever the control plane changes).</summary>
        public static event Action LeasesChanged;

        /// <summary>Scan the loaded scene(s) for containers and index them by id.</summary>
        public static void Rebuild()
        {
            var found = UnityEngine.Object.FindObjectsByType<Container>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .OrderBy(c => c.ContainerId, StringComparer.Ordinal)
                .ToList();
            Load(found, gridded: false);
        }

        /// <summary>
        /// Index <paramref name="ordered"/> as given (position = wire index). With <paramref name="gridded"/> every
        /// container must carry its <see cref="Container.Cell"/>; adjacency is then only tested between containers
        /// of neighbouring cells and <see cref="Find"/> only visits the cells around the point.
        /// </summary>
        public static void Load(IList<Container> ordered, bool gridded)
        {
            Containers.Clear();
            ById.Clear();
            _grid = gridded ? new Dictionary<Vector3Int, List<Container>>() : null;
            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                if (ById.ContainsKey(c.ContainerId))
                    throw new InvalidOperationException($"Duplicate container id '{c.ContainerId}'");
                c.Index = (ushort)i;
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

        public static Container Get(ushort index) => index < Containers.Count ? Containers[index] : null;

        /// <summary>Re-read every container's transform (the worker does this once per tick; see <see cref="Container.RefreshCache"/>).</summary>
        public static void RefreshCaches()
        {
            for (int i = 0; i < Containers.Count; i++) Containers[i].RefreshCache();
        }

        public static Container FindById(string id) => id != null && ById.TryGetValue(id, out var c) ? c : null;

        /// <summary>
        /// The container whose volume holds the point; if none does, the nearest one. Containers may nest (an
        /// "outdoor" box enclosing per-building boxes): when several hold the point the smallest volume wins, so the
        /// most specific container is chosen. Pure geometry, so every process agrees.
        /// </summary>
        public static Container Find(Vector3 worldPosition)
        {
            if (_grid != null)
            {
                CollectAround(WorldOrigin.CellOf(worldPosition), Candidates);
                var hit = FindAmong(Candidates, worldPosition);
                if (hit != null) return hit;
            }
            return FindAmong(Containers, worldPosition);
        }

        private static Container FindAmong(List<Container> list, Vector3 worldPosition)
        {
            Container inside = null;
            float insideVolume = float.MaxValue;
            Container nearest = null;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
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
            return inside ?? nearest;
        }

        /// <summary>
        /// The container the entity should belong to after applying hysteresis: it must be at least
        /// <paramref name="hysteresis"/> metres inside a different container before we consider it moved.
        /// </summary>
        public static Container Resolve(Vector3 worldPosition, Container current, float hysteresis)
        {
            if (current == null) return Find(worldPosition);
            var candidate = Find(worldPosition);
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

        public static void ApplyLease(string containerId, string workerId, ushort workerIndex, ulong epoch)
        {
            var c = FindById(containerId);
            if (c == null) return;
            c.OwnerWorkerId = workerId ?? "";
            c.OwnerWorkerIndex = workerIndex;
            c.LeaseEpoch = epoch;
        }

        /// <summary>Tell listeners (world streaming, overlays) that a batch of <see cref="ApplyLease"/> calls is complete.</summary>
        public static void NotifyLeasesChanged() => LeasesChanged?.Invoke();
    }
}
