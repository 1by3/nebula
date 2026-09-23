using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 18, releasing a chunk (<c>docs/conformance-suite.md</c>, NEB-262; design
    /// <c>docs/dynamic-worlds.md</c>, "Retiring a chunk under a carrier"): <b>a persistent entity left in a chunk
    /// nobody wants is checkpointed and despawned with it, and comes back once when the chunk is leased again</b>;
    /// and a chunk is not released while another worker still simulates something filed under it.
    /// <para>
    /// The second rule closes the one path by which an entity could outlive its chunk's release. The owner only sees a
    /// ghost of an entity whose authority is still on another worker (a handover in flight, or a peer that has not
    /// connected). Releasing then empties the chunk without the entity, and the other worker moves the live entity into
    /// whatever box is nearest instead: no checkpoint, no despawn, and the chunk's next restore finds its old record.
    /// </para>
    /// <para>
    /// Tier B: one <b>real</b> <see cref="NebulaWorker"/> on the <see cref="ConformanceMesh"/>, a real
    /// <see cref="RuntimeGridAllocator"/> and <see cref="NebulaPersistence"/> over a <see cref="LocalPersistenceStore"/>,
    /// and a <see cref="LocalControlPlane"/> whose clock the test drives. A ghost is an entity whose authority flag is
    /// off here, exactly as the ghost band leaves it.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceChunkReleaseTests
    {
        private static readonly Vector3 Cell = new Vector3(64f, 64f, 64f);
        private const float RetireAfter = 30f;

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private LocalPersistenceStore _store;
        private NebulaPersistence _persistence;
        private DateTime _clock;
        private float _time;
        private ushort _beaconPrefab;

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(1);
            var beacon = new GameObject("beacon-prefab");
            beacon.AddComponent<NetworkIdentity>();
            beacon.AddComponent<PersistentEntity>();
            _beaconPrefab = _mesh.RegisterPrefab(beacon);

            _clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _time = 0f;
            _plane = new LocalControlPlane { Clock = () => _clock };
            _plane.Connect();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", flags).SetValue(W.Instance, _plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", flags).GetValue(W.Instance);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", flags).SetValue(registration, _plane.DocumentId ?? "");

            _store = new LocalPersistenceStore(); // memory backend
            _store.Connect();
            _mesh.Config.PersistenceRestoreGraceSeconds = 1f;
            _persistence = new NebulaPersistence(W.Instance, _mesh.Config, _store) { Now = () => _time };
            typeof(NebulaWorker).GetProperty(nameof(NebulaWorker.Persistence)).SetValue(W.Instance, _persistence);
        }

        [TearDown]
        public void TearDown()
        {
            _persistence?.Shutdown();
            _mesh.Dispose();
            _plane.Dispose();
            _store?.Dispose();
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
        }

        private Container Chunk(RuntimeGrid grid, Vector3Int coord)
        {
            var chunk = ContainerRegistry.RegisterRuntime(grid.IdOf(coord), grid.BoundsOf(coord));
            ContainerRegistry.ApplyLease(chunk.ContainerId, W.Id, W.Index, 1);
            _plane.EnsureRuntimeContainer(chunk.ContainerId, ContainerRegistry.ToAbsolute(grid.BoundsOf(coord), 0UL), W.Id, null);
            ContainerRegistry.NotifyLeasesChanged();
            return chunk;
        }

        private void Advance(float seconds)
        {
            _clock = _clock.AddSeconds(seconds);
            _time += seconds;
        }

        private void Tick(RuntimeGridAllocator allocator) => W.Act(() => allocator.Tick(_time));

        private bool Alive(NetworkIdentity e) => e != null && W.Find(e.NetId) == e;

        [Test]
        public void AChunkIsNotReleasedWhileAnotherWorkerSimulatesSomethingFiledInIt()
        {
            var grid = new RuntimeGrid(Cell, planar: true);
            var coord = new Vector3Int(1, 0, 0);
            var chunk = Chunk(grid, coord);
            var beacon = W.SpawnServerDriven(_beaconPrefab, chunk, grid.CenterOf(coord), Quaternion.identity);
            string key = beacon.Persistent.Key;
            var allocator = new RuntimeGridAllocator(W.Instance, grid) { TickIntervalSeconds = 0f, RetireAfterSeconds = RetireAfter };
            Tick(allocator);

            // The beacon is filed under the chunk, but its authority is still on another worker: only a ghost is here.
            beacon.HasAuthority = false;
            Advance(RetireAfter + 1f);
            Tick(allocator);
            Assert.That(_plane.FindLease(chunk.ContainerId), Is.Not.Null, "the chunk waits for the entity another worker is handing over");

            // The handover lands. Now nobody wants the chunk and everything in it is this worker's: it goes, and the
            // beacon with it, checkpointed.
            beacon.HasAuthority = true;
            Advance(1f);
            Tick(allocator);
            Assert.That(_plane.FindLease(chunk.ContainerId), Is.Null);
            Assert.That(Alive(beacon), Is.False, "the beacon did not outlive its chunk");
            PersistedEntityRecord record = null;
            _store.Load(key, r => record = r);
            _store.Tick();
            Assert.That(record, Is.Not.Null);
            Assert.That(record.ContainerId, Is.EqualTo(chunk.ContainerId), "it was saved in the chunk it was released with");

            // Leased again: the beacon comes back, once.
            ContainerRegistry.SyncRuntime(_plane.Leases);
            ContainerRegistry.NotifyLeasesChanged();
            var again = Chunk(grid, coord);
            int restored = 0;
            _persistence.ContainerRestored += (id, n) => { if (id == again.ContainerId) restored += n; };
            for (int i = 0; i < 4; i++)
            {
                Advance(1f);
                W.Act(() => _persistence.Update());
                _store.Tick();
            }
            Assert.That(restored, Is.EqualTo(1));
            var back = _persistence.Find(key);
            Assert.That(back, Is.Not.Null);
            Assert.That(back.Container, Is.SameAs(again));
        }
    }
}
