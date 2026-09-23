using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>The World map's data path: what a worker writes about itself, what the orchestrator keeps, and the static geometry.</summary>
    public class MeshTelemetryTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private double _now;

        private MeshTelemetry Make() => new MeshTelemetry(() => _now);

        private Container MakeStatic(string id, Vector3 position, Vector3 size)
        {
            var go = new GameObject(id);
            go.transform.position = position;
            var c = go.AddComponent<Container>();
            c.ContainerId = id;
            c.Size = size;
            c.Center = new Vector3(0, size.y * 0.5f, 0);
            _objects.Add(go);
            return c;
        }

        private NetworkIdentity MakeEntity(string name, ulong netId, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            identity.Initialize();
            identity.NetId = netId;
            identity.HasAuthority = true;
            identity.SetContainer(ContainerRegistry.Find(position));
            return identity;
        }

        private NetworkIdentity MakeCarrier(string name, ulong netId, Vector3 position, Vector3 size, bool framed = false, float yaw = 0f)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = name;
            box.Size = size;
            box.Center = new Vector3(0, size.y * 0.5f, 0);
            box.OwnPhysicsFrame = framed;
            go.AddComponent<DynamicContainer>();
            identity.Initialize();
            identity.NetId = netId;
            identity.HasAuthority = true;
            identity.IsServerDriven = true;
            identity.SetContainer(ContainerRegistry.Find(position, box));
            identity.InvokeSpawn();
            return identity;
        }

        [SetUp]
        public void SetUp()
        {
            _now = 100;
            MakeStatic("outdoor", new Vector3(0, 0, 0), new Vector3(200, 60, 200));
            MakeStatic("hut", new Vector3(50, 0, 50), new Vector3(10, 5, 10));
            ContainerRegistry.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        [Test]
        public void TheLatestDocumentPerWorkerIsKeptAndServedInWorkerOrder()
        {
            var t = Make();
            Assert.IsNull(t.Accept("{\"worker\":\"w2\",\"tick\":7}", out _));
            Assert.IsNull(t.Accept("{\"worker\":\"w1\",\"tick\":1}", out _));
            Assert.IsNull(t.Accept("{\"worker\":\"w1\",\"tick\":2}\n", out _));
            Assert.AreEqual(2, t.WorkerCount);
            string map = t.BuildMapJson();
            StringAssert.Contains("\"doc\":{\"worker\":\"w1\",\"tick\":2}", map);
            StringAssert.DoesNotContain("\"tick\":1}", map);
            Assert.Less(map.IndexOf("\"id\":\"w1\""), map.IndexOf("\"id\":\"w2\""));
        }

        [Test]
        public void DocumentsThatAreNotAWorkerObjectAreRefused()
        {
            var t = Make();
            Assert.IsNotNull(t.Accept("", out _));
            Assert.IsNotNull(t.Accept("[1,2]", out _));
            Assert.IsNotNull(t.Accept("{\"tick\":1,\"worker\":\"w1\"}", out _), "the worker id must come first");
            Assert.IsNotNull(t.Accept("{\"worker\":\"w1\"", out _), "truncated");
            Assert.IsNotNull(t.Accept("{\"worker\":\"w\\\"1\"}", out _), "ids are plain");
            Assert.AreEqual(0, t.WorkerCount);
        }

        [Test]
        public void WorkersAreAskedForEntitiesOnlyWhileSomebodyReadsTheMap()
        {
            var t = Make();
            t.Accept("{\"worker\":\"w1\"}", out bool detail);
            Assert.IsFalse(detail);
            t.BuildMapJson();
            _now += 1;
            t.Accept("{\"worker\":\"w1\"}", out detail);
            Assert.IsTrue(detail);
            _now += MeshTelemetry.DetailWindowSeconds + 1;
            t.Accept("{\"worker\":\"w1\"}", out detail);
            Assert.IsFalse(detail);
        }

        [Test]
        public void StaleAndForgottenWorkersLeaveTheMap()
        {
            var t = Make();
            t.Accept("{\"worker\":\"w1\"}", out _);
            t.Accept("{\"worker\":\"w3\"}", out _);
            _now += MeshTelemetry.ExpireSeconds + 1;
            t.Accept("{\"worker\":\"w2\"}", out _);
            t.Forget("w3");
            string map = t.BuildMapJson();
            StringAssert.DoesNotContain("\"w1\"", map);
            StringAssert.DoesNotContain("\"w3\"", map);
            StringAssert.Contains("\"id\":\"w2\"", map);
            Assert.AreEqual(1, t.WorkerCount);
        }

        [Test]
        public void AbsolutePositionsAddTheFloatingOriginBack()
        {
            MeshTelemetry.ToAbsolute(new Vector3(1, 2, 3), new Vector3Int(2, 0, -1), new Vector3(256, 128, 256), out double x, out double y, out double z);
            Assert.AreEqual(513, x, 1e-6);
            Assert.AreEqual(2, y, 1e-6);
            Assert.AreEqual(-253, z, 1e-6);
        }

        [Test]
        public void RuntimeOnlyWorldReportsAbsoluteEntityPositionsAndItsOrigin()
        {
            var world = ScriptableObject.CreateInstance<Nebula.World.WorldDefinition>();
            try
            {
                world.WorldName = "Runtime world";
                world.CellSize = new Vector3(64f, 256f, 64f);
                NebulaWorld.LoadRuntime(world);
                NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(0, 0, -3));
                ContainerRegistry.Load(new List<Container>(), gridded: false);
                Assert.IsFalse(ContainerRegistry.IsGridded, "a runtime-only world intentionally has no authored container grid");

                var player = MakeEntity("Player", 9, new Vector3(30.8f, 1f, -85.31f));
                player.OwnerClientId = 7;
                string doc = WorkerTelemetry.ForTests().Write("w3", 3, 10, new[] { player }, true, null);

                StringAssert.Contains("\"origin\":[0,0,-3]", doc);
                StringAssert.Contains("[\"9\",\"p\",30.8,1,-277.31", doc);
                StringAssert.StartsWith("{\"partitioned\":true,", MeshTelemetry.BuildGeometryJson(null));
            }
            finally
            {
                NebulaWorld.Unload();
                Object.DestroyImmediate(world);
                ContainerRegistry.Rebuild();
            }
        }

        [Test]
        public void GeometryDescribesEveryStaticContainerWithItsEnclosingContainer()
        {
            string g = MeshTelemetry.BuildGeometryJson(null);
            StringAssert.StartsWith("{\"partitioned\":false,", g);
            StringAssert.Contains("{\"id\":\"hut\",\"index\":0,\"center\":[50,2.5,50],\"size\":[10,5,10],\"rotation\":[0,0,0,1],\"parent\":\"outdoor\",\"neighbors\":[\"outdoor\"]}", g);
            StringAssert.Contains("{\"id\":\"outdoor\",\"index\":1,\"center\":[0,30,0],\"size\":[200,60,200],\"rotation\":[0,0,0,1],\"parent\":\"\",", g);
        }

        [Test]
        public void InsideAPhysicsFrameTheDocumentReportsWhereThingsAreInTheScope()
        {
            // docs/container-tree.md §3: a worker keeps what is inside a frame in the frame's coordinates. The map draws
            // the scope, so the crew and a room fixed in the ship are reported where they are, not at frame-local numbers.
            NebulaRuntime.IsServer = true; NebulaRuntime.IsClient = false; // a worker: frames stay in simulation space
            PhysicsFrames.SceneFactory = () => UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(s);
            var ship = MakeCarrier("ship", 42, new Vector3(-20, 0, 0), new Vector3(10, 6, 20), framed: true, yaw: 90f);
            Assert.IsNotNull(ship.Carried.Frame);
            var room = ContainerRegistry.RegisterRuntime(7, ContainerPlacement.Child("ship#42", new Vector3(0, 1, 4), new Vector3(4, 2, 4), ContainerAuthority.Leased));
            Assert.AreSame(ship.Carried, room.Parent);

            var go = new GameObject("crew");
            _objects.Add(go);
            var crew = go.AddComponent<NetworkIdentity>();
            crew.Initialize();
            crew.NetId = 5;
            crew.HasAuthority = true;
            crew.IsServerDriven = true;
            crew.SetContainer(ship.Carried);
            crew.transform.localPosition = new Vector3(0, 1, 2); // frame-local
            crew.transform.localRotation = Quaternion.identity;

            string doc = WorkerTelemetry.ForTests().Write("w1", 1, 7, new[] { ship, crew }, true, null);

            // The ship faces +x: two metres forward of its origin is two metres east.
            StringAssert.Contains("[\"5\",\"n\",-18,1,0,90,", doc);
            var roomCenter = ship.transform.TransformPoint(room.ToWorld(room.Center));
            StringAssert.Contains($"{{\"id\":\"rt_7\",\"carrier\":\"42\",\"enclosing\":\"ship#42\",", doc);
            StringAssert.Contains("\"fixed\":true,\"pinned\":true,\"frame\":false,", doc);
            StringAssert.Contains($"\"center\":[{R(roomCenter.x)},{R(roomCenter.y)},{R(roomCenter.z)}]", doc);
            StringAssert.Contains("\"frame\":true,", doc, "the ship itself says it has a frame");
        }

        private static string R(float v) => System.Math.Round((double)v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);

        [Test]
        public void AWorkerDocumentCountsWhatItHoldsPerContainerAndReportsItsCarriers()
        {
            var ship = MakeCarrier("ship", 42, new Vector3(-20, 0, 0), new Vector3(10, 6, 20));
            var player = MakeEntity("Player Jesse (3)", 1, new Vector3(50, 1, 50));
            player.OwnerClientId = 3;
            var bot = MakeEntity("Bot", 2, new Vector3(51, 1, 50));
            bot.OwnerClientId = 4;
            bot.OwnerIsBot = true;
            var npc = MakeEntity("npc", 3, new Vector3(0, 1, 0));
            npc.IsServerDriven = true;
            var ghost = MakeEntity("ghost", 4, new Vector3(1, 1, 0));
            ghost.HasAuthority = false;
            var crew = MakeEntity("crew", 5, new Vector3(-20, 1, 0));
            crew.IsServerDriven = true;
            Assert.AreEqual("ship#42", crew.Container.ContainerId);

            var telemetry = WorkerTelemetry.ForTests();
            var entities = new[] { ship, player, bot, npc, ghost, crew };
            string doc = telemetry.Write("w2", 2, 99, entities, true, null);

            StringAssert.StartsWith("{\"worker\":\"w2\",\"index\":2,\"tick\":99,\"detail\":true,", doc);
            // Slots in first-seen order: outdoor (the ship), hut (the player), ship#42 (the crew). Each row also
            // carries its cost telemetry (docs/cost-telemetry.md): the scope, the weighted entity sum, and the
            // measured rates, which are 0 here because nothing has been ticked.
            StringAssert.Contains("\"containers\":[" +
                "{\"id\":\"outdoor\",\"owned\":1,\"players\":0,\"bots\":0,\"serverDriven\":2,\"other\":0,\"ghosts\":1,\"scope\":\"\",\"cost\":2,\"tickMs\":0,\"bytesOut\":0,\"gatewayBytes\":0}," +
                "{\"id\":\"hut\",\"owned\":1,\"players\":1,\"bots\":1,\"serverDriven\":0,\"other\":0,\"ghosts\":0,\"scope\":\"\",\"cost\":6,\"tickMs\":0,\"bytesOut\":0,\"gatewayBytes\":0}," +
                "{\"id\":\"ship#42\",\"owned\":1,\"players\":0,\"bots\":0,\"serverDriven\":1,\"other\":0,\"ghosts\":0,\"scope\":\"\",\"enclosing\":\"outdoor\",\"cost\":1,\"tickMs\":0,\"bytesOut\":0,\"gatewayBytes\":0}]", doc);
            StringAssert.Contains("\"carried\":[{\"id\":\"ship#42\",\"carrier\":\"42\",\"enclosing\":\"outdoor\",\"depth\":1,\"pinned\":false,\"frame\":false,\"contents\":1,\"center\":[-20,3,0],\"size\":[10,6,20],\"rotation\":[0,0,0,1],\"position\":[-20,0,0]", doc);
            StringAssert.Contains("[\"42\",\"v\",-20,0,0,0,0]", doc);
            StringAssert.Contains("[\"1\",\"p\",50,1,50,0,1,\"Player Jesse (3)\"]", doc);
            StringAssert.Contains("[\"2\",\"b\",51,1,50,0,1,\"Bot\"]", doc);
            StringAssert.Contains("[\"5\",\"n\",-20,1,0,0,2]", doc);
            StringAssert.DoesNotContain("[\"4\",", doc, "ghosts are counted, not drawn: their authority draws them");
            Assert.IsNull(Make().Accept(doc, out _), "the orchestrator accepts what a worker writes");

            string idle = telemetry.Write("w2", 2, 100, entities, false, new[] { new Vector3Int(0, 0, 0) });
            StringAssert.DoesNotContain("\"entities\"", idle);
            StringAssert.Contains("\"loadedCells\":[[0,0,0]]", idle);
            StringAssert.Contains("\"carried\":[{\"id\":\"ship#42\"", idle, "carried containers are reported even while nobody watches");
        }
    }
}
