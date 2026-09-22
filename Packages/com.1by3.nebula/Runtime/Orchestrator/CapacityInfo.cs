using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// How close one container - or one whole scope - is to what a single worker can carry, derived from the cost
    /// telemetry the worker already reports (<see cref="ContainerCost"/>, docs/cost-telemetry.md). One number and
    /// the name of the component it came from, because "full" is not a head count: a station can be full of
    /// simulation, of replication or of per-client relay, and the three have different fixes.
    /// <para>
    /// The orchestrator derives it and publishes it on the lease row, so a gateway answers "is this target at
    /// capacity" from the control-plane document it already mirrors, without asking anybody. Design record:
    /// <c>docs/capacity-admission.md</c>.
    /// </para>
    /// </summary>
    public struct CapacityInfo
    {
        /// <summary>The container this is about; empty when the reading is about a whole scope.</summary>
        public string ContainerId;
        /// <summary>The scope the reading belongs to (<see cref="EntityLocation.ScopeKey"/>); empty in the public world.</summary>
        public string ScopeKey;
        /// <summary>
        /// A worker has reported cost for this target lately. False means "nobody has said": an unknown target is
        /// never at capacity, so a mesh without cost telemetry admits exactly what it always did.
        /// </summary>
        public bool Known;
        /// <summary>
        /// The dominant component's share of its own budget (<see cref="ContainerCost.DominantSaturation"/>).
        /// 1 = the whole budget. For a scope, the worst of its parts.
        /// </summary>
        public float Saturation;
        /// <summary>Which component <see cref="Saturation"/> is about (<see cref="ContainerCost.Dominant"/>).</summary>
        public CostComponent Dominant;
        /// <summary>
        /// <see cref="Saturation"/> is at or above <c>NebulaConfig.CapacitySaturation</c>. The orchestrator decides
        /// this, not the reader: the threshold is one mesh-wide setting and the gateways must not disagree about it.
        /// </summary>
        public bool AtCapacity;
        /// <summary>
        /// Why the planner cannot relieve this target by moving anything (<see cref="SaturationReport.Cause"/>), or
        /// <see cref="SaturationCause.None"/> when the reading is cost telemetry alone. A cohesion group, an
        /// affinity group, a hold or a missing boundary is the difference between "busy, and the mesh is about to
        /// spread it" and "busy, and nothing the mesh can do will help" - which is the one a queue is for.
        /// </summary>
        public SaturationCause Cause;

        /// <summary>The name <see cref="Dominant"/> goes by in the JSON, a log line and the dashboard.</summary>
        public string DominantName => ContainerCost.NameOf(Dominant);

        public override string ToString() =>
            !Known ? "capacity unknown"
                : $"{(AtCapacity ? "at capacity" : "below capacity")}: {Saturation:0.##} of the {DominantName} budget" +
                  (Cause == SaturationCause.None ? "" : $", and the planner cannot relieve it ({SaturationReport.NameOf(Cause)})");
    }

    /// <summary>
    /// Deriving the capacity signal from cost rows, and reading it back off the control plane. Pure and static: the
    /// orchestrator writes, every other role reads, and both sides run the same functions so a test can check one
    /// without a mesh. Design record: <c>docs/capacity-admission.md</c>.
    /// </summary>
    public static class NebulaCapacity
    {
        /// <summary>
        /// Turn one cost row into a capacity reading. <paramref name="threshold"/> is
        /// <c>NebulaConfig.CapacitySaturation</c>; 0 or less turns the signal off, so nothing is ever at capacity
        /// and every join is admitted as it was before. A row with no measurement in it at all
        /// (<see cref="ContainerCost.ContainerId"/> unset) is not a reading.
        /// </summary>
        public static CapacityInfo Derive(in ContainerCost row, float threshold)
        {
            var info = new CapacityInfo
            {
                ContainerId = row.ContainerId,
                ScopeKey = row.ScopeKey ?? "",
                Known = !string.IsNullOrEmpty(row.ContainerId),
                Saturation = float.IsNaN(row.DominantSaturation) ? 0f : Math.Max(0f, row.DominantSaturation),
                Dominant = row.Dominant,
            };
            info.AtCapacity = info.Known && threshold > 0f && info.Saturation >= threshold;
            return info;
        }

        /// <summary>
        /// The capacity of one container, as the orchestrator last published it on the lease row. Unknown when
        /// there is no row, or no reading on it yet.
        /// </summary>
        public static CapacityInfo Of(IControlPlane cp, string containerId)
        {
            var lease = cp != null ? cp.FindLease(containerId) : null;
            if (lease == null || !lease.HasCapacity) return new CapacityInfo { ContainerId = containerId ?? "", ScopeKey = lease?.Instance?.ScopeKey ?? "" };
            return new CapacityInfo
            {
                ContainerId = containerId ?? "",
                ScopeKey = lease.Instance?.ScopeKey ?? "",
                Known = true,
                Saturation = lease.Saturation,
                Dominant = lease.Dominant,
                AtCapacity = lease.AtCapacity,
                Cause = lease.SaturationCause,
            };
        }

        /// <summary>
        /// The capacity of a whole scope: the worst of its parts. A scope is one interaction domain that cannot be
        /// split past its authored boundaries, so one saturated part is a saturated scope - taking the mean would
        /// hide exactly the part that is about to cost everyone their tick. The public world (an empty key) has no
        /// scope row and is never at capacity as a whole; its containers are judged one at a time.
        /// </summary>
        public static CapacityInfo OfScope(IControlPlane cp, string scopeKey)
        {
            var result = new CapacityInfo { ScopeKey = scopeKey ?? "" };
            if (cp == null || string.IsNullOrEmpty(scopeKey)) return result;
            var scope = cp.FindScope(scopeKey);
            if (scope?.ContainerIds == null || IsPerContainer(scope)) return result;
            for (int i = 0; i < scope.ContainerIds.Count; i++) result = Worse(result, Of(cp, scope.ContainerIds[i]));
            result.ScopeKey = scopeKey;
            return result;
        }

        /// <summary>
        /// The more saturated of two readings, preferring a known one over an unknown one. An unknown reading never
        /// wins, so one worker that has not reported yet cannot make a busy scope look idle.
        /// </summary>
        public static CapacityInfo Worse(in CapacityInfo a, in CapacityInfo b)
        {
            if (!b.Known) return a;
            if (!a.Known) return b;
            if (b.AtCapacity != a.AtCapacity) return b.AtCapacity ? b : a;
            // Between two equally full parts, the one nothing can relieve is the one worth naming.
            if (b.Saturation == a.Saturation && b.Cause != a.Cause) return a.Cause == SaturationCause.None ? b : a;
            return b.Saturation > a.Saturation ? b : a;
        }

        /// <summary>
        /// Whether this scope is judged one container at a time rather than as a whole. A grid scope
        /// (<see cref="ScopeKind.Grid"/>) is an unbounded procedural world whose row names only the anchor chunk:
        /// its chunks are separate places, so "the world is full" is not a thing it can be. A parts scope is one
        /// authored interaction domain and is judged whole (D3).
        /// </summary>
        public static bool IsPerContainer(ScopeInfo scope) =>
            scope == null || string.Equals(scope.Definition?.Kind, ScopeKind.Grid, StringComparison.Ordinal);

        /// <summary>Whether the target a client named is judged per container: the public world, or a grid scope.</summary>
        public static bool IsPerContainer(IControlPlane cp, string scopeKey) =>
            string.IsNullOrEmpty(scopeKey) || IsPerContainer(cp != null ? cp.FindScope(scopeKey) : null);

        /// <summary>
        /// The capacity of the target a client is asking to enter: the scope when it named one that is judged as a
        /// whole, otherwise the container it would be placed in. This is the one call a gateway or a worker makes.
        /// </summary>
        public static CapacityInfo Target(IControlPlane cp, string scopeKey, string containerId) =>
            IsPerContainer(cp, scopeKey) ? Of(cp, containerId) : OfScope(cp, scopeKey);

        /// <summary>
        /// Fold the planner's saturation reports (<see cref="SaturationReport"/>, docs/cohesion-rebalancing.md) into
        /// the readings. A container the planner reports it cannot relieve - because a cohesion group or an affinity
        /// group spans it, because it is held, because the game authored no boundary inside it, or because it asked
        /// for a worker of its own - is at capacity once its item's utilization reaches the threshold, whatever its
        /// own cost row says. This is the case the whole item exists for: moving something would fix a hot
        /// container, and here nothing can move, so the only honest answers are "queue", "deny" or "degrade".
        /// <para>
        /// The report never lowers a reading: a container already at capacity by its own cost stays there, and the
        /// report's utilization is taken only when it is the larger of the two. Every container of the item is
        /// marked, not only the heaviest, because a client is placed into one of them and they move together.
        /// </para>
        /// </summary>
        public static void Apply(IReadOnlyList<SaturationReport> reports, float threshold, Dictionary<string, CapacityInfo> result)
        {
            if (reports == null || result == null || threshold <= 0f) return;
            for (int i = 0; i < reports.Count; i++)
            {
                var report = reports[i];
                if (report.Cause == SaturationCause.None) continue;
                float utilization = float.IsNaN(report.Utilization) ? 0f : Math.Max(0f, report.Utilization);
                if (utilization < threshold) continue;
                var containers = report.Containers;
                int count = containers != null ? containers.Length : 0;
                for (int c = -1; c < count; c++)
                {
                    string id = c < 0 ? report.ContainerId : containers[c];
                    if (string.IsNullOrEmpty(id)) continue;
                    result.TryGetValue(id, out var info);
                    info.ContainerId = id;
                    if (string.IsNullOrEmpty(info.ScopeKey)) info.ScopeKey = report.ScopeKey ?? "";
                    if (!info.Known || utilization > info.Saturation)
                    {
                        info.Saturation = utilization;
                        info.Dominant = CostComponent.Simulation;
                    }
                    info.Known = true;
                    info.AtCapacity = true;
                    info.Cause = report.Cause;
                    result[id] = info;
                }
            }
        }

        /// <summary>
        /// The readings for a set of cost rows, keyed by container. Used by the orchestrator to decide what to
        /// publish, and by <c>GET /api/cost</c> to say which rows are at capacity.
        /// </summary>
        public static void Derive(IReadOnlyDictionary<string, ContainerCost> rows, float threshold, Dictionary<string, CapacityInfo> result)
        {
            result.Clear();
            if (rows == null) return;
            foreach (var kv in rows) result[kv.Key] = Derive(kv.Value, threshold);
        }
    }
}
