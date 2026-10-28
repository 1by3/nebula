using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The worker's per-frame catch-up budget (NEB-483), the ordinary slow frame that still skips nothing (NEB-479),
    /// and a predicted entity that stays on its owner's pose across skipped ticks (NEB-412).
    /// </summary>
    public sealed class WorkerCatchUpBudgetTests
    {
        private GameObject _host;
        private NebulaWorker _worker;
        private long _now;
        private long _tickCost;
        private readonly System.Collections.Generic.List<uint> _ran = new System.Collections.Generic.List<uint>();

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("worker");
            _worker = _host.AddComponent<NebulaWorker>();
            _now = 1000;
            _worker.BudgetClock = () => _now;
            _worker.TickForTest = tick => { _ran.Add(tick); _now += _tickCost; };
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_host);
            NetworkTime.Tick = 0;
            PredictedBehaviourBase.ReplaySkippedTicks = true;
        }

        private static long Ms(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

        /// <summary>One rendered frame: Unity runs <paramref name="fixedUpdates"/> FixedUpdates, all seeing the wall clock at tick <paramref name="derived"/>.</summary>
        private void Frame(int frame, uint derived, int fixedUpdates)
        {
            for (int i = 0; i < fixedUpdates; i++) _worker.CatchUp(derived, frame);
        }

        private int Field(string name) =>
            (int)typeof(NebulaWorker).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(_worker);

        [Test]
        public void AFrameOfSlowTicksSimulatesNothingBeyondTheBudget()
        {
            _tickCost = Ms(24);
            Frame(1, 100, 1); // first tick: starts the clock
            _ran.Clear();
            // A one second frame: 60 ticks elapsed, and Unity runs 20 FixedUpdates.
            Frame(2, 160, 20);
            // 24 ms ticks against a 40 ms budget: two ticks (24 ms, then 48 ms spent) and no more.
            Assert.AreEqual(2, _ran.Count, "ticks simulated in the frame");
            Assert.LessOrEqual(Field("_profileBudgetCuts"), 20);
            Assert.Greater(Field("_profileBudgetCuts"), 0, "the cut-off is counted");
            Assert.Greater(Field("_profileSkippedTicks"), 0, "ticks beyond the lag limit are counted as skipped");
        }

        [Test]
        public void SustainedLoadKeepsEveryFrameNearTheBudget()
        {
            _tickCost = Ms(24);
            Frame(0, 100, 1);
            uint derived = 100;
            int worst = 0;
            for (int frame = 1; frame <= 40; frame++)
            {
                _ran.Clear();
                derived += 5; // 83 ms of wall clock per frame at 60 Hz
                Frame(frame, derived, 5);
                worst = System.Math.Max(worst, _ran.Count);
            }
            Assert.LessOrEqual(worst, 2, "no frame simulates more than the budget allows");
            Assert.Greater(Field("_profileSkippedTicks"), 0, "the worker sheds the ticks it cannot afford");
        }

        [Test]
        public void AnOrdinaryFiftyMillisecondFrameSkipsNothing()
        {
            _tickCost = Ms(6);
            Frame(0, 100, 1);
            uint derived = 100;
            for (int frame = 1; frame <= 30; frame++)
            {
                derived += 3; // 50 ms
                _ran.Clear();
                Frame(frame, derived, 3);
                Assert.AreEqual(3, _ran.Count, $"frame {frame} runs the three ticks that elapsed");
            }
            Assert.AreEqual(0, Field("_profileSkippedTicks"));
            Assert.AreEqual(0, Field("_profileBudgetCuts"));
            for (int i = 1; i < _ran.Count; i++) Assert.AreEqual(_ran[i - 1] + 1, _ran[i]);
        }

        [Test]
        public void LeftoverTicksWithinTheLagLimitCarryToTheNextFrame()
        {
            _tickCost = Ms(24);
            Frame(0, 100, 1);
            _ran.Clear();
            Frame(1, 106, 3); // six ticks elapsed, budget allows two
            Assert.AreEqual(new uint[] { 101, 102 }, _ran.ToArray());
            _ran.Clear();
            Frame(2, 106, 3); // no new time: the next frame continues where the budget stopped
            Assert.AreEqual(new uint[] { 103, 104 }, _ran.ToArray());
            Assert.AreEqual(0, Field("_profileSkippedTicks"), "within MaxLagTicks nothing is skipped");
        }

        [Test]
        public void SkippedTicksAreSteppedAfterAHitchButNotWhileTheWorkerKeepsSkipping()
        {
            _tickCost = Ms(1);
            Frame(0, 100, 1);
            Frame(1, 101, 1);
            Frame(2, 141, 1); // one long frame: 40 ticks elapsed, 25 of them past the lag limit
            Assert.Greater(Field("_profileSkippedTicks"), 0);
            Assert.IsTrue(PredictedBehaviourBase.ReplaySkippedTicks, "a single skip is a hitch: predicted entities step the skipped ticks");
            Frame(3, 170, 1); // and again within a second: the worker is overloaded
            Assert.IsFalse(PredictedBehaviourBase.ReplaySkippedTicks, "while it keeps skipping, it does not add the skipped ticks back");
            uint derived = 170;
            for (int frame = 4; frame < 80; frame++) Frame(frame, ++derived, 3); // it catches up and keeps up for over a second
            Assert.AreEqual(0, _worker.SkippingSustained(derived) ? 1 : 0);
            Assert.IsTrue(PredictedBehaviourBase.ReplaySkippedTicks, "caught up: the next hitch is stepped again");
        }

        [Test]
        public void ABudgetOfZeroIsOff()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            config.WorkerCatchUpBudgetMs = 0f;
            typeof(NebulaWorker).GetProperty("Config").SetValue(_worker, config);
            _tickCost = Ms(24);
            Frame(0, 100, 1);
            _ran.Clear();
            Frame(1, 104, 3);
            Assert.AreEqual(4, _ran.Count, "MaxCatchUpTicks still applies per FixedUpdate, so 2 + 2");
            Object.DestroyImmediate(config);
        }
    }

    /// <summary>NEB-412: the authority steps a predicted entity through the inputs of ticks the worker skipped.</summary>
    public sealed class SkippedTickInputTests
    {
        [Test]
        public void SkippingTenTicksMidFlightLeavesTheWorkerOnTheOwnersPose()
        {
            using var rig = new PredictedPairRig(lag: 3);
            rig.Worker.StepSkipped = true; // the walker advances itself, so it opts in
            rig.Client.Hands = new Vector3(6f, 0f, 3f);
            rig.Advance(20);
            int corrections = rig.Client.Corrections;
            // The worker misses ten ticks: the owner keeps predicting and sending, the worker does not run them.
            rig.SkipWorkerTicks(10);
            rig.Advance(30);
            Assert.AreEqual(10, rig.Worker.SkippedTicksStepped, "each skipped tick was stepped from its input");
            Assert.AreEqual(corrections, rig.Client.Corrections, "the owner was never corrected");
        }

        [Test]
        public void ByDefaultTheSameSkipIsACorrection()
        {
            // Told nothing of the dropped ticks (NEB-484 tells a client of protocol 31, DroppedTickRebaseTests).
            using var rig = new PredictedPairRig(lag: 3) { ReportDropped = false };
            rig.Client.Hands = new Vector3(6f, 0f, 3f);
            rig.Advance(20);
            int corrections = rig.Client.Corrections;
            rig.SkipWorkerTicks(10);
            rig.Advance(30);
            Assert.AreEqual(0, rig.Worker.SkippedTicksStepped);
            Assert.Greater(rig.Client.Corrections, corrections, "an entity that does not opt in is corrected after a skip");
        }
    }
}
