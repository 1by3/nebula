using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// What the orchestrator does with a retirement nobody can take over, and with a worker it has let go of
    /// (NEB-482). The control plane is local and the passes are run one at a time through reflection, so no process
    /// is launched.
    /// </summary>
    public sealed class OrchestratorRetireTests
    {
        private GameObject _go;
        private NebulaConfig _config;
        private LocalControlPlane _plane;
        private NebulaOrchestrator _orchestrator;

        [SetUp]
        public void SetUp()
        {
            ContainerRegistry.Rebuild();
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            _config.UseLocalControlPlane = true;
            _config.DashboardPort = 0;
            _config.WorkerCount = 1;
            _config.AutoScale = false;
            _plane = new LocalControlPlane();
            _plane.Connect();
            _go = new GameObject("orchestrator");
            _orchestrator = _go.AddComponent<NebulaOrchestrator>();
            _orchestrator.Initialize(_config, _plane);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
            Object.DestroyImmediate(_config);
            _plane.Dispose();
            ContainerRegistry.Rebuild();
        }

        private static MethodInfo Method(string name) => typeof(NebulaOrchestrator).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        private void Call(string name, params object[] args) => Method(name).Invoke(_orchestrator, args);

        private void AddWorker(string id, uint index)
        {
            _plane.RegisterWorker(id, index, "127.0.0.1", (ushort)(7000 + index));
            _plane.HeartbeatWorker(id, WorkerStatus.Ready, default);
        }

        [Test]
        public void ARetirementWithNobodyToTakeOverIsCancelledAndTheLeasesStay()
        {
            AddWorker("w1", 1);
            _plane.EnsureContainer("c0");
            _plane.AssignContainer("c0", "w1");

            // The dashboard (or the scaler) retires the only live worker while the mesh still wants one.
            Assert.IsTrue(_orchestrator.RemoveWorker("w1"));
            Assert.AreEqual(1, _orchestrator.DesiredWorkers);

            Call("Rebalance");

            var lease = _plane.FindLease("c0");
            Assert.AreEqual("w1", lease.WorkerId, "the lease was not released");
            Assert.AreNotEqual(LeaseState.Orphaned, lease.State);
            Assert.IsNotNull(_plane.FindWorker("w1"), "and the worker is still on the control plane");
            // Nothing is draining any more, so the worker is not killed after the drain timeout.
            Call("FinishRetirements");
            Assert.IsNotNull(_plane.FindWorker("w1"));
        }

        [Test]
        public void ScalingToZeroStillReleasesTheLeases()
        {
            AddWorker("w1", 1);
            _plane.EnsureContainer("c0");
            _plane.AssignContainer("c0", "w1");
            _orchestrator.SetDesiredWorkers(0);
            _orchestrator.RemoveWorker("w1");

            Call("Rebalance");

            Assert.AreEqual(LeaseState.Orphaned, _plane.FindLease("c0").State, "with nothing wanted the retiree's leases are released as before");
        }

        [Test]
        public void AWorkerTheOrchestratorReleasedIsRefusedWhenItRegistersAgainAndDealtNothing()
        {
            AddWorker("w1", 1);
            AddWorker("w2", 2);
            _plane.EnsureContainer("c0");
            Call("MarkReleased", "w1");

            // w1's process is still up for a few seconds and registers itself again.
            _plane.RegisterWorker("w1", 1, "127.0.0.1", 7001);
            _plane.HeartbeatWorker("w1", WorkerStatus.Ready, default);
            Call("ReapDeadWorkers");

            Assert.IsNull(_plane.FindWorker("w1"), "the released worker's row is taken away");
            var live = (System.Collections.Generic.List<WorkerInfo>)Method("LiveWorkers").Invoke(_orchestrator, null);
            CollectionAssert.AreEqual(new[] { "w2" }, live.Select(w => w.WorkerId).ToArray());

            // And it stays refused after the short-lived retired mark has been forgotten.
            var retired = (System.Collections.Generic.Dictionary<string, float>)typeof(NebulaOrchestrator).GetField("_retired", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_orchestrator);
            retired.Clear();
            _plane.RegisterWorker("w1", 1, "127.0.0.1", 7001);
            _plane.HeartbeatWorker("w1", WorkerStatus.Ready, default);
            live = (System.Collections.Generic.List<WorkerInfo>)Method("LiveWorkers").Invoke(_orchestrator, null);
            CollectionAssert.AreEqual(new[] { "w2" }, live.Select(w => w.WorkerId).ToArray());
        }
    }
}
