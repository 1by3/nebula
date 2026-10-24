using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// A gateway dials a worker again when a link it still wants is lost, fails, or never finishes its dial
    /// (CLOUD-11). Before, a link kept alive by a waiting client's spawn reason stayed unconnected for good: the
    /// worker reported ready, the gateway had no connection to it, and every join was held at ScopeNotReady.
    /// </summary>
    public sealed class GatewayWorkerRedialTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        private GameObject _go;
        private NebulaConfig _config;
        private LocalControlPlane _plane;
        private NebulaGateway _gateway;
        private ClientDisconnectTests.ScriptedTransport _transport;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            _config.EncryptClients = false;
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0))) _config.GatewayPort = (ushort)((IPEndPoint)probe.Client.LocalEndPoint).Port;
            _plane = new LocalControlPlane();
            _plane.Connect();
            _plane.RegisterWorker("w1", 1, "10.0.0.5", 7101);
            _go = new GameObject("gateway-redial");
            _gateway = _go.AddComponent<NebulaGateway>();
            _gateway.Initialize(_config, _plane);
            var field = typeof(NebulaGateway).GetField("_transport", Hidden);
            ((IDisposable)field.GetValue(_gateway)).Dispose();
            _transport = new ClientDisconnectTests.ScriptedTransport();
            field.SetValue(_gateway, _transport);
        }

        [TearDown]
        public void TearDown()
        {
            typeof(NebulaGateway).GetField("_transport", Hidden).SetValue(_gateway, null);
            Object.DestroyImmediate(_go);
            Object.DestroyImmediate(_config);
            _plane.Dispose();
        }

        private void EnsureLink(string workerId) =>
            typeof(NebulaGateway).GetMethod("EnsureLink", Hidden).Invoke(_gateway, new object[] { workerId });

        private object Link(string workerId) =>
            ((System.Collections.IDictionary)typeof(NebulaGateway).GetField("_links", Hidden).GetValue(_gateway))[workerId];

        private void SetLink(string workerId, string field, double value) =>
            Link(workerId).GetType().GetField(field).SetValue(Link(workerId), value);

        private void Deliver(TransportEvent ev) =>
            typeof(NebulaGateway).GetMethod("HandleTransportEvent", Hidden).Invoke(_gateway, new object[] { ev });

        [Test]
        public void AFailedDialIsTriedAgainAfterTheBackoff()
        {
            EnsureLink("w1");
            Assert.That(_transport.Dialled.Count, Is.EqualTo(1));
            Deliver(TransportEvent.Disconnected(_transport.Dialled[0], TransportDisconnectReason.HostUnreachable));

            EnsureLink("w1");
            Assert.That(_transport.Dialled.Count, Is.EqualTo(1), "inside the backoff nothing is dialled");

            SetLink("w1", "NextDialAt", 0);
            EnsureLink("w1");
            Assert.That(_transport.Dialled.Count, Is.EqualTo(2), "the link is still wanted, so the worker is dialled again");
        }

        [Test]
        public void ADialThatNeverFinishesIsAbandonedAndTriedAgain()
        {
            EnsureLink("w1");
            int first = _transport.Dialled[0];
            EnsureLink("w1");
            Assert.That(_transport.Dialled.Count, Is.EqualTo(1), "a dial in flight is left alone");

            SetLink("w1", "DialStartedAt", -1000);
            SetLink("w1", "NextDialAt", 0);
            EnsureLink("w1");
            Assert.That(_transport.Closed, Does.Contain(first), "the hung dial is closed");
            Assert.That(_transport.Dialled.Count, Is.EqualTo(2));
        }

        [Test]
        public void ADeadWorkerIsNotDialledAgain()
        {
            EnsureLink("w1");
            Deliver(TransportEvent.Disconnected(_transport.Dialled[0], TransportDisconnectReason.ClosedByRemote));
            _plane.UnregisterWorker("w1");

            SetLink("w1", "NextDialAt", 0);
            EnsureLink("w1");
            Assert.That(_transport.Dialled.Count, Is.EqualTo(1));
        }
    }
}
