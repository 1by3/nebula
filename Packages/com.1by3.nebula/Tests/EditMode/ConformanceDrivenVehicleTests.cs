using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 39 (NEB-361, <c>docs/driven-vehicles.md</c>): a server-owned vehicle a player drives. The
    /// worker hands the controls to a client with <see cref="NetworkIdentity.SetDriver"/>; from then on the vehicle's
    /// <see cref="PredictedBehaviour{TInput}"/> runs that client's input, sent as <see cref="MsgId.DriveInput"/>, and the
    /// worker reports its state to that client alone, as it does to a pawn's owner. Nobody else's input is run; a
    /// driver that goes quiet gives the wheel back to the worker's own input after a timeout, and gets it back when its
    /// input returns; <see cref="NetworkIdentity.ClearDriver"/> and the driver's session ending give it back for good.
    /// A driven vehicle keeps its driver and its pending inputs across a worker seam, and inputs sent to the old owner
    /// reach the new one. Inside a moving scope (a car with a passenger in a flying ship's hold) the passenger stays in
    /// its seat and the driver's prediction agrees with the worker every tick, and both still hold when the car drives
    /// out of the hold.
    /// <para>
    /// Tier B on the <see cref="ConformanceMesh"/>, with a recording gateway; the client's side of the last scenario is
    /// a second <see cref="TestVehicle"/> run through the same predict and reconcile calls <see cref="NebulaClient"/>
    /// makes. <see cref="ConformanceDrivenVehicleClientTests"/> covers the client component itself.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceDrivenVehicleTests
    {
        private const ulong Driver = 7;
        private const ulong Stranger = 8;
        private static readonly Vector3 CarBox = new Vector3(2.4f, 2f, 4.4f);
        private static readonly Vector3 Hold = new Vector3(40f, 10f, 60f);

        private ConformanceMesh _mesh;
        private ushort _carPrefab, _carrierCarPrefab, _riderPrefab, _shipPrefab;
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
            NebulaRuntime.IsClient = false;
            PredictedBehaviour<DriveInput>.LogCorrections = false;
            NebulaRuntime.IsServer = true;
            _mesh?.Dispose();
            _mesh = null;
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ fixtures

        private static GameObject CarPrefab(bool carrier)
        {
            var car = new GameObject(carrier ? "carrier-car-prefab" : "car-prefab");
            var identity = car.AddComponent<NetworkIdentity>();
            identity.AlwaysRelevant = true; // the recording gateway subscribes no regions
            if (carrier)
            {
                var box = car.AddComponent<Container>();
                box.ContainerId = "car";
                box.Size = CarBox;
                box.Center = new Vector3(0f, CarBox.y * 0.5f, 0f);
            }
            car.AddComponent<TestVehicle>(); // brings its NetworkTransform
            return car;
        }

        private static GameObject RiderPrefab()
        {
            var rider = new GameObject("passenger-prefab");
            rider.AddComponent<NetworkIdentity>();
            rider.AddComponent<NetworkTransform>();
            return rider;
        }

        private static GameObject ShipPrefab()
        {
            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>().AlwaysRelevant = true;
            var box = ship.AddComponent<Container>();
            box.ContainerId = "hold";
            box.Size = Hold;
            box.Center = new Vector3(0f, Hold.y * 0.5f, 0f);
            box.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>();
            return ship;
        }

        /// <summary>Ground containers side by side along x, the seams at x = 0 when there are two, each leased to its worker.</summary>
        private List<Container> MeshWith(int workers)
        {
            _mesh = new ConformanceMesh(workers);
            var containers = new List<Container>();
            float width = 4000f / workers;
            for (int i = 0; i < workers; i++)
                containers.Add(_mesh.AddStaticContainer($"ground-{i}", new Vector3(-2000f + width * (i + 0.5f), -50f, 0f), new Vector3(width, 100f, 4000f)));
            for (int i = 0; i < workers; i++) _mesh.SetOwner(containers[i], _mesh[i]);
            _carPrefab = _mesh.RegisterPrefab(CarPrefab(carrier: false));
            _carrierCarPrefab = _mesh.RegisterPrefab(CarPrefab(carrier: true));
            _riderPrefab = _mesh.RegisterPrefab(RiderPrefab());
            _shipPrefab = _mesh.RegisterPrefab(ShipPrefab());
            return containers;
        }

        private static byte[] Payload(DriveInput input)
        {
            var w = new NetworkWriter(16);
            input.Serialize(w);
            return w.ToArray();
        }

        /// <summary>What a gateway forwards for a driver: its input for <paramref name="tick"/>, stamped with <paramref name="client"/>.</summary>
        private void Drive(ConformanceMesh.Gateway gateway, ConformanceMesh.Worker to, NetworkIdentity car, ulong client, uint tick, DriveInput input) =>
            DriveFrames(gateway, to, car, client, new List<ClientInputMsg.Frame> { new ClientInputMsg.Frame { Tick = tick, Payload = Payload(input) } });

        private void DriveFrames(ConformanceMesh.Gateway gateway, ConformanceMesh.Worker to, NetworkIdentity car, ulong client, List<ClientInputMsg.Frame> frames) =>
            _mesh.FromGateway(gateway, to, w => new DriveInputMsg { ClientId = client, NetId = car.NetId, Frames = frames }.Write(w));

        private EntitySpawnMsg LastSpawnTo(string gateway, ulong netId)
        {
            EntitySpawnMsg? found = null;
            foreach (var m in _mesh.DeliveredOf(MsgId.EntitySpawn, gateway))
            {
                var spawn = m.Read(r => EntitySpawnMsg.Read(r));
                if (spawn.NetId == netId) found = spawn;
            }
            Assert.IsTrue(found.HasValue, $"the gateway was sent a spawn of #{netId}");
            return found.Value;
        }

        private List<(string from, OwnerStateMsg msg)> OwnerStatesTo(string gateway, ulong netId)
        {
            var list = new List<(string, OwnerStateMsg)>();
            foreach (var m in _mesh.DeliveredOf(MsgId.OwnerState, gateway))
            {
                var state = m.Read(r => OwnerStateMsg.Read(r));
                if (state.NetId == netId) list.Add((m.From, state));
            }
            return list;
        }

        private static readonly DriveInput Floored = new DriveInput { Throttle = 1f };

        // ------------------------------------------------------------------------------------ the seat

        [Test]
        public void TheDriverSteersAndTheWorkerTakesTheWheelBack()
        {
            var ground = MeshWith(1)[0];
            var gateway = _mesh.AddGateway("gw");
            _mesh.LinkGateway(gateway);
            var car = W1.SpawnServerDriven(_carPrefab, ground, new Vector3(0f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f));
            var vehicle = car.GetComponent<TestVehicle>();
            vehicle.Parked = new DriveInput { Throttle = 0f };
            uint t = 1;
            W1.Tick(t++);
            _mesh.Pump();
            Assert.AreEqual(1, vehicle.ServerInputTicks, "nobody drives it: the worker's own input");
            Assert.AreEqual(0UL, LastSpawnTo("gw", car.NetId).DriverClientId);

            // A driven vehicle is updated every tick, whatever its interval.
            car.UpdateInterval = 6;
            bool set = false;
            W1.Act(() => set = car.SetDriver(Driver));
            Assert.IsTrue(set);
            Assert.AreEqual(Driver, car.DriverClientId);
            Assert.AreEqual(1, vehicle.DriverChanges, "OnDriverChanged on the worker");
            Assert.AreEqual(Driver, vehicle.LastDriverSeen);

            // Until the driver's first input arrives, the worker's own input.
            W1.Tick(t++);
            _mesh.Pump();
            Assert.AreEqual(2, vehicle.ServerInputTicks);
            Assert.AreEqual(Driver, LastSpawnTo("gw", car.NetId).DriverClientId, "the gateways are told who drives it");

            // The driver's input drives it, every tick, and the driver is sent the result.
            int serverTicks = vehicle.ServerInputTicks;
            float speed = vehicle.Speed;
            for (int i = 0; i < 30; i++, t++)
            {
                Drive(gateway, W1, car, Driver, t, Floored);
                W1.Tick(t);
                _mesh.Pump();
                Assert.IsTrue(vehicle.ProcessedInputThisTick, $"tick {t}: the driver's input was run");
                Assert.That(vehicle.Speed, Is.GreaterThan(speed), $"tick {t}: it accelerates every tick, its update interval notwithstanding");
                speed = vehicle.Speed;
            }
            Assert.AreEqual(serverTicks, vehicle.ServerInputTicks, "no tick fell back to the worker's input");
            var states = OwnerStatesTo("gw", car.NetId);
            Assert.That(states.Count, Is.GreaterThanOrEqualTo(30), "the worker reports its state every tick");
            var last = states[states.Count - 1].msg;
            Assert.AreEqual(Driver, last.OwnerClientId, "to the driver");
            Assert.AreEqual(t - 1, last.Tick);
            Assert.AreEqual(t - 1, last.LastInputTick);
            Assert.That(car.transform.position.x, Is.GreaterThan(0.5f), "it drove off along +x");

            // Somebody else's input for it is not run: the worker repeats the driver's last input instead.
            int missed = vehicle.InputsMissed;
            Drive(gateway, W1, car, Stranger, t, new DriveInput { Throttle = -1f, Steer = 1f });
            W1.Tick(t++);
            Assert.AreEqual(1f, vehicle.LastInput.Throttle, "the driver's last input, repeated");
            Assert.AreEqual(0f, vehicle.LastInput.Steer);
            Assert.AreEqual(missed + 1, vehicle.InputsMissed);

            // The driver goes quiet: its last input is repeated for the timeout, then the worker takes the wheel.
            uint quietFrom = t;
            for (int i = 0; i < 40; i++) W1.Tick(t++);
            Assert.AreEqual(40 - 29, vehicle.ServerInputTicks - serverTicks, "after 30 ticks without input (one of them the stranger's), the worker's own input");
            Assert.AreEqual(0f, vehicle.LastInput.Throttle, "the worker's input: coasting");
            Assert.AreEqual(Driver, car.DriverClientId, "the driver keeps the seat");
            // ... and gives it back as soon as the driver's input returns.
            Drive(gateway, W1, car, Driver, t, Floored);
            W1.Tick(t++);
            Assert.IsTrue(vehicle.ProcessedInputThisTick, "the returning driver's input is run at once");
            Assert.AreEqual(1f, vehicle.LastInput.Throttle);
            Assert.That(quietFrom, Is.GreaterThan(0u));

            // A driven vehicle cannot sleep.
            W1.Act(() => car.Sleep());
            W1.Tick(t++);
            Assert.IsFalse(car.IsDormant, "Sleep is refused while it is driven");

            // The worker takes the controls back: the driver's input is no longer run (and its update interval applies
            // again, so it is put back to every tick to count ticks here).
            car.UpdateInterval = 1;
            W1.Act(() => car.ClearDriver());
            Assert.AreEqual(0UL, car.DriverClientId);
            Assert.AreEqual(2, vehicle.DriverChanges);
            serverTicks = vehicle.ServerInputTicks;
            Drive(gateway, W1, car, Driver, t, Floored);
            W1.Tick(t++);
            _mesh.Pump();
            Assert.IsFalse(vehicle.ProcessedInputThisTick, "the former driver's input is dropped");
            Assert.AreEqual(serverTicks + 1, vehicle.ServerInputTicks);
            Assert.AreEqual(0UL, LastSpawnTo("gw", car.NetId).DriverClientId, "the gateways are told nobody drives it");
            int statesBefore = OwnerStatesTo("gw", car.NetId).Count;
            W1.Tick(t++);
            _mesh.Pump();
            Assert.AreEqual(statesBefore, OwnerStatesTo("gw", car.NetId).Count, "nobody is sent its state any more");

            // The driver's session ending gives the wheel back too.
            W1.Act(() => car.SetDriver(Driver));
            var despawnPlayer = typeof(NebulaWorker).GetMethod("DespawnPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
            W1.Act(() => despawnPlayer.Invoke(W1.Instance, new object[] { Driver }));
            Assert.AreEqual(0UL, car.DriverClientId, "the seat is released when the driver's session ends");

            // A pawn cannot be driven by anybody else, and a copy without authority cannot hand out the seat.
            car.OwnerClientId = 99;
            W1.Act(() => set = car.SetDriver(Driver));
            Assert.IsFalse(set, "a client's own entity is not a vehicle for another");
            car.OwnerClientId = 0;
        }

        // ------------------------------------------------------------------------------------ the seam

        /// <summary>
        /// Across a worker seam. What rides in a vehicle goes with it whoever drives it (a carrier hands its riders over
        /// with it, scenario 37); in this one-process mesh the two workers' copies of a carrier share one box, so the
        /// passenger half is in <see cref="ACarDrivenInAFlyingShipsHoldIsPredictedWithoutACorrection"/>, on one worker.
        /// </summary>
        [Test]
        public void ADrivenVehicleCrossesASeamWithItsDriverAndItsInputs()
        {
            var grounds = MeshWith(2);
            var w2 = _mesh[1];
            var gateway = _mesh.AddGateway("gw");
            _mesh.LinkGateway(gateway);
            var car = W1.SpawnServerDriven(_carPrefab, grounds[0], new Vector3(-6f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f));
            W1.Act(() => car.SetDriver(Driver));

            NetworkIdentity Authority(ulong netId) => W1.Find(netId) is NetworkIdentity a && a.HasAuthority ? a : w2.Find(netId);
            var target = W1;
            uint handedAt = 0;
            float speed = 0f;
            int stale = 0;
            for (uint t = 1; t < 200; t++)
            {
                var holder = Authority(car.NetId);
                // The gateway learns of the new owner from its spawn a moment after the handover: for a few ticks it
                // still sends the driver's input to the worker that had the car, which passes it on.
                if (handedAt != 0 && target == W1 && t > handedAt + 5) target = w2;
                if (handedAt != 0 && target == W1) stale++;
                Drive(gateway, target, car, Driver, t, Floored);
                _mesh.Pump();
                W1.Tick(t);
                w2.Tick(t);
                _mesh.Pump();
                var now = Authority(car.NetId);
                Assert.IsNotNull(now, $"tick {t}: somebody has the car");
                if (handedAt == 0 && now != holder) handedAt = t;
                var vehicle = now.GetComponent<TestVehicle>();
                if (t > 1)
                {
                    Assert.That(vehicle.Speed, Is.GreaterThan(speed), $"tick {t}: the car accelerates every tick, across the seam");
                    Assert.IsTrue(vehicle.ProcessedInputThisTick || now != holder, $"tick {t}: the driver's input was run");
                }
                speed = vehicle.Speed;
            }
            Assert.That(handedAt, Is.GreaterThan(0u), "the car crossed the seam");
            Assert.That(stale, Is.GreaterThan(0), "inputs went to the old owner after the handover");
            var theirs = w2.Find(car.NetId);
            Assert.IsTrue(theirs.HasAuthority, "w2 has the car");
            Assert.AreEqual(Driver, theirs.DriverClientId, "with its driver");
            Assert.AreEqual(0, theirs.GetComponent<TestVehicle>().ServerInputTicks, "w2 never ran its own input for it: the driver's inputs came with it and after it");
            Assert.AreEqual(0, theirs.GetComponent<TestVehicle>().InputsMissed, "nor repeated one: the inputs sent to w1 reached it in time");
            Assert.That(theirs.transform.position.x, Is.GreaterThan(5f));

            var states = OwnerStatesTo("gw", car.NetId);
            var (from, lastState) = states[states.Count - 1];
            Assert.AreEqual(w2.Id, from, "the driver hears from the new owner");
            Assert.AreEqual(Driver, lastState.OwnerClientId);
            Assert.That(lastState.Epoch, Is.GreaterThan(states[0].msg.Epoch), "at a newer epoch, so the driver drops the old owner's late reports");
            Assert.AreEqual(Driver, LastSpawnTo("gw", car.NetId).DriverClientId, "the new owner's spawn names the driver");
        }

        // ------------------------------------------------------------------------------------ the moving scope

        [Test]
        public void ACarDrivenInAFlyingShipsHoldIsPredictedWithoutACorrection()
        {
            var ground = MeshWith(1)[0];
            var gateway = _mesh.AddGateway("gw");
            _mesh.LinkGateway(gateway);
            var ship = W1.SpawnServerDriven(_shipPrefab, ground, new Vector3(0f, 0f, 0f), Quaternion.identity);
            ContainerRegistry.RefreshCaches();
            var hold = ship.Carried;
            var car = W1.SpawnServerDriven(_carrierCarPrefab, hold, ship.transform.TransformPoint(new Vector3(0f, 0.5f, -10f)), ship.transform.rotation);
            Assume.That(car.Container, Is.SameAs(hold), "the car starts in the hold");
            ContainerRegistry.RefreshCaches();
            Assume.That(car.Carried, Is.Not.Null, "the car carries a box: its seats");
            var seat = new Vector3(0.5f, 0.6f, -1f);
            var passenger = W1.SpawnServerDriven(_riderPrefab, car.Carried, car.transform.TransformPoint(seat), car.transform.rotation);
            Assume.That(passenger.Container, Is.SameAs(car.Carried), "a passenger sits in the car");
            W1.Act(() => car.SetDriver(Driver));
            PredictedBehaviour<DriveInput>.LogCorrections = true;

            // The driver's client: a copy of the car in the ship's hold, predicting three ticks ahead of the worker
            // with the same inputs and reconciled to what the worker sends, as NebulaClient does.
            var go = new GameObject("car-on-the-drivers-client");
            _objects.Add(go);
            var copy = go.AddComponent<TestVehicle>();
            go.GetComponent<NetworkIdentity>().Initialize();
            copy.Identity.SetContainer(hold);
            copy.Identity.SetLocalPose(hold, car.LocalPosition, car.LocalRotation);
            const uint Lead = 3;
            DriveInput Wheel(uint tick) => tick < 520
                ? new DriveInput { Throttle = 0.3f, Steer = 1f }   // circling in the hold
                : new DriveInput { Throttle = 0.6f, Steer = 0f };  // then straight out of it
            var sent = new Dictionary<uint, byte[]>();
            int seenStates = 0;
            var shipVelocity = Vector3.zero;
            float yawRate = 0f;
            bool left = false;
            for (uint t = 1; t < 760; t++)
            {
                // The client predicts tick t + Lead now; its input reaches the worker before the worker gets there.
                uint predicted = t + Lead;
                copy.Hands = () => Wheel(predicted);
                var w = new NetworkWriter(16);
                copy.ClientPredictTick(predicted, w);
                sent[predicted] = w.ToArray();
                if (sent.TryGetValue(t, out var payload))
                    DriveFrames(gateway, W1, car, Driver, new List<ClientInputMsg.Frame> { new ClientInputMsg.Frame { Tick = t, Payload = payload } });

                // The ship: up to 200 m/s over two seconds, turning at 10 degrees a second, then back to a stop.
                if (t < 120) shipVelocity = ship.transform.forward * (200f * t / 120f);
                else if (t < 400) shipVelocity = ship.transform.forward * 200f;
                else if (t < 460) shipVelocity = ship.transform.forward * (200f * (460 - t) / 60f);
                else shipVelocity = Vector3.zero;
                yawRate = t < 400 ? 10f : 0f;
                ship.transform.SetPositionAndRotation(ship.transform.position + shipVelocity * NetworkTime.TickInterval,
                    ship.transform.rotation * Quaternion.Euler(0f, yawRate * NetworkTime.TickInterval, 0f));
                ship.Motion.Velocity = shipVelocity;
                W1.Tick(t);
                _mesh.Pump();
                if (t < 520) Assert.AreSame(hold, car.Container, $"tick {t}: the car is still in the hold");
                if (car.Container != hold) left = true;
                Assert.AreSame(car.Carried, passenger.Container, $"tick {t}: the passenger is still in its seat");
                Assert.That(Vector3.Distance(seat, passenger.LocalPosition), Is.LessThan(0.01f), $"tick {t}: at the seat ({passenger.LocalPosition})");

                // The worker's report for tick t reaches the client, which moves to the worker's container first.
                var states = OwnerStatesTo("gw", car.NetId);
                for (; seenStates < states.Count; seenStates++)
                {
                    var state = states[seenStates].msg;
                    var container = ContainerRegistry.Resolve(state.Container);
                    if (container != copy.Identity.Container) copy.Identity.SetContainer(container);
                    copy.ClientReconcile(state.Tick, new NetworkReader(state.State));
                }
            }
            Assert.That(ship.transform.position.magnitude, Is.GreaterThan(300f), "the ship flew");
            Assert.IsTrue(left, "the car drove out of the hold");
            Assert.AreSame(ground, car.Container, "onto the ground");
            Assert.AreSame(ground, copy.Identity.Container, "and the driver's copy followed it there");
            Assert.AreSame(car.Carried, passenger.Container, "with its passenger");
            Assert.AreEqual(0, car.GetComponent<TestVehicle>().InputsMissed, "every input arrived in time");
            Assert.AreEqual(0, copy.Corrections, $"the driver's prediction agreed with the worker every tick, in the flying hold and out of it (worst {copy.MaxCorrectionMagnitude:0.000} m)");
        }
    }
}
