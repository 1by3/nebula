using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 43 (NEB-394, <c>docs/container-tree.md</c> §8): an <see cref="InstanceBoundary"/> built in a
    /// chunk of a grid hosted by a framed carrier (a dungeon door on a planet that is a carrier in its system's scope,
    /// D20 and D22). Its position is read in the scope's own space, out of the planet's frame, as an entity's is, so the
    /// instance is prepared at the door and a player standing on the planet enters it; leaving puts the player back in the
    /// planet's chunk, in the planet's frame, at the door, not on the carrier's box or nowhere.
    /// <para>
    /// Tier B: one real <see cref="NebulaWorker"/> on the <see cref="ConformanceMesh"/> running the boundary in its tick, a
    /// <see cref="LocalControlPlane"/> for the scope, the instance and the lease rows, and preview scenes for the frames.
    /// The client's half of a crossing is stood in for by marking the preparation client-ready, as in
    /// <see cref="InstanceBoundaryScopedGridTests"/>.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFramedInstanceBoundaryTests
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
        private InstanceBoundary _boundary;

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
            if (_boundary != null) typeof(InstanceBoundary).GetMethod("OnDisable", Flags).Invoke(_boundary, null);
            _boundary = null;
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

        private void AcknowledgeClients()
        {
            var field = typeof(NebulaWorker).GetField("_instanceTransfers", Flags);
            foreach (var transfer in ((Dictionary<uint, InstanceTransfer>)field.GetValue(W.Instance)).Values) transfer.ClientReady = true;
        }

        private static void AreClose(Vector3 expected, Vector3 actual, string message, float tolerance = 1e-2f)
        {
            Assert.AreEqual(expected.x, actual.x, tolerance, message + " (x)");
            Assert.AreEqual(expected.y, actual.y, tolerance, message + " (y)");
            Assert.AreEqual(expected.z, actual.z, tolerance, message + " (z)");
        }

        /// <summary>
        /// The scope (a root grid of 8 km cubes) with the planet standing in its chunk, the planet's ground hosted by the
        /// planet, the door's chunk leased, and a boundary built in that chunk's content as a game builds it.
        /// </summary>
        private Container Build(out NetworkIdentity planet, out RuntimeGrid ground)
        {
            var space = new RuntimeGrid(SpaceCell, planar: false, scopeKey: Scope);
            NebulaChunks.Activate(space, NebulaRoles.Worker, true, null);
            _plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Scope,
                Definition = new ChunkGridDefinition { CellSize = SpaceCell, Planar = false }.ToScopeDefinition(),
                PreferredWorkerId = W.Id,
            });
            var spaceInstance = new InstanceContainerInfo { InstanceId = space.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(Vector3Int.zero) };
            var spaceChunk = ContainerRegistry.RegisterRuntime(space.IdOf(Vector3Int.zero), space.BoundsOf(Vector3Int.zero), spaceInstance);
            ContainerRegistry.ApplyLease(spaceChunk.ContainerId, W.Id, W.Index, 1);
            _plane.EnsureRuntimeContainer(spaceChunk.ContainerId, ContainerRegistry.ToAbsolute(space.BoundsOf(Vector3Int.zero), space.InstanceId), W.Id, spaceInstance);

            NetworkIdentity p = null;
            W.Act(() =>
            {
                p = NetworkPrefabs.Instantiate(_planetPrefab, SystemLayoutFree.ToFrame(space, PlanetAt), Quaternion.identity, spaceChunk.ContentRoot);
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
            _boundary = go.AddComponent<InstanceBoundary>();
            _boundary.Template = _template;
            _boundary.Interior = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f));
            _boundary.PreparationDistance = 6f;
            typeof(InstanceBoundary).GetMethod("OnEnable", Flags).Invoke(_boundary, null);
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
        public void ABoundaryOnAFramedPlanetStandsWhereThePlanetPutsIt()
        {
            var chunk = Build(out var planet, out _);
            Assert.AreEqual(ScopeKeys.Hash(Scope), _boundary.HostInstanceId, "the boundary stands in the planet's scope");
            var expected = PlanetAt + Door;
            var at = _boundary.AbsolutePosition;
            AreClose(expected, at.ToVector3(), "the boundary's absolute position is the door's in the scope, out of the planet's frame");
            AreClose(expected, _boundary.InstanceOrigin.ToVector3(), "and the instance is prepared there");
            Assert.IsNotNull(chunk);
        }

        [Test]
        public void APlayerOnAFramedPlanetEntersTheInstanceAndLeavesBackOntoThePlanet()
        {
            var chunk = Build(out var planet, out var ground);
            var door = PlanetAt + Door;
            var player = Player(chunk, planet, Door + new Vector3(0f, 1f, -8f));
            Assume.That(player.Container, Is.SameAs(chunk));
            Assume.That(player.Space, Is.SameAs(planet.Carried), "the player stands in the planet's frame");

            Tick(); // near the door: the instance is activated at the door
            var scope = _plane.FindScope("vault/" + Owner);
            Assert.That(scope, Is.Not.Null, "approaching the door prepares the player's instance");
            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
            Assert.That(hall, Is.Not.Null, "its entry part is registered");
            AreClose(door + new Vector3(0f, 2f, 0f), _plane.FindLease(hall.ContainerId).Bounds.center, "the instance stands at the door, in the scope's absolute coordinates");

            Tick(); // the crossing is prepared
            AcknowledgeClients();
            player.transform.position = planet.Carried.Frame.LocalToSimulation(Door + new Vector3(0f, 1f, 1f));
            Tick(); // inside the volume: committed

            Assert.AreSame(hall, player.Container, "the player crossed into the instance");
            Assert.AreEqual(NebulaWorker.InstanceKey("vault/" + Owner), player.InstanceId);
            AreClose(door + new Vector3(0f, 1f, 1f), ContainerRegistry.ToAbsolutePrecise(player.transform.position, player.InstanceId).ToVector3(),
                "at the same absolute position as the door it walked through");

            // Back out: held until the crossing is ready, then committed into the planet's chunk.
            player.transform.position += new Vector3(0f, 0f, -7f);
            Tick();
            AcknowledgeClients();
            player.transform.position += new Vector3(0f, 0f, -7f);
            Tick();

            Assert.AreSame(chunk, player.Container, "back in the planet's chunk, not the planet's own box or the scope's space");
            Assert.AreSame(planet.Carried, player.Space, "in the planet's frame");
            Assert.AreEqual(ScopeKeys.Hash(Scope), player.InstanceId);
            AreClose(Door + new Vector3(0f, 1f, -6f), planet.Carried.Frame.SimulationToLocal(player.transform.position),
                "where it walked out, in the planet's own coordinates");
            Assert.IsTrue(ground.Owns(player.Container));
        }

        /// <summary>The scope frame arithmetic the tests need, without a game's layout.</summary>
        private static class SystemLayoutFree
        {
            public static Vector3 ToFrame(RuntimeGrid grid, Vector3 absolute) => absolute + grid.Frame.OriginOffset;
        }
    }
}
