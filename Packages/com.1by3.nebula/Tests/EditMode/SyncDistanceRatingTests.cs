using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Distance-rated sync state on the worker (<c>docs/server-owned-entities.md</c> D11): what a rated behaviour's
    /// chunks are flagged with, that a whole-state behaviour writes keyframes only, and the one reliable keyframe a
    /// rated behaviour sends once it has stopped changing. What the gateway does with the flags runs against a real
    /// gateway in the service tier (conformance scenario 38, <c>ConformancePriorityTierTests</c>).
    /// </summary>
    public class SyncDistanceRatingTests
    {
        internal abstract class Probe : NetworkBehaviour
        {
            public int Value;
            public int Reads, FullReads;
            public readonly List<bool> Writes = new List<bool>();
            public override bool HasSyncState => true;
            public void Set(int value) { Value = value; MarkSyncDirty(); }
            public override void WriteSyncState(NetworkWriter writer, bool full) { Writes.Add(full); writer.WriteInt(Value); }
            public override void ReadSyncState(NetworkReader reader, uint tick, bool full)
            {
                Value = reader.ReadInt();
                Reads++;
                if (full) FullReads++;
            }
        }

        internal sealed class PlainProbe : Probe { }
        internal sealed class DeltaProbe : Probe { public override SyncDistanceRating SyncDistanceRating => SyncDistanceRating.Keyframes; }
        internal sealed class WholeProbe : Probe
        {
            public override SyncDistanceRating SyncDistanceRating => SyncDistanceRating.WholeState;
            public override Delivery SyncDelivery => Delivery.Sequenced;
        }

        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            NebulaRuntime.Reset();
            NebulaRuntime.IsServer = true;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            NebulaRuntime.Reset();
        }

        private NetworkIdentity Make(string name, bool authority, params Type[] probes)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var id = go.AddComponent<NetworkIdentity>();
            foreach (var t in probes) go.AddComponent(t);
            id.Initialize();
            id.IsSpawned = true;
            if (authority) id.SetAuthority(true);
            return id;
        }

        private static List<(byte index, SyncStateCodec.ChunkFlags flags, int value)> Chunks(byte[] envelope)
        {
            var list = new List<(byte, SyncStateCodec.ChunkFlags, int)>();
            if (envelope == null || envelope.Length == 0) return list;
            SyncStateCodec.ReadEnvelope(new NetworkReader(envelope), (index, flags, chunk) =>
                list.Add((index, flags, chunk.Count >= 4 ? new NetworkReader(chunk).ReadInt() : 0)));
            return list;
        }

        private static byte[] Write(NetworkIdentity e, uint tick, Delivery delivery)
        {
            var w = new NetworkWriter(256);
            return e.WriteSyncState(w, tick, delivery, forGateway: true) > 0 ? w.ToArray() : null;
        }

        /// <summary>One tick as the worker runs it: every destination's two passes, then ClearDirty.</summary>
        private static (List<(byte index, SyncStateCodec.ChunkFlags flags, int value)> reliable, List<(byte index, SyncStateCodec.ChunkFlags flags, int value)> sequenced) Tick(NetworkIdentity e, uint tick)
        {
            var reliable = Chunks(Write(e, tick, Delivery.ReliableOrdered));
            var sequenced = Chunks(Write(e, tick, Delivery.Sequenced));
            // A second destination in the same tick is written exactly the same.
            CollectionAssert.AreEqual(reliable, Chunks(Write(e, tick, Delivery.ReliableOrdered)), $"tick {tick}: every destination gets the same reliable chunks");
            CollectionAssert.AreEqual(sequenced, Chunks(Write(e, tick, Delivery.Sequenced)), $"tick {tick}: and the same sequenced ones");
            e.ClearDirty();
            return (reliable, sequenced);
        }

        [Test]
        public void ARatedBehavioursChunksAreFlaggedAndAnUnratedOnesAreNot()
        {
            var e = Make("mixed", true, typeof(PlainProbe), typeof(DeltaProbe));
            var plain = e.GetComponent<PlainProbe>();
            var rated = e.GetComponent<DeltaProbe>();
            Assert.AreEqual(SyncDistanceRating.Off, plain.DistanceRating);
            Assert.AreEqual(SyncDistanceRating.Keyframes, rated.DistanceRating);
            Tick(e, 1); // the keyframes that open the stream
            plain.Set(1);
            rated.Set(2);
            var chunks = Tick(e, 2).reliable;
            Assert.AreEqual(2, chunks.Count);
            Assert.AreEqual(SyncStateCodec.ChunkFlags.None, chunks[0].flags, "an unrated delta is byte for byte what it was");
            Assert.AreEqual(SyncStateCodec.ChunkFlags.DistanceRated, chunks[1].flags, "a rated delta says so and nothing else");

            // A client reads the flagged chunk as any other.
            var client = Make("client", false, typeof(PlainProbe), typeof(DeltaProbe));
            var w = new NetworkWriter(64);
            int at = SyncStateCodec.BeginEnvelope(w);
            SyncStateCodec.WriteRawChunk(w, 1, SyncStateCodec.ChunkFlags.Full | SyncStateCodec.ChunkFlags.DistanceRated | SyncStateCodec.ChunkFlags.Settled,
                new ArraySegment<byte>(BitConverter.GetBytes(42)));
            SyncStateCodec.EndEnvelope(w, at, 1);
            client.ReadSyncState(new NetworkReader(w.ToSegment()), 3, null, reliable: true);
            var read = client.GetComponent<DeltaProbe>();
            Assert.AreEqual(42, read.Value);
            Assert.AreEqual(1, read.FullReads, "the rating bits do not hide the keyframe bit");
        }

        [Test]
        public void AWholeStateBehaviourIsAskedForItsWholeStateEveryTimeAndEveryChunkIsAKeyframe()
        {
            var e = Make("whole", true, typeof(WholeProbe));
            var probe = e.GetComponent<WholeProbe>();
            Tick(e, 1);
            probe.Writes.Clear();
            for (uint t = 2; t < 10; t++)
            {
                probe.Set((int)t);
                var chunks = Tick(e, t).sequenced;
                Assert.AreEqual(1, chunks.Count);
                Assert.AreEqual(SyncStateCodec.ChunkFlags.Full | SyncStateCodec.ChunkFlags.DistanceRated, chunks[0].flags, $"tick {t}");
            }
            CollectionAssert.DoesNotContain(probe.Writes, false, "never asked for a delta");
            // Unchanged, it writes nothing but the periodic keyframe an unreliable behaviour always writes.
            Assert.IsEmpty(Tick(e, 11).sequenced);
        }

        [Test]
        public void ARatedBehaviourThatStopsChangingSendsOneReliableKeyframeOnceItHasSettled()
        {
            var e = Make("settling", true, typeof(DeltaProbe));
            var probe = e.GetComponent<DeltaProbe>();
            Tick(e, 1);
            Assert.AreEqual(1, e.SyncSettlesPending, "any change that goes out owes a settle keyframe, the opening one too: a far client may not be sent it");

            probe.Set(5);
            var change = Tick(e, 3).reliable;
            Assert.AreEqual(SyncStateCodec.ChunkFlags.DistanceRated, change[0].flags, "a delta");
            probe.Set(6);
            Tick(e, 4);
            Assert.AreEqual(1, e.SyncSettlesPending, "a change went out: a settle keyframe is owed");

            uint settleAt = 4 + NetworkIdentity.SyncSettleTicks;
            for (uint t = 5; t < settleAt; t++)
            {
                var (reliable, sequenced) = Tick(e, t);
                Assert.IsEmpty(reliable, $"tick {t}: nothing while it is still settling");
                Assert.IsEmpty(sequenced, $"tick {t}");
            }
            var settle = Tick(e, settleAt);
            Assert.AreEqual(1, settle.reliable.Count, "one settle keyframe");
            Assert.AreEqual(SyncStateCodec.ChunkFlags.Full | SyncStateCodec.ChunkFlags.Settled | SyncStateCodec.ChunkFlags.DistanceRated, settle.reliable[0].flags);
            Assert.AreEqual(6, settle.reliable[0].value, "carrying the latest state");
            Assert.AreEqual(0, e.SyncSettlesPending, "paid");
            for (uint t = settleAt + 1; t < settleAt + 90; t++) Assert.IsEmpty(Tick(e, t).reliable, $"tick {t}: and only once");

            // A change during the wait pushes the settle back.
            probe.Set(7);
            Tick(e, 200);
            probe.Set(8);
            Tick(e, 220);
            Assert.IsEmpty(Tick(e, 200 + NetworkIdentity.SyncSettleTicks).reliable, "30 ticks after the first change is not settled");
            Assert.AreEqual(1, Tick(e, 220 + NetworkIdentity.SyncSettleTicks).reliable.Count, "30 after the last is");
        }

        [Test]
        public void ASequencedRatedBehaviourSettlesOnTheReliableStream()
        {
            var e = Make("whole", true, typeof(WholeProbe));
            var probe = e.GetComponent<WholeProbe>();
            Tick(e, 1);
            probe.Set(9);
            Tick(e, 2);
            uint settleAt = 2 + NetworkIdentity.SyncSettleTicks;
            for (uint t = 3; t < settleAt; t++) Tick(e, t);
            var (reliable, sequenced) = Tick(e, settleAt);
            Assert.AreEqual(1, reliable.Count, "the settle keyframe goes reliably whatever the behaviour's own delivery");
            Assert.AreEqual(9, reliable[0].value);
            Assert.IsEmpty(sequenced, "and is not also sent on the sequenced stream");
        }

        [Test]
        public void ASleepingEntityStaysInThePublishPassUntilItsSettleKeyframeHasGone()
        {
            var e = Make("sleeper", true, typeof(DeltaProbe));
            var probe = e.GetComponent<DeltaProbe>();
            Tick(e, 1);
            probe.Set(3);
            Tick(e, 2);
            Assert.IsTrue(e.HasPendingChanges, "a settle keyframe is owed");
            uint settleAt = 2 + NetworkIdentity.SyncSettleTicks;
            for (uint t = 3; t <= settleAt; t++) Tick(e, t);
            Assert.IsFalse(e.HasPendingChanges, "nothing left once it has gone");
        }

        [Test]
        public void TheRootTransformIsNeverRatedByItsBehaviour()
        {
            var go = new GameObject("rooted");
            _objects.Add(go);
            var id = go.AddComponent<NetworkIdentity>();
            go.AddComponent<NetworkTransform>();
            id.Initialize();
            Assert.IsNotNull(id.RootTransform);
            Assert.AreEqual(SyncDistanceRating.Off, id.RootTransform.DistanceRating, "the gateway rates the root transform's own stream already");
        }
    }
}
