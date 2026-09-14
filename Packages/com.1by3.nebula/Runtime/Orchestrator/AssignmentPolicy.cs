using System;
using System.Collections.Generic;
using System.Linq;
using Nebula.World;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>What one worker last reported about one container: who is inside it (see <see cref="WorkerTelemetry"/>).</summary>
    public struct ContainerLoad
    {
        public int Players;
        public int Bots;
        public int ServerDriven;
        public int Other;
        public int Ghosts;
        /// <summary>The worker that reported it.</summary>
        public string WorkerId;
        /// <summary>When the report arrived, on the telemetry clock.</summary>
        public double ReceivedAt;

        public int Authoritative => Players + Bots + ServerDriven + Other;
    }

    /// <summary>
    /// How much simulating a container costs a worker, from what is inside it. Players are the expensive ones (an
    /// input stream, prediction, interest management), bots less so, server-driven entities less again; every
    /// container costs <see cref="Base"/> merely by being leased (its geometry, its scene entities, its ghosts).
    /// Ghosts are not counted: they are a consequence of the neighbours' load, not the container's own.
    /// </summary>
    [Serializable]
    public struct CostWeights
    {
        public float Base;
        public float Player;
        public float Bot;
        public float ServerDriven;
        public float Other;

        public static CostWeights Default => new CostWeights { Base = 1f, Player = 4f, Bot = 2f, ServerDriven = 1f, Other = 0.5f };

        public float Of(in ContainerLoad load) => Base + load.Players * Player + load.Bots * Bot + load.ServerDriven * ServerDriven + load.Other * Other;
    }

    /// <summary>Everything an assignment policy may look at. Built by the orchestrator once per pass.</summary>
    public sealed class AssignmentInput
    {
        /// <summary>The baked containers, in wire order (Morton order in a partitioned world).</summary>
        public IReadOnlyList<Container> Baked = Array.Empty<Container>();
        /// <summary>The runtime containers the control plane currently names.</summary>
        public IReadOnlyList<Container> Runtime = Array.Empty<Container>();
        /// <summary>Workers that may receive containers: live, not retiring.</summary>
        public IList<WorkerInfo> Eligible = Array.Empty<WorkerInfo>();
        /// <summary>Every lease row.</summary>
        public IReadOnlyList<LeaseInfo> Leases = Array.Empty<LeaseInfo>();
        /// <summary>The latest per-container load each worker reported, by container id. Containers nobody reported are absent.</summary>
        public IReadOnlyDictionary<string, ContainerLoad> Occupancy = new Dictionary<string, ContainerLoad>();
        /// <summary>The baked order is spatial (a partitioned world) and should be kept when dealing.</summary>
        public bool KeepOrder;

        /// <summary>The active, eligible owner of a container, or "".</summary>
        public string OwnerOf(string containerId)
        {
            for (int i = 0; i < Leases.Count; i++)
            {
                var l = Leases[i];
                if (l.ContainerId != containerId) continue;
                if (l.State != LeaseState.Active) return "";
                foreach (var w in Eligible) if (w.WorkerId == l.WorkerId) return l.WorkerId;
                return "";
            }
            return "";
        }
    }

    /// <summary>
    /// Decides which worker simulates which container. The orchestrator runs the policy every pass and applies the
    /// changes it returns through the control plane; a policy is a pure function of its input so it can be tested
    /// without a mesh. Nebula ships <see cref="BakedAssignmentPolicy"/> (deal by count, for authored and baked
    /// worlds) and <see cref="CostBalancedAssignmentPolicy"/> (deal by load, for worlds whose shape is decided at
    /// runtime); a game may install its own through <see cref="NebulaOrchestrator.Policy"/>.
    /// </summary>
    public interface IAssignmentPolicy
    {
        /// <summary>Shown on the dashboard.</summary>
        string Name { get; }
        /// <summary>The (container id, worker id) assignments that should change. Containers not mentioned keep their lease.</summary>
        List<KeyValuePair<string, string>> Compute(AssignmentInput input);
    }

    /// <summary>
    /// The original policy: baked containers are dealt as evenly as possible by count, sticky to their owner, in
    /// wire order when that order is spatial (<see cref="NebulaOrchestrator.ComputeAssignment"/>). Runtime
    /// containers stay with the worker that asked for them and only move when that worker is gone
    /// (<see cref="NebulaOrchestrator.ComputeRuntimeAssignment"/>).
    /// </summary>
    public sealed class BakedAssignmentPolicy : IAssignmentPolicy
    {
        public string Name => "baked";

        public List<KeyValuePair<string, string>> Compute(AssignmentInput input)
        {
            var ids = new List<string>(input.Baked.Count);
            for (int i = 0; i < input.Baked.Count; i++) ids.Add(input.Baked[i].ContainerId);
            var changes = NebulaOrchestrator.ComputeAssignment(ids, input.Eligible, input.Leases.ToList(), input.KeepOrder);
            changes.AddRange(NebulaOrchestrator.ComputeRuntimeAssignment(input.Leases, input.Eligible));
            return changes;
        }
    }

    /// <summary>
    /// Deal by load: every container (baked or runtime) costs what <see cref="CostWeights"/> says its occupants
    /// cost, containers are ordered along a Morton curve of their centres so neighbours sit next to each other, and
    /// the curve is cut into one contiguous run of roughly equal cost per worker. Each run goes to the worker that
    /// already holds most of it, so a rebalance moves containers at run edges and nothing else. Nothing moves at all
    /// while the most loaded worker is within <see cref="Threshold"/> of the average and every container has an
    /// owner; an orphan is then placed next to its neighbours' owner without disturbing anyone.
    /// </summary>
    public sealed class CostBalancedAssignmentPolicy : IAssignmentPolicy
    {
        /// <summary>Metres per Morton cell when ordering containers; coarse enough that a chunk's centre never straddles it.</summary>
        public const float OrderQuantum = 8f;

        public CostWeights Weights = CostWeights.Default;
        /// <summary>How far above the average a worker's cost may be before containers are re-dealt (0.3 = 30 %).</summary>
        public float Threshold = 0.3f;

        public string Name => "cost";

        private struct Item
        {
            public string Id;
            public ulong Key;
            public float Cost;
            public string Owner;
        }

        /// <summary>Cost of one container from what was last reported about it (<see cref="CostWeights.Base"/> when nothing was).</summary>
        public float CostOf(string containerId, IReadOnlyDictionary<string, ContainerLoad> occupancy)
        {
            return occupancy != null && occupancy.TryGetValue(containerId, out var load) ? Weights.Of(load) : Weights.Base;
        }

        /// <summary>Total cost of every container in <paramref name="input"/>: what the mesh as a whole is carrying.</summary>
        public float TotalCost(AssignmentInput input)
        {
            float total = 0f;
            for (int i = 0; i < input.Baked.Count; i++) total += CostOf(input.Baked[i].ContainerId, input.Occupancy);
            for (int i = 0; i < input.Runtime.Count; i++) total += CostOf(input.Runtime[i].ContainerId, input.Occupancy);
            return total;
        }

        /// <summary>Morton key of a container's centre, so a sort puts spatial neighbours next to each other.</summary>
        public static ulong OrderKey(Container c)
        {
            var p = ContainerRegistry.ToAbsolute(c.WorldBounds).center;
            return WorldGrid.Morton(new Vector3Int(Mathf.FloorToInt(p.x / OrderQuantum), Mathf.FloorToInt(p.y / OrderQuantum), Mathf.FloorToInt(p.z / OrderQuantum)));
        }

        public List<KeyValuePair<string, string>> Compute(AssignmentInput input)
        {
            var changes = new List<KeyValuePair<string, string>>();
            var workers = input.Eligible.OrderBy(w => w.WorkerIndex).Select(w => w.WorkerId).ToList();
            int k = workers.Count;
            if (k == 0) return changes;

            var items = new List<Item>(input.Baked.Count + input.Runtime.Count);
            Collect(input.Baked, input, items);
            Collect(input.Runtime, input, items);
            int n = items.Count;
            if (n == 0) return changes;
            items = items.OrderBy(it => it.Key).ThenBy(it => it.Id, StringComparer.Ordinal).ToList();

            float total = 0f;
            var load = workers.ToDictionary(w => w, w => 0f);
            bool anyUnowned = false;
            foreach (var it in items)
            {
                total += it.Cost;
                if (it.Owner == "") anyUnowned = true;
                else load[it.Owner] += it.Cost;
            }
            float target = total / k;
            float maxLoad = load.Values.Max();
            bool balanced = maxLoad <= target * (1f + Threshold) + 1e-3f;

            if (balanced)
            {
                if (!anyUnowned) return changes;
                // Only the orphans move: each goes to the owner of its nearest owned neighbour on the curve when that
                // worker has room, otherwise to the least loaded worker.
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Owner != "") continue;
                    string pick = NeighbourOwner(items, i);
                    if (pick == null || load[pick] + items[i].Cost > target * (1f + Threshold)) pick = LeastLoaded(workers, load);
                    load[pick] += items[i].Cost;
                    items[i] = new Item { Id = items[i].Id, Key = items[i].Key, Cost = items[i].Cost, Owner = pick };
                    changes.Add(new KeyValuePair<string, string>(items[i].Id, pick));
                }
                return changes;
            }

            // Re-cut the curve into k contiguous runs of roughly equal cost.
            var runs = new List<(int start, int end)>();
            float remaining = total;
            int runsLeft = k, at = 0;
            for (int r = 0; r < k; r++)
            {
                float runTarget = remaining / runsLeft;
                float acc = 0f;
                int start = at;
                while (at < n)
                {
                    float c = items[at].Cost;
                    if (acc > 0f && acc + c > runTarget && (acc + c - runTarget) > (runTarget - acc)) break; // closer without it
                    if (n - (at + 1) < runsLeft - 1) { acc += c; at++; break; } // leave one for each run still to come
                    acc += c;
                    at++;
                }
                runs.Add((start, at));
                remaining -= acc;
                runsLeft--;
            }

            // Each run goes to the worker already holding most of its cost; ties and leftovers by worker index.
            var runOwner = new string[runs.Count];
            var taken = new HashSet<string>();
            var claims = new List<(int run, string worker, float held)>();
            for (int r = 0; r < runs.Count; r++)
            {
                var held = new Dictionary<string, float>();
                for (int i = runs[r].start; i < runs[r].end; i++)
                {
                    string o = items[i].Owner;
                    if (o == "") continue;
                    held[o] = (held.TryGetValue(o, out float h) ? h : 0f) + items[i].Cost;
                }
                foreach (var kv in held) claims.Add((r, kv.Key, kv.Value));
            }
            foreach (var claim in claims.OrderByDescending(c => c.held).ThenBy(c => c.run))
            {
                if (runOwner[claim.run] != null || taken.Contains(claim.worker)) continue;
                runOwner[claim.run] = claim.worker;
                taken.Add(claim.worker);
            }
            var free = new Queue<string>(workers.Where(w => !taken.Contains(w)));
            for (int r = 0; r < runs.Count; r++)
            {
                if (runOwner[r] == null) runOwner[r] = free.Dequeue();
                for (int i = runs[r].start; i < runs[r].end; i++)
                    if (items[i].Owner != runOwner[r]) changes.Add(new KeyValuePair<string, string>(items[i].Id, runOwner[r]));
            }
            return changes;
        }

        private void Collect(IReadOnlyList<Container> containers, AssignmentInput input, List<Item> items)
        {
            for (int i = 0; i < containers.Count; i++)
            {
                var c = containers[i];
                items.Add(new Item { Id = c.ContainerId, Key = OrderKey(c), Cost = CostOf(c.ContainerId, input.Occupancy), Owner = input.OwnerOf(c.ContainerId) });
            }
        }

        private static string NeighbourOwner(List<Item> items, int i)
        {
            for (int d = 1; d < items.Count; d++)
            {
                if (i - d >= 0 && items[i - d].Owner != "") return items[i - d].Owner;
                if (i + d < items.Count && items[i + d].Owner != "") return items[i + d].Owner;
                if (i - d < 0 && i + d >= items.Count) break;
            }
            return null;
        }

        private static string LeastLoaded(List<string> workers, Dictionary<string, float> load)
        {
            string best = null;
            foreach (var w in workers) if (best == null || load[w] < load[best]) best = w;
            return best;
        }
    }
}
