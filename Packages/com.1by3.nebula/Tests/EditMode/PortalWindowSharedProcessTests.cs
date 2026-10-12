using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// A window onto ground that is a frame with regions of its own, seen by a network gateway that shares its process with a worker
    /// (<c>docs/portals.md</c> D20, NEB-431; the Editor's Multiplayer Play Mode loop or any in-process host). The registry of such a process
    /// also holds the carried frame's box, which a network gateway of its own never does. The gateway must still know that box as a frame
    /// with regions of its own from its lease row, or a client inside a room looking out through a door on that ground finds no frame,
    /// and is sent none of the ground, nor the carrier it is fixed in. <c>PortalWindowFrameTests</c> (Services~) is the same rule on a
    /// gateway of its own.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalWindowSharedProcessTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly Type GatewayType = typeof(NebulaGateway);
        private const ulong PlanetId = 77, PawnId = 9001, ClientId = 42, Scope = 5151;
        private const ulong BunkerRuntime = 5100, GroundRuntime = 9_000_001, FarGroundRuntime = 9_000_002;
        private static readonly Vector3 PlanetAt = new Vector3(100_000f, 0f, 0f);

        private readonly List<GameObject> _objects = new List<GameObject>();
        private Container _space, _planetBox;
        private NetworkIdentity _planet;
        private NebulaGateway _gateway;
        private NebulaConfig _config;
        private object _client, _pawn;

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            var space = new GameObject("space");
            _objects.Add(space);
            _space = space.AddComponent<Container>();
            _space.ContainerId = "space";
            _space.Size = new Vector3(400_000f, 400_000f, 400_000f);
            ContainerRegistry.Rebuild();

            // The planet, a carrier whose box is a frame with regions of its own, as a worker in this process registered it when it spawned.
            var planet = new GameObject("planet");
            _objects.Add(planet);
            planet.transform.position = PlanetAt;
            _planet = planet.AddComponent<NetworkIdentity>();
            _planet.NetId = PlanetId;
            _planetBox = planet.AddComponent<Container>();
            _planetBox.ContainerId = "planet";
            _planetBox.Size = new Vector3(8000f, 1000f, 8000f);
            _planetBox.OwnPhysicsFrame = true;
            _planetBox.FrameInterest = FrameInterestMode.OwnRegions;
            ContainerRegistry.RegisterDynamic(_planetBox, _planet);
            Assert.That(ContainerRegistry.FindById(_planetBox.ContainerId), Is.SameAs(_planetBox), "this process's registry holds the planet's box, as a worker's does");

            _gateway = new GameObject("gateway").AddComponent<NebulaGateway>();
            _objects.Add(_gateway.gameObject);
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            GatewayType.GetField("<Config>k__BackingField", Flags).SetValue(_gateway, _config);
            GatewayType.GetField("_transport", Flags).SetValue(_gateway, new ConformanceMesh.RecordingTransport());
            Call("InitializeInterest");

            // The lease rows the gateway mirrors: the planet's box, two chunks of its ground fixed in it (one under the door, one 3 km along)
            // and the room the player is in, one container of another scope.
            string planetId = _planetBox.ContainerId;
            Call("IndexLeases", (IReadOnlyList<LeaseInfo>)new List<LeaseInfo>
            {
                new LeaseInfo { ContainerId = planetId, WorkerId = "w1", Epoch = 1, State = LeaseState.Active, OwnPhysicsFrame = true, FrameInterest = FrameInterestMode.OwnRegions },
                Ground(GroundRuntime, new Vector3(10f, 0f, 10f)),
                Ground(FarGroundRuntime, new Vector3(3000f, 0f, 10f)),
                new LeaseInfo
                {
                    ContainerId = ContainerRegistry.RuntimeContainerId(BunkerRuntime), WorkerId = "w1", Epoch = 1, State = LeaseState.Active,
                    HasBounds = true, BoundsCenter = new Vector3(-50_000f, 0f, 0f), BoundsSize = new Vector3(40f, 40f, 40f), Instance = new InstanceContainerInfo { InstanceId = Scope },
                },
            });

            Call("RefreshAbsolute", Record(PlanetId, ContainerRef.Of(_space), PlanetAt));
            // The player in the room, 5 m inside its far face (the wall at -50020, facing +x).
            _pawn = Record(PawnId, ContainerRef.Runtime(BunkerRuntime), new Vector3(-15f, 0f, 0f));
            Set(_pawn, "OwnerClientId", ClientId);
            Call("RefreshAbsolute", _pawn);
            var clientType = GatewayType.GetNestedType("ClientConn", BindingFlags.NonPublic);
            _client = Activator.CreateInstance(clientType, true);
            Set(_client, "ClientId", ClientId); Set(_client, "PeerId", 7); Set(_client, "Welcomed", true); Set(_client, "PawnNetId", PawnId); Set(_client, "LastScope", Scope);
            Dictionary("_clientsById").Add(ClientId, _client);
        }

        private LeaseInfo Ground(ulong runtimeId, Vector3 center) => new LeaseInfo
        {
            ContainerId = ContainerRegistry.RuntimeContainerId(runtimeId), WorkerId = "w1", Epoch = 1, State = LeaseState.Active,
            HasBounds = true, ParentId = _planetBox.ContainerId, Center = Double3.From(center), BoundsSize = new Vector3(64f, 64f, 64f), Authority = ContainerAuthority.Leased,
        };

        [TearDown]
        public void TearDown()
        {
            ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
            if (_planetBox != null) ContainerRegistry.UnregisterDynamic(_planetBox);
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
            if (_config != null) Object.DestroyImmediate(_config);
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        private object Call(string name, params object[] args)
        {
            try { return GatewayType.GetMethod(name, Flags).Invoke(_gateway, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }

        private static object Field(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
        private IDictionary Dictionary(string name) => (IDictionary)GatewayType.GetField(name, Flags).GetValue(_gateway);
        private static object Nested(string name) => Activator.CreateInstance(GatewayType.GetNestedType(name, BindingFlags.NonPublic), true);

        private object Record(ulong netId, ContainerRef container, Vector3 at)
        {
            var rec = Nested("EntityRecord");
            Set(rec, "NetId", netId);
            Set(rec, "Container", container);
            Set(rec, "OwnerWorkerIndex", (ushort)1);
            var spawn = (EntitySpawnMsg)Field(rec, "LastSpawn");
            spawn.NetId = netId; spawn.Container = container; spawn.LocalPosition = at; spawn.LocalRotation = Quaternion.identity; spawn.LocalScale = Vector3.one;
            Set(rec, "LastSpawn", spawn);
            Dictionary("_entities").Add(netId, rec);
            return rec;
        }

        private void OpenDoor()
        {
            // The door on the planet's ground at frame-local (10, 0, 10), facing +z into the room, whose far face is the room's wall.
            var portal = new PortalRecord
            {
                PortalId = 1, Id = "door", NearScope = 0, NearContainerId = ContainerRegistry.RuntimeContainerId(GroundRuntime), NearContainer = ContainerRef.Runtime(GroundRuntime),
                NearOrigin = new Double3(PlanetAt.x + 10.0, 0.0, 10.0), NearRotation = Quaternion.identity, FarKind = PortalFarKind.Template, TemplateId = "room",
                FarOrigin = new Double3(-50_020, 0, 0), FarRotation = Quaternion.Euler(0f, 90f, 0f),
                ApertureKind = PortalApertureKind.Box, HalfWidth = 1.5f, HalfHeight = 2.2f, BandMargin = 0.5f, BandDepth = 2f, WindowMargin = 8f, WindowDepth = 48f,
            };
            var entry = Nested("PortalEntry");
            Set(entry, "Record", portal);
            Dictionary("_portals").Add(portal.PortalId, entry);
            var link = Nested("LinkEntry");
            Set(link, "Record", new PortalLinkRecord { LinkId = 2, PortalId = 1, FarScope = Scope, FarEntryId = ContainerRegistry.RuntimeContainerId(BunkerRuntime), State = PortalState.Open });
            ((HashSet<ulong>)Field(link, "Observers")).Add(ClientId);
            Dictionary("_portalLinks").Add(2UL, link);
            ((IList)Field(entry, "Links")).Add(link);
        }

        [Test]
        public void ACarriedFrameTheRegistryAlsoHoldsIsStillAFrameWithRegionsOfItsOwn()
        {
            var carriers = (HashSet<ulong>)GatewayType.GetField("_ownRegionCarriers", Flags).GetValue(_gateway);
            Assert.That(carriers, Does.Contain(PlanetId), "known as a carried frame by its lease row, though this process's registry resolves its box");
            Assert.That((bool)Call("IsOwnRegionsFrame", ContainerRef.Dynamic(PlanetId)), Is.True);
            Assert.That(Call("CarrierOf", ContainerRef.Dynamic(PlanetId)), Is.EqualTo(0UL), "and, as on a gateway of its own, a planet carries nothing for interest");
        }

        [Test]
        public void APlayerInsideLooksOutAtTheGroundAtTheDoorAndThePlanetItIsFixedIn()
        {
            OpenDoor();
            Call("RefreshWindows", _client, true);

            Assert.That(((IList)Field(_client, "Windows")).Count, Is.EqualTo(1), "the player looks out through the door");
            var frames = (IList)Field(_client, "FrameWindows");
            Assert.That(frames.Count, Is.EqualTo(1), "and the window reaches into the planet's frame, where its ground is");
            Assert.That((ulong)Field(frames[0], "FrameKey"), Is.EqualTo(RegionKeys.FrameKeyOf(ContainerRef.Dynamic(PlanetId))));
            Assert.That(((HashSet<ulong>)Field(_client, "WindowCarriers")).ToArray(), Is.EquivalentTo(new[] { PlanetId }), "the planet the ground is fixed in, and no more");

            Call("EvaluateClient", _client, 0.0);
            var rows = ((HashSet<string>)Field(_client, "KnownContainers")).ToList();
            Assert.That(rows, Does.Contain(ContainerRegistry.RuntimeContainerId(GroundRuntime)), "the ground in front of the door is sent");
            Assert.That(rows, Does.Not.Contain(ContainerRegistry.RuntimeContainerId(FarGroundRuntime)), "the rest of the planet's ground is not");
            Assert.That((bool)Call("CanObserve", _client, Dictionary("_entities")[PlanetId]), Is.True, "the player may hold the planet the ground is fixed in");
        }
    }
}
