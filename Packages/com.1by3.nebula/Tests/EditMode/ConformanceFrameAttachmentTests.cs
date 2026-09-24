using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 25 (<c>docs/frame-bodies.md</c> §2): attaching an entity to a container with
    /// <see cref="FrameAttachment"/>. An attached entity's container is pinned, so the worker's tick never re-resolves
    /// it from its position, but the owner check still hands it to whichever worker owns the container. Its body is
    /// held kinematic at the attached pose through a flight, a handover and a restore, a late joiner hears that it is
    /// attached, it detaches gently, and it detaches by itself when its container goes.
    /// <para>
    /// Tier B: real <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/>, one whole tick at a time, with
    /// Editor preview scenes for physics frames and a real <see cref="NebulaPersistence"/> over a
    /// <see cref="LocalPersistenceStore"/>. The container registry is process-wide, so of two workers' copies of one
    /// carrier only the last registered owns the box and its frame; scenarios that hand a ship over assert only on the
    /// receiving worker's copies.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFrameAttachmentTests
    {
        private const float Dt = NetworkTime.TickInterval;

        private ConformanceMesh _mesh;
        private LocalPersistenceStore _store;
        private PhysicsMaterial _material;
        private Container _west, _east, _space;
        private ushort _propPrefab, _shipPrefab, _crate, _looseCrate;

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _material = new PhysicsMaterial("crate") { staticFriction = 0.6f, dynamicFriction = 0.6f, bounciness = 0f };
        }

        [TearDown]
        public void TearDown()
        {
            NebulaRuntime.RpcSink = null;
            _mesh?.Dispose();
            _mesh = null;
            _store?.Dispose();
            _store = null;
            if (_material != null) Object.DestroyImmediate(_material);
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            Assert.AreEqual(0, PhysicsFrames.All.Count, "every frame was released with its container");
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ fixtures

        /// <summary>Two workers; two chunks side by side at x = 0, west leased to w1 and east to w2.</summary>
        private void TwoChunks()
        {
            _mesh = new ConformanceMesh(2);
            _mesh.Config.HandoverHysteresis = 0.5f;
            _west = _mesh.AddStaticContainer("west", new Vector3(-32f, 0f, 0f), new Vector3(64f, 40f, 64f));
            _east = _mesh.AddStaticContainer("east", new Vector3(32f, 0f, 0f), new Vector3(64f, 40f, 64f));
            _mesh.SetOwner(_west, W1);
            _mesh.SetOwner(_east, W2);
            var prop = new GameObject("prop-prefab");
            prop.AddComponent<NetworkIdentity>();
            _propPrefab = _mesh.RegisterPrefab(prop);
        }

        /// <summary>An entity with no body at all that can be attached: a fixture, a sign, a turret mount.</summary>
        private ushort FixturePrefab()
        {
            var fixture = new GameObject("fixture-prefab");
            fixture.AddComponent<NetworkIdentity>();
            fixture.AddComponent<FrameAttachment>();
            return _mesh.RegisterPrefab(fixture);
        }

        /// <summary>A 1 m crate that can be attached: the loose-crate prefab of scenario 24 plus a <see cref="FrameAttachment"/>.</summary>
        private GameObject AttachablePrefab(string name, bool persistent = false)
        {
            var crate = ConformanceFrameBodiesTests.CratePrefab(name, 1.0f, 60f, _material, persistent);
            crate.AddComponent<FrameAttachment>();
            return crate;
        }

        /// <summary>One or two workers, a space big enough to fly in, leased to w1, and the closed-hold ship of scenario 24.</summary>
        private void Space(int workers)
        {
            _mesh = new ConformanceMesh(workers);
            _space = _mesh.AddStaticContainer("space", new Vector3(0f, -10000f, 0f), new Vector3(20000f, 20000f, 20000f));
            _mesh.SetOwner(_space, W1);
            _shipPrefab = _mesh.RegisterPrefab(ConformanceFrameBodiesTests.HoldPrefab());
            _crate = _mesh.RegisterPrefab(AttachablePrefab("attachable-crate"));
            _looseCrate = _mesh.RegisterPrefab(ConformanceFrameBodiesTests.CratePrefab("loose-crate", 0.5f, 15f, _material));
        }

        private static NetworkIdentity SpawnIn(ConformanceMesh.Worker w, ushort prefab, Container box, Vector3 local, Quaternion rotation = default) =>
            ConformanceFrameBodiesTests.SpawnIn(w, prefab, box, local, rotation);

        private static void Ticks(ConformanceMesh.Worker w, ref uint tick, int count)
        {
            for (int i = 0; i < count; i++) w.Tick(tick++);
        }

        // ------------------------------------------------------------------------------------ the pin (D8)

        [Test]
        public void APinnedEntityIsNotResolvedIntoTheNeighbouringChunkButFollowsARedeal()
        {
            TwoChunks();
            var pinned = W1.SpawnServerDriven(_propPrefab, _west, new Vector3(-1f, 1f, 0f), Quaternion.identity);
            var loose = W1.SpawnServerDriven(_propPrefab, _west, new Vector3(-1f, 1f, 4f), Quaternion.identity);
            pinned.ContainerPinned = true;

            // Both drift 2 m into the east chunk, well past the hysteresis.
            pinned.transform.position = new Vector3(2f, 1f, 0f);
            loose.transform.position = new Vector3(2f, 1f, 4f);
            W1.Tick(1);
            Assert.AreSame(_east, loose.Container, "an unpinned entity is resolved into the chunk it stands in");
            Assert.IsFalse(loose.HasAuthority, "and handed to that chunk's worker");
            Assert.AreSame(_west, pinned.Container, "a pinned one keeps its container");
            Assert.IsTrue(pinned.HasAuthority, "and its worker");

            // The west chunk is re-dealt to w2: the pinned entity goes with it.
            _mesh.SetOwner(_west, W2);
            W1.Tick(2);
            Assert.IsFalse(pinned.HasAuthority, "the owner check still runs for a pinned entity");
            _mesh.Pump();
            var theirs = W2.Find(pinned.NetId);
            Assert.IsTrue(theirs.HasAuthority);
            Assert.AreSame(_west, theirs.Container, "still in the chunk it was pinned to");
        }

        [Test]
        public void AnEntityAttachedToAChunkStaysInItAndFollowsItsRedeal()
        {
            TwoChunks();
            var fixtures = FixturePrefab();
            var fixture = W1.SpawnServerDriven(fixtures, _west, new Vector3(-1f, 1f, 0f), Quaternion.identity);
            var attachment = fixture.GetComponent<FrameAttachment>();

            // Attached with its origin 2 m into the east chunk: a shelf that overhangs the seam.
            var local = _west.ToLocal(new Vector3(2f, 1f, 0f));
            Assert.IsTrue(attachment.Attach(_west, local, Quaternion.identity));
            Assert.IsTrue(fixture.ContainerPinned);
            uint tick = 1;
            Ticks(W1, ref tick, 3);
            Assert.AreSame(_west, fixture.Container, "not resolved into the chunk its origin is in");
            Assert.IsTrue(fixture.HasAuthority, "and not handed to that chunk's worker");
            Assert.AreSame(_west, attachment.AttachedTo);

            _mesh.SetOwner(_west, W2);
            W1.Tick(tick++);
            Assert.IsFalse(fixture.HasAuthority, "a re-deal of its chunk takes it along");
            _mesh.Pump();
            var theirs = W2.Find(fixture.NetId);
            var theirAttachment = theirs.GetComponent<FrameAttachment>();
            Assert.IsTrue(theirs.HasAuthority);
            Assert.IsTrue(theirAttachment.Attached, "still attached on its new worker");
            Assert.IsTrue(theirs.ContainerPinned, "and pinned there");
            Assert.That(Vector3.Distance(local, theirAttachment.AttachedLocalPosition), Is.LessThan(1e-4f));
            Ticks(W2, ref tick, 3);
            Assert.AreSame(_west, theirs.Container);
            Assert.IsTrue(theirs.HasAuthority);
            Assert.That(Vector3.Distance(new Vector3(2f, 1f, 0f), theirs.transform.position), Is.LessThan(1e-3f));
        }

        // ------------------------------------------------------------------------------------ in a ship's frame (D7, D10)

        [Test]
        public void AnAttachedCrateHoldsItsPoseThroughAFlightAndCarriesALooseOne()
        {
            Space(1);
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, Vector3.zero, Quaternion.identity);
            var box = ship.Carried;
            var at = new Vector3(1f, 1.5f, 2f);
            var turn = Quaternion.Euler(0f, 30f, 0f);
            var shelf = SpawnIn(W1, _crate, box, at, turn);
            var attachment = shelf.GetComponent<FrameAttachment>();
            var changes = new List<bool>();
            attachment.AttachedChanged += changes.Add;
            Assert.IsTrue(attachment.Attach(box, at, turn));
            Assert.IsTrue(attachment.Attached);
            Assert.AreSame(box, attachment.AttachedTo);
            Assert.IsTrue(shelf.ContainerPinned);
            Assert.IsTrue(shelf.GetComponent<Rigidbody>().isKinematic, "held");
            CollectionAssert.AreEqual(new[] { true }, changes);

            // A loose crate on top of it, mid-air in the hold: only the attached crate holds it up.
            var loose = SpawnIn(W1, _looseCrate, box, at + new Vector3(0f, 0.5f + 0.26f, 0f));
            uint tick = 1;
            Ticks(W1, ref tick, 60);
            float restY = loose.LocalPosition.y;
            Assert.That(restY, Is.EqualTo(at.y + 0.75f).Within(0.02f), "the loose crate rests on the attached one");

            var flight = ConformanceFrameBodiesTests.Flight.Start(ship.transform.position);
            for (int t = 0; t < ConformanceFrameBodiesTests.Flight.Ticks; t++)
            {
                flight.Advance(t);
                ship.transform.SetPositionAndRotation(flight.Position, flight.Rotation);
                W1.Tick(tick++);
                Assert.That(Vector3.Distance(at, shelf.LocalPosition), Is.LessThan(1e-4f), $"tick {t}: the attached pose is constant");
                Assert.That(Quaternion.Angle(turn, shelf.LocalRotation), Is.LessThan(0.01f), $"tick {t}");
                Assert.AreSame(box, shelf.Container);
            }
            Assert.That(loose.LocalPosition.y, Is.EqualTo(restY).Within(0.01f), "still resting on it after the flight");
            Assert.AreSame(box, loose.Container);
            CollectionAssert.AreEqual(new[] { true }, changes, "attached once, never let go");
        }

        [Test]
        public void ADetachIsGentle()
        {
            Space(1);
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, Vector3.zero, Quaternion.identity);
            var box = ship.Carried;
            uint tick = 1;

            // Resting on the floor, exactly: letting go moves it less than a millimetre.
            var resting = SpawnIn(W1, _crate, box, new Vector3(-1.5f, 0.5f, 0f));
            var restingAttachment = resting.GetComponent<FrameAttachment>();
            Assert.IsTrue(restingAttachment.Attach());
            // Two crates attached 2 cm inside each other: letting go eases them apart instead of launching them.
            var a = SpawnIn(W1, _crate, box, new Vector3(1.5f, 0.5f, 0f));
            var b = SpawnIn(W1, _crate, box, new Vector3(1.5f, 0.5f, 0.98f));
            Assert.IsTrue(a.GetComponent<FrameAttachment>().Attach());
            Assert.IsTrue(b.GetComponent<FrameAttachment>().Attach());
            var aBody = a.GetComponent<Rigidbody>();
            var bBody = b.GetComponent<Rigidbody>();
            float depenetration = aBody.maxDepenetrationVelocity;
            // The ship is flying at 1 km/s: in its frame that is no velocity at all.
            for (int i = 0; i < 30; i++)
            {
                ship.transform.position += new Vector3(1000f * Dt, 0f, 0f);
                W1.Tick(tick++);
            }

            var before = resting.LocalPosition;
            Assert.IsTrue(restingAttachment.Detach());
            Assert.IsFalse(restingAttachment.Detach(), "detaching twice does nothing");
            Assert.IsTrue(a.GetComponent<FrameAttachment>().Detach());
            Assert.IsTrue(b.GetComponent<FrameAttachment>().Detach());
            Assert.IsFalse(restingAttachment.Attached);
            Assert.IsFalse(resting.ContainerPinned);
            Assert.AreSame(box, resting.Container, "it stays in its container");
            var body = resting.GetComponent<Rigidbody>();
            Assert.IsFalse(body.isKinematic, "handed to the solver");
            Assert.That(aBody.maxDepenetrationVelocity, Is.EqualTo(1f).Within(1e-4f), "depenetration capped while it settles");
            float fastest = 0f;
            for (int i = 0; i < 10; i++)
            {
                ship.transform.position += new Vector3(1000f * Dt, 0f, 0f);
                W1.Tick(tick++);
                Assert.That(Vector3.Distance(before, resting.LocalPosition), Is.LessThan(0.001f), $"tick {i}: it barely moved");
                Assert.That(body.linearVelocity.magnitude, Is.LessThan(0.05f), $"tick {i}: and is barely moving");
                fastest = Mathf.Max(fastest, aBody.linearVelocity.magnitude, bBody.linearVelocity.magnitude);
            }
            Assert.That(fastest, Is.LessThan(1.2f), "the overlapping pair was eased apart at the capped speed");
            Assert.That(aBody.maxDepenetrationVelocity, Is.EqualTo(depenetration), "and the body's own setting is back");
            Ticks(W1, ref tick, 30);
            Assert.That(Mathf.Abs(b.LocalPosition.z - a.LocalPosition.z), Is.GreaterThan(0.99f), "no longer overlapping");
        }

        // ------------------------------------------------------------------------------------ handover (D12)

        [Test]
        public void AHandoverOfTheShipKeepsItsCargoAttached()
        {
            Space(2);
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, Vector3.zero, Quaternion.identity);
            var box = ship.Carried;
            var at = new Vector3(-2f, 0.5f, 3f);
            var turn = Quaternion.Euler(0f, 45f, 0f);
            var crate = SpawnIn(W1, _crate, box, at);
            Assert.IsTrue(crate.GetComponent<FrameAttachment>().Attach(box, at, turn));
            uint tick = 1;
            Ticks(W1, ref tick, 10);

            _mesh.SetOwner(_space, W2);
            W1.Tick(tick++);
            Assert.IsFalse(crate.HasAuthority);
            Assert.IsFalse(crate.ContainerPinned, "the pin belongs to the authority");
            _mesh.Pump();
            // Tier limit: w1's copies hang in the same registered frame; in a live mesh they are in another process.
            foreach (var c in crate.GetComponentsInChildren<Collider>()) c.enabled = false;

            var theirs = W2.Find(crate.NetId);
            var attachment = theirs.GetComponent<FrameAttachment>();
            Assert.IsTrue(theirs.HasAuthority);
            Assert.IsTrue(attachment.Attached, "attached on its new worker");
            Assert.IsTrue(theirs.ContainerPinned);
            Assert.IsTrue(theirs.GetComponent<Rigidbody>().isKinematic, "held from the moment it arrived");
            Assert.That(Vector3.Distance(at, attachment.AttachedLocalPosition), Is.LessThan(1e-4f));
            Ticks(W2, ref tick, 30);
            Assert.That(Vector3.Distance(at, theirs.LocalPosition), Is.LessThan(1e-4f));
            Assert.That(Quaternion.Angle(turn, theirs.LocalRotation), Is.LessThan(0.01f));
        }

        [Test]
        public void ABareKinematicBodyStaysKinematicAfterAHandoverAndADetach()
        {
            TwoChunks();
            var prefab = new GameObject("bare-kinematic-prefab");
            prefab.AddComponent<NetworkIdentity>();
            prefab.AddComponent<Rigidbody>().isKinematic = true; // a platform the game moves itself
            prefab.AddComponent<BoxCollider>();
            prefab.AddComponent<FrameAttachment>();
            var platforms = _mesh.RegisterPrefab(prefab);
            var platform = W1.SpawnServerDriven(platforms, _west, new Vector3(-10f, 1f, 0f), Quaternion.identity);
            Assert.IsTrue(platform.GetComponent<FrameAttachment>().Attach());
            uint tick = 1;
            Ticks(W1, ref tick, 3);

            _mesh.SetOwner(_west, W2);
            W1.Tick(tick++);
            _mesh.Pump();
            var theirs = W2.Find(platform.NetId);
            var attachment = theirs.GetComponent<FrameAttachment>();
            Assert.IsTrue(theirs.HasAuthority);
            Assert.IsTrue(attachment.Attached);
            var body = theirs.GetComponent<Rigidbody>();
            Assert.IsTrue(body.isKinematic, "held");
            Assert.IsTrue(attachment.Detach());
            Assert.IsTrue(body.isKinematic, "let go, it is as kinematic as its prefab made it");
        }

        // ------------------------------------------------------------------------------------ losing the container (D13)

        [Test]
        public void AnEntitySetDownByItsCarrierDetaches()
        {
            Space(1);
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, new Vector3(30f, 0f, 0f), Quaternion.identity);
            var crate = SpawnIn(W1, _crate, ship.Carried, new Vector3(0f, 0.5f, 0f));
            var attachment = crate.GetComponent<FrameAttachment>();
            Assert.IsTrue(attachment.Attach());
            var changes = new List<bool>();
            attachment.AttachedChanged += changes.Add;
            uint tick = 1;
            Ticks(W1, ref tick, 5);

            W1.Act(() => W1.Instance.Despawn(ship));
            Assert.AreSame(_space, crate.Container, "set down where the ship stood");
            Assert.IsFalse(attachment.Attached, "its container went: it detached");
            Assert.IsNull(attachment.AttachedTo);
            Assert.IsFalse(crate.ContainerPinned);
            Assert.IsFalse(crate.GetComponent<Rigidbody>().isKinematic);
            CollectionAssert.AreEqual(new[] { false }, changes);
        }

        // ------------------------------------------------------------------------------------ persistence (D14)

        [Test]
        public void AnAttachedEntityComesBackAttachedInItsChunk()
        {
            TwoChunks();
            _store = ConformanceFrameBodiesTests.PersistenceFor(_mesh);
            var prefab = AttachablePrefab("saved-crate", persistent: true);
            prefab.GetComponent<PersistentEntity>().PersistPose = false; // the attachment carries its own pose
            var saved = _mesh.RegisterPrefab(prefab);
            var crate = W1.SpawnServerDriven(saved, _west, new Vector3(-10f, 0.5f, 0f), Quaternion.identity);
            var local = new Vector3(3f, 2f, 5f);
            var turn = Quaternion.Euler(0f, 60f, 0f);
            Assert.IsTrue(crate.GetComponent<FrameAttachment>().Attach(_west, local, turn));
            string key = crate.Persistent.EnsureKey();

            W1.Act(() => W1.Instance.Despawn(crate, keepPersisted: true));
            var record = ConformanceFrameBodiesTests.RecordOf(_store, key);
            Assert.IsNotNull(record);
            Assert.AreEqual(_west.ContainerId, record.ContainerId);

            NetworkIdentity back = null;
            W1.Act(() => back = W1.Instance.Persistence.Restore(record));
            Assert.IsNotNull(back);
            var attachment = back.GetComponent<FrameAttachment>();
            Assert.IsTrue(attachment.Attached, "restored attached");
            Assert.AreSame(_west, attachment.AttachedTo);
            Assert.IsTrue(back.ContainerPinned);
            Assert.IsTrue(back.GetComponent<Rigidbody>().isKinematic, "held from its first tick");
            Assert.That(Vector3.Distance(local, back.LocalPosition), Is.LessThan(1e-4f), "at the attached pose, although PersistPose is off");
            Assert.That(Quaternion.Angle(turn, back.LocalRotation), Is.LessThan(0.01f));
        }

        [Test]
        public void AttachedCargoStowedWithItsShipComesBackAttached()
        {
            Space(1);
            _store = ConformanceFrameBodiesTests.PersistenceFor(_mesh);
            var shipPrefab = ConformanceFrameBodiesTests.HoldPrefab("stowable-ship-prefab");
            shipPrefab.AddComponent<PersistentEntity>();
            var ships = _mesh.RegisterPrefab(shipPrefab);
            var cargo = _mesh.RegisterPrefab(AttachablePrefab("strapped-crate", persistent: true));
            var ship = W1.SpawnServerDriven(ships, _space, new Vector3(30f, 0f, 0f), Quaternion.identity);
            var at = new Vector3(2f, 1.2f, -3f); // strapped mid-air to a grid
            var crate = SpawnIn(W1, cargo, ship.Carried, at);
            Assert.IsTrue(crate.GetComponent<FrameAttachment>().Attach());
            uint tick = 1;
            Ticks(W1, ref tick, 5);
            string shipKey = ship.Persistent.EnsureKey(), crateKey = crate.Persistent.EnsureKey();
            ulong crateId = crate.NetId;

            W1.Act(() => W1.Instance.Despawn(ship, keepPersisted: true, CargoPolicy.Stow));
            Assert.IsNull(W1.Find(crateId), "stowed with the ship");
            var restored = ConformanceFrameBodiesTests.RestoreWithCargo(W1, _store, ConformanceFrameBodiesTests.RecordOf(_store, shipKey));
            Assert.IsNotNull(restored);
            var back = W1.Instance.Persistence.Find(crateKey);
            Assert.IsNotNull(back, "back with the ship");
            var attachment = back.GetComponent<FrameAttachment>();
            Assert.IsTrue(attachment.Attached);
            Assert.AreSame(restored.Carried, attachment.AttachedTo);
            Ticks(W1, ref tick, 30);
            Assert.That(Vector3.Distance(at, back.LocalPosition), Is.LessThan(1e-4f), "still strapped where it was, not fallen");
        }

        [Test]
        public void ADetachedSaveIsWrittenAndRestoresDetached()
        {
            TwoChunks();
            _store = ConformanceFrameBodiesTests.PersistenceFor(_mesh);
            var saved = _mesh.RegisterPrefab(AttachablePrefab("saved-crate", persistent: true));
            var crate = W1.SpawnServerDriven(saved, _west, new Vector3(-10f, 0.5f, 0f), Quaternion.identity);
            var attachment = crate.GetComponent<FrameAttachment>();
            string key = crate.Persistent.EnsureKey();
            Assert.IsTrue(attachment.Attach());
            W1.Act(() => W1.Instance.Persistence.SaveNow(crate));
            var attachedRecord = ConformanceFrameBodiesTests.RecordOf(_store, key);
            Assert.IsTrue(attachment.Detach());
            W1.Act(() => W1.Instance.Persistence.SaveNow(crate));
            var detachedRecord = ConformanceFrameBodiesTests.RecordOf(_store, key);
            Assert.IsTrue(PersistentStateCodec.TryReadBehaviourState(detachedRecord.State, nameof(FrameAttachment), out _),
                "a detached save still writes the attachment's chunk");

            // A copy that already says it is attached (a scene entity's last values) takes the record's word for it.
            var copy = NetworkPrefabs.Instantiate(saved, Vector3.zero, Quaternion.identity, null);
            try
            {
                W1.Act(() => W1.Instance.Persistence.Apply(attachedRecord, copy, applyPose: false));
                var copyAttachment = copy.GetComponent<FrameAttachment>();
                Assume.That(copyAttachment.Attached);
                W1.Act(() => W1.Instance.Persistence.Apply(detachedRecord, copy, applyPose: false));
                Assert.IsFalse(copyAttachment.Attached, "restored detached");
                Assert.IsFalse(copy.GetComponent<NetworkRigidbody>().Held, "and not held");
            }
            finally { Object.DestroyImmediate(copy.gameObject); }
        }

        [Test]
        public void ApplyingARecordToALiveEntityAttachesOrDetachesItAsTheRecordSays()
        {
            TwoChunks();
            var north = _mesh.AddStaticContainer("north", new Vector3(-32f, 0f, 96f), new Vector3(64f, 40f, 64f));
            _mesh.SetOwner(north, W1);
            _store = ConformanceFrameBodiesTests.PersistenceFor(_mesh);
            var saved = _mesh.RegisterPrefab(AttachablePrefab("saved-crate", persistent: true));
            var crate = W1.SpawnServerDriven(saved, _west, new Vector3(-10f, 0.5f, 0f), Quaternion.identity);
            var attachment = crate.GetComponent<FrameAttachment>();
            var body = crate.GetComponent<Rigidbody>();
            string key = crate.Persistent.EnsureKey();
            var local = new Vector3(3f, 2f, 5f);
            var turn = Quaternion.Euler(0f, 60f, 0f);
            Assert.IsTrue(attachment.Attach(_west, local, turn));
            W1.Act(() => W1.Instance.Persistence.SaveNow(crate));
            var attachedRecord = ConformanceFrameBodiesTests.RecordOf(_store, key);

            // Attached somewhere else since, then the record is applied to the live entity.
            Assert.IsTrue(attachment.Attach(north, new Vector3(0f, 1f, 0f), Quaternion.identity));
            W1.Act(() => W1.Instance.Persistence.Apply(attachedRecord, crate));
            Assert.IsTrue(attachment.Attached, "moving into the record's container did not detach it");
            Assert.AreSame(_west, attachment.AttachedTo);
            Assert.IsTrue(crate.ContainerPinned, "pinned");
            Assert.IsTrue(body.isKinematic, "held");
            uint tick = 1;
            Ticks(W1, ref tick, 3);
            Assert.IsTrue(attachment.Attached);
            Assert.AreSame(_west, crate.Container);
            Assert.That(Vector3.Distance(local, crate.LocalPosition), Is.LessThan(1e-4f), "at the record's attached pose");
            Assert.That(Quaternion.Angle(turn, crate.LocalRotation), Is.LessThan(0.01f));

            // A record saved detached, applied to a live attached entity, lets it go.
            Assert.IsTrue(attachment.Detach());
            W1.Act(() => W1.Instance.Persistence.SaveNow(crate));
            var detachedRecord = ConformanceFrameBodiesTests.RecordOf(_store, key);
            Assert.IsTrue(attachment.Attach());
            W1.Act(() => W1.Instance.Persistence.Apply(detachedRecord, crate));
            Assert.IsFalse(attachment.Attached);
            Assert.IsFalse(crate.ContainerPinned);
            Assert.IsFalse(body.isKinematic);
        }

        [Test]
        public void ASceneEntitySpawnedAgainWithNoRecordIsNotAttached()
        {
            TwoChunks();
            var go = AttachablePrefab("scene-crate");
            var crate = go.GetComponent<NetworkIdentity>();
            crate.SceneId = 4242;
            go.transform.position = new Vector3(-10f, 0.5f, 0f);
            try
            {
                W1.Act(() => W1.Instance.SpawnServerDriven(crate, _west));
                var attachment = crate.GetComponent<FrameAttachment>();
                Assert.IsTrue(attachment.Attach());
                W1.Act(() => W1.Instance.Despawn(crate));
                Assert.IsFalse(crate.IsSpawned);
                Assert.IsFalse(attachment.Attached, "the attachment does not outlive the entity's life on the network");

                W1.Act(() => W1.Instance.SpawnServerDriven(crate, _west));
                Assert.IsFalse(attachment.Attached, "spawned again with no record: not attached");
                Assert.IsFalse(crate.ContainerPinned);
                Assert.IsFalse(crate.GetComponent<Rigidbody>().isKinematic, "and not held");
            }
            finally
            {
                if (crate.IsSpawned) W1.Act(() => W1.Instance.Despawn(crate));
                Object.DestroyImmediate(go);
            }
        }

        // ------------------------------------------------------------------------------------ late joiners (D15)

        [Test]
        public void ALateJoinersSpawnSaysItIsAttached()
        {
            Space(1);
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, Vector3.zero, Quaternion.identity);
            var crate = SpawnIn(W1, _crate, ship.Carried, new Vector3(0f, 0.5f, 0f));
            Assert.IsTrue(crate.GetComponent<FrameAttachment>().Attach());

            var writer = new NetworkWriter();
            EntitySpawnMsg.From(crate, new NetworkWriter()).Write(writer, MsgId.EntitySpawn);
            var reader = new NetworkReader(writer.ToArray());
            reader.ReadByte();
            var msg = EntitySpawnMsg.Read(reader);

            // A copy built the way a client builds one from a spawn: variables read, then activated and spawned.
            var copy = NetworkPrefabs.Instantiate(_crate, Vector3.zero, Quaternion.identity, null);
            try
            {
                copy.Initialize();
                var attachment = copy.GetComponent<FrameAttachment>();
                var changes = new List<bool>();
                attachment.AttachedChanged += changes.Add;
                copy.ReadVars(new NetworkReader(msg.Vars));
                copy.gameObject.SetActive(true);
                copy.InvokeSpawn();
                Assert.IsTrue(attachment.Attached, "the spawn's variables say it is attached");
                CollectionAssert.AreEqual(new[] { true }, changes, "and the late joiner heard it once");
            }
            finally { Object.DestroyImmediate(copy.gameObject); }
        }

        [Test]
        public void AnActiveCopyHearsItIsAttachedAtItsSpawnNotBefore()
        {
            Space(1);
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, Vector3.zero, Quaternion.identity);
            var crate = SpawnIn(W1, _crate, ship.Carried, new Vector3(0f, 0.5f, 0f));
            Assert.IsTrue(crate.GetComponent<FrameAttachment>().Attach());
            var vars = new NetworkWriter();
            crate.WriteVars(vars);

            // An object that is already active when its values arrive, as a scene entity is.
            var copy = NetworkPrefabs.Instantiate(_crate, Vector3.zero, Quaternion.identity, null);
            try
            {
                copy.gameObject.SetActive(true);
                copy.Initialize();
                var attachment = copy.GetComponent<FrameAttachment>();
                var early = new List<bool>();
                attachment.AttachedChanged += early.Add;
                copy.ReadVars(new NetworkReader(vars.ToArray()));
                Assert.IsTrue(attachment.Attached);
                CollectionAssert.IsEmpty(early, "nothing is reported before the spawn");

                // A listener that subscribes in its own OnNetworkSpawn, after the values were read.
                var late = new List<bool>();
                attachment.AttachedChanged += late.Add;
                copy.InvokeSpawn();
                CollectionAssert.AreEqual(new[] { true }, early, "reported once, at the spawn");
                CollectionAssert.AreEqual(new[] { true }, late, "to every listener subscribed by then");
            }
            finally { Object.DestroyImmediate(copy.gameObject); }
        }

        // ------------------------------------------------------------------------------------ any worker can ask (D7)

        [Test]
        public void AnotherWorkerCanAskTheAuthorityToAttachAndDetach()
        {
            TwoChunks();
            var fixtures = FixturePrefab();
            var fixture = W1.SpawnServerDriven(fixtures, _west, new Vector3(-1f, 1f, 0f), Quaternion.identity);
            W1.PublishTick(1);
            _mesh.Pump();
            var ghost = W2.Find(fixture.NetId);
            Assume.That(ghost, Is.Not.Null, "w1's ghost band gave w2 a copy at the seam");
            var local = _west.ToLocal(new Vector3(-1f, 2f, 0f));
            // The mesh does not run NebulaWorker.Initialize, which creates the call contract's tracker and router.
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("_callTracker", flags).SetValue(W2.Instance, new AuthorityCallTracker(W2.Index));
            typeof(NebulaWorker).GetField("_callRouter", flags).SetValue(W1.Instance, new AuthorityCallRouter(_mesh.Config.AuthorityCallMaxHops));

            W2.Act(() =>
            {
                NebulaRuntime.RpcSink = W2.Instance;
                ghost.GetComponent<FrameAttachment>().RequestAttach(_west, local, Quaternion.identity);
            });
            _mesh.Pump();
            var attachment = fixture.GetComponent<FrameAttachment>();
            Assert.IsTrue(attachment.Attached, "the authority attached it");
            Assert.That(Vector3.Distance(local, fixture.LocalPosition), Is.LessThan(1e-4f));

            W2.Act(() =>
            {
                NebulaRuntime.RpcSink = W2.Instance;
                ghost.GetComponent<FrameAttachment>().RequestDetach();
            });
            _mesh.Pump();
            Assert.IsFalse(attachment.Attached, "and detached it");
        }
    }
}
