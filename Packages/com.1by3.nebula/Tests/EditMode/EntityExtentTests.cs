using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Unit tests of an entity's extent (<c>docs/entity-extents.md</c>): the box-to-seam distances the ghost band
    /// measures, the box computed from colliders, the API on <see cref="NetworkIdentity"/>, and the persistence chunk.
    /// The whole behaviour on two and three real workers is conformance scenario 28, <c>ConformanceEntityExtentTests</c>.
    /// </summary>
    public sealed class EntityExtentTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly Vector3[] _corners = new Vector3[8];

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
        }

        private Container Box(string id, Vector3 position, Vector3 size, Quaternion rotation = default)
        {
            var go = new GameObject(id);
            go.transform.SetPositionAndRotation(position, rotation == default ? Quaternion.identity : rotation);
            var c = go.AddComponent<Container>();
            c.ContainerId = id;
            c.Size = size;
            c.Center = Vector3.zero;
            _objects.Add(go);
            return c;
        }

        private NetworkIdentity Entity(string name, Vector3 position, Quaternion rotation = default)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(position, rotation == default ? Quaternion.identity : rotation);
            _objects.Add(go);
            return go.AddComponent<NetworkIdentity>();
        }

        private Vector3[] CornersOf(Bounds b)
        {
            EntityExtents.Corners(Matrix4x4.identity, b, _corners);
            return _corners;
        }

        private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-3f, string message = null)
        {
            Assert.AreEqual(expected.x, actual.x, tolerance, message);
            Assert.AreEqual(expected.y, actual.y, tolerance, message);
            Assert.AreEqual(expected.z, actual.z, tolerance, message);
        }

        // ------------------------------------------------------------------------------------ distances

        [Test]
        public void TheGapBetweenAnExtentAndABoxIsMeasuredToItsNearestFace()
        {
            var east = Box("east", new Vector3(150f, 0f, 0f), new Vector3(100f, 100f, 100f)); // x in [100, 200]
            Assert.AreEqual(0f, east.DistanceToCorners(CornersOf(new Bounds(new Vector3(50f, 0f, 0f), new Vector3(100f, 10f, 10f))), 8), 1e-3f,
                "a 100 m extent rooted 50 m from the seam reaches it exactly");
            Assert.AreEqual(10f, east.DistanceToCorners(CornersOf(new Bounds(new Vector3(40f, 0f, 0f), new Vector3(100f, 10f, 10f))), 8), 1e-3f);
            Assert.Less(east.DistanceToCorners(CornersOf(new Bounds(new Vector3(60f, 0f, 0f), new Vector3(100f, 10f, 10f))), 8), 0f,
                "an extent across the seam overlaps the box");
            // Diagonal: 3 m short on x and 4 m on z is 5 m away.
            Assert.AreEqual(5f, east.DistanceToCorners(CornersOf(new Bounds(new Vector3(47f, 0f, -104f), new Vector3(100f, 10f, 100f))), 8), 1e-3f);
        }

        [Test]
        public void ForASinglePointTheGapIsTheSignedDistance()
        {
            var box = Box("box", new Vector3(3f, -2f, 7f), new Vector3(10f, 4f, 6f), Quaternion.Euler(0f, 30f, 0f));
            foreach (var p in new[] { new Vector3(20f, 0f, 0f), new Vector3(3f, 9f, 7f), new Vector3(-9f, -2f, 20f) })
            {
                for (int i = 0; i < 8; i++) _corners[i] = p;
                Assert.AreEqual(box.SignedDistance(p), box.DistanceToCorners(_corners, 8), 1e-3f, $"at {p}");
            }
        }

        [Test]
        public void ARotatedBoxIsMeasuredInItsOwnFrame()
        {
            // A chunk turned 90 degrees about y: its long axis now runs along world z.
            var box = Box("turned", new Vector3(0f, 0f, 0f), new Vector3(200f, 10f, 20f), Quaternion.Euler(0f, 90f, 0f)); // world x in [-10, 10], z in [-100, 100]
            Assert.AreEqual(5f, box.DistanceToCorners(CornersOf(new Bounds(new Vector3(20f, 0f, 50f), new Vector3(10f, 2f, 10f))), 8), 1e-3f);
        }

        [Test]
        public void ARotatedExtentIsNeverMeasuredFartherThanItIs()
        {
            // A 100 x 2 m wall turned 45 degrees next to a box: the box around it is measured, which can only be nearer.
            var east = Box("east", new Vector3(150f, 0f, 0f), new Vector3(100f, 100f, 100f));
            var wall = Entity("wall", new Vector3(50f, 0f, 0f), Quaternion.Euler(0f, 45f, 0f));
            wall.SetExtent(new Bounds(Vector3.zero, new Vector3(100f, 2f, 2f)));
            EntityExtents.Corners(wall.transform.localToWorldMatrix, wall.Extent, _corners);
            float reach = 0f;
            for (int i = 0; i < 8; i++) reach = Mathf.Max(reach, _corners[i].x);
            float measured = east.DistanceToCorners(_corners, 8);
            Assert.LessOrEqual(measured, 100f - reach + 1e-3f);
            Assert.AreEqual(100f - reach, measured, 1e-3f, "aligned axes: the farthest corner is the one that counts");
        }

        [Test]
        public void TheSeamWithAnEnclosingBoxIsTheContainersOwnSurface()
        {
            var outdoor = Box("outdoor", Vector3.zero, new Vector3(1000f, 200f, 1000f));
            var building = Box("building", new Vector3(100f, 0f, 0f), new Vector3(40f, 20f, 40f)); // x in [80, 120]
            ContainerRegistry.Rebuild();
            Assert.IsTrue(outdoor.Encloses(building));

            var inside = CornersOf(new Bounds(new Vector3(100f, 0f, 0f), new Vector3(20f, 4f, 20f)));
            Assert.AreEqual(10f, NebulaWorker.ExtentSeamDistance(building, inside, outdoor), 1e-3f,
                "10 m from leaving the building on every side");
            var poking = CornersOf(new Bounds(new Vector3(110f, 0f, 0f), new Vector3(30f, 4f, 20f))); // x in [95, 125]
            Assert.Less(NebulaWorker.ExtentSeamDistance(building, poking, outdoor), 0f, "it reaches out of the building");
        }

        [Test]
        public void ASiblingBoxIsMeasuredByTheGapAndAnotherSpaceIsNeverASeam()
        {
            var west = Box("west", new Vector3(-50f, 0f, 0f), new Vector3(100f, 100f, 100f));
            var east = Box("east", new Vector3(50f, 0f, 0f), new Vector3(100f, 100f, 100f));
            ContainerRegistry.Rebuild();
            var corners = CornersOf(new Bounds(new Vector3(-30f, 0f, 0f), new Vector3(52f, 4f, 4f))); // x in [-56, -4]
            Assert.AreEqual(4f, NebulaWorker.ExtentSeamDistance(west, corners, east), 1e-3f);
        }

        // ------------------------------------------------------------------------------------ colliders

        [Test]
        public void TheColliderExtentBoundsEveryShapeInTheRootsSpace()
        {
            var e = Entity("structure", new Vector3(1000f, 0f, 500f), Quaternion.Euler(0f, 90f, 0f));
            var wall = new GameObject("wall");
            _objects.Add(wall);
            wall.transform.SetParent(e.transform, false);
            wall.transform.localPosition = new Vector3(40f, 0f, 0f);
            wall.AddComponent<BoxCollider>().size = new Vector3(2f, 10f, 30f);
            var dome = new GameObject("dome");
            _objects.Add(dome);
            dome.transform.SetParent(e.transform, false);
            dome.transform.localPosition = new Vector3(-20f, 5f, 0f);
            dome.AddComponent<SphereCollider>().radius = 6f;

            Assert.IsTrue(EntityExtents.TryComputeColliderExtent(e, out var box));
            AssertNear(new Vector3(-26f, -5f, -15f), box.min, 1e-3f, "the root's own rotation and position come off");
            AssertNear(new Vector3(41f, 11f, 15f), box.max);
        }

        [Test]
        public void ARoundShapeGrowsByTheLargestScaleAxis()
        {
            var e = Entity("prop", Vector3.zero);
            var ball = new GameObject("ball");
            _objects.Add(ball);
            ball.transform.SetParent(e.transform, false);
            ball.transform.localScale = new Vector3(1f, 3f, 1f);
            ball.AddComponent<SphereCollider>().radius = 1f;
            Assert.IsTrue(EntityExtents.TryComputeColliderExtent(e, out var box));
            AssertNear(new Vector3(6f, 6f, 6f), box.size, 1e-3f, "Unity scales a sphere by its largest axis, so it is 3 m in radius on every axis");

            var pill = new GameObject("pill");
            _objects.Add(pill);
            pill.transform.SetParent(e.transform, false);
            pill.transform.localPosition = new Vector3(20f, 0f, 100f);
            var capsule = pill.AddComponent<CapsuleCollider>();
            capsule.radius = 0.5f;
            capsule.height = 4f;
            capsule.direction = 2; // along z
            Assert.IsTrue(EntityExtents.TryComputeColliderExtent(e, out box));
            Assert.AreEqual(20.5f, box.max.x, 1e-3f);
            Assert.AreEqual(102f, box.max.z, 1e-3f, "along its axis a capsule is half its height");
        }

        [Test]
        public void TriggersDisabledCollidersAndRidersDoNotCount()
        {
            var e = Entity("ship", Vector3.zero);
            var hull = e.gameObject.AddComponent<BoxCollider>();
            hull.size = new Vector3(4f, 4f, 4f);

            var zone = new GameObject("zone");
            _objects.Add(zone);
            zone.transform.SetParent(e.transform, false);
            var trigger = zone.AddComponent<BoxCollider>();
            trigger.size = new Vector3(100f, 100f, 100f);
            trigger.isTrigger = true;

            var off = new GameObject("off");
            _objects.Add(off);
            off.transform.SetParent(e.transform, false);
            var disabled = off.AddComponent<BoxCollider>();
            disabled.size = new Vector3(50f, 50f, 50f);
            disabled.enabled = false;

            var inactive = new GameObject("inactive");
            _objects.Add(inactive);
            inactive.transform.SetParent(e.transform, false);
            inactive.AddComponent<BoxCollider>().size = new Vector3(60f, 60f, 60f);
            inactive.SetActive(false);

            var rider = new GameObject("rider");
            _objects.Add(rider);
            rider.transform.SetParent(e.transform, false);
            rider.transform.localPosition = new Vector3(0f, 0f, 30f);
            rider.AddComponent<NetworkIdentity>();
            rider.AddComponent<BoxCollider>();

            Assert.IsTrue(EntityExtents.TryComputeColliderExtent(e, out var box));
            AssertNear(new Vector3(4f, 4f, 4f), box.size, 1e-3f, "only the hull counts");
        }

        [Test]
        public void AnEntityWithoutCollidersHasNoColliderExtent()
        {
            var e = Entity("empty", Vector3.zero);
            Assert.IsFalse(EntityExtents.TryComputeColliderExtent(e, out _));
            e.UseColliderExtent();
            Assert.IsFalse(e.TryGetExtent(out _), "measured by its root, as if it had no extent");
            Assert.IsFalse(e.TryGetExtentForBand(1, out _));
        }

        // ------------------------------------------------------------------------------------ API

        [Test]
        public void AnEntityHasNoExtentUntilItIsGivenOne()
        {
            var e = Entity("crate", Vector3.zero);
            Assert.AreEqual(EntityExtentSource.None, e.ExtentSource);
            Assert.IsFalse(e.TryGetExtent(out _));
            Assert.IsFalse(e.TryGetExtentForBand(1, out _), "the ghost band measures it by its root, as before");
            Assert.IsFalse(e.ExtentChangedAtRuntime);
            Assert.AreEqual(default(Bounds), e.Extent);
        }

        [Test]
        public void SetAndClearAreRuntimeChanges()
        {
            var e = Entity("section", Vector3.zero);
            e.SetExtent(new Bounds(new Vector3(0f, 5f, 0f), new Vector3(-20f, 10f, 100f)));
            Assert.AreEqual(EntityExtentSource.Explicit, e.ExtentSource);
            Assert.IsTrue(e.ExtentChangedAtRuntime);
            Assert.IsTrue(e.TryGetExtentForBand(1, out var box));
            AssertNear(new Vector3(20f, 10f, 100f), box.size, 1e-6f, "a negative size is taken as its absolute value");

            e.ClearExtent();
            Assert.AreEqual(EntityExtentSource.None, e.ExtentSource);
            Assert.IsFalse(e.TryGetExtentForBand(2, out _));
            Assert.IsTrue(e.ExtentChangedAtRuntime, "a cleared extent is a runtime change too, so it travels and persists");
        }

        [Test]
        public void AColliderExtentFollowsItsCollidersOnRefreshAndOnTheRefreshInterval()
        {
            var e = Entity("structure", Vector3.zero);
            var wall = new GameObject("wall");
            _objects.Add(wall);
            wall.transform.SetParent(e.transform, false);
            var collider = wall.AddComponent<BoxCollider>();
            collider.size = new Vector3(10f, 10f, 10f);
            e.UseColliderExtent();
            Assert.IsTrue(e.TryGetExtentForBand(100, out var box));
            Assert.AreEqual(10f, box.size.x, 1e-3f);

            collider.size = new Vector3(30f, 10f, 10f);
            Assert.IsTrue(e.TryGetExtentForBand(101, out box));
            Assert.AreEqual(10f, box.size.x, 1e-3f, "not computed every tick");
            Assert.IsTrue(e.TryGetExtentForBand(100 + NetworkIdentity.ColliderExtentRefreshTicks, out box));
            Assert.AreEqual(30f, box.size.x, 1e-3f, "computed again on the refresh interval");

            collider.size = new Vector3(50f, 10f, 10f);
            e.RefreshExtent();
            Assert.IsTrue(e.TryGetExtentForBand(161, out box));
            Assert.AreEqual(50f, box.size.x, 1e-3f, "and at once after RefreshExtent");
        }

        [Test]
        public void AnAuthoredExtentIsNotARuntimeChange()
        {
            var prefab = new GameObject("authored");
            _objects.Add(prefab);
            prefab.SetActive(false);
            var e = prefab.AddComponent<NetworkIdentity>();
            // What the inspector writes: the serialized fields, not the runtime API.
            var type = typeof(NetworkIdentity);
            type.GetField("_extentSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(e, EntityExtentSource.Explicit);
            type.GetField("_extent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(e, new Bounds(Vector3.zero, new Vector3(8f, 8f, 8f)));
            var copy = Object.Instantiate(prefab).GetComponent<NetworkIdentity>();
            _objects.Add(copy.gameObject);
            Assert.AreEqual(EntityExtentSource.Explicit, copy.ExtentSource, "every process instantiates the authored box");
            AssertNear(new Vector3(8f, 8f, 8f), copy.Extent.size);
            Assert.IsFalse(copy.ExtentChangedAtRuntime, "so nothing needs to travel");
        }

        // ------------------------------------------------------------------------------------ persistence

        private NetworkIdentity Persistent(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var e = go.AddComponent<NetworkIdentity>();
            go.AddComponent<PersistentEntity>();
            e.Initialize();
            return e;
        }

        [Test]
        public void ARuntimeExtentIsSavedAndRestored()
        {
            var e = Persistent("section");
            e.SetExtent(new Bounds(new Vector3(0f, 6f, 64f), new Vector3(40f, 12f, 256f)));
            Assert.IsTrue(e.Persistent.IsDirty, "a runtime change asks for a checkpoint");
            var blob = PersistentStateCodec.Write(e);

            var restored = Persistent("section-restored");
            Assert.AreEqual(EntityExtentSource.None, restored.ExtentSource);
            PersistentStateCodec.Read(new NetworkReader(blob), restored);
            Assert.AreEqual(EntityExtentSource.Explicit, restored.ExtentSource);
            AssertNear(new Vector3(0f, 6f, 64f), restored.Extent.center);
            AssertNear(new Vector3(40f, 12f, 256f), restored.Extent.size);
            Assert.IsTrue(restored.ExtentChangedAtRuntime, "it travels on with the next handover");
        }

        [Test]
        public void AnEntityWithoutARuntimeExtentSavesTheRecordItAlwaysDid()
        {
            var plain = Persistent("crate");
            var blob = PersistentStateCodec.Write(plain);
            var r = new NetworkReader(blob);
            Assert.AreEqual(PersistentStateCodec.Version, r.ReadByte());
            Assert.AreEqual(0, r.ReadUShort(), "no entry: the extent chunk is written only for a runtime extent");
        }
    }
}
