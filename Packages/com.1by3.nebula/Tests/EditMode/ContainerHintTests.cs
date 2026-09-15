using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Container hints: the struct and its compact form, the manifest and control-plane round trips, and the four
    /// things the cost policy does with them (multiplier, dedicated, affinity, seam).
    /// </summary>
    public class ContainerHintTests
    {
        private const float Chunk = 64f;
        private static ulong IdOf(int x, int z) => ((ulong)(uint)x << 32) | (uint)z;
        private static Bounds ChunkBounds(int x, int z) => new Bounds(new Vector3((x + 0.5f) * Chunk, 0f, (z + 0.5f) * Chunk), new Vector3(Chunk, 512f, Chunk));

        private static List<WorkerInfo> Workers(params uint[] indices) =>
            indices.Select(i => new WorkerInfo { WorkerId = $"w{i}", WorkerIndex = i, Status = WorkerStatus.Ready }).ToList();

        private static LeaseInfo Lease(string id, string worker) =>
            new LeaseInfo { ContainerId = id, WorkerId = worker, State = worker == "" ? LeaseState.Orphaned : LeaseState.Active, Epoch = 1, HasBounds = ContainerRegistry.IsRuntimeId(id) };

        private static ContainerLoad Load(int players) => new ContainerLoad { Players = players };

        [SetUp]
        public void SetUp() => ContainerRegistry.Rebuild();

        [TearDown]
        public void TearDown() => ContainerRegistry.Rebuild();

        /// <summary>A row of chunks along x, so the Morton order is the row order.</summary>
        private static List<string> Row(int count)
        {
            var ids = new List<string>();
            for (int x = 0; x < count; x++) ids.Add(ContainerRegistry.RegisterRuntime(IdOf(x, 0), ChunkBounds(x, 0)).ContainerId);
            return ids;
        }

        private static AssignmentInput Input(List<WorkerInfo> workers, List<LeaseInfo> leases,
            Dictionary<string, ContainerLoad> occupancy = null, Dictionary<string, ContainerHint> hints = null) => new AssignmentInput
        {
            Baked = ContainerRegistry.All,
            Runtime = ContainerRegistry.Runtime,
            Eligible = workers,
            Leases = leases,
            Occupancy = occupancy ?? new Dictionary<string, ContainerLoad>(),
            Hints = hints ?? new Dictionary<string, ContainerHint>(),
            KeepOrder = ContainerRegistry.IsGridded,
        };

        private static Dictionary<string, string> Apply(List<LeaseInfo> leases, List<KeyValuePair<string, string>> changes)
        {
            var map = leases.ToDictionary(l => l.ContainerId, l => l.State == LeaseState.Active ? l.WorkerId : "");
            foreach (var c in changes) map[c.Key] = c.Value;
            return map;
        }

        // ---------------------------------------------------------------------------------------- the struct

        [Test]
        public void DefaultHintSaysNothing()
        {
            Assert.IsTrue(ContainerHint.Default.IsDefault);
            Assert.IsTrue(default(ContainerHint).IsDefault, "a zeroed struct out of a list means 'as measured', not 'free'");
            Assert.AreEqual(1f, default(ContainerHint).EffectiveMultiplier);
            Assert.AreEqual("", ContainerHint.Default.ToString());
            Assert.AreEqual(ContainerHint.Default, default(ContainerHint));
        }

        [Test]
        public void HintRoundTripsThroughItsCompactForm()
        {
            var hint = new ContainerHint { CostMultiplier = 2.5f, AffinityGroup = "dungeon-3", SeamCost = 0.75f, Dedicated = true };
            string text = hint.ToString();
            Assert.AreEqual("x2.5,group=dungeon-3,seam=0.75,dedicated", text);
            var back = ContainerHint.Parse(text);
            Assert.AreEqual(hint, back);
            Assert.AreEqual(ContainerHint.Default, ContainerHint.Parse(""));
            Assert.AreEqual(ContainerHint.Default, ContainerHint.Parse("nonsense,=,x"), "unknown parts are ignored, not rejected");
            Assert.IsTrue(ContainerHint.Parse("seam=5").EffectiveSeamCost <= 1f, "the seam cost is clamped where it is used");
        }

        [Test]
        public void ManifestEntryCarriesTheHint()
        {
            var manifest = ScriptableObject.CreateInstance<WorldContainerManifest>();
            try
            {
                var hint = new ContainerHint { CostMultiplier = 3f, AffinityGroup = "hub", SeamCost = 0.5f, Dedicated = true };
                manifest.Entries.Add(new WorldContainerManifest.Entry { Id = "cell_0_0_0", Cell = Vector3Int.zero, IsCell = true, Size = Vector3.one * 256f, Hint = hint });
                var json = JsonUtility.ToJson(manifest);
                var back = ScriptableObject.CreateInstance<WorldContainerManifest>();
                JsonUtility.FromJsonOverwrite(json, back);
                Assert.AreEqual(hint, back.Entries[0].Hint, "the hint survives Unity serialization of the manifest");
                Object.DestroyImmediate(back);
            }
            finally { Object.DestroyImmediate(manifest); }
        }

        [Test]
        public void ControlPlaneStoresTheHintBesideTheLease()
        {
            var cp = new LocalControlPlane();
            cp.Connect();
            cp.EnsureContainer("arena");
            Assert.IsFalse(cp.FindLease("arena").HasHint);

            var hint = new ContainerHint { CostMultiplier = 2f, AffinityGroup = "raid", SeamCost = 0.25f, Dedicated = true };
            cp.SetContainerHint("arena", hint);
            Assert.IsTrue(cp.FindLease("arena").HasHint);
            Assert.AreEqual(hint, cp.FindLease("arena").Hint);

            // A hint may be set before anything leases the container: the row is the hint's home.
            cp.SetContainerHint("not-yet", new ContainerHint { CostMultiplier = 4f });
            Assert.AreEqual(4f, cp.FindLease("not-yet").Hint.EffectiveMultiplier);

            // Through the stored document and back, which is also the worker/gateway wire format.
            var back = ControlPlaneJson.Parse(cp.ToJson());
            var row = back.Leases.First(l => l.ContainerId == "arena");
            Assert.IsTrue(row.HasHint);
            Assert.AreEqual(hint, row.Hint);

            // And clearing it lets the baked hint stand again.
            cp.SetContainerHint("arena", ContainerHint.Default);
            Assert.IsFalse(cp.FindLease("arena").HasHint);
            Assert.IsFalse(ControlPlaneJson.Parse(cp.ToJson()).Leases.First(l => l.ContainerId == "arena").HasHint);
        }

        [Test]
        public void TheHintOpTravelsOverTheWire()
        {
            var cp = new LocalControlPlane();
            cp.Connect();
            cp.EnsureContainer("arena");
            var op = new ControlPlaneJson.OpWriter();
            string body = ControlPlaneJson.WriteBatch(new[]
            {
                op.Op(ControlPlaneJson.SetContainerHint).Arg("containerId", "arena").Arg("hint", "x2,group=g,seam=0.5,dedicated").End(),
            });
            Assert.IsNull(ControlPlaneJson.ApplyBatch(body, cp));
            var row = cp.FindLease("arena");
            Assert.IsTrue(row.HasHint && row.Hint.Dedicated && row.Hint.Group == "g");
            Assert.AreEqual(2f, row.Hint.EffectiveMultiplier);
        }

        // ---------------------------------------------------------------------------------------- the policy

        [Test]
        public void CostMultiplierChangesWhereTheCurveIsCut()
        {
            var ids = Row(4);
            var workers = Workers(1, 2);
            var leases = ids.Select(id => Lease(id, "")).ToList();
            var policy = new CostBalancedAssignmentPolicy();

            // Four equal containers and two workers: two each.
            var plain = Apply(leases, policy.Compute(Input(workers, leases)));
            Assert.AreEqual(2, ids.Count(id => plain[id] == plain[ids[0]]), "an unhinted row splits down the middle");

            // The first container is told it is worth three of the others, so it takes a worker on its own.
            var hints = new Dictionary<string, ContainerHint> { [ids[0]] = new ContainerHint { CostMultiplier = 3f } };
            var weighted = Apply(leases, policy.Compute(Input(workers, leases, hints: hints)));
            Assert.AreNotEqual(weighted[ids[0]], weighted[ids[1]], "the heavy container is cut off from its neighbour");
            Assert.AreEqual(weighted[ids[1]], weighted[ids[3]], "and everything else shares the other worker");
        }

        [Test]
        public void DedicatedReservesAWorkerAndTheRestShareTheOthers()
        {
            var ids = Row(5);
            var workers = Workers(1, 2, 3);
            var leases = ids.Select(id => Lease(id, "")).ToList();
            var policy = new CostBalancedAssignmentPolicy();
            var hints = new Dictionary<string, ContainerHint> { [ids[2]] = new ContainerHint { CostMultiplier = 1f, Dedicated = true } };

            var map = Apply(leases, policy.Compute(Input(workers, leases, hints: hints)));
            string hub = map[ids[2]];
            Assert.IsFalse(string.IsNullOrEmpty(hub));
            foreach (string id in ids.Where(i => i != ids[2]))
                Assert.AreNotEqual(hub, map[id], $"{id} must not share the dedicated worker");
            Assert.AreEqual(2, ids.Where(i => i != ids[2]).Select(i => map[i]).Distinct().Count(), "the other four share the two remaining workers");
            Assert.AreEqual("", policy.Note);
        }

        [Test]
        public void TooFewWorkersForTheDedicatedContainersIsReportedAndShared()
        {
            var ids = Row(3);
            var workers = Workers(1);
            var leases = ids.Select(id => Lease(id, "")).ToList();
            var policy = new CostBalancedAssignmentPolicy();
            var hints = new Dictionary<string, ContainerHint> { [ids[0]] = new ContainerHint { Dedicated = true } };

            var map = Apply(leases, policy.Compute(Input(workers, leases, hints: hints)));
            Assert.AreEqual(1, ids.Select(i => map[i]).Distinct().Count(), "one worker holds everything");
            StringAssert.Contains("dedicated", policy.Note);
            StringAssert.Contains("2", policy.Note, "it says how many workers would be needed");
        }

        [Test]
        public void AnAffinityGroupIsDealtAsOneContainer()
        {
            var ids = Row(4);
            var workers = Workers(1, 2);
            var leases = ids.Select(id => Lease(id, "")).ToList();
            var policy = new CostBalancedAssignmentPolicy();

            // The two containers either side of the natural cut share a group, so the cut has to move.
            var hints = new Dictionary<string, ContainerHint>
            {
                [ids[1]] = new ContainerHint { AffinityGroup = "dungeon" },
                [ids[2]] = new ContainerHint { AffinityGroup = "dungeon" },
            };
            var map = Apply(leases, policy.Compute(Input(workers, leases, hints: hints)));
            Assert.AreEqual(map[ids[1]], map[ids[2]], "the group's rooms land on one worker");
            Assert.AreEqual(2, ids.Select(i => map[i]).Distinct().Count(), "both workers are still used");
        }

        [Test]
        public void ASplitAffinityGroupIsPutBackTogether()
        {
            var ids = Row(4);
            var workers = Workers(1, 2);
            // Already dealt with the group straddling the seam.
            var leases = new List<LeaseInfo> { Lease(ids[0], "w1"), Lease(ids[1], "w1"), Lease(ids[2], "w2"), Lease(ids[3], "w2") };
            var policy = new CostBalancedAssignmentPolicy();
            var hints = new Dictionary<string, ContainerHint>
            {
                [ids[1]] = new ContainerHint { AffinityGroup = "dungeon" },
                [ids[2]] = new ContainerHint { AffinityGroup = "dungeon" },
            };
            var map = Apply(leases, policy.Compute(Input(workers, leases, hints: hints)));
            Assert.AreEqual(map[ids[1]], map[ids[2]], "a group that got split is reunited on the next pass");
        }

        [Test]
        public void SeamCostMovesTheCutElsewhere()
        {
            var ids = Row(4);
            var workers = Workers(1, 2);
            var leases = ids.Select(id => Lease(id, "")).ToList();
            var policy = new CostBalancedAssignmentPolicy();
            var occupancy = ids.ToDictionary(id => id, _ => Load(1));

            var plain = Apply(leases, policy.Compute(Input(workers, leases, occupancy)));
            Assert.AreNotEqual(plain[ids[1]], plain[ids[2]], "without hints the cut falls in the middle");

            // The middle pair say a boundary between them is expensive, so the planner cuts to one side instead.
            var hints = new Dictionary<string, ContainerHint>
            {
                [ids[1]] = new ContainerHint { SeamCost = 1f },
                [ids[2]] = new ContainerHint { SeamCost = 1f },
            };
            var hinted = Apply(leases, policy.Compute(Input(workers, leases, occupancy, hints)));
            Assert.AreEqual(hinted[ids[1]], hinted[ids[2]], "the contested pair is no longer split");
            Assert.AreEqual(2, ids.Select(i => hinted[i]).Distinct().Count(), "and both workers still have work");
        }

        [Test]
        public void PredictSeesTheHintsToo()
        {
            var ids = Row(4);
            var workers = Workers(1, 2);
            var leases = ids.Select(id => Lease(id, "w1")).ToList();
            var policy = new CostBalancedAssignmentPolicy();
            var utilization = ids.ToDictionary(id => id, _ => 0.1f);
            var hints = new Dictionary<string, ContainerHint> { [ids[0]] = new ContainerHint { CostMultiplier = 4f } };

            var input = Input(workers, leases, hints: hints);
            input.Utilization = utilization;
            var plan = policy.Predict(input, 2);
            // The multiplier scales the attributed load the dry run adds up, so the hinted container dominates.
            Assert.AreEqual(0.4f, AssignmentPlanner.UtilizationOf(input, ids[0]), 1e-4f);
            Assert.AreEqual(0.1f, AssignmentPlanner.UtilizationOf(input, ids[1]), 1e-4f);
            Assert.AreEqual(0.4f, plan.HeaviestUtilization, 1e-4f, "and it is named as the container to blame");
            Assert.AreEqual(ids[0], plan.HeaviestContainer);
        }

        [Test]
        public void AnUnhintedWorldIsDealtExactlyAsBefore()
        {
            var ids = Row(6);
            var workers = Workers(1, 2, 3);
            var leases = ids.Select(id => Lease(id, "")).ToList();
            var policy = new CostBalancedAssignmentPolicy();

            var withEmptyTable = Apply(leases, policy.Compute(Input(workers, leases)));
            var withNullTable = Apply(leases, policy.Compute(new AssignmentInput
            {
                Baked = ContainerRegistry.All,
                Runtime = ContainerRegistry.Runtime,
                Eligible = workers,
                Leases = leases,
                Occupancy = new Dictionary<string, ContainerLoad>(),
                Hints = null,
                KeepOrder = ContainerRegistry.IsGridded,
            }));
            CollectionAssert.AreEquivalent(withEmptyTable, withNullTable, "no hints must mean the old behaviour, table or not");
            Assert.AreEqual(3, ids.Select(i => withEmptyTable[i]).Distinct().Count());
        }

        [Test]
        public void AskingForARuntimeContainerWithoutAHintNeverTouchesTheOneOnTheRow()
        {
            var dashboard = new ContainerHint { CostMultiplier = 3f, AffinityGroup = "", SeamCost = 0f, Dedicated = true };
            var none = ContainerHint.Default;

            // RequestRuntimeContainer(id, bounds) is called every time an entity approaches the box. It says nothing
            // about the hint, so it must not clear one somebody set from the dashboard or an earlier explicit call.
            Assert.IsFalse(NebulaWorker.ShouldWriteHint(false, true, dashboard, none), "the no-hint overload never writes");
            Assert.IsFalse(NebulaWorker.ShouldWriteHint(false, false, none, none));

            // The explicit overload writes only what actually changes, so the per-approach call is not a write per tick.
            Assert.IsTrue(NebulaWorker.ShouldWriteHint(true, false, none, dashboard), "a new hint on a bare row");
            Assert.IsFalse(NebulaWorker.ShouldWriteHint(true, true, dashboard, dashboard), "the row already says this");
            Assert.IsTrue(NebulaWorker.ShouldWriteHint(true, true, dashboard, none), "explicitly asking for the default clears it");
            Assert.IsTrue(NebulaWorker.ShouldWriteHint(true, true, dashboard, new ContainerHint { CostMultiplier = 2f, AffinityGroup = "", SeamCost = 0f, Dedicated = true }));
        }
    }
}
