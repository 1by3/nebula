using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 11 of <c>docs/conformance-suite.md</c>: <b>the lifecycle hooks fire in the documented
    /// order</b>. Design of record: <c>docs/lifecycle-hooks.md</c>; the sequence they sit in is
    /// <c>docs/scope-lifecycle.md</c>.
    /// <para>
    /// Tier B/C, Unity only: the production <see cref="WorkerScopeLifecycle"/> and a real
    /// <see cref="NebulaPersistence"/> over a real <see cref="LocalPersistenceStore"/>, against the bare
    /// <see cref="NebulaWorker"/> fixture NEB-224/NEB-240 left in <c>ConformancePersistenceDurabilityTests</c> and
    /// <c>ConformanceScopeCheckpointTests</c>, with a real <see cref="LocalControlPlane"/> holding the scope and its
    /// lease rows. Tier A would not do: every guarantee here is an ordering <i>between</i> the store's restore path
    /// and the worker's retire sequence, and both are Unity-side. What the test stands in for is only the
    /// orchestrator's sweep (it moves the scope's state by hand, exactly as <c>ConformanceScopeLifecycleTests</c>
    /// does) and the frame loop, which is driven a pass at a time through the clock seams.
    /// </para>
    /// <para>
    /// One tier limit, stated once: a scope's part carries an isolation id, so <i>spawning</i> a restored entity
    /// into it goes through <see cref="InstanceScenes"/>, which needs play mode. The restore therefore reads and
    /// judges the part's records here but brings nothing back into the box, and the count the hook reports is
    /// asserted against <see cref="NebulaPersistence.RestoredCountFor"/> — the very number the scope's <c>Restored</c>
    /// acknowledgement carries — rather than against a spawned entity. That a checkpointed entity comes back
    /// identical is <c>ConformanceScopeCheckpointTests</c>; end to end on a real mesh is tier D and is not built.
    /// </para>
    /// <para>
    /// The four ordering guarantees asserted here are D1–D4 and D6 of <c>docs/lifecycle-hooks.md</c>:
    /// <c>OnScopeActivating</c> before any of the scope's containers restores; <c>OnContainerRestored</c> only after
    /// the restore; <c>OnBeforeRetire</c> complete before anything is saved; <c>OnRetired</c> after the lease has
    /// gone. A hook that hangs or throws is logged and the retire proceeds.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceLifecycleHooksTests
    {
        private const string Key = "settlement/frontier-3";
        private const string Part = "interior";
        private const string Worker = "w1";

        private sealed class Colonist : NetworkBehaviour
        {
            [Persist] public NetworkVariable<int> Rations = new NetworkVariable<int>(1);
        }

        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly List<string> _order = new List<string>();
        private NebulaPersistence _persistence;
        private LocalPersistenceStore _store;
        private LocalControlPlane _plane;
        private NebulaConfig _config;
        private NebulaWorker _worker;
        private WorkerScopeLifecycle _agent;
        private string _containerId;
        private float _clock;

        [SetUp]
        public void SetUp()
        {
            _clock = 0f;
            _order.Clear();
            NebulaLifecycle.Reset();
            ContainerRegistry.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            NebulaLifecycle.Reset();
            NetworkPrefabs.Register(null);
            _agent?.CancelAll();
            _agent = null;
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

        // ------------------------------------------------------------------------------------------- the fixture

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        private static void SetProperty(object target, string name, object value) =>
            target.GetType().GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        private static List<PersistentEntity> TrackedOf(NebulaPersistence persistence) =>
            (List<PersistentEntity>)typeof(NebulaPersistence)
                .GetField("_tracked", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(persistence);

        private static Dictionary<string, float> LeasedSinceOf(NebulaPersistence persistence) =>
            (Dictionary<string, float>)typeof(NebulaPersistence)
                .GetField("_leasedSince", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(persistence);

        /// <summary>A scope with one part, owned by this worker, and a worker wired to a real store and control plane.</summary>
        private Container Mesh()
        {
            _plane = new LocalControlPlane();
            _disposables.Add(_plane);
            _plane.Connect();
            _plane.RegisterWorker(Worker, 0, "127.0.0.1", 7000);
            _plane.HeartbeatWorker(Worker, WorkerStatus.Ready, default);
            Activate();
            Sync();
            _containerId = ScopeKeys.ContainerId(Key, Part);
            var container = ContainerRegistry.FindById(_containerId);
            Assert.That(container, Is.Not.Null, "the activation registered the part");

            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            _config.PersistenceRestoreGraceSeconds = 1f;
            _config.PersistenceCheckpointSeconds = 5f;
            _store = new LocalPersistenceStore();
            _store.Connect();
            _worker = NewObject("worker").AddComponent<NebulaWorker>();
            SetProperty(_worker, "WorkerId", Worker);
            SetProperty(_worker, "ControlPlane", _plane);
            _persistence = new NebulaPersistence(_worker, _config, _store) { Now = () => _clock };
            SetProperty(_worker, "Persistence", _persistence);
            _agent = _worker.ScopeLifecycleAgent;
            _persistence.RestoreGate = _agent.MayRestore;   // what NebulaWorker.Initialize wires up
            Assert.That(container.IsOwnedBy(Worker), Is.True);
            return container;
        }

        /// <summary>Mirror the control plane's lease rows into the registry, owners and all, as a worker's own pass does.</summary>
        private void Sync()
        {
            ContainerRegistry.SyncRuntime(_plane.Leases);
            foreach (var lease in _plane.Leases)
                ContainerRegistry.ApplyLease(lease.ContainerId, lease.WorkerId, 0, lease.Epoch, lease.State);
            ContainerRegistry.NotifyLeasesChanged();
        }

        private void Activate() => _plane.ActivateScope(new ScopeActivationRequest
        {
            ScopeKey = Key,
            PreferredWorkerId = Worker,
            Requester = "matchmaker",
            Definition = new ScopeDefinition
            {
                Kind = ScopeKind.Parts,
                Parts = { new ScopePart { PartId = Part, Center = new Vector3(500, 0, 0), Size = new Vector3(40, 40, 40) } },
            },
        });

        /// <summary>One frame of the worker: the scope pass, the persistence pass, and the store's callbacks.</summary>
        private void Frame()
        {
            _agent.Pass(_clock);
            _persistence.Update();
            _store.Tick();
        }

        /// <summary>A record of the scope's part in the store, as a previous run's checkpoint left it.</summary>
        private void SaveRecord(string key) => _store.Save(new PersistedEntityRecord
        {
            Key = key, PrefabId = 0, PrefabName = "Colonist", ScopeKey = Key, ContainerId = _containerId, Epoch = 1, SavedBy = "w0",
        });


        private PersistentEntity Colonise(Container container, string key)
        {
            var go = NewObject(key);
            go.transform.position = new Vector3(500, 0, 0);
            var identity = go.AddComponent<NetworkIdentity>();
            go.AddComponent<Colonist>();
            var pe = go.AddComponent<PersistentEntity>();
            pe.Key = key;
            identity.Initialize();
            identity.HasAuthority = true;
            identity.IsSpawned = true;
            identity.Container = container;
            TrackedOf(_persistence).Add(pe);
            return pe;
        }

        private int CountInStore()
        {
            int n = -1;
            _store.CountRecords(Key, "", c => n = c);
            _store.Tick();
            return n;
        }

        // ------------------------------------------------------------------------------- D1, D2: coming to life

        [Test]
        public void AnActivationSaysWhetherThereAreRecordsBeforeAnyOfThemComesBack()
        {
            var container = Mesh();
            SaveRecord("colonist-1");
            SaveRecord("colonist-2");
            _store.Tick();

            bool? hasRecords = null;
            int restored = -1;
            NebulaLifecycle.OnScopeActivating += (scope, any) =>
            {
                _order.Add("activating");
                Assert.That(scope.ScopeKey, Is.EqualTo(Key));
                hasRecords = any;
            };
            NebulaLifecycle.OnContainerRestored += (c, n) =>
            {
                _order.Add("restored");
                Assert.That(c, Is.SameAs(container), "the hook names the container that came back");
                restored = n;
            };

            LeasedSinceOf(_persistence)[_containerId] = 0f;

            // The grace period is over, so the restore is due — but the scope has not been announced yet, and the
            // gate holds it: nothing may come back before the game has been told the scope is coming to life.
            _clock = 2f;
            _persistence.Update();
            _store.Tick();
            Assert.That(_order, Is.Empty);
            Assert.That(_persistence.IsContainerRestored(_containerId), Is.False, "the restore is held behind OnScopeActivating");

            // The worker's scope pass asks the store the cheap question and the answer raises the hook.
            _agent.Pass(_clock);
            _store.Tick();
            Assert.That(_order, Is.EqualTo(new[] { "activating" }));
            Assert.That(hasRecords, Is.True, "two records are saved under the scope's key");

            // Only now do the records come back, and the restore hook follows them.
            Frame();
            Assert.That(_order, Is.EqualTo(new[] { "activating", "restored" }), "restore completes before Restored fires");
            Assert.That(_persistence.IsContainerRestored(_containerId), Is.True, "the records were read and judged before the hook");
            Assert.That(restored, Is.EqualTo(_persistence.RestoredCountFor(_containerId)),
                "the hook reports what came back — the same number the scope's Restored acknowledgement carries");

            Frame();
            Assert.That(_order, Is.EqualTo(new[] { "activating", "restored" }), "each fires once per activation, not once per frame");
        }

        [Test]
        public void AFirstActivationIsToldThereIsNothingSaved()
        {
            Mesh();
            bool? hasRecords = null;
            NebulaLifecycle.OnScopeActivating += (scope, any) => hasRecords = any;

            LeasedSinceOf(_persistence)[_containerId] = 0f;
            _clock = 2f;
            Frame();
            _store.Tick();

            Assert.That(hasRecords, Is.False, "nothing was ever saved under this key: the game seeds the scope itself");
        }

        [Test]
        public void WithNoHandlerTheRestorePathIsUntouched()
        {
            Mesh();
            SaveRecord("colonist-1");
            _store.Tick();

            // Nobody is listening, so nothing is held and nothing is asked of the store: the restore happens on the
            // frame the grace period ends, exactly as it did before this item.
            LeasedSinceOf(_persistence)[_containerId] = 0f;
            _clock = 2f;
            _persistence.Update();
            _store.Tick();
            Assert.That(_persistence.IsContainerRestored(_containerId), Is.True, "no gate, no wait: the restore runs on the frame the grace period ends");
        }

        [Test]
        public void RecreatingARetiredPartOnTheSameWorkerLoadsItAgain()
        {
            Mesh();
            int completions = 0;
            _persistence.ContainerRestored += (id, count) => completions++;
            LeasedSinceOf(_persistence)[_containerId] = 0f;
            _clock = 2f;
            Frame();
            Assert.That(completions, Is.EqualTo(1));

            _plane.RemoveContainer(_containerId);
            _plane.SetScopeState(Key, ScopeState.Retired);
            Sync();
            _agent.Pass(_clock);
            Assert.That(_persistence.IsContainerRestored(_containerId), Is.False);

            Activate();
            Sync();
            Frame();
            Assert.That(_plane.FindScope(Key).Acks, Is.Empty, "the previous lease's completion cannot acknowledge this restore");
            Assert.That(completions, Is.EqualTo(1), "a new lease waits out its own restore grace");

            _clock = 4f;
            Frame();
            Frame();
            Assert.That(completions, Is.EqualTo(2), "the recreated part was loaded again");
            Assert.That(_plane.FindScope(Key).FindAck(_containerId, ScopePhase.Restored), Is.Not.Null);
        }

        [Test]
        public void ALoadFromThePreviousLeaseCannotCompleteTheNextRestore()
        {
            Mesh();
            LeasedSinceOf(_persistence)[_containerId] = 0f;
            _clock = 2f;
            _persistence.Update(); // Queue the old lease's load result without delivering it.

            _plane.RemoveContainer(_containerId);
            _plane.SetScopeState(Key, ScopeState.Retired);
            Sync();
            Activate();
            Sync();
            _store.Tick();
            Assert.That(_persistence.IsContainerRestored(_containerId), Is.False,
                "even an empty result from the old lease must be discarded");

            _clock = 4f;
            Frame();
            Assert.That(_persistence.IsContainerRestored(_containerId), Is.True);
        }

        [Test]
        public void ADisconnectedStoreDoesNotSeedOrAcknowledgeARestoringScope()
        {
            Mesh();
            SaveRecord("saved-colonist");
            _store.Dispose(); // Keep the records but model a configured store that is temporarily unavailable.
            _plane.SetScopeState(Key, ScopeState.Restoring);
            bool? hasRecords = null;
            NebulaLifecycle.OnScopeActivating += (scope, any) => hasRecords = any;
            LeasedSinceOf(_persistence)[_containerId] = 0f;

            _clock = 2f;
            Frame();
            Assert.That(hasRecords, Is.Null, "an unavailable store does not prove that the scope is empty");
            Assert.That(_plane.FindScope(Key).Acks, Is.Empty, "an unavailable store has not restored the part");
            Assert.That(_agent.MayRestore(_containerId), Is.False);

            _store.Connect();
            Frame();
            Assert.That(hasRecords, Is.True);
            Frame();
            Frame();
            Assert.That(_plane.FindScope(Key).FindAck(_containerId, ScopePhase.Restored), Is.Not.Null);
        }

        [Test]
        public void AnUnavailableStoreOpensTheGateOnTimeoutWithoutClaimingTheScopeIsEmpty()
        {
            Mesh();
            _store.Dispose();
            bool? hasRecords = null;
            NebulaLifecycle.OnScopeActivating += (scope, any) => hasRecords = any;
            _agent.Pass(0f);

            LogAssert.Expect(LogType.Warning, new Regex("store did not answer whether it holds any records"));
            _agent.Pass(WorkerScopeLifecycle.ActivatingTimeoutSeconds + 1f);
            Assert.That(_agent.MayRestore(_containerId), Is.True);
            Assert.That(hasRecords, Is.Null);

            SaveRecord("saved-colonist");
            _store.Connect();
            _agent.Pass(WorkerScopeLifecycle.ActivatingTimeoutSeconds + 2f);
            _store.Tick();
            Assert.That(hasRecords, Is.True, "recovery still raises the hook with the store's actual answer");
        }

        [Test]
        public void ACountFromThePreviousActivationCannotSeedTheNextOne()
        {
            Mesh();
            var answers = new List<bool>();
            NebulaLifecycle.OnScopeActivating += (scope, any) => answers.Add(any);
            _agent.Pass(0f); // Queue the old activation's answer: no records.

            _plane.RemoveContainer(_containerId);
            _plane.SetScopeState(Key, ScopeState.Retired);
            Sync();
            _agent.Pass(1f);
            SaveRecord("saved-colonist");
            Activate();
            Sync();
            _agent.Pass(2f); // The new activation has a different answer.
            _store.Tick();

            Assert.That(answers, Is.EqualTo(new[] { true }), "the old callback belongs to an activation that ended");
        }

        // ------------------------------------------------------------------------- D3, D6: going back to sleep

        [Test]
        public void TheBeforeRetireWindowFinishesBeforeAnythingIsSavedAndRetiredComesAfterTheLease()
        {
            var container = Mesh();
            Colonise(container, "colonist-1");

            // No RunContinuationsAsynchronously: completing it here runs the waiter inline, so the next pass of the
            // retire sequence sees a finished window rather than racing a thread pool.
            var gate = new TaskCompletionSource<bool>();
            Container retired = null;
            NebulaLifecycle.OnBeforeRetire += (c, cancel) =>
            {
                _order.Add("before-retire");
                Assert.That(c, Is.SameAs(container));
                Assert.That(CountInStore(), Is.Zero, "the window opens before anything is saved");
                return gate.Task;
            };
            NebulaLifecycle.OnRetired += c => { _order.Add("retired"); retired = c; };

            var scope = _plane.FindScope(Key);
            _plane.SetScopeState(Key, ScopeState.Retiring);

            // Step 2 of the retire sequence: the window opens and the sequence waits in it.
            _agent.Pass(_clock);
            _store.Tick();
            Assert.That(_order, Is.EqualTo(new[] { "before-retire" }));
            Assert.That(CountInStore(), Is.Zero, "nothing is checkpointed while the game is still writing its summary");
            Assert.That(scope.FindAck(_containerId, ScopePhase.Checkpointed), Is.Null, "and the part has not acknowledged");

            // The game is done: the forced checkpoint, the write barrier and the acknowledgement follow.
            gate.SetResult(true);
            _clock += WorkerScopeLifecycle.PassSeconds;
            _agent.Pass(_clock);
            _store.Tick();
            _agent.Pass(_clock);
            _store.Tick();
            Assert.That(CountInStore(), Is.EqualTo(1), "the checkpoint ran only after the window closed");
            var ack = scope.FindAck(_containerId, ScopePhase.Checkpointed);
            Assert.That(ack, Is.Not.Null, "and the orchestrator is told the part is safe to let go");
            Assert.That(ack.Count, Is.EqualTo(1));
            Assert.That(_order, Is.EqualTo(new[] { "before-retire" }), "nothing is retired while the lease is still held");

            // Step 5 is the orchestrator's: the leases go, and only then is the part retired.
            _plane.RemoveContainer(_containerId);
            _plane.SetScopeState(Key, ScopeState.Retired);
            Sync();
            _clock += WorkerScopeLifecycle.PassSeconds;
            _agent.Pass(_clock);
            Assert.That(_order, Is.EqualTo(new[] { "before-retire", "retired" }));
            Assert.That(retired, Is.SameAs(container), "the hook names the part that went");
        }

        [Test]
        public void AWindowThatOverrunsIsCancelledAndTheRetireProceeds()
        {
            var container = Mesh();
            Colonise(container, "colonist-1");

            var started = new TaskCompletionSource<bool>();
            bool cancelled = false;
            NebulaLifecycle.OnBeforeRetire += (c, cancel) =>
            {
                cancel.Register(() => cancelled = true);
                return started.Task; // never completes on its own
            };

            _plane.SetScopeState(Key, ScopeState.Retiring);
            _agent.Pass(_clock);
            Assert.That(CountInStore(), Is.Zero, "the retire is waiting in the window");

            LogAssert.Expect(LogType.Warning, new Regex("before-retire hook for .* did not finish within"));
            _clock += WorkerScopeLifecycle.BeforeRetireTimeoutSeconds + 1f;
            _agent.Pass(_clock);
            _store.Tick();
            _agent.Pass(_clock);
            _store.Tick();

            Assert.That(cancelled, Is.True, "the token is cancelled when the window closes");
            Assert.That(CountInStore(), Is.EqualTo(1), "and the retire carries on: a game's hung task never strands a scope");
            Assert.That(_plane.FindScope(Key).FindAck(_containerId, ScopePhase.Checkpointed), Is.Not.Null);
            started.SetResult(true);
        }

        [Test]
        public void AHandlerThatThrowsIsLoggedAndTheOthersStillRun()
        {
            var container = Mesh();
            Colonise(container, "colonist-1");

            bool second = false;
            NebulaLifecycle.OnBeforeRetire += (c, cancel) => throw new InvalidOperationException("the game's summary failed");
            NebulaLifecycle.OnBeforeRetire += (c, cancel) => { second = true; return Task.CompletedTask; };

            LogAssert.Expect(LogType.Error, new Regex("OnBeforeRetire handler threw"));
            _plane.SetScopeState(Key, ScopeState.Retiring);
            _agent.Pass(_clock);
            _store.Tick();
            _clock += WorkerScopeLifecycle.PassSeconds;
            _agent.Pass(_clock);
            _store.Tick();

            Assert.That(second, Is.True, "one handler's failure does not stop the next");
            Assert.That(CountInStore(), Is.EqualTo(1), "nor the retire");
        }

        // -------------------------------------------------------------------------------- D5: the offline read

        [Test]
        public void TheStoreCountsAScopesRecordsWithoutActivatingIt()
        {
            Mesh();
            SaveRecord("colonist-1");
            SaveRecord("colonist-2");
            _store.Save(new PersistedEntityRecord { Key = "crate", PrefabId = 0, ContainerId = "cell-1", Epoch = 1 });
            _store.Tick();

            Assert.That(CountInStore(), Is.EqualTo(2), "the scope's records, and not the public world's");

            int publicWorld = -1;
            _store.CountRecords(EntityLocation.PublicScope, "", n => publicWorld = n);
            _store.Tick();
            Assert.That(publicWorld, Is.EqualTo(1));

            int inPart = -1, elsewhere = -1;
            _store.CountRecords(Key, _containerId, n => inPart = n);
            _store.CountRecords(Key, "rt_somewhere-else", n => elsewhere = n);
            _store.Tick();
            Assert.That(inPart, Is.EqualTo(2), "a container of the scope narrows the count");
            Assert.That(elsewhere, Is.Zero);

            // The offline read itself: the records of a scope nothing is simulating are ordinary records.
            IReadOnlyList<PersistedEntityRecord> offline = null;
            _store.LoadWhere(r => r.ScopeKey == Key, r => offline = r);
            _store.Tick();
            Assert.That(offline, Is.Not.Null);
            Assert.That(offline.Count, Is.EqualTo(2), "IPersistenceStore.LoadWhere is the offline read; no scope was activated");
        }
    }
}
