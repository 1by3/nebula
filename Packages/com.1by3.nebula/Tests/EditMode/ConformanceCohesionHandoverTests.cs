using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 7 (<c>docs/conformance-suite.md</c>), the handover half: an entity cohesion group is
    /// handed over as a unit, and a member that cannot come along is reported rather than silently left behind
    /// (<c>docs/cohesion-hints.md</c>, D4/D5). Tier B: two or three real <see cref="NebulaWorker"/> components on the
    /// <see cref="ConformanceMesh"/>, so the sender's own <c>TransferAuthority</c> builds the messages, the bytes it
    /// hands its transport are parsed by the receiver's own <c>Dispatch</c>, and the group travels in the spawn data
    /// exactly as it does in a live mesh. The planner half is tier A, in <c>ConformanceCohesionTests.cs</c>.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceCohesionHandoverTests
    {
        private const uint Group = 5;

        private ConformanceMesh _mesh;
        private Container _north, _south;
        private ushort _prefabId, _carrierPrefabId;
        private int _splitsBefore;

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(3);
            // Two containers far enough apart that nothing but the cohesion group binds them.
            _north = _mesh.AddStaticContainer("cohesion-north", new Vector3(0f, 0f, 200f), new Vector3(200, 60, 200));
            _south = _mesh.AddStaticContainer("cohesion-south", new Vector3(0f, 0f, -200f), new Vector3(200, 60, 200));
            var prefab = new GameObject("cohesion-prefab");
            prefab.AddComponent<NetworkIdentity>();
            _prefabId = _mesh.RegisterPrefab(prefab);

            // A carrier: an entity with a box of its own, so a group member can have passengers.
            var carrier = new GameObject("cohesion-carrier");
            carrier.AddComponent<NetworkIdentity>();
            var box = carrier.AddComponent<Container>();
            box.ContainerId = "hold";
            box.Size = new Vector3(10, 6, 10);
            box.Center = new Vector3(0, 3, 0);
            carrier.AddComponent<DynamicContainer>();
            _carrierPrefabId = _mesh.RegisterPrefab(carrier);
            _splitsBefore = NebulaDiagnostics.SplitCohesionGroups;
        }

        [TearDown]
        public void TearDown() => _mesh.Dispose();

        private NetworkIdentity Spawn(ConformanceMesh.Worker owner, Container container, uint group)
        {
            var e = owner.SpawnServerDriven(_prefabId, container, container.transform.position, Quaternion.identity);
            if (group != 0) e.JoinCohesionGroup(group);
            return e;
        }

        private static List<ulong> TransferredTo(ConformanceMesh mesh, string worker) =>
            mesh.DeliveredOf(MsgId.AuthorityTransfer, worker).Select(m => m.Read(AuthorityTransferMsg.Read).Entity.NetId).ToList();

        // ---- the group moves as a unit --------------------------------------------------------------------

        [Test]
        public void AFailedGroupHandoffCannotMoveItsMembersWithTheNextUnrelatedHandoff()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var w3 = _mesh[2];
            var leader = Spawn(w1, _north, Group);
            var follower = Spawn(w1, _south, Group);
            var unrelated = Spawn(w1, _north, 0);
            System.Action<NetworkIdentity, string> thrower = (e, target) =>
                throw new System.InvalidOperationException("handoff callback failed");
            w1.Instance.AuthorityHandedOff += thrower;
            try { Assert.Throws<System.InvalidOperationException>(() => w1.Transfer(leader, w2)); }
            finally { w1.Instance.AuthorityHandedOff -= thrower; }
            _mesh.Pump();

            w1.Transfer(unrelated, w3);
            _mesh.Pump();

            Assert.IsTrue(follower.HasAuthority, "the failed group's queue must not join an unrelated transfer");
            CollectionAssert.AreEquivalent(new[] { unrelated.NetId }, TransferredTo(_mesh, w3.Id));

            var laterMember = Spawn(w1, _south, Group);
            w1.Transfer(follower, w2);
            _mesh.Pump();
            Assert.IsTrue(w2.Find(laterMember.NetId).HasAuthority, "the group must be expanded anew after a failed transfer");
            Assert.AreEqual(_splitsBefore, NebulaDiagnostics.SplitCohesionGroups);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ZeroCostWeightSurvivesHandoverToANewOrExistingCopy(bool existingCopy)
        {
            var source = _mesh[0];
            var target = _mesh[1];
            var entity = Spawn(source, _north, 0);
            if (existingCopy)
            {
                entity.SetCostWeight(7f);
                source.Transfer(entity, target);
                _mesh.Pump();
                source = _mesh[1];
                target = _mesh[0];
                entity = source.Find(entity.NetId);
                Assert.AreEqual(7f, target.Find(entity.NetId).EffectiveCostWeight);
            }
            entity.SetCostWeight(0f);

            source.Transfer(entity, target);
            _mesh.Pump();

            var received = target.Find(entity.NetId);
            Assert.IsTrue(received.HasAuthority);
            Assert.AreEqual(0f, received.EffectiveCostWeight, "zero is an explicit weight, even when an older copy had positive cost");
            received.RecomputeCostWeight();
            Assert.AreEqual(0f, received.EffectiveCostWeight, "the received weight remains pinned");
        }

        [TestCase(2)]
        [TestCase(6)]
        public void ASpawnWithoutTheCostFieldPreservesTheReceiversWeight(int omittedBytes)
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var entity = Spawn(w1, _north, 0);
            entity.SetCostWeight(7f);
            w1.Transfer(entity, w2);
            _mesh.Pump();
            var msg = EntitySpawnMsg.From(w2.Find(entity.NetId), new NetworkWriter());
            var writer = new NetworkWriter();
            msg.Write(writer, MsgId.GhostSpawn);
            var bytes = writer.ToArray();
            // Omit either the final weight or both added trailing fields, as a shorter spawn payload does.
            System.Array.Resize(ref bytes, bytes.Length - omittedBytes);
            var reader = new NetworkReader(bytes);
            reader.ReadByte();
            Assert.AreEqual(-1f, EntitySpawnMsg.Read(reader).CostWeight, "absence is distinct from an explicit zero");

            w1.Dispatch(w1.PeersById[w2.Id], bytes);

            Assert.AreEqual(7f, entity.EffectiveCostWeight, "a missing field has no opinion about the receiver's weight");
        }

        [Test]
        public void HandingOverOneMemberTakesTheWholeGroupToTheSameWorker()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var hull = Spawn(w1, _north, Group);
            var turret = Spawn(w1, _south, Group);   // a different container: nothing else would move it
            var bystander = Spawn(w1, _south, 0);

            w1.Transfer(hull, w2);
            _mesh.Pump();

            Assert.IsFalse(hull.HasAuthority, "the entity that was handed over is a ghost here now");
            Assert.IsFalse(turret.HasAuthority, "and so is the rest of its group");
            Assert.IsTrue(bystander.HasAuthority, "an entity outside the group does not move");

            var arrived = TransferredTo(_mesh, w2.Id);
            CollectionAssert.AreEquivalent(new[] { hull.NetId, turret.NetId }, arrived, "one transfer per member, and no more");
            Assert.IsTrue(w2.Find(hull.NetId).HasAuthority);
            Assert.IsTrue(w2.Find(turret.NetId).HasAuthority);
            Assert.AreEqual(Group, w2.Find(turret.NetId).CohesionGroup, "the group travels in the spawn data");
            Assert.AreEqual(_south, w2.Find(turret.NetId).Container, "each member keeps its own container");
            Assert.AreEqual(_splitsBefore, NebulaDiagnostics.SplitCohesionGroups, "nothing was split");
        }

        [Test]
        public void HandingOverTheOtherMemberIsTheSameHandover()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var hull = Spawn(w1, _north, Group);
            var turret = Spawn(w1, _south, Group);

            w1.Transfer(turret, w2);   // the member that is not the "first" one
            _mesh.Pump();

            CollectionAssert.AreEquivalent(new[] { hull.NetId, turret.NetId }, TransferredTo(_mesh, w2.Id));
            Assert.IsTrue(w2.Find(hull.NetId).HasAuthority);
            Assert.IsTrue(w2.Find(turret.NetId).HasAuthority);
        }

        [Test]
        public void AGroupOfThreeMovesInOneHandoffAndBackAgain()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var a = Spawn(w1, _north, Group);
            var b = Spawn(w1, _south, Group);
            var c = Spawn(w1, _north, Group);

            w1.Transfer(a, w2);
            _mesh.Pump();
            Assert.AreEqual(3, TransferredTo(_mesh, w2.Id).Count);

            // And back: the receiving worker's copies are now the group, and handing one back takes all three.
            var w2a = w2.Find(a.NetId);
            w2.Transfer(w2a, w1);
            _mesh.Pump();

            CollectionAssert.AreEquivalent(new[] { a.NetId, b.NetId, c.NetId }, TransferredTo(_mesh, w1.Id));
            Assert.IsTrue(a.HasAuthority);
            Assert.IsTrue(b.HasAuthority);
            Assert.IsTrue(c.HasAuthority);
            Assert.AreEqual(3u, a.Epoch, "one epoch bump per transfer: the spawn, out and back");
            Assert.AreEqual(3u, b.Epoch, "a member carried along bumps its epoch exactly like the one that was asked for");
            Assert.AreEqual(_splitsBefore, NebulaDiagnostics.SplitCohesionGroups);
        }

        [Test]
        public void AGroupMemberThatIsACarrierStillTakesItsPassengers()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var hull = Spawn(w1, _north, Group);
            // The ship is in the group; the crate rides inside it and is in no group at all.
            var ship = w1.SpawnServerDriven(_carrierPrefabId, _south, _south.transform.position, Quaternion.identity);
            ship.JoinCohesionGroup(Group);
            Assume.That(ship.Carried, Is.Not.Null, "the carrier registered its box");
            var crate = w1.SpawnServerDriven(_prefabId, ship.Carried, ship.transform.position + Vector3.up, Quaternion.identity);
            Assume.That(crate.Container, Is.SameAs(ship.Carried));

            w1.Transfer(hull, w2);
            _mesh.Pump();

            CollectionAssert.AreEquivalent(new[] { hull.NetId, ship.NetId, crate.NetId }, TransferredTo(_mesh, w2.Id),
                "a member that is a carrier opens a handover scope of its own, so its passengers come too");
            Assert.IsTrue(w2.Find(crate.NetId).HasAuthority);
            Assert.AreEqual(_splitsBefore, NebulaDiagnostics.SplitCohesionGroups, "a passenger is not a missing member");
        }

        [Test]
        public void LeavingTheGroupStopsTheEntityFromFollowing()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var hull = Spawn(w1, _north, Group);
            var turret = Spawn(w1, _south, Group);
            turret.LeaveCohesionGroup();

            w1.Transfer(hull, w2);
            _mesh.Pump();

            CollectionAssert.AreEquivalent(new[] { hull.NetId }, TransferredTo(_mesh, w2.Id));
            Assert.IsTrue(turret.HasAuthority, "an entity that left the group stays where it is");
            Assert.AreEqual(_splitsBefore, NebulaDiagnostics.SplitCohesionGroups, "leaving a group is not a split");
        }

        // ---- a group that cannot be moved as a unit is reported --------------------------------------------

        [Test]
        public void AMemberThisWorkerDoesNotOwnIsReportedAndNeverSilentlySplit()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var w3 = _mesh[2];
            var hull = Spawn(w1, _north, Group);
            // The turret is handed to w3 before it joins the group, so w1 keeps only a ghost of it. The game then
            // puts both in the group, as game code running on every copy would.
            var turret = Spawn(w1, _south, 0);
            w1.Transfer(turret, w3);
            _mesh.Pump();
            Assume.That(turret.HasAuthority, Is.False, "w1 holds the turret as a ghost now");
            turret.JoinCohesionGroup(Group);

            LogAssert.Expect(LogType.Warning, new Regex(@"cohesion group 5 cannot be handed over as a unit"));
            w1.Transfer(hull, w2);
            _mesh.Pump();

            Assert.AreEqual(_splitsBefore + 1, NebulaDiagnostics.SplitCohesionGroups, "the failure is counted, not swallowed");
            CollectionAssert.AreEquivalent(new[] { hull.NetId }, TransferredTo(_mesh, w2.Id),
                "the entity that was asked for still moves: refusing would strand it in a container this worker no longer leases");
            Assert.IsTrue(w3.Find(turret.NetId).HasAuthority, "the member this worker does not own is untouched");
        }

        [Test]
        public void AGhostOnTheWayToTheSameWorkerIsNotASplit()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var hull = Spawn(w1, _north, Group);
            var turret = Spawn(w1, _south, 0);
            w1.Transfer(turret, w2);
            _mesh.Pump();
            turret.JoinCohesionGroup(Group);

            w1.Transfer(hull, w2);   // the group is meeting up on w2, not being split
            _mesh.Pump();

            Assert.AreEqual(_splitsBefore, NebulaDiagnostics.SplitCohesionGroups);
            Assert.IsTrue(w2.Find(hull.NetId).HasAuthority);
            Assert.IsTrue(w2.Find(turret.NetId).HasAuthority);
        }

        // ---- what the worker reports ----------------------------------------------------------------------

        [Test]
        public void TheWorkerReportsItsGroupsAndHoldsAndTheOrchestratorReadsThemBack()
        {
            var w1 = _mesh[0];
            Spawn(w1, _north, Group);
            Spawn(w1, _south, Group);
            w1.Instance.HoldContainer(_south, 6f);
            Assert.AreEqual(1, w1.Instance.HeldContainers);
            Assert.That(w1.Instance.HoldRemaining(_south.ContainerId), Is.GreaterThan(0f));

            var spans = new List<NebulaWorker.CohesionSpan>();
            w1.Instance.CopyCohesion(spans);
            Assert.AreEqual(1, spans.Count);
            Assert.AreEqual(Group, spans[0].Group);
            Assert.AreEqual(2, spans[0].Members);
            CollectionAssert.AreEquivalent(new[] { _north.ContainerId, _south.ContainerId }, spans[0].Containers);

            // Through the real telemetry document and the orchestrator's reader, as a live mesh does it.
            var telemetry = WorkerTelemetry.ForTests();
            telemetry.Update(w1.Instance);   // reads the holds and groups off the worker, as it does every second
            string document = telemetry.Write(w1.Id, w1.Index, 1, w1.Instance.Entities, false, null);
            var mesh = new MeshTelemetry(() => 0.0);
            Assert.IsNull(mesh.Accept(document, out _));

            var holds = new Dictionary<string, float>();
            mesh.CopyHolds(holds);
            Assert.AreEqual(1, holds.Count, "the hold reached the orchestrator");
            Assert.That(holds[_south.ContainerId], Is.GreaterThan(0f));

            var groups = new List<CohesionGroupInfo>();
            mesh.CopyCohesion(groups);
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(Group, groups[0].Group);
            Assert.AreEqual(2, groups[0].Members);
            CollectionAssert.AreEquivalent(new[] { _north.ContainerId, _south.ContainerId }, groups[0].Containers);

            w1.Instance.ReleaseHold(_south.ContainerId);
            Assert.AreEqual(0, w1.Instance.HeldContainers);
        }

        [Test]
        public void AHoldIsClampedAndExtendedButNeverShortened()
        {
            var w1 = _mesh[0];
            w1.Instance.HoldContainer(_north.ContainerId, 10f * NebulaWorker.MaxHoldSeconds);
            Assert.That(w1.Instance.HoldRemaining(_north.ContainerId), Is.LessThanOrEqualTo(NebulaWorker.MaxHoldSeconds));

            float long_ = w1.Instance.HoldRemaining(_north.ContainerId);
            w1.Instance.HoldContainer(_north.ContainerId, 1f);
            Assert.AreEqual(long_, w1.Instance.HoldRemaining(_north.ContainerId), 0.5f, "a shorter hold does not cut a longer one short");

            w1.Instance.HoldContainer(_north.ContainerId, 0f);
            Assert.AreEqual(0f, w1.Instance.HoldRemaining(_north.ContainerId), "zero seconds releases it");
        }
    }
}
