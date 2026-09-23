using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 18, across two workers (<c>docs/conformance-suite.md</c>, design
    /// <c>docs/dynamic-worlds.md</c>, "Retiring a chunk under a carrier", D5): the riders a worker owns aboard a
    /// carrier that another worker simulates follow the carrier out of the world when its owner empties the box it
    /// is in, saved aboard it, and are put down where the carrier stood when the carrier is despawned any other way.
    /// <para>
    /// Before this, <see cref="NebulaWorker.EmptyContainer"/> took along only the riders its own worker owned. A worker
    /// that held the carrier as a ghost heard an ordinary despawn and set its own riders down in the box around the
    /// carrier, where they outlived the ship in a box that was itself being unloaded.
    /// </para>
    /// <para>
    /// Tier B: two <b>real</b> <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/>. w1 owns the yard and
    /// the ship; the ship stands at the seam with w2's dock, so w1's own ghost band gives w2 a ghost of it. The ship's
    /// interior is pinned to w2, which is how a rider stays owned by a worker other than its carrier's. The riders
    /// are spawned on w2 into the ghost's box. Persistence is a real <see cref="NebulaPersistence"/> per worker over
    /// one shared <see cref="LocalPersistenceStore"/> (memory backend), as a mesh shares its store.
    /// </para>
    /// <para>
    /// One limit of the tier: the container registry is process-wide, so w2's ghost of the ship registers the
    /// ship's box and w1's copy loses the registration. Nothing is asserted about w1's copy of the box.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceGhostCarrierRidersTests
    {
        private ConformanceMesh _mesh;
        private LocalPersistenceStore _store;
        private Container _yard;
        private ushort _shipPrefab, _cratePrefab, _dronePrefab;

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            _yard = _mesh.AddStaticContainer("yard", Vector3.zero, new Vector3(64f, 40f, 64f));      // x in [-32, 32]
            var dock = _mesh.AddStaticContainer("dock", new Vector3(64f, 0f, 0f), new Vector3(64f, 40f, 64f)); // x in [32, 96]
            _mesh.SetOwner(_yard, W1);
            _mesh.SetOwner(dock, W2);

            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var hull = ship.AddComponent<Container>();
            hull.ContainerId = "hull";
            hull.Size = new Vector3(20f, 6f, 20f);
            hull.Center = new Vector3(0f, 2f, 0f); // the origin is well inside its own box, clear of the floor face
            ship.AddComponent<DynamicContainer>();
            ship.AddComponent<PersistentEntity>();
            _shipPrefab = _mesh.RegisterPrefab(ship);
            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            crate.AddComponent<PersistentEntity>();
            _cratePrefab = _mesh.RegisterPrefab(crate);
            var drone = new GameObject("drone-prefab");
            drone.AddComponent<NetworkIdentity>();
            _dronePrefab = _mesh.RegisterPrefab(drone);

            _store = new LocalPersistenceStore(); // memory backend, shared by both workers
            _store.Connect();
            foreach (var w in _mesh.Workers)
            {
                var persistence = new NebulaPersistence(w.Instance, _mesh.Config, _store);
                typeof(NebulaWorker).GetProperty(nameof(NebulaWorker.Persistence)).SetValue(w.Instance, persistence);
            }
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            _store?.Dispose();
            _store = null;
        }

        /// <summary>
        /// The ship on w1, 2 m from the seam with w2's dock, ghosted to w2 by w1's own ghost band, with its interior
        /// pinned to w2 and a persistent crate and a transient drone aboard that w2 owns.
        /// </summary>
        private (NetworkIdentity ship, NetworkIdentity ghost, NetworkIdentity crate, NetworkIdentity drone) ShipWithForeignRiders()
        {
            var ship = W1.SpawnServerDriven(_shipPrefab, _yard, new Vector3(30f, 0f, 0f), Quaternion.identity);
            Assume.That(ship.Container, Is.SameAs(_yard));
            W1.PublishTick(1);
            _mesh.Pump();
            var ghost = W2.Find(ship.NetId);
            Assume.That(ghost, Is.Not.Null, "w1's ghost band gave w2 a copy of the ship at the seam");
            Assume.That(ghost.HasAuthority, Is.False);
            Assume.That(ghost.Carried, Is.Not.Null);
            ContainerRegistry.ApplyLease(ghost.Carried.ContainerId, W2.Id, W2.Index, 1, LeaseState.Pinned);
            Assume.That(ghost.Carried.OwnerWorkerId, Is.EqualTo(W2.Id), "the interior is simulated by w2, not by the ship's owner");

            NetworkIdentity crate = null, drone = null;
            W2.Act(() =>
            {
                crate = NetworkPrefabs.Instantiate(_cratePrefab, ghost.transform.position + Vector3.up, Quaternion.identity, ghost.transform);
                W2.Instance.SpawnServerDriven(crate, ghost.Carried);
                drone = NetworkPrefabs.Instantiate(_dronePrefab, ghost.transform.position + Vector3.up + Vector3.right, Quaternion.identity, ghost.transform);
                W2.Instance.SpawnServerDriven(drone, ghost.Carried);
            });
            Assume.That(crate.Container, Is.SameAs(ghost.Carried));
            Assume.That(crate.HasAuthority && drone.HasAuthority, Is.True, "w2 owns the riders");
            return (ship, ghost, crate, drone);
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

        /// <summary>
        /// The owner empties the yard (a chunk retired, a scope part checkpointed): the ship is saved and despawned
        /// with its cargo, and the riders w2 owns aboard its ghost leave with it. None is set down in the yard, the
        /// persistent crate's record names the ship's own record, so it comes back with the ship wherever the ship
        /// is restored, and the transient drone is gone, as it would be on the ship's own worker.
        /// </summary>
        [Test]
        public void RidersOwnedHereLeaveWithAGhostCarrierWhoseBoxWasEmptiedAndAreSavedAboard()
        {
            var (ship, ghost, crate, drone) = ShipWithForeignRiders();
            ulong shipId = ship.NetId, crateId = crate.NetId, droneId = drone.NetId;
            string crateKey = crate.Persistent.EnsureKey();

            W1.Act(() => W1.Instance.EmptyContainer(_yard));
            string shipKey = ship.Persistent.Key;
            Assume.That(shipKey, Is.Not.Empty, "the ship was checkpointed as it left");
            _mesh.Pump();

            var ghostDespawns = _mesh.DeliveredOf(MsgId.GhostDespawn, W2.Id);
            Assert.That(ghostDespawns, Has.Count.EqualTo(1));
            var msg = ghostDespawns[0].Read(EntityDespawnMsg.Read);
            Assert.That(msg.NetId, Is.EqualTo(shipId));
            Assert.That(msg.TakesRiders, Is.True, "the owner says the ship left with its cargo");
            Assert.That(msg.CarrierKey, Is.EqualTo(shipKey));

            Assert.That(W2.Find(shipId), Is.Null, "the ghost is gone");
            Assert.That(W2.Find(crateId), Is.Null, "the crate left with the ship");
            Assert.That(W2.Find(droneId), Is.Null, "the drone left with the ship");
            Assert.That(_yard.Entities.Exists(e => e != null && (e.NetId == crateId || e.NetId == droneId)), Is.False,
                "no rider was set down in the box being emptied");

            var shipRecord = RecordOf(shipKey);
            Assert.That(shipRecord, Is.Not.Null);
            Assert.That(shipRecord.ContainerId, Is.EqualTo(_yard.ContainerId), "the ship comes back with the yard");
            var crateRecord = RecordOf(crateKey);
            Assert.That(crateRecord, Is.Not.Null, "the crate was checkpointed, not forgotten");
            Assert.That(crateRecord.CarrierKey, Is.EqualTo(shipKey), "saved aboard the ship's own record, not a key the ghost made up");
            Assert.That(crateRecord.SavedBy, Is.EqualTo(W2.Id));
        }

        /// <summary>
        /// Any other despawn of the carrier (destroyed for good, a player's vehicle leaving with its player, a
        /// scene object unloaded) sets its riders down where it stood, on its owner and on w2 alike, in the box the
        /// carrier was in. They survive it, and the next tick hands them to whoever owns that box.
        /// </summary>
        [Test]
        public void RidersOwnedHereArePutDownWhenAGhostCarrierIsDespawnedAnyOtherWay()
        {
            var (ship, ghost, crate, drone) = ShipWithForeignRiders();
            ulong shipId = ship.NetId;

            W1.Act(() => W1.Instance.Despawn(ship));
            _mesh.Pump();

            var msg = _mesh.DeliveredOf(MsgId.GhostDespawn, W2.Id)[0].Read(EntityDespawnMsg.Read);
            Assert.That(msg.TakesRiders, Is.False);
            Assert.That(W2.Find(shipId), Is.Null, "the ghost is gone");
            foreach (var rider in new[] { crate, drone })
            {
                Assert.That(W2.Find(rider.NetId), Is.SameAs(rider), $"{rider} outlives the ship");
                Assert.That(rider.HasAuthority, Is.True);
                Assert.That(rider.Container, Is.SameAs(_yard), $"{rider} is put down in the box the ship was in");
            }
        }

        /// <summary>
        /// The wire form: the two trailing fields are written only when the riders leave, so every other despawn,
        /// and every despawn a gateway or client sees, keeps its bytes (protocol 18).
        /// </summary>
        [Test]
        public void TheRidersFlagIsATrailingFieldWrittenOnlyWhenSet()
        {
            var w = new NetworkWriter(64);
            new EntityDespawnMsg { NetId = 9, Epoch = 3 }.Write(w, MsgId.GhostDespawn);
            Assert.That(w.Length, Is.EqualTo(1 + 8 + 4 + 2), "unchanged when the riders stay");
            var plain = ReadBack(w);
            Assert.That(plain.TakesRiders, Is.False);
            Assert.That(plain.CarrierKey, Is.Empty);

            w.Reset();
            new EntityDespawnMsg { NetId = 9, Epoch = 3, TakesRiders = true, CarrierKey = "ship:abc" }.Write(w, MsgId.GhostDespawn);
            var back = ReadBack(w);
            Assert.That(back.NetId, Is.EqualTo(9UL));
            Assert.That(back.Epoch, Is.EqualTo(3u));
            Assert.That(back.TakesRiders, Is.True);
            Assert.That(back.CarrierKey, Is.EqualTo("ship:abc"));
        }

        private static EntityDespawnMsg ReadBack(NetworkWriter w)
        {
            var r = new NetworkReader(w.ToArray());
            Assert.That((MsgId)r.ReadByte(), Is.EqualTo(MsgId.GhostDespawn));
            return EntityDespawnMsg.Read(r);
        }
    }
}
