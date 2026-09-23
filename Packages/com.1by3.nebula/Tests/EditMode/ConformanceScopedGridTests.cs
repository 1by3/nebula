using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The worker-side half of conformance scenario 4 (<c>docs/conformance-suite.md</c>, design
    /// <c>docs/scoped-chunk-grids.md</c>): two chunk grids whose coordinates overlap exactly are two worlds, and
    /// nothing ghosts between them. Tier B — two <b>real</b> <see cref="NebulaWorker"/>s on the
    /// <see cref="ConformanceMesh"/>, each owning a chunk, with the sender's own ghost band deciding what to send
    /// and the receiver's own <c>Dispatch</c> applying it. Tier A cannot say this: a <c>FakeWorker</c> implements
    /// the protocol, not the ghost band. What a <i>client</i> is told is the other half of the scenario, in
    /// <c>Services~/Nebula.Services.Tests/ConformanceScopedGridTests.cs</c>.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceScopedGridTests
    {
        private const string Alpha = "world/alpha";
        private const string Beta = "world/beta";
        private static readonly Vector3 Cell = new Vector3(64f, 64f, 64f);

        private ConformanceMesh _mesh;
        private RuntimeGrid _alpha, _beta;
        private ushort _prefabId;

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            _alpha = new RuntimeGrid(Cell, planar: false, scopeKey: Alpha);
            _beta = new RuntimeGrid(Cell, planar: false, scopeKey: Beta);
            var prefab = new GameObject("rock-prefab");
            prefab.AddComponent<NetworkIdentity>();
            _prefabId = _mesh.RegisterPrefab(prefab);
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            ContainerRegistry.PruneRuntime(new System.Collections.Generic.HashSet<ulong>());
            ContainerRegistry.Rebuild();
        }

        /// <summary>One chunk of a scoped grid, registered from the lease row the control plane would have written.</summary>
        private Container Chunk(RuntimeGrid grid, Vector3Int coord, ConformanceMesh.Worker owner)
        {
            var container = ContainerRegistry.RegisterRuntime(grid.IdOf(coord), grid.BoundsOf(coord), new InstanceContainerInfo
            {
                InstanceId = grid.InstanceId,
                ScopeKey = grid.ScopeKey,
                PartId = ChunkKeys.PartId(coord),
            });
            ContainerRegistry.ApplyLease(container.ContainerId, owner.Id, owner.Index, 1);
            return container;
        }

        /// <summary>
        /// Points <paramref name="worker"/> at <paramref name="plane"/> and marks it registered into the plane's
        /// current document, as <see cref="WorkerRegistration.Register"/> would, without writing a worker row.
        /// </summary>
        private static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", flags).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", flags).SetValue(registration, plane.DocumentId ?? "");
        }

        private LocalControlPlane PlaneForWorkers()
        {
            var plane = new LocalControlPlane();
            plane.Connect();
            foreach (var worker in _mesh.Workers)
            {
                AttachRegistered(worker.Instance, plane);
                plane.RegisterWorker(worker.Id, worker.Index, "127.0.0.1", (ushort)(7000 + worker.Index));
                plane.HeartbeatWorker(worker.Id, WorkerStatus.Ready, default);
            }
            return plane;
        }

        private ScopeActivationRequest GridRequest(string key, ConformanceMesh.Worker owner) => new ScopeActivationRequest
        {
            ScopeKey = key,
            Definition = new ChunkGridDefinition { CellSize = Cell, Planar = false }.ToScopeDefinition(),
            PreferredWorkerId = owner.Id,
        };

        private static void StopScope(LocalControlPlane plane, string key, string state)
        {
            if (state == "removed") plane.RemoveScope(key);
            else plane.SetScopeState(key, state);
        }

        [Test]
        public void AnEntityGhostsAcrossItsOwnScopesSeam()
        {
            var here = Chunk(_alpha, Vector3Int.zero, _mesh[0]);
            Chunk(_alpha, new Vector3Int(1, 0, 0), _mesh[1]);

            // Two metres from the seam, inside the four-metre ghost band.
            var rock = _mesh[0].SpawnServerDriven(_prefabId, here, new Vector3(62f, 32f, 32f), Quaternion.identity);
            _mesh[0].PublishTick(1);
            _mesh.Pump();

            Assert.IsNotNull(_mesh[1].Find(rock.NetId), "the neighbouring chunk's owner holds it warm, as in any chunked world");
        }

        [Test]
        public void AnEntityNeverGhostsIntoAnotherScopeStandingOnTheSameGround()
        {
            var here = Chunk(_alpha, Vector3Int.zero, _mesh[0]);
            var there = Chunk(_beta, Vector3Int.zero, _mesh[1]);

            Assert.AreEqual(here.WorldBounds, there.WorldBounds, "the two chunks occupy exactly the same box");
            Assert.AreNotEqual(here.ContainerId, there.ContainerId);
            Assert.IsFalse(here.Neighbors.Contains(there), "adjacency is scope-qualified: overlapping boxes in two scopes are not neighbours");

            var rock = _mesh[0].SpawnServerDriven(_prefabId, here, new Vector3(32f, 32f, 32f), Quaternion.identity);
            _mesh[0].PublishTick(1);
            _mesh.Pump();

            Assert.IsNull(_mesh[1].Find(rock.NetId), "nothing crosses a scope boundary, however close the two worlds are in metres");
            Assert.AreEqual(0, _mesh.DeliveredOf(MsgId.GhostSpawn).Count, "and no ghost was even offered");
        }

        [Test]
        public void ChunksOfTwoScopesAtOneCoordinateAreTwoContainersWithTwoLeaseKeys()
        {
            var here = Chunk(_alpha, new Vector3Int(2, 0, -3), _mesh[0]);
            var there = Chunk(_beta, new Vector3Int(2, 0, -3), _mesh[1]);

            Assert.AreNotEqual(here.RuntimeId, there.RuntimeId);
            Assert.AreEqual(ScopeKeys.Hash(Alpha), here.InstanceId);
            Assert.AreEqual(ScopeKeys.Hash(Beta), there.InstanceId);
            Assert.AreEqual(ScopeKeys.ContainerId(Alpha, "c/2/0/-3"), here.ContainerId, "a chunk's lease key is the scope's own derivation");
            Assert.AreSame(here, ContainerRegistry.GetRuntime(_alpha.IdOf(new Vector3Int(2, 0, -3))));
            Assert.AreSame(there, ContainerRegistry.GetRuntime(_beta.IdOf(new Vector3Int(2, 0, -3))));
        }

        [TestCase(ScopeState.Retiring)]
        [TestCase(ScopeState.Retired)]
        public void AStoppedScopeDoesNotRecreatePinnedChunksAndResumesAfterActivation(string state)
        {
            using var plane = new LocalControlPlane();
            plane.Connect();
            var worker = _mesh[0].Instance;
            AttachRegistered(worker, plane);
            plane.RegisterWorker(_mesh[0].Id, _mesh[0].Index, "127.0.0.1", 7000);
            plane.HeartbeatWorker(_mesh[0].Id, WorkerStatus.Ready, default);
            var request = new ScopeActivationRequest
            {
                ScopeKey = Alpha,
                Definition = new ChunkGridDefinition { CellSize = Cell }.ToScopeDefinition(),
                PreferredWorkerId = _mesh[0].Id,
            };
            plane.ActivateScope(request);
            var allocator = new RuntimeGridAllocator(worker, _alpha) { TickIntervalSeconds = 0f };
            allocator.AddPin(Vector3Int.zero);
            string anchor = _alpha.ContainerIdOf(Vector3Int.zero);
            Assert.That(plane.FindLease(anchor), Is.Not.Null);

            plane.SetScopeState(Alpha, state);
            plane.RemoveContainer(anchor);
            allocator.Tick(1f);
            allocator.EnsureContainer(new Vector3Int(1, 0, 0), _ => Assert.Fail("a stopped scope cannot allocate"));
            Assert.That(plane.FindLease(anchor), Is.Null, "a pin must not resurrect a retired anchor");
            Assert.That(plane.FindLease(_alpha.ContainerIdOf(new Vector3Int(1, 0, 0))), Is.Null);
            Assert.That(allocator.WantedIds, Is.Empty);

            plane.SetScopeState(Alpha, ScopeState.Retired);
            plane.ActivateScope(request);
            allocator.Tick(2f);
            Assert.That(plane.FindLease(anchor), Is.Not.Null);
            Assert.That(allocator.IsWanted(Vector3Int.zero), Is.True, "explicit activation reopens allocation");
        }

        [Test]
        public void ARetiringScopeLeavesEveryChunkToTheLifecycle()
        {
            using var plane = new LocalControlPlane();
            var clock = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            plane.Clock = () => clock;
            plane.Connect();
            var worker = _mesh[0].Instance;
            AttachRegistered(worker, plane);
            plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Alpha,
                Definition = new ChunkGridDefinition { CellSize = Cell, Planar = false }.ToScopeDefinition(),
                PreferredWorkerId = _mesh[0].Id,
            });
            var anchor = Chunk(_alpha, Vector3Int.zero, _mesh[0]);
            var otherCoord = new Vector3Int(1, 0, 0);
            var other = Chunk(_alpha, otherCoord, _mesh[0]);
            plane.EnsureRuntimeContainer(other.ContainerId, ContainerRegistry.ToAbsolute(_alpha.BoundsOf(otherCoord), _alpha.InstanceId), _mesh[0].Id, other.Instance);
            var allocator = new RuntimeGridAllocator(worker, _alpha)
            {
                TickIntervalSeconds = 0f,
                RetireAfterSeconds = 1f,
            };
            allocator.AddPin(Vector3Int.zero);
            allocator.Tick(0f);

            plane.SetScopeState(Alpha, ScopeState.Retiring);
            clock = clock.AddSeconds(2);
            allocator.Tick(2f);

            Assert.That(plane.FindScope(Alpha).Acks, Is.Empty, "the lifecycle has not completed its checkpoint");
            Assert.That(plane.FindLease(anchor.ContainerId), Is.Not.Null,
                "allocator idleness must not bypass the lifecycle's checkpoint barrier");
            Assert.That(plane.FindLease(other.ContainerId), Is.Not.Null,
                "nor may it for a chunk leased on demand: every live part is checkpointed and acknowledged by the lifecycle (docs/scope-lifecycle.md D4)");
            Assert.That(allocator.WantedIds, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovingAnObservedScopeStopsAllocationUntilExplicitActivation(bool observeThroughEnsure)
        {
            using var plane = PlaneForWorkers();
            // Use a fresh scope key so the ensure exercises pending registration, never the immediate-result path.
            string key = Alpha + (observeThroughEnsure ? "/removed-ensure" : "/removed-tick");
            var grid = new RuntimeGrid(Cell, planar: false, scopeKey: key);
            var allocator = new RuntimeGridAllocator(_mesh[0].Instance, grid) { TickIntervalSeconds = 0f };
            allocator.AddPin(Vector3Int.zero);
            var request = GridRequest(key, _mesh[0]);
            plane.ActivateScope(request);
            if (observeThroughEnsure)
            {
                Assert.That(ContainerRegistry.GetRuntime(grid.IdOf(Vector3Int.zero)), Is.Null);
                allocator.EnsureContainer(Vector3Int.zero, _ => Assert.Fail("an obsolete ensure must not complete"));
            }
            else allocator.Tick(0f);

            plane.RemoveScope(key);
            // Model a late registration from the request made before removal.
            if (observeThroughEnsure) Chunk(grid, Vector3Int.zero, _mesh[0]);
            allocator.Tick(1f);
            allocator.EnsureContainer(new Vector3Int(1, 0, 0), _ => Assert.Fail("a removed scope cannot allocate"));
            Assert.That(plane.Leases, Is.Empty);
            Assert.That(allocator.WantedIds, Is.Empty);

            plane.ActivateScope(request);
            allocator.Tick(2f);
            Assert.That(plane.FindLease(grid.ContainerIdOf(Vector3Int.zero)), Is.Not.Null);
            Assert.That(allocator.IsWanted(Vector3Int.zero), Is.True);
        }

        [Test]
        public void ALowLevelScopedAllocatorCanStillStartWithoutAScopeRow()
        {
            using var plane = PlaneForWorkers();
            var allocator = new RuntimeGridAllocator(_mesh[0].Instance, _alpha) { TickIntervalSeconds = 0f };
            allocator.AddPin(Vector3Int.zero);
            allocator.Tick(0f);
            Assert.That(plane.FindScope(Alpha), Is.Null);
            Assert.That(plane.FindLease(_alpha.ContainerIdOf(Vector3Int.zero)), Is.Not.Null);
        }

        [Test]
        public void ARemovedScopeCannotReuseAnAllocatorWithDifferentGridArithmetic()
        {
            using var plane = PlaneForWorkers();
            plane.ActivateScope(GridRequest(Alpha, _mesh[0]));
            var allocator = new RuntimeGridAllocator(_mesh[0].Instance, _alpha) { TickIntervalSeconds = 0f };
            allocator.AddPin(Vector3Int.zero);
            allocator.Tick(0f);
            string oldAnchor = _alpha.ContainerIdOf(Vector3Int.zero);
            plane.RemoveScope(Alpha);
            allocator.Tick(1f);

            var changed = new ChunkGridDefinition
            {
                CellSize = Cell * 2f,
                Planar = false,
                Anchor = new Vector3Int(1, 0, 0),
            };
            plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Alpha,
                Definition = changed.ToScopeDefinition(),
                PreferredWorkerId = _mesh[0].Id,
            });
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                $"{NebulaLog.Prefix} chunk allocator for scope '{Alpha}' stopped because its definition changed; restart the worker or use a new scope key to load the new grid definition");
            allocator.Tick(2f);
            allocator.Tick(3f);
            allocator.EnsureContainer(new Vector3Int(2, 0, 0), _ => Assert.Fail("the old grid cannot place the replacement scope's chunks"));

            Assert.That(allocator.WantedIds, Is.Empty);
            Assert.That(plane.FindLease(oldAnchor), Is.Null, "the old pin must not return in the replacement scope");
            Assert.That(plane.Leases.Count, Is.EqualTo(1), "only explicit activation may create the replacement anchor");
            var newAnchor = plane.FindLease(ChunkKeys.ContainerId(Alpha, changed.Anchor));
            Assert.That(newAnchor, Is.Not.Null);
            Assert.That(newAnchor.Bounds, Is.EqualTo(changed.AbsoluteBoundsOf(changed.Anchor)));
        }

        [TestCase(ScopeState.Retiring)]
        [TestCase(ScopeState.Restoring)]
        [TestCase(ScopeState.Retired)]
        [TestCase("removed")]
        public void ATransferCannotPrepareIntoAScopeThatDoesNotAdmit(string state)
        {
            using var plane = PlaneForWorkers();
            var source = _mesh.AddStaticContainer("source", Vector3.zero, Cell);
            _mesh.SetOwner(source, _mesh[0]);
            plane.ActivateScope(GridRequest(Beta, _mesh[1]));
            var destination = Chunk(_beta, Vector3Int.zero, _mesh[1]);
            var entity = _mesh[0].SpawnServerDriven(_prefabId, source, Vector3.zero, Quaternion.identity);
            StopScope(plane, Beta, state);

            var transfer = _mesh[0].Instance.PrepareTransfer(entity, destination);
            _mesh.Pump();
            Assert.That(transfer.Finished, Is.True);
            Assert.That(transfer.Error, Is.Not.Null);
            Assert.That(transfer.Ready, Is.False);
            Assert.That(_mesh.DeliveredOf(MsgId.InstancePrepare), Is.Empty, "refusal happens before content preparation is sent");
            Assert.That(entity.Container, Is.SameAs(source));
        }

        [TestCase(ScopeState.Retiring)]
        [TestCase(ScopeState.Restoring)]
        [TestCase("removed")]
        public void ATransferRechecksScopeAdmissionOnTheDestinationWorker(string state)
        {
            using var plane = PlaneForWorkers();
            var source = _mesh.AddStaticContainer("source", Vector3.zero, Cell);
            _mesh.SetOwner(source, _mesh[0]);
            plane.ActivateScope(GridRequest(Beta, _mesh[1]));
            var destination = Chunk(_beta, Vector3Int.zero, _mesh[1]);
            var entity = _mesh[0].SpawnServerDriven(_prefabId, source, Vector3.zero, Quaternion.identity);
            var transfer = _mesh[0].Instance.PrepareTransfer(entity, destination);

            StopScope(plane, Beta, state);
            _mesh.Pump();
            Assert.That(_mesh.DeliveredOf(MsgId.InstanceReady).Count, Is.EqualTo(1));
            Assert.That(_mesh.DeliveredOf(MsgId.InstanceReady)[0].Read(InstancePreparationMsg.Read).Success, Is.False);
            Assert.That(transfer.Ready, Is.False);
            Assert.That(transfer.Error, Is.Not.Null);
        }

        [TestCase(ScopeState.Retiring)]
        [TestCase(ScopeState.Restoring)]
        [TestCase("removed")]
        public void AReadyTransferCannotCommitAfterItsScopeStopsAdmitting(string state)
        {
            using var plane = PlaneForWorkers();
            var source = _mesh.AddStaticContainer("source", Vector3.zero, Cell);
            _mesh.SetOwner(source, _mesh[0]);
            plane.ActivateScope(GridRequest(Beta, _mesh[1]));
            var destination = Chunk(_beta, Vector3Int.zero, _mesh[1]);
            var entity = _mesh[0].SpawnServerDriven(_prefabId, source, Vector3.zero, Quaternion.identity);
            var transfer = _mesh[0].Instance.PrepareTransfer(entity, destination);
            _mesh.Pump();
            Assert.That(transfer.Ready, Is.True);
            var epoch = entity.Epoch;

            StopScope(plane, Beta, state);
            Assert.That(_mesh[0].Instance.TryCommitTransfer(transfer, Vector3.one, Quaternion.identity), Is.False);
            Assert.That(entity.Container, Is.SameAs(source));
            Assert.That(entity.Epoch, Is.EqualTo(epoch));
            Assert.That(entity.transform.position, Is.EqualTo(Vector3.zero));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PublicAndLegacyUnkeyedDestinationsStillAcceptTransfers(bool legacyInstance)
        {
            var source = _mesh.AddStaticContainer("source", Vector3.zero, Cell);
            _mesh.SetOwner(source, _mesh[0]);
            Container destination;
            if (legacyInstance)
            {
                destination = Chunk(_beta, Vector3Int.zero, _mesh[1]);
                destination.Instance.ScopeKey = "";
            }
            else
            {
                destination = _mesh.AddStaticContainer("destination", new Vector3(100, 0, 0), Cell);
                _mesh.SetOwner(destination, _mesh[1]);
            }
            var entity = _mesh[0].SpawnServerDriven(_prefabId, source, Vector3.zero, Quaternion.identity);
            var transfer = _mesh[0].Instance.PrepareTransfer(entity, destination);
            _mesh.Pump();
            Assert.That(transfer.Ready, Is.True);
            Assert.That(_mesh[0].Instance.TryCommitTransfer(transfer, Vector3.one, Quaternion.identity), Is.True);
            Assert.That(entity.Container, Is.SameAs(destination));
        }
    }
}
