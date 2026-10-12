using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-429 (<c>docs/portals.md</c> D14 to D17): any entity this worker simulates crosses an open link on its own, not only a player's
    /// pawn. A server-owned crate pushed through commits on the tick its root passes with no preparation, no hold and nothing instantiated;
    /// a projectile moving 5 m a step is committed before the step that would carry it through; a carrier crosses with its riders seated;
    /// an entity the link does not let through is stopped at the face and <see cref="Portal.Refused"/> is raised; and a portal is asked only
    /// about the entities that can matter to it. Tier B, on <see cref="PortalHarness"/>.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalAnyEntityTests
    {
        private PortalHarness _h;
        private ushort _crateBodyPrefab, _shipPrefab;
        private readonly List<(NetworkIdentity entity, ulong from, ulong to)> _crossed = new List<(NetworkIdentity, ulong, ulong)>();
        private readonly List<(NetworkIdentity entity, PortalLink link, PortalRefusal why)> _refused = new List<(NetworkIdentity, PortalLink, PortalRefusal)>();

        [SetUp]
        public void SetUp()
        {
            _h = new PortalHarness(1);
            StateHistory.WindowTicks = 16; // the mesh has no Initialize(), which is what reads the config knob
            _crossed.Clear();
            _refused.Clear();
            _h.Door.Crossed += (entity, portal, from, to) => _crossed.Add((entity, from, to));
            _h.Door.Refused += (entity, link, why) => _refused.Add((entity, link, why));
            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            var body = crate.AddComponent<Rigidbody>();
            body.useGravity = false;
            _crateBodyPrefab = _h.Mesh.RegisterPrefab(crate);
            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var hold = ship.AddComponent<Container>();
            hold.ContainerId = "hold";
            hold.Size = new Vector3(3f, 2f, 3f);
            hold.Center = new Vector3(0f, 1f, 0f);
            ship.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            _shipPrefab = _h.Mesh.RegisterPrefab(ship);
        }

        [TearDown] public void TearDown() => _h.Dispose();

        private System.Collections.IDictionary Transfers =>
            (System.Collections.IDictionary)typeof(NebulaWorker).GetField("_instanceTransfers", PortalHarness.Hidden).GetValue(_h.W.Instance);

        private NetworkIdentity Crate(float x, float z) => _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, PortalHarness.At(x, z), Quaternion.identity);

        private static Double3 Absolute(NetworkIdentity entity) => ContainerRegistry.ToAbsolutePrecise(entity.ScopePositionPrecise, entity.InstanceId);

        // ------------------------------------------------------------------------------------------ a crate pushed through

        [Test]
        public void AServerOwnedCratePushedThroughAnOpenApertureCommitsOnTheTickItsRootPasses()
        {
            _h.OpenDoor();
            _h.Tick();
            var crate = Crate(0.5f, -1f);
            _h.Tick();
            int objects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length;
            uint epoch = crate.Epoch;
            ulong netId = crate.NetId;

            // Pushed 3 cm a tick: a root that is past the plane by less than the depth has not crossed.
            float z = -1f;
            float last = z;
            int ticks = 0;
            while (crate.InstanceId == 0UL && ticks++ < 80)
            {
                last = z;
                z += 0.03f;
                crate.transform.position = PortalHarness.At(0.5f, z);
                _h.Tick();
            }
            Assert.That(crate.InstanceId, Is.EqualTo(PortalHarness.Private), "in the room");
            Assert.That(z, Is.GreaterThanOrEqualTo(Portal.CrossDepth - 1e-4f), "on the tick its root ended CrossDepth beyond the plane");
            Assert.That(last, Is.LessThan(Portal.CrossDepth), "and not before");
            Assert.That(z - Portal.CrossDepth, Is.LessThan(0.031f), "to the step");
            Assert.That(crate.Container, Is.SameAs(_h.Hall), "into the entry part of the room");
            Assert.That((crate.transform.position - PortalHarness.At(0.5f, z)).magnitude, Is.LessThan(1e-4f), "at the same absolute pose: nothing held it");
            Assert.That(crate.NetId, Is.EqualTo(netId));
            Assert.That(crate.Epoch, Is.EqualTo(epoch + 1));
            Assert.That(Transfers.Count, Is.EqualTo(0), "no PrepareTransfer");
            Assert.That(Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length, Is.EqualTo(objects), "and nothing was instantiated");
            Assert.That(_crossed, Is.EqualTo(new[] { (crate, 0UL, PortalHarness.Private) }));
            Assert.That(_refused, Is.Empty);

            // And back out: a server-owned entity leaves just as it came.
            crate.transform.position = PortalHarness.At(0.5f, -0.2f);
            _h.Tick();
            Assert.That(crate.Container, Is.SameAs(_h.Street));
            Assert.That(_crossed.Count, Is.EqualTo(2));
            Assert.That(_crossed[1], Is.EqualTo((crate, PortalHarness.Private, 0UL)));
        }

        [Test]
        public void AnUnownedEntityBesideTheApertureOrFarFromItDoesNotCross()
        {
            _h.OpenDoor();
            var beside = Crate(5f, -1f);
            var far = Crate(0f, -60f);
            _h.Ticks(2);
            beside.transform.position = PortalHarness.At(5f, 1f);
            far.transform.position = PortalHarness.At(0f, 1f); // jumped across without having been near: it did not walk through
            _h.Ticks(2);
            Assert.That(beside.InstanceId, Is.EqualTo(0UL), "past the plane, beside the opening");
            Assert.That(far.InstanceId, Is.EqualTo(0UL));
            Assert.That(_crossed, Is.Empty);
        }

        // ------------------------------------------------------------------------------------------ a fast body

        [Test]
        public void AProjectileAt300MetresASecondCrossesWithoutAStepBehindTheFace()
        {
            _h.OpenDoor();
            _h.Tick();
            float dt = NetworkTime.TickInterval;
            float step = 300f * dt; // 5 m a tick
            Assert.That(step, Is.GreaterThan(Portal.CrossDepth * 50f));
            var shot = Crate(0f, 0f);
            shot.transform.position = PortalHarness.At(0f, -1.5f * step);
            shot.Motion.Velocity = new Vector3(0f, 0f, 300f);
            _h.Tick();

            int stepsBehindTheFace = 0;
            float committedAt = float.NaN;
            for (int i = 0; i < 6; i++)
            {
                // The simulation step that follows the worker's tick: whichever scene the shot is in moves it by its velocity.
                var before = Absolute(shot) - _h.Door.NearFace.Origin;
                if (shot.InstanceId == 0UL && before.Z + step >= Portal.CrossDepth) stepsBehindTheFace++; // this step would end in the street's hill, behind the face
                shot.transform.position += shot.Motion.Velocity * dt;
                _h.Tick();
                if (shot.InstanceId != 0UL && float.IsNaN(committedAt)) committedAt = (float)(Absolute(shot) - _h.Door.NearFace.Origin).Z;
            }
            Assert.That(shot.InstanceId, Is.EqualTo(PortalHarness.Private), "it went through");
            Assert.That(stepsBehindTheFace, Is.EqualTo(0), "it never began a step in the near scene that would carry it beyond the plane");
            Assert.That(_crossed.Count, Is.EqualTo(1));
            Assert.That(committedAt, Is.LessThan(0f).And.GreaterThan(-step), "committed one step short of the plane: the step that carried it through ran in the far scene");
            Assert.That(shot.Motion.Velocity.z, Is.EqualTo(300f).Within(1e-3f));
        }

        [Test]
        public void ASlowBodyIsNotCommittedEarlyByThePrediction()
        {
            _h.OpenDoor();
            _h.Tick();
            var crate = Crate(0f, -1f);
            crate.Motion.Velocity = new Vector3(0f, 0f, 1.5f); // 2.5 cm a step: less than the depth
            _h.Tick();
            crate.transform.position = PortalHarness.At(0f, -0.04f);
            _h.Tick();
            Assert.That(crate.InstanceId, Is.EqualTo(0UL), "still short of the plane: the swept test decides, not the prediction");
            crate.transform.position = PortalHarness.At(0f, 0.04f);
            _h.Tick();
            Assert.That(crate.InstanceId, Is.EqualTo(0UL), "past the plane by less than the depth: the hysteresis");
            crate.transform.position = PortalHarness.At(0f, 0.06f);
            _h.Tick();
            Assert.That(crate.InstanceId, Is.EqualTo(PortalHarness.Private));
        }

        // ------------------------------------------------------------------------------------------ a carrier and its riders

        [Test]
        public void ACarrierWithTwoRidersCrossesWithThemSeated()
        {
            _h.OpenDoor();
            var ship = _h.W.SpawnServerDriven(_shipPrefab, _h.Street, PortalHarness.At(0f, -3f), Quaternion.identity);
            var hold = ship.Carried;
            Assume.That(hold, Is.Not.Null);
            var first = _h.W.SpawnServerDriven(_h.PawnPrefab, hold, ship.transform.position + new Vector3(0.5f, 0.2f, 0.5f), Quaternion.identity);
            var second = _h.W.SpawnServerDriven(_h.PawnPrefab, hold, ship.transform.position + new Vector3(-0.5f, 0.2f, -0.5f), Quaternion.identity);
            Assert.That(first.Container, Is.SameAs(hold), "seated");
            _h.Ticks(2);
            var seatFirst = first.LocalPosition;
            var seatSecond = second.LocalPosition;
            uint epochFirst = first.Epoch;

            ship.transform.position = PortalHarness.At(0f, 1f); // the riders go with it
            _h.Tick();

            Assert.That(ship.Container, Is.SameAs(_h.Hall), "the carrier crossed");
            Assert.That(first.Container, Is.SameAs(hold), "and the riders are still in their seats");
            Assert.That(second.Container, Is.SameAs(hold));
            Assert.That(first.InstanceId, Is.EqualTo(PortalHarness.Private), "in the room, through the ship they ride in");
            Assert.That(second.InstanceId, Is.EqualTo(PortalHarness.Private));
            Assert.That((first.LocalPosition - seatFirst).magnitude, Is.LessThan(1e-4f), "exactly where they sat");
            Assert.That((second.LocalPosition - seatSecond).magnitude, Is.LessThan(1e-4f));
            Assert.That(first.Epoch, Is.EqualTo(epochFirst + 1), "a new epoch, so every copy takes the next state as a location");
            Assert.That(_crossed.Count, Is.EqualTo(3), "the ship and its two riders");
            Assert.That(_h.Hall.Entities, Does.Contain(ship));
            // The riders did not cross on their own on the way: they are aboard.
            Assert.That(_crossed.FindAll(c => c.entity == first || c.entity == second).Count, Is.EqualTo(2));

            // The way out takes the whole crew again.
            _h.Tick();
            ship.transform.position = PortalHarness.At(0f, -0.3f);
            _h.Tick();
            Assert.That(ship.Container, Is.SameAs(_h.Street));
            Assert.That(first.Container, Is.SameAs(hold));
            Assert.That(first.InstanceId, Is.EqualTo(0UL));
        }

        [Test]
        public void ACarrierIsRefusedWhenAnyoneAboardIsNotAdmitted()
        {
            var link = _h.OpenDoor();
            var ship = _h.W.SpawnServerDriven(_shipPrefab, _h.Street, PortalHarness.At(0f, -3f), Quaternion.identity);
            var rider = _h.W.SpawnServerDriven(_h.PawnPrefab, ship.Carried, ship.transform.position + new Vector3(0f, 0.2f, 0f), Quaternion.identity);
            rider.OwnerClientId = 99; // a client that is not a member of the link: it has not been readied for the room
            rider.OwnerIdentity = "stranger";
            _h.Ticks(2);
            ship.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(ship.Container, Is.SameAs(_h.Street), "nobody is carried into a scope their client has not been readied for");
            Assert.That(rider.Container, Is.SameAs(ship.Carried));
            Assert.That(_refused.Count, Is.EqualTo(1));
            Assert.That(_refused[0].entity, Is.SameAs(ship));
            Assert.That(_refused[0].why, Is.EqualTo(PortalRefusal.NotAdmitted));
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        // ------------------------------------------------------------------------------------------ refusal

        [Test]
        public void ARefusedCrateIsStoppedShortOfThePlaneAndRefusedIsRaised()
        {
            _h.Ticks(2); // the door was never opened
            var crate = Crate(0.5f, -1f);
            crate.Motion.Velocity = new Vector3(1f, 0.5f, 4f);
            _h.Tick();
            int refusals = NebulaDiagnostics.PortalRefusals;
            crate.transform.position = PortalHarness.At(0.5f, 0.5f);
            _h.Tick();

            Assert.That(crate.InstanceId, Is.EqualTo(0UL), "it did not cross");
            Assert.That(crate.transform.position.z, Is.EqualTo(PortalHarness.At(0f, -Portal.CrossDepth).z).Within(1e-4f), "stopped CrossDepth short of the plane");
            Assert.That(crate.transform.position.x, Is.EqualTo(PortalHarness.At(0.5f, 0f).x).Within(1e-3f), "where its path met it");
            Assert.That(crate.Motion.Velocity.z, Is.EqualTo(0f).Within(1e-5f), "the part of the velocity that pointed through is gone");
            Assert.That(crate.Motion.Velocity.x, Is.EqualTo(1f).Within(1e-5f), "and nothing else of it");
            Assert.That(NebulaDiagnostics.PortalRefusals, Is.EqualTo(refusals + 1));
            Assert.That(_refused, Is.EqualTo(new[] { (crate, (PortalLink)null, PortalRefusal.LinkNotOpen) }));
        }

        [Test]
        public void ADynamicBodyThatIsRefusedIsStoppedLikeAnyMover()
        {
            _h.Ticks(2);
            var crate = _h.W.SpawnServerDriven(_crateBodyPrefab, _h.Street, PortalHarness.At(0f, -1f), Quaternion.identity);
            var body = crate.GetComponent<Rigidbody>();
            body.linearVelocity = new Vector3(0f, 0f, 6f);
            _h.Tick();
            crate.transform.position = PortalHarness.At(0f, 0.5f);
            _h.Tick();
            Assert.That(crate.InstanceId, Is.EqualTo(0UL));
            Assert.That(crate.transform.position.z, Is.EqualTo(PortalHarness.At(0f, -Portal.CrossDepth).z).Within(1e-4f));
            Assert.That(body.linearVelocity.z, Is.EqualTo(0f).Within(1e-5f), "its body's velocity too: the collider that would have stopped it is NEB-432's");
            Assert.That(_refused.Count, Is.EqualTo(1));
        }

        [Test]
        public void AnEntityWithNoPartyAtAFaceWithTwoOpenLinksIsRefusedAsAmbiguous()
        {
            _h.OpenDoor(party: "player-a");
            var b = _h.Door.Open("player-b", new[] { PortalHarness.Client });
            _h.Ticks(3);
            _h.Warm(b);
            _h.Tick();
            Assert.That(b.State, Is.EqualTo(PortalState.Open));
            var crate = Crate(0f, -1f);
            _h.Tick();
            crate.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(crate.InstanceId, Is.EqualTo(0UL), "nothing says whose it is");
            Assert.That(_refused, Is.EqualTo(new[] { (crate, (PortalLink)null, PortalRefusal.AmbiguousParty) }));

            // The game says whose it is, and it goes into that party's room.
            _h.Door.PartyOf = entity => entity == crate ? "player-b" : null;
            crate.transform.position = PortalHarness.At(0f, -1f);
            _h.Tick();
            crate.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(crate.InstanceId, Is.EqualTo(b.FarScope));
        }

        [Test]
        public void CanEnterRefusesAnUnownedEntityWithTheLinkItWasRefusedBy()
        {
            var link = _h.OpenDoor();
            _h.Door.CanEnter = (entity, l) => false;
            var crate = Crate(0f, -1f);
            _h.Tick();
            crate.transform.position = PortalHarness.At(0f, 1f);
            _h.Tick();
            Assert.That(crate.InstanceId, Is.EqualTo(0UL));
            Assert.That(_refused, Is.EqualTo(new[] { (crate, link, PortalRefusal.NotAdmitted) }));
        }

        [Test]
        public void AnEntityThatKeepsPushingIsRefusedOnEveryTickItReachesTheAperture()
        {
            _h.Ticks(2);
            var crate = Crate(0f, -1f);
            _h.Tick();
            for (int i = 0; i < 3; i++)
            {
                crate.transform.position = PortalHarness.At(0f, 0.5f);
                _h.Tick();
            }
            Assert.That(_refused.Count, Is.EqualTo(3));
            Assert.That(crate.transform.position.z, Is.EqualTo(PortalHarness.At(0f, -Portal.CrossDepth).z).Within(1e-4f));
        }

        // ------------------------------------------------------------------------------------------ what is asked

        [Test]
        public void APortalIsAskedOnlyAboutEntitiesOnItsContainerChain()
        {
            // Fifty doors in a second container; a hundred crates in the street with one door of its own.
            var yard = _h.Mesh.AddStaticContainer("yard", new Vector3(1000f, 0f, 0f), new Vector3(100f, 20f, 100f));
            _h.Mesh.SetOwner(yard, _h.W);
            for (int i = 0; i < 50; i++) _h.NewPortal("yard-door-" + i, new Vector3(1000f + i, 1f, 0f));
            var crates = new List<NetworkIdentity>();
            for (int i = 0; i < 100; i++) crates.Add(_h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, new Vector3(-100f + i, 1f, -100f), Quaternion.identity));
            _h.Ticks(2);
            int before = Portals.Asks;
            _h.Tick();
            int asks = Portals.Asks - before;
            Assert.That(asks, Is.LessThanOrEqualTo(crates.Count + 1), $"the street's one door is asked about each crate, never the yard's fifty ({asks} asks)");
            Assert.That(asks, Is.GreaterThanOrEqualTo(crates.Count));
        }

        [Test]
        public void ADoorBeyondAChunkSeamIsAskedAboutAnEntityOnTheOtherSide()
        {
            // The door stands in the street; the entity walks at it from the next container east, which is where its container still is when its root passes.
            var east = _h.Mesh.AddStaticContainer("east", new Vector3(300f, 0f, 0f), new Vector3(200f, 50f, 400f));
            _h.Mesh.SetOwner(east, _h.W);
            var seam = _h.NewPortal("seam-door", new Vector3(199f, 1f, 0f));
            seam.transform.rotation = Quaternion.Euler(0f, 270f, 0f); // facing -x, into the street
            var link = seam.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Ticks(3);
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), link.ToString());
            var crate = _h.W.SpawnServerDriven(_h.PawnPrefab, east, new Vector3(204f, 1f, 0f), Quaternion.identity);
            Assume.That(crate.Container, Is.SameAs(east));
            _h.Tick();
            crate.transform.position = new Vector3(198f, 1f, 0f); // through the plane x = 199, still in the east container's box until the resolver runs
            _h.Tick();
            Assert.That(crate.InstanceId, Is.EqualTo(link.FarScope), "the portal in the neighbouring container was asked");
        }
    }
}
