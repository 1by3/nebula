using System;

namespace Nebula
{
    /// <summary>
    /// Tick model. Every worker derives the tick it should be simulating from the wall clock and a fixed
    /// origin, so workers agree on tick numbers without a tick master (a restarted worker knows the
    /// correct tick immediately, and the control plane is never on the hot path). Gameplay code should
    /// only ever see tick numbers, never wall-clock time.
    /// </summary>
    public static class NetworkTime
    {
        public const int TickRate = 60;
        public const float TickInterval = 1f / TickRate;

        private static readonly DateTime Origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The tick the wall clock says it is right now.</summary>
        public static uint DerivedTick => (uint)((DateTime.UtcNow - Origin).TotalSeconds * TickRate);

        public static double DerivedTickExact => (DateTime.UtcNow - Origin).TotalSeconds * TickRate;

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

        public static float TicksToSeconds(uint ticks) => ticks * TickInterval;
    }
}
