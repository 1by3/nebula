using System.Collections.Generic;
using System.Diagnostics;

namespace Nebula
{
    /// <summary>
    /// What each container this worker leases is actually costing it, measured rather than guessed: the
    /// <c>NetworkTick</c> time spent on its entities, the replication bytes they put on the wire, and the owner
    /// state a gateway has to relay for them. The numbers go out with the worker's telemetry document
    /// (<see cref="WorkerTelemetry"/>) as one row per container and end up in <see cref="ContainerCost"/>.
    /// <para>
    /// The accumulators live on <see cref="Container"/> itself, so the hot paths (once per authoritative entity
    /// per tick) add to a field instead of hashing a container id. The meter keeps the list of containers it has
    /// touched since the last sample so it can clear exactly those. Nothing here allocates once the world is warm.
    /// </para>
    /// <para>
    /// Design record: docs/cost-telemetry.md. The attribution is exact for what it covers and covers
    /// <i>only</i> what a container's own entities caused: the rest of a tick (physics, the interest pass, the
    /// publish loop, the engine's own frame work) belongs to no container and is deliberately left out.
    /// </para>
    /// </summary>
    public sealed class ContainerCostMeter
    {
        private static readonly double MsPerStopwatchTick = 1000.0 / Stopwatch.Frequency;

        /// <summary>One container's costs since the last sample, as the telemetry document reports them.</summary>
        public struct Rate
        {
            /// <summary>Milliseconds of <c>NetworkTick</c> per simulated tick.</summary>
            public float TickMs;
            /// <summary>Bytes per second of world state, netvars and sync state, counted once per receiving gateway.</summary>
            public long BytesOutPerSec;
            /// <summary>Bytes per second of owner state (one client each, relayed by its gateway).</summary>
            public long GatewayBytesPerSec;
        }

        private readonly List<Container> _touched = new List<Container>(64);
        private readonly Dictionary<string, Rate> _samples = new Dictionary<string, Rate>(64, System.StringComparer.Ordinal);
        // Entities in no container at all. They are nobody's cost, but dropping them silently would make the rows
        // of a world with loose entities add up to less than the worker spends; they are reported under "".
        private long _looseSim, _looseReplication, _looseGateway;
        private int _ticks;
        private float _sampledAt;

        /// <summary>The latest sample, by container id ("" for entities in no container). Replaced by <see cref="Sample(float)"/>.</summary>
        public IReadOnlyDictionary<string, Rate> Latest => _samples;

        /// <summary>Simulated ticks counted into the current window.</summary>
        public int Ticks => _ticks;

        /// <summary>One simulated tick has begun; called from the worker's tick so a sample can be per tick.</summary>
        public void CountTick() => _ticks++;

        /// <summary>Charge <paramref name="stopwatchTicks"/> of <c>NetworkTick</c> time to <paramref name="container"/>.</summary>
        public void AddSimulation(Container container, long stopwatchTicks)
        {
            if (container == null) { _looseSim += stopwatchTicks; return; }
            Track(container);
            container.CostSimTicks += stopwatchTicks;
        }

        /// <summary>Charge <paramref name="bytes"/> of worker-to-gateway replication to <paramref name="container"/>.</summary>
        public void AddReplication(Container container, long bytes)
        {
            if (container == null) { _looseReplication += bytes; return; }
            Track(container);
            container.CostReplicationBytes += bytes;
        }

        /// <summary>Charge <paramref name="bytes"/> of per-client relay traffic (owner state) to <paramref name="container"/>.</summary>
        public void AddGateway(Container container, long bytes)
        {
            if (container == null) { _looseGateway += bytes; return; }
            Track(container);
            container.CostGatewayBytes += bytes;
        }

        private void Track(Container container)
        {
            if (container.CostTracked) return;
            container.CostTracked = true;
            _touched.Add(container);
        }

        /// <summary>
        /// Turn everything counted since the previous call into per-tick and per-second rates in
        /// <see cref="Latest"/>, and start a new window. <paramref name="now"/> is a monotonic clock in seconds;
        /// the first call only establishes the baseline. Main thread, outside the tick.
        /// </summary>
        public void Sample(float now)
        {
            float dt = now - _sampledAt;
            bool usable = _sampledAt > 0f && dt > 0.001f && _ticks > 0;
            _samples.Clear();
            if (usable)
            {
                if (_looseSim != 0 || _looseReplication != 0 || _looseGateway != 0)
                    _samples[""] = Rates(_looseSim, _looseReplication, _looseGateway, dt);
                for (int i = 0; i < _touched.Count; i++)
                {
                    var c = _touched[i];
                    _samples[c.ContainerId] = Rates(c.CostSimTicks, c.CostReplicationBytes, c.CostGatewayBytes, dt);
                }
            }
            for (int i = 0; i < _touched.Count; i++)
            {
                var c = _touched[i];
                c.CostSimTicks = 0;
                c.CostReplicationBytes = 0;
                c.CostGatewayBytes = 0;
                c.CostTracked = false;
            }
            _touched.Clear();
            _looseSim = _looseReplication = _looseGateway = 0;
            _ticks = 0;
            _sampledAt = now;
        }

        private Rate Rates(long simTicks, long replication, long gateway, float dt)
        {
            return new Rate
            {
                TickMs = (float)(simTicks * MsPerStopwatchTick / _ticks),
                BytesOutPerSec = (long)(replication / dt),
                GatewayBytesPerSec = (long)(gateway / dt),
            };
        }

        /// <summary>The sample for a container id, or all zeroes when it has not been measured yet.</summary>
        public Rate Of(string containerId) => _samples.TryGetValue(containerId ?? "", out var s) ? s : default;
    }
}
