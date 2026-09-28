using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The client's reconnect schedule and the transport timeouts it is configured with (NEB-352). Pure C#:
    /// compiled into the package's EditMode tests and into Nebula.Services.Tests.
    /// </summary>
    public class ReconnectBackoffTests
    {
        [Test]
        public void TheDefaultsRetryAfterOneSecondThenEveryTwoForever()
        {
            var backoff = ReconnectBackoff.Default;
            Assert.That(backoff.DelayAfter(0), Is.EqualTo(1f));
            Assert.That(backoff.DelayAfter(1), Is.EqualTo(2f));
            Assert.That(backoff.DelayAfter(2), Is.EqualTo(2f));
            Assert.That(backoff.DelayAfter(500), Is.EqualTo(2f));
            Assert.That(backoff.ShouldGiveUp(1e6f), Is.False, "0 never gives up");
        }

        [Test]
        public void EachDelayGrowsByTheFactorUpToTheMaximum()
        {
            var backoff = new ReconnectBackoff(0.5f, 3f, 10f, 0f);
            Assert.That(backoff.DelayAfter(0), Is.EqualTo(0.5f).Within(1e-5));
            Assert.That(backoff.DelayAfter(1), Is.EqualTo(1.5f).Within(1e-5));
            Assert.That(backoff.DelayAfter(2), Is.EqualTo(4.5f).Within(1e-5));
            Assert.That(backoff.DelayAfter(3), Is.EqualTo(10f));
            Assert.That(backoff.DelayAfter(int.MaxValue), Is.EqualTo(10f), "no overflow however long it has been");
        }

        [Test]
        public void AFactorOfOneKeepsTheDelayFixed()
        {
            var backoff = new ReconnectBackoff(3f, 1f, 30f, 0f);
            Assert.That(backoff.DelayAfter(0), Is.EqualTo(3f));
            Assert.That(backoff.DelayAfter(7), Is.EqualTo(3f));
        }

        [Test]
        public void TheGiveUpTimeStopsTheClient()
        {
            var backoff = new ReconnectBackoff(1f, 2f, 2f, 40f);
            Assert.That(backoff.ShouldGiveUp(39.9f), Is.False);
            Assert.That(backoff.ShouldGiveUp(40f), Is.True);
        }

        [Test]
        public void ValuesThatMakeNoSenseAreRepaired()
        {
            var backoff = new ReconnectBackoff(-1f, 0.5f, 0f, -5f);
            Assert.That(backoff.FirstDelaySeconds, Is.EqualTo(0f));
            Assert.That(backoff.Factor, Is.EqualTo(1f), "a delay never shrinks");
            Assert.That(backoff.GiveUpSeconds, Is.EqualTo(0f));
            Assert.That(backoff.ShouldGiveUp(100f), Is.False);

            var nan = new ReconnectBackoff(float.NaN, float.PositiveInfinity, float.NaN, float.NaN);
            Assert.That(nan.DelayAfter(0), Is.EqualTo(1f));
            Assert.That(nan.DelayAfter(1), Is.EqualTo(2f));

            var inverted = new ReconnectBackoff(5f, 2f, 1f, 0f);
            Assert.That(inverted.DelayAfter(0), Is.EqualTo(5f), "a maximum below the first delay does not cut the first delay");
        }

        [Test]
        public void TheTransportTakesItsTimeoutAndPingInterval()
        {
            using (var standard = new LiteNetTransport("t"))
            {
                Assert.That(standard.DisconnectTimeoutMs, Is.EqualTo(8000));
                Assert.That(standard.PingIntervalMs, Is.EqualTo(500));
            }
            using (var tuned = new LiteNetTransport("t", 3000, 250))
            {
                Assert.That(tuned.DisconnectTimeoutMs, Is.EqualTo(3000));
                Assert.That(tuned.PingIntervalMs, Is.EqualTo(250));
            }
            using (var clamped = new LiteNetTransport("t", 100, 0))
            {
                Assert.That(clamped.PingIntervalMs, Is.EqualTo(10));
                Assert.That(clamped.DisconnectTimeoutMs, Is.EqualTo(100));
            }
            using (var tooShort = new LiteNetTransport("t", 300, 500))
                Assert.That(tooShort.DisconnectTimeoutMs, Is.EqualTo(1000), "at least two pings long");
        }
    }
}
