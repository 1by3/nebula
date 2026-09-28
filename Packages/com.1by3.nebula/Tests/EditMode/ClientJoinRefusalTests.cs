using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// A join refusal the client does not retry ends the connection: <see cref="NebulaClient.ConnectionState"/>
    /// becomes <see cref="NebulaClient.State.Disconnected"/> and <see cref="NebulaClient.ConnectionStateChanged"/>
    /// fires, without waiting for the gateway to close the link (NEB-353).
    /// </summary>
    public class ClientJoinRefusalTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string TokenPref = "nebula.identityToken";

        private GameObject _gameObject;
        private NebulaClient _client;
        private bool _hadSavedToken;
        private string _previousToken;

        [SetUp]
        public void SetUp()
        {
            _hadSavedToken = PlayerPrefs.HasKey(TokenPref);
            _previousToken = PlayerPrefs.GetString(TokenPref, "");
            _gameObject = new GameObject("Join refusal test");
            _client = _gameObject.AddComponent<NebulaClient>();
            typeof(NebulaClient).GetProperty("WantsConnection").SetValue(_client, true);
            typeof(NebulaClient).GetProperty("ConnectionState").SetValue(_client, NebulaClient.State.Connected);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_gameObject);
            if (_hadSavedToken) PlayerPrefs.SetString(TokenPref, _previousToken);
            else PlayerPrefs.DeleteKey(TokenPref);
            PlayerPrefs.Save();
        }

        private void Refuse(JoinRejectReason code, bool retry)
        {
            var writer = new NetworkWriter();
            new JoinRejectedMsg { Code = code, Reason = "test refusal", Retry = retry, Saturation = 0.95f }.Write(writer);
            typeof(NebulaClient).GetMethod("Dispatch", Hidden).Invoke(_client, new object[] { new NetworkReader(writer.ToSegment()) });
        }

        [TestCase(JoinRejectReason.ProtocolUnsupported)]
        [TestCase(JoinRejectReason.ContentVersionMismatch)]
        [TestCase(JoinRejectReason.EncryptionRequired)]
        [TestCase(JoinRejectReason.AtCapacity)]
        [TestCase(JoinRejectReason.Denied)]
        [TestCase(JoinRejectReason.None)]
        public void RefusalThatIsNotRetriedEndsInDisconnected(JoinRejectReason code)
        {
            var states = new List<NebulaClient.State>();
            _client.ConnectionStateChanged += states.Add;
            JoinRejectReason seenInHandler = JoinRejectReason.None;
            _client.ConnectionStateChanged += _ => seenInHandler = _client.JoinRejectReason;

            Refuse(code, retry: false);

            Assert.That(_client.WantsConnection, Is.False);
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Disconnected));
            Assert.That(states, Is.EqualTo(new[] { NebulaClient.State.Disconnected }));
            Assert.That(_client.JoinRejectReason, Is.EqualTo(code), "the typed reason survives the end of the connection");
            Assert.That(seenInHandler, Is.EqualTo(code), "and is already set when the state change is raised");
            Assert.That(_client.JoinRejectSaturation, Is.EqualTo(0.95f).Within(0.01f), "quantized on the wire");
            Assert.That(_client.LastError, Does.Contain("test refusal"));
        }

        [Test]
        public void RefusalThatIsRetriedKeepsTheClientConnecting()
        {
            var states = new List<NebulaClient.State>();
            _client.ConnectionStateChanged += states.Add;

            Refuse(JoinRejectReason.None, retry: true);

            Assert.That(_client.WantsConnection, Is.True);
            Assert.That(states, Is.Empty, "the transport's own disconnect drives a retried refusal");
            Assert.That(_client.LastError, Does.Contain("retrying"));
        }

        [Test]
        public void RefusalOfTheSavedAnonymousTokenForgetsItAndRetries()
        {
            typeof(NebulaClient).GetField("_storedToken", Hidden).SetValue(_client, "stale-token");
            typeof(NebulaClient).GetField("_presentedStoredToken", Hidden).SetValue(_client, true);

            Refuse(JoinRejectReason.None, retry: false);

            Assert.That(_client.WantsConnection, Is.True);
            Assert.That(_client.ConnectionState, Is.EqualTo(NebulaClient.State.Connected));
            Assert.That(typeof(NebulaClient).GetField("_storedToken", Hidden).GetValue(_client), Is.EqualTo(""));
        }
    }
}
