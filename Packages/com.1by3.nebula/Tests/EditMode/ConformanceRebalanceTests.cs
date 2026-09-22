using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
using Object = UnityEngine.Object;
#endif

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 13 (<c>docs/conformance-suite.md</c>): a hot container is re-dealt along the boundaries
    /// the game authored without splitting a cohesion group, a held container is not moved until its hold expires,
    /// and one that cannot be split is reported as saturated with the reason
    /// (<c>docs/cohesion-rebalancing.md</c>). Tier A: <see cref="CostBalancedAssignmentPolicy"/>,
    /// <see cref="AssignmentPlanner"/> and <see cref="WorkerScaler"/> are pure C#, so the same file runs in the
    /// Unity Editor and under <c>dotnet test</c>.
    /// <para>
    /// Containers are built for the policy directly rather than through the registry, exactly as
    /// <c>ConformanceCohesionTests</c> does: the policy reads the lists it is handed, and the two builds construct a
    /// <see cref="Container"/> differently.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public class ConformanceRebalanceTests
    {
        private const float Span = 64f;
        private const uint Fleet = 7;

        private readonly List<Container> _containers = new List<Container>();
#if !NEBULA_SERVICE
        private readonly List<GameObject> _objects = new List<GameObject>();
#endif

        [TearDown]
        public void TearDown()
        {
            _containers.Clear();
#if !NEBULA_SERVICE
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
#endif
        }

        /// <summary>A row of equal districts along x, so the Morton order the policy sorts by is c0, c1, c2...</summary>
        private List<Container> Row(int count)
        {
            _containers.Clear();
            for (int i = 0; i < count; i++)
            {
                string id = "c" + i;
                var position = new Vector3((i + 0.5f) * Span, 0f, 0f);
#if NEBULA_SERVICE
                _containers.Add(new Container
                {
                    ContainerId = id,
                    Index = (ushort)i,
                    Size = new Vector3(Span, Span, Span),
                    transform = new ContainerFrame { position = position },
                });
#else
                var go = new GameObject(id);
                _objects.Add(go);
                go.transform.position = position;
                var c = go.AddComponent<Container>();
                c.ContainerId = id;
                c.Size = new Vector3(Span, Span, Span);
                c.Center = Vector3.zero;
                _containers.Add(c);
#endif
            }
            return _containers;
        }

        private static List<WorkerInfo> Workers(params uint[] indices) =>
            indices.Select(i => new WorkerInfo { WorkerId = $"w{i}", WorkerIndex = i, Status = WorkerStatus.Ready }).ToList();

        private static List<LeaseInfo> Leases(params (string container, string worker)[] rows) =>
            rows.Select(r => new LeaseInfo
            {
                ContainerId = r.container,
                WorkerId = r.worker,
                State = r.worker == "" ? LeaseState.Orphaned : LeaseState.Active,
                Epoch = 1,
            }).ToList();

        private static Dictionary<string, ContainerLoad> EvenLoad(IEnumerable<Container> containers) =>
            containers.ToDictionary(c => c.ContainerId, c => new ContainerLoad { Players = 1 });

        /// <summary>A docked fleet: one cohesion group whose members sit in the named districts.</summary>
        private static List<CohesionGroupInfo> Group(uint group, params string[] containers)
        {
            var info = new CohesionGroupInfo { Group = group, Members = containers.Length };
            info.Containers.AddRange(containers);
            info.Workers.Add("w1");
            return new List<CohesionGroupInfo> { info };
        }

        private AssignmentInput Input(
            List<Container> containers,
            List<WorkerInfo> workers,
            List<LeaseInfo> leases,
            IReadOnlyList<CohesionGroupInfo> cohesion = null,
            Dictionary<string, float> holds = null,
            Dictionary<string, float> utilization = null,
            Dictionary<string, ContainerHint> hints = null) => new AssignmentInput
            {
                Baked = containers,
                Runtime = new List<Container>(),
                Eligible = workers,
                Leases = leases,
                Occupancy = EvenLoad(containers),
                Utilization = utilization ?? new Dictionary<string, float>(),
                Hints = hints ?? new Dictionary<string, ContainerHint>(),
                Cohesion = cohesion ?? new List<CohesionGroupInfo>(),
                Holds = holds ?? new Dictionary<string, float>(),
            };

        private static Dictionary<string, string> Apply(List<LeaseInfo> leases, List<KeyValuePair<string, string>> changes)
        {
            var map = leases.ToDictionary(l => l.ContainerId, l => l.State == LeaseState.Active ? l.WorkerId : "");
            foreach (var change in changes) map[change.Key] = change.Value;
            return map;
        }

        // ---- re-deal along the authored boundaries ---------------------------------------------------------

        [Test]
        public void AHotWorkersDistrictsAreReDealtAlongTheAuthoredBoundariesWithoutSplittingACohesionGroup()
        {
            // A city of six districts, all simulated by one worker. c2 and c3 hold a docked fleet: one cohesion
            // group. The seam hints say a cut beside them is expensive, which is the authored boundary the planner
            // is meant to respect; the group is what it may not cut through at all.
            var containers = Row(6);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w1"), ("c3", "w1"), ("c4", "w1"), ("c5", "w1"));
            var hints = new Dictionary<string, ContainerHint>
            {
                { "c2", new ContainerHint { CostMultiplier = 1f, SeamCost = 0.9f, AffinityGroup = "" } },
                { "c3", new ContainerHint { CostMultiplier = 1f, SeamCost = 0.9f, AffinityGroup = "" } },
            };
            var policy = new CostBalancedAssignmentPolicy();
            var input = Input(containers, workers, leases, Group(Fleet, "c2", "c3"), hints: hints);

            var changes = policy.Compute(input);
            var owners = Apply(leases, changes);

            Assert.IsNotEmpty(changes, "one worker holding the whole city is re-dealt");
            Assert.AreEqual(2, owners.Values.Distinct().Count(), "the districts end up on both workers");
            Assert.AreEqual(owners["c2"], owners["c3"], "the docked fleet is never split across the cut");
            // Every move is explained, and the explanation names the boundary the cut fell on.
            Assert.AreEqual(changes.Count, policy.Moves.Count, "one explained move per change");
            foreach (var move in policy.Moves)
            {
                Assert.IsNotNull(move.Reason);
                Assert.IsNotEmpty(move.Reason, $"{move.ContainerId} moved without an explanation");
                StringAssert.Contains("boundary", move.Reason);
                Assert.AreEqual("w1", move.From);
                Assert.AreEqual(owners[move.ContainerId], move.To);
            }
            Assert.IsEmpty(policy.Saturated, "nothing is hot: no utilization was measured at all");
        }

        [Test]
        public void AMoveOfAWholeGroupSaysWhichGroupKeptItTogether()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            // Everything on w2; the group spans the two districts the cut would otherwise fall between.
            var leases = Leases(("c0", "w2"), ("c1", "w2"), ("c2", "w2"), ("c3", "w2"));
            var policy = new CostBalancedAssignmentPolicy();

            var changes = policy.Compute(Input(containers, workers, leases, Group(Fleet, "c1", "c2")));
            var owners = Apply(leases, changes);

            Assert.AreEqual(owners["c1"], owners["c2"]);
            var groupMoves = policy.Moves.Where(m => m.ContainerId == "c1" || m.ContainerId == "c2").ToList();
            if (groupMoves.Count > 0)
            {
                Assert.AreEqual(2, groupMoves.Count, "a group moves as one item, so both containers are listed");
                foreach (var move in groupMoves)
                {
                    StringAssert.Contains("cohesion 7", move.Reason);
                    StringAssert.Contains("c1, c2", move.Reason);
                }
            }
            foreach (var move in policy.Moves) Assert.IsNotEmpty(move.Reason);
        }

        // ---- holds -----------------------------------------------------------------------------------------

        [Test]
        public void AHeldDistrictIsNotMovedUntilItsHoldExpires()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w2"), ("c1", "w2"), ("c2", "w2"), ("c3", "w2"));
            var policy = new CostBalancedAssignmentPolicy();

            // The main battle is in c2: the worker asked for it not to be rebalanced for another eight seconds.
            var held = policy.Compute(Input(containers, workers, leases, holds: new Dictionary<string, float> { { "c2", 8f } }));
            CollectionAssert.DoesNotContain(held.Select(c => c.Key).ToList(), "c2", "a held district does not move");
            CollectionAssert.Contains(held.Select(c => c.Key).ToList(), "c3", "the rest of the re-deal still happens");

            // The hold has expired: the telemetry no longer reports it, and the same pass moves it.
            var after = policy.Compute(Input(containers, workers, leases));
            CollectionAssert.Contains(after.Select(c => c.Key).ToList(), "c2", "once the hold expires the district moves");
            foreach (var move in policy.Moves) Assert.IsNotEmpty(move.Reason);
        }

        [Test]
        public void AHotDistrictUnderAHoldIsReportedSaturatedWithTheHoldAsTheReason()
        {
            var containers = Row(2);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"));
            var utilization = new Dictionary<string, float> { { "c0", 0.9f }, { "c1", 0.05f } };
            var policy = new CostBalancedAssignmentPolicy();
            var input = Input(containers, workers, leases, holds: new Dictionary<string, float> { { "c0", 4.2f } }, utilization: utilization);

            var changes = policy.Compute(input);

            CollectionAssert.DoesNotContain(changes.Select(c => c.Key).ToList(), "c0");
            Assert.AreEqual(1, policy.Saturated.Count);
            var row = policy.Saturated[0];
            Assert.AreEqual("c0", row.ContainerId);
            Assert.AreEqual("w1", row.WorkerId);
            Assert.AreEqual(SaturationCause.Held, row.Cause);
            StringAssert.Contains("held for another 4.2 s", row.Reason);
        }

        // ---- saturation ------------------------------------------------------------------------------------

        [Test]
        public void ADistrictThatCannotBeSplitWithoutBreakingACohesionGroupIsReportedWithTheReason()
        {
            // c0 and c1 are one item because the fleet spans them, and together they carry the whole worker.
            var containers = Row(3);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w2"));
            var utilization = new Dictionary<string, float> { { "c0", 0.5f }, { "c1", 0.45f }, { "c2", 0.05f } };
            var policy = new CostBalancedAssignmentPolicy();
            var input = Input(containers, workers, leases, Group(Fleet, "c0", "c1"), utilization: utilization);

            var changes = policy.Compute(input);

            Assert.IsEmpty(changes, "there is no cut that relieves w1 without splitting the group");
            Assert.AreEqual(1, policy.Saturated.Count);
            var row = policy.Saturated[0];
            Assert.AreEqual(SaturationCause.CohesionGroup, row.Cause);
            Assert.AreEqual("c0", row.ContainerId, "the heaviest container of the item");
            Assert.AreEqual("", row.ScopeKey, "these districts are in the public world");
            Assert.AreEqual("w1", row.WorkerId);
            Assert.AreEqual(0.95f, row.Utilization, 1e-4f);
            CollectionAssert.AreEquivalent(new[] { "c0", "c1" }, row.Containers);
            StringAssert.Contains("cohesion 7 spans c0, c1", row.Reason);
        }

        [Test]
        public void AHotDistrictWithNoAuthoredBoundaryIsReportedAsHavingNoneRatherThanShuffled()
        {
            var containers = Row(2);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w2"));
            var utilization = new Dictionary<string, float> { { "c0", 0.9f }, { "c1", 0.05f } };
            var policy = new CostBalancedAssignmentPolicy();

            var changes = policy.Compute(Input(containers, workers, leases, utilization: utilization));

            Assert.IsEmpty(changes, "moving the one district it holds would only move the problem");
            Assert.AreEqual(1, policy.Saturated.Count);
            var row = policy.Saturated[0];
            Assert.AreEqual(SaturationCause.NoBoundary, row.Cause);
            Assert.AreEqual("c0", row.ContainerId);
            StringAssert.Contains("no boundary hint", row.Reason);
            StringAssert.Contains("never subdivides a container", row.Reason);
        }

        [Test]
        public void AQuietMeshReportsNothingHoweverUnevenItIs()
        {
            var containers = Row(2);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w2"));
            // Lopsided, but nowhere near a tick budget: there is nothing to explain.
            var utilization = new Dictionary<string, float> { { "c0", 0.2f }, { "c1", 0.01f } };
            var policy = new CostBalancedAssignmentPolicy();

            policy.Compute(Input(containers, workers, leases, utilization: utilization));

            Assert.IsEmpty(policy.Saturated);
        }

        // ---- the dry run and the scaler --------------------------------------------------------------------

        [Test]
        public void ADryRunKeepsACohesionGroupWholeSoThePredictedPeakIsHonest()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w2"), ("c3", "w2"));
            var utilization = new Dictionary<string, float> { { "c0", 0.4f }, { "c1", 0.1f }, { "c2", 0.1f }, { "c3", 0.4f } };
            var policy = new CostBalancedAssignmentPolicy();
            var input = Input(containers, workers, leases, Group(Fleet, "c0", "c3"), utilization: utilization);

            var plan = policy.Predict(input, 2);

            var of = plan.Containers.First(kv => kv.Value.Contains("c0"));
            CollectionAssert.Contains(of.Value, "c3", "the dry run may not promise a layout the real deal cannot produce");
            Assert.AreEqual(0, plan.Unassigned);
            Assert.IsNotEmpty(plan.Moves, "the plan carries the policy's explanations");
            foreach (var move in plan.Moves) Assert.IsNotEmpty(move.Reason);
        }

        [Test]
        public void TheScalerRefusesToGrowAndNamesTheGroupThatStopsTheSplit()
        {
            var containers = Row(3);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w2"));
            var utilization = new Dictionary<string, float> { { "c0", 0.5f }, { "c1", 0.45f }, { "c2", 0.05f } };
            var policy = new CostBalancedAssignmentPolicy();
            var input = Input(containers, workers, leases, Group(Fleet, "c0", "c1"), utilization: utilization);
            var settings = ScaleSettings.Default;
            settings.HoldSeconds = 0f;
            var scaler = new WorkerScaler();

            var decision = scaler.Evaluate(
                0.0,
                new Dictionary<string, float> { { "w1", 0.95f }, { "w2", 0.05f } },
                input, policy, 2, settings, false);

            Assert.AreEqual(ScaleAction.None, decision.Action, "another worker would not help");
            Assert.AreEqual("c0", decision.BlockedBy);
            Assert.AreEqual(SaturationCause.CohesionGroup, decision.BlockedCause);
            StringAssert.Contains("cohesion 7 spans c0, c1", decision.BlockedReason);
            StringAssert.Contains("cohesion 7 spans c0, c1", decision.Reason);
            StringAssert.Contains("cannot be split", decision.Reason);
        }

        [Test]
        public void APolicyThatDoesNotExplainItselfLeavesTheScalersSentenceExactlyAsItWas()
        {
            var containers = Row(2);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w2"));
            var utilization = new Dictionary<string, float> { { "c0", 0.9f }, { "c1", 0.05f } };
            var input = Input(containers, workers, leases, utilization: utilization);
            var settings = ScaleSettings.Default;
            settings.HoldSeconds = 0f;

            var decision = new WorkerScaler().Evaluate(
                0.0,
                new Dictionary<string, float> { { "w1", 0.9f }, { "w2", 0.05f } },
                input, new BakedAssignmentPolicy(), 2, settings, false);

            Assert.AreEqual(SaturationCause.None, decision.BlockedCause);
            Assert.AreEqual("", decision.BlockedReason);
        }
    }
}
