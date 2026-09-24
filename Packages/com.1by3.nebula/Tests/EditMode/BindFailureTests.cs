using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// A gateway or worker whose port another process holds (another mesh on the same machine) fails once and
    /// cleanly: one error naming the port, a queryable failed state, and no exception from every frame after
    /// (NEB-317).
    /// </summary>
    public sealed class BindFailureTests
    {
        private UdpClient _squatter;
        private int _port;
        private GameObject _go;
        private NebulaConfig _config;
        private LocalControlPlane _plane;

        [SetUp]
        public void SetUp()
        {
            _squatter = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            _port = ((IPEndPoint)_squatter.Client.LocalEndPoint).Port;
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            _config.EncryptClients = false;
            _plane = new LocalControlPlane();
            _plane.Connect();
            _go = new GameObject("bind-failure");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
            Object.DestroyImmediate(_config);
            _plane.Dispose();
            _squatter.Dispose();
            NebulaRuntime.RpcSink = null;
            NebulaRuntime.LocalWorkerId = "";
            NebulaRuntime.LocalWorkerIndex = 0;
        }

        private static void Tick(Component component) =>
            component.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(component, null);

        [Test]
        public void GatewayOnATakenPortFailsOnceAndStaysQuiet()
        {
            _config.GatewayPort = (ushort)_port;
            var gateway = _go.AddComponent<NebulaGateway>();
            LogAssert.Expect(LogType.Error, new Regex($"could not bind udp/{_port}: another process is using it, most likely another Nebula mesh"));

            Assert.DoesNotThrow(() => gateway.Initialize(_config, _plane));

            Assert.IsTrue(gateway.Failed);
            Assert.IsFalse(gateway.IsListening);
            StringAssert.Contains($"udp/{_port}", gateway.FailureReason);
            Assert.IsFalse(gateway.enabled);
            Assert.DoesNotThrow(() => { Tick(gateway); Tick(gateway); });
        }

        [Test]
        public void WorkerOnATakenPortFailsOnceAndStaysQuiet()
        {
            // No -nebula-port in a test run, so the worker listens on WorkerBasePort + index 1.
            _config.WorkerBasePort = (ushort)(_port - 1);
            var worker = _go.AddComponent<NebulaWorker>();
            LogAssert.Expect(LogType.Error, new Regex($"could not bind udp/{_port}"));

            Assert.DoesNotThrow(() => worker.Initialize(_config, _plane));

            Assert.IsTrue(worker.Failed);
            Assert.IsFalse(worker.IsListening);
            Assert.IsFalse(worker.enabled);
            Assert.DoesNotThrow(() => { Tick(worker); Tick(worker); });
        }

        [Test]
        public void GatewayOnAFreePortListens()
        {
            _squatter.Dispose();
            _config.GatewayPort = (ushort)_port;
            var gateway = _go.AddComponent<NebulaGateway>();

            gateway.Initialize(_config, _plane);

            Assert.IsFalse(gateway.Failed);
            Assert.IsTrue(gateway.IsListening);
        }
    }
}
