using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public class ContainerTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        private Container Make(string id, Vector3 position, Vector3 size)
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

        [SetUp]
        public void SetUp()
        {
            Make("b-west", new Vector3(-10, 0, 0), new Vector3(20, 10, 20));
            Make("a-east", new Vector3(10, 0, 0), new Vector3(20, 10, 20));
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
        public void IndicesAreAssignedInIdOrder()
        {
            Assert.AreEqual(2, ContainerRegistry.Count);
            Assert.AreEqual("a-east", ContainerRegistry.Get(0).ContainerId);
            Assert.AreEqual("b-west", ContainerRegistry.Get(1).ContainerId);
        }

        [Test]
        public void TouchingContainersAreNeighbours()
        {
            var east = ContainerRegistry.FindById("a-east");
            var west = ContainerRegistry.FindById("b-west");
            CollectionAssert.Contains(east.Neighbors, west);
            CollectionAssert.Contains(west.Neighbors, east);
        }

        [Test]
        public void FindReturnsTheContainingVolume()
        {
            Assert.AreEqual("a-east", ContainerRegistry.Find(new Vector3(5, 1, 0)).ContainerId);
            Assert.AreEqual("b-west", ContainerRegistry.Find(new Vector3(-5, 1, 0)).ContainerId);
        }

        [Test]
        public void SignedDistanceIsNegativeInsideAndPositiveOutside()
        {
            var east = ContainerRegistry.FindById("a-east");
            // Depth is measured to the nearest wall only: from the centre of a 20x10x20 box that is 10 m, even though
            // the floor and ceiling are 5 m away (pawns stand on the floor; it is not a seam).
            Assert.AreEqual(-10f, east.SignedDistance(new Vector3(10, 5, 0)), 0.001f);
            Assert.AreEqual(-10f, east.SignedDistance(new Vector3(10, 0.1f, 0)), 0.001f);
            Assert.AreEqual(-2f, east.SignedDistance(new Vector3(2, 5, 0)), 0.001f);
            Assert.AreEqual(3f, east.SignedDistance(new Vector3(-3, 5, 0)), 0.001f);
            // Above the ceiling is outside, whatever the horizontal position.
            Assert.AreEqual(4f, east.SignedDistance(new Vector3(10, 14, 0)), 0.001f);
        }

        [Test]
        public void ResolveAppliesHysteresisAtTheBoundary()
        {
            var east = ContainerRegistry.FindById("a-east");
            var west = ContainerRegistry.FindById("b-west");
            // Just across the boundary into the west: not far enough yet.
            Assert.AreSame(east, ContainerRegistry.Resolve(new Vector3(-0.2f, 1, 0), east, 0.35f));
            // Past the hysteresis band: it moved.
            Assert.AreSame(west, ContainerRegistry.Resolve(new Vector3(-0.5f, 1, 0), east, 0.35f));
            // And it does not flap straight back.
            Assert.AreSame(west, ContainerRegistry.Resolve(new Vector3(0.2f, 1, 0), west, 0.35f));
        }

        [Test]
        public void NestedContainersResolveToTheInnermostBox()
        {
            // A small "room" box entirely inside the east box, like a building inside an outdoor container.
            var room = Make("c-room", new Vector3(10, 0, 0), new Vector3(4, 4, 4));
            ContainerRegistry.Rebuild();
            var east = ContainerRegistry.FindById("a-east");
            Assert.AreSame(room, ContainerRegistry.Find(new Vector3(10, 1, 0)));
            Assert.AreSame(east, ContainerRegistry.Find(new Vector3(15, 1, 0)));
            // Entering the room from the enclosing box honours hysteresis, then flips.
            Assert.AreSame(east, ContainerRegistry.Resolve(new Vector3(11.9f, 1, 0), east, 0.35f));
            Assert.AreSame(room, ContainerRegistry.Resolve(new Vector3(11.5f, 1, 0), east, 0.35f));
            // Leaving the room falls straight back to the enclosing box.
            Assert.AreSame(east, ContainerRegistry.Resolve(new Vector3(12.5f, 1, 0), room, 0.35f));
            CollectionAssert.Contains(room.Neighbors, east);
        }

        [Test]
        public void SegmentIntersectionReportsEntryAndExit()
        {
            var east = ContainerRegistry.FindById("a-east"); // x in [0, 20], y in [0, 10], z in [-10, 10]
            // Straight through along x, from outside to outside: enters at x=0 (t=0.25), leaves at x=20 (t=0.75).
            Assert.IsTrue(east.IntersectsSegment(new Vector3(-10, 5, 0), new Vector3(30, 5, 0), 0f, out float tEnter, out float tExit));
            Assert.AreEqual(0.25f, tEnter, 1e-4f);
            Assert.AreEqual(0.75f, tExit, 1e-4f);
            // Starting inside: enters at 0.
            Assert.IsTrue(east.IntersectsSegment(new Vector3(10, 5, 0), new Vector3(30, 5, 0), 0f, out tEnter, out tExit));
            Assert.AreEqual(0f, tEnter, 1e-4f);
            Assert.AreEqual(0.5f, tExit, 1e-4f);
            // Parallel to the box but above the ceiling: a miss, unless the margin reaches it.
            Assert.IsFalse(east.IntersectsSegment(new Vector3(-10, 11, 0), new Vector3(30, 11, 0), 0f, out _, out _));
            Assert.IsTrue(east.IntersectsSegment(new Vector3(-10, 11, 0), new Vector3(30, 11, 0), 1.5f, out _, out _));
            // Ends before reaching the box.
            Assert.IsFalse(east.IntersectsSegment(new Vector3(-10, 5, 0), new Vector3(-2, 5, 0), 0f, out _, out _));
        }

        [Test]
        public void AlongListsEveryBoxTheSegmentCrossesIncludingNested()
        {
            var room = Make("c-room", new Vector3(10, 0, 0), new Vector3(4, 4, 4));
            ContainerRegistry.Rebuild();
            var east = ContainerRegistry.FindById("a-east");
            var west = ContainerRegistry.FindById("b-west");
            var hit = new List<Container>();
            // West to east through the room: all three.
            ContainerRegistry.Along(new Vector3(-15, 1, 0), new Vector3(18, 1, 0), 0f, hit);
            CollectionAssert.AreEquivalent(new[] { east, west, room }, hit);
            // A shot that stays in the west box.
            hit.Clear();
            ContainerRegistry.Along(new Vector3(-15, 1, 0), new Vector3(-5, 1, 0), 0f, hit);
            CollectionAssert.AreEquivalent(new[] { west }, hit);
            // Grazing past the room (z = 3, room spans z in [-2, 2]) misses it without a margin and catches it with one.
            hit.Clear();
            ContainerRegistry.Along(new Vector3(5, 1, 3), new Vector3(18, 1, 3), 0f, hit);
            CollectionAssert.AreEquivalent(new[] { east }, hit);
            hit.Clear();
            ContainerRegistry.Along(new Vector3(5, 1, 3), new Vector3(18, 1, 3), 1.5f, hit);
            CollectionAssert.AreEquivalent(new[] { east, room }, hit);
        }
    }
}
