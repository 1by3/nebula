using System;

namespace Nebula
{
    /// <summary>
    /// Several transports behind one <see cref="ITransport"/>, so the gateway accepts native clients over UDP and
    /// browsers over WebRTC without caring which link a peer arrived on. The first transport is the primary:
    /// <see cref="Listen"/>, <see cref="StartClient"/> and <see cref="Connect"/> go to it, and the others accept on
    /// ports of their own that were opened before they were added. A peer id is the inner id times the number of
    /// transports plus the transport's index.
    /// </summary>
    public sealed class MultiTransport : ITransport
    {
        private readonly ITransport[] _transports;
        private readonly Action<TransportEvent>[] _forwarders;
        private Action<TransportEvent> _handler;

        public MultiTransport(params ITransport[] transports)
        {
            if (transports == null || transports.Length == 0) throw new ArgumentException("at least one transport is required", nameof(transports));
            _transports = transports;
            _forwarders = new Action<TransportEvent>[transports.Length];
            for (int i = 0; i < transports.Length; i++)
            {
                int index = i;
                _forwarders[i] = ev => _handler?.Invoke(new TransportEvent(ev.Type, Outer(ev.PeerId, index), ev.Data));
            }
        }

        public string Name => _transports[0].Name;
        public bool IsRunning => _transports[0].IsRunning;
        public int LocalPort => _transports[0].LocalPort;

        public void Listen(int port) => _transports[0].Listen(port);

        public void StartClient() => _transports[0].StartClient();

        public int Connect(string host, int port) => Outer(_transports[0].Connect(host, port), 0);

        public void Disconnect(int peerId)
        {
            if (TryInner(peerId, out var transport, out int inner)) transport.Disconnect(inner);
        }

        public bool IsConnected(int peerId) => TryInner(peerId, out var transport, out int inner) && transport.IsConnected(inner);

        public int RoundTripMs(int peerId) => TryInner(peerId, out var transport, out int inner) ? transport.RoundTripMs(inner) : -1;

        public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
        {
            if (TryInner(peerId, out var transport, out int inner)) transport.Send(inner, delivery, payload);
        }

        public void Poll(Action<TransportEvent> handler)
        {
            _handler = handler;
            try
            {
                for (int i = 0; i < _transports.Length; i++) _transports[i].Poll(_forwarders[i]);
            }
            finally { _handler = null; }
        }

        public void Flush()
        {
            foreach (var t in _transports) t.Flush();
        }

        public void Stop()
        {
            foreach (var t in _transports) t.Stop();
        }

        public void Dispose()
        {
            foreach (var t in _transports) t.Dispose();
        }

        private int Outer(int inner, int index) => inner * _transports.Length + index;

        private bool TryInner(int peerId, out ITransport transport, out int inner)
        {
            transport = null;
            inner = -1;
            if (peerId < 0) return false;
            transport = _transports[peerId % _transports.Length];
            inner = peerId / _transports.Length;
            return true;
        }
    }
}
