using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="NebulaClient.Disconnected"/> says why the connection ended and whether the client retries
    /// (NEB-351). The client runs over a scripted transport, so each test decides exactly what the link reports.
    /// </summary>
    public class ClientDisconnectTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string TokenPref = "nebula.identityToken";

        /// <summary>An <see cref="ITransport"/> that records what the client asks of it and delivers nothing by itself.</summary>
        internal sealed class ScriptedTransport : ITransport
        {
            public int NextPeer = 7;
            public readonly List<int> Dialled = new List<int>();
            public readonly List<int> Closed = new List<int>();
            public string Name => "scripted";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { }
            public void StartClient() { }
            public int Connect(string host, int port) { int peer = NextPeer++; Dialled.Add(peer); return peer; }
            public void Disconnect(int peerId) => Closed.Add(peerId);
            public bool IsConnected(int peerId) => true;
            public int RoundTripMs(int peerId) => -1;
            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload) { }
            public void Poll(Action<TransportEvent> handler) { }
            public void Flush() { }
            public void Stop() { }
            public void Dispose() { }
        }

        private GameObject _gameObject;
        private NebulaClient _client;
        private ScriptedTransport _transport;
        private NebulaConfig _config;
        private readonly List<DisconnectInfo> _disconnects = new List<DisconnectInfo>();
        private readonly List<string> _order = new List<string>();
        private bool _hadSavedToken;
        private string _previousToken;

        [SetUp]
        public void SetUp()
        {
            _hadSavedToken = PlayerPrefs.HasKey(TokenPref);
            _previousToken = PlayerPrefs.GetString(TokenPref, "");
            _gameObject = new GameObject("Client disconnect test");
            _client = _gameObject.AddComponent<NebulaClient>();
            _transport = new ScriptedTransport();
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            typeof(NebulaClient).GetField("_transport", Hidden).SetValue(_client, _transport);
            typeof(NebulaClient).GetProperty("Config").SetValue(_client, _config);
            _disconnects.Clear();
            _order.Clear();
            _client.Disconnected += info => { _disconnects.Add(info); _order.Add("Disconnected"); };
            _client.ConnectionStateChanged += state => _order.Add("State:" + state);
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

        /// <summary>Connect and bring the link up: the client is <see cref="NebulaClient.State.Connected"/> and has sent its hello.</summary>
        private int ConnectLinkUp()
        {
            _client.Connect();
            int peer = Peer;
            Deliver(new TransportEvent(TransportEvent.Kind.Connected, peer, default));
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Connected));
            _order.Clear();
            return peer;
        }

        [TestCase(TransportDisconnectReason.Timeout, DisconnectReason.Timeout)]
        [TestCase(TransportDisconnectReason.ClosedByRemote, DisconnectReason.ClosedByServer)]
        [TestCase(TransportDisconnectReason.NetworkUnreachable, DisconnectReason.NetworkUnreachable)]
        [TestCase(TransportDisconnectReason.Unknown, DisconnectReason.Unknown)]
        public void ALostLinkSaysWhyAndThatTheClientRetries(TransportDisconnectReason transport, DisconnectReason expected)
        {
            int peer = ConnectLinkUp();

            Deliver(TransportEvent.Disconnected(peer, transport));

            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(expected));
            Assert.That(_disconnects[0].WillRetry, Is.True);
            Assert.That(_disconnects[0].Message, Is.EqualTo("disconnected from gateway (retrying)"));
            Assert.That(_client.LastError, Is.EqualTo("disconnected from gateway (retrying)"), "LastError keeps its text");
            Assert.That(_client.LastDisconnect.Reason, Is.EqualTo(expected));
            Assert.That(_client.WantsConnection, Is.True);
            Assert.That(_order, Is.EqualTo(new[] { "State:Disconnected", "Disconnected" }), "the state changes first");
        }

        [Test]
        public void AGatewayThatNeverAnswersIsReportedOncePerOutage()
        {
            _client.Connect();
            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.HostUnreachable));
            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.HostUnreachable));
            Assert.That(_disconnects[0].Message, Does.Contain("could not reach"));

            // The retry fails too: LastDisconnect follows it, the event is not raised again.
            _client.Connect();
            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.NetworkUnreachable));
            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_client.LastDisconnect.Reason, Is.EqualTo(DisconnectReason.NetworkUnreachable));

            // Giving up is always reported.
            _client.Disconnect();
            Assert.That(_disconnects.Count, Is.EqualTo(2));
            Assert.That(_disconnects[1].Reason, Is.EqualTo(DisconnectReason.ClientRequested));
            Assert.That(_disconnects[1].WillRetry, Is.False);
        }

        [Test]
        public void AWelcomeEndsTheOutageSoTheNextDropIsReported()
        {
            int peer = ConnectLinkUp();
            Deliver(TransportEvent.Disconnected(peer, TransportDisconnectReason.Timeout));
            peer = ConnectLinkUp();
            Receive(w => new WelcomeMsg { ClientId = 5, Identity = "", Token = "", SessionToken = "s" }.Write(w));
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.InGame));

            Deliver(TransportEvent.Disconnected(peer, TransportDisconnectReason.ClosedByRemote));

            Assert.That(_disconnects.Count, Is.EqualTo(2));
            Assert.That(_disconnects[1].Reason, Is.EqualTo(DisconnectReason.ClosedByServer));
        }

        [Test]
        public void TheCloseOfALinkTheClientAlreadyLeftIsIgnored()
        {
            int peer = ConnectLinkUp();
            Deliver(TransportEvent.Disconnected(peer + 100, TransportDisconnectReason.ClosedByRemote));
            Assert.That(_disconnects, Is.Empty);
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Connected));
        }

        [Test]
        public void AnEncryptionFailureIsReportedAsSuch()
        {
            _client.Connect();
            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.SecurityFailure));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.EncryptionFailed));
        }

        [Test]
        public void DisconnectIsClientRequestedAndReportedOnce()
        {
            int peer = ConnectLinkUp();

            _client.Disconnect();
            _client.Disconnect();
            Deliver(TransportEvent.Disconnected(peer, TransportDisconnectReason.LocalRequest));

            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.ClientRequested));
            Assert.That(_disconnects[0].WillRetry, Is.False);
            Assert.That(_transport.Closed, Is.EqualTo(new[] { peer }));
        }

        [Test]
        public void AReplacedSessionIsReportedAsSuchAndNotAsClientRequested()
        {
            ConnectLinkUp();
            Receive(w => new SessionReplacedMsg { Reason = "you signed in somewhere else" }.Write(w));

            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.SessionReplaced));
            Assert.That(_disconnects[0].Message, Is.EqualTo("you signed in somewhere else"));
            Assert.That(_disconnects[0].WillRetry, Is.False);
            Assert.That(_client.WantsConnection, Is.False);
        }

        [Test]
        public void ADrainingGatewayIsReportedWithARetry()
        {
            int peer = ConnectLinkUp();
            Receive(w => new GatewayDrainingMsg { ReconnectWithinSeconds = 10 }.Write(w));

            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.GatewayDraining));
            Assert.That(_disconnects[0].WillRetry, Is.True);
            Assert.That(_client.WantsConnection, Is.True);

            // The link it dropped reports its close later; that is not a second outage.
            Deliver(TransportEvent.Disconnected(peer, TransportDisconnectReason.LocalRequest));
            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_client.LastDisconnect.Reason, Is.EqualTo(DisconnectReason.GatewayDraining));
        }

        [Test]
        public void ARefusalThatStopsRetriesIsJoinRefused()
        {
            ConnectLinkUp();
            Receive(w => new JoinRejectedMsg { Code = JoinRejectReason.AtCapacity, Reason = "full", Retry = false }.Write(w));

            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.JoinRefused));
            Assert.That(_disconnects[0].WillRetry, Is.False);
            Assert.That(_disconnects[0].Message, Does.Contain("full"));
        }

        [Test]
        public void ARefusalTheClientRetriesIsJoinRefusedWhenTheGatewayClosesTheLink()
        {
            int peer = ConnectLinkUp();
            Receive(w => new JoinRejectedMsg { Reason = "gateway is draining", Retry = true }.Write(w));
            Assert.That(_disconnects, Is.Empty, "nothing has ended yet");

            Deliver(TransportEvent.Disconnected(peer, TransportDisconnectReason.ClosedByRemote));

            Assert.That(_disconnects.Count, Is.EqualTo(1));
            Assert.That(_disconnects[0].Reason, Is.EqualTo(DisconnectReason.JoinRefused));
            Assert.That(_disconnects[0].WillRetry, Is.True);
            Assert.That(_client.LastError, Does.Contain("gateway unavailable: gateway is draining"));
        }
    }
}
