using System.Collections.Generic;
using NUnit.Framework;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula.Tests
{
    /// <summary>
    /// Round trips for the messages interest management adds in protocol 17, and for the fields it adds to
    /// messages that already existed. A field that survives its own writer but not the reader is the kind of
    /// bug that shows up as an entity nobody can see.
    /// </summary>
    public class InterestWireTests
    {
        private static NetworkReader Written(System.Action<NetworkWriter> write, MsgId expected)
        {
            var w = new NetworkWriter(512);
            write(w);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            Assert.AreEqual((byte)expected, r.ReadByte());
            return r;
        }

        [Test]
        public void TheProtocolIsAtLeastTheOneThatCarriesInterest()
        {
            Assert.GreaterOrEqual(HelloMsg.ProtocolVersion, 18);
        }

        [Test]
        public void InterestSubscribeRoundTrips()
        {
            var grid = InterestGrid.Resolve(InterestSettings.Default, 256, cellsCentred: true);
            var msg = new InterestSubscribeMsg
            {
                Seq = 42, BaseSeq = 41, Grid = grid,
                Flags = InterestSubscribeFlags.Full | InterestSubscribeFlags.Commit,
                Add = new List<ulong> { 1, 2, 3 },
                Remove = new List<ulong> { 4 },
                FociRegions = new List<ulong> { 5, 6 },
                Entities = new List<ulong> { 7 },
                SetCount = 3, SetHash = 0x0123456789ABCDEF,
            };
            var back = InterestSubscribeMsg.Read(Written(msg.Write, MsgId.InterestSubscribe));
            Assert.AreEqual(42u, back.Seq);
            Assert.AreEqual(41u, back.BaseSeq);
            Assert.IsTrue(back.IsFull);
            Assert.IsTrue(back.IsCommit);
            CollectionAssert.AreEqual(msg.Add, back.Add);
            CollectionAssert.AreEqual(msg.Remove, back.Remove);
            CollectionAssert.AreEqual(msg.FociRegions, back.FociRegions);
            CollectionAssert.AreEqual(msg.Entities, back.Entities);
            Assert.AreEqual(3u, back.SetCount);
            Assert.AreEqual(0x0123456789ABCDEFUL, back.SetHash);
            Assert.IsTrue(grid.Matches(back.Grid));
        }

        [Test]
        public void ReadingIntoAPreviousMessageReusesItsLists()
        {
            var first = new InterestSubscribeMsg
            {
                Seq = 1, Add = new List<ulong> { 1 }, Remove = new List<ulong>(), FociRegions = new List<ulong>(), Entities = new List<ulong>(),
            };
            var reuse = InterestSubscribeMsg.Read(Written(first.Write, MsgId.InterestSubscribe));
            var second = InterestSubscribeMsg.Read(Written(new InterestSubscribeMsg
            {
                Seq = 2, Add = new List<ulong> { 9, 8 }, Remove = new List<ulong>(), FociRegions = new List<ulong>(), Entities = new List<ulong>(),
            }.Write, MsgId.InterestSubscribe), reuse);
            Assert.AreSame(reuse.Add, second.Add);
            CollectionAssert.AreEqual(new ulong[] { 9, 8 }, second.Add);
        }

        [Test]
        public void ResyncRedirectAndForgetRoundTrip()
        {
            var resync = InterestResyncMsg.Read(Written(new InterestResyncMsg { HaveSeq = 7 }.Write, MsgId.InterestResync));
            Assert.AreEqual(7u, resync.HaveSeq);

            var redirect = EntityRedirectMsg.Read(Written(new EntityRedirectMsg { NetId = 99, NewWorkerIndex = 4 }.Write, MsgId.EntityRedirect));
            Assert.AreEqual(99UL, redirect.NetId);
            Assert.AreEqual(4, redirect.NewWorkerIndex);

            var forget = EntityForgetMsg.Read(Written(new EntityForgetMsg { NetId = 5, Epoch = 6 }.Write, MsgId.EntityForget));
            Assert.AreEqual(5UL, forget.NetId);
            Assert.AreEqual(6u, forget.Epoch);
        }

        [Test]
        public void AFocusHintRoundTrips()
        {
            // Absolute world coordinates in double: a strategy camera 40 km out is a place a float cannot name
            // to the metre, and the gateway buckets by exactly these numbers.
            var hint = ClientFocusHintMsg.Read(Written(new ClientFocusHintMsg { X = 41234.5, Y = -2.5, Z = 3.25 }.Write, MsgId.ClientFocusHint));
            Assert.AreEqual(41234.5, hint.X);
            Assert.AreEqual(-2.5, hint.Y);
            Assert.AreEqual(3.25, hint.Z);
            Assert.AreEqual(1e-7, ClientFocusHintMsg.Read(Written(new ClientFocusHintMsg { X = 1e-7 }.Write, MsgId.ClientFocusHint)).X,
                "the point travels as a double and is not rounded through a float on the way");

            var clear = ClientFocusHintMsg.Read(Written(new ClientFocusHintMsg { Generation = 7, Clear = true }.Write, MsgId.ClientFocusHint));
            Assert.IsTrue(clear.Clear);
            Assert.AreEqual(7, clear.Generation);
            Assert.IsTrue(ClientFocusHintMsg.IsStale(6, 7));
            Assert.IsFalse(ClientFocusHintMsg.IsStale(7, 7));
            Assert.IsTrue(ClientFocusHintMsg.IsStale(250, 3), "generations wrap");
            Assert.IsFalse(ClientFocusHintMsg.IsStale(3, 250));
        }

        [Test]
        public void ASpawnCarriesItsPrefabsInterestFieldsAndTheClientsViewSequence()
        {
            var msg = new EntitySpawnMsg
            {
                NetId = 3, PrefabId = 1, OwnerIdentity = "", Vars = System.Array.Empty<byte>(), State = System.Array.Empty<byte>(),
                RelevanceRadius = 512f, InterestFlags = EntityInterestFlags.AlwaysRelevant, InterestGroup = 9, ViewSeq = 1234,
            };
            var w = new NetworkWriter(512);
            msg.Write(w, MsgId.EntitySpawn);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            Assert.AreEqual((byte)MsgId.EntitySpawn, r.ReadByte());
            var back = EntitySpawnMsg.Read(r);
            Assert.AreEqual(512f, back.RelevanceRadius, 1f, "the radius travels as an f16");
            Assert.AreEqual(EntityInterestFlags.AlwaysRelevant, back.InterestFlags);
            Assert.AreEqual(9, back.InterestGroup);
            Assert.AreEqual(1234, back.ViewSeq);
        }

        [Test]
        public void ADespawnCarriesTheViewItEnds()
        {
            var w = new NetworkWriter(64);
            new EntityDespawnMsg { NetId = 3, Epoch = 2, ViewSeq = 7 }.Write(w, MsgId.EntityDespawn);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            Assert.AreEqual((byte)MsgId.EntityDespawn, r.ReadByte());
            var back = EntityDespawnMsg.Read(r);
            Assert.AreEqual(3UL, back.NetId);
            Assert.AreEqual(2u, back.Epoch);
            Assert.AreEqual(7, back.ViewSeq);
        }

        [Test]
        public void ContainerOwnershipCarriesSnapshotsAndDeltas()
        {
            var entries = new List<ContainerOwnershipEntry>
            {
                new ContainerOwnershipEntry { ContainerIndex = 0, ContainerId = "arena", WorkerIndex = 1, WorkerId = "w1", Epoch = 3, State = LeaseState.Active },
            };
            var w = new NetworkWriter(256);
            ContainerOwnershipMsg.Write(w, entries);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            r.ReadByte();
            var full = ContainerOwnershipMsg.Read(r);
            Assert.IsTrue(full.Full);
            Assert.AreEqual(1, full.Upserts.Count);
            Assert.AreEqual(0, full.Removes.Count);

            w.Reset();
            ContainerOwnershipMsg.Write(w, entries, false, new List<string> { "rt_9", "rt_10" });
            r.Set(w.ToSegment());
            r.ReadByte();
            var delta = ContainerOwnershipMsg.Read(r);
            Assert.IsFalse(delta.Full);
            Assert.AreEqual("arena", delta.Upserts[0].ContainerId);
            CollectionAssert.AreEqual(new[] { "rt_9", "rt_10" }, delta.Removes);
        }
    }
}
