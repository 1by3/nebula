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
        /// <summary>
        /// How much of a worker's tick budget each container is costing, by container id: its worker's p90
        /// utilization spread over the containers it leases in proportion to their cost-weighted occupancy
        /// (<see cref="WorkerLoadTracker.Attribute"/>). 0.4 means "four tenths of a tick". Containers nobody
        /// reported are absent. This is what the scaler's dry runs add up.
        /// </summary>
        public IReadOnlyDictionary<string, float> Utilization = new Dictionary<string, float>();
        /// <summary>
        /// What the game says about each container beyond what the mesh can measure, by container id
        /// (<see cref="ContainerHint"/>). The orchestrator merges the baked hints with the ones set while the mesh
        /// runs, the runtime value winning. Containers nobody hinted are absent, which reads as
        /// <see cref="ContainerHint.Default"/>.
        /// </summary>
        public IReadOnlyDictionary<string, ContainerHint> Hints = new Dictionary<string, ContainerHint>();
        /// <summary>The baked order is spatial (a partitioned world) and should be kept when dealing.</summary>
        public bool KeepOrder;

        /// <summary>The hint for a container, or <see cref="ContainerHint.Default"/> when nothing was said about it.</summary>
        public ContainerHint HintOf(string containerId)
        {
            return Hints != null && Hints.TryGetValue(containerId, out var h) ? h : ContainerHint.Default;
        }

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
        /// <summary>
        /// The smallest improvement in the busiest worker's measured utilization that justifies moving containers
        /// during play (0.1 = a tenth of a tick budget). Checked only when
        /// <see cref="AssignmentInput.Utilization"/> carries real measurements and every container already has an
        /// owner; an orphan is always placed.
        /// </summary>
        public float MinGain = 0.1f;
        /// <summary>
        /// How busy the busiest worker must already be before the <see cref="MinGain"/> veto applies at all (a
        /// fraction of a tick budget; half the default scale-out line). Below it the utilization signal is too
        /// small to show a gain of <see cref="MinGain"/> however lopsided the mesh is - a 3x cost imbalance over
        /// two nearly idle workers still measures as a few hundredths of a tick - and vetoing on it would freeze
        /// the layout until something is already hot. The cost-unit balance rule decides instead.
        /// </summary>
        public float MinGainFloor = 0.35f;
        /// <summary>
        /// TODO (phase 3): a container with a player within this many metres of the seam that would move is left
        /// alone for a pass rather than handed over mid-fight. The occupancy report carries counts, not positions -
        /// only the detail documents the map page asks for have positions - so the check needs a cheap per-container
        /// "nearest player to each face" figure from the worker before it can be implemented honestly.
        /// </summary>
        public float SeamGraceMeters = 10f;

        public string Name => "cost";

        /// <summary>
        /// One thing to deal: a container, or a whole affinity group treated as a single container. Ids are kept as
        /// a list because a group moves together; a singleton (the common case) carries one.
        /// </summary>
        private struct Item
        {
            public List<string> Ids;
            public ulong Key;
            public float Cost;
            public string Owner;
            /// <summary>Highest seam cost of the members: how much the planner is charged for cutting beside it.</summary>
            public float Seam;
            /// <summary>Any member asked for a worker to itself.</summary>
            public bool Dedicated;
            public string Id => Ids[0];
        }

        /// <summary>
        /// What the last <see cref="Compute"/> could not honour, for the dashboard and the log ("" when everything
        /// fitted). Today only one thing can go wrong: more <see cref="ContainerHint.Dedicated"/> containers than the
        /// mesh has workers to spare, in which case none of them is reserved and they share like anything else.
        /// </summary>
        public string Note { get; private set; } = "";

        /// <summary>Cost of one container from what was last reported about it (<see cref="CostWeights.Base"/> when nothing was).</summary>
        public float CostOf(string containerId, IReadOnlyDictionary<string, ContainerLoad> occupancy)
        {
            return occupancy != null && occupancy.TryGetValue(containerId, out var load) ? Weights.Of(load) : Weights.Base;
        }

        /// <summary>
        /// The same cost after <see cref="ContainerHint.CostMultiplier"/>: what the planner actually deals with. The
        /// multiplier is the game's correction for a container whose occupancy says little about its tick time.
        /// </summary>
        public float CostOf(string containerId, AssignmentInput input)
        {
            return CostOf(containerId, input.Occupancy) * input.HintOf(containerId).EffectiveMultiplier;
        }

        /// <summary>Total cost of every container in <paramref name="input"/>: what the mesh as a whole is carrying.</summary>
        public float TotalCost(AssignmentInput input)
        {
            float total = 0f;
            for (int i = 0; i < input.Baked.Count; i++) total += CostOf(input.Baked[i].ContainerId, input);
            for (int i = 0; i < input.Runtime.Count; i++) total += CostOf(input.Runtime[i].ContainerId, input);
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
            Note = "";
            var changes = new List<KeyValuePair<string, string>>();
            var workers = input.Eligible.OrderBy(w => w.WorkerIndex).Select(w => w.WorkerId).ToList();
            int k = workers.Count;
            if (k == 0) return changes;

            var items = new List<Item>(input.Baked.Count + input.Runtime.Count);
            Collect(input.Baked, input, items);
            Collect(input.Runtime, input, items);
            items = MergeGroups(items, input);
            int n = items.Count;
            if (n == 0) return changes;
            items = items.OrderBy(it => it.Key).ThenBy(it => it.Id, StringComparer.Ordinal).ToList();

            // Dedicated containers are taken off the curve first and each given a worker of its own, so the rest is
            // dealt across what is left. That only works while the mesh has a worker to spare for everything else;
            // with fewer workers than dedicated containers plus one, nothing is reserved and they share like any
            // other container (Note says so, and the dashboard shows it).
            var dedicated = new List<int>();
            for (int i = 0; i < n; i++) if (items[i].Dedicated) dedicated.Add(i);
            if (dedicated.Count > 0 && k < dedicated.Count + 1)
            {
                Note = $"{dedicated.Count} dedicated container(s) but only {k} worker(s): none is reserved until the mesh has {dedicated.Count + 1}";
                dedicated.Clear();
            }

            var shared = new List<string>(workers);           // workers the rest of the curve is dealt to
            if (dedicated.Count > 0)
            {
                var reservedOf = new Dictionary<int, string>();
                // Prefer the worker already holding it, so reserving a container does not move it for nothing.
                foreach (int i in dedicated)
                {
                    string owner = items[i].Owner;
                    if (owner == "" || !shared.Remove(owner)) continue;
                    reservedOf[i] = owner;
                }
                foreach (int i in dedicated)
                {
                    if (reservedOf.ContainsKey(i)) continue;
                    reservedOf[i] = shared[0];
                    shared.RemoveAt(0);
                }
                foreach (int i in dedicated)
                    if (items[i].Owner != reservedOf[i])
                        foreach (string id in items[i].Ids) changes.Add(new KeyValuePair<string, string>(id, reservedOf[i]));
                items = items.Where(it => !it.Dedicated).ToList();
                n = items.Count;
                if (n == 0) return changes;
            }
            int shareCount = shared.Count;

            float total = 0f;
            var load = workers.ToDictionary(w => w, w => 0f);
            bool anyUnowned = false, anyOwned = false;
            foreach (var it in items)
            {
                total += it.Cost;
                if (it.Owner == "") anyUnowned = true;
                else { anyOwned = true; if (load.ContainsKey(it.Owner)) load[it.Owner] += it.Cost; }
            }
            float target = total / shareCount;
            float maxLoad = shared.Max(w => load[w]);
            // Nobody owns anything (a fresh mesh, or a dry run of this policy): cut the curve properly instead of
            // filling workers one after another with the orphan rule.
            bool balanced = anyOwned && maxLoad <= target * (1f + Threshold) + 1e-3f;

            if (balanced)
            {
                if (!anyUnowned) return changes;
                // Only the orphans move: each goes to the owner of its nearest owned neighbour on the curve when that
                // worker has room, otherwise to the least loaded worker.
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Owner != "") continue;
                    string pick = NeighbourOwner(items, i);
                    if (pick == null || !shared.Contains(pick) || load[pick] + items[i].Cost > target * (1f + Threshold)) pick = LeastLoaded(shared, load);
                    load[pick] += items[i].Cost;
                    var placed = items[i];
                    placed.Owner = pick;
                    items[i] = placed;
                    foreach (string id in placed.Ids) changes.Add(new KeyValuePair<string, string>(id, pick));
                }
                return changes;
            }

            // Re-cut the curve into one contiguous run of roughly equal cost per sharing worker.
            var runs = new List<(int start, int end)>();
            float remaining = total;
            int runsLeft = shareCount, at = 0;
            for (int r = 0; r < shareCount; r++)
            {
                float runTarget = remaining / runsLeft;
                float acc = 0f;
                int start = at;
                while (at < n)
                {
                    float c = items[at].Cost;
                    // Cutting here splits two neighbours that both said a seam between them is expensive, so charge
                    // the cut a fraction of a worker's budget and let the run grow past its target instead. Zero
                    // when either side is indifferent, which is every container nobody hinted.
                    float seam = at > start ? Math.Min(items[at - 1].Seam, items[at].Seam) * target : 0f;
                    if (acc > 0f && acc + c > runTarget && (acc + c - runTarget) > (runTarget - acc) + seam) break; // closer without it
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
                    if (o == "" || !shared.Contains(o)) continue;
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
            var free = new Queue<string>(shared.Where(w => !taken.Contains(w)));
            for (int r = 0; r < runs.Count; r++) if (runOwner[r] == null) runOwner[r] = free.Dequeue();

            // Would the re-deal actually relieve the busiest worker? Handing containers over costs entity transfers
            // and a visible seam, so a move that buys less than MinGain of a tick is not worth making. Only checked
            // when every container has an owner (an orphan must be placed regardless) and the mesh has measured
            // utilization to compare with; in weight units alone there is no budget to be a fraction of.
            if (!anyUnowned && input.Utilization != null && input.Utilization.Count > 0)
            {
                var before = workers.ToDictionary(w => w, w => 0f);
                var after = workers.ToDictionary(w => w, w => 0f);
                for (int r = 0; r < runs.Count; r++)
                    for (int i = runs[r].start; i < runs[r].end; i++)
                    {
                        float u = 0f;
                        foreach (string id in items[i].Ids) u += AssignmentPlanner.UtilizationOf(input, id);
                        if (items[i].Owner != "" && before.ContainsKey(items[i].Owner)) before[items[i].Owner] += u;
                        after[runOwner[r]] += u;
                    }
                float beforeMax = before.Values.Max();
                if (beforeMax >= MinGainFloor && beforeMax - after.Values.Max() < MinGain) return changes;
            }

            for (int r = 0; r < runs.Count; r++)
            {
                for (int i = runs[r].start; i < runs[r].end; i++)
                    if (items[i].Owner != runOwner[r])
                        foreach (string id in items[i].Ids) changes.Add(new KeyValuePair<string, string>(id, runOwner[r]));
            }
            return changes;
        }

        private void Collect(IReadOnlyList<Container> containers, AssignmentInput input, List<Item> items)
        {
            for (int i = 0; i < containers.Count; i++)
            {
                var c = containers[i];
                var hint = input.HintOf(c.ContainerId);
                items.Add(new Item
                {
                    Ids = new List<string>(1) { c.ContainerId },
                    Key = OrderKey(c),
                    Cost = CostOf(c.ContainerId, input.Occupancy) * hint.EffectiveMultiplier,
                    Owner = input.OwnerOf(c.ContainerId),
                    Seam = hint.EffectiveSeamCost,
                    Dedicated = hint.Dedicated,
                });
            }
        }

        /// <summary>
        /// Fold the containers sharing an <see cref="ContainerHint.AffinityGroup"/> into one item, so the curve deals
        /// them to a single worker: costs add up, the group sits at its lowest member's Morton key (its first
        /// position on the curve), and it counts as owned only when every member is already on the same worker -
        /// a group that got split is an orphan the next pass puts back together. Returns the list unchanged when
        /// nothing carries a group, which is the common case.
        /// </summary>
        private static List<Item> MergeGroups(List<Item> items, AssignmentInput input)
        {
            Dictionary<string, int> groups = null;
            for (int i = 0; i < items.Count; i++)
            {
                string group = input.HintOf(items[i].Id).Group;
                if (group == "") continue;
                groups = groups ?? new Dictionary<string, int>(StringComparer.Ordinal);
                if (!groups.TryGetValue(group, out int at)) { groups[group] = i; continue; }
                var head = items[at];
                var member = items[i];
                head.Ids.AddRange(member.Ids);
                head.Cost += member.Cost;
                head.Key = Math.Min(head.Key, member.Key);
                head.Seam = Math.Max(head.Seam, member.Seam);
                head.Dedicated |= member.Dedicated;
                // One dissenting owner makes the whole group unowned, which is what puts it back on one worker.
                if (head.Owner != member.Owner) head.Owner = "";
                items[at] = head;
                items[i] = default;
            }
            if (groups == null) return items;
            var merged = new List<Item>(items.Count);
            for (int i = 0; i < items.Count; i++) if (items[i].Ids != null) merged.Add(items[i]);
            return merged;
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
