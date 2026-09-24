using NUnit.Framework;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 8, wire leg (see <c>docs/conformance-suite.md</c>): every field of an
    /// <see cref="AuthorityTransferMsg"/> survives its own writer and reader. Pure C#, so the same test runs in the
    /// Unity package and in <c>Nebula.Services.Tests</c>: the standalone gateway and the worker must agree on these
    /// bytes, and a field that survives the writer but not the reader is a handover that silently loses state.
    /// The worker-side leg (build, apply, hooks) is <c>ConformanceHandoverStateTests</c>, Unity only.
    /// </summary>
    [Category("Conformance")]
    public class ConformanceHandoverWireTests
    {
        private static AuthorityTransferMsg Loaded() => new AuthorityTransferMsg
        {
            Entity = new EntitySpawnMsg
            {
                NetId = (1UL << 48) | 7,
                PrefabId = 3,
                OwnerClientId = 0,
                OwnerIdentity = "",
                Container = new ContainerRef(2),
                Epoch = 5,
                OwnerWorkerIndex = 2,
                LocalPosition = new Vector3(12f, 0.5f, -7f),
                LocalRotation = Quaternion.Euler(0f, 90f, 0f),
                LocalScale = new Vector3(1f, 2f, 1.5f),
                Velocity = new Vector3(1.5f, 0f, -2f),
                Flags = EntityFlags.ServerDriven,
                SceneId = 0,
                Vars = new byte[] { 1, 0, 42, 0, 0, 0 },
                State = new byte[] { 1, 1, 1, 3, 0, 9, 9, 9 },
                RelevanceRadius = 200f,
                InterestFlags = EntityInterestFlags.None,
                InterestGroup = 4,
            },
            NewEpoch = 6,
            PendingInputs = new byte[0],
            HandoverState = new byte[] { 2, 8, 0, 0, 0, 160, 63, 239, 190, 173, 222, 0, 0 },
            GhostWorkers = new[] { "w3", "w1" },
            SessionGeneration = 0,
            SessionGateway = "",
            InterestGateways = new string[0],
        };

        private static AuthorityTransferMsg RoundTrip(AuthorityTransferMsg msg, out int remaining)
        {
            var w = new NetworkWriter(512);
            msg.Write(w);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            Assert.AreEqual((byte)MsgId.AuthorityTransfer, r.ReadByte());
            var back = AuthorityTransferMsg.Read(r);
            remaining = r.Remaining;
            return back;
        }

        [Test]
        public void EveryFieldOfAServerDrivenTransferSurvivesTheWire()
        {
            var msg = Loaded();
            var back = RoundTrip(msg, out int remaining);
            Assert.AreEqual(0, remaining, "the reader consumed exactly what the writer produced");

            var e = msg.Entity; var b = back.Entity;
            Assert.AreEqual(e.NetId, b.NetId);
            Assert.AreEqual(e.PrefabId, b.PrefabId);
            Assert.AreEqual(e.OwnerClientId, b.OwnerClientId);
            Assert.AreEqual(e.OwnerIdentity, b.OwnerIdentity);
            Assert.AreEqual(e.Container, b.Container);
            Assert.AreEqual(e.Epoch, b.Epoch);
            Assert.AreEqual(e.OwnerWorkerIndex, b.OwnerWorkerIndex);
            Assert.AreEqual(e.LocalPosition, b.LocalPosition);
            Assert.AreEqual(e.LocalRotation, b.LocalRotation);
            Assert.AreEqual(e.LocalScale, b.LocalScale);
            Assert.AreEqual(e.Velocity, b.Velocity);
            Assert.AreEqual(EntityFlags.ServerDriven, b.Flags, "the server-driven flag is what makes the receiver mark the entity");
            Assert.AreEqual(e.SceneId, b.SceneId);
            CollectionAssert.AreEqual(e.Vars, b.Vars);
            CollectionAssert.AreEqual(e.State, b.State);
            Assert.AreEqual(e.RelevanceRadius, b.RelevanceRadius, 1f, "the radius travels as an f16");
            Assert.AreEqual(e.InterestFlags, b.InterestFlags);
            Assert.AreEqual(e.InterestGroup, b.InterestGroup);

            Assert.AreEqual(msg.NewEpoch, back.NewEpoch);
            CollectionAssert.AreEqual(msg.PendingInputs, back.PendingInputs);
            CollectionAssert.AreEqual(msg.HandoverState, back.HandoverState);
            CollectionAssert.AreEqual(msg.GhostWorkers, back.GhostWorkers);
            Assert.AreEqual(msg.SessionGeneration, back.SessionGeneration);
            Assert.AreEqual(msg.SessionGateway, back.SessionGateway);
            CollectionAssert.AreEqual(msg.InterestGateways, back.InterestGateways);
        }

        [Test]
        public void AnOwnedTransferCarriesTheSessionAndItsFollowers()
        {
            var msg = Loaded();
            msg.Entity.OwnerClientId = 0xABCDEF;
            msg.Entity.OwnerIdentity = "player-17";
            msg.Entity.Flags = EntityFlags.None;
            msg.PendingInputs = new byte[] { 9, 8, 7 };
            msg.SessionGeneration = 3;
            msg.SessionGateway = "g1#1a2b3c4d";
            msg.InterestGateways = new[] { "g2#00000001" };

            var back = RoundTrip(msg, out int remaining);
            Assert.AreEqual(0, remaining);
            Assert.AreEqual(0xABCDEFUL, back.Entity.OwnerClientId);
            Assert.AreEqual("player-17", back.Entity.OwnerIdentity);
            Assert.AreEqual(EntityFlags.None, back.Entity.Flags);
            CollectionAssert.AreEqual(msg.PendingInputs, back.PendingInputs);
            Assert.AreEqual(3UL, back.SessionGeneration);
            Assert.AreEqual("g1#1a2b3c4d", back.SessionGateway);
            CollectionAssert.AreEqual(msg.InterestGateways, back.InterestGateways);
        }

        [Test]
        public void AnOrphanedSessionTravelsWithTheTimeItHadLeft()
        {
            var msg = Loaded();
            msg.Entity.OwnerClientId = 0xABCDEF;
            msg.SessionGeneration = 3;
            msg.SessionGateway = "g1#1a2b3c4d";
            msg.SessionOrphan = PlayerSessions.OrphanKind.Released;
            msg.SessionReclaimRemaining = 12.5f;

            var back = RoundTrip(msg, out int remaining);
            Assert.AreEqual(0, remaining);
            Assert.IsFalse(back.Crossing, "the crossing byte is written as 0 to reach the section after it");
            Assert.AreEqual(PlayerSessions.OrphanKind.Released, back.SessionOrphan);
            Assert.AreEqual(12.5f, back.SessionReclaimRemaining);

            msg.Crossing = true;
            msg.SessionOrphan = PlayerSessions.OrphanKind.GatewayLost;
            back = RoundTrip(msg, out remaining);
            Assert.AreEqual(0, remaining);
            Assert.IsTrue(back.Crossing);
            Assert.AreEqual(PlayerSessions.OrphanKind.GatewayLost, back.SessionOrphan);
        }

        [Test]
        public void AConnectedSessionAddsNoBytes()
        {
            var msg = Loaded();
            var w = new NetworkWriter(512);
            msg.Write(w);
            int plain = w.Length;
            msg.SessionReclaimRemaining = 7f; // ignored while the session is not an orphan
            w = new NetworkWriter(512);
            msg.Write(w);
            Assert.AreEqual(plain, w.Length, "the trailing section is written only for an orphan");

            var back = RoundTrip(msg, out int remaining);
            Assert.AreEqual(0, remaining);
            Assert.AreEqual(PlayerSessions.OrphanKind.None, back.SessionOrphan);
            Assert.AreEqual(0f, back.SessionReclaimRemaining);
        }

        [Test]
        public void NullBlobsAndListsReadBackEmptyNotNull()
        {
            var msg = Loaded();
            msg.PendingInputs = null;
            msg.HandoverState = null;
            msg.GhostWorkers = null;
            msg.SessionGateway = null;
            msg.InterestGateways = null;
            msg.Entity.Vars = null;
            msg.Entity.State = null;
            msg.Entity.OwnerIdentity = null;

            var back = RoundTrip(msg, out int remaining);
            Assert.AreEqual(0, remaining);
            Assert.IsNotNull(back.PendingInputs); Assert.AreEqual(0, back.PendingInputs.Length);
            Assert.IsNotNull(back.HandoverState); Assert.AreEqual(0, back.HandoverState.Length);
            Assert.IsNotNull(back.GhostWorkers); Assert.AreEqual(0, back.GhostWorkers.Length);
            Assert.IsNotNull(back.InterestGateways); Assert.AreEqual(0, back.InterestGateways.Length);
            Assert.AreEqual("", back.SessionGateway);
            Assert.IsNotNull(back.Entity.Vars); Assert.AreEqual(0, back.Entity.Vars.Length);
            Assert.IsNotNull(back.Entity.State); Assert.AreEqual(0, back.Entity.State.Length);
            Assert.AreEqual("", back.Entity.OwnerIdentity);
        }
    }
}
