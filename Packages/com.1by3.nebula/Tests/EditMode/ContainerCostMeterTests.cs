using System.Diagnostics;
using NUnit.Framework;

namespace Nebula.Tests
{
    public sealed class ContainerCostMeterTests
    {
        private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

        [Test]
        public void AWindowOfOneSlowTickIsNotAReading()
        {
            // The first telemetry beat after a worker starts: one tick that spawned everything it was handed.
            var meter = new ContainerCostMeter();
            meter.Sample(1f);
            meter.CountTick();
            meter.AddSimulation(null, Ms(28));
            meter.Sample(2f);
            Assert.AreEqual(0, meter.Latest.Count, "a one-tick window stays open instead of reading 28 ms per tick");
            Assert.AreEqual(1, meter.Ticks);

            for (int i = 1; i < ContainerCostMeter.MinTicksPerSample; i++)
            {
                meter.CountTick();
                meter.AddSimulation(null, Ms(1));
            }
            meter.Sample(3f);
            Assert.AreEqual(0, meter.Ticks, "the window closed");
            float expected = (28f + (ContainerCostMeter.MinTicksPerSample - 1)) / ContainerCostMeter.MinTicksPerSample;
            Assert.AreEqual(expected, meter.Of("").TickMs, 0.05f, "the slow tick is averaged over the whole window");
        }

        [Test]
        public void AShortWindowKeepsThePreviousReading()
        {
            var meter = new ContainerCostMeter();
            meter.Sample(1f);
            for (int i = 0; i < ContainerCostMeter.MinTicksPerSample; i++) { meter.CountTick(); meter.AddSimulation(null, Ms(2)); }
            meter.Sample(2f);
            Assert.AreEqual(2f, meter.Of("").TickMs, 0.05f);

            meter.CountTick();
            meter.AddSimulation(null, Ms(50));
            meter.Sample(3f);
            Assert.AreEqual(2f, meter.Of("").TickMs, 0.05f, "one hitch between two beats does not replace the reading");
        }

        [Test]
        public void TheFirstCallOnlyStartsTheWindow()
        {
            var meter = new ContainerCostMeter();
            meter.CountTick();
            meter.AddSimulation(null, Ms(5));
            meter.Sample(1f);
            Assert.AreEqual(0, meter.Latest.Count);
            Assert.AreEqual(0, meter.Ticks);
        }
    }
}
