using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Deliberate session endings on the worker (NEB-354). A goodbye or a kick ends the session at once: the game's
    /// <see cref="NebulaGameMode.OnPlayerDespawn"/> runs and the pawn goes without waiting out
    /// <see cref="NebulaConfig.SessionReclaimSeconds"/>, while a lost link still gets the whole grace. The end follows
    /// the pawn across a handover in either order, and a kick asked of the worker reaches the gateway that speaks for
    /// the session. Two real workers on the <see cref="ConformanceMesh"/> with its recording gateway stub; the gateway
    /// half is the service suite's <c>ConformanceSessionEndingTests</c>.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceSessionEndingTests
    {
        private const ulong Client = 77;
        private const float Grace = 30f;
        private static readonly MethodInfo ExpireMethod = typeof(NebulaWorker).GetMethod("ExpireSessions", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo GameModeField = typeof(NebulaWorker).GetField("_gameMode", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>A game mode that writes down every hook the worker calls, and on which worker.</summary>
        private sealed class RecordingGameMode : NebulaGameMode
        {
            public readonly List<string> Calls = new List<string>();
            public override void OnPlayerDespawn(NebulaWorker worker, NetworkIdentity player) => Calls.Add($"despawn {worker.WorkerId} {player.OwnerClientId}");
        }

        private ConformanceMesh _mesh;
        private ConformanceMesh.Gateway _gateway;
        private Container _outdoor;
        private ushort _prefabId;
        private GameObject _gameModeHost;
        private RecordingGameMode _game;

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
            _gameModeHost = new GameObject("game-mode");
            _game = _gameModeHost.AddComponent<RecordingGameMode>();
            foreach (var w in _mesh.Workers) GameModeField.SetValue(w.Instance, _game);
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            Object.DestroyImmediate(_gameModeHost);
        }

        private static PlayerSessions SessionsOf(ConformanceMesh.Worker worker) => (PlayerSessions)worker.GetField("_sessions");

        private static void ExpireAt(ConformanceMesh.Worker worker, double now) => worker.Act(() => ExpireMethod.Invoke(worker.Instance, new object[] { now }));

        private NetworkIdentity SpawnPawn(ConformanceMesh.Worker owner)
        {
            NetworkIdentity pawn = null;
            owner.Act(() =>
            {
                pawn = NetworkPrefabs.Instantiate(_prefabId, new Vector3(5f, 1f, 5f), Quaternion.identity, _outdoor.transform);
                owner.Instance.Spawn(pawn, _outdoor, Client);
            });
            _mesh.FromGateway(_gateway, owner, w => new SpawnPlayerMsg { ClientId = Client, Container = pawn.ContainerRef, Name = "pilot", Generation = 1 }.Write(w));
            return pawn;
        }

        private void Despawn(ConformanceMesh.Worker worker, bool endNow) =>
            _mesh.FromGateway(_gateway, worker, w => new DespawnPlayerMsg { ClientId = Client, Generation = 1, EndNow = endNow }.Write(w));

        private static bool Holds(ConformanceMesh.Worker worker, ulong netId) =>
            worker.Find(netId) is NetworkIdentity e && e != null && e.HasAuthority;

        [Test]
        public void AGoodbyeRemovesThePawnAtOnceAndTellsTheGame()
        {
            var w1 = _mesh[0];
            ulong netId = SpawnPawn(w1).NetId;

            Despawn(w1, endNow: true);
            _mesh.Pump();
            Assert.IsNull(w1.Find(netId), "no reclaim grace for a player who left on purpose");
            Assert.AreEqual(new[] { $"despawn w1 {Client}" }, _game.Calls);
            Assert.IsFalse(SessionsOf(w1).TryGet(Client, out _), "the session is gone");
        }

        [Test]
        public void ALostLinkStillKeepsThePawnForTheGrace()
        {
            var w1 = _mesh[0];
            ulong netId = SpawnPawn(w1).NetId;

            Despawn(w1, endNow: false);
            _mesh.Pump();
            Assert.IsTrue(Holds(w1, netId));
            CollectionAssert.IsEmpty(_game.Calls);
            ExpireAt(w1, Time.unscaledTimeAsDouble + Grace - 1);
            Assert.IsTrue(Holds(w1, netId), "still inside the grace");
        }

        [Test]
        public void AGoodbyeThatReachesTheNewWorkerFirstRemovesThePawnWhenItArrives()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            ulong netId = pawn.NetId;

            // The gateway followed the redirect of a handover still in flight, and the player left.
            w1.Transfer(pawn, w2);
            Despawn(w2, endNow: true);
            _mesh.Pump();
            Assert.AreEqual(PlayerSessions.OrphanKind.Ended, SessionsOf(w2).TryGet(Client, out var s) ? s.Orphan : PlayerSessions.OrphanKind.None,
                "the handover does not undo the end");
            ExpireAt(w2, Time.unscaledTimeAsDouble);
            _mesh.Pump();
            Assert.IsNull(w2.Find(netId), "removed on the next pass, not after the grace");
            Assert.AreEqual(new[] { $"despawn w2 {Client}" }, _game.Calls);
        }

        [Test]
        public void AGoodbyeSentToTheWorkerThePawnLeftFollowsIt()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            ulong netId = pawn.NetId;
            w1.Transfer(pawn, w2);
            _mesh.Pump();
            Assume.That(Holds(w2, netId), Is.True);

            Despawn(w1, endNow: true);
            _mesh.Pump();
            Assert.IsNull(w2.Find(netId), "the old owner forwards the end and the new owner acts on it at once");
            Assert.AreEqual(new[] { $"despawn w2 {Client}" }, _game.Calls);
        }

        [Test]
        public void AKickTellsTheSessionsGatewayAndEndsTheSession()
        {
            _mesh.LinkGateway(_gateway);
            var w1 = _mesh[0];
            ulong netId = SpawnPawn(w1).NetId;

            bool kicked = false;
            w1.Act(() => kicked = w1.Instance.Kick(Client, 17, "left the arena bounds"));
            Assert.IsTrue(kicked);
            _mesh.Pump();
            var sent = _mesh.DeliveredOf(MsgId.KickPlayer, _gateway.Id);
            Assert.AreEqual(1, sent.Count, "the gateway that speaks for the session is told");
            var msg = sent[0].Read(KickPlayerMsg.Read);
            Assert.AreEqual((Client, 1UL, (ushort)17, "left the arena bounds"), (msg.ClientId, msg.Generation, msg.Code, msg.Reason));
            Assert.IsNull(w1.Find(netId), "and the session ends here without waiting for it");
            Assert.AreEqual(new[] { $"despawn w1 {Client}" }, _game.Calls);

            bool unknown = true;
            w1.Act(() => unknown = w1.Instance.Kick(12345, 0, ""));
            Assert.IsFalse(unknown, "a session this worker never heard of");
        }

        [Test]
        public void AKickAskedOfTheWorkerThePawnLeftEndsItOnTheNewOwner()
        {
            _mesh.LinkGateway(_gateway);
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            ulong netId = pawn.NetId;
            w1.Transfer(pawn, w2);
            _mesh.Pump();
            Assume.That(Holds(w2, netId), Is.True);

            w1.Act(() => w1.Instance.Kick(pawn, 4, "vote kick"));
            _mesh.Pump();
            Assert.AreEqual(1, _mesh.DeliveredOf(MsgId.KickPlayer, _gateway.Id).Count);
            Assert.IsNull(w2.Find(netId));
            Assert.AreEqual(new[] { $"despawn w2 {Client}" }, _game.Calls);
        }
    }
}
