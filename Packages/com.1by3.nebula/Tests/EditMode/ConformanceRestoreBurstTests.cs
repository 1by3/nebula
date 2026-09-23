using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 17 of <c>docs/conformance-suite.md</c>: <b>a worker that gains hundreds of containers at
    /// once restores every one of them with a bounded number of store requests</b>. However many leases arrive in one
    /// go, <see cref="NebulaPersistence"/> reads them through <see cref="IPersistenceStore.LoadContainers"/> in batches
    /// of at most <see cref="NebulaPersistence.MaxContainersPerRestoreLoad"/>, with at most
    /// <see cref="NebulaPersistence.MaxRestoreLoadsInFlight"/> batches waiting on the store; every container is still
    /// read exactly once and judged with its own records, and a container whose lease moves on while its batch is in
    /// flight is not restored from the stale answer.
    /// <para>
    /// The failure this pins: a worker that was re-dealt a few hundred containers after another worker died fired one
    /// load per container at the orchestrator, all at once. The burst starved the worker's thread pool, its
    /// control-plane heartbeat stopped completing, and the orchestrator declared it dead too and dealt everything to
    /// the next worker, which got the same burst.
    /// </para>
    /// <para>
    /// Tier C, Unity only: a real <see cref="NebulaPersistence"/> on the bare <see cref="NebulaWorker"/> fixture of
    /// <c>ConformanceScopeCheckpointTests</c>, over a real <see cref="LocalPersistenceStore"/> behind a pass-through
    /// that counts the reads and holds their answers until the test releases them, so "in flight" is something the
    /// test controls rather than something it races. The leases are real runtime-container rows applied through
    /// <see cref="ContainerRegistry"/>, as a worker's own control-plane pass applies them. Tier B would add a second
    /// worker and nothing else: the batching is one worker's decision. The records are scene-entity records, which a
    /// restore files for the worker's scene pass instead of spawning, so each can be traced back to the container it
    /// was read for without play mode. The store and HTTP half (one request per batch, a bounded number of requests
    /// in flight, the SQL query) is <c>Services~/Nebula.Services.Tests/ConformanceRestoreBurstTests.cs</c>.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceRestoreBurstTests
    {
        private const string Worker = "w1";

        /// <summary>The worker's store, with the answers to container reads held back until <see cref="Release"/>.</summary>
        private sealed class HoldingStore : IPersistenceStore
        {
            public readonly LocalPersistenceStore Inner = new LocalPersistenceStore();
            public readonly List<IReadOnlyList<string>> Reads = new List<IReadOnlyList<string>>();
            private readonly List<Action> _held = new List<Action>();
            public int MostInFlight;
            public int InFlight => _issued - _released;
            private int _issued, _released;

            public bool IsConnected => Inner.IsConnected;
            public string Backend => "holding";
            public int KnownCount => Inner.KnownCount;
            public void Connect() => Inner.Connect();
            public void Tick() => Inner.Tick();
            public void Save(PersistedEntityRecord record) => Inner.Save(record);
            public void Delete(string key) => Inner.Delete(key);
            public void WhenWritten(Action onWritten) => Inner.WhenWritten(onWritten);
            public void Load(string key, Action<PersistedEntityRecord> onLoaded) => Inner.Load(key, onLoaded);
            public void LoadCarried(string carrierKey, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded) => Inner.LoadCarried(carrierKey, onLoaded);
            public void LoadWhere(Func<PersistedEntityRecord, bool> predicate, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded) => Inner.LoadWhere(predicate, onLoaded);
            public void CountRecords(string scopeKey, string containerId, Action<int> onCounted) => Inner.CountRecords(scopeKey, containerId, onCounted);
            public void Clear() => Inner.Clear();
            public void Dispose() => Inner.Dispose();

            public void LoadContainers(IReadOnlyList<string> containerIds, Action<IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>>> onLoaded)
            {
                Reads.Add(new List<string>(containerIds));
                _issued++;
                MostInFlight = Math.Max(MostInFlight, InFlight);
                Inner.LoadContainers(containerIds, answer => _held.Add(() => { _released++; onLoaded(answer); }));
            }

            /// <summary>Answer every container read issued so far, on the main thread, as a store's Tick would.</summary>
            public void Release()
            {
                Inner.Tick();
                var answers = _held.ToArray();
                _held.Clear();
                foreach (var answer in answers) answer();
            }
        }

        private readonly List<GameObject> _objects = new List<GameObject>();
        private NebulaPersistence _persistence;
        private HoldingStore _store;
        private NebulaConfig _config;
        private NebulaWorker _worker;
        private float _clock;
        private readonly List<string> _restored = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _clock = 0f;
            _restored.Clear();
            NebulaLifecycle.Reset();
            ContainerRegistry.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            _persistence?.Shutdown();
            _persistence = null;
            _store?.Dispose();
            _store = null;
            if (_config != null) UnityEngine.Object.DestroyImmediate(_config);
            _config = null;
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            NebulaLifecycle.Reset();
            ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
            ContainerRegistry.Rebuild();
        }

        // ------------------------------------------------------------------------------------------- the fixture

        private static void SetProperty(object target, string name, object value) =>
            target.GetType().GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        private static Dictionary<uint, PersistedEntityRecord> SceneRecordsOf(NebulaPersistence persistence) =>
            (Dictionary<uint, PersistedEntityRecord>)typeof(NebulaPersistence)
                .GetField("_sceneRecords", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(persistence);

        private static string Chunk(int i) => ContainerRegistry.RuntimeContainerId((ulong)(1000 + i));

        private static LeaseInfo Lease(int i, string workerId) => new LeaseInfo
        {
            ContainerId = Chunk(i), WorkerId = workerId, Epoch = 1, State = LeaseState.Active,
            HasBounds = true, BoundsCenter = new Vector3(i * 20f, 0, 0), BoundsSize = new Vector3(10, 10, 10),
        };

        /// <summary>A worker with persistence on, and <paramref name="containers"/> chunks with one saved scene entity each.</summary>
        private void BuildWorker(int containers)
        {
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            _config.PersistenceRestoreGraceSeconds = 1f;
            _store = new HoldingStore();
            _store.Connect();
            for (int i = 0; i < containers; i++)
            {
                _store.Save(new PersistedEntityRecord { Key = "door-" + i, SceneId = (uint)(i + 1), ContainerId = Chunk(i), Epoch = 1, SavedBy = "w0" });
                // A second record a restore reads and deliberately leaves alone: a client owned it.
                if (i % 2 == 0) _store.Save(new PersistedEntityRecord { Key = "pawn-" + i, PrefabName = "Pawn", ContainerId = Chunk(i), Owned = true, Epoch = 1, SavedBy = "w0" });
            }
            // Something saved in a carrier and somewhere this worker does not lease: never part of a container read.
            _store.Save(new PersistedEntityRecord { Key = "cargo", SceneId = 900000, CarrierKey = "door-0", Epoch = 1 });
            _store.Save(new PersistedEntityRecord { Key = "far", SceneId = 900001, ContainerId = "elsewhere", Epoch = 1 });

            var go = new GameObject("worker");
            _objects.Add(go);
            _worker = go.AddComponent<NebulaWorker>();
            SetProperty(_worker, "WorkerId", Worker);
            _persistence = new NebulaPersistence(_worker, _config, _store) { Now = () => _clock };
            _persistence.ContainerRestored += (id, _) => _restored.Add(id);
        }

        /// <summary>The control plane deals these rows to the worker: register the boxes and apply the owners, as its lease pass does.</summary>
        private static void Deal(IReadOnlyList<LeaseInfo> rows)
        {
            ContainerRegistry.SyncRuntime(rows);
            foreach (var row in rows) ContainerRegistry.ApplyLease(row.ContainerId, row.WorkerId, 0, row.Epoch, row.State);
            ContainerRegistry.NotifyLeasesChanged();
        }

        private void Frame()
        {
            _persistence.Update();
            _store.Tick();
        }

        // ------------------------------------------------------------------------------------------- the scenario

        [Test]
        public void AWorkerDealtHundredsOfContainersAtOnceRestoresThemAllInAFewBoundedReads()
        {
            const int count = 600;
            BuildWorker(count);
            var rows = new List<LeaseInfo>();
            for (int i = 0; i < count; i++) rows.Add(Lease(i, Worker));
            Deal(rows);   // one control-plane change hands the worker all of them, as a re-deal after a worker died does

            _clock = 0.5f;
            for (int f = 0; f < 3; f++) Frame();
            Assert.That(_store.Reads, Is.Empty, "nothing is read inside the grace period, which lets a handover win");

            _clock = 2f;
            for (int f = 0; f < 5; f++) Frame();
            Assert.That(_store.Reads.Count, Is.EqualTo(NebulaPersistence.MaxRestoreLoadsInFlight),
                "the due containers go out in bounded batches, and no more batches than the in-flight bound while none has answered");
            Assert.That(_persistence.RestoreLoadsInFlight, Is.EqualTo(NebulaPersistence.MaxRestoreLoadsInFlight));
            foreach (var read in _store.Reads) Assert.That(read.Count, Is.EqualTo(NebulaPersistence.MaxContainersPerRestoreLoad), "each batch is full");
            Assert.That(_restored, Is.Empty, "nothing is restored before its read answers");

            // Answer, let the worker issue the rest, answer again, until it asks for nothing more.
            for (int round = 0; round < 10 && _restored.Count < count; round++)
            {
                _store.Release();
                Frame();
            }
            _store.Release();
            for (int f = 0; f < 3; f++) Frame();

            int expectedReads = (count + NebulaPersistence.MaxContainersPerRestoreLoad - 1) / NebulaPersistence.MaxContainersPerRestoreLoad;
            Assert.That(_store.Reads.Count, Is.EqualTo(expectedReads), "600 containers cost three reads, not six hundred");
            Assert.That(_store.MostInFlight, Is.LessThanOrEqualTo(NebulaPersistence.MaxRestoreLoadsInFlight), "never more reads waiting than the bound");
            Assert.That(_persistence.RestoreLoadsInFlight, Is.Zero);

            var asked = new HashSet<string>();
            foreach (var read in _store.Reads)
            {
                Assert.That(read.Count, Is.LessThanOrEqualTo(NebulaPersistence.MaxContainersPerRestoreLoad));
                foreach (var id in read) Assert.That(asked.Add(id), Is.True, $"{id} is read once");
            }
            Assert.That(asked.Count, Is.EqualTo(count), "every leased container was read");

            Assert.That(_restored.Count, Is.EqualTo(count), "every container's restore completed, once");
            Assert.That(new HashSet<string>(_restored).Count, Is.EqualTo(count));
            var sceneRecords = SceneRecordsOf(_persistence);
            Assert.That(sceneRecords.Count, Is.EqualTo(count), "each container's scene entity was brought back, and nothing from a carrier or an unleased box");
            for (int i = 0; i < count; i++)
            {
                Assert.That(_persistence.IsContainerRestored(Chunk(i)), Is.True, Chunk(i));
                Assert.That(sceneRecords.TryGetValue((uint)(i + 1), out var record), Is.True, $"door-{i}");
                Assert.That(record.ContainerId, Is.EqualTo(Chunk(i)), "judged with its own container's records");
            }
        }

        [Test]
        public void AContainerWhoseLeaseMovesOnWhileItsBatchIsInFlightIsNotRestoredFromTheStaleAnswer()
        {
            BuildWorker(3);
            Deal(new[] { Lease(0, Worker), Lease(1, Worker), Lease(2, Worker) });
            _clock = 2f;
            Frame();
            Assert.That(_store.Reads.Count, Is.EqualTo(1), "three containers, one read");
            Assert.That(_store.Reads[0], Is.EquivalentTo(new[] { Chunk(0), Chunk(1), Chunk(2) }));

            // Chunk 1 is dealt to another worker while the read is out.
            Deal(new[] { Lease(0, Worker), Lease(1, "w2"), Lease(2, Worker) });
            _store.Release();
            Frame();
            Assert.That(_restored, Is.EquivalentTo(new[] { Chunk(0), Chunk(2) }), "the rest of the batch still restores");
            Assert.That(_persistence.IsContainerRestored(Chunk(1)), Is.False);
            Assert.That(SceneRecordsOf(_persistence).ContainsKey(2), Is.False, "chunk 1's entity is the other worker's to restore");

            // It comes back: a fresh lease, a fresh grace period, a read of its own.
            Deal(new[] { Lease(0, Worker), Lease(1, Worker), Lease(2, Worker) });
            Frame();
            Assert.That(_store.Reads.Count, Is.EqualTo(1), "not before its new grace period");
            _clock = 4f;
            Frame();
            Assert.That(_store.Reads.Count, Is.EqualTo(2));
            Assert.That(_store.Reads[1], Is.EquivalentTo(new[] { Chunk(1) }), "only the container that came back is read again");
            _store.Release();
            Frame();
            Assert.That(_persistence.IsContainerRestored(Chunk(1)), Is.True);
            Assert.That(SceneRecordsOf(_persistence)[2].ContainerId, Is.EqualTo(Chunk(1)));
            Assert.That(_restored.Count, Is.EqualTo(3));
        }
    }
}
