using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The subscription protocol (design §5). These tests are the reason it is safe to send deltas at all: every
    /// way the two ends could drift apart — a lost base, a reordered chunk, a repeated message, a bug in the
    /// diff — must end in the worker keeping what it had and asking for a full snapshot, never in a set that
    /// looks fine and quietly hides entities.
    /// </summary>
    public class InterestSubscriptionTests
    {
        private double _now;

        private RegionSubscriptionSender Sender(double linger = 0)
        {
            _now = 0;
            return new RegionSubscriptionSender(() => _now)
            {
                Grid = InterestGrid.Resolve(InterestSettings.Default),
                LingerSeconds = linger,
                ResyncSeconds = 0,
            };
        }

        private static List<InterestSubscribeMsg> Build(RegionSubscriptionSender sender, double now)
        {
            var messages = new List<InterestSubscribeMsg>();
            sender.Build(messages, now);
            return messages;
        }

        private static bool ApplyAll(RegionSubscriptionReceiver receiver, List<InterestSubscribeMsg> messages, out string rejection)
        {
            rejection = null;
            foreach (var msg in messages) if (!receiver.Apply(msg, out rejection)) return false;
            return true;
        }

        /// <summary>A message survives the wire unchanged; the protocol is only worth testing on what is actually sent.</summary>
        private static InterestSubscribeMsg RoundTrip(in InterestSubscribeMsg msg)
        {
            var w = new NetworkWriter(1024);
            msg.Write(w);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            Assert.AreEqual((byte)MsgId.InterestSubscribe, r.ReadByte());
            return InterestSubscribeMsg.Read(r);
        }

        [Test]
        public void TheFirstBuildIsAFullSnapshotAndTheNextIsADelta()
        {
            var sender = Sender();
            sender.Add(10); sender.Add(11);
            var first = Build(sender, 0);
            Assert.AreEqual(1, first.Count);
            Assert.IsTrue(first[0].IsFull);
            Assert.IsTrue(first[0].IsCommit);
            Assert.AreEqual(2, first[0].Add.Count);

            sender.Add(12);
            sender.Drop(10);
            var second = Build(sender, 1);
            Assert.AreEqual(1, second.Count);
            Assert.IsFalse(second[0].IsFull);
            Assert.AreEqual(first[0].Seq, second[0].BaseSeq);
            CollectionAssert.AreEqual(new ulong[] { 12 }, second[0].Add);
            CollectionAssert.AreEqual(new ulong[] { 10 }, second[0].Remove);
        }

        [Test]
        public void NothingIsSentWhenNothingChanged()
        {
            var sender = Sender();
            sender.Add(10);
            Assert.IsTrue(sender.Build(new List<InterestSubscribeMsg>(), 0));
            Assert.IsFalse(sender.Build(new List<InterestSubscribeMsg>(), 1));
        }

        [Test]
        public void AReceiverAppliesAFullSnapshotAndThenDeltas()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver { Grid = sender.Grid };
            sender.Add(10); sender.Add(11);
            Assert.IsTrue(ApplyAll(receiver, Build(sender, 0), out _));
            Assert.AreEqual(2, receiver.Count);
            CollectionAssert.AreEquivalent(new ulong[] { 10, 11 }, new List<ulong>(receiver.Added));

            sender.Add(12); sender.Drop(11);
            Assert.IsTrue(ApplyAll(receiver, Build(sender, 1), out _));
            Assert.IsTrue(receiver.Contains(12));
            Assert.IsFalse(receiver.Contains(11));
            CollectionAssert.AreEqual(new ulong[] { 12 }, new List<ulong>(receiver.Added));
            CollectionAssert.AreEqual(new ulong[] { 11 }, new List<ulong>(receiver.Removed));
            Assert.AreEqual(RegionSubscription.Hash(new ulong[] { 10, 12 }), receiver.Hash);
        }

        [Test]
        public void ADeltaOnTheWrongBaseIsRefusedAndTheOldSetSurvives()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver { Grid = sender.Grid };
            sender.Add(10);
            ApplyAll(receiver, Build(sender, 0), out _);
            var stale = new InterestSubscribeMsg
            {
                Seq = 9, BaseSeq = 8, Grid = sender.Grid, Flags = InterestSubscribeFlags.Commit,
                Add = new List<ulong> { 77 }, Remove = new List<ulong>(), FociRegions = new List<ulong>(), Entities = new List<ulong>(),
                SetCount = 2, SetHash = RegionSubscription.Hash(new ulong[] { 10, 77 }),
            };
            Assert.IsFalse(receiver.Apply(stale, out string rejection));
            StringAssert.Contains("based on seq", rejection);
            Assert.AreEqual(1, receiver.Count);
            Assert.IsFalse(receiver.Contains(77));
            Assert.AreEqual(1u, receiver.Seq);
        }

        [Test]
        public void AResultThatDoesNotMatchTheSendersHashIsRolledBack()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver { Grid = sender.Grid };
            sender.Add(10);
            ApplyAll(receiver, Build(sender, 0), out _);
            var corrupt = new InterestSubscribeMsg
            {
                Seq = 2, BaseSeq = 1, Grid = sender.Grid, Flags = InterestSubscribeFlags.Commit,
                Add = new List<ulong> { 11 }, Remove = new List<ulong>(), FociRegions = new List<ulong>(), Entities = new List<ulong>(),
                SetCount = 2, SetHash = 0xDEADBEEF,
            };
            Assert.IsFalse(receiver.Apply(corrupt, out string rejection));
            StringAssert.Contains("did not verify", rejection);
            CollectionAssert.AreEqual(new ulong[] { 10 }, new List<ulong>(Regions(receiver)));
            Assert.AreEqual(1u, receiver.Seq);
        }

        [Test]
        public void AResyncMakesTheNextBuildAFullSnapshot()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver { Grid = sender.Grid };
            sender.Add(10);
            ApplyAll(receiver, Build(sender, 0), out _);
            sender.Add(11);
            var lost = Build(sender, 1);          // never applied: pretend the worker rejected it
            Assert.IsFalse(lost[0].IsFull);
            sender.OnResync(receiver.Seq);
            var recovery = Build(sender, 2);
            Assert.IsTrue(recovery[0].IsFull);
            Assert.IsTrue(ApplyAll(receiver, recovery, out _));
            CollectionAssert.AreEquivalent(new ulong[] { 10, 11 }, new List<ulong>(Regions(receiver)));
        }

        [Test]
        public void ALargeSetIsChunkedAndOnlyTheLastChunkCommits()
        {
            var sender = Sender();
            sender.MaxRegionsPerMessage = 4;
            for (ulong region = 1; region <= 10; region++) sender.Add(region);
            var messages = Build(sender, 0);
            Assert.AreEqual(3, messages.Count);
            Assert.IsFalse(messages[0].IsCommit);
            Assert.IsFalse(messages[1].IsCommit);
            Assert.IsTrue(messages[2].IsCommit);
            Assert.AreEqual(0, messages[0].FociRegions.Count, "the side channels ride on the committing chunk");

            var receiver = new RegionSubscriptionReceiver { Grid = sender.Grid };
            Assert.IsTrue(receiver.Apply(messages[0], out _));
            Assert.IsTrue(receiver.IsStaging);
            Assert.AreEqual(0u, receiver.Seq, "an uncommitted chain does not move the sequence");
            Assert.IsTrue(receiver.Apply(messages[1], out _));
            Assert.IsTrue(receiver.Apply(messages[2], out _));
            Assert.IsFalse(receiver.IsStaging);
            Assert.AreEqual(10, receiver.Count);
            Assert.AreEqual(10, new List<ulong>(receiver.Added).Count);
        }

        [Test]
        public void AChainThatNeverCommitsLeavesTheOldSetAlone()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver { Grid = sender.Grid };
            sender.Add(10);
            ApplyAll(receiver, Build(sender, 0), out _);
            sender.MaxRegionsPerMessage = 1;
            sender.Add(11); sender.Add(12);
            var chunks = Build(sender, 1);
            Assert.IsTrue(receiver.Apply(chunks[0], out _));
            Assert.IsTrue(receiver.IsStaging);
            // The link resets before the commit: the next thing the worker sees is a new chain, and the partial
            // one must leave no trace.
            var fresh = new InterestSubscribeMsg
            {
                Seq = 5, BaseSeq = 1, Grid = sender.Grid, Flags = InterestSubscribeFlags.Commit,
                Add = new List<ulong> { 20 }, Remove = new List<ulong>(), FociRegions = new List<ulong>(), Entities = new List<ulong>(),
                SetCount = 2, SetHash = RegionSubscription.Hash(new ulong[] { 10, 20 }),
            };
            Assert.IsTrue(receiver.Apply(fresh, out string rejection), rejection);
            CollectionAssert.AreEquivalent(new ulong[] { 10, 20 }, new List<ulong>(Regions(receiver)));
        }

        [Test]
        public void DeliveringTheSameMessageTwiceChangesNothing()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver { Grid = sender.Grid };
            sender.Add(10); sender.Add(11);
            var messages = Build(sender, 0);
            Assert.IsTrue(ApplyAll(receiver, messages, out _));
            Assert.IsTrue(receiver.Apply(RoundTrip(messages[0]), out string rejection), rejection);
            Assert.AreEqual(2, receiver.Count);
            Assert.AreEqual(1u, receiver.Seq);
        }

        [Test]
        public void AReorderedDeltaIsRefused()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver { Grid = sender.Grid };
            sender.Add(10);
            ApplyAll(receiver, Build(sender, 0), out _);
            sender.Add(11);
            var second = Build(sender, 1)[0];
            sender.Add(12);
            var third = Build(sender, 2)[0];
            Assert.IsFalse(receiver.Apply(third, out string rejection), "the message it was built on never arrived");
            StringAssert.Contains("based on seq", rejection);
            Assert.IsTrue(receiver.Apply(second, out _));
            Assert.IsTrue(receiver.Apply(third, out _));
            CollectionAssert.AreEquivalent(new ulong[] { 10, 11, 12 }, new List<ulong>(Regions(receiver)));
        }

        [Test]
        public void AGridMismatchIsRefusedRatherThanFilteredWith()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver { Grid = new InterestGrid(32) };
            sender.Add(10);
            var messages = Build(sender, 0);
            Assert.IsFalse(receiver.Apply(messages[0], out string rejection));
            StringAssert.Contains("grid mismatch", rejection);
            Assert.AreEqual(0, receiver.Count);
        }

        [Test]
        public void AReceiverWithoutAGridAdoptsTheSenders()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver();
            sender.Add(10);
            Assert.IsTrue(ApplyAll(receiver, Build(sender, 0), out _));
            Assert.IsTrue(receiver.Grid.Matches(sender.Grid));
        }

        [Test]
        public void ARegionIsKeptForTheLingerAfterTheLastClientNeedsIt()
        {
            var sender = Sender(linger: 3);
            sender.BeginPass();
            sender.Need(10);
            sender.Need(11);
            sender.EndPass(0);
            Assert.AreEqual(2, sender.Count);

            sender.BeginPass();
            sender.Need(10);
            sender.EndPass(1);
            Assert.IsTrue(sender.Contains(11), "pacing along a region edge must not start and stop the stream");

            sender.BeginPass();
            sender.Need(10);
            sender.EndPass(2.9);
            Assert.IsTrue(sender.Contains(11));

            sender.BeginPass();
            sender.Need(10);
            sender.EndPass(4.1);
            Assert.IsFalse(sender.Contains(11));
            Assert.AreEqual(1, sender.Count);
        }

        [Test]
        public void NeedingARegionAgainCancelsItsLinger()
        {
            var sender = Sender(linger: 3);
            sender.BeginPass(); sender.Need(10); sender.EndPass(0);
            sender.BeginPass(); sender.EndPass(1);
            sender.BeginPass(); sender.Need(10); sender.EndPass(2);
            sender.BeginPass(); sender.Need(10); sender.EndPass(10);
            Assert.IsTrue(sender.Contains(10));
        }

        [Test]
        public void TheAuditSnapshotGoesOutOnItsOwnSchedule()
        {
            var sender = Sender();
            sender.ResyncSeconds = 30;
            sender.Add(10);
            Assert.IsTrue(Build(sender, 0)[0].IsFull);
            Assert.IsFalse(sender.Build(new List<InterestSubscribeMsg>(), 5));
            var audit = Build(sender, 31);
            Assert.AreEqual(1, audit.Count);
            Assert.IsTrue(audit[0].IsFull);
        }

        [Test]
        public void FociAndExplicitEntitiesTravelInFullAndOnTheirOwnAreEnoughToSend()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver { Grid = sender.Grid };
            sender.Add(10);
            ApplyAll(receiver, Build(sender, 0), out _);
            sender.SetFoci(new List<ulong> { 10, 11 });
            sender.SetEntities(new List<ulong> { 900 });
            var messages = Build(sender, 1);
            Assert.AreEqual(1, messages.Count, "a changed focus list is worth a message even with no region change");
            Assert.IsTrue(ApplyAll(receiver, messages, out _));
            CollectionAssert.AreEqual(new ulong[] { 10, 11 }, new List<ulong>(receiver.FociRegions));
            CollectionAssert.AreEqual(new ulong[] { 900 }, new List<ulong>(receiver.Entities));
        }

        [Test]
        public void TheChangeCallbackFiresOnceTheSetIsUsable()
        {
            var sender = Sender();
            var receiver = new RegionSubscriptionReceiver { Grid = sender.Grid };
            int changes = 0;
            int countWhenNotified = -1;
            receiver.Changed += r => { changes++; countWhenNotified = r.Count; };
            sender.MaxRegionsPerMessage = 1;
            sender.Add(10); sender.Add(11);
            ApplyAll(receiver, Build(sender, 0), out _);
            Assert.AreEqual(1, changes, "a chunked update notifies once, on the commit");
            Assert.AreEqual(2, countWhenNotified);
        }

        private static List<ulong> Regions(RegionSubscriptionReceiver receiver)
        {
            var list = new List<ulong>();
            foreach (ulong region in receiver) list.Add(region);
            return list;
        }
    }
}
