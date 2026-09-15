#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nebula
{
    /// <summary>
    /// <see cref="ITransport"/> for web builds, where there are no UDP sockets: the browser's RTCPeerConnection
    /// (WebGL/NebulaWebRtc.jslib) with two data channels agreed in advance with the gateway, reliable and ordered for
    /// <see cref="Delivery.ReliableOrdered"/> and unordered without retransmissions for <see cref="Delivery.Sequenced"/>.
    /// <see cref="Connect"/> posts the offer to <c>http://host:port/nebula/rtc</c> (https when the page is), where
    /// port is the gateway's <see cref="NebulaConfig.WebPort"/>. Dial-out only.
    /// <para>
    /// The unreliable channel is unordered, so each message on it starts with a 16-bit little-endian sequence number and
    /// a message older than the newest one delivered is dropped, as LiteNetLib does for <see cref="Delivery.Sequenced"/>.
    /// </para>
    /// </summary>
    public sealed class WebRtcClientTransport : ITransport
    {
        [DllImport("__Internal")] private static extern int NebulaRtc_Connect(string host, int port);
        [DllImport("__Internal")] private static extern int NebulaRtc_State(int id);
        [DllImport("__Internal")] private static extern int NebulaRtc_NextSize(int id);
        [DllImport("__Internal")] private static extern int NebulaRtc_NextChannel(int id);
        [DllImport("__Internal")] private static extern int NebulaRtc_Receive(int id, byte[] buffer, int capacity);
        [DllImport("__Internal")] private static extern int NebulaRtc_Send(int id, int channel, byte[] data, int offset, int length);
        [DllImport("__Internal")] private static extern int NebulaRtc_RoundTripMs(int id);
        [DllImport("__Internal")] private static extern void NebulaRtc_Close(int id);

        private const int StateOpen = 1, StateClosed = 2;
        private const int ReliableChannel = 0, UnreliableChannel = 1;
        private const int SequenceBytes = 2;

        private sealed class Link
        {
            public int Id;
            public bool Announced;
            public ushort SendSequence, ReceiveSequence;
            public bool ReceivedSequenced;
        }

        private readonly Dictionary<int, Link> _links = new Dictionary<int, Link>();
        private readonly List<Link> _scratch = new List<Link>();
        private byte[] _buffer = new byte[4096];
        private byte[] _sendBuffer = new byte[1500];

        public WebRtcClientTransport(string name) { Name = name; }

        public string Name { get; }
        public bool IsRunning { get; private set; }
        public int LocalPort => 0;

        public void Listen(int port) => throw new NotSupportedException("a browser cannot accept connections");

        public void StartClient() => IsRunning = true;

        public int Connect(string host, int port)
        {
            IsRunning = true;
            int id = NebulaRtc_Connect(host, port);
            _links[id] = new Link { Id = id };
            return id;
        }

        public void Disconnect(int peerId)
        {
            if (_links.ContainsKey(peerId)) NebulaRtc_Close(peerId);
        }

        public bool IsConnected(int peerId) => _links.ContainsKey(peerId) && NebulaRtc_State(peerId) == StateOpen;

        public int RoundTripMs(int peerId) => _links.ContainsKey(peerId) ? NebulaRtc_RoundTripMs(peerId) : -1;

        public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
        {
            if (payload.Count == 0 || !_links.TryGetValue(peerId, out var link)) return;
            if (delivery != Delivery.Sequenced)
            {
                NebulaRtc_Send(peerId, ReliableChannel, payload.Array, payload.Offset, payload.Count);
                return;
            }
            int length = payload.Count + SequenceBytes;
            if (_sendBuffer.Length < length) _sendBuffer = new byte[Math.Max(length, _sendBuffer.Length * 2)];
            ushort sequence = link.SendSequence++;
            _sendBuffer[0] = (byte)sequence;
            _sendBuffer[1] = (byte)(sequence >> 8);
            Buffer.BlockCopy(payload.Array, payload.Offset, _sendBuffer, SequenceBytes, payload.Count);
            NebulaRtc_Send(peerId, UnreliableChannel, _sendBuffer, 0, length);
        }

        public void Poll(Action<TransportEvent> handler)
        {
            // Handlers may connect or disconnect, so walk a copy.
            _scratch.Clear();
            _scratch.AddRange(_links.Values);
            foreach (var link in _scratch)
            {
                int state = NebulaRtc_State(link.Id);
                if (state == StateOpen && !link.Announced)
                {
                    link.Announced = true;
                    handler(new TransportEvent(TransportEvent.Kind.Connected, link.Id, default));
                }
                // Messages that arrived before a close are still delivered.
                while (link.Announced)
                {
                    int size = NebulaRtc_NextSize(link.Id);
                    if (size < 0) break;
                    if (size > _buffer.Length) _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
                    int channel = NebulaRtc_NextChannel(link.Id);
                    int n = NebulaRtc_Receive(link.Id, _buffer, _buffer.Length);
                    if (n < 0) break;
                    if (channel != UnreliableChannel)
                    {
                        handler(new TransportEvent(TransportEvent.Kind.Data, link.Id, new ArraySegment<byte>(_buffer, 0, n)));
                        continue;
                    }
                    if (n < SequenceBytes) continue;
                    ushort sequence = (ushort)(_buffer[0] | _buffer[1] << 8);
                    if (link.ReceivedSequenced && !IsNewer(sequence, link.ReceiveSequence)) continue; // arrived after a newer one
                    link.ReceivedSequenced = true;
                    link.ReceiveSequence = sequence;
                    handler(new TransportEvent(TransportEvent.Kind.Data, link.Id, new ArraySegment<byte>(_buffer, SequenceBytes, n - SequenceBytes)));
                }
                if (state != StateClosed) continue;
                NebulaRtc_Close(link.Id);
                _links.Remove(link.Id);
                handler(new TransportEvent(TransportEvent.Kind.Disconnected, link.Id, default));
            }
        }

        private static bool IsNewer(ushort sequence, ushort last) => sequence != last && (ushort)(sequence - last) < 0x8000;

        /// <summary>The browser puts data channel messages on the wire as they are sent.</summary>
        public void Flush() { }

        public void Stop()
        {
            foreach (int id in _links.Keys) NebulaRtc_Close(id);
            _links.Clear();
            IsRunning = false;
        }

        public void Dispose() => Stop();
    }
}
#endif
