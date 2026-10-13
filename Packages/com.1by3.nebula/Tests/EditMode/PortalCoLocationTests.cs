using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 68, the worker half (<c>docs/portals.md</c> D34, D36): the cohesion block of a worker's telemetry reports every link that is not
    /// closed as a binding of the near face's container with the containers behind it, from the moment the link prepares to the moment it closes, and nothing for
    /// a door nobody opened; the rows reach the planner through the real telemetry document and the orchestrator's reader, so a chain of three linked scopes
    /// is one item. Tier B on <see cref="PortalHarness"/>; the planner's side on its own is <c>ConformancePortalCohesionTests</c> (tier A).
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalCoLocationTests
    {
        private PortalHarness _h;

        [SetUp] public void SetUp() => _h = new PortalHarness(2);
        [TearDown] public void TearDown() => _h.Dispose();

        private static List<NebulaWorker.PortalBinding> Bindings(ConformanceMesh.Worker worker)
        {
            var rows = new List<NebulaWorker.PortalBinding>();
            worker.Instance.CopyPortalBindings(rows);
            return rows;
        }

        // ------------------------------------------------------------------------------------------ what a worker reports

        [Test]
        public void ALinkBindsTheDoorsContainerWithTheRoomBehindItFromPreparingToClosed()
        {
            Assert.That(Bindings(_h.W), Is.Empty, "a door nobody opened binds nothing");

            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Ticks(3);
            var rows = Bindings(_h.W);
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(rows[0].LinkId, Is.EqualTo(link.Id));
            Assert.That(rows[0].Portal, Is.EqualTo("door"));
            Assert.That(rows[0].State, Is.EqualTo(PortalState.Preparing), "a link that is only getting ready already binds: the room is dealt with the door while it prepares");
            Assert.That(rows[0].Wait, Is.EqualTo(PortalWait.ClientWarming));
            Assert.That(rows[0].Containers, Is.EqualTo(new[] { "street", PortalHarness.HallId }));
            Assert.That(rows[0].Split, Is.False);

            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
            rows = Bindings(_h.W);
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(rows[0].State, Is.EqualTo(PortalState.Open));
            Assert.That(rows[0].Wait, Is.EqualTo(PortalWait.None));
            Assert.That(NebulaDiagnostics.BoundPortalLinks, Is.EqualTo(1));

            _h.Door.Close(PortalHarness.Owner);
            _h.Ticks(2);
            Assert.That(link.State, Is.EqualTo(PortalState.Closed));
            Assert.That(Bindings(_h.W), Is.Empty, "the binding goes when the link closes");
            Assert.That(NebulaDiagnostics.BoundPortalLinks, Is.EqualTo(0));
        }

        [Test]
        public void OnlyTheDoorsInUseAreBoundInATownWithManyOfThem()
        {
            var doors = new List<Portal> { _h.Door };
            for (int i = 0; i < 39; i++) doors.Add(_h.NewPortal("hangar-" + i, new Vector3(60f + i * 4f, 0f, 20f)));
            Assert.That(Portals.All.Count, Is.GreaterThanOrEqualTo(40));

            _h.Ticks(2);
            Assert.That(Bindings(_h.W), Is.Empty, "forty doors, none open: nothing is bound");

            doors[7].Open("crew-7", new ulong[0]);
            _h.Ticks(4);
            var rows = Bindings(_h.W);
            Assert.That(rows.Count, Is.EqualTo(1), "one door in use binds one link");
            Assert.That(rows[0].Portal, Is.EqualTo("hangar-6"));
            Assert.That(rows[0].Containers.Count, Is.EqualTo(2));

            _h.Door.Open(PortalHarness.Owner, new ulong[0]);
            _h.Ticks(4);
            Assert.That(Bindings(_h.W).Count, Is.EqualTo(2));
            doors[7].Close("crew-7");
            _h.Ticks(3);
            Assert.That(Bindings(_h.W).Select(r => r.Portal), Is.EqualTo(new[] { "door" }), "and closing one takes only its row");
        }

        [Test]
        public void ALinkIntoAnotherWorkersRoomSaysItIsSplitAndWhatItWaitsFor()
        {
            _h.HallOnSecondWorker = true;
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Tick();
            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarWorkerHandshake));
            var rows = Bindings(_h.W);
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(rows[0].Split, Is.True, "the hall is the other worker's");
            Assert.That(rows[0].Wait, Is.EqualTo(PortalWait.FarWorkerHandshake));
            Assert.That(rows[0].Containers, Is.EqualTo(new[] { "street", PortalHarness.HallId }), "still bound: the planner is what brings them together");
        }

        [Test]
        public void TheWorkersTelemetryDocumentCarriesTheBindingToTheOrchestratorsReader()
        {
            var link = _h.OpenDoor();
            var telemetry = WorkerTelemetry.ForTests();
            telemetry.Update(_h.W.Instance);
            string document = telemetry.Write(_h.W.Id, _h.W.Index, 1, _h.W.Instance.Entities, false, null);
            StringAssert.Contains("\"portalLinks\":[{\"id\":\"" + link.Id.ToString("x") + "\"", document);

            var mesh = new MeshTelemetry(() => 0.0);
            Assert.IsNull(mesh.Accept(document, out _));
            var groups = new List<CohesionGroupInfo>();
            mesh.CopyCohesion(groups);
            Assert.That(groups.Count, Is.EqualTo(1));
            Assert.That(groups[0].Label, Is.EqualTo("portal door " + link.Id.ToString("x")));
            Assert.That(groups[0].Containers, Is.EqualTo(new[] { "street", PortalHarness.HallId }));
            Assert.That(groups[0].Detail, Is.EqualTo("Open"));
            Assert.That(groups[0].IsSplitLink, Is.False);
        }

        // ------------------------------------------------------------------------------------------ a chain through the planner

        private static LeaseInfo Lease(string container, string worker) =>
            new LeaseInfo { ContainerId = container, WorkerId = worker, State = LeaseState.Active, Epoch = 1 };

        [Test]
        public void AChainOfThreeLinkedScopesStaysOnOneWorkerWhenTheMeshRebalances()
        {
            // street -> hall (the harness's door) -> vault (a door fixed in the hall).
            _h.OpenDoor();
            var vault = _h.MakeTemplate("annex", ("room", new Vector3(0f, 2f, 0f), new Vector3(6f, 4f, 6f)));
            var inner = _h.NewPortal("inner-door", new Vector3(20f, 0f, 23f), _h.Hall.transform, vault);
            var innerLink = inner.Open("crew-2", new[] { PortalHarness.Client });
            _h.Ticks(4);
            _h.Warm(innerLink);
            _h.Tick();
            Assert.That(innerLink.State, Is.EqualTo(PortalState.Open), innerLink.ToString());
            string vaultId = PortalHarness.RuntimePart("annex", "crew-2", "room").ContainerId;

            var telemetry = WorkerTelemetry.ForTests();
            telemetry.Update(_h.W.Instance);
            var mesh = new MeshTelemetry(() => 0.0);
            Assert.IsNull(mesh.Accept(telemetry.Write(_h.W.Id, _h.W.Index, 1, _h.W.Instance.Entities, false, null), out _));
            var groups = new List<CohesionGroupInfo>();
            mesh.CopyCohesion(groups);
            Assert.That(groups.Count, Is.EqualTo(2), "one row for each link in use");
            Assert.That(groups.Select(g => g.Label.StartsWith("portal ")), Is.All.True);

            // The three are dealt to two workers by the cost policy: the hall and the vault are the second worker's, the street the first's.
            var street = ContainerRegistry.FindById("street");
            var runtime = new List<Container> { _h.Hall, PortalHarness.RuntimePart("annex", "crew-2", "room") };
            var workers = new List<WorkerInfo>
            {
                new WorkerInfo { WorkerId = _h.W.Id, WorkerIndex = _h.W.Index, Status = WorkerStatus.Ready },
                new WorkerInfo { WorkerId = _h.Other.Id, WorkerIndex = _h.Other.Index, Status = WorkerStatus.Ready },
            };
            var leases = new List<LeaseInfo> { Lease("street", _h.W.Id), Lease(PortalHarness.HallId, _h.Other.Id), Lease(vaultId, _h.Other.Id) };
            var occupancy = new Dictionary<string, ContainerLoad>
            {
                { "street", new ContainerLoad { Players = 1 } }, { PortalHarness.HallId, new ContainerLoad { Players = 1 } }, { vaultId, new ContainerLoad { Players = 1 } },
            };
            AssignmentInput Input(IReadOnlyList<CohesionGroupInfo> cohesion) => new AssignmentInput
            {
                Baked = new List<Container> { street }, Runtime = runtime, Eligible = workers, Leases = leases, Occupancy = occupancy,
                Utilization = new Dictionary<string, float>(), Hints = new Dictionary<string, ContainerHint>(), Cohesion = cohesion, Holds = new Dictionary<string, float>(),
            };
            Dictionary<string, string> Owners(List<KeyValuePair<string, string>> changes)
            {
                var map = leases.ToDictionary(l => l.ContainerId, l => l.WorkerId);
                foreach (var change in changes) map[change.Key] = change.Value;
                return map;
            }
            var policy = new CostBalancedAssignmentPolicy();

            var without = Owners(policy.Compute(Input(new List<CohesionGroupInfo>())));
            Assert.That(without.Values.Distinct().Count(), Is.EqualTo(2), "three equal containers on two workers are dealt apart without the links");

            var bound = Owners(policy.Compute(Input(groups)));
            Assert.That(bound["street"], Is.EqualTo(bound[PortalHarness.HallId]), "the front door binds the street and the hall");
            Assert.That(bound[PortalHarness.HallId], Is.EqualTo(bound[vaultId]), "the inner door binds the hall and the vault: the chain is one item");
        }
    }
}
