using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 77 (NEB-496, <c>docs/container-tree.md</c> §3, §5): a carrier with its own physics frame
    /// (B, ship-sized) that enters, rides in and leaves a much larger carrier with its own frame (A, 40 m by 100 m with a
    /// 12 m envelope over its deck) while A cruises at 200 m/s and turns at 6 degrees a second. A's aft half is a bay
    /// leased on its own (D20), to the second worker when there are two, so B rides in A while another worker simulates A.
    /// <list type="bullet">
    /// <item>Entering: B crosses into A's frame at A's box edge on A's pose owner, its riders keep their seats, and its
    /// pose composes in double through both frames with no error at the crossing; B's own frame reads its motion
    /// relative to A from the first tick after the crossing. A loose body turning with A stands still once inside.</item>
    /// <item>Floating origin: a shift of A's frame, of B's, and of both on one tick moves nothing.</item>
    /// <item>Leaving: B leaves with A's velocity added, ω × r included, and a client drawing B sees no jump.</item>
    /// <item>Walking: a rider walks out of B's frame into A's and back, and its own client predicts the walk with no
    /// correction.</item>
    /// <item>Prediction: a ship its pilot predicts crosses into and out of a translating A with no correction.</item>
    /// <item>Late join: a client that hears of the riders before their carriers draws both nested where they are.</item>
    /// <item>Persistence: riders of B are saved against B, B against A; A stowed with its cargo comes back at depth two.</item>
    /// <item>Seam handover: A crosses a worker seam with B and B's rider aboard, carrier first, in one tick.</item>
    /// <item>Overlapping boxes: a framed carrier never goes inside a smaller framed carrier whose box holds its origin.</item>
    /// </list>
    /// <para>
    /// Tier B on the <see cref="ConformanceMesh"/>, one and two workers, each with its own carried boxes; frames are Editor
    /// preview scenes. The runtime containers (the bay) are process-wide, and a frame object is shared by every worker
    /// whose entities stand in it, as the planet's frame is in scenario 40. The client halves reuse the worker's objects
    /// after the flight, as scenario 40 does, or a <see cref="NebulaClient"/> of their own after the mesh is gone; the
    /// walker is kinematic, so its prediction is checked without PhysX.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceNestedFramedCarrierTests
    {
        private const float Dt = NetworkTime.TickInterval;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>A's box: 40 m wide, 100 m long, from 2 m under its deck (y = 0) to 14 m over it.</summary>
        private static readonly Vector3 CarrierSize = new Vector3(40f, 16f, 100f);
        private static readonly Vector3 CarrierCenter = new Vector3(0f, 6f, 0f);
        /// <summary>The aft half of A, a leased runtime container in A's frame: where B lands.</summary>
        private static readonly Vector3 BayCenter = new Vector3(0f, 6f, -25f);
        private static readonly Vector3 BaySize = new Vector3(40f, 16f, 50f);
        private const ulong BayId = 4000;
        /// <summary>B's box, its floor at y = 0 in its own coordinates; its deck is wider than the box, so a walker leaves the box standing.</summary>
        private static readonly Vector3 ShipSize = new Vector3(10f, 6f, 20f);
        private static readonly Vector3 ShipCenter = new Vector3(0f, 3f, 0f);

        private const float Cruise = 200f;
        private const float TurnDegreesPerSecond = 6f;
        private static readonly Vector3 Omega = new Vector3(0f, TurnDegreesPerSecond * Mathf.Deg2Rad, 0f);
        /// <summary>A cruises round a circle about 1.9 km in radius: from here it never reaches the seam at x = 0.</summary>
        private static readonly Vector3 CarrierStart = new Vector3(-6000f, 0f, 0f);

        /// <summary>Where B rests on A's deck (A-local) and how it is turned there.</summary>
        private static readonly Vector3 Landed = new Vector3(0f, 0f, -25f);
        private static readonly Quaternion LandedTurn = Quaternion.Euler(0f, 30f, 0f);
        /// <summary>Beside A's starboard face, A-local: where B starts its approach and where it goes when it leaves.</summary>
        private static readonly Vector3 Beside = new Vector3(45f, 4f, -25f);
        private const float ApproachSeconds = 3f;
        /// <summary>The rider's seat in B, B-local.</summary>
        private static readonly Vector3 Seat = new Vector3(1f, 1f, 4f);
        private const float WalkSpeed = 6f;

        private ConformanceMesh _mesh;
        private Container _west, _east;
        private ushort _carrierPrefab, _shipPrefab, _crewPrefab, _walkerPrefab;
        private NetworkIdentity _a;
        private Container _bay;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private Action<Container> _lateRegistered;

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
        }

        [TearDown]
        public void TearDown()
        {
            if (_lateRegistered != null)
            {
                ContainerRegistry.DynamicRegistered -= _lateRegistered;
                ContainerRegistry.RuntimeRegistered -= _lateRegistered;
                _lateRegistered = null;
            }
            ContainerRegistry.WorkerIdByIndex = index => NebulaRuntime.IsServer && index == NebulaRuntime.LocalWorkerIndex ? NebulaRuntime.LocalWorkerId : "";
            PhysicsFrames.EndSimulation();
            NebulaRuntime.IsClient = false;
            NebulaRuntime.IsServer = true;
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            _mesh?.Dispose();
            _mesh = null;
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            NetworkPrefabs.Register(null);
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ fixtures

        /// <summary>
        /// A rider that walks by input, kinematically: the input is a velocity in <see cref="Reference"/>'s axes (A's),
        /// which the walker turns into the frame it stands in (B is turned in A). Server-driven on the worker, where a
        /// script gives the input; predicted with the same inputs on its client.
        /// </summary>
        internal sealed class FrameWalker : PredictedBehaviour<WalkInput>
        {
            public Container Reference;
            public Func<uint, Vector3> Script;
            public Vector3 Hands;

            protected override WalkInput GatherInput() => new WalkInput { Move = Hands };
            protected override WalkInput GatherServerInput(uint tick) => new WalkInput { Move = Script != null ? Script(tick) : Vector3.zero };

            protected override void Simulate(uint tick, in WalkInput input, float deltaTime)
            {
                var container = Identity.Container;
                var space = container != null ? container.InnerSpace : null;
                var move = Reference != null ? PhysicsFrames.ConvertLocal(Quaternion.identity, Reference, space) * input.Move : input.Move;
                Identity.SetLocalPose(container, Identity.LocalPosition + move * deltaTime, Identity.LocalRotation);
                Identity.Motion.Velocity = move;
            }
        }

        /// <summary>The prefabs, in registration order: A, B, a rider, a walker. Built twice when a client needs its own.</summary>
        private static List<GameObject> Prefabs(bool persistent)
        {
            var carrier = new GameObject("carrier-prefab");
            carrier.AddComponent<NetworkIdentity>();
            var hull = carrier.AddComponent<Container>();
            hull.ContainerId = "carrier";
            hull.Size = CarrierSize;
            hull.Center = CarrierCenter;
            hull.OwnPhysicsFrame = true;
            carrier.AddComponent<NetworkTransform>();
            Deck(carrier, new Vector3(CarrierSize.x, 0.2f, CarrierSize.z));
            if (persistent) carrier.AddComponent<PersistentEntity>();

            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var box = ship.AddComponent<Container>();
            box.ContainerId = "ship";
            box.Size = ShipSize;
            box.Center = ShipCenter;
            box.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>();
            Deck(ship, new Vector3(12f, 0.2f, 24f));
            if (persistent) ship.AddComponent<PersistentEntity>();

            var crew = new GameObject("crew-prefab");
            crew.AddComponent<NetworkIdentity>();
            crew.AddComponent<NetworkTransform>();
            if (persistent) crew.AddComponent<PersistentEntity>();

            var walker = new GameObject("walker-prefab");
            walker.AddComponent<NetworkIdentity>();
            walker.AddComponent<FrameWalker>();
            return new List<GameObject> { carrier, ship, crew, walker };
        }

        private static void Deck(GameObject owner, Vector3 size)
        {
            var deck = new GameObject("deck");
            deck.transform.SetParent(owner.transform, false);
            deck.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            deck.AddComponent<BoxCollider>().size = size;
        }

        /// <summary>
        /// One worker and one space, or two workers with the space split at x = 0 (west w1, east w2). A cruises in the
        /// west on w1; its bay is leased to the last worker.
        /// </summary>
        private void Build(int workers, bool persistent = false, Vector3? carrierAt = null)
        {
            _mesh = new ConformanceMesh(workers, perWorkerRegistry: true);
            // A live worker names the owner of a carried box from its carrier's authority through its peers
            // (NebulaWorker.ResolveWorkerId); the mesh's workers are w1..wN at indices 1..N.
            ContainerRegistry.WorkerIdByIndex = index => index >= 1 && index <= workers ? $"w{index}" : "";
            var size = new Vector3(workers == 1 ? 200000f : 100000f, 200000f, 200000f);
            _west = _mesh.AddStaticContainer(workers == 1 ? "space" : "west", new Vector3(workers == 1 ? 0f : -50000f, 0f, 0f), size);
            _west.Center = Vector3.zero;
            if (workers > 1)
            {
                _east = _mesh.AddStaticContainer("east", new Vector3(50000f, 0f, 0f), size);
                _east.Center = Vector3.zero;
            }
            ContainerRegistry.Rebuild();
            _mesh.SetOwner(_west, W1);
            if (_east != null) _mesh.SetOwner(_east, W2);
            var prefabs = Prefabs(persistent);
            _carrierPrefab = _mesh.RegisterPrefab(prefabs[0]);
            _shipPrefab = _mesh.RegisterPrefab(prefabs[1]);
            _crewPrefab = _mesh.RegisterPrefab(prefabs[2]);
            _walkerPrefab = _mesh.RegisterPrefab(prefabs[3]);

            _a = W1.SpawnServerDriven(_carrierPrefab, _west, carrierAt ?? CarrierStart, Quaternion.identity);
            Assume.That(_a.Carried, Is.Not.Null);
            Assume.That(_a.Carried.Frame, Is.Not.Null, "A has its frame");
            _bay = ContainerRegistry.RegisterRuntime(BayId, ContainerPlacement.Child(_a.Carried.ContainerId, BayCenter, BaySize, ContainerAuthority.Leased));
            Assume.That(_bay, Is.Not.Null);
            var bayOwner = _mesh[workers - 1];
            ContainerRegistry.ApplyLease(_bay.ContainerId, bayOwner.Id, bayOwner.Index, 1);
        }

        private static NetworkIdentity SpawnIn(ConformanceMesh.Worker w, ushort prefab, Container box, Vector3 local, Quaternion rotation) =>
            ConformanceFrameBodiesTests.SpawnIn(w, prefab, box, local, rotation);

        /// <summary>A point given A-local, local to <paramref name="box"/> (a container in A's frame, or A's own).</summary>
        private static Vector3 LocalTo(Container box, Vector3 aLocal) => box.OwnPhysicsFrame ? aLocal : aLocal - box.transform.localPosition;

        private NetworkIdentity Authoritative(ulong netId, out ConformanceMesh.Worker owner)
        {
            foreach (var w in _mesh.Workers)
            {
                var e = w.Find(netId);
                if (e != null && e.HasAuthority) { owner = w; return e; }
            }
            owner = null;
            return null;
        }

        private NetworkIdentity Authoritative(ulong netId) => Authoritative(netId, out _);

        private void TickAll(uint tick)
        {
            foreach (var w in _mesh.Workers) w.Tick(tick);
            _mesh.Pump();
        }

        /// <summary>A's authoritative copy one tick on along its cruise: forward at <see cref="Cruise"/>, turning about its up.</summary>
        private void MoveCarrier()
        {
            var a = Authoritative(_a.NetId) ?? _a;
            var t = a.transform;
            t.rotation = Quaternion.AngleAxis(TurnDegreesPerSecond * Dt, Vector3.up) * t.rotation;
            a.Motion.Velocity = t.forward * Cruise;
            t.position += a.Motion.Velocity * Dt;
        }

        /// <summary>Where <paramref name="e"/> stands in <paramref name="space"/>'s frame-local coordinates, composed in double through every frame between.</summary>
        private static Vector3 In(NetworkIdentity e, Container space) => PhysicsFrames.ConvertLocal(e.FramePositionPrecise, e.Space, space).ToVector3();

        /// <summary>
        /// Put B's authoritative copy at an A-local pose, moving with an A-local velocity, in whichever space it is in, as
        /// a pilot flying relative to A would.
        /// </summary>
        private void PlaceShip(NetworkIdentity b, Vector3 local, Quaternion turn, Vector3 localVelocity)
        {
            var space = b.Space;
            if (space != null)
            {
                b.transform.SetPositionAndRotation(space.Frame.LocalToSimulation(local), turn);
                b.Motion.Velocity = localVelocity;
                return;
            }
            var t = _a.transform;
            b.transform.SetPositionAndRotation(t.TransformPoint(local), t.rotation * turn);
            b.Motion.Velocity = t.forward * Cruise + Vector3.Cross(Omega, t.rotation * local) + t.rotation * localVelocity;
        }

        private static void Smooth(float t, float seconds, Vector3 from, Vector3 to, out Vector3 position, out Vector3 velocity)
        {
            float u = Mathf.Clamp01(t / seconds);
            position = Vector3.Lerp(from, to, u * u * (3f - 2f * u));
            velocity = u >= 1f ? Vector3.zero : (to - from) * (6f * u * (1f - u) / seconds);
        }

        private sealed class Ids
        {
            public ulong Ship, Crew;
        }

        /// <summary>
        /// B, spawned beside A's starboard face with a rider in its seat, flies into A's bay and sets down while A cruises
        /// and turns. Asserts the crossing on the workers' side and returns the ids.
        /// </summary>
        private Ids LandShip(ref uint tick)
        {
            int crossingsBefore = 0;
            foreach (var w in _mesh.Workers) crossingsBefore += w.Instance.FrameCrossings;
            var aT = _a.transform;
            var ship = W1.SpawnServerDriven(_shipPrefab, _west, aT.TransformPoint(Beside), aT.rotation * LandedTurn);
            Assume.That(ship.Carried != null && ship.Carried.Frame != null, "B has its frame");
            var crew = SpawnIn(W1, _crewPrefab, ship.Carried, Seat, Quaternion.identity);
            var ids = new Ids { Ship = ship.NetId, Crew = crew.NetId };
            var frameA = _a.Carried;

            float worst = 0f, worstCrew = 0f, worstShipFrameVelocity = 0f, worstShipFrameSpin = 0f, worstShipFrameAccel = 0f;
            string worstAt = "", worstFrameAt = "";
            int crossedAt = -1;
            int steps = Mathf.CeilToInt(ApproachSeconds / Dt) + 60;
            for (int i = 0; i < steps; i++, tick++)
            {
                Smooth(i * Dt, ApproachSeconds, Beside, Landed, out var local, out var localVelocity);
                MoveCarrier();
                PlaceShip(Authoritative(ids.Ship), local, LandedTurn, localVelocity);
                TickAll(tick);

                var b = Authoritative(ids.Ship, out var owner);
                Assert.IsNotNull(b, $"t{tick}: somebody simulates B");
                float error = Vector3.Distance(local, In(b, frameA));
                if (error > worst) { worst = error; worstAt = $"t{tick} on {owner.Id} in {(b.Container != null ? b.Container.ContainerId : "none")}"; }
                if (b.Space == frameA && crossedAt < 0) crossedAt = i;

                // The rider never moves in B's frame and stays under B's frame root, whoever simulates either.
                var c = Authoritative(ids.Crew, out var crewOwner);
                Assert.IsNotNull(c, $"t{tick}: somebody simulates the rider");
                Assert.AreSame(owner, crewOwner, $"t{tick}: the rider is simulated with its carrier");
                Assert.AreSame(b.Carried, c.Container, $"t{tick}: the rider is in B's box");
                Assert.AreSame(b.Carried.Frame.Root, c.transform.parent, $"t{tick}: under B's frame root");
                worstCrew = Mathf.Max(worstCrew, Vector3.Distance(Seat, b.Carried.Frame.SimulationToLocal(c.transform.position)));

                // B's own frame reads its motion in the space around it: A's frame, from the first tick after the crossing.
                var state = b.Carried.Frame.State;
                if (crossedAt >= 0 && i > crossedAt && state.HasRates)
                {
                    float v = Vector3.Distance(localVelocity, state.Velocity);
                    if (v > worstShipFrameVelocity) { worstShipFrameVelocity = v; worstFrameAt = $"t{tick}: velocity {state.Velocity} against {localVelocity}"; }
                    worstShipFrameSpin = Mathf.Max(worstShipFrameSpin, state.AngularVelocity.magnitude);
                    worstShipFrameAccel = Mathf.Max(worstShipFrameAccel, state.Acceleration.magnitude);
                }
            }
            int crossings = -crossingsBefore;
            foreach (var w in _mesh.Workers) crossings += w.Instance.FrameCrossings;
            TestContext.WriteLine($"landing: worst position error {worst * 1000f:0.000} mm at {worstAt}; rider {worstCrew * 1000f:0.000} mm; B's frame after the crossing: velocity off by {worstShipFrameVelocity:0.000} m/s ({worstFrameAt}), spin {worstShipFrameSpin:0.0000} rad/s, acceleration {worstShipFrameAccel:0.00} m/s²");

            var landed = Authoritative(ids.Ship, out var landedOwner);
            Assert.That(crossedAt, Is.GreaterThanOrEqualTo(0), "B crossed into A's frame");
            Assert.AreEqual(1, crossings, "once, on A's pose owner");
            Assert.AreSame(_bay, landed.Container, "B rests in A's bay");
            Assert.AreSame(_mesh[_mesh.Workers.Count - 1], landedOwner, "simulated by the bay's owner");
            Assert.That(worst, Is.LessThan(0.01f), "no position error at the crossing or after: " + worstAt);
            Assert.That(worstCrew, Is.LessThan(1e-3f), "the rider kept its seat");
            Assert.That(worstShipFrameVelocity, Is.LessThan(0.5f), "B's frame reads its velocity relative to A: " + worstFrameAt);
            Assert.That(worstShipFrameSpin, Is.LessThan(0.01f), "and no spin of its own");
            Assert.That(worstShipFrameAccel, Is.LessThan(60f), "and no kick of acceleration at the crossing");
            Assert.That(Vector3.Distance(Landed, landed.LocalPosition + _bay.transform.localPosition), Is.LessThan(0.01f));
            return ids;
        }

        /// <summary>A rides on in A for <paramref name="ticks"/> ticks; B and its rider never move in A's and B's frames.</summary>
        private void Ride(Ids ids, ref uint tick, int ticks)
        {
            for (int i = 0; i < ticks; i++, tick++)
            {
                MoveCarrier();
                TickAll(tick);
                var b = Authoritative(ids.Ship);
                Assert.That(Vector3.Distance(Landed, In(b, _a.Carried)), Is.LessThan(1e-3f), $"t{tick}: B rides still in A");
                var c = Authoritative(ids.Crew);
                Assert.That(Vector3.Distance(Seat, In(c, b.Carried)), Is.LessThan(1e-3f), $"t{tick}: the rider rides still in B");
            }
        }

        // ------------------------------------------------------------------------------------ entering

        [TestCase(1, TestName = "BEntersAMovingTurningCarrierAtItsBoxEdge(one worker)")]
        [TestCase(2, TestName = "BEntersAMovingTurningCarrierAtItsBoxEdge(two workers)")]
        public void BEntersAMovingTurningCarrierAtItsBoxEdge(int workers)
        {
            Build(workers);
            uint tick = 1;
            var ids = LandShip(ref tick);
            Ride(ids, ref tick, 120);
        }

        /// <summary>
        /// A loose body (a crate with a dynamic rigidbody) drifting with A, turning with it, crosses into A's frame: it
        /// stands still there. Its velocity loses A's (ω × r included), and its spin loses A's spin; rotating the spin into
        /// A's axes alone left it turning at A's rate inside A.
        /// </summary>
        [Test]
        public void ABodyTurningWithACarrierStandsStillInItsFrame()
        {
            Build(1);
            var prefab = new GameObject("loose-body-prefab");
            prefab.AddComponent<NetworkIdentity>();
            var rb = prefab.AddComponent<Rigidbody>();
            rb.useGravity = false;
            prefab.AddComponent<BoxCollider>();
            var bodies = _mesh.RegisterPrefab(prefab);
            uint tick = 1;
            for (int i = 0; i < 30; i++, tick++) { MoveCarrier(); TickAll(tick); }
            var local = new Vector3(15f, 4f, 30f); // inside A's box, clear of the bay
            MoveCarrier();
            var t = _a.transform;
            var crate = W1.SpawnServerDriven(bodies, _west, t.TransformPoint(local), t.rotation);
            var body = crate.GetComponent<Rigidbody>();
            body.position = crate.transform.position;
            body.rotation = crate.transform.rotation;
            body.linearVelocity = t.forward * Cruise + Vector3.Cross(Omega, t.rotation * local);
            body.angularVelocity = Omega;
            Assume.That(crate.Container, Is.SameAs(_west));
            TickAll(tick++);
            Assert.AreSame(_a.Carried, crate.Container, "the body crossed into A's frame");
            Assert.That(body.linearVelocity.magnitude, Is.LessThan(0.5f), $"it does not move in A ({body.linearVelocity})");
            Assert.That(body.angularVelocity.magnitude, Is.LessThan(0.005f), $"nor turn in it ({body.angularVelocity}, A turns at {Omega.y} rad/s)");
        }

        // ------------------------------------------------------------------------------------ floating origin

        [TestCase(1, TestName = "OriginShiftsInEitherFrameMoveNothing(one worker)")]
        [TestCase(2, TestName = "OriginShiftsInEitherFrameMoveNothing(two workers)")]
        public void OriginShiftsInEitherFrameMoveNothing(int workers)
        {
            Build(workers);
            uint tick = 1;
            var ids = LandShip(ref tick);
            Ride(ids, ref tick, 30);
            var b = Authoritative(ids.Ship, out var owner);
            var c = Authoritative(ids.Crew);
            var frameA = _a.Carried.Frame;
            var frameB = b.Carried.Frame;

            void Check(string what)
            {
                Assert.That(Vector3.Distance(Landed, In(b, _a.Carried)), Is.LessThan(1e-3f), what + ": B is where it was in A");
                Assert.That(Vector3.Distance(Landed, frameA.SimulationToLocal(b.transform.position)), Is.LessThan(1e-3f), what + ": and A's frame reads it there");
                Assert.That(Vector3.Distance(Seat, In(c, b.Carried)), Is.LessThan(1e-3f), what + ": the rider is where it was in B");
                Assert.That(Vector3.Distance(Seat, frameB.SimulationToLocal(c.transform.position)), Is.LessThan(1e-3f), what + ": and B's frame reads it there");
                var truth = _a.transform.TransformPoint(Landed + LandedTurn * Seat);
                Assert.That(Vector3.Distance(truth, c.ScopePositionPrecise.ToVector3()), Is.LessThan(0.002f), what + ": the rider is where it was in the scope");
            }

            void TickAndCheckRates(string what)
            {
                for (int i = 0; i < 3; i++, tick++)
                {
                    MoveCarrier();
                    TickAll(tick);
                    Check($"{what}, t{tick}");
                    Assert.That(frameB.State.Velocity.magnitude, Is.LessThan(0.05f), $"{what}, t{tick}: B's frame reads no motion from the shift");
                    Assert.That(frameA.State.Velocity.magnitude, Is.EqualTo(Cruise).Within(1f), $"{what}, t{tick}: nor does A's");
                }
            }

            Check("before");
            owner.Act(() => PhysicsFrames.ShiftOrigin(frameA, new Vector3(1024f, 0f, -2048f)));
            Check("A's origin shifted");
            TickAndCheckRates("after A's shift");

            owner.Act(() => PhysicsFrames.ShiftOrigin(frameB, new Vector3(-1024f, 0f, 3072f)));
            Assert.That(c.transform.position.magnitude, Is.GreaterThan(2000f), "B's frame root moved in simulation space");
            Check("B's origin shifted");
            TickAndCheckRates("after B's shift");

            owner.Act(() =>
            {
                PhysicsFrames.ShiftOrigin(frameA, new Vector3(-3072f, 0f, 1024f));
                PhysicsFrames.ShiftOrigin(frameB, Vector3.zero);
            });
            Check("both shifted on one tick");
            TickAndCheckRates("after both shifted");
            Ride(ids, ref tick, 30);
        }

        // ------------------------------------------------------------------------------------ leaving

        private struct Sample
        {
            public uint Tick;
            public ushort Worker;
            public EntityStateEntry Ship;
            public Vector3 CarrierPosition;
            public Quaternion CarrierRotation;
            public Vector3 Truth;
        }

        private static EntityStateEntry Sent(NetworkIdentity e)
        {
            var entry = EntityStateEntry.Snapshot(e);
            entry.Fields |= TransformFields.Location | TransformFields.Reliable;
            var w = new NetworkWriter();
            entry.Write(w);
            return EntityStateEntry.Read(new NetworkReader(w.ToSegment()));
        }

        /// <summary>
        /// B takes off from A's bay and leaves through A's starboard face while A cruises and turns. Returns what a
        /// client is sent of B each tick.
        /// </summary>
        private List<Sample> TakeOff(Ids ids, ref uint tick)
        {
            var stream = new List<Sample>();
            int handoffsBefore = 0, crossingsBefore = 0;
            foreach (var w in _mesh.Workers) { handoffsBefore += w.Instance.CrossingHandoffs; crossingsBefore += w.Instance.FrameCrossings; }
            var depart = Beside + new Vector3(15f, 1f, 0f);
            float worst = 0f, worstCrew = 0f, worstVelocity = -1f;
            string worstAt = "", velocityAt = "";
            bool outside = false;
            int steps = Mathf.CeilToInt(ApproachSeconds / Dt) + 60;
            for (int i = 0; i < steps; i++, tick++)
            {
                Smooth(i * Dt, ApproachSeconds, Landed, depart, out var local, out var localVelocity);
                MoveCarrier();
                PlaceShip(Authoritative(ids.Ship), local, LandedTurn, localVelocity);
                TickAll(tick);

                var b = Authoritative(ids.Ship, out var owner);
                Assert.IsNotNull(b, $"t{tick}: somebody simulates B");
                float error = Vector3.Distance(local, In(b, _a.Carried));
                if (error > worst) { worst = error; worstAt = $"t{tick} on {owner.Id} in {(b.Container != null ? b.Container.ContainerId : "none")}"; }
                if (b.Space == null && !outside)
                {
                    // The tick it left A's frame: the velocity it carries out is A's at that point plus its own.
                    outside = true;
                    var t = _a.transform;
                    var expected = t.forward * Cruise + Vector3.Cross(Omega, t.rotation * local) + t.rotation * localVelocity;
                    worstVelocity = Vector3.Distance(expected, b.Motion.Velocity);
                    velocityAt = $"t{tick}: {b.Motion.Velocity} against {expected} (A's own {t.forward * Cruise}, ω × r {Vector3.Cross(Omega, t.rotation * local)})";
                }
                var c = Authoritative(ids.Crew);
                Assert.AreSame(b.Carried, c.Container, $"t{tick}: the rider is in B's box");
                worstCrew = Mathf.Max(worstCrew, Vector3.Distance(Seat, b.Carried.Frame.SimulationToLocal(c.transform.position)));
                var streamed = b;
                ushort index = owner.Index;
                uint now = tick;
                owner.Act(() => stream.Add(new Sample
                {
                    Tick = now, Worker = index, Ship = Sent(streamed), CarrierPosition = _a.transform.position,
                    CarrierRotation = _a.transform.rotation, Truth = _a.transform.TransformPoint(local),
                }));
            }
            int handoffs = -handoffsBefore, crossings = -crossingsBefore;
            foreach (var w in _mesh.Workers) { handoffs += w.Instance.CrossingHandoffs; crossings += w.Instance.FrameCrossings; }
            TestContext.WriteLine($"take-off: worst position error {worst * 1000f:0.000} mm at {worstAt}; rider {worstCrew * 1000f:0.000} mm; velocity carried out off by {worstVelocity:0.000} m/s ({velocityAt}); crossings {crossings}, hand-offs to the pose owner {handoffs}");
            var gone = Authoritative(ids.Ship, out var finalOwner);
            Assert.IsTrue(outside, "B left A's frame");
            Assert.AreSame(_west, gone.Container, "out in space");
            Assert.AreSame(W1, finalOwner, "through A's pose owner");
            Assert.AreEqual(1, crossings, "one crossing out");
            Assert.AreEqual(_mesh.Workers.Count - 1, handoffs, "the bay's other owner hands B to A's pose owner for it");
            Assert.That(worst, Is.LessThan(0.01f), "no position error at the crossing: " + worstAt);
            Assert.That(worstCrew, Is.LessThan(1e-3f), "the rider kept its seat");
            Assert.That(worstVelocity, Is.LessThan(0.5f), "B leaves with A's velocity, ω × r included: " + velocityAt);
            return stream;
        }

        [TestCase(1, TestName = "BLeavesAMovingCarrierWithItsVelocity(one worker)")]
        [TestCase(2, TestName = "BLeavesAMovingCarrierWithItsVelocity(two workers)")]
        public void BLeavesAMovingCarrierWithItsVelocity(int workers)
        {
            Build(workers);
            uint tick = 1;
            var ids = LandShip(ref tick);
            Ride(ids, ref tick, 60);
            TakeOff(ids, ref tick);
        }

        /// <summary>
        /// The pilot's client draws B, interpolating two ticks behind with A interpolated as a client holds it, from
        /// exactly what B's workers streamed: no jump at the crossing out of A's frame (the pilot's own pose is B's
        /// pose times its seat, so a jump of B is a jump of the pilot).
        /// </summary>
        [TestCase(1, TestName = "AClientDrawsBLeavingWithoutAJump(one worker)")]
        [TestCase(2, TestName = "AClientDrawsBLeavingWithoutAJump(two workers)")]
        public void AClientDrawsBLeavingWithoutAJump(int workers)
        {
            Build(workers);
            uint tick = 1;
            var ids = LandShip(ref tick);
            Ride(ids, ref tick, 60);
            var stream = TakeOff(ids, ref tick);

            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            var go = new GameObject("ship-on-a-client");
            _objects.Add(go);
            var copy = go.AddComponent<NetworkIdentity>();
            go.AddComponent<NetworkTransform>().Interpolate = true;
            copy.Initialize();
            copy.NetId = stream[0].Ship.NetId;
            copy.Epoch = stream[0].Ship.Epoch;
            copy.InvokeSpawn();
            var carrierBuffer = _a.gameObject.AddComponent<RemoteInterpolator>();
            _a.Interpolator = carrierBuffer;

            const double Delay = 2.0;
            const int SubSteps = 4;
            float worst = 0f, worstJump = 0f;
            string worstAt = "", worstJumpAt = "";
            Vector3 lastShown = default, lastTruth = default;
            bool haveLast = false;
            int compared = 0;
            for (int i = 0; i < stream.Count; i++)
            {
                carrierBuffer.Push(stream[i].Tick, stream[i].CarrierPosition, stream[i].CarrierRotation, Vector3.zero);
                if (!copy.ReceiveState(stream[i].Tick, stream[i].Worker, stream[i].Ship)) continue;
                for (int k = 0; k < SubSteps; k++)
                {
                    double render = stream[i].Tick - Delay + (double)k / SubSteps;
                    int at = (int)Math.Floor(render) - (int)stream[0].Tick;
                    if (at < 0 || at + 1 >= stream.Count) continue;
                    float f = (float)(render - Math.Floor(render));
                    _a.transform.SetPositionAndRotation(
                        Vector3.Lerp(stream[at].CarrierPosition, stream[at + 1].CarrierPosition, f),
                        Quaternion.Slerp(stream[at].CarrierRotation, stream[at + 1].CarrierRotation, f));
                    PhysicsFrames.PoseForRender();
                    ContainerRegistry.RefreshCaches();
                    copy.RemoteTick(render);
                    var shown = copy.Container != null ? copy.Container.ToWorld(copy.LocalPosition) : copy.LocalPosition;
                    var truth = Vector3.Lerp(stream[at].Truth, stream[at + 1].Truth, f);
                    float error = Vector3.Distance(truth, shown);
                    if (error > worst) { worst = error; worstAt = $"render tick {render:0.00} in {(copy.Container != null ? copy.Container.ContainerId : "none")}: shown {shown}, truth {truth}"; }
                    if (haveLast)
                    {
                        float jump = Mathf.Abs(Vector3.Distance(shown, lastShown) - Vector3.Distance(truth, lastTruth));
                        if (jump > worstJump) { worstJump = jump; worstJumpAt = $"render tick {render:0.00} in {(copy.Container != null ? copy.Container.ContainerId : "none")}"; }
                    }
                    lastShown = shown;
                    lastTruth = truth;
                    haveLast = true;
                    compared++;
                }
            }
            TestContext.WriteLine($"client: {compared} render steps; worst error {worst * 1000f:0.0} mm at {worstAt}; worst jump {worstJump * 1000f:0.0} mm at {worstJumpAt}");
            Assert.That(compared, Is.GreaterThan(stream.Count * 3), "every render step was compared");
            Assert.That(worst, Is.LessThan(0.01f), "drawn within a centimetre of B everywhere: " + worstAt);
            Assert.That(worstJump, Is.LessThan(0.01f), "and no step jumps by a centimetre: " + worstJumpAt);
        }

        // ------------------------------------------------------------------------------------ walking between frames

        private struct WalkStep
        {
            public uint Tick;
            public Vector3 Input;
            public Container Container;
            public byte[] State;
            public Vector3 InA;
            public Vector3 CarrierPosition;
            public Quaternion CarrierRotation;
        }

        /// <summary>
        /// A walker in B walks out through B's stern into A's bay, along A's deck into A's fore part, and back to where it
        /// started in B, at <see cref="WalkSpeed"/>, while A cruises and turns. Its A-local position is continuous at every
        /// tick and its velocity is carried into each frame. Returns what its worker reported each tick.
        /// </summary>
        private List<WalkStep> Walk(Ids ids, ref uint tick)
        {
            var b = Authoritative(ids.Ship, out var shipOwner);
            var start = new Vector3(0f, 1f, 2f);
            var walker = SpawnIn(shipOwner, _walkerPrefab, b.Carried, start, Quaternion.identity);
            ulong walkerId = walker.NetId;
            var stern = Landed + LandedTurn * new Vector3(0f, 1f, -12f);
            var aside = new Vector3(-15f, 1f, stern.z);
            var fore = new Vector3(-15f, 1f, 12f);
            var origin = Landed + LandedTurn * start;
            var waypoints = new[] { stern, aside, fore, aside, stern, origin };
            int next = 0;
            var steps = new List<WalkStep>();
            var visited = new List<string>();
            int crossingsBefore = 0;
            foreach (var w in _mesh.Workers) crossingsBefore += w.Instance.FrameCrossings;
            float worstVelocity = 0f;
            string velocityAt = "";
            Vector3 last = origin;
            for (int guard = 0; guard < 4000 && next < waypoints.Length; guard++, tick++)
            {
                var mine = Authoritative(walkerId, out var owner);
                var here = In(mine, _a.Carried);
                var to = waypoints[next] - here;
                if (to.magnitude < 1e-3f) { next++; tick--; continue; }
                var input = to.magnitude > WalkSpeed * Dt ? to.normalized * WalkSpeed : to / Dt;
                var legs = mine.GetComponent<FrameWalker>();
                legs.Reference = _a.Carried;
                legs.Script = _ => input;
                var before = mine.Container;
                MoveCarrier();
                TickAll(tick);
                mine = Authoritative(walkerId, out owner);
                Assert.IsNotNull(mine, $"t{tick}: somebody simulates the walker");
                var now = In(mine, _a.Carried);
                Assert.That(Vector3.Distance(last, now), Is.LessThan(WalkSpeed * Dt + 1e-3f), $"t{tick}: no jump in A ({last} -> {now}, {before?.ContainerId} -> {mine.Container?.ContainerId})");
                if (mine.Container != before || visited.Count == 0)
                {
                    visited.Add($"{(mine.Container == b.Carried ? "B" : mine.Container == _bay ? "bay" : mine.Container == _a.Carried ? "A" : mine.Container?.ContainerId)}@{owner.Id}");
                    // The velocity it was given in A's axes, carried into the space it is in now.
                    var expected = PhysicsFrames.ConvertLocal(Quaternion.identity, _a.Carried, mine.Space) * input;
                    float v = Vector3.Distance(expected, mine.Motion.Velocity);
                    if (v > worstVelocity) { worstVelocity = v; velocityAt = $"t{tick} into {visited[visited.Count - 1]}: {mine.Motion.Velocity} against {expected}"; }
                }
                last = now;
                var state = new NetworkWriter(64);
                mine.Predicted.WriteOwnerState(state);
                steps.Add(new WalkStep
                {
                    Tick = tick, Input = input, Container = mine.Container, State = state.ToArray(), InA = now,
                    CarrierPosition = _a.transform.position, CarrierRotation = _a.transform.rotation,
                });
            }
            int crossings = -crossingsBefore;
            foreach (var w in _mesh.Workers) crossings += w.Instance.FrameCrossings;
            TestContext.WriteLine($"walk: {string.Join(" -> ", visited)}; frame crossings {crossings}; velocity carried across off by {worstVelocity:0.0000} m/s ({velocityAt})");
            Assert.AreEqual(waypoints.Length, next, "the walk was finished");
            var end = Authoritative(walkerId);
            Assert.AreSame(Authoritative(ids.Ship).Carried, end.Container, "back in B");
            Assert.AreEqual(2, crossings, "out of B's frame once and back in once");
            Assert.That(visited, Has.Some.StartsWith("bay@"), "it walked in A's bay");
            Assert.That(visited, Has.Some.StartsWith("A@w1"), "and in A's fore part, on A's worker");
            Assert.That(worstVelocity, Is.LessThan(0.05f), "its velocity is carried into each frame: " + velocityAt);
            return steps;
        }

        [TestCase(1, TestName = "ARiderWalksFromBIntoAAndBack(one worker)")]
        [TestCase(2, TestName = "ARiderWalksFromBIntoAAndBack(two workers)")]
        public void ARiderWalksFromBIntoAAndBack(int workers)
        {
            Build(workers);
            uint tick = 1;
            var ids = LandShip(ref tick);
            Ride(ids, ref tick, 30);
            Walk(ids, ref tick);
        }

        /// <summary>
        /// The walker's own client predicts the walk with the inputs it sent, three ticks ahead of its worker, and
        /// reconciles with the worker's owner state as <c>NebulaClient.OnOwnerState</c> does: the container named first,
        /// then the state, in that container's frame at its simulation pose. The prediction recorded in B matches the
        /// worker's state in A's bay and back: no correction, and the walker is drawn where it is in A at every step.
        /// One worker: the client reuses the worker's frames, as scenario 40's client half does.
        /// </summary>
        [Test]
        public void TheWalkersClientPredictsTheWalkWithoutACorrection()
        {
            Build(1);
            uint tick = 1;
            var ids = LandShip(ref tick);
            Ride(ids, ref tick, 30);
            var steps = Walk(ids, ref tick);
            var b = Authoritative(ids.Ship);

            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            var go = new GameObject("walker-on-its-client");
            _objects.Add(go);
            var legs = go.AddComponent<FrameWalker>();
            legs.Reference = _a.Carried;
            var pawn = go.GetComponent<NetworkIdentity>();
            pawn.Initialize();
            pawn.NetId = 990;
            pawn.Epoch = 1;
            pawn.IsLocalPlayer = true;
            pawn.InvokeSpawn();
            pawn.SetContainer(b.Carried);
            const int Lag = 3;
            var input = new NetworkWriter(32);
            float worstDrawn = 0f;
            string worstAt = "";
            for (int n = 0; n < steps.Count; n++)
            {
                var s = steps[n];
                _a.transform.SetPositionAndRotation(s.CarrierPosition, s.CarrierRotation);
                PhysicsFrames.PoseForRender();
                if (n == 0) pawn.SetLocalPose(b.Carried, new Vector3(0f, 1f, 2f), Quaternion.identity);
                NetworkTime.Tick = s.Tick;
                PhysicsFrames.BeginSimulation(pawn.Container != null ? pawn.Container.InnerSpace : null, pawn.transform);
                legs.Hands = s.Input;
                input.Reset();
                pawn.Predicted.ClientPredictTick(s.Tick, input);
                PhysicsFrames.EndSimulation();
                if (n >= Lag)
                {
                    var r = steps[n - Lag];
                    if (r.Container != pawn.Container)
                    {
                        PhysicsFrames.ReleaseSimulationPose();
                        pawn.SetContainer(r.Container);
                    }
                    PhysicsFrames.BeginSimulation(r.Container != null ? r.Container.InnerSpace : null, pawn.transform);
                    pawn.Predicted.ClientReconcile(r.Tick, new NetworkReader(r.State));
                    PhysicsFrames.EndSimulation();
                }
                PhysicsFrames.PoseForRender();
                var drawn = _a.Carried.Frame.RenderToLocalPrecise(Double3.From(pawn.transform.position)).ToVector3();
                float error = Vector3.Distance(s.InA, drawn);
                if (error > worstDrawn) { worstDrawn = error; worstAt = $"t{s.Tick} in {(pawn.Container == b.Carried ? "B" : pawn.Container?.ContainerId)}: drawn {drawn}, worker {s.InA}"; }
            }
            TestContext.WriteLine($"client: {steps.Count} steps, corrections {pawn.Predicted.Corrections}, largest {pawn.Predicted.MaxCorrectionMagnitude * 1000f:0.000} mm; worst drawn error in A {worstDrawn * 1000f:0.000} mm at {worstAt}");
            Assert.AreEqual(0, pawn.Predicted.Corrections, "the prediction recorded in one frame matches the worker's state in the other");
            Assert.That(worstDrawn, Is.LessThan(0.005f), "drawn where it is in A at every step: " + worstAt);
            Assert.AreSame(b.Carried, pawn.Container, "back in B");
        }

        // ------------------------------------------------------------------------------------ a pilot-predicted ship

        /// <summary>
        /// A ship that coasts: each tick it moves by its velocity in the space it is in, and the input (an acceleration)
        /// changes the velocity. Predicted by its pilot's client with the same inputs; its state is its pose and velocity in
        /// its container, which Nebula converts at a crossing on the worker.
        /// </summary>
        internal sealed class Coasting : PredictedBehaviour<WalkInput>
        {
            protected override WalkInput GatherInput() => default;

            protected override void Simulate(uint tick, in WalkInput input, float deltaTime)
            {
                Identity.Motion.Velocity += input.Move * deltaTime;
                Identity.SetLocalPose(Identity.Container, Identity.LocalPosition + Identity.Motion.Velocity * deltaTime, Identity.LocalRotation);
            }
        }

        private struct PilotStep
        {
            public uint Tick;
            public Container Container;
            public byte[] State;
            public Vector3 CarrierPosition;
            public Quaternion CarrierRotation;
        }

        /// <summary>Which part of a carrier the pilot's ship flies through, and where the carrier is.</summary>
        public enum PilotPath
        {
            /// <summary>A's bay, a leased container in A's frame; A a few kilometres from the scene's origin.</summary>
            Bay,
            /// <summary>A's own box, a few kilometres out.</summary>
            OwnBox,
            /// <summary>
            /// A carrier's own box, with the carrier ten thousand kilometres from the worker's scene origin (the space of a
            /// star system held far out), where a float is a metre apart.
            /// </summary>
            Far,
        }

        /// <summary>
        /// The pilot's own prediction of a ship crossing into a carrier that translates at <paramref name="speed"/>, and out
        /// of it on the far side (<c>docs/portals.md</c> §13 R3: no correction over 0.1 m). The worker flies the ship (a
        /// framed carrier, coasting 10 m/s across the carrier); the pilot's client predicts it three ticks ahead of the
        /// worker, holds the carrier's state one tick behind the ship's owner state (they travel separately, and the
        /// owner's report of a tick can come first), draws the carrier two ticks behind that, and reconciles each tick with
        /// the worker's owner state as <c>NebulaClient.OnOwnerState</c> does: the container named first, then the state.
        /// <para>
        /// What a game showed (1.4 to 1.7 m at 25 m/s, then 5 to 30 cm): the client related the prediction, recorded in the
        /// container the ship left, to the worker's state in the one it entered through the carrier as it is drawn rather
        /// than as it stood at that tick, and then only up to its newest sample, a tick short; and the worker converted the
        /// ship's pose at the crossing through Unity's float world positions, which ten thousand kilometres out are a metre
        /// apart (<see cref="PilotPath.Far"/>, at 60 m/s so the carrier moves a whole float step a tick and its frame's
        /// sampled velocity is exact).
        /// </para>
        /// </summary>
        [TestCase(25f, PilotPath.Bay, TestName = "APilotPredictsItsShipAcrossAMovingCarrierWithoutACorrection(25 m/s, through the bay)")]
        [TestCase(100f, PilotPath.Bay, TestName = "APilotPredictsItsShipAcrossAMovingCarrierWithoutACorrection(100 m/s, through the bay)")]
        [TestCase(25f, PilotPath.OwnBox, TestName = "APilotPredictsItsShipAcrossAMovingCarrierWithoutACorrection(25 m/s, through A's own box)")]
        [TestCase(100f, PilotPath.OwnBox, TestName = "APilotPredictsItsShipAcrossAMovingCarrierWithoutACorrection(100 m/s, through A's own box)")]
        [TestCase(60f, PilotPath.Far, TestName = "APilotPredictsItsShipAcrossAMovingCarrierWithoutACorrection(60 m/s, 10,000 km out)")]
        public void APilotPredictsItsShipAcrossAMovingCarrierWithoutACorrection(float speed, PilotPath path)
        {
            Build(1);
            var prefab = Prefabs(false)[1];
            prefab.name = "pilot-ship-prefab";
            prefab.AddComponent<Coasting>();
            ushort pilotShip = _mesh.RegisterPrefab(prefab);
            Container space = _west;
            NetworkIdentity carrier = _a;
            var heading = Quaternion.identity;
            var farOffset = new Vector3(1.0e7f, 0f, 0f); // a float is a metre apart here
            if (path == PilotPath.Far)
            {
                space = _mesh.AddStaticContainer("far", farOffset, new Vector3(20000f, 20000f, 20000f));
                space.Center = Vector3.zero;
                ContainerRegistry.Rebuild();
                _mesh.SetOwner(space, W1);
                heading = Quaternion.Euler(0f, 90f, 0f); // flying +x, a whole metre a tick at 60 m/s
                carrier = W1.SpawnServerDriven(_carrierPrefab, space, farOffset, heading);
                carrier.transform.localPosition = Vector3.zero;
            }
            var carrierVelocity = heading * new Vector3(0f, 0f, speed);
            void Translate()
            {
                carrier.Motion.Velocity = carrierVelocity;
                carrier.transform.localPosition += carrierVelocity * Dt; // in its container's small numbers, as a game moves it
            }
            uint tick = 1;
            for (int i = 0; i < 30; i++, tick++) { Translate(); TickAll(tick); }
            // Carrier-local; off the metre grid far out, so a pose rounded to a float there shows.
            var relative = new Vector3(28f, 3f, path == PilotPath.Bay ? -20f : path == PilotPath.Far ? 20.37f : 20f);
            var relativeVelocity = new Vector3(-10f, 0f, 0f);
            var ship = W1.SpawnServerDriven(pilotShip, space, carrier.transform.position, Quaternion.identity);
            ship.transform.localPosition = carrier.transform.localPosition + heading * relative;
            ship.Motion.Velocity = carrierVelocity + heading * relativeVelocity;
            var steps = new List<PilotStep>();
            var containers = new List<Container>();
            float worstIn = 0f, worstOut = 0f, worstVelocity = 0f;
            var lastContainer = ship.Container;
            var lastLocal = ship.LocalPosition;
            var lastVelocity = ship.Motion.Velocity;
            for (int i = 0; i < 400; i++, tick++)
            {
                Translate();
                TickAll(tick);
                if (ship.Container != lastContainer)
                {
                    // Where the ship's own step put it this tick, in the space it left (the same float sum its simulation
                    // made), carried into the space it entered in double: what the crossing should have written.
                    var stepped = Double3.From(lastLocal + lastVelocity * Dt);
                    var carrierLocal = Double3.From(carrier.transform.localPosition);
                    if (lastContainer == space)
                    {
                        var expected = PhysicsFrames.Rotate(Quaternion.Inverse(heading), stepped - carrierLocal);
                        worstIn = Mathf.Max(worstIn, (float)Distance(expected, Double3.From(In(ship, carrier.Carried))));
                    }
                    else
                    {
                        var expected = carrierLocal + PhysicsFrames.Rotate(heading, stepped);
                        worstOut = Mathf.Max(worstOut, (float)Distance(expected, Double3.From(ship.LocalPosition)));
                    }
                }
                if (ship.Container != space) worstVelocity = Mathf.Max(worstVelocity, Vector3.Distance(relativeVelocity, ship.Motion.Velocity));
                lastContainer = ship.Container;
                lastLocal = ship.Container == space ? ship.LocalPosition : In(ship, carrier.Carried);
                lastVelocity = ship.Motion.Velocity;
                var state = new NetworkWriter(64);
                ship.Predicted.WriteOwnerState(state);
                steps.Add(new PilotStep { Tick = tick, Container = ship.Container, State = state.ToArray(), CarrierPosition = carrier.transform.localPosition, CarrierRotation = carrier.transform.localRotation });
                if (containers.Count == 0 || containers[containers.Count - 1] != ship.Container) containers.Add(ship.Container);
            }
            TestContext.WriteLine($"{path}, carrier at {speed} m/s: containers {string.Join(" -> ", containers.ConvertAll(c => c.ContainerId))}; on the worker, pose written at the crossing in {worstIn * 1000f:0.000} mm, out {worstOut * 1000f:0.000} mm, velocity in the carrier {worstVelocity:0.000} m/s");
            Assume.That(containers.Count, Is.EqualTo(3), "the ship flew into the carrier and out of it again");
            Assume.That(containers[1], Is.SameAs(path == PilotPath.Bay ? _bay : carrier.Carried), "through the part of the carrier asked for");
            Assert.That(worstIn, Is.LessThan(0.002f), "the worker put the ship where it was, in the carrier's coordinates");
            Assert.That(worstOut, Is.LessThan(0.002f), "and back where it was in space");
            Assert.That(worstVelocity, Is.LessThan(0.1f), "with the carrier's velocity taken off");

            // The pilot's client keeps its origin near what it draws: the far scope is moved to Unity's origin on its side.
            if (path == PilotPath.Far)
            {
                space.transform.position = Vector3.zero;
                space.RefreshCache();
            }
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            var go = new GameObject("ship-on-its-pilots-client");
            _objects.Add(go);
            go.AddComponent<Coasting>();
            var pawn = go.GetComponent<NetworkIdentity>();
            pawn.Initialize();
            pawn.NetId = 991;
            pawn.Epoch = 1;
            pawn.IsLocalPlayer = true;
            pawn.InvokeSpawn();
            pawn.SetContainer(steps[0].Container);
            var buffer = carrier.gameObject.AddComponent<RemoteInterpolator>();
            carrier.Interpolator = buffer;
            const int Lead = 3, CarrierLag = 1, Delay = 2;
            var input = new NetworkWriter(32);
            for (int n = 0; n < steps.Count; n++)
            {
                if (n == 0)
                {
                    // Where the worker had it at its first recorded tick, before predicting the next.
                    pawn.Predicted.ClientReconcile(steps[0].Tick, new NetworkReader(steps[0].State));
                    continue;
                }
                int heard = n - Lead - CarrierLag;
                if (heard >= 0) buffer.Push(steps[heard].Tick, space, steps[heard].CarrierPosition, steps[heard].CarrierRotation, carrierVelocity);
                var drawn = steps[Mathf.Max(0, heard - Delay)];
                carrier.transform.SetLocalPositionAndRotation(drawn.CarrierPosition, drawn.CarrierRotation);
                PhysicsFrames.PoseForRender();
                ContainerRegistry.RefreshCaches();
                if (n >= Lead)
                {
                    var r = steps[n - Lead];
                    if (r.Container != pawn.Container)
                    {
                        PhysicsFrames.ReleaseSimulationPose();
                        pawn.SetContainer(r.Container);
                    }
                    PhysicsFrames.BeginSimulation(r.Container != null ? r.Container.InnerSpace : null, pawn.transform);
                    pawn.Predicted.ClientReconcile(r.Tick, new NetworkReader(r.State));
                    PhysicsFrames.EndSimulation();
                }
                NetworkTime.Tick = steps[n].Tick;
                PhysicsFrames.BeginSimulation(pawn.Container != null ? pawn.Container.InnerSpace : null, pawn.transform);
                input.Reset();
                pawn.Predicted.ClientPredictTick(steps[n].Tick, input);
                PhysicsFrames.EndSimulation();
            }
            TestContext.WriteLine($"client: {pawn.Predicted.Corrections} corrections, largest {pawn.Predicted.MaxCorrectionMagnitude * 1000f:0.0} mm, the ship ended in {pawn.Container?.ContainerId}");
            Assert.AreSame(space, pawn.Container, "the client followed the ship in and out");
            Assert.That(pawn.Predicted.MaxCorrectionMagnitude, Is.LessThanOrEqualTo(0.1f), "no correction over 0.1 m at either crossing (R3)");
            Assert.AreEqual(0, pawn.Predicted.Corrections, "nor any correction at all: the prediction agrees with the worker in both spaces");
        }

        // ------------------------------------------------------------------------------------ late join

        private sealed class ClientWorld
        {
            public NebulaClient Client;
            public IDictionary Entities;
            public NetworkIdentity Get(ulong netId)
            {
                var keys = new List<string>();
                foreach (var k in Entities.Keys) keys.Add(k.ToString());
                Assert.IsTrue(Entities.Contains(netId), $"the client holds {netId} (it holds {string.Join(", ", keys)})");
                return (NetworkIdentity)Entities[netId];
            }
        }

        /// <summary>
        /// A client that joins while B rides in A's bay with its rider aboard. Its spawns arrive in the order given, the
        /// riders possibly before their carriers; once all are here it draws A, B and the rider exactly where the worker
        /// has them, B's frame composed through A's.
        /// </summary>
        [TestCase(false, TestName = "ALateJoinerSeesBothCarriersNested(carriers first)")]
        [TestCase(true, TestName = "ALateJoinerSeesBothCarriersNested(riders first)")]
        public void ALateJoinerSeesBothCarriersNested(bool ridersFirst)
        {
            Build(1);
            var b = SpawnIn(W1, _shipPrefab, _bay, LocalTo(_bay, Landed), LandedTurn);
            var c = SpawnIn(W1, _crewPrefab, b.Carried, Seat, Quaternion.identity);
            uint tick = 1;
            var ids = new Ids { Ship = b.NetId, Crew = c.NetId };
            Ride(ids, ref tick, 60);
            Assume.That(b.Container, Is.SameAs(_bay));
            var aPosition = Double3.From(_a.transform.position);
            var aRotation = _a.transform.rotation;
            var shipTruth = aPosition + PhysicsFrames.Rotate(aRotation, Double3.From(Landed));
            var crewTruth = aPosition + PhysicsFrames.Rotate(aRotation, Double3.From(Landed) + PhysicsFrames.Rotate(LandedTurn, Double3.From(Seat)));
            EntitySpawnMsg aMsg = default, bMsg = default, cMsg = default;
            W1.Act(() =>
            {
                aMsg = EntitySpawnMsg.From(_a, new NetworkWriter(256));
                bMsg = EntitySpawnMsg.From(b, new NetworkWriter(256));
                cMsg = EntitySpawnMsg.From(c, new NetworkWriter(256));
            });
            string carrierId = _a.Carried.ContainerId, bayId = _bay.ContainerId;
            ulong aId = _a.NetId;

            // The mesh goes: the client is another process, with its own copies of everything.
            _mesh.Dispose();
            _mesh = null;
            foreach (var r in new List<Container>(ContainerRegistry.Runtime)) if (r != null) Object.DestroyImmediate(r.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            PhysicsFrames.DrainPool();
            var world = JoinAsClient();
            // The bay's row, as the client's container-ownership update registers it: held until A is here.
            ContainerRegistry.RegisterRuntime(BayId, ContainerPlacement.Child(carrierId, BayCenter, BaySize, ContainerAuthority.Leased));
            var spawn = typeof(NebulaClient).GetMethod("OnEntitySpawn", Private);
            var order = ridersFirst ? new[] { cMsg, bMsg, aMsg } : new[] { aMsg, bMsg, cMsg };
            foreach (var msg in order) spawn.Invoke(world.Client, new object[] { msg });
            PhysicsFrames.PoseForRender();
            ContainerRegistry.RefreshCaches();

            var a2 = world.Get(aId);
            var b2 = world.Get(ids.Ship);
            var c2 = world.Get(ids.Crew);
            Assert.IsNotNull(a2.Carried, "A's box is here");
            var bay = b2.Container;
            Assert.AreEqual(bayId, bay != null ? bay.ContainerId : null, "B is in A's bay");
            Assert.AreSame(a2.Carried, bay.Parent, "the bay is in A");
            Assert.AreSame(b2.Carried, c2.Container, "the rider is in B");
            Assert.AreSame(b2.Carried.Frame.Root, c2.transform.parent, "under B's frame root");
            Assert.That(Distance(shipTruth, Double3.From(b2.transform.position)), Is.LessThan(1e-3), "B is drawn where it is");
            Assert.That(Distance(shipTruth, b2.Carried.Frame.LocalToRenderPrecise(Double3.Zero)), Is.LessThan(1e-3), "and so is its frame");
            Assert.That(Distance(crewTruth, Double3.From(c2.transform.position)), Is.LessThan(1e-3), "the rider is drawn where it is, through both frames");
            Assert.That(Distance(crewTruth, b2.Carried.Frame.LocalToRenderPrecise(Double3.From(Seat))), Is.LessThan(1e-3));
        }

        private static double Distance(Double3 a, Double3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

        /// <summary>A client process: the scope's static container, the prefabs, and a <see cref="NebulaClient"/> in game.</summary>
        private ClientWorld JoinAsClient()
        {
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            var space = new GameObject("space");
            _objects.Add(space);
            var box = space.AddComponent<Container>();
            box.ContainerId = "space";
            box.Size = new Vector3(200000f, 200000f, 200000f);
            box.Center = Vector3.zero;
            ContainerRegistry.Rebuild();
            var prefabs = Prefabs(false);
            foreach (var p in prefabs) { p.SetActive(false); _objects.Add(p); }
            NetworkPrefabs.Register(prefabs);
            var go = new GameObject("client");
            _objects.Add(go);
            var client = go.AddComponent<NebulaClient>();
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            typeof(NebulaClient).GetProperty("Config").SetValue(client, config);
            typeof(NebulaClient).GetProperty("ClientId").SetValue(client, 7UL);
            typeof(NebulaClient).GetProperty("ConnectionState").SetValue(client, NebulaClient.State.InGame);
            typeof(NebulaClient).GetField("_transport", Private).SetValue(client, new ConformanceMesh.RecordingTransport());
            typeof(NebulaClient).GetField("_gatewayPeer", Private).SetValue(client, 1);
            typeof(NebulaClient).GetField("_serverTickEstimate", Private).SetValue(client, 1000.0);
            var entities = (IDictionary)typeof(NebulaClient).GetField("_entities", Private).GetValue(client);
            // What the client's start hooks up: a spawn held for a container binds when the container registers.
            _lateRegistered = (Action<Container>)Delegate.CreateDelegate(typeof(Action<Container>), client, typeof(NebulaClient).GetMethod("OnLateContainerRegistered", Private));
            ContainerRegistry.DynamicRegistered += _lateRegistered;
            ContainerRegistry.RuntimeRegistered += _lateRegistered;
            return new ClientWorld { Client = client, Entities = entities };
        }

        // ------------------------------------------------------------------------------------ persistence

        /// <summary>
        /// A with B landed in it and B's rider aboard, all persistent: the rider's record names B, B's names A. A put away
        /// with <see cref="CargoPolicy.Stow"/> takes both, and restoring A brings back B in A and the rider in B, each at
        /// its pose.
        /// </summary>
        [TestCase(1, TestName = "AStowedCarrierComesBackWithBAndItsRider(one worker)")]
        [TestCase(2, TestName = "AStowedCarrierComesBackWithBAndItsRider(two workers)")]
        public void AStowedCarrierComesBackWithBAndItsRider(int workers)
        {
            Build(workers, persistent: true);
            var store = ConformanceFrameBodiesTests.PersistenceFor(_mesh);
            try
            {
                var shipAt = new Vector3(5f, 0f, 20f); // in A's fore part, A's own box: A's worker holds B
                var b = SpawnIn(W1, _shipPrefab, _a.Carried, shipAt, LandedTurn);
                var c = SpawnIn(W1, _crewPrefab, b.Carried, Seat, Quaternion.identity);
                uint tick = 1;
                for (int i = 0; i < 30; i++, tick++) { MoveCarrier(); TickAll(tick); }
                Assume.That(b.Container, Is.SameAs(_a.Carried));
                Assume.That(c.Container, Is.SameAs(b.Carried));
                string aKey = _a.Persistent.EnsureKey(), bKey = b.Persistent.EnsureKey(), cKey = c.Persistent.EnsureKey();
                ulong bId = b.NetId, cId = c.NetId;

                W1.Act(() => W1.Instance.Despawn(_a, keepPersisted: true, CargoPolicy.Stow));
                Assert.IsNull(W1.Find(bId), "B was stowed with A");
                Assert.IsNull(W1.Find(cId), "and so was B's rider");
                var cRecord = ConformanceFrameBodiesTests.RecordOf(store, cKey);
                var bRecord = ConformanceFrameBodiesTests.RecordOf(store, bKey);
                Assert.AreEqual(bKey, cRecord.CarrierKey, "the rider is saved against B");
                Assert.AreEqual(aKey, bRecord.CarrierKey, "B is saved against A");

                var restored = ConformanceFrameBodiesTests.RestoreWithCargo(W1, store, ConformanceFrameBodiesTests.RecordOf(store, aKey));
                Assert.IsNotNull(restored, "A is back");
                var b2 = W1.Instance.Persistence.Find(bKey);
                var c2 = W1.Instance.Persistence.Find(cKey);
                Assert.IsNotNull(b2, "B is back with A");
                Assert.IsNotNull(c2, "B's rider is back with B");
                Assert.AreSame(restored.Carried, b2.Container, "B in A");
                Assert.AreSame(b2.Carried, c2.Container, "the rider in B");
                Assert.That(Vector3.Distance(shipAt, b2.LocalPosition), Is.LessThan(1e-3f), "B where it stood in A");
                Assert.That(Quaternion.Angle(LandedTurn, b2.LocalRotation), Is.LessThan(0.01f));
                Assert.That(Vector3.Distance(Seat, c2.LocalPosition), Is.LessThan(1e-3f), "the rider in its seat");
                Assert.That(Vector3.Distance(Seat, b2.Carried.Frame.SimulationToLocal(c2.transform.position)), Is.LessThan(1e-3f), "in B's frame");
                Assert.That(Vector3.Distance(shipAt, restored.Carried.Frame.SimulationToLocal(b2.transform.position)), Is.LessThan(1e-3f), "in A's frame");
                for (int i = 0; i < 30; i++, tick++)
                {
                    restored.transform.position += Vector3.forward * (Cruise * Dt);
                    TickAll(tick);
                }
                Assert.AreSame(restored.Carried, b2.Container, "and they ride on");
                Assert.AreSame(b2.Carried, c2.Container);
            }
            finally { store.Dispose(); }
        }

        // ------------------------------------------------------------------------------------ a worker seam

        /// <summary>
        /// A crosses the seam between the two workers' space with B landed in it and B's rider aboard, cruising and
        /// turning. The tick A goes, B and the rider go with it: carrier first, all three in that one tick, and w2 has them
        /// exactly where they were in A's and B's frames.
        /// </summary>
        [Test]
        public void ACrossesAWorkerSeamWithBAndItsRiderAboard()
        {
            Build(2, carrierAt: new Vector3(-120f, 0f, 0f));
            // Turned to head east across the seam at x = 0.
            _a.transform.rotation = Quaternion.Euler(0f, 80f, 0f);
            var shipAt = new Vector3(5f, 0f, 20f);
            var b = SpawnIn(W1, _shipPrefab, _a.Carried, shipAt, LandedTurn);
            var c = SpawnIn(W1, _crewPrefab, b.Carried, Seat, Quaternion.identity);
            ulong aId = _a.NetId, bId = b.NetId, cId = c.NetId;
            uint tick = 1;
            int handedAt = -1;
            for (int i = 0; i < 120 && handedAt < 0; i++, tick++)
            {
                MoveCarrier();
                int before = _mesh.Delivered.Count;
                foreach (var w in _mesh.Workers) w.Tick(tick);
                bool aLeft = !_a.HasAuthority;
                if (aLeft)
                {
                    handedAt = i;
                    Assert.IsFalse(b.HasAuthority, "B left w1 on A's tick");
                    Assert.IsFalse(c.HasAuthority, "and so did its rider");
                }
                else
                {
                    Assert.IsTrue(b.HasAuthority, $"t{tick}: B stays with A");
                    Assert.IsTrue(c.HasAuthority, $"t{tick}: the rider stays with B");
                }
                _mesh.Pump();
                if (!aLeft) continue;
                var order = new List<ulong>();
                for (int m = before; m < _mesh.Delivered.Count; m++)
                {
                    var msg = _mesh.Delivered[m];
                    if (msg.Id == MsgId.AuthorityTransfer && msg.To == W2.Id) order.Add(msg.Read(AuthorityTransferMsg.Read).Entity.NetId);
                }
                CollectionAssert.AreEqual(new[] { aId, bId, cId }, order, "one handover: carrier first, then B, then B's rider");
            }
            Assert.That(handedAt, Is.GreaterThanOrEqualTo(0), "A crossed the seam");

            var a2 = W2.Find(aId);
            var b2 = W2.Find(bId);
            var c2 = W2.Find(cId);
            Assert.IsTrue(a2.HasAuthority && b2.HasAuthority && c2.HasAuthority, "w2 simulates all three");
            Assert.AreSame(_east, a2.Container);
            Assert.AreSame(a2.Carried, b2.Container, "B in w2's A");
            Assert.AreSame(b2.Carried, c2.Container, "the rider in w2's B");
            Assert.That(Vector3.Distance(shipAt, b2.LocalPosition), Is.LessThan(1e-3f), "B where it was in A");
            Assert.That(Vector3.Distance(Seat, c2.LocalPosition), Is.LessThan(1e-3f), "the rider in its seat");
            int crossings = 0;
            foreach (var w in _mesh.Workers) crossings += w.Instance.FrameCrossings;
            Assert.AreEqual(0, crossings, "nobody crossed a frame");

            for (int i = 0; i < 60; i++, tick++)
            {
                MoveCarrier();
                TickAll(tick);
                Assert.IsTrue(a2.HasAuthority && b2.HasAuthority && c2.HasAuthority, $"t{tick}: w2 keeps all three");
                Assert.That(Vector3.Distance(shipAt, a2.Carried.Frame.SimulationToLocal(b2.transform.position)), Is.LessThan(1e-3f), $"t{tick}: B rides still in A");
                Assert.That(Vector3.Distance(Seat, b2.Carried.Frame.SimulationToLocal(c2.transform.position)), Is.LessThan(1e-3f), $"t{tick}: the rider in B");
            }
        }

        // ------------------------------------------------------------------------------------ overlapping boxes

        /// <summary>
        /// B, in the same space as A, is put down so that B's box holds A's origin (it is deep in A's box). Whichever of
        /// the two the worker resolves first, B goes into A and A stays in space: a framed carrier never goes inside a
        /// framed carrier whose box is smaller than its own.
        /// </summary>
        [TestCase(false, TestName = "ACarrierNeverGoesInsideASmallerOneOverItsOrigin(A resolved first)")]
        [TestCase(true, TestName = "ACarrierNeverGoesInsideASmallerOneOverItsOrigin(B resolved first)")]
        public void ACarrierNeverGoesInsideASmallerOneOverItsOrigin(bool shipFirst)
        {
            _mesh = new ConformanceMesh(1, perWorkerRegistry: true);
            _west = _mesh.AddStaticContainer("space", Vector3.zero, new Vector3(200000f, 200000f, 200000f));
            _west.Center = Vector3.zero;
            ContainerRegistry.Rebuild();
            _mesh.SetOwner(_west, W1);
            var prefabs = Prefabs(false);
            _carrierPrefab = _mesh.RegisterPrefab(prefabs[0]);
            _shipPrefab = _mesh.RegisterPrefab(prefabs[1]);
            var far = new Vector3(5000f, 0f, 0f);
            NetworkIdentity a, b;
            // Spawned apart; the order of spawning is the order the worker resolves them in.
            if (shipFirst) { b = W1.SpawnServerDriven(_shipPrefab, _west, far, Quaternion.identity); a = W1.SpawnServerDriven(_carrierPrefab, _west, CarrierStart, Quaternion.identity); }
            else { a = W1.SpawnServerDriven(_carrierPrefab, _west, CarrierStart, Quaternion.identity); b = W1.SpawnServerDriven(_shipPrefab, _west, far, Quaternion.identity); }
            W1.Tick(1);
            // B is put 1 m under A's deck: its box (from its origin to 6 m above it) holds A's origin, and B's origin is in A's box.
            b.transform.SetPositionAndRotation(a.transform.position + new Vector3(0f, -1f, 0f), Quaternion.identity);
            Assume.That(b.Carried.Contains(a.transform.position), Is.True, "B's box holds A's origin");
            Assume.That(a.Carried.Contains(b.transform.position), Is.True, "and A's box holds B's origin");
            W1.Tick(2);
            W1.Tick(3);
            Assert.AreSame(_west, a.Container, "A stays in space");
            Assert.AreSame(a.Carried, b.Container != null ? b.Container.InnerSpace : null, "B went into A");
        }
    }
}
