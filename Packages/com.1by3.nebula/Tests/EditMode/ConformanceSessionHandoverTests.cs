using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// A pawn handed over while its player is away (NEB-309). When a player disconnects, the worker keeps the pawn for
    /// <see cref="NebulaConfig.SessionReclaimSeconds"/> and then removes it. If the pawn changes worker inside that
    /// window, the next worker has to know the player is gone and for how much longer to wait, or it keeps the pawn,
    /// and the chunk it stands in, forever. Two real workers on the <see cref="ConformanceMesh"/>; the gateway is the
    /// mesh's recording stub, and the grace is run out by calling the worker's own <c>ExpireSessions</c> with a
    /// later clock.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceSessionHandoverTests
    {
        private const ulong Client = 77;
        private const float Grace = 30f;
        private static readonly MethodInfo ExpireMethod = typeof(NebulaWorker).GetMethod("ExpireSessions", BindingFlags.Instance | BindingFlags.NonPublic);

        private ConformanceMesh _mesh;
        private ConformanceMesh.Gateway _gateway;
        private Container _outdoor;
        private ushort _prefabId;

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            _mesh.Config.SessionReclaimSeconds = Grace;
            _gateway = _mesh.AddGateway("g1");
            _outdoor = _mesh.AddStaticContainer("conformance-outdoor", Vector3.zero, new Vector3(400, 60, 400));
            var prefab = new GameObject("pawn-prefab");
            prefab.AddComponent<NetworkIdentity>();
            _prefabId = _mesh.RegisterPrefab(prefab);
        }

        [TearDown]
        public void TearDown() => _mesh.Dispose();

        private static PlayerSessions SessionsOf(ConformanceMesh.Worker worker) => (PlayerSessions)worker.GetField("_sessions");

        private static PlayerSessions.Session SessionOn(ConformanceMesh.Worker worker)
        {
            Assert.IsTrue(SessionsOf(worker).TryGet(Client, out var session), $"{worker.Id} knows the session");
            return session;
        }

        /// <summary>Run the worker's own reclaim pass as if its clock read <paramref name="now"/>.</summary>
        private static void ExpireAt(ConformanceMesh.Worker worker, double now) => worker.Act(() => ExpireMethod.Invoke(worker.Instance, new object[] { now }));

        /// <summary>The player's pawn on <paramref name="owner"/>, claimed by the gateway with generation 1 as a join would.</summary>
        private NetworkIdentity SpawnPawn(ConformanceMesh.Worker owner)
        {
            NetworkIdentity pawn = null;
            owner.Act(() =>
            {
                pawn = NetworkPrefabs.Instantiate(_prefabId, new Vector3(5f, 1f, 5f), Quaternion.identity, _outdoor.transform);
                owner.Instance.Spawn(pawn, _outdoor, Client);
            });
            Claim(owner, 1, pawn);
            Assume.That(SessionOn(owner).Orphan, Is.EqualTo(PlayerSessions.OrphanKind.None));
            return pawn;
        }

        private void Claim(ConformanceMesh.Worker worker, ulong generation, NetworkIdentity pawn) =>
            _mesh.FromGateway(_gateway, worker, w => new SpawnPlayerMsg { ClientId = Client, Container = pawn.ContainerRef, Name = "pilot", Generation = generation }.Write(w));

        private void PlayerLeaves(ConformanceMesh.Worker worker) =>
            _mesh.FromGateway(_gateway, worker, w => new DespawnPlayerMsg { ClientId = Client, Generation = 1 }.Write(w));

        private static bool Holds(ConformanceMesh.Worker worker, ulong netId) =>
            worker.Find(netId) is NetworkIdentity e && e != null && e.HasAuthority;

        [Test]
        public void APawnHandedOverAfterItsPlayerLeftIsRemovedWhenTheGraceRunsOut()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            ulong netId = pawn.NetId;

            PlayerLeaves(w1);
            Assert.IsTrue(Holds(w1, netId), "the pawn is kept for the reclaim grace");
            // 20 s of the window pass on the first worker before the pawn moves.
            SessionOn(w1).OrphanedAt -= 20;

            w1.Transfer(pawn, w2);
            _mesh.Pump();
            Assert.IsTrue(Holds(w2, netId), "the second worker owns the pawn");

            var session = SessionOn(w2);
            Assert.AreEqual(PlayerSessions.OrphanKind.Released, session.Orphan, "the player is still gone after the handover");
            double now = Time.unscaledTimeAsDouble;
            Assert.AreEqual(10, PlayerSessions.ReclaimRemaining(session, now, Grace), 0.5, "the countdown resumes, it does not restart");

            ExpireAt(w2, now + 9);
            Assert.IsTrue(Holds(w2, netId), "still inside the window");
            ExpireAt(w2, now + 11);
            _mesh.Pump();
            Assert.IsNull(w2.Find(netId), "nobody came back: the pawn is removed");
            Assert.IsFalse(SessionsOf(w2).TryGet(Client, out _));
        }

        [Test]
        public void APlayerWhoReconnectsToTheNewWorkerInTheWindowKeepsThePawn()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            ulong netId = pawn.NetId;

            PlayerLeaves(w1);
            w1.Transfer(pawn, w2);
            _mesh.Pump();
            Assume.That(SessionOn(w2).Orphan, Is.EqualTo(PlayerSessions.OrphanKind.Released));

            // The client connects again, and the gateway claims the pawn from the worker that now owns it.
            int announcedBefore = _mesh.DeliveredOf(MsgId.EntitySpawn, _gateway.Id).Count;
            Claim(w2, 2, pawn);
            _mesh.Pump();

            var session = SessionOn(w2);
            Assert.AreEqual(PlayerSessions.OrphanKind.None, session.Orphan, "the reclaim renews the session");
            Assert.AreEqual(2UL, session.Generation);
            Assert.Greater(_mesh.DeliveredOf(MsgId.EntitySpawn, _gateway.Id).Count, announcedBefore, "the pawn is announced again to the gateway that claimed it");
            ExpireAt(w2, Time.unscaledTimeAsDouble + Grace * 3);
            Assert.IsTrue(Holds(w2, netId), "a reclaimed pawn is never expired");
        }

        [Test]
        public void ALeaveThatReachesTheNewWorkerBeforeTheHandoverIsNotLost()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            ulong netId = pawn.NetId;

            // The gateway followed the redirect of a handover still in flight: the leave reaches the new worker
            // first, and the handover, sent while the player was connected, arrives after it.
            w1.Transfer(pawn, w2);
            PlayerLeaves(w2);
            _mesh.Pump();
            Assert.IsTrue(Holds(w2, netId));
            Assert.AreEqual(PlayerSessions.OrphanKind.Released, SessionOn(w2).Orphan, "the handover does not undo the leave");

            ExpireAt(w2, Time.unscaledTimeAsDouble + Grace + 1);
            _mesh.Pump();
            Assert.IsNull(w2.Find(netId), "the pawn is removed after the grace");
        }

        [Test]
        public void AConnectedPlayersPawnCrossesWithoutACountdown()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            ulong netId = pawn.NetId;

            w1.Transfer(pawn, w2);
            _mesh.Pump();
            var transfer = _mesh.DeliveredOf(MsgId.AuthorityTransfer, w2.Id)[0].Read(AuthorityTransferMsg.Read);
            Assert.AreEqual(PlayerSessions.OrphanKind.None, transfer.SessionOrphan);
            Assert.AreEqual(_gateway.Key, transfer.SessionGateway);
            Assert.AreEqual(PlayerSessions.OrphanKind.None, SessionOn(w2).Orphan);
            ExpireAt(w2, Time.unscaledTimeAsDouble + Grace * 3);
            Assert.IsTrue(Holds(w2, netId));
        }
    }
}
