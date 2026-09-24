using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// A rider's chunk (NEB-306): <see cref="RuntimeGrid.CoordOf(NetworkIdentity)"/> for an entity aboard a carrier,
    /// the lease ring <see cref="RuntimeGridAllocator"/> pulls around it, and the grid a client follows the origin of.
    /// <para>
    /// Inside a physics frame a worker's transform reads frame-local coordinates, and the frame's root sits at Unity's
    /// origin (<c>docs/container-tree.md</c> D11). The grid used to read a rider's cell from that transform, so a pilot
    /// in a ship with its own frame pulled the ring around cell (0,0) or (-1,-1) wherever the ship flew, and the ship,
    /// being server-driven, pulled none of its own. A client in the same seat followed the public grid's origin
    /// instead of the scoped world's, because its pawn's container is the ship's, which is no chunk.
    /// </para>
    /// <para>
    /// Tier B, as in <c>ConformanceCrewedCarrierRetireTests</c>: one real <see cref="NebulaWorker"/> on the
    /// <see cref="ConformanceMesh"/> and a real allocator over a <see cref="LocalControlPlane"/>. Frames get preview
    /// scenes of their own, as in <c>ConformanceFramedWorldTests</c>.
    /// </para>
    /// </summary>
    public sealed class RuntimeGridRiderTests
    {
        private static readonly Vector3 Cell = new Vector3(64f, 64f, 64f);
        private static readonly Vector3Int Far = new Vector3Int(5, 0, 5);
        private const ulong PilotClient = 7;
        private const string Planet = "world/planet";

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private ushort _framedShipPrefab, _shipPrefab, _framedShuttlePrefab, _shuttlePrefab, _pawnPrefab;

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _mesh = new ConformanceMesh(1);
            _framedShipPrefab = _mesh.RegisterPrefab(Carrier("framed-ship-prefab", "hull", new Vector3(40f, 12f, 40f), new Vector3(0f, 4f, 0f), framed: true));
            _shipPrefab = _mesh.RegisterPrefab(Carrier("ship-prefab", "deck", new Vector3(40f, 12f, 40f), new Vector3(0f, 4f, 0f), framed: false));
            _framedShuttlePrefab = _mesh.RegisterPrefab(Carrier("framed-shuttle-prefab", "cabin", new Vector3(8f, 4f, 8f), new Vector3(0f, 1.5f, 0f), framed: true));
            _shuttlePrefab = _mesh.RegisterPrefab(Carrier("shuttle-prefab", "bench", new Vector3(8f, 4f, 8f), new Vector3(0f, 1.5f, 0f), framed: false));
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);

            _plane = new LocalControlPlane { Clock = () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            _plane.Connect();
            AttachRegistered(W.Instance, _plane);
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            _plane.Dispose();
            NebulaChunks.ResetForNewSession();
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        /// <summary>A vehicle prefab carrying a box of its own, with a physics frame when <paramref name="framed"/>.</summary>
        private static GameObject Carrier(string name, string label, Vector3 size, Vector3 center, bool framed)
        {
            var go = new GameObject(name);
            go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = label;
            box.Size = size;
            box.Center = center;
            box.OwnPhysicsFrame = framed;
            go.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            return go;
        }

        /// <summary>Points <paramref name="worker"/> at <paramref name="plane"/> and marks it registered, as in <c>ConformanceCrewedCarrierRetireTests</c>.</summary>
        private static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", flags).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", flags).SetValue(registration, plane.DocumentId ?? "");
        }

        private static RuntimeGrid Grid(string scopeKey) => new RuntimeGrid(Cell, planar: true, scopeKey: scopeKey);

        /// <summary>One chunk owned by the worker, registered and leased as the allocator and orchestrator would leave it.</summary>
        private Container Chunk(RuntimeGrid grid, Vector3Int coord)
        {
            var instance = grid.IsPublic ? null : new InstanceContainerInfo
            {
                InstanceId = grid.InstanceId,
                ScopeKey = grid.ScopeKey,
                PartId = ChunkKeys.PartId(coord),
            };
            var chunk = ContainerRegistry.RegisterRuntime(grid.IdOf(coord), grid.BoundsOf(coord), instance);
            ContainerRegistry.ApplyLease(chunk.ContainerId, W.Id, W.Index, 1);
            _plane.EnsureRuntimeContainer(chunk.ContainerId, ContainerRegistry.ToAbsolute(grid.BoundsOf(coord), grid.InstanceId), W.Id, instance);
            return chunk;
        }

        private RuntimeGridAllocator Allocator(RuntimeGrid grid) =>
            new RuntimeGridAllocator(W.Instance, grid) { TickIntervalSeconds = 0f, RetireAfterSeconds = 30f, Ring = 1 };

        /// <summary>A server-driven ship standing in <paramref name="chunk"/> at <paramref name="position"/>.</summary>
        private NetworkIdentity SpawnShip(ushort prefab, Container chunk, Vector3 position)
        {
            var ship = W.SpawnServerDriven(prefab, chunk, position, Quaternion.identity);
            Assume.That(ship.Carried, Is.Not.Null, "the ship registered its box");
            Assume.That(ship.Container, Is.SameAs(chunk));
            return ship;
        }

        /// <summary>
        /// Spawn <paramref name="prefab"/> aboard <paramref name="carrier"/> at <paramref name="local"/> in the carrier's
        /// own coordinates, owned by <paramref name="client"/> when it is not 0. Inside a frame the pose is written in
        /// simulation space, as a worker simulates it; outside one it is the carrier's local pose.
        /// </summary>
        private NetworkIdentity SpawnAboard(ushort prefab, NetworkIdentity carrier, Vector3 local, ulong client = 0)
        {
            var box = carrier.Carried;
            NetworkIdentity rider = null;
            W.Act(() =>
            {
                rider = NetworkPrefabs.Instantiate(prefab, Vector3.zero, Quaternion.identity, box.ContentRoot);
                if (client != 0) W.Instance.Spawn(rider, box, client);
                else W.Instance.SpawnServerDriven(rider, box);
            });
            rider.transform.position = box.Frame != null ? box.Frame.LocalToSimulation(local) : carrier.transform.TransformPoint(local);
            Assume.That(rider.Container, Is.SameAs(box), $"{rider} rides in {carrier}");
            return rider;
        }

        private static void AssertRingAround(RuntimeGridAllocator allocator, Vector3Int center)
        {
            for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                    Assert.That(allocator.IsWanted(center + new Vector3Int(x, 0, z)), Is.True, $"the ring around {center} includes {center + new Vector3Int(x, 0, z)}");
            Assert.That(allocator.IsWanted(Vector3Int.zero), Is.False, "nothing is asked for at the origin, where the frame's root sits");
            Assert.That(allocator.IsWanted(new Vector3Int(-1, 0, -1)), Is.False);
        }

        // ------------------------------------------------------------------------------------------ one carrier

        /// <summary>
        /// The bug as it was seen: a client's pilot in a ship with its own frame, the ship in a chunk far from the
        /// origin. The pilot's transform reads frame-local coordinates, a few metres from Unity's origin; its cell is the
        /// ship's, and so is its ring. Both the public world and a scoped grid.
        /// </summary>
        [TestCase("")]
        [TestCase(Planet)]
        public void ARiderInAFramedCarrierPullsTheRingAroundTheCarrier(string scopeKey)
        {
            var grid = Grid(scopeKey);
            var chunk = Chunk(grid, Far);
            var ship = SpawnShip(_framedShipPrefab, chunk, grid.CenterOf(Far));
            var pilot = SpawnAboard(_pawnPrefab, ship, new Vector3(0f, 1f, 2f), PilotClient);
            Assume.That(pilot.Space, Is.SameAs(ship.Carried), "the pilot is in the ship's frame");
            Assume.That(grid.CoordOf(pilot.transform.position), Is.Not.EqualTo(Far), "its transform alone names a cell near the origin");

            Assert.That(grid.CoordOf(pilot), Is.EqualTo(Far), "a rider is in the cell its carrier is in");
            var allocator = Allocator(grid);
            W.Act(() => allocator.Tick(0f));
            AssertRingAround(allocator, Far);
        }

        /// <summary>A ship without a frame of its own: its riders read scope coordinates already, and nothing changes.</summary>
        [TestCase("")]
        [TestCase(Planet)]
        public void ARiderInACarrierWithoutAFramePullsTheRingAroundTheCarrier(string scopeKey)
        {
            var grid = Grid(scopeKey);
            var chunk = Chunk(grid, Far);
            var ship = SpawnShip(_shipPrefab, chunk, grid.CenterOf(Far));
            var pilot = SpawnAboard(_pawnPrefab, ship, new Vector3(0f, 1f, 2f), PilotClient);
            Assume.That(pilot.Space, Is.Null, "no frame anywhere");

            Assert.That(grid.CoordOf(pilot), Is.EqualTo(Far));
            var allocator = Allocator(grid);
            W.Act(() => allocator.Tick(0f));
            AssertRingAround(allocator, Far);
        }

        /// <summary>
        /// The ring follows the ship, not the chunk it is filed under: a ship that has flown two cells on before the next
        /// resolution is still in its old chunk, and its pilot's ring is already around where the ship is.
        /// </summary>
        [Test]
        public void ARidersRingFollowsAFramedCarrierThatHasLeftItsChunk()
        {
            var grid = Grid(Planet);
            var chunk = Chunk(grid, Far);
            var ship = SpawnShip(_framedShipPrefab, chunk, grid.CenterOf(Far));
            var pilot = SpawnAboard(_pawnPrefab, ship, new Vector3(0f, 1f, 2f), PilotClient);
            var ahead = Far + new Vector3Int(2, 0, 0);
            ship.transform.position = grid.CenterOf(ahead);
            Assume.That(ship.Container, Is.SameAs(chunk));

            var allocator = Allocator(grid);
            W.Act(() => allocator.Tick(0f));
            Assert.That(grid.CoordOf(pilot), Is.EqualTo(ahead));
            AssertRingAround(allocator, ahead);
        }

        // ------------------------------------------------------------------------------------------ nested

        /// <summary>
        /// A passenger in a shuttle parked in a framed ship's hangar, framed and not: every frame between the passenger
        /// and the chunk is converted out of, so its cell is the ship's.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void ARiderInNestedCarriersPullsTheRingAroundTheOutermostCarrier(bool framedShuttle)
        {
            var grid = Grid(Planet);
            var chunk = Chunk(grid, Far);
            var ship = SpawnShip(_framedShipPrefab, chunk, grid.CenterOf(Far));
            var shuttle = SpawnAboard(framedShuttle ? _framedShuttlePrefab : _shuttlePrefab, ship, new Vector3(6f, 1f, -8f));
            var passenger = SpawnAboard(_pawnPrefab, shuttle, new Vector3(0f, 0.5f, 1f), PilotClient);
            Assume.That(shuttle.Container, Is.SameAs(ship.Carried));
            Assume.That(passenger.Space, Is.SameAs(framedShuttle ? shuttle.Carried : ship.Carried));

            Assert.That(grid.CoordOf(passenger), Is.EqualTo(Far));
            var allocator = Allocator(grid);
            W.Act(() => allocator.Tick(0f));
            AssertRingAround(allocator, Far);
        }

        /// <summary>
        /// A runtime room fixed inside a framed ship is no chunk, even for the public grid, which can unpack any id: an
        /// entity in it is where the room is, and the room is where the ship is.
        /// </summary>
        [Test]
        public void AnEntityInARuntimeRoomFixedInAFrameIsInTheCellOfItsCarrier()
        {
            var grid = Grid("");
            var chunk = Chunk(grid, Far);
            var ship = SpawnShip(_framedShipPrefab, chunk, grid.CenterOf(Far));
            var room = ContainerRegistry.RegisterRuntime(4242UL, ContainerPlacement.Child(ship.Carried.ContainerId, new Vector3(0f, 2f, 10f), new Vector3(8f, 4f, 8f), ContainerAuthority.Inherited));
            Assume.That(room, Is.Not.Null);
            Assume.That(room.Space, Is.SameAs(ship.Carried));
            Assume.That(grid.TryCoordOf(room.RuntimeId, out var unpacked) && unpacked != Far, "the room's id unpacks to some other cell");

            NetworkIdentity crew = null;
            W.Act(() =>
            {
                crew = NetworkPrefabs.Instantiate(_pawnPrefab, Vector3.zero, Quaternion.identity, room.ContentRoot);
                W.Instance.Spawn(crew, room, PilotClient);
            });
            crew.transform.position = ship.Carried.Frame.LocalToSimulation(new Vector3(0f, 1f, 10f));
            Assume.That(crew.Container, Is.SameAs(room));

            Assert.That(grid.CoordOf(crew), Is.EqualTo(Far));
        }

        /// <summary>No container and a chunk of the grid: the two cases that already worked, unchanged.</summary>
        [Test]
        public void AnEntityInAChunkOrInNoContainerIsWhereItWas()
        {
            var grid = Grid("");
            var chunk = Chunk(grid, Far);
            var walker = W.SpawnServerDriven(_pawnPrefab, chunk, grid.CenterOf(Far) + new Vector3(10f, 0f, -10f), Quaternion.identity);
            Assert.That(grid.CoordOf(walker), Is.EqualTo(Far));

            var loose = new GameObject("loose").AddComponent<NetworkIdentity>();
            try
            {
                loose.transform.position = grid.CenterOf(new Vector3Int(-3, 0, 2));
                Assert.That(loose.Container, Is.Null);
                Assert.That(grid.CoordOf(loose), Is.EqualTo(new Vector3Int(-3, 0, 2)));
            }
            finally { UnityEngine.Object.DestroyImmediate(loose.gameObject); }
        }

        // ------------------------------------------------------------------------------------------ client origin

        /// <summary>
        /// A client follows the origin of the grid its pawn stands in (<c>docs/scope-frames.md</c> D6). A pilot's
        /// container is the ship's, which is no chunk; the grid is the one of the chunk the ship is in, not the public
        /// grid the lookup used to fall back to.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void AClientFollowsTheGridItsCarrierIsIn(bool framed)
        {
            var pub = Grid("");
            var planet = Grid(Planet);
            NebulaChunks.Activate(pub, NebulaRoles.Client, headless: true, allocator: null);
            NebulaChunks.Activate(planet, NebulaRoles.Client, headless: true, allocator: null);
            var chunk = Chunk(planet, Far);
            var ship = SpawnShip(framed ? _framedShipPrefab : _shipPrefab, chunk, planet.CenterOf(Far));
            var shuttle = SpawnAboard(_framedShuttlePrefab, ship, new Vector3(6f, 1f, -8f));
            var pilot = SpawnAboard(_pawnPrefab, ship, new Vector3(0f, 1f, 2f), PilotClient);
            var passenger = SpawnAboard(_pawnPrefab, shuttle, new Vector3(0f, 0.5f, 1f), PilotClient + 1);

            Assert.That(NebulaChunks.GridOf(pilot.Container), Is.Null, "the ship's box is not a chunk");
            Assert.That(NebulaChunks.GridHolding(pilot.Container), Is.SameAs(planet));
            Assert.That(NebulaChunks.GridHolding(chunk), Is.SameAs(planet), "a chunk holds itself");
            Assert.That(NebulaChunkedWorld.OriginGridOf(pilot, pub), Is.SameAs(planet), "a pilot follows the world the ship is in");
            Assert.That(NebulaChunkedWorld.OriginGridOf(passenger, pub), Is.SameAs(planet), "at any depth");
            Assert.That(NebulaChunkedWorld.OriginGridOf(null, pub), Is.SameAs(pub), "no pawn: the public grid, as before");
        }
    }
}
