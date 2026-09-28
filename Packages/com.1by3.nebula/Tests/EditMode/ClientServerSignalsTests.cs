using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// What the client tells a game about the server while the link stays up (NEB-355): the local pawn lost to a
    /// worker failure or removed by the server (<see cref="NebulaClient.LocalPlayerLost"/>), the gateway's
    /// <see cref="JoinHoldReason.Recovering"/> hold, and a server that stops sending state
    /// (<see cref="NebulaClient.ServerStalled"/>, <see cref="NebulaClient.ServerResumed"/>). The client runs over a
    /// transport that delivers nothing by itself, on a clock the test moves.
    /// </summary>
    public class ClientServerSignalsTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const ulong Me = 5;
        private const ulong PawnId = 0x0001_0000_0000_0010UL;

        private GameObject _gameObject;
        private GameObject _prefab;
        private NebulaClient _client;
        private ClientDisconnectTests.ScriptedTransport _transport;
        private NebulaConfig _config;
        private float _now;
        private readonly List<string> _events = new List<string>();

        [SetUp]
        public void SetUp()
        {
            NebulaRuntime.Reset();
            ContainerRegistry.Rebuild();
            _prefab = new GameObject("pawn-prefab");
            _prefab.AddComponent<NetworkIdentity>();
            _prefab.SetActive(false);
            NetworkPrefabs.Register(new List<GameObject> { _prefab });
            _gameObject = new GameObject("Client signals test");
            _client = _gameObject.AddComponent<NebulaClient>();
            _transport = new ClientDisconnectTests.ScriptedTransport();
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            typeof(NebulaClient).GetField("_transport", Hidden).SetValue(_client, _transport);
            typeof(NebulaClient).GetProperty("Config").SetValue(_client, _config);
            _now = 100f;
            _client.ClockForTests = () => _now;
            _events.Clear();
            _client.LocalPlayerLost += (pawn, cause) => _events.Add($"lost {cause} {(pawn != null ? pawn.NetId : 0)}");
            _client.ServerStalled += () => _events.Add("stalled");
            _client.ServerResumed += () => _events.Add("resumed");
        }

        [TearDown]
        public void TearDown()
        {
            typeof(NebulaClient).GetField("_transport", Hidden).SetValue(_client, null);
            Object.DestroyImmediate(_gameObject);
            Object.DestroyImmediate(_config);
            Object.DestroyImmediate(_prefab);
            NetworkPrefabs.Register(null);
            NebulaRuntime.Reset();
            NetworkTime.LatestServerTick = 0;
        }

        private void Deliver(TransportEvent ev) =>
            typeof(NebulaClient).GetMethod("HandleTransportEvent", Hidden).Invoke(_client, new object[] { ev });

        private void Receive(Action<NetworkWriter> write)
        {
            var writer = new NetworkWriter();
            write(writer);
            typeof(NebulaClient).GetMethod("Dispatch", Hidden).Invoke(_client, new object[] { new NetworkReader(writer.ToSegment()) });
        }

        private int Peer => (int)typeof(NebulaClient).GetField("_gatewayPeer", Hidden).GetValue(_client);

        private void JoinStatus(JoinState state, JoinHoldReason reason = JoinHoldReason.None) =>
            Receive(w => new JoinStatusMsg { State = state, Reason = reason }.Write(w));

        private void SpawnPawn() => Receive(w => new EntitySpawnMsg
        {
            NetId = PawnId, PrefabId = 0, OwnerClientId = Me, OwnerIdentity = "id", Container = ContainerRef.None, Epoch = 1,
            OwnerWorkerIndex = 1, LocalRotation = Quaternion.identity, LocalScale = Vector3.one,
        }.Write(w, MsgId.EntitySpawn));

        private void DespawnPawn() => Receive(w => new EntityDespawnMsg { NetId = PawnId, Epoch = 1 }.Write(w, MsgId.EntityDespawn));

        /// <summary>A tick of world state with no entries: state from the server, as far as the stall watch goes.</summary>
        private void WorldState(uint tick) => Receive(w =>
        {
            int slot = WorldStateMsg.Begin(w, MsgId.WorldState, tick, 1);
            WorldStateMsg.End(w, slot, 0);
        });

        /// <summary>Welcomed, joined and holding the local pawn.</summary>
        private void InTheWorld()
        {
            _client.Connect();
            Deliver(new TransportEvent(TransportEvent.Kind.Connected, Peer, default));
            Receive(w => new WelcomeMsg { ClientId = Me, TickRate = 60, Identity = "id", SessionToken = "s", NegotiatedVersion = HelloMsg.ProtocolVersion }.Write(w));
            JoinStatus(JoinState.Joined);
            SpawnPawn();
            Assert.That(_client.LocalPlayer, Is.Not.Null);
        }

        private void Tick(float seconds)
        {
            _now += seconds;
            _client.TickStallWatch();
        }

        [Test]
        public void APawnLostWithItsWorkerIsReportedAsARecovery()
        {
            InTheWorld();
            // The gateway says it is placing the player again before it despawns the pawn.
            JoinStatus(JoinState.Starting, JoinHoldReason.Recovering);
            Assert.That(_client.JoinHoldReason, Is.EqualTo(JoinHoldReason.Recovering));
            DespawnPawn();
            Assert.That(_events, Is.EqualTo(new[] { $"lost WorkerLost {PawnId}" }));
            Assert.That(_client.LocalPlayer, Is.Null);
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.InGame), "the connection stays up");
        }

        [Test]
        public void APawnTheServerRemovedIsReportedAsDespawned()
        {
            InTheWorld();
            DespawnPawn();
            Assert.That(_events, Is.EqualTo(new[] { $"lost Despawned {PawnId}" }));
        }

        [Test]
        public void EndingTheConnectionIsNotALostPawn()
        {
            InTheWorld();
            _client.Disconnect();
            CollectionAssert.IsEmpty(_events, "the world is cleared; Disconnected says why");
        }

        [Test]
        public void AServerThatStopsSendingStateIsReportedAsStalledAndThenResumed()
        {
            InTheWorld();
            WorldState(10);
            Tick(1.5f);
            Assert.That(_client.SecondsSinceServerState, Is.EqualTo(1.5f).Within(1e-4));
            CollectionAssert.IsEmpty(_events);
            Tick(0.6f);
            Assert.That(_events, Is.EqualTo(new[] { "stalled" }));
            Assert.That(_client.IsServerStalled, Is.True);
            Tick(5f);
            Assert.That(_events, Is.EqualTo(new[] { "stalled" }), "raised once per stall");

            WorldState(11);
            Assert.That(_events, Is.EqualTo(new[] { "stalled", "resumed" }));
            Assert.That(_client.IsServerStalled, Is.False);
            Assert.That(_client.SecondsSinceServerState, Is.Zero);
        }

        [Test]
        public void AStallThatBecomesARecoveryEndsWithResumed()
        {
            InTheWorld();
            WorldState(10);
            Tick(3f);
            Assert.That(_events, Is.EqualTo(new[] { "stalled" }));
            // The orchestrator declares the worker dead; the gateway recovers the player.
            JoinStatus(JoinState.Starting, JoinHoldReason.Recovering);
            Assert.That(_events, Is.EqualTo(new[] { "stalled", "resumed" }), "every stall is closed");
            Tick(10f);
            Assert.That(_events.Count, Is.EqualTo(2), "a held join is not watched");
        }

        [Test]
        public void TheWatchIsOffWhileJoiningAndBeforeAnyStateSinceTheJoin()
        {
            _client.Connect();
            Deliver(new TransportEvent(TransportEvent.Kind.Connected, Peer, default));
            Receive(w => new WelcomeMsg { ClientId = Me, TickRate = 60, Identity = "id", SessionToken = "s", NegotiatedVersion = HelloMsg.ProtocolVersion }.Write(w));
            JoinStatus(JoinState.Starting, JoinHoldReason.WorldStarting);
            WorldState(1);
            Tick(30f);
            CollectionAssert.IsEmpty(_events, "a join still in progress never looks stalled");

            JoinStatus(JoinState.Joined);
            Tick(30f);
            CollectionAssert.IsEmpty(_events, "nothing has been sent since the join: a scope that sends nothing is not a stall");
            Assert.That(_client.SecondsSinceServerState, Is.Zero);
        }

        [Test]
        public void TheWatchCanBeTurnedOff()
        {
            _config.ClientStallSeconds = 0f;
            InTheWorld();
            WorldState(10);
            Tick(60f);
            CollectionAssert.IsEmpty(_events);
        }

        [Test]
        public void AnotherPlayersPawnSaysWhetherItsOwnerIsConnected()
        {
            InTheWorld();
            const ulong Other = PawnId + 1;
            EntitySpawnMsg OtherPawn(EntityFlags flags) => new EntitySpawnMsg
            {
                NetId = Other, PrefabId = 0, OwnerClientId = 9, OwnerIdentity = "other", Container = ContainerRef.None, Epoch = 1,
                OwnerWorkerIndex = 1, LocalRotation = Quaternion.identity, LocalScale = Vector3.one, Flags = flags,
            };
            Receive(w => OtherPawn(EntityFlags.None).Write(w, MsgId.EntitySpawn));
            var other = _client.Find(Other);
            Assert.That(other.IsOwnerConnected, Is.True);
            var changes = new List<bool>();
            other.OwnerConnectedChanged += changes.Add;

            // The owning worker announces the pawn again, in place, when its player drops and when it comes back.
            Receive(w => OtherPawn(EntityFlags.OwnerDisconnected).Write(w, MsgId.EntitySpawn));
            Assert.That(other.IsOwnerConnected, Is.False);
            Receive(w => OtherPawn(EntityFlags.None).Write(w, MsgId.EntitySpawn));
            Assert.That(other.IsOwnerConnected, Is.True);
            Assert.That(changes, Is.EqualTo(new[] { false, true }));
        }

        [Test]
        public void ALostConnectionEndsAStall()
        {
            InTheWorld();
            WorldState(10);
            Tick(3f);
            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.Timeout));
            Assert.That(_events, Is.EqualTo(new[] { "stalled", "resumed" }));
            Assert.That(_client.SecondsSinceServerState, Is.Zero);
        }
    }
}
