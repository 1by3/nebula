using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>An <see cref="InstanceBoundary"/>'s interior turns with the boundary; with no rotation it is the axis-aligned box it always was.</summary>
    public sealed class InstanceBoundaryOrientationTests
    {
        private GameObject _go;
        private InstanceBoundary _door;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("door");
            _door = _go.AddComponent<InstanceBoundary>();
            _door.Interior = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(4f, 4f, 1f));
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_go);

        [Test]
        public void ATiltedDoorDetectsACrossingThroughItAndNotBesideIt()
        {
            var tilt = Quaternion.Euler(35f, 0f, 0f);
            Assert.IsTrue(_door.InteriorContains(tilt * new Vector3(0f, 2f, 0f), tilt, 0f));
            Assert.IsTrue(_door.InteriorContains(tilt * new Vector3(1.5f, 3.9f, 0.45f), tilt, 0f));
            Assert.IsFalse(_door.InteriorContains(tilt * new Vector3(0f, 2f, 0.7f), tilt, 0f));
            Assert.IsFalse(_door.InteriorContains(tilt * new Vector3(0f, 4.2f, 0f), tilt, 0f));
            // Ground under the unturned box that the tilted door's volume does not cover.
            Assert.IsFalse(_door.InteriorContains(new Vector3(0f, 0.3f, -0.45f), tilt, 0f));
            Assert.IsTrue(_door.InteriorContains(new Vector3(0f, 0.3f, -0.45f), Quaternion.identity, 0f));
        }

        [Test]
        public void TheIdentityRotationBehavesAsTheAxisAlignedBoundsDid()
        {
            foreach (var p in new[] { new Vector3(0f, 2f, 0f), new Vector3(2f, 4f, 0.5f), new Vector3(2.01f, 2f, 0f), new Vector3(0f, -0.01f, 0f), new Vector3(0f, 2f, 0.9f) })
            {
                Assert.AreEqual(_door.Interior.Contains(p), _door.InteriorContains(p, Quaternion.identity, 0f), p.ToString());
                var wide = _door.Interior;
                wide.Expand(1f);
                Assert.AreEqual(wide.Contains(p), _door.InteriorContains(p, Quaternion.identity, 0.5f), p.ToString());
            }
        }
    }
}
