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
    /// Placing and transferring an entity by a frame-local pose (<see cref="NetworkIdentity.PlaceInFrame(Container, Double3, Quaternion)"/>,
    /// <see cref="NebulaWorker.TryCommitTransfer(InstanceTransfer, Container, Double3, Quaternion)"/>,
    /// <see cref="NebulaWorker.TryCommitTransfers(IReadOnlyList{InstanceTransfer}, Double3)"/>): a planet whose carrier
    /// stands 300,000 km from its scope's origin, where a float is 32 m apart, and a pad 205 km from the planet's centre.
    /// A pose given in the planet's own coordinates lands exactly, to the millimetre, in the chunk of the planet's ground
    /// that holds it, on the worker and on the wire, because it is never rounded through the scope's own space.
    /// <para>
    /// Tier B: one real <see cref="NebulaWorker"/> on the <see cref="ConformanceMesh"/>, a <see cref="LocalControlPlane"/>
    /// for the scopes and the lease rows, and preview scenes for the frames, as in
    /// <see cref="ConformanceFramedInstanceBoundaryTests"/>.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFrameLocalPlacementTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Scope = "space/far";
        private const string GridKey = "planet/far/0";
        private static readonly Vector3 SpaceCell = new Vector3(8192f, 8192f, 8192f);
        /// <summary>The planet's carrier, absolute: 300,000 km out, where a float is 32 m apart.</summary>
        private static readonly Vector3 PlanetAt = new Vector3(300_000_000f, 0f, 0f);
        private static readonly Vector3 PlanetBox = new Vector3(500_000f, 2_000f, 500_000f);
        private static readonly Vector3 GroundCell = new Vector3(512f, 2_000f, 512f);
        /// <summary>A pad on the planet's ground, in the planet's own coordinates: 205 km from its centre, with fractions no float there holds.</summary>
        private static readonly Double3 Pad = new Double3(204_801.234567, 1.5, -1_234.987654);
        private const double Millimetre = 1e-3;

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private WorldDefinition _definition;
        private InstanceTemplate _template;
        private ushort _planetPrefab, _pawnPrefab, _shipPrefab;
        private uint _tick;
        private RuntimeGrid _ground;

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = GroundCell;
            NebulaWorld.LoadRuntime(_definition);
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
            _template.TemplateId = "hangar";
            _template.Parts = new[] { new InstanceTemplate.Part { Id = "hall", Bounds = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(16f, 4f, 16f)) } };
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

            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var interior = ship.AddComponent<Container>();
            interior.ContainerId = "ship";
            interior.Size = new Vector3(20f, 8f, 40f);
            interior.Center = new Vector3(0f, 4f, 0f);
            interior.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>();
            _shipPrefab = _mesh.RegisterPrefab(ship);

            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);
            _tick = 0;
            _ground = null;
        }

        [TearDown]
        public void TearDown()
        {
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

        /// <summary>The space scope with the planet in its chunk 300,000 km out, and the planet's ground (a grid the planet hosts) with the pad's chunk leased.</summary>
        private Container Build(out NetworkIdentity planet)
        {
            var space = new RuntimeGrid(SpaceCell, planar: false, scopeKey: Scope);
            NebulaChunks.Activate(space, NebulaRoles.Worker, true, null);
            _plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Scope,
                Definition = new ChunkGridDefinition { CellSize = SpaceCell, Planar = false }.ToScopeDefinition(),
                PreferredWorkerId = W.Id,
            });
            var coord = space.CoordOfAbsolute(PlanetAt);
            var spaceInstance = new InstanceContainerInfo { InstanceId = space.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(coord) };
            var spaceChunk = ContainerRegistry.RegisterRuntime(space.IdOf(coord), space.BoundsOf(coord), spaceInstance);
            ContainerRegistry.ApplyLease(spaceChunk.ContainerId, W.Id, W.Index, 1);
            _plane.EnsureRuntimeContainer(spaceChunk.ContainerId, ContainerRegistry.ToAbsolute(space.BoundsOf(coord), space.InstanceId), W.Id, spaceInstance);

            NetworkIdentity p = null;
            W.Act(() =>
            {
                p = NetworkPrefabs.Instantiate(_planetPrefab, PlanetAt + space.Frame.OriginOffset, Quaternion.identity, spaceChunk.ContentRoot);
                W.Instance.SpawnServerDriven(p, spaceChunk);
            });
            planet = p;
            Assume.That(planet.Carried, Is.Not.Null, "the planet carries its box");
            Assume.That(planet.Carried.Frame, Is.Not.Null, "and its box has a frame of its own");
            Assume.That(Vector3.Distance(planet.transform.position, PlanetAt), Is.LessThan(64f), "the carrier stands 300,000 km from the scope's origin");
            return AddGroundChunk(planet, Pad);
        }

        private Container AddGroundChunk(NetworkIdentity planet, Double3 at)
        {
            if (_ground == null)
            {
                _ground = RuntimeGrid.Hosted(new ChunkGridDefinition { CellSize = GroundCell, Planar = true }, Scope, GridKey, planet.Carried.ContainerId);
                NebulaChunks.Activate(_ground, NebulaRoles.Worker, true, null);
            }
            var ground = _ground;
            var coord = ground.CoordOfAbsolute(at.ToVector3());
            var info = new InstanceContainerInfo { InstanceId = ScopeKeys.Hash(Scope), ScopeKey = Scope, PartId = ground.PartIdOf(coord) };
            _plane.EnsureRuntimeContainer(ground.ContainerIdOf(coord), ground.PlacementOf(coord), W.Id, info);
            Mirror();
            var chunk = ContainerRegistry.GetRuntime(ground.IdOf(coord));
            Assume.That(chunk, Is.Not.Null, "the pad's chunk registers under the planet");
            Assume.That(chunk.InnerSpace, Is.SameAs(planet.Carried), "in the planet's frame");
            return chunk;
        }

        private NetworkIdentity Spawn(ushort prefab, Container container, Vector3 position, Quaternion rotation)
        {
            NetworkIdentity e = null;
            W.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(prefab, position, rotation, container.ContentRoot);
                e.transform.localPosition = position;
                e.transform.localRotation = rotation;
                W.Instance.SpawnServerDriven(e, container);
            });
            return e;
        }

        /// <summary>The spawn as a gateway, and through it every client, receives it: written by the worker, read back from the bytes.</summary>
        private EntitySpawnMsg OnTheWire(NetworkIdentity entity)
        {
            EntitySpawnMsg sent = default;
            W.Act(() => sent = EntitySpawnMsg.From(entity, new NetworkWriter(256), forGateway: true));
            var writer = new NetworkWriter(512);
            sent.Write(writer, MsgId.EntitySpawn);
            var reader = new NetworkReader(writer.ToSegment());
            Assert.AreEqual((byte)MsgId.EntitySpawn, reader.ReadByte());
            return EntitySpawnMsg.Read(reader);
        }

        /// <summary>Where a frame-local point is in <paramref name="chunk"/>'s own coordinates, worked out in double from the chunk's place in the frame.</summary>
        private static Double3 InChunk(Container chunk, Double3 frameLocal) => frameLocal - chunk.transform.localPosition;

        private static double Distance(Double3 a, Vector3 b) =>
            Math.Sqrt((a.X - b.x) * (a.X - b.x) + (a.Y - b.y) * (a.Y - b.y) + (a.Z - b.z) * (a.Z - b.z));

        private static double Distance(Double3 a, Double3 b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

        /// <summary>The entity is in <paramref name="chunk"/>, in the planet's frame, at the frame-local <see cref="Pad"/> to the millimetre, on the worker and on the wire.</summary>
        private void AssertOnThePad(NetworkIdentity entity, NetworkIdentity planet, Container chunk, Quaternion rotation, string how)
        {
            Assert.AreSame(chunk, entity.Container, how + ": in the pad's chunk");
            Assert.AreSame(planet.Carried, entity.Space, how + ": in the planet's frame");
            var expected = InChunk(chunk, Pad);
            double local = Distance(expected, entity.LocalPosition);
            double frame = Distance(Pad, entity.FramePositionPrecise);
            var wire = OnTheWire(entity);
            double onTheWire = Distance(expected, wire.LocalPosition);
            TestContext.WriteLine($"{how}: chunk-local error {local:E3} m, frame-local error {frame:E3} m, wire error {onTheWire:E3} m");
            Assert.AreEqual(ContainerRef.Of(chunk), wire.Container, how + ": the wire names the chunk");
            Assert.Less(local, Millimetre, how + ": the chunk-local pose is exact");
            Assert.Less(frame, Millimetre, how + ": and so is the frame-local one");
            Assert.Less(onTheWire, Millimetre, how + ": and clients receive it");
            Assert.Less(Quaternion.Angle(rotation, entity.LocalRotation), 0.01f, how + ": facing as asked (the chunk is not turned in the frame)");
        }

        // ------------------------------------------------------------------------------------ placement in a scope

        [Test]
        public void PlacedOnAPadOfAFarFramedPlanetAnEntityLandsExactlyInTheChunk()
        {
            var chunk = Build(out var planet);
            var pawn = Spawn(_pawnPrefab, planet.Carried, new Vector3(0f, 1f, 0f), Quaternion.identity);
            Assume.That(pawn.Space, Is.SameAs(planet.Carried));
            var facing = Quaternion.Euler(0f, 37.5f, 0f);
            W.Act(() => pawn.PlaceInFrame(planet.Carried, Pad, facing));
            AssertOnThePad(pawn, planet, chunk, facing, "placed in the frame");
        }

        [Test]
        public void PlacedFromTheScopesOwnSpaceTheEntityLandsInTheFrameExactly()
        {
            var chunk = Build(out var planet);
            var spaceChunk = planet.Container;
            var pawn = Spawn(_pawnPrefab, spaceChunk, planet.transform.localPosition + new Vector3(0f, 300_000f, 0f), Quaternion.identity);
            Assume.That(pawn.Space, Is.Null, "starts in the scope's own space, outside the planet");
            W.Act(() => pawn.PlaceInFrame(planet.Carried, Pad, Quaternion.identity));
            AssertOnThePad(pawn, planet, chunk, Quaternion.identity, "placed from space");

            // For comparison, the same place reached through the scope's own space by an entity already in the planet's
            // frame (PlaceInScope): the float 300,000 km out rounds it by metres.
            var other = Spawn(_pawnPrefab, planet.Carried, new Vector3(0f, 1f, 0f), Quaternion.identity);
            var scope = PhysicsFrames.ConvertLocal(Pad, planet.Carried, null);
            W.Act(() => other.PlaceInScope(scope.ToVector3(), Quaternion.identity));
            Assume.That(other.Space, Is.SameAs(planet.Carried));
            TestContext.WriteLine($"PlaceInScope at {scope}: frame-local error {Distance(Pad, other.FramePositionPrecise):0.000} m, in {other.Container?.ContainerId}");
        }

        [Test]
        public void APointNotInAnyChunkLandsDirectlyInTheFrame()
        {
            Build(out var planet);
            var pawn = Spawn(_pawnPrefab, planet.Carried, Vector3.zero, Quaternion.identity);
            var near = new Double3(12.345678, 2.5, -7.654321);
            W.Act(() => pawn.PlaceInFrame(planet.Carried, near, Quaternion.identity));
            Assert.AreSame(planet.Carried, pawn.Container, "no chunk holds the point: the planet's own box does");
            double error = Distance(near, pawn.LocalPosition);
            TestContext.WriteLine($"directly in the frame: error {error:E3} m");
            Assert.Less(error, 1e-5, "frame-local, to the float near the frame's origin");
            Assert.Less(Distance(near, OnTheWire(pawn).LocalPosition), 1e-5, "and on the wire");
        }

        [Test]
        public void APointInsideAShipOnThePlanetEntersTheShipsFrame()
        {
            var chunk = Build(out var planet);
            var shipAt = InChunk(chunk, Pad + new Double3(40.0, 0.5, 0.0)).ToVector3();
            var shipTurn = Quaternion.Euler(0f, 30f, 0f);
            var ship = Spawn(_shipPrefab, chunk, shipAt, shipTurn);
            Assume.That(ship.Carried, Is.Not.Null, "the ship carries its interior");
            Assume.That(ship.Carried.Frame, Is.Not.Null, "with a frame of its own");
            Assume.That(ship.Space, Is.SameAs(planet.Carried), "the ship stands in the planet's frame");
            var pawn = Spawn(_pawnPrefab, chunk, InChunk(chunk, Pad).ToVector3(), Quaternion.identity);

            // A seat in the ship, given in the planet's coordinates.
            var seatInShip = new Double3(1.25, 1.0, -3.5);
            var seatInPlanet = PhysicsFrames.ConvertLocal(seatInShip, ship.Carried, planet.Carried);
            W.Act(() => pawn.PlaceInFrame(planet.Carried, seatInPlanet, shipTurn));
            Assert.AreSame(ship.Carried, pawn.Container, "aboard the ship");
            Assert.AreSame(ship.Carried, pawn.Space, "in its frame");
            double error = Distance(seatInShip, pawn.LocalPosition);
            TestContext.WriteLine($"aboard: ship-local error {error:E3} m");
            Assert.Less(error, Millimetre, "at the seat, in the ship's coordinates");
            Assert.Less(Quaternion.Angle(Quaternion.identity, pawn.LocalRotation), 0.01f, "facing the ship's forward");
        }

        [Test]
        public void PlacingInAFrameOfAnotherScopeIsRefused()
        {
            Build(out var planet);
            var hall = PrepareHall(new Double3(1000, 0, 1000));
            var pawn = Spawn(_pawnPrefab, hall, new Vector3(0f, 1f, 0f), Quaternion.identity);
            Assert.Throws<InvalidOperationException>(() => W.Act(() => pawn.PlaceInFrame(planet.Carried, Pad, Quaternion.identity)));
        }

        // ------------------------------------------------------------------------------------ transfers into the planet

        private Container PrepareHall(Double3 at)
        {
            var hallRef = W.Instance.PrepareInstance(_template, "crew-" + at.X, at)[0];
            Tick();
            var hall = hallRef.Resolve();
            Assume.That(hall, Is.Not.Null, "the hangar instance is prepared");
            return hall;
        }

        private InstanceTransfer Prepare(NetworkIdentity entity, Container destination)
        {
            var transfer = W.Instance.PrepareTransfer(entity, destination);
            for (int i = 0; i < 5 && !transfer.Ready && transfer.Error == null; i++) { transfer.ClientReady = true; Tick(); }
            Assert.IsNull(transfer.Error, "the preparation succeeds");
            Assert.IsTrue(transfer.Ready, "and is ready");
            return transfer;
        }

        [Test]
        public void ATransferCommittedAtAFrameLocalPoseLandsExactlyOnThePad()
        {
            var chunk = Build(out var planet);
            var hall = PrepareHall(new Double3(1000, 0, 1000));
            var pawn = Spawn(_pawnPrefab, hall, new Vector3(0f, 1f, 0f), Quaternion.identity);
            var transfer = Prepare(pawn, chunk);
            var facing = Quaternion.Euler(0f, -110f, 0f);
            uint epoch = pawn.Epoch;
            bool committed = false;
            W.Act(() => committed = W.Instance.TryCommitTransfer(transfer, planet.Carried, Pad, facing));
            Assert.IsTrue(committed, "committed");
            Assert.AreEqual(ScopeKeys.Hash(Scope), pawn.InstanceId, "in the planet's scope");
            Assert.AreEqual(epoch + 1, pawn.Epoch, "a new epoch, as every commit");
            Assert.IsTrue(transfer.Finished);
            AssertOnThePad(pawn, planet, chunk, facing, "transferred into the frame");
        }

        [Test]
        public void ATransferCommittedAtAScopePoseIsRoundedThroughTheScopeAndTheFrameLocalOneIsNot()
        {
            // The old commit, for comparison: the same pad named in the scope's own space, a float 300,000 km out.
            var chunk = Build(out var planet);
            var hall = PrepareHall(new Double3(1000, 0, 1000));
            var pawn = Spawn(_pawnPrefab, hall, new Vector3(0f, 1f, 0f), Quaternion.identity);
            var transfer = Prepare(pawn, chunk);
            var scope = PhysicsFrames.ConvertLocal(Pad, planet.Carried, null);
            bool committed = false;
            W.Act(() => committed = W.Instance.TryCommitTransfer(transfer, scope.ToVector3(), Quaternion.identity));
            Assert.IsTrue(committed);
            TestContext.WriteLine($"scope-pose commit: frame-local error {Distance(Pad, pawn.FramePositionPrecise):0.000} m");

            // The scope's own space as the frame (null) in double is exact again.
            var other = Spawn(_pawnPrefab, hall, new Vector3(1f, 1f, 0f), Quaternion.identity);
            var second = Prepare(other, chunk);
            W.Act(() => committed = W.Instance.TryCommitTransfer(second, null, scope, Quaternion.identity));
            Assert.IsTrue(committed);
            AssertOnThePad(other, planet, chunk, Quaternion.identity, "committed at a double scope pose");
        }

        [Test]
        public void AGroupTranslatedInDoubleLandsExactlyOnThePad()
        {
            var chunk = Build(out var planet);
            var hall = PrepareHall(new Double3(1000, 0, 1000));
            var lead = Spawn(_pawnPrefab, hall, new Vector3(0f, 1f, 0f), Quaternion.identity);
            var wing = Spawn(_pawnPrefab, hall, new Vector3(2.5f, 1f, -1.25f), Quaternion.identity);
            var group = new List<InstanceTransfer> { Prepare(lead, chunk), Prepare(wing, chunk) };
            Double3 translation = default, leadAt = default, wingAt = default;
            W.Act(() =>
            {
                leadAt = lead.ScopePositionPrecise;
                wingAt = wing.ScopePositionPrecise;
                translation = PhysicsFrames.ConvertLocal(Pad, planet.Carried, null) - leadAt;
            });
            bool committed = false;
            W.Act(() => committed = W.Instance.TryCommitTransfers(group, translation));
            Assert.IsTrue(committed, "the group commits");
            AssertOnThePad(lead, planet, chunk, Quaternion.identity, "the lead");
            Assert.AreSame(chunk, wing.Container, "the wing lands in the same chunk");
            var wingExpected = Pad + new Vector3(2.5f, 0f, -1.25f);
            double error = Distance(wingExpected, wing.FramePositionPrecise);
            TestContext.WriteLine($"the wing: frame-local error {error:E3} m");
            Assert.Less(error, Millimetre, "and keeps its place beside the lead to the millimetre");
        }
    }
}
