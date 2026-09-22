using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// How busy each worker is, as a fraction of its tick budget: <c>WorkerInfo.TickMs / tickPeriodMs</c>, sampled
    /// from every heartbeat and kept for a short window (<see cref="NebulaConfig.ScaleWindowSeconds"/>). The 90th
    /// percentile of that window is the scaling signal: a GC spike does not launch a machine, a sustained 80 % does.
    /// <para>
    /// The tracker is a plain object with an injected clock, so the scaler can be exercised over synthetic histories
    /// without a mesh or Unity time. Samples are kept in a per-worker ring of structs and the percentile is taken
    /// from a reused scratch buffer, so a pass allocates nothing once the mesh is warm.
    /// </para>
    /// </summary>
    public sealed class WorkerLoadTracker
    {
        /// <summary>One simulated tick's wall-clock budget, in milliseconds (<see cref="NetworkTime.TickRate"/>).</summary>
        public const float TickPeriodMs = 1000f / NetworkTime.TickRate;

        /// <summary>Fraction of the window used as "the" utilization of a worker.</summary>
        public const float Percentile = 0.9f;

        private struct Reading
        {
            public double At;
            public float Utilization;
        }

        private sealed class History
        {
            public readonly List<Reading> Samples = new List<Reading>(64);
            public double LastSeen;
        }

        private readonly Dictionary<string, History> _workers = new Dictionary<string, History>(StringComparer.Ordinal);
        private readonly List<float> _scratch = new List<float>(64);
        private readonly List<string> _dead = new List<string>();
        private readonly Func<double> _now;

        /// <summary>Length of the window each worker keeps, in seconds.</summary>
        public float WindowSeconds = 20f;

        /// <param name="now">Seconds on a monotonic clock. Tests pass their own; the orchestrator passes Unity time.</param>
        public WorkerLoadTracker(Func<double> now)
        {
            _now = now ?? throw new ArgumentNullException(nameof(now));
        }

        /// <summary>Workers with at least one live sample.</summary>
        public int Count => _workers.Count;

        /// <summary>Utilization of one tick time, as a fraction of the tick budget (0.5 = half a tick period).</summary>
        public static float UtilizationOf(float tickMs) => tickMs <= 0f ? 0f : tickMs / TickPeriodMs;

        /// <summary>Record one heartbeat's tick time. Repeated identical heartbeats are kept: the window is time-based.</summary>
        public void Sample(string workerId, float tickMs) => SampleAt(workerId, tickMs, _now());

        /// <summary>Record one sample at an explicit time (tests, and replaying a batch of heartbeats).</summary>
        public void SampleAt(string workerId, float tickMs, double at)
        {
            if (string.IsNullOrEmpty(workerId)) return;
            if (!_workers.TryGetValue(workerId, out var h)) _workers[workerId] = h = new History();
            h.LastSeen = at;
            h.Samples.Add(new Reading { At = at, Utilization = UtilizationOf(tickMs) });
            Trim(h, at);
        }

        /// <summary>Drop samples (and whole workers) older than the window. Called once per pass so a dead worker's history fades.</summary>
        public void Expire()
        {
            double now = _now();
            _dead.Clear();
            foreach (var kv in _workers)
            {
                Trim(kv.Value, now);
                if (kv.Value.Samples.Count == 0) _dead.Add(kv.Key);
            }
            for (int i = 0; i < _dead.Count; i++) _workers.Remove(_dead[i]);
        }

        /// <summary>
        /// How many heartbeats a worker must have inside the window before its utilization means anything. Below
        /// this it reads as 0 % busy simply because nothing has been reported yet, and the worker is not considered
        /// for retirement (<see cref="WorkerScaler.IsWarm"/>).
        /// </summary>
        public int MinSamples = 3;

        /// <summary>How many samples a worker has inside the window right now.</summary>
        public int SampleCount(string workerId)
        {
            if (workerId == null || !_workers.TryGetValue(workerId, out var h)) return 0;
            double now = _now();
            int count = 0;
            for (int i = 0; i < h.Samples.Count; i++) if (now - h.Samples[i].At <= WindowSeconds) count++;
            return count;
        }

        /// <summary>Has this worker reported enough to be judged (<see cref="MinSamples"/>)?</summary>
        public bool IsWarm(string workerId) => SampleCount(workerId) >= MinSamples;

        /// <summary>Forget a worker outright (it died or retired).</summary>
        public void Forget(string workerId) => _workers.Remove(workerId ?? "");

        /// <summary>
        /// The 90th percentile of a worker's window, or 0 when it has never reported. Nearest-rank on the samples
        /// still inside the window, so one bad tick in twenty does not count as the worker's load.
        /// </summary>
        public float Utilization(string workerId)
        {
            if (workerId == null || !_workers.TryGetValue(workerId, out var h)) return 0f;
            double now = _now();
            _scratch.Clear();
            for (int i = 0; i < h.Samples.Count; i++)
                if (now - h.Samples[i].At <= WindowSeconds) _scratch.Add(h.Samples[i].Utilization);
            if (_scratch.Count == 0) return 0f;
            _scratch.Sort();
            int rank = (int)Math.Ceiling(Percentile * _scratch.Count) - 1;
            if (rank < 0) rank = 0;
            if (rank >= _scratch.Count) rank = _scratch.Count - 1;
            return _scratch[rank];
        }

        /// <summary>Fill <paramref name="result"/> (cleared first) with the p90 utilization of every worker in <paramref name="workers"/>, including ones with no samples yet (0).</summary>
        public void CopyUtilization(IEnumerable<WorkerInfo> workers, Dictionary<string, float> result)
        {
            result.Clear();
            foreach (var w in workers) result[w.WorkerId] = Utilization(w.WorkerId);
        }

        /// <summary>
        /// Spread each worker's p90 utilization over the containers it leases, in proportion to what
        /// <see cref="CostWeights"/> says their occupants cost. A container nobody reported gets
        /// <see cref="CostWeights.Base"/>, so an empty container still carries its share of the worker's floor.
        /// A worker holding no container contributes nothing; its load shows up in the mesh's mean only.
        /// <para>
        /// This is a proxy: a container full of static props costs less than one full of NPCs chasing paths. The
        /// hint's cost multiplier (phase 3) is the manual correction.
        /// </para>
        /// </summary>
        /// <param name="result">Cleared and filled with container id -> attributed utilization.</param>
        public static void Attribute(
            IReadOnlyDictionary<string, float> workerUtilization,
            IReadOnlyList<LeaseInfo> leases,
            IReadOnlyDictionary<string, ContainerLoad> occupancy,
            in CostWeights weights,
            Dictionary<string, float> result)
        {
            result.Clear();
            if (workerUtilization == null || leases == null) return;
            foreach (var kv in workerUtilization)
            {
                float utilization = kv.Value;
                float total = 0f;
                for (int i = 0; i < leases.Count; i++)
                {
                    var l = leases[i];
                    if (l.WorkerId != kv.Key || !LeaseState.IsOwning(l.State)) continue;
                    total += WeightOf(l.ContainerId, occupancy, weights);
                }
                if (total <= 0f) continue;
                for (int i = 0; i < leases.Count; i++)
                {
                    var l = leases[i];
                    if (l.WorkerId != kv.Key || !LeaseState.IsOwning(l.State)) continue;
                    result[l.ContainerId] = utilization * (WeightOf(l.ContainerId, occupancy, weights) / total);
                }
            }
        }

        private static float WeightOf(string containerId, IReadOnlyDictionary<string, ContainerLoad> occupancy, in CostWeights weights)
        {
            return occupancy != null && occupancy.TryGetValue(containerId, out var load) ? weights.Of(load) : weights.Base;
        }

        private void Trim(History h, double now)
        {
            int drop = 0;
            while (drop < h.Samples.Count && now - h.Samples[drop].At > WindowSeconds) drop++;
            if (drop > 0) h.Samples.RemoveRange(0, drop);
        }
    }
}
