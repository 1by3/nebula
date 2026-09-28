using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// A game's handler for a connection event may call <see cref="NebulaClient.Connect"/> or
    /// <see cref="NebulaClient.Disconnect"/> from inside it: a "reconnect" button wired straight to
    /// <see cref="NebulaClient.Disconnected"/>, a menu that gives up on <see cref="NebulaClient.ReconnectGaveUp"/>, a
    /// quit flow on <see cref="NebulaClient.LeaveFinished"/>. Whatever the handler does, the client must end in a state
    /// that matches what it last reported and last asked for: a live link when it is connecting, no retry when it
    /// said it would not retry. The client runs over the scripted transport, on a clock the test moves.
    /// </summary>
    public class ClientReentrancyTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string TokenPref = "nebula.identityToken";

        private GameObject _gameObject;
        private NebulaClient _client;
        private ClientDisconnectTests.ScriptedTransport _transport;
        private NebulaConfig _config;
        private float _now;
        private readonly List<DisconnectInfo> _disconnects = new List<DisconnectInfo>();
        private bool _hadSavedToken;
        private string _previousToken;

        [SetUp]
        public void SetUp()
        {
            _hadSavedToken = PlayerPrefs.HasKey(TokenPref);
            _previousToken = PlayerPrefs.GetString(TokenPref, "");
            _gameObject = new GameObject("Client re-entrancy test");
            _client = _gameObject.AddComponent<NebulaClient>();
            _transport = new ClientDisconnectTests.ScriptedTransport();
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            typeof(NebulaClient).GetField("_transport", Hidden).SetValue(_client, _transport);
            typeof(NebulaClient).GetProperty("Config").SetValue(_client, _config);
            _now = 100f;
            _client.ClockForTests = () => _now;
            _disconnects.Clear();
            _client.Disconnected += _disconnects.Add;
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

        private int Welcomed()
        {
            _client.Connect();
            int peer = Peer;
            Deliver(new TransportEvent(TransportEvent.Kind.Connected, peer, default));
            Receive(w => new WelcomeMsg { ClientId = 5, TickRate = 60, Identity = "id", SessionToken = "s", NegotiatedVersion = HelloMsg.ProtocolVersion }.Write(w));
            _disconnects.Clear();
            return peer;
        }

        /// <summary>The client is connecting on a link it has just dialled, and nothing else is open.</summary>
        private void AssertConnectingOnANewLink(int oldPeer)
        {
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Connecting));
            Assert.That(_client.WantsConnection, Is.True);
            Assert.That(Peer, Is.GreaterThanOrEqualTo(0));
            Assert.That(Peer, Is.Not.EqualTo(oldPeer));
            Assert.That(_transport.Dialled[_transport.Dialled.Count - 1], Is.EqualTo(Peer));
        }

        /// <summary>The client has stopped: no link, no retry, and it said so last.</summary>
        private void AssertStopped()
        {
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Disconnected));
            Assert.That(_client.WantsConnection, Is.False);
            Assert.That(_client.IsReconnecting, Is.False);
            Assert.That(Peer, Is.EqualTo(-1));
            Assert.That(_client.LastDisconnect.WillRetry, Is.False, "the last thing it reported is that it stopped");
            int dialled = _transport.Dialled.Count;
            _now += 60f;
            _client.TickConnection();
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled), "and nothing dials again");
        }

        [Test]
        public void ConnectFromADisconnectedHandlerAfterALostLinkConnects()
        {
            int peer = Welcomed();
            _client.Disconnected += info => { if (info.WillRetry) _client.Connect(); };
            Deliver(TransportEvent.Disconnected(peer, TransportDisconnectReason.Timeout));
            AssertConnectingOnANewLink(peer);
        }

        [Test]
        public void DisconnectFromADisconnectedHandlerAfterALostLinkStops()
        {
            int peer = Welcomed();
            int reconnecting = 0;
            _client.Reconnecting += () => reconnecting++;
            _client.Disconnected += info => { if (info.WillRetry) _client.Disconnect(); };
            Deliver(TransportEvent.Disconnected(peer, TransportDisconnectReason.Timeout));
            AssertStopped();
            Assert.That(reconnecting, Is.Zero, "a client that stopped is not reconnecting");
            Assert.That(_disconnects.Count, Is.EqualTo(2));
            Assert.That(_disconnects[1].Reason, Is.EqualTo(DisconnectReason.ClientRequested));
        }

        [Test]
        public void DisconnectFromADisconnectedHandlerInsideDisconnectDoesNotRecurse()
        {
            Welcomed();
            _client.Disconnected += _ => _client.Disconnect();
            _client.Disconnect();
            AssertStopped();
            Assert.That(_disconnects.Count, Is.EqualTo(1));
        }

        [Test]
        public void DisconnectFromAGatewayDrainingHandlerStops()
        {
            Welcomed();
            _client.GatewayDraining += _ => _client.Disconnect();
            Receive(w => new GatewayDrainingMsg { ReconnectWithinSeconds = 10 }.Write(w));
            AssertStopped();
        }

        [Test]
        public void ConnectFromAKickHandlerStartsANewSession()
        {
            int peer = Welcomed();
            _client.Disconnected += info => { if (info.Reason == DisconnectReason.Kicked) _client.Connect(); };
            Receive(w => new KickedMsg { Code = 1, Reason = "bye" }.Write(w));
            AssertConnectingOnANewLink(peer);
            Assert.That(_client.SessionToken, Is.Empty);
        }

        [Test]
        public void ConnectFromAReconnectGaveUpHandlerStartsAgain()
        {
            _config.ReconnectGiveUpSeconds = 5f;
            int peer = Welcomed();
            _client.ReconnectGaveUp += () => _client.Connect();
            Deliver(TransportEvent.Disconnected(peer, TransportDisconnectReason.Timeout));
            int last = peer;
            for (int i = 0; i < 20 && _client.IsReconnecting; i++)
            {
                _now += 1f;
                _client.TickConnection();
                if (_client.IsReconnecting && _client.ConnectionState == NebulaClient.State.Connecting && Peer != last)
                {
                    last = Peer;
                    Deliver(TransportEvent.Disconnected(last, TransportDisconnectReason.HostUnreachable));
                }
            }
            Assert.That(_client.IsReconnecting, Is.False);
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Connecting), "the handler's Connect wins");
            Assert.That(_client.WantsConnection, Is.True);
            Assert.That(_client.ReconnectAttempt, Is.Zero, "a fresh start, not the outage it gave up on");
        }

        [Test]
        public void ConnectFromALeaveFinishedHandlerAfterATimeoutConnects()
        {
            int peer = Welcomed();
            _client.LeaveFinished += _ => _client.Connect();
            _client.Leave(0.5f);
            _now += 1f;
            _client.TickConnection();
            Assert.That(_client.IsLeaving, Is.False);
            Assert.That(_transport.Closed, Does.Contain(peer));
            AssertConnectingOnANewLink(peer);
        }

        [Test]
        public void DisconnectFromALeaveFinishedHandlerRaisedByConnectLetsConnectWin()
        {
            int peer = Welcomed();
            _client.LeaveFinished += _ => _client.Disconnect();
            _client.Leave();
            // Connecting again while the goodbye is unconfirmed ends the leave, whose handler disconnects; the game
            // asked to connect last, so the client connects.
            _client.Connect();
            Assert.That(_client.IsLeaving, Is.False);
            Assert.That(_transport.Closed, Does.Contain(peer));
            AssertConnectingOnANewLink(peer);
        }

        [Test]
        public void DisconnectFromALeaveFinishedHandlerWithNoConnectionStaysStopped()
        {
            _client.Connect();
            _client.LeaveFinished += _ => _client.Disconnect();
            _client.Leave();
            AssertStopped();
        }
    }
}
