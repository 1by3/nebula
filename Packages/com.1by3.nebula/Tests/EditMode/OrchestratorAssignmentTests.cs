using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Nebula.Tests
{
    public class OrchestratorAssignmentTests
    {
        private static readonly List<string> Containers = new List<string> { "quadrant-NE", "quadrant-NW", "quadrant-SE", "quadrant-SW" };

        private static List<WorkerInfo> Workers(params uint[] indices) =>
            indices.Select(i => new WorkerInfo { WorkerId = $"w{i}", WorkerIndex = i, Status = WorkerStatus.Ready }).ToList();

        private static List<LeaseInfo> Leases(params (string c, string w)[] pairs) =>
            pairs.Select(p => new LeaseInfo { ContainerId = p.c, WorkerId = p.w, State = p.w == "" ? LeaseState.Orphaned : LeaseState.Active, Epoch = 1 }).ToList();

        private static Dictionary<string, string> Apply(List<LeaseInfo> leases, List<KeyValuePair<string, string>> changes)
        {
            var map = leases.ToDictionary(l => l.ContainerId, l => l.State == LeaseState.Active ? l.WorkerId : "");
            foreach (var c in changes) map[c.Key] = c.Value;
            return map;
        }

        [Test]
        public void FourWorkersGetOneContainerEach()
        {
            var changes = NebulaOrchestrator.ComputeAssignment(Containers, Workers(1, 2, 3, 4), new List<LeaseInfo>());
            Assert.AreEqual(4, changes.Count);
            var owners = changes.Select(c => c.Value).ToList();
            CollectionAssert.AreEquivalent(new[] { "w1", "w2", "w3", "w4" }, owners);
        }

        [Test]
        public void ThreeWorkersMeansOneOfThemSimulatesTwo()
        {
            var changes = NebulaOrchestrator.ComputeAssignment(Containers, Workers(1, 2, 3), new List<LeaseInfo>());
            var counts = changes.GroupBy(c => c.Value).ToDictionary(g => g.Key, g => g.Count());
            Assert.AreEqual(3, counts.Count);
            CollectionAssert.AreEquivalent(new[] { 2, 1, 1 }, counts.Values);
        }

        [Test]
        public void OneWorkerTakesEverything()
        {
            var changes = NebulaOrchestrator.ComputeAssignment(Containers, Workers(7), new List<LeaseInfo>());
            Assert.AreEqual(4, changes.Count);
            Assert.IsTrue(changes.All(c => c.Value == "w7"));
        }

        [Test]
        public void NoWorkersMeansNoChanges()
        {
            var changes = NebulaOrchestrator.ComputeAssignment(Containers, new List<WorkerInfo>(), new List<LeaseInfo>());
            Assert.IsEmpty(changes);
        }

        [Test]
        public void StableAssignmentIsANoOp()
        {
            var leases = Leases(("quadrant-NE", "w1"), ("quadrant-NW", "w2"), ("quadrant-SE", "w3"), ("quadrant-SW", "w4"));
            var changes = NebulaOrchestrator.ComputeAssignment(Containers, Workers(1, 2, 3, 4), leases);
            Assert.IsEmpty(changes);
        }

        [Test]
        public void DeadWorkersContainerMovesAndNothingElseDoes()
        {
            var leases = Leases(("quadrant-NE", "w1"), ("quadrant-NW", "w2"), ("quadrant-SE", "w3"), ("quadrant-SW", "w4"));
            var changes = NebulaOrchestrator.ComputeAssignment(Containers, Workers(1, 3, 4), leases);
            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual("quadrant-NW", changes[0].Key);
            Assert.AreNotEqual("w2", changes[0].Value);
        }

        [Test]
        public void ReturningWorkerGetsAContainerBackWithOneMove()
        {
            var leases = Leases(("quadrant-NE", "w1"), ("quadrant-NW", "w1"), ("quadrant-SE", "w3"), ("quadrant-SW", "w4"));
            var changes = NebulaOrchestrator.ComputeAssignment(Containers, Workers(1, 2, 3, 4), leases);
            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual("w2", changes[0].Value);
            var final = Apply(leases, changes);
            Assert.AreEqual(4, final.Values.Distinct().Count());
        }

        [Test]
        public void RetiringWorkerIsSimplyLeftOutAndOnlyItsContainerMoves()
        {
            // The orchestrator excludes a draining worker from the eligible set; the policy must then move exactly its container.
            var leases = Leases(("quadrant-NE", "w1"), ("quadrant-NW", "w2"), ("quadrant-SE", "w3"), ("quadrant-SW", "w4"));
            var changes = NebulaOrchestrator.ComputeAssignment(Containers, Workers(1, 2, 3), leases);
            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual("quadrant-SW", changes[0].Key);
            CollectionAssert.Contains(new[] { "w1", "w2", "w3" }, changes[0].Value);
        }

        [Test]
        public void NextFreeIndexReusesGapsAndStartsAtOne()
        {
            Assert.AreEqual(1u, NebulaOrchestrator.NextFreeIndex(new uint[0]));
            Assert.AreEqual(3u, NebulaOrchestrator.NextFreeIndex(new uint[] { 1, 2, 4 }));
            Assert.AreEqual(5u, NebulaOrchestrator.NextFreeIndex(new uint[] { 1, 2, 3, 4 }));
        }

        [Test]
        public void HighestIndexWorkerRetiresFirst()
        {
            var candidates = new[] { new KeyValuePair<string, uint>("w2", 2), new KeyValuePair<string, uint>("w5", 5), new KeyValuePair<string, uint>("w3", 3) };
            Assert.AreEqual("w5", NebulaOrchestrator.PickWorkerToRetire(candidates));
            Assert.IsNull(NebulaOrchestrator.PickWorkerToRetire(new KeyValuePair<string, uint>[0]));
        }

        [Test]
        public void DashboardJsonHelpersEscapeAndParse()
        {
            Assert.AreEqual("\"a\\\"b\\n\"", JsonWriter.Quote("a\"b\n"));
            Assert.IsTrue(OrchestratorHttpServer.TryGetInt("{ \"desired\" : 3 }", "desired", out int n));
            Assert.AreEqual(3, n);
            Assert.IsFalse(OrchestratorHttpServer.TryGetInt("{}", "desired", out _));
            Assert.AreEqual("w7", OrchestratorHttpServer.GetString("{\"workerId\":\"w7\"}", "workerId"));
            Assert.AreEqual("", OrchestratorHttpServer.GetString("{}", "workerId"));
        }

        [Test]
        public void OrphanedContainersAreDealtToTheLeastLoadedWorkers()
        {
            var leases = Leases(("quadrant-NE", "w1"), ("quadrant-NW", ""), ("quadrant-SE", ""), ("quadrant-SW", "w2"));
            var changes = NebulaOrchestrator.ComputeAssignment(Containers, Workers(1, 2), leases);
            var final = Apply(leases, changes);
            Assert.AreEqual(2, final.Values.Count(v => v == "w1"));
            Assert.AreEqual(2, final.Values.Count(v => v == "w2"));
        }
    }
}
