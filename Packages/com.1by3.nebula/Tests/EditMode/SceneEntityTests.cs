using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public class SceneEntityTests
    {
        private sealed class Door : NetworkBehaviour
        {
            public NetworkVariable<bool> IsOpen = new NetworkVariable<bool>(false);
        }

        [TearDown]
        public void TearDown()
        {
            SceneEntities.Clear();
        }

        /// <summary>Awake does not run in edit mode, so the test does what Awake does for a scene entity: initialise and register.</summary>
        private static NetworkIdentity MakeSceneEntity(uint sceneId)
        {
            var go = new GameObject($"scene entity {sceneId}");
            var id = go.AddComponent<NetworkIdentity>();
            go.AddComponent<Door>();
            id.SceneId = sceneId;
            id.Initialize();
            SceneEntities.Register(id);
            return id;
        }

        [Test]
        public void SceneObjectRegistersAndLeavesOnDestroy()
        {
            var id = MakeSceneEntity(1234);
            try
            {
                Assert.IsTrue(id.IsSceneEntity);
                Assert.IsTrue(id.Initialized);
                Assert.AreSame(id, SceneEntities.Find(1234));
                Assert.AreEqual(1, id.Behaviours.Length);
                Assert.IsFalse(id.IsSpawned, "registered is not spawned");
                SceneEntities.Unregister(id); // what OnDestroy does (not run by DestroyImmediate in edit mode)
                Assert.IsNull(SceneEntities.Find(1234));
                Assert.AreEqual(0, SceneEntities.Count);
            }
            finally
            {
                Object.DestroyImmediate(id.gameObject);
            }
        }

        [Test]
        public void PrefabStyleIdentityDoesNotRegister()
        {
            var go = new GameObject("prefab-like");
            try
            {
                var id = go.AddComponent<NetworkIdentity>();
                id.Initialize();
                SceneEntities.Register(id); // a no-op without a scene id
                Assert.AreEqual(0, SceneEntities.Count);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UnbindReturnsToTheUnspawnedStateKeepingValues()
        {
            var id = MakeSceneEntity(77);
            try
            {
                var door = id.GetComponent<Door>();
                // Bind the way a client does: net id, epoch, container-less pose, values from the wire.
                id.NetId = (2UL << 48) | 5;
                id.Epoch = 3;
                id.OwnerWorkerIndex = 2;
                id.InvokeSpawn();
                var w = new NetworkWriter();
                w.WriteBool(true);
                id.ReadVars(new NetworkReader(w.ToSegment()));
                Assert.IsTrue(door.IsOpen.Value);
                Assert.IsTrue(id.IsSpawned);

                id.Unbind();
                Assert.IsFalse(id.IsSpawned);
                Assert.AreEqual(0UL, id.NetId);
                Assert.AreEqual(0u, id.Epoch);
                Assert.IsTrue(door.IsOpen.Value, "the last replicated value stays until the next binding overwrites it");
                Assert.AreSame(id, SceneEntities.Find(77), "still resident: the object belongs to its scene");
            }
            finally
            {
                Object.DestroyImmediate(id.gameObject);
            }
        }

        [Test]
        public void SpawnMessageCarriesTheSceneId()
        {
            var id = MakeSceneEntity(99);
            try
            {
                var msg = EntitySpawnMsg.From(id, new NetworkWriter());
                Assert.AreEqual(99u, msg.SceneId);
                Assert.AreEqual(ushort.MaxValue, msg.PrefabId, "a scene entity has no prefab id");
            }
            finally
            {
                Object.DestroyImmediate(id.gameObject);
            }
        }
    }
}
