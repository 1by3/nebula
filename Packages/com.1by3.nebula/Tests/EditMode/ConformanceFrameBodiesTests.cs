using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 24 (<c>docs/frame-bodies.md</c> §1): loose rigidbodies in a moving physics frame. Crates
    /// in a ship's closed hold are ordinary <see cref="NetworkRigidbody"/> props simulated in the ship's frame, where
    /// the ship stands still: a stack survives a whole flight, crosses out and back in with its velocity converted,
    /// and keeps its pose and sleep across a handover of the ship.
    /// <para>
    /// Tier B: real <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/>, driven one whole tick at a
    /// time, so each frame scene steps exactly as a worker steps it (<see cref="PhysicsFrames.Simulate"/>). Frames
    /// get Editor preview scenes, which have physics scenes of their own. The container registry is process-wide, so
    /// of two workers' copies of one carrier only the last registered owns the box and its frame; the handover
    /// scenario asserts only on the receiving worker's copies.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFrameBodiesTests
    {
        private const float Dt = NetworkTime.TickInterval;
        /// <summary>The hold's inside: 6 m wide, 4 m high, 10 m long, floor at y = 0 in the ship's own coordinates.</summary>
        private static readonly Vector3 Hold = new Vector3(6f, 4f, 10f);

        private ConformanceMesh _mesh;
        private Container _space;
        private ushort _shipPrefab, _bigCrate, _midCrate, _smallCrate;
        private PhysicsMaterial _material;

        private ConformanceMesh.Worker W1 => _mesh[0];

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
            _mesh?.Dispose();
            _mesh = null;
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            Assert.AreEqual(0, PhysicsFrames.All.Count, "every frame was released with its container");
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
            if (_material != null) Object.DestroyImmediate(_material);
        }

        // ------------------------------------------------------------------------------------ fixtures

        /// <summary>A ship whose framed box is a closed hold: floor, ceiling and four walls, 0.2 m thick, outside the box.</summary>
        internal static GameObject HoldPrefab(string name = "hold-prefab")
        {
            var ship = new GameObject(name);
            ship.AddComponent<NetworkIdentity>();
            var box = ship.AddComponent<Container>();
            box.ContainerId = "hold";
            box.Size = Hold;
            box.Center = new Vector3(0f, Hold.y * 0.5f, 0f);
            box.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>();
            const float t = 0.2f;
            Slab(ship, "floor", new Vector3(0f, -t * 0.5f, 0f), new Vector3(Hold.x + 2 * t, t, Hold.z + 2 * t));
            Slab(ship, "ceiling", new Vector3(0f, Hold.y + t * 0.5f, 0f), new Vector3(Hold.x + 2 * t, t, Hold.z + 2 * t));
            Slab(ship, "port", new Vector3(-(Hold.x + t) * 0.5f, Hold.y * 0.5f, 0f), new Vector3(t, Hold.y, Hold.z));
            Slab(ship, "starboard", new Vector3((Hold.x + t) * 0.5f, Hold.y * 0.5f, 0f), new Vector3(t, Hold.y, Hold.z));
            Slab(ship, "bow", new Vector3(0f, Hold.y * 0.5f, (Hold.z + t) * 0.5f), new Vector3(Hold.x, Hold.y, t));
            Slab(ship, "stern", new Vector3(0f, Hold.y * 0.5f, -(Hold.z + t) * 0.5f), new Vector3(Hold.x, Hold.y, t));
            return ship;
        }

        internal static GameObject Slab(GameObject parent, string name, Vector3 localPosition, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPosition;
            go.AddComponent<BoxCollider>().size = size;
            return go;
        }

        /// <summary>
        /// A crate prefab with the settings <c>docs/frame-bodies.md</c> D1 recommends: 10 solver iterations,
        /// speculative CCD, friction 0.6.
        /// </summary>
        internal static GameObject CratePrefab(string name, float size, float mass, PhysicsMaterial material, bool persistent = false)
        {
            var crate = new GameObject(name);
            crate.AddComponent<NetworkIdentity>();
            crate.AddComponent<NetworkTransform>();
            var body = crate.AddComponent<Rigidbody>();
            body.mass = mass;
            body.solverIterations = 10;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            crate.AddComponent<NetworkRigidbody>();
            var collider = crate.AddComponent<BoxCollider>();
            collider.size = Vector3.one * size;
            collider.sharedMaterial = material;
            if (persistent) crate.AddComponent<PersistentEntity>();
            return crate;
        }

        private void MeshWith(int workers)
        {
            _mesh = new ConformanceMesh(workers);
            // Big enough for a 5 km cruise: the ship never leaves it, so only the frame's crossings are in play.
            _space = _mesh.AddStaticContainer("space", new Vector3(0f, -10000f, 0f), new Vector3(20000f, 20000f, 20000f));
            _mesh.SetOwner(_space, W1);
            _shipPrefab = _mesh.RegisterPrefab(HoldPrefab());
            _bigCrate = _mesh.RegisterPrefab(CratePrefab("crate-big", 1.6f, 200f, _material));
            _midCrate = _mesh.RegisterPrefab(CratePrefab("crate-mid", 1.0f, 60f, _material));
            _smallCrate = _mesh.RegisterPrefab(CratePrefab("crate-small", 0.5f, 15f, _material));
        }

        /// <summary>Spawn a crate at a frame-local pose in <paramref name="box"/> (on a worker a frame's transforms read frame-local coordinates).</summary>
        internal static NetworkIdentity SpawnIn(ConformanceMesh.Worker w, ushort prefab, Container box, Vector3 local, Quaternion rotation = default)
        {
            if (rotation == default) rotation = Quaternion.identity;
            NetworkIdentity crate = null;
            w.Act(() =>
            {
                crate = NetworkPrefabs.Instantiate(prefab, local, rotation, box.ContentRoot);
                w.Instance.SpawnServerDriven(crate, box);
            });
            crate.SetLocalPose(box, local, rotation);
            var body = crate.GetComponent<Rigidbody>();
            if (body != null) { body.position = crate.transform.position; body.rotation = crate.transform.rotation; }
            return crate;
        }

        /// <summary>Three crates stacked in the middle of the hold, biggest at the bottom, a centimetre apart.</summary>
        private NetworkIdentity[] Stack(ConformanceMesh.Worker w, Container box)
        {
            return new[]
            {
                SpawnIn(w, _bigCrate, box, new Vector3(0f, 0.81f, 0f)),
                SpawnIn(w, _midCrate, box, new Vector3(0.1f, 1.62f + 0.51f, 0f)),
                SpawnIn(w, _smallCrate, box, new Vector3(-0.1f, 2.64f + 0.26f, 0.1f)),
            };
        }

        /// <summary>
        /// The ship's motion for one tick of a 900-tick flight: rest, takeoff, surges of ±20 m/s², a roll and a yaw at
        /// 80°/s, a 1 km/s cruise, and a landing that snaps the ship back onto the ground at rest at tick 780.
        /// </summary>
        internal struct Flight
        {
            public const int Ticks = 900;
            public const int Landing = 780;
            public Vector3 Position, Velocity;
            public Quaternion Rotation;

            public static Flight Start(Vector3 position) => new Flight { Position = position, Rotation = Quaternion.identity };

            public void Advance(int t)
            {
                var accel = Vector3.zero;
                var turn = Vector3.zero; // degrees per second, ship axes
                var forward = Rotation * Vector3.forward;
                if (t >= 60 && t < 120) accel = Vector3.up * 5f;
                else if (t >= 120 && t < 180) accel = Vector3.down * 5f;
                else if (t >= 180 && t < 300) accel = forward * (((t - 180) / 30) % 2 == 0 ? 20f : -20f);
                else if (t >= 300 && t < 330) turn = new Vector3(0f, 0f, 80f);
                else if (t >= 330 && t < 360) turn = new Vector3(0f, 0f, -80f);
                else if (t >= 360 && t < 390) turn = new Vector3(0f, 80f, 0f);
                else if (t >= 390 && t < 420) turn = new Vector3(0f, -80f, 0f);
                if (t == 420) Velocity = forward * 1000f;
                if (t == 720) Velocity = Vector3.zero;
                if (t == Landing)
                {
                    // The landing snap: straight onto the ground, level and at rest, in one tick.
                    Position = new Vector3(Position.x, 0f, Position.z);
                    Rotation = Quaternion.Euler(0f, Rotation.eulerAngles.y, 0f);
                    Velocity = Vector3.zero;
                    return;
                }
                Velocity += accel * Dt;
                Position += Velocity * Dt;
                Rotation = Rotation * Quaternion.Euler(turn * Dt);
            }
        }

        private static bool InsideHold(NetworkIdentity crate, float halfSize)
        {
            var p = crate.LocalPosition;
            const float slack = 0.02f;
            return Mathf.Abs(p.x) <= Hold.x * 0.5f - halfSize + slack && Mathf.Abs(p.z) <= Hold.z * 0.5f - halfSize + slack
                && p.y >= halfSize - slack && p.y <= Hold.y - halfSize + slack;
        }

        /// <summary>
        /// Fly the ship through <see cref="Flight"/> with the stack aboard and check it every tick. Returns the largest
        /// horizontal slide of any crate from where it settled, in metres.
        /// </summary>
        internal static float FlyAndCheck(ConformanceMesh.Worker w, NetworkIdentity ship, NetworkIdentity[] stack, float[] halfSizes, uint firstTick, float maxStepMetres = 0.05f)
        {
            var box = ship.Carried;
            var flight = Flight.Start(ship.transform.position);
            var last = new Vector3[stack.Length];
            var settled = new Vector3[stack.Length];
            for (int i = 0; i < stack.Length; i++) settled[i] = last[i] = stack[i].LocalPosition;
            float slide = 0f;
            int allAsleepAt = -1;
            for (int t = 0; t < Flight.Ticks; t++)
            {
                flight.Advance(t);
                ship.transform.SetPositionAndRotation(flight.Position, flight.Rotation);
                w.Tick(firstTick + (uint)t);
                bool allAsleep = true;
                for (int i = 0; i < stack.Length; i++)
                {
                    var crate = stack[i];
                    Assert.AreSame(box, crate.Container, $"tick {t}: {crate} is still in the hold");
                    Assert.IsTrue(InsideHold(crate, halfSizes[i]), $"tick {t}: {crate} is inside the hull at {crate.LocalPosition:F3}");
                    var p = crate.LocalPosition;
                    float step = Vector3.Distance(p, last[i]);
                    Assert.That(step, Is.LessThan(maxStepMetres), $"tick {t}: {crate} jumped {step:F3} m");
                    last[i] = p;
                    var flat = p - settled[i];
                    flat.y = 0f;
                    slide = Mathf.Max(slide, flat.magnitude);
                    if (!crate.GetComponent<Rigidbody>().IsSleeping()) allAsleep = false;
                }
                for (int i = 1; i < stack.Length; i++)
                    Assert.That(stack[i].LocalPosition.y, Is.GreaterThan(stack[i - 1].LocalPosition.y), $"tick {t}: the stack kept its order");
                if (t >= Flight.Landing && allAsleep && allAsleepAt < 0) allAsleepAt = t;
                if (!allAsleep) allAsleepAt = -1;
            }
            Assert.That(allAsleepAt, Is.GreaterThanOrEqualTo(0), "every crate is asleep at the end of the flight");
            Assert.That(allAsleepAt - Flight.Landing, Is.LessThanOrEqualTo(120), "asleep within 2 s of landing");
            return slide;
        }

        private static void Settle(ConformanceMesh.Worker w, ref uint tick, int ticks)
        {
            for (int i = 0; i < ticks; i++) w.Tick(tick++);
        }

        // ------------------------------------------------------------------------------------ a whole flight

        [Test]
        public void AStackOfCratesSurvivesAWholeFlight()
        {
            MeshWith(1);
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, Vector3.zero, Quaternion.identity);
            var stack = Stack(W1, ship.Carried);
            uint tick = 1;
            Settle(W1, ref tick, 90);
            foreach (var crate in stack) Assert.IsTrue(crate.GetComponent<Rigidbody>().IsSleeping(), $"{crate} settled and slept before take-off");

            float slide = FlyAndCheck(W1, ship, stack, new[] { 0.8f, 0.5f, 0.25f }, tick);
            Assert.That(slide, Is.LessThan(0.005f), "without FrameInertia the frame feels nothing of the ship's motion");
        }

        // ------------------------------------------------------------------------------------ crossings

        [Test]
        public void ARigidbodyCrossesOutAndBackInWithItsVelocityConverted()
        {
            MeshWith(1);
            _mesh.Config.HandoverHysteresis = 0.35f;
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, Vector3.zero, Quaternion.Euler(0f, 90f, 0f));
            var box = ship.Carried;
            var crate = SpawnIn(W1, _smallCrate, box, new Vector3(0f, 0.25f, 0f));
            var body = crate.GetComponent<Rigidbody>();
            uint tick = 1;
            // The ship cruises at 1 km/s along x (its own forward, turned 90°).
            for (int i = 0; i < 10; i++)
            {
                ship.transform.position += new Vector3(1000f * Dt, 0f, 0f);
                W1.Tick(tick++);
            }
            Assume.That(box.Frame.State.HasRates, Is.True);
            Assert.That(body.linearVelocity.magnitude, Is.LessThan(0.1f), "at rest in the frame");

            // Out through the stern, past the box by more than the hysteresis.
            crate.SetLocalPose(box, new Vector3(0f, 0.25f, -(Hold.z * 0.5f + 0.5f)), Quaternion.identity);
            body.position = crate.transform.position;
            ship.transform.position += new Vector3(1000f * Dt, 0f, 0f);
            W1.Tick(tick++);
            Assert.AreSame(_space, crate.Container, "the pose owner took it out of the frame");
            Assert.IsFalse(body.isKinematic, "still simulated here");
            Assert.That(body.linearVelocity.x, Is.EqualTo(1000f).Within(5f), "it keeps the ship's velocity in the space around the frame");
            Assert.That(crate.Motion.Velocity.x, Is.EqualTo(1000f).Within(5f));

            // Back in: the ship catches it up.
            ship.transform.position += new Vector3(1000f * Dt, 0f, 0f);
            crate.transform.position = ship.transform.TransformPoint(new Vector3(0f, 0.25f, 2f));
            body.position = crate.transform.position;
            W1.Tick(tick++);
            Assert.AreSame(box, crate.Container, "the pose owner took it back into the frame");
            Assert.That(Vector3.Distance(new Vector3(0f, 0.25f, 2f), crate.LocalPosition), Is.LessThan(0.05f));
            Assert.That(body.linearVelocity.magnitude, Is.LessThan(5f), "the ship's velocity was taken off: it is nearly at rest relative to the ship");
        }

        // ------------------------------------------------------------------------------------ handover

        [Test]
        public void AHandoverOfTheShipKeepsTheCratesPosesAndSleep()
        {
            MeshWith(2);
            var w2 = _mesh[1];
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, Vector3.zero, Quaternion.identity);
            var stack = Stack(W1, ship.Carried);
            uint tick = 1;
            Settle(W1, ref tick, 90);
            var poses = new Vector3[stack.Length];
            for (int i = 0; i < stack.Length; i++)
            {
                Assume.That(stack[i].GetComponent<Rigidbody>().IsSleeping(), Is.True);
                poses[i] = stack[i].LocalPosition;
            }

            // The space is re-dealt to w2: the ship goes, and its cargo with it on the same ordered stream.
            _mesh.SetOwner(_space, w2);
            W1.Tick(tick++);
            Assert.IsFalse(ship.HasAuthority);
            _mesh.Pump();

            var theirs = new NetworkIdentity[stack.Length];
            for (int i = 0; i < stack.Length; i++)
            {
                theirs[i] = w2.Find(stack[i].NetId);
                Assert.IsNotNull(theirs[i]);
                Assert.IsTrue(theirs[i].HasAuthority, $"{theirs[i]} came with the ship");
                Assert.That(Vector3.Distance(poses[i], theirs[i].LocalPosition), Is.LessThan(1e-3f), "the pose came across exactly");
                var body = theirs[i].GetComponent<Rigidbody>();
                Assert.IsFalse(body.isKinematic, "dynamic on its new owner");
                Assert.IsTrue(body.IsSleeping(), "and still asleep");
            }
            // Tier limit: both workers' copies of the crates hang in the one registered frame. w1's are ghosts now,
            // which in a live mesh are in another process; here their colliders would overlap w2's crates.
            foreach (var mine in stack) foreach (var c in mine.GetComponentsInChildren<Collider>()) c.enabled = false;
            for (int i = 0; i < 60; i++) w2.Tick(tick++);
            for (int i = 0; i < stack.Length; i++)
            {
                Assert.That(Vector3.Distance(poses[i], theirs[i].LocalPosition), Is.LessThan(0.005f), $"{theirs[i]} did not move on its new owner");
                Assert.IsTrue(theirs[i].GetComponent<Rigidbody>().IsSleeping(), $"{theirs[i]} stayed asleep");
            }
        }
    }
}
