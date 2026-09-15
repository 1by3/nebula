using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// What a policy would do with a hypothetical number of workers: which containers each of them would hold, and
    /// how loaded that would leave them. The scaler compares the plan at N, N+1 and N-1 instead of guessing from an
    /// average (<see cref="WorkerScaler"/>).
    /// </summary>
    public sealed class AssignmentPlan
    {
        /// <summary>How many workers the plan was drawn for.</summary>
        public int WorkerCount;
        /// <summary>Synthetic worker id -> the containers it would hold.</summary>
        public readonly Dictionary<string, List<string>> Containers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        /// <summary>Synthetic worker id -> predicted utilization (the sum of its containers' attributed load).</summary>
        public readonly Dictionary<string, float> Load = new Dictionary<string, float>(StringComparer.Ordinal);
        /// <summary>The busiest worker's predicted utilization.</summary>
        public float Peak;
        /// <summary>Mean predicted utilization across the plan's workers.</summary>
        public float Mean;
        /// <summary>The busiest worker in the plan.</summary>
        public string PeakWorker = "";
        /// <summary>The heaviest single container on the busiest worker: the one to blame when splitting does not help.</summary>
        public string HeaviestContainer = "";
        /// <summary>That container's attributed utilization.</summary>
        public float HeaviestUtilization;
        /// <summary>Containers the policy left unassigned (a policy that ignores runtime containers, say).</summary>
        public int Unassigned;
    }

    /// <summary>
    /// Runs an <see cref="IAssignmentPolicy"/> as a dry run against a hypothetical worker count. Unity's Mono has no
    /// default interface methods, so this is an extension rather than a member of <see cref="IAssignmentPolicy"/>:
    /// every policy a game already wrote gains <c>Predict</c> without changing.
    /// </summary>
    public static class AssignmentPlanner
    {
        /// <summary>Prefix of the made-up worker ids a dry run deals to. They never reach the control plane.</summary>
        public const string SyntheticPrefix = "sim-";

        /// <summary>The id of the nth synthetic worker of a dry run.</summary>
        public static string SyntheticWorker(int index) => SyntheticPrefix + index.ToString();

        /// <summary>
        /// Deal <paramref name="input"/>'s containers across <paramref name="workerCount"/> empty workers and add up
        /// what each would carry. The dry run has no leases, so every container is an orphan the policy must place;
        /// the result is the layout the policy would converge on with that many workers, not the cost of getting
        /// there. Per-container load comes from <see cref="AssignmentInput.Utilization"/>.
        /// </summary>
        public static AssignmentPlan Predict(this IAssignmentPolicy policy, AssignmentInput input, int workerCount)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            var plan = new AssignmentPlan { WorkerCount = workerCount };
            if (input == null || workerCount <= 0) return plan;

            var workers = new List<WorkerInfo>(workerCount);
            for (int i = 0; i < workerCount; i++)
            {
                string id = SyntheticWorker(i);
                workers.Add(new WorkerInfo { WorkerId = id, WorkerIndex = (uint)(i + 1), Status = WorkerStatus.Ready, LastHeartbeat = DateTime.UtcNow });
                plan.Containers[id] = new List<string>();
                plan.Load[id] = 0f;
            }

            var dry = new AssignmentInput
            {
                Baked = input.Baked,
                Runtime = input.Runtime,
                Eligible = workers,
                Leases = Array.Empty<LeaseInfo>(),
                Occupancy = input.Occupancy,
                Utilization = input.Utilization,
                Hints = input.Hints,
                KeepOrder = input.KeepOrder,
            };

            var placed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var change in policy.Compute(dry))
                if (plan.Containers.ContainsKey(change.Value)) placed[change.Key] = change.Value;

            Account(plan, placed, input, input.Baked);
            Account(plan, placed, input, input.Runtime);

            foreach (var kv in plan.Load)
            {
                plan.Mean += kv.Value;
                if (kv.Value > plan.Peak || plan.PeakWorker == "") { plan.Peak = kv.Value; plan.PeakWorker = kv.Key; }
            }
            plan.Mean /= workerCount;
            foreach (string id in plan.Containers[plan.PeakWorker])
            {
                float u = UtilizationOf(input, id);
                if (u > plan.HeaviestUtilization || plan.HeaviestContainer == "") { plan.HeaviestUtilization = u; plan.HeaviestContainer = id; }
            }
            return plan;
        }

        /// <summary>
        /// Attributed utilization of one container as the planner weighs it: what was measured, scaled by
        /// <see cref="ContainerHint.CostMultiplier"/>. 0 when nothing was reported for it. Every place that adds up
        /// predicted load goes through here, so a hinted container is as heavy in a dry run as it is in a deal.
        /// </summary>
        public static float UtilizationOf(AssignmentInput input, string containerId)
        {
            if (input.Utilization == null || !input.Utilization.TryGetValue(containerId, out float u)) return 0f;
            return u * input.HintOf(containerId).EffectiveMultiplier;
        }

        private static void Account(AssignmentPlan plan, Dictionary<string, string> placed, AssignmentInput input, IReadOnlyList<Container> containers)
        {
            for (int i = 0; i < containers.Count; i++)
            {
                string id = containers[i].ContainerId;
                if (!placed.TryGetValue(id, out string worker)) { plan.Unassigned++; continue; }
                plan.Containers[worker].Add(id);
                plan.Load[worker] += UtilizationOf(input, id);
            }
        }
    }
}
