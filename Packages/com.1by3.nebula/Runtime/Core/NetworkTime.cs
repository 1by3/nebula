using System;

namespace Nebula
{
    /// <summary>
    /// Provides the shared simulation tick. Each worker calculates its tick from the wall clock and a fixed origin, so
    /// workers agree without a coordinating process. Use tick numbers rather than wall-clock timestamps in gameplay
    /// simulation.
    /// </summary>
    /// <remarks>
    /// The tick rate is <see cref="DefaultTickRate"/> (60 Hz) unless the game sets <c>NebulaConfig.TickRate</c>
    /// (docs/tick-rate.md). Every process of a mesh runs at the same rate: workers, the orchestrator and the gateway
    /// read it from the configuration (or <c>-nebula-tick-rate</c>, or the <c>NEBULA_TICK_RATE</c> environment
    /// variable), and a client takes it from the gateway's <see cref="WelcomeMsg.TickRate"/>. Code that means a
    /// duration should hold it in seconds and convert with <see cref="SecondsToTicks"/>, so it keeps its meaning at any
    /// rate; a tick count is right only for what is counted in steps (a number of inputs, a snapshot buffer depth).
    /// </remarks>
    public static class NetworkTime
    {
        /// <summary>The tick rate when the game does not set one, and the rate Nebula's own tick-count defaults were tuned at.</summary>
        public const int DefaultTickRate = 60;

        /// <summary>The lowest tick rate a mesh may run at.</summary>
        public const int MinTickRate = 10;

        /// <summary>The highest tick rate a mesh may run at (the rate travels as a byte in <see cref="WelcomeMsg"/>).</summary>
        public const int MaxTickRate = 240;

        private static int _tickRate = DefaultTickRate;
        private static float _tickInterval = 1f / DefaultTickRate;

        /// <summary>Simulation ticks per second in this process: the same on every worker, gateway and client of a mesh.</summary>
        public static int TickRate => _tickRate;

        /// <summary>Seconds per tick: <c>1 / TickRate</c>. Unity's fixed step (<c>Time.fixedDeltaTime</c>) is set to it.</summary>
        public static float TickInterval => _tickInterval;

        /// <summary>Raised after <see cref="Configure"/> changed the rate (a client that learned the mesh's rate from its gateway, say).</summary>
        public static event Action<int> TickRateChanged;

        /// <summary>
        /// Set the tick rate of this process: <see cref="ClampRate"/> of <paramref name="ticksPerSecond"/>. Workers,
        /// gateways and orchestrators call it at start-up from the configuration; a client calls it again with the rate
        /// its gateway welcomed it with. Returns the rate in use.
        /// </summary>
        public static int Configure(int ticksPerSecond)
        {
            int rate = ClampRate(ticksPerSecond);
            if (rate == _tickRate) return rate;
            _tickRate = rate;
            _tickInterval = 1f / rate;
            var changed = TickRateChanged;
            if (changed != null)
                foreach (Action<int> handler in changed.GetInvocationList())
                {
                    try { handler(rate); }
                    catch (Exception e) { NebulaLog.Error($"TickRateChanged handler threw: {e}"); }
                }
            return rate;
        }

        /// <summary>A tick rate within <see cref="MinTickRate"/>..<see cref="MaxTickRate"/>; 0 or less is <see cref="DefaultTickRate"/>.</summary>
        public static int ClampRate(int ticksPerSecond) =>
            ticksPerSecond <= 0 ? DefaultTickRate : Math.Max(MinTickRate, Math.Min(MaxTickRate, ticksPerSecond));

        /// <summary>
        /// The rate a process should run at: <c>-nebula-tick-rate</c> on its command line, else the
        /// <c>NEBULA_TICK_RATE</c> environment variable (every role of a local mesh started from a shell that set it runs
        /// at that rate), else <paramref name="configured"/> (<c>NebulaConfig.TickRate</c>).
        /// </summary>
        public static int ResolveRate(int configured)
        {
            string value = NebulaEnv.Get("NEBULA_TICK_RATE");
            return !string.IsNullOrWhiteSpace(value) && int.TryParse(value.Trim(), out int rate) && rate > 0 ? rate : configured;
        }

        private static readonly DateTime Origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The tick the wall clock says it is right now.</summary>
        public static uint DerivedTick => (uint)((DateTime.UtcNow - Origin).TotalSeconds * _tickRate);

        public static double DerivedTickExact => (DateTime.UtcNow - Origin).TotalSeconds * _tickRate;

        /// <summary>
        /// The tick currently being simulated. On a worker this is the authoritative simulation tick.
        /// On a client it is the tick the local player is predicting (ahead of the server by the input lead).
        /// </summary>
        public static uint Tick { get; internal set; }

        /// <summary>Latest server tick the client has heard about (client only).</summary>
        public static uint LatestServerTick { get; internal set; }

        /// <summary>
        /// The (fractional) tick non-authoritative copies are presenting right now: on a client a few ticks behind
        /// the newest snapshot (<c>InterpolationDelayTicks</c>), on a worker one tick behind the simulation tick for
        /// ghosts. Buffered interpolators sample at this tick.
        /// </summary>
        public static double RenderTick { get; internal set; }

        public static float TicksToSeconds(uint ticks) => ticks * _tickInterval;

        /// <summary>A duration in ticks at the current rate, rounded to the nearest tick and at least 1 (0 for 0 or less).</summary>
        public static int SecondsToTicks(double seconds) => seconds <= 0 ? 0 : Math.Max(1, (int)Math.Round(seconds * _tickRate));

        /// <summary>
        /// A duration in ticks at the current rate, rounded up (0 for 0 or less): for a margin that must cover at least
        /// <paramref name="seconds"/>. A thousandth of a tick of slack absorbs a float's rounding (32/60 s stored as a float is
        /// 32.000002 ticks at 60 Hz, which is 32, not 33).
        /// </summary>
        public static int SecondsToTicksCeil(double seconds) => seconds <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(seconds * _tickRate - 1e-3));

        /// <summary>A duration in fractional ticks at the current rate, for thresholds compared with tick arithmetic.</summary>
        public static double SecondsToTicksExact(double seconds) => seconds * _tickRate;

        /// <summary>
        /// A count of ticks tuned at <see cref="DefaultTickRate"/> (an interval or a divisor), rescaled so it lasts as
        /// long at the current rate: rounded, at least 1. 0 or less is returned unchanged (it usually means none, or off).
        /// </summary>
        public static int FromDefaultRateTicks(int ticksAt60)
        {
            if (ticksAt60 <= 0 || _tickRate == DefaultTickRate) return ticksAt60;
            return Math.Max(1, (int)Math.Round(ticksAt60 * (double)_tickRate / DefaultTickRate));
        }
    }
}
