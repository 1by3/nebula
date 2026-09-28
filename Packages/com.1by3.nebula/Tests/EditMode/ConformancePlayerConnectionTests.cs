using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The worker's side of a player dropping and coming back (NEB-356): <see cref="NebulaGameMode.OnPlayerDisconnected"/>
    /// when the reclaim grace starts, from a client's lost link or from the gateway's own link to the worker,
    /// <see cref="NebulaGameMode.OnPlayerReconnected"/> when the session is reclaimed in time, and the replicated
    /// <see cref="NetworkIdentity.IsOwnerConnected"/> the gateways and ghost workers are told of. The hooks follow the
    /// pawn across a handover during the grace, in either order, and are called once per drop; a goodbye goes straight
    /// to <see cref="NebulaGameMode.OnPlayerDespawn"/>. Two real workers on the <see cref="ConformanceMesh"/> with
    /// its recording gateway stub.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformancePlayerConnectionTests
    {
        private const ulong Client = 77;
        private const float Grace = 30f;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly MethodInfo ExpireMethod = typeof(NebulaWorker).GetMethod("ExpireSessions", Hidden);
        private static readonly MethodInfo PeerLostMethod = typeof(NebulaWorker).GetMethod("OnPeerLost", Hidden);
        private static readonly FieldInfo GameModeField = typeof(NebulaWorker).GetField("_gameMode", Hidden);

        private sealed class RecordingGameMode : NebulaGameMode
        {
            public readonly List<string> Calls = new List<string>();
            public override void OnPlayerDisconnected(NebulaWorker worker, NetworkIdentity player) =>
                Calls.Add($"disconnected {worker.WorkerId} connected={player.IsOwnerConnected}");
            public override void OnPlayerReconnected(NebulaWorker worker, NetworkIdentity player) =>
                Calls.Add($"reconnected {worker.WorkerId} connected={player.IsOwnerConnected}");
            public override void OnPlayerDespawn(NebulaWorker worker, NetworkIdentity player) => Calls.Add($"despawn {worker.WorkerId}");
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

        private static void ExpireAt(ConformanceMesh.Worker worker, double now) => worker.Act(() => ExpireMethod.Invoke(worker.Instance, new object[] { now }));

        private NetworkIdentity SpawnPawn(ConformanceMesh.Worker owner)
        {
            NetworkIdentity pawn = null;
            owner.Act(() =>
            {
                pawn = NetworkPrefabs.Instantiate(_prefabId, new Vector3(5f, 1f, 5f), Quaternion.identity, _outdoor.transform);
                owner.Instance.Spawn(pawn, _outdoor, Client);
            });
            Claim(owner, 1, pawn);
            return pawn;
        }

        private void Claim(ConformanceMesh.Worker worker, ulong generation, NetworkIdentity pawn) =>
            _mesh.FromGateway(_gateway, worker, w => new SpawnPlayerMsg { ClientId = Client, Container = pawn.ContainerRef, Name = "pilot", Generation = generation }.Write(w));

        private void PlayerLeaves(ConformanceMesh.Worker worker, bool endNow = false) =>
            _mesh.FromGateway(_gateway, worker, w => new DespawnPlayerMsg { ClientId = Client, Generation = 1, EndNow = endNow }.Write(w));

        /// <summary>The newest announcement of the pawn a gateway was sent, as it would relay it to its clients.</summary>
        private EntitySpawnMsg LastAnnouncement(ulong netId)
        {
            var spawns = _mesh.DeliveredOf(MsgId.EntitySpawn, _gateway.Id);
            for (int i = spawns.Count - 1; i >= 0; i--)
            {
                var msg = spawns[i].Read(r => EntitySpawnMsg.Read(r));
                if (msg.NetId == netId) return msg;
            }
            Assert.Fail($"the gateway was never told of #{netId}");
            return default;
        }

        [Test]
        public void ALostLinkStartsTheGraceWithTheHookAndAReclaimEndsIt()
        {
            _mesh.LinkGateway(_gateway);
            var w1 = _mesh[0];
            var pawn = SpawnPawn(w1);
            Assert.IsTrue(pawn.IsOwnerConnected);
            var changes = new List<bool>();
            pawn.OwnerConnectedChanged += changes.Add;

            PlayerLeaves(w1);
            _mesh.Pump();
            Assert.AreEqual(new[] { "disconnected w1 connected=False" }, _game.Calls, "the hook runs when the grace starts, the flag already set");
            Assert.IsFalse(pawn.IsOwnerConnected);
            Assert.AreEqual(new[] { false }, changes);
            Assert.AreNotEqual(EntityFlags.None, LastAnnouncement(pawn.NetId).Flags & EntityFlags.OwnerDisconnected, "the gateways are told, for their clients");

            Claim(w1, 2, pawn);
            _mesh.Pump();
            Assert.AreEqual(new[] { "disconnected w1 connected=False", "reconnected w1 connected=True" }, _game.Calls);
            Assert.IsTrue(pawn.IsOwnerConnected);
            Assert.AreEqual(EntityFlags.None, LastAnnouncement(pawn.NetId).Flags & EntityFlags.OwnerDisconnected);
        }

        [Test]
        public void AGraceThatRunsOutEndsWithOnPlayerDespawn()
        {
            var w1 = _mesh[0];
            ulong netId = SpawnPawn(w1).NetId;
            PlayerLeaves(w1);
            ExpireAt(w1, Time.unscaledTimeAsDouble + Grace + 1);
            _mesh.Pump();
            Assert.IsNull(w1.Find(netId));
            Assert.AreEqual(new[] { "disconnected w1 connected=False", "despawn w1" }, _game.Calls);
        }

        [Test]
        public void AGoodbyeGoesStraightToOnPlayerDespawn()
        {
            var w1 = _mesh[0];
            SpawnPawn(w1);
            PlayerLeaves(w1, endNow: true);
            _mesh.Pump();
            Assert.AreEqual(new[] { "despawn w1" }, _game.Calls, "a player who left on purpose is not a disconnection");
        }

        [Test]
        public void TheGatewaysLinkDroppingIsADisconnectionUntilItComesBack()
        {
            _mesh.LinkGateway(_gateway);
            var w1 = _mesh[0];
            var pawn = SpawnPawn(w1);
            var gatewayPeer = w1.PeersById[_gateway.Id];

            w1.Act(() => PeerLostMethod.Invoke(w1.Instance, new[] { gatewayPeer }));
            Assert.AreEqual(new[] { "disconnected w1 connected=False" }, _game.Calls);
            Assert.IsFalse(pawn.IsOwnerConnected);

            // The same gateway (same incarnation) links again: its sessions are renewed.
            _mesh.FromGateway(_gateway, w1, w => new HelloMsg { Role = PeerRole.Gateway, Id = _gateway.Id, Incarnation = 1 }.Write(w));
            Assert.AreEqual(new[] { "disconnected w1 connected=False", "reconnected w1 connected=True" }, _game.Calls);
            Assert.IsTrue(pawn.IsOwnerConnected);
        }

        [Test]
        public void TheHooksFollowThePawnAcrossAHandoverDuringTheGrace()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            ulong netId = pawn.NetId;

            PlayerLeaves(w1);
            w1.Transfer(pawn, w2);
            _mesh.Pump();
            var moved = w2.Find(netId);
            Assert.IsTrue(moved != null && moved.HasAuthority);
            Assert.IsFalse(moved.IsOwnerConnected, "the flag travels with the pawn");
            Assert.AreEqual(new[] { "disconnected w1 connected=False" }, _game.Calls, "once per drop: the new owner does not call it again");

            Claim(w2, 2, moved);
            _mesh.Pump();
            Assert.AreEqual(new[] { "disconnected w1 connected=False", "reconnected w2 connected=True" }, _game.Calls, "the new owner reports the return");
        }

        [Test]
        public void ADropThatReachesTheNewWorkerFirstIsReportedThere()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            ulong netId = pawn.NetId;

            // The handover was sent while the player was connected; the release reaches the new owner before it.
            w1.Transfer(pawn, w2);
            PlayerLeaves(w2);
            _mesh.Pump();
            Assert.IsFalse(w2.Find(netId).IsOwnerConnected);
            Assert.AreEqual(new[] { "disconnected w2 connected=False" }, _game.Calls);

            ExpireAt(w2, Time.unscaledTimeAsDouble + Grace + 1);
            _mesh.Pump();
            Assert.AreEqual(new[] { "disconnected w2 connected=False", "despawn w2" }, _game.Calls);
        }

        [Test]
        public void ADropFoundAtTheHandoverReachesTheGhostsToo()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            ulong netId = pawn.NetId;

            // The drop reaches the new owner before the handover, which was sent while the player was connected.
            // The old owner keeps a ghost of the pawn, and its copy must say the player is away as well.
            w1.Transfer(pawn, w2);
            PlayerLeaves(w2);
            _mesh.Pump();
            Assert.IsTrue(w2.Find(netId).HasAuthority);
            var ghost = w1.Find(netId);
            Assert.IsTrue(ghost != null && !ghost.HasAuthority, "the old owner holds a ghost");
            Assert.IsFalse(ghost.IsOwnerConnected, "the ghost is told of the drop found at the handover");
        }

        [Test]
        public void AConnectedPlayersPawnCallsNoHook()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var pawn = SpawnPawn(w1);
            w1.Transfer(pawn, w2);
            _mesh.Pump();
            Assert.IsTrue(w2.Find(pawn.NetId).IsOwnerConnected);
            CollectionAssert.IsEmpty(_game.Calls);
        }
    }
}
