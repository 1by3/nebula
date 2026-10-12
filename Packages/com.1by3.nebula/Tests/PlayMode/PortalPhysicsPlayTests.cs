using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-432 (<c>docs/portals.md</c> D26 to D28) in play mode, where physics scenes exist: a body in a band is mirrored into the other scope's scene as a kinematic
    /// proxy, each side's static geometry stands in the other's throat, and a thin collider over the cross-section is solid for everything the link does not admit.
    /// A real <see cref="NebulaWorker"/> on the in-process mesh, real scenes and real PhysX; the test steps the street's scene once to each worker tick.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalPhysicsPlayTests
    {
        private PortalPhysicsRig _rig;
        private readonly List<(NetworkIdentity entity, PortalLink link, PortalRefusal why)> _refused = new List<(NetworkIdentity, PortalLink, PortalRefusal)>();

        private PortalHarness H => _rig.H;

        [SetUp]
        public void SetUp()
        {
            _rig = new PortalPhysicsRig();
            _refused.Clear();
            H.Door.Refused += (entity, link, why) => _refused.Add((entity, link, why));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            // Whatever the rig destroyed with Destroy and unloaded with the scenes is gone by the next test's SetUp.
            yield return null;
            yield return null;
        }

        private static Vector3 Street(float x, float y, float z) => new Vector3(20f + x, y, 20f + z);

        // ------------------------------------------------------------------------------------------ resting across the face

        [UnityTest]
        public IEnumerator ACrateRestingAcrossAnOpenFaceHasAProxyOnTheOtherSideAndSleeps()
        {
            _rig.OpenDoor();
            var crate = _rig.Crate(Street(0f, 0.5f, -0.02f), PortalPhysicsRig.Slippery());
            var body = PortalPhysicsRig.BodyOf(crate);
            _rig.Steps(240);

            Assert.That(crate.InstanceId, Is.EqualTo(0UL), "its root is short of the plane: it has not crossed");
            Assert.That(body.IsSleeping(), Is.True, "resting on the street's floor with a proxy of itself in the hall, it sleeps");
            var proxies = PortalPhysicsRig.ProxiesOf(crate);
            Assert.That(proxies.Count, Is.EqualTo(1), "one proxy, for its one collider");
            Assert.That(proxies[0].gameObject.scene, Is.EqualTo(_rig.HallScene), "in the hall's scene");
            Assert.That(proxies[0].gameObject.layer, Is.EqualTo(Portals.ProxyLayer));
            Assert.That(proxies[0].Source, Is.SameAs(crate.GetComponent<BoxCollider>()));
            Assert.That(Vector3.Distance(proxies[0].transform.position, crate.transform.position), Is.LessThan(0.01f), "posed where the crate is (a coincident link)");
            Assert.That(NebulaDiagnostics.PortalProxies, Is.GreaterThanOrEqualTo(1));
            yield break;
        }

        [UnityTest]
        public IEnumerator AProxyIsPosedOnlyWhenItsSourceMovedSoABodyRestingOnItSleeps()
        {
            _rig.OpenDoor();
            var crate = _rig.Crate(Street(0f, 0.5f, -0.02f), PortalPhysicsRig.Slippery());
            _rig.Steps(240);
            var proxy = PortalPhysicsRig.ProxiesOf(crate)[0];
            var before = proxy.transform.position;
            var proxyBody = proxy.GetComponent<Rigidbody>();
            Assert.That(proxyBody.isKinematic, Is.True);
            // A sleeping source is not looked at again: the proxy stays exactly where it was posed, so what rests on it is not woken by a moving surface.
            _rig.Steps(60);
            Assert.That(proxy.transform.position, Is.EqualTo(before));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ crossing

        [UnityTest]
        public IEnumerator ABodyPushedThroughCrossesWithoutAJoltAndItsProxySwapsSidesOnTheSameTick()
        {
            _rig.OpenDoor();
            var crate = _rig.Crate(Street(0f, 0.5f, -3f), PortalPhysicsRig.Slippery());
            var body = PortalPhysicsRig.BodyOf(crate);
            body.linearVelocity = new Vector3(0f, 0f, 2f);
            Vector3 velocity = body.linearVelocity;
            Vector3 position = crate.transform.position;
            bool crossed = false;
            int ticksInBand = 0;
            for (int i = 0; i < 400 && !crossed; i++)
            {
                _rig.Step();
                var local = crate.transform.position - Street(0f, 0.5f, 0f);
                // Wherever the crate is inside the band, it has its body in one scene and exactly one proxy in the other: never a tick without a collider on either side.
                if (Mathf.Abs(local.z) < 1.5f)
                {
                    ticksInBand++;
                    Assert.That(PortalPhysicsRig.ProxiesOf(crate).Count, Is.EqualTo(1), $"tick {i}: z {local.z:0.000}");
                    var proxy = PortalPhysicsRig.ProxiesOf(crate)[0];
                    bool inHall = crate.InstanceId != 0UL;
                    Assert.That(proxy.gameObject.scene == _rig.HallScene, Is.Not.EqualTo(inHall), $"tick {i}: the proxy is on the side the crate is not");
                }
                crossed = crate.InstanceId != 0UL;
                if (!crossed) { velocity = body.linearVelocity; position = crate.transform.position; }
            }
            Assert.That(crossed, Is.True, "pushed through an open link");
            Assert.That(crate.Container, Is.SameAs(H.Hall));
            Assert.That(ticksInBand, Is.GreaterThan(30));
            // No jolt: the speed it left with is the speed it arrived with, and it moved by that speed.
            Assert.That(body.linearVelocity.z, Is.EqualTo(velocity.z).Within(0.05f), "velocity continuous across the commit");
            Assert.That(Vector3.Distance(crate.transform.position, position), Is.LessThan(2f * PortalPhysicsRig.Dt * velocity.z + 0.02f), "no jump");
            // And a few ticks on, it carries on through the hall with the street's proxy of it behind.
            _rig.Steps(30);
            Assert.That(crate.InstanceId, Is.EqualTo(PortalHarness.Private));
            Assert.That(body.linearVelocity.z, Is.EqualTo(velocity.z).Within(0.05f));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ solid

        [UnityTest]
        public IEnumerator AClosedPortalStopsABodyAndRaisesRefused()
        {
            var crate = _rig.Crate(Street(0f, 0.5f, -3f), PortalPhysicsRig.Slippery());
            var body = PortalPhysicsRig.BodyOf(crate);
            body.linearVelocity = new Vector3(0f, 0f, 3f);
            float furthest = crate.transform.position.z;
            for (int i = 0; i < 240; i++)
            {
                _rig.Step();
                furthest = Mathf.Max(furthest, crate.transform.position.z);
            }
            Assert.That(crate.InstanceId, Is.EqualTo(0UL));
            Assert.That(furthest, Is.LessThan(20f - 0.5f + 0.01f), "its front stops at the aperture collider, 5 cm either side of the plane");
            Assert.That(furthest, Is.GreaterThan(20f - 0.5f - 0.2f), "and it got there");
            Assert.That(_refused.Count, Is.GreaterThan(0), "refused for as long as it pressed against the aperture");
            Assert.That(_refused[0].link, Is.Null, "no link has ever been opened here");
            Assert.That(_refused[0].entity, Is.SameAs(crate));
            yield break;
        }

        [UnityTest]
        public IEnumerator ARefusedRigidbodyBouncesOffTheApertureColliderInsteadOfBeingClamped()
        {
            var link = _rig.OpenDoor();
            // Owned by a client that is no member of the link: the default admission refuses it.
            var crate = _rig.Crate(Street(0f, 0.5f, -3f), PortalPhysicsRig.Slippery(0.8f), owner: 8);
            var body = PortalPhysicsRig.BodyOf(crate);
            body.linearVelocity = new Vector3(0f, 0f, 3f);
            float furthest = crate.transform.position.z;
            float lowest = 0f;
            for (int i = 0; i < 120; i++)
            {
                _rig.Step();
                furthest = Mathf.Max(furthest, crate.transform.position.z);
                lowest = Mathf.Min(lowest, body.linearVelocity.z);
            }
            Assert.That(crate.InstanceId, Is.EqualTo(0UL), "it did not cross");
            Assert.That(furthest, Is.LessThan(19.6f), "stopped at the collider");
            Assert.That(lowest, Is.LessThan(-1f), "and thrown back by it: it bounced");
            Assert.That(_refused.Count, Is.GreaterThan(0));
            Assert.That(_refused[0].why, Is.EqualTo(PortalRefusal.NotAdmitted));
            Assert.That(_refused[0].link, Is.SameAs(link));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ bodies on both sides

        /// <summary>A crate of the hall's scope, straddling the face from the far side.</summary>
        private NetworkIdentity HallCrate(float z, float mass = 1f)
        {
            var crate = H.W.SpawnServerDriven(_rig.CratePrefab, H.Hall, Street(0f, 0.5f, z), Quaternion.identity);
            crate.GetComponent<BoxCollider>().sharedMaterial = PortalPhysicsRig.Slippery();
            PortalPhysicsRig.BodyOf(crate).mass = mass;
            return crate;
        }

        [UnityTest]
        public IEnumerator ABodyRestingOnAnotherBodysProxySleeps()
        {
            _rig.OpenDoor();
            var below = HallCrate(0.3f);
            _rig.Steps(90);
            Assert.That(below.InstanceId, Is.EqualTo(PortalHarness.Private));
            var belowAt = below.transform.position;
            // Dropped from 2.5 m onto the part of it that stands in the street: it lands on the proxy of the crate below, in the street's scene.
            var above = _rig.Crate(Street(0f, 3f, -0.1f), PortalPhysicsRig.Slippery());
            var aboveBody = PortalPhysicsRig.BodyOf(above);
            _rig.Steps(300);
            Assert.That(above.InstanceId, Is.EqualTo(0UL));
            Assert.That(above.transform.position.y, Is.EqualTo(1.5f).Within(0.03f), "resting on the proxy of the crate below");
            Assert.That(aboveBody.IsSleeping(), Is.True, "and asleep: nothing moves the proxy it rests on");
            Assert.That(PortalPhysicsRig.BodyOf(below).IsSleeping(), Is.True);
            Assert.That(Vector3.Distance(below.transform.position, belowAt), Is.LessThan(0.02f), "the crate below was not moved");
            yield break;
        }

        [UnityTest]
        public IEnumerator ADroppedBodyDoesNotSqueezeTheOneBelowOutAndThePairCollidesInOneSceneOnly()
        {
            var link = _rig.OpenDoor();
            var below = HallCrate(0.3f);
            _rig.Steps(90);
            var belowAt = below.transform.position;
            var above = _rig.Crate(Street(0f, 4f, -0.1f), PortalPhysicsRig.Slippery());
            PortalPhysicsRig.BodyOf(above).mass = 10f;
            float moved = 0f;
            bool pairSeen = false;
            for (int i = 0; i < 300; i++)
            {
                _rig.Step();
                moved = Mathf.Max(moved, Vector3.Distance(below.transform.position, belowAt));
                if (H.W.Instance.PortalPhysics.TryGetRig(link.Id, out var rig) && rig.Pairs.Count > 0) pairSeen = true;
            }
            string dump = "";
            if (H.W.Instance.PortalPhysics.TryGetRig(link.Id, out var dumpRig))
                foreach (var t in dumpRig.Tracked.Values) dump += $" [{t.Entity.name} near={t.EntityNear} parts={t.Parts.Count} y={t.Entity.transform.position.y:0.00}]";
            Assert.That(pairSeen, Is.True, "the two bodies, one native to each side, were recognised as a pair" + dump);
            Assert.That(moved, Is.LessThan(0.02f), "mirrored both ways, the falling crate's copy would have driven the one below out of its place (docs/portals.md, section 18)");
            Assert.That(above.transform.position.y, Is.EqualTo(1.5f).Within(0.03f));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ the throat, the apertures and readiness

        private static List<PortalProxy> Throat() => PortalPhysicsRig.Proxies(p => p.IsStatic && !p.IsAperture);

        private static List<PortalProxy> AllThroat()
        {
            var result = new List<PortalProxy>();
            foreach (var proxy in Object.FindObjectsByType<PortalProxy>(FindObjectsInactive.Include)) if (proxy.IsStatic && !proxy.IsAperture) result.Add(proxy);
            return result;
        }

        [UnityTest]
        public IEnumerator TheThroatAndTheApertureCollidersAreBuiltBeforeTheLinkIsReadyEnabledAtOpenAndDroppedAtClosed()
        {
            var link = H.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _rig.Step();
            _rig.BuildHall();
            _rig.Steps(3);
            // Every condition but the member's client holds: the physics of both sides is built (D10 condition 4), the throat switched off until the link opens.
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming));
            Assert.That(PortalPhysicsRig.Proxies(p => p.IsAperture).Count, Is.EqualTo(2), "one aperture collider in each side's scene");
            var built = AllThroat();
            Assert.That(built.Count, Is.GreaterThanOrEqualTo(2), "the street's doorstep in the hall, the hall's floor in the street");
            Assert.That(built.TrueForAll(p => !p.gameObject.activeSelf), Is.True, "but not enabled");
            Assert.That(built.Exists(p => p.gameObject.scene == _rig.HallScene), Is.True);
            Assert.That(built.Exists(p => p.gameObject.scene != _rig.HallScene), Is.True);

            H.Warm(link);
            _rig.Step();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
            Assert.That(Throat().Count, Is.EqualTo(built.Count), "enabled at Open");

            Assert.That(H.Door.Close(PortalHarness.Owner), Is.True);
            _rig.Steps(3);
            Assert.That(link.State, Is.EqualTo(PortalState.Closed));
            yield return null;
            Assert.That(AllThroat().Count, Is.EqualTo(0), "dropped at Closed");
            Assert.That(PortalPhysicsRig.Proxies(p => p.IsAperture).Count, Is.EqualTo(2), "the apertures stay: a closed portal is a solid plane");
            Assert.That(NebulaDiagnostics.PortalProxies, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator ValidateFindsTheStaticCollidersThatStandInTheCrossSectionAndLogsThem()
        {
            Assert.That(H.Door.Validate(), Is.True, "the street's tunnel is clear");
            var blockers = new List<Collider>();
            var stone = _rig.Box("stone in the doorway", Street(0f, PortalPhysicsRig.FaceY, 1f), new Vector3(1f, 1f, 1f));
            Physics.SyncTransforms();
            LogAssert.Expect(LogType.Warning, new Regex("static collider.*cross-section"));
            Assert.That(H.Door.Validate(blockers), Is.False);
            Assert.That(blockers, Is.EqualTo(new[] { stone.GetComponent<Collider>() }));
            // And a link that starts preparing checks the same, and says so.
            LogAssert.Expect(LogType.Warning, new Regex("stone in the doorway"));
            H.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _rig.Step();
            _rig.BuildHall();
            _rig.Steps(3);
            yield break;
        }

        // ------------------------------------------------------------------------------------------ pooling and bounds

        [UnityTest]
        public IEnumerator ProxiesAreBuiltWhileTheLinkPreparesSoABodyEnteringTheBandBuildsNothing()
        {
            var crate = _rig.Crate(Street(0f, 0.5f, -6f), PortalPhysicsRig.Slippery());
            _rig.OpenDoor();
            var physics = H.W.Instance.PortalPhysics;
            int built = physics.ProxiesBuilt, reused = physics.ProxiesReused;
            Assert.That(built, Is.GreaterThanOrEqualTo(1), "one for the crate near the door, built while the link prepared");
            Assert.That(PortalPhysicsRig.ProxiesOf(crate).Count, Is.EqualTo(0), "none is posed for a body outside the band");
            PortalPhysicsRig.BodyOf(crate).linearVelocity = new Vector3(0f, 0f, 2f);
            for (int i = 0; i < 240 && PortalPhysicsRig.ProxiesOf(crate).Count == 0; i++) _rig.Step();
            Assert.That(PortalPhysicsRig.ProxiesOf(crate).Count, Is.EqualTo(1), "it entered the band");
            Assert.That(physics.ProxiesBuilt, Is.EqualTo(built), "and nothing was built for it");
            Assert.That(physics.ProxiesReused, Is.GreaterThan(reused), "a pooled proxy was taken");
            yield break;
        }

        [UnityTest]
        public IEnumerator APortalBuildsNoMoreBodyProxiesThanItsBound()
        {
            _rig.OpenDoor();
            H.Door.MaxProxiesPerPortal = 2;
            var crates = new List<NetworkIdentity>();
            for (int i = 0; i < 4; i++) crates.Add(_rig.Crate(Street(-1.5f + i, 0.5f, -0.02f), PortalPhysicsRig.Slippery()));
            LogAssert.Expect(LogType.Warning, new Regex("MaxProxiesPerPortal"));
            _rig.Steps(30);
            Assert.That(H.Door.ProxyCount, Is.EqualTo(2));
            int standing = 0;
            foreach (var crate in crates) standing += PortalPhysicsRig.ProxiesOf(crate).Count;
            Assert.That(standing, Is.EqualTo(2));
            Assert.That(NebulaDiagnostics.PortalProxiesDropped, Is.GreaterThan(0));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ observation only, and back out again

        [UnityTest]
        public IEnumerator AnObservationOnlyPortalKeepsItsApertureSolidForEveryone()
        {
            H.Door.ObservationOnly = true;
            _rig.OpenDoor();
            var crate = _rig.Crate(Street(0f, 0.5f, -3f), PortalPhysicsRig.Slippery()); // server-owned: admitted by any other portal
            PortalPhysicsRig.BodyOf(crate).linearVelocity = new Vector3(0f, 0f, 3f);
            float furthest = -10f;
            for (int i = 0; i < 180; i++)
            {
                _rig.Step();
                furthest = Mathf.Max(furthest, crate.transform.position.z - 20f);
            }
            Assert.That(crate.InstanceId, Is.EqualTo(0UL));
            Assert.That(furthest, Is.LessThan(-0.4f), "stopped at the collider");
            Assert.That(_refused.Count, Is.GreaterThan(0));
            Assert.That(_refused[0].why, Is.EqualTo(PortalRefusal.NotAdmitted));
            yield break;
        }

        [UnityTest]
        public IEnumerator ABodyPushedOutOfTheHallCrossesBackWithoutAJolt()
        {
            _rig.OpenDoor();
            var crate = HallCrate(3f);
            var body = PortalPhysicsRig.BodyOf(crate);
            body.linearVelocity = new Vector3(0f, 0f, -2f);
            Vector3 velocity = body.linearVelocity;
            bool crossed = false;
            for (int i = 0; i < 400 && !crossed; i++)
            {
                _rig.Step();
                crossed = crate.InstanceId == 0UL;
                if (!crossed) velocity = body.linearVelocity;
            }
            Assert.That(crossed, Is.True, "leaving an open link through its far aperture");
            Assert.That(body.linearVelocity.z, Is.EqualTo(velocity.z).Within(0.05f));
            _rig.Steps(30);
            Assert.That(crate.InstanceId, Is.EqualTo(0UL));
            Assert.That(PortalPhysicsRig.ProxiesOf(crate).Count, Is.LessThanOrEqualTo(1));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ a pawn with a character controller

        [UnityTest]
        public IEnumerator APawnWalksThroughAnOpenFaceAndIsStoppedByTheApertureWhenItIsNotAdmitted()
        {
            _rig.OpenDoor(); // for client 7, a warm member
            var member = _rig.Walker(-1f, -3f, PortalHarness.Client);
            var stranger = _rig.Walker(1f, -3f, 8);
            float strangerFurthest = -10f;
            bool strangerSeenInBand = false;
            for (int i = 0; i < 360 && member.InstanceId == 0UL; i++)
            {
                PortalPhysicsRig.Walk(member, 2f);
                PortalPhysicsRig.Walk(stranger, 2f);
                _rig.Step();
                strangerFurthest = Mathf.Max(strangerFurthest, stranger.transform.position.z - 20f);
                strangerSeenInBand |= PortalPhysicsRig.ProxiesOf(stranger).Count > 0;
            }
            Assert.That(member.InstanceId, Is.EqualTo(PortalHarness.Private), "a warm member walks through: the aperture collider is ignored for it");
            Assert.That(member.Container, Is.SameAs(H.Hall));
            Assert.That(strangerFurthest, Is.LessThan(-0.3f), "the stranger met the aperture collider and went no further");
            Assert.That(strangerFurthest, Is.GreaterThan(-0.6f), "right up to it");
            Assert.That(strangerSeenInBand, Is.True, "a capsule proxy of it stood in the hall while it was in the band");
            Assert.That(_refused.Exists(r => r.entity == stranger && r.why == PortalRefusal.NotAdmitted), Is.True, "and Refused was raised for it");
            yield break;
        }
    }
}
