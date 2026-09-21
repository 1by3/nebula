using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// Worker-side publishing (design §6): every region knows which gateways want it, regions that share a
    /// subscriber mask are grouped so one serialization serves them all, and a rebucket tells the worker exactly
    /// which links need a spawn and which need to forget.
    /// </summary>
    public class RegionPublisherTests
    {
        private static RegionSubscriptionReceiver Subscription(params ulong[] regions)
        {
            var receiver = new RegionSubscriptionReceiver();
            var msg = new InterestSubscribeMsg
            {
                Seq = 1, Grid = InterestGrid.Resolve(InterestSettings.Default),
                Flags = InterestSubscribeFlags.Full | InterestSubscribeFlags.Commit,
                Add = new List<ulong>(regions), Remove = new List<ulong>(), FociRegions = new List<ulong>(), Entities = new List<ulong>(),
                SetCount = (uint)regions.Length, SetHash = RegionSubscription.Hash(regions),
            };
            Assert.IsTrue(receiver.Apply(msg, out string rejection), rejection);
            return receiver;
        }

        private static void Update(RegionSubscriptionReceiver receiver, ulong[] regions)
        {
            var msg = new InterestSubscribeMsg
            {
                Seq = receiver.Seq + 1, BaseSeq = receiver.Seq, Grid = receiver.Grid,
                Flags = InterestSubscribeFlags.Full | InterestSubscribeFlags.Commit,
                Add = new List<ulong>(regions), Remove = new List<ulong>(), FociRegions = new List<ulong>(), Entities = new List<ulong>(),
                SetCount = (uint)regions.Length, SetHash = RegionSubscription.Hash(regions),
            };
            Assert.IsTrue(receiver.Apply(msg, out string rejection), rejection);
        }

        [Test]
        public void MasksCollectEveryGatewayThatWantsARegion()
        {
            var publisher = new RegionPublisher();
            publisher.AddGateway(0, Subscription(10, 11));
            publisher.AddGateway(3, Subscription(11, 12));
            Assert.AreEqual(1UL, publisher.MaskOf(10));
            Assert.AreEqual(1UL | (1UL << 3), publisher.MaskOf(11));
            Assert.AreEqual(1UL << 3, publisher.MaskOf(12));
            Assert.AreEqual(0UL, publisher.MaskOf(99));
            Assert.AreEqual(3, publisher.SubscribedRegions);
        }

        [Test]
        public void ASubscriptionChangeMovesOnlyTheRegionsItNamed()
        {
            var publisher = new RegionPublisher();
            var receiver = Subscription(10, 11);
            publisher.AddGateway(0, receiver);
            Update(receiver, new ulong[] { 11, 12 });
            publisher.ApplyChanges(0, receiver);
            Assert.AreEqual(0UL, publisher.MaskOf(10));
            Assert.AreEqual(1UL, publisher.MaskOf(11));
            Assert.AreEqual(1UL, publisher.MaskOf(12));
        }

        [Test]
        public void DroppingAGatewayClearsItsBitEverywhere()
        {
            var publisher = new RegionPublisher();
            var a = Subscription(10, 11);
            publisher.AddGateway(0, a);
            publisher.AddGateway(1, Subscription(11));
            publisher.RemoveGateway(0);
            Assert.AreEqual(0UL, publisher.MaskOf(10), "nobody wants it now, so the region is forgotten entirely");
            Assert.AreEqual(2UL, publisher.MaskOf(11));
            Assert.AreEqual(2UL, publisher.LinkedMask);
        }

        [Test]
        public void RegionsAreGroupedByMaskAndCounted()
        {
            var index = new InterestIndex<int>();
            index.Add(1, 10, 1);
            index.Add(2, 11, 2);
            index.Add(3, 11, 3);
            index.Add(4, 12, 4);
            index.Add(5, 99, 5);        // nobody subscribes region 99
            var publisher = new RegionPublisher();
            publisher.AddGateway(0, Subscription(10, 11));
            publisher.AddGateway(1, Subscription(11, 12));
            publisher.BuildGroups(index);

            Assert.AreEqual(3, publisher.Groups.Count);
            int entitiesInShared = 0, entitiesTotal = 0;
            foreach (var group in publisher.Groups)
            {
                entitiesTotal += group.EntityCount;
                if (group.Mask == 0b11) { entitiesInShared = group.EntityCount; CollectionAssert.AreEqual(new ulong[] { 11 }, group.Regions); }
            }
            Assert.AreEqual(2, entitiesInShared, "both entities of region 11 are written once for both gateways");
            Assert.AreEqual(4, entitiesTotal);
            Assert.AreEqual(4, publisher.FilteredEntities);
            Assert.AreEqual(5, publisher.TotalEntities);
        }

        [Test]
        public void RebuildingReusesItsListsAndForgetsTheLastTick()
        {
            var index = new InterestIndex<int>();
            index.Add(1, 10, 1);
            var publisher = new RegionPublisher();
            publisher.AddGateway(0, Subscription(10));
            publisher.BuildGroups(index);
            Assert.AreEqual(1, publisher.Groups.Count);
            index.Remove(1);
            publisher.BuildGroups(index);
            Assert.AreEqual(0, publisher.Groups.Count);
            Assert.AreEqual(0, publisher.TotalEntities);
        }

        [Test]
        public void ARebucketNamesTheLinksThatNeedASpawnAndTheOnesThatMustForget()
        {
            var publisher = new RegionPublisher();
            publisher.AddGateway(0, Subscription(10));          // only the old region
            publisher.AddGateway(1, Subscription(10, 20));      // both
            publisher.AddGateway(2, Subscription(20));          // only the new one
            Assert.AreEqual(1UL << 2, publisher.SpawnBits(10, 20), "only the gateway that did not have it hears a spawn");
            Assert.AreEqual(1UL << 0, publisher.ForgetBits(10, 20), "only the gateway that loses it is told to forget");
            Assert.AreEqual(0UL, publisher.SpawnBits(10, 10));
            Assert.AreEqual(0UL, publisher.ForgetBits(10, 10));
        }

        [Test]
        public void AWideEntityIsMatchedAgainstEachGatewaysFoci()
        {
            var grid = InterestGrid.Resolve(InterestSettings.Default);   // 64 m regions, planar
            var near = Subscription();
            var far = Subscription();
            Foci(near, grid.RegionOf(0, 0, 0));
            Foci(far, grid.RegionOf(5000, 0, 0));
            var publisher = new RegionPublisher();
            publisher.AddGateway(0, near);
            publisher.AddGateway(1, far);
            Assert.AreEqual(1UL, publisher.WideMask(grid, 300, 0, 0, 400));
            Assert.AreEqual(0UL, publisher.WideMask(grid, 300, 0, 0, 100));
            Assert.AreEqual(0b11UL, publisher.WideMask(grid, 2500, 0, 0, 2600));
        }

        [Test]
        public void ExplicitSubscriptionsAreMatchedById()
        {
            var receiver = Subscription(10);
            var msg = new InterestSubscribeMsg
            {
                Seq = receiver.Seq + 1, BaseSeq = receiver.Seq, Grid = receiver.Grid, Flags = InterestSubscribeFlags.Commit,
                Add = new List<ulong>(), Remove = new List<ulong>(), FociRegions = new List<ulong>(), Entities = new List<ulong> { 777 },
                SetCount = 1, SetHash = RegionSubscription.Hash(new ulong[] { 10 }),
            };
            Assert.IsTrue(receiver.Apply(msg, out _));
            var publisher = new RegionPublisher();
            publisher.AddGateway(2, receiver);
            Assert.AreEqual(1UL << 2, publisher.ExplicitMask(777));
            Assert.AreEqual(0UL, publisher.ExplicitMask(778));
        }

        private static void Foci(RegionSubscriptionReceiver receiver, params ulong[] regions)
        {
            var msg = new InterestSubscribeMsg
            {
                Seq = receiver.Seq + 1, BaseSeq = receiver.Seq, Grid = receiver.Grid, Flags = InterestSubscribeFlags.Commit,
                Add = new List<ulong>(), Remove = new List<ulong>(), FociRegions = new List<ulong>(regions), Entities = new List<ulong>(),
                SetCount = (uint)receiver.Count, SetHash = receiver.Hash,
            };
            Assert.IsTrue(receiver.Apply(msg, out string rejection), rejection);
        }
    }
}
