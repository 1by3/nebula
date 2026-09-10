using System.Collections.Generic;
using System.Linq;
using Nebula.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public class EditorSetupTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created) if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
            foreach (var c in Object.FindObjectsByType<Container>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (c.ContainerId.StartsWith("setuptest")) Object.DestroyImmediate(c.gameObject);
        }

        private GameObject Make(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        [Test]
        public void SplitTilesTheFootprintOfARotatedScaledContainer()
        {
            var go = Make("setuptest");
            go.transform.SetPositionAndRotation(new Vector3(5f, 1f, -3f), Quaternion.Euler(0f, 30f, 0f));
            go.transform.localScale = new Vector3(2f, 1f, 1f);
            var box = go.AddComponent<Container>();
            box.ContainerId = "setuptest";
            box.Size = new Vector3(20f, 10f, 40f);
            box.Center = new Vector3(3f, 5f, -2f);

            var samples = new List<Vector3>();
            foreach (float fx in new[] { -0.9f, -0.3f, 0.2f, 0.8f })
            foreach (float fy in new[] { -0.4f, 0.4f })
            foreach (float fz in new[] { -0.7f, -0.1f, 0.4f, 0.9f })
                samples.Add(box.ToWorld(box.Center + Vector3.Scale(box.Size * 0.5f, new Vector3(fx, fy, fz))));
            float volume = box.Volume;

            var parts = NebulaSetup.Split(box, 2, 2);

            Assert.That(box == null, "the original container is removed");
            Assert.AreEqual(4, parts.Count);
            CollectionAssert.AreEquivalent(new[] { "setuptest-0-0", "setuptest-0-1", "setuptest-1-0", "setuptest-1-1" }, parts.Select(p => p.ContainerId));
            foreach (var p in samples)
                Assert.AreEqual(1, parts.Count(c => c.Contains(p)), $"sample {p} lies in exactly one part");
            Assert.AreEqual(volume, parts.Sum(c => c.Size.x * c.Size.y * c.Size.z) * 2f, 0.01f, "parts keep the scaled volume");
        }

        [Test]
        public void UniqueContainerIdSkipsIdsInUse()
        {
            Make("a").AddComponent<Container>().ContainerId = "setuptest-id";
            Assert.AreEqual("setuptest-free", NebulaSetup.UniqueContainerId("setuptest-free"));
            Assert.AreEqual("setuptest-id-2", NebulaSetup.UniqueContainerId("setuptest-id"));
        }

        [Test]
        public void HubKeepsCamerasAndSunButNotLevelContent()
        {
            Assert.IsTrue(WorldSetup.StaysInHub(Make("camera").AddComponent<Camera>().gameObject));

            var sun = Make("sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            Assert.IsTrue(WorldSetup.StaysInHub(sun.gameObject));

            var lamp = Make("lamp").AddComponent<Light>();
            lamp.type = LightType.Point;
            Assert.IsFalse(WorldSetup.StaysInHub(lamp.gameObject));

            var level = Make("level");
            level.AddComponent<MeshRenderer>();
            Assert.IsFalse(WorldSetup.StaysInHub(level));
        }
    }
}
