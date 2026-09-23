using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 19 (<c>docs/conformance-suite.md</c>, design <c>docs/persistence-durability.md</c>
    /// D8–D11): <b>kill a worker's heartbeats, not its process, with persistent entities dirty, and exactly one live
    /// copy of each entity is left</b>, holding the survivor's state, with nothing the old owner wrote late winning
    /// in the store.
    /// <para>
    /// The failure this pins (NEB-256): a worker that had only missed heartbeats was declared dead, its container
    /// was dealt to another worker, and that worker restored the container's entities from their last checkpoint
    /// while the "dead" worker's copies were still alive. For a while two authoritative copies of each entity
    /// existed, and the restored one was up to a checkpoint interval stale.
    /// </para>
    /// <para>
    /// Tier B: two <b>real</b> <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/>, each with a real
    /// <see cref="NebulaPersistence"/> over one shared <see cref="LocalPersistenceStore"/> (the orchestrator's
    /// store), and a <see cref="LocalControlPlane"/> whose clock the test drives. The orchestrator's part — noticing
    /// the stale heartbeat, removing the row and dealing the container — is done by hand with the same calls
    /// <c>NebulaOrchestrator.ReapDeadWorkers</c> makes. The container registry is process-wide, so both workers see
    /// a lease change at the same moment; in a real mesh the old owner may see it much later, which is why the
    /// fence (D8) and the epoch stamp (D9) must not depend on it. The gateway half (a client is never shown both
    /// copies) is <c>Services~/Nebula.Services.Tests/DeclaredDeadWorkerTests.cs</c>.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceDeclaredDeadWorkerTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const int Crates = 3;

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private LocalPersistenceStore _store;
        private NebulaPersistence _p1, _p2;
        private DateTime _clock;
        private float _time;
        private ushort _cratePrefab;
        private Container _cell;

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            NebulaLifecycle.Reset();
            _mesh = new ConformanceMesh(2);
            _mesh.Config.PersistenceRestoreGraceSeconds = 1f;
            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            crate.AddComponent<PersistentEntity>();
            _cratePrefab = _mesh.RegisterPrefab(crate);
            _cell = _mesh.AddStaticContainer("cell", Vector3.zero, new Vector3(100f, 20f, 100f));

            _clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _time = 10f;
            _plane = new LocalControlPlane { Clock = () => _clock };
            _plane.Connect();
            foreach (var w in _mesh.Workers)
            {
                _plane.RegisterWorker(w.Id, w.Index, "127.0.0.1", (ushort)(7000 + w.Index));
                _plane.HeartbeatWorker(w.Id, WorkerStatus.Ready, default);
                Attach(w.Instance, _plane);
            }
            _plane.EnsureContainer(_cell.ContainerId);
            _plane.AssignContainer(_cell.ContainerId, W1.Id);
            ControlPlaneChanged(W1);
            ControlPlaneChanged(W2);
            Assume.That(_cell.IsOwnedBy(W1.Id));

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
            _plane.Dispose();
            _store.Dispose();
            NebulaLifecycle.Reset();
        }

        // ------------------------------------------------------------------------------------------- the fixture

        /// <summary>Point a worker at the control plane and mark it registered into the current document.</summary>
        private static void Attach(NebulaWorker worker, LocalControlPlane plane)
        {
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", Flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", Flags).GetValue(worker);
            registration.WorkerId = worker.WorkerId;
            registration.WorkerIndex = worker.WorkerIndex;
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", Flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", Flags).SetValue(registration, plane.DocumentId ?? "");
        }

        private NebulaPersistence AttachPersistence(ConformanceMesh.Worker w)
        {
            var persistence = new NebulaPersistence(w.Instance, _mesh.Config, _store) { Now = () => _time };
            typeof(NebulaWorker).GetProperty(nameof(NebulaWorker.Persistence)).SetValue(w.Instance, persistence);
            return persistence;
        }

        /// <summary>The worker's own reaction to a new control-plane document, exactly as its mirror raises it.</summary>
        private static void ControlPlaneChanged(ConformanceMesh.Worker w) =>
            w.Act(() => typeof(NebulaWorker).GetMethod("OnControlPlaneChanged", Flags).Invoke(w.Instance, null));

        /// <summary>One frame of a worker's persistence pass, and the store answering what it was asked.</summary>
        private void Frame(ConformanceMesh.Worker w, NebulaPersistence p) => w.Act(() => { p.Update(); _store.Tick(); });

        /// <summary>Time passes; only the workers named here get a heartbeat through to the control plane.</summary>
        private void Pass(float seconds, params ConformanceMesh.Worker[] heartbeating)
        {
            _clock = _clock.AddSeconds(seconds);
            _time += seconds;
            foreach (var w in heartbeating) _plane.HeartbeatWorker(w.Id, WorkerStatus.Ready, default);
        }

        private PersistedEntityRecord RecordOf(string key)
        {
            PersistedEntityRecord record = null;
            bool answered = false;
            _store.Load(key, r => { record = r; answered = true; });
            _store.Tick();
            Assert.That(answered, Is.True);
            return record;
        }

        private static int LiveCopies(ConformanceMesh.Worker w, string key)
        {
            int n = 0;
            foreach (var e in w.Instance.Authoritative)
                if (e != null && e.IsSpawned && e.Persistent != null && e.Persistent.Key == key) n++;
            return n;
        }

        private List<NetworkIdentity> SpawnCrates(ConformanceMesh.Worker w, Container container)
        {
            var crates = new List<NetworkIdentity>();
            for (int i = 0; i < Crates; i++)
                crates.Add(w.SpawnServerDriven(_cratePrefab, container, container.transform.position + new Vector3(i * 5f, 1f, 0f), Quaternion.identity));
            return crates;
        }

        // ------------------------------------------------------------------------------------------- the scenario

        [Test]
        public void AWorkerDeclaredDeadWhileRunningLeavesExactlyOneCopyAndCannotOverwriteIt()
        {
            var crates = SpawnCrates(W1, _cell);
            var keys = new List<string>();
            foreach (var c in crates) keys.Add(c.Persistent.EnsureKey());
            Frame(W1, _p1);
            int savedByOldOwner = _p1.SavedCount;
            Assume.That(savedByOldOwner, Is.EqualTo(Crates), "the first checkpoint of every crate");
            var checkpoint = new Dictionary<string, Vector3>();
            foreach (var key in keys) checkpoint[key] = RecordOf(key).LocalPosition;

            // The crates change after that checkpoint: this is the state a restore cannot know about.
            foreach (var c in crates) { c.transform.position += new Vector3(0f, 0f, 7f); c.Persistent.MarkDirty(); }

            // w1's heartbeats stop landing; the process runs on. Past the timeout it fences itself: the same test the
            // orchestrator is about to apply to it.
            Pass(_mesh.Config.WorkerTimeoutSeconds + 1f, W2);
            Assert.That(W1.Instance.IsFenced, Is.True, "no heartbeat for longer than WorkerTimeoutSeconds");
            Assert.That(W2.Instance.IsFenced, Is.False);
            Frame(W1, _p1);
            W1.Act(() => _p1.SaveNow(crates[0]));
            Assert.That(_p1.SavedCount, Is.EqualTo(savedByOldOwner), "a fenced worker writes nothing, even when asked to");
            Assert.That(crates[0].Persistent.IsDirty, Is.True, "the change is kept for when the fence lifts");

            // The orchestrator declares it dead and deals its container to the survivor.
            _plane.UnregisterWorker(W1.Id);
            _plane.AssignContainer(_cell.ContainerId, W2.Id);
            ControlPlaneChanged(W2);
            Assert.That(_cell.IsOwnedBy(W2.Id), Is.True);
            var w2Peers = (System.Collections.IDictionary)W2.GetField("_workerPeersById");
            Assert.That(w2Peers.Contains(W1.Id), Is.False, "the survivor drops its link to the declared-dead worker (D11)");

            // The survivor restores after its grace period, and stamps the new epoch into the store first.
            Pass(_mesh.Config.PersistenceRestoreGraceSeconds + 0.1f, W2);
            Frame(W2, _p2);
            Assert.That(_p2.RestoredCount, Is.EqualTo(Crates), "every crate came back from its checkpoint");
            Frame(W2, _p2);
            foreach (var key in keys)
            {
                var record = RecordOf(key);
                Assert.That(record.SavedBy, Is.EqualTo(W2.Id), $"{key}: the restored life saved first (D9)");
                Assert.That(record.Epoch, Is.EqualTo(2u), $"{key}: at the epoch after the checkpoint's");
            }

            // Something the old owner had queued before it fenced reaches the store late. It is refused.
            foreach (var c in crates)
            {
                var late = _p1.BuildRecord(c);
                Assume.That(late.Epoch, Is.EqualTo(1u));
                _store.Save(late);
            }
            foreach (var key in keys)
            {
                var record = RecordOf(key);
                Assert.That(record.SavedBy, Is.EqualTo(W2.Id), $"{key}: the late save did not overwrite the survivor's record");
                Assert.That(record.LocalPosition, Is.EqualTo(checkpoint[key]));
            }

            // The old owner's control plane reaches it again: it learns it was declared dead and drops everything
            // in the container it lost, without saving, and registers again.
            ControlPlaneChanged(W1);
            Assert.That(_p1.SavedCount, Is.EqualTo(savedByOldOwner), "dropped without a checkpoint (D10)");
            foreach (var key in keys)
            {
                Assert.That(LiveCopies(W1, key), Is.Zero, $"{key}: the old owner holds no copy");
                Assert.That(LiveCopies(W2, key), Is.EqualTo(1), $"{key}: exactly one live copy, on the survivor");
                var record = RecordOf(key);
                Assert.That(record.SavedBy, Is.EqualTo(W2.Id));
                Assert.That(record.Epoch, Is.EqualTo(2u));
            }
            Assert.That(_plane.FindWorker(W1.Id), Is.Not.Null, "it is back on the control plane as an empty worker");
            Assert.That(W1.Instance.IsFenced, Is.False);
        }

        /// <summary>
        /// A fence that turns out to be a false alarm (the orchestrator was slow, not deciding) costs nothing: the
        /// worker kept simulating, and what changed while it was fenced is saved once its heartbeat lands.
        /// </summary>
        [Test]
        public void AWorkerWhoseHeartbeatLandsAgainSavesWhatChangedWhileItWasFenced()
        {
            var crates = SpawnCrates(W1, _cell);
            Frame(W1, _p1);
            int saved = _p1.SavedCount;
            foreach (var c in crates) { c.transform.position += new Vector3(0f, 0f, 7f); c.Persistent.MarkDirty(); }

            Pass(_mesh.Config.WorkerTimeoutSeconds + 1f, W2);
            Frame(W1, _p1);
            Assert.That(_p1.SavedCount, Is.EqualTo(saved));

            Pass(0.1f, W1, W2);
            Assert.That(W1.Instance.IsFenced, Is.False);
            Frame(W1, _p1);
            Assert.That(_p1.SavedCount, Is.EqualTo(saved + Crates), "every change made while fenced is saved now");
            foreach (var c in crates)
                Assert.That(RecordOf(c.Persistent.Key).LocalPosition.z, Is.EqualTo(c.LocalPosition.z).Within(1e-3f));
            Assert.That(LiveCopies(W1, crates[0].Persistent.Key), Is.EqualTo(1));
        }

        /// <summary>
        /// A fenced worker hands nothing over: its idea of who owns a container may be stale, and the receiver would
        /// hold a copy of something that is being restored elsewhere. Once the fence lifts the handover happens.
        /// </summary>
        [Test]
        public void AFencedWorkerHandsNothingOverUntilItsHeartbeatLands()
        {
            var crates = SpawnCrates(W1, _cell);
            Pass(_mesh.Config.WorkerTimeoutSeconds + 1f, W2);
            Assume.That(W1.Instance.IsFenced, Is.True);

            // The cell's lease moves to w2 (a rebalance that raced the missed heartbeats).
            ContainerRegistry.ApplyLease(_cell.ContainerId, W2.Id, W2.Index, 2);
            W1.Tick(1);
            _mesh.Pump();
            foreach (var c in crates)
            {
                Assert.That(c.HasAuthority, Is.True, "kept while fenced");
                Assert.That(W2.Find(c.NetId), Is.Null, "nothing reached the other worker");
            }

            Pass(0.1f, W1, W2);
            W1.Tick(2);
            _mesh.Pump();
            foreach (var c in crates)
            {
                var there = W2.Find(c.NetId);
                Assert.That(there, Is.Not.Null, "handed over once the heartbeat landed");
                Assert.That(there.HasAuthority, Is.True);
            }
        }
    }
}
