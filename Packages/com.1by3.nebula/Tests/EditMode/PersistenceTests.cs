using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The persistence layer without a worker: the store's write rules, the state blob's tolerance of schema
    /// changes, the key that travels with an entity, and the rule that decides what is safe to bring back.
    /// </summary>
    public class PersistenceTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private string _tempDir;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
            if (_tempDir != null && Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
            _tempDir = null;
        }

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        private static PersistedEntityRecord Record(string key, uint epoch = 1, string container = "c1", string carrier = "")
        {
            return new PersistedEntityRecord
            {
                Key = key,
                PrefabName = "Box",
                PrefabId = 0,
                ContainerId = container,
                CarrierKey = carrier,
                Epoch = epoch,
                LocalPosition = new Vector3(1, 2, 3),
                LocalRotation = Quaternion.Euler(0, 90, 0),
                Name = key,
                SavedBy = "w1",
                State = new byte[] { 1, 2, 3 },
            };
        }

        private static PersistedEntityRecord Loaded(LocalPersistenceStore store, string key)
        {
            PersistedEntityRecord result = null;
            store.Load(key, r => result = r);
            store.Tick();
            return result;
        }

        private static IReadOnlyList<PersistedEntityRecord> LoadedContainer(LocalPersistenceStore store, string containerId)
        {
            IReadOnlyList<PersistedEntityRecord> result = null;
            store.LoadContainers(new[] { containerId }, r => result = r[containerId]);
            store.Tick();
            return result;
        }

        // ---------------------------------------------------------------------------------------- the store

        [Test]
        public void LocalStoreSavesLoadsAndAnswersContainerAndCarrierQueries()
        {
            var store = new LocalPersistenceStore();
            store.Connect();
            store.Save(Record("crate-1"));
            store.Save(Record("crate-2"));
            store.Save(Record("cargo-1", 1, "", "ship-1"));
            Assert.AreEqual(3, store.KnownCount);
            Assert.AreEqual("memory", store.Backend);

            var one = Loaded(store, "crate-1");
            Assert.IsNotNull(one);
            Assert.AreEqual(new Vector3(1, 2, 3), one.LocalPosition);
            Assert.AreEqual(1UL, one.Version);
            Assert.IsNull(Loaded(store, "nobody"));

            var inContainer = LoadedContainer(store, "c1");
            Assert.AreEqual(2, inContainer.Count, "the carried record belongs to its carrier, not to a container");

            IReadOnlyList<PersistedEntityRecord> carried = null;
            store.LoadCarried(new[] { "ship-1" }, r => carried = r["ship-1"]);
            store.Tick();
            Assert.AreEqual(1, carried.Count);
            Assert.AreEqual("cargo-1", carried[0].Key);

            store.Delete("crate-2");
            Assert.IsNull(Loaded(store, "crate-2"));
            store.Clear();
            Assert.AreEqual(0, store.KnownCount);
            store.Dispose();
        }

        [Test]
        public void LocalStoreRejectsAnOlderEpochAndBumpsTheVersionOnEveryAcceptedSave()
        {
            var store = new LocalPersistenceStore();
            store.Connect();
            store.Save(Record("crate-1", 5));
            Assert.AreEqual(1UL, Loaded(store, "crate-1").Version);

            // The same epoch is the same owner saving again.
            var again = Record("crate-1", 5);
            again.Name = "second";
            store.Save(again);
            var loaded = Loaded(store, "crate-1");
            Assert.AreEqual(2UL, loaded.Version);
            Assert.AreEqual("second", loaded.Name);

            // A newer epoch (the entity was handed over) always wins.
            var newer = Record("crate-1", 6);
            newer.Name = "third";
            store.Save(newer);
            Assert.AreEqual("third", Loaded(store, "crate-1").Name);
            Assert.AreEqual(3UL, Loaded(store, "crate-1").Version);

            // A worker that lost authority must not overwrite what its successor wrote.
            var stale = Record("crate-1", 5);
            stale.Name = "stale";
            store.Save(stale);
            loaded = Loaded(store, "crate-1");
            Assert.AreEqual("third", loaded.Name);
            Assert.AreEqual(3UL, loaded.Version);
        }

        [Test]
        public void LocalStoreRoundTripsThroughItsFile()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "nebula-persist-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(_tempDir, "world.bin");

            var store = new LocalPersistenceStore(file);
            store.Connect();
            Assert.AreEqual("local", store.Backend);
            store.Save(Record("crate-1", 4));
            store.Save(Record("cargo-1", 2, "", "ship-1"));
            store.Dispose();
            Assert.IsTrue(File.Exists(file));

            var reopened = new LocalPersistenceStore(file);
            reopened.Connect();
            Assert.AreEqual(2, reopened.KnownCount);
            var crate = Loaded(reopened, "crate-1");
            Assert.AreEqual(4U, crate.Epoch);
            Assert.AreEqual("Box", crate.PrefabName);
            Assert.AreEqual("c1", crate.ContainerId);
            Assert.AreEqual(new byte[] { 1, 2, 3 }, crate.State);
            Assert.AreEqual(new Vector3(1, 2, 3), crate.LocalPosition);
            Assert.AreEqual("ship-1", Loaded(reopened, "cargo-1").CarrierKey);
            reopened.Dispose();
        }

        [Test]
        public void LocalStoreGroupsBarriersAndOnlyAnswersAfterTheSnapshotIsDurable()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "nebula-persist-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(_tempDir, "world.bin");
            var store = new LocalPersistenceStore(file) { WriteBarrierIntervalSeconds = 0.02f };
            store.Connect();
            bool first = false, second = false;
            store.Save(Record("crate-1"));
            store.WhenWritten(() => first = true);
            store.Save(Record("crate-2"));
            store.WhenWritten(() => second = true);

            Assert.IsFalse(first || second, "a barrier must not answer from memory");
            var timeout = Stopwatch.StartNew();
            while (!first || !second)
            {
                store.Tick();
                if (timeout.Elapsed.TotalSeconds > 3) Assert.Fail("barriers did not finish");
                Thread.Sleep(1);
            }
            Assert.AreEqual(1, store.FileWriteCount, "barriers in one group share a full-file rewrite");

            var reopened = new LocalPersistenceStore(file);
            reopened.Connect();
            Assert.IsNotNull(Loaded(reopened, "crate-1"));
            Assert.IsNotNull(Loaded(reopened, "crate-2"));
            reopened.Dispose();
            store.Dispose();
        }

        [Test]
        public void LocalStoreDoesNotAnswerABarrierWhenTheFileWriteFails()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "nebula-persist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            string unwritableFile = Path.Combine(_tempDir, "is-a-directory");
            Directory.CreateDirectory(unwritableFile);
            var store = new LocalPersistenceStore(unwritableFile) { WriteBarrierIntervalSeconds = 0f };
            store.Connect();
            store.Save(Record("crate-1"));
            bool answered = false;
            store.WhenWritten(() => answered = true);
            store.Tick();
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed.TotalSeconds < 0.2)
            {
                store.Tick();
                Thread.Sleep(1);
            }
            Assert.IsFalse(answered, "a failed disk write cannot satisfy a durability barrier");
            Assert.AreEqual(0, store.FileWriteCount);
            store.Dispose();
        }

        // ---------------------------------------------------------------------------------------- the state blob

        private sealed class Chest : NetworkBehaviour
        {
            [Persist] public NetworkVariable<int> Coins = new NetworkVariable<int>(3);
            public NetworkVariable<int> Sparkle = new NetworkVariable<int>(7);
            public ushort Lock;
            public bool Greedy;
            public bool ReadRan;

            public override void WritePersistentState(NetworkWriter writer) => writer.WriteUShort(Lock);

            public override void ReadPersistentState(NetworkReader reader)
            {
                if (Greedy) reader.ReadULong(); // more than the chunk holds: throws inside our own bytes
                Lock = reader.ReadUShort();
                ReadRan = true;
            }
        }

        private sealed class Banner : NetworkBehaviour
        {
            [Persist] public NetworkVariable<string> Text = new NetworkVariable<string>("none");
        }

        private sealed class Quiet : NetworkBehaviour
        {
            public NetworkVariable<float> Wobble = new NetworkVariable<float>(0.5f);
        }

        private NetworkIdentity MakeChest(string name, bool withBanner)
        {
            var go = NewObject(name);
            var identity = go.AddComponent<NetworkIdentity>();
            go.AddComponent<Chest>();
            if (withBanner) go.AddComponent<Banner>();
            go.AddComponent<Quiet>();
            identity.Initialize();
            identity.HasAuthority = true;
            return identity;
        }

        [Test]
        public void PersistedVariablesAreNamedAndRoundTripWhileUnpersistedOnesAreLeftAlone()
        {
            var a = MakeChest("a", true);
            var chestA = a.GetComponent<Chest>();
            Assert.AreEqual("Chest.Coins", chestA.Coins.Name);
            Assert.IsTrue(chestA.Coins.Persist);
            Assert.IsFalse(chestA.Sparkle.Persist, "a NetworkVariable without [Persist] is replicated but not saved");

            chestA.Coins.Value = 41;
            chestA.Sparkle.Value = 99;
            chestA.Lock = 1234;
            a.GetComponent<Banner>().Text.Value = "welcome";
            Assert.IsTrue(chestA.Coins.PersistDirty);
            Assert.IsTrue(PersistentStateCodec.HasDirtyVars(a));

            var blob = PersistentStateCodec.Write(a);
            Assert.IsFalse(PersistentStateCodec.HasDirtyVars(a), "writing the blob captures what changed");

            var b = MakeChest("b", true);
            PersistentStateCodec.Read(blob, b);
            var chestB = b.GetComponent<Chest>();
            Assert.AreEqual(41, chestB.Coins.Value);
            Assert.AreEqual(7, chestB.Sparkle.Value, "an unpersisted variable keeps its own value");
            Assert.AreEqual(1234, chestB.Lock, "the behaviour's own chunk round-trips");
            Assert.IsTrue(chestB.ReadRan);
            Assert.AreEqual("welcome", b.GetComponent<Banner>().Text.Value);
        }

        [Test]
        public void UnknownEntriesAreSkippedAndMissingOnesKeepTheirDefaults()
        {
            var rich = MakeChest("rich", true);
            rich.GetComponent<Chest>().Coins.Value = 12;
            rich.GetComponent<Banner>().Text.Value = "gone in the next build";
            var blob = PersistentStateCodec.Write(rich);

            // A build where the banner no longer exists: its entries have no home and are skipped.
            var poor = MakeChest("poor", false);
            PersistentStateCodec.Read(blob, poor);
            Assert.AreEqual(12, poor.GetComponent<Chest>().Coins.Value);

            // A build that gained a banner since the save: it keeps its default.
            var blobWithoutBanner = PersistentStateCodec.Write(poor);
            var richer = MakeChest("richer", true);
            PersistentStateCodec.Read(blobWithoutBanner, richer);
            Assert.AreEqual(12, richer.GetComponent<Chest>().Coins.Value);
            Assert.AreEqual("none", richer.GetComponent<Banner>().Text.Value);
        }

        [Test]
        public void ARecordFromAContainerThatIsNotHereRestoresStateButKeepsALiveEntityWhereItIs()
        {
            // A returning player's pawn: spawned first, then given its record. The chunk it was saved in is not
            // loaded, and its local position means nothing without that chunk.
            var store = new LocalPersistenceStore();
            store.Connect();
            var persistence = new NebulaPersistence(NewObject("worker").AddComponent<NebulaWorker>(), ScriptableObject.CreateInstance<NebulaConfig>(), store);
            var source = MakeChest("saved", true);
            source.GetComponent<Chest>().Coins.Value = 41;
            var record = Record("player:someone", 1, ContainerRegistry.RuntimeContainerId(987654321UL));
            record.State = PersistentStateCodec.Write(source);

            var pawn = MakeChest("pawn", true);
            pawn.IsSpawned = true;
            pawn.transform.position = new Vector3(10f, 0f, 20f);
            persistence.Apply(record, pawn);

            Assert.AreEqual(41, pawn.GetComponent<Chest>().Coins.Value, "the state still comes back");
            Assert.AreEqual(new Vector3(10f, 0f, 20f), pawn.transform.position, "the pose does not");
            Assert.IsNull(pawn.Container);

            // Not yet spawned (the restore path) the record's pose is still applied as before.
            var restored = MakeChest("restored", true);
            persistence.Apply(record, restored);
            Assert.AreEqual(new Vector3(1, 2, 3), restored.transform.position);
            persistence.Shutdown();
            store.Dispose();
        }

        [Test]
        public void ASingleEntryCanBeReadOutOfABlobWithoutAnEntity()
        {
            var a = MakeChest("a", true);
            a.GetComponent<Chest>().Lock = 4242;
            a.GetComponent<Chest>().Coins.Value = 19;
            a.GetComponent<Banner>().Text.Value = "hello";
            var blob = PersistentStateCodec.Write(a);

            // What a game does with a record it loaded for an entity that is not spawned here.
            Assert.IsTrue(PersistentStateCodec.TryReadBehaviourState(blob, "Chest", out var chest));
            Assert.AreEqual(4242, new NetworkReader(chest).ReadUShort());
            Assert.IsTrue(PersistentStateCodec.TryReadEntry(blob, "Banner.Text", out var banner));
            Assert.AreEqual("hello", new NetworkReader(banner).ReadString());
            Assert.IsTrue(PersistentStateCodec.TryReadEntry(blob, "Chest.Coins", out var coins));
            Assert.AreEqual(19, new NetworkReader(coins).ReadInt());

            Assert.IsFalse(PersistentStateCodec.TryReadEntry(blob, "Chest.Sparkle", out _), "an unpersisted variable is not in the blob");
            Assert.IsFalse(PersistentStateCodec.TryReadEntry(blob, "Nothing#state", out _));
            Assert.IsFalse(PersistentStateCodec.TryReadEntry(null, "Chest#state", out _));
            Assert.IsFalse(PersistentStateCodec.TryReadEntry(System.Array.Empty<byte>(), "Chest#state", out _));

            var newer = (byte[])blob.Clone();
            newer[0] = PersistentStateCodec.Version + 1;
            Assert.IsFalse(PersistentStateCodec.TryReadEntry(newer, "Chest#state", out _), "a blob from a newer build is not parsed");
        }

        [Test]
        public void ABehaviourThatReadsPastItsChunkIsIsolated()
        {
            var a = MakeChest("a", true);
            a.GetComponent<Chest>().Lock = 7;
            a.GetComponent<Banner>().Text.Value = "intact";
            var blob = PersistentStateCodec.Write(a);

            var b = MakeChest("b", true);
            b.GetComponent<Chest>().Greedy = true;
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("ReadPersistentState on Chest"));
            PersistentStateCodec.Read(blob, b);
            Assert.IsFalse(b.GetComponent<Chest>().ReadRan, "the greedy read failed inside its own chunk");
            Assert.AreEqual("intact", b.GetComponent<Banner>().Text.Value, "the entries after it still applied");
        }

        // ---------------------------------------------------------------------------------------- the key

        [Test]
        public void ThePersistentEntityIsFoundOnTheIdentityAndGeneratesAKeyWhenItHasNone()
        {
            var go = NewObject("Crate");
            var identity = go.AddComponent<NetworkIdentity>();
            var pe = go.AddComponent<PersistentEntity>();
            identity.Initialize();
            Assert.AreSame(pe, identity.Persistent);
            Assert.AreEqual("", pe.Key);

            string key = pe.EnsureKey();
            StringAssert.StartsWith("Crate:", key);
            Assert.AreEqual(key, pe.EnsureKey(), "the generated key is stable");

            var sceneGo = NewObject("Door");
            var sceneIdentity = sceneGo.AddComponent<NetworkIdentity>();
            sceneIdentity.SceneId = 77;
            sceneGo.AddComponent<PersistentEntity>();
            sceneIdentity.Initialize();
            Assert.AreEqual("scene:77", sceneIdentity.Persistent.EnsureKey());
        }

        [Test]
        [Category("Conformance")] // scenario 8: a persistent entity's identity is part of its handover state (docs/conformance-suite.md)
        public void TheKeyTravelsWithTheHandoverSoTheNextWorkerUpdatesTheSameRecord()
        {
            var a = NewObject("Crate");
            var ia = a.AddComponent<NetworkIdentity>();
            var pa = a.AddComponent<PersistentEntity>();
            ia.Initialize();
            pa.Key = "crate-42";
            pa.HasBeenSaved = true;
            pa.LastSavedVersion = 9;

            var writer = new NetworkWriter();
            ia.WriteHandoverState(writer);

            var b = NewObject("Crate");
            var ib = b.AddComponent<NetworkIdentity>();
            b.AddComponent<PersistentEntity>();
            ib.Initialize();
            ib.ReadHandoverState(new NetworkReader(writer.ToSegment()));
            Assert.AreEqual("crate-42", ib.Persistent.Key);
            Assert.IsTrue(ib.Persistent.HasBeenSaved);
            Assert.AreEqual(9UL, ib.Persistent.LastSavedVersion);

            // Once saved, the key is fixed: the record in the store is named by it.
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("already saved"));
            ib.Persistent.Key = "something-else";
            Assert.AreEqual("crate-42", ib.Persistent.Key);
        }

        [Test]
        public void AssigningAPersistedVariableMarksTheEntityForACheckpoint()
        {
            var go = NewObject("Chest");
            var identity = go.AddComponent<NetworkIdentity>();
            var chest = go.AddComponent<Chest>();
            var pe = go.AddComponent<PersistentEntity>();
            identity.Initialize();
            identity.HasAuthority = true;
            Assert.IsFalse(pe.IsDirty);

            chest.Sparkle.Value = 5;
            Assert.IsFalse(pe.IsDirty, "a variable that is not persisted does not schedule a save");

            chest.Coins.Value = 5;
            Assert.IsTrue(pe.IsDirty);
        }

        // ---------------------------------------------------------------------------------------- restore planning

        [Test]
        public void RestorePlanningNeverBringsBackWhatIsAliveOrOwnedAndWaitsOutALiveSaver()
        {
            var record = Record("crate-1");
            Assert.AreEqual(NebulaPersistence.RestorePlan.Restore,
                NebulaPersistence.Plan(record, aliveHere: false, saverAlive: false, recordAgeSeconds: 60, checkpointSeconds: 5f));

            Assert.AreEqual(NebulaPersistence.RestorePlan.SkipAlive,
                NebulaPersistence.Plan(record, aliveHere: true, saverAlive: false, recordAgeSeconds: 60, checkpointSeconds: 5f));

            var owned = Record("player-1");
            owned.Owned = true;
            Assert.AreEqual(NebulaPersistence.RestorePlan.SkipOwned,
                NebulaPersistence.Plan(owned, aliveHere: false, saverAlive: false, recordAgeSeconds: 60, checkpointSeconds: 5f),
                "there is no client to own the result; the game restores these on rejoin");

            var carried = Record("cargo-1", 1, "", "ship-1");
            Assert.AreEqual(NebulaPersistence.RestorePlan.SkipCarried,
                NebulaPersistence.Plan(carried, aliveHere: false, saverAlive: false, recordAgeSeconds: 60, checkpointSeconds: 5f),
                "cargo comes back with its ship, not with a static container");

            // A fresh record whose saver is still running: it may yet arrive by handover.
            Assert.AreEqual(NebulaPersistence.RestorePlan.Wait,
                NebulaPersistence.Plan(record, aliveHere: false, saverAlive: true, recordAgeSeconds: 2, checkpointSeconds: 5f));
            Assert.AreEqual(NebulaPersistence.RestorePlan.Restore,
                NebulaPersistence.Plan(record, aliveHere: false, saverAlive: true, recordAgeSeconds: 30, checkpointSeconds: 5f),
                "a saver that stopped checkpointing it is not coming");
        }
    }
}
