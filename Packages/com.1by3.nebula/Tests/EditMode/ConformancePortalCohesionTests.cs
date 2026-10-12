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
    /// Conformance scenario 68, the planner half (<c>docs/portals.md</c> D34, D36; <c>docs/cohesion-hints.md</c> D7): a portal link that is not closed
    /// binds the container its near face is fixed in with the containers behind it, so the cost policy deals them as one item and never to two workers;
    /// the rows a worker reports reach the planner through the orchestrator's telemetry reader, and a link that closed binds nothing. Tier A: the
    /// production <see cref="CostBalancedAssignmentPolicy"/> and <see cref="MeshTelemetry"/> are pure C#, so the same file runs in the Unity Editor and under
    /// <c>dotnet test</c>. The worker half (what a real worker writes) is <c>PortalCoLocationTests</c>, tier B.
    /// </summary>
    [Category("Conformance")]
    public class ConformancePortalCohesionTests
    {
        private const float Span = 64f;

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
            rows.Select(r => new LeaseInfo { ContainerId = r.container, WorkerId = r.worker, State = r.worker == "" ? LeaseState.Orphaned : LeaseState.Active, Epoch = 1 }).ToList();

        /// <summary>A portal link as the orchestrator's reader hands it to the planner.</summary>
        private static CohesionGroupInfo Link(string portal, string id, params string[] containers)
        {
            var info = new CohesionGroupInfo { Label = "portal " + portal + " " + id, Detail = "Open" };
            info.Containers.AddRange(containers);
            info.Workers.Add("w1");
            return info;
        }

        private static AssignmentInput Input(List<Container> containers, List<WorkerInfo> workers, List<LeaseInfo> leases,
            IReadOnlyList<CohesionGroupInfo> cohesion = null, Dictionary<string, float> holds = null, Dictionary<string, float> utilization = null) => new AssignmentInput
            {
                Baked = containers,
                Runtime = new List<Container>(),
                Eligible = workers,
                Leases = leases,
                Occupancy = containers.ToDictionary(c => c.ContainerId, c => new ContainerLoad { Players = 1 }),
                Utilization = utilization ?? new Dictionary<string, float>(),
                Hints = new Dictionary<string, ContainerHint>(),
                Cohesion = cohesion ?? new List<CohesionGroupInfo>(),
                Holds = holds ?? new Dictionary<string, float>(),
            };

        private static Dictionary<string, string> Apply(List<LeaseInfo> leases, List<KeyValuePair<string, string>> changes)
        {
            var map = leases.ToDictionary(l => l.ContainerId, l => l.State == LeaseState.Active ? l.WorkerId : "");
            foreach (var change in changes) map[change.Key] = change.Value;
            return map;
        }

        // ---- the planner deals a link's containers as one item ---------------------------------------------

        [Test]
        public void ALinkThatIsNotClosedKeepsTheDoorAndTheRoomBehindItOnOneWorker()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            // The door's container and the room sit at opposite ends of the curve, on different workers: left alone, the planner leaves them there.
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w2"), ("c3", "w2"));
            var policy = new CostBalancedAssignmentPolicy();

            var apart = Apply(leases, policy.Compute(Input(containers, workers, leases)));
            Assert.AreNotEqual(apart["c0"], apart["c3"], "without a link the two ends of the curve stay apart");

            var bound = Apply(leases, policy.Compute(Input(containers, workers, leases, new List<CohesionGroupInfo> { Link("door", "1f", "c0", "c3") })));
            Assert.AreEqual(bound["c0"], bound["c3"], "an open, preparing or closing link deals its containers as one item");
            Assert.IsEmpty(policy.Unsplittable);
        }

        [Test]
        public void AChainOfThreeLinkedScopesStaysOnOneWorkerThroughAReDeal()
        {
            var containers = Row(6);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w1"), ("c3", "w2"), ("c4", "w2"), ("c5", "w2"));
            var policy = new CostBalancedAssignmentPolicy();
            // street (c0) -> hall (c2) -> vault (c5): two links that share the hall, so the union-find joins all three.
            var links = new List<CohesionGroupInfo> { Link("front-door", "1f", "c0", "c2"), Link("inner-door", "2e", "c2", "c5") };

            var owners = Apply(leases, policy.Compute(Input(containers, workers, leases, links)));

            Assert.AreEqual(owners["c0"], owners["c2"], "the first link binds the street and the hall");
            Assert.AreEqual(owners["c2"], owners["c5"], "the second binds the hall and the vault, so the chain is one item");
            Assert.AreEqual(2, owners.Values.Distinct().Count(), "the curve is still re-dealt across both workers");
        }

        [Test]
        public void ALinkThatClosedBindsNothingSoOnlyTheDoorsInUseAreBound()
        {
            var containers = Row(6);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w1"), ("c3", "w2"), ("c4", "w2"), ("c5", "w2"));
            var policy = new CostBalancedAssignmentPolicy();
            // Forty hangar doors in a town report nothing; the one in use reports its row. The others bind nothing at all.
            var inUse = new List<CohesionGroupInfo> { Link("hangar-7", "77", "c1", "c4") };
            var owners = Apply(leases, policy.Compute(Input(containers, workers, leases, inUse)));
            Assert.AreEqual(owners["c1"], owners["c4"]);
            Assert.AreEqual(2, owners.Values.Distinct().Count(), "one link does not pin the rest of the town");

            var closed = Apply(leases, policy.Compute(Input(containers, workers, leases)));
            Assert.AreNotEqual(closed["c1"], closed["c4"], "the binding goes with the link");
        }

        [Test]
        public void ALinkIsReportedByItsLabelWhenItDoesNotFitOneWorker()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w2"), ("c3", "w2"));
            var utilization = new Dictionary<string, float> { { "c0", 0.7f }, { "c1", 0.1f }, { "c2", 0.1f }, { "c3", 0.8f } };
            var policy = new CostBalancedAssignmentPolicy();

            var owners = Apply(leases, policy.Compute(Input(containers, workers, leases, new List<CohesionGroupInfo> { Link("door", "1f", "c0", "c3") }, utilization: utilization)));

            Assert.AreEqual(owners["c0"], owners["c3"], "an oversize link is still never split");
            Assert.AreEqual(1, policy.Unsplittable.Count);
            Assert.AreEqual("portal door 1f", policy.Unsplittable[0].Group, "named by the link, not as a cohesion group");
            StringAssert.Contains("portal door 1f", policy.Note);
        }

        [Test]
        public void AHoldOnTheRoomHoldsTheDoorsContainerToo()
        {
            var containers = Row(4);
            var workers = Workers(1, 2);
            var leases = Leases(("c0", "w1"), ("c1", "w1"), ("c2", "w1"), ("c3", "w1"));
            var policy = new CostBalancedAssignmentPolicy();
            var changes = policy.Compute(Input(containers, workers, leases,
                new List<CohesionGroupInfo> { Link("door", "1f", "c2", "c3") },
                holds: new Dictionary<string, float> { { "c2", 3f } }));

            var moved = changes.Select(c => c.Key).ToList();
            CollectionAssert.DoesNotContain(moved, "c2");
            CollectionAssert.DoesNotContain(moved, "c3", "the hold reaches the whole item, links included (docs/cohesion-hints.md D8)");
        }

        // ---- what the workers report -----------------------------------------------------------------------

        private static string Document(string worker, string portalLinks) =>
            "{\"worker\":\"" + worker + "\",\"index\":1,\"tick\":10,\"detail\":false," +
            "\"holds\":[],\"cohesion\":[],\"portalLinks\":[" + portalLinks + "]," +
            "\"containers\":[{\"id\":\"c0\",\"players\":1,\"bots\":0,\"serverDriven\":0,\"other\":0,\"ghosts\":0}]}";

        [Test]
        public void ThePortalLinksADocumentCarriesAreParsedWithTheirStateAndWait()
        {
            var spans = new List<MeshTelemetry.PortalLinkSpan>();
            MeshTelemetry.ParsePortalLinks(Document("w1",
                "{\"id\":\"1f\",\"portal\":\"door\",\"state\":\"Preparing\",\"wait\":\"FarWorkerHandshake\",\"split\":1,\"in\":[\"c0\",\"c3\"]}," +
                "{\"id\":\"2e\",\"portal\":\"hatch\",\"state\":\"Open\",\"wait\":\"None\",\"split\":0,\"in\":[\"c3\"]}"), spans);

            Assert.AreEqual(2, spans.Count);
            Assert.AreEqual("1f", spans[0].LinkId);
            Assert.AreEqual("door", spans[0].Portal);
            Assert.AreEqual("Preparing", spans[0].State);
            Assert.AreEqual("FarWorkerHandshake", spans[0].Wait);
            Assert.IsTrue(spans[0].Split);
            CollectionAssert.AreEqual(new[] { "c0", "c3" }, spans[0].Containers);
            Assert.IsFalse(spans[1].Split);
        }

        [Test]
        public void TheOrchestratorHandsLinksToThePlannerAndDropsThemWhenTheWorkerStopsSayingThem()
        {
            double now = 100.0;
            var telemetry = new MeshTelemetry(() => now);
            Assert.IsNull(telemetry.Accept(Document("w1", "{\"id\":\"1f\",\"portal\":\"door\",\"state\":\"Open\",\"wait\":\"None\",\"split\":0,\"in\":[\"c0\",\"c3\"]}"), out _));

            var groups = new List<CohesionGroupInfo>();
            telemetry.CopyCohesion(groups);
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual("portal door 1f", groups[0].Label);
            Assert.AreEqual("portal door 1f", groups[0].ToString());
            Assert.AreEqual("Open", groups[0].Detail);
            CollectionAssert.AreEqual(new[] { "c0", "c3" }, groups[0].Containers);
            CollectionAssert.AreEqual(new[] { "w1" }, groups[0].Workers);

            now += MeshTelemetry.ExpireSeconds + 1.0;
            telemetry.CopyCohesion(groups);
            Assert.IsEmpty(groups, "a worker that stopped reporting binds nothing");

            now = 200.0;
            telemetry.Accept(Document("w1", "{\"id\":\"1f\",\"portal\":\"door\",\"state\":\"Open\",\"wait\":\"None\",\"split\":0,\"in\":[\"c0\",\"c3\"]}"), out _);
            telemetry.Accept(Document("w1", ""), out _);
            telemetry.CopyCohesion(groups);
            Assert.IsEmpty(groups, "the next document replaces the last: a link that closed is no longer in it");
        }

        [Test]
        public void AWaitIsShownBesideTheStateAndASplitLinkIsFlagged()
        {
            var telemetry = new MeshTelemetry(() => 0.0);
            telemetry.Accept(Document("w1", "{\"id\":\"1f\",\"portal\":\"door\",\"state\":\"Preparing\",\"wait\":\"FarWorkerHandshake\",\"split\":1,\"in\":[\"c0\",\"c3\"]}"), out _);
            var groups = new List<CohesionGroupInfo>();
            telemetry.CopyCohesion(groups);
            Assert.AreEqual("Preparing, waiting: FarWorkerHandshake", groups[0].Detail);
            Assert.IsTrue(groups[0].IsSplitLink);
        }
    }
}
