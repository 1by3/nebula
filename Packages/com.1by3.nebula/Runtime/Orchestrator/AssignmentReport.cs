using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// One container the planner decided to hand to another worker, with the sentence that explains why
    /// (<c>docs/cohesion-rebalancing.md</c>). The moves of one pass are the change list the orchestrator applies,
    /// annotated: which authored boundary the cut fell on, what the two workers were carrying, and which cohesion
    /// or affinity group forced the cut to be where it is.
    /// </summary>
    public struct AssignmentMove
    {
        /// <summary>The container that changes hands.</summary>
        public string ContainerId;
        /// <summary>The worker that holds it now, or "" when nobody does (an orphan being placed).</summary>
        public string From;
        /// <summary>The worker it is being given to.</summary>
        public string To;
        /// <summary>
        /// Why, in one sentence a human can act on. Never empty for a move a built-in policy produced; a policy a
        /// game wrote itself may leave it empty, and the dashboard then shows the bare move.
        /// </summary>
        public string Reason;

        public override string ToString() =>
            (From == "" ? $"place {ContainerId} on {To}" : $"move {ContainerId} {From} -> {To}") + (string.IsNullOrEmpty(Reason) ? "" : ": " + Reason);
    }

    /// <summary>Why the planner could not take load off the busiest worker (<see cref="SaturationReport"/>).</summary>
    public enum SaturationCause
    {
        /// <summary>Nothing is saturated.</summary>
        None = 0,
        /// <summary>
        /// One container carries the load and the game authored no boundary inside it. Nebula never subdivides a
        /// container: the fix is a finer bake, a runtime container, or a <c>seam</c>/<c>x2</c> hint next door.
        /// </summary>
        NoBoundary = 1,
        /// <summary>An entity cohesion group spans the containers, so cutting between them would split the group.</summary>
        CohesionGroup = 2,
        /// <summary>A <see cref="ContainerHint.AffinityGroup"/> binds the containers, so they are dealt as one.</summary>
        AffinityGroup = 3,
        /// <summary>Every container that could have moved is under a hold that has not expired.</summary>
        Held = 4,
        /// <summary>The container asked for a worker of its own, and it already has one.</summary>
        Dedicated = 5,
    }

    /// <summary>
    /// The planner's answer to "why is this still hot?". Emitted when the busiest worker's load cannot be reduced by
    /// moving anything it holds, and read by <see cref="WorkerScaler"/> (which names it in
    /// <see cref="ScaleDecision.Reason"/>), by the dashboard, and by admission reporting, which groups the rows by
    /// <see cref="ScopeKey"/>. A report is a statement, never an action: Nebula does not split a container, and
    /// never breaks a group or a hold to relieve a worker (<c>docs/cohesion-rebalancing.md</c>, D5).
    /// </summary>
    public struct SaturationReport
    {
        /// <summary>The heaviest container of the item that cannot move: what the operator should look at first.</summary>
        public string ContainerId;
        /// <summary>The scope the container belongs to ("" for the public world), so the rows group per scope.</summary>
        public string ScopeKey;
        /// <summary>Every container of the item, including <see cref="ContainerId"/>.</summary>
        public string[] Containers;
        /// <summary>The worker that is carrying it, or "" when the item has no owner.</summary>
        public string WorkerId;
        /// <summary>What the whole item costs its worker, in tick budgets (1 = a whole worker).</summary>
        public float Utilization;
        /// <summary>Which constraint stops the planner.</summary>
        public SaturationCause Cause;
        /// <summary>The cause as a sentence: "cohesion 7 spans c0, c3", "held for another 4.2 s", "no boundary hint".</summary>
        public string Reason;

        /// <summary>The short form the scaler appends to its blocked reason and the dashboard prints.</summary>
        public override string ToString() => Reason ?? "";

        /// <summary>The cause as it goes into JSON.</summary>
        public static string NameOf(SaturationCause cause)
        {
            switch (cause)
            {
                case SaturationCause.NoBoundary: return "no-boundary";
                case SaturationCause.CohesionGroup: return "cohesion-group";
                case SaturationCause.AffinityGroup: return "affinity-group";
                case SaturationCause.Held: return "held";
                case SaturationCause.Dedicated: return "dedicated";
                default: return "";
            }
        }
    }

    /// <summary>
    /// A policy that can say why it moved what it moved. Unity's Mono has no default interface methods and a game's
    /// own <see cref="IAssignmentPolicy"/> must keep compiling, so explaining is a second interface the built-in
    /// <see cref="CostBalancedAssignmentPolicy"/> implements rather than a member of the policy interface. The
    /// orchestrator logs and publishes the moves when the policy in force offers them.
    /// </summary>
    public interface IExplainsAssignment
    {
        /// <summary>The moves of the last <see cref="IAssignmentPolicy.Compute"/>, in the order they were produced.</summary>
        IReadOnlyList<AssignmentMove> Moves { get; }

        /// <summary>
        /// What the last <see cref="IAssignmentPolicy.Compute"/> could not relieve, hottest first. Empty when the
        /// mesh is balanced, when nothing is hot enough to be worth reporting, or when no utilization was measured.
        /// </summary>
        IReadOnlyList<SaturationReport> Saturated { get; }
    }

    /// <summary>Helpers shared by the reports.</summary>
    internal static class AssignmentText
    {
        /// <summary>"c0, c1 and c2", or the first few with a count when there are many.</summary>
        public static string List(IReadOnlyList<string> ids, int max = 4)
        {
            if (ids == null || ids.Count == 0) return "";
            int n = Math.Min(ids.Count, max);
            var parts = new List<string>(n);
            for (int i = 0; i < n; i++) parts.Add(ids[i]);
            string text = string.Join(", ", parts);
            return ids.Count > n ? text + $" (+{ids.Count - n} more)" : text;
        }
    }
}
