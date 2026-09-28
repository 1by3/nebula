using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Nebula
{
    /// <summary>
    /// <see cref="ITransport"/> over LiteNetLib. Sequenced maps to <see cref="DeliveryMethod.Sequenced"/>,
    /// everything else to <see cref="DeliveryMethod.ReliableOrdered"/>. Two LiteNetLib channels are used so a
    /// large reliable message cannot head-of-line block the transform stream.
    /// Events are queued by LiteNetLib and drained on the caller's thread inside <see cref="Poll"/>.
    /// </summary>
    public sealed class LiteNetTransport : ITransport, INetEventListener
    {
        public const string ConnectionKey = "nebula-v1";

        private readonly NetManager _net;
        private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();
        private readonly List<TransportEvent> _pending = new List<TransportEvent>();
        private readonly List<byte[]> _pendingBuffers = new List<byte[]>();
        private Action<TransportEvent> _handler;
        private long _oversized;

        /// <summary>The disconnect timeout a transport gets when none is given: 8 s without a packet ends a link.</summary>
        public const int DefaultDisconnectTimeoutMs = 8000;
        /// <summary>The keep-alive ping interval a transport gets when none is given.</summary>
        public const int DefaultPingIntervalMs = 500;

        public LiteNetTransport(string name) : this(name, DefaultDisconnectTimeoutMs, DefaultPingIntervalMs) { }

        /// <summary>
        /// A transport whose links end after <paramref name="disconnectTimeoutMs"/> without a packet from the
        /// remote, and that sends a keep-alive ping every <paramref name="pingIntervalMs"/>. The ping interval is
        /// kept to at least 10 ms and the timeout to at least twice the ping interval.
        /// </summary>
        public LiteNetTransport(string name, int disconnectTimeoutMs, int pingIntervalMs)
        {
            Name = name;
            int ping = Math.Max(10, pingIntervalMs);
            _net = new NetManager(this)
            {
                ChannelsCount = 2,
                UnsyncedEvents = false,
                AutoRecycle = true,
                IPv6Enabled = false,
                DisconnectTimeout = Math.Max(ping * 2, disconnectTimeoutMs),
                UpdateTime = 5,
                PingInterval = ping,
            };
        }

        /// <summary>Milliseconds without a packet from the remote after which a link ends with <see cref="TransportDisconnectReason.Timeout"/>.</summary>
        public int DisconnectTimeoutMs => _net.DisconnectTimeout;
        /// <summary>Milliseconds between keep-alive pings.</summary>
        public int PingIntervalMs => _net.PingInterval;

        public string Name { get; }
        public bool IsRunning => _net.IsRunning;
        public int LocalPort => _net.LocalPort;

        public void Listen(int port)
        {
            if (!_net.Start(port))
                throw new InvalidOperationException($"[{Name}] could not bind UDP port {port}");
        }

        public void StartClient()
        {
            if (!_net.IsRunning && !_net.Start())
                throw new InvalidOperationException($"[{Name}] could not start socket");
        }

        public int Connect(string host, int port)
        {
            if (!_net.IsRunning) StartClient();
            var peer = _net.Connect(host, port, ConnectionKey);
            if (peer == null) throw new InvalidOperationException($"[{Name}] connect to {host}:{port} refused locally");
            _peers[peer.Id] = peer;
            return peer.Id;
        }

        public void Disconnect(int peerId)
        {
            if (_peers.TryGetValue(peerId, out var peer)) peer.Disconnect();
        }

        public bool IsConnected(int peerId)
        {
            return _peers.TryGetValue(peerId, out var peer) && peer.ConnectionState == ConnectionState.Connected;
        }

        public int RoundTripMs(int peerId)
        {
            return _peers.TryGetValue(peerId, out var peer) ? peer.RoundTripTime : -1;
        }

        public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
        {
            if (!_peers.TryGetValue(peerId, out var peer) || peer.ConnectionState != ConnectionState.Connected) return;
            try
            {
                if (delivery == Delivery.Sequenced)
                    peer.Send(payload.Array, payload.Offset, payload.Count, 0, DeliveryMethod.Sequenced);
                else
                    peer.Send(payload.Array, payload.Offset, payload.Count, 1, DeliveryMethod.ReliableOrdered);
            }
            catch (TooBigPacketException e)
            {
                // A Sequenced packet over the peer's MTU cannot be fragmented. Drop just this packet for this peer
                // rather than letting the exception abort the caller's tick or its broadcast loop.
                _oversized++;
                if ((_oversized & (_oversized - 1)) == 0)
                    NebulaLog.Warn($"[{Name}] dropped {payload.Count}-byte {delivery} packet to peer {peerId} ({_oversized} so far): {e.Message}");
            }
        }

        public void Poll(Action<TransportEvent> handler)
        {
            _handler = handler;
            _net.PollEvents();
            _handler = null;
        }

        public void Flush() => _net.TriggerUpdate();

        public void Stop()
        {
            _net.Stop();
            _peers.Clear();
        }

        public void Dispose() => Stop();

        // ---- INetEventListener --------------------------------------------------------------

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            _peers[peer.Id] = peer;
            _handler?.Invoke(new TransportEvent(TransportEvent.Kind.Connected, peer.Id, default));
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, LiteNetLib.DisconnectInfo disconnectInfo)
        {
            _peers.Remove(peer.Id);
            _handler?.Invoke(TransportEvent.Disconnected(peer.Id, ReasonOf(disconnectInfo.Reason, disconnectInfo.SocketErrorCode)));
        }

        /// <summary>LiteNetLib's reason for a closed link, in the transport-neutral terms of <see cref="TransportDisconnectReason"/>.</summary>
        internal static TransportDisconnectReason ReasonOf(LiteNetLib.DisconnectReason reason, SocketError socketError = SocketError.Success)
        {
            switch (reason)
            {
                case LiteNetLib.DisconnectReason.Timeout: return TransportDisconnectReason.Timeout;
                case LiteNetLib.DisconnectReason.NetworkUnreachable: return TransportDisconnectReason.NetworkUnreachable;
                // ConnectionFailed: every connect attempt went unanswered. UnknownHost: the name did not resolve.
                case LiteNetLib.DisconnectReason.ConnectionFailed:
                case LiteNetLib.DisconnectReason.HostUnreachable:
                case LiteNetLib.DisconnectReason.UnknownHost:
                    return socketError == SocketError.NetworkUnreachable || socketError == SocketError.NetworkDown
                        ? TransportDisconnectReason.NetworkUnreachable
                        : TransportDisconnectReason.HostUnreachable;
                // Reconnect: the remote accepted a new connection from this endpoint. PeerNotFound: the remote no
                // longer knows this link, typically because it restarted.
                case LiteNetLib.DisconnectReason.RemoteConnectionClose:
                case LiteNetLib.DisconnectReason.Reconnect:
                case LiteNetLib.DisconnectReason.PeerNotFound:
                    return TransportDisconnectReason.ClosedByRemote;
                case LiteNetLib.DisconnectReason.DisconnectPeerCalled: return TransportDisconnectReason.LocalRequest;
                case LiteNetLib.DisconnectReason.ConnectionRejected:
                case LiteNetLib.DisconnectReason.InvalidProtocol:
                    return TransportDisconnectReason.Rejected;
                default: return TransportDisconnectReason.Unknown;
            }
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            NebulaLog.Warn($"[{Name}] socket error {socketError} from {endPoint}");
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            // The reader is recycled after this call; hand out a view that is valid for the duration of the handler.
            var seg = new ArraySegment<byte>(reader.RawData, reader.Position, reader.AvailableBytes);
            _handler?.Invoke(new TransportEvent(TransportEvent.Kind.Data, peer.Id, seg));
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            request.AcceptIfKey(ConnectionKey);
        }
    }
}
