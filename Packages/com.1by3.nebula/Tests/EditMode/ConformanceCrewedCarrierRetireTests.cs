using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 18 (<c>docs/conformance-suite.md</c>, design <c>docs/dynamic-worlds.md</c>, "Retiring a
    /// chunk under a carrier"): a chunk is never retired from under a client's pawn, however deep in carriers the
    /// pawn rides, nor from under an entity that is filed under it but stands in live space elsewhere; and a chunk
    /// that is retired takes a vehicle's cargo with the vehicle, each rider saved aboard its carrier.
    /// <para>
    /// Before this, the allocator counted only the entities directly in a chunk. A sample with fast ships outran its
    /// chunk leases: every ship stayed filed under the last chunk it had left, that chunk fell out of the pilots'
    /// ring, and after the retire delay the allocator checkpointed and despawned the ships from under their pilots,
    /// who were left seated in carriers that no longer existed.
    /// </para>
    /// <para>
    /// Tier B: one <b>real</b> <see cref="NebulaWorker"/> on the <see cref="ConformanceMesh"/> with a real
    /// <see cref="RuntimeGridAllocator"/> over a <see cref="LocalControlPlane"/> whose clock the test drives, as in
    /// <c>ConformanceScopedGridTests</c>. The control plane's rows are not mirrored into the registry, so a cell the
    /// allocator asks for gets a lease row and never a registered box: exactly the "leases fell behind" state the
    /// bug was seen in. The persistence half uses a real <see cref="NebulaPersistence"/> over a
    /// <see cref="LocalPersistenceStore"/> (memory backend).
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceCrewedCarrierRetireTests
    {
        private static readonly Vector3 Cell = new Vector3(64f, 64f, 64f);
        private const float RetireAfter = 30f;
        private const ulong PilotClient = 7;

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private LocalPersistenceStore _store;
        private DateTime _clock;
        private ushort _shipPrefab, _shuttlePrefab, _cratePrefab, _pawnPrefab;

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(1);
            _shipPrefab = _mesh.RegisterPrefab(Carrier("ship-prefab", "hull", new Vector3(40f, 12f, 40f), new Vector3(0f, 4f, 0f)));
            _shuttlePrefab = _mesh.RegisterPrefab(Carrier("shuttle-prefab", "cabin", new Vector3(8f, 4f, 8f), new Vector3(0f, 1.5f, 0f)));
            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            crate.AddComponent<PersistentEntity>();
            _cratePrefab = _mesh.RegisterPrefab(crate);
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);

            _clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _plane = new LocalControlPlane { Clock = () => _clock };
            _plane.Connect();
            AttachRegistered(W.Instance, _plane);
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            _plane.Dispose();
            _store?.Dispose();
            _store = null;
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
        }

        /// <summary>A persistent vehicle prefab carrying a box of its own.</summary>
        private static GameObject Carrier(string name, string label, Vector3 size, Vector3 center)
        {
            var go = new GameObject(name);
            go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = label;
            box.Size = size;
            box.Center = center; // the origin is well inside its own box, clear of the floor face
            go.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            go.AddComponent<PersistentEntity>();
            return go;
        }

        /// <summary>
        /// Points <paramref name="worker"/> at <paramref name="plane"/> and marks it registered into the plane's
        /// current document, as <see cref="WorkerRegistration.Register"/> would, without writing a worker row.
        /// </summary>
        private static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", flags).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", flags).SetValue(registration, plane.DocumentId ?? "");
        }

        private NebulaPersistence AttachPersistence()
        {
            _store = new LocalPersistenceStore(); // memory backend
            _store.Connect();
            var persistence = new NebulaPersistence(W.Instance, _mesh.Config, _store);
            typeof(NebulaWorker).GetProperty(nameof(NebulaWorker.Persistence)).SetValue(W.Instance, persistence);
            return persistence;
        }

        /// <summary>
        /// One chunk owned by the worker: registered in the registry and leased on the control plane at the current
        /// clock, as the allocator's own request and the orchestrator's assignment would have left it.
        /// </summary>
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
            new RuntimeGridAllocator(W.Instance, grid) { TickIntervalSeconds = 0f, RetireAfterSeconds = RetireAfter, Ring = 1 };

        private void Tick(RuntimeGridAllocator allocator, float time) => W.Act(() => allocator.Tick(time));

        /// <summary>Let the retire delay run out, on the allocator's clock and on the lease rows' clock alike.</summary>
        private void PassRetireDelay() => _clock = _clock.AddSeconds(RetireAfter + 1f);

        private NetworkIdentity SpawnShip(Container chunk, Vector3 position)
        {
            var ship = W.SpawnServerDriven(_shipPrefab, chunk, position, Quaternion.identity);
            Assume.That(ship.Carried, Is.Not.Null, "the ship registered its box");
            Assume.That(ship.Container, Is.SameAs(chunk));
            return ship;
        }

        /// <summary>Spawn <paramref name="prefab"/> aboard <paramref name="carrier"/>, owned by a client when <paramref name="client"/> is not 0.</summary>
        private NetworkIdentity SpawnAboard(ushort prefab, NetworkIdentity carrier, ulong client = 0)
        {
            NetworkIdentity rider = null;
            W.Act(() =>
            {
                rider = NetworkPrefabs.Instantiate(prefab, carrier.transform.position + Vector3.up, Quaternion.identity, carrier.transform);
                if (client != 0) W.Instance.Spawn(rider, carrier.Carried, client);
                else W.Instance.SpawnServerDriven(rider, carrier.Carried);
            });
            Assume.That(rider.Container, Is.SameAs(carrier.Carried), $"{rider} rides in {carrier}");
            return rider;
        }

        private bool Alive(NetworkIdentity e) => e != null && W.Find(e.NetId) == e;

        private PersistedEntityRecord RecordOf(string key)
        {
            PersistedEntityRecord record = null;
            bool answered = false;
            _store.Load(key, r => { record = r; answered = true; });
            _store.Tick();
            Assert.That(answered, Is.True);
            return record;
        }

        /// <summary>
        /// The bug as it was seen: a ship outruns the chunk leases, stays filed under the chunk it left, and that
        /// chunk falls out of the ring. With its pilot aboard it keeps the chunk; empty, it is unloaded with it.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void AChunkAFastShipLeftBehindIsRetiredOnlyWhenNobodyIsAboard(bool crewed)
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Vector3Int.zero);
            var ship = SpawnShip(chunk, grid.CenterOf(Vector3Int.zero));
            var pilot = crewed ? SpawnAboard(_pawnPrefab, ship, PilotClient) : null;

            // About 17 km past the only chunk there is. Container resolution keeps the ship in the nearest box when
            // no box holds it, so it is still filed under the chunk it left.
            var far = new Vector3Int(270, 0, 0);
            ship.transform.position = grid.CenterOf(far);
            Assume.That(ship.Container, Is.SameAs(chunk));
            Assume.That(grid.CoordOf(ship), Is.EqualTo(far));

            var allocator = Allocator(grid);
            Tick(allocator, 0f);
            Assert.That(allocator.IsWanted(Vector3Int.zero), Is.False, "the ring follows where the pilot is, not the chunk the ship is filed under");
            Assert.That(allocator.IsWanted(far), Is.EqualTo(crewed), "the chunks around the pilot are asked for, and are not there yet");

            PassRetireDelay();
            Tick(allocator, RetireAfter + 1f);

            if (crewed)
            {
                Assert.That(_plane.FindLease(chunk.ContainerId), Is.Not.Null, "a chunk holding a ship with a client's pawn aboard is occupied");
                Assert.That(Alive(ship), Is.True, "the ship was not despawned from under its pilot");
                Assert.That(Alive(pilot), Is.True);
                Assert.That(pilot.Container, Is.SameAs(ship.Carried), "and the pilot is still in its seat");
            }
            else
            {
                Assert.That(_plane.FindLease(chunk.ContainerId), Is.Null, "an empty ship in space nobody wants is unloaded with its chunk");
                Assert.That(Alive(ship), Is.False);
            }
        }

        /// <summary>
        /// The occupancy rule on its own, at every depth. The pawn is moved away from its carrier while it is still
        /// filed aboard (as it is between two ticks), so its ring does not cover the chunk and the ship stands in
        /// its own chunk: only occupancy can keep the chunk. A pilot in a ship, and a passenger in a shuttle in that
        /// ship's hangar, both occupy it; an empty ship does not.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void AClientsPawnRidingAtAnyDepthOccupiesTheChunkItsCarrierIsIn(int depth)
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var coord = new Vector3Int(1, 0, 0);
            var chunk = Chunk(grid, coord);
            var ship = SpawnShip(chunk, grid.CenterOf(coord));
            NetworkIdentity pawn = null;
            if (depth == 1) pawn = SpawnAboard(_pawnPrefab, ship, PilotClient);
            if (depth == 2) pawn = SpawnAboard(_pawnPrefab, SpawnAboard(_shuttlePrefab, ship), PilotClient);
            var away = new Vector3Int(40, 0, 0);
            if (pawn != null)
            {
                pawn.transform.position = grid.CenterOf(away);
                Assume.That(pawn.Container, Is.Not.SameAs(chunk), "still filed aboard");
            }

            var allocator = Allocator(grid);
            Tick(allocator, 0f);
            PassRetireDelay();
            Tick(allocator, RetireAfter + 1f);

            Assert.That(allocator.IsWanted(coord), Is.False, "nothing wants the chunk the ship is in");
            bool occupied = depth > 0;
            Assert.That(_plane.FindLease(chunk.ContainerId) != null, Is.EqualTo(occupied),
                occupied ? $"a client's pawn {depth} carrier(s) deep occupies the chunk" : "an empty ship does not occupy the chunk");
            Assert.That(Alive(ship), Is.EqualTo(occupied));
            if (occupied) Assert.That(Alive(pawn), Is.True);
        }

        /// <summary>
        /// The stale-registration rule on its own: an uncrewed ship filed under a chunk it has left keeps that chunk
        /// while it stands in a cell this worker wants or another worker has leased, because it moves into that
        /// cell as soon as the cell is registered. Standing in space nobody wants, it is unloaded with the chunk.
        /// </summary>
        [TestCase("wanted here", true)]
        [TestCase("leased by another worker", true)]
        [TestCase("nowhere", false)]
        public void AnEntityFiledUnderAChunkKeepsItWhileItStandsWhereTheWorldIsLoaded(string where, bool kept)
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Vector3Int.zero);
            // A player ten cells away, in a chunk of its own, keeps the ring around (10, 0, 0) wanted.
            var home = Chunk(grid, new Vector3Int(10, 0, 0));
            W.Act(() =>
            {
                var player = NetworkPrefabs.Instantiate(_pawnPrefab, grid.CenterOf(new Vector3Int(10, 0, 0)), Quaternion.identity, home.transform);
                W.Instance.Spawn(player, home, PilotClient);
            });
            var ship = SpawnShip(chunk, grid.CenterOf(Vector3Int.zero));

            var standsIn = where == "wanted here" ? new Vector3Int(9, 0, 0)
                : where == "leased by another worker" ? new Vector3Int(20, 0, 0)
                : new Vector3Int(5, 0, 0);
            ship.transform.position = grid.CenterOf(standsIn);
            Assume.That(ship.Container, Is.SameAs(chunk));

            var allocator = Allocator(grid);
            Tick(allocator, 0f);
            PassRetireDelay();
            if (where == "leased by another worker")
                _plane.EnsureRuntimeContainer(grid.ContainerIdOf(standsIn), ContainerRegistry.ToAbsolute(grid.BoundsOf(standsIn), 0UL), "w2", null); // asked for just now
            Tick(allocator, RetireAfter + 1f);

            Assert.That(allocator.IsWanted(standsIn), Is.EqualTo(where == "wanted here"));
            Assert.That(_plane.FindLease(chunk.ContainerId) != null, Is.EqualTo(kept), $"the ship stands {where}");
            Assert.That(Alive(ship), Is.EqualTo(kept));
            Assert.That(_plane.FindLease(home.ContainerId), Is.Not.Null, "the player's own chunk is wanted throughout");
        }

        /// <summary>
        /// A chunk that is retired takes a vehicle's cargo with the vehicle: every rider at every depth is despawned,
        /// none is set down in the chunk being deleted, and each persistent one is saved aboard its carrier, so it
        /// comes back with the carrier. The forced checkpoint of a retiring part saves the riders too.
        /// </summary>
        [Test]
        public void ReleasingAChunkTakesEverythingAboardItsShipsAndSavesItAboard()
        {
            var persistence = AttachPersistence();
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Vector3Int.zero);
            var ship = SpawnShip(chunk, grid.CenterOf(Vector3Int.zero));
            var shuttle = SpawnAboard(_shuttlePrefab, ship);
            var crate = SpawnAboard(_cratePrefab, shuttle);
            var drone = SpawnAboard(_pawnPrefab, ship); // transient: nothing to save
            string shipKey = ship.Persistent.Key, shuttleKey = shuttle.Persistent.Key, crateKey = crate.Persistent.Key;

            Assert.That(persistence.CheckpointContainer(chunk.ContainerId), Is.EqualTo(3),
                "the forced checkpoint saves the ship, the shuttle in its hangar and the crate in the shuttle");

            bool released = false;
            W.Act(() => released = W.Instance.ReleaseRuntimeContainer(chunk.RuntimeId));
            Assert.That(released, Is.True);
            Assert.That(_plane.FindLease(chunk.ContainerId), Is.Null);

            foreach (var e in new[] { ship, shuttle, crate, drone }) Assert.That(Alive(e), Is.False, "everything aboard leaves with the ship");
            Assert.That(chunk.Entities, Is.Empty, "no rider was set down in the chunk being deleted");

            var shipRecord = RecordOf(shipKey);
            Assert.That(shipRecord, Is.Not.Null);
            Assert.That(shipRecord.ContainerId, Is.EqualTo(chunk.ContainerId), "the ship comes back with its chunk");
            Assert.That(shipRecord.CarrierKey, Is.Empty);
            var shuttleRecord = RecordOf(shuttleKey);
            Assert.That(shuttleRecord.CarrierKey, Is.EqualTo(shipKey), "the shuttle was saved aboard the ship");
            Assert.That(shuttleRecord.ContainerId, Is.Empty);
            Assert.That(RecordOf(crateKey).CarrierKey, Is.EqualTo(shuttleKey), "the crate was saved aboard the shuttle");
        }

        /// <summary>
        /// The orchestrator's view of a scope counts riders (NEB-259). The worker's telemetry reports a pilot under
        /// the ship's own carried container, which is right for cost, and the scope's occupancy folds it into the
        /// chunk the ship is in, through a shuttle in the ship's hangar too. A custom retire policy reading
        /// <see cref="ScopeRetireContext.Players"/> must not see an empty scope when every player is aboard a vehicle.
        /// </summary>
        [Test]
        public void PlayersAboardVehiclesCountTowardsTheirScopesOccupancy()
        {
            const string scopeKey = "world/crewed";
            var grid = new RuntimeGrid(Cell, planar: true, scopeKey: scopeKey);
            _plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = scopeKey,
                Definition = new ChunkGridDefinition { CellSize = Cell, Planar = true }.ToScopeDefinition(),
                PreferredWorkerId = W.Id,
            });
            Chunk(grid, Vector3Int.zero);
            var coord = new Vector3Int(2, 0, 0);
            var chunk = Chunk(grid, coord);
            var ship = SpawnShip(chunk, grid.CenterOf(coord));
            SpawnAboard(_pawnPrefab, ship, PilotClient);
            SpawnAboard(_pawnPrefab, SpawnAboard(_shuttlePrefab, ship), PilotClient + 1);

            string document = null;
            W.Act(() => document = WorkerTelemetry.ForTests().Write(W.Id, W.Index, 1, W.Instance.Entities, false, null));
            var mesh = new MeshTelemetry(() => 0.0);
            Assert.That(mesh.Accept(document, out _), Is.Null);
            var occupancy = new Dictionary<string, ContainerLoad>();
            mesh.CopyOccupancy(occupancy);
            Assert.That(occupancy[chunk.ContainerId].Players, Is.EqualTo(0), "for cost the pilots stay in the containers they ride in");
            Assert.That(occupancy[ship.Carried.ContainerId].Enclosing, Is.EqualTo(chunk.ContainerId), "the telemetry says where the ship is");

            var parts = new List<string>();
            ScopeLifecycle.CollectParts(_plane, _plane.FindScope(scopeKey), parts);
            ScopeLifecycle.Occupancy(parts, occupancy, out int entities, out int players);
            Assert.That(players, Is.EqualTo(2), "the pilot, and the passenger of the shuttle in the ship's hangar");
            Assert.That(entities, Is.EqualTo(4), "the ship, the shuttle and both riders");
        }

        /// <summary>
        /// The scope lifecycle's idle rule reads the same contents: a part is busy while emptying it would lose
        /// something aboard a vehicle in it, whether that is a client's pawn or transient state.
        /// </summary>
        [Test]
        public void APartIsBusyWhileAnythingAboardAShipInItWouldBeLost()
        {
            var persistence = AttachPersistence();
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Vector3Int.zero);
            var ship = SpawnShip(chunk, grid.CenterOf(Vector3Int.zero));
            persistence.SaveNow(ship);
            Assert.That(WorkerScopeLifecycle.IsBusy(chunk), Is.False, "a saved ship with nobody aboard loses nothing");

            var drone = SpawnAboard(_pawnPrefab, ship);
            Assert.That(WorkerScopeLifecycle.IsBusy(chunk), Is.True, "transient state aboard would be lost");
            W.Act(() => W.Instance.Despawn(drone));
            Assert.That(WorkerScopeLifecycle.IsBusy(chunk), Is.False);

            var shuttle = SpawnAboard(_shuttlePrefab, ship);
            persistence.SaveNow(shuttle);
            Assert.That(WorkerScopeLifecycle.IsBusy(chunk), Is.False, "a saved shuttle in the ship's hangar loses nothing either");
            SpawnAboard(_pawnPrefab, shuttle, PilotClient);
            Assert.That(WorkerScopeLifecycle.IsBusy(chunk), Is.True, "a client's pawn two carriers deep is standing in the part");
        }
    }
}
