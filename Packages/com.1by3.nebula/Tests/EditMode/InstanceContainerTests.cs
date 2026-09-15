using System.Collections.Generic;
using Nebula;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace Nebula.Tests
{
    public class InstanceContainerTests
    {
        private GameObject _public;
        [SetUp]
        public void Setup()
        {
            _public = new GameObject("public");
            var c = _public.AddComponent<Container>(); c.Center = Vector3.zero; c.Size = Vector3.one * 100;
            ContainerRegistry.Load(new[] { c }, false);
        }

        [TearDown]
        public void Cleanup()
        {
            ContainerRegistry.Load(new Container[0], false);
            Object.DestroyImmediate(_public);
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.name.StartsWith("Nebula instance ")) EditorSceneManager.CloseScene(scene, true);
            }
            InstanceScenes.Reset();
        }

        private Container Private(ulong id, ulong scope) => ContainerRegistry.RegisterRuntime(id,
            new Bounds(Vector3.zero, Vector3.one * 10), new InstanceContainerInfo { InstanceId = scope });

        [Test]
        public void OverlappingCopiesResolveOnlyWithinTheirScope()
        {
            var first = Private(1, 101); var second = Private(2, 202);
            Assert.That(ContainerRegistry.Find(Vector3.zero), Is.EqualTo(_public.GetComponent<Container>()));
            Assert.That(ContainerRegistry.Find(Vector3.zero, instanceId: 101), Is.EqualTo(first));
            Assert.That(ContainerRegistry.Resolve(new Vector3(20,0,0), second, 0), Is.EqualTo(second), "walking cannot escape a scope without a boundary transfer");
            var along = new List<Container>();
            ContainerRegistry.Along(new Vector3(-20,0,0), new Vector3(20,0,0), 0, along, 101);
            Assert.That(along, Is.EquivalentTo(new[] { first }));
            ContainerRegistry.NeighborsOf(first, along);
            Assert.That(along, Is.Empty);
        }

        [Test]
        public void MissingContentCannotBecomeReady()
        {
            var c = ContainerRegistry.RegisterRuntime(1, new Bounds(Vector3.zero, Vector3.one),
                new InstanceContainerInfo { InstanceId=101, ContentResource="missing-instance-content" });
            Assert.That(InstanceScenes.Prepare(c), Is.False);
        }
    }
}
