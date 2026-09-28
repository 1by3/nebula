using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The client's reconnecting state, its backoff and its give-up time (NEB-352), over a scripted transport and
    /// a clock the test moves by hand.
    /// </summary>
    public class ClientReconnectTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string TokenPref = "nebula.identityToken";

        private GameObject _gameObject;
        private NebulaClient _client;
        private ClientDisconnectTests.ScriptedTransport _transport;
        private NebulaConfig _config;
        private float _now;
        private readonly List<string> _events = new List<string>();
        private readonly List<DisconnectInfo> _disconnects = new List<DisconnectInfo>();
        private bool _hadSavedToken;
        private string _previousToken;

        [SetUp]
        public void SetUp()
        {
            _hadSavedToken = PlayerPrefs.HasKey(TokenPref);
            _previousToken = PlayerPrefs.GetString(TokenPref, "");
            _gameObject = new GameObject("Client reconnect test");
            _client = _gameObject.AddComponent<NebulaClient>();
            _transport = new ClientDisconnectTests.ScriptedTransport();
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            typeof(NebulaClient).GetField("_transport", Hidden).SetValue(_client, _transport);
            typeof(NebulaClient).GetProperty("Config").SetValue(_client, _config);
            _now = 100f;
            _client.ClockForTests = () => _now;
            _events.Clear();
            _disconnects.Clear();
            _client.ConnectionStateChanged += s => _events.Add("State:" + s);
            _client.Disconnected += info => { _disconnects.Add(info); _events.Add("Disconnected:" + info.Reason + (info.WillRetry ? "+retry" : "")); };
            _client.Reconnecting += () => _events.Add("Reconnecting");
            _client.Reconnected += reclaimed => _events.Add("Reconnected:" + reclaimed);
            _client.ReconnectGaveUp += () => _events.Add("GaveUp");
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

        private int Peer => (int)typeof(NebulaClient).GetField("_gatewayPeer", Hidden).GetValue(_client);

        private void Deliver(TransportEvent ev) =>
            typeof(NebulaClient).GetMethod("HandleTransportEvent", Hidden).Invoke(_client, new object[] { ev });

        private void Receive(System.Action<NetworkWriter> write)
        {
            var writer = new NetworkWriter();
            write(writer);
            typeof(NebulaClient).GetMethod("Dispatch", Hidden).Invoke(_client, new object[] { new NetworkReader(writer.ToSegment()) });
        }

        private void LinkUpAndWelcome(bool reclaimed = false)
        {
            Deliver(new TransportEvent(TransportEvent.Kind.Connected, Peer, default));
            Receive(w => new WelcomeMsg { ClientId = 9, Identity = "", Token = "", SessionToken = "session", Reclaimed = reclaimed }.Write(w));
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.InGame));
        }

        /// <summary>Connected and welcomed, then the link drops: the client is reconnecting.</summary>
        private void PlayThenDrop(TransportDisconnectReason reason = TransportDisconnectReason.Timeout)
        {
            _client.Connect();
            LinkUpAndWelcome();
            _events.Clear();
            _disconnects.Clear();
            Deliver(TransportEvent.Disconnected(Peer, reason));
        }

        private void Advance(float seconds)
        {
            _now += seconds;
            _client.TickConnection();
        }

        [Test]
        public void ADropAfterTheWelcomeEntersTheReconnectingState()
        {
            PlayThenDrop();

            Assert.That(_client.IsReconnecting, Is.True);
            Assert.That(_client.ReconnectAttempt, Is.EqualTo(0));
            Assert.That(_client.ReconnectElapsedSeconds, Is.EqualTo(0f));
            Assert.That(_client.WantsConnection, Is.True);
            Assert.That(_events, Is.EqualTo(new[] { "State:Disconnected", "Disconnected:Timeout+retry", "Reconnecting" }));
        }

        [Test]
        public void RetriesFollowTheBackoffAndAreReportedOnce()
        {
            PlayThenDrop();
            int dialled = _transport.Dialled.Count;

            Advance(0.75f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled), "the first retry waits a second");
            Advance(0.25f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled + 1));
            Assert.That(_client.ReconnectAttempt, Is.EqualTo(1));
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Connecting));

            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.HostUnreachable));
            Assert.That(_client.IsReconnecting, Is.True);
            Advance(1.5f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled + 1), "later retries wait two seconds");
            Advance(0.5f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled + 2));
            Assert.That(_client.ReconnectAttempt, Is.EqualTo(2));
            Assert.That(_client.ReconnectElapsedSeconds, Is.EqualTo(3f).Within(1e-4));

            Assert.That(_events, Is.EqualTo(new[]
            {
                "State:Disconnected", "Disconnected:Timeout+retry", "Reconnecting",
                "State:Connecting", "State:Disconnected", "State:Connecting",
            }), "one Disconnected and one Reconnecting for the whole outage");
            Assert.That(_client.LastDisconnect.Reason, Is.EqualTo(DisconnectReason.HostUnreachable), "LastDisconnect follows each failure");
        }

        [Test]
        public void TheBackoffComesFromTheConfig()
        {
            _config.ReconnectFirstDelaySeconds = 0.5f;
            _config.ReconnectBackoffFactor = 3f;
            _config.ReconnectMaxDelaySeconds = 4f;
            PlayThenDrop();
            int dialled = _transport.Dialled.Count;

            Advance(0.5f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled + 1));
            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.HostUnreachable));
            Advance(1.25f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled + 1));
            Advance(0.25f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled + 2), "0.5 s × 3");
            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.HostUnreachable));
            Advance(3.75f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled + 2));
            Advance(0.25f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled + 3), "capped at 4 s");
        }

        [Test]
        public void AWelcomeEndsTheReconnectionAndSaysWhetherTheSessionCameBack()
        {
            PlayThenDrop();
            Advance(1f);
            LinkUpAndWelcome(reclaimed: true);

            Assert.That(_client.IsReconnecting, Is.False);
            Assert.That(_client.ReconnectAttempt, Is.EqualTo(0));
            Assert.That(_client.ReconnectElapsedSeconds, Is.EqualTo(0f));
            Assert.That(_events, Is.EqualTo(new[]
            {
                "State:Disconnected", "Disconnected:Timeout+retry", "Reconnecting",
                "State:Connecting", "State:Connected", "State:InGame", "Reconnected:True",
            }));

            // The next outage is a new one, reported and counted from the start.
            _events.Clear();
            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.ClosedByRemote));
            Assert.That(_events, Is.EqualTo(new[] { "State:Disconnected", "Disconnected:ClosedByServer+retry", "Reconnecting" }));
            Advance(1f);
            Assert.That(_client.ReconnectAttempt, Is.EqualTo(1));
        }

        [Test]
        public void ALostPawnComesBackAsReconnectedButNotReclaimed()
        {
            PlayThenDrop();
            Advance(1f);
            LinkUpAndWelcome(reclaimed: false);
            Assert.That(_events[_events.Count - 1], Is.EqualTo("Reconnected:False"));
        }

        [Test]
        public void TheClientGivesUpAfterTheGiveUpTime()
        {
            _config.ReconnectGiveUpSeconds = 5f;
            PlayThenDrop();
            for (int i = 0; i < 4; i++)
            {
                Advance(1f);
                if (_client.ConnectionState == NebulaClient.State.Connecting)
                    Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.HostUnreachable));
            }
            Assert.That(_client.IsReconnecting, Is.True);
            int dialled = _transport.Dialled.Count;
            Advance(0.5f);
            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.HostUnreachable)); // (a stale close; ignored)
            _events.Clear();

            Advance(0.5f);

            Assert.That(_client.IsReconnecting, Is.False);
            Assert.That(_client.WantsConnection, Is.False);
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Disconnected));
            Assert.That(_events, Is.EqualTo(new[] { "Disconnected:HostUnreachable", "GaveUp" }));
            Assert.That(_disconnects[_disconnects.Count - 1].WillRetry, Is.False);
            Assert.That(_client.LastError, Does.StartWith("gave up reconnecting after 5 s"));
            Assert.That(_client.LastError, Does.Not.Contain("retrying"));

            Advance(60f);
            Assert.That(_transport.Dialled.Count, Is.LessThanOrEqualTo(dialled + 1), "nothing is dialled after giving up");
        }

        [Test]
        public void AnAttemptInFlightIsAbandonedWhenTheClientGivesUp()
        {
            _config.ReconnectGiveUpSeconds = 3f;
            PlayThenDrop();
            Advance(1f);
            Deliver(new TransportEvent(TransportEvent.Kind.Connected, Peer, default)); // linked, not yet welcomed
            int peer = Peer;
            _events.Clear();

            Advance(2f);

            Assert.That(_transport.Closed, Does.Contain(peer));
            Assert.That(_events, Is.EqualTo(new[] { "State:Disconnected", "Disconnected:Timeout", "GaveUp" }));
        }

        [Test]
        public void TheDefaultNeverGivesUp()
        {
            PlayThenDrop();
            for (int i = 0; i < 100; i++)
            {
                Advance(10f);
                if (_client.ConnectionState == NebulaClient.State.Connecting)
                    Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.HostUnreachable));
            }
            Assert.That(_client.IsReconnecting, Is.True);
            Assert.That(_client.WantsConnection, Is.True);
            Assert.That(_events, Does.Not.Contain("GaveUp"));
            Assert.That(_client.ReconnectAttempt, Is.EqualTo(100));
        }

        [Test]
        public void AFirstConnectionThatFailsIsNotAReconnection()
        {
            _config.ReconnectGiveUpSeconds = 2f;
            _client.Connect();
            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.HostUnreachable));

            Assert.That(_client.IsReconnecting, Is.False);
            Assert.That(_events, Does.Not.Contain("Reconnecting"));
            int dialled = _transport.Dialled.Count;
            Advance(1f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled + 1), "it still retries after a second");
            Deliver(TransportEvent.Disconnected(Peer, TransportDisconnectReason.HostUnreachable));
            Advance(10f);
            Assert.That(_client.WantsConnection, Is.True, "the give-up time applies to reconnections only");
        }

        [Test]
        public void ADrainingGatewayUsesTheSameReconnectingState()
        {
            _client.Connect();
            LinkUpAndWelcome();
            _events.Clear();
            int dialled = _transport.Dialled.Count;

            Receive(w => new GatewayDrainingMsg { ReconnectWithinSeconds = 10 }.Write(w));

            Assert.That(_client.IsReconnecting, Is.True);
            Assert.That(_events, Is.EqualTo(new[] { "State:Disconnected", "Disconnected:GatewayDraining+retry", "Reconnecting" }));
            Advance(0.2f);
            Assert.That(_transport.Dialled.Count, Is.EqualTo(dialled + 1), "a drain reconnects at once");
            LinkUpAndWelcome(reclaimed: true);
            Assert.That(_events[_events.Count - 1], Is.EqualTo("Reconnected:True"));
        }

        [Test]
        public void DisconnectEndsTheReconnectionWithoutGivingUp()
        {
            PlayThenDrop();
            _events.Clear();

            _client.Disconnect();

            Assert.That(_client.IsReconnecting, Is.False);
            Assert.That(_events, Is.EqualTo(new[] { "Disconnected:ClientRequested" }));
        }

        [Test]
        public void ARefusalWhileReconnectingEndsTheReconnection()
        {
            PlayThenDrop();
            Advance(1f);
            Deliver(new TransportEvent(TransportEvent.Kind.Connected, Peer, default));
            _events.Clear();

            Receive(w => new JoinRejectedMsg { Code = JoinRejectReason.AtCapacity, Reason = "full", Retry = false }.Write(w));

            Assert.That(_client.IsReconnecting, Is.False);
            Assert.That(_client.WantsConnection, Is.False);
            Assert.That(_events, Is.EqualTo(new[] { "State:Disconnected", "Disconnected:JoinRefused" }));
        }
    }
}
