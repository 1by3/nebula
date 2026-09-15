using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The planner-driven scaler over synthetic histories: grow, hold, blocked by a container that cannot be split,
    /// shrink, the min/max limits, and one change per hold.
    /// </summary>
    public class WorkerScalerTests
    {
        private const float Chunk = 64f;
        private static ulong IdOf(int x) => (ulong)(uint)x;
        private static Bounds ChunkBounds(int x) => new Bounds(new Vector3((x + 0.5f) * Chunk, 0f, 0.5f * Chunk), new Vector3(Chunk, 512f, Chunk));

        private readonly CostBalancedAssignmentPolicy _policy = new CostBalancedAssignmentPolicy();

        [SetUp]
        public void SetUp() => ContainerRegistry.Rebuild();

        [TearDown]
        public void TearDown() => ContainerRegistry.Rebuild();

        /// <summary>A row of chunks along x, so the Morton order is the row order.</summary>
        private static List<string> Row(int count)
        {
            var ids = new List<string>();
            for (int x = 0; x < count; x++) ids.Add(ContainerRegistry.RegisterRuntime(IdOf(x), ChunkBounds(x)).ContainerId);
            return ids;
        }

        private static AssignmentInput Input(Dictionary<string, float> utilization, Dictionary<string, ContainerLoad> occupancy = null) => new AssignmentInput
        {
            Baked = ContainerRegistry.All,
            Runtime = ContainerRegistry.Runtime,
            Eligible = new List<WorkerInfo>(),
            Leases = new List<LeaseInfo>(),
            Occupancy = occupancy ?? new Dictionary<string, ContainerLoad>(),
            Utilization = utilization,
            KeepOrder = ContainerRegistry.IsGridded,
        };

        private static Dictionary<string, float> Spread(IEnumerable<string> ids, float each) => ids.ToDictionary(id => id, _ => each);

        private static ScaleSettings Settings(int min = 1, int max = 4) => new ScaleSettings
        {
            ScaleOutUtilization = 0.7f,
            ScaleInUtilization = 0.3f,
            HoldSeconds = 30f,
            MinGain = 0.1f,
            MinWorkers = min,
            MaxWorkers = max,
        };

        [Test]
        public void GrowsOnlyAfterTheHoldAndOnlyWhenAnExtraWorkerHelps()
        {
            var ids = Row(8);
            var input = Input(Spread(ids, 0.9f / 8f));
            var workers = new Dictionary<string, float> { ["w1"] = 0.9f };
            var scaler = new WorkerScaler();

            var d = scaler.Evaluate(0.0, workers, input, _policy, 1, Settings(), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            Assert.AreEqual(0.9f, d.Peak, 1e-4f);
            StringAssert.Contains("holding", d.Reason);

            d = scaler.Evaluate(29.0, workers, input, _policy, 1, Settings(), false);
            Assert.AreEqual(ScaleAction.None, d.Action, "still inside the hold");
            Assert.AreEqual(29f, d.HeldSeconds, 1e-3f);

            d = scaler.Evaluate(31.0, workers, input, _policy, 1, Settings(), false);
            Assert.AreEqual(ScaleAction.Grow, d.Action);
            Assert.AreEqual("", d.BlockedBy);
            StringAssert.Contains("growing to 2", d.Reason);

            // One change per hold: the clock restarts, so the very next pass only holds again.
            d = scaler.Evaluate(31.5, workers, input, _policy, 2, Settings(), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            StringAssert.Contains("holding", d.Reason);
        }

        [Test]
        public void DoesNotGrowWhileAWorkerIsStillLaunchingOrDraining()
        {
            var ids = Row(8);
            var input = Input(Spread(ids, 0.9f / 8f));
            var workers = new Dictionary<string, float> { ["w1"] = 0.9f };
            var scaler = new WorkerScaler();
            scaler.Evaluate(0.0, workers, input, _policy, 1, Settings(), false);
            var d = scaler.Evaluate(60.0, workers, input, _policy, 1, Settings(), true);
            Assert.AreEqual(ScaleAction.None, d.Action);
            StringAssert.Contains("settle", d.Reason);
            Assert.AreEqual(0f, scaler.HeldOut(60.0), "the hold restarts once the mesh has settled");
        }

        [Test]
        public void ReportsTheContainerThatCannotBeSplitInsteadOfGrowing()
        {
            var ids = Row(2);
            // One cell holds a crowd: splitting the world in two leaves it whole on one worker.
            var occupancy = new Dictionary<string, ContainerLoad> { [ids[0]] = new ContainerLoad { Players = 20 } };
            var input = Input(new Dictionary<string, float> { [ids[0]] = 0.85f, [ids[1]] = 0.05f }, occupancy);
            var workers = new Dictionary<string, float> { ["w1"] = 0.9f };
            var scaler = new WorkerScaler();

            scaler.Evaluate(0.0, workers, input, _policy, 1, Settings(), false);
            var d = scaler.Evaluate(31.0, workers, input, _policy, 1, Settings(), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            Assert.AreEqual(ids[0], d.BlockedBy);
            StringAssert.Contains("cannot be split", d.Reason);
        }

        [Test]
        public void DoesNotGrowPastTheCeiling()
        {
            var ids = Row(8);
            var input = Input(Spread(ids, 0.9f / 8f));
            var workers = new Dictionary<string, float> { ["w1"] = 0.9f, ["w2"] = 0.9f };
            var scaler = new WorkerScaler();
            scaler.Evaluate(0.0, workers, input, _policy, 2, Settings(min: 1, max: 2), false);
            var d = scaler.Evaluate(60.0, workers, input, _policy, 2, Settings(min: 1, max: 2), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            Assert.AreEqual(0f, d.HeldSeconds, "an overloaded mesh at the ceiling is not 'holding', there is nothing to wait for");
        }

        [Test]
        public void ShrinksToTheCheapestWorkerWhenTheMeshIsQuiet()
        {
            var ids = Row(8);
            var input = Input(Spread(ids, 0.2f / 8f));
            var workers = new Dictionary<string, float> { ["w1"] = 0.15f, ["w2"] = 0.05f };
            var scaler = new WorkerScaler();

            var d = scaler.Evaluate(0.0, workers, input, _policy, 2, Settings(), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            StringAssert.Contains("holding: mean", d.Reason);

            d = scaler.Evaluate(31.0, workers, input, _policy, 2, Settings(), false);
            Assert.AreEqual(ScaleAction.Shrink, d.Action);
            Assert.AreEqual("w2", d.RetireWorkerId, "the worker carrying least is the cheapest to hand over");

            // Never below the floor.
            d = scaler.Evaluate(0.0, new Dictionary<string, float> { ["w1"] = 0.05f }, input, _policy, 1, Settings(), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            StringAssert.Contains("steady", d.Reason);
        }

        [Test]
        public void ShrinksToZeroWhenTheFloorAllowsIt()
        {
            var ids = Row(2);
            var input = Input(Spread(ids, 0f));
            var workers = new Dictionary<string, float> { ["w1"] = 0f };
            var scaler = new WorkerScaler();
            scaler.Evaluate(0.0, workers, input, _policy, 1, Settings(min: 0), false);
            var d = scaler.Evaluate(31.0, workers, input, _policy, 1, Settings(min: 0), false);
            Assert.AreEqual(ScaleAction.Shrink, d.Action);
            Assert.AreEqual("w1", d.RetireWorkerId);
        }

        [Test]
        public void DoesNotShrinkWhenTheSurvivorsWouldBeOverTheScaleOutLine()
        {
            // Quiet on average (0.29) and nobody near the scale-out line, but the cells are the same size and
            // carry very different loads: cutting the curve for two workers puts both busy cells on one of them.
            var ids = Row(3);
            var input = Input(new Dictionary<string, float> { [ids[0]] = 0.4f, [ids[1]] = 0.4f, [ids[2]] = 0.07f });
            var workers = new Dictionary<string, float> { ["w1"] = 0.4f, ["w2"] = 0.4f, ["w3"] = 0.07f };
            var scaler = new WorkerScaler();
            scaler.Evaluate(0.0, workers, input, _policy, 3, Settings(min: 1, max: 8), false);
            var d = scaler.Evaluate(31.0, workers, input, _policy, 3, Settings(min: 1, max: 8), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            StringAssert.Contains("not shrinking", d.Reason);
        }

        [Test]
        public void NeverRetiresAWorkerThatMayNotGo()
        {
            var ids = Row(4);
            var input = Input(Spread(ids, 0.05f));
            var workers = new Dictionary<string, float> { ["w1"] = 0.1f, ["w2"] = 0.1f };
            // Stands in for the Dedicated hint of phase 3: this worker is pinned to its container.
            var scaler = new WorkerScaler { CanRetire = id => id != "w2" };
            scaler.Evaluate(0.0, workers, input, _policy, 2, Settings(), false);
            var d = scaler.Evaluate(31.0, workers, input, _policy, 2, Settings(), false);
            Assert.AreEqual(ScaleAction.Shrink, d.Action);
            Assert.AreEqual("w1", d.RetireWorkerId);

            var stuck = new WorkerScaler { CanRetire = _ => false };
            stuck.Evaluate(0.0, workers, input, _policy, 2, Settings(), false);
            d = stuck.Evaluate(31.0, workers, input, _policy, 2, Settings(), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            StringAssert.Contains("may not move", d.Reason);
        }

        // ---------------------------------------------------------------- the settle check and parked workers

        [Test]
        public void ParkedWorkersAreNotCountedAsInFlightSoAParkingMeshSettlesAfterAShrink()
        {
            WorkerScaler.MeshMember Member(bool retiring, bool parked) => new WorkerScaler.MeshMember { Retiring = retiring, Parked = parked };

            // Two workers in service, driving towards two: settled.
            Assert.IsFalse(WorkerScaler.IsInFlight(0, new[] { Member(false, false), Member(false, false) }, 2));
            // One retired into the host's idle pool and the desired count came down with it. Park clears Retiring,
            // so counting "not retiring" would see two workers against a desired one and never settle again.
            Assert.IsFalse(WorkerScaler.IsInFlight(0, new[] { Member(false, false), Member(false, true) }, 1));
            // Still draining: nothing may be decided on this pass.
            Assert.IsTrue(WorkerScaler.IsInFlight(1, new[] { Member(true, false), Member(false, false) }, 1));
            // A launch that has not landed yet.
            Assert.IsTrue(WorkerScaler.IsInFlight(0, new[] { Member(false, false) }, 2));
        }

        [Test]
        public void AParkedWorkerDoesNotStallTheScalerForever()
        {
            var ids = Row(8);
            var input = Input(Spread(ids, 0.9f / 8f));
            var workers = new Dictionary<string, float> { ["w1"] = 0.9f };
            var managed = new[]
            {
                new WorkerScaler.MeshMember { Retiring = false, Parked = false },
                new WorkerScaler.MeshMember { Retiring = false, Parked = true },
            };
            var scaler = new WorkerScaler();
            bool inFlight = WorkerScaler.IsInFlight(0, managed, 1);
            scaler.Evaluate(0.0, workers, input, _policy, 1, Settings(), inFlight);
            var d = scaler.Evaluate(31.0, workers, input, _policy, 1, Settings(), inFlight);
            Assert.AreEqual(ScaleAction.Grow, d.Action, "the hot mesh still grows while one machine sits in the idle pool");
        }

        // ---------------------------------------------------------------- a dry run that says nothing

        [Test]
        public void GrowsOnTheMeasurementWhenThePolicyLeavesTheDryRunUnplanned()
        {
            // The baked policy places runtime containers from the lease rows only, and a dry run has none: every
            // container comes back unassigned and the predicted peak is 0. Believing that would report "N workers
            // are enough" for ever on an all-runtime world.
            var ids = Row(8);
            var input = Input(Spread(ids, 0.9f / 8f));
            var baked = new BakedAssignmentPolicy();

            var plan = baked.Predict(input, 1);
            Assert.AreEqual(ids.Count, plan.Unassigned, "nothing was placed, so the plan means nothing");
            Assert.AreEqual(0f, plan.Peak, 1e-4f);

            var workers = new Dictionary<string, float> { ["w1"] = 0.9f };
            var scaler = new WorkerScaler();
            scaler.Evaluate(0.0, workers, input, baked, 1, Settings(), false);
            var d = scaler.Evaluate(31.0, workers, input, baked, 1, Settings(), false);
            Assert.AreEqual(ScaleAction.Grow, d.Action);
            StringAssert.Contains("unplaced", d.Reason);
        }

        [Test]
        public void GrowsWhenTheDryRunSaysReDealButThePolicyWouldNotMoveAnything()
        {
            // Measured peak is high; the dry run, dealing to empty workers, predicts a comfortable split. With no
            // eligible workers of its own the policy returns no changes, so the re-deal the scaler is waiting for is
            // never going to happen and the mesh would sit over the line for ever.
            var ids = Row(2);
            var input = Input(new Dictionary<string, float> { [ids[0]] = 0.3f, [ids[1]] = 0.3f });
            var workers = new Dictionary<string, float> { ["w1"] = 0.9f };
            var scaler = new WorkerScaler();

            Assert.AreEqual(0, _policy.Compute(input).Count, "the policy has nothing to move this pass");
            Assert.LessOrEqual(_policy.Predict(input, 1).Peak, 0.7f, "and the dry run says one worker is plenty");

            scaler.Evaluate(0.0, workers, input, _policy, 1, Settings(), false);
            var d = scaler.Evaluate(31.0, workers, input, _policy, 1, Settings(), false);
            Assert.AreEqual(ScaleAction.Grow, d.Action);
            StringAssert.Contains("not re-dealing", d.Reason);
        }

        [Test]
        public void StillWaitsForAReDealThePolicyWouldActuallyMake()
        {
            // The same shape, but the policy has workers to deal to and one of them is holding everything: it will
            // move containers this pass, so growing would be premature.
            var ids = Row(4);
            var input = Input(new Dictionary<string, float> { [ids[0]] = 0.3f, [ids[1]] = 0.3f, [ids[2]] = 0.15f, [ids[3]] = 0.15f });
            input.Eligible = new List<WorkerInfo>
            {
                new WorkerInfo { WorkerId = "w1", WorkerIndex = 1, Status = WorkerStatus.Ready },
                new WorkerInfo { WorkerId = "w2", WorkerIndex = 2, Status = WorkerStatus.Ready },
            };
            input.Leases = ids.Select(id => new LeaseInfo { ContainerId = id, WorkerId = "w1", State = LeaseState.Active }).ToList();
            var workers = new Dictionary<string, float> { ["w1"] = 0.9f, ["w2"] = 0.05f };
            var scaler = new WorkerScaler();

            Assert.Greater(_policy.Compute(input).Count, 0, "w2 is idle and the policy is about to use it");
            scaler.Evaluate(0.0, workers, input, _policy, 2, Settings(), false);
            var d = scaler.Evaluate(31.0, workers, input, _policy, 2, Settings(), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            StringAssert.Contains("re-dealing", d.Reason);
        }

        // ---------------------------------------------------------------- a worker whose window is still filling

        [Test]
        public void AWorkerWithNoSamplesIsNeitherTheRetireeNorAReasonToShrink()
        {
            var ids = Row(8);
            var input = Input(Spread(ids, 0.2f / 8f));
            // w1 is busy and w2 has only just been launched: its empty window reads as 0, which used to drag the
            // mean under the scale-in line and make the new machine the cheapest one to hand over.
            var workers = new Dictionary<string, float> { ["w1"] = 0.55f, ["w2"] = 0f };
            var scaler = new WorkerScaler { IsWarm = id => id != "w2" };

            var d = scaler.Evaluate(0.0, workers, input, _policy, 2, Settings(), false);
            Assert.AreEqual(0.55f, d.Mean, 1e-4f, "the cold worker is not part of the signal");
            d = scaler.Evaluate(31.0, workers, input, _policy, 2, Settings(), false);
            Assert.AreEqual(ScaleAction.None, d.Action);

            // Even with the mesh genuinely quiet, nothing is retired until the new worker has reported.
            var quiet = new Dictionary<string, float> { ["w1"] = 0.1f, ["w2"] = 0f };
            scaler.Evaluate(60.0, quiet, input, _policy, 2, Settings(), false);
            d = scaler.Evaluate(95.0, quiet, input, _policy, 2, Settings(), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            StringAssert.Contains("filled their window", d.Reason);

            // Once it has reported, the usual rule applies again and the quiet worker goes.
            var warm = new WorkerScaler();
            warm.Evaluate(100.0, quiet, input, _policy, 2, Settings(), false);
            d = warm.Evaluate(131.0, quiet, input, _policy, 2, Settings(), false);
            Assert.AreEqual(ScaleAction.Shrink, d.Action);
            Assert.AreEqual("w2", d.RetireWorkerId);
        }

        [Test]
        public void TheLoadTrackerKnowsWhichWorkersHaveReportedEnough()
        {
            double now = 0.0;
            var loads = new WorkerLoadTracker(() => now) { WindowSeconds = 20f, MinSamples = 3 };
            Assert.IsFalse(loads.IsWarm("w1"), "never heard of it");
            loads.SampleAt("w1", 8f, 0.0);
            loads.SampleAt("w1", 8f, 1.0);
            now = 1.0;
            Assert.AreEqual(2, loads.SampleCount("w1"));
            Assert.IsFalse(loads.IsWarm("w1"));
            loads.SampleAt("w1", 8f, 2.0);
            now = 2.0;
            Assert.IsTrue(loads.IsWarm("w1"));
            // The samples age out of the window and it goes cold again.
            now = 40.0;
            Assert.AreEqual(0, loads.SampleCount("w1"));
            Assert.IsFalse(loads.IsWarm("w1"));
        }

        [Test]
        public void SaysNothingWithoutWorkers()
        {
            var d = new WorkerScaler().Evaluate(0.0, new Dictionary<string, float>(), Input(new Dictionary<string, float>()), _policy, 0, Settings(min: 0), false);
            Assert.AreEqual(ScaleAction.None, d.Action);
            StringAssert.Contains("no workers", d.Reason);
        }
    }
}
