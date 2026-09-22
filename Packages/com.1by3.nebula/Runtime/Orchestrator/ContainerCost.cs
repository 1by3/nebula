using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// The three things a container can be expensive in. Interest management limits the second and the third but
    /// not the first, so "this container is hot" is not one number: a boss arena full of NPCs and a plaza full of
    /// players want opposite fixes, and the operator has to be able to tell them apart (docs/cost-telemetry.md).
    /// </summary>
    public enum CostComponent
    {
        /// <summary>Simulating the container's own entities: the <c>NetworkTick</c> callbacks the worker ran for them.</summary>
        Simulation,
        /// <summary>Replicating them: world state, netvars and sync state leaving the worker for gateways.</summary>
        Replication,
        /// <summary>The per-client relay: owner state, which a gateway forwards to exactly one client each.</summary>
        Gateway,
    }

    /// <summary>
    /// What one container cost the worker that leases it, split into the three components of
    /// <see cref="CostComponent"/>. One row per container per worker, refreshed at the telemetry cadence
    /// (<see cref="WorkerTelemetry.IdleIntervalSeconds"/>) and kept by the orchestrator per lease
    /// (<see cref="MeshTelemetry.CopyContainerCost"/>); the dashboard, <c>GET /api/cost</c> and the scaler's
    /// "unsplittable container" reason all read the same rows. Design record: docs/cost-telemetry.md.
    /// <para>
    /// Absolute numbers, not shares: a share cannot say whether one container alone on a worker is hot because of
    /// what it simulates or because of what it sends. <see cref="Dominant"/> is decided by
    /// <see cref="Resolve"/>, which measures every component against a budget of its own.
    /// </para>
    /// </summary>
    public struct ContainerCost
    {
        /// <summary>The container these numbers are about.</summary>
        public string ContainerId;
        /// <summary>Its scope (<see cref="EntityLocation.ScopeKey"/>): empty in the public world, the instance key inside an instance.</summary>
        public string ScopeKey;
        /// <summary>The worker that reported it.</summary>
        public string WorkerId;
        /// <summary>
        /// The cost-weighted sum of the authoritative entities inside it: for each one, its category weight from
        /// <see cref="CostWeights"/> times its own <see cref="NebulaCost"/> weight. <see cref="CostWeights.Base"/>
        /// is <i>not</i> included; the orchestrator adds it (a leased container costs something when empty).
        /// </summary>
        public float EntityCostSum;
        /// <summary>Milliseconds of <c>NetworkTick</c> per simulated tick spent on this container's entities, measured.</summary>
        public float TickShareMs;
        /// <summary>
        /// <see cref="TickShareMs"/> as a fraction of the simulation time this worker attributed to containers at
        /// all. The parts of a tick that belong to no container (physics, interest, publishing) are outside it, so
        /// the rows of one worker sum to 1 and not to its utilization.
        /// </summary>
        public float TickShare;
        /// <summary>Bytes per second of world state, netvars and sync state sent to gateways for this container's entities, counted once per receiving gateway.</summary>
        public long BytesOutPerSec;
        /// <summary>Bytes per second of owner state: the part of the traffic a gateway relays to one client each.</summary>
        public long GatewayBytesPerSec;
        /// <summary>Ghosts of other workers' entities this worker holds for this container.</summary>
        public int GhostCount;
        /// <summary>Which component this container is most expensive in (see <see cref="Resolve"/>).</summary>
        public CostComponent Dominant;
        /// <summary>How saturated <see cref="Dominant"/> is, against its budget. 1 = the whole budget.</summary>
        public float DominantSaturation;
        /// <summary>When the report arrived, on the telemetry clock.</summary>
        public double ReceivedAt;

        /// <summary>The name <see cref="Dominant"/> goes by in a reason string, the JSON and the dashboard.</summary>
        public string DominantName => NameOf(Dominant);

        /// <summary>The lowercase wire name of a component.</summary>
        public static string NameOf(CostComponent c)
        {
            switch (c)
            {
                case CostComponent.Replication: return "replication";
                case CostComponent.Gateway: return "gateway";
                default: return "simulation";
            }
        }

        /// <summary>The component a wire name (<see cref="NameOf"/>) stands for; anything unknown reads as <see cref="CostComponent.Simulation"/>.</summary>
        public static CostComponent ComponentOf(string name)
        {
            switch (name)
            {
                case "replication": return CostComponent.Replication;
                case "gateway": return CostComponent.Gateway;
                default: return CostComponent.Simulation;
            }
        }

        /// <summary>
        /// Decide <see cref="Dominant"/> and <see cref="DominantSaturation"/> for one row by measuring each
        /// component against a budget of its own: simulation against the tick period, replication and the gateway
        /// relay against <paramref name="linkBytesPerSec"/> (<see cref="NebulaConfig.CostLinkBudgetMbps"/>).
        /// Comparing the three against each other directly would be comparing milliseconds with bytes, and
        /// comparing each container's share of its worker would call the only container on a worker dominant in
        /// everything at once - which is exactly the case the scaler needs an answer for. Ties go to simulation,
        /// then replication, so the answer is stable between passes. Pure function.
        /// </summary>
        public static void Resolve(ref ContainerCost row, float tickPeriodMs, double linkBytesPerSec)
        {
            float sim = tickPeriodMs > 0f ? row.TickShareMs / tickPeriodMs : 0f;
            float rep = linkBytesPerSec > 0.0 ? (float)(row.BytesOutPerSec / linkBytesPerSec) : 0f;
            float gw = linkBytesPerSec > 0.0 ? (float)(row.GatewayBytesPerSec / linkBytesPerSec) : 0f;
            row.Dominant = CostComponent.Simulation;
            row.DominantSaturation = sim;
            if (rep > row.DominantSaturation) { row.Dominant = CostComponent.Replication; row.DominantSaturation = rep; }
            if (gw > row.DominantSaturation) { row.Dominant = CostComponent.Gateway; row.DominantSaturation = gw; }
        }

        /// <summary>
        /// Fill in <see cref="TickShare"/> for every row of one worker (they are a fraction of that worker's own
        /// attributed simulation time) and resolve each row's dominant component. Called once per accepted
        /// telemetry document, over that document's rows only.
        /// </summary>
        public static void Normalize(IList<ContainerCost> rows, float tickPeriodMs, double linkBytesPerSec)
        {
            if (rows == null) return;
            float total = 0f;
            for (int i = 0; i < rows.Count; i++) total += rows[i].TickShareMs;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                row.TickShare = total > 0f ? row.TickShareMs / total : 0f;
                Resolve(ref row, tickPeriodMs, linkBytesPerSec);
                rows[i] = row;
            }
        }

        /// <summary>
        /// One row as the dashboard and <c>GET /api/cost</c> serve it. <paramref name="capacitySaturation"/> is
        /// <see cref="NebulaConfig.CapacitySaturation"/>, so the row can also say whether this container counts as
        /// at capacity (docs/capacity-admission.md); 0 leaves <c>atCapacity</c> false.
        /// </summary>
        public static void Write(JsonWriter w, in ContainerCost row, float capacitySaturation = 0f)
        {
            w.BeginObject();
            w.Prop("id", row.ContainerId ?? "");
            w.Prop("scope", row.ScopeKey ?? "");
            w.Prop("worker", row.WorkerId ?? "");
            w.Prop("cost", Math.Round(row.EntityCostSum, 3));
            w.Prop("tickMs", Math.Round(row.TickShareMs, 4));
            w.Prop("tickShare", Math.Round(row.TickShare, 4));
            w.Prop("bytesOut", row.BytesOutPerSec);
            w.Prop("gatewayBytes", row.GatewayBytesPerSec);
            w.Prop("ghosts", row.GhostCount);
            w.Prop("dominant", row.DominantName);
            w.Prop("saturation", Math.Round(row.DominantSaturation, 4));
            w.Prop("atCapacity", NebulaCapacity.Derive(row, capacitySaturation).AtCapacity);
            w.EndObject();
        }

        public override string ToString() =>
            $"{ContainerId} on {WorkerId}: cost {EntityCostSum:0.##}, {TickShareMs:0.###} ms/tick, {BytesOutPerSec} B/s out, {GatewayBytesPerSec} B/s relayed ({DominantName})";
    }
}
