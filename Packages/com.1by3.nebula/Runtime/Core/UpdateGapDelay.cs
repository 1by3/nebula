using System;

namespace Nebula
{
    /// <summary>
    /// The extra render delay a client gives one remote entity whose state arrives further apart than the render
    /// delay: an entity the worker updates every few ticks (<see cref="NetworkIdentity.UpdateInterval"/>), or one the
    /// gateway sends a client only every Nth tick (the distance tiers). Without it such an entity is drawn past its
    /// newest state for most of every gap, extrapolated by its velocity, and snaps back when the next state arrives.
    /// With it the entity is drawn far enough behind to have a state on either side of the render tick, so it
    /// interpolates across its own gap. Entities whose state arrives within the render delay get none, so a player
    /// or a vehicle sent every tick is drawn exactly as late as before.
    /// <para>
    /// The gap is measured, not configured: the larger of the last two gaps between states, in ticks, so the
    /// staggered windows of a gateway divisor (an entity updated every 6 ticks, sent in 20-tick windows, arrives 18
    /// and 24 ticks apart) settle on the longer one. A gap longer than <see cref="MaxGapTicks"/> is taken for an
    /// entity at rest (a resting entity is not sent) and ignored. The delay moves toward its target at half the tick
    /// rate, so the entity plays at between half and one and a half times real speed while it changes, and never
    /// jumps.
    /// </para>
    /// </summary>
    public sealed class UpdateGapDelay
    {
        /// <summary>How fast the delay moves toward its target, in ticks per second: half the tick rate.</summary>
        public const double SlewTicksPerSecond = NetworkTime.TickRate * 0.5;

        /// <summary>
        /// Gaps longer than this, in ticks (one second), are an entity at rest, not its rate. It stays under the
        /// 64 ticks a <see cref="RemoteInterpolator"/> buffers, so the older state of a gap is still there to
        /// interpolate from.
        /// </summary>
        public const int MaxGapTicks = NetworkTime.TickRate;

        private uint _lastTick;
        private bool _hasTick;
        private int _gap, _previousGap;

        /// <summary>The larger of the last two gaps between states within the limit, in ticks; 0 until two have arrived.</summary>
        public int GapTicks => Math.Max(_gap, _previousGap);

        /// <summary>The extra delay in use, in ticks (fractional while it moves toward <see cref="TargetTicks"/>).</summary>
        public double DelayTicks { get; private set; }

        /// <summary>The delay <see cref="Advance"/> last aimed for, in ticks.</summary>
        public int TargetTicks { get; private set; }

        /// <summary>
        /// A state for <paramref name="tick"/> arrived. A gap longer than <see cref="MaxGapTicks"/> is taken for the
        /// entity resting and does not count; an older or repeated tick is ignored.
        /// </summary>
        public void Observe(uint tick)
        {
            if (_hasTick && tick <= _lastTick) return;
            if (_hasTick)
            {
                uint gap = tick - _lastTick;
                if (gap <= MaxGapTicks) { _previousGap = _gap; _gap = (int)gap; }
            }
            _lastTick = tick;
            _hasTick = true;
        }

        /// <summary>
        /// The target for a client rendering <paramref name="renderDelayTicks"/> behind the newest state: one tick
        /// more than the part of the gap the render delay does not cover, when the gap is longer than the render
        /// delay, and 0 otherwise; never more than <paramref name="maxDelayTicks"/>.
        /// </summary>
        public static int TargetFor(int gapTicks, int renderDelayTicks, int maxDelayTicks)
        {
            if (maxDelayTicks <= 0 || gapTicks <= renderDelayTicks) return 0;
            return Math.Min(gapTicks - renderDelayTicks + 1, maxDelayTicks);
        }

        /// <summary>
        /// Move the delay toward its target by at most <see cref="SlewTicksPerSecond"/> times
        /// <paramref name="deltaSeconds"/>, and return it. <paramref name="maxDelayTicks"/> of 0 turns the delay off
        /// at once.
        /// </summary>
        public double Advance(int renderDelayTicks, int maxDelayTicks, double deltaSeconds)
        {
            TargetTicks = TargetFor(GapTicks, renderDelayTicks, maxDelayTicks);
            if (maxDelayTicks <= 0) { DelayTicks = 0; return 0; }
            double step = SlewTicksPerSecond * Math.Max(0, deltaSeconds);
            double error = TargetTicks - DelayTicks;
            DelayTicks = Math.Abs(error) <= step ? TargetTicks : DelayTicks + Math.Sign(error) * step;
            return DelayTicks;
        }

        /// <summary>Forget every gap and the delay (a despawned identity going back to a pool).</summary>
        public void Reset()
        {
            _hasTick = false;
            _lastTick = 0;
            _gap = _previousGap = 0;
            DelayTicks = 0;
            TargetTicks = 0;
        }
    }
}
