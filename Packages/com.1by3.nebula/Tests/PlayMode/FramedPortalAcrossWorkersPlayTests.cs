using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-432 (<c>docs/portals.md</c> D26, D29) on two workers in one process, the planet's door on the first and the vault on the second (a split link): a body in the
    /// band of the door is ghosted to the vault's worker and held there as a proxy only, posed through the link by the face as the planet's frame holds it; a body in the
    /// vault's band is ghosted to the planet's worker and posed into the planet frame's scene, in the frame's simulation space.
    /// </summary>
    [Category("Conformance")]
    public sealed class FramedPortalAcrossWorkersPlayTests
    {
        private FramedPortalRig _rig;

        [SetUp]
        public void SetUp()
        {
            _rig = new FramedPortalRig(2) { HallOnSecondWorker = true };
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            for (int i = 0; i < 30 && PortalPhysicsRig.NebulaScenesLoaded(); i++) yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator ABodyOnThePlanetsSideIsAProxyOnTheVaultsWorkerPosedThroughTheFacesFrame()
        {
            var link = _rig.OpenDoor();
            Assert.That(_rig.Hall.OwnerWorkerId, Is.EqualTo(_rig.Other.Id), "the vault is the second worker's");
            _rig.PlanetFirst = true; // the planet's worker settles a body's aperture pairs before the vault's worker steps the planet's scene, as in production it alone would
            var crate = _rig.Crate(0f, 0.5f, -0.02f, FramedPortalRig.Slippery());
            var outside = _rig.Crate(0f, 0.5f, -9f, FramedPortalRig.Slippery());
            _rig.Steps(120);

            var ghost = _rig.Other.Find(crate.NetId);
            Assert.That(ghost, Is.Not.Null, "the vault's worker holds a ghost of a body in the band");
            Assert.That(ghost.IsPortalGhost, Is.True);
            foreach (var c in ghost.GetComponentsInChildren<Collider>(true)) Assert.That(c.enabled, Is.False, "no body in the planet's scene there");
            var proxies = PortalPhysicsRig.ProxiesOf(ghost);
            Assert.That(proxies.Count, Is.EqualTo(1), "its proxy stands in the vault's scene");
            Assert.That(proxies[0].gameObject.scene, Is.EqualTo(_rig.HallScene));
            var face = ContainerRegistry.ToFrame(link.FarFace.Origin, link.FarScope);
            var inDoor = _rig.InDoorAxes(crate.transform.position);
            var expected = face + link.FarFace.Rotation * (inDoor - new Vector3(0f, FramedPortalRig.FaceHeight, 0f));
            Assert.That(Vector3.Distance(proxies[0].transform.position, expected), Is.LessThan(0.05f),
                $"posed through the link (a tick behind its authority): crate door axes {inDoor} ghost door axes {_rig.InDoorAxes(ghost.transform.position)} ghost space {ghost.Space} container {ghost.Container} proxy {proxies[0].transform.position} expected {expected} face {face}");
            Assert.That(_rig.Other.Find(outside.NetId), Is.Null, "a body 9 m back is not in the band: nothing of it is ghosted across");
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
            yield break;
        }

        [UnityTest]
        public IEnumerator ABodyInTheVaultsBandIsAProxyOnThePlanetsWorkerInThePlanetsScene()
        {
            _rig.OpenDoor();
            var crate = _rig.HallCrate(0f, 0.5f, 0.3f, FramedPortalRig.Slippery());
            _rig.Steps(120);

            Assert.That(crate.HasAuthority, Is.True);
            Assert.That(crate.InstanceId, Is.EqualTo(FramedPortalRig.Private));
            var ghost = _rig.W.Find(crate.NetId);
            Assert.That(ghost, Is.Not.Null, "the planet's worker holds a ghost of a body in the vault's band");
            Assert.That(ghost.IsPortalGhost, Is.True);
            var proxies = PortalPhysicsRig.ProxiesOf(ghost);
            Assert.That(proxies.Count, Is.EqualTo(1));
            Assert.That(proxies[0].gameObject.scene, Is.EqualTo(_rig.PlanetScene), "in the planet frame's scene");
            Assert.That(Vector3.Distance(proxies[0].transform.position, _rig.At(0f, 0.5f, 0.3f)), Is.LessThan(0.05f), "at the same place in the door's axes, in the frame's simulation space");
            yield break;
        }
    }
}
