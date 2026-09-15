using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>The pluggable assignment policies and the occupancy they read.</summary>
    public class AssignmentPolicyTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        private const float Chunk = 64f;
        private static ulong IdOf(int x, int z) => ((ulong)(uint)x << 32) | (uint)z;
        private static Bounds ChunkBounds(int x, int z) => new Bounds(new Vector3((x + 0.5f) * Chunk, 0f, (z + 0.5f) * Chunk), new Vector3(Chunk, 512f, Chunk));

        private static List<WorkerInfo> Workers(params uint[] indices) =>
            indices.Select(i => new WorkerInfo { WorkerId = $"w{i}", WorkerIndex = i, Status = WorkerStatus.Ready }).ToList();

        private static LeaseInfo Lease(string id, string worker) =>
            new LeaseInfo { ContainerId = id, WorkerId = worker, State = worker == "" ? LeaseState.Orphaned : LeaseState.Active, Epoch = 1, HasBounds = ContainerRegistry.IsRuntimeId(id) };

        private static ContainerLoad Load(int players, int bots = 0, int serverDriven = 0) => new ContainerLoad { Players = players, Bots = bots, ServerDriven = serverDriven };

        [SetUp]
        public void SetUp() => ContainerRegistry.Rebuild();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
        }

        /// <summary>A row of chunks along x, so the Morton order is the row order.</summary>
        private List<string> Row(int count)
        {
            var ids = new List<string>();
            for (int x = 0; x < count; x++) ids.Add(ContainerRegistry.RegisterRuntime(IdOf(x, 0), ChunkBounds(x, 0)).ContainerId);
            return ids;
        }

        private static AssignmentInput Input(List<WorkerInfo> workers, List<LeaseInfo> leases, Dictionary<string, ContainerLoad> occupancy = null) => new AssignmentInput
        {
            Baked = ContainerRegistry.All,
            Runtime = ContainerRegistry.Runtime,
            Eligible = workers,
            Leases = leases,
            Occupancy = occupancy ?? new Dictionary<string, ContainerLoad>(),
            KeepOrder = ContainerRegistry.IsGridded,
        };

        private static Dictionary<string, string> Apply(List<LeaseInfo> leases, List<KeyValuePair<string, string>> changes)
        {
            var map = leases.ToDictionary(l => l.ContainerId, l => l.State == LeaseState.Active ? l.WorkerId : "");
            foreach (var c in changes) map[c.Key] = c.Value;
            return map;
        }

        [Test]
        public void CostWeightsPriceOccupants()
        {
            var w = CostWeights.Default;
            Assert.AreEqual(1f, w.Of(default), 1e-5f);
            Assert.AreEqual(1f + 4f * 2 + 2f * 3 + 1f * 5 + 0.5f * 4, w.Of(new ContainerLoad { Players = 2, Bots = 3, ServerDriven = 5, Other = 4, Ghosts = 99 }), 1e-5f);
        }

        [Test]
        public void TelemetryContainersAreParsedWithoutTheEntityList()
        {
            string json = "{\"worker\":\"w1\",\"index\":1,\"tick\":5,\"detail\":true,\"containers\":[{\"id\":\"arena\",\"players\":2,\"bots\":1,\"serverDriven\":7,\"other\":0,\"ghosts\":3},{\"id\":\"\",\"players\":0,\"bots\":0,\"serverDriven\":0,\"other\":1,\"ghosts\":0},{\"id\":\"rt_12\",\"players\":0,\"bots\":4,\"serverDriven\":0,\"other\":0,\"ghosts\":0}],\"carried\":[],\"entities\":[{\"id\":\"1\",\"x\":1.5}]}";
            var parsed = new List<KeyValuePair<string, ContainerLoad>>();
            MeshTelemetry.ParseContainers(json, parsed);
            Assert.AreEqual(2, parsed.Count, "the slot for entities in no container is skipped");
            Assert.AreEqual("arena", parsed[0].Key);
            Assert.AreEqual(2, parsed[0].Value.Players);
            Assert.AreEqual(1, parsed[0].Value.Bots);
            Assert.AreEqual(7, parsed[0].Value.ServerDriven);
            Assert.AreEqual(3, parsed[0].Value.Ghosts);
            Assert.AreEqual("rt_12", parsed[1].Key);
            Assert.AreEqual(4, parsed[1].Value.Bots);

            double now = 0;
            var telemetry = new MeshTelemetry(() => now);
            Assert.IsNull(telemetry.Accept(json, out _));
            var occupancy = new Dictionary<string, ContainerLoad>();
            telemetry.CopyOccupancy(occupancy);
            Assert.AreEqual(2, occupancy.Count);
            Assert.AreEqual("w1", occupancy["arena"].WorkerId);
            // A newer report from the same worker replaces its containers; another worker's report adds to them.
            Assert.IsNull(telemetry.Accept("{\"worker\":\"w1\",\"containers\":[{\"id\":\"arena\",\"players\":5}]}", out _));
            Assert.IsNull(telemetry.Accept("{\"worker\":\"w2\",\"containers\":[{\"id\":\"rt_13\",\"players\":1}]}", out _));
            telemetry.CopyOccupancy(occupancy);
            CollectionAssert.AreEquivalent(new[] { "arena", "rt_13" }, occupancy.Keys);
            Assert.AreEqual(5, occupancy["arena"].Players);
            // Old reports expire; a forgotten worker's vanish at once.
            now = MeshTelemetry.ExpireSeconds + 1;
            telemetry.CopyOccupancy(occupancy);
            Assert.IsEmpty(occupancy);
            telemetry.Accept("{\"worker\":\"w2\",\"containers\":[{\"id\":\"rt_13\",\"players\":1}]}", out _);
            telemetry.CopyOccupancy(occupancy);
            CollectionAssert.AreEquivalent(new[] { "rt_13" }, occupancy.Keys);
            telemetry.Forget("w2");
            telemetry.CopyOccupancy(occupancy);
            Assert.IsEmpty(occupancy);
        }

        [Test]
        public void BakedPolicyIsTheOriginalDealerPlusStickyRuntimeContainers()
        {
            var ids = Row(2);
            var leases = new List<LeaseInfo> { Lease("arena", ""), Lease(ids[0], "w2"), Lease(ids[1], "w9") };
            var go = new GameObject("arena");
            _objects.Add(go);
            var c = go.AddComponent<Container>();
            c.ContainerId = "arena";
            ContainerRegistry.Rebuild(); // forgets the runtime boxes...
            ids = Row(2);              // ...so register them again on top of the baked set
            var policy = new BakedAssignmentPolicy();
            var changes = policy.Compute(Input(Workers(1, 2), leases));
            var final = Apply(leases, changes);
            Assert.AreEqual("w1", final["arena"], "the only baked container goes to the first worker");
            Assert.AreEqual("w2", final[ids[0]], "a runtime container with a live owner stays");
            Assert.AreEqual("w1", final[ids[1]], "an orphaned runtime container goes to the least loaded worker");
        }

        [Test]
        public void CostPolicyLeavesABalancedMeshAlone()
        {
            var ids = Row(4);
            var leases = new List<LeaseInfo> { Lease(ids[0], "w1"), Lease(ids[1], "w1"), Lease(ids[2], "w2"), Lease(ids[3], "w2") };
            var policy = new CostBalancedAssignmentPolicy();
            Assert.IsEmpty(policy.Compute(Input(Workers(1, 2), leases)));
            // Slightly uneven load, within the threshold: still nothing moves.
            var occupancy = new Dictionary<string, ContainerLoad> { [ids[0]] = Load(1), [ids[2]] = Load(0, 1) };
            Assert.IsEmpty(policy.Compute(Input(Workers(1, 2), leases, occupancy)));
            Assert.AreEqual(4f + 4f + 2f, policy.TotalCost(Input(Workers(1, 2), leases, occupancy)), 1e-4f);
        }

        [Test]
        public void CostPolicyMovesEdgeContainersTowardsTheIdleWorker()
        {
            // Eight chunks in a row, all on w1, w2 idle: the far half of the row moves to w2 as one contiguous run.
            var ids = Row(8);
            var leases = ids.Select(id => Lease(id, "w1")).ToList();
            var policy = new CostBalancedAssignmentPolicy();
            var changes = policy.Compute(Input(Workers(1, 2), leases));
            Assert.AreEqual(4, changes.Count);
            Assert.IsTrue(changes.All(c => c.Value == "w2"));
            var moved = changes.Select(c => c.Key).ToList();
            var final = Apply(leases, changes);
            // w1 keeps one contiguous run and w2 gets the other: the boundary is a single seam.
            int seams = 0;
            for (int i = 1; i < ids.Count; i++) if (final[ids[i]] != final[ids[i - 1]]) seams++;
            Assert.AreEqual(1, seams);
            Assert.AreEqual("w1", final[ids[0]], "w1 already held everything, so it keeps the run it holds most of");
            Assert.AreEqual(4, final.Values.Count(v => v == "w1"));
        }

        [Test]
        public void CostPolicyCutsByCostNotByCount()
        {
            // A crowd in the first chunk: it alone is worth more than the other seven together, so w1 keeps only it.
            var ids = Row(8);
            var leases = ids.Select(id => Lease(id, "w1")).ToList();
            var occupancy = new Dictionary<string, ContainerLoad> { [ids[0]] = Load(20) }; // cost 81 vs 7 x 1
            var policy = new CostBalancedAssignmentPolicy();
            var final = Apply(leases, policy.Compute(Input(Workers(1, 2), leases, occupancy)));
            Assert.AreEqual("w1", final[ids[0]]);
            for (int i = 1; i < ids.Count; i++) Assert.AreEqual("w2", final[ids[i]], ids[i]);
        }

        [Test]
        public void CostPolicyPlacesAnOrphanNextToItsNeighboursOwnerWhenBalanced()
        {
            var ids = Row(6);
            var leases = new List<LeaseInfo> { Lease(ids[0], "w1"), Lease(ids[1], "w1"), Lease(ids[2], "w1"), Lease(ids[3], "w2"), Lease(ids[4], "w2"), Lease(ids[5], "") };
            var policy = new CostBalancedAssignmentPolicy();
            var changes = policy.Compute(Input(Workers(1, 2), leases));
            Assert.AreEqual(1, changes.Count, "a balanced mesh only places the orphan");
            Assert.AreEqual(ids[5], changes[0].Key);
            Assert.AreEqual("w2", changes[0].Value, "next to its neighbour's owner");
        }

        [Test]
        public void CostPolicyGivesEveryWorkerSomethingWhenThereAreMoreWorkersThanContainers()
        {
            var ids = Row(2);
            var leases = ids.Select(id => Lease(id, "")).ToList();
            var policy = new CostBalancedAssignmentPolicy();
            var final = Apply(leases, policy.Compute(Input(Workers(1, 2, 3), leases)));
            Assert.AreEqual(2, final.Values.Distinct().Count());
            Assert.IsTrue(final.Values.All(v => v != ""));
            Assert.IsEmpty(policy.Compute(Input(new List<WorkerInfo>(), leases)));
        }

        [Test]
        public void CostPolicyMixesBakedAndRuntimeContainersOnOneCurve()
        {
            var go = new GameObject("arena");
            _objects.Add(go);
            var c = go.AddComponent<Container>();
            c.ContainerId = "arena";
            c.Size = new Vector3(Chunk, 512f, Chunk);
            c.Center = Vector3.zero;
            go.transform.position = ChunkBounds(-1, 0).center; // just west of the first chunk
            ContainerRegistry.Rebuild();
            var ids = Row(3);
            var leases = new List<LeaseInfo> { Lease("arena", ""), Lease(ids[0], ""), Lease(ids[1], ""), Lease(ids[2], "") };
            var policy = new CostBalancedAssignmentPolicy();
            var final = Apply(leases, policy.Compute(Input(Workers(1, 2), leases)));
            Assert.AreEqual(2, final.Values.Count(v => v == "w1"));
            Assert.AreEqual(2, final.Values.Count(v => v == "w2"));
            Assert.AreEqual(final["arena"], final[ids[0]], "the arena and its eastern neighbour sit together on the curve");
        }

        [Test]
        public void PredictDealsToSyntheticWorkersAndAddsUpTheirLoad()
        {
            var ids = Row(8);
            var leases = ids.Select(id => Lease(id, "w1")).ToList();
            var input = Input(Workers(1), leases);
            input.Utilization = ids.ToDictionary(id => id, _ => 0.1f);
            var policy = new CostBalancedAssignmentPolicy();

            var one = policy.Predict(input, 1);
            Assert.AreEqual(0.8f, one.Peak, 1e-4f, "one worker carries everything");
            Assert.AreEqual(0.8f, one.Mean, 1e-4f);
            Assert.AreEqual(0, one.Unassigned);
            Assert.AreEqual(8, one.Containers[one.PeakWorker].Count);

            var two = policy.Predict(input, 2);
            Assert.AreEqual(0.4f, two.Peak, 1e-4f, "two workers split the row evenly");
            Assert.AreEqual(2, two.Containers.Count);
            CollectionAssert.AreEquivalent(ids, two.Containers.Values.SelectMany(c => c).ToList());
            Assert.IsTrue(two.Containers.Keys.All(k => k.StartsWith(AssignmentPlanner.SyntheticPrefix)), "the dry run never names a real worker");
            Assert.AreEqual(0.1f, two.HeaviestUtilization, 1e-4f);

            Assert.AreEqual(0f, policy.Predict(input, 0).Peak, "no workers, no plan");
        }

        [Test]
        public void PredictNamesTheContainerThatCarriesTheLoadOnItsOwn()
        {
            // One cell holds a crowd: however many workers there are, it lands whole on one of them.
            var ids = Row(4);
            var leases = ids.Select(id => Lease(id, "w1")).ToList();
            var occupancy = new Dictionary<string, ContainerLoad> { [ids[2]] = Load(30) };
            var input = Input(Workers(1), leases, occupancy);
            input.Utilization = new Dictionary<string, float> { [ids[0]] = 0.02f, [ids[1]] = 0.02f, [ids[2]] = 0.8f, [ids[3]] = 0.02f };
            var policy = new CostBalancedAssignmentPolicy();

            var four = policy.Predict(input, 4);
            Assert.AreEqual(0.8f, four.Peak, 1e-4f, "splitting four ways does not split the crowd");
            Assert.AreEqual(ids[2], four.HeaviestContainer);
            Assert.AreEqual(1, four.Containers[four.PeakWorker].Count);
        }

        [Test]
        public void PredictWorksForTheBakedPolicyToo()
        {
            var ids = Row(4);
            var leases = ids.Select(id => Lease(id, "w1")).ToList();
            var input = Input(Workers(1), leases);
            input.Utilization = ids.ToDictionary(id => id, _ => 0.2f);
            // The baked policy only deals baked containers, so a runtime-only world leaves them all unassigned.
            var plan = new BakedAssignmentPolicy().Predict(input, 2);
            Assert.AreEqual(4, plan.Unassigned);
            Assert.AreEqual(0f, plan.Peak);
        }

        [Test]
        public void CostPolicyRefusesAReDealThatBuysAlmostNothing()
        {
            // Eight chunks, w1 holds seven and w2 one: unbalanced by count, but w2's single chunk is where the load
            // actually is. Cutting the curve in half hands three more of w1's chunks to the worker already carrying
            // the crowd, so the busiest worker ends up worse off; the mesh is busy enough for that to be believed.
            var ids = Row(8);
            var leases = ids.Select((id, i) => Lease(id, i < 7 ? "w1" : "w2")).ToList();
            var input = Input(Workers(1, 2), leases);
            input.Utilization = ids.ToDictionary(id => id, id => id == ids[7] ? 0.45f : 0.07f);
            var policy = new CostBalancedAssignmentPolicy();
            Assert.IsEmpty(policy.Compute(input), "0.49 -> 0.66 on the busiest worker is not a gain at all");

            // The same layout with the load spread evenly does move: the busiest worker halves.
            input.Utilization = ids.ToDictionary(id => id, _ => 0.15f);
            Assert.IsNotEmpty(policy.Compute(input));
        }

        [Test]
        public void CostPolicyStillReDealsWhenNobodyIsBusyEnoughForUtilizationToMeanAnything()
        {
            // Seven chunks against one is a threefold cost imbalance, but at 0.01 of a tick each no re-deal can
            // possibly buy MinGain of a tick budget. Vetoing on that froze the layout until something was already
            // hot; below MinGainFloor the cost-unit balance rule is the one that decides.
            var ids = Row(8);
            var leases = ids.Select((id, i) => Lease(id, i < 7 ? "w1" : "w2")).ToList();
            var input = Input(Workers(1, 2), leases);
            input.Utilization = ids.ToDictionary(id => id, _ => 0.01f);
            var policy = new CostBalancedAssignmentPolicy();
            Assert.Less(0.07f, policy.MinGainFloor, "the busiest worker is nowhere near the floor");

            var changes = policy.Compute(input);
            Assert.IsNotEmpty(changes, "a 7:1 split is re-dealt even though the tick times cannot show the gain");
            var after = Apply(leases, changes);
            Assert.AreEqual(4, ids.Count(id => after[id] == "w1"));
            Assert.AreEqual(4, ids.Count(id => after[id] == "w2"));
        }
    }
}
