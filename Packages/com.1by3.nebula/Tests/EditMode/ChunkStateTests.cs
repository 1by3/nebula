using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Chunk state (<c>docs/chunk-state.md</c>, NEB-276), the lease holder's half: sparse per-object entries saved
    /// with a chunk, kept across a chunk's release and a scope's retire, expired lazily, and costing nothing for a
    /// chunk nobody touched.
    /// <para>
    /// One <b>real</b> <see cref="NebulaWorker"/> on the <see cref="ConformanceMesh"/>, <see cref="NebulaPersistence"/>
    /// over a <see cref="LocalPersistenceStore"/>, and a <see cref="LocalControlPlane"/>. The test drives every clock:
    /// the control plane's, the persistence service's, the chunk state service's and <see cref="ChunkState.NowUnixMs"/>.
    /// </para>
    /// </summary>
    public sealed class ChunkStatePersistenceTests
    {
        private static readonly Vector3 Cell = new Vector3(64f, 64f, 64f);
        private const string Scope = "planet/chunk-state";
        private static readonly Vector3Int Coord = new Vector3Int(2, 0, 1);

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private LocalPersistenceStore _store;
        private NebulaPersistence _persistence;
        private ChunkStateService _service;
        private DateTime _clock;
        private float _time;
        private long _nowMs;
        private string _storePath;
        private readonly List<ObjectStateChange> _changes = new List<ObjectStateChange>();

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(1);
            _clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _time = 0f;
            _nowMs = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            ChunkState.Clock = () => _nowMs;
            ChunkState.Changed += Record;
            _plane = new LocalControlPlane { Clock = () => _clock };
            _plane.Connect();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", flags).SetValue(W.Instance, _plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", flags).GetValue(W.Instance);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", flags).SetValue(registration, _plane.DocumentId ?? "");
            _mesh.Config.PersistenceRestoreGraceSeconds = 1f;
            AttachStore(new LocalPersistenceStore());
            _service = W.Instance.ChunkStates;
            _service.Now = () => _time;
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
            ChunkState.ResetForNewSession();
            _changes.Clear();
            if (_storePath != null && File.Exists(_storePath)) File.Delete(_storePath);
        }

        private void Record(in ObjectStateChange change) => _changes.Add(change);

        private void AttachStore(LocalPersistenceStore store)
        {
            _persistence?.Shutdown();
            _store?.Dispose();
            _store = store;
            _store.Connect();
            _persistence = new NebulaPersistence(W.Instance, _mesh.Config, _store) { Now = () => _time };
            typeof(NebulaWorker).GetProperty(nameof(NebulaWorker.Persistence)).SetValue(W.Instance, _persistence);
        }

        private Container Chunk(RuntimeGrid grid, Vector3Int coord, InstanceContainerInfo instance = null)
        {
            var chunk = ContainerRegistry.RegisterRuntime(grid.IdOf(coord), grid.BoundsOf(coord), instance);
            ContainerRegistry.ApplyLease(chunk.ContainerId, W.Id, W.Index, 1);
            _plane.EnsureRuntimeContainer(chunk.ContainerId, ContainerRegistry.ToAbsolute(grid.BoundsOf(coord), grid.InstanceId), W.Id, instance);
            ContainerRegistry.NotifyLeasesChanged();
            return chunk;
        }

        private void Advance(float seconds)
        {
            _clock = _clock.AddSeconds(seconds);
            _time += seconds;
        }

        /// <summary>Frames of the worker's own passes: persistence (restore, checkpoint), the store, chunk state.</summary>
        private void Settle(int frames = 4)
        {
            for (int i = 0; i < frames; i++)
            {
                Advance(1f);
                W.Act(() => _persistence.Update());
                _store.Tick();
                W.Act(() => _persistence.Update());
                W.Act(() => _service.Update());
            }
        }

        private List<PersistedEntityRecord> Records()
        {
            List<PersistedEntityRecord> records = null;
            _store.LoadWhere(null, r => records = new List<PersistedEntityRecord>(r));
            _store.Tick();
            return records;
        }

        private ChunkStateResult Set(Container chunk, ulong objectId, ObjectState state)
        {
            ChunkStateResult result = default;
            bool done = false;
            W.Act(() => _service.Set(chunk, objectId, state, r => { result = r; done = true; }));
            Assert.That(done, Is.True, "the lease holder of a ready chunk applies a change before the call returns");
            return result;
        }

        private void Release(Container chunk)
        {
            bool released = false;
            W.Act(() => released = W.Instance.ReleaseRuntimeContainer(chunk.RuntimeId));
            Assert.That(released, Is.True);
            ContainerRegistry.SyncRuntime(_plane.Leases);
            ContainerRegistry.NotifyLeasesChanged();
        }

        [Test]
        public void AnUntouchedChunkWritesNoRecordAndSpawnsNothing()
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Coord);
            Settle();
            Assert.That(_service.CanApplyLocally(chunk.ContainerId), Is.True);

            Assert.That(ChunkState.TryGet(chunk, 7, out _), Is.False, "an untouched object has no entry");
            Assert.That(ChunkState.Holds(chunk.ContainerId), Is.False);

            ChunkStateResult cleared = default;
            W.Act(() => _service.Clear(chunk, 7, r => cleared = r));
            Assert.That(cleared.Outcome, Is.EqualTo(ChunkStateOutcome.Applied), "clearing an untouched object is a no-op that succeeds");

            ChunkStateResult noop = default;
            W.Act(() => _service.CompareAndSet(chunk, 7, null, null, r => noop = r));
            Assert.That(noop.Succeeded, Is.True);

            Settle(2);
            Assert.That(W.Instance.EntityCount, Is.EqualTo(0), "no entity for a chunk with no entries");
            Assert.That(Records(), Is.Empty, "and no record");

            Release(chunk);
            Assert.That(Records(), Is.Empty, "releasing an untouched chunk writes nothing either");
        }

        [Test]
        public void AnEntryIsSavedAndComesBackWhenTheChunkIsLeasedAgain()
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Coord);
            Settle();

            Assert.That(Set(chunk, 42, new ObjectState(3)).Succeeded, Is.True);
            Assert.That(Set(chunk, 43, new ObjectState(1, 0, new byte[] { 9, 8, 7 })).Succeeded, Is.True);
            Assert.That(ChunkState.TryGet(chunk, 42, out var mined), Is.True);
            Assert.That(mined.Value, Is.EqualTo(3u));
            Assert.That(W.Instance.EntityCount, Is.EqualTo(1), "one entity carries every entry of the chunk");
            var entity = _service.FindAuthoritative(chunk.ContainerId);
            Assert.That(entity.Identity.Container, Is.SameAs(chunk));
            Assert.That(entity.Identity.ExcludeFromOccupancy, Is.True, "it is bookkeeping, not occupancy");
            Assert.That(entity.Identity.RelevanceRadius, Is.GreaterThan(InterestSettings.Default.Radius), "clients near any edge of the chunk hear it");

            Settle(1);
            var records = Records();
            Assert.That(records.Count, Is.EqualTo(1));
            var record = records[0];
            Assert.That(record.Key, Is.EqualTo(ChunkState.KeyOf(chunk.ContainerId)));
            Assert.That(record.ContainerId, Is.EqualTo(chunk.ContainerId));
            Assert.That(record.PrefabId, Is.EqualTo(ChunkStateEntity.PrefabId));
            Assert.That(record.PrefabName, Is.EqualTo(ChunkStateEntity.PrefabName));
            Assert.That(WorkerScopeLifecycle.IsBusy(chunk), Is.False, "a saved chunk state does not keep its part busy");

            _changes.Clear();
            Release(chunk);
            Assert.That(W.Instance.EntityCount, Is.EqualTo(0));
            Assert.That(ChunkState.Holds(chunk.ContainerId), Is.False);
            Assert.That(_changes.FindAll(c => c.Kind == ObjectStateChangeKind.Forgotten).Count, Is.EqualTo(2), "the entries left this process; they were not cleared");
            Assert.That(Records().Count, Is.EqualTo(1), "the record stays while the chunk is unloaded");

            _changes.Clear();
            var again = Chunk(grid, Coord);
            Settle();
            Assert.That(ChunkState.TryGet(again, 42, out var back), Is.True, "the entry came back with the chunk");
            Assert.That(back.Value, Is.EqualTo(3u));
            Assert.That(ChunkState.TryGet(again, 43, out var withPayload), Is.True);
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, withPayload.Payload);
            Assert.That(_changes.FindAll(c => c.Kind == ObjectStateChangeKind.Set && c.IsAuthority).Count, Is.EqualTo(2), "the restore raised each entry once");
            Assert.That(_service.FindAuthoritative(again.ContainerId).Identity.Container, Is.SameAs(again));
        }

        [Test]
        public void AnEntrySurvivesAFileStoreBeingClosedAndOpenedAgain()
        {
            // The Editor's play loop: stop saves the local store to its file, play reads it back.
            _storePath = Path.Combine(Path.GetTempPath(), $"nebula-chunkstate-{Guid.NewGuid():N}.bin");
            AttachStore(new LocalPersistenceStore(_storePath));
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Coord);
            Settle();
            Assert.That(Set(chunk, 5, new ObjectState(11, _nowMs + 3_600_000)).Succeeded, Is.True);
            Settle(1);
            Release(chunk);
            _persistence.Shutdown();
            _persistence = null;
            _store.Flush();
            _store.Dispose();
            _store = null;

            AttachStore(new LocalPersistenceStore(_storePath));
            var again = Chunk(grid, Coord);
            Settle();
            Assert.That(ChunkState.TryGet(again, 5, out var back), Is.True);
            Assert.That(back, Is.EqualTo(new ObjectState(11, _nowMs + 3_600_000)), "value and absolute expiry time, as saved");
        }

        [Test]
        public void AnEntryRevertsWhenItsTimePassesAndTheEmptyRecordIsDeleted()
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Coord);
            Settle();
            Assert.That(Set(chunk, 1, new ObjectState(0, _nowMs + 10_000)).Succeeded, Is.True);
            Settle(1);
            Assert.That(Records().Count, Is.EqualTo(1));

            _nowMs += 10_000;
            Assert.That(ChunkState.TryGet(chunk, 1, out _), Is.False, "an entry whose time has come reads as untouched at once, before anything removes it");
            Assert.That(ChunkState.CountIn(chunk.ContainerId), Is.EqualTo(0));

            _changes.Clear();
            ChunkState.PollExpiries();
            Assert.That(_changes.Count, Is.EqualTo(1));
            Assert.That(_changes[0].Kind, Is.EqualTo(ObjectStateChangeKind.Expired));
            Assert.That(_changes[0].ObjectId, Is.EqualTo(1UL));
            Assert.That(_changes[0].HasState, Is.False);

            // The empty entity lingers a moment so the removal reaches clients as a change, then goes with its record.
            W.Act(() => _service.Update());
            Assert.That(W.Instance.EntityCount, Is.EqualTo(1));
            Advance(ChunkStateService.EmptyLingerSeconds + 0.1f);
            W.Act(() => _service.Update());
            Advance(ChunkStateService.EmptyLingerSeconds + 0.1f);
            W.Act(() => _service.Update());
            Assert.That(W.Instance.EntityCount, Is.EqualTo(0), "an emptied chunk spawns nothing");
            Assert.That(Records(), Is.Empty, "and keeps no record");
        }

        [Test]
        public void AnExpiryThatPassedWhileTheChunkWasUnloadedIsAppliedWhenItLoads()
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Coord);
            Settle();
            Assert.That(Set(chunk, 1, new ObjectState(0, _nowMs + 60_000)).Succeeded, Is.True);
            Assert.That(Set(chunk, 2, new ObjectState(4)).Succeeded, Is.True);
            Settle(1);
            Release(chunk);

            // Nothing ticks while the chunk is unloaded. An hour later it is leased again.
            _nowMs += 3_600_000;
            _changes.Clear();
            var again = Chunk(grid, Coord);
            Settle();
            Assert.That(ChunkState.TryGet(again, 1, out _), Is.False, "the timed entry reverted while nobody was here");
            Assert.That(ChunkState.TryGet(again, 2, out var kept), Is.True);
            Assert.That(kept.Value, Is.EqualTo(4u));
            Assert.That(_changes.TrueForAll(c => c.ObjectId == 2UL), Is.True, "an entry that expired while unloaded is not raised as if it had arrived");
            Settle(1);
            Assert.That(Records().Count, Is.EqualTo(1));
        }

        [Test]
        public void AChunkWhoseEveryEntryExpiredWhileUnloadedLeavesNothingBehind()
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Coord);
            Settle();
            Assert.That(Set(chunk, 1, new ObjectState(0, _nowMs + 60_000)).Succeeded, Is.True);
            Settle(1);
            Release(chunk);
            Assert.That(Records().Count, Is.EqualTo(1));

            _nowMs += 120_000;
            var again = Chunk(grid, Coord);
            Settle(6);
            Assert.That(ChunkState.TryGet(again, 1, out _), Is.False);
            Assert.That(W.Instance.EntityCount, Is.EqualTo(0), "the restored entity had nothing left and was removed");
            Assert.That(Records(), Is.Empty, "and so was its record");
        }

        [Test]
        public void AChangeToAChunkThatIsStillRestoringWaitsForTheRestore()
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            // Saved state from an earlier run, and a fresh lease.
            var chunk = Chunk(grid, Coord);
            Settle();
            Assert.That(Set(chunk, 9, new ObjectState(5)).Succeeded, Is.True);
            Settle(1);
            Release(chunk);

            var again = Chunk(grid, Coord);
            Assert.That(_service.CanApplyLocally(again.ContainerId), Is.False, "the saved state has not been read yet");
            ChunkStateResult result = default;
            bool done = false;
            W.Act(() => _service.CompareAndSet(again, 9, new ObjectState(5), new ObjectState(4), r => { result = r; done = true; }));
            Assert.That(done, Is.False, "held until the restore");
            Assert.That(_service.PendingCount, Is.EqualTo(1));

            Settle();
            Assert.That(done, Is.True);
            Assert.That(result.Outcome, Is.EqualTo(ChunkStateOutcome.Applied), "applied against the restored entry, not an empty chunk");
            Assert.That(result.State.Value, Is.EqualTo(4u));
            Assert.That(W.Instance.EntityCount, Is.EqualTo(1), "one entity: the restored one, not a second copy");
        }

        [Test]
        public void AChunkHoldsEntriesUpToItsLimitAndTheyAllComeBack()
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Coord);
            Settle();
            ChunkState.Changed -= Record; // thousands of changes are not the point here

            int fits = (ChunkState.MaxEncodedBytes - 3) / 13;
            for (int i = 0; i < fits; i++)
            {
                Assert.That(_service.TrySetLocal(chunk.ContainerId, (ulong)i, new ObjectState(1), out var r), Is.True);
                Assert.That(r.Succeeded, Is.True, $"entry {i}");
            }
            Assert.That(_service.TrySetLocal(chunk.ContainerId, 1_000_000, new ObjectState(1), out var over), Is.True);
            Assert.That(over.Outcome, Is.EqualTo(ChunkStateOutcome.Rejected), "one more would not fit");
            Assert.That(_service.TrySetLocal(chunk.ContainerId, 3, new ObjectState(2), out var overwrite), Is.True);
            Assert.That(overwrite.Succeeded, Is.True, "changing an existing entry at the same size still fits");
            Assert.That(_service.FindAuthoritative(chunk.ContainerId).EncodedBytes, Is.LessThanOrEqualTo(ChunkState.MaxEncodedBytes));

            Settle(1);
            Release(chunk);
            var again = Chunk(grid, Coord);
            Settle();
            Assert.That(ChunkState.CountIn(again.ContainerId), Is.EqualTo(fits), "a full chunk saves and restores whole");
            Assert.That(ChunkState.TryGet(again, 3, out var three), Is.True);
            Assert.That(three.Value, Is.EqualTo(2u));
        }

        [Test]
        public void APayloadLongerThanTheLimitIsRejected()
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var chunk = Chunk(grid, Coord);
            Settle();
            var result = Set(chunk, 1, new ObjectState(1, 0, new byte[ChunkState.MaxPayloadBytes + 1]));
            Assert.That(result.Outcome, Is.EqualTo(ChunkStateOutcome.Rejected));
            Assert.That(W.Instance.EntityCount, Is.EqualTo(0));
            Assert.That(Set(chunk, 1, new ObjectState(1, 0, new byte[ChunkState.MaxPayloadBytes])).Succeeded, Is.True);
        }

        [Test]
        public void EntriesSurviveTheirScopeBeingRetiredAndRestoredIncludingAnExpiryThatPassedMeanwhile()
        {
            const float retireThreshold = 300f;
            _plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Scope,
                Definition = new ChunkGridDefinition { CellSize = Cell, Planar = true }.ToScopeDefinition(),
                PreferredWorkerId = W.Id,
            });
            var grid = new RuntimeGrid(Cell, planar: true, scopeKey: Scope);
            var scope = _plane.FindScope(Scope);
            InstanceContainerInfo InstanceOf(Vector3Int c) => new InstanceContainerInfo { InstanceId = grid.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(c) };
            Chunk(grid, Vector3Int.zero, InstanceOf(Vector3Int.zero));
            var far = Chunk(grid, Coord, InstanceOf(Coord));
            Settle();

            Assert.That(Set(far, 100, new ObjectState(2)).Succeeded, Is.True);
            Assert.That(Set(far, 101, new ObjectState(0, _nowMs + 600_000)).Succeeded, Is.True);
            Settle(1);
            Assert.That(WorkerScopeLifecycle.IsBusy(far), Is.False, "saved chunk state does not keep the scope hot");

            // Retire, the way the orchestrator's sweep and the worker's lifecycle agent do it.
            Advance(retireThreshold + 1f);
            _plane.SetScopeState(Scope, ScopeState.Retiring);
            var agent = W.Instance.ScopeLifecycleAgent;
            for (int i = 0; i < 4; i++)
            {
                W.Act(() => agent.Pass(_time));
                _store.Tick();
                Advance(1f);
            }
            Assert.That(scope.FindAck(far.ContainerId, ScopePhase.Checkpointed), Is.Not.Null);
            Assert.That(scope.FindAck(far.ContainerId, ScopePhase.Checkpointed).Count, Is.EqualTo(1), "the chunk's state was checkpointed with its part");
            Assert.That(W.Instance.EntityCount, Is.EqualTo(0));
            var parts = new List<string>();
            ScopeLifecycle.CollectParts(_plane, scope, parts);
            foreach (var id in parts) _plane.RemoveContainer(id);
            _plane.SetScopeState(Scope, ScopeState.Retired);
            ContainerRegistry.SyncRuntime(_plane.Leases);
            ContainerRegistry.NotifyLeasesChanged();

            // Retired for a day: the timed entry's hour passes with nobody there.
            _nowMs += 86_400_000;

            _plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Scope,
                Definition = new ChunkGridDefinition { CellSize = Cell, Planar = true }.ToScopeDefinition(),
                PreferredWorkerId = W.Id,
            });
            Chunk(grid, Vector3Int.zero, InstanceOf(Vector3Int.zero));
            var farAgain = Chunk(grid, Coord, InstanceOf(Coord));
            Settle();
            Assert.That(ChunkState.TryGet(farAgain, 100, out var kept), Is.True, "the entry came back with the scope");
            Assert.That(kept.Value, Is.EqualTo(2u));
            Assert.That(ChunkState.TryGet(farAgain, 101, out _), Is.False, "the timed entry expired while the scope was retired");
        }
    }

    /// <summary>
    /// Chunk state (<c>docs/chunk-state.md</c>, NEB-276) across workers: a change made on a worker that does not hold
    /// the chunk's lease is applied by the one that does, as a compare-and-set, and the entries reach the workers
    /// that hold a ghost of the chunk's entity. Two real workers on the <see cref="ConformanceMesh"/>; no store.
    /// </summary>
    public sealed class ChunkStateRoutingTests
    {
        private ConformanceMesh _mesh;
        private Container _yard, _dock;
        private long _nowMs;
        private readonly List<ObjectStateChange> _changes = new List<ObjectStateChange>();

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            // Narrow boxes, so the centre of the yard is inside the ghost band of the dock next to it.
            _yard = _mesh.AddStaticContainer("yard", Vector3.zero, new Vector3(6f, 10f, 6f));          // x in [-3, 3]
            _dock = _mesh.AddStaticContainer("dock", new Vector3(6f, 0f, 0f), new Vector3(6f, 10f, 6f)); // x in [3, 9]
            _mesh.SetOwner(_yard, W1);
            _mesh.SetOwner(_dock, W2);
            _nowMs = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            ChunkState.Clock = () => _nowMs;
            ChunkState.Changed += Record;
            // Both services exist before any message flows, as they do on a worker that has initialised.
            _ = W1.Instance.ChunkStates;
            _ = W2.Instance.ChunkStates;
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            ChunkState.ResetForNewSession();
            _changes.Clear();
        }

        private void Record(in ObjectStateChange change) => _changes.Add(change);

        [Test]
        public void CompareAndSetAppliesOnlyWhenTheEntryIsStillAsExpected()
        {
            var s = W1.Instance.ChunkStates;
            ChunkStateResult r = default;
            W1.Act(() => Assert.That(s.TryCompareAndSetLocal("yard", 9, null, new ObjectState(5), out r), Is.True));
            Assert.That(r.Outcome, Is.EqualTo(ChunkStateOutcome.Applied));

            W1.Act(() => s.TryCompareAndSetLocal("yard", 9, null, new ObjectState(4), out r));
            Assert.That(r.Outcome, Is.EqualTo(ChunkStateOutcome.Conflict), "it is no longer untouched");
            Assert.That(r.HasState, Is.True);
            Assert.That(r.State.Value, Is.EqualTo(5u), "and the conflict says what it is instead");

            W1.Act(() => s.TryCompareAndSetLocal("yard", 9, new ObjectState(5), new ObjectState(4), out r));
            Assert.That(r.Outcome, Is.EqualTo(ChunkStateOutcome.Applied));
            Assert.That(r.State.Value, Is.EqualTo(4u));

            W1.Act(() => s.TryCompareAndSetLocal("yard", 9, new ObjectState(5), null, out r));
            Assert.That(r.Outcome, Is.EqualTo(ChunkStateOutcome.Conflict));

            W1.Act(() => s.TryCompareAndSetLocal("yard", 9, new ObjectState(4), null, out r));
            Assert.That(r.Outcome, Is.EqualTo(ChunkStateOutcome.Applied));
            Assert.That(r.HasState, Is.False, "cleared: untouched again");
            Assert.That(ChunkState.TryGet(_yard, 9, out _), Is.False);

            Assert.That(W2.Instance.ChunkStates.TryCompareAndSetLocal("yard", 9, null, new ObjectState(1), out _), Is.False,
                "the local fast path never applies on a worker without the lease");
        }

        [Test]
        public void AWorkerWithoutTheLeaseRoutesItsChangesToTheLeaseHolderAndOnlyOneOfTwoRacingConsumesWins()
        {
            var results = new List<ChunkStateResult>();
            W2.Act(() =>
            {
                // Two players on the second worker take the last unit of the same untouched node at once.
                W2.Instance.ChunkStates.CompareAndSet(_yard, 5, null, new ObjectState(0, _nowMs + 60_000), results.Add);
                W2.Instance.ChunkStates.CompareAndSet(_yard, 5, null, new ObjectState(0, _nowMs + 60_000), results.Add);
            });
            Assert.That(results, Is.Empty, "the changes are on their way to the lease holder");
            Assert.That(W2.Instance.ChunkStates.PendingCount, Is.EqualTo(2));

            _mesh.Pump();
            Assert.That(results.Count, Is.EqualTo(2));
            Assert.That(results[0].Outcome, Is.EqualTo(ChunkStateOutcome.Applied));
            Assert.That(results[1].Outcome, Is.EqualTo(ChunkStateOutcome.Conflict), "the second found the node already taken");
            Assert.That(results[1].State.ExpiresAtUnixMs, Is.EqualTo(_nowMs + 60_000));
            Assert.That(W2.Instance.ChunkStates.PendingCount, Is.EqualTo(0));

            var entity = W1.Instance.ChunkStates.FindAuthoritative("yard");
            Assert.That(entity, Is.Not.Null, "the lease holder created the chunk's entity");
            Assert.That(W1.Instance.AuthoritativeCount, Is.EqualTo(1));
            Assert.That(W2.Instance.AuthoritativeCount, Is.EqualTo(0));
            Assert.That(_mesh.DeliveredOf(MsgId.WorkerMessage).Count, Is.EqualTo(4), "a request and a reply each");
        }

        [Test]
        public void AChangeToAChunkNobodyLeasesIsUnavailable()
        {
            ChunkStateResult r = default;
            bool done = false;
            W2.Act(() => W2.Instance.ChunkStates.Set("rt_123456", 1, new ObjectState(1), x => { r = x; done = true; }));
            Assert.That(done, Is.True);
            Assert.That(r.Outcome, Is.EqualTo(ChunkStateOutcome.Unavailable));
        }

        [Test]
        public void AGhostOfTheChunksEntityReceivesTheEntriesAndTheirChanges()
        {
            ChunkStateResult r = default;
            W1.Act(() => W1.Instance.ChunkStates.TrySetLocal("yard", 1, new ObjectState(7), out r));
            Assert.That(r.Succeeded, Is.True);
            var entity = W1.Instance.ChunkStates.FindAuthoritative("yard");

            _changes.Clear();
            W1.PublishTick(1);
            _mesh.Pump();
            var ghost = W2.Find(entity.NetId);
            Assert.That(ghost, Is.Not.Null, "the neighbour holds a ghost of the chunk's entity");
            var copy = ghost.GetComponent<ChunkStateEntity>();
            Assert.That(copy.HasAuthority, Is.False);
            Assert.That(copy.TryGetLive(1, _nowMs, out var seen), Is.True);
            Assert.That(seen.Value, Is.EqualTo(7u));
            Assert.That(_changes.Exists(c => !c.IsAuthority && c.Kind == ObjectStateChangeKind.Set && c.ObjectId == 1), Is.True, "the ghost raised the entry it arrived with");

            _changes.Clear();
            W1.Act(() => W1.Instance.ChunkStates.TrySetLocal("yard", 2, new ObjectState(3), out r));
            W1.Act(() => W1.Instance.ChunkStates.TrySetLocal("yard", 1, null, out r));
            W1.PublishTick(2);
            _mesh.Pump();
            Assert.That(copy.TryGetLive(1, _nowMs, out _), Is.False);
            Assert.That(copy.TryGetLive(2, _nowMs, out var two), Is.True);
            Assert.That(two.Value, Is.EqualTo(3u));
            var onGhost = _changes.FindAll(c => !c.IsAuthority);
            Assert.That(onGhost.Exists(c => c.Kind == ObjectStateChangeKind.Set && c.ObjectId == 2), Is.True);
            Assert.That(onGhost.Exists(c => c.Kind == ObjectStateChangeKind.Cleared && c.ObjectId == 1), Is.True);
        }
    }

    /// <summary>
    /// Chunk state (<c>docs/chunk-state.md</c>, NEB-276) on a client: the entries arrive with the entity's spawn and
    /// every change after it, reads honour expiry against the server's clock, and a change event is raised for each
    /// arrival, change, expiry and departure. The messages are fed to a real <see cref="NebulaClient"/>'s handlers.
    /// </summary>
    public sealed class ChunkStateClientTests
    {
        private const ulong NetId = 0x0001_0000_0000_0077UL;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<ObjectStateChange> _changes = new List<ObjectStateChange>();
        private NebulaClient _client;
        private Container _chunk;
        private ChunkStateEntity _source;
        private long _nowMs;

        [SetUp]
        public void SetUp()
        {
            NebulaRuntime.Reset();
            NebulaRuntime.IsClient = true;
            ContainerRegistry.Rebuild();
            _chunk = ContainerRegistry.RegisterRuntime(RuntimeGrid.PackId(new Vector3Int(1, 0, 1)), new Bounds(new Vector3(96f, 32f, 96f), new Vector3(64f, 64f, 64f)));
            var go = new GameObject("client");
            _objects.Add(go);
            _client = go.AddComponent<NebulaClient>();
            _nowMs = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            ChunkState.Clock = () => _nowMs;
            ChunkState.Changed += Record;
            // What the authority would hold: an unspawned copy of the built-in prefab, used only to encode entries.
            var source = NetworkPrefabs.Instantiate(ChunkStateEntity.PrefabId, Vector3.zero, Quaternion.identity, null);
            _objects.Add(source.gameObject);
            _source = source.GetComponent<ChunkStateEntity>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            ChunkState.ResetForNewSession();
            NebulaRuntime.Reset();
            NetworkTime.LatestServerTick = 0;
            _changes.Clear();
        }

        private void Record(in ObjectStateChange change) => _changes.Add(change);

        private byte[] Vars()
        {
            var w = new NetworkWriter();
            _source.Identity.WriteVars(w);
            return w.ToArray();
        }

        /// <summary>The entries in full, as a spawn carries them; what was changed so far counts as sent.</summary>
        private byte[] Maps()
        {
            var w = new NetworkWriter();
            _source.Identity.WriteMapsFull(w);
            _source.Identity.ClearDirty();
            return w.ToArray();
        }

        /// <summary>The entries changed since the last send, as the authority's next EntityMaps carries them.</summary>
        private EntityMapsMsg Changes()
        {
            var w = new NetworkWriter();
            _source.Identity.WriteMapsDelta(w);
            _source.Identity.ClearDirty();
            return new EntityMapsMsg { NetId = NetId, Epoch = 1, Maps = w.ToArray() };
        }

        private void Invoke(string method, object msg) =>
            typeof(NebulaClient).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_client, new[] { msg });

        private void Spawn()
        {
            Invoke("OnEntitySpawn", new EntitySpawnMsg
            {
                NetId = NetId,
                PrefabId = ChunkStateEntity.PrefabId,
                Container = _chunk.Ref,
                Epoch = 1,
                OwnerWorkerIndex = 1,
                LocalRotation = Quaternion.identity,
                LocalScale = Vector3.one,
                Vars = Vars(),
                Maps = Maps(),
                State = Array.Empty<byte>(),
                Flags = EntityFlags.ServerDriven,
            });
            var replica = _client.Entities is IEnumerable all ? Find(all) : null;
            if (replica != null) _objects.Add(replica.gameObject);
        }

        private static NetworkIdentity Find(IEnumerable entities)
        {
            foreach (var e in entities) if (e is NetworkIdentity id && id.NetId == NetId) return id;
            return null;
        }

        [Test]
        public void AClientReadsTheEntriesItReceivesAndHearsEveryChange()
        {
            _source.Seed(1, new ObjectState(3));
            _source.Seed(2, new ObjectState(0, _nowMs + 30_000));
            Assert.That(ChunkState.TryGet(_chunk, 1, out _), Is.False, "nothing is known before the chunk's state arrives");

            Spawn();
            Assert.That(ChunkState.Holds(_chunk.ContainerId), Is.True);
            Assert.That(ChunkState.TryGet(_chunk, 1, out var one), Is.True);
            Assert.That(one.Value, Is.EqualTo(3u));
            Assert.That(_changes.FindAll(c => c.Kind == ObjectStateChangeKind.Set).Count, Is.EqualTo(2), "each entry is raised as it arrives");
            Assert.That(_changes.TrueForAll(c => !c.IsAuthority && c.Container == _chunk), Is.True);

            // A change: one entry updated, one cleared, one added.
            _changes.Clear();
            _source.Seed(1, new ObjectState(2));
            _source.Seed(3, new ObjectState(8));
            _source.Remove(2);
            var changes = Changes();
            Assert.That(changes.Maps.Length, Is.LessThan(64), "only the three entries that changed are sent");
            Invoke("OnEntityMaps", changes);
            Assert.That(ChunkState.TryGet(_chunk, 1, out one), Is.True);
            Assert.That(one.Value, Is.EqualTo(2u));
            Assert.That(ChunkState.TryGet(_chunk, 2, out _), Is.False);
            Assert.That(ChunkState.TryGet(_chunk, 3, out _), Is.True);
            Assert.That(_changes.Count, Is.EqualTo(3), "one change per entry that differs");
            Assert.That(_changes.Exists(c => c.ObjectId == 2 && c.Kind == ObjectStateChangeKind.Cleared), Is.True);

            // Leaving the client's interest: the entries are forgotten, not cleared.
            _changes.Clear();
            Invoke("OnEntityDespawn", new EntityDespawnMsg { NetId = NetId, Epoch = 1 });
            Assert.That(ChunkState.Holds(_chunk.ContainerId), Is.False);
            Assert.That(_changes.Count, Is.EqualTo(2));
            Assert.That(_changes.TrueForAll(c => c.Kind == ObjectStateChangeKind.Forgotten), Is.True);
        }

        [Test]
        public void AClientExpiresAnEntryOnItsOwnWhenItsTimeComes()
        {
            _source.Seed(4, new ObjectState(0, _nowMs + 5_000));
            Spawn();
            Assert.That(ChunkState.TryGet(_chunk, 4, out _), Is.True);

            _changes.Clear();
            _nowMs += 5_000;
            Assert.That(ChunkState.TryGet(_chunk, 4, out _), Is.False, "reads compare the time at once");
            ChunkState.PollExpiries();
            Assert.That(_changes.Count, Is.EqualTo(1));
            Assert.That(_changes[0].Kind, Is.EqualTo(ObjectStateChangeKind.Expired));

            // The authority's own removal arrives later and changes nothing more.
            _changes.Clear();
            _source.Remove(4);
            Invoke("OnEntityMaps", Changes());
            Assert.That(_changes, Is.Empty);
        }

        [Test]
        public void AClientsClockFollowsTheServersTick()
        {
            ChunkState.Clock = null;
            long local = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.That(Math.Abs(ChunkState.NowUnixMs - local), Is.LessThan(1_000), "no server tick yet: the local clock");

            // The server is an hour ahead of this client's clock.
            NetworkTime.LatestServerTick = unchecked(NetworkTime.DerivedTick + (uint)(3600 * NetworkTime.TickRate));
            Assert.That(Math.Abs(ChunkState.NowUnixMs - (local + 3_600_000)), Is.LessThan(1_000));
        }
    }
}
