using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="PlayerSessions"/>, the worker's record of which gateway speaks for each player and how long a
    /// disconnected player's pawn is kept. Pure C#, so the same tests run in <c>Nebula.Services.Tests</c>. The
    /// handover leg, two real workers passing an orphaned session, is <c>ConformanceSessionHandoverTests</c>.
    /// </summary>
    public class PlayerSessionsTests
    {
        private const double Grace = 30;
        private const string G1 = "g1#1";

        private static List<ulong> Expire(PlayerSessions sessions, double now)
        {
            var expired = new List<ulong>();
            sessions.Expire(now, Grace, expired);
            return expired;
        }

        [Test]
        public void AReleasedSessionExpiresAfterTheGrace()
        {
            var sessions = new PlayerSessions();
            sessions.Register(7, 1, G1);
            Assert.IsTrue(sessions.Release(7, 1, 100));
            Assert.IsTrue(sessions.TryGet(7, out var s));
            Assert.AreEqual(PlayerSessions.OrphanKind.Released, s.Orphan);
            Assert.AreEqual(10, PlayerSessions.ReclaimRemaining(s, 120, Grace), 1e-9);
            CollectionAssert.IsEmpty(Expire(sessions, 129));
            CollectionAssert.AreEqual(new[] { 7UL }, Expire(sessions, 130));
        }

        [Test]
        public void TheSameGatewayComingBackDoesNotRenewASessionItReleased()
        {
            var sessions = new PlayerSessions();
            sessions.Register(7, 1, G1);
            sessions.Register(8, 1, G1);
            sessions.Release(7, 1, 100);
            sessions.GatewayLost(G1, 105);
            sessions.GatewayReturned(G1);
            Assert.IsTrue(sessions.TryGet(7, out var released));
            Assert.AreEqual(PlayerSessions.OrphanKind.Released, released.Orphan, "the client left; a link blip does not bring it back");
            Assert.IsTrue(sessions.TryGet(8, out var connected));
            Assert.AreEqual(PlayerSessions.OrphanKind.None, connected.Orphan);
            CollectionAssert.AreEqual(new[] { 7UL }, Expire(sessions, 130));
        }

        [Test]
        public void AnAdoptedOrphanResumesItsCountdownOnTheReceiversClock()
        {
            // The sender had 12 s left; the receiver's clock reads something unrelated.
            var sessions = new PlayerSessions();
            sessions.Adopt(7, 1, G1, PlayerSessions.OrphanKind.Released, 12, 5000, Grace);
            Assert.IsTrue(sessions.TryGet(7, out var s));
            Assert.AreEqual(PlayerSessions.OrphanKind.Released, s.Orphan);
            Assert.AreEqual(12, PlayerSessions.ReclaimRemaining(s, 5000, Grace), 1e-9);
            CollectionAssert.IsEmpty(Expire(sessions, 5011.9));
            CollectionAssert.AreEqual(new[] { 7UL }, Expire(sessions, 5012));
        }

        [Test]
        public void AnAdoptedOrphanIsReclaimedByANewerClaim()
        {
            var sessions = new PlayerSessions();
            sessions.Adopt(7, 1, G1, PlayerSessions.OrphanKind.Released, 12, 5000, Grace);
            Assert.AreEqual(PlayerSessions.Claim.Reclaimed, sessions.Register(7, 2, "g2#1"));
            Assert.IsTrue(sessions.TryGet(7, out var s));
            Assert.AreEqual(PlayerSessions.OrphanKind.None, s.Orphan);
            CollectionAssert.IsEmpty(Expire(sessions, 9999));
        }

        [Test]
        public void AnOrphanWhoseGatewayLinkWasLostIsRenewedWhenThatGatewayLinksHere()
        {
            var sessions = new PlayerSessions();
            sessions.Adopt(7, 1, G1, PlayerSessions.OrphanKind.GatewayLost, 20, 50, Grace);
            Assert.IsTrue(sessions.TryGet(7, out var s));
            Assert.AreEqual(PlayerSessions.OrphanKind.GatewayLost, s.Orphan);
            sessions.GatewayReturned(G1);
            Assert.AreEqual(PlayerSessions.OrphanKind.None, s.Orphan);
        }

        [Test]
        public void AReleaseHeardBeforeTheHandoverIsNotUndoneByIt()
        {
            // The gateway followed the pawn's redirect and told the new worker the player left before the old
            // worker's handover, which still thinks the player connected, arrived.
            var sessions = new PlayerSessions();
            Assert.IsTrue(sessions.Release(7, 1, 100));
            sessions.Adopt(7, 1, G1, PlayerSessions.OrphanKind.None, 0, 100.5, Grace);
            Assert.IsTrue(sessions.TryGet(7, out var s));
            Assert.AreEqual(PlayerSessions.OrphanKind.Released, s.Orphan);
            Assert.AreEqual(G1, s.Gateway);
            CollectionAssert.AreEqual(new[] { 7UL }, Expire(sessions, 130));
        }

        [Test]
        public void AnAdoptionFromAnOlderGenerationChangesNothing()
        {
            var sessions = new PlayerSessions();
            sessions.Register(7, 2, "g2#1");
            sessions.Adopt(7, 1, G1, PlayerSessions.OrphanKind.Released, 1, 100, Grace);
            Assert.IsTrue(sessions.TryGet(7, out var s));
            Assert.AreEqual(2UL, s.Generation);
            Assert.AreEqual("g2#1", s.Gateway);
            Assert.AreEqual(PlayerSessions.OrphanKind.None, s.Orphan);
        }

        [Test]
        public void AConnectedAdoptionStillRenewsASessionOfTheSameGeneration()
        {
            var sessions = new PlayerSessions();
            sessions.Register(7, 1, G1);
            sessions.GatewayLost(G1, 100);
            sessions.Adopt(7, 1, G1);
            Assert.IsTrue(sessions.TryGet(7, out var s));
            Assert.AreEqual(PlayerSessions.OrphanKind.None, s.Orphan, "only a release is kept; a lost link is not news the sender lacked");
        }
    }
}
