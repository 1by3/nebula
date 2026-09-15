using System;
using System.Collections.Generic;
using System.Linq;
using Nebula.Hosting;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// Phase 4 of autoscaling: a retired worker is parked instead of killed on a host where the instance is already
    /// paid for, and a scale-out takes it back before launching anything. The billing clock and the rules the
    /// orchestrator applies are pure functions, so they are tested here without a mesh; the host contract is tested
    /// against a fake host that parks and against the two hosts that ship.
    /// </summary>
    public class IdlePoolTests
    {
        private const float Hour = HetznerWorkerHost.BillingPeriodSeconds;

        // ------------------------------------------------------------------------------------ billing clock

        [Test]
        public void AMachineParkedInsideItsFirstHourIsPaidForUntilTheHourEnds()
        {
            // Launched at t = 100, parked five minutes later: Hetzner has charged one started hour.
            Assert.AreEqual(100f + Hour, HetznerWorkerHost.BilledUntilSeconds(100f, 100f + 300f), 0.01f);
        }

        [Test]
        public void AMachineIsAlwaysPaidForAtLeastOneStartedHour()
        {
            Assert.AreEqual(Hour, HetznerWorkerHost.BilledUntilSeconds(0f, 0f), 0.01f);
            Assert.AreEqual(Hour, HetznerWorkerHost.BilledUntilSeconds(0f, 1f), 0.01f);
        }

        [Test]
        public void EveryStartedHourCounts()
        {
            Assert.AreEqual(2f * Hour, HetznerWorkerHost.BilledUntilSeconds(0f, Hour + 60f), 0.01f);
            Assert.AreEqual(3f * Hour, HetznerWorkerHost.BilledUntilSeconds(0f, 2f * Hour + 1f), 0.01f);
            // Exactly on the boundary the hour that has just ended is the one that was paid for.
            Assert.AreEqual(Hour, HetznerWorkerHost.BilledUntilSeconds(0f, Hour), 0.01f);
        }

        [Test]
        public void AShorterIdlePoolGivesTheMachineUpEarlyAndALongerOneIsNotHonoured()
        {
            // Parked at t = 600 of a paid hour: free until the hour ends, less the margin the delete call needs.
            float Margin = HetznerWorkerHost.BillingSafetyMarginSeconds;
            Assert.AreEqual(Hour - Margin, HetznerWorkerHost.RetainUntilSeconds(0f, 600f, 0f), 0.01f);
            Assert.AreEqual(900f, HetznerWorkerHost.RetainUntilSeconds(0f, 600f, 300f), 0.01f);
            // Two hours of idle pool would mean paying for a second hour; the machine goes at the end of the first.
            Assert.AreEqual(Hour - Margin, HetznerWorkerHost.RetainUntilSeconds(0f, 600f, 2f * Hour), 0.01f);
        }

        [Test]
        public void AnIdlePoolShorterThanTheBillingMarginIsStillHonouredInFull()
        {
            // The margin exists so the delete lands before the next hour is charged; it is not a charge against a
            // retention the developer asked for. Subtracting it here made IdlePoolSeconds = 60 mean "delete at once".
            float Margin = HetznerWorkerHost.BillingSafetyMarginSeconds;
            Assert.Greater(Margin, 60f, "the test only means something while the margin is longer than the pool");
            float retainUntil = HetznerWorkerHost.RetainUntilSeconds(0f, 600f, 60f);
            Assert.AreEqual(660f, retainUntil, 0.01f);
            Assert.IsFalse(HetznerWorkerHost.ParkedInstanceExpired(600f, retainUntil), "parked, and not deleted on the next tick");
            Assert.IsFalse(HetznerWorkerHost.ParkedInstanceExpired(659f, retainUntil));
            Assert.IsTrue(HetznerWorkerHost.ParkedInstanceExpired(660f, retainUntil), "the whole minute that was asked for, then gone");
        }

        [Test]
        public void AParkedMachineIsDeletedShortlyBeforeItsPaidHourRunsOut()
        {
            float retainUntil = HetznerWorkerHost.RetainUntilSeconds(0f, 600f, 0f);
            Assert.AreEqual(Hour - HetznerWorkerHost.BillingSafetyMarginSeconds, retainUntil, 0.01f, "the margin is taken off the billed deadline");
            Assert.IsFalse(HetznerWorkerHost.ParkedInstanceExpired(600f, retainUntil));
            Assert.IsFalse(HetznerWorkerHost.ParkedInstanceExpired(retainUntil - 1f, retainUntil));
            Assert.IsTrue(HetznerWorkerHost.ParkedInstanceExpired(retainUntil, retainUntil));
            Assert.Less(retainUntil, Hour, "and the delete goes out before the next hour is charged");
            Assert.IsTrue(HetznerWorkerHost.ParkedInstanceExpired(retainUntil + 10f, retainUntil));
        }

        // ------------------------------------------------------------------------------------ orchestrator rules

        private static List<WorkerInfo> Live(params string[] ids) =>
            ids.Select((id, i) => new WorkerInfo { WorkerId = id, WorkerIndex = (uint)(i + 1), Status = WorkerStatus.Ready }).ToList();

        [Test]
        public void AParkedWorkerIsNeverDealtContainers()
        {
            var live = Live("w1", "w2", "w3");
            var eligible = NebulaOrchestrator.EligibleWorkers(live, new string[0], new string[0], new[] { "w2" });
            CollectionAssert.AreEquivalent(new[] { "w1", "w3" }, eligible.Select(w => w.WorkerId));
        }

        [Test]
        public void DrainingRetiredAndParkedWorkersAreAllIneligible()
        {
            var live = Live("w1", "w2", "w3", "w4");
            var eligible = NebulaOrchestrator.EligibleWorkers(live, new[] { "w2" }, new[] { "w3" }, new[] { "w4" });
            CollectionAssert.AreEqual(new[] { "w1" }, eligible.Select(w => w.WorkerId));
        }

        [Test]
        public void AScaleOutTakesBackTheMostRecentlyParkedWorker()
        {
            var pool = new[]
            {
                new KeyValuePair<string, float>("w3", 100f),
                new KeyValuePair<string, float>("w7", 340f),
                new KeyValuePair<string, float>("w5", 220f),
            };
            Assert.AreEqual("w7", NebulaOrchestrator.PickWorkerToUnpark(pool));
            Assert.IsNull(NebulaOrchestrator.PickWorkerToUnpark(new KeyValuePair<string, float>[0]));
        }

        [Test]
        public void ParkedWorkersKeepTheirIndexSoNoLaunchReusesIt()
        {
            // w2 is parked and still in the managed list, so the next launch gets index 4, not 2.
            Assert.AreEqual(4u, NebulaOrchestrator.NextFreeIndex(new uint[] { 1, 2, 3 }));
        }

        // ------------------------------------------------------------------------------------ host contract

        [Test]
        public void AHostThatParksKeepsTheInstanceAndGivesItBackWithTheSameWorkerId()
        {
            var host = new FakeParkingHost();
            var handle = host.Launch(new WorkerLaunchSpec { WorkerId = "w2", Index = 2 });
            Assert.AreEqual(WorkerHandleState.Running, handle.State);

            host.Park(handle, 0f);
            Assert.AreEqual(WorkerHandleState.Parked, handle.State);
            Assert.AreEqual(0, host.Killed.Count, "parking must not delete the instance");
            Assert.AreEqual(1, host.Live.Count);

            Assert.IsTrue(host.Unpark(handle));
            Assert.AreEqual(WorkerHandleState.Running, handle.State);
            Assert.AreEqual("w2", handle.WorkerId);
            Assert.AreEqual(1, host.Launches, "unparking must not launch a second instance");
        }

        [Test]
        public void AParkedInstanceTheHostHasDroppedRefusesToUnpark()
        {
            var host = new FakeParkingHost();
            var handle = host.Launch(new WorkerLaunchSpec { WorkerId = "w2", Index = 2 });
            host.Park(handle, 0f);
            host.ExpireEverythingParked();
            Assert.AreEqual(WorkerHandleState.Exited, handle.State);
            CollectionAssert.AreEqual(new[] { "w2" }, host.Killed);
            Assert.IsFalse(host.Unpark(handle), "the orchestrator must launch a fresh worker instead");
        }

        [Test]
        public void AHostThatIgnoresParkingKillsInstead()
        {
            var host = new NoParkHost();
            var handle = host.Launch(new WorkerLaunchSpec { WorkerId = "w1", Index = 1 });
            Assert.IsFalse(host.SupportsParking);
            host.Park(handle, 30f);
            CollectionAssert.AreEqual(new[] { "w1" }, host.Killed);
            Assert.IsFalse(host.Unpark(handle));
        }

        [Test]
        public void TheProcessHostDoesNotParkAndBootsInSeconds()
        {
            using (var host = new ProcessWorkerHost("", "127.0.0.1"))
            {
                Assert.IsFalse(host.SupportsParking, "a local process costs nothing to restart");
                Assert.Less(host.TypicalBootSeconds, 10f);
            }
        }

        [Test]
        public void TheHetznerHostParksAndReportsACloudBootTime()
        {
            using (var host = new HetznerWorkerHost(new CloudHostSettings { ApiToken = "test" }))
            {
                Assert.IsTrue(host.SupportsParking);
                Assert.Greater(host.TypicalBootSeconds, 30f, "a VM that boots and downloads a build is not warm in seconds");
            }
        }

        // ------------------------------------------------------------------------------------ fakes

        private sealed class FakeHandle : IWorkerHandle
        {
            public string WorkerId { get; set; }
            public uint Index;
            public WorkerHandleState State { get; set; }
            public string Reason { get; set; } = "";
            public string Address => "10.0.0.1";
            public float RetainUntil;
            public float ParkedSecondsRemaining => State == WorkerHandleState.Parked ? 60f : -1f;
            public string Describe => $"fake instance for {WorkerId}";
        }

        /// <summary>A host with an idle pool, like the cloud ones: parking keeps the instance until the pool expires.</summary>
        private sealed class FakeParkingHost : IWorkerHost
        {
            public readonly List<FakeHandle> Live = new List<FakeHandle>();
            public readonly List<string> Killed = new List<string>();
            public int Launches;

            public string Name => "fake";
            public bool IsReady => true;
            public string InitializationError => "";
            public bool SupportsParking => true;
            public float TypicalBootSeconds => 60f;

            public void Initialize(Action<string, string> log) { }

            public IWorkerHandle Launch(WorkerLaunchSpec spec)
            {
                Launches++;
                var h = new FakeHandle { WorkerId = spec.WorkerId, Index = spec.Index, State = WorkerHandleState.Running };
                Live.Add(h);
                return h;
            }

            public void Kill(IWorkerHandle handle)
            {
                var h = (FakeHandle)handle;
                h.State = WorkerHandleState.Exited;
                h.Reason = "killed";
                Live.Remove(h);
                Killed.Add(h.WorkerId);
            }

            public void Park(IWorkerHandle handle, float idlePoolSeconds)
            {
                var h = (FakeHandle)handle;
                h.State = WorkerHandleState.Parked;
                h.RetainUntil = idlePoolSeconds > 0f ? idlePoolSeconds : HetznerWorkerHost.BillingPeriodSeconds;
            }

            public bool Unpark(IWorkerHandle handle)
            {
                var h = (FakeHandle)handle;
                if (h.State != WorkerHandleState.Parked) return false;
                h.State = WorkerHandleState.Running;
                return true;
            }

            /// <summary>What <see cref="HetznerWorkerHost.Tick"/> does once the paid hour is over.</summary>
            public void ExpireEverythingParked()
            {
                foreach (var h in Live.Where(x => x.State == WorkerHandleState.Parked).ToList())
                {
                    Kill(h);
                    h.Reason = "idle pool expired";
                }
            }

            public void Tick() { }
            public void WriteHandleJson(IWorkerHandle handle, JsonWriter w) { }
            public void Dispose() { }
        }

        /// <summary>A third-party host written before parking existed: <see cref="WorkerHostBase"/> answers for it.</summary>
        private sealed class NoParkHost : WorkerHostBase
        {
            public readonly List<string> Killed = new List<string>();

            public override string Name => "no-park";
            public override bool IsReady => true;
            public override string InitializationError => "";
            public override void Initialize(Action<string, string> log) { }

            public override IWorkerHandle Launch(WorkerLaunchSpec spec) =>
                new FakeHandle { WorkerId = spec.WorkerId, Index = spec.Index, State = WorkerHandleState.Running };

            public override void Kill(IWorkerHandle handle)
            {
                ((FakeHandle)handle).State = WorkerHandleState.Exited;
                Killed.Add(handle.WorkerId);
            }

            public override void Tick() { }
            public override void WriteHandleJson(IWorkerHandle handle, JsonWriter w) { }
            public override void Dispose() { }
        }
    }
}
