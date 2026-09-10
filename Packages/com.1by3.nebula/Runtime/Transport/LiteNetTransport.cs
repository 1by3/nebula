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

        public LiteNetTransport(string name)
        {
            Name = name;
            _net = new NetManager(this)
            {
                ChannelsCount = 2,
                UnsyncedEvents = false,
                AutoRecycle = true,
                IPv6Enabled = false,
                DisconnectTimeout = 8000,
                UpdateTime = 5,
                PingInterval = 500,
            };
        }

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

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _peers.Remove(peer.Id);
            _handler?.Invoke(new TransportEvent(TransportEvent.Kind.Disconnected, peer.Id, default));
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
