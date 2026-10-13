using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-433 (<c>docs/portals.md</c> D32) on a client, for prediction and aim: the pawn is in the street, a replica in the hall, and the link is an open fixed
    /// portal known from its records. The client queries the scenes it holds against the replicas as they stand; the link it is a member of is the one it goes through.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalQueriesClientPlayTests
    {
        private static ulong _scopes = 94000;
        private ulong HallScope;
        private const ulong PortalId = 7800;
        private const ulong LinkId = 7801;
        private readonly List<GameObject> _made = new List<GameObject>();
        private Container _street, _hall;
        private NetworkIdentity _pawn, _replica;
        private PortalPhysics _client;
        private PortalQueries _queries;

        [SetUp]
        public void SetUp()
        {
            NebulaRuntime.Reset();
            NebulaRuntime.IsClient = true;
            HallScope = ++_scopes;
            var go = new GameObject("street");
            _made.Add(go);
            _street = go.AddComponent<Container>();
            _street.ContainerId = "client-street-q";
            _street.Size = new Vector3(100f, 50f, 100f);
            _street.Center = new Vector3(0f, 25f, 0f);
            go.transform.position = new Vector3(20f, 0f, 20f);
            ContainerRegistry.Rebuild();
            _hall = ContainerRegistry.RegisterRuntime(HallScope, new Bounds(new Vector3(20f, 2f, 20f), new Vector3(20f, 10f, 20f)), new InstanceContainerInfo { InstanceId = HallScope });
            Assert.That(InstanceScenes.Prepare(_hall), Is.True);
            Floor("street floor", _street.transform, new Vector3(20f, -0.5f, 14f), new Vector3(30f, 1f, 16f));
            Floor("hall floor", _hall.transform, new Vector3(20f, -0.5f, 24f), new Vector3(8f, 1f, 8f));

            _pawn = Entity("pawn", _street, new Vector3(20f, 1f, 14f), addBox: false);
            _replica = Entity("replica", _hall, new Vector3(20f, 0.5f, 22f), addBox: true);
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
            _queries = new PortalQueries(_client, null);
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
            entity.NetId = (ulong)(200 + _made.Count);
            entity.SetContainer(container);
            go.transform.position = at;
            entity.IsSpawned = true;
            return entity;
        }

        [UnityTest]
        public IEnumerator AClientCastsThroughTheLinkItIsAMemberOfAndHitsAReplicaOfTheOtherScope()
        {
            _client.StepClient(_pawn, new[] { _pawn, _replica }, 10);
            Physics.SyncTransforms();
            Assert.That(_queries.Raycast(PortalAsker.Of(_pawn), 0UL, new Vector3(20f, 0.5f, 14f), Vector3.forward, 30f, out var hit), Is.True);
            Assert.That(hit.Entity, Is.SameAs(_replica));
            Assert.That(hit.Scope, Is.EqualTo(HallScope));
            Assert.That(hit.Flags, Is.EqualTo(PortalHitFlags.ThroughPortal));
            Assert.That(hit.LinkId, Is.EqualTo(LinkId));
            Assert.That(hit.Distance, Is.EqualTo(7.5f).Within(0.02f));
            Assert.That(hit.PointInScope.z, Is.EqualTo(21.5f).Within(0.01f));

            // The pawn's own collider, were it in the way, is not a hit; a tick means nothing to a client, which keeps no history.
            var options = new PortalQueryOptions { Tick = 3, RewindNear = true };
            Assert.That(_queries.Raycast(PortalAsker.Of(_pawn), 0UL, new Vector3(20f, 0.5f, 14f), Vector3.forward, 30f, out var withTick, options), Is.True);
            Assert.That(withTick.Entity, Is.SameAs(_replica));
            Assert.That(withTick.Flags & PortalHitFlags.Rewound, Is.EqualTo(PortalHitFlags.None));
            yield break;
        }

        [UnityTest]
        public IEnumerator ALinkRemovedLeavesTheFaceSolid()
        {
            _client.StepClient(_pawn, new[] { _pawn, _replica }, 10);
            Portals.ApplyLinks(new PortalLinksUpdate { Removes = { LinkId } });
            _client.StepClient(_pawn, new[] { _pawn, _replica }, 11);
            yield return null;
            Assert.That(_queries.Raycast(PortalAsker.None, 0UL, new Vector3(20f, 0.5f, 14f), Vector3.forward, 30f, out var hit), Is.True);
            Assert.That(hit.IsAperture, Is.True);
            Assert.That(hit.Distance, Is.EqualTo(6f).Within(0.01f));
            Assert.That(hit.Scope, Is.EqualTo(0UL));
            yield break;
        }
    }
}
