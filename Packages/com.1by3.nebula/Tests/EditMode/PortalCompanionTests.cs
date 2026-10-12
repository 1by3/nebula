using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-428 (<c>docs/portals.md</c> D14): a <see cref="Portal"/> crosses an entity together with its companions
    /// (<see cref="Portal.ResolveCompanions"/>): the group commits as one on the tick the leader passes, into a destination this worker
    /// owns or another worker owns, every member keeping its identity and arriving at its own pose mapped through the link. A companion that
    /// cannot cross is left behind and reported (<see cref="Portal.CompanionLeftBehind"/>). With the far side warm a companion never waits for a
    /// preparation of its own, so the old boundary's pending-companion wait is gone; a companion whose client is not a warm member is simply
    /// left behind. Tier B, on <see cref="PortalHarness"/> with two workers.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalCompanionTests
    {
        private PortalHarness _h;
        private NetworkIdentity _crate;
        private bool _holding;
        private readonly List<(NetworkIdentity leader, NetworkIdentity companion, PortalRefusal why)> _leftBehind =
            new List<(NetworkIdentity, NetworkIdentity, PortalRefusal)>();

        [SetUp]
        public void SetUp()
        {
            _h = new PortalHarness(2);
            _holding = true;
            _leftBehind.Clear();
            // A server-owned crate the player holds just in front of them.
            _crate = _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, PortalHarness.At(1f, -5f), Quaternion.identity);
            _h.Door.ResolveCompanions = (entity, companions) => { if (entity == _h.Player && _holding) companions.Add(_crate); };
            _h.Door.CompanionLeftBehind += (leader, companion, why) => _leftBehind.Add((leader, companion, why));
        }

        [TearDown] public void TearDown() => _h.Dispose();

        /// <summary>Walk the player, crate in hand, through the face: the crate goes ahead of them.</summary>
        private void StepIn()
        {
            _crate.transform.position = PortalHarness.At(1f, 2f);
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
        }

        [Test]
        public void ACompanionCrossesTogetherWithItsLeader()
        {
            ulong crateId = _crate.NetId;
            var link = _h.OpenDoor();
            _h.Tick();
            StepIn();
            var cratePosition = _crate.transform.position;
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private));
            Assert.That(_h.Player.Container, Is.SameAs(_h.Hall));
            Assert.That(_crate.InstanceId, Is.EqualTo(PortalHarness.Private), "the crate crossed in the same tick");
            Assert.That(_crate.Container, Is.SameAs(_h.Hall), "into the same container");
            Assert.That(_crate.NetId, Is.EqualTo(crateId), "as the same entity");
            Assert.That(_crate.HasAuthority, Is.True);
            Assert.That(_crate.transform.position, Is.EqualTo(cratePosition), "at the same absolute pose");
            Assert.That(_leftBehind, Is.Empty);

            // And back out together, from inside.
            _h.Tick();
            _crate.transform.position = PortalHarness.At(1f, -1f);
            _h.Player.transform.position = PortalHarness.At(0f, -0.5f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street));
            Assert.That(_crate.Container, Is.SameAs(_h.Street), "the way out takes the crate too");
            Assert.That(_crate.InstanceId, Is.EqualTo(0UL));
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void ACompanionKeepsItsOwnTurnedPoseThroughATurnedFace()
        {
            var turn = Quaternion.Euler(0f, 90f, 0f);
            _h.Door.transform.rotation = turn;
            _h.OpenDoor();
            _h.Player.transform.position = new Vector3(14f, 1f, 20f);
            _crate.transform.position = new Vector3(15f, 1f, 21f);
            _crate.transform.rotation = turn;
            _h.Ticks(2);
            _crate.transform.position = new Vector3(22f, 1f, 21f);
            _h.Player.transform.position = new Vector3(21f, 1f, 20f);
            _h.Tick();
            Assert.That(_crate.Container, Is.SameAs(_h.Hall));
            var at = ContainerRegistry.ToAbsolutePrecise(_crate.ScopePositionPrecise, _crate.InstanceId);
            Assert.That(at.X, Is.EqualTo(19.0).Within(1e-3), "the crate's offset from the door, turned into the instance's axes");
            Assert.That(at.Z, Is.EqualTo(22.0).Within(1e-3));
            Assert.That(Quaternion.Angle(Quaternion.identity, _crate.transform.rotation), Is.LessThan(0.05f), "and its rotation turned with the leader's, which a translation alone would not do");
        }

        [Test]
        public void WithoutCompanionsEachEntityCrossesOnItsOwn()
        {
            _holding = false;
            _h.OpenDoor();
            _h.Tick();
            StepIn();
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private));
            Assert.That(_crate.Container, Is.SameAs(_h.Hall), "and a server-owned entity that is nobody's companion crosses on its own (NEB-429)");
        }

        [Test]
        public void ACompanionDoesNotCrossOnItsOwnWhileItsLeaderIsNearTheFace()
        {
            var companion = _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, PortalHarness.At(1f, -5f), Quaternion.identity);
            companion.OwnerClientId = 8;
            companion.OwnerIdentity = "player-b";
            _h.Door.ResolveCompanions = (entity, list) => { if (entity == _h.Player) list.Add(companion); };
            var link = _h.OpenDoor(members: new ulong[] { PortalHarness.Client, 8 });
            _h.Tick();
            // The companion is a pawn of another client and goes ahead of its leader: it must not cross first on its own.
            companion.transform.position = PortalHarness.At(1f, 2f);
            _h.Tick();
            Assert.That(companion.Container, Is.SameAs(_h.Street), "it moves with its leader and does not cross on its own while it is one");
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void TheGroupCrossesIntoADestinationAnotherWorkerOwns()
        {
            _h.HallOnSecondWorker = true;
            ulong playerId = _h.Player.NetId, crateId = _crate.NetId;
            _h.OpenDoor();
            _h.Tick();
            Assert.That(_h.Hall.OwnerWorkerId, Is.EqualTo(_h.Other.Id));
            Assert.That(_h.Mesh.DeliveredOf(MsgId.InstancePrepare, _h.Other.Id).Count, Is.EqualTo(1), "the destination worker is asked once, for the link, not for each member");

            StepIn();
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Hall));
            Assert.That(_crate.Container, Is.SameAs(_h.Hall), "committed together, into the other worker's container");
            _h.Tick(); // each follows its container's lease to the other worker
            var player = _h.Other.Find(playerId);
            var crate = _h.Other.Find(crateId);
            Assert.That(player, Is.Not.Null.And.Property(nameof(NetworkIdentity.HasAuthority)).True, "the player arrived on the other worker");
            Assert.That(crate, Is.Not.Null.And.Property(nameof(NetworkIdentity.HasAuthority)).True, "and so did the crate, as the same entity");
            Assert.That(crate.InstanceId, Is.EqualTo(PortalHarness.Private));
        }

        // ------------------------------------------------------------------------------------------ left behind

        [Test]
        public void ARefusedCompanionIsLeftBehindAndReported()
        {
            _h.Door.CanEnter = (entity, link) => entity != _crate;
            _h.OpenDoor();
            _h.Tick();
            StepIn();
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private), "the leader crosses alone");
            Assert.That(_crate.Container, Is.SameAs(_h.Street), "the crate stays where it was");
            Assert.That(_leftBehind.Count, Is.EqualTo(1));
            Assert.That(_leftBehind[0].leader, Is.SameAs(_h.Player));
            Assert.That(_leftBehind[0].companion, Is.SameAs(_crate));
            Assert.That(_leftBehind[0].why, Is.EqualTo(PortalRefusal.NotAdmitted));
        }

        [Test]
        public void ACompanionThisWorkerDoesNotSimulateIsLeftBehind()
        {
            _h.OpenDoor();
            _h.Tick();
            _crate.HasAuthority = false; // as when another worker simulates it
            StepIn();
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private));
            Assert.That(_leftBehind.Count, Is.EqualTo(1));
            Assert.That(_leftBehind[0].why, Is.EqualTo(PortalRefusal.NotSimulated));
            _crate.HasAuthority = true;
        }

        [Test]
        public void ACompanionOfAClientThatIsNotAWarmMemberIsLeftBehind()
        {
            _crate.OwnerClientId = 8;
            _crate.OwnerIdentity = "player-b";
            _h.OpenDoor();
            _h.Tick();
            StepIn();
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private));
            Assert.That(_crate.Container, Is.SameAs(_h.Street));
            Assert.That(_leftBehind.Count, Is.EqualTo(1));
            Assert.That(_leftBehind[0].why, Is.EqualTo(PortalRefusal.NotAdmitted), "no pending wait: a companion either may cross now or is left behind");
        }
    }
}
