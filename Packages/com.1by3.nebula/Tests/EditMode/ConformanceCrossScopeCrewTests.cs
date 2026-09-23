using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The worker half of conformance scenario 16 (<c>docs/conformance-suite.md</c>, design
    /// <c>docs/scope-activation.md</c> §11): a ship and its crew are committed into a chunk of another grid scope as
    /// one group, and the crew arrive in their seats. Tier B — one real <see cref="NebulaWorker"/> on the
    /// <see cref="ConformanceMesh"/> with a real carried <see cref="Container"/>, because what is asserted is what
    /// <see cref="NebulaWorker.TryCommitTransfers"/> does to live containers and entities, which no fixture has.
    /// The gateway and client half — the crossing becomes ready, and the crew's clients follow the ship — is
    /// <c>Services~/Nebula.Services.Tests/ConformanceCrossScopeCarrierTests.cs</c> (tier A).
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceCrossScopeCrewTests
    {
        private const string Planet = "world/planet";
        private const string Space = "world/space";

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private RuntimeGrid _planet, _space;
        private ushort _crewPrefab, _shipPrefab;

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(1);
            _planet = new RuntimeGrid(new Vector3(64f, 512f, 64f), planar: true, scopeKey: Planet);
            _space = new RuntimeGrid(new Vector3(64f, 64f, 64f), planar: false, scopeKey: Space);
            // A crossing into a scope is admitted only while the scope's row says it is active, so both worlds are
            // activated on a control plane the worker is attached to, as they would be in a mesh.
            _plane = new LocalControlPlane();
            _plane.Connect();
            AttachRegistered(_mesh[0].Instance, _plane);
            _plane.RegisterWorker(_mesh[0].Id, _mesh[0].Index, "127.0.0.1", 7000);
            _plane.HeartbeatWorker(_mesh[0].Id, WorkerStatus.Ready, default);
            foreach (var (key, grid) in new[] { (Planet, _planet), (Space, _space) })
                _plane.ActivateScope(new ScopeActivationRequest
                {
                    ScopeKey = key,
                    Definition = new ChunkGridDefinition { CellSize = grid.CellSize, Planar = key == Planet }.ToScopeDefinition(),
                    PreferredWorkerId = _mesh[0].Id,
                });
            var crew = new GameObject("crew-prefab");
            crew.AddComponent<NetworkIdentity>();
            _crewPrefab = _mesh.RegisterPrefab(crew);
            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var interior = ship.AddComponent<Container>();
            interior.ContainerId = "interior";
            interior.Size = new Vector3(10, 6, 20);
            interior.Center = new Vector3(0, 3, 0);
            ship.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            _shipPrefab = _mesh.RegisterPrefab(ship);
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            _plane.Dispose();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
        }

        /// <summary>Point the worker at <paramref name="plane"/> as if it had registered there (see <c>ConformanceScopedGridTests</c>).</summary>
        private static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", flags).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", flags).SetValue(registration, plane.DocumentId ?? "");
        }

        /// <summary>One chunk of a scoped grid, registered from the lease row the control plane would have written.</summary>
        private Container Chunk(RuntimeGrid grid)
        {
            var container = ContainerRegistry.RegisterRuntime(grid.IdOf(Vector3Int.zero), grid.BoundsOf(Vector3Int.zero), new InstanceContainerInfo
            {
                InstanceId = grid.InstanceId,
                ScopeKey = grid.ScopeKey,
                PartId = ChunkKeys.PartId(Vector3Int.zero),
            });
            ContainerRegistry.ApplyLease(container.ContainerId, _mesh[0].Id, _mesh[0].Index, 1);
            return container;
        }

        private (NetworkIdentity Ship, Container Interior, NetworkIdentity Pilot, NetworkIdentity Gunner) Crewed(Container at)
        {
            var ship = _mesh[0].SpawnServerDriven(_shipPrefab, at, at.WorldBounds.center, Quaternion.identity);
            var interior = ship.GetComponent<Container>();
            var pilot = _mesh[0].SpawnServerDriven(_crewPrefab, interior, ship.transform.position + new Vector3(0, 1, 4), Quaternion.identity);
            var gunner = _mesh[0].SpawnServerDriven(_crewPrefab, interior, ship.transform.position + new Vector3(1, 1, -3), Quaternion.identity);
            Assert.AreSame(interior, pilot.Container, "the crew are seated inside the ship");
            Assert.AreSame(interior, gunner.Container);
            return (ship, interior, pilot, gunner);
        }

        [Test]
        public void ACrewedShipCommittedWithItsCrewKeepsEveryoneInTheirSeats()
        {
            var planet = Chunk(_planet);
            var space = Chunk(_space);
            var (ship, interior, pilot, gunner) = Crewed(planet);
            var pilotSeat = pilot.LocalPosition;
            uint shipEpoch = ship.Epoch, pilotEpoch = pilot.Epoch;

            var group = new List<InstanceTransfer>();
            _mesh[0].Act(() =>
            {
                group.Add(_mesh[0].Instance.PrepareTransfer(ship, space));
                group.Add(_mesh[0].Instance.PrepareTransfer(pilot, space));
                group.Add(_mesh[0].Instance.PrepareTransfer(gunner, space));
            });
            foreach (var t in group) Assert.IsTrue(t.Ready, "every member is ready: the destination is this worker's and nobody here has a client. " + t.Error);

            bool committed = false;
            _mesh[0].Act(() => committed = _mesh[0].Instance.TryCommitTransfers(group, new Vector3(0f, 200f, 0f)));

            Assert.IsTrue(committed);
            Assert.IsTrue(group.TrueForAll(t => t.Finished), "every member's crossing is recorded");
            Assert.AreSame(space, ship.Container, "the ship is in the space chunk");
            Assert.AreSame(interior, pilot.Container, "the crew are still in their seats, not put down in the chunk (D19)");
            Assert.AreSame(interior, gunner.Container);
            Assert.AreEqual(_space.InstanceId, pilot.InstanceId, "and in space, through the ship they ride in");
            Assert.AreEqual(Space, gunner.ScopeKey);
            Assert.AreEqual(pilotSeat.x, pilot.LocalPosition.x, 1e-4f, "exactly where they sat");
            Assert.AreEqual(pilotSeat.z, pilot.LocalPosition.z, 1e-4f);
            Assert.AreEqual(shipEpoch + 1, ship.Epoch);
            Assert.AreEqual(pilotEpoch + 1, pilot.Epoch, "a new epoch, so every copy takes the next state as a fresh location");
        }

        [Test]
        public void ARiderCommittedWithoutItsShipIsPutDownInTheDestination()
        {
            var planet = Chunk(_planet);
            var space = Chunk(_space);
            var (ship, interior, pilot, _) = Crewed(planet);

            InstanceTransfer transfer = null;
            _mesh[0].Act(() => transfer = _mesh[0].Instance.PrepareTransfer(pilot, space));
            Assert.IsTrue(transfer.Ready, transfer.Error);
            bool committed = false;
            _mesh[0].Act(() => committed = _mesh[0].Instance.TryCommitTransfers(new[] { transfer }, Vector3.zero));

            Assert.IsTrue(committed);
            Assert.AreSame(space, pilot.Container, "a rider whose ship stays behind leaves it: the seat rule is only for a carrier in the same group");
            Assert.AreSame(planet, ship.Container);
            Assert.AreEqual(1, interior.Entities.Count, "and the ship keeps only its other rider");
        }
    }
}
