using System;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The handoff bookkeeping a worker does while it moves a carrier and everything riding in it to another
    /// worker (design D85/D87), exercised on the <b>production</b> types themselves: <see cref="HandoverScope"/>
    /// holds the depth and the follower set, and <see cref="CarriedTransition.Resolve{T}"/> is where a
    /// follower's transition is suppressed. Nothing here is a stand-in — delete the suppression from the
    /// resolver, or the clear from the scope's exit, and these fail.
    /// <para>
    /// Pure C#, so the standalone gateway build runs them too.
    /// </para>
    /// </summary>
    public class HandoverScopeTests
    {
        /// <summary>A ship (1) in region 10 carrying a crate (2) and a seat (3), both always relevant on their own account.</summary>
        private static InterestIndex<string> Ship(ulong region = 10)
        {
            var index = new InterestIndex<string>();
            index.Add(1, region, "ship");
            index.AddGlobal(2, "crate");
            index.AddGlobal(3, "seat");
            index.SetCarrier(2, 1);
            index.SetCarrier(3, 1);
            return index;
        }

        /// <summary>One gateway on bit 0, subscribing nothing: it holds global entities and nothing else.</summary>
        private static RegionPublisher OneGateway()
        {
            var publisher = new RegionPublisher();
            publisher.AddGateway(0, new RegionSubscriptionReceiver());
            return publisher;
        }

        // ------------------------------------------------------------------------------------------- the scope

        [Test]
        public void TheOutermostFrameOwnsTheFollowerSet()
        {
            var scope = new HandoverScope();
            Assert.AreEqual(0, scope.Depth);
            using (var outer = scope.Begin())
            {
                Assert.IsTrue(outer.IsOutermost);
                scope.Add(7);
                using (var inner = scope.Begin())
                {
                    Assert.IsFalse(inner.IsOutermost, "a nested transfer does not collect again");
                    Assert.AreEqual(2, scope.Depth);
                    Assert.IsTrue(scope.Follows(7), "the set the outermost transfer collected is still in force");
                }
                Assert.AreEqual(1, scope.Depth);
                Assert.IsTrue(scope.Follows(7), "leaving a nested transfer does not end the handoff");
            }
            Assert.AreEqual(0, scope.Depth);
            Assert.AreEqual(0, scope.FollowerCount);
            Assert.IsFalse(scope.Follows(7));
        }

        [Test]
        public void AThrowingTransferLeavesNeitherDepthNorFollowersBehind()
        {
            var scope = new HandoverScope();
            Assert.Throws<InvalidOperationException>(() =>
            {
                using (var outer = scope.Begin())
                {
                    scope.Add(7);
                    using (scope.Begin())
                    {
                        throw new InvalidOperationException("a handover callback threw");
                    }
                }
            });
            Assert.AreEqual(0, scope.Depth, "a failed handoff must not poison the next one");
            Assert.AreEqual(0, scope.FollowerCount);
        }

        [Test]
        public void ALaterHandoffAfterAFailedOneCollectsItsOwnFollowers()
        {
            var index = Ship();
            var scope = new HandoverScope();
            try
            {
                using (scope.Begin())
                {
                    scope.Collect(index, 1);
                    throw new InvalidOperationException("persistence threw mid-handoff");
                }
            }
            catch (InvalidOperationException)
            {
                // The point of the test is what the worker is left holding, not the exception.
            }

            // An unrelated ship, carrying nothing that was aboard the first one.
            index.Add(4, 20, "tug");
            index.Add(5, 20, "barrel");
            index.SetCarrier(5, 4);
            using (var frame = scope.Begin())
            {
                Assert.IsTrue(frame.IsOutermost, "the failed handoff released the scope");
                Assert.AreEqual(1, scope.Collect(index, 4));
                Assert.IsTrue(scope.Follows(5));
                Assert.IsFalse(scope.Follows(2), "the first handoff's always-relevant crate is not aboard this tug");
                Assert.IsFalse(scope.Follows(3));
            }
        }

        [Test]
        public void CollectTakesTheWholeSubtreeAndSaysHowMuchIsNew()
        {
            var index = Ship();
            var scope = new HandoverScope();
            using (scope.Begin())
            {
                Assert.AreEqual(2, scope.Collect(index, 1));
                Assert.AreEqual(0, scope.Collect(index, 1), "collecting twice records nothing new");
                Assert.AreEqual(0, scope.Collect(index, 2), "a crate carries nothing");
                Assert.IsFalse(scope.Follows(1), "the carrier is not its own follower; it is redirected in its own right");
            }
        }

        [Test]
        public void ClearForgetsEverything()
        {
            var scope = new HandoverScope();
            scope.Begin();
            scope.Add(7);
            scope.Clear();
            Assert.AreEqual(0, scope.Depth);
            Assert.IsFalse(scope.Follows(7));
        }

        // ------------------------------------------------------------------------------------- the suppression

        [Test]
        public void RemovingACarrierPublishesTheOrphanItReallyLeavesBehind()
        {
            // No handoff: the ship is destroyed, so the always-relevant crate is global on its own account again
            // and the one linked gateway has to be sent it.
            var index = Ship();
            var publisher = OneGateway();
            var carried = new CarriedTransition();
            carried.Capture(index, 1, publisher);
            index.Remove(1);
            carried.Resolve(index, publisher);
            Assert.AreEqual(RegionPublisher.Bit(0), SlotOf(carried, 2).After, "the crate is announced to the gateway");
            Assert.AreEqual(0UL, SlotOf(carried, 2).Before);
        }

        [Test]
        public void APassengerFollowingItsCarrierPublishesNothingAtAll()
        {
            // The same removal, but the ship is being handed to another worker and the crate is going with it.
            // Announcing the crate here would give the gateway an entity that then gets neither a redirect nor a
            // forget, and it would cache it for ever.
            var index = Ship();
            var publisher = OneGateway();
            var carried = new CarriedTransition();
            var scope = new HandoverScope();
            using (scope.Begin())
            {
                scope.Collect(index, 1);
                carried.Capture(index, 1, publisher);
                index.Remove(1);
                carried.Resolve(index, publisher, null, scope);
                var crate = SlotOf(carried, 2);
                Assert.AreEqual(crate.Before, crate.After, "nothing is published for a passenger that is leaving too");
                Assert.AreEqual(InterestPlacement.Global, crate.To, "the placement is still resolved, so a stale wide mask can be dropped");
            }
        }

        [Test]
        public void APinnedInteriorThatStaysBehindIsStillPublished()
        {
            // A worker collects only what really leaves, so an interior pinned to a worker of its own is not in
            // the set and is orphaned exactly as a despawned carrier's survivors are.
            var index = Ship();
            var publisher = OneGateway();
            var carried = new CarriedTransition();
            var scope = new HandoverScope();
            using (scope.Begin())
            {
                scope.Add(3); // the seat follows; the crate is pinned and stays
                carried.Capture(index, 1, publisher);
                index.Remove(1);
                carried.Resolve(index, publisher, null, scope);
                Assert.AreEqual(RegionPublisher.Bit(0), SlotOf(carried, 2).After, "the crate stayed, so it is announced");
                var seat = SlotOf(carried, 3);
                Assert.AreEqual(seat.Before, seat.After);
            }
        }

        [Test]
        public void SuppressionEndsWithTheHandoff()
        {
            var index = Ship();
            var publisher = OneGateway();
            var scope = new HandoverScope();
            using (scope.Begin()) scope.Collect(index, 1);
            var carried = new CarriedTransition();
            carried.Capture(index, 1, publisher);
            index.Remove(1);
            carried.Resolve(index, publisher, null, scope);
            Assert.AreEqual(RegionPublisher.Bit(0), SlotOf(carried, 2).After, "the handoff is over; this is an ordinary orphaning");
        }

        private static CarriedTransition.Slot SlotOf(CarriedTransition carried, ulong id)
        {
            for (int i = 0; i < carried.Count; i++) if (carried[i].Id == id) return carried[i];
            Assert.Fail($"no slot was captured for #{id}");
            return default;
        }
    }
}
