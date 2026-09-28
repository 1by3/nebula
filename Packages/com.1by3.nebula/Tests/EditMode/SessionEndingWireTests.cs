using NUnit.Framework;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula.Tests
{
    /// <summary>
    /// The protocol-22 session endings on the wire (NEB-354): <see cref="GoodbyeMsg"/>, <see cref="KickedMsg"/>,
    /// <see cref="KickPlayerMsg"/>, and the trailing fields of <see cref="GatewayDrainingMsg"/> and
    /// <see cref="DespawnPlayerMsg"/>. A trailing field that is not set is not written, so the bytes an older reader
    /// sees are the bytes it always saw. Pure C#, so the same tests run in <c>Nebula.Services.Tests</c>.
    /// </summary>
    [Category("Conformance")]
    public class SessionEndingWireTests
    {
        private static NetworkReader ReaderAfterId(NetworkWriter w, MsgId expected)
        {
            var r = new NetworkReader(w.ToSegment());
            Assert.AreEqual(expected, (MsgId)r.ReadByte());
            return r;
        }

        [Test]
        public void APlainDrainNoticeIsTheProtocol21Layout()
        {
            var w = new NetworkWriter();
            new GatewayDrainingMsg { ReconnectWithinSeconds = 10 }.Write(w);
            Assert.AreEqual(3, w.Length, "id and u16 only: nothing trailing for a reader that stops there");
            var back = GatewayDrainingMsg.Read(ReaderAfterId(w, MsgId.GatewayDraining));
            Assert.AreEqual(10, back.ReconnectWithinSeconds);
            Assert.IsFalse(back.ServerShutdown);
        }

        [Test]
        public void AShutdownNoticeCarriesTheTrailingFlag()
        {
            var w = new NetworkWriter();
            new GatewayDrainingMsg { ReconnectWithinSeconds = 5, ServerShutdown = true }.Write(w);
            Assert.AreEqual(4, w.Length);
            var r = ReaderAfterId(w, MsgId.GatewayDraining);
            var back = GatewayDrainingMsg.Read(r);
            Assert.AreEqual(5, back.ReconnectWithinSeconds);
            Assert.IsTrue(back.ServerShutdown);
            Assert.AreEqual(0, r.Remaining);
        }

        [Test]
        public void AKickCarriesItsCodeAndReason()
        {
            var w = new NetworkWriter();
            new KickedMsg { Code = 403, Reason = "removed by a moderator" }.Write(w);
            var back = KickedMsg.Read(ReaderAfterId(w, MsgId.Kicked));
            Assert.AreEqual(403, back.Code);
            Assert.AreEqual("removed by a moderator", back.Reason);

            var worker = new NetworkWriter();
            new KickPlayerMsg { ClientId = 0x1234_0000_0007UL, Generation = 9, Code = 2, Reason = "idle" }.Write(worker);
            var kick = KickPlayerMsg.Read(ReaderAfterId(worker, MsgId.KickPlayer));
            Assert.AreEqual(0x1234_0000_0007UL, kick.ClientId);
            Assert.AreEqual(9UL, kick.Generation);
            Assert.AreEqual(2, kick.Code);
            Assert.AreEqual("idle", kick.Reason);
        }

        [Test]
        public void AGoodbyeIsItsIdAlone()
        {
            var w = new NetworkWriter();
            new GoodbyeMsg().Write(w);
            Assert.AreEqual(1, w.Length);
            Assert.AreEqual((byte)MsgId.Goodbye, w.ToArray()[0]);
        }

        [Test]
        public void ADespawnEndsTheSessionOnlyWhenItSaysSo()
        {
            var plain = new NetworkWriter();
            new DespawnPlayerMsg { ClientId = 7, Generation = 3 }.Write(plain);
            Assert.AreEqual(17, plain.Length, "a lost link's despawn is the layout it always was");
            Assert.IsFalse(DespawnPlayerMsg.Read(ReaderAfterId(plain, MsgId.DespawnPlayer)).EndNow);

            var ended = new NetworkWriter();
            new DespawnPlayerMsg { ClientId = 7, Generation = 3, EndNow = true }.Write(ended);
            var back = DespawnPlayerMsg.Read(ReaderAfterId(ended, MsgId.DespawnPlayer));
            Assert.AreEqual((7UL, 3UL, true), (back.ClientId, back.Generation, back.EndNow));
        }

        [Test]
        public void AnEndedSessionTravelsWithItsPawn()
        {
            var msg = new AuthorityTransferMsg
            {
                Entity = new EntitySpawnMsg
                {
                    NetId = 42, PrefabId = 1, OwnerClientId = 7, OwnerIdentity = "", Container = new ContainerRef(0), Epoch = 1,
                    OwnerWorkerIndex = 1, LocalRotation = Quaternion.identity, LocalScale = Vector3.one,
                },
                NewEpoch = 2, PendingInputs = new byte[0], HandoverState = new byte[0], GhostWorkers = new string[0],
                InterestGateways = new string[0],
                SessionGeneration = 3, SessionGateway = "g1#1", SessionOrphan = PlayerSessions.OrphanKind.Ended,
            };
            var w = new NetworkWriter();
            msg.Write(w);
            var back = AuthorityTransferMsg.Read(ReaderAfterId(w, MsgId.AuthorityTransfer));
            Assert.AreEqual(PlayerSessions.OrphanKind.Ended, back.SessionOrphan);
        }
    }
}
