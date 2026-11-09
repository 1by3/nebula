using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="NebulaWorker.Ticked"/>, the worker's once-per-tick event, on a real worker (<see cref="CrowdMesh"/>)
    /// whose ticks are run by the worker's own catch-up loop: raised exactly once for every tick the worker simulates,
    /// after the entities' <c>NetworkTick</c>, never for a tick it did not simulate; a handler that throws is logged and
    /// the others still run; and the handlers are forgotten when a play session starts.
    /// </summary>
    public sealed class WorkerTickedTests
    {
        private CrowdMesh _crowd;
        private NebulaWorker _worker;
        private CrowdMesh.Walker _walker;

        [SetUp]
        public void SetUp()
        {
            NebulaWorker.ResetTicked();
            _crowd = new CrowdMesh();
            _worker = _crowd.W1.Instance;
            // The catch-up budget is wall time; a clock that never advances keeps the test independent of how long a tick takes.
            _worker.BudgetClock = () => 0;
            _walker = _crowd.SpawnWalkers(_crowd.W1, 1, -120f, -110f, 0f)[0].GetComponent<CrowdMesh.Walker>();
        }

        [TearDown]
        public void TearDown()
        {
            NebulaWorker.ResetTicked();
            _crowd?.Dispose();
            NetworkTime.Tick = 0;
            PredictedBehaviourBase.ReplaySkippedTicks = true;
        }

        /// <summary>One rendered frame: Unity runs <paramref name="fixedUpdates"/> FixedUpdates, all seeing the wall clock at tick <paramref name="derived"/>.</summary>
        private void Frame(int frame, uint derived, int fixedUpdates)
        {
            for (int i = 0; i < fixedUpdates; i++) _crowd.W1.Act(() => _worker.CatchUp(derived, frame));
        }

        [Test]
        public void RaisedExactlyOnceForEveryTickTheWorkerSimulatesUnderCatchUp()
        {
            var ticks = new List<uint>();
            var deltas = new List<float>();
            var entityTicksAtRaise = new List<int>();
            NebulaWorker.Ticked += (tick, deltaTime) =>
            {
                ticks.Add(tick);
                deltas.Add(deltaTime);
                entityTicksAtRaise.Add(_walker.Ticks);
                Assert.AreEqual(tick, _worker.CurrentTick, "raised inside the tick it names");
            };

            Frame(1, 100, 1);
            CollectionAssert.AreEqual(new uint[] { 100 }, ticks, "the first tick");

            // A slow frame: five ticks elapsed, and Unity runs three FixedUpdates to make up for it. The loop simulates
            // at most MaxCatchUpTicks per FixedUpdate, so the five ticks are spread over the three calls.
            Frame(2, 105, 3);
            CollectionAssert.AreEqual(new uint[] { 100, 101, 102, 103, 104, 105 }, ticks, "once per tick of the catch-up, in order");

            // FixedUpdates that find no new tick simulate nothing, and raise nothing.
            Frame(3, 105, 2);
            Assert.AreEqual(6, ticks.Count, "nothing for a FixedUpdate with no new tick");

            // Further behind than MaxLagTicks: the worker skips tick numbers, and the event is not raised for them.
            uint far = 105 + (uint)NebulaWorker.MaxLagTicks + 10;
            Frame(4, far, 1);
            uint resumed = far - (uint)NebulaWorker.MaxLagTicks;
            CollectionAssert.AreEqual(new uint[] { 100, 101, 102, 103, 104, 105, resumed + 1, resumed + 2 }, ticks, "skipped tick numbers are not raised");

            for (int i = 0; i < ticks.Count; i++)
            {
                Assert.AreEqual(NetworkTime.TickInterval, deltas[i], "the tick's length");
                Assert.AreEqual(i + 1, entityTicksAtRaise[i], "after the entity's NetworkTick of the same tick, and before the next one");
            }
        }

        [Test]
        public void AHandlerThatThrowsIsLoggedAndTheOthersStillRun()
        {
            int before = 0, after = 0;
            NebulaWorker.Ticked += (tick, deltaTime) => before++;
            NebulaWorker.Ticked += (tick, deltaTime) => throw new InvalidOperationException("a game bug");
            NebulaWorker.Ticked += (tick, deltaTime) => after++;

            LogAssert.Expect(LogType.Error, new Regex("NebulaWorker.Ticked handler threw on tick 100.*a game bug", RegexOptions.Singleline));
            LogAssert.Expect(LogType.Error, new Regex("NebulaWorker.Ticked handler threw on tick 101.*a game bug", RegexOptions.Singleline));
            Frame(1, 100, 1);
            Frame(2, 101, 1);

            Assert.AreEqual(2, before);
            Assert.AreEqual(2, after, "the handler after the one that threw still ran, on both ticks");
            Assert.AreEqual(2, _walker.Ticks, "and the worker kept ticking");
        }

        [Test]
        public void HandlersAddedAndRemovedBetweenTicksAreHonoured()
        {
            int first = 0, second = 0;
            Action<uint, float> a = (tick, deltaTime) => first++;
            Action<uint, float> b = (tick, deltaTime) => second++;
            NebulaWorker.Ticked += a;
            Frame(1, 100, 1);
            NebulaWorker.Ticked += b;
            Frame(2, 101, 1);
            NebulaWorker.Ticked -= a;
            Frame(3, 102, 1);

            Assert.AreEqual(2, first, "ticks 100 and 101");
            Assert.AreEqual(2, second, "ticks 101 and 102");
        }

        [Test]
        public void ANewPlaySessionForgetsEveryHandler()
        {
            int raised = 0;
            NebulaWorker.Ticked += (tick, deltaTime) => raised++;
            Frame(1, 100, 1);
            Assert.AreEqual(1, raised);

            // What NebulaStatics runs before the first scene of a play session loads.
            NebulaWorker.ResetTicked();
            Frame(2, 101, 1);
            Assert.AreEqual(1, raised, "the previous session's subscriber is gone");
        }
    }
}
