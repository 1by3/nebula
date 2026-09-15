using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>The scaling signal: the rolling per-worker utilization window and how it is attributed to containers.</summary>
    public class WorkerLoadTrackerTests
    {
        private double _now;

        private WorkerLoadTracker NewTracker(float window = 20f)
        {
            _now = 0.0;
            return new WorkerLoadTracker(() => _now) { WindowSeconds = window };
        }

        /// <summary>Tick time that costs exactly <paramref name="fraction"/> of one tick.</summary>
        private static float Ms(float fraction) => fraction * WorkerLoadTracker.TickPeriodMs;

        [Test]
        public void UtilizationIsTickTimeOverTheTickBudget()
        {
            Assert.AreEqual(16.666f, WorkerLoadTracker.TickPeriodMs, 0.01f, "60 Hz");
            Assert.AreEqual(0f, WorkerLoadTracker.UtilizationOf(0f));
            Assert.AreEqual(0.5f, WorkerLoadTracker.UtilizationOf(Ms(0.5f)), 1e-4f);
            Assert.AreEqual(1.2f, WorkerLoadTracker.UtilizationOf(Ms(1.2f)), 1e-4f, "a worker over budget reports over 1");
        }

        [Test]
        public void ThePercentileIgnoresTheOccasionalSpike()
        {
            var t = NewTracker();
            // Nineteen quiet ticks and one terrible one: the p90 is still the quiet figure.
            for (int i = 0; i < 19; i++) { t.SampleAt("w1", Ms(0.2f), _now); _now += 0.5; }
            t.SampleAt("w1", Ms(4f), _now);
            Assert.AreEqual(0.2f, t.Utilization("w1"), 1e-3f);
            // Half the window busy: the p90 is the busy figure.
            var t2 = NewTracker();
            for (int i = 0; i < 10; i++) { t2.SampleAt("w2", Ms(0.2f), i); t2.SampleAt("w2", Ms(0.8f), i + 0.25); }
            Assert.AreEqual(0.8f, t2.Utilization("w2"), 1e-3f);
            Assert.AreEqual(0f, t2.Utilization("nobody"), "a worker that never reported is not busy");
        }

        [Test]
        public void SamplesLeaveTheWindowAndWorkersExpire()
        {
            var t = NewTracker(window: 10f);
            for (int i = 0; i < 10; i++) t.SampleAt("w1", Ms(0.9f), i);
            Assert.AreEqual(0.9f, t.Utilization("w1"), 1e-3f);
            // A window and more later the busy samples have fallen out and only the quiet one counts.
            _now = 20.0;
            t.SampleAt("w1", Ms(0.1f), _now);
            Assert.AreEqual(0.1f, t.Utilization("w1"), 1e-3f);
            // Nothing arrives for a whole window: the worker is forgotten.
            _now = 40.0;
            t.Expire();
            Assert.AreEqual(0, t.Count);
            Assert.AreEqual(0f, t.Utilization("w1"));

            t.SampleAt("w1", Ms(0.5f), _now);
            Assert.AreEqual(1, t.Count);
            t.Forget("w1");
            Assert.AreEqual(0, t.Count);
        }

        [Test]
        public void UtilizationIsSpreadOverAWorkersContainersByWhatIsInsideThem()
        {
            var weights = CostWeights.Default; // base 1, player 4
            var utilization = new Dictionary<string, float> { ["w1"] = 0.6f, ["w2"] = 0.2f, ["idle"] = 0.05f };
            var leases = new List<LeaseInfo>
            {
                new LeaseInfo { ContainerId = "a", WorkerId = "w1", State = LeaseState.Active },
                new LeaseInfo { ContainerId = "b", WorkerId = "w1", State = LeaseState.Active },
                new LeaseInfo { ContainerId = "c", WorkerId = "w2", State = LeaseState.Active },
                new LeaseInfo { ContainerId = "gone", WorkerId = "w2", State = LeaseState.Orphaned },
            };
            // "a" holds two players (cost 1 + 8 = 9), "b" was never reported (cost 1).
            var occupancy = new Dictionary<string, ContainerLoad> { ["a"] = new ContainerLoad { Players = 2 } };
            var result = new Dictionary<string, float>();
            WorkerLoadTracker.Attribute(utilization, leases, occupancy, weights, result);

            Assert.AreEqual(0.6f * 9f / 10f, result["a"], 1e-4f, "the busy container carries most of its worker's tick");
            Assert.AreEqual(0.6f * 1f / 10f, result["b"], 1e-4f);
            Assert.AreEqual(0.6f, result["a"] + result["b"], 1e-4f, "a worker's containers add up to its utilization");
            Assert.AreEqual(0.2f, result["c"], 1e-4f, "the only container of a worker carries all of it");
            Assert.IsFalse(result.ContainsKey("gone"), "a lease nobody owns is not attributed");
            Assert.AreEqual(3, result.Count, "a worker holding nothing contributes nothing");
        }
    }
}
