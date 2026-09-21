using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Guardrails on the wire: the baked (static) container reference stays two bytes and the messages that carry
    /// container references keep their layout, whatever else the registry learns to do.
    /// </summary>
    public class WireFormatTests
    {
        private static ContainerRef RoundTrip(ContainerRef r, out int bytes)
        {
            var w = new NetworkWriter(64);
            r.Write(w);
            bytes = w.ToSegment().Count;
            var reader = new NetworkReader();
            reader.Set(w.ToSegment());
            return ContainerRef.Read(reader);
        }

        [Test]
        public void StaticReferenceIsTwoBytesAndRoundTrips()
        {
            var r = new ContainerRef(7);
            Assert.IsTrue(r.IsStatic);
            Assert.IsFalse(r.MayArriveLater);
            var back = RoundTrip(r, out int bytes);
            Assert.AreEqual(2, bytes);
            Assert.AreEqual(r, back);
            Assert.AreEqual(0UL, back.NetId);
            Assert.AreEqual("7", back.ToString());
        }

        [Test]
        public void NoneIsTwoBytes()
        {
            var back = RoundTrip(ContainerRef.None, out int bytes);
            Assert.AreEqual(2, bytes);
            Assert.IsTrue(back.IsNone);
            Assert.IsFalse(back.IsStatic);
        }

        [Test]
        public void DynamicReferenceIsTenBytesAndRoundTrips()
        {
            var r = ContainerRef.Dynamic(0x1234_5678_9ABC_DEF0UL);
            Assert.IsTrue(r.IsDynamic);
            Assert.IsTrue(r.MayArriveLater);
            Assert.IsFalse(r.IsRuntime);
            var back = RoundTrip(r, out int bytes);
            Assert.AreEqual(ContainerRef.MaxWireSize, bytes);
            Assert.AreEqual(r, back);
            Assert.AreEqual(0x1234_5678_9ABC_DEF0UL, back.NetId);
        }

        [Test]
        public void RuntimeReferenceIsTenBytesAndRoundTrips()
        {
            var r = ContainerRef.Runtime(42);
            Assert.IsTrue(r.IsRuntime);
            Assert.IsTrue(r.MayArriveLater);
            Assert.IsFalse(r.IsDynamic);
            Assert.IsFalse(r.IsStatic);
            Assert.AreEqual(42UL, r.RuntimeId);
            var back = RoundTrip(r, out int bytes);
            Assert.AreEqual(ContainerRef.MaxWireSize, bytes);
            Assert.AreEqual(r, back);
            Assert.AreEqual("rt_42", back.ToString());
            // The three marker indices are distinct and leave the dense range below them untouched.
            Assert.AreNotEqual(ContainerRef.DynamicIndex, ContainerRef.RuntimeIndex);
            Assert.AreNotEqual(ContainerRef.NoneIndex, ContainerRef.RuntimeIndex);
            Assert.Less(ContainerRef.RuntimeIndex, ContainerRef.DynamicIndex);
        }

        [Test]
        public void ReferencesWithDifferentPayloadsAreDifferentKeys()
        {
            var set = new HashSet<ContainerRef> { ContainerRef.Dynamic(5), ContainerRef.Runtime(5), new ContainerRef(5), ContainerRef.None };
            Assert.AreEqual(4, set.Count);
            Assert.IsTrue(set.Contains(ContainerRef.Runtime(5)));
        }

        [Test]
        public void EntityStateEntryReservesTheLargestReference()
        {
            Assert.AreEqual(8 + 4 + 10 + 2 + 12 + 16 + 12 + 6, EntityStateEntry.WireSize);
            var entry = new EntityStateEntry { NetId = 9, Epoch = 2, Container = ContainerRef.Runtime(77), LocalPosition = new Vector3(1, 2, 3), LocalRotation = Quaternion.identity, Velocity = Vector3.zero, Fields = TransformFields.Position };
            var w = new NetworkWriter(64);
            entry.Write(w);
            Assert.LessOrEqual(w.ToSegment().Count, EntityStateEntry.WireSize);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            var back = EntityStateEntry.Read(r);
            Assert.AreEqual(ContainerRef.Runtime(77), back.Container);
            Assert.AreEqual(new Vector3(1, 2, 3), back.LocalPosition);
        }

        [Test]
        public void ContainerOwnershipEntriesCarryABoxOnlyWhenTheyHaveOne()
        {
            var entries = new List<ContainerOwnershipEntry>
            {
                new ContainerOwnershipEntry { ContainerIndex = 0, ContainerId = "arena", WorkerIndex = 1, WorkerId = "w1", Epoch = 3, State = LeaseState.Active },
                new ContainerOwnershipEntry { ContainerIndex = ContainerRef.RuntimeIndex, ContainerId = "rt_12", WorkerIndex = 2, WorkerId = "w2", Epoch = 1, State = LeaseState.Active, HasBounds = true, BoundsCenter = new Vector3(32, 0, 96), BoundsSize = new Vector3(64, 512, 64) },
            };
            var w = new NetworkWriter(256);
            ContainerOwnershipMsg.Write(w, entries);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            Assert.AreEqual((byte)MsgId.ContainerOwnership, r.ReadByte());
            var back = ContainerOwnershipMsg.Read(r).Upserts;
            Assert.AreEqual(2, back.Count);
            Assert.IsFalse(back[0].HasBounds);
            Assert.AreEqual("arena", back[0].ContainerId);
            Assert.IsTrue(back[1].HasBounds);
            Assert.AreEqual(new Vector3(32, 0, 96), back[1].BoundsCenter);
            Assert.AreEqual(new Vector3(64, 512, 64), back[1].BoundsSize);
            Assert.AreEqual("w2", back[1].WorkerId);
        }

        [Test]
        public void SpawnPlayerNamesItsContainerByReference()
        {
            var w = new NetworkWriter(64);
            new SpawnPlayerMsg { ClientId = 5, Container = ContainerRef.Runtime(3), Name = "jo", IsBot = true }.Write(w);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            Assert.AreEqual((byte)MsgId.SpawnPlayer, r.ReadByte());
            var back = SpawnPlayerMsg.Read(r);
            Assert.AreEqual(5u, back.ClientId);
            Assert.AreEqual(ContainerRef.Runtime(3), back.Container);
            Assert.AreEqual("jo", back.Name);
            Assert.IsTrue(back.IsBot);
        }
    }
}
