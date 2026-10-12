using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-428 (<c>docs/portals.md</c> §4): a player-owned pawn crosses through an open <see cref="PortalLink"/> on the tick its root ends
    /// beyond the face, with no preparation of its own and nothing held, keeping its identity and absolute pose; a closed, unwarmed or
    /// refused crossing meets the aperture; the party picks the link. These are the scenarios of the old boundary's request and readiness
    /// tests, ported: what a request and a preparation were is now the link's state. Tier B, on <see cref="PortalHarness"/>.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalCrossingTests
    {
        private PortalHarness _h;
        private readonly List<(NetworkIdentity entity, bool leaving)> _crossed = new List<(NetworkIdentity, bool)>();

        [SetUp]
        public void SetUp()
        {
            _h = new PortalHarness(1);
            _crossed.Clear();
            _h.Door.Crossed += (entity, link, leaving) => _crossed.Add((entity, leaving));
        }

        [TearDown] public void TearDown() => _h.Dispose();

        private System.Collections.IDictionary Transfers =>
            (System.Collections.IDictionary)typeof(NebulaWorker).GetField("_instanceTransfers", PortalHarness.Hidden).GetValue(_h.W.Instance);

        private static Double3 Absolute(NetworkIdentity entity) => ContainerRegistry.ToAbsolutePrecise(entity.ScopePositionPrecise, entity.InstanceId);

        // ------------------------------------------------------------------------------------------ crossing

        [Test]
        public void AnOpenLinkLetsAPawnThroughOnTheTickItPassesAndBackOutAgain()
        {
            var link = _h.OpenDoor();
            _h.Tick(); // standing near the face, ready
            int refusals = NebulaDiagnostics.PortalRefusals;
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private), "inside on the tick it passed");
            Assert.That(_h.Player.Container, Is.SameAs(_h.Hall));
            Assert.That(_h.Player.transform.position, Is.EqualTo(PortalHarness.At(0f, 1f)), "at the same absolute pose");
            Assert.That(Transfers.Count, Is.EqualTo(0), "no per-entity preparation, nothing held");
            Assert.That(NebulaDiagnostics.PortalRefusals, Is.EqualTo(refusals));
            Assert.That(_crossed, Is.EqualTo(new[] { (_h.Player, false) }));

            // Back out: just short of the face, in the instance's own axes.
            _h.Player.transform.position = PortalHarness.At(0f, -0.5f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street), "left just past the face");
            Assert.That(_h.Player.InstanceId, Is.EqualTo(0UL));
            Assert.That(_h.Player.transform.position, Is.EqualTo(PortalHarness.At(0f, -0.5f)));
            Assert.That(_crossed[1], Is.EqualTo((_h.Player, true)));
        }

        [Test]
        public void ThePawnKeepsItsIdentityAndEpochChangesSoCopiesTakeTheNextStateAsALocation()
        {
            _h.OpenDoor();
            _h.Tick();
            ulong netId = _h.Player.NetId;
            uint epoch = _h.Player.Epoch;
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.NetId, Is.EqualTo(netId));
            Assert.That(_h.Player.Epoch, Is.EqualTo(epoch + 1));
            Assert.That(_h.Player.HasAuthority, Is.True);
        }

        [Test]
        public void AFarPawnIsNotTrackedAndASpawnedPastThePlaneDoesNotCross()
        {
            _h.OpenDoor();
            _h.Player.transform.position = PortalHarness.At(0f, -40f);
            _h.Ticks(2);
            Assert.That(_h.Player.InstanceId, Is.EqualTo(0UL));
            // It appears past the plane in the street (inside the hill): with no earlier position it has not passed through the opening.
            _h.Player.transform.position = PortalHarness.At(0f, 3f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(0UL), "a body that did not walk through the aperture does not cross");
        }

        [Test]
        public void ABodyThatMissesTheApertureDoesNotCross()
        {
            _h.OpenDoor();
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(5f, -1f); // beside the 4 m wide opening
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(5f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(0UL), "past the plane, but not through the opening");
        }

        [Test]
        public void StandingAtTheFaceDoesNotFlipScopeOnEveryTick()
        {
            _h.OpenDoor();
            _h.Tick();
            int flips = 0;
            ulong scope = _h.Player.InstanceId;
            for (int i = 0; i < 16; i++)
            {
                _h.Player.transform.position = PortalHarness.At(0f, i % 2 == 0 ? 0.03f : -0.03f); // within CrossDepth either side
                _h.Tick();
                if (_h.Player.InstanceId != scope) { flips++; scope = _h.Player.InstanceId; }
            }
            Assert.That(flips, Is.EqualTo(0), "the depth is the hysteresis the exit margin was");

            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private));
            for (int i = 0; i < 16; i++)
            {
                _h.Player.transform.position = PortalHarness.At(0f, i % 2 == 0 ? 0.03f : 0.4f);
                _h.Tick();
            }
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private), "and from inside, short of the plane by less than the depth, it stays");
            _h.Player.transform.position = PortalHarness.At(0f, -0.06f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street), "it leaves once it is past it");
        }

        // ------------------------------------------------------------------------------------------ meeting the aperture

        [Test]
        public void AClosedPortalHoldsAPawnWhereItLastStood()
        {
            _h.Ticks(2); // never opened
            int refusals = NebulaDiagnostics.PortalRefusals;
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street), "the face is closed");
            Assert.That(_h.Player.transform.position, Is.EqualTo(PortalHarness.At(0f, -6f)), "held where it last stood");
            Assert.That(NebulaDiagnostics.PortalRefusals, Is.EqualTo(refusals + 1));
        }

        [Test]
        public void APreparingLinkRefusesAndThenAdmitsOnceOpen()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Ticks(2);
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street), "nothing crosses while the link prepares");

            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private));
        }

        [Test]
        public void ANonMemberAndAnUnwarmMemberAreRefused()
        {
            var link = _h.OpenDoor(members: new ulong[] { 8 }); // the pawn's client, 7, is not a member
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street), "a client that is not a member does not cross");

            _h.Player.transform.position = PortalHarness.At(0f, -6f);
            _h.Tick();
            _h.Door.AddMember(PortalHarness.Owner, PortalHarness.Client);
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street), "and a member that is not warm yet meets the aperture too, though the link is open");
            Assert.That(link.State, Is.EqualTo(PortalState.Open));

            _h.Player.transform.position = PortalHarness.At(0f, -6f);
            _h.Tick();
            _h.Warm(link);
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private), "warm, it passes");
        }

        [Test]
        public void CanEnterDecidesAdmissionAndLeavingIsNotChecked()
        {
            _h.OpenDoor();
            _h.Tick();
            _h.Door.CanEnter = (entity, link) => false;
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street));

            _h.Door.CanEnter = (entity, link) => entity == _h.Player && link.Party == PortalHarness.Owner;
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Hall));

            _h.Door.CanEnter = (entity, link) => false;
            _h.Player.transform.position = PortalHarness.At(0f, -0.5f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street), "an occupant can always leave");
        }

        [Test]
        public void AnOccupantCannotLeaveThroughALinkThatClosedUnderIt()
        {
            _h.OpenDoor();
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(0f, 3f);
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(0f, 3f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private));
            _h.Door.Close(PortalHarness.Owner);
            _h.Tick();
            Assert.That(_h.Door.StateOf(PortalHarness.Owner), Is.EqualTo(PortalState.Closed));
            _h.Player.transform.position = PortalHarness.At(0f, -0.5f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private), "a closed aperture is solid from inside as well");
            Assert.That(_h.Player.transform.position, Is.EqualTo(PortalHarness.At(0f, 3f)), "held where it last stood");
        }

        [Test]
        public void AServerOwnedEntityDoesNotCrossOnItsOwn()
        {
            _h.OpenDoor();
            var crate = _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, PortalHarness.At(1f, -6f), Quaternion.identity);
            _h.Tick();
            crate.transform.position = PortalHarness.At(1f, 1f);
            _h.Tick();
            Assert.That(crate.Container, Is.SameAs(_h.Street), "every other entity crossing on its own is NEB-429's");
        }

        // ------------------------------------------------------------------------------------------ parties

        private NetworkIdentity Pawn(ulong client, string identity, float x)
        {
            var pawn = _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, PortalHarness.At(x, -6f), Quaternion.identity);
            pawn.OwnerClientId = client;
            pawn.OwnerIdentity = identity;
            return pawn;
        }

        [Test]
        public void EachPartyCrossesIntoItsOwnFarScopeThroughTheSameFace()
        {
            var a = _h.OpenDoor(party: "player-a", members: new[] { PortalHarness.Client });
            var b = _h.Door.Open("player-b", new ulong[] { 8 });
            _h.Ticks(3);
            _h.Warm(b, 8);
            _h.Tick();
            Assert.That(b.State, Is.EqualTo(PortalState.Open));
            var other = Pawn(8, "player-b", 1f);
            _h.Ticks(2);
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            other.transform.position = PortalHarness.At(1f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(NebulaWorker.InstanceKey("vault/player-a")));
            Assert.That(other.InstanceId, Is.EqualTo(NebulaWorker.InstanceKey("vault/player-b")), "one near face, one far scope per party");
            Assert.That(a.FarScope, Is.Not.EqualTo(b.FarScope));
        }

        [Test]
        public void AnEntityWithNoPartyUsesTheFacesOneOpenLinkOrNone()
        {
            _h.Door.PartyOf = entity => null;
            var a = _h.OpenDoor(party: "player-a", members: new[] { PortalHarness.Client });
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private), "exactly one link is open: it is the one");

            _h.Player.transform.position = PortalHarness.At(0f, -0.5f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(0UL));
            var b = _h.Door.Open("player-b", new[] { PortalHarness.Client });
            _h.Ticks(3);
            _h.Warm(b);
            _h.Tick();
            Assert.That(b.State, Is.EqualTo(PortalState.Open));
            _h.Player.transform.position = PortalHarness.At(0f, -6f);
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(0UL), "two links are open and nothing says whose it is: ambiguous, refused");
            Assert.That(a.State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void PartyOfOverridesTheOwnersIdentity()
        {
            _h.Door.PartyOf = entity => "shared";
            var link = _h.OpenDoor(party: "shared", members: new[] { PortalHarness.Client });
            _h.Tick();
            _h.Player.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(NebulaWorker.InstanceKey("vault/shared")));
            Assert.That(link.FarScope, Is.EqualTo(_h.Player.InstanceId));
        }

        [Test]
        public void APortalOnAnotherScopeIgnoresPlayersThereAtTheSameNumbers()
        {
            _h.OpenDoor();
            // A pawn in the instance of another party stands where the door is, in numbers: it is in another scope.
            var other = _h.Door.Open("player-c", new[] { PortalHarness.Client });
            _h.Ticks(3);
            _h.Warm(other);
            _h.Tick();
            Assert.That(other.State, Is.EqualTo(PortalState.Open));
            Assert.That(other.FarScope, Is.Not.EqualTo(_h.Hall.InstanceId));
        }

        // ------------------------------------------------------------------------------------------ a turned face

        [Test]
        public void ATurnedFaceTurnsPositionRotationAndVelocityThroughTheLink()
        {
            var turn = Quaternion.Euler(0f, 90f, 0f); // the face looks along world +x
            _h.Door.transform.rotation = turn;
            var link = _h.OpenDoor();
            Assert.That(Quaternion.Angle(link.Turn, turn), Is.LessThan(0.01f), "the link turns the instance about the door");
            Assert.That(link.NearFace.Forward.x, Is.EqualTo(1f).Within(1e-5f));
            _h.Player.transform.position = new Vector3(14f, 1f, 20f);
            _h.Player.transform.rotation = turn;
            _h.Tick();
            _h.Tick();
            _h.Player.transform.position = new Vector3(21f, 1f, 20f);
            _h.Player.transform.rotation = turn * Quaternion.Euler(0f, 10f, 0f);
            _h.Player.Motion.Velocity = new Vector3(2f, 0f, 0f);
            _h.Tick();

            Assert.That(_h.Player.Container, Is.SameAs(_h.Hall), "through the turned face");
            var local = Absolute(_h.Player) - link.NearFace.Origin;
            Assert.That(local.X, Is.EqualTo(0.0).Within(1e-3));
            Assert.That(local.Y, Is.EqualTo(1.0).Within(1e-3));
            Assert.That(local.Z, Is.EqualTo(1.0).Within(1e-3), "upright in the instance: the door's own offsets");
            Assert.That(Quaternion.Angle(Quaternion.Euler(0f, 10f, 0f), _h.Player.transform.rotation), Is.LessThan(0.05f), "facing as it did, in the instance's axes");
            Assert.That((_h.Player.Motion.Velocity - new Vector3(0f, 0f, 2f)).magnitude, Is.LessThan(1e-4f), "moving the same way");

            _h.Player.transform.position = new Vector3(20f, 1f, 19.5f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street), "back out");
            Assert.That(_h.Player.transform.position.x, Is.EqualTo(19.5f).Within(2e-3f), "turned back to where it walked out");
            Assert.That(_h.Player.transform.position.z, Is.EqualTo(20f).Within(2e-3f));
            Assert.That(Quaternion.Angle(turn * Quaternion.Euler(0f, 10f, 0f), _h.Player.transform.rotation), Is.LessThan(0.05f));
            Assert.That((_h.Player.Motion.Velocity - new Vector3(2f, 0f, 0f)).magnitude, Is.LessThan(1e-4f));
        }

        [Test]
        public void ATiltedFaceDetectsACrossingThroughItsOwnApertureAndNotBesideOrAboveIt()
        {
            var tilt = Quaternion.Euler(35f, 0f, 0f); // a doorway on sloping ground
            _h.Door.transform.rotation = tilt;
            _h.OpenDoor();
            Vector3 At(float x, float y, float z) => PortalHarness.DoorAt + tilt * new Vector3(x, y, z);

            _h.Player.transform.position = At(0f, 4f, -3f); // above the 5 m tall opening, along the slope
            _h.Ticks(2);
            _h.Player.transform.position = At(0f, 4f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(0UL), "past the plane, but above the opening");

            _h.Player.transform.position = At(5f, 1f, -3f);
            _h.Ticks(2);
            _h.Player.transform.position = At(5f, 1f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(0UL), "and beside it");

            _h.Player.transform.position = At(0f, 1f, -3f);
            _h.Ticks(2);
            _h.Player.transform.position = At(0f, 1f, 1f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(PortalHarness.Private), "but through it, along the tilted way in");
            var local = Absolute(_h.Player) - _h.Door.NearFace.Origin;
            Assert.That(local.Z, Is.EqualTo(1.0).Within(1e-3), "upright in the instance: the offsets the door's axes gave");
            Assert.That(local.Y, Is.EqualTo(1.0).Within(1e-3));
        }

        [Test]
        public void LinkChangedIsRaisedOncePerChangeAndNotWhileNothingChanges()
        {
            var seen = new List<(PortalState, PortalWait)>();
            _h.Door.LinkChanged += link => seen.Add((link.State, link.Wait));
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Ticks(3);
            _h.Warm(link);
            _h.Tick();
            int count = seen.Count;
            Assert.That(seen[0], Is.EqualTo((PortalState.Preparing, PortalWait.FarScopeActivating)));
            Assert.That(seen[seen.Count - 1], Is.EqualTo((PortalState.Open, PortalWait.None)));
            Assert.That(seen, Is.Unique, "no state and wait is reported twice in a row for nothing");
            _h.Ticks(5);
            Assert.That(seen.Count, Is.EqualTo(count), "raised once, not on every tick it stays open");
            _h.Door.Close(PortalHarness.Owner);
            _h.Tick();
            Assert.That(seen[seen.Count - 1], Is.EqualTo((PortalState.Closed, PortalWait.None)));
        }

        // ------------------------------------------------------------------------------------------ a fixed, offset far side

        [Test]
        public void ACrossingThroughAnOffsetFixedFarSideLandsAtTheMappedPointAndBack()
        {
            var parts = _h.W.Instance.PrepareInstance(_h.Template, "other", new Double3(100, 0, 100));
            _h.Ticks(2);
            var portal = _h.NewPortal("bridge", new Vector3(60f, 0f, 20f));
            portal.SetFixedFarSide("vault/other", new Double3(100, 0, 100), Quaternion.identity);
            var link = portal.Open("", new[] { PortalHarness.Client });
            _h.Ticks(3);
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), link.ToString());
            _h.Player.transform.position = new Vector3(60f, 1f, 14f);
            _h.Player.OwnerIdentity = "somebody";
            _h.Ticks(2);
            _h.Player.transform.position = new Vector3(60.5f, 1f, 21f);
            _h.Tick();
            Assert.That(_h.Player.InstanceId, Is.EqualTo(NebulaWorker.InstanceKey("vault/other")), "into the scope that exists anyway");
            var landed = Absolute(_h.Player);
            Assert.That(landed.X, Is.EqualTo(100.5).Within(1e-3), "the translation of an offset portal");
            Assert.That(landed.Y, Is.EqualTo(1.0).Within(1e-3));
            Assert.That(landed.Z, Is.EqualTo(101.0).Within(1e-3));
            Assert.That(_h.Player.Container.ContainerId, Is.EqualTo(ContainerRegistry.RuntimeContainerId(parts[0].RuntimeId)));

            _h.Player.transform.position = new Vector3(100.5f, 1f, 99.5f);
            _h.Tick();
            Assert.That(_h.Player.Container, Is.SameAs(_h.Street));
            Assert.That(_h.Player.transform.position.x, Is.EqualTo(60.5f).Within(2e-3f));
            Assert.That(_h.Player.transform.position.z, Is.EqualTo(19.5f).Within(2e-3f));
        }
    }
}
