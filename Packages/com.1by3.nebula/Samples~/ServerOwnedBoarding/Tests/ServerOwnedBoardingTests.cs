using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NebulaSamples.Tests
{
    /// <summary>
    /// The sample end to end on one real <see cref="NebulaWorker"/>, driven a whole tick at a time: a
    /// <see cref="ScopeWalker"/> spawned server-driven walks onto a drifting <see cref="MovingPlatform"/>, rides it
    /// through a 1 km/s flight with a turn, and walks off onto the ground again, never jumping in the scope.
    /// </summary>
    public sealed class ServerOwnedBoardingTests
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const float Dt = NetworkTime.TickInterval;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private NebulaWorker _worker;
        private NebulaConfig _config;
        private MethodInfo _tick;
        private uint _tickNumber;

        // ------------------------------------------------------------------------------------ a one-worker mesh

        /// <summary>A transport that sends nowhere: the worker has no peers and no gateways here.</summary>
        private sealed class NoTransport : ITransport
        {
            public string Name => "none";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { }
            public void StartClient() { }
            public int Connect(string host, int port) => 0;
            public void Disconnect(int peerId) { }
            public bool IsConnected(int peerId) => false;
            public int RoundTripMs(int peerId) => 0;
            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload) { }
            public void Poll(Action<TransportEvent> handler) { }
            public void Flush() { }
            public void Stop() { }
            public void Dispose() { }
        }

        [SetUp]
        public void SetUp()
        {
            typeof(NebulaRuntime).GetMethod("Reset", Members).Invoke(null, null);
            typeof(NebulaRuntime).GetProperty("IsServer", Members).SetValue(null, true);
            // A physics frame is a scene with physics of its own; outside play mode, a preview scene is one.
            typeof(PhysicsFrames).GetField("SceneFactory", Members).SetValue(null, (Func<Scene>)(() => EditorSceneManager.NewPreviewScene()));
            typeof(PhysicsFrames).GetField("SceneDisposer", Members).SetValue(null, (Action<Scene>)(s => EditorSceneManager.ClosePreviewScene(s)));
            ContainerRegistry.Rebuild();
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            var host = New("worker");
            _worker = host.AddComponent<NebulaWorker>();
            typeof(NebulaWorker).GetProperty("WorkerId").SetValue(_worker, "w1");
            typeof(NebulaWorker).GetProperty("WorkerIndex").SetValue(_worker, (ushort)1);
            typeof(NebulaWorker).GetProperty("Config").SetValue(_worker, _config);
            typeof(NebulaWorker).GetField("_transport", Members).SetValue(_worker, new NoTransport());
            typeof(NebulaWorker).GetField("_interestGrid", Members).SetValue(_worker, InterestGrid.Resolve(InterestSettings.Default));
            typeof(NebulaRuntime).GetProperty("LocalWorkerIndex", Members).SetValue(null, (ushort)1);
            typeof(NebulaRuntime).GetProperty("LocalWorkerId", Members).SetValue(null, "w1");
            _tick = typeof(NebulaWorker).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        [TearDown]
        public void TearDown()
        {
            if (_worker != null) foreach (var e in new List<NetworkIdentity>(_worker.Entities)) if (e != null) Object.DestroyImmediate(e.gameObject);
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            if (_config != null) Object.DestroyImmediate(_config);
            NetworkPrefabs.Register(null);
            ContainerRegistry.Rebuild();
            typeof(PhysicsFrames).GetMethod("DrainPool", Members)?.Invoke(null, null);
            typeof(PhysicsFrames).GetField("SceneFactory", Members).SetValue(null, null);
            typeof(PhysicsFrames).GetField("SceneDisposer", Members).SetValue(null, null);
            typeof(NebulaRuntime).GetMethod("Reset", Members).Invoke(null, null);
        }

        private GameObject New(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        private void Tick()
        {
            try { _tick.Invoke(_worker, new object[] { ++_tickNumber }); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
        }

        private NetworkIdentity Spawn(ushort prefabId, Container container, Vector3 position, Quaternion rotation)
        {
            var identity = NetworkPrefabs.Instantiate(prefabId, position, rotation, container.transform);
            _worker.SpawnServerDriven(identity, container);
            return identity;
        }

        // ------------------------------------------------------------------------------------ the sample's prefabs

        private GameObject PlatformPrefab()
        {
            var platform = New("platform");
            platform.SetActive(false);
            platform.AddComponent<NetworkIdentity>();
            var box = platform.AddComponent<Container>();
            box.ContainerId = "platform";
            // From the deck to well above a standing character, and 0.5 m in from the deck's edges: a walker leaving
            // is past the box by the handover hysteresis while it still stands on the deck.
            box.Size = new Vector3(7f, 4f, 11f);
            box.Center = new Vector3(0f, 2f, 0f);
            box.OwnPhysicsFrame = true;
            platform.AddComponent<NetworkTransform>();
            platform.AddComponent<MovingPlatform>();
            var deck = new GameObject("deck");
            deck.transform.SetParent(platform.transform, false);
            deck.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            deck.AddComponent<BoxCollider>().size = new Vector3(8f, 0.2f, 12f);
            var spot = new GameObject("spot"); // where the walker stands aboard
            spot.transform.SetParent(platform.transform, false);
            spot.transform.localPosition = new Vector3(0f, 0f, -2f);
            return platform;
        }

        private GameObject WalkerPrefab()
        {
            var walker = New("walker");
            walker.SetActive(false);
            walker.AddComponent<NetworkIdentity>();
            walker.AddComponent<NetworkTransform>();
            var controller = walker.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.4f;
            controller.center = Vector3.zero;
            controller.skinWidth = 0.02f;
            controller.minMoveDistance = 0f;
            walker.AddComponent<ScopeWalker>();
            return walker;
        }

        // ------------------------------------------------------------------------------------ the scenario

        [Test]
        public void AWalkerNoPlayerOwnsBoardsAMovingPlatformRidesItAndWalksOff()
        {
            var ground = New("ground");
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.AddComponent<BoxCollider>().size = new Vector3(4000f, 1f, 4000f);
            var area = New("area");
            area.transform.position = new Vector3(0f, -50f, 0f);
            var world = area.AddComponent<Container>();
            world.ContainerId = "area";
            world.Size = new Vector3(4000f, 100f, 4000f);
            world.Center = new Vector3(0f, 50f, 0f);
            ContainerRegistry.Rebuild();
            ContainerRegistry.ApplyLease("area", "w1", 1, 1);
            var platformPrefab = PlatformPrefab();
            var walkerPrefab = WalkerPrefab();
            NetworkPrefabs.Register(new List<GameObject> { platformPrefab, walkerPrefab }); // prefab ids 0 and 1
            Physics.SyncTransforms();

            var platform = Spawn(0, world, new Vector3(10f, 0f, 0f), Quaternion.identity);
            var mover = platform.GetComponent<MovingPlatform>();
            var box = platform.Carried;
            var walker = Spawn(1, world, new Vector3(-4f, 0.93f, 0f), Quaternion.identity);
            var legs = walker.GetComponent<ScopeWalker>();
            Assert.AreEqual(0UL, walker.OwnerClientId, "no player owns the walker");
            mover.Velocity = new Vector3(0f, 0f, 1.5f);
            float bound = (legs.Speed + 1.5f) * Dt + 0.02f;

            // Board: walk to the spot on the drifting deck.
            legs.WalkTo(platform.transform.Find("spot"));
            for (int i = 0; i < 300 && !(walker.Container == box && legs.Arrived); i++) StepWithoutAJump(walker, bound);
            Assert.AreSame(box, walker.Container, "aboard the platform");
            Assert.IsTrue(legs.Arrived, "on the spot");

            // Fly: up to 1 km/s in a second, turning at 20 degrees a second.
            var standing = walker.LocalPosition;
            mover.TurnRate = 20f;
            for (int i = 0; i < 120; i++)
            {
                mover.Velocity = platform.transform.forward * (1000f * Mathf.Min(1f, (i + 1) / 60f));
                Tick();
                Assert.AreSame(box, walker.Container, $"flight tick {i}: aboard");
                Assert.That(Vector3.Distance(standing, walker.LocalPosition), Is.LessThan(0.05f), $"flight tick {i}: standing on the spot");
            }

            // Land: back to a drift, and walk off the side to a point on the ground.
            mover.TurnRate = 0f;
            mover.Velocity = new Vector3(0f, 0f, 1.5f);
            platform.transform.SetPositionAndRotation(new Vector3(-30f, 0f, 200f), Quaternion.Euler(0f, 90f, 0f));
            for (int i = 0; i < 5; i++) Tick();
            var off = platform.transform.TransformPoint(new Vector3(-10f, 0f, 0f));
            legs.WalkTo(off);
            for (int i = 0; i < 300 && !(walker.Container == world && legs.Arrived); i++) StepWithoutAJump(walker, bound);
            Assert.AreSame(world, walker.Container, "back on the ground");
            Assert.IsTrue(legs.Arrived, "at the point");
            var at = walker.ToScope(walker.transform.position);
            Assert.That(new Vector2(at.x - off.x, at.z - off.z).magnitude, Is.LessThan(0.15f));
        }

        private void StepWithoutAJump(NetworkIdentity walker, float bound)
        {
            var before = walker.ToScope(walker.transform.position);
            Tick();
            float step = Vector3.Distance(before, walker.ToScope(walker.transform.position));
            Assert.That(step, Is.LessThan(bound), $"tick {_tickNumber}: no jump in the scope ({step:0.000} m, {walker.Container?.ContainerId})");
        }
    }
}
