using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public class NetworkPrefabTests
    {
        [TearDown]
        public void TearDown()
        {
            NetworkPrefabs.Register(null);
            SceneEntities.Clear();
        }

        [Test]
        public void CopyOfPrefabWithLeftoverSceneIdIsNotASceneEntity()
        {
            // A prefab dragged out of a saved scene keeps the scene object's id.
            var prefab = new GameObject("player prefab");
            var parent = new GameObject("container");
            NetworkIdentity copy = null;
            try
            {
                prefab.AddComponent<NetworkIdentity>().SceneId = 4242;
                NetworkPrefabs.Register(new[] { prefab });
                parent.transform.position = new Vector3(10f, 0f, 10f);

                copy = NetworkPrefabs.Instantiate(0, new Vector3(12f, 0f, 9f), Quaternion.identity, parent.transform);

                Assert.AreEqual(0u, copy.SceneId);
                Assert.IsFalse(copy.IsSceneEntity);
                Assert.AreEqual(0, copy.PrefabId);
                Assert.IsTrue(copy.Initialized);
                Assert.AreSame(parent.transform, copy.transform.parent);
                Assert.AreEqual(new Vector3(12f, 0f, 9f), copy.transform.position, "world position survives the move out of the holder");
                Assert.IsTrue(copy.gameObject.activeInHierarchy);
                Assert.IsNull(SceneEntities.Find(4242));
                Assert.AreEqual(4242u, prefab.GetComponent<NetworkIdentity>().SceneId, "the prefab asset itself is left alone");
            }
            finally
            {
                if (copy != null) Object.DestroyImmediate(copy.gameObject);
                Object.DestroyImmediate(parent);
                Object.DestroyImmediate(prefab);
            }
        }
    }
}
