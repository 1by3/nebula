using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The subtree half of design D3: enumerating what a carrier carries, and working out which gateways gain or
    /// lose each of them when the carrier changes bucket. The index already moves a ship and its passengers as
    /// one unit; this is what makes the <i>publication</i> move with them, so a gateway subscribing only the
    /// destination is not sent a ship full of invisible passengers and one subscribing only the origin does not
    /// keep them for ever.
    /// </summary>
    public class CarriedTransitionTests
    {
        /// <summary>A ship (1) carrying a seat (2) and a crate (4), with a passenger (3) in the seat.</summary>
        private static InterestIndex<string> Ship(ulong region = 10)
        {
            var index = new InterestIndex<string>();
            index.Add(1, region, "ship");
            index.Add(2, region, "seat");
            index.Add(3, region, "passenger");
            index.Add(4, region, "crate");
            index.SetCarrier(2, 1);
            index.SetCarrier(3, 2);
            index.SetCarrier(4, 1);
            return index;
        }

        // ------------------------------------------------------------------------------------------- the walk

        [Test]
        public void CollectCarriedListsTheWholeSubtreeCarriersBeforeTheirContents()
        {
            var index = Ship();
            var ids = new List<ulong>();
            Assert.AreEqual(3, index.CollectCarried(1, ids));
            CollectionAssert.AreEquivalent(new ulong[] { 2, 3, 4 }, ids);
            Assert.Less(ids.IndexOf(2), ids.IndexOf(3), "a seat is listed before the passenger sitting in it");
        }

        [Test]
        public void CollectCarriedAppendsNothingForSomethingCarryingNothing()
        {
            var index = Ship();
            var ids = new List<ulong> { 99 };
            Assert.AreEqual(0, index.CollectCarried(3, ids), "a passenger carries nothing");
            Assert.AreEqual(0, index.CollectCarried(1234, ids), "and an unknown id is not an error");
            CollectionAssert.AreEqual(new ulong[] { 99 }, ids, "the caller's list is left alone");
            Assert.IsFalse(index.HasCarried(3));
            Assert.IsTrue(index.HasCarried(1));
        }

        [Test]
        public void ACarrierCycleIsRefusedWhenItIsAskedFor()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "a");
            index.Add(2, 10, "b");
            index.SetCarrier(2, 1);
            Assert.AreEqual(CarrierLink.Cycle, index.SetCarrier(1, 2),
                "a cycle is refused where it is created, not walked around later");
            Assert.AreEqual(0UL, index.CarrierOf(1), "and the refused link changes nothing");
            var ids = new List<ulong>();
            Assert.AreEqual(1, index.CollectCarried(1, ids));
            CollectionAssert.AreEqual(new ulong[] { 2 }, ids);
        }

        // ------------------------------------------------------------------------------------------- transitions

        private static RegionPublisher Publisher(params (int bit, ulong region)[] subscriptions)
        {
            var publisher = new RegionPublisher();
            // Registering the link as well as the region is what makes LinkedMask - the set a global entity
            // reaches - mean anything here, and every one of these bits is a gateway that really is linked.
            foreach (var (bit, region) in subscriptions) { publisher.AddGateway(bit, null); publisher.Subscribe(bit, region); }
            return publisher;
        }

        private static CarriedTransition.Slot SlotOf(CarriedTransition transition, ulong id)
        {
            for (int i = 0; i < transition.Count; i++) if (transition[i].Id == id) return transition[i];
            Assert.Fail($"#{id} is not in the captured subtree");
            return default;
        }

        [Test]
        public void EveryPassengerGetsTheCarriersRegionTransition()
        {
            var index = Ship();
            var publisher = Publisher((0, 10), (1, 20));
            var transition = new CarriedTransition();
            transition.Capture(index, 1, publisher);
            index.Move(1, 20);
            transition.Resolve(index, publisher);

            Assert.AreEqual(4, transition.Count, "the ship and everything riding in it");
            Assert.AreEqual(1UL, transition[0].Id, "the carrier is first, so a spawn walk sends it first");
            var order = new List<ulong>();
            for (int i = 0; i < transition.Count; i++) order.Add(transition[i].Id);
            Assert.Less(order.IndexOf(2), order.IndexOf(3), "and the seat before the passenger sitting in it");
            for (int i = 0; i < transition.Count; i++)
            {
                Assert.AreEqual(1UL << 0, transition[i].Before, $"#{transition[i].Id} was only in the origin gateway's set");
                Assert.AreEqual(1UL << 1, transition[i].After, $"#{transition[i].Id} is now only in the destination gateway's set");
                Assert.AreEqual(20UL, transition[i].ToRegion);
            }
        }

        [Test]
        public void ACarrierWithNothingAboardIsStillOneSlot()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "crate");
            var publisher = Publisher((0, 20));
            var transition = new CarriedTransition();
            transition.Capture(index, 1, publisher);
            index.Move(1, 20);
            transition.Resolve(index, publisher);
            Assert.AreEqual(1, transition.Count);
            Assert.AreEqual(0UL, transition[0].Before);
            Assert.AreEqual(1UL, transition[0].After);
        }

        [Test]
        public void NothingIsPublishedWhenTheSameGatewaysSubscribeBothRegions()
        {
            var index = Ship();
            var publisher = Publisher((0, 10), (0, 20));
            var transition = new CarriedTransition();
            transition.Capture(index, 1, publisher);
            index.Move(1, 20);
            transition.Resolve(index, publisher);
            for (int i = 0; i < transition.Count; i++)
                Assert.AreEqual(transition[i].Before, transition[i].After, $"#{transition[i].Id} changes no gateway's view");
        }

        [Test]
        public void AWideOrGlobalPassengerRidesWithTheShipLikeAnyOther()
        {
            var index = Ship();
            index.AddWide(3, "searchlight"); // its own reach is irrelevant while it is aboard (design D70)
            index.AddGlobal(4, "beacon");
            Assert.AreEqual(0, index.WideCount, "a passenger is published as its carrier is, not as its prefab asks");
            Assert.AreEqual(0, index.GlobalCount);
            var publisher = Publisher((0, 10), (1, 20));
            var transition = new CarriedTransition();
            transition.Capture(index, 1, publisher);
            index.Move(1, 20);
            transition.Resolve(index, publisher);

            foreach (ulong id in new ulong[] { 2, 3, 4 })
            {
                var slot = SlotOf(transition, id);
                Assert.AreEqual(1UL << 0, slot.Before, $"#{id} was heard about where the ship was");
                Assert.AreEqual(1UL << 1, slot.After, $"#{id} is heard about where the ship is now");
            }
        }

        [Test]
        public void BoardingAnOrdinaryShipTakesAnAlwaysRelevantCrateOutOfEveryOtherGatewaysSet()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.AddGlobal(2, "beacon crate");
            var publisher = Publisher((0, 10), (1, 20));
            Assert.AreEqual(1, index.GlobalCount, "on its own it is in every set");

            var transition = new CarriedTransition();
            transition.Capture(index, 2, publisher);
            index.SetCarrier(2, 1);
            transition.Resolve(index, publisher);

            Assert.AreEqual(0, index.GlobalCount, "aboard, it is bucketed with the ship");
            var slot = SlotOf(transition, 2);
            Assert.AreEqual(publisher.LinkedMask, slot.Before, "every linked gateway held it");
            Assert.AreEqual(1UL << 0, slot.After, "and only the ship's own subscriber keeps it");
            Assert.AreNotEqual(0UL, slot.Before & ~slot.After, "so the far gateway is told to forget it");
        }

        [Test]
        public void GettingOffGivesAnAlwaysRelevantCrateItsOwnReachBack()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.AddGlobal(2, "beacon crate");
            index.SetCarrier(2, 1);
            var publisher = Publisher((0, 10), (1, 20));

            var transition = new CarriedTransition();
            transition.Capture(index, 2, publisher);
            index.SetCarrier(2, 0);
            transition.Resolve(index, publisher);

            Assert.AreEqual(1, index.GlobalCount, "off the ship it is always relevant again");
            var slot = SlotOf(transition, 2);
            Assert.AreEqual(1UL << 0, slot.Before);
            Assert.AreEqual(publisher.LinkedMask, slot.After, "so every linked gateway is sent it");
        }

        [Test]
        public void BoardingMovesThePassengerFromItsOwnRegionToTheCarriers()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.Add(2, 77, "crate");
            index.Add(3, 77, "cat"); // riding in the crate: a nested carrier boards too
            index.SetCarrier(3, 2);
            var publisher = Publisher((0, 77), (1, 10));
            var transition = new CarriedTransition();
            transition.Capture(index, 2, publisher);
            index.SetCarrier(2, 1);
            transition.Resolve(index, publisher);

            Assert.AreEqual(2, transition.Count);
            Assert.AreEqual(2UL, transition[0].Id, "the crate is captured before what it carries");
            foreach (ulong id in new ulong[] { 2, 3 })
            {
                var slot = SlotOf(transition, id);
                Assert.AreEqual(1UL << 0, slot.Before, $"#{id} was heard about where the crate stood");
                Assert.AreEqual(1UL << 1, slot.After, $"#{id} is heard about where the ship is");
            }
        }

        [Test]
        public void DisembarkingPublishesTheStepBackToItsOwnRegion()
        {
            var index = Ship();
            var publisher = Publisher((0, 10), (1, 55));
            var transition = new CarriedTransition();
            transition.Capture(index, 2, publisher); // the seat, with the passenger in it, leaves the ship
            index.SetCarrier(2, 0);
            index.Move(2, 55);
            transition.Resolve(index, publisher);

            Assert.AreEqual(2, transition.Count);
            foreach (ulong id in new ulong[] { 2, 3 })
            {
                var slot = SlotOf(transition, id);
                Assert.AreEqual(1UL << 0, slot.Before);
                Assert.AreEqual(1UL << 1, slot.After, $"#{id} left the ship's region with the seat");
            }
            Assert.IsTrue(index.TryGetPlacement(1, out _, out ulong shipRegion));
            Assert.AreEqual(10UL, shipRegion, "and the ship stays where it was");
        }

        [Test]
        public void CaptureForgetsThePreviousSubtree()
        {
            var index = Ship();
            var publisher = Publisher((0, 10));
            var transition = new CarriedTransition();
            transition.Capture(index, 1, publisher);
            transition.Capture(index, 3, publisher);
            Assert.AreEqual(1, transition.Count, "a second capture replaces the first");
            transition.Clear();
            Assert.AreEqual(0, transition.Count);
        }

        // ------------------------------------------------------------------- placement follows the root (D70)

        [Test]
        public void ANestedPassengerTakesTheOutermostCarriersPlacement()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.Add(2, 77, "crate");
            index.AddGlobal(3, "beacon cat"); // always relevant, three levels down
            index.SetCarrier(3, 2);
            index.SetCarrier(2, 1);

            Assert.IsTrue(index.TryGetPlacement(3, out var placement, out ulong region));
            Assert.AreEqual(InterestPlacement.Region, placement, "the outermost carrier decides, not the crate it sits in");
            Assert.AreEqual(10UL, region);
            Assert.AreEqual(1UL, index.RootOf(3));
            Assert.IsTrue(index.TryGetOwnPlacement(3, out var own, out _));
            Assert.AreEqual(InterestPlacement.Global, own, "and what it asked for itself is remembered for when it gets off");

            index.Move(1, 20);
            Assert.AreEqual(3, index.Region(20).Count, "the whole chain moves as one unit");
        }

        [Test]
        public void APassengerIndexedBeforeItsCarrierIsPlacedWhenTheCarrierArrives()
        {
            var index = new InterestIndex<string>();
            index.AddGlobal(2, "beacon crate");
            Assert.AreEqual(CarrierLink.Linked, index.SetCarrier(2, 1), "the link is remembered even with no carrier");
            Assert.AreEqual(1, index.GlobalCount, "and until the ship exists the crate is what it says it is");

            index.Add(1, 10, "ship");
            Assert.AreEqual(0, index.GlobalCount, "the ship's arrival places what was already riding in it");
            CollectionAssert.AreEquivalent(new ulong[] { 1, 2 }, Ids(index.Region(10)));
        }

        [Test]
        public void AnOrphanedPassengerGetsItsOwnReachBack()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.AddGlobal(2, "beacon crate");
            index.Add(3, 10, "ordinary crate");
            index.SetCarrier(2, 1);
            index.SetCarrier(3, 1);
            Assert.AreEqual(0, index.GlobalCount);

            index.Remove(1); // the ship is despawned out from under them

            Assert.AreEqual(1, index.GlobalCount, "an always-relevant crate is always relevant again");
            CollectionAssert.AreEqual(new ulong[] { 3 }, Ids(index.Region(10)),
                "and an ordinary one stays where the ship left it until its own position says otherwise");
            Assert.AreEqual(0UL, index.CarrierOf(2));
        }

        // ------------------------------------------------------------------- the whole subtree, and only trees

        private static List<ulong> Ids(InterestIndex<string>.Bucket bucket)
        {
            var ids = new List<ulong>();
            foreach (var entry in bucket) ids.Add(entry.Id);
            ids.Sort();
            return ids;
        }

        /// <summary>
        /// Well past the 4,096-node walk cap that used to be here: a cap silently rebucketed part of a ship and
        /// left the rest addressed to the gateways it had sailed away from.
        /// </summary>
        [Test]
        public void AWideSubtreeIsWalkedAndRebucketedWhole()
        {
            const int children = 5000, grandchildren = 500;
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            for (ulong id = 2; id < 2 + children; id++) { index.Add(id, 99, "crate"); index.SetCarrier(id, 1); }
            for (ulong id = 0; id < grandchildren; id++)
            {
                ulong cat = 100000 + id;
                index.Add(cat, 99, "cat");
                index.SetCarrier(cat, 2 + id); // one in every tenth crate, all far past the old cap
            }
            int total = 1 + children + grandchildren;

            var ids = new List<ulong>();
            Assert.AreEqual(total - 1, index.CollectCarried(1, ids), "every crate and every cat, to any width");
            Assert.AreEqual(total, index.Region(10).Count, "and all of them are bucketed with the ship");

            index.Move(1, 20);
            Assert.AreEqual(total, index.Region(20).Count, "the whole subtree moves, not the first 4,096 of it");
            Assert.AreEqual(0, index.Region(10).Count);
        }

        [Test]
        public void ADeepChainIsWalkedWhole()
        {
            const int depth = 5000;
            var index = new InterestIndex<string>();
            index.Add(1, 10, "root");
            for (ulong id = 2; id <= depth; id++) { index.Add(id, 99, "link"); index.SetCarrier(id, id - 1); }

            var ids = new List<ulong>();
            Assert.AreEqual(depth - 1, index.CollectCarried(1, ids));
            Assert.AreEqual(depth, index.Region(10).Count, "a chain is a subtree like any other");
            Assert.AreEqual(1UL, index.RootOf(depth), "and the outermost carrier is the root of all of it");

            index.Move(1, 20);
            Assert.AreEqual(depth, index.Region(20).Count);
        }

        [Test]
        public void AnItemCannotBeCarriedByItself()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "crate");
            Assert.AreEqual(CarrierLink.Cycle, index.SetCarrier(1, 1));
            Assert.AreEqual(0UL, index.CarrierOf(1));
            Assert.IsFalse(index.HasCarried(1));
            Assert.AreEqual(1, index.Region(10).Count);
        }

        [Test]
        public void ALongCycleIsRefusedAndTheValidChainSurvives()
        {
            const int depth = 200;
            var index = new InterestIndex<string>();
            index.Add(1, 10, "root");
            for (ulong id = 2; id <= depth; id++) { index.Add(id, 99, "link"); index.SetCarrier(id, id - 1); }

            Assert.AreEqual(CarrierLink.Cycle, index.SetCarrier(1, depth), "the root cannot ride in its own descendant");
            Assert.AreEqual(0UL, index.CarrierOf(1), "and the refusal leaves the chain exactly as it was");
            Assert.AreEqual(depth, index.Region(10).Count);
            var ids = new List<ulong>();
            Assert.AreEqual(depth - 1, index.CollectCarried(1, ids), "so the walk still terminates on the whole tree");

            index.Move(1, 20);
            Assert.AreEqual(depth, index.Region(20).Count, "and the tree still moves as one unit");
        }

        // ------------------------------------------------------------------- depth is bounded by the tree, not a constant

        [Test]
        public void DepthOfCountsEveryCarrierInTheChain()
        {
            const int depth = 40; // past every cap the publication path used to carry (8 and 16)
            var index = new InterestIndex<string>();
            index.Add(1, 10, "root");
            for (ulong id = 2; id <= depth; id++) { index.Add(id, 99, "link"); index.SetCarrier(id, id - 1); }

            Assert.AreEqual(0, index.DepthOf(1), "something standing in the world is depth 0");
            Assert.AreEqual(1, index.DepthOf(2));
            Assert.AreEqual(depth - 1, index.DepthOf(depth), "and the bottom of the chain is as deep as it really is");
            Assert.AreEqual(0, index.DepthOf(4040), "an unknown id is not an error");
        }

        [Test]
        public void DepthOfStopsAtACarrierThatIsNotIndexedYet()
        {
            var index = new InterestIndex<string>();
            index.Add(2, 10, "crate");
            index.SetCarrier(2, 1); // the ship has not arrived
            Assert.AreEqual(0, index.DepthOf(2), "as far up as the index can see is where the chain ends");
            index.Add(1, 10, "ship");
            Assert.AreEqual(1, index.DepthOf(2));
        }

        // ------------------------------------------------------------------- the carrier arrives after its passengers

        [Test]
        public void CapturingAMissingCarrierStillRecordsThePassengersWaitingForIt()
        {
            var index = new InterestIndex<string>();
            index.AddGlobal(2, "beacon crate");
            index.Add(3, 77, "cat");
            index.SetCarrier(2, 1); // both name a ship that does not exist yet
            index.SetCarrier(3, 2);
            var publisher = Publisher((0, 10), (1, 20));

            var transition = new CarriedTransition();
            transition.Capture(index, 1, publisher);
            Assert.AreEqual(2, transition.Count, "the carrier has no slot, its pending subtree has one each");
            Assert.AreEqual(2UL, transition[0].Id, "carrier before its contents, as always");

            index.Add(1, 10, "ship");
            transition.Resolve(index, publisher);

            var crate = SlotOf(transition, 2);
            Assert.AreEqual(publisher.LinkedMask, crate.Before, "every linked gateway held the always-relevant crate");
            Assert.AreEqual(1UL << 0, crate.After, "and only the ship's own subscriber keeps it");
            Assert.AreNotEqual(0UL, crate.Before & ~crate.After, "so the far gateway is told to forget it");
            var cat = SlotOf(transition, 3);
            Assert.AreEqual(publisher.LinkedMask, cat.Before,
                "it rides in the crate, and with no ship to seat them the crate's own always-relevance was theirs");
            Assert.AreEqual(1UL << 0, cat.After, "and it arrives with the crate it is riding in");
        }

        // ------------------------------------------------------------------- the carrier is removed

        [Test]
        public void RemovingACarrierPublishesWhatItsPassengersGetBack()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.AddGlobal(2, "beacon crate");
            index.Add(3, 10, "ordinary crate");
            index.SetCarrier(2, 1);
            index.SetCarrier(3, 1);
            var publisher = Publisher((0, 10), (1, 20));

            var transition = new CarriedTransition();
            transition.Capture(index, 1, publisher);
            index.Remove(1);
            transition.Resolve(index, publisher);

            var ship = SlotOf(transition, 1);
            Assert.AreEqual(ship.Before, ship.After,
                "the removed carrier publishes nothing here: its own despawn says it better than a forget");
            var beacon = SlotOf(transition, 2);
            Assert.AreEqual(1UL << 0, beacon.Before, "aboard, only the ship's gateway heard about it");
            Assert.AreEqual(publisher.LinkedMask, beacon.After, "orphaned, it is always relevant on its own account again");
            var ordinary = SlotOf(transition, 3);
            Assert.AreEqual(ordinary.Before, ordinary.After, "and one with no reach of its own stays where it was left");
        }

        /// <summary>
        /// A region passenger is put down where the ship left it, so it keeps that gateway. A <b>wide</b> one is
        /// matched on its own reach again the moment the ship is gone, and that reach need not cover where the
        /// ship was: the gateway that only ever heard about it through the ship has to be told to forget it.
        /// </summary>
        [Test]
        public void RemovingACarrierTakesAWidePassengerOutOfTheGatewayThatOnlyKnewItThroughIt()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            index.AddWide(2, "searchlight");
            index.Add(3, 20, "cat"); // riding in the searchlight, so it follows the searchlight
            index.SetCarrier(2, 1);
            index.SetCarrier(3, 2);
            var publisher = Publisher((0, 10), (1, 20));
            // Its own reach covers gateway 1's part of the world and not gateway 0's.
            ulong WideMask(ulong id) => 1UL << 1;
            foreach (ulong id in new ulong[] { 2, 3 })
                Assert.IsTrue(index.TryGetPlacement(id, out _, out ulong region) && region == 10);

            var transition = new CarriedTransition();
            transition.Capture(index, 1, publisher, WideMask);
            index.Remove(1);
            transition.Resolve(index, publisher, WideMask);

            Assert.AreEqual(2, index.WideCount,
                "orphaned, the searchlight is wide on its own account again - and the cat rides with it");
            foreach (ulong id in new ulong[] { 2, 3 })
            {
                var slot = SlotOf(transition, id);
                Assert.AreEqual(1UL << 0, slot.Before, $"#{id} was heard about where the ship was");
                Assert.AreEqual(1UL << 1, slot.After, $"#{id} is heard about wherever its own reach goes now");
                Assert.AreNotEqual(0UL, slot.Before & ~slot.After, $"so #{id} is forgotten by the ship's gateway");
            }
            var order = new List<ulong>();
            for (int i = 0; i < transition.Count; i++) order.Add(transition[i].Id);
            Assert.Less(order.IndexOf(2), order.IndexOf(3),
                "carrier before its contents, so a backwards walk forgets the cat before the searchlight it sat in");
        }

        [Test]
        public void LinkingSomethingThatIsNotIndexedSaysSoRatherThanGuessing()
        {
            var index = new InterestIndex<string>();
            index.Add(1, 10, "ship");
            Assert.AreEqual(CarrierLink.UnknownItem, index.SetCarrier(404, 1));
            Assert.IsFalse(index.HasCarried(1), "nothing is remembered for an item the index has never seen");
            Assert.AreEqual(CarrierLink.Unchanged, index.SetCarrier(1, 0), "and detaching what is not carried is a no-op");
        }
    }
}
