using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-432 (<c>docs/portals.md</c> D25, D26) on a client whose pawn stands in a physics frame: the same pieces a worker builds for the planet's side, in the frame's
    /// scene and its simulation space, for the pawn's prediction. The world is the one of <see cref="FramedPortalRig"/> (a door on a planet that is a carrier, turned, 12,000
    /// km from its scope's origin); its worker has built what it builds and is then put aside, and the process becomes a rendering client that knows the portal from its
    /// records and holds a replica of a crate in the vault.
    /// </summary>
    [Category("Conformance")]
    public sealed class FramedPortalClientPlayTests
    {
        private FramedPortalRig _rig;
        private PortalPhysics _client;
        private bool _wasServer, _wasClient;
        private NetworkIdentity _pawn, _replica;

        [SetUp]
        public void SetUp()
        {
            _rig = new FramedPortalRig();
            _wasServer = NebulaRuntime.IsServer;
            _wasClient = NebulaRuntime.IsClient;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _client?.Dispose();
            Portals.ResetClient();
            PhysicsFrames.EndSimulation();
            NebulaRuntime.IsServer = _wasServer;
            NebulaRuntime.IsClient = _wasClient;
            _rig.Dispose();
            for (int i = 0; i < 30 && PortalPhysicsRig.NebulaScenesLoaded(); i++) yield return null;
            yield return null;
        }

        /// <summary>The worker has opened the door and built its half; it is put aside, and this process becomes the client of a pawn on the planet with a replica in the vault.</summary>
        private void BecomeClient(float replicaZ = 0.3f)
        {
            var link = _rig.OpenDoor();
            var crate = _rig.HallCrate(0f, 0.5f, replicaZ, FramedPortalRig.Slippery());
            _rig.Steps(5);
            _replica = crate;
            _rig.W.Instance.PortalPhysics.Dispose();
            foreach (var proxy in Object.FindObjectsByType<PortalProxy>(FindObjectsInactive.Include)) Object.DestroyImmediate(proxy.gameObject);
            _pawn = _rig.Player;
            _pawn.transform.position = _rig.At(0f, 1f, -0.2f);
            Physics.SyncTransforms();

            // What the worker publishes to the client, taken while this process is still the worker that reads its frames at their simulation pose.
            var portalRecord = _rig.Portal.ToRecord();
            var record = Portal.ToLinkRecord(link);
            record.Flags |= PortalLinkFlags.Member | PortalLinkFlags.Observer;
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            Portals.ApplyRecords(new PortalsUpdate { Upserts = { portalRecord } });
            Portals.ApplyLinks(new PortalLinksUpdate { Upserts = { record } });
            _client = new PortalPhysics(null);
        }

        [UnityTest]
        public IEnumerator APawnInThePlanetsFrameGetsTheApertureTheThroatAndAProxyOfTheVaultsReplicaInTheFramesScene()
        {
            BecomeClient();
            var space = _pawn.Space;
            Assume.That(space, Is.SameAs(_rig.Planet.Carried));
            _client.StepClient(_pawn, new[] { _pawn, _replica }, 10);

            Assert.That(PhysicsFrames.InSimulationPose(space), Is.False, "the frame is back at its render pose when the step is over");
            var apertures = PortalPhysicsRig.Proxies(p => p.IsAperture);
            Assert.That(apertures.Count, Is.EqualTo(1), "the aperture collider of the pawn's side");
            Assert.That(apertures[0].gameObject.scene, Is.EqualTo(_rig.PlanetScene), "in the planet's frame scene, not the scope's");
            Assert.That(Vector3.Distance(apertures[0].transform.position, _rig.SimAt(0f, FramedPortalRig.FaceHeight, 0f)), Is.LessThan(0.003f), "at the door in the frame's simulation space, as the worker had it");
            Assert.That(Quaternion.Angle(apertures[0].transform.rotation, FramedPortalRig.DoorTurn), Is.LessThan(0.01f));

            var statics = PortalPhysicsRig.Proxies(p => p.IsStatic && !p.IsAperture);
            Assert.That(statics.Count, Is.GreaterThanOrEqualTo(1), "the vault's floor stands in the planet's scene as part of the throat");
            Assert.That(statics.TrueForAll(p => p.gameObject.scene == _rig.PlanetScene), Is.True);

            var proxies = PortalPhysicsRig.ProxiesOf(_replica);
            Assert.That(proxies.Count, Is.EqualTo(1), "one proxy for the replica's one collider");
            Assert.That(proxies[0].gameObject.scene, Is.EqualTo(_rig.PlanetScene), "in the pawn's frame scene, not the replica's");
            // The replica stands 0.3 m behind the face, 0.5 m up from the vault's floor: the same place in the door's axes on the planet's side.
            Assert.That(Vector3.Distance(proxies[0].transform.position, _rig.SimAt(0f, 0.5f, 0.3f)), Is.LessThan(0.01f), "posed through the link into the frame's simulation space");
            yield break;
        }

        [UnityTest]
        public IEnumerator TheClientsPiecesStandWhereTheWorkersDidAfterTheFramesOriginMoves()
        {
            BecomeClient();
            _client.StepClient(_pawn, new[] { _pawn, _replica }, 10);
            var shift = new Vector3(2048f, 0f, 1024f);
            PhysicsFrames.ShiftOrigin(_rig.Planet.Carried.Frame, FramedPortalRig.Door + shift);
            _client.StepClient(_pawn, new[] { _pawn, _replica }, 11);

            var aperture = PortalPhysicsRig.Proxies(p => p.IsAperture)[0];
            Assert.That(Vector3.Distance(aperture.transform.position, _rig.SimAt(0f, FramedPortalRig.FaceHeight, 0f)), Is.LessThan(0.003f), "the aperture collider follows the door");
            Assert.That(aperture.transform.position.magnitude, Is.LessThan(4096f));
            var proxy = PortalPhysicsRig.ProxiesOf(_replica)[0];
            Assert.That(Vector3.Distance(proxy.transform.position, _rig.SimAt(0f, 0.5f, 0.3f)), Is.LessThan(0.01f), "and so does the replica's proxy");
            yield break;
        }
    }
}
