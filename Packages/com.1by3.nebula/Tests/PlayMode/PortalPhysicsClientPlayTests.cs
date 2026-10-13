using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-432 (<c>docs/portals.md</c> D25, D27, D28) on a client: for its pawn's scope it builds the static throat, the aperture collider and a proxy for every
    /// replica of the other scope in the band, and poses the proxy from the replica's newest authoritative state (the sample its worker posed its own proxy from),
    /// never from the pose the replica is drawn at. The pawn is in the street, a replica in the hall; the link is an open fixed portal known from its records.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalPhysicsClientPlayTests
    {
        private static ulong _scopes = 93000;
        private ulong HallScope;
        private const ulong PortalId = 7700;
        private const ulong LinkId = 7701;
        private readonly List<GameObject> _made = new List<GameObject>();
        private Container _street, _hall;
        private NetworkIdentity _pawn, _replica;
        private PortalPhysics _client;

        [SetUp]
        public void SetUp()
        {
            NebulaRuntime.Reset();
            NebulaRuntime.IsClient = true;
            HallScope = ++_scopes;
            var go = new GameObject("street");
            _made.Add(go);
            _street = go.AddComponent<Container>();
            _street.ContainerId = "client-street";
            _street.Size = new Vector3(100f, 50f, 100f);
            _street.Center = new Vector3(0f, 25f, 0f);
            go.transform.position = new Vector3(20f, 0f, 20f);
            ContainerRegistry.Rebuild();
            _hall = ContainerRegistry.RegisterRuntime(HallScope, new Bounds(new Vector3(20f, 2f, 20f), new Vector3(20f, 10f, 20f)), new InstanceContainerInfo { InstanceId = HallScope });
            Assert.That(InstanceScenes.Prepare(_hall), Is.True);
            Floor("street floor", _street.transform, new Vector3(20f, -0.5f, 14f), new Vector3(30f, 1f, 16f));
            Floor("hall floor", _hall.transform, new Vector3(20f, -0.5f, 24f), new Vector3(8f, 1f, 8f));

            _pawn = Entity("pawn", _street, new Vector3(20f, 1f, 14f), addBox: false);
            _replica = Entity("replica", _hall, new Vector3(20f, 0.5f, 20.3f), addBox: true);
            _replica.Interpolator = _replica.gameObject.AddComponent<RemoteInterpolator>();

            var portal = new PortalRecord
            {
                PortalId = PortalId, Id = "door", NearScope = 0UL, NearContainerId = _street.ContainerId, NearContainer = ContainerRef.Of(_street),
                NearOrigin = new Double3(20.0, 2.5, 20.0), NearRotation = Quaternion.identity,
                FarKind = PortalFarKind.Fixed, FarScope = HallScope, FarOrigin = new Double3(20.0, 2.5, 20.0), FarRotation = Quaternion.identity,
                TemplateId = "", ApertureKind = PortalApertureKind.Box, HalfWidth = 2f, HalfHeight = 2.5f, BandMargin = 0.5f, BandDepth = 2f, WindowMargin = 8f, WindowDepth = 48f,
            };
            Portals.ApplyRecords(new PortalsUpdate { Upserts = { portal } });
            Portals.ApplyLinks(new PortalLinksUpdate
            {
                Upserts =
                {
                    new PortalLinkRecord
                    {
                        LinkId = LinkId, PortalId = PortalId, FarScope = HallScope, FarEntryId = _hall.ContainerId, FarEntry = ContainerRef.Of(_hall),
                        State = PortalState.Open, Flags = PortalLinkFlags.Member | PortalLinkFlags.Observer, Generation = 1,
                    },
                },
            });
            _client = new PortalPhysics(null);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _client?.Dispose();
            Portals.ResetClient();
            foreach (var go in _made) if (go != null) Object.DestroyImmediate(go);
            _made.Clear();
            ContainerRegistry.UnregisterRuntime(HallScope);
            ContainerRegistry.Rebuild();
            InstanceScenes.Reset();
            NebulaRuntime.Reset();
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.name.StartsWith("Nebula instance") && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
            yield return null;
            yield return null;
        }

        private void Floor(string name, Transform parent, Vector3 center, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            go.AddComponent<BoxCollider>().size = size;
            _made.Add(go);
        }

        private NetworkIdentity Entity(string name, Container container, Vector3 at, bool addBox)
        {
            var go = new GameObject(name);
            _made.Add(go);
            if (addBox) go.AddComponent<BoxCollider>().size = Vector3.one;
            var entity = go.AddComponent<NetworkIdentity>();
            entity.NetId = (ulong)(100 + _made.Count);
            entity.SetContainer(container);
            go.transform.position = at;
            entity.IsSpawned = true;
            return entity;
        }

        [UnityTest]
        public IEnumerator AReplicaInTheBandGetsAProxyInThePawnsSceneAndTheThroatAndTheApertureStand()
        {
            _client.StepClient(_pawn, new[] { _pawn, _replica }, 10);
            var proxies = PortalPhysicsRig.ProxiesOf(_replica);
            Assert.That(proxies.Count, Is.EqualTo(1), "one proxy for the replica's one collider");
            Assert.That(proxies[0].gameObject.scene, Is.EqualTo(_pawn.gameObject.scene), "in the pawn's scene, not the replica's");
            var statics = PortalPhysicsRig.Proxies(p => p.IsStatic && !p.IsAperture);
            Assert.That(statics.Count, Is.GreaterThanOrEqualTo(1), "the hall's floor stands in the street's scene as part of the throat");
            Assert.That(statics.TrueForAll(p => p.gameObject.scene == _pawn.gameObject.scene), Is.True);
            var apertures = PortalPhysicsRig.Proxies(p => p.IsAperture);
            Assert.That(apertures.Count, Is.EqualTo(1), "and the aperture collider of the street's side");
            Assert.That(apertures[0].gameObject.scene, Is.EqualTo(_pawn.gameObject.scene));
            yield break;
        }

        [UnityTest]
        public IEnumerator AProxyIsPosedFromTheReplicasNewestAuthoritativeStateNotFromWhereItIsDrawn()
        {
            var interpolator = _replica.GetComponent<RemoteInterpolator>();
            var older = new Vector3(20f, 0.5f, 20.1f);
            var newest = new Vector3(20f, 0.5f, 20.4f);
            interpolator.Push(10, _hall, _hall.ToLocal(older), Quaternion.identity, Vector3.zero);
            interpolator.Push(11, _hall, _hall.ToLocal(newest), Quaternion.identity, Vector3.zero);
            // Drawn between the two samples, as an interpolated replica is.
            _replica.transform.position = Vector3.Lerp(older, newest, 0.5f);

            _client.StepClient(_pawn, new[] { _pawn, _replica }, 12);
            var proxy = PortalPhysicsRig.ProxiesOf(_replica)[0];
            Assert.That(proxy.transform.position.z, Is.EqualTo(newest.z).Within(0.001f), "the newest sample's pose (the one the worker posed from)");
            Assert.That(Mathf.Abs(proxy.transform.position.z - _replica.transform.position.z), Is.GreaterThan(0.1f), "not the interpolated pose it is drawn at");

            // A later sample moves it; with nothing new the proxy is left alone.
            var latest = new Vector3(20f, 0.5f, 20.5f);
            interpolator.Push(12, _hall, _hall.ToLocal(latest), Quaternion.identity, Vector3.zero);
            _client.StepClient(_pawn, new[] { _pawn, _replica }, 13);
            Assert.That(proxy.transform.position.z, Is.EqualTo(latest.z).Within(0.001f));
            yield break;
        }

        [UnityTest]
        public IEnumerator ABodyOutsideTheBandHasNoProxyAndTheApertureIsSolidWithoutALink()
        {
            _replica.transform.position = new Vector3(20f, 0.5f, 28f);
            _client.StepClient(_pawn, new[] { _pawn, _replica }, 10);
            Assert.That(PortalPhysicsRig.ProxiesOf(_replica).Count, Is.EqualTo(0));
            Portals.ApplyLinks(new PortalLinksUpdate { Removes = { LinkId } });
            _client.StepClient(_pawn, new[] { _pawn, _replica }, 11);
            yield return null; // what Nebula destroys goes at the end of the frame
            Assert.That(PortalPhysicsRig.Proxies(p => p.IsAperture).Count, Is.EqualTo(1), "a face the pawn is on the near side of is solid whether or not anything has opened it");
            Assert.That(PortalPhysicsRig.Proxies(p => p.IsStatic && !p.IsAperture).Count, Is.EqualTo(0), "and its throat is gone with the link");
            yield break;
        }
    }
}
