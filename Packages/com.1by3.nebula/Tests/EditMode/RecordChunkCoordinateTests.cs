using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-366: a persisted record keeps its container's part id (<see cref="PersistedEntityRecord.PartId"/>), so a
    /// process that never named a scoped chunk can still place an entity saved in it. A scoped chunk's container id
    /// is a hash of the scope key and the coordinate, so without the part id only a process that named the chunk, or
    /// adopted it from a lease row, could say where the record is.
    /// <list type="bullet">
    /// <item>A crate saved by a real worker in a scoped chunk is placed, after the process is gone, by an empty grid
    /// from the record alone (<see cref="RuntimeGrid.TryCoordOf(PersistedEntityRecord, out Vector3Int)"/>,
    /// <see cref="ChunkKeys.TryCoordOf"/>), and the grid learns the id.</item>
    /// <item>A record from before the part id was recorded is placed by <see cref="RuntimeGrid.TryFindCoordNear"/>
    /// with a hint, and not without one.</item>
    /// <item>Every store format reads older records as "part unknown": the local store's file versions 1 and 2, and
    /// JSON without <c>partId</c>. The SQL migration is in the services suite.</item>
    /// </list>
    /// </summary>
    public sealed class RecordChunkCoordinateTests
    {
        private static readonly Vector3 Cell = new Vector3(64f, 64f, 64f);
        private const string Scope = "planet/grid-366";
        private static readonly Vector3Int Where = new Vector3Int(5, 0, -3);

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private LocalPersistenceStore _store;
        private NebulaPersistence _persistence;
        private string _tempDir;

        [TearDown]
        public void TearDown()
        {
            ShutDownWorker();
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            if (_tempDir != null && Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
            _tempDir = null;
        }

        private void ShutDownWorker()
        {
            _persistence?.Shutdown();
            _persistence = null;
            _store?.Dispose();
            _store = null;
            _mesh?.Dispose();
            _mesh = null;
            _plane?.Dispose();
            _plane = null;
        }

        private string TempFile()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "nebula-neb366-" + Guid.NewGuid().ToString("N"));
            return Path.Combine(_tempDir, "world.bin");
        }

        private static PersistedEntityRecord Loaded(LocalPersistenceStore store, string key)
        {
            PersistedEntityRecord result = null;
            store.Load(key, r => result = r);
            store.Tick();
            return result;
        }

        /// <summary>A scoped record as a build from before NEB-366 wrote it: everything but the part id.</summary>
        private static PersistedEntityRecord LegacyRecord(string scopeKey, string containerId) => new PersistedEntityRecord
        {
            Key = "crate-legacy",
            PrefabName = "Box",
            ScopeKey = scopeKey,
            ContainerId = containerId,
            LocalPosition = new Vector3(1, 2, 3),
            LocalRotation = Quaternion.identity,
            Epoch = 3,
            SavedBy = "w1",
            State = new byte[] { 9 },
        };

        // ------------------------------------------------------------------------------- a fresh process

        [Test]
        public void ARecordSavedInAScopedChunkIsPlacedByAFreshProcessFromTheRecordAlone()
        {
            string file = TempFile();

            // Process one: a real worker saves a crate that stands in chunk (5, 0, -3) of a scoped grid.
            _mesh = new ConformanceMesh(1);
            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            crate.AddComponent<PersistentEntity>();
            ushort cratePrefab = _mesh.RegisterPrefab(crate);
            var w = _mesh[0];
            _plane = new LocalControlPlane();
            _plane.Connect();
            AttachRegistered(w.Instance, _plane);
            _store = new LocalPersistenceStore(file);
            _store.Connect();
            _persistence = new NebulaPersistence(w.Instance, _mesh.Config, _store);
            typeof(NebulaWorker).GetProperty(nameof(NebulaWorker.Persistence)).SetValue(w.Instance, _persistence);

            var grid = new RuntimeGrid(Cell, planar: true, scopeKey: Scope);
            var instance = new InstanceContainerInfo { InstanceId = grid.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(Where) };
            var chunk = ContainerRegistry.RegisterRuntime(grid.IdOf(Where), grid.BoundsOf(Where), instance);
            ContainerRegistry.ApplyLease(chunk.ContainerId, w.Id, w.Index, 1);
            _plane.EnsureRuntimeContainer(chunk.ContainerId, ContainerRegistry.ToAbsolute(grid.BoundsOf(Where), grid.InstanceId), w.Id, instance);

            NetworkIdentity box = null;
            w.Act(() =>
            {
                box = NetworkPrefabs.Instantiate(cratePrefab, chunk.transform.position, Quaternion.identity, chunk.transform);
                w.Instance.Spawn(box, chunk);
            });
            Assume.That(box.Container, Is.SameAs(chunk));
            string key = box.Persistent.EnsureKey();
            ulong id = chunk.RuntimeId;
            string containerId = chunk.ContainerId;
            w.Act(() => _persistence.SaveNow(box));
            _store.Flush();

            // The process is gone: its worker, its registry and every grid that named the chunk.
            ShutDownWorker();
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();

            // Process two: the store's file and an empty grid of the same scope.
            _store = new LocalPersistenceStore(file);
            _store.Connect();
            var record = Loaded(_store, key);
            Assert.IsNotNull(record);
            Assert.AreEqual(Scope, record.ScopeKey);
            Assert.AreEqual(containerId, record.ContainerId);
            Assert.AreEqual(ChunkKeys.PartId(Where), record.PartId, "the save recorded the chunk's part id");

            Assert.IsTrue(ChunkKeys.TryCoordOf(record, out var pure), "pure C# placement, no grid needed");
            Assert.AreEqual(Where, pure);

            var fresh = new RuntimeGrid(Cell, planar: true, scopeKey: Scope);
            Assert.IsFalse(fresh.TryCoordOf(id, out _), "a scoped id this process never named cannot be unpacked");
            Assert.IsTrue(fresh.TryCoordOf(record, out var placed));
            Assert.AreEqual(Where, placed);
            Assert.IsTrue(fresh.TryCoordOf(id, out var learned), "placing the record taught the grid the id");
            Assert.AreEqual(Where, learned);

            // The orchestrator's wire format carries it too.
            var overTheWire = PersistedRecordJson.ParseOne(PersistedRecordJson.WriteOne(record));
            Assert.AreEqual(ChunkKeys.PartId(Where), overTheWire.PartId);
            Assert.IsTrue(ChunkKeys.TryCoordOf(overTheWire, out var remote));
            Assert.AreEqual(Where, remote);
        }

        /// <summary>Points <paramref name="worker"/> at <paramref name="plane"/> and marks it registered, as <c>ConformanceGridScopeLifecycleTests</c> does.</summary>
        private static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", flags).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", flags).SetValue(registration, plane.DocumentId ?? "");
        }

        // ------------------------------------------------------------------------------- placing a record

        [Test]
        public void APartIdThatDoesNotHashToTheContainerIdIsNotTrusted()
        {
            var record = LegacyRecord(Scope, ChunkKeys.ContainerId(Scope, Where));
            record.PartId = ChunkKeys.PartId(Where + Vector3Int.right);
            Assert.IsFalse(ChunkKeys.TryCoordOf(record, out _));
            var grid = new RuntimeGrid(Cell, planar: true, scopeKey: Scope);
            Assert.IsFalse(grid.TryCoordOf(record, out _));
            Assert.IsFalse(grid.TryCoordOf(ChunkKeys.RuntimeId(Scope, Where), out _), "a wrong part id teaches the grid nothing");

            // The same part id under another scope key names another container.
            record.PartId = ChunkKeys.PartId(Where);
            record.ScopeKey = "planet/other";
            Assert.IsFalse(ChunkKeys.TryCoordOf(record, out _));
            Assert.IsFalse(grid.TryCoordOf(record, out _), "a record of another scope is not this grid's");
        }

        [Test]
        public void ARecordOutsideARuntimeChunkIsNotPlaced()
        {
            var grid = new RuntimeGrid(Cell, planar: true, scopeKey: Scope);
            var carried = LegacyRecord(Scope, "");
            carried.CarrierKey = "ship-1";
            Assert.IsFalse(ChunkKeys.TryCoordOf(carried, out _));
            Assert.IsFalse(grid.TryCoordOf(carried, out _));

            var interior = LegacyRecord(Scope, ScopeKeys.ContainerId(Scope, "interior"));
            interior.PartId = "interior";
            Assert.IsFalse(ChunkKeys.TryCoordOf(interior, out _), "an instance's part is not a chunk");
            Assert.IsFalse(grid.TryCoordOf(interior, out _));

            Assert.IsFalse(ChunkKeys.TryCoordOf(LegacyRecord("", "hangar-a"), out _), "a static container");
            Assert.IsFalse(ChunkKeys.TryCoordOf(LegacyRecord("", ""), out _), "no container");
            Assert.IsFalse(ChunkKeys.TryCoordOf(null, out _));
            Assert.IsFalse(grid.TryCoordOf((PersistedEntityRecord)null, out _));
        }

        [Test]
        public void APublicWorldRecordIsPlacedWithOrWithoutAPartId()
        {
            var at = new Vector3Int(-7, 0, 12);
            var record = LegacyRecord("", ChunkKeys.ContainerId("", at));
            Assert.IsTrue(ChunkKeys.TryCoordOf(record, out var coord));
            Assert.AreEqual(at, coord);
            var grid = new RuntimeGrid(Cell, planar: true);
            Assert.IsTrue(grid.TryCoordOf(record, out coord));
            Assert.AreEqual(at, coord);

            record.PartId = ChunkKeys.PartId(at);
            Assert.IsTrue(grid.TryCoordOf(record, out coord));
            Assert.AreEqual(at, coord);
        }

        [Test]
        public void MovingARecordThroughItsLocationClearsThePartId()
        {
            var record = LegacyRecord(Scope, ChunkKeys.ContainerId(Scope, Where));
            record.PartId = ChunkKeys.PartId(Where);

            // The same container, a new pose: the part id still names it.
            record.Location = new EntityLocation(Scope, record.ContainerId, Vector3.one, Quaternion.identity);
            Assert.AreEqual(ChunkKeys.PartId(Where), record.PartId);

            record.Location = new EntityLocation(Scope, ChunkKeys.ContainerId(Scope, Vector3Int.zero), Vector3.one, Quaternion.identity);
            Assert.AreEqual("", record.PartId, "the part id named the old container");
            Assert.AreEqual(ChunkKeys.PartId(Where), Clone(Where).PartId, "Clone keeps it");
        }

        private static PersistedEntityRecord Clone(Vector3Int at)
        {
            var record = LegacyRecord(Scope, ChunkKeys.ContainerId(Scope, at));
            record.PartId = ChunkKeys.PartId(at);
            return record.Clone();
        }

        // ------------------------------------------------------------------------------- legacy records

        [Test]
        public void ALegacyRecordIsPlacedThroughAHintAndNotWithoutOne()
        {
            var record = LegacyRecord(Scope, ChunkKeys.ContainerId(Scope, Where));
            Assert.AreEqual("", record.PartId);
            var grid = new RuntimeGrid(Cell, planar: true, scopeKey: Scope);
            Assert.IsFalse(ChunkKeys.TryCoordOf(record, out _), "no part id, and a scoped id cannot be unpacked");
            Assert.IsFalse(grid.TryCoordOf(record, out _));

            Assert.IsTrue(ContainerRegistry.TryParseRuntimeId(record.ContainerId, out ulong id));
            Assert.IsFalse(grid.TryFindCoordNear(id, new Vector3Int(20, 0, 20), 3, out _), "a hint too far away finds nothing");
            Assert.IsFalse(grid.TryFindCoordNear(id, Where, -1, out _), "a negative radius finds nothing");
            Assert.IsFalse(grid.TryCoordOf(record, out _), "a failed search teaches the grid nothing");

            // An approximate position two cells off, with a stray height a planar grid ignores.
            Assert.IsTrue(grid.TryFindCoordNear(id, new Vector3Int(7, 4, -2), 2, out var found));
            Assert.AreEqual(Where, found);
            Assert.IsTrue(grid.TryCoordOf(record, out var placed), "the grid learned the id");
            Assert.AreEqual(Where, placed);
            Assert.IsTrue(grid.TryFindCoordNear(id, new Vector3Int(100, 0, 100), 0, out found), "a known id is answered from what the grid knows");
            Assert.AreEqual(Where, found);
        }

        [Test]
        public void AVolumetricGridSearchesACube()
        {
            var at = new Vector3Int(2, -3, 1);
            var grid = new RuntimeGrid(Cell, planar: false, scopeKey: Scope);
            ulong id = ChunkKeys.RuntimeId(Scope, at);
            Assert.IsFalse(grid.TryFindCoordNear(id, Vector3Int.zero, 2, out _), "y is three cells away");
            Assert.IsTrue(grid.TryFindCoordNear(id, Vector3Int.zero, 3, out var found));
            Assert.AreEqual(at, found);
        }

        [Test]
        public void ThePublicGridUnpacksAndChecksTheRadius()
        {
            var at = new Vector3Int(10, 0, -4);
            var grid = new RuntimeGrid(Cell, planar: true);
            ulong id = grid.IdOf(at);
            Assert.IsTrue(grid.TryFindCoordNear(id, new Vector3Int(9, 0, -3), 1, out var found));
            Assert.AreEqual(at, found);
            Assert.IsFalse(grid.TryFindCoordNear(id, Vector3Int.zero, 3, out _));
        }

        [Test]
        public void ASearchFindsEveryCellOfItsSquare()
        {
            // Each cell of a radius-2 square around the hint, the corners and the centre included, is found.
            var around = new Vector3Int(-1, 0, 4);
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                {
                    var at = around + new Vector3Int(dx, 0, dz);
                    var grid = new RuntimeGrid(Cell, planar: true, scopeKey: Scope);
                    Assert.IsTrue(grid.TryFindCoordNear(ChunkKeys.RuntimeId(Scope, at), around, 2, out var found), at.ToString());
                    Assert.AreEqual(at, found);
                }
        }

        // ------------------------------------------------------------------------------- store formats

        [Test]
        public void TheLocalStoreKeepsThePartIdThroughItsFile()
        {
            string file = TempFile();
            var record = LegacyRecord(Scope, ChunkKeys.ContainerId(Scope, Where));
            record.PartId = ChunkKeys.PartId(Where);
            _store = new LocalPersistenceStore(file);
            _store.Connect();
            _store.Save(record);
            _store.Dispose();
            _store = new LocalPersistenceStore(file);
            _store.Connect();
            Assert.AreEqual(ChunkKeys.PartId(Where), Loaded(_store, record.Key).PartId);
        }

        [TestCase((byte)1)]
        [TestCase((byte)2)]
        public void TheLocalStoreReadsAnOlderFileAsPartUnknown(byte version)
        {
            string file = TempFile();
            Directory.CreateDirectory(_tempDir);
            var record = LegacyRecord(version >= 2 ? Scope : "", ChunkKeys.ContainerId(version >= 2 ? Scope : "", Where));
            record.PartId = "c/1/2/3"; // not written by these versions
            File.WriteAllBytes(file, LocalPersistenceStore.EncodeFile(new[] { record }, version));

            _store = new LocalPersistenceStore(file);
            _store.Connect();
            var loaded = Loaded(_store, record.Key);
            Assert.IsNotNull(loaded, "the older file still loads");
            Assert.AreEqual("", loaded.PartId);
            Assert.AreEqual(record.ContainerId, loaded.ContainerId);
            Assert.AreEqual(version >= 2 ? Scope : "", loaded.ScopeKey);
            Assert.AreEqual(new byte[] { 9 }, loaded.State);
            Assert.AreEqual(3u, loaded.Epoch);

            // The next save writes the current version, part id included.
            loaded.PartId = ChunkKeys.PartId(Where);
            loaded.Epoch = 4;
            _store.Save(loaded);
            _store.Dispose();
            var bytes = File.ReadAllBytes(file);
            Assert.AreEqual(LocalPersistenceStore.FileVersion, bytes[4]);
            _store = new LocalPersistenceStore(file);
            _store.Connect();
            Assert.AreEqual(ChunkKeys.PartId(Where), Loaded(_store, record.Key).PartId);
        }

        [Test]
        public void JsonWithoutAPartIdReadsAsPartUnknown()
        {
            var record = LegacyRecord(Scope, ChunkKeys.ContainerId(Scope, Where));
            record.PartId = ChunkKeys.PartId(Where);
            string json = PersistedRecordJson.WriteOne(record);
            StringAssert.Contains("\"partId\":\"c/5/0/-3\"", json);

            string older = json.Replace(",\"partId\":\"c/5/0/-3\"", "");
            Assume.That(older, Does.Not.Contain("partId"));
            var parsed = PersistedRecordJson.ParseOne(older);
            Assert.AreEqual("", parsed.PartId);
            Assert.AreEqual(record.ContainerId, parsed.ContainerId);
        }
    }
}
