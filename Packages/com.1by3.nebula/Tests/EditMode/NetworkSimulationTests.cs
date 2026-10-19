using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The simulated latency, jitter and loss a client can put on its link to the gateway (NEB-472). Pure C#:
    /// compiled into the package's EditMode tests. The round-trip test runs over real loopback sockets; the rest use a
    /// fake transport and a manual clock so they do not depend on timing.
    /// </summary>
    public class NetworkSimulationTests
    {
        private const double TickMs = 1000.0 / 60.0;

        // ---- over real sockets ------------------------------------------------------------------------------

        /// <summary>Measures <paramref name="samples"/> echoes of a small packet from <paramref name="client"/>, in milliseconds.</summary>
        private static List<double> MeasureRoundTrips(ITransport client, int clientPeer, ITransport server, int samples, Delivery delivery)
        {
            var results = new List<double>();
            var payload = new ArraySegment<byte>(new byte[] { 1, 2, 3, 4 });
            for (int i = 0; i < samples; i++)
            {
                var watch = Stopwatch.StartNew();
                client.Send(clientPeer, delivery, payload);
                client.Flush();
                bool echoed = false, back = false;
                while (!back && watch.ElapsedMilliseconds < 3000)
                {
                    server.Poll(ev =>
                    {
                        if (ev.Type != TransportEvent.Kind.Data) return;
                        server.Send(ev.PeerId, delivery, ev.Data);
                        server.Flush();
                        echoed = true;
                    });
                    client.Poll(ev => { if (ev.Type == TransportEvent.Kind.Data) back = true; });
                    client.Flush();
                    Thread.Yield(); // Thread.Sleep(1) sleeps a whole 15 ms timer period on Windows, which would swamp the measurement
                }
                Assert.That(back, Is.True, $"echo {i} came back (server echoed: {echoed})");
                results.Add(watch.Elapsed.TotalMilliseconds);
                Thread.Sleep(20);
            }
            return results;
        }

        private static void Connect(ITransport client, ITransport server, out int clientPeer)
        {
            server.Listen(0);
            clientPeer = client.Connect("127.0.0.1", server.LocalPort);
            bool connected = false;
            var watch = Stopwatch.StartNew();
            while (!connected && watch.ElapsedMilliseconds < 5000)
            {
                server.Poll(_ => { });
                client.Poll(ev => { if (ev.Type == TransportEvent.Kind.Connected) connected = true; });
                Thread.Sleep(1);
            }
            Assert.That(connected, Is.True, "the client connected");
        }

        [TestCase(Delivery.Sequenced)]
        [TestCase(Delivery.ReliableOrdered)]
        public void AHundredMillisecondsOfSimulatedLatencyIsAHundredMillisecondRoundTripWithinOneTick(Delivery delivery)
        {
            var server = new LiteNetTransport("sim-server");
            var simulation = new NetworkSimulation(100f, 0f, 0f);
            var client = new SimulatedNetworkTransport(new LiteNetTransport("sim-client"), simulation);
            try
            {
                Connect(client, server, out int peer);
                var trips = MeasureRoundTrips(client, peer, server, 20, delivery);
                trips.Sort();
                double median = trips[trips.Count / 2];
                TestContext.WriteLine($"{delivery}: round trips (ms) min {trips[0]:F1} median {median:F1} max {trips[trips.Count - 1]:F1}; transport reports {client.RoundTripMs(peer)} ms");
                Assert.That(median, Is.EqualTo(100.0).Within(TickMs), "median round trip");
                // The echo can land a poll late; no sample may be more than two ticks off.
                Assert.That(trips[0], Is.GreaterThanOrEqualTo(100.0 - TickMs), "fastest round trip");
                Assert.That(trips[trips.Count - 1], Is.LessThanOrEqualTo(100.0 + 2 * TickMs), "slowest round trip");
                Assert.That(client.RoundTripMs(peer), Is.EqualTo(100).Within(1 + (int)Math.Ceiling(TickMs)), "RoundTripMs, which the client reads as its RTT");
            }
            finally { client.Dispose(); server.Dispose(); }
        }

        [Test]
        public void NoSimulationAddsNothing()
        {
            var server = new LiteNetTransport("sim-server");
            var client = new SimulatedNetworkTransport(new LiteNetTransport("sim-client"), new NetworkSimulation());
            try
            {
                Connect(client, server, out int peer);
                var trips = MeasureRoundTrips(client, peer, server, 10, Delivery.ReliableOrdered);
                trips.Sort();
                TestContext.WriteLine($"median round trip with the simulation off: {trips[trips.Count / 2]:F1} ms");
                Assert.That(trips[trips.Count / 2], Is.LessThan(TickMs));
            }
            finally { client.Dispose(); server.Dispose(); }
        }

        // ---- with a fake link and a manual clock ---------------------------------------------------------------

        private sealed class FakeTransport : ITransport
        {
            public readonly List<(Delivery Delivery, byte[] Bytes)> Sent = new List<(Delivery, byte[])>();
            public readonly List<TransportEvent> Incoming = new List<TransportEvent>();
            public int Rtt = 4;
            public string Name => "fake";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { }
            public void StartClient() { }
            public int Connect(string host, int port) => 1;
            public void Disconnect(int peerId) { }
            public bool IsConnected(int peerId) => true;
            public int RoundTripMs(int peerId) => Rtt;
            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
            {
                var copy = new byte[payload.Count];
                Buffer.BlockCopy(payload.Array, payload.Offset, copy, 0, payload.Count);
                Sent.Add((delivery, copy));
            }
            public void Poll(Action<TransportEvent> handler)
            {
                foreach (var ev in Incoming) handler(ev);
                Incoming.Clear();
            }
            public void Flush() { }
            public void Stop() { }
            public void Dispose() { }
        }

        private static ArraySegment<byte> Packet(byte id) => new ArraySegment<byte>(new[] { id });

        private static List<byte> Received(SimulatedNetworkTransport t)
        {
            var got = new List<byte>();
            t.Poll(ev => { if (ev.Type == TransportEvent.Kind.Data) got.Add(ev.Data.Array[ev.Data.Offset]); });
            return got;
        }

        [Test]
        public void HalfTheLatencyDelaysEachDirection()
        {
            double now = 0;
            var inner = new FakeTransport();
            var t = new SimulatedNetworkTransport(inner, new NetworkSimulation(100f, 0f, 0f), new Random(1), () => now);

            t.Send(1, Delivery.Sequenced, Packet(7));
            t.Flush();
            Assert.That(inner.Sent, Is.Empty, "held");
            now = 0.049; t.Flush();
            Assert.That(inner.Sent, Is.Empty, "still held at 49 ms");
            now = 0.050; t.Flush();
            Assert.That(inner.Sent.Count, Is.EqualTo(1), "sent after 50 ms");

            inner.Incoming.Add(new TransportEvent(TransportEvent.Kind.Data, 1, Packet(9), true));
            Assert.That(Received(t), Is.Empty, "incoming is held on arrival");
            now = 0.099; Assert.That(Received(t), Is.Empty);
            now = 0.100; Assert.That(Received(t), Is.EqualTo(new byte[] { 9 }));
            Assert.That(t.RoundTripMs(1), Is.EqualTo(104), "the inner link's 4 ms plus the simulated 100 ms");
        }

        [Test]
        public void JitterVariesTheDelayWithinHalfItEitherWayAndNeverBelowZero()
        {
            double now = 0;
            var inner = new FakeTransport();
            var t = new SimulatedNetworkTransport(inner, new NetworkSimulation(40f, 30f, 0f), new Random(5), () => now);
            for (int i = 0; i < 200; i++) t.Send(1, Delivery.ReliableOrdered, Packet((byte)i));
            // Every packet leaves between 20 - 15 = 5 ms and 20 + 15 = 35 ms.
            now = 0.0049; t.Flush();
            Assert.That(inner.Sent, Is.Empty, "nothing before 5 ms");
            now = 0.0351; t.Flush();
            Assert.That(inner.Sent.Count, Is.EqualTo(200), "everything by 35 ms");
            for (int i = 0; i < 200; i++) Assert.That(inner.Sent[i].Bytes[0], Is.EqualTo((byte)i), "reliable packets stay in order");

            var zero = new SimulatedNetworkTransport(new FakeTransport(), new NetworkSimulation(0f, 100f, 0f), new Random(5), () => now);
            for (int i = 0; i < 100; i++) zero.Send(1, Delivery.Sequenced, Packet(1));
            Assert.That(() => { now += 0.2; zero.Flush(); }, Throws.Nothing, "a jitter wider than the latency does not make a negative delay");
        }

        [Test]
        public void LossDropsSequencedPacketsAndDelaysReliableOnesByARoundTrip()
        {
            double now = 0;
            var inner = new FakeTransport();
            var t = new SimulatedNetworkTransport(inner, new NetworkSimulation(0f, 0f, 30f), new Random(11), () => now);
            const int n = 2000;
            for (int i = 0; i < n; i++) t.Send(1, Delivery.Sequenced, Packet(1));
            now = 1; t.Flush();
            double lost = 100.0 * (n - inner.Sent.Count) / n;
            TestContext.WriteLine($"lost {lost:F1}% of {n} sequenced packets at 30%");
            Assert.That(lost, Is.EqualTo(30.0).Within(4.0));

            inner.Sent.Clear();
            var reliable = new SimulatedNetworkTransport(inner, new NetworkSimulation(60f, 0f, 100f), new Random(2), () => now);
            for (int i = 0; i < 5; i++) reliable.Send(1, Delivery.ReliableOrdered, Packet((byte)i));
            now += 0.089; reliable.Flush();
            Assert.That(inner.Sent, Is.Empty, "every reliable packet was 'lost' once: 30 ms out plus a 60 ms retransmission");
            now += 0.002; reliable.Flush();
            Assert.That(inner.Sent.Count, Is.EqualTo(5), "none was dropped for good");
        }

        [Test]
        public void ALateSequencedPacketIsDroppedButALateReliableOneIsNot()
        {
            double now = 0;
            var inner = new FakeTransport();
            // A wide jitter reorders; find a seed where the second packet overtakes the first.
            int seed = 0;
            for (; seed < 200; seed++)
            {
                var probe = new FakeTransport();
                double c = 0;
                var p = new SimulatedNetworkTransport(probe, new NetworkSimulation(0f, 200f, 0f), new Random(seed), () => c);
                p.Send(1, Delivery.Sequenced, Packet(1));
                p.Send(1, Delivery.Sequenced, Packet(2));
                c = 1; p.Flush();
                if (probe.Sent.Count == 1) break;
            }
            Assert.That(seed, Is.LessThan(200), "found a seed that reorders");

            var t = new SimulatedNetworkTransport(inner, new NetworkSimulation(0f, 200f, 0f), new Random(seed), () => now);
            t.Send(1, Delivery.Sequenced, Packet(1));
            t.Send(1, Delivery.Sequenced, Packet(2));
            now = 1; t.Flush();
            Assert.That(inner.Sent.Count, Is.EqualTo(1), "the overtaken sequenced packet is dropped");
            Assert.That(inner.Sent[0].Bytes[0], Is.EqualTo(2));
            Assert.That(t.DroppedPackets, Is.EqualTo(1));

            inner.Sent.Clear();
            var r = new SimulatedNetworkTransport(inner, new NetworkSimulation(0f, 200f, 0f), new Random(seed), () => now);
            r.Send(1, Delivery.ReliableOrdered, Packet(1));
            r.Send(1, Delivery.ReliableOrdered, Packet(2));
            now += 1; r.Flush();
            Assert.That(inner.Sent.Count, Is.EqualTo(2));
            Assert.That(inner.Sent[0].Bytes[0], Is.EqualTo(1), "reliable stays in order");
        }

        [Test]
        public void SettingsChangeWhileRunningAndSanitise()
        {
            double now = 0;
            var inner = new FakeTransport();
            var settings = new NetworkSimulation();
            var t = new SimulatedNetworkTransport(inner, settings, new Random(1), () => now);
            t.Send(1, Delivery.ReliableOrdered, Packet(1));
            Assert.That(inner.Sent.Count, Is.EqualTo(1), "off: sent straight through");

            settings.Set(200f, 0f, 0f);
            t.Send(1, Delivery.ReliableOrdered, Packet(2));
            Assert.That(inner.Sent.Count, Is.EqualTo(1), "on: held");
            settings.Set(0f, 0f, 0f);
            t.Send(1, Delivery.ReliableOrdered, Packet(3));
            Assert.That(inner.Sent.Count, Is.EqualTo(1), "turned off: a packet queued behind a held one waits its turn, so order holds");
            now = 0.1; t.Flush();
            Assert.That(inner.Sent.Count, Is.EqualTo(3));

            settings.Set(-5f, float.NaN, 400f);
            Assert.That(settings.LatencyMs, Is.EqualTo(0f));
            Assert.That(settings.JitterMs, Is.EqualTo(0f));
            Assert.That(settings.LossPercent, Is.EqualTo(100f));
            Assert.That(new NetworkSimulation().ToString(), Is.EqualTo("off"));
            Assert.That(new NetworkSimulation(100f, 20f, 2f).ToString(), Is.EqualTo("100 ms round trip, 20 ms jitter, 2% loss"));
        }
    }
}
