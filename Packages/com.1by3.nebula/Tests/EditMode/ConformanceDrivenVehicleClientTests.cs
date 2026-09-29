using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The client half of conformance scenario 39 (NEB-361, <c>docs/driven-vehicles.md</c>): a <see cref="NebulaClient"/>
    /// predicts the entity a spawn names it the driver of. It gathers and simulates the vehicle's input every tick,
    /// sends it as <see cref="MsgId.DriveInput"/>, reconciles the vehicle with the worker's owner state, and ignores
    /// owner state for anything it does not drive; when a spawn names nobody, it stops and the vehicle goes back to
    /// the interpolated stream from where the prediction left it. Every copy's behaviour hears of each change.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceDrivenVehicleClientTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const ulong Me = 7;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private NebulaClient _client;
        private ConformanceMesh.RecordingTransport _transport;
        private IDictionary _entities;

        [SetUp]
        public void SetUp()
        {
            ContainerRegistry.Rebuild();
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            var prefab = new GameObject("car-prefab");
            _objects.Add(prefab);
            prefab.AddComponent<NetworkIdentity>();
            prefab.AddComponent<TestVehicle>();
            prefab.SetActive(false);
            NetworkPrefabs.Register(new List<GameObject> { prefab });

            var go = new GameObject("client");
            _objects.Add(go);
            _client = go.AddComponent<NebulaClient>();
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            typeof(NebulaClient).GetProperty("Config").SetValue(_client, config);
            typeof(NebulaClient).GetProperty("ClientId").SetValue(_client, Me);
            typeof(NebulaClient).GetProperty("ConnectionState").SetValue(_client, NebulaClient.State.InGame);
            _transport = new ConformanceMesh.RecordingTransport();
            typeof(NebulaClient).GetField("_transport", Private).SetValue(_client, _transport);
            typeof(NebulaClient).GetField("_gatewayPeer", Private).SetValue(_client, 1);
            typeof(NebulaClient).GetField("_serverTickEstimate", Private).SetValue(_client, 1000.0);
            _entities = (IDictionary)typeof(NebulaClient).GetField("_entities", Private).GetValue(_client);
        }

        [TearDown]
        public void TearDown()
        {
            typeof(NebulaClient).GetField("_transport", Private).SetValue(_client, null);
            foreach (var e in _entities.Values) if (e is NetworkIdentity id && id != null) Object.DestroyImmediate(id.gameObject);
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            NetworkPrefabs.Register(new List<GameObject>());
            ContainerRegistry.Rebuild();
            NebulaRuntime.IsClient = false;
            NebulaRuntime.IsServer = true;
        }

        private void Spawn(ulong netId, ulong driver, uint epoch = 1) =>
            typeof(NebulaClient).GetMethod("OnEntitySpawn", Private).Invoke(_client, new object[]
            {
                new EntitySpawnMsg
                {
                    NetId = netId, PrefabId = 0, Epoch = epoch, Container = ContainerRef.None, LocalPosition = Vector3.zero,
                    LocalRotation = Quaternion.identity, LocalScale = Vector3.one, Vars = System.Array.Empty<byte>(),
                    State = System.Array.Empty<byte>(), DriverClientId = driver,
                },
            });

        private void FixedUpdate() => typeof(NebulaClient).GetMethod("FixedUpdate", Private).Invoke(_client, null);

        private void OwnerState(ulong netId, uint tick, Vector3 position, Quaternion rotation, Vector3 velocity, float speed)
        {
            var w = new NetworkWriter(64);
            w.WriteVector3(position);
            w.WriteQuaternion(rotation);
            w.WriteVector3(velocity);
            w.WriteFloat(speed);
            typeof(NebulaClient).GetMethod("OnOwnerState", Private).Invoke(_client, new object[]
            {
                new OwnerStateMsg { NetId = netId, Epoch = 1, Tick = tick, LastInputTick = tick, InputLead = 3, OwnerClientId = Me, Container = ContainerRef.None, State = w.ToArray() },
            });
        }

        private List<DriveInputMsg> SentDriveInputs()
        {
            var list = new List<DriveInputMsg>();
            while (_transport.Outbox.Count > 0)
            {
                var bytes = _transport.Outbox.Dequeue().Value;
                if ((MsgId)bytes[0] != MsgId.DriveInput) continue;
                var r = new NetworkReader(bytes);
                r.ReadByte();
                list.Add(DriveInputMsg.Read(r));
            }
            return list;
        }

        [Test]
        public void TheClientPredictsWhatItDrivesAndStopsWhenTheSeatIsTaken()
        {
            Spawn(50, driver: Me);
            Spawn(51, driver: 9);
            var car = (NetworkIdentity)_entities[50UL];
            var other = (NetworkIdentity)_entities[51UL];
            var vehicle = car.GetComponent<TestVehicle>();
            Assert.IsTrue(car.IsLocallyDriven, "the spawn names this client the driver");
            Assert.IsTrue(vehicle.IsDriver);
            Assert.IsFalse(other.IsLocallyDriven);
            CollectionAssert.AreEqual(new[] { car }, _client.DrivenEntities);
            Assert.AreEqual(1, vehicle.DriverChanges, "OnDriverChanged on the driver's client");
            Assert.AreEqual(1, other.GetComponent<TestVehicle>().DriverChanges, "and on every other client that holds a driven entity");
            Assert.AreEqual(9UL, other.DriverClientId, "every client knows who drives what");

            // Every tick: the vehicle is simulated with the player's input, and the input goes to the gateway.
            vehicle.Hands = () => new DriveInput { Throttle = 1f };
            _transport.Outbox.Clear();
            for (int i = 0; i < 5; i++) FixedUpdate();
            Assert.That(vehicle.Speed, Is.GreaterThan(0f), "predicted at once");
            Assert.That(car.transform.position.z, Is.GreaterThan(0f), "it moved forward");
            var sent = SentDriveInputs();
            Assert.AreEqual(5, sent.Count, "one DriveInput a tick");
            var lastSent = sent[sent.Count - 1];
            Assert.AreEqual(50UL, lastSent.NetId);
            Assert.AreEqual(0UL, lastSent.ClientId, "the gateway stamps the sender");
            Assert.AreEqual(3, lastSent.Frames.Count, "the last three inputs, against loss");
            Assert.AreEqual(lastSent.Frames[1].Tick + 1, lastSent.Frames[2].Tick);

            // The worker's state for the newest predicted tick disagrees by a metre: corrected once.
            uint predicted = (uint)typeof(NebulaClient).GetField("_predictTick", Private).GetValue(_client);
            Assert.AreEqual(lastSent.Frames[2].Tick, predicted);
            var shoved = car.transform.position + Vector3.right;
            OwnerState(50, predicted, shoved, car.transform.rotation, car.Motion.Velocity, vehicle.Speed);
            Assert.AreEqual(1, vehicle.Corrections, "reconciled to the worker's state");
            Assert.That(Vector3.Distance(shoved, car.transform.position), Is.LessThan(1e-4f));
            // Owner state for a vehicle somebody else drives is not this client's to apply.
            OwnerState(51, predicted, new Vector3(5f, 0f, 5f), Quaternion.identity, Vector3.zero, 0f);
            Assert.AreEqual(Vector3.zero, other.transform.position);

            // The seat is taken back: the client stops predicting, and the interpolator starts from where it was.
            var at = car.transform.position;
            Spawn(50, driver: 0);
            Assert.IsFalse(car.IsLocallyDriven);
            Assert.IsFalse(vehicle.IsDriver);
            CollectionAssert.IsEmpty(_client.DrivenEntities);
            Assert.AreEqual(2, vehicle.DriverChanges);
            Assert.IsTrue(car.Interpolator.HasSamples);
            Assert.That(Vector3.Distance(at, car.Interpolator.LatestLocalPosition), Is.LessThan(1e-4f), "the stream picks up from the predicted pose");
            float speed = vehicle.Speed;
            _transport.Outbox.Clear();
            FixedUpdate();
            CollectionAssert.IsEmpty(SentDriveInputs(), "nothing is sent for it any more");
            Assert.AreEqual(speed, vehicle.Speed, "nor simulated");
        }
    }
}
