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
        /// <summary>
        /// What the worker's own tally of the entities inside came to, each one's category weight times its
        /// <see cref="NebulaCost"/> multiplier (docs/cost-telemetry.md). Valid only when
        /// <see cref="HasEntityCost"/>; <see cref="CostWeights.Base"/> is not in it.
        /// </summary>
        public float EntityCostSum;
        /// <summary>The report carried an entity cost sum, so <see cref="CostWeights.Of"/> uses it instead of counting heads.</summary>
        public bool HasEntityCost;

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

        /// <summary>
        /// What this container costs. When the worker reported a per-entity cost sum
        /// (<see cref="ContainerLoad.HasEntityCost"/>) that sum is used: it is these same category weights with each
        /// entity's own multiplier applied (<see cref="NebulaCost"/>), so a world that sets no weights gets exactly
        /// the number the head count below gives. Otherwise the heads are counted here, as they always were.
        /// </summary>
        public float Of(in ContainerLoad load) => Base + (load.HasEntityCost
            ? load.EntityCostSum
            : load.Players * Player + load.Bots * Bot + load.ServerDriven * ServerDriven + load.Other * Other);
    }

    /// <summary>
    /// One entity cohesion group as the orchestrator sees it: the union of what every worker reported about the
    /// members it owns (<see cref="NebulaWorker.CohesionSpan"/>, <c>docs/cohesion-hints.md</c> D7). The planner
    /// deals its containers as one item, so the group never ends up split across two workers.
    /// </summary>
    public sealed class CohesionGroupInfo
    {
        /// <summary>The group id the game chose (<c>NetworkIdentity.CohesionGroup</c>); never 0.</summary>
        public uint Group;
        /// <summary>How many members the mesh holds, across every worker that reported the group.</summary>
        public int Members;
        /// <summary>The containers its members are in, in report order. Members in no container name none.</summary>
        public readonly List<string> Containers = new List<string>(2);
        /// <summary>The workers that reported members. More than one means the group is split right now.</summary>
        public readonly List<string> Workers = new List<string>(1);

        /// <summary>The group as it is named in a log line, a telemetry row and on the dashboard.</summary>
        public override string ToString() => "cohesion " + Group.ToString();
    }

    /// <summary>
    /// A group of containers the planner had to keep on one worker but could not fit there
    /// (<c>docs/cohesion-hints.md</c>, D9). Reported, never acted on by splitting the group: the mesh keeps running
    /// with the group whole on an overloaded worker, and this row says so on the dashboard and in the log.
    /// </summary>
    public struct UnsplittableGroup
    {
        /// <summary>"cohesion 17" or "affinity dungeon-3": which kind of group and which one.</summary>
        public string Group;
        /// <summary>Its containers, as the planner dealt them.</summary>
        public string[] Containers;
        /// <summary>The measured utilization of the whole group, in tick budgets (1 = a whole worker).</summary>
        public float Utilization;
        /// <summary>What it was compared against (<see cref="CostBalancedAssignmentPolicy.MaxGroupUtilization"/>).</summary>
        public float Limit;

        /// <summary>One line for the log and the dashboard.</summary>
        public override string ToString() =>
            $"{Group} needs {Utilization:0.##} of a worker's tick budget across {(Containers != null ? Containers.Length : 0)} container(s), more than the {Limit:0.##} one worker can give it; it is kept whole and never split";
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
        /// <summary>
        /// What each container is measured to cost its worker, split into simulation, replication and gateway
        /// relay (<see cref="ContainerCost"/>, docs/cost-telemetry.md). Containers nobody reported are absent.
        /// The policies do not read it; the scaler does, to name what makes a hot container hot.
        /// </summary>
        public IReadOnlyDictionary<string, ContainerCost> Cost = new Dictionary<string, ContainerCost>();
        /// <summary>The baked order is spatial (a partitioned world) and should be kept when dealing.</summary>
        public bool KeepOrder;
        /// <summary>
        /// Containers a worker asked not to be rebalanced, by container id, with the seconds each hold still has
        /// to run on the orchestrator's clock (<see cref="NebulaWorker.HoldContainer(string, float)"/>,
        /// <c>docs/cohesion-hints.md</c> D8). Containers nobody holds are absent.
        /// </summary>
        public IReadOnlyDictionary<string, float> Holds = new Dictionary<string, float>();
        /// <summary>
        /// The entity cohesion groups the mesh is holding (<see cref="CohesionGroupInfo"/>). Their containers are
        /// dealt as one item, exactly like an <see cref="ContainerHint.AffinityGroup"/>, and are never split.
        /// </summary>
        public IReadOnlyList<CohesionGroupInfo> Cohesion = Array.Empty<CohesionGroupInfo>();

        /// <summary>Whether a container is under a live hold, so moving it now is deferred.</summary>
        public bool IsHeld(string containerId) => Holds != null && containerId != null && Holds.ContainsKey(containerId);

        /// <summary>How many seconds a container's hold still has to run, or 0 when it is not held.</summary>
        public float HoldSeconds(string containerId)
        {
            return Holds != null && containerId != null && Holds.TryGetValue(containerId, out float s) ? s : 0f;
        }

        /// <summary>
        /// Remove the changes that would move a held container, so a hold defers a rebalance whichever policy
        /// computed it. A hold never keeps an <b>orphan</b> where it is: a container with no live owner has to be
        /// placed, and the hold is about not being moved, not about staying unowned. Returns how many were dropped.
        /// </summary>
        public int DropHeldChanges(List<KeyValuePair<string, string>> changes)
        {
            if (changes == null || Holds == null || Holds.Count == 0) return 0;
            int dropped = 0;
            for (int i = changes.Count - 1; i >= 0; i--)
            {
                if (!IsHeld(changes[i].Key) || OwnerOf(changes[i].Key) == "") continue;
                changes.RemoveAt(i);
                dropped++;
            }
            return dropped;
        }

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
    public sealed class CostBalancedAssignmentPolicy : IAssignmentPolicy, IExplainsAssignment
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
            /// <summary>A member is under a live hold and the item already has an owner: it does not move this pass.</summary>
            public bool Held;
            /// <summary>The longest hold still to run over the members, in seconds; 0 when nothing is held.</summary>
            public float HoldSeconds;
            /// <summary>What made this item more than one container ("cohesion 17", "affinity hub"), or "".</summary>
            public string GroupLabel;
            /// <summary>The scope key of the first member ("" for the public world), for the saturation rows.</summary>
            public string Scope;
            public string Id => Ids[0];
        }

        /// <summary>
        /// A group (cohesion or affinity) whose containers cost more than one worker's tick budget is kept whole
        /// anyway and reported. This is that budget, as a fraction of a tick: 1 is "a whole worker". Raise it for a
        /// mesh that runs its workers past their tick budget on purpose; lower it to be told earlier.
        /// </summary>
        public float MaxGroupUtilization = 1f;

        /// <summary>
        /// What the last <see cref="Compute"/> could not honour, for the dashboard and the log ("" when everything
        /// fitted). Two things can go wrong: more <see cref="ContainerHint.Dedicated"/> containers than the mesh has
        /// workers to spare, in which case none of them is reserved and they share like anything else; and a group
        /// that does not fit one worker (<see cref="Unsplittable"/>), which is kept whole regardless.
        /// </summary>
        public string Note { get; private set; } = "";

        /// <summary>
        /// The groups the last <see cref="Compute"/> kept whole although they do not fit one worker
        /// (<c>docs/cohesion-hints.md</c>, D9). Empty when everything fitted. Read by the orchestrator for the log
        /// and the dashboard.
        /// </summary>
        public IReadOnlyList<UnsplittableGroup> Unsplittable => _unsplittable;

        /// <summary>
        /// How busy the busiest worker must be before the planner bothers to say why it cannot be relieved
        /// (a fraction of a tick budget; the scale-out line). Below it "nothing moved" is simply "nothing needed to".
        /// </summary>
        public float SaturationUtilization = 0.7f;

        /// <summary>
        /// Every move of the last <see cref="Compute"/> with the sentence that explains it
        /// (<c>docs/cohesion-rebalancing.md</c>). Same order and same containers as the returned change list.
        /// </summary>
        public IReadOnlyList<AssignmentMove> Moves => _moves;

        /// <summary>
        /// What the last <see cref="Compute"/> could not relieve, hottest first: the busiest worker is over
        /// <see cref="SaturationUtilization"/> and nothing it holds may be moved away, because a cohesion or
        /// affinity group spans it, because it is held, because it asked for a worker of its own, or because the
        /// game authored no boundary inside it. Empty when the mesh has measured no utilization.
        /// </summary>
        public IReadOnlyList<SaturationReport> Saturated => _saturated;

        private readonly List<UnsplittableGroup> _unsplittable = new List<UnsplittableGroup>();
        private readonly List<AssignmentMove> _moves = new List<AssignmentMove>();
        private readonly List<SaturationReport> _saturated = new List<SaturationReport>();

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
            _unsplittable.Clear();
            _moves.Clear();
            _saturated.Clear();
            List<Item> snapshot = null;
            var changes = Deal(input, ref snapshot);
            // Whatever the deal came to, say why the busiest worker is still hot when nothing it holds may move
            // (docs/cohesion-rebalancing.md, D5). Runs last so it can see the change list it is judging.
            ReportSaturation(snapshot, input, changes);
            return changes;
        }

        private List<KeyValuePair<string, string>> Deal(AssignmentInput input, ref List<Item> snapshot)
        {
            var changes = new List<KeyValuePair<string, string>>();
            var workers = input.Eligible.OrderBy(w => w.WorkerIndex).Select(w => w.WorkerId).ToList();
            int k = workers.Count;
            if (k == 0) return changes;

            var items = new List<Item>(input.Baked.Count + input.Runtime.Count);
            Collect(input.Baked, input, items);
            Collect(input.Runtime, input, items);
            items = MergeGroups(items, input);
            ReportUnsplittable(items, input);
            int n = items.Count;
            if (n == 0) return changes;
            items = items.OrderBy(it => it.Key).ThenBy(it => it.Id, StringComparer.Ordinal).ToList();
            // A copy of the items as they stand before anything is dealt: who owns what now, what each one costs and
            // what binds it. The saturation report judges the mesh as it is, not as this pass leaves it.
            snapshot = new List<Item>(items);

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
                    if (items[i].Owner != reservedOf[i] && !items[i].Held)
                        Move(changes, items[i], reservedOf[i],
                            $"'dedicated' hint: {items[i].Id} is reserved a worker of its own, and {reservedOf[i]} is free for it");
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
                    string pick = NeighbourOwner(items, i), why = "next to its neighbour on the curve";
                    if (pick == null || !shared.Contains(pick) || load[pick] + items[i].Cost > target * (1f + Threshold))
                    {
                        pick = LeastLoaded(shared, load);
                        why = "the least loaded worker";
                    }
                    load[pick] += items[i].Cost;
                    var placed = items[i];
                    placed.Owner = pick;
                    items[i] = placed;
                    Move(changes, placed, pick,
                        $"{Describe(placed)} has no owner; placed on {pick}, {why} (it costs {placed.Cost:0.#} of the {target:0.#} each worker is carrying)"
                        + Constrained(placed));
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
            bool measured = input.Utilization != null && input.Utilization.Count > 0;
            float beforeMax = 0f, afterMax = 0f;
            if (measured)
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
                beforeMax = before.Values.Max();
                afterMax = after.Values.Max();
                if (!anyUnowned && beforeMax >= MinGainFloor && beforeMax - afterMax < MinGain) return changes;
            }

            string relief = measured
                ? $"busiest worker {beforeMax:0.00} -> {afterMax:0.00} of a tick"
                : $"{target:0.#} cost units per worker";
            for (int r = 0; r < runs.Count; r++)
            {
                // Which authored boundary this run starts at: the cut falls between two neighbours on the curve, and
                // the seam hints are what decided it could fall there (docs/cohesion-rebalancing.md, D3).
                if (runs[r].start >= runs[r].end) continue; // more workers than items: this one gets nothing
                string boundary = runs[r].start == 0
                    ? "the start of the curve"
                    : $"the boundary between {items[runs[r].start - 1].Id} and {items[runs[r].start].Id}";
                float runCost = 0f;
                for (int i = runs[r].start; i < runs[r].end; i++) runCost += items[i].Cost;
                for (int i = runs[r].start; i < runs[r].end; i++)
                    // A held item stays with the worker it is on until its hold expires (docs/cohesion-hints.md, D8).
                    if (items[i].Owner != runOwner[r] && !items[i].Held)
                        Move(changes, items[i], runOwner[r],
                            $"re-deal along {boundary}: {Describe(items[i])} joins the run {runOwner[r]} takes "
                            + $"({runs[r].end - runs[r].start} item(s), {runCost:0.#} of a {target:0.#} target); {relief}"
                            + Constrained(items[i]));
            }
            return changes;
        }

        /// <summary>
        /// Record one item's move and the sentence that explains it, then add its containers to the change list. A
        /// group moves as one item, so every container of it gets the same explanation and they read as one decision
        /// on the dashboard.
        /// </summary>
        private void Move(List<KeyValuePair<string, string>> changes, in Item item, string to, string reason)
        {
            foreach (string id in item.Ids)
            {
                changes.Add(new KeyValuePair<string, string>(id, to));
                _moves.Add(new AssignmentMove { ContainerId = id, From = item.Owner ?? "", To = to, Reason = reason });
            }
        }

        /// <summary>"c4", or "cohesion 7 (c1, c2)" when the item is a whole group.</summary>
        private static string Describe(in Item item) =>
            item.Ids.Count == 1 ? item.Id
                : (item.GroupLabel != "" ? item.GroupLabel : "the group at " + item.Id) + " (" + AssignmentText.List(item.Ids) + ")";

        /// <summary>The trailing clause that names what constrained the move, or "" when nothing did.</summary>
        private static string Constrained(in Item item)
        {
            if (item.Ids.Count < 2) return "";
            return $"; {(item.GroupLabel != "" ? item.GroupLabel : "the group")} spans {AssignmentText.List(item.Ids)} and is never cut, so they move together";
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
                    // A hold defers a move, so it only means anything while the container has an owner to stay with.
                    Held = input.IsHeld(c.ContainerId) && input.OwnerOf(c.ContainerId) != "",
                    HoldSeconds = input.HoldSeconds(c.ContainerId),
                    GroupLabel = "",
                    Scope = c.ScopeKey ?? "",
                });
            }
        }

        /// <summary>
        /// Fold the containers that must be dealt together into one item, so the curve gives them to a single
        /// worker: costs add up, the item sits at its lowest member's Morton key (its first position on the curve),
        /// and it counts as owned only when every member is already on the same worker - an item that got split is
        /// an orphan the next pass puts back together. Two things bind containers together, and they compose: an
        /// <see cref="ContainerHint.AffinityGroup"/> on the hint, and an entity cohesion group whose members sit in
        /// more than one container (<see cref="AssignmentInput.Cohesion"/>, <c>docs/cohesion-hints.md</c> D7). A
        /// container in both is in one item with everything either of them names. Returns the list unchanged when
        /// nothing binds anything, which is the common case.
        /// </summary>
        private static List<Item> MergeGroups(List<Item> items, AssignmentInput input)
        {
            var bindings = Bindings(items, input);
            if (bindings == null) return items;

            // Union-find over item indices: a container named by two groups joins them into one item.
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++) index[items[i].Id] = i;
            var parent = new int[items.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            var label = new string[items.Count];
            bool any = false;
            foreach (var binding in bindings)
            {
                int head = -1;
                for (int m = 0; m < binding.Value.Count; m++)
                {
                    if (!index.TryGetValue(binding.Value[m], out int i)) continue; // a container the planner does not deal
                    int root = Find(i);
                    if (head < 0) { head = root; label[root] = label[root] ?? binding.Key; continue; }
                    if (root == head) continue;
                    parent[root] = head;
                    label[head] = label[head] ?? binding.Key;
                    any = true;
                }
            }
            if (!any) return items;

            var merged = new List<Item>(items.Count);
            var slotOf = new Dictionary<int, int>();
            for (int i = 0; i < items.Count; i++)
            {
                int root = Find(i);
                if (!slotOf.TryGetValue(root, out int slot))
                {
                    var first = items[root];
                    first.GroupLabel = label[root] ?? "";
                    slotOf[root] = slot = merged.Count;
                    merged.Add(first);
                    if (root == i) continue;
                }
                if (root == i) continue;
                var head = merged[slot];
                var member = items[i];
                head.Ids.AddRange(member.Ids);
                head.Cost += member.Cost;
                head.Key = Math.Min(head.Key, member.Key);
                head.Seam = Math.Max(head.Seam, member.Seam);
                head.Dedicated |= member.Dedicated;
                // Holding any member holds the whole item: honouring the hold on one container while its group
                // moves would be the split the group exists to prevent.
                head.Held |= member.Held;
                head.HoldSeconds = Math.Max(head.HoldSeconds, member.HoldSeconds);
                // One dissenting owner makes the whole item unowned, which is what puts it back on one worker.
                if (head.Owner != member.Owner) head.Owner = "";
                merged[slot] = head;
            }
            for (int i = 0; i < merged.Count; i++)
            {
                var item = merged[i];
                if (item.Owner == "") item.Held = false; // an item nobody owns is placed, hold or not
                merged[i] = item;
            }
            return merged;
        }

        /// <summary>
        /// Every set of containers that must land on one worker, labelled for the reports: the affinity groups on
        /// the hints and the cohesion groups the workers reported. Null when there are none.
        /// </summary>
        private static List<KeyValuePair<string, List<string>>> Bindings(List<Item> items, AssignmentInput input)
        {
            List<KeyValuePair<string, List<string>>> bindings = null;
            Dictionary<string, List<string>> affinity = null;
            for (int i = 0; i < items.Count; i++)
            {
                string group = input.HintOf(items[i].Id).Group;
                if (group == "") continue;
                affinity = affinity ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (!affinity.TryGetValue(group, out var list)) affinity[group] = list = new List<string>(2);
                list.Add(items[i].Id);
            }
            if (affinity != null)
            {
                bindings = new List<KeyValuePair<string, List<string>>>(affinity.Count);
                foreach (var kv in affinity) bindings.Add(new KeyValuePair<string, List<string>>("affinity " + kv.Key, kv.Value));
            }
            if (input.Cohesion != null)
            {
                for (int i = 0; i < input.Cohesion.Count; i++)
                {
                    var group = input.Cohesion[i];
                    if (group == null || group.Containers.Count < 2) continue; // one container is already one item
                    bindings = bindings ?? new List<KeyValuePair<string, List<string>>>(1);
                    bindings.Add(new KeyValuePair<string, List<string>>(group.ToString(), group.Containers));
                }
            }
            return bindings;
        }

        /// <summary>
        /// Name the items that had to be kept on one worker but cost more than one worker can give them
        /// (<see cref="MaxGroupUtilization"/>). Nothing is done about it here: splitting the group is exactly what
        /// the hint forbids, so the planner deals it whole and the mesh is told (<c>docs/cohesion-hints.md</c>, D9).
        /// Measured utilization is the yardstick; a mesh that has reported none yet reports nothing.
        /// </summary>
        private void ReportUnsplittable(List<Item> items, AssignmentInput input)
        {
            if (input.Utilization == null || input.Utilization.Count == 0) return;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Ids.Count < 2) continue;
                float utilization = 0f;
                foreach (string id in items[i].Ids) utilization += AssignmentPlanner.UtilizationOf(input, id);
                if (utilization <= MaxGroupUtilization) continue;
                _unsplittable.Add(new UnsplittableGroup
                {
                    Group = items[i].GroupLabel != "" ? items[i].GroupLabel : "group of " + items[i].Id,
                    Containers = items[i].Ids.ToArray(),
                    Utilization = utilization,
                    Limit = MaxGroupUtilization,
                });
            }
            if (_unsplittable.Count == 0) return;
            string note = _unsplittable[0].ToString();
            if (_unsplittable.Count > 1) note += $" (and {_unsplittable.Count - 1} more)";
            Note = Note == "" ? note : Note + "; " + note;
        }

        /// <summary>
        /// Say why the busiest worker is still over <see cref="SaturationUtilization"/> when this pass moved nothing
        /// off it (<c>docs/cohesion-rebalancing.md</c>, D5). Two kinds of row: a group that does not fit one worker
        /// at all, which is saturated whoever holds it, and the heaviest item on the busiest worker when that item
        /// alone would keep the worker hot however the rest is re-dealt. The cause names the constraint the planner
        /// refuses to break - a group, a hold, a reservation - or says there is no boundary to cut along.
        /// </summary>
        private void ReportSaturation(List<Item> items, AssignmentInput input, List<KeyValuePair<string, string>> changes)
        {
            if (items == null || items.Count == 0 || input.Utilization == null || input.Utilization.Count == 0) return;

            var reported = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Ids.Count < 2) continue;
                float groupUtilization = Utilization(items[i], input);
                if (groupUtilization <= MaxGroupUtilization) continue;
                _saturated.Add(Report(items[i], input, groupUtilization));
                reported.Add(items[i].Id);
            }

            // The busiest worker as it stands now, from the owners the pass started with.
            var load = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Owner == "") continue;
                load.TryGetValue(items[i].Owner, out float sum);
                load[items[i].Owner] = sum + Utilization(items[i], input);
            }
            string hot = "";
            float hottest = 0f;
            foreach (var kv in load)
                if (kv.Value > hottest || (kv.Value == hottest && string.CompareOrdinal(kv.Key, hot) < 0)) { hot = kv.Key; hottest = kv.Value; }
            if (hot == "" || hottest < SaturationUtilization) return;
            // Something is moving off it: it is being relieved, so there is nothing to explain this pass.
            for (int i = 0; i < changes.Count; i++)
                if (changes[i].Value != hot && input.OwnerOf(changes[i].Key) == hot) return;

            int blocker = -1;
            float blockerUtilization = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Owner != hot) continue;
                float u = Utilization(items[i], input);
                if (blocker >= 0 && u <= blockerUtilization) continue;
                blocker = i;
                blockerUtilization = u;
            }
            // One item has to be hot enough on its own that moving everything else away would not bring the worker
            // under the line. Otherwise the worker is merely unbalanced, which is not saturation.
            if (blocker < 0 || blockerUtilization < SaturationUtilization || reported.Contains(items[blocker].Id)) return;
            _saturated.Add(Report(items[blocker], input, blockerUtilization));
        }

        /// <summary>One saturation row for an item that may not be split, with the cause and the sentence.</summary>
        private static SaturationReport Report(in Item item, AssignmentInput input, float utilization)
        {
            // The heaviest container of the item: what an operator should open first.
            string heaviest = item.Id;
            float best = -1f;
            foreach (string id in item.Ids)
            {
                float u = AssignmentPlanner.UtilizationOf(input, id);
                if (u > best) { best = u; heaviest = id; }
            }
            SaturationCause cause;
            string reason;
            if (item.Ids.Count > 1)
            {
                cause = (item.GroupLabel ?? "").StartsWith("affinity", StringComparison.Ordinal) ? SaturationCause.AffinityGroup : SaturationCause.CohesionGroup;
                string label = item.GroupLabel != "" ? item.GroupLabel : "the group at " + item.Id;
                reason = $"{label} spans {AssignmentText.List(item.Ids)}, so cutting between them would split it; the item needs "
                    + $"{utilization:0.00} of a tick and is kept whole";
            }
            else if (item.Dedicated)
            {
                cause = SaturationCause.Dedicated;
                reason = $"{item.Id} asked for a worker of its own ('dedicated' hint) and needs {utilization:0.00} of that worker's tick on its own";
            }
            else if (item.Held)
            {
                cause = SaturationCause.Held;
                reason = $"{item.Id} is held for another {item.HoldSeconds:0.#} s and is not moved until the hold expires";
            }
            else
            {
                cause = SaturationCause.NoBoundary;
                reason = $"no boundary hint: {item.Id} carries {utilization:0.00} of a tick on its own and the game authored no boundary inside it "
                    + "(bake it as more cells, register runtime containers, or hint a seam); Nebula never subdivides a container";
            }
            return new SaturationReport
            {
                ContainerId = heaviest,
                ScopeKey = item.Scope ?? "",
                Containers = item.Ids.ToArray(),
                WorkerId = item.Owner ?? "",
                Utilization = utilization,
                Cause = cause,
                Reason = reason,
            };
        }

        /// <summary>The measured utilization of a whole item: what its containers cost the worker that holds them.</summary>
        private static float Utilization(in Item item, AssignmentInput input)
        {
            float u = 0f;
            foreach (string id in item.Ids) u += AssignmentPlanner.UtilizationOf(input, id);
            return u;
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
