using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    public class InstancePhysicsTests
    {
        [UnityTest]
        public IEnumerator PrivatePhysicsCannotHitAnotherCopy()
        {
            var first = ContainerRegistry.RegisterRuntime(91001, new Bounds(Vector3.zero, Vector3.one * 10), new InstanceContainerInfo { InstanceId = 91001 });
            var second = ContainerRegistry.RegisterRuntime(91002, new Bounds(Vector3.zero, Vector3.one * 10), new InstanceContainerInfo { InstanceId = 91002 });
            try
            {
                Assert.That(InstanceScenes.Prepare(first), Is.True);
                Assert.That(InstanceScenes.Prepare(second), Is.True);
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(wall, first.gameObject.scene);
                wall.transform.SetParent(first.transform, false);
                Physics.SyncTransforms();
                Assert.That(InstanceScenes.PhysicsFor(91001).Raycast(new Vector3(0,0,-3), Vector3.forward, out _, 6), Is.True);
                Assert.That(InstanceScenes.PhysicsFor(91002).Raycast(new Vector3(0,0,-3), Vector3.forward, out _, 6), Is.False);
            }
            finally
            {
                ContainerRegistry.UnregisterRuntime(91001);
                ContainerRegistry.UnregisterRuntime(91002);
            }
            yield return null;
        }
    }
}
