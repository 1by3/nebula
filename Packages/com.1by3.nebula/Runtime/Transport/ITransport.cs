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

    /// <summary>
    /// Why a transport link closed, in terms that do not depend on the transport: carried by a
    /// <see cref="TransportEvent.Kind.Disconnected"/> event in <see cref="TransportEvent.Reason"/>. A transport that
    /// cannot tell reports <see cref="Unknown"/>.
    /// </summary>
    public enum TransportDisconnectReason : byte
    {
        /// <summary>The transport did not say, or could not tell.</summary>
        Unknown = 0,
        /// <summary>This side closed the link (<see cref="ITransport.Disconnect"/> or <see cref="ITransport.Stop"/>).</summary>
        LocalRequest = 1,
        /// <summary>The link was up and the remote stopped answering for longer than the transport's disconnect timeout.</summary>
        Timeout = 2,
        /// <summary>The local network is down: no route to any host, no interface, or the socket failed.</summary>
        NetworkUnreachable = 3,
        /// <summary>The link never came up: the host did not answer, refused the connection, or its name did not resolve.</summary>
        HostUnreachable = 4,
        /// <summary>The remote closed the link, or no longer knows this side (it restarted).</summary>
        ClosedByRemote = 5,
        /// <summary>The remote refused the connection request at the transport level, for example a different connection key or protocol.</summary>
        Rejected = 6,
        /// <summary>A secure transport refused the link: the key exchange failed or the certificate did not match.</summary>
        SecurityFailure = 7,
    }

    public readonly struct TransportEvent
    {
        public enum Kind : byte { Connected, Disconnected, Data }

        public readonly Kind Type;
        public readonly int PeerId;
        public readonly ArraySegment<byte> Data;
        /// <summary>Why the link closed, for a <see cref="Kind.Disconnected"/> event; <see cref="TransportDisconnectReason.Unknown"/> otherwise.</summary>
        public readonly TransportDisconnectReason Reason;

        public TransportEvent(Kind type, int peerId, ArraySegment<byte> data)
            : this(type, peerId, data, TransportDisconnectReason.Unknown) { }

        /// <summary>An event that carries why its link closed (<paramref name="reason"/>), for <see cref="Kind.Disconnected"/>.</summary>
        public TransportEvent(Kind type, int peerId, ArraySegment<byte> data, TransportDisconnectReason reason)
        {
            Type = type;
            PeerId = peerId;
            Data = data;
            Reason = reason;
        }

        /// <summary>A <see cref="Kind.Disconnected"/> event for <paramref name="peerId"/> that says why the link closed.</summary>
        public static TransportEvent Disconnected(int peerId, TransportDisconnectReason reason)
            => new TransportEvent(Kind.Disconnected, peerId, default, reason);
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

        /// <summary>
        /// Push everything queued by <see cref="Send"/> onto the wire now instead of at the transport's next internal
        /// update. Call once per tick after the last Send, so a packet never waits for the sender's timer.
        /// </summary>
        void Flush();

        void Stop();
    }
}
