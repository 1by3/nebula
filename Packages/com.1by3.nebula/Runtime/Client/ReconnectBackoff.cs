using System;

namespace Nebula
{
    /// <summary>
    /// The client's reconnect schedule (<see cref="NebulaConfig.ReconnectFirstDelaySeconds"/>,
    /// <see cref="NebulaConfig.ReconnectBackoffFactor"/>, <see cref="NebulaConfig.ReconnectMaxDelaySeconds"/>,
    /// <see cref="NebulaConfig.ReconnectGiveUpSeconds"/>) with its values made safe. Pure C#, so the schedule is
    /// tested outside Unity too.
    /// </summary>
    internal readonly struct ReconnectBackoff
    {
        public readonly float FirstDelaySeconds;
        public readonly float Factor;
        public readonly float MaxDelaySeconds;
        public readonly float GiveUpSeconds;

        public ReconnectBackoff(float firstDelaySeconds, float factor, float maxDelaySeconds, float giveUpSeconds)
        {
            FirstDelaySeconds = Clean(firstDelaySeconds, 1f, 0f);
            Factor = Clean(factor, 2f, 1f);
            MaxDelaySeconds = Math.Max(FirstDelaySeconds, Clean(maxDelaySeconds, 2f, 0f));
            GiveUpSeconds = Clean(giveUpSeconds, 0f, 0f);
        }

        /// <summary>The schedule of a client that has never been configured: 1 s, then every 2 s, forever.</summary>
        public static ReconnectBackoff Default => new ReconnectBackoff(1f, 2f, 2f, 0f);

        /// <summary>
        /// Seconds to wait before the next attempt, when <paramref name="attemptsMade"/> attempts have already
        /// failed since the connection dropped: the first delay, multiplied by the factor for each failed attempt,
        /// capped at the maximum.
        /// </summary>
        public float DelayAfter(int attemptsMade)
        {
            double delay = FirstDelaySeconds * Math.Pow(Factor, Math.Max(0, attemptsMade));
            return (float)Math.Min(delay, MaxDelaySeconds);
        }

        /// <summary>Whether a client that has been reconnecting for <paramref name="elapsedSeconds"/> should stop. Never, when the give-up time is 0.</summary>
        public bool ShouldGiveUp(float elapsedSeconds) => GiveUpSeconds > 0f && elapsedSeconds >= GiveUpSeconds;

        private static float Clean(float value, float fallback, float min)
            => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(min, value);
    }
}
