using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The loop-phase latency of an input that travels client, gateway, worker and back as an owner RPC (NEB-475): a
    /// worker that polls its network before it simulates, the frame rate of a worker or gateway process, and the
    /// flushes that put a call on the wire as soon as it is sent. The last test measures the part of the round trip that
    /// is not network time over real loopback sockets.
    /// </summary>
    public sealed class LoopLatencyTests
    {
        private const double TickMs = 1000.0 / 60.0;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        // ---- configuration --------------------------------------------------------------------------------------

        [Test]
        public void TheServerFrameRateIsNeverBelowTheTickRateAndIsCapped()
        {
            Assert.AreEqual(NetworkTime.TickRate, NebulaConfig.ServerFrameRateOf(null));
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                Assert.AreEqual(240, config.ServerFrameRate, "default: four loops per tick, as the standalone gateway runs");
                Assert.AreEqual(240, NebulaConfig.ServerFrameRateOf(config));
                config.ServerFrameRate = 0;
                Assert.AreEqual(NetworkTime.TickRate, NebulaConfig.ServerFrameRateOf(config), "0 keeps the old behavior");
                config.ServerFrameRate = 30;
                Assert.AreEqual(NetworkTime.TickRate, NebulaConfig.ServerFrameRateOf(config));
                config.ServerFrameRate = 100000;
                Assert.AreEqual(1000, NebulaConfig.ServerFrameRateOf(config));
                Assert.IsTrue(config.WorkerPollBeforeTick);
                Assert.IsTrue(config.FlushRpcsImmediately);
            }
            finally { Object.DestroyImmediate(config); }
        }

        // ---- the worker polls before it simulates ---------------------------------------------------------------

        private sealed class OrderTransport : ITransport
        {
            public readonly List<string> Log;
            public OrderTransport(List<string> log) { Log = log; }
            public string Name => "order";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { }
            public void StartClient() { }
            public int Connect(string host, int port) => 1;
            public void Disconnect(int peerId) { }
            public bool IsConnected(int peerId) => true;
            public int RoundTripMs(int peerId) => 0;
            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload) { }
            public void Poll(Action<TransportEvent> handler) { Log.Add("poll"); }
            public void Flush() { Log.Add("flush"); }
            public void Stop() { }
            public void Dispose() { }
        }

        private GameObject _host;
        private NebulaWorker _worker;
        private readonly List<string> _log = new List<string>();

        private void StartWorker(bool pollBeforeTick)
        {
            _host = new GameObject("loop-latency-worker");
            _worker = _host.AddComponent<NebulaWorker>();
            typeof(NebulaWorker).GetField("_transport", Flags).SetValue(_worker, new OrderTransport(_log));
            typeof(NebulaWorker).GetField("<IsListening>k__BackingField", Flags).SetValue(_worker, true);
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            config.WorkerPollBeforeTick = pollBeforeTick;
            typeof(NebulaWorker).GetField("<Config>k__BackingField", Flags).SetValue(_worker, config);
            _worker.TickForTest = tick => _log.Add("tick");
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
            _host = null;
            _log.Clear();
            NetworkTime.Tick = 0;
        }

        private void FixedUpdate() => typeof(NebulaWorker).GetMethod("FixedUpdate", Flags).Invoke(_worker, null);

        [Test]
        public void AWorkerPollsItsNetworkBeforeTheTickSoAnInputThatJustArrivedIsSeenByIt()
        {
            StartWorker(pollBeforeTick: true);
            FixedUpdate();
            CollectionAssert.AreEqual(new[] { "poll", "tick" }, _log);
        }

        [Test]
        public void AWorkerPollsOncePerFrameEvenWhenUnityRunsSeveralFixedUpdatesInIt()
        {
            StartWorker(pollBeforeTick: true);
            FixedUpdate();
            FixedUpdate(); // same Time.frameCount: a catch-up burst
            int polls = _log.FindAll(e => e == "poll").Count;
            Assert.AreEqual(1, polls);
        }

        [Test]
        public void WithThePollBeforeTickOffTheWorkerOnlyPollsInUpdateAsBefore()
        {
            StartWorker(pollBeforeTick: false);
            FixedUpdate();
            CollectionAssert.AreEqual(new[] { "tick" }, _log);
        }

        // ---- the non-network part of input -> owner RPC ---------------------------------------------------------

        /// <summary>
        /// One input travelling client, gateway, worker and back, with the gateway and the worker each running a loop
        /// of their own period and phase on this thread, over real loopback sockets. The worker answers from the loop
        /// that polls the input (as <c>OnServerInputReceived</c> does) and either flushes at once or leaves the
        /// packet for the end of its next tick. Returns the milliseconds from the client's send to the answer's
        /// arrival.
        /// </summary>
        private static double RoundTrip(LiteNetTransport client, int clientPeer, LiteNetTransport gateway, int gwClientPeer, int gwWorkerPeer,
            LiteNetTransport worker, int workerPeer, double gatewayPeriodMs, double workerPeriodMs, bool flushAnswerAtOnce, Random random)
        {
            var clock = Stopwatch.StartNew();
            double nextGateway = random.NextDouble() * gatewayPeriodMs;
            double nextWorker = random.NextDouble() * workerPeriodMs;
            double nextTick = random.NextDouble() * TickMs;
            bool answerWaitsForTick = false, arrived = false;
            var payload = new ArraySegment<byte>(new byte[] { 1, 2, 3, 4 });
            double sentAt = clock.Elapsed.TotalMilliseconds;
            client.Send(clientPeer, Delivery.Sequenced, payload);
            client.Flush();

            while (!arrived && clock.Elapsed.TotalMilliseconds < 2000)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                client.Poll(ev => { if (ev.Type == TransportEvent.Kind.Data) arrived = true; });
                if (now >= nextGateway)
                {
                    nextGateway += gatewayPeriodMs;
                    gateway.Poll(ev =>
                    {
                        if (ev.Type != TransportEvent.Kind.Data) return;
                        gateway.Send(ev.PeerId == gwClientPeer ? gwWorkerPeer : gwClientPeer, Delivery.Sequenced, ev.Data);
                    });
                    gateway.Flush();
                }
                if (now >= nextWorker)
                {
                    nextWorker += workerPeriodMs;
                    worker.Poll(ev =>
                    {
                        if (ev.Type != TransportEvent.Kind.Data) return;
                        worker.Send(workerPeer, Delivery.Sequenced, ev.Data);
                        if (flushAnswerAtOnce) worker.Flush(); else answerWaitsForTick = true;
                    });
                }
                if (now >= nextTick)
                {
                    nextTick += TickMs;
                    if (answerWaitsForTick) { worker.Flush(); answerWaitsForTick = false; }
                }
                Thread.Yield();
            }
            Assert.That(arrived, Is.True, "the answer came back");
            return clock.Elapsed.TotalMilliseconds - sentAt;
        }

        private static void Drain(params ITransport[] transports)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 30)
                foreach (var t in transports) t.Poll(_ => { });
        }

        [Test]
        [Category("Timing")]
        public void TheLoopPhasePartOfInputToOwnerRpcDropsByMoreThanATickWithTheNewDefaults()
        {
            var gateway = new LiteNetTransport("loop-gateway");
            var client = new LiteNetTransport("loop-client");
            var worker = new LiteNetTransport("loop-worker");
            try
            {
                gateway.Listen(0);
                int gwClientPeer = -1, gwWorkerPeer = -1;
                bool clientUp = false, workerUp = false;
                int clientPeer = client.Connect("127.0.0.1", gateway.LocalPort);
                var watch = Stopwatch.StartNew();
                while ((!clientUp || gwClientPeer < 0) && watch.ElapsedMilliseconds < 5000)
                {
                    gateway.Poll(ev => { if (ev.Type == TransportEvent.Kind.Connected && gwClientPeer < 0) gwClientPeer = ev.PeerId; });
                    client.Poll(ev => { if (ev.Type == TransportEvent.Kind.Connected) clientUp = true; });
                    Thread.Sleep(1);
                }
                int workerPeer = worker.Connect("127.0.0.1", gateway.LocalPort);
                while ((!workerUp || gwWorkerPeer < 0) && watch.ElapsedMilliseconds < 10000)
                {
                    gateway.Poll(ev => { if (ev.Type == TransportEvent.Kind.Connected && gwWorkerPeer < 0 && ev.PeerId != gwClientPeer) gwWorkerPeer = ev.PeerId; });
                    worker.Poll(ev => { if (ev.Type == TransportEvent.Kind.Connected) workerUp = true; });
                    Thread.Sleep(1);
                }
                Assert.That(clientUp && workerUp && gwClientPeer >= 0 && gwWorkerPeer >= 0, Is.True, "the three links came up");

                var before = new List<double>();
                var after = new List<double>();
                var random = new Random(475);
                for (int i = 0; i < 40; i++)
                {
                    // Before: Unity-hosted loops at the tick rate, the worker's answer left for the end of its tick.
                    before.Add(RoundTrip(client, clientPeer, gateway, gwClientPeer, gwWorkerPeer, worker, workerPeer, TickMs, TickMs, false, random));
                    Drain(client, gateway, worker);
                    // After: loops at four times the tick rate, the answer flushed as it is sent.
                    after.Add(RoundTrip(client, clientPeer, gateway, gwClientPeer, gwWorkerPeer, worker, workerPeer, TickMs / 4, TickMs / 4, true, random));
                    Drain(client, gateway, worker);
                }
                before.Sort();
                after.Sort();
                double p50Before = before[before.Count / 2], p50After = after[after.Count / 2];
                double p95Before = before[(int)(before.Count * 0.95)], p95After = after[(int)(after.Count * 0.95)];
                TestContext.WriteLine($"input -> owner RPC over loopback, without network time: before p50 {p50Before:F1} ms p95 {p95Before:F1} ms; after p50 {p50After:F1} ms p95 {p95After:F1} ms; saved p50 {p50Before - p50After:F1} ms p95 {p95Before - p95After:F1} ms (one tick is {TickMs:F1} ms)");
                Assert.That(p50Before - p50After, Is.GreaterThanOrEqualTo(TickMs), "the median drops by at least one tick");
                Assert.That(p95Before - p95After, Is.GreaterThanOrEqualTo(TickMs), "and so does the 95th percentile");
            }
            finally { client.Dispose(); gateway.Dispose(); worker.Dispose(); }
        }
    }
}
