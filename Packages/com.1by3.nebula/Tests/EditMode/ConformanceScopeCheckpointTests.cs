using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The store half of conformance scenario 3 (<c>docs/conformance-suite.md</c>, NEB-240): the worker-side
    /// <b>checkpoint-then-release</b> step of a retire saves every persistent entity in a scope's part, the save
    /// becomes durable before the part is emptied, and what comes back on the re-activation is <b>identical</b>.
    /// <para>
    /// Tier B/C: a real <see cref="NebulaPersistence"/> over a real <see cref="LocalPersistenceStore"/> (memory
    /// backend) against a bare <see cref="NebulaWorker"/> that is never started, the fixture NEB-224 left in
    /// <see cref="ConformancePersistenceDurabilityTests"/>, with <see cref="NebulaPersistence.Now"/> as the clock
    /// seam. The container is a genuine scope part: a <see cref="LocalControlPlane"/> activates the scope and
    /// <see cref="ContainerRegistry.SyncRuntime"/> registers its lease rows, so the records carry the scope key
    /// the location contract asks for. Nothing here re-implements the scheduler or the codec.
    /// </para>
    /// <para>
    /// What this cannot cover is a real retire on a real mesh (tier D, not built): the orchestrator's sweep and the
    /// gateway's refusal are covered separately in <c>ConformanceScopeLifecycleTests</c> and
    /// <c>Services~/Nebula.Services.Tests/ConformanceScopeAdmissionTests.cs</c>.
    /// </para>
    /// </summary>
    public sealed class ConformanceScopeCheckpointTests
    {
        private const string Key = "interior/raid-7";
        private const string Part = "interior";

        private sealed class Chest : NetworkBehaviour
        {
            [Persist] public NetworkVariable<int> Coins = new NetworkVariable<int>(3);
            public ushort Lock;

            public override void WritePersistentState(NetworkWriter writer) => writer.WriteUShort(Lock);
            public override void ReadPersistentState(NetworkReader reader) => Lock = reader.ReadUShort();
        }

        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private NebulaPersistence _persistence;
        private LocalPersistenceStore _store;
        private NebulaConfig _config;
        private float _clock;

        [SetUp]
        public void SetUp()
        {
            _clock = 0f;
            ContainerRegistry.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            _persistence?.Shutdown();
            _persistence = null;
            _store?.Dispose();
            _store = null;
            foreach (var d in _disposables) { try { d.Dispose(); } catch { } }
            _disposables.Clear();
            if (_config != null) UnityEngine.Object.DestroyImmediate(_config);
            _config = null;
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
            ContainerRegistry.Rebuild();
        }

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        /// <summary><c>_tracked</c> is private; reached directly rather than through the spawn event, which only
        /// <see cref="NebulaWorker"/> itself can raise (D1 in docs/conformance-suite.md).</summary>
        private static List<PersistentEntity> TrackedOf(NebulaPersistence persistence) =>
            (List<PersistentEntity>)typeof(NebulaPersistence)
                .GetField("_tracked", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(persistence);

        [Test]
        [Category("Conformance")] // scenario 3: the forced checkpoint of a retiring scope's part, and what comes back
        public void RetiringAPartCheckpointsEveryPersistentEntityAndItComesBackIdentical()
        {
            // The scope and its lease rows, exactly as an activation writes them, so the part is a real scope
            // container with a real scope key rather than a hand-made box.
            var plane = new LocalControlPlane();
            _disposables.Add(plane);
            plane.Connect();
            plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Key,
                PreferredWorkerId = "w1",
                Definition = new ScopeDefinition
                {
                    Kind = ScopeKind.Parts,
                    Parts = { new ScopePart { PartId = Part, Center = new Vector3(500, 0, 0), Size = new Vector3(40, 40, 40) } },
                },
            });
            ContainerRegistry.SyncRuntime(plane.Leases);
            string containerId = ScopeKeys.ContainerId(Key, Part);
            var container = ContainerRegistry.FindById(containerId);
            Assert.That(container, Is.Not.Null, "the activation registered the part");
            Assert.That(container.ScopeKey, Is.EqualTo(Key));

            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            _config.PersistenceCheckpointSeconds = 5f;
            _store = new LocalPersistenceStore(); // memory backend: no store-write latency of its own
            _store.Connect();
            var worker = NewObject("worker").AddComponent<NebulaWorker>();
            _persistence = new NebulaPersistence(worker, _config, _store) { Now = () => _clock };
            var tracked = TrackedOf(_persistence);

            const int count = 5;
            var expected = new Dictionary<string, (int Coins, ushort Lock, Vector3 Position)>();
            for (int i = 0; i < count; i++)
            {
                var go = NewObject("chest" + i);
                go.transform.position = new Vector3(500 + i, 0, 0);
                var identity = go.AddComponent<NetworkIdentity>();
                var chest = go.AddComponent<Chest>();
                var pe = go.AddComponent<PersistentEntity>();
                pe.Key = "chest" + i;
                identity.Initialize();
                identity.HasAuthority = true;
                identity.IsSpawned = true;
                identity.Container = container;
                chest.Coins.Value = 100 + i;
                chest.Lock = (ushort)(7 + i);
                tracked.Add(pe);
                expected[pe.Key] = (chest.Coins.Value, chest.Lock, go.transform.position);
            }

            // An entity of another container, to prove the forced checkpoint is scoped to the part being retired.
            var outsider = NewObject("outsider").AddComponent<NetworkIdentity>();
            outsider.gameObject.AddComponent<Chest>();
            var outsiderPe = outsider.gameObject.AddComponent<PersistentEntity>();
            outsiderPe.Key = "outsider";
            outsider.Initialize();
            outsider.HasAuthority = true;
            outsider.IsSpawned = true;
            tracked.Add(outsiderPe);

            // The forced checkpoint: the step the retire sequence runs before it empties the part. Nothing was due
            // on the schedule — the clock has not moved — so a save here is the retire's doing, not the timer's.
            int saved = _persistence.CheckpointContainer(containerId);
            Assert.That(saved, Is.EqualTo(count), "every persistent entity in the part, and only those");

            // The write barrier the sequence waits on before it acknowledges (IPersistenceStore.WhenWritten).
            bool durable = false;
            _store.WhenWritten(() => durable = true);
            _store.Tick();
            Assert.That(durable, Is.True, "the saves reached the store before the part is emptied");

            // "Comes back identical": every record is there, in the right place, and applying it to a fresh entity
            // reproduces the state the retire took away.
            IReadOnlyList<PersistedEntityRecord> records = null;
            _store.LoadContainer(containerId, r => records = r);
            _store.Tick();
            Assert.That(records, Is.Not.Null);
            Assert.That(records.Count, Is.EqualTo(count), "the part's records, and not the outsider's");

            foreach (var record in records)
            {
                Assert.That(expected.ContainsKey(record.Key), $"unexpected record {record.Key}");
                var want = expected[record.Key];
                Assert.That(record.ContainerId, Is.EqualTo(containerId), "the record names the scope's part");
                Assert.That(record.ScopeKey, Is.EqualTo(Key), "and the scope it belonged to (location contract D6)");

                var fresh = NewObject("restored-" + record.Key).AddComponent<NetworkIdentity>();
                var freshChest = fresh.gameObject.AddComponent<Chest>();
                fresh.gameObject.AddComponent<PersistentEntity>();
                fresh.Initialize();
                fresh.HasAuthority = true;
                fresh.Container = container;
                _persistence.Apply(record, fresh);

                Assert.That(freshChest.Coins.Value, Is.EqualTo(want.Coins), $"{record.Key} kept its persisted variable");
                Assert.That(freshChest.Lock, Is.EqualTo(want.Lock), $"{record.Key} kept its behaviour state");
                Assert.That(fresh.transform.position.x, Is.EqualTo(want.Position.x).Within(0.001f), $"{record.Key} came back where it was");
            }
        }

        [Test]
        [Category("Conformance")] // scenario 3: the restore of a re-activated part completes, and says so once
        public void ThePartsRestoreCompletesOnceAndReportsItsCount()
        {
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            _config.PersistenceRestoreGraceSeconds = 1f;
            _store = new LocalPersistenceStore();
            _store.Connect();
            var worker = NewObject("worker").AddComponent<NebulaWorker>();
            _persistence = new NebulaPersistence(worker, _config, _store) { Now = () => _clock };

            // A part with nothing saved in it still has to report its restore complete, or a re-activated scope
            // whose contents were all transient would never admit anybody again.
            var completed = new List<string>();
            int reported = -1;
            _persistence.ContainerRestored += (id, n) => { completed.Add(id); reported = n; };

            var leasedSince = (Dictionary<string, float>)typeof(NebulaPersistence)
                .GetField("_leasedSince", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_persistence);
            string containerId = ScopeKeys.ContainerId(Key, Part);
            leasedSince[containerId] = 0f;

            _clock = 0.5f;
            _persistence.Update();
            _store.Tick();
            Assert.That(completed, Is.Empty, "the restore waits out PersistenceRestoreGraceSeconds first");
            Assert.That(_persistence.IsContainerRestored(containerId), Is.False);

            _clock = 2f;
            _persistence.Update();
            _store.Tick();
            Assert.That(completed, Is.EqualTo(new[] { containerId }));
            Assert.That(reported, Is.EqualTo(0), "nothing was saved in it");
            Assert.That(_persistence.IsContainerRestored(containerId), Is.True, "which is what the scope's ack is waiting on");

            _clock = 4f;
            _persistence.Update();
            _store.Tick();
            Assert.That(completed.Count, Is.EqualTo(1), "and it is reported once per lease, not once per frame");
        }
    }
}
