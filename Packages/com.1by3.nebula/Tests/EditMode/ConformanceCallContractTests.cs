using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The cross-worker call contract (docs/cross-worker-calls.md) exercised on the <b>production</b> types the
    /// worker runs: <see cref="AuthorityCallRouter"/> decides apply / forward / reject, <see cref="AuthorityCallLedger"/>
    /// is the at-most-once memory, <see cref="AuthorityCallTracker"/> is the sender's reply channel, and
    /// <see cref="AuthorityCallMsg"/> / <see cref="AuthorityCallReplyMsg"/> are the bytes on the wire. Nothing here
    /// is a stand-in: change the epoch rule, the hop bound, or the ledger's eviction and these fail.
    /// <para>
    /// Pure C#, so the standalone services build runs them too.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public class ConformanceCallContractTests
    {
        private const int MaxHops = 3;
        private const ulong Entity = (1UL << 48) | 7; // spawned by worker 1, sequence 7

        /// <summary>
        /// A scripted worker: its router, a scripted view of one entity, a count of how often the method ran, and
        /// the outgoing forwards it produced. The transport is the test: it hands a message from one worker to the
        /// next, exactly as reliable ordered delivery would, with the epoch bumps a handover makes in between.
        /// </summary>
        private sealed class Worker
        {
            public readonly ushort Index;
            public readonly AuthorityCallRouter Router;
            public readonly AuthorityCallTracker Tracker;
            public bool Known = true;
            public bool HasAuthority;
            public uint Epoch;
            public bool CanForward;
            public int Applied;
            public readonly List<AuthorityCallReplyMsg> Replies = new List<AuthorityCallReplyMsg>();

            public Worker(ushort index, int maxHops = MaxHops, AuthorityCallLedger ledger = null)
            {
                Index = index;
                Router = new AuthorityCallRouter(maxHops, ledger);
                Tracker = new AuthorityCallTracker(index);
            }

            public AuthorityCallTarget Target => !Known ? AuthorityCallTarget.Unknown
                : HasAuthority ? AuthorityCallTarget.Authoritative(Epoch)
                : AuthorityCallTarget.Ghost(Epoch, CanForward);

            /// <summary>What NebulaWorker.OnAuthorityRpc does with the message, minus the transport: returns the message to forward, or null.</summary>
            public AuthorityCallMsg? Receive(AuthorityCallMsg msg, uint tick = 100)
            {
                var decision = Router.Decide(msg.CallId, msg.Epoch, msg.Hops, Target, tick);
                switch (decision.Action)
                {
                    case AuthorityCallAction.Apply:
                        Applied++;
                        if (msg.WantsReply) Replies.Add(new AuthorityCallReplyMsg { CallId = msg.CallId, Outcome = AuthorityCallOutcome.Accepted, Epoch = Epoch, Hops = msg.Hops });
                        return null;
                    case AuthorityCallAction.Forward:
                        msg.Hops++;
                        return msg;
                    default:
                        if (msg.WantsReply) Replies.Add(new AuthorityCallReplyMsg { CallId = msg.CallId, Outcome = decision.Outcome, Epoch = Known ? Epoch : 0, Hops = msg.Hops });
                        return null;
                }
            }
        }

        private static AuthorityCallMsg Call(Worker sender, uint epochSeen, bool wantsReply = true) => new AuthorityCallMsg
        {
            CallId = sender.Tracker.Mint(),
            Hops = 0,
            Flags = wantsReply ? AuthorityCallFlags.WantsReply : AuthorityCallFlags.None,
            NetId = Entity,
            Epoch = epochSeen,
            BehaviourIndex = 0,
            MethodHash = 0x1234ABCD,
            Args = new byte[] { 1, 2, 3 },
        };

        /// <summary>Deliver the sender's reply queue to it, as the wire would (through a write/read round trip).</summary>
        private static void DeliverReplies(Worker from, Worker to)
        {
            foreach (var reply in from.Replies)
            {
                Assert.AreEqual(to.Index, AuthorityCallId.WorkerIndexOf(reply.CallId), "a reply goes to the worker that minted the call id");
                to.Tracker.Complete(reply.CallId, RoundTrip(reply).ToResult());
            }
            from.Replies.Clear();
        }

        // ------------------------------------------------------------------------- (a) handover between send and apply

        [Test]
        public void ACallWhoseTargetHandsOverInFlightIsAppliedExactlyOnce()
        {
            var a = new Worker(1) { HasAuthority = false, Epoch = 1, CanForward = true }; // holds a ghost, sends the call
            var b = new Worker(2) { HasAuthority = true, Epoch = 1 };                     // the authority when A sent
            var c = new Worker(3) { HasAuthority = false, Epoch = 1, CanForward = true };

            AuthorityCallResult? seen = null;
            var call = Call(a, epochSeen: 1);
            a.Tracker.Track(call.CallId, deadlineTick: 1000, r => seen = r);

            // While the call is on the wire, B hands the entity to C: both bump to epoch 2, B remembers where it went.
            b.HasAuthority = false; b.Epoch = 2; b.CanForward = true;
            c.HasAuthority = true; c.Epoch = 2;

            var fromB = b.Receive(RoundTrip(call));
            Assert.IsNotNull(fromB, "B no longer has authority: it forwards");
            Assert.AreEqual(1, fromB.Value.Hops);
            Assert.AreEqual(call.CallId, fromB.Value.CallId, "forwarding keeps the call id");
            Assert.AreEqual(0, b.Applied);

            var fromC = c.Receive(RoundTrip(fromB.Value));
            Assert.IsNull(fromC, "C has authority: it applies");
            Assert.AreEqual(1, c.Applied);

            // The same call arrives at C a second time (a retry, a loop, a replay): at-most-once holds.
            Assert.IsNull(c.Receive(RoundTrip(fromB.Value)));
            Assert.AreEqual(1, c.Applied, "the replay did not run the method again");
            Assert.AreEqual(2, c.Replies.Count);
            Assert.AreEqual(AuthorityCallOutcome.Accepted, c.Replies[0].Outcome);
            Assert.AreEqual(AuthorityCallOutcome.RejectedDuplicate, c.Replies[1].Outcome);
            Assert.AreEqual(1, c.Router.Ledger.Duplicates);

            // The first reply settles the sender; the duplicate's reply is late and dropped.
            DeliverReplies(c, a);
            Assert.IsTrue(seen.HasValue);
            Assert.AreEqual(AuthorityCallOutcome.Accepted, seen.Value.Outcome);
            Assert.AreEqual(2u, seen.Value.TargetEpoch, "the reply carries the epoch the call was applied at");
            Assert.AreEqual(1, seen.Value.Hops);
            Assert.AreEqual(1, a.Tracker.LateReplies);
            Assert.AreEqual(0, a.Tracker.PendingCount);
        }

        [Test]
        public void ACallForwardedBackToItsSenderSettlesInProcess()
        {
            // A sends to B; B hands the entity back to A before it arrives; B forwards; A now has authority and applies.
            var a = new Worker(1) { HasAuthority = false, Epoch = 1, CanForward = true };
            var b = new Worker(2) { HasAuthority = true, Epoch = 1 };
            var call = Call(a, 1);
            b.HasAuthority = false; b.Epoch = 2; b.CanForward = true;
            a.HasAuthority = true; a.Epoch = 2;
            var forwarded = b.Receive(call);
            Assert.IsNotNull(forwarded);
            Assert.IsNull(a.Receive(forwarded.Value));
            Assert.AreEqual(1, a.Applied);
            Assert.AreEqual(a.Index, AuthorityCallId.WorkerIndexOf(a.Replies[0].CallId), "the reply is addressed to A itself: the worker completes its own tracker");
        }

        // ------------------------------------------------------------------------- (b) stale epoch

        [Test]
        public void AStaleEpochIsRejectedAndTheSenderObservesIt()
        {
            var a = new Worker(1) { Epoch = 5, CanForward = true };
            var b = new Worker(2) { HasAuthority = true, Epoch = 9 }; // four authority changes ahead of A's copy: past the bound of 3
            AuthorityCallResult? seen = null;
            var call = Call(a, epochSeen: 5);
            a.Tracker.Track(call.CallId, 1000, r => seen = r);

            Assert.IsNull(b.Receive(RoundTrip(call)));
            Assert.AreEqual(0, b.Applied, "a stale call never runs");
            Assert.IsFalse(b.Router.Ledger.Contains(call.CallId), "a rejected call is not remembered as applied");
            Assert.AreEqual(1, b.Replies.Count);
            Assert.AreEqual(AuthorityCallOutcome.RejectedStaleEpoch, b.Replies[0].Outcome);

            DeliverReplies(b, a);
            Assert.AreEqual(AuthorityCallOutcome.RejectedStaleEpoch, seen.Value.Outcome);
            Assert.AreEqual(9u, seen.Value.TargetEpoch, "the sender learns how far ahead the entity is");
            Assert.IsFalse(seen.Value.Succeeded);
        }

        [Test]
        public void TheEpochWindowIsTheHopBound()
        {
            var router = new AuthorityCallRouter(MaxHops);
            Assert.IsFalse(router.IsStale(callEpoch: 7, entityEpoch: 7), "same epoch");
            Assert.IsFalse(router.IsStale(callEpoch: 4, entityEpoch: 7), "three handovers behind: what three forwards could have carried it through");
            Assert.IsTrue(router.IsStale(callEpoch: 3, entityEpoch: 7), "four behind: further than any legitimate forwarding path");
            Assert.IsTrue(router.IsStale(callEpoch: 8, entityEpoch: 7), "a call from the future: the applying copy is behind the caller's, so it is not the authority the caller meant");

            var strict = new AuthorityCallRouter(0);
            Assert.IsTrue(strict.IsStale(6, 7), "with no forwarding allowed, only an exact epoch match is current");
            Assert.IsFalse(strict.IsStale(7, 7));
        }

        [Test]
        public void StalenessIsCheckedBeforeAuthority()
        {
            var ghost = new Worker(2) { HasAuthority = false, Epoch = 9, CanForward = true };
            var stale = Call(new Worker(1), epochSeen: 1);
            Assert.IsNull(ghost.Receive(stale), "a ghost does not forward a call that is already stale by its own copy");
            Assert.AreEqual(AuthorityCallOutcome.RejectedStaleEpoch, ghost.Replies[0].Outcome);
        }

        // ------------------------------------------------------------------------- (c) hop bound

        [Test]
        public void ForwardingStopsAtTheHopBound()
        {
            var sender = new Worker(1);
            var call = Call(sender, epochSeen: 1);
            // Every worker on the path is a ghost that believes somebody else has authority: the pathological loop.
            var path = new List<Worker>();
            for (ushort i = 2; i <= 6; i++) path.Add(new Worker(i) { HasAuthority = false, Epoch = 1, CanForward = true });

            AuthorityCallMsg current = call;
            int forwards = 0;
            Worker last = null;
            foreach (var w in path)
            {
                last = w;
                var next = w.Receive(RoundTrip(current));
                if (next == null) break;
                forwards++;
                current = next.Value;
            }
            Assert.AreEqual(MaxHops, forwards, "the call was forwarded exactly MaxHops times");
            Assert.AreEqual(MaxHops, current.Hops);
            Assert.AreEqual(1, last.Replies.Count, "the worker that received the call at the bound rejected it");
            Assert.AreEqual(AuthorityCallOutcome.RejectedHopLimit, last.Replies[0].Outcome);
            Assert.AreEqual(MaxHops, last.Replies[0].Hops);
            foreach (var w in path) Assert.AreEqual(0, w.Applied);
            Assert.AreEqual(path[MaxHops], last, "workers past the bound never saw the call");
        }

        [Test]
        public void ACallAtTheBoundIsStillAppliedByItsAuthority()
        {
            var authority = new Worker(9) { HasAuthority = true, Epoch = 4 };
            var call = Call(new Worker(1), epochSeen: 1);
            call.Hops = MaxHops;
            Assert.IsNull(authority.Receive(call));
            Assert.AreEqual(1, authority.Applied, "the bound limits forwarding, not applying");
        }

        [Test]
        public void AGhostWithNobodyToForwardToRejectsAsUnreachableAndAMissingEntityAsUnknown()
        {
            var stranded = new Worker(2) { HasAuthority = false, Epoch = 1, CanForward = false };
            Assert.IsNull(stranded.Receive(Call(new Worker(1), 1)));
            Assert.AreEqual(AuthorityCallOutcome.RejectedUnreachable, stranded.Replies[0].Outcome);

            var empty = new Worker(3) { Known = false };
            Assert.IsNull(empty.Receive(Call(new Worker(1), 1)));
            Assert.AreEqual(AuthorityCallOutcome.RejectedUnknownEntity, empty.Replies[0].Outcome);
            Assert.AreEqual(0u, empty.Replies[0].Epoch, "no entity, no epoch to report");
        }

        [Test]
        public void AFireAndForgetCallIsDecidedByTheSameRulesWithoutAReply()
        {
            var b = new Worker(2) { HasAuthority = true, Epoch = 9 };
            var call = Call(new Worker(1), epochSeen: 1, wantsReply: false);
            Assert.IsNull(b.Receive(call));
            Assert.AreEqual(0, b.Applied);
            Assert.AreEqual(0, b.Replies.Count, "nobody asked");
            Assert.AreEqual(1, b.Router.Rejected);
        }

        // ------------------------------------------------------------------------- (d) bounded ledger

        [Test]
        public void TheLedgerForgetsTheOldestIdPastItsCapacityAndStillDedupesTheNewest()
        {
            var ledger = new AuthorityCallLedger(capacity: 8, ttlTicks: 1000);
            for (ulong id = 1; id <= 8; id++) Assert.IsTrue(ledger.TryRecord(id, tick: 10));
            Assert.AreEqual(8, ledger.Count);
            Assert.IsFalse(ledger.TryRecord(1, 11), "at capacity, every id is still remembered");

            Assert.IsTrue(ledger.TryRecord(9, 12), "one past capacity is accepted...");
            Assert.AreEqual(8, ledger.Count, "...within the bound...");
            Assert.AreEqual(1, ledger.Evictions);
            Assert.IsFalse(ledger.Contains(1), "...by forgetting the oldest");
            Assert.IsTrue(ledger.TryRecord(1, 13), "a forgotten id would be applied again: that is what the bound means");
            Assert.IsFalse(ledger.Contains(2), "which cost the next oldest its place");
            Assert.IsFalse(ledger.TryRecord(9, 14), "the newest is still a duplicate");
            Assert.IsFalse(ledger.TryRecord(8, 14));
            Assert.AreEqual(3, ledger.Duplicates);
        }

        [Test]
        public void TheLedgerExpiresIdsByTick()
        {
            var ledger = new AuthorityCallLedger(capacity: 100, ttlTicks: 600);
            ledger.TryRecord(1, 0);
            ledger.TryRecord(2, 300);
            ledger.Expire(600);
            Assert.IsTrue(ledger.Contains(1), "exactly TtlTicks old is still remembered");
            ledger.Expire(601);
            Assert.IsFalse(ledger.Contains(1));
            Assert.IsTrue(ledger.Contains(2));
            ledger.Expire(901);
            Assert.AreEqual(0, ledger.Count);
            Assert.IsTrue(ledger.TryRecord(2, 902), "after expiry the id is a new call again");
        }

        [Test]
        public void TheRouterRecordsOnlyWhatItApplies()
        {
            var ledger = new AuthorityCallLedger(4, 100);
            var w = new Worker(2, MaxHops, ledger) { HasAuthority = false, Epoch = 1, CanForward = true };
            var call = Call(new Worker(1), 1);
            Assert.IsNotNull(w.Receive(call));
            Assert.AreEqual(0, ledger.Count, "a forward is not an application: the same call may legitimately return here after another handover");
            w.HasAuthority = true;
            Assert.IsNull(w.Receive(call));
            Assert.AreEqual(1, ledger.Count);
            Assert.AreEqual(1, w.Applied);
        }

        [Test]
        public void TheDefaultBoundsAreWhatTheContractStates()
        {
            var ledger = new AuthorityCallLedger();
            Assert.AreEqual(4096, ledger.Capacity);
            Assert.AreEqual(600u, ledger.TtlTicks);
            Assert.AreEqual(4096, AuthorityCallLedger.DefaultCapacity);
            Assert.AreEqual(600u, AuthorityCallLedger.DefaultTtlTicks);
        }

        // ------------------------------------------------------------------------- call ids and the sender's tracker

        [Test]
        public void CallIdsCarryTheirSenderAndRestartIntoFreshRanges()
        {
            ulong id = AuthorityCallId.Make(5, 42);
            Assert.AreEqual(5, AuthorityCallId.WorkerIndexOf(id));
            Assert.AreEqual(42UL, AuthorityCallId.SequenceOf(id));
            Assert.AreEqual((5UL << 48) | 42UL, id);

            var first = new AuthorityCallTracker(5, AuthorityCallId.InitialSequence(0x0000_1234));
            var restarted = new AuthorityCallTracker(5, AuthorityCallId.InitialSequence(0x0000_1235));
            ulong a = first.Mint(), b = restarted.Mint();
            Assert.AreEqual(5, AuthorityCallId.WorkerIndexOf(a));
            Assert.AreNotEqual(a, b, "the same worker index after a restart mints ids the old run never used");
            Assert.AreEqual(0x1234_0000_0001UL, AuthorityCallId.SequenceOf(a));
            Assert.AreEqual(0x1235_0000_0001UL, AuthorityCallId.SequenceOf(b));
            Assert.AreNotEqual(first.Mint(), a, "and every mint is new");
        }

        [Test]
        public void ATrackedCallSettlesExactlyOnceByReplyOrByDeadline()
        {
            var tracker = new AuthorityCallTracker(1);
            var results = new List<AuthorityCallResult>();
            ulong replied = tracker.Mint(), abandoned = tracker.Mint();
            tracker.Track(replied, deadlineTick: 50, results.Add);
            tracker.Track(abandoned, deadlineTick: 50, results.Add);
            Assert.AreEqual(2, tracker.PendingCount);

            Assert.IsTrue(tracker.Complete(replied, new AuthorityCallResult(replied, AuthorityCallOutcome.Accepted, 3, 1)));
            Assert.IsFalse(tracker.Complete(replied, new AuthorityCallResult(replied, AuthorityCallOutcome.Accepted, 3, 1)), "a second reply is late");
            tracker.Expire(49);
            Assert.AreEqual(1, results.Count, "the deadline has not passed");
            tracker.Expire(50);
            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(AuthorityCallOutcome.TimedOut, results[1].Outcome);
            Assert.AreEqual(abandoned, results[1].CallId);
            Assert.AreEqual(0, tracker.PendingCount);
            tracker.Expire(1000);
            Assert.AreEqual(2, results.Count, "nothing settles twice");
            Assert.AreEqual(1, tracker.LateReplies);
        }

        // ------------------------------------------------------------------------- (e) wire format

        private static AuthorityCallMsg RoundTrip(AuthorityCallMsg msg)
        {
            var w = new NetworkWriter(256);
            msg.Write(w);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            Assert.AreEqual((byte)MsgId.AuthorityRpc, r.ReadByte());
            return AuthorityCallMsg.Read(r);
        }

        private static AuthorityCallReplyMsg RoundTrip(AuthorityCallReplyMsg msg)
        {
            var w = new NetworkWriter(64);
            msg.Write(w);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            Assert.AreEqual((byte)MsgId.AuthorityRpcReply, r.ReadByte());
            return AuthorityCallReplyMsg.Read(r);
        }

        [Test]
        public void TheContractIsInTheProtocolSinceEighteenAndTheIdsArePinned()
        {
            Assert.GreaterOrEqual(HelloMsg.ProtocolVersion, 18, "the contract messages arrived in protocol 18");
            Assert.AreEqual(46, (byte)MsgId.AuthorityRpc);
            Assert.AreEqual(49, (byte)MsgId.AuthorityRpcReply);
            Assert.AreEqual(1, (byte)AuthorityCallFlags.WantsReply);
            Assert.AreEqual(1, (byte)AuthorityCallOutcome.Accepted);
            Assert.AreEqual(2, (byte)AuthorityCallOutcome.RejectedStaleEpoch);
            Assert.AreEqual(3, (byte)AuthorityCallOutcome.RejectedUnknownEntity);
            Assert.AreEqual(4, (byte)AuthorityCallOutcome.RejectedHopLimit);
            Assert.AreEqual(5, (byte)AuthorityCallOutcome.RejectedDuplicate);
            Assert.AreEqual(6, (byte)AuthorityCallOutcome.RejectedUnreachable);
            Assert.AreEqual(7, (byte)AuthorityCallOutcome.TimedOut);
        }

        [Test]
        public void AuthorityCallMsgBytesArePinned()
        {
            var msg = new AuthorityCallMsg
            {
                CallId = AuthorityCallId.Make(2, 42),
                Hops = 1,
                Flags = AuthorityCallFlags.WantsReply,
                NetId = Entity,
                Epoch = 5,
                BehaviourIndex = 3,
                MethodHash = 0xDEADBEEF,
                Args = new byte[] { 0x11, 0x22 },
            };
            var w = new NetworkWriter(64);
            msg.Write(w);
            var expected = new byte[]
            {
                0x2E,                                           // MsgId.AuthorityRpc = 46
                0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, // call_id: worker 2 << 48 | 42, little-endian
                0x01,                                           // hops
                0x01,                                           // flags: WantsReply
                0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, // net_id: worker 1 << 48 | 7
                0x05, 0x00, 0x00, 0x00,                         // entity_epoch
                0x03,                                           // behavior_index
                0xEF, 0xBE, 0xAD, 0xDE,                         // method_hash
                0x02, 0x00, 0x11, 0x22,                         // args: u16 length, bytes
            };
            CollectionAssert.AreEqual(expected, w.ToArray());

            var back = RoundTrip(msg);
            Assert.AreEqual(msg.CallId, back.CallId);
            Assert.AreEqual(msg.Hops, back.Hops);
            Assert.AreEqual(msg.Flags, back.Flags);
            Assert.IsTrue(back.WantsReply);
            Assert.AreEqual(msg.NetId, back.NetId);
            Assert.AreEqual(msg.Epoch, back.Epoch);
            Assert.AreEqual(msg.BehaviourIndex, back.BehaviourIndex);
            Assert.AreEqual(msg.MethodHash, back.MethodHash);
            CollectionAssert.AreEqual(msg.Args, back.Args);
        }

        [Test]
        public void AuthorityCallReplyMsgBytesArePinned()
        {
            var msg = new AuthorityCallReplyMsg { CallId = AuthorityCallId.Make(2, 42), Outcome = AuthorityCallOutcome.RejectedStaleEpoch, Epoch = 9, Hops = 1 };
            var w = new NetworkWriter(64);
            msg.Write(w);
            var expected = new byte[]
            {
                0x31,                                           // MsgId.AuthorityRpcReply = 49
                0x2A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, // call_id
                0x02,                                           // outcome: RejectedStaleEpoch
                0x09, 0x00, 0x00, 0x00,                         // entity_epoch on the deciding worker
                0x01,                                           // hops
            };
            CollectionAssert.AreEqual(expected, w.ToArray());

            var back = RoundTrip(msg);
            Assert.AreEqual(msg.CallId, back.CallId);
            Assert.AreEqual(msg.Outcome, back.Outcome);
            Assert.AreEqual(msg.Epoch, back.Epoch);
            Assert.AreEqual(msg.Hops, back.Hops);
            var result = back.ToResult();
            Assert.AreEqual(AuthorityCallOutcome.RejectedStaleEpoch, result.Outcome);
            Assert.AreEqual(9u, result.TargetEpoch);
            Assert.AreEqual(1, result.Hops);
            Assert.IsFalse(result.Succeeded);
        }

        [Test]
        public void AnEmptyArgumentListRoundTrips()
        {
            var back = RoundTrip(new AuthorityCallMsg { CallId = 1, NetId = Entity, Epoch = 1, Args = Array.Empty<byte>() });
            Assert.AreEqual(0, back.Args.Length);
            Assert.IsFalse(back.WantsReply);
        }
    }
}
