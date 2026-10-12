using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-433 (<c>docs/portals.md</c> D32, D33) in play mode: raycasts, sweeps and overlaps that continue through an open portal, on the street, the door and the hall
    /// of <see cref="PortalPhysicsRig"/> with real PhysX. A real worker on the in-process mesh; the test steps the street's scene once to each worker tick.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalQueriesPlayTests
    {
        private PortalPhysicsRig _rig;
        private int _history;
        private PortalHit[] _hits;
        private readonly PortalQueryOptions _options = new PortalQueryOptions();

        private PortalHarness H => _rig.H;
        private PortalQueries Q => H.W.Instance.PortalQueries;
        private static PortalAsker PartyA => PortalAsker.OfParty(PortalHarness.Owner);

        [SetUp]
        public void SetUp()
        {
            _history = StateHistory.WindowTicks;
            _rig = new PortalPhysicsRig();
            StateHistory.WindowTicks = 300; // the mesh has no Initialize(), which is what reads the config knob
            _hits = new PortalHit[16];
            _options.Tick = 0;
            _options.RewindNear = false;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            StateHistory.WindowTicks = _history;
            yield return null;
            yield return null;
        }

        private static Vector3 Street(float x, float y, float z) => new Vector3(20f + x, y, 20f + z);

        private NetworkIdentity HallCrate(Vector3 at, bool kinematic = false, Container hall = null)
        {
            var crate = H.W.SpawnServerDriven(_rig.CratePrefab, hall ?? H.Hall, at, Quaternion.identity);
            crate.GetComponent<BoxCollider>().sharedMaterial = PortalPhysicsRig.Slippery();
            if (kinematic) crate.GetComponent<Rigidbody>().isKinematic = true;
            return crate;
        }

        private int Entities(int n)
        {
            int count = 0;
            for (int i = 0; i < n; i++) if (_hits[i].Entity != null) count++;
            return count;
        }

        // ------------------------------------------------------------------------------------------ through an open aperture

        [UnityTest]
        public IEnumerator ARayThroughAnOpenApertureHitsAnEntityOfTheFarScopeAndSaysWhereInBothSpaces()
        {
            var link = _rig.OpenDoor();
            var crate = HallCrate(Street(0f, 0.5f, 4f));
            _rig.Steps(20);

            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var hit), Is.True);
            Assert.That(hit.Entity, Is.SameAs(crate));
            Assert.That(hit.Scope, Is.EqualTo(PortalHarness.Private), "the hall's scope, not the street's");
            Assert.That(hit.Flags, Is.EqualTo(PortalHitFlags.ThroughPortal));
            Assert.That(hit.LinkId, Is.EqualTo(link.Id));
            Assert.That(hit.Collider, Is.SameAs(crate.GetComponent<BoxCollider>()));
            Assert.That(hit.Distance, Is.EqualTo(13.5f).Within(0.02f), "10 m of street, 3.5 m of hall");
            Assert.That(hit.PointInScope.z, Is.EqualTo(23.5f).Within(0.01f));
            Assert.That(hit.PointInAskerSpace.z, Is.EqualTo(23.5f).Within(0.01f), "a coincident link: the straight continuation lands in the same place");
            Assert.That(hit.Normal, Is.EqualTo(Vector3.back).Using(new Vector3EqualityComparer(0.01f)));
            yield break;
        }

        [UnityTest]
        public IEnumerator ARayThatHitsTheStreetFirstIsNotCarriedOn()
        {
            _rig.OpenDoor();
            HallCrate(Street(0f, 0.5f, 4f));
            _rig.Steps(10);
            // Left of the doorway, the hill's front wall stands at the face.
            Assert.That(Q.Raycast(PartyA, 0UL, Street(-3f, 1f, -10f), Vector3.forward, 40f, out var hit), Is.True);
            Assert.That(hit.Scope, Is.EqualTo(0UL));
            Assert.That(hit.Flags, Is.EqualTo(PortalHitFlags.None));
            Assert.That(hit.Collider.name, Is.EqualTo("hill left"));
            Assert.That(hit.Distance, Is.EqualTo(10f).Within(0.02f));
            yield break;
        }

        [UnityTest]
        public IEnumerator ARayStopsWhereItsLengthRunsOutBeforeTheFace()
        {
            _rig.OpenDoor();
            HallCrate(Street(0f, 0.5f, 4f));
            _rig.Steps(10);
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 9f, out _), Is.False);
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 11f, out var hit), Is.False, "past the face but short of the crate: nothing");
            Assert.That(hit.Collider, Is.Null);
            yield break;
        }

        [UnityTest]
        public IEnumerator RaycastAllReturnsTheStreetAndTheHallInOrderAndStopsAtAClosedAperture()
        {
            _rig.OpenDoor();
            var street = _rig.Crate(Street(0f, 0.5f, -3f), PortalPhysicsRig.Slippery());
            var hall = HallCrate(Street(0f, 0.5f, 4f));
            _rig.Steps(30);
            int n = Q.RaycastAll(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, _hits);
            Assert.That(n, Is.EqualTo(3), "the street's crate, the hall's crate and the hall's back wall");
            Assert.That(_hits[0].Entity, Is.SameAs(street));
            Assert.That(_hits[0].Flags, Is.EqualTo(PortalHitFlags.None));
            Assert.That(_hits[1].Entity, Is.SameAs(hall));
            Assert.That(_hits[1].IsThroughPortal, Is.True);
            Assert.That(_hits[2].Collider.name, Is.EqualTo("hall wall"));
            Assert.That(_hits[0].Distance, Is.LessThan(_hits[1].Distance));
            Assert.That(_hits[1].Distance, Is.LessThan(_hits[2].Distance));
            yield break;
        }

        [UnityTest]
        public IEnumerator TheAskersOwnColliderIsNotAHit()
        {
            _rig.OpenDoor();
            var asker = _rig.Crate(Street(0f, 0.5f, -6f), PortalPhysicsRig.Slippery());
            var hall = HallCrate(Street(0f, 0.5f, 4f));
            _rig.Steps(30);
            Assert.That(Q.Raycast(PortalAsker.OfParty(PortalHarness.Owner, asker), 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var hit), Is.True);
            Assert.That(hit.Entity, Is.SameAs(hall), "past its own crate, which it stands behind");
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var blocked), Is.True);
            Assert.That(blocked.Entity, Is.SameAs(asker), "anyone else is stopped by it");
            yield break;
        }

        // ------------------------------------------------------------------------------------------ closed

        [UnityTest]
        public IEnumerator AClosedApertureIsHitOnThePlaneAndFlaggedAsAnAperture()
        {
            _rig.Steps(2);
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0.5f, 1f, -10f), Vector3.forward, 40f, out var hit), Is.True, "a portal nobody opened is solid");
            Assert.That(hit.IsAperture, Is.True);
            Assert.That(hit.Flags, Is.EqualTo(PortalHitFlags.Aperture));
            Assert.That(hit.Entity, Is.Null);
            Assert.That(hit.Scope, Is.EqualTo(0UL));
            Assert.That(hit.Distance, Is.EqualTo(10f).Within(0.001f));
            Assert.That(hit.PointInScope.z, Is.EqualTo(20f).Within(0.001f));
            Assert.That(hit.PointInScope.x, Is.EqualTo(20.5f).Within(0.001f));
            Assert.That(hit.Normal, Is.EqualTo(Vector3.back).Using(new Vector3EqualityComparer(0.001f)), "facing the ray");
            Assert.That(PortalProxy.Of(hit.Collider).IsAperture, Is.True);
            yield break;
        }

        [UnityTest]
        public IEnumerator AClosingLinkAndAnObservationOnlyPortalAreSolidToo()
        {
            var link = _rig.OpenDoor();
            _rig.Steps(2);
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
            H.Door.ObservationOnly = true;
            _rig.Steps(2);
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var hit), Is.True);
            Assert.That(hit.IsAperture, Is.True, "an observation-only portal admits nothing: it is a plane");
            Assert.That(hit.Flags & PortalHitFlags.DepthLimit, Is.EqualTo(PortalHitFlags.None));
            H.Door.ObservationOnly = false;
            Assert.That(H.Door.Close(PortalHarness.Owner), Is.True);
            _rig.Steps(2);
            Assert.That(link.State, Is.Not.EqualTo(PortalState.Open), "closing or closed");
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var closing), Is.True);
            Assert.That(closing.IsAperture, Is.True);
            yield break;
        }

        // ------------------------------------------------------------------------------------------ proxies

        [UnityTest]
        public IEnumerator AProxyIsNeverAHit()
        {
            _rig.OpenDoor();
            var crate = _rig.Crate(Street(0f, 0.5f, -0.02f), PortalPhysicsRig.Slippery());
            _rig.Steps(40);
            var proxies = PortalPhysicsRig.ProxiesOf(crate);
            Assert.That(proxies.Count, Is.EqualTo(1), "the crate across the face has a proxy in the hall");
            // Raw physics sees the proxy in the hall's scene: from 6 m into the hall back toward the face.
            var hallPhysics = _rig.HallScene.GetPhysicsScene();
            Assert.That(hallPhysics.Raycast(Street(0f, 0.5f, 6f), Vector3.back, out var raw, 10f), Is.True);
            Assert.That(PortalProxy.Is(raw.collider), Is.True, "the setup: a plain cast hits the copy");

            // A query from the hall does not.
            int n = Q.RaycastAll(PortalAsker.OfParty(PortalHarness.Owner), PortalHarness.Private, Street(0f, 0.5f, 6f), Vector3.back, 12f, _hits);
            for (int i = 0; i < n; i++) Assert.That(PortalProxy.Is(_hits[i].Collider) && !_hits[i].IsAperture, Is.False, "hit " + i + ": " + _hits[i]);
            // Even with every layer asked for.
            _options.LayerMask = -1;
            n = Q.RaycastAll(PortalAsker.OfParty(PortalHarness.Owner), PortalHarness.Private, Street(0f, 0.5f, 6f), Vector3.back, 12f, _hits, _options);
            for (int i = 0; i < n; i++) Assert.That(PortalProxy.Is(_hits[i].Collider) && !_hits[i].IsAperture, Is.False, "hit " + i + ": " + _hits[i]);
            yield break;
        }

        // ------------------------------------------------------------------------------------------ parties

        [UnityTest]
        public IEnumerator AFaceWithTwoPartiesLinksCastsIntoTheAskersPartysScope()
        {
            _rig.OpenDoor();
            var crateA = HallCrate(Street(0f, 0.5f, 4f));
            // A second party opens the same door onto its own hall.
            var linkB = H.Door.Open("player-b", new[] { 8UL });
            _rig.Step();
            var hallB = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/player-b/hall"));
            Assert.That(hallB, Is.Not.Null);
            _rig.Steps(3);
            H.Warm(linkB, 8UL);
            _rig.Step();
            Assert.That(linkB.State, Is.EqualTo(PortalState.Open));
            var crateB = HallCrate(Street(0f, 0.5f, 3f), kinematic: true, hall: hallB);
            _rig.Steps(20);
            ulong scopeB = NebulaWorker.InstanceKey("vault/player-b");

            Assert.That(Q.Raycast(PortalAsker.OfParty("player-a"), 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var a), Is.True);
            Assert.That(a.Entity, Is.SameAs(crateA));
            Assert.That(a.Scope, Is.EqualTo(PortalHarness.Private));
            Assert.That(Q.Raycast(PortalAsker.OfParty("player-b"), 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var b), Is.True);
            Assert.That(b.Entity, Is.SameAs(crateB));
            Assert.That(b.Scope, Is.EqualTo(scopeB), "the other party's hall");
            Assert.That(b.LinkId, Is.EqualTo(linkB.Id));
            Assert.That(b.Distance, Is.EqualTo(12.5f).Within(0.02f));

            // Nobody named, two links open: the cast does not choose for the game (D12). It is the plane.
            Assert.That(Q.Raycast(PortalAsker.None, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var nobody), Is.True);
            Assert.That(nobody.IsAperture, Is.True);

            // An entity asks by its owner's identity, as a crossing resolves its party.
            var asker = _rig.Crate(Street(6f, 0.5f, -6f), PortalPhysicsRig.Slippery(), owner: 8UL);
            asker.OwnerIdentity = "player-b";
            Assert.That(Q.Raycast(PortalAsker.Of(asker), 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var byEntity), Is.True);
            Assert.That(byEntity.Entity, Is.SameAs(crateB));
            // A party with no link on the face meets the plane.
            Assert.That(Q.Raycast(PortalAsker.OfParty("player-c"), 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var stranger), Is.True);
            Assert.That(stranger.IsAperture, Is.True);
            yield break;
        }

        // ------------------------------------------------------------------------------------------ a turned door

        [UnityTest]
        public IEnumerator ARayThroughATurnedDoorHitsWhereItWasAimed()
        {
            _rig.Steps(2);
            var origin = new Vector3(60f, 2.5f, 20f);
            var turn = Quaternion.Euler(0f, 35f, 0f);
            var go = new GameObject("door2");
            H.Objects.Add(go);
            go.transform.SetPositionAndRotation(origin, turn);
            var door = go.AddComponent<Portal>();
            door.Id = "door2";
            door.FarTemplate = H.Template;
            door.Aperture = PortalAperture.Box(2f, 2.5f);
            door.ClockForTests = () => H.Now;
            PortalHarness.Enable(door);

            var link = door.Open("player-b", new[] { 8UL });
            _rig.Step();
            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/player-b/hall"));
            Assert.That(hall, Is.Not.Null);
            var face = ContainerRegistry.ToFrame(link.FarFace.Origin, link.FarScope);
            var upright = link.FarFace.Rotation;
            var wall = _rig.Box("hall b wall", face + upright * new Vector3(0f, 0f, 8.5f), new Vector3(8f, 5f, 1f), hall.transform);
            wall.transform.rotation = upright;
            _rig.Steps(3);
            H.Warm(link, 8UL);
            _rig.Step();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), link.ToString());
            Assert.That(Quaternion.Angle(link.Turn, turn), Is.LessThan(0.01f), "the link is a pure turn about the door");

            // Aimed 0.4 m to the left and 0.7 m above the centre of the doorway, along the door's own axis, from 6 m out.
            var local = new Vector3(-0.4f, 0.7f, -6f);
            var from = origin + turn * local;
            Assert.That(Q.Raycast(PortalAsker.OfParty("player-b"), 0UL, from, turn * Vector3.forward, 40f, out var hit), Is.True);
            Assert.That(hit.Scope, Is.EqualTo(NebulaWorker.InstanceKey("vault/player-b")));
            Assert.That(hit.Collider.gameObject, Is.SameAs(wall));
            Assert.That(hit.Distance, Is.EqualTo(14f).Within(0.02f), "6 m of street, 8 m of hall");
            var expectedInHall = face + upright * new Vector3(-0.4f, 0.7f, 8f);
            Assert.That(Vector3.Distance(hit.PointInScope, expectedInHall), Is.LessThan(0.01f), "on the wall, where the ray was aimed, in the hall's upright space");
            var expectedInStreet = origin + turn * new Vector3(-0.4f, 0.7f, 8f);
            Assert.That(Vector3.Distance(hit.PointInAskerSpace, expectedInStreet), Is.LessThan(0.01f), "and the continuation in the street's, turned with the door");
            Assert.That(Vector3.Dot(hit.Normal, Vector3.back), Is.GreaterThan(0.999f));
            Assert.That(Vector3.Dot(hit.NormalInAskerSpace, turn * Vector3.back), Is.GreaterThan(0.999f), "the normal turned with the door");

            // A ray that is turned against the door's axis but still passes through the opening is carried the same way.
            var oblique = (turn * new Vector3(0.1f, 0.05f, 1f)).normalized;
            var start = origin + turn * new Vector3(-0.4f, 0.2f, -4f);
            Assert.That(Q.Raycast(PortalAsker.OfParty("player-b"), 0UL, start, oblique, 40f, out var angled), Is.True);
            float expectedLength = 12f / Vector3.Dot(oblique, turn * Vector3.forward); // 4 m in front of the door, 8 m of the wall's depth along the door's axis
            Assert.That(angled.Distance, Is.EqualTo(expectedLength).Within(0.03f));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ overlaps

        [UnityTest]
        public IEnumerator AnOverlapStraddlingTheFaceFindsBothSidesAndTheClosedPlaneWhenTheLinkIsNotOpen()
        {
            _rig.OpenDoor();
            var street = _rig.Crate(Street(0f, 0.5f, -1.5f), PortalPhysicsRig.Slippery());
            var hall = HallCrate(Street(0f, 0.5f, 1.5f));
            _rig.Steps(30);
            var centre = Street(0f, 0.5f, 0f);
            int n = Q.OverlapSphere(PartyA, 0UL, centre, 1.5f, _hits);
            Assert.That(Entities(n), Is.EqualTo(2), "two crates (and the floors of both scopes under them)");
            Assert.That(n, Is.GreaterThan(2));
            NetworkIdentity farEntity = null, nearEntity = null;
            for (int i = 0; i < n; i++)
            {
                if (_hits[i].Entity == hall) { farEntity = hall; Assert.That(_hits[i].Scope, Is.EqualTo(PortalHarness.Private)); Assert.That(_hits[i].IsThroughPortal, Is.True); }
                if (_hits[i].Entity == street) { nearEntity = street; Assert.That(_hits[i].Scope, Is.EqualTo(0UL)); Assert.That(_hits[i].IsThroughPortal, Is.False); }
            }
            Assert.That(farEntity, Is.SameAs(hall), "the part of the sphere beyond the face reaches the crate of the hall");
            Assert.That(nearEntity, Is.SameAs(street));

            // A sphere that stays on the street side does not reach into the hall.
            Assert.That(Entities(Q.OverlapSphere(PartyA, 0UL, Street(0f, 0.5f, -1f), 0.9f, _hits)), Is.EqualTo(1));
            // A capsule and a box, across the face.
            Assert.That(Entities(Q.OverlapCapsule(PartyA, 0UL, Street(0f, 0.5f, -1.5f), Street(0f, 0.5f, 1.5f), 0.3f, _hits)), Is.EqualTo(2));
            Assert.That(Entities(Q.OverlapBox(PartyA, 0UL, centre, new Vector3(0.5f, 0.5f, 1.5f), Quaternion.identity, _hits)), Is.EqualTo(2));
            // Beside the doorway the sphere reaches the hall's side of the plane, but outside the opening there is only the hill.
            Assert.That(Q.OverlapSphere(PartyA, 0UL, Street(3.4f, 0.5f, 0f), 0.5f, _hits), Is.GreaterThanOrEqualTo(0));

            // With the link not open the straddling shape meets the closed plane and nothing of the hall.
            H.Door.Close(PortalHarness.Owner);
            _rig.Steps(3);
            n = Q.OverlapSphere(PartyA, 0UL, centre, 1.5f, _hits);
            bool plane = false;
            for (int i = 0; i < n; i++)
            {
                Assert.That(_hits[i].Entity, Is.Not.SameAs(hall));
                if (_hits[i].IsAperture) plane = true;
            }
            Assert.That(plane, Is.True, "a closed aperture the shape straddles is a hit on the plane");
            yield break;
        }

        [UnityTest]
        public IEnumerator ASweepAnOpenApertureCarriesTheShapeThroughAndAClosedOneStopsIt()
        {
            _rig.OpenDoor();
            var crate = HallCrate(Street(0f, 0.5f, 4f));
            _rig.Steps(20);
            var from = Street(0f, 0.7f, -10f);
            Assert.That(Q.SweepSphere(PartyA, 0UL, from, 0.3f, Vector3.forward, 40f, out var sphere), Is.True);
            Assert.That(sphere.Entity, Is.SameAs(crate));
            Assert.That(sphere.Distance, Is.EqualTo(13.2f).Within(0.03f), "the crate's face at 23.5, less the sphere's radius");
            Assert.That(Q.SweepCapsule(PartyA, 0UL, from + Vector3.down * 0.2f, from + Vector3.up * 0.3f, 0.25f, Vector3.forward, 40f, out var capsule), Is.True);
            Assert.That(capsule.Entity, Is.SameAs(crate));
            Assert.That(Q.SweepBox(PartyA, 0UL, from, new Vector3(0.3f, 0.3f, 0.3f), Quaternion.identity, Vector3.forward, 40f, out var box), Is.True);
            Assert.That(box.Entity, Is.SameAs(crate));
            Assert.That(box.Distance, Is.EqualTo(13.2f).Within(0.03f));
            // Too wide for the doorway: the street's hill stops it at the face.
            Assert.That(Q.SweepSphere(PartyA, 0UL, from, 2.2f, Vector3.forward, 40f, out var wide), Is.True);
            Assert.That(wide.Scope, Is.EqualTo(0UL), "it never fits through");
            // Aimed at the hill beside the doorway, the centre's path misses the opening.
            Assert.That(Q.SweepSphere(PartyA, 0UL, Street(-3f, 1f, -10f), 0.3f, Vector3.forward, 40f, out var side), Is.True);
            Assert.That(side.Collider.name, Is.EqualTo("hill left"));

            H.Door.Close(PortalHarness.Owner);
            _rig.Steps(3);
            Assert.That(Q.SweepSphere(PartyA, 0UL, from, 0.3f, Vector3.forward, 40f, out var closed), Is.True);
            Assert.That(closed.IsAperture, Is.True);
            yield break;
        }
    }
}
