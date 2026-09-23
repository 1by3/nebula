using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 3, grid scopes (<c>docs/conformance-suite.md</c>, NEB-252; design
    /// <c>docs/scope-lifecycle.md</c> D4): <b>a grid scope is judged, retired and restored over every chunk it has
    /// leased, not only its anchor</b>. A grid scope's row names only its anchor chunk; every other chunk is leased on
    /// demand around the scope's pawns. Before this, a player standing a few chunks from the anchor was invisible to
    /// the idle sweep, the default policy retired the scope from under the player, and only the anchor was checkpointed.
    /// <para>
    /// Tier B: one <b>real</b> <see cref="NebulaWorker"/> on the <see cref="ConformanceMesh"/>, a real
    /// <see cref="RuntimeGridAllocator"/>, <see cref="WorkerScopeLifecycle"/> and <see cref="NebulaPersistence"/>
    /// over a <see cref="LocalPersistenceStore"/>, and a <see cref="LocalControlPlane"/> whose clock the test drives.
    /// The test stands in for the orchestrator's sweep only, using the same <see cref="ScopeLifecycle"/> functions
    /// it calls (<c>NebulaOrchestrator.SweepScopes</c>), and for the telemetry that reports occupancy.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceGridScopeLifecycleTests
    {
        private static readonly Vector3 Cell = new Vector3(64f, 64f, 64f);
        private const string Scope = "planet/grid-7";
        private const float RetireThreshold = 300f;
        private const ulong Client = 7;
        /// <summary>Three chunks from the anchor.</summary>
        private static readonly Vector3Int Far = new Vector3Int(3, 0, 0);

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private LocalPersistenceStore _store;
        private NebulaPersistence _persistence;
        private DateTime _clock;
        private float _time;
        private ushort _cratePrefab, _pawnPrefab;

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(1);
            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            crate.AddComponent<PersistentEntity>();
            _cratePrefab = _mesh.RegisterPrefab(crate);
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);

            _clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _time = 0f;
            _plane = new LocalControlPlane { Clock = () => _clock };
            _plane.Connect();
            AttachRegistered(W.Instance, _plane);
        }

        [TearDown]
        public void TearDown()
        {
            _persistence?.Shutdown();
            _persistence = null;
            _mesh.Dispose();
            _plane.Dispose();
            _store?.Dispose();
            _store = null;
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
        }

        /// <summary>Points <paramref name="worker"/> at <paramref name="plane"/> and marks it registered, as <c>ConformanceCrewedCarrierRetireTests</c> does.</summary>
        private static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", flags).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", flags).SetValue(registration, plane.DocumentId ?? "");
        }

        private void AttachPersistence()
        {
            _store = new LocalPersistenceStore(); // memory backend
            _store.Connect();
            _mesh.Config.PersistenceRestoreGraceSeconds = 1f;
            _persistence = new NebulaPersistence(W.Instance, _mesh.Config, _store) { Now = () => _time };
            typeof(NebulaWorker).GetProperty(nameof(NebulaWorker.Persistence)).SetValue(W.Instance, _persistence);
        }

        private RuntimeGrid Activate()
        {
            _plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Scope,
                Definition = new ChunkGridDefinition { CellSize = Cell, Planar = true }.ToScopeDefinition(),
                PreferredWorkerId = W.Id,
            });
            return new RuntimeGrid(Cell, planar: true, scopeKey: Scope);
        }

        /// <summary>One chunk of the scope owned by the worker: registered, and leased on the control plane (a no-op when the row exists).</summary>
        private Container Chunk(RuntimeGrid grid, Vector3Int coord)
        {
            var instance = new InstanceContainerInfo { InstanceId = grid.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(coord) };
            var chunk = ContainerRegistry.RegisterRuntime(grid.IdOf(coord), grid.BoundsOf(coord), instance);
            ContainerRegistry.ApplyLease(chunk.ContainerId, W.Id, W.Index, 1);
            _plane.EnsureRuntimeContainer(chunk.ContainerId, ContainerRegistry.ToAbsolute(grid.BoundsOf(coord), grid.InstanceId), W.Id, instance);
            return chunk;
        }

        /// <summary>The allocator a worker that knows the scope runs: the anchor pinned, a ring of 0 around each pawn so no other chunk is leased.</summary>
        private RuntimeGridAllocator Allocator(RuntimeGrid grid)
        {
            // Retiring chunks by the allocator's own rule is not what this scenario is about: the lifecycle does it.
            var allocator = new RuntimeGridAllocator(W.Instance, grid) { TickIntervalSeconds = 0f, RetireAfterSeconds = 100000f, Ring = 0 };
            allocator.AddPin(Vector3Int.zero);
            return allocator;
        }

        private void Tick(RuntimeGridAllocator allocator) => W.Act(() => allocator.Tick(_time));

        private void Advance(float seconds)
        {
            _clock = _clock.AddSeconds(seconds);
            _time += seconds;
        }

        private NetworkIdentity SpawnPawn(Container chunk, Vector3 position)
        {
            NetworkIdentity pawn = null;
            W.Act(() =>
            {
                pawn = NetworkPrefabs.Instantiate(_pawnPrefab, position, Quaternion.identity, chunk.transform);
                W.Instance.Spawn(pawn, chunk, Client);
            });
            return pawn;
        }

        private List<string> Parts(ScopeInfo scope)
        {
            var parts = new List<string>();
            ScopeLifecycle.CollectParts(_plane, scope, parts);
            return parts;
        }

        /// <summary>The retire context the orchestrator builds, over <paramref name="parts"/>, with the occupancy telemetry reports.</summary>
        private ScopeRetireContext Context(ScopeInfo scope, IReadOnlyList<string> parts, Dictionary<string, ContainerLoad> occupancy)
        {
            ScopeLifecycle.Occupancy(parts, occupancy, out int entities, out int players);
            return new ScopeRetireContext
            {
                Scope = scope,
                IdleSeconds = ScopeLifecycle.IdleSeconds(_plane, parts),
                Entities = entities,
                Players = players,
                RetireAfterSeconds = RetireThreshold,
            };
        }

        private bool Alive(NetworkIdentity e) => e != null && W.Find(e.NetId) == e;

        [Test]
        public void APlayerThreeChunksFromTheAnchorKeepsTheScopeFromRetiring()
        {
            var grid = Activate();
            var scope = _plane.FindScope(Scope);
            string anchorId = grid.ContainerIdOf(Vector3Int.zero);
            Assume.That(scope.ContainerIds, Is.EqualTo(new[] { anchorId }), "a grid scope's row names its anchor chunk only");
            Chunk(grid, Vector3Int.zero);
            var far = Chunk(grid, Far);
            SpawnPawn(far, grid.CenterOf(Far));

            var allocator = Allocator(grid);
            Tick(allocator);
            Advance(RetireThreshold + 1f);
            Tick(allocator); // the ring around the pawn is wanted again, and says so on its lease row

            var parts = Parts(scope);
            Assert.That(parts, Is.EquivalentTo(new[] { anchorId, far.ContainerId }), "the live parts are the anchor and every chunk leased under the scope's key");
            Assert.That(ScopeLifecycle.IdleSeconds(_plane, new[] { anchorId }), Is.GreaterThan(RetireThreshold),
                "nothing wants the anchor: every worker pins it, and a pin must not keep the scope hot");
            Assert.That(ScopeLifecycle.IdleSeconds(_plane, scope), Is.LessThan(NebulaWorker.RuntimeTouchSeconds),
                "but the chunk the player stands in is wanted, and the scope's idle age is its youngest part");

            // The telemetry reports the player in the chunk it stands in, not in the anchor.
            var occupancy = new Dictionary<string, ContainerLoad> { [far.ContainerId] = new ContainerLoad { Players = 1 } };
            var context = Context(scope, parts, occupancy);
            Assert.That(context.Players, Is.EqualTo(1));
            Assert.That(ScopeLifecycle.RetireWhenIdle(context), Is.False, "a player anywhere in the scope keeps it");

            // What the sweep saw before: the anchor alone, idle past the threshold and empty.
            ScopeLifecycle.Occupancy(scope, occupancy, out _, out int anchorPlayers);
            Assert.That(anchorPlayers, Is.EqualTo(0), "the row-only overload still means the row's containers");
        }

        [Test]
        public void WhenThePlayerLeavesEveryLeasedChunkIsCheckpointedReleasedAndRestored()
        {
            AttachPersistence();
            var grid = Activate();
            var scope = _plane.FindScope(Scope);
            string anchorId = grid.ContainerIdOf(Vector3Int.zero);
            Chunk(grid, Vector3Int.zero);
            var far = Chunk(grid, Far);
            var pawn = SpawnPawn(far, grid.CenterOf(Far));
            var crate = W.SpawnServerDriven(_cratePrefab, far, grid.CenterOf(Far) + new Vector3(3f, 0f, 2f), Quaternion.identity);
            string crateKey = crate.Persistent.Key;

            var allocator = Allocator(grid);
            Tick(allocator);

            // The player leaves the scope, and nothing wants any part of it for longer than the threshold.
            W.Act(() => W.Instance.Despawn(pawn));
            Advance(RetireThreshold + 1f);
            Tick(allocator);
            var parts = Parts(scope);
            Assert.That(ScopeLifecycle.RetireWhenIdle(Context(scope, parts, new Dictionary<string, ContainerLoad>())), Is.True);

            // Retire: every live part goes through the sequence on the worker that owns it, the far chunk included.
            _plane.SetScopeState(Scope, ScopeState.Retiring);
            Tick(allocator);
            Assert.That(_plane.FindLease(far.ContainerId), Is.Not.Null, "the allocator leaves a retiring scope's chunks to the lifecycle");
            var agent = W.Instance.ScopeLifecycleAgent;
            for (int i = 0; i < 4; i++)
            {
                W.Act(() => agent.Pass(_time));
                _store.Tick();
                Advance(1f);
            }
            Assert.That(scope.FindAck(anchorId, ScopePhase.Checkpointed), Is.Not.Null);
            Assert.That(scope.FindAck(far.ContainerId, ScopePhase.Checkpointed), Is.Not.Null, "the chunk leased on demand acknowledged its own checkpoint");
            Assert.That(scope.FindAck(far.ContainerId, ScopePhase.Checkpointed).Count, Is.EqualTo(1), "and saved the crate in it");
            Assert.That(ScopeLifecycle.NextState(scope, Parts(scope), 1, out bool timedOut), Is.EqualTo(ScopeState.Retired));
            Assert.That(timedOut, Is.False);
            Assert.That(Alive(crate), Is.False, "the far chunk was emptied");

            PersistedEntityRecord record = null;
            _store.Load(crateKey, r => record = r);
            _store.Tick();
            Assert.That(record, Is.Not.Null);
            Assert.That(record.ContainerId, Is.EqualTo(far.ContainerId));
            Assert.That(record.ScopeKey, Is.EqualTo(Scope));

            // The orchestrator releases every live part, not only the anchor.
            foreach (var id in Parts(scope)) _plane.RemoveContainer(id);
            _plane.SetScopeState(Scope, ScopeState.Retired);
            Assert.That(_plane.Leases, Is.Empty, "a retired grid scope holds no chunk at all");
            ContainerRegistry.SyncRuntime(_plane.Leases);
            ContainerRegistry.NotifyLeasesChanged();

            // Re-activation recreates the anchor only. The far chunk comes back when a pawn rings it again, through
            // the ordinary lease-landing restore, and brings the crate back with it.
            Activate();
            Assert.That(scope.State, Is.EqualTo(ScopeState.Restoring));
            Assert.That(_plane.FindLease(far.ContainerId), Is.Null);
            Chunk(grid, Vector3Int.zero);
            var farAgain = Chunk(grid, Far);
            ContainerRegistry.NotifyLeasesChanged();
            for (int i = 0; i < 4; i++)
            {
                Advance(1f);
                W.Act(() => _persistence.Update());
                _store.Tick();
                W.Act(() => _persistence.Update());
                W.Act(() => agent.Pass(_time));
            }
            var restored = _persistence.Find(crateKey);
            Assert.That(restored, Is.Not.Null, "the crate saved in a non-anchor chunk came back");
            Assert.That(restored.Container, Is.SameAs(farAgain));
            Assert.That(scope.FindAck(anchorId, ScopePhase.Restored), Is.Not.Null);
            Assert.That(ScopeLifecycle.NextState(scope, Parts(scope), 1, out _), Is.EqualTo(ScopeState.Active),
                "a restore waits for the row's parts only; the other chunks are not recreated by the activation");
        }

        [Test]
        public void AnAckForAChunkLeasedUnderTheScopesKeyIsAccepted()
        {
            var grid = Activate();
            var scope = _plane.FindScope(Scope);
            var far = Chunk(grid, Far);
            _plane.EnsureRuntimeContainer("rt_elsewhere", new Bounds(Vector3.zero, Cell), W.Id, null);
            _plane.SetScopeState(Scope, ScopeState.Retiring);

            _plane.AckScopePart(Scope, far.ContainerId, ScopePhase.Checkpointed, 2, W.Id);
            _plane.AckScopePart(Scope, "rt_elsewhere", ScopePhase.Checkpointed, 2, W.Id);

            Assert.That(scope.FindAck(far.ContainerId, ScopePhase.Checkpointed), Is.Not.Null, "a live part of the scope");
            Assert.That(scope.FindAck("rt_elsewhere", ScopePhase.Checkpointed), Is.Null, "a container of no scope");
            Assert.That(ScopeLifecycle.NextState(scope, Parts(scope), 1, out _), Is.Null, "the anchor has not acknowledged yet");
        }
    }
}
