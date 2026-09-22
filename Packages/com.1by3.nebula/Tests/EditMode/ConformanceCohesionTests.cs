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
    /// Conformance scenario 7 (<c>docs/conformance-suite.md</c>), the planner half: the cost policy deals a cohesion
    /// group's containers as one item and never splits them, a held container is not moved until its hold expires,
    /// and a group that does not fit one worker is reported instead of being split
    /// (<c>docs/cohesion-hints.md</c>). Tier A: the production <see cref="CostBalancedAssignmentPolicy"/> and
    /// <see cref="MeshTelemetry"/> are pure C#, so the same file runs in the Unity Editor and under
    /// <c>dotnet test</c>; nothing here needs a worker, a mesh or a socket. The handover half of the scenario needs
    /// real workers and lives in <c>ConformanceCohesionHandoverTests.cs</c> (tier B).
    /// <para>
    /// Containers are built for the policy directly rather than through the registry: the policy reads the lists it
    /// is handed, and the two builds construct a <see cref="Container"/> differently (a component in Unity, a plain
    /// manifest row in the services).
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public class ConformanceCohesionTests
    {
        private const float Span = 64f;
        private const uint Group = 7;

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

        /// <summary>A row of equal boxes along x, so the Morton order the policy sorts by is c0, c1, c2...</summary>
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

        /// <summary>One player in every container, so every container costs the same.</summary>
        private static Dictionary<string, ContainerLoad> EvenLoad(IEnumerable<Container> containers) =>
            containers.ToDictionary(c => c.ContainerId, c => new ContainerLoad { Players = 1 });

        private static CohesionGroupInfo Cohesion(uint group, int members, params string[] containers)
        {
            var info = new CohesionGroupInfo { Group = group, Members = members };
            info.Containers.AddRange(containers);
            info.Workers.Add("w1");
            return info;
        }

        private AssignmentInput Input(
            List<Container> containers,
            List<WorkerInfo> workers,
            List<LeaseInfo> leases,
            IReadOnlyList<CohesionGroupInfo> cohesion = null,
            Dictionary<string, float> holds = null,
            Dictionary<string, float> utilization = null) => new AssignmentInput
            {
                Baked = containers,
                Runtime = new List<Container>(),
                Eligible = workers,
                Leases = leases,
                Occupancy = EvenLoad(containers),
                Utilization = utilization ?? new Dictionary<string, float>(),
                Hints = new Dictionary<string, ContainerHint>(),
                Cohesion = cohesion ?? new List<CohesionGroupInfo>(),
                Holds = holds ?? new Dictionary<string, float>(),
            };

        /// <summary>The owner of every container after the changes are applied, as the control plane would.</summary>
        private static Dictionary<string, string> Apply(List<LeaseInfo> leases, List<KeyValuePair<string, string>> changes)
        {
            var map = leases.ToDictionary(l => l.ContainerId, l => l.State == LeaseState.Active ? l.WorkerId : "");
            foreach (var change in changes) map[change.Key] = change.Value;
            return map;
        }

        // ---- the planner never splits a group -------------------------------------------------------------

        [Test]
        public void AGroupSpanningTwoContainersIsDealtToOneWorker()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            // The group's two containers sit at opposite ends of the curve and on different workers right now:
            // without the hint the planner would happily leave them there.
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w2"), ("c3", "w2"));
            var policy = new CostBalancedAssignmentPolicy();

            var split = Apply(leases, policy.Compute(Input(containers, workers, leases)));
            Assert.AreNotEqual(split["c0"], split["c3"], "without a group the two ends of the curve stay apart");

            var input = Input(containers, workers, leases, new List<CohesionGroupInfo> { Cohesion(Group, 2, "c0", "c3") });
            var owners = Apply(leases, policy.Compute(input));

            Assert.AreEqual(owners["c0"], owners["c3"], "the group's containers are dealt as one item");
            Assert.AreEqual(4, owners.Count);
            CollectionAssert.IsSubsetOf(new[] { owners["c0"] }, new[] { "w1", "w2" });
            Assert.IsEmpty(policy.Unsplittable, "the group fits: nothing to report");
        }

        [Test]
        public void AGroupIsKeptWholeThroughAReDealOfTheWholeCurve()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w1"), ("c3", "w1")); // one worker holds everything
            var policy = new CostBalancedAssignmentPolicy();

            // c1 and c2 are in one group, so the cut that would run between them has to move instead.
            var input = Input(containers, workers, leases, new List<CohesionGroupInfo> { Cohesion(Group, 3, "c1", "c2") });
            var changes = policy.Compute(input);
            var owners = Apply(leases, changes);

            Assert.AreEqual(owners["c1"], owners["c2"], "the cut never falls inside the group");
            Assert.AreEqual(2, owners.Values.Distinct().Count(), "the curve is still re-dealt across both workers");
        }

        [Test]
        public void AGroupThatDoesNotFitOneWorkerIsReportedAndStillNotSplit()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w2"), ("c3", "w2"));
            // Measured utilization: the two group members alone want more than a whole tick budget.
            var utilization = new Dictionary<string, float> { { "c0", 0.7f }, { "c1", 0.1f }, { "c2", 0.1f }, { "c3", 0.8f } };
            var policy = new CostBalancedAssignmentPolicy();
            var input = Input(containers, workers, leases, new List<CohesionGroupInfo> { Cohesion(Group, 2, "c0", "c3") }, utilization: utilization);

            var owners = Apply(leases, policy.Compute(input));

            Assert.AreEqual(owners["c0"], owners["c3"], "an oversize group is still never split");
            Assert.AreEqual(1, policy.Unsplittable.Count);
            var reported = policy.Unsplittable[0];
            Assert.AreEqual("cohesion 7", reported.Group);
            CollectionAssert.AreEquivalent(new[] { "c0", "c3" }, reported.Containers);
            Assert.AreEqual(1.5f, reported.Utilization, 1e-4f);
            Assert.AreEqual(policy.MaxGroupUtilization, reported.Limit);
            StringAssert.Contains("cohesion 7", policy.Note);
            StringAssert.Contains("kept whole", policy.Note);
        }

        [Test]
        public void AGroupInsideOneContainerBindsNothingAndIsNeverReported()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w2"), ("c3", "w2"));
            var utilization = new Dictionary<string, float> { { "c0", 0.9f }, { "c1", 0.9f }, { "c2", 0.1f }, { "c3", 0.1f } };
            var policy = new CostBalancedAssignmentPolicy();
            var input = Input(containers, workers, leases, new List<CohesionGroupInfo> { Cohesion(Group, 5, "c0") }, utilization: utilization);

            policy.Compute(input);

            Assert.IsEmpty(policy.Unsplittable, "a group inside one container is already on one worker, whatever it costs");
        }

        // ---- holds ----------------------------------------------------------------------------------------

        [Test]
        public void AHoldDefersTheMoveUntilItExpires()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w1"), ("c3", "w1"));
            var policy = new CostBalancedAssignmentPolicy();

            var moved = policy.Compute(Input(containers, workers, leases)).Select(c => c.Key).ToList();
            CollectionAssert.Contains(moved, "c2", "without a hold the tail of the curve goes to the empty worker");
            CollectionAssert.Contains(moved, "c3");

            var holds = new Dictionary<string, float> { { "c2", 4.5f } };
            var held = policy.Compute(Input(containers, workers, leases, holds: holds));
            CollectionAssert.DoesNotContain(held.Select(c => c.Key).ToList(), "c2", "a held container is not moved");
            CollectionAssert.Contains(held.Select(c => c.Key).ToList(), "c3", "its neighbours still move");

            // The hold expired: the orchestrator's snapshot no longer carries it and the move happens.
            var after = policy.Compute(Input(containers, workers, leases)).Select(c => c.Key).ToList();
            CollectionAssert.Contains(after, "c2");
        }

        [Test]
        public void AHoldOnOneMemberHoldsTheWholeGroup()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w1"), ("c3", "w1"));
            var policy = new CostBalancedAssignmentPolicy();
            var input = Input(containers, workers, leases,
                new List<CohesionGroupInfo> { Cohesion(Group, 2, "c2", "c3") },
                holds: new Dictionary<string, float> { { "c2", 3f } });

            var changes = policy.Compute(input);

            CollectionAssert.DoesNotContain(changes.Select(c => c.Key).ToList(), "c2");
            CollectionAssert.DoesNotContain(changes.Select(c => c.Key).ToList(), "c3",
                "honouring the hold on one member while the other moved would be the split the group forbids");
        }

        [Test]
        public void AHoldNeverStrandsAnOrphan()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", ""), ("c1", "w1"), ("c2", "w2"), ("c3", "w2"));
            var policy = new CostBalancedAssignmentPolicy();
            var input = Input(containers, workers, leases, holds: new Dictionary<string, float> { { "c0", 10f } });

            var owners = Apply(leases, policy.Compute(input));

            Assert.AreNotEqual("", owners["c0"], "a hold defers a move; it cannot leave a container unowned");
        }

        [Test]
        public void DropHeldChangesIsTheBackstopForAPolicyOfTheGamesOwn()
        {
            var containers = Row(2);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", ""));
            var input = Input(containers, workers, leases, holds: new Dictionary<string, float> { { "c0", 2f }, { "c1", 2f } });

            var changes = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("c0", "w2"),
                new KeyValuePair<string, string>("c1", "w2"),
            };
            int dropped = input.DropHeldChanges(changes);

            Assert.AreEqual(1, dropped);
            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual("c1", changes[0].Key, "the orphan is still placed; only the owned held container stays put");
        }

        // ---- what the workers report ----------------------------------------------------------------------

        private static string Document(string worker, string holds, string cohesion) =>
            "{\"worker\":\"" + worker + "\",\"index\":1,\"tick\":10,\"detail\":false," +
            "\"holds\":[" + holds + "],\"cohesion\":[" + cohesion + "]," +
            "\"containers\":[{\"id\":\"c0\",\"players\":1,\"bots\":0,\"serverDriven\":0,\"other\":0,\"ghosts\":0}]}";

        [Test]
        public void AHoldReportedInSecondsBecomesADeadlineOnTheOrchestratorClock()
        {
            double now = 100.0;
            var telemetry = new MeshTelemetry(() => now);
            Assert.IsNull(telemetry.Accept(Document("w1", "{\"id\":\"c2\",\"seconds\":5.0}", ""), out _));

            var holds = new Dictionary<string, float>();
            telemetry.CopyHolds(holds);
            Assert.AreEqual(1, holds.Count);
            Assert.AreEqual(5f, holds["c2"], 1e-3f);
            Assert.AreEqual("w1", telemetry.HolderOf("c2"));

            now += 3.0;
            telemetry.CopyHolds(holds);
            Assert.AreEqual(2f, holds["c2"], 1e-3f, "the deadline is the orchestrator's; the worker reports what is left");

            now += 3.0;
            telemetry.CopyHolds(holds);
            Assert.IsEmpty(holds, "the hold expires on the orchestrator's clock without another document");
            Assert.AreEqual("", telemetry.HolderOf("c2"));
        }

        [Test]
        public void ASecondDocumentReplacesTheHoldsOfThatWorkerOnly()
        {
            double now = 0.0;
            var telemetry = new MeshTelemetry(() => now);
            telemetry.Accept(Document("w1", "{\"id\":\"c0\",\"seconds\":9.0}", ""), out _);
            telemetry.Accept(Document("w2", "{\"id\":\"c1\",\"seconds\":9.0}", ""), out _);

            var holds = new Dictionary<string, float>();
            telemetry.CopyHolds(holds);
            CollectionAssert.AreEquivalent(new[] { "c0", "c1" }, holds.Keys);

            // w1 released its hold: its next document simply does not carry it.
            telemetry.Accept(Document("w1", "", ""), out _);
            telemetry.CopyHolds(holds);
            CollectionAssert.AreEquivalent(new[] { "c1" }, holds.Keys);

            telemetry.Forget("w2");
            telemetry.CopyHolds(holds);
            Assert.IsEmpty(holds, "a worker that is gone holds nothing");
        }

        [Test]
        public void CohesionSpansFromTwoWorkersMergeIntoOneGroup()
        {
            double now = 0.0;
            var telemetry = new MeshTelemetry(() => now);
            telemetry.Accept(Document("w1", "", "{\"group\":7,\"members\":2,\"containers\":[\"c0\",\"c1\"]}"), out _);
            telemetry.Accept(Document("w2", "", "{\"group\":7,\"members\":1,\"containers\":[\"c1\",\"c3\"]},{\"group\":9,\"members\":4,\"containers\":[]}"), out _);

            var groups = new List<CohesionGroupInfo>();
            telemetry.CopyCohesion(groups);

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual(7u, groups[0].Group);
            Assert.AreEqual(3, groups[0].Members);
            CollectionAssert.AreEquivalent(new[] { "c0", "c1", "c3" }, groups[0].Containers);
            CollectionAssert.AreEquivalent(new[] { "w1", "w2" }, groups[0].Workers);
            Assert.AreEqual(9u, groups[1].Group);
            Assert.IsEmpty(groups[1].Containers, "members in no container name none");
            Assert.AreEqual("cohesion 7", groups[0].ToString());

            // A worker that stops reporting takes its half of the group with it when its rows expire.
            now += MeshTelemetry.ExpireSeconds + 1.0;
            telemetry.Accept(Document("w1", "", "{\"group\":7,\"members\":2,\"containers\":[\"c0\",\"c1\"]}"), out _);
            telemetry.CopyCohesion(groups);
            Assert.AreEqual(1, groups.Count);
            CollectionAssert.AreEquivalent(new[] { "c0", "c1" }, groups[0].Containers);
        }

        [Test]
        public void ADocumentWithoutCohesionHintsChangesNothing()
        {
            var telemetry = new MeshTelemetry(() => 0.0);
            telemetry.Accept("{\"worker\":\"w1\",\"containers\":[]}", out _);

            var holds = new Dictionary<string, float>();
            var groups = new List<CohesionGroupInfo>();
            telemetry.CopyHolds(holds);
            telemetry.CopyCohesion(groups);

            Assert.IsEmpty(holds);
            Assert.IsEmpty(groups);
        }
    }
}
