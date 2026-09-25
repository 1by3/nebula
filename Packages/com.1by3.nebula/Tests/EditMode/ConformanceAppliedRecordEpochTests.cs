using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// A record applied to an entity with <see cref="NebulaPersistence.Apply"/> brings its epoch with it
    /// (NEB-325, <c>docs/persistence-durability.md</c> D13).
    /// <para>
    /// The failure this pins: the documented way to bring a returning player back is to spawn the pawn, read its
    /// record with <see cref="NebulaPersistence.Load"/> and apply it. The pawn kept the epoch it was spawned at (1),
    /// while its record carried the epoch of the pawn's last life (5 after four handovers, say). The store keeps a
    /// save only when its epoch is at least the stored one, so every checkpoint of the pawn was refused for the
    /// whole session, and the refusal was a debug line.
    /// </para>
    /// <para>
    /// Tier B: two <b>real</b> <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/>, each with a real
    /// <see cref="NebulaPersistence"/> over one shared <see cref="LocalPersistenceStore"/>, as in
    /// <see cref="ConformanceDeclaredDeadWorkerTests"/>. The container is leased to nobody, so no restore runs on its
    /// own: every record comes back because the test applies it.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceAppliedRecordEpochTests
    {
        private const string Key = "player:alice";

        private sealed class Wallet : NetworkBehaviour
        {
            [Persist] public NetworkVariable<int> Coins = new NetworkVariable<int>(0);
        }

        private ConformanceMesh _mesh;
        private LocalPersistenceStore _store;
        private NebulaPersistence _p1, _p2;
        private float _time;
        private ushort _pawnPrefab;
        private Container _cell;

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            NebulaLifecycle.Reset();
            _mesh = new ConformanceMesh(2);
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            pawn.AddComponent<PersistentEntity>();
            pawn.AddComponent<Wallet>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);
            _cell = _mesh.AddStaticContainer("cell", Vector3.zero, new Vector3(100f, 20f, 100f));
            _time = 10f;
            _store = new LocalPersistenceStore(); // memory backend: the orchestrator's store, shared by both workers
            _store.Connect();
            _p1 = AttachPersistence(W1);
            _p2 = AttachPersistence(W2);
        }

        [TearDown]
        public void TearDown()
        {
            _p1?.Shutdown();
            _p2?.Shutdown();
            _mesh.Dispose();
            _store.Dispose();
            NebulaLifecycle.Reset();
        }

        private NebulaPersistence AttachPersistence(ConformanceMesh.Worker w)
        {
            var persistence = new NebulaPersistence(w.Instance, _mesh.Config, _store) { Now = () => _time };
            typeof(NebulaWorker).GetProperty(nameof(NebulaWorker.Persistence)).SetValue(w.Instance, persistence);
            return persistence;
        }

        /// <summary>One frame of a worker's persistence pass, and the store answering what it was asked.</summary>
        private void Frame(ConformanceMesh.Worker w, NebulaPersistence p) => w.Act(() => { p.Update(); _store.Tick(); });

        private PersistedEntityRecord RecordOf(string key)
        {
            PersistedEntityRecord record = null;
            _store.Load(key, r => record = r);
            _store.Tick();
            Assert.That(record, Is.Not.Null, $"{key} is in the store");
            return record;
        }

        private int CoinsIn(PersistedEntityRecord record)
        {
            var probe = NetworkPrefabs.Instantiate(_pawnPrefab, Vector3.zero, Quaternion.identity, null);
            try
            {
                probe.Initialize();
                PersistentStateCodec.Read(record.State, probe);
                return probe.GetComponent<Wallet>().Coins.Value;
            }
            finally { Object.DestroyImmediate(probe.gameObject); }
        }

        /// <summary>
        /// The player's previous life, on w2: handed over four times (epoch 5), holding 40 coins, saved. It stays
        /// alive, standing for a worker that lost the pawn and does not know it yet.
        /// </summary>
        private NetworkIdentity PreviousLife()
        {
            var old = W2.SpawnServerDriven(_pawnPrefab, _cell, new Vector3(5f, 1f, 5f), Quaternion.identity);
            old.Persistent.Key = Key;
            old.Epoch = 5;
            old.GetComponent<Wallet>().Coins.Value = 40;
            W2.Act(() => _p2.SaveNow(old));
            var saved = RecordOf(Key);
            Assume.That(saved.Epoch, Is.EqualTo(5u));
            Assume.That(CoinsIn(saved), Is.EqualTo(40));
            return old;
        }

        /// <summary>The documented rejoin: spawn a fresh pawn on w1, read its record and apply it when it arrives.</summary>
        private NetworkIdentity Rejoin()
        {
            var pawn = W1.SpawnServerDriven(_pawnPrefab, _cell, new Vector3(20f, 1f, 20f), Quaternion.identity);
            Assume.That(pawn.Epoch, Is.EqualTo(1u), "a new spawn starts at epoch 1, below its record's");
            W1.Act(() =>
            {
                pawn.Persistent.Key = Key;
                _p1.Load(Key, record => _p1.Apply(record, pawn, applyPose: false));
                _store.Tick();
            });
            return pawn;
        }

        [Test]
        public void APawnGivenItsRecordAfterItSpawnedKeepsSaving()
        {
            PreviousLife();
            var pawn = Rejoin();

            Assert.That(pawn.GetComponent<Wallet>().Coins.Value, Is.EqualTo(40), "the state came back");
            Assert.That(pawn.Epoch, Is.EqualTo(6u), "the pawn continues the record's lineage at the next epoch, as a restore would");
            Assert.That(pawn.Persistent.StampPending, Is.True);
            W1.Act(() => pawn.PrepareReplication(1));
            Assert.That(pawn.HasReplicationState && pawn.ReplicationState.Epoch == 6u, Is.True,
                "the new epoch goes out with the next state, so ghosts and clients take it as after a handover");

            // The stamp: the next pass saves it, without waiting for the checkpoint interval.
            Frame(W1, _p1);
            var stamped = RecordOf(Key);
            Assert.That(stamped.Epoch, Is.EqualTo(6u));
            Assert.That(stamped.SavedBy, Is.EqualTo(W1.Id));

            // What the player does from here is saved on the schedule, for the rest of the session.
            pawn.GetComponent<Wallet>().Coins.Value = 55;
            _time += _mesh.Config.PersistenceCheckpointSeconds + 1f;
            Frame(W1, _p1);
            var later = RecordOf(Key);
            Assert.That(CoinsIn(later), Is.EqualTo(55), "the checkpoint landed");
            Assert.That(later.Epoch, Is.GreaterThanOrEqualTo(6u));
            Assert.That(_store.StaleSavesDropped, Is.Zero, "no save of the pawn was refused");
        }

        [Test]
        public void ASaveFromTheOlderLifeIsStillRefusedAndWarnedAboutOnce()
        {
            var old = PreviousLife();
            var pawn = Rejoin();
            Frame(W1, _p1);
            Assume.That(RecordOf(Key).Epoch, Is.EqualTo(6u));

            // w2 still holds the previous life and saves it late. The epoch rule refuses it, and says so.
            old.GetComponent<Wallet>().Coins.Value = 999;
            var warnings = new List<string>();
            Application.LogCallback onLog = (message, _, type) => { if (type == LogType.Warning && message.Contains("dropped a save of")) warnings.Add(message); };
            Application.logMessageReceived += onLog;
            try
            {
                W2.Act(() => _p2.SaveNow(old));
                W2.Act(() => _p2.SaveNow(old)); // a second refusal of the same key is not warned about again
            }
            finally { Application.logMessageReceived -= onLog; }
            Assert.That(warnings.Count, Is.EqualTo(1), "one warning per key");
            StringAssert.Contains("dropped a save of player:alice at epoch 5 from w2 because the store holds epoch 6", warnings[0]);

            var record = RecordOf(Key);
            Assert.That(record.SavedBy, Is.EqualTo(W1.Id), "the older life did not overwrite the rejoined pawn's record");
            Assert.That(CoinsIn(record), Is.EqualTo(40));
            Assert.That(_store.StaleSavesDropped, Is.EqualTo(2));
            Assert.That(pawn.Epoch, Is.EqualTo(6u));
        }

        [Test]
        public void ARecordAppliedBeforeTheSpawnSetsTheSpawnEpochOnce()
        {
            PreviousLife();
            var record = RecordOf(Key);

            // The game applies the record first and spawns afterwards, with an ordinary spawn call.
            NetworkIdentity pawn = null;
            W1.Act(() =>
            {
                pawn = NetworkPrefabs.Instantiate(_pawnPrefab, new Vector3(20f, 1f, 20f), Quaternion.identity, _cell.transform);
                _p1.Apply(record, pawn, applyPose: false);
                W1.Instance.SpawnServerDriven(pawn, _cell);
            });
            Assert.That(pawn.Epoch, Is.EqualTo(6u), "spawned at the record's epoch + 1, not 1");
            Assert.That(pawn.Persistent.AdoptedEpoch, Is.Zero, "the spawn consumed it");
            Frame(W1, _p1);
            Assert.That(RecordOf(Key).SavedBy, Is.EqualTo(W1.Id), "and its first save landed");

            // A restore applies the record and spawns at record.Epoch + 1 itself: the two do not add up.
            var copy = RecordOf(Key);
            copy.Key = "player:bob";
            _store.Save(copy);
            NetworkIdentity restored = null;
            W1.Act(() => restored = _p1.Restore(RecordOf("player:bob")));
            Assert.That(restored, Is.Not.Null);
            Assert.That(restored.Epoch, Is.EqualTo(7u), "the restored copy of an epoch-6 record comes back at 7, once");
        }

        [Test]
        public void AGhostGivenARecordTakesTheStateButNotTheEpoch()
        {
            PreviousLife();
            var record = RecordOf(Key);
            var ghost = W1.SpawnServerDriven(_pawnPrefab, _cell, new Vector3(20f, 1f, 20f), Quaternion.identity);
            ghost.HasAuthority = false; // w1 holds a copy it does not own
            W1.Act(() => _p1.Apply(record, ghost, applyPose: false));
            Assert.That(ghost.GetComponent<Wallet>().Coins.Value, Is.EqualTo(40));
            Assert.That(ghost.Epoch, Is.EqualTo(1u), "only the owner's epoch counts");
            Assert.That(ghost.Persistent.StampPending, Is.False);
        }
    }
}
