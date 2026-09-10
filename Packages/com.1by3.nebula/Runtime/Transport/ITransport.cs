using System;

namespace Nebula
{
    /// <summary>
    /// Delivery classes every Nebula link needs. Maps 1:1 onto QUIC streams/datagrams and onto
    /// LiteNetLib delivery methods, so swapping the transport later is contained to one class.
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
    /// The seam between Nebula and whatever moves bytes. Sockets, reliability and sequencing are bought
    /// (LiteNetLib today), replication and authority are built on top. Poll-driven: nothing is raised
    /// outside <see cref="Poll"/>, so nothing mutates the world behind the simulation loop's back.
    /// A single transport can both listen and dial out, which is what workers need (they accept the
    /// gateway and their peers, and dial peers themselves).
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
