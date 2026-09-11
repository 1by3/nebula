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

        private NetworkIdentity MakeCarrier(string name, ulong netId, Vector3 position, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = name;
            box.Size = size;
            box.Center = new Vector3(0, size.y * 0.5f, 0);
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
        public void GeometryDescribesEveryStaticContainerWithItsEnclosingContainer()
        {
            string g = MeshTelemetry.BuildGeometryJson(null);
            StringAssert.StartsWith("{\"partitioned\":false,", g);
            StringAssert.Contains("{\"id\":\"hut\",\"index\":0,\"center\":[50,2.5,50],\"size\":[10,5,10],\"rotation\":[0,0,0,1],\"parent\":\"outdoor\",\"neighbors\":[\"outdoor\"]}", g);
            StringAssert.Contains("{\"id\":\"outdoor\",\"index\":1,\"center\":[0,30,0],\"size\":[200,60,200],\"rotation\":[0,0,0,1],\"parent\":\"\",", g);
        }

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
            // Slots in first-seen order: outdoor (the ship), hut (the player), ship#42 (the crew).
            StringAssert.Contains("\"containers\":[{\"id\":\"outdoor\",\"players\":0,\"bots\":0,\"serverDriven\":2,\"other\":0,\"ghosts\":1},{\"id\":\"hut\",\"players\":1,\"bots\":1,\"serverDriven\":0,\"other\":0,\"ghosts\":0},{\"id\":\"ship#42\",\"players\":0,\"bots\":0,\"serverDriven\":1,\"other\":0,\"ghosts\":0}]", doc);
            StringAssert.Contains("\"carried\":[{\"id\":\"ship#42\",\"carrier\":\"42\",\"enclosing\":\"outdoor\",\"depth\":1,\"pinned\":false,\"contents\":1,\"center\":[-20,3,0],\"size\":[10,6,20],\"rotation\":[0,0,0,1],\"position\":[-20,0,0]", doc);
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
