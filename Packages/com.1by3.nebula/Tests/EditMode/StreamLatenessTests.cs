using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="StreamLateness"/>: the measurement behind the adaptive render delay (<see cref="NebulaConfig.AdaptiveInterpolationDelay"/>).
    /// </summary>
    public sealed class StreamLatenessTests
    {
        private const double Tick = 1.0 / 60.0;

        [Test]
        public void ACleanStreamAddsNothing()
        {
            var lateness = new StreamLateness();
            for (int i = 0; i < 120; i++) lateness.Add(1, i * Tick, (i % 3) * 0.1f);
            Assert.That(lateness.Percentile(120 * Tick, 95f), Is.LessThan(1f), "rounded down, no extra tick");
        }

        [Test]
        public void AJitteryStreamGetsItsNinetyFifthPercentile()
        {
            var lateness = new StreamLateness();
            // One packet in ten arrives four ticks late, one in twenty-five eight ticks late.
            for (int i = 0; i < 100; i++) lateness.Add(1, i * Tick, i % 25 == 0 ? 8f : i % 10 == 0 ? 4f : 0f);
            Assert.AreEqual(4f, lateness.Percentile(100 * Tick, 95f), "the 95th percentile is the four-tick spikes, not the worst");
            Assert.AreEqual(8f, lateness.Percentile(100 * Tick, 100f), "the 100th is the worst");
            Assert.AreEqual(0f, lateness.Percentile(100 * Tick, 50f));
        }

        [Test]
        public void ASlowWorkerCountsEvenWhenItSendsFewPackets()
        {
            var lateness = new StreamLateness();
            for (int i = 0; i < 200; i++) lateness.Add(1, i * Tick, 0f);
            // A second worker, with a few entities, six ticks behind: 4% of the packets.
            for (int i = 0; i < 8; i++) lateness.Add(2, i * 20 * Tick, 6f);
            Assert.AreEqual(6f, lateness.Percentile(200 * Tick, 95f), "taken per worker, the worst worker's");
        }

        [Test]
        public void OldSamplesAndSilentStreamsLeaveTheWindow()
        {
            var lateness = new StreamLateness();
            for (int i = 0; i < 60; i++) lateness.Add(1, i * Tick, 10f);
            for (int i = 60; i < 240; i++) lateness.Add(1, i * Tick, 1f);
            Assert.AreEqual(1f, lateness.Percentile(240 * Tick, 95f), "the late second is more than two seconds old");
            lateness.Add(2, 240 * Tick, 9f);
            Assert.AreEqual(9f, lateness.Percentile(240 * Tick, 95f));
            Assert.AreEqual(0f, lateness.Percentile(240 * Tick + 2.5, 95f), "both streams silent for longer than the window");
        }

        [Test]
        public void TheDefaultsCoverTwelveTicksAtTheNinetyFifthPercentile()
        {
            var config = UnityEngine.ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                Assert.IsTrue(config.AdaptiveInterpolationDelay);
                Assert.AreEqual(95f, config.InterpolationJitterPercentile);
                Assert.AreEqual(12, config.MaxStreamLagTicks);
            }
            finally { UnityEngine.Object.DestroyImmediate(config); }
        }
    }
}
