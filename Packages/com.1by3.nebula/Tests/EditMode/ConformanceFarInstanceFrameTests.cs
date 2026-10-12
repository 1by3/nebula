using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-426 (<c>docs/scope-frames.md</c> D11): an instance prepared 50,000 km from the public origin (a dungeon behind a
    /// door on a planet far out in its system's scope) gets an origin frame of its own, pinned near the instance's origin.
    /// A player who walks through the door is simulated near Unity's origin inside the instance, to the millimetre of the
    /// absolute point it walked through, and walks back out onto the planet just as precisely. Before, the instance lived
    /// in the public frame, 5×10^7 m from Unity's origin, where a float is 4 m apart.
    /// <para>Tier B, set up as <see cref="ConformanceFramedPortalTests"/>, with the planet far out. Its door is a <see cref="Portal"/> since NEB-428.</para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFarInstanceFrameTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Scope = "space/far-vaults";
        private const string GridKey = "planet/far-vaults/0";
        private const string Owner = "player-a";
        private static readonly Vector3 SpaceCell = new Vector3(8192f, 8192f, 8192f);
        /// <summary>The planet's carrier, absolute in its scope: 50,000 km and more out on two axes.</summary>
        private static readonly Vector3 PlanetAt = new Vector3(50_000_000f, -280_000f, -60_000_000f);
        private static readonly Vector3 PlanetBox = new Vector3(4096f, 400f, 4096f);
        private static readonly Vector3 GroundCell = new Vector3(128f, 400f, 128f);
        /// <summary>The door, in the planet's own coordinates, with fractions no float 10^7 m out holds.</summary>
        private static readonly Vector3 Door = new Vector3(320.437f, 0.25f, 192.811f);
        private const double Millimetre = 1e-3;
        private const float NearOrigin = 10_000f;

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private WorldDefinition _definition;
        private InstanceTemplate _template;
        private ushort _planetPrefab, _pawnPrefab;
        private uint _tick;
        private Portal _portal;
        private ConformanceMesh.Gateway _gateway;

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = GroundCell;
            NebulaWorld.LoadRuntime(_definition); // the public frame, at absolute (0,0,0)
            _mesh = new ConformanceMesh(1);
            _gateway = _mesh.AddGateway("gw");
            _mesh.LinkGateway(_gateway);
            _mesh.Config.GhostBandMargin = -1f;
            _plane = new LocalControlPlane();
            _plane.Connect();
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", Flags).SetValue(W.Instance, _plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", Flags).GetValue(W.Instance);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", Flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", Flags).SetValue(registration, _plane.DocumentId ?? "");
            _plane.RegisterWorker(W.Id, W.Index, "127.0.0.1", 7000);
            _plane.HeartbeatWorker(W.Id, WorkerStatus.Ready, default);

            _template = ScriptableObject.CreateInstance<InstanceTemplate>();
            _template.TemplateId = "vault";
            _template.Parts = new[] { new InstanceTemplate.Part { Id = "hall", Bounds = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f)) } };
            _template.ObservePublic = false;

            var planet = new GameObject("planet-prefab");
            planet.AddComponent<NetworkIdentity>();
            var box = planet.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = PlanetBox;
            box.Center = Vector3.zero;
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            planet.AddComponent<NetworkTransform>();
            _planetPrefab = _mesh.RegisterPrefab(planet);
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);
            _tick = 0;
        }

        [TearDown]
        public void TearDown()
        {
            if (_portal != null) typeof(Portal).GetMethod("OnDisable", Flags).Invoke(_portal, null);
            _portal = null;
            _mesh.Dispose();
            _plane.Dispose();
            NebulaChunks.ResetForNewSession();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
            Object.DestroyImmediate(_template);
            Object.DestroyImmediate(_definition);
            Portals.ResetForNewSession();
        }

        private void Mirror()
        {
            ContainerRegistry.SyncRuntime(_plane.Leases);
            foreach (var lease in _plane.Leases)
                if (lease.WorkerId == W.Id) ContainerRegistry.ApplyLease(lease.ContainerId, W.Id, W.Index, lease.Epoch);
        }

        private void Tick()
        {
            W.Tick(++_tick);
            Mirror();
        }

        /// <summary>Open the door for the player's party and run the ticks that get it ready: activated, content ready, client warm.</summary>
        private PortalLink OpenDoor()
        {
            var link = _portal.Open(Owner, new ulong[] { 7 });
            Tick();
            Tick();
            _mesh.FromGateway(_gateway, W, w => new PortalWarmMsg { LinkId = link.Id, Generation = link.Generation, ClientId = 7 }.Write(w));
            Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), link.ToString());
            return link;
        }

        private static double Distance(Double3 a, Double3 b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

        /// <summary>
        /// The scope with its origin moved to the planet's chunk (as a worker that leases it keeps it), the planet in that
        /// chunk, its hosted ground with the door's chunk leased, and the portal built at the door.
        /// </summary>
        private Container Build(out NetworkIdentity planet, out RuntimeGrid space) => Build(out planet, out space, Quaternion.identity);

        private Container Build(out NetworkIdentity planet, out RuntimeGrid space, Quaternion doorRotation)
        {
            space = new RuntimeGrid(SpaceCell, planar: false, scopeKey: Scope);
            NebulaChunks.Activate(space, NebulaRoles.Worker, true, null);
            _plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Scope,
                Definition = new ChunkGridDefinition { CellSize = SpaceCell, Planar = false }.ToScopeDefinition(),
                PreferredWorkerId = W.Id,
            });
            var coord = space.CoordOfAbsolute(PlanetAt);
            space.ShiftOrigin(coord);
            var spaceInstance = new InstanceContainerInfo { InstanceId = space.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(coord) };
            var spaceChunk = ContainerRegistry.RegisterRuntime(space.IdOf(coord), space.BoundsOf(coord), spaceInstance);
            ContainerRegistry.ApplyLease(spaceChunk.ContainerId, W.Id, W.Index, 1);
            _plane.EnsureRuntimeContainer(spaceChunk.ContainerId, ContainerRegistry.ToAbsolute(space.BoundsOf(coord), space.InstanceId), W.Id, spaceInstance);

            var at = ContainerRegistry.ToFrame(Double3.From(PlanetAt), space.InstanceId);
            NetworkIdentity p = null;
            W.Act(() =>
            {
                p = NetworkPrefabs.Instantiate(_planetPrefab, at, Quaternion.identity, spaceChunk.ContentRoot);
                W.Instance.SpawnServerDriven(p, spaceChunk);
            });
            planet = p;
            Assume.That(planet.Carried, Is.Not.Null, "the planet carries its box");
            Assume.That(planet.Carried.Frame, Is.Not.Null, "and its box has a frame of its own");

            var ground = RuntimeGrid.Hosted(new ChunkGridDefinition { CellSize = GroundCell, Planar = true }, Scope, GridKey, planet.Carried.ContainerId);
            NebulaChunks.Activate(ground, NebulaRoles.Worker, true, null);
            var groundCoord = ground.CoordOfAbsolute(Door);
            var info = new InstanceContainerInfo { InstanceId = space.InstanceId, ScopeKey = Scope, PartId = ground.PartIdOf(groundCoord) };
            _plane.EnsureRuntimeContainer(ground.ContainerIdOf(groundCoord), ground.PlacementOf(groundCoord), W.Id, info);
            Mirror();
            var chunk = ContainerRegistry.GetRuntime(ground.IdOf(groundCoord));
            Assume.That(chunk, Is.Not.Null, "the door's chunk registers under the planet");

            var root = new GameObject("content");
            root.transform.SetParent(chunk.transform, false);
            var go = new GameObject("vault door");
            go.transform.SetParent(root.transform, false);
            go.transform.position = planet.Carried.Frame.LocalToSimulation(Door);
            go.transform.rotation = doorRotation;
            _portal = go.AddComponent<Portal>();
            _portal.Id = "vault-door";
            _portal.FarTemplate = _template;
            _portal.Aperture = PortalAperture.Box(2f, 2.5f);
            typeof(Portal).GetMethod("OnEnable", Flags).Invoke(_portal, null);
            return chunk;
        }

        private NetworkIdentity Player(Container chunk, NetworkIdentity planet, Vector3 local)
        {
            NetworkIdentity e = null;
            W.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(_pawnPrefab, local, Quaternion.identity, chunk.ContentRoot);
                W.Instance.SpawnServerDriven(e, chunk);
            });
            e.transform.position = planet.Carried.Frame.LocalToSimulation(local);
            e.OwnerClientId = 7;
            e.OwnerIdentity = Owner;
            return e;
        }

        /// <summary>A planet-local point as an absolute one, in double, independent of the boundary code.</summary>
        private static Double3 Absolute(NetworkIdentity planet, Vector3 planetLocal) =>
            ContainerRegistry.ToAbsolutePrecise(planet.ScopePositionPrecise + Double3.From(planetLocal), planet.InstanceId);

        [Test]
        public void AFarInstanceGetsAFrameOfItsOwnNearUnitysOrigin()
        {
            var chunk = Build(out var planet, out _);
            var player = Player(chunk, planet, Door + new Vector3(0f, 1f, -6f));
            OpenDoor(); // the instance is activated at the door
            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
            Assert.That(hall, Is.Not.Null, "approaching the door prepares the instance");
            Assert.IsTrue(ScopeFrames.HasFrame(hall.InstanceId), "the instance has a frame of its own");
            Assert.IsTrue(ScopeFrames.Of(hall.InstanceId).IsPinned, "pinned near its origin");
            Assert.Less(hall.transform.position.magnitude, NearOrigin, "its container is near Unity's origin");

            // The lease row keeps the part's centre in double: the door plus the part's offset, to the millimetre.
            var expected = _portal.NearFace.Origin + new Double3(0, 2, 0);
            var row = _plane.FindLease(hall.ContainerId).Center;
            TestContext.WriteLine($"row error {Distance(expected, row):E3} m");
            Assert.Less(Distance(expected, row), Millimetre, "the part's absolute centre is not narrowed to float");
            Assert.Less(Distance(expected, ContainerRegistry.ToAbsolutePrecise(hall.WorldBounds.center, hall.InstanceId)), Millimetre,
                "and the container stands exactly there");
            Assert.IsNotNull(player);
        }

        [Test]
        public void APlayerCrossesIntoAFarInstanceAndBackOutToTheMillimetre()
        {
            var chunk = Build(out var planet, out _);
            var player = Player(chunk, planet, Door + new Vector3(0f, 1f, -6f));
            OpenDoor();
            var inside = Door + new Vector3(0f, 1f, 1f);
            player.transform.position = planet.Carried.Frame.LocalToSimulation(inside);
            var expectedIn = Absolute(planet, inside);
            Tick(); // through the opening: committed

            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
            Assert.AreSame(hall, player.Container, "the player crossed into the instance");
            Assert.AreEqual(NebulaWorker.InstanceKey("vault/" + Owner), player.InstanceId);
            Assert.Less(player.transform.position.magnitude, NearOrigin, "simulated near Unity's origin, not 5×10^7 m out");
            var actualIn = ContainerRegistry.ToAbsolutePrecise(player.ScopePositionPrecise, player.InstanceId);
            TestContext.WriteLine($"enter error {Distance(expectedIn, actualIn):E3} m at {player.transform.position}");
            Assert.Less(Distance(expectedIn, actualIn), Millimetre, "at the absolute point it walked through");
            var interiorLocal = actualIn - _portal.NearFace.Origin;
            Assert.Less(Distance(interiorLocal, Double3.From(inside - Door)), Millimetre, "which is that point relative to the instance's origin");

            // Back out, committed into the planet's chunk at the pose the player had when it stepped out.
            player.transform.position += new Vector3(0f, 0f, -1.5f);
            var expectedOut = expectedIn + new Double3(0, 0, -1.5);
            Tick();

            Assert.AreSame(chunk, player.Container, "back in the planet's chunk");
            Assert.AreSame(planet.Carried, player.Space, "in the planet's frame");
            var actualOut = Absolute(planet, planet.Carried.Frame.SimulationToLocal(player.transform.position));
            TestContext.WriteLine($"leave error {Distance(expectedOut, actualOut):E3} m");
            Assert.Less(Distance(expectedOut, actualOut), Millimetre, "where it walked out, to the millimetre");
        }

        [Test]
        public void AnInstanceFramePinsNearItsOriginAndGoesWithItsLastContainer()
        {
            var origin = new Double3(-69_812_950.423, -287_858.949, -89_203_063.320);
            ulong scope = ScopeKeys.Hash("far/instance");
            var info = new InstanceContainerInfo { InstanceId = scope, ScopeKey = "far/instance", PartId = "hall" };
            var c = ContainerRegistry.RegisterRuntime(ScopeKeys.Hash("far/instance/hall"), ContainerPlacement.Root(origin, new Vector3(8f, 4f, 8f)), info);
            Assert.IsNotNull(c);
            Assert.IsTrue(ScopeFrames.Of(scope).IsPinned);
            Assert.Less(c.transform.position.magnitude, GroundCell.magnitude, "within a cell of Unity's origin");
            Assert.Less(Distance(origin, ContainerRegistry.ToAbsolutePrecise(c.WorldBounds.center, scope)), Millimetre);
            // A grid naming the same scope later takes the frame over.
            Assert.IsFalse(ScopeFrames.Ensure("far/instance", scope, new Vector3(64f, 64f, 64f)).IsPinned, "a grid's frame replaces the pin");
            ScopeFrames.Remove("far/instance");
            ScopeFrames.Pin("far/instance", scope, origin.X, origin.Y, origin.Z);
            Assert.IsTrue(ContainerRegistry.UnregisterRuntime(c.RuntimeId));
            Assert.IsFalse(ScopeFrames.HasFrame(scope), "the pinned frame goes with the instance's last container");
        }

        /// <summary>A door turned 55° about x and 180° about y (a bunker on a sphere), as Station Nine's on Mass.</summary>
        private static readonly Quaternion Turned = Quaternion.Euler(0f, 180f, 0f) * Quaternion.Euler(55f, 0f, 0f);

        /// <summary>A point <paramref name="doorLocal"/> in the door's own axes, planet-local.</summary>
        private static Vector3 AtDoor(Vector3 doorLocal) => Door + Turned * doorLocal;

        [Test]
        public void ATurnedDoorTurnsItsInstanceAndCrossingsTurnWithIt()
        {
            var chunk = Build(out var planet, out _, Turned);
            var player = Player(chunk, planet, AtDoor(new Vector3(0f, 1f, -6f)));
            OpenDoor();
            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
            Assert.That(hall, Is.Not.Null, "the instance is prepared at the turned door");
            var row = _plane.FindLease(hall.ContainerId).Instance;
            Assert.IsTrue(row.HasPose, "its lease row carries its pose");
            Assert.Less(Quaternion.Angle(Turned, row.PoseRotation), 0.01f, "turned as the door is");
            Assert.IsTrue(InstancePoses.TryGet(hall.InstanceId, out var origin, out var rotation), "the worker learned the pose from the row");
            Assert.Less(Distance(origin, _portal.NearFace.Origin), Millimetre);
            // The interior is upright in its own space: the part's box stands at the origin plus its own offset.
            Assert.Less(Distance(_portal.NearFace.Origin + new Double3(0, 2, 0), ContainerRegistry.ToAbsolutePrecise(hall.WorldBounds.center, hall.InstanceId)), Millimetre);

            var inside = AtDoor(new Vector3(0f, 1f, 1f));
            player.transform.position = planet.Carried.Frame.LocalToSimulation(inside);
            var facing = Turned * Quaternion.Euler(0f, 10f, 0f); // walking in, looking a little right
            player.transform.rotation = facing;
            player.Velocity = Turned * new Vector3(0f, 0f, 2f);
            Tick();

            Assert.AreSame(hall, player.Container, "the player crossed into the turned instance");
            var local = ContainerRegistry.ToAbsolutePrecise(player.ScopePositionPrecise, player.InstanceId) - _portal.NearFace.Origin;
            TestContext.WriteLine($"enter local {local}");
            Assert.Less(Distance(local, new Double3(0, 1, 1)), Millimetre, "upright in the instance: the door's own offsets");
            Assert.Less(Quaternion.Angle(Quaternion.Euler(0f, 10f, 0f), player.transform.rotation), 0.05f, "facing the same way, in the instance's axes");
            Assert.Less((player.Velocity - new Vector3(0f, 0f, 2f)).magnitude, 1e-4f, "and moving the same way");
            Assert.Less(Quaternion.Angle(Quaternion.Inverse(Turned), InstancePoses.Turn(chunk.InstanceId, hall.InstanceId)), 0.01f, "the turn a game reads");

            // Out again: back past the threshold in the instance's own axes, onto the planet at the door's turned offsets.
            player.transform.position += new Vector3(0f, 0f, -1.5f);
            Tick();
            Assert.AreSame(chunk, player.Container, "back in the planet's chunk");
            var outside = planet.Carried.Frame.SimulationToLocal(player.transform.position);
            TestContext.WriteLine($"leave at {outside} expected {AtDoor(new Vector3(0f, 1f, -0.5f))}");
            Assert.Less((outside - AtDoor(new Vector3(0f, 1f, -0.5f))).magnitude, 2e-3f, "where it walked out, turned back");
            Assert.Less(Quaternion.Angle(facing, player.transform.rotation), 0.05f, "facing as it went in");
        }
    }
}
