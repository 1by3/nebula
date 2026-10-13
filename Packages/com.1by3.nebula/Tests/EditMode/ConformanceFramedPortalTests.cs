using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 43 (NEB-394, <c>docs/container-tree.md</c> §8; a portal since NEB-428): a <see cref="Portal"/> built in a
    /// chunk of a grid hosted by a framed carrier (a dungeon door on a planet that is a carrier in its system's scope,
    /// D20 and D22). Its face is read in the scope's own space, out of the planet's frame, as an entity's is, so the
    /// instance is prepared at the door and a player standing on the planet enters it; leaving puts the player back in the
    /// planet's chunk, in the planet's frame, at the door, not on the carrier's box or nowhere.
    /// <para>
    /// Tier B: one real <see cref="NebulaWorker"/> on the <see cref="ConformanceMesh"/> running the portal in its tick, a
    /// <see cref="LocalControlPlane"/> for the scope, the instance and the lease rows, and preview scenes for the frames.
    /// The client's half of readiness is stood in for by its <see cref="MsgId.PortalWarm"/>, as in
    /// <see cref="PortalScopedGridTests"/>.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFramedPortalTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Scope = "space/vaults";
        private const string GridKey = "planet/vaults/0";
        private const string Owner = "player-a";
        private static readonly Vector3 SpaceCell = new Vector3(8192f, 8192f, 8192f);
        /// <summary>Where the planet's ground origin stands in the scope, absolute: well away from the scope's origin.</summary>
        private static readonly Vector3 PlanetAt = new Vector3(2000f, 1000f, 3000f);
        private static readonly Vector3 PlanetBox = new Vector3(1024f, 400f, 1024f);
        private static readonly Vector3 GroundCell = new Vector3(128f, 400f, 128f);
        /// <summary>The door, in the planet's own coordinates, in the middle of ground chunk (2, 0, 1).</summary>
        private static readonly Vector3 Door = new Vector3(320f, 0f, 192f);

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
            NebulaWorld.LoadRuntime(_definition); // the public frame, which an instance lives in
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

        /// <summary>The control plane's rows into the registry, with their owners, as a worker's control-plane pass does it.</summary>
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

        private static void AreClose(Vector3 expected, Vector3 actual, string message, float tolerance = 1e-2f)
        {
            Assert.AreEqual(expected.x, actual.x, tolerance, message + " (x)");
            Assert.AreEqual(expected.y, actual.y, tolerance, message + " (y)");
            Assert.AreEqual(expected.z, actual.z, tolerance, message + " (z)");
        }

        /// <summary>
        /// The scope (a root grid of 8 km cubes) with the planet standing in its chunk, the planet's ground hosted by the
        /// planet, the door's chunk leased, and a portal built in that chunk's content as a game builds it.
        /// </summary>
        private Container Build(out NetworkIdentity planet, out RuntimeGrid ground) => Build(out planet, out ground, Quaternion.identity, Double3.From(PlanetAt));

        /// <summary>
        /// <see cref="Build(out NetworkIdentity, out RuntimeGrid)"/> with the planet turned by <paramref name="planetRotation"/> in its scope and standing at
        /// <paramref name="planetAt"/> (absolute, in double): the scope's frame is moved to the planet's cell first, as its grid follows its players.
        /// </summary>
        private Container Build(out NetworkIdentity planet, out RuntimeGrid ground, Quaternion planetRotation, Double3 planetAt)
        {
            var space = new RuntimeGrid(SpaceCell, planar: false, scopeKey: Scope);
            NebulaChunks.Activate(space, NebulaRoles.Worker, true, null);
            var cell = space.CoordOfAbsolute(planetAt.ToVector3());
            space.ShiftOrigin(cell);
            _plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Scope,
                Definition = new ChunkGridDefinition { CellSize = SpaceCell, Planar = false }.ToScopeDefinition(),
                PreferredWorkerId = W.Id,
            });
            var spaceInstance = new InstanceContainerInfo { InstanceId = space.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(cell) };
            var spaceChunk = ContainerRegistry.RegisterRuntime(space.IdOf(cell), space.BoundsOf(cell), spaceInstance);
            ContainerRegistry.ApplyLease(spaceChunk.ContainerId, W.Id, W.Index, 1);
            _plane.EnsureRuntimeContainer(spaceChunk.ContainerId, space.PlacementOf(cell), W.Id, spaceInstance);

            NetworkIdentity p = null;
            W.Act(() =>
            {
                p = NetworkPrefabs.Instantiate(_planetPrefab, ContainerRegistry.ToFrame(planetAt, space.InstanceId), planetRotation, spaceChunk.ContentRoot);
                W.Instance.SpawnServerDriven(p, spaceChunk);
            });
            planet = p;
            Assume.That(planet.Carried, Is.Not.Null, "the planet carries its box");
            Assume.That(planet.Carried.Frame, Is.Not.Null, "and its box has a frame of its own");

            ground = RuntimeGrid.Hosted(new ChunkGridDefinition { CellSize = GroundCell, Planar = true }, Scope, GridKey, planet.Carried.ContainerId);
            NebulaChunks.Activate(ground, NebulaRoles.Worker, true, null);
            var coord = ground.CoordOfAbsolute(Door);
            var info = new InstanceContainerInfo { InstanceId = space.InstanceId, ScopeKey = Scope, PartId = ground.PartIdOf(coord) };
            _plane.EnsureRuntimeContainer(ground.ContainerIdOf(coord), ground.PlacementOf(coord), W.Id, info);
            Mirror();
            var chunk = ContainerRegistry.GetRuntime(ground.IdOf(coord));
            Assume.That(chunk, Is.Not.Null, "the door's chunk registers under the planet");
            Assume.That(chunk.InnerSpace, Is.SameAs(planet.Carried), "in the planet's frame");

            var root = new GameObject("content");
            root.transform.SetParent(chunk.transform, false);
            var go = new GameObject("vault door");
            go.transform.SetParent(root.transform, false);
            go.transform.position = planet.Carried.Frame.LocalToSimulation(Door);
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

        [Test]
        public void APortalOnAFramedPlanetStandsWhereThePlanetPutsIt()
        {
            var chunk = Build(out var planet, out _);
            Assert.AreEqual(ScopeKeys.Hash(Scope), _portal.NearScope, "the portal stands in the planet's scope");
            Assert.AreSame(chunk, _portal.NearContainer, "fixed in the planet's chunk");
            var expected = PlanetAt + Door;
            var at = _portal.NearFace.Origin;
            AreClose(expected, at.ToVector3(), "the face's absolute position is the door's in the scope, out of the planet's frame");
            Assert.IsNotNull(chunk);
        }

        [Test]
        public void APlayerOnAFramedPlanetEntersTheInstanceAndLeavesBackOntoThePlanet()
        {
            var chunk = Build(out var planet, out var ground);
            var door = PlanetAt + Door;
            var player = Player(chunk, planet, Door + new Vector3(0f, 1f, -6f));
            Assume.That(player.Container, Is.SameAs(chunk));
            Assume.That(player.Space, Is.SameAs(planet.Carried), "the player stands in the planet's frame");

            OpenDoor(); // the instance is activated at the door, ready, and the client warm
            var scope = _plane.FindScope("vault/" + Owner);
            Assert.That(scope, Is.Not.Null, "opening the door prepares the player's instance");
            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
            Assert.That(hall, Is.Not.Null, "its entry part is registered");
            AreClose(door + new Vector3(0f, 2f, 0f), _plane.FindLease(hall.ContainerId).Bounds.center, "the instance stands at the door, in the scope's absolute coordinates");

            player.transform.position = planet.Carried.Frame.LocalToSimulation(Door + new Vector3(0f, 1f, 1f));
            Tick(); // through the opening: committed

            Assert.AreSame(hall, player.Container, "the player crossed into the instance");
            Assert.AreEqual(NebulaWorker.InstanceKey("vault/" + Owner), player.InstanceId);
            AreClose(door + new Vector3(0f, 1f, 1f), ContainerRegistry.ToAbsolutePrecise(player.transform.position, player.InstanceId).ToVector3(),
                "at the same absolute position as the door it walked through");

            // Back out, onto the planet.
            player.transform.position += new Vector3(0f, 0f, -1.5f);
            Tick();

            Assert.AreSame(chunk, player.Container, "back in the planet's chunk, not the planet's own box or the scope's space");
            Assert.AreSame(planet.Carried, player.Space, "in the planet's frame");
            Assert.AreEqual(ScopeKeys.Hash(Scope), player.InstanceId);
            AreClose(Door + new Vector3(0f, 1f, -0.5f), planet.Carried.Frame.SimulationToLocal(player.transform.position),
                "where it walked out, in the planet's own coordinates");
            Assert.IsTrue(ground.Owns(player.Container));
        }

        /// <summary>Walk the player from beside the door into the instance; returns the hall it entered.</summary>
        private Container Enter(NetworkIdentity player, NetworkIdentity planet)
        {
            OpenDoor();
            player.transform.position = planet.Carried.Frame.LocalToSimulation(Door + new Vector3(0f, 1f, 1f));
            Tick();
            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
            Assume.That(player.Container, Is.SameAs(hall), "entered");
            return hall;
        }

        /// <summary>A committed transfer of <paramref name="entity"/> into <paramref name="destination"/> at a pose in the scope's own space.</summary>
        private bool Transfer(NetworkIdentity entity, Container destination, Vector3 scopePosition)
        {
            var transfer = W.Instance.PrepareTransfer(entity, destination);
            for (int i = 0; i < 5 && !transfer.Ready && transfer.Error == null; i++) { transfer.ClientReady = true; Tick(); }
            Assert.IsNull(transfer.Error, "the preparation succeeds");
            Assert.IsTrue(transfer.Ready, "and is ready");
            return W.Instance.TryCommitTransfer(transfer, scopePosition, Quaternion.identity);
        }

        /// <summary>
        /// A floor below the lobby and back (a dungeon's elevator: transfers between two instances of the player's), then
        /// walking out of the lobby: the portal still lets the player back onto the planet, into its chunk.
        /// </summary>
        [Test]
        public void AfterARoundTripToAnotherInstanceThePlayerStillWalksOutOntoThePlanet()
        {
            var chunk = Build(out var planet, out _);
            var door = PlanetAt + Door;
            var player = Player(chunk, planet, Door + new Vector3(0f, 1f, -6f));
            var hall = Enter(player, planet);

            var below = new Double3(door.x, door.y - 40.0, door.z);
            var floorRef = W.Instance.PrepareInstance(_template, Owner + "|1", below)[0];
            Tick();
            var floor = floorRef.Resolve();
            Assert.IsNotNull(floor, "the floor below is prepared");
            Assert.IsTrue(Transfer(player, floor, ContainerRegistry.ToFrame(below + new Double3(0, 1, 0), floor.InstanceId)), "down the elevator");
            Assert.AreSame(floor, player.Container);
            Assert.IsTrue(Transfer(player, hall, ContainerRegistry.ToFrame(Double3.From(door + new Vector3(0f, 1f, 1f)), hall.InstanceId)), "and back up");
            Assert.AreSame(hall, player.Container);

            player.transform.position += new Vector3(0f, 0f, -0.5f); // the transfer left it at z = +1 in the instance's own frame
            Tick();
            player.transform.position += new Vector3(0f, 0f, -1.0f);
            Tick();
            Assert.AreSame(chunk, player.Container, "out of the lobby into the planet's chunk");
            Assert.AreSame(planet.Carried, player.Space);
            AreClose(Door + new Vector3(0f, 1f, -0.5f), planet.Carried.Frame.SimulationToLocal(player.transform.position), "where it walked out");
        }

        /// <summary>
        /// A transfer from the instance straight into another chunk of the planet (a respawn in town from inside a
        /// dungeon): committed at a pose in the scope's own space, it lands in the chunk, in the planet's frame, at the
        /// planet-local point that pose names.
        /// </summary>
        [Test]
        public void ATransferFromTheInstanceLandsInAChunkOfThePlanet()
        {
            var chunk = Build(out var planet, out var ground);
            var player = Player(chunk, planet, Door + new Vector3(0f, 1f, -6f));
            Enter(player, planet);

            var town = new Vector3(-200f, 1f, -300f);
            var coord = ground.CoordOfAbsolute(town);
            var info = new InstanceContainerInfo { InstanceId = ScopeKeys.Hash(Scope), ScopeKey = Scope, PartId = ground.PartIdOf(coord) };
            _plane.EnsureRuntimeContainer(ground.ContainerIdOf(coord), ground.PlacementOf(coord), W.Id, info);
            Mirror();
            var townChunk = ContainerRegistry.GetRuntime(ground.IdOf(coord));
            Assume.That(townChunk, Is.Not.Null);

            var scopePosition = PhysicsFrames.ToScope(planet.Carried.Frame.LocalToSimulation(town), planet.Carried);
            Assert.IsTrue(Transfer(player, townChunk, scopePosition), "committed");
            Assert.AreSame(townChunk, player.Container, "in the town's chunk");
            Assert.AreSame(planet.Carried, player.Space, "in the planet's frame");
            Assert.AreEqual(ScopeKeys.Hash(Scope), player.InstanceId);
            AreClose(town, planet.Carried.Frame.SimulationToLocal(player.transform.position), "at the planet-local point");
        }


        /// <summary>
        /// The planet turned 54.7° about x against its scope (a door on a sphere), 10⁸ m from the scope's origin: a template's far face is the near face's origin with the
        /// identity rotation in the instance's own upright space, the instance is posed by the near face's rotation, and every reader of the far
        /// face agrees on that one convention (<c>docs/portals.md</c> D5, as built). A player 0.3 m inside the face, at the floor 1.26 m below the
        /// aperture's centre, is inside the far window as a network gateway that shares this process measures it from what the worker sends (so the
        /// client inside looks out), the crossing maps through the faces with no offset both ways, and the link's map, the instance's pose from its
        /// lease row and the map a client draws with are one turn about one point.
        /// </summary>
        [Test]
        public void OnAPlanetTurnedAgainstItsScopeAPlayerJustInsideTheInstanceIsInsideTheFarWindowAndTheMapsAgree()
        {
            var turn = Quaternion.Euler(54.7f, 0f, 0f);
            // A door 10^8 m out in a star system, where a float is 8 m apart.
            var chunk = Build(out var planet, out _, turn, new Double3(-69_812_950.421, -287_858.201, -89_203_062.32) - Double3.From(Door));
            var player = Player(chunk, planet, Door + new Vector3(0f, 1f, -6f));
            var link = OpenDoor();
            var record = _portal.ToRecord();
            ulong instance = NebulaWorker.InstanceKey("vault/" + Owner);
            Assert.That(Quaternion.Angle(record.NearRotation, turn), Is.LessThan(0.01f), "the near face is turned as the planet is");
            Assert.That(record.FarRotation, Is.EqualTo(Quaternion.identity), "the far face is upright in the instance's own space");
            Assert.That(record.FarOrigin, Is.EqualTo(record.NearOrigin), "and stands at the near face's origin: a coincident portal");

            // In through the door: (0, 1, 1) from the door in the planet's axes is (0, 1, 1) from the face in the instance's.
            player.transform.position = planet.Carried.Frame.LocalToSimulation(Door + new Vector3(0f, 1f, 1f));
            Tick();
            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
            Assert.AreSame(hall, player.Container, "the player crossed into the instance");
            var inside = ContainerRegistry.ToAbsolutePrecise(player.transform.position, instance) - record.FarOrigin;
            AreClose(new Vector3(0f, 1f, 1f), inside.ToVector3(), "the crossing maps through the faces with no offset", 2e-3f);

            // 0.3 m inside the face, at the floor under the aperture's centre.
            var at = record.FarOrigin + new Double3(0, -1.26, 0.3);
            player.transform.position = ContainerRegistry.ToFrame(at, instance);
            Tick();
            Assert.AreSame(hall, player.Container, "still inside");

            // A network gateway that shares this process (the Editor's dev loop), fed what the worker sends.
            var gateway = SharedProcessGateway(record, link, instance, player, out var client);
            try
            {
                var pawn = GatewayEntities(gateway)[player.NetId];
                var point = (Double3)CallGateway(gateway, "ScopePointOf", pawn);
                var window = record.FarWindow(instance);
                AreClose(new Vector3(0f, -1.26f, 0.3f), window.ToLocal(point).ToVector3(), "the gateway reads the player in the far face's axes where it stands", 2e-3f);
                Assert.That(window.Contains(point), Is.True, "0.3 m inside the face is inside the far window");
                CallGateway(gateway, "RefreshWindows", client, true);
                Assert.That(((IList)GatewayField(client, "Windows")).Count, Is.EqualTo(1), "the player inside looks out through the open door");
            }
            finally { Object.DestroyImmediate(gateway.gameObject); }

            // The link's map, the instance's pose from its lease row, and the record's faces (what a client draws with) are one turn about one point.
            var toNear = link.ToNearMap;
            Assert.That(Quaternion.Angle(toNear.Turn, turn), Is.LessThan(0.01f), "the instance is turned into the planet's scope by the near face's rotation");
            Assert.That(InstancePoses.TryGet(instance, out var poseOrigin, out var poseRotation), Is.True, "the instance's pose is learned from its row");
            Assert.That(Quaternion.Angle(poseRotation, toNear.Turn), Is.LessThan(0.01f), "the pose and the link turn alike");
            Assert.That(poseOrigin, Is.EqualTo(record.FarOrigin), "about the face");
            var drawn = new PortalMap(record.NearScope, instance, record.NearOrigin, record.FarOrigin, record.FarRotation * Quaternion.Inverse(record.NearRotation)).Inverse;
            var outside = PhysicsFrames.Rotate(turn, new Double3(0, -1.26, 0.3)).ToVector3();
            AreClose(outside, (toNear.Point(at) - record.NearOrigin).ToVector3(), "the link carries the point out through the face", 2e-3f);
            AreClose(outside, (drawn.Point(at) - record.NearOrigin).ToVector3(), "the client draws it there", 2e-3f);

            // Back out through the face: where it walked out, in the planet's own axes.
            player.transform.position = ContainerRegistry.ToFrame(record.FarOrigin + new Double3(0, -1.26, -0.5), instance);
            Tick();
            Assert.AreSame(chunk, player.Container, "back in the planet's chunk");
            AreClose(Door + new Vector3(0f, -1.26f, -0.5f), planet.Carried.Frame.SimulationToLocal(player.transform.position), "where it walked out, in the planet's own coordinates", 2e-3f);
        }

        private static readonly BindingFlags GatewayFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static object CallGateway(NebulaGateway gateway, string name, params object[] args)
        {
            try { return typeof(NebulaGateway).GetMethod(name, GatewayFlags).Invoke(gateway, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }

        private static object GatewayField(object target, string name) => target.GetType().GetField(name, GatewayFlags).GetValue(target);
        private static void SetGatewayField(object target, string name, object value) => target.GetType().GetField(name, GatewayFlags).SetValue(target, value);
        private static IDictionary GatewayDictionary(NebulaGateway gateway, string name) => (IDictionary)typeof(NebulaGateway).GetField(name, GatewayFlags).GetValue(gateway);
        private static IDictionary GatewayEntities(NebulaGateway gateway) => GatewayDictionary(gateway, "_entities");
        private static object GatewayNested(string name) => System.Activator.CreateInstance(typeof(NebulaGateway).GetNestedType(name, BindingFlags.NonPublic), true);

        /// <summary>
        /// A <see cref="NebulaGateway"/> in this process, as the Editor's dev loop runs one beside its worker: the control plane's rows, the player's
        /// record as the worker sends it (container-local), the portal's record and the party's open link, and the player's client.
        /// </summary>
        private NebulaGateway SharedProcessGateway(PortalRecord portal, PortalLink link, ulong instance, NetworkIdentity player, out object client)
        {
            var gateway = new GameObject("gateway").AddComponent<NebulaGateway>();
            typeof(NebulaGateway).GetField("<Config>k__BackingField", GatewayFlags).SetValue(gateway, _mesh.Config);
            typeof(NebulaGateway).GetField("_transport", GatewayFlags).SetValue(gateway, new ConformanceMesh.RecordingTransport());
            CallGateway(gateway, "InitializeInterest");
            CallGateway(gateway, "IndexLeases", (IReadOnlyList<LeaseInfo>)new List<LeaseInfo>(_plane.Leases));

            var rec = GatewayNested("EntityRecord");
            SetGatewayField(rec, "NetId", player.NetId);
            SetGatewayField(rec, "Container", player.ContainerRef);
            SetGatewayField(rec, "OwnerWorkerIndex", W.Index);
            SetGatewayField(rec, "OwnerClientId", 7UL);
            var spawn = (EntitySpawnMsg)GatewayField(rec, "LastSpawn");
            spawn.NetId = player.NetId; spawn.Container = player.ContainerRef; spawn.LocalPosition = player.LocalPosition; spawn.LocalRotation = player.LocalRotation; spawn.LocalScale = Vector3.one;
            SetGatewayField(rec, "LastSpawn", spawn);
            GatewayEntities(gateway).Add(player.NetId, rec);
            CallGateway(gateway, "RefreshAbsolute", rec);

            var entry = GatewayNested("PortalEntry");
            SetGatewayField(entry, "Record", portal);
            GatewayDictionary(gateway, "_portals").Add(portal.PortalId, entry);
            var linkEntry = GatewayNested("LinkEntry");
            SetGatewayField(linkEntry, "Record", new PortalLinkRecord { LinkId = link.Id, PortalId = portal.PortalId, FarScope = instance, State = PortalState.Open });
            ((HashSet<ulong>)GatewayField(linkEntry, "Observers")).Add(7UL);
            GatewayDictionary(gateway, "_portalLinks").Add(link.Id, linkEntry);
            ((IList)GatewayField(entry, "Links")).Add(linkEntry);

            client = System.Activator.CreateInstance(typeof(NebulaGateway).GetNestedType("ClientConn", BindingFlags.NonPublic), true);
            SetGatewayField(client, "ClientId", 7UL); SetGatewayField(client, "PeerId", 7); SetGatewayField(client, "Welcomed", true);
            SetGatewayField(client, "PawnNetId", player.NetId); SetGatewayField(client, "LastScope", instance);
            GatewayDictionary(gateway, "_clientsById").Add(7UL, client);
            return gateway;
        }
    }
}
