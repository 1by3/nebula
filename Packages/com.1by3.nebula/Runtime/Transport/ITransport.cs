using System;

namespace Nebula
{
    /// <summary>
    /// Delivery classes supported by each Nebula network link.
    /// </summary>
    public enum Delivery : byte
    {
        /// <summary>Unreliable, newest-wins (transform streams).</summary>
        Sequenced = 0,
        /// <summary>Reliable, ordered (spawns, state, authority events, RPCs).</summary>
        ReliableOrdered = 1,
    }

    public readonly struct TransportEvent
    {
        public enum Kind : byte { Connected, Disconnected, Data }

        public readonly Kind Type;
        public readonly int PeerId;
        public readonly ArraySegment<byte> Data;

        public TransportEvent(Kind type, int peerId, ArraySegment<byte> data)
        {
            Type = type;
            PeerId = peerId;
            Data = data;
        }
    }

    /// <summary>
    /// Provides the network transport used by Nebula. Call <see cref="Poll"/> to receive events on the simulation
    /// thread. A transport can listen for incoming connections and connect to other processes, which lets a worker
    /// accept the gateway and worker peers while also connecting to peers.
    /// </summary>
    public interface ITransport : IDisposable
    {
        string Name { get; }
        bool IsRunning { get; }
        int LocalPort { get; }

        /// <summary>Bind and accept inbound links (port 0 = any free port).</summary>
        void Listen(int port);

        /// <summary>Start listening on no particular port (dial-out only).</summary>
        void StartClient();

        /// <summary>Dial a remote. Returns the peer id the link will use once connected.</summary>
        int Connect(string host, int port);

        void Disconnect(int peerId);

        bool IsConnected(int peerId);

        /// <summary>Round trip in milliseconds as measured by the link, or -1 if unknown.</summary>
        int RoundTripMs(int peerId);

        void Send(int peerId, Delivery delivery, ArraySegment<byte> payload);

        /// <summary>Drain the socket and deliver every pending event to <paramref name="handler"/>.</summary>
        void Poll(Action<TransportEvent> handler);

        void Stop();
    }
}
