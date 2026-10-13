using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-432 (<c>docs/portals.md</c> D29, D30) on two workers in one process: the street belongs to the first, the hall to the second. A body in the band of
    /// the face is ghosted to the worker that owns the other side and held there as a proxy only, posed through the link in its own scene, with no body in its
    /// scope's; nothing outside the band is ghosted across; and <see cref="ContainerRegistry"/> still makes no neighbours of containers of two scopes.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalPhysicsAcrossWorkersPlayTests
    {
        private PortalPhysicsRig _rig;
        private PortalHarness H => _rig.H;

        [SetUp]
        public void SetUp()
        {
            _rig = new PortalPhysicsRig(2);
            H.HallOnSecondWorker = true;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            yield return null;
            yield return null;
        }

        private static Vector3 Street(float x, float y, float z) => new Vector3(20f + x, y, 20f + z);

        [UnityTest]
        public IEnumerator ABodyInTheBandIsAProxyOnTheFarWorkerAndNothingOutsideTheBandIsGhostedAcross()
        {
            var link = _rig.OpenDoor();
            Assert.That(H.Hall.OwnerWorkerId, Is.EqualTo(H.Other.Id), "the hall is the second worker's");
            var inBand = _rig.Crate(Street(0f, 0.5f, -0.02f), PortalPhysicsRig.Slippery());
            var outside = _rig.Crate(Street(0f, 0.5f, -9f), PortalPhysicsRig.Slippery());
            var crowd = _rig.Crate(Street(6f, 0.5f, -1f), PortalPhysicsRig.Slippery());
            _rig.Steps(120);

            var ghost = H.Other.Find(inBand.NetId);
            Assert.That(ghost, Is.Not.Null, "the far worker holds a ghost of a body in the band");
            Assert.That(ghost.HasAuthority, Is.False);
            Assert.That(ghost.IsPortalGhost, Is.True, "held across a portal, as a proxy only");
            foreach (var c in ghost.GetComponentsInChildren<Collider>(true)) Assert.That(c.enabled, Is.False, "no body in the entity's own scope's scene there");
            var proxies = PortalPhysicsRig.ProxiesOf(ghost);
            Assert.That(proxies.Count, Is.EqualTo(1), "its proxy stands in the far worker's scene");
            Assert.That(proxies[0].gameObject.scene, Is.EqualTo(_rig.HallScene));
            Assert.That(Vector3.Distance(proxies[0].transform.position, inBand.transform.position), Is.LessThan(0.05f), "posed through the link (a tick behind its authority)");

            Assert.That(H.Other.Find(outside.NetId), Is.Null, "a body 9 m back is in the window but not in the band: nothing of it is ghosted across");
            Assert.That(H.Other.Find(crowd.NetId), Is.Null, "nor one 6 m to the side");
            Assert.That(H.Other.Find(H.Player.NetId), Is.Null, "nor the pawn");
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
            yield break;
        }

        [UnityTest]
        public IEnumerator ABodyInTheFarBandIsAProxyOnTheNearWorker()
        {
            _rig.OpenDoor();
            var crate = H.Other.SpawnServerDriven(_rig.CratePrefab, H.Hall, Street(0f, 0.5f, 0.3f), Quaternion.identity);
            crate.GetComponent<BoxCollider>().sharedMaterial = PortalPhysicsRig.Slippery();
            _rig.Steps(120);

            Assert.That(crate.HasAuthority, Is.True);
            Assert.That(crate.InstanceId, Is.EqualTo(PortalHarness.Private));
            var ghost = H.W.Find(crate.NetId);
            Assert.That(ghost, Is.Not.Null, "the street's worker holds a ghost of a body in the hall's band");
            Assert.That(ghost.IsPortalGhost, Is.True);
            var proxies = PortalPhysicsRig.ProxiesOf(ghost);
            Assert.That(proxies.Count, Is.EqualTo(1));
            Assert.That(proxies[0].gameObject.scene.handle, Is.Not.EqualTo(_rig.HallScene.handle), "in the street's scene");
            Assert.That(Vector3.Distance(proxies[0].transform.position, crate.transform.position), Is.LessThan(0.05f), $"crate {crate.transform.position} ghost {ghost.transform.position} ({ghost.Container?.ContainerId}) proxy {proxies[0].transform.position}");
            yield break;
        }

        [UnityTest]
        public IEnumerator PortalAdjacencyIsASeparateRelationAndTheRegistryStillMakesNoNeighboursAcrossScopes()
        {
            _rig.OpenDoor();
            _rig.Steps(5);
            Assert.That(new List<Container>(H.Street.Neighbors).Contains(H.Hall), Is.False);
            Assert.That(new List<Container>(H.Hall.Neighbors).Contains(H.Street), Is.False);
            Assert.That(H.W.Instance.PortalAdjacencyTable.Relates(ContainerRef.Of(H.Street), ContainerRef.Of(H.Hall)), Is.True, "but the portal relates them");
            Assert.That(H.Other.Instance.PortalAdjacencyTable.Relates(ContainerRef.Of(H.Street), ContainerRef.Of(H.Hall)), Is.True, "and tells the far worker so");
            yield break;
        }

        [UnityTest]
        public IEnumerator ABodyPushedThroughASplitLinkArrivesOnTheFarWorkerAsABodyAgain()
        {
            _rig.OpenDoor();
            var crate = _rig.Crate(Street(0f, 0.5f, -3f), PortalPhysicsRig.Slippery());
            ulong id = crate.NetId;
            PortalPhysicsRig.BodyOf(crate).linearVelocity = new Vector3(0f, 0f, 2f);
            for (int i = 0; i < 400 && crate.InstanceId == 0UL; i++) _rig.Step();
            Assert.That(crate.InstanceId, Is.EqualTo(PortalHarness.Private), "committed into the hall, which the other worker owns");
            _rig.Steps(20);
            var arrived = H.Other.Find(id);
            Assert.That(arrived, Is.Not.Null);
            Assert.That(arrived.HasAuthority, Is.True, "authority followed the container's lease");
            Assert.That(arrived.IsPortalGhost, Is.False);
            Assert.That(arrived.GetComponent<BoxCollider>().enabled, Is.True, "its collider is back: it is a body on the worker that owns it");
            Assert.That(arrived.gameObject.scene, Is.EqualTo(_rig.HallScene));
            Assert.That(arrived.GetComponent<Rigidbody>().isKinematic, Is.False);
            Assert.That(H.W.Find(id).HasAuthority, Is.False);
            yield break;
        }
    }
}
