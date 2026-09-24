using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 21 (<c>docs/container-tree.md</c> §3): a container with a physics frame of its own. The frame
    /// is a local physics scene in which the container stands still, holding a static copy of the carrier's colliders;
    /// what is inside simulates in container-local coordinates with the container's own "down"; entities cross into and
    /// out of the frame through the frame's pose owner, converted at its pose; a worker that is not the pose owner hands
    /// the entity over instead; and the game's crossing policy can veto or defer a crossing.
    /// <para>
    /// Frames get Editor preview scenes here, which have physics scenes of their own like the runtime scenes a player
    /// creates. Tier B's limit applies: the container registry is process-wide, so of two workers' copies of one
    /// carrier only the last registered owns the box and its frame; the scenarios keep the copies' poses in step where
    /// that matters and say so.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformancePhysicsFrameTests
    {
        private ConformanceMesh _mesh;
        private ushort _shipPrefab, _crewPrefab;
        private Container _yard;
        private readonly List<GameObject> _objects = new List<GameObject>();

        private ConformanceMesh.Worker W1 => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
        }

        [TearDown]
        public void TearDown()
        {
            PhysicsFrames.CrossingPolicy = null;
            _mesh?.Dispose();
            _mesh = null;
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            Assert.AreEqual(0, PhysicsFrames.All.Count, "every frame was released with its container");
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        /// <summary>A ship prefab: a framed carried box 10 x 6 x 20 m with a floor and one wall as its geometry.</summary>
        private static GameObject ShipPrefab()
        {
            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var box = ship.AddComponent<Container>();
            box.ContainerId = "ship";
            box.Size = new Vector3(10f, 6f, 20f);
            box.Center = new Vector3(0f, 3f, 0f);
            box.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            var floor = new GameObject("floor");
            floor.transform.SetParent(ship.transform, false);
            floor.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            floor.AddComponent<BoxCollider>().size = new Vector3(10f, 0.2f, 20f);
            var wall = new GameObject("wall");
            wall.transform.SetParent(ship.transform, false);
            wall.transform.localPosition = new Vector3(5f, 3f, 0f);
            wall.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            wall.AddComponent<BoxCollider>().size = new Vector3(6f, 0.2f, 20f);
            return ship;
        }

        private void MeshWith(int workers)
        {
            _mesh = new ConformanceMesh(workers);
            _yard = _mesh.AddStaticContainer("yard", Vector3.zero, new Vector3(400f, 100f, 400f));
            _mesh.SetOwner(_yard, W1);
            _shipPrefab = _mesh.RegisterPrefab(ShipPrefab());
            var crew = new GameObject("crew-prefab");
            crew.AddComponent<NetworkIdentity>();
            _crewPrefab = _mesh.RegisterPrefab(crew);
        }

        // ------------------------------------------------------------------------------------ the frame itself

        [Test]
        public void AFramedCarrierGetsAStillSceneWithACopyOfItsColliders()
        {
            MeshWith(1);
            var ship = W1.SpawnServerDriven(_shipPrefab, _yard, new Vector3(20f, 1f, 0f), Quaternion.Euler(0f, 30f, 0f));
            var frame = ship.Carried.Frame;
            Assert.IsNotNull(frame, "a container with OwnPhysicsFrame has a frame once it is registered");
            Assert.IsTrue(frame.Scene.IsValid());
            Assert.AreNotEqual(ship.gameObject.scene, frame.Scene, "the frame is a scene of its own");
            Assert.AreEqual(Vector3.zero, frame.Root.position);
            Assert.AreEqual(Quaternion.identity, frame.Root.rotation);
            Assert.AreEqual(2, frame.Clones.Count, "the floor and the wall");
            var wall = frame.Clones.Find(p => p.Key.name == "wall").Value;
            Assert.That(Vector3.Distance(new Vector3(5f, 3f, 0f), wall.transform.localPosition), Is.LessThan(1e-4f));
            Assert.AreSame(frame.Root, ship.Carried.ContentRoot);

            // The ship turns and flies; the frame does not move.
            ship.transform.SetPositionAndRotation(new Vector3(500f, 40f, -30f), Quaternion.Euler(20f, 100f, 70f));
            PhysicsFrames.SyncAllContent();
            Assert.AreEqual(Vector3.zero, frame.Root.position);
            Assert.That(Vector3.Distance(new Vector3(5f, 3f, 0f), wall.transform.localPosition), Is.LessThan(1e-3f));
        }

        [Test]
        public void PartsWithANestedIdentityAreTheCarriersGeometry()
        {
            // A door or a seat on the hull whose NetworkBehaviour pulled in a NetworkIdentity of its own (RequireComponent):
            // that identity is never spawned, the carrier owns the behaviour, and the door must block and be hit in the frame.
            MeshWith(1);
            var prefab = ShipPrefab();
            var door = new GameObject("door");
            door.transform.SetParent(prefab.transform, false);
            door.transform.localPosition = new Vector3(0f, 1.5f, 5f);
            door.AddComponent<NetworkIdentity>();
            door.AddComponent<BoxCollider>().size = new Vector3(2f, 3f, 0.2f);
            var shipWithDoor = _mesh.RegisterPrefab(prefab);
            var ship = W1.SpawnServerDriven(shipWithDoor, _yard, new Vector3(20f, 1f, 0f), Quaternion.identity);
            var frame = ship.Carried.Frame;
            Assert.AreEqual(3, frame.Clones.Count, "the floor, the wall and the door");
            var source = ship.GetComponentsInChildren<BoxCollider>(true);
            var shipDoor = System.Array.Find(source, c => c.name == "door");
            var copy = frame.Clones.Find(p => p.Key == shipDoor).Value;
            Assert.IsNotNull(copy);
            Assert.AreSame(shipDoor, PhysicsFrames.SourceOf(copy), "a ray that meets the copy finds the door");

            // The door slides open and is switched off: its copy follows. On a worker a copy that moves is a kinematic
            // body from then on, and reaches its new pose when the frame's scene steps (docs/frame-bodies.md D3).
            shipDoor.transform.localPosition = new Vector3(2f, 1.5f, 5f);
            shipDoor.enabled = false;
            PhysicsFrames.SyncAllContent();
            PhysicsFrames.Simulate(NetworkTime.TickInterval);
            Assert.That(Vector3.Distance(new Vector3(2f, 1.5f, 5f), copy.transform.localPosition), Is.LessThan(1e-4f));
            Assert.IsFalse(copy.enabled);
            var body = copy.GetComponent<Rigidbody>();
            Assert.IsNotNull(body, "the door's copy became a body when it moved");
            Assert.IsTrue(body.isKinematic);
        }

        [Test]
        public void ARigidbodyInsideFallsTowardsTheContainersOwnFloor()
        {
            MeshWith(1);
            // The ship is rolled onto its side: in the world, its floor is a wall.
            var ship = W1.SpawnServerDriven(_shipPrefab, _yard, new Vector3(20f, 10f, 0f), Quaternion.Euler(0f, 0f, 90f));
            var frame = ship.Carried.Frame;
            var crate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _objects.Add(crate);
            crate.transform.SetParent(frame.Root, false);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(crate.transform.root.gameObject, frame.Scene);
            crate.transform.localPosition = new Vector3(0f, 3f, 0f);
            var body = crate.AddComponent<Rigidbody>();
            Physics.SyncTransforms();
            for (int i = 0; i < 120; i++) PhysicsFrames.Simulate(1f / 60f);
            var local = crate.transform.localPosition;
            Assert.That(local.y, Is.EqualTo(0.5f).Within(0.1f), "it came to rest on the ship's floor, the ship's own down");
            Assert.That(Mathf.Abs(local.x), Is.LessThan(0.05f), "not towards the world's down, which is the ship's side");
            Assert.That(body.linearVelocity.magnitude, Is.LessThan(0.1f));
        }

        [Test]
        public void FrameStateReportsTheCarriersMotion()
        {
            MeshWith(1);
            var ship = W1.SpawnServerDriven(_shipPrefab, _yard, Vector3.zero, Quaternion.identity);
            var frame = ship.Carried.Frame;
            const float dt = 1f / 60f;
            for (uint t = 1; t <= 5; t++)
            {
                ship.transform.position += new Vector3(1000f * dt, 0f, 0f); // 1 km/s
                ship.transform.rotation = Quaternion.Euler(0f, 30f * t * dt, 0f); // 30 degrees per second
                PhysicsFrames.UpdateStates(t, dt);
            }
            Assert.IsTrue(frame.State.HasRates);
            Assert.That(frame.State.Velocity.x, Is.EqualTo(1000f).Within(1f));
            Assert.That(frame.State.AngularVelocity.y, Is.EqualTo(30f * Mathf.Deg2Rad).Within(0.01f));
            Assert.That(frame.State.Acceleration.magnitude, Is.LessThan(1f), "constant velocity: no acceleration");
            Assert.That(frame.State.PointVelocity(new Vector3(0f, 0f, 10f)).x, Is.EqualTo(1000f + 10f * 30f * Mathf.Deg2Rad).Within(1f), "v + ω × r");
        }

        // ------------------------------------------------------------------------------------ crossings (D15)

        [Test]
        public void ThePoseOwnerCrossesAnEntityInAndOutAtTheFramesPose()
        {
            MeshWith(1);
            _mesh.Config.HandoverHysteresis = 0.35f;
            var ship = W1.SpawnServerDriven(_shipPrefab, _yard, new Vector3(40f, 0f, 10f), Quaternion.Euler(0f, 90f, 0f));
            var box = ship.Carried;
            ContainerRegistry.RefreshCaches();
            var inside = ship.transform.TransformPoint(new Vector3(1f, 1f, 2f));
            var crew = W1.SpawnServerDriven(_crewPrefab, _yard, inside, Quaternion.identity);
            Assume.That(crew.Container, Is.SameAs(_yard));

            W1.Tick(1);
            Assert.AreSame(box, crew.Container, "the pose owner took it into the frame");
            Assert.AreSame(box.Frame.Root, crew.transform.parent);
            Assert.AreEqual(box.Frame.Scene, crew.gameObject.scene, "it simulates in the frame's scene now");
            Assert.That(Vector3.Distance(new Vector3(1f, 1f, 2f), crew.transform.position), Is.LessThan(1e-3f), "frame coordinates are container-local");
            Assert.That(Vector3.Distance(new Vector3(1f, 1f, 2f), crew.LocalPosition), Is.LessThan(1e-3f));
            Assert.AreEqual(1, W1.Instance.FrameCrossings);

            // The ship flies off at 1 km/s: nothing inside the frame moves in frame coordinates.
            float dt = NetworkTime.TickInterval;
            for (uint t = 2; t <= 6; t++)
            {
                ship.transform.position += new Vector3(1000f * dt, 0f, 0f);
                W1.Tick(t);
            }
            Assert.AreSame(box, crew.Container);
            Assert.That(Vector3.Distance(new Vector3(1f, 1f, 2f), crew.LocalPosition), Is.LessThan(1e-3f), "no lag error inside a frame");

            // Out through the stern: past the inner box by more than the hysteresis.
            crew.transform.localPosition = new Vector3(1f, 1f, -10.5f);
            ship.transform.position += new Vector3(1000f * dt, 0f, 0f); // still flying at 1 km/s this tick
            var expectedWorld = ship.transform.TransformPoint(new Vector3(1f, 1f, -10.5f));
            W1.Tick(7);
            Assert.AreSame(_yard, crew.Container, "the pose owner took it out of the frame");
            Assert.AreEqual(_yard.gameObject.scene, crew.gameObject.scene);
            Assert.That(Vector3.Distance(expectedWorld, crew.transform.position), Is.LessThan(1e-3f), "converted at the frame's pose of this tick: no error at the crossing");
            Assert.That(crew.Motion.Velocity.x, Is.EqualTo(1000f).Within(5f), "it keeps the ship's velocity");
        }

        [Test]
        public void ANonPoseOwnerHandsTheEntityToThePoseOwnerUnconverted()
        {
            MeshWith(2);
            var w2 = _mesh[1];
            var dock = _mesh.AddStaticContainer("dock", new Vector3(400f, 0f, 0f), new Vector3(400f, 100f, 400f)); // x in [200, 600]
            _mesh.SetOwner(_yard, W1);
            _mesh.SetOwner(dock, w2);
            var ship = W1.SpawnServerDriven(_shipPrefab, _yard, new Vector3(198f, 0f, 0f), Quaternion.identity);
            W1.PublishTick(1);
            _mesh.Pump();
            var ghost = w2.Find(ship.NetId);
            Assume.That(ghost, Is.Not.Null, "w1's ghost band gave w2 a copy of the ship at the seam");
            Assume.That(ghost.Carried, Is.Not.Null);
            var box = ghost.Carried;
            Assume.That(box.Frame, Is.Not.Null);

            // The engine room: a room fixed in the ship's frame, leased to w2 (allowed: the ship has its own frame, D20).
            var engine = ContainerRegistry.RegisterRuntime(900, ContainerPlacement.Child(box.ContainerId, new Vector3(0f, 3f, -6f), new Vector3(8f, 6f, 6f), ContainerAuthority.Leased));
            ContainerRegistry.ApplyLease(engine.ContainerId, w2.Id, w2.Index, 1);
            Assume.That(engine.IsLeased, Is.True);
            Assume.That(engine.OwnerWorkerId, Is.EqualTo(w2.Id));

            NetworkIdentity crew = null;
            w2.Act(() =>
            {
                crew = NetworkPrefabs.Instantiate(_crewPrefab, new Vector3(0f, 1f, -6f), Quaternion.identity, engine.ContentRoot);
                w2.Instance.SpawnServerDriven(crew, engine);
            });
            Assume.That(crew.Container, Is.SameAs(engine));
            // Positions below are frame coordinates, which are what a transform reads inside a frame on a worker.
            crew.transform.position = new Vector3(0f, 1f, -6f);

            // The crew member steps out through the stern. w2 does not simulate the ship, so it does not convert.
            crew.transform.position = new Vector3(0f, 1f, -10.6f);
            w2.Tick(2);
            Assert.IsFalse(crew.HasAuthority, "w2 handed it to the ship's owner");
            Assert.AreEqual(1, w2.Instance.CrossingHandoffs);
            _mesh.Pump();
            var sent = _mesh.DeliveredOf(MsgId.AuthorityTransfer, W1.Id);
            Assert.AreEqual(1, sent.Count);
            var msg = sent[0].Read(AuthorityTransferMsg.Read);
            Assert.IsTrue(msg.Crossing, "flagged as a crossing");
            Assert.AreEqual(ContainerRef.Runtime(900), msg.Entity.Container, "still in the engine room's coordinates: nothing was converted");

            var mine = W1.Find(crew.NetId);
            Assert.IsTrue(mine.HasAuthority);
            // Tier limit: the registered box is w2's copy of the ship; keep it where w1's is.
            ghost.transform.SetPositionAndRotation(ship.transform.position, ship.transform.rotation);
            var expected = ship.transform.TransformPoint(new Vector3(0f, 1f, -10.6f));
            W1.Tick(3);
            Assert.AreSame(_yard, mine.Container, "the pose owner crossed it out");
            Assert.That(Vector3.Distance(expected, mine.transform.position), Is.LessThan(1e-3f));
            Assert.AreEqual(1, W1.Instance.FrameCrossings);
        }

        [Test]
        public void WalkingBetweenTwoOwnersInsideAFrameIsAnOrdinaryHandover()
        {
            MeshWith(2);
            var w2 = _mesh[1];
            var ship = W1.SpawnServerDriven(_shipPrefab, _yard, new Vector3(0f, 0f, 0f), Quaternion.Euler(0f, 45f, 0f));
            var box = ship.Carried;
            var engine = ContainerRegistry.RegisterRuntime(901, ContainerPlacement.Child(box.ContainerId, new Vector3(0f, 3f, -6f), new Vector3(8f, 6f, 6f), ContainerAuthority.Leased));
            ContainerRegistry.ApplyLease(engine.ContainerId, w2.Id, w2.Index, 1);

            NetworkIdentity crew = null;
            W1.Act(() =>
            {
                crew = NetworkPrefabs.Instantiate(_crewPrefab, new Vector3(0f, 1f, 5f), Quaternion.identity, box.ContentRoot);
                W1.Instance.SpawnServerDriven(crew, box);
            });
            crew.transform.position = new Vector3(0f, 1f, 5f);
            Assume.That(crew.Container, Is.SameAs(box), "on the bridge, which is the ship's own frame, simulated by w1");

            crew.transform.position = new Vector3(0f, 1f, -6f); // walks aft into the engine room (frame coordinates)
            W1.Tick(1);
            Assert.AreSame(engine, crew.Container);
            Assert.IsFalse(crew.HasAuthority, "handed to w2, which leases the engine room");
            Assert.AreEqual(0, W1.Instance.FrameCrossings, "no frame boundary was crossed");
            _mesh.Pump();
            var theirs = w2.Find(crew.NetId);
            Assert.IsTrue(theirs.HasAuthority);
            var transfer = _mesh.DeliveredOf(MsgId.AuthorityTransfer, w2.Id)[0].Read(AuthorityTransferMsg.Read);
            Assert.IsFalse(transfer.Crossing);
            Assert.That(Vector3.Distance(new Vector3(0f, 1f, -6f), theirs.transform.position), Is.LessThan(1e-3f), "exact: both sides use the same frame coordinates");
            Assert.That(Vector3.Distance(new Vector3(0f, -2f, 0f), theirs.LocalPosition), Is.LessThan(1e-3f), "and the wire carries engine-room-local coordinates");
        }

        // ------------------------------------------------------------------------------------ hooks (D16)

        private sealed class Portal : IFrameCrossingPolicy
        {
            public FrameCrossing Answer = FrameCrossing.Veto;
            public int Asked;
            public bool LastLeaving;
            public FrameCrossing Decide(NetworkIdentity entity, PhysicsFrame frame, bool leaving, Container from, Container to)
            {
                Asked++;
                LastLeaving = leaving;
                return Answer;
            }
        }

        [Test]
        public void TheCrossingPolicyCanVetoAndDefer()
        {
            MeshWith(1);
            var ship = W1.SpawnServerDriven(_shipPrefab, _yard, new Vector3(40f, 0f, 0f), Quaternion.identity);
            var crew = W1.SpawnServerDriven(_crewPrefab, _yard, new Vector3(40f, 1f, 3f), Quaternion.identity);
            var portal = new Portal { Answer = FrameCrossing.Veto };
            PhysicsFrames.CrossingPolicy = portal;

            W1.Tick(1);
            Assert.AreSame(_yard, crew.Container, "vetoed: it stays outside");
            Assert.AreEqual(1, portal.Asked);
            Assert.IsFalse(portal.LastLeaving);

            portal.Answer = FrameCrossing.Defer;
            W1.Tick(2);
            Assert.AreSame(_yard, crew.Container, "deferred: not yet");
            Assert.AreEqual(2, portal.Asked, "asked again the next tick");

            portal.Answer = FrameCrossing.Allow;
            W1.Tick(3);
            Assert.AreSame(ship.Carried, crew.Container);
        }
    }
}
