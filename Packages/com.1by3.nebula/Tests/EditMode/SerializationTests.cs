using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public class SerializationTests
    {
        [Test]
        public void PrimitivesRoundTrip()
        {
            var w = new NetworkWriter(8);
            w.WriteByte(7);
            w.WriteBool(true);
            w.WriteUShort(65000);
            w.WriteInt(-12345);
            w.WriteUInt(4000000000u);
            w.WriteULong(ulong.MaxValue - 5);
            w.WriteFloat(-1.25f);
            w.WriteDouble(3.5e10);
            w.WriteString("héllo");
            w.WriteString(null);
            w.WriteVector3(new Vector3(1, 2, 3));
            w.WriteQuaternion(Quaternion.Euler(10, 20, 30));
            w.WriteBytes(new byte[] { 1, 2, 3 });

            var r = new NetworkReader(w.ToSegment());
            Assert.AreEqual(7, r.ReadByte());
            Assert.IsTrue(r.ReadBool());
            Assert.AreEqual(65000, r.ReadUShort());
            Assert.AreEqual(-12345, r.ReadInt());
            Assert.AreEqual(4000000000u, r.ReadUInt());
            Assert.AreEqual(ulong.MaxValue - 5, r.ReadULong());
            Assert.AreEqual(-1.25f, r.ReadFloat());
            Assert.AreEqual(3.5e10, r.ReadDouble());
            Assert.AreEqual("héllo", r.ReadString());
            Assert.IsNull(r.ReadString());
            Assert.AreEqual(new Vector3(1, 2, 3), r.ReadVector3());
            var q = r.ReadQuaternion();
            Assert.Less(Quaternion.Angle(q, Quaternion.Euler(10, 20, 30)), 0.001f);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, r.ReadBytes());
            Assert.AreEqual(0, r.Remaining);
        }

        [Test]
        public void ReadingPastTheEndThrows()
        {
            var r = new NetworkReader(new byte[] { 1 });
            r.ReadByte();
            Assert.Throws<System.InvalidOperationException>(() => r.ReadUInt());
        }

        [Test]
        public void ReservedCountIsPatched()
        {
            var w = new NetworkWriter();
            int slot = w.ReserveUShort();
            w.WriteByte(1);
            w.WriteByte(2);
            w.PatchUShort(slot, 2);
            var r = new NetworkReader(w.ToSegment());
            Assert.AreEqual(2, r.ReadUShort());
        }

        private enum Weapon { Rifle = 3, Pistol = 9 }

        [Test]
        public void TypedSerializationHandlesEnumsAndUnityTypes()
        {
            var w = new NetworkWriter();
            NetworkSerialization.Write(w, Weapon.Pistol);
            NetworkSerialization.Write(w, new Color(0.1f, 0.2f, 0.3f, 1f));
            NetworkSerialization.Write(w, "x");
            var r = new NetworkReader(w.ToSegment());
            Assert.AreEqual(Weapon.Pistol, NetworkSerialization.Read<Weapon>(r));
            Assert.AreEqual(new Color(0.1f, 0.2f, 0.3f, 1f), NetworkSerialization.Read<Color>(r));
            Assert.AreEqual("x", NetworkSerialization.Read<string>(r));
        }

        [Test]
        public void EntitySpawnMessageRoundTrips()
        {
            var msg = new EntitySpawnMsg
            {
                NetId = (1UL << 48) | 42,
                PrefabId = 3,
                OwnerClientId = 9,
                ContainerIndex = 2,
                Epoch = 5,
                OwnerWorkerIndex = 1,
                LocalPosition = new Vector3(1, 0, -2),
                LocalRotation = Quaternion.identity,
                Velocity = new Vector3(0, 1, 0),
                Flags = EntityFlags.ServerDriven,
                SceneId = 0xC0FFEE,
                Vars = new byte[] { 9, 8, 7 },
            };
            var w = new NetworkWriter();
            msg.Write(w, MsgId.GhostSpawn);
            var r = new NetworkReader(w.ToSegment());
            Assert.AreEqual(MsgId.GhostSpawn, (MsgId)r.ReadByte());
            var back = EntitySpawnMsg.Read(r);
            Assert.AreEqual(msg.NetId, back.NetId);
            Assert.AreEqual(msg.Epoch, back.Epoch);
            Assert.AreEqual(msg.OwnerClientId, back.OwnerClientId);
            Assert.AreEqual(EntityFlags.ServerDriven, back.Flags);
            Assert.AreEqual(0xC0FFEEu, back.SceneId);
            Assert.AreEqual(msg.LocalPosition, back.LocalPosition);
            CollectionAssert.AreEqual(msg.Vars, back.Vars);
        }

        [Test]
        public void AuthorityTransferMessageCarriesHandoverState()
        {
            var msg = new AuthorityTransferMsg
            {
                Entity = new EntitySpawnMsg { NetId = 7, PrefabId = 1, LocalRotation = Quaternion.identity },
                NewEpoch = 3,
                PendingInputs = new byte[] { 1, 2 },
                HandoverState = new byte[] { 5, 6, 7, 8 },
            };
            var w = new NetworkWriter();
            msg.Write(w);
            var r = new NetworkReader(w.ToSegment());
            Assert.AreEqual(MsgId.AuthorityTransfer, (MsgId)r.ReadByte());
            var back = AuthorityTransferMsg.Read(r);
            Assert.AreEqual(3u, back.NewEpoch);
            CollectionAssert.AreEqual(msg.PendingInputs, back.PendingInputs);
            CollectionAssert.AreEqual(msg.HandoverState, back.HandoverState);
            Assert.AreEqual(0, r.Remaining);
        }

        [Test]
        public void ReadSegmentIsBoundedAndAdvances()
        {
            var r = new NetworkReader(new byte[] { 1, 2, 3, 4 });
            r.ReadByte();
            var seg = r.ReadSegment(2);
            CollectionAssert.AreEqual(new byte[] { 2, 3 }, seg);
            Assert.AreEqual(4, r.ReadByte());
            Assert.Throws<System.InvalidOperationException>(() => r.ReadSegment(1));
            var sub = new NetworkReader(seg);
            sub.ReadUShort();
            Assert.Throws<System.InvalidOperationException>(() => sub.ReadByte());
        }

        [Test]
        public void SkipAdvancesAndIsBoundsChecked()
        {
            var r = new NetworkReader(new byte[] { 1, 2, 3 });
            r.Skip(2);
            Assert.AreEqual(3, r.ReadByte());
            Assert.Throws<System.InvalidOperationException>(() => r.Skip(1));
        }
    }
}
