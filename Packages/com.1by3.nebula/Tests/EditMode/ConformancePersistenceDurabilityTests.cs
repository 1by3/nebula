using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Scenario 10 (docs/conformance-suite.md, NEB-224): after a worker is killed, the change it could not save is
    /// bounded by the durability window documented in docs/persistence-durability.md, not by whatever the
    /// scheduler happens to do that day.
    /// <para>
    /// Tier C: a real <see cref="NebulaPersistence"/> checkpointing into a real <see cref="LocalPersistenceStore"/>
    /// (in-memory backend, so the store itself adds no write latency - see the doc's D4 for the file/remote terms),
    /// against a bare <see cref="NebulaWorker"/> that is never actually started (no transport, no control plane):
    /// this exercises the production scheduler (<see cref="NebulaPersistence.Update"/>, its per-frame save budget
    /// and its min-save throttle) rather than a re-implementation of it. <see cref="NebulaPersistence.Now"/> is the
    /// clock seam that makes the scheduler's own timers deterministic instead of waiting on
    /// <c>Time.unscaledTime</c>.
    /// </para>
    /// </summary>
    public sealed class ConformancePersistenceDurabilityTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private NebulaPersistence _persistence;
        private LocalPersistenceStore _store;
        private NebulaConfig _config;

        [SetUp]
        public void SetUp()
        {
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
        private static List<PersistentEntity> TrackedOf(NebulaPersistence persistence)
        {
            var field = typeof(NebulaPersistence).GetField("_tracked", BindingFlags.NonPublic | BindingFlags.Instance);
            return (List<PersistentEntity>)field.GetValue(persistence);
        }

        [Test]
        [Category("Conformance")] // scenario 10: loss after a worker kill stays within the documented durability window (docs/persistence-durability.md)
        public void LossAfterAWorkerKillNeverExceedsTheDocumentedDurabilityWindow()
        {
            const int entityCount = 200; // > MaxSavesPerFrame, so the per-frame budget actually has to spread saves across frames
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            _config.PersistenceCheckpointSeconds = 5f;
            _store = new LocalPersistenceStore(); // memory backend: D4 (store write latency) is 0 for this scenario
            _store.Connect();

            var host = NewObject("worker");
            var worker = host.AddComponent<NebulaWorker>();
            _persistence = new NebulaPersistence(worker, _config, _store);
            float clock = 0f;
            _persistence.Now = () => clock;

            var identities = new List<NetworkIdentity>(entityCount);
            var tracked = TrackedOf(_persistence);
            for (int i = 0; i < entityCount; i++)
            {
                var go = NewObject("e" + i);
                var identity = go.AddComponent<NetworkIdentity>();
                go.AddComponent<PersistentEntity>();
                identity.Initialize();
                identity.HasAuthority = true;
                identity.IsSpawned = true;
                identities.Add(identity);
                tracked.Add(identity.Persistent);
            }

            int framesToDrainAll = (int)Math.Ceiling(entityCount / (double)NebulaPersistence.MaxSavesPerFrame);

            // t=0: everyone gets its first-ever checkpoint (IsDue's "never saved" branch), establishing a real
            // LastSavedAt baseline. Several frames because the per-frame budget caps how many land on any one call.
            for (int i = 0; i < framesToDrainAll; i++) _persistence.Update();
            Assert.AreEqual(entityCount, _store.KnownCount, "the initial backlog must be fully drained before the scenario starts");
            foreach (var identity in identities) Assert.AreEqual(1UL, VersionOf(identity.Persistent.Key), "baseline: every entity starts at version 1");

            // Every entity changes at the same instant: the worst case for both the min-save throttle (D2) and the
            // per-frame save budget (D3) in docs/persistence-durability.md.
            foreach (var identity in identities) identity.Persistent.MarkDirty();

            // Before the min-save throttle's window closes, none of the changes may have reached the store yet:
            // this is what makes MinSaveIntervalSeconds a real term in the bound rather than a formality.
            clock = NebulaPersistence.MinSaveIntervalSeconds * 0.5f;
            _persistence.Update();
            foreach (var identity in identities) Assert.AreEqual(1UL, VersionOf(identity.Persistent.Key), "the min-save throttle must not be bypassed by a dirty flag");

            // The throttle's window closes; the scheduler now needs framesToDrainAll frames to reach everyone,
            // because of the per-frame save budget (D3). One frame is not enough when entityCount > MaxSavesPerFrame.
            clock = NebulaPersistence.MinSaveIntervalSeconds;
            _persistence.Update();
            int savedAfterOneFrame = CountAtVersion(identities, 2UL);
            Assert.AreEqual(NebulaPersistence.MaxSavesPerFrame, savedAfterOneFrame,
                "one frame saves exactly one budget's worth; the rest wait for the next frame(s) (D3)");

            // Simulate the worker being killed after exactly the time the documented bound allows: the min-save
            // wait, plus the remaining frames the per-frame budget needs. No more Update() calls after this - the
            // process is gone, and whatever is not in the store now is lost for good.
            for (int i = 1; i < framesToDrainAll; i++) _persistence.Update();

            int lost = CountAtVersion(identities, 1UL); // still holding the pre-change value: the dirty write never landed
            int documentedBoundFrames = framesToDrainAll; // the exact D3 term used above, not a re-derivation
            Assert.AreEqual(0, lost,
                $"loss must be 0 within the documented bound (min-save wait + {documentedBoundFrames} frame(s) of the " +
                $"{NebulaPersistence.MaxSavesPerFrame}-per-frame budget); a change that a real worker still held at kill time was not found in the store");
        }

        /// <summary>The version the store actually holds for <paramref name="key"/> right now (0 when there is no record).</summary>
        private ulong VersionOf(string key)
        {
            ulong result = 0;
            _store.Load(key, r => result = r?.Version ?? 0);
            _store.Tick();
            return result;
        }

        private int CountAtVersion(List<NetworkIdentity> identities, ulong version)
        {
            int count = 0;
            foreach (var identity in identities) if (VersionOf(identity.Persistent.Key) == version) count++;
            return count;
        }
    }
}
