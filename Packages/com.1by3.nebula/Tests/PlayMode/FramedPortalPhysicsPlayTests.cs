using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-432 (<c>docs/portals.md</c> D26 to D28) on a side that simulates in a physics frame: a door on a planet that is a carrier, 12,000 km from its scope's
    /// origin and turned, into an instance. The proxies, the throat and the aperture collider of the planet's side stand in the planet frame's own physics scene,
    /// about the door's face worked out in double from the link and the frame's pose; the vault's bodies are mirrored into that scene, and the planet's into the
    /// vault's.
    /// </summary>
    [Category("Conformance")]
    public sealed class FramedPortalPhysicsPlayTests
    {
        private FramedPortalRig _rig;
        private readonly List<(NetworkIdentity entity, PortalLink link, PortalRefusal why)> _refused = new List<(NetworkIdentity, PortalLink, PortalRefusal)>();

        [SetUp]
        public void SetUp()
        {
            _rig = new FramedPortalRig();
            _refused.Clear();
            _rig.Portal.Refused += (entity, link, why) => _refused.Add((entity, link, why));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            // The scenes Nebula made are unloaded asynchronously, and the next test makes scenes of the same names.
            for (int i = 0; i < 30 && PortalPhysicsRig.NebulaScenesLoaded(); i++) yield return null;
            yield return null;
        }

        private static List<PortalProxy> ProxiesOf(NetworkIdentity entity) => PortalPhysicsRig.ProxiesOf(entity);

        // ------------------------------------------------------------------------------------------ the scene and the pose

        [UnityTest]
        public IEnumerator TheApertureColliderStandsInThePlanetsFrameSceneAboutTheDoorAndIsSolidWithoutALink()
        {
            _rig.Steps(3);
            var apertures = PortalPhysicsRig.Proxies(p => p.IsAperture);
            Assert.That(apertures.Count, Is.EqualTo(1), "the door's own side, whether or not anything opened it");
            var aperture = apertures[0];
            Assert.That(aperture.gameObject.scene, Is.EqualTo(_rig.PlanetScene), "in the scene the planet's bodies simulate in, not the scope's");
            Assert.That(Vector3.Distance(aperture.transform.position, _rig.At(0f, FramedPortalRig.FaceHeight, 0f)), Is.LessThan(0.002f), "at the door, in the frame's simulation space");
            Assert.That(Quaternion.Angle(aperture.transform.rotation, FramedPortalRig.DoorTurn), Is.LessThan(0.01f), "turned as the door is on the planet, not as the planet is in its scope");
            Assert.That(aperture.transform.position.magnitude, Is.LessThan(4096f), "small: the frame's floating origin is at the door");
            yield break;
        }

        [UnityTest]
        public IEnumerator ACrateRestingAcrossTheFaceHasAProxyInTheVaultPosedThroughTheLinkAndSleeps()
        {
            var link = _rig.OpenDoor();
            var crate = _rig.Crate(0f, 0.5f, -0.02f, FramedPortalRig.Slippery());
            var body = FramedPortalRig.BodyOf(crate);
            _rig.Steps(240);

            Assert.That(crate.InstanceId, Is.EqualTo(ScopeKeys.Hash(FramedPortalRig.Scope)), "it has not crossed");
            Assert.That(crate.Space, Is.SameAs(_rig.Planet.Carried));
            Assert.That(body.IsSleeping(), Is.True, "resting on the planet's ground with a proxy of itself in the vault, it sleeps");
            var proxies = ProxiesOf(crate);
            Assert.That(proxies.Count, Is.EqualTo(1), "one proxy, for its one collider");
            Assert.That(proxies[0].gameObject.scene, Is.EqualTo(_rig.HallScene), "in the vault's scene");
            Assert.That(proxies[0].Source, Is.SameAs(crate.GetComponent<BoxCollider>()));
            // The vault's face is the door's, upright in its own space (the link's turn is the door's inverse): the crate's place in the door's axes is the proxy's in the face's.
            var face = ContainerRegistry.ToFrame(link.FarFace.Origin, link.FarScope);
            var inDoor = _rig.InDoorAxes(crate.transform.position);
            var expected = face + link.FarFace.Rotation * (inDoor - new Vector3(0f, FramedPortalRig.FaceHeight, 0f));
            Assert.That(Vector3.Distance(proxies[0].transform.position, expected), Is.LessThan(0.01f), "posed through the link");
            Assert.That(Quaternion.Angle(proxies[0].transform.rotation, link.FarFace.Rotation * Quaternion.Inverse(FramedPortalRig.DoorTurn) * crate.transform.rotation), Is.LessThan(0.5f));
            yield break;
        }

        [UnityTest]
        public IEnumerator TheThroatStandsOnBothSidesInTheirOwnScenes()
        {
            _rig.OpenDoor();
            _rig.Steps(2);
            var throat = PortalPhysicsRig.Proxies(p => p.IsStatic && !p.IsAperture);
            Assert.That(throat.Exists(p => p.gameObject.scene == _rig.HallScene), Is.True, "the planet's doorstep stands in the vault");
            Assert.That(throat.Exists(p => p.gameObject.scene == _rig.PlanetScene), Is.True, "the vault's floor stands in the planet's scene");
            Assert.That(throat.TrueForAll(p => p.gameObject.scene == _rig.HallScene || p.gameObject.scene == _rig.PlanetScene), Is.True);
            foreach (var p in throat)
                if (p.gameObject.scene == _rig.PlanetScene)
                    Assert.That(p.transform.position.magnitude, Is.LessThan(4096f), "in the frame's simulation space, near its origin");
            yield break;
        }

        // ------------------------------------------------------------------------------------------ solid

        [UnityTest]
        public IEnumerator AClosedPortalStopsABodyOnThePlanetSide()
        {
            var crate = _rig.Crate(0f, 0.5f, -3f, FramedPortalRig.Slippery());
            var body = FramedPortalRig.BodyOf(crate);
            body.linearVelocity = FramedPortalRig.Forward * 3f;
            float furthest = -10f;
            for (int i = 0; i < 240; i++)
            {
                _rig.Step();
                furthest = Mathf.Max(furthest, _rig.InDoorAxes(crate.transform.position).z);
            }
            Assert.That(crate.InstanceId, Is.EqualTo(ScopeKeys.Hash(FramedPortalRig.Scope)), "it did not cross");
            Assert.That(furthest, Is.LessThan(-0.5f + 0.01f), "its front stops at the aperture collider, 5 cm short of the plane");
            Assert.That(furthest, Is.GreaterThan(-0.5f - 0.2f), "and it got there");
            Assert.That(_refused.Count, Is.GreaterThan(0), "refused for as long as it pressed against the aperture");
            Assert.That(_refused[0].entity, Is.SameAs(crate));
            yield break;
        }

        [UnityTest]
        public IEnumerator ARefusedRigidbodyBouncesOffTheApertureColliderOnThePlanetSide()
        {
            var link = _rig.OpenDoor();
            var crate = _rig.Crate(0f, 0.5f, -3f, FramedPortalRig.Slippery(0.8f), owner: 8);
            var body = FramedPortalRig.BodyOf(crate);
            body.linearVelocity = FramedPortalRig.Forward * 3f;
            float furthest = -10f;
            float fastestBack = 0f;
            for (int i = 0; i < 120; i++)
            {
                _rig.Step();
                furthest = Mathf.Max(furthest, _rig.InDoorAxes(crate.transform.position).z);
                fastestBack = Mathf.Min(fastestBack, Vector3.Dot(body.linearVelocity, FramedPortalRig.Forward));
            }
            Assert.That(crate.InstanceId, Is.EqualTo(ScopeKeys.Hash(FramedPortalRig.Scope)), "it did not cross");
            Assert.That(furthest, Is.LessThan(-0.4f), "stopped at the collider");
            Assert.That(fastestBack, Is.LessThan(-1f), "and thrown back by it: it bounced");
            Assert.That(_refused.Count, Is.GreaterThan(0));
            Assert.That(_refused[0].why, Is.EqualTo(PortalRefusal.NotAdmitted));
            Assert.That(_refused[0].link, Is.SameAs(link));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ bodies on both sides

        [UnityTest]
        public IEnumerator ABodyInsideTheVaultBlocksABodyOnThePlanetSideThroughItsProxy()
        {
            _rig.OpenDoor();
            var below = _rig.HallCrate(0f, 0.5f, 0.3f, FramedPortalRig.Slippery());
            _rig.Steps(90);
            Assert.That(below.InstanceId, Is.EqualTo(FramedPortalRig.Private));
            var belowAt = below.transform.position;
            var proxies = ProxiesOf(below);
            Assert.That(proxies.Count, Is.EqualTo(1), "the vault's crate straddles the face: a proxy of it in the planet's scene");
            Assert.That(proxies[0].gameObject.scene, Is.EqualTo(_rig.PlanetScene));
            // Dropped from 2.5 m onto the part of it that stands on the planet's side: it lands on the proxy of the crate below.
            var above = _rig.Crate(0f, 3f, -0.1f, FramedPortalRig.Slippery());
            var aboveBody = FramedPortalRig.BodyOf(above);
            _rig.Steps(300);
            Assert.That(above.InstanceId, Is.EqualTo(ScopeKeys.Hash(FramedPortalRig.Scope)));
            Assert.That(_rig.InDoorAxes(above.transform.position).y, Is.EqualTo(1.5f).Within(0.03f), "resting on the proxy of the crate below");
            Assert.That(aboveBody.IsSleeping(), Is.True, "and asleep");
            Assert.That(Vector3.Distance(below.transform.position, belowAt), Is.LessThan(0.02f), "the crate below was not moved");
            yield break;
        }

        // ------------------------------------------------------------------------------------------ a move of the frame's origin

        [UnityTest]
        public IEnumerator WhenTheFramesFloatingOriginMovesTheApertureTheThroatAndTheProxiesAreSetAgainAboutTheDoor()
        {
            _rig.OpenDoor();
            var crate = _rig.Crate(0f, 0.5f, -0.02f, FramedPortalRig.Slippery());
            var hallCrate = _rig.HallCrate(1.5f, 0.5f, 0.02f, FramedPortalRig.Slippery());
            _rig.Steps(120);
            var aperture = PortalPhysicsRig.Proxies(p => p.IsAperture && p.gameObject.scene == _rig.PlanetScene)[0];
            var before = aperture.transform.position;
            var hallProxy = ProxiesOf(hallCrate)[0];
            var hallProxyBefore = hallProxy.transform.position;
            Assert.That(ProxiesOf(crate).Count, Is.EqualTo(1));

            // The worker moves a frame's origin to what it simulates (docs/container-tree.md D19): everything in the frame moves with it, the scene-level copies do not.
            var shift = new Vector3(1024f, 0f, -512f);
            PhysicsFrames.ShiftOrigin(_rig.Frame, FramedPortalRig.Door + shift);
            _rig.Steps(30);

            aperture = PortalPhysicsRig.Proxies(p => p.IsAperture && p.gameObject.scene == _rig.PlanetScene)[0];
            Assert.That(Vector3.Distance(aperture.transform.position, before - shift), Is.LessThan(0.002f), "the aperture collider followed the door");
            Assert.That(Vector3.Distance(aperture.transform.position, _rig.At(0f, FramedPortalRig.FaceHeight, 0f)), Is.LessThan(0.002f));
            var hallProxyNow = ProxiesOf(hallCrate)[0];
            Assert.That(Vector3.Distance(hallProxyNow.transform.position, hallProxyBefore - shift), Is.LessThan(0.02f), "and so did the vault crate's proxy on the planet");
            foreach (var p in PortalPhysicsRig.Proxies(p => p.IsStatic && !p.IsAperture && p.gameObject.scene == _rig.PlanetScene))
                Assert.That(p.transform.position.magnitude, Is.LessThan(4096f), "the vault's floor in the throat follows too");
            Assert.That(FramedPortalRig.BodyOf(crate).IsSleeping(), Is.True, "what rests across the face still rests");
            Assert.That(_rig.InDoorAxes(crate.transform.position).y, Is.EqualTo(0.5f).Within(0.03f));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ a crossing

        [UnityTest]
        public IEnumerator ABodyPushedThroughTheFramedDoorCrossesWithoutAJoltAndItsProxySwapsSidesOnTheSameTick()
        {
            _rig.OpenDoor();
            var crate = _rig.Crate(0f, 0.5f, -3f, FramedPortalRig.Slippery());
            var body = FramedPortalRig.BodyOf(crate);
            body.linearVelocity = FramedPortalRig.Forward * 2f;
            Vector3 velocity = Quaternion.Inverse(FramedPortalRig.DoorTurn) * body.linearVelocity;
            bool crossed = false;
            int ticksInBand = 0;
            for (int i = 0; i < 400 && !crossed; i++)
            {
                _rig.Step();
                var local = _rig.InDoorAxes(crate.transform.position);
                if (crate.InstanceId == ScopeKeys.Hash(FramedPortalRig.Scope) && Mathf.Abs(local.z) < 1.5f)
                {
                    ticksInBand++;
                    var proxies = ProxiesOf(crate);
                    Assert.That(proxies.Count, Is.EqualTo(1), $"tick {i}: z {local.z:0.000}");
                    Assert.That(proxies[0].gameObject.scene, Is.EqualTo(_rig.HallScene), $"tick {i}: the proxy is on the side the crate is not");
                }
                crossed = crate.InstanceId == FramedPortalRig.Private;
                if (!crossed) velocity = Quaternion.Inverse(FramedPortalRig.DoorTurn) * body.linearVelocity;
            }
            Assert.That(crossed, Is.True, "pushed through an open link");
            Assert.That(crate.Container, Is.SameAs(_rig.Hall));
            Assert.That(ticksInBand, Is.GreaterThan(30));
            Assert.That(body.linearVelocity.z, Is.EqualTo(velocity.z).Within(0.05f), "velocity continuous across the commit, turned by the door's turn");
            _rig.Steps(30);
            var behind = ProxiesOf(crate);
            Assert.That(behind.Count, Is.LessThanOrEqualTo(1));
            Assert.That(behind.TrueForAll(p => p.gameObject.scene == _rig.PlanetScene), Is.True, "the crate's proxy is now on the planet, behind it");
            yield break;
        }

        // ------------------------------------------------------------------------------------------ a carrier that moves

        [UnityTest]
        public IEnumerator TheFacesFrameInThePlanetsSceneDoesNotDependOnWhereTheCarrierStandsInItsScope()
        {
            _rig.Steps(2);
            var space = _rig.Planet.Carried;
            var before = PortalSpaces.FaceFrame(_rig.Portal.NearFace, space);
            Assert.That(before.Valid, Is.True);
            Assert.That(Vector3.Distance(before.Origin, _rig.At(0f, FramedPortalRig.FaceHeight, 0f)), Is.LessThan(0.002f), "the door in the frame's simulation space");
            var from = _rig.Planet.transform.position;
            var turn = _rig.Planet.transform.rotation;
            try
            {
                // The planet is moved in its scope, a float there being a metre apart at best, and turned a little about its centre; the door goes with it (it is fixed in
                // the planet), so its absolute face is read out of the frame again, and worked back into the frame's own coordinates in double.
                foreach (var move in new[] { new Vector3(4f, 0f, 0f), new Vector3(-3f, 2f, 5f), new Vector3(1000f, -500f, 250f) })
                {
                    _rig.Planet.transform.position = from + move;
                    _rig.Planet.transform.rotation = Quaternion.Euler(0.01f * move.x, 0f, 0f) * turn;
                    var after = PortalSpaces.FaceFrame(_rig.Portal.NearFace, space);
                    Assert.That(Vector3.Distance(after.Origin, before.Origin), Is.LessThan(0.002f), $"moved by {move}: the same place in the frame");
                    Assert.That(Quaternion.Angle(after.Rotation, before.Rotation), Is.LessThan(0.01f));
                    Assert.That(after.SameAs(before), Is.True);
                }
            }
            finally
            {
                _rig.Planet.transform.SetPositionAndRotation(from, turn);
            }
            yield break;
        }
    }
}
