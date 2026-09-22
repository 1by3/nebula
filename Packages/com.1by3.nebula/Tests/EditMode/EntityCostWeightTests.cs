using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The per-entity cost weight (docs/cost-telemetry.md): where a weight comes from and in what order
    /// (<see cref="NebulaCost"/>), that it survives a handover on the wire, and that a worker's telemetry document
    /// reports the weighted sum per container.
    /// </summary>
    public class EntityCostWeightTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        private NetworkIdentity Make(string name, float authored = 1f)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            identity.CostWeight = authored;
            identity.Initialize();
            return identity;
        }

        [SetUp]
        public void SetUp() => NebulaCost.Reset();

        [TearDown]
        public void TearDown()
        {
            NebulaCost.Reset();
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
        }

        [Test]
        public void TheAuthoredWeightIsOneUnlessTheGameSaysOtherwise()
        {
            var plain = Make("plain");
            plain.RecomputeCostWeight();
            Assert.AreEqual(1f, plain.EffectiveCostWeight, 1e-5f, "the default must leave balancing exactly as it was");

            var boss = Make("boss", 8f);
            boss.RecomputeCostWeight();
            Assert.AreEqual(8f, boss.EffectiveCostWeight, 1e-5f);
        }

        [Test]
        public void TheCallbackOverridesTheAuthoredWeightAndMayDecline()
        {
            var boss = Make("boss", 8f);
            var critter = Make("critter", 1f);
            NebulaCost.EntityWeight = id => id.name == "boss" ? 32f : -1f; // negative = no opinion

            boss.RecomputeCostWeight();
            critter.RecomputeCostWeight();
            Assert.AreEqual(32f, boss.EffectiveCostWeight, 1e-5f, "the callback wins over the authored value");
            Assert.AreEqual(1f, critter.EffectiveCostWeight, 1e-5f, "declining leaves the authored value standing");
        }

        [Test]
        public void SetCostWeightBeatsBothAndIsNotRecomputedAway()
        {
            var e = Make("npc", 2f);
            NebulaCost.EntityWeight = _ => 5f;
            e.RecomputeCostWeight();
            Assert.AreEqual(5f, e.EffectiveCostWeight, 1e-5f);

            e.SetCostWeight(11f);
            Assert.AreEqual(11f, e.EffectiveCostWeight, 1e-5f);
            e.RecomputeCostWeight();
            Assert.AreEqual(11f, e.EffectiveCostWeight, 1e-5f, "a pinned weight is the game's word, not a suggestion");
        }

        [Test]
        public void AWeightIsClampedAndAThrowingCallbackIsNotFatal()
        {
            var e = Make("npc", 3f);
            NebulaCost.EntityWeight = _ => float.PositiveInfinity;
            e.RecomputeCostWeight();
            Assert.AreEqual(NebulaCost.MaxWeight, e.EffectiveCostWeight, 1e-3f);

            NebulaCost.EntityWeight = _ => throw new System.InvalidOperationException("boom");
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            e.SetCostWeight(1f);      // clear the ceiling; SetCostWeight pins, so recompute would be a no-op
            var fresh = Make("fresh", 3f);
            fresh.RecomputeCostWeight();
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            Assert.AreEqual(3f, fresh.EffectiveCostWeight, 1e-5f, "a callback that throws must not cost the entity its weight");
        }

        [Test]
        public void TheWeightTravelsWithTheEntityInItsSpawnState()
        {
            var boss = Make("boss", 1f);
            boss.NetId = 42;
            boss.SetCostWeight(12f);

            var scratch = new NetworkWriter();
            var msg = EntitySpawnMsg.From(boss, scratch);
            Assert.AreEqual(12f, msg.CostWeight, 0.05f, "an f16 is precise enough for a multiplier");

            var writer = new NetworkWriter();
            msg.Write(writer, MsgId.EntitySpawn);
            var reader = new NetworkReader(new System.ArraySegment<byte>(writer.ToArray(), 1, writer.Length - 1));
            var read = EntitySpawnMsg.Read(reader);
            Assert.AreEqual(12f, read.CostWeight, 0.05f);

            // The receiving worker takes the carried weight rather than re-asking the callback, so the entity
            // reports the same cost on both sides of the seam.
            NebulaCost.EntityWeight = _ => 1f;
            var landed = Make("boss-on-w2", 1f);
            landed.ApplyCarriedCostWeight(read.CostWeight);
            landed.RecomputeCostWeight();
            Assert.AreEqual(12f, landed.EffectiveCostWeight, 0.05f);
        }

        [Test]
        public void AnEmptyOwnedContainerReportsZeroCapacityAfterItsLastEntityLeaves()
        {
            var go = new GameObject("arena");
            _objects.Add(go);
            var container = go.AddComponent<Container>();
            container.ContainerId = "arena";
            container.Size = new Vector3(64f, 64f, 64f);
            ContainerRegistry.Rebuild();
            container.OwnerWorkerId = "w1";

            var telemetry = new MeshTelemetry(() => 1.0);
            telemetry.Accept("{\"worker\":\"w1\",\"containers\":[{\"id\":\"arena\",\"owned\":1,\"tickMs\":20}]}", out _);
            var rows = new Dictionary<string, ContainerCost>();
            telemetry.CopyContainerCost(rows);
            Assert.IsTrue(NebulaCapacity.Derive(rows["arena"], 0.9f).AtCapacity);

            string empty = WorkerTelemetry.ForTests().Write("w1", 1, 11,
                System.Array.Empty<NetworkIdentity>(), false, null);
            telemetry.Accept(empty, out _);
            telemetry.CopyContainerCost(rows);
            Assert.IsTrue(rows.ContainsKey("arena"), "a healthy empty owner publishes zero, not missing telemetry");
            var capacity = NebulaCapacity.Derive(rows["arena"], 0.9f);
            Assert.IsTrue(capacity.Known);
            Assert.IsFalse(capacity.AtCapacity);
            Assert.AreEqual(0f, capacity.Saturation);
        }

        [Test]
        public void AGhostOnlyWorkerMarksItsContainerRowAsUnowned()
        {
            var go = new GameObject("arena");
            _objects.Add(go);
            var container = go.AddComponent<Container>();
            container.ContainerId = "arena";
            container.Size = new Vector3(64f, 64f, 64f);
            ContainerRegistry.Rebuild();
            container.OwnerWorkerId = "w1";
            var ghost = Make("ghost");
            ghost.NetId = 42;
            ghost.HasAuthority = false;
            ghost.SetContainer(container);

            string json = WorkerTelemetry.ForTests().Write("w2", 2, 10, new[] { ghost }, false, null);
            StringAssert.Contains("\"owned\":0", json);
            StringAssert.Contains("\"ghosts\":1", json);
            var rows = new List<ContainerCost>();
            MeshTelemetry.ParseContainers(json, null, rows);
            Assert.IsEmpty(rows, "the map retains ghost counts without replacing the owner's capacity");
        }

        [Test]
        public void TheTelemetryDocumentReportsTheWeightedSumPerContainer()
        {
            var go = new GameObject("arena");
            _objects.Add(go);
            var container = go.AddComponent<Container>();
            container.ContainerId = "arena";
            container.Size = new Vector3(64f, 64f, 64f);
            ContainerRegistry.Rebuild();

            var boss = Make("boss");
            boss.NetId = 1;
            boss.HasAuthority = true;
            boss.IsServerDriven = true;
            boss.SetCostWeight(10f);
            boss.SetContainer(container);

            var critter = Make("critter");
            critter.NetId = 2;
            critter.HasAuthority = true;
            critter.IsServerDriven = true;
            critter.SetCostWeight(0.5f);
            critter.SetContainer(container);

            string json = WorkerTelemetry.ForTests().Write("w1", 1, 10, new[] { boss, critter }, false, null);
            // CostWeights.Default.ServerDriven is 1, so the sum is 10 + 0.5 - and the two heads alone would be 2.
            StringAssert.Contains("\"cost\":10.5", json);
            StringAssert.Contains("\"serverDriven\":2", json);

            var loads = new List<KeyValuePair<string, ContainerLoad>>();
            MeshTelemetry.ParseContainers(json, loads);
            var load = loads.Find(kv => kv.Key == "arena").Value;
            Assert.IsTrue(load.HasEntityCost);
            var weights = CostWeights.Default;
            Assert.AreEqual(weights.Base + 10.5f, weights.Of(load), 1e-3f, "the planner charges the weighted sum, not the head count");
        }
    }
}
