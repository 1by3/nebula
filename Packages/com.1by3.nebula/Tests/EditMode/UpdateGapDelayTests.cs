using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The per-entity render delay for entities whose state arrives further apart than the render delay
    /// (<see cref="UpdateGapDelay"/>, <see cref="NebulaConfig.SlowUpdateMaxDelayTicks"/>): what gap it measures, what
    /// delay that asks for, how fast the delay moves, and that an entity sent in a slow tier then moves smoothly
    /// through a real <see cref="RemoteInterpolator"/> where it used to hold and jump.
    /// </summary>
    public sealed class UpdateGapDelayTests
    {
        private const int RenderDelay = 3;
        private const int MaxDelay = 30;
        private const double Frame = 1.0 / NetworkTime.TickRate;

        private static UpdateGapDelay Fed(uint first, params uint[] gaps)
        {
            var d = new UpdateGapDelay();
            uint t = first;
            d.Observe(t);
            foreach (var g in gaps) { t += g; d.Observe(t); }
            return d;
        }

        private static int Target(UpdateGapDelay d, int maxDelay) => UpdateGapDelay.TargetFor(d.GapTicks, RenderDelay, maxDelay);

        [Test]
        public void AStreamSentEveryTickOrWithinTheRenderDelayGetsNoExtraDelay()
        {
            Assert.AreEqual(0, Target(Fed(100, 1, 1, 1), MaxDelay), "every tick");
            Assert.AreEqual(0, Target(Fed(100, 2, 3, 2), MaxDelay), "within the render delay");
            Assert.AreEqual(0, Target(new UpdateGapDelay(), MaxDelay), "nothing measured yet");
            Assert.AreEqual(0, Target(Fed(100), MaxDelay), "one state is no gap");
        }

        [Test]
        public void ASlowStreamIsDelayedByTheGapTheRenderDelayDoesNotCoverPlusATick()
        {
            Assert.AreEqual(4, Target(Fed(100, 6, 6, 6), MaxDelay), "an entity updated every 6 ticks");
            Assert.AreEqual(18, Target(Fed(100, 20, 20), MaxDelay), "every 20th tick");
            Assert.AreEqual(MaxDelay, Target(Fed(100, 50, 50), MaxDelay), "capped");
            Assert.AreEqual(0, Target(Fed(100, 20, 20), 0), "0 turns it off");
        }

        [Test]
        public void StaggeredWindowsSettleOnTheLongerGapAndOneShortGapDoesNotDropIt()
        {
            var d = Fed(100, 18, 24, 18, 24, 18);
            Assert.AreEqual(24, d.GapTicks, "an entity updated every 6 ticks in 20-tick windows arrives 18 and 24 apart");
            d.Observe(100u + 18 + 24 + 18 + 24 + 18 + 2);
            Assert.AreEqual(18, d.GapTicks, "a single short gap (a settle keyframe) keeps the one before it");
        }

        [Test]
        public void AGapLongerThanASecondIsAnEntityAtRestAndIsIgnored()
        {
            var d = Fed(100, 6, 6, 600);
            Assert.AreEqual(6, d.GapTicks, "it stood still for ten seconds, then walked on at its rate");
            d.Observe(50); // older than the newest: out of order
            Assert.AreEqual(6, d.GapTicks);
        }

        [Test]
        public void TheDelayMovesAtHalfTheTickRateAndNeverJumps()
        {
            var d = Fed(100, 20, 20);
            double previous = 0;
            int frames = 0;
            while (d.DelayTicks < 18 && frames < 1000)
            {
                d.Advance(RenderDelay, MaxDelay, Frame);
                Assert.LessOrEqual(d.DelayTicks - previous, UpdateGapDelay.SlewTicksPerSecond * Frame + 1e-9, "at most half a tick a frame");
                previous = d.DelayTicks;
                frames++;
            }
            Assert.AreEqual(18, d.DelayTicks, 1e-9);
            Assert.AreEqual(36, frames, "18 ticks at 30 ticks a second is 0.6 s");
            d.Advance(RenderDelay, 0, Frame);
            Assert.AreEqual(0, d.DelayTicks, "turning it off drops it at once");
            d.Reset();
            Assert.AreEqual(0, d.GapTicks);
            Assert.AreEqual(0, d.Advance(RenderDelay, MaxDelay, Frame));
        }

        /// <summary>
        /// An entity walking at 3 m/s, sent every 20th tick with no velocity (as a crowd member in a slow distance
        /// tier is), drawn once a tick <see cref="RenderDelay"/> ticks behind the newest tick heard. Without the delay
        /// it stands for most of every gap and jumps a metre; with it, once the delay has settled, it moves the same
        /// 5 cm every tick.
        /// </summary>
        [Test]
        public void AnEntitySentEveryTwentiethTickMovesSmoothlyInsteadOfHoldingAndJumping()
        {
            Assert.Greater(Walk(delayed: false), 0.8f, "without the delay it holds for 17 ticks, then jumps 85 cm");
            Assert.Less(Walk(delayed: true), 0.051f, "with it, every step is the 5 cm it walks a tick");
        }

        private static float Walk(bool delayed)
        {
            const float speed = 3f;
            const uint gap = 20;
            var go = new GameObject("slow walker");
            try
            {
                var buffer = go.AddComponent<RemoteInterpolator>();
                var delay = new UpdateGapDelay();
                float worst = 0;
                bool has = false;
                Vector3 last = default;
                for (uint heard = 1000; heard < 1600; heard++)
                {
                    if (heard % gap == 0)
                    {
                        buffer.Push(heard, new Vector3(speed * heard * NetworkTime.TickInterval, 0, 0), Quaternion.identity, Vector3.zero);
                        delay.Observe(heard);
                    }
                    double extra = delayed ? delay.Advance(RenderDelay, MaxDelay, Frame) : 0;
                    Assert.IsTrue(buffer.Sample(heard - RenderDelay - extra, out Vector3 p, out Quaternion _));
                    // Measure once the delay has settled (0.6 s to reach 18 ticks) and the buffer has two states.
                    if (heard >= 1200)
                    {
                        if (has) worst = Mathf.Max(worst, Mathf.Abs(p.x - last.x));
                        has = true;
                    }
                    last = p;
                }
                return worst;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
