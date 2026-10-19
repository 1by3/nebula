using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// A point carried between two boxes of one physics frame (a ship crossing a planet's chunks) goes through the frame's
    /// root in double (<see cref="PhysicsFrames.PointIn"/>, <see cref="PhysicsFrames.PointFrom"/>), so the answer is the same
    /// wherever the root stands: a client moves it between its drawn and simulated poses inside a frame, and a comparison
    /// through world matrices cached on either side of that move read the difference as a correction of metres.
    /// </summary>
    public sealed class FrameChildPointTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private Transform Child(string name, Vector3 position, Quaternion rotation)
        {
            var t = new GameObject(name).transform;
            t.SetParent(_root.transform, false);
            t.SetLocalPositionAndRotation(position, rotation);
            return t;
        }

        [Test]
        public void APointGoesFromOneBoxToAnotherAsTheirPosesSay()
        {
            _root = new GameObject("frame root");
            var a = Child("chunk a", new Vector3(120f, 3f, -40f), Quaternion.Euler(0f, 30f, 2f));
            var b = Child("chunk b", new Vector3(248f, -5f, -40f), Quaternion.Euler(1f, -20f, 0f));
            var local = new Vector3(12.5f, 40f, -7.25f);
            var expected = b.InverseTransformPoint(a.TransformPoint(local)); // the root at the identity: Unity's own answer is exact enough
            var got = PhysicsFrames.PointFrom(PhysicsFrames.PointIn(a, local, _root.transform), b, _root.transform);
            Assert.That(Vector3.Distance(expected, got), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(local, PhysicsFrames.PointFrom(PhysicsFrames.PointIn(a, local, _root.transform), a, _root.transform)), Is.LessThan(1e-5f), "there and back");
        }

        [Test]
        public void WhereTheRootStandsChangesNothing()
        {
            _root = new GameObject("frame root");
            var a = Child("chunk a", new Vector3(-150000f, 95000f, 100000f), Quaternion.Euler(10f, 40f, 0f));
            var b = Child("chunk b", new Vector3(-150128f, 95010f, 100004f), Quaternion.Euler(11f, 39f, 0f));
            var local = new Vector3(30f, 2f, -60f);
            var before = PhysicsFrames.PointFrom(PhysicsFrames.PointIn(a, local, _root.transform), b, _root.transform);
            // The root moved to a drawn pose 200 km away and turned, as a client poses a planet for drawing.
            _root.transform.SetPositionAndRotation(new Vector3(150000f, -95000f, -100000f), Quaternion.Euler(0f, 73f, 5f));
            var after = PhysicsFrames.PointFrom(PhysicsFrames.PointIn(a, local, _root.transform), b, _root.transform);
            Assert.AreEqual(before, after);
        }
    }
}
