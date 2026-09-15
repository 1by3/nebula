using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Nebula.Hosting;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Scale to zero: the floor a pending join puts under the scaler, the join-status message the gateway holds a
    /// client with, and the pending-join count travelling on the gateway heartbeat.
    /// </summary>
    public class ScaleToZeroTests
    {
        private const float Chunk = 64f;
        private static Bounds ChunkBounds(int x) => new Bounds(new Vector3((x + 0.5f) * Chunk, 0f, 0.5f * Chunk), new Vector3(Chunk, 512f, Chunk));

        private readonly CostBalancedAssignmentPolicy _policy = new CostBalancedAssignmentPolicy();

        [SetUp]
        public void SetUp() => ContainerRegistry.Rebuild();

        [TearDown]
        public void TearDown() => ContainerRegistry.Rebuild();

        private static List<string> Row(int count)
        {
            var ids = new List<string>();
            for (int x = 0; x < count; x++) ids.Add(ContainerRegistry.RegisterRuntime((ulong)(uint)x, ChunkBounds(x)).ContainerId);
            return ids;
        }

        private static AssignmentInput Input(Dictionary<string, float> utilization) => new AssignmentInput
        {
            Baked = ContainerRegistry.All,
            Runtime = ContainerRegistry.Runtime,
            Eligible = new List<WorkerInfo>(),
            Leases = new List<LeaseInfo>(),
            Occupancy = new Dictionary<string, ContainerLoad>(),
            Utilization = utilization,
            KeepOrder = ContainerRegistry.IsGridded,
        };

        private static ScaleSettings Settings(int min, int max = 4) => new ScaleSettings
        {
            ScaleOutUtilization = 0.7f,
            ScaleInUtilization = 0.3f,
            HoldSeconds = 30f,
            MinGain = 0.1f,
            MinWorkers = min,
            MaxWorkers = max,
        };

        [Test]
        public void AnIdleMeshMayHaveNoWorkersButADemandedOneKeepsOne()
        {
            Assert.AreEqual(0, WorkerScaler.FloorWorkers(0, false), "nobody on the mesh and MinWorkers 0: zero is allowed");
            Assert.AreEqual(1, WorkerScaler.FloorWorkers(0, true), "a client connected or waiting keeps one worker running");
            Assert.AreEqual(2, WorkerScaler.FloorWorkers(2, true), "a higher floor still wins");
            Assert.AreEqual(2, WorkerScaler.FloorWorkers(2, false));
            Assert.AreEqual(0, WorkerScaler.FloorWorkers(-1, false), "a negative floor is read as zero");
        }

        [Test]
        public void ShrinksToZeroOnlyWhileNobodyIsWaitingToJoin()
        {
            var ids = Row(4);
            var input = Input(ids.ToDictionary(id => id, _ => 0.01f));
            var workers = new Dictionary<string, float> { ["w1"] = 0.02f };

            // A pending join puts a floor of one worker under the scaler, so the shrink it would otherwise make never happens.
            var busy = new WorkerScaler();
            busy.Evaluate(0.0, workers, input, _policy, 1, Settings(WorkerScaler.FloorWorkers(0, true)), false);
            var held = busy.Evaluate(120.0, workers, input, _policy, 1, Settings(WorkerScaler.FloorWorkers(0, true)), false);
            Assert.AreEqual(ScaleAction.None, held.Action, "a client is connected or waiting: the last worker stays");

            // The same history with nobody on the mesh retires the last worker.
            var idle = new WorkerScaler();
            idle.Evaluate(0.0, workers, input, _policy, 1, Settings(WorkerScaler.FloorWorkers(0, false)), false);
            var gone = idle.Evaluate(120.0, workers, input, _policy, 1, Settings(WorkerScaler.FloorWorkers(0, false)), false);
            Assert.AreEqual(ScaleAction.Shrink, gone.Action);
            Assert.AreEqual("w1", gone.RetireWorkerId);
        }

        [Test]
        public void JoinStatusSurvivesTheWire()
        {
            var w = new NetworkWriter();
            new JoinStatusMsg { State = JoinState.Starting, EstimatedSeconds = 75 }.Write(w);
            var r = new NetworkReader(w.ToArray());
            Assert.AreEqual(MsgId.JoinStatus, (MsgId)r.ReadByte());
            var msg = JoinStatusMsg.Read(r);
            Assert.AreEqual(JoinState.Starting, msg.State);
            Assert.AreEqual(75, msg.EstimatedSeconds);
        }

        [Test]
        public void GatewayHeartbeatCarriesPendingJoinsThroughTheControlPlane()
        {
            var cp = new LocalControlPlane();
            cp.Connect();
            cp.RegisterGateway("gw1", "203.0.113.9", 7000);
            cp.HeartbeatGateway("gw1", 3);
            Assert.AreEqual(3u, cp.Gateways[0].PendingJoins);

            var parsed = ControlPlaneJson.Parse(cp.ToJson());
            Assert.AreEqual(3u, parsed.Gateways[0].PendingJoins, "the orchestrator reads the count off a mirrored snapshot");

            // And the operation the remote plane sends is applied with the count intact.
            var applied = new LocalControlPlane();
            applied.Connect();
            applied.RegisterGateway("gw1", "203.0.113.9", 7000);
            Assert.IsNull(ControlPlaneJson.ApplyBatch("{\"ops\":[{\"op\":\"HeartbeatGateway\",\"gatewayId\":\"gw1\",\"pendingJoins\":2}]}", applied));
            Assert.AreEqual(2u, applied.Gateways[0].PendingJoins);
        }

        [Test]
        public void TheBootEstimateIsAMeshSettingTheGatewayCanRead()
        {
            var cp = new LocalControlPlane();
            cp.Connect();
            cp.SetSetting(MeshSettings.BootSeconds, "75");
            Assert.AreEqual(75, cp.GetSettingInt(MeshSettings.BootSeconds, 0));
            Assert.AreEqual(0, cp.GetSettingInt(MeshSettings.BootSeconds + ".missing", 0));
            Assert.AreEqual(3f, new ProcessWorkerHost("worker.exe", "127.0.0.1").TypicalBootSeconds, "the process host boots in about 3 s");
        }
    }
}
