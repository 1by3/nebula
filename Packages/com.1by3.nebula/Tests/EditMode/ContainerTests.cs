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
        public void FitToBoundsWrapsChildRenderersInLocalSpace()
        {
            var go = new GameObject("fit");
            _objects.Add(go);
            go.transform.SetPositionAndRotation(new Vector3(100, 0, 0), Quaternion.Euler(0, 45, 0));
            var c = go.AddComponent<Container>();
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(go.transform, false);
            cube.transform.localPosition = new Vector3(2, 1, 0);
            cube.transform.localScale = new Vector3(4, 2, 6);
            Object.DestroyImmediate(cube.GetComponent<Collider>());

            Assert.IsTrue(c.FitToBounds());
            AssertNear(new Vector3(2, 1, 0), c.Center);
            AssertNear(new Vector3(4, 2, 6), c.Size);
        }

        [Test]
        public void FitToBoundsLeavesAnEmptyObjectUnchanged()
        {
            var c = Make("empty", new Vector3(0, 100, 0), new Vector3(7, 8, 9));
            var center = c.Center;
            Assert.IsFalse(c.FitToBounds());
            Assert.AreEqual(new Vector3(7, 8, 9), c.Size);
            Assert.AreEqual(center, c.Center);
        }

        [Test]
        public void NewContainerIdsAreRandom()
        {
            StringAssert.StartsWith("container-", Container.NewContainerId());
            Assert.AreNotEqual(Container.NewContainerId(), Container.NewContainerId());
        }

        private static void AssertNear(Vector3 expected, Vector3 actual)
        {
            Assert.Less(Vector3.Distance(expected, actual), 1e-3f, $"expected {expected} but was {actual}");
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
            // Leaving the room past the band falls back to the enclosing box.
            Assert.AreSame(east, ContainerRegistry.Resolve(new Vector3(12.5f, 1, 0), room, 0.35f));
            CollectionAssert.Contains(room.Neighbors, east);
        }

        [Test]
        public void ResolveKeepsAHysteresisBandAtANestedBoxsFloor()
        {
            // A raised deck inside the east box, like a ship's interior entered up a ramp through its floor.
            var deck = Make("c-deck", new Vector3(10, 4, 0), new Vector3(6, 4, 6));
            ContainerRegistry.Rebuild();
            var east = ContainerRegistry.FindById("a-east");
            float floor = deck.WorldBounds.min.y;
            // Depth ignores the floor, so a point just above it and well inside the walls is already in the deck.
            Assert.AreSame(deck, ContainerRegistry.Resolve(new Vector3(10, floor + 0.01f, 0), east, 0.35f));
            // Dipping just below the floor does not flip it straight back out...
            Assert.AreSame(deck, ContainerRegistry.Resolve(new Vector3(10, floor - 0.1f, 0), deck, 0.35f));
            // ...clearly below it does.
            Assert.AreSame(east, ContainerRegistry.Resolve(new Vector3(10, floor - 0.5f, 0), deck, 0.35f));
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
