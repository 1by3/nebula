using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-433 (<c>docs/portals.md</c> D32, D33) where the far scope is not simply the next scene of the same worker: on a split link the near worker has only the
    /// band, so the far leg is tested against the ghost proxies there and a cast that leaves the band is <see cref="PortalHitFlags.Truncated"/>; and a second
    /// portal behind the first is met, not passed (one portal deep).
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalQueriesWorkersPlayTests
    {
        private PortalPhysicsRig _rig;
        private PortalHit[] _hits;
        private PortalHarness H => _rig.H;
        private PortalQueries Q => H.W.Instance.PortalQueries;
        private static PortalAsker PartyA => PortalAsker.OfParty(PortalHarness.Owner);

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            yield return null;
            yield return null;
        }

        private static Vector3 Street(float x, float y, float z) => new Vector3(20f + x, y, 20f + z);

        private void SplitRig()
        {
            _rig = new PortalPhysicsRig(2);
            H.HallOnSecondWorker = true;
            _hits = new PortalHit[16];
        }

        // ------------------------------------------------------------------------------------------ a split link

        [UnityTest]
        public IEnumerator OnASplitLinkTheFarLegIsTestedAgainstTheGhostProxiesInTheBand()
        {
            SplitRig();
            var link = _rig.OpenDoor();
            Assert.That(H.Hall.OwnerWorkerId, Is.EqualTo(H.Other.Id));
            var crate = H.Other.SpawnServerDriven(_rig.CratePrefab, H.Hall, Street(0f, 0.5f, 1.3f), Quaternion.identity);
            crate.GetComponent<BoxCollider>().sharedMaterial = PortalPhysicsRig.Slippery();
            _rig.Steps(120);
            var ghost = H.W.Find(crate.NetId);
            Assert.That(ghost, Is.Not.Null, "the near worker holds a ghost of a body in the far band");

            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var hit), Is.True);
            Assert.That(hit.Entity, Is.SameAs(ghost), "the ghost this worker holds of it");
            Assert.That(hit.Entity.NetId, Is.EqualTo(crate.NetId));
            Assert.That(hit.IsTruncated, Is.False);
            Assert.That(hit.Scope, Is.EqualTo(PortalHarness.Private), "in the hall's scope, though this worker holds only its proxy");
            Assert.That(hit.Flags, Is.EqualTo(PortalHitFlags.ThroughPortal));
            Assert.That(hit.LinkId, Is.EqualTo(link.Id));
            Assert.That(hit.Distance, Is.EqualTo(10.8f).Within(0.03f), "10 m of street, 0.8 m to the crate's face");
            Assert.That(hit.PointInScope.z, Is.EqualTo(20.8f).Within(0.03f));
            Assert.That(PortalProxy.Is(hit.Collider), Is.False, "the ghost's own collider is reported, never the proxy");
            yield break;
        }

        [UnityTest]
        public IEnumerator OnASplitLinkACastThatLeavesTheBandIsTruncated()
        {
            SplitRig();
            var link = _rig.OpenDoor();
            var far = H.Other.SpawnServerDriven(_rig.CratePrefab, H.Hall, Street(0f, 0.5f, 5f), Quaternion.identity);
            far.GetComponent<BoxCollider>().sharedMaterial = PortalPhysicsRig.Slippery();
            _rig.Steps(60);
            Assert.That(H.W.Find(far.NetId), Is.Null, "5 m into the hall is outside the band: nothing of it is here");

            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var hit), Is.True);
            Assert.That(hit.IsTruncated, Is.True, "the crate and the wall are beyond what this worker knows");
            Assert.That(hit.Flags, Is.EqualTo(PortalHitFlags.Truncated | PortalHitFlags.ThroughPortal));
            Assert.That(hit.Collider, Is.Null);
            Assert.That(hit.Entity, Is.Null);
            Assert.That(hit.Scope, Is.EqualTo(PortalHarness.Private));
            Assert.That(hit.LinkId, Is.EqualTo(link.Id));
            Assert.That(hit.Distance, Is.EqualTo(12f).Within(0.02f), "10 m of street and the band's 2 m");
            Assert.That(hit.PointInScope.z, Is.EqualTo(22f).Within(0.02f), "where it left the band, in the hall's space");

            // A cast that ends inside the band is a miss, not a truncation.
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 11f, out var shortOfIt), Is.False);
            Assert.That(shortOfIt.Flags, Is.EqualTo(PortalHitFlags.None));
            // A street hit comes before any of it.
            Assert.That(Q.Raycast(PartyA, 0UL, Street(-3f, 1f, -10f), Vector3.forward, 40f, out var street), Is.True);
            Assert.That(street.IsTruncated, Is.False);
            Assert.That(street.Scope, Is.EqualTo(0UL));

            // A sweep and an overlap say the same.
            Assert.That(Q.SweepSphere(PartyA, 0UL, Street(0f, 0.7f, -10f), 0.2f, Vector3.forward, 40f, out var sweep), Is.True);
            Assert.That(sweep.IsTruncated, Is.True);
            Assert.That(sweep.Distance, Is.EqualTo(12f).Within(0.02f));
            _hits = new PortalHit[16];
            int n = Q.OverlapSphere(PartyA, 0UL, Street(0f, 0.7f, 0f), 2.6f, _hits);
            bool truncated = false;
            for (int i = 0; i < n; i++) truncated |= _hits[i].IsTruncated;
            Assert.That(truncated, Is.True, "a shape that reaches past the band");
            yield break;
        }

        // ------------------------------------------------------------------------------------------ one portal deep

        [UnityTest]
        public IEnumerator AQueryIsOnePortalDeepAndStopsAtTheNextAperture()
        {
            _rig = new PortalPhysicsRig();
            _hits = new PortalHit[16];
            _rig.OpenDoor();
            // A second door in the hall, 3 m in, onto another room.
            var second = ScriptableObject.CreateInstance<InstanceTemplate>();
            second.TemplateId = "annex";
            second.Parts = new[] { new InstanceTemplate.Part { Id = "room", Bounds = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f)) } };
            var go = new GameObject("inner door");
            H.Objects.Add(go);
            go.transform.SetParent(H.Hall.transform, false);
            go.transform.SetPositionAndRotation(Street(0f, 3f, 3f), Quaternion.identity);
            var inner = go.AddComponent<Portal>();
            inner.Id = "inner";
            inner.FarTemplate = second;
            inner.Aperture = PortalAperture.Box(1f, 2f);
            inner.ClockForTests = () => H.Now;
            PortalHarness.Enable(inner);
            _rig.Steps(2);

            // The inner door is closed: a cast along the hall meets it on the plane, in the hall's scope, behind the first door.
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 3f, -10f), Vector3.forward, 40f, out var closed), Is.True);
            Assert.That(closed.IsAperture, Is.True);
            Assert.That(closed.Flags, Is.EqualTo(PortalHitFlags.Aperture | PortalHitFlags.ThroughPortal));
            Assert.That(closed.Scope, Is.EqualTo(PortalHarness.Private));
            Assert.That(closed.Distance, Is.EqualTo(13f).Within(0.02f));
            Assert.That(closed.Collider, Is.Not.Null);

            // Open it. The cast still does not go on into the annex: it stops at the plane and says why.
            H.OpenDoor(inner, PortalHarness.Owner);
            _rig.Steps(3);
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 3f, -10f), Vector3.forward, 40f, out var open), Is.True);
            Assert.That(open.Flags, Is.EqualTo(PortalHitFlags.Aperture | PortalHitFlags.ThroughPortal | PortalHitFlags.DepthLimit));
            Assert.That(open.Scope, Is.EqualTo(PortalHarness.Private), "the hall's, not the annex's");
            Assert.That(open.Distance, Is.EqualTo(13f).Within(0.02f));

            // Beside the inner door's opening the cast goes on to the hall's wall.
            Assert.That(Q.Raycast(PartyA, 0UL, Street(1.5f, 3f, -10f), Vector3.forward, 40f, out var beside), Is.True);
            Assert.That(beside.IsAperture, Is.False);
            Assert.That(beside.Collider.name, Is.EqualTo("hall wall"));
            PortalHarness.Disable(inner);
            Object.DestroyImmediate(second);
            yield break;
        }
    }

    /// <summary>
    /// NEB-433 (<c>docs/portals.md</c> D32) from the ground of a planet that is a carrier: the cast starts in the frame's own physics scene, in its simulation
    /// space, and goes through a door turned on the planet into an instance.
    /// </summary>
    [Category("Conformance")]
    public sealed class FramedPortalQueriesPlayTests
    {
        private FramedPortalRig _rig;

        [SetUp]
        public void SetUp() => _rig = new FramedPortalRig();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            for (int i = 0; i < 30 && PortalPhysicsRig.NebulaScenesLoaded(); i++) yield return null;
            yield return null;
        }

        private PortalQueries Q => _rig.W.Instance.PortalQueries;
        private static ulong PlanetScope => ScopeKeys.Hash(FramedPortalRig.Scope);

        [UnityTest]
        public IEnumerator ARayFromAFramedPlanetsGroundThroughItsDoorHitsWhereItWasAimed()
        {
            var link = _rig.OpenDoor();
            _rig.Steps(2);
            var options = new PortalQueryOptions { Space = _rig.Planet.Carried };
            var asker = PortalAsker.OfParty(FramedPortalRig.Owner);
            var origin = _rig.At(0.7f, FramedPortalRig.FaceHeight + 0.4f, -6f);
            Assert.That(Q.Raycast(asker, PlanetScope, origin, FramedPortalRig.Forward, 40f, out var hit, options), Is.True);
            Assert.That(hit.Scope, Is.EqualTo(FramedPortalRig.Private));
            Assert.That(hit.Flags, Is.EqualTo(PortalHitFlags.ThroughPortal));
            Assert.That(hit.Collider.name, Is.EqualTo("hall wall"));
            Assert.That(hit.Distance, Is.EqualTo(14f).Within(0.02f), "6 m of ground and 8 m of vault");
            var face = ContainerRegistry.ToFrame(link.FarFace.Origin, link.FarScope);
            Assert.That(Vector3.Distance(hit.PointInScope, face + link.FarFace.Rotation * new Vector3(0.7f, 0.4f, 8f)), Is.LessThan(0.01f), "on the wall, in the vault's space");
            Assert.That(Vector3.Distance(hit.PointInAskerSpace, _rig.At(0.7f, FramedPortalRig.FaceHeight + 0.4f, 8f)), Is.LessThan(0.01f), "and the straight continuation in the planet's frame");
            Assert.That(hit.PointInAskerSpace.magnitude, Is.LessThan(4096f), "small numbers: face-relative, 12,000 km from the scope's origin");

            // A crate of the vault is hit the same way, and from the other side a ray out of the vault reaches the ground.
            var crate = _rig.HallCrate(0f, 0.5f, 4f, FramedPortalRig.Slippery());
            _rig.Steps(30);
            Assert.That(Q.Raycast(asker, PlanetScope, _rig.At(0f, 0.5f, -6f), FramedPortalRig.Forward, 40f, out var crateHit, options), Is.True);
            Assert.That(crateHit.Entity, Is.SameAs(crate));
            Assert.That(crateHit.Distance, Is.EqualTo(9.5f).Within(0.03f));

            var ground = _rig.Crate(0f, 0.5f, -3f, FramedPortalRig.Slippery());
            _rig.Steps(30);
            var inVault = face + link.FarFace.Rotation * new Vector3(0f, 0.5f - FramedPortalRig.FaceHeight, 3f);
            Assert.That(Q.Raycast(asker, link.FarScope, inVault, link.FarFace.Rotation * Vector3.back, 20f, out var leaving), Is.True);
            Assert.That(leaving.Entity, Is.SameAs(ground), "out of the vault and onto the planet's ground");
            Assert.That(leaving.Scope, Is.EqualTo(PlanetScope));
            Assert.That(leaving.Flags, Is.EqualTo(PortalHitFlags.ThroughPortal));
            Assert.That(leaving.Distance, Is.EqualTo(5.5f).Within(0.03f), "3 m of vault and 2.5 m of ground");
            Assert.That(Vector3.Distance(leaving.PointInScope, ground.transform.position + FramedPortalRig.DoorTurn * new Vector3(0f, 0f, 0.5f)), Is.LessThan(0.03f), "in the planet frame's simulation space");
            yield break;
        }

        [UnityTest]
        public IEnumerator AClosedFramedDoorIsHitOnItsPlane()
        {
            _rig.Steps(3);
            var options = new PortalQueryOptions { Space = _rig.Planet.Carried };
            Assert.That(Q.Raycast(PortalAsker.OfParty(FramedPortalRig.Owner), PlanetScope, _rig.At(0f, FramedPortalRig.FaceHeight, -6f), FramedPortalRig.Forward, 40f, out var hit, options), Is.True);
            Assert.That(hit.IsAperture, Is.True);
            Assert.That(hit.Scope, Is.EqualTo(PlanetScope));
            Assert.That(hit.Distance, Is.EqualTo(6f).Within(0.01f));
            Assert.That(Vector3.Distance(hit.PointInScope, _rig.At(0f, FramedPortalRig.FaceHeight, 0f)), Is.LessThan(0.01f));
            yield break;
        }
    }
}
