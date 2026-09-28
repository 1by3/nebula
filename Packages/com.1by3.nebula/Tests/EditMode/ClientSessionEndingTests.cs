using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The client's side of the deliberate session endings (NEB-354): <see cref="NebulaClient.Leave"/> says goodbye
    /// and waits for the gateway to close the link; a <see cref="KickedMsg"/> ends the connection for good with the
    /// server's code; a shutdown notice is reported as <see cref="DisconnectReason.ServerShutdown"/> and retried like
    /// a drop. The client runs over a transport that records what it sends, and each test delivers the gateway's side
    /// by hand.
    /// </summary>
    public class ClientSessionEndingTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string TokenPref = "nebula.identityToken";

        private sealed class RecordingTransport : ITransport
        {
            public int NextPeer = 7;
            public readonly List<int> Closed = new List<int>();
            public readonly List<(int Peer, byte[] Bytes)> Sent = new List<(int, byte[])>();
            public string Name => "recording";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { }
            public void StartClient() { }
            public int Connect(string host, int port) => NextPeer++;
            public void Disconnect(int peerId) => Closed.Add(peerId);
            public bool IsConnected(int peerId) => true;
            public int RoundTripMs(int peerId) => -1;
            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
            {
                var copy = new byte[payload.Count];
                Buffer.BlockCopy(payload.Array, payload.Offset, copy, 0, payload.Count);
                Sent.Add((peerId, copy));
            }
            public void Poll(Action<TransportEvent> handler) { }
            public void Flush() { }
            public void Stop() { }
            public void Dispose() { }

            public int CountOf(MsgId id)
            {
                int n = 0;
                foreach (var (_, bytes) in Sent) if (bytes.Length > 0 && bytes[0] == (byte)id) n++;
                return n;
            }
        }

        private GameObject _gameObject;
        private NebulaClient _client;
        private RecordingTransport _transport;
        private NebulaConfig _config;
        private float _now;
        private readonly List<DisconnectInfo> _disconnects = new List<DisconnectInfo>();
        private readonly List<bool> _leaves = new List<bool>();
        private bool _hadSavedToken;
        private string _previousToken;

        [SetUp]
        public void SetUp()
        {
            _hadSavedToken = PlayerPrefs.HasKey(TokenPref);
            _previousToken = PlayerPrefs.GetString(TokenPref, "");
            _gameObject = new GameObject("Client session ending test");
            _client = _gameObject.AddComponent<NebulaClient>();
            _transport = new RecordingTransport();
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            typeof(NebulaClient).GetField("_transport", Hidden).SetValue(_client, _transport);
            typeof(NebulaClient).GetProperty("Config").SetValue(_client, _config);
            _now = 100f;
            _client.ClockForTests = () => _now;
            _disconnects.Clear();
            _leaves.Clear();
            _client.Disconnected += _disconnects.Add;
            _client.LeaveFinished += _leaves.Add;
        }

        [TearDown]
        public void TearDown()
        {
            typeof(NebulaClient).GetField("_transport", Hidden).SetValue(_client, null);
            Object.DestroyImmediate(_gameObject);
            Object.DestroyImmediate(_config);
            NebulaRuntime.LocalClientId = 0;
            NebulaRuntime.LocalIdentity = "";
            if (_hadSavedToken) PlayerPrefs.SetString(TokenPref, _previousToken);
            else PlayerPrefs.DeleteKey(TokenPref);
            PlayerPrefs.Save();
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

        /// <summary>Connect, bring the link up and be welcomed at <paramref name="protocol"/>: the client is in game with a session token.</summary>
        private int Welcomed(ushort protocol = HelloMsg.ProtocolVersion)
        {
            _client.Connect();
            int peer = Peer;
            Deliver(new TransportEvent(TransportEvent.Kind.Connected, peer, default));
            Receive(w => new WelcomeMsg { ClientId = 5, TickRate = 60, Identity = "id", SessionToken = "session-token", NegotiatedVersion = protocol }.Write(w));
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.InGame));
            _disconnects.Clear();
            return peer;
        }

        [Test]
        public void LeaveSaysGoodbyeAndWaitsForTheGatewayToCloseTheLink()
        {
            int peer = Welcomed();

            _client.Leave();

            Assert.That(_transport.CountOf(MsgId.Goodbye), Is.EqualTo(1), "a goodbye is sent");
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Disconnected));
            Assert.That(_client.WantsConnection, Is.False);
            Assert.That(_client.SessionToken, Is.Empty, "the session is over: the next connection is a new one");
            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.ClientRequested));
            Assert.That(_disconnects[0].WillRetry, Is.False);
            Assert.That(_client.IsLeaving, Is.True);
            Assert.That(_transport.Closed, Is.Empty, "the link stays open until the gateway closes it");
            CollectionAssert.IsEmpty(_leaves);

            // The gateway ends the session and closes the link: that is the acknowledgement.
            Deliver(TransportEvent.Disconnected(peer, TransportDisconnectReason.ClosedByRemote));
            Assert.That(_client.IsLeaving, Is.False);
            Assert.That(_leaves, Is.EqualTo(new[] { true }));
            Assert.That(_disconnects.Count, Is.EqualTo(1), "the close of a link the client left is not a new outage");
            _now += 60f;
            _client.TickConnection();
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Disconnected), "and nothing reconnects");
        }

        [Test]
        public void ALeaveTheGatewayNeverConfirmsClosesTheLinkAfterTheTimeout()
        {
            int peer = Welcomed();
            _client.Leave(0.5f);
            _now += 0.4f;
            _client.TickConnection();
            Assert.That(_client.IsLeaving, Is.True);
            _now += 0.2f;
            _client.TickConnection();
            Assert.That(_client.IsLeaving, Is.False);
            Assert.That(_transport.Closed, Is.EqualTo(new[] { peer }), "the client closes the link itself");
            Assert.That(_leaves, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void LeaveWithoutAWelcomedConnectionActsLikeDisconnect()
        {
            _client.Connect();
            _client.Leave();
            Assert.That(_transport.CountOf(MsgId.Goodbye), Is.Zero, "nobody to say goodbye to");
            Assert.That(_client.IsLeaving, Is.False);
            Assert.That(_leaves, Is.EqualTo(new[] { false }), "raised at once, so a waiting quit is not held up");
            Assert.That(_client.WantsConnection, Is.False);
            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.ClientRequested));
        }

        [Test]
        public void ConnectingAgainWhileLeavingClosesTheOldLink()
        {
            int peer = Welcomed();
            _client.Leave();
            _client.Connect();
            Assert.That(_client.IsLeaving, Is.False);
            Assert.That(_transport.Closed, Does.Contain(peer));
            Assert.That(_leaves, Is.EqualTo(new[] { false }));
            Assert.That(Peer, Is.Not.EqualTo(peer), "a new link, for a new session");
        }

        [Test]
        public void DataOnTheLinkBeingLeftIsIgnored()
        {
            int peer = Welcomed();
            _client.Leave();
            var w = new NetworkWriter();
            new JoinStatusMsg { State = JoinState.Joined }.Write(w);
            Deliver(new TransportEvent(TransportEvent.Kind.Data, peer, w.ToSegment()));
            Assert.That(_client.Join, Is.EqualTo(JoinState.None), "the world this client left does not come back");
        }

        private static byte[] Message(Action<NetworkWriter> write)
        {
            var w = new NetworkWriter();
            write(w);
            return w.ToArray();
        }

        private static ArraySegment<byte> Batch(params byte[][] messages)
        {
            var w = new NetworkWriter();
            w.WriteByte((byte)MsgId.Batch);
            w.WriteUShort((ushort)messages.Length);
            foreach (var m in messages)
            {
                w.WriteUShort((ushort)m.Length);
                w.WriteRaw(new ArraySegment<byte>(m));
            }
            return new ArraySegment<byte>(w.ToArray());
        }

        [Test]
        public void WhatFollowsADrainNoticeInABatchIsNotApplied()
        {
            int peer = Welcomed();
            Receive(w => new JoinStatusMsg { State = JoinState.Joined }.Write(w));
            // The rest of the gateway's batch was written before the client dropped the link: a join status and a
            // welcome that would put a client that is now Disconnected back in game.
            Deliver(new TransportEvent(TransportEvent.Kind.Data, peer, Batch(
                Message(w => new GatewayDrainingMsg { ReconnectWithinSeconds = 10 }.Write(w)),
                Message(w => new JoinStatusMsg { State = JoinState.Joined }.Write(w)),
                Message(w => new WelcomeMsg { ClientId = 5, TickRate = 60, Identity = "id", SessionToken = "stale", NegotiatedVersion = HelloMsg.ProtocolVersion }.Write(w)))));

            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Disconnected));
            Assert.That(_client.Join, Is.EqualTo(JoinState.None), "the world stays cleared");
            Assert.That(_client.SessionToken, Is.EqualTo("session-token"), "nothing after the notice was read");
            Assert.That(_client.IsReconnecting, Is.True);

            // Packets still in flight on the dropped link are ignored too.
            Deliver(new TransportEvent(TransportEvent.Kind.Data, peer, new ArraySegment<byte>(
                Message(w => new WelcomeMsg { ClientId = 5, TickRate = 60, Identity = "id", SessionToken = "stale", NegotiatedVersion = HelloMsg.ProtocolVersion }.Write(w)))));
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Disconnected));
            Assert.That(_client.SessionToken, Is.EqualTo("session-token"));
        }

        [Test]
        public void WhatFollowsAKickInABatchIsNotApplied()
        {
            int peer = Welcomed();
            Deliver(new TransportEvent(TransportEvent.Kind.Data, peer, Batch(
                Message(w => new KickedMsg { Code = 1, Reason = "bye" }.Write(w)),
                Message(w => new JoinStatusMsg { State = JoinState.Joined }.Write(w)))));
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Disconnected));
            Assert.That(_client.Join, Is.EqualTo(JoinState.None));
        }

        [Test]
        public void AKickEndsTheConnectionWithTheServersCodeAndIsNotRetried()
        {
            Welcomed();
            Receive(w => new KickedMsg { Code = 42, Reason = "removed by a moderator" }.Write(w));

            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Disconnected));
            Assert.That(_client.WantsConnection, Is.False);
            Assert.That(_client.IsReconnecting, Is.False);
            Assert.That(_client.SessionToken, Is.Empty, "the session ended with the kick");
            Assert.That(_client.LastError, Is.EqualTo("removed by a moderator"));
            Assert.That(_disconnects.Count, Is.EqualTo(1));
            var info = _disconnects[0];
            Assert.That(info.Reason, Is.EqualTo(DisconnectReason.Kicked));
            Assert.That(info.Code, Is.EqualTo(42));
            Assert.That(info.Message, Is.EqualTo("removed by a moderator"));
            Assert.That(info.WillRetry, Is.False);
            Assert.That(_client.LastDisconnect.Code, Is.EqualTo(42));
            _now += 60f;
            _client.TickConnection();
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Disconnected), "a kicked client does not reconnect by itself");
        }

        [Test]
        public void AShutdownNoticeIsReportedAsServerShutdownAndRetried()
        {
            int peer = Welcomed();
            int drains = 0;
            _client.GatewayDraining += _ => drains++;
            Receive(w => new GatewayDrainingMsg { ReconnectWithinSeconds = 10, ServerShutdown = true }.Write(w));

            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.ServerShutdown));
            Assert.That(_disconnects[0].WillRetry, Is.True);
            Assert.That(_client.IsReconnecting, Is.True);
            Assert.That(_client.SessionToken, Is.EqualTo("session-token"), "kept, so a server back in time returns the pawn");
            Assert.That(drains, Is.Zero, "not a drain to another gateway");
            Assert.That(_transport.Closed, Does.Contain(peer));
            _now += _config.ReconnectFirstDelaySeconds + 0.01f;
            _client.TickConnection();
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Connecting), "it reconnects on the normal schedule");
        }

        [Test]
        public void APlainDrainNoticeIsStillADrain()
        {
            Welcomed();
            Receive(w => new GatewayDrainingMsg { ReconnectWithinSeconds = 10 }.Write(w));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.GatewayDraining));
        }
    }
}
