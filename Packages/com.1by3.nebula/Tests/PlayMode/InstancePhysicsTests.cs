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

        /// <summary>
        /// Conformance scenario 76 (<c>docs/conformance-suite.md</c>). A client that rejoins in the same process prepares its scopes again under the same ids (NEB-405). Forgetting a
        /// scope's scene (<c>Reset</c>) unloads it, and a scene still unloading does not block the next one: both
        /// times the scope gets a physics scene of its own that answers queries.
        /// </summary>
        [UnityTest]
        [Category("Conformance")]
        public IEnumerator ARejoiningClientPreparesTheSameScopeAgain()
        {
            const ulong Id = 91011;
            for (int session = 0; session < 3; session++)
            {
                var box = ContainerRegistry.RegisterRuntime(Id, new Bounds(Vector3.zero, Vector3.one * 10), new InstanceContainerInfo { InstanceId = Id });
                try
                {
                    Assert.That(InstanceScenes.Prepare(box), Is.True, "session " + session);
                    Assert.That(box.gameObject.scene.IsValid() && box.gameObject.scene.isLoaded, Is.True, "the container is in a scene of its own, session " + session);
                    Assert.That(box.gameObject.scene, Is.Not.EqualTo(SceneManager.GetActiveScene()));
                    var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    SceneManager.MoveGameObjectToScene(wall, box.gameObject.scene);
                    wall.transform.SetParent(box.transform, false);
                    Physics.SyncTransforms();
                    Assert.That(InstanceScenes.PhysicsFor(Id).Raycast(new Vector3(0, 0, -3), Vector3.forward, out _, 6), Is.True, "session " + session);
                }
                finally
                {
                    ContainerRegistry.UnregisterRuntime(Id);
                }
                // Alternate the two ways a scope's scene goes: forgotten wholesale, or released with its last container.
                if (session == 1) InstanceScenes.Reset();
                yield return null;
            }
            InstanceScenes.Reset();
            yield return null;
        }

        /// <summary>
        /// Conformance scenario 76. A client holds a scoped grid's chunk in its scope's physics scene as soon as it exists, though nothing is built
        /// in it yet (NEB-308); the public world's cells stay in the default one.
        /// </summary>
        [UnityTest]
        [Category("Conformance")]
        public IEnumerator AnEmptyScopedChunkOnAClientIsInItsScopesPhysicsScene()
        {
            const ulong Scope = 91021;
            bool wasClient = NebulaRuntime.IsClient;
            NebulaRuntime.IsClient = true;
            Container scoped = null, pub = null;
            try
            {
                scoped = ContainerRegistry.RegisterRuntime(91021, new Bounds(Vector3.zero, Vector3.one * 10), new InstanceContainerInfo { InstanceId = Scope, ScopeKey = "world/host" });
                pub = ContainerRegistry.RegisterRuntime(91022, new Bounds(new Vector3(100, 0, 0), Vector3.one * 10));
                Assert.That(scoped.gameObject.scene.GetPhysicsScene(), Is.EqualTo(InstanceScenes.PhysicsFor(Scope)), "the chunk is in its scope's scene");
                Assert.That(scoped.gameObject.scene.GetPhysicsScene(), Is.Not.EqualTo(Physics.defaultPhysicsScene));
                Assert.That(pub.gameObject.scene.GetPhysicsScene(), Is.EqualTo(Physics.defaultPhysicsScene), "a public cell stays in the default scene");
            }
            finally
            {
                NebulaRuntime.IsClient = wasClient;
                ContainerRegistry.UnregisterRuntime(91021);
                ContainerRegistry.UnregisterRuntime(91022);
            }
            yield return null;
        }
    }
}
