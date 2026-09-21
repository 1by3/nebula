using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The bucketing the gateway and the worker share: dense region lists that survive swap-removes, carried
    /// entities that move as one unit with their carrier, and the two lists that exist so nobody has to widen a
    /// scan for the handful of entities that are visible from far away.
    /// </summary>
    public class InterestIndexTests
    {
        private static List<ulong> Ids(InterestIndex<string>.Bucket bucket)
        {
            var ids = new List<ulong>();
            foreach (var entry in bucket) ids.Add(entry.Id);
            ids.Sort();
            return ids;
        }

        [Test]
        public void ItemsLandInTheirRegionAndComeBackOut()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 100, "a");
            index.Add(2, 100, "b");
            index.Add(3, 200, "c");
            Assert.AreEqual(3, index.Count);
            Assert.AreEqual(2, index.RegionCount);
            CollectionAssert.AreEqual(new ulong[] { 1, 2 }, Ids(index.Region(100)));
            Assert.IsTrue(index.TryGetValue(3, out string value));
            Assert.AreEqual("c", value);
        }

        [Test]
        public void SwapRemoveKeepsEveryRemainingItemFindable()
        {
            var index = new InterestIndex<string>();
            for (ulong id = 1; id <= 5; id++) index.Add(id, 7, "e" + id);
            Assert.IsTrue(index.Remove(1));
            Assert.IsTrue(index.Remove(3));
            CollectionAssert.AreEqual(new ulong[] { 2, 4, 5 }, Ids(index.Region(7)));
            // The items the swap moved must still be reachable by id, or a later remove would corrupt the list.
            Assert.IsTrue(index.Remove(5));
            Assert.IsTrue(index.Remove(2));
            CollectionAssert.AreEqual(new ulong[] { 4 }, Ids(index.Region(7)));
            Assert.IsTrue(index.Remove(4));
            Assert.AreEqual(0, index.RegionCount, "an empty region is dropped so the region count stays meaningful");
        }

        [Test]
        public void MovingAnItemRebucketsItOnce()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "a");
            index.Add(2, 10, "b");
            Assert.IsTrue(index.Move(1, 20));
            CollectionAssert.AreEqual(new ulong[] { 2 }, Ids(index.Region(10)));
            CollectionAssert.AreEqual(new ulong[] { 1 }, Ids(index.Region(20)));
            Assert.IsTrue(index.TryGetPlacement(1, out var placement, out ulong region));
            Assert.AreEqual(InterestPlacement.Region, placement);
            Assert.AreEqual(20UL, region);
        }

        [Test]
        public void ACarriersSubtreeRebucketsWithIt()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.Add(2, 10, "seat");
            index.Add(3, 10, "passenger");
            index.SetCarrier(2, 1);
            index.SetCarrier(3, 2);
            index.Move(1, 42);
            CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, Ids(index.Region(42)));
            Assert.AreEqual(0, index.Region(10).Count);
        }

        [Test]
        public void ACarriedItemCannotWanderOffOnItsOwn()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.Add(2, 10, "seat");
            index.SetCarrier(2, 1);
            Assert.IsFalse(index.Move(2, 99), "a passenger is bucketed with its ship, at any speed");
            CollectionAssert.AreEqual(new ulong[] { 1, 2 }, Ids(index.Region(10)));
            Assert.AreEqual(1UL, index.CarrierOf(2));
        }

        [Test]
        public void LinkingACarrierPullsTheChildIntoItsRegion()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.Add(2, 77, "crate");
            index.SetCarrier(2, 1);
            CollectionAssert.AreEqual(new ulong[] { 1, 2 }, Ids(index.Region(10)));
            index.SetCarrier(2, 0);
            Assert.AreEqual(0UL, index.CarrierOf(2));
            Assert.IsTrue(index.Move(2, 77), "detached, it buckets by its own position again");
        }

        [Test]
        public void RemovingACarrierOrphansItsChildrenWhereTheyStand()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.Add(2, 10, "seat");
            index.SetCarrier(2, 1);
            index.Remove(1);
            Assert.AreEqual(0UL, index.CarrierOf(2));
            CollectionAssert.AreEqual(new ulong[] { 2 }, Ids(index.Region(10)));
        }

        [Test]
        public void WideAndGlobalItemsLiveOutsideTheRegionBuckets()
        {
            var index = new InterestIndex<string>();
            index.AddWide(1, "dropship");
            index.AddGlobal(2, "match timer");
            index.Add(3, 10, "crate");
            Assert.AreEqual(1, index.RegionCount, "only the crate is bucketed by region");
            Assert.AreEqual(1, index.WideCount);
            Assert.AreEqual(1, index.GlobalCount);
            Assert.IsTrue(index.TryGetPlacement(1, out var wide, out _));
            Assert.AreEqual(InterestPlacement.Wide, wide);
            // A prefab whose radius drops back under the default returns to ordinary bucketing.
            index.Add(1, 10, "dropship");
            Assert.AreEqual(0, index.WideCount);
            CollectionAssert.AreEqual(new ulong[] { 1, 3 }, Ids(index.Region(10)));
        }

        [Test]
        public void ClearReleasesEverything()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "a");
            index.AddWide(2, "b");
            index.AddGlobal(3, "c");
            index.SetCarrier(1, 3);
            index.Clear();
            Assert.AreEqual(0, index.Count);
            Assert.AreEqual(0, index.RegionCount);
            Assert.AreEqual(0, index.WideCount);
            Assert.AreEqual(0, index.GlobalCount);
        }
    }
}
