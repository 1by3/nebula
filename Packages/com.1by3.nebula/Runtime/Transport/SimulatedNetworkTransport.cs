using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Nebula
{
    /// <summary>
    /// Network conditions to simulate on one link, for testing prediction and lag compensation on one machine.
    /// <see cref="LatencyMs"/> is the added <b>round trip</b>: half of it delays each packet the link sends and the
    /// other half delays each packet it receives. All values are clamped to a sensible range on use.
    /// </summary>
    public sealed class NetworkSimulation
    {
        /// <summary>The most latency, jitter or loss the simulation accepts: 5 seconds, or 100 percent.</summary>
        public const float MaxMs = 5000f;

        private float _latencyMs, _jitterMs, _lossPercent;

        public NetworkSimulation() { }

        /// <summary>A simulation of <paramref name="latencyMs"/> of added round trip, <paramref name="jitterMs"/> of jitter and <paramref name="lossPercent"/> of loss.</summary>
        public NetworkSimulation(float latencyMs, float jitterMs, float lossPercent)
        {
            Set(latencyMs, jitterMs, lossPercent);
        }

        /// <summary>Added round-trip time in milliseconds: <c>LatencyMs / 2</c> in each direction. 0 adds none.</summary>
        public float LatencyMs => _latencyMs;

        /// <summary>
        /// Jitter in milliseconds: each packet's delay in each direction varies by a random amount between
        /// <c>-JitterMs / 2</c> and <c>+JitterMs / 2</c> around its half of <see cref="LatencyMs"/>, never below zero.
        /// A packet sent after another can therefore arrive first; a <see cref="Delivery.Sequenced"/> packet that is
        /// overtaken is dropped, as a real sequenced link does, and reliable packets stay in order.
        /// </summary>
        public float JitterMs => _jitterMs;

        /// <summary>
        /// Percent of packets lost in each direction, 0 to 100. A lost <see cref="Delivery.Sequenced"/> packet never
        /// arrives. A lost reliable packet arrives one extra round trip late, as a retransmission would, and holds up
        /// the reliable packets behind it.
        /// </summary>
        public float LossPercent => _lossPercent;

        /// <summary>Whether any of the three values would change the link.</summary>
        public bool Active => _latencyMs > 0f || _jitterMs > 0f || _lossPercent > 0f;

        /// <summary>
        /// Sets all three values. Negative and non-numeric values count as 0; latency and jitter are capped at
        /// <see cref="MaxMs"/> and loss at 100. The change applies to packets sent or received from now on.
        /// </summary>
        public void Set(float latencyMs, float jitterMs, float lossPercent)
        {
            _latencyMs = Clamp(latencyMs, MaxMs);
            _jitterMs = Clamp(jitterMs, MaxMs);
            _lossPercent = Clamp(lossPercent, 100f);
        }

        private static float Clamp(float value, float max) => float.IsNaN(value) || value <= 0f ? 0f : Math.Min(value, max);

        /// <summary>Whether this build may simulate network conditions: the Editor and development builds can; release builds cannot.</summary>
        public static bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || NEBULA_SERVICE
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>A short description for logs and overlays, for example <c>100 ms round trip, 20 ms jitter, 2% loss</c>; <c>off</c> when inactive.</summary>
        public override string ToString()
        {
            if (!Active) return "off";
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return string.Format(c, "{0:0.#} ms round trip, {1:0.#} ms jitter, {2:0.#}% loss", _latencyMs, _jitterMs, _lossPercent);
        }
    }

    /// <summary>
    /// An <see cref="ITransport"/> that adds the latency, jitter and packet loss of a <see cref="NetworkSimulation"/>
    /// to every link of another transport, in both directions, without touching the remote side. Wrap a client's
    /// transport to see how the game behaves on a slow link while the server runs on the same machine. It does not
    /// depend on LiteNetLib's debug-only simulation, so it works in the Editor and in development builds alike.
    ///
    /// <para>Outgoing packets are held for half the configured latency before the inner transport sends them;
    /// incoming packets are held for the other half before <see cref="Poll"/> delivers them. A request and its reply
    /// therefore take the configured latency longer, plus the link's own time. <see cref="RoundTripMs"/> adds the
    /// configured latency to the inner link's measurement, because the inner transport's own keep-alive packets never
    /// pass through the simulation. Connection and disconnection events are not delayed, so a link opens and closes at
    /// its real time. Held packets are released from <see cref="Poll"/> and <see cref="Flush"/>, so the effective
    /// resolution is how often your game calls them: once per tick.</para>
    /// </summary>
    public sealed class SimulatedNetworkTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly NetworkSimulation _settings;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Func<double> _now;
        private readonly Random _random;
        private readonly Dictionary<int, Lane> _out = new Dictionary<int, Lane>();
        private readonly Dictionary<int, Lane> _in = new Dictionary<int, Lane>();
        private readonly Action<TransportEvent> _forward;
        private Action<TransportEvent> _handler;

        /// <summary>Wraps <paramref name="inner"/> so its links obey <paramref name="settings"/>, which can be changed while the transport runs.</summary>
        public SimulatedNetworkTransport(ITransport inner, NetworkSimulation settings) : this(inner, settings, null, null) { }

        /// <summary>As the public constructor, with a seeded random source and a clock in seconds for tests.</summary>
        internal SimulatedNetworkTransport(ITransport inner, NetworkSimulation settings, Random random, Func<double> now)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _settings = settings ?? new NetworkSimulation();
            _random = random ?? new Random();
            _now = now;
            _forward = OnInner;
        }

        /// <summary>The conditions this transport applies. Change them with <see cref="NetworkSimulation.Set"/>.</summary>
        public NetworkSimulation Settings => _settings;

        /// <summary>Packets dropped by the simulated loss, or overtaken on a sequenced link, since this transport was created.</summary>
        public long DroppedPackets { get; private set; }

        private double Now => _now != null ? _now() : _clock.Elapsed.TotalSeconds;

        public string Name => _inner.Name;
        public bool IsRunning => _inner.IsRunning;
        public int LocalPort => _inner.LocalPort;
        public void Listen(int port) => _inner.Listen(port);
        public void StartClient() => _inner.StartClient();
        public int Connect(string host, int port) => _inner.Connect(host, port);
        public bool IsConnected(int peerId) => _inner.IsConnected(peerId);
        public void Disconnect(int peerId) => _inner.Disconnect(peerId);

        /// <summary>The inner link's round trip plus the simulated latency; -1 when the inner transport cannot measure.</summary>
        public int RoundTripMs(int peerId)
        {
            int inner = _inner.RoundTripMs(peerId);
            if (inner < 0) return inner;
            return inner + (int)Math.Round(_settings.LatencyMs);
        }

        public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
        {
            if (!_settings.Active && !HasQueued(_out, peerId))
            {
                _inner.Send(peerId, delivery, payload);
                return;
            }
            Enqueue(_out, peerId, delivery, payload);
        }

        public void Poll(Action<TransportEvent> handler)
        {
            _handler = handler;
            try
            {
                ReleaseOutgoing();
                _inner.Poll(_forward);
                ReleaseIncoming();
            }
            finally { _handler = null; }
        }

        public void Flush()
        {
            ReleaseOutgoing();
            _inner.Flush();
        }

        public void Stop()
        {
            _out.Clear();
            _in.Clear();
            _inner.Stop();
        }

        public void Dispose()
        {
            _out.Clear();
            _in.Clear();
            _inner.Dispose();
        }

        // ---- simulation ---------------------------------------------------------------------------------------

        private void OnInner(TransportEvent ev)
        {
            switch (ev.Type)
            {
                case TransportEvent.Kind.Data:
                    if (!_settings.Active && !HasQueued(_in, ev.PeerId)) _handler?.Invoke(ev);
                    else Enqueue(_in, ev.PeerId, ev.Sequenced ? Delivery.Sequenced : Delivery.ReliableOrdered, ev.Data);
                    return;
                case TransportEvent.Kind.Disconnected:
                    _out.Remove(ev.PeerId);
                    _in.Remove(ev.PeerId);
                    _handler?.Invoke(ev);
                    return;
                default:
                    _handler?.Invoke(ev);
                    return;
            }
        }

        private static bool HasQueued(Dictionary<int, Lane> lanes, int peerId) => lanes.TryGetValue(peerId, out var lane) && lane.Packets.Count > 0;

        private void Enqueue(Dictionary<int, Lane> lanes, int peerId, Delivery delivery, ArraySegment<byte> payload)
        {
            if (!lanes.TryGetValue(peerId, out var lane)) lanes[peerId] = lane = new Lane();
            bool sequenced = delivery == Delivery.Sequenced;
            double now = Now;
            double half = _settings.LatencyMs * 0.5;
            double delay = half;
            if (_settings.JitterMs > 0f) delay += (_random.NextDouble() - 0.5) * _settings.JitterMs;
            if (delay < 0) delay = 0;

            bool lost = _settings.LossPercent > 0f && _random.NextDouble() * 100.0 < _settings.LossPercent;
            if (lost)
            {
                if (sequenced) { DroppedPackets++; return; }
                // Reliable: a retransmission, one round trip later.
                delay += Math.Max(_settings.LatencyMs, 1f);
            }

            var bytes = new byte[payload.Count];
            Buffer.BlockCopy(payload.Array, payload.Offset, bytes, 0, payload.Count);
            double due = now + delay / 1000.0;
            // Reliable traffic stays in order: a packet is never delivered before the one ahead of it.
            if (!sequenced && due < lane.LastDue) due = lane.LastDue;
            if (!sequenced) lane.LastDue = due;
            lane.Packets.Add(new Held { Due = due, Order = lane.NextOrder++, Delivery = delivery, Sequenced = sequenced, Bytes = bytes });
        }

        private void ReleaseOutgoing()
        {
            if (_out.Count == 0) return;
            double now = Now;
            foreach (var pair in _out)
            {
                var lane = pair.Value;
                Release(lane, now, held =>
                    _inner.Send(pair.Key, held.Delivery, new ArraySegment<byte>(held.Bytes)));
            }
        }

        private void ReleaseIncoming()
        {
            if (_in.Count == 0) return;
            double now = Now;
            foreach (var pair in _in)
            {
                int peer = pair.Key;
                Release(pair.Value, now, held =>
                    _handler?.Invoke(new TransportEvent(TransportEvent.Kind.Data, peer, new ArraySegment<byte>(held.Bytes))));
            }
        }

        private void Release(Lane lane, double now, Action<Held> deliver)
        {
            if (lane.Packets.Count == 0) return;
            // Earliest due first, then in the order they were queued. The list is short (one link's flight time).
            lane.Packets.Sort((a, b) => a.Due != b.Due ? a.Due.CompareTo(b.Due) : a.Order.CompareTo(b.Order));
            int released = 0;
            while (released < lane.Packets.Count && lane.Packets[released].Due <= now)
            {
                var held = lane.Packets[released++];
                if (held.Sequenced)
                {
                    // A sequenced link drops a packet that an already delivered, newer one overtook.
                    if (held.Order < lane.LastSequencedOrder) { DroppedPackets++; continue; }
                    lane.LastSequencedOrder = held.Order;
                }
                deliver(held);
            }
            lane.Packets.RemoveRange(0, released);
        }

        private struct Held
        {
            public double Due;
            public long Order;
            public Delivery Delivery;
            public bool Sequenced;
            public byte[] Bytes;
        }

        private sealed class Lane
        {
            public readonly List<Held> Packets = new List<Held>();
            public long NextOrder;
            public long LastSequencedOrder = -1;
            public double LastDue;
        }
    }
}
