using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// How a client treats the gateway's certificate. See <c>docs/transport-encryption.md</c> §4.
    /// </summary>
    public sealed class ClientEncryption
    {
        /// <summary>
        /// The SHA-256 of the gateway's SubjectPublicKeyInfo, as hex (colons and case are ignored). Empty accepts
        /// whatever certificate the gateway presents, which encrypts the link but does not authenticate the
        /// gateway; the client logs that once per connection.
        /// </summary>
        public string Fingerprint = "";

        /// <summary>How long the client waits for the gateway's half of the handshake before giving up.</summary>
        public float TimeoutSeconds = 5f;
    }

    /// <summary>
    /// A transport that can tell, per peer, whether the link is encrypted. <see cref="EncryptedTransport"/>,
    /// <see cref="MultiTransport"/> and the WebRTC transports implement it; the gateway asks it before it lets a
    /// client's <c>Hello</c> through when <c>NebulaConfig.RequireEncryption</c> is set.
    /// </summary>
    public interface ISecureTransport
    {
        /// <summary>Whether traffic with this peer is encrypted and its far end authenticated as far as the configuration asked.</summary>
        bool IsEncrypted(int peerId);

        /// <summary>The last handshake failure, for the connection UI. Empty when nothing has failed.</summary>
        string SecurityError { get; }
    }

    /// <summary>Whether a link is encrypted, for callers that hold a plain <see cref="ITransport"/>.</summary>
    public static class TransportSecurity
    {
        public static bool IsEncrypted(ITransport transport, int peerId) =>
            transport is ISecureTransport secure && secure.IsEncrypted(peerId);

        public static string ErrorOf(ITransport transport) =>
            transport is ISecureTransport secure ? secure.SecurityError ?? "" : "";
    }

    /// <summary>
    /// Client-to-gateway encryption, wrapped around a UDP transport so nothing above it changes: the gateway and
    /// the client exchange exactly the messages they always did, and this layer seals each packet.
    ///
    /// <para>A client wrapper (<see cref="ForClient"/>) runs the handshake as soon as the link comes up and holds
    /// the <c>Connected</c> event back until it finishes, so the first thing above it ever sends is already
    /// encrypted. A gateway wrapper (<see cref="ForGateway"/>) answers a handshake on any inbound link that opens
    /// with one and leaves every other link exactly as it was: that is what keeps worker and gateway peers, which
    /// share the gateway's socket and live inside the deployment's private network, on plaintext (D1).</para>
    ///
    /// <para>Wire format, all little-endian: a handshake frame is <c>[tag][body]</c> and a data frame is
    /// <c>[0xE3][seq:u32]</c> followed by the ChaCha20-Poly1305 sealing of the payload with the frame header as
    /// associated data — 21 bytes over the plaintext. Tags start at 0xE0, above every <see cref="MsgId"/>, so a
    /// gateway with encryption turned off simply ignores the frame.</para>
    /// </summary>
    public sealed class EncryptedTransport : ITransport, ISecureTransport
    {
        private const byte TagClientHello = 0xE0, TagServerHello = 0xE1, TagFinished = 0xE2, TagData = 0xE3;
        private const byte WireVersion = 1;
        private const int HeaderSize = 5;
        private const string HandshakeLabel = "nebula-transport-v1";
        /// <summary>Beyond this many packets on one link the 32-bit sequence would wrap and reuse a nonce, so the link is dropped instead.</summary>
        private const uint MaxSequence = uint.MaxValue - 16;

        private readonly ITransport _inner;
        private readonly TransportIdentity _identity;   // gateway side
        private readonly ClientEncryption _client;      // client side
        private readonly Dictionary<int, Link> _links = new Dictionary<int, Link>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Action<TransportEvent> _forward;
        private Action<TransportEvent> _handler;
        private byte[] _sendScratch = new byte[2048], _receiveScratch = new byte[2048];
        private readonly byte[] _header = new byte[HeaderSize];
        private long _dropped;

        private EncryptedTransport(ITransport inner, TransportIdentity identity, ClientEncryption client)
        {
            _inner = inner;
            _identity = identity;
            _client = client;
            _forward = OnInner;
        }

        /// <summary>The gateway's side: answers handshakes with <paramref name="identity"/>, passes plaintext links through.</summary>
        public static EncryptedTransport ForGateway(ITransport inner, TransportIdentity identity) => new EncryptedTransport(inner, identity, null);

        /// <summary>The client's side: every link this transport dials is encrypted, or it does not come up at all.</summary>
        public static EncryptedTransport ForClient(ITransport inner, ClientEncryption settings) => new EncryptedTransport(inner, null, settings ?? new ClientEncryption());

        /// <summary>The certificate fingerprint this gateway presents; null on a client.</summary>
        public string Fingerprint => _identity?.Fingerprint;

        public string SecurityError { get; private set; } = "";

        /// <summary>Packets refused by the AEAD (forged, corrupted or replayed) since the transport started.</summary>
        public long DroppedPackets => _dropped;

        public bool IsEncrypted(int peerId) => _links.TryGetValue(peerId, out var link) && link.Keyed;

        // ---- ITransport ------------------------------------------------------------------------------------

        public string Name => _inner.Name;
        public bool IsRunning => _inner.IsRunning;
        public int LocalPort => _inner.LocalPort;
        public void Listen(int port) => _inner.Listen(port);
        public void StartClient() => _inner.StartClient();
        public void Disconnect(int peerId) => _inner.Disconnect(peerId);
        public bool IsConnected(int peerId) => _inner.IsConnected(peerId) && (!_links.TryGetValue(peerId, out var link) || link.Open);
        public int RoundTripMs(int peerId) => _inner.RoundTripMs(peerId);
        public void Flush() => _inner.Flush();

        public int Connect(string host, int port)
        {
            int peer = _inner.Connect(host, port);
            if (_client != null) _links[peer] = new Link(true);
            return peer;
        }

        public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
        {
            if (!_links.TryGetValue(peerId, out var link) || !link.Keyed)
            {
                _inner.Send(peerId, delivery, payload);     // a plaintext peer: a worker, or a gateway with encryption off
                return;
            }
            if (link.SendSequence >= MaxSequence)
            {
                Fail(peerId, link, "the link reached its packet limit and must be re-established");
                return;
            }
            uint sequence = link.SendSequence++;
            int total = HeaderSize + payload.Count + ChaCha20Poly1305Managed.TagSize;
            if (_sendScratch.Length < total) _sendScratch = new byte[Math.Max(total, _sendScratch.Length * 2)];
            _sendScratch[0] = TagData;
            WriteUInt(_sendScratch, 1, sequence);
            ChaCha20Poly1305Managed.Seal(link.SendKey, Nonce(link.SendSalt, sequence), payload, _sendScratch, HeaderSize, _sendScratch, HeaderSize);
            _inner.Send(peerId, delivery, new ArraySegment<byte>(_sendScratch, 0, total));
        }

        public void Poll(Action<TransportEvent> handler)
        {
            _handler = handler;
            try
            {
                _inner.Poll(_forward);
                CheckTimeouts();
            }
            finally { _handler = null; }
        }

        public void Stop()
        {
            _links.Clear();
            _inner.Stop();
        }

        public void Dispose()
        {
            _links.Clear();
            _inner.Dispose();
            _identity?.Dispose();
        }

        // ---- handshake -------------------------------------------------------------------------------------

        private void OnInner(TransportEvent ev)
        {
            switch (ev.Type)
            {
                case TransportEvent.Kind.Connected:
                    if (_client == null) { _handler?.Invoke(ev); return; }   // the gateway learns what a link is from its first frame
                    StartHandshake(ev.PeerId);
                    return;
                case TransportEvent.Kind.Disconnected:
                    // A link that never came up above this layer produces a Disconnected all the same: whoever is
                    // waiting for one event or the other must be told the attempt is over.
                    _links.Remove(ev.PeerId);
                    _handler?.Invoke(ev);
                    return;
                case TransportEvent.Kind.Data:
                    OnData(ev);
                    return;
            }
        }

        private void OnData(TransportEvent ev)
        {
            var data = ev.Data;
            _links.TryGetValue(ev.PeerId, out var link);
            byte tag = data.Count > 0 ? data.Array[data.Offset] : (byte)0;

            if (link == null)
            {
                if (_client != null) return;                                  // a client only talks to links it dialled
                if (tag != TagClientHello) { _handler?.Invoke(ev); return; }  // a worker or a plaintext client: unchanged
                link = new Link(false);
                _links[ev.PeerId] = link;
                if (!AcceptClientHello(ev.PeerId, link, data)) return;
                return;
            }

            if (!link.Keyed)
            {
                if (_client != null && tag == TagServerHello) AcceptServerHello(ev.PeerId, link, data);
                else if (_client == null) Fail(ev.PeerId, link, "the client sent traffic before its key exchange");
                return;
            }

            if (tag == TagFinished) { CheckFinished(ev.PeerId, link, data); return; }
            if (tag != TagData || data.Count < HeaderSize + ChaCha20Poly1305Managed.TagSize) { _dropped++; return; }

            uint sequence = ReadUInt(data.Array, data.Offset + 1);
            if (!link.Accept(sequence)) { _dropped++; return; }               // replayed, or too far behind the window
            int sealedLength = data.Count - HeaderSize;
            if (_receiveScratch.Length < sealedLength) _receiveScratch = new byte[Math.Max(sealedLength, _receiveScratch.Length * 2)];
            Buffer.BlockCopy(data.Array, data.Offset, _header, 0, HeaderSize);
            int length = ChaCha20Poly1305Managed.Open(link.ReceiveKey, Nonce(link.ReceiveSalt, sequence),
                new ArraySegment<byte>(data.Array, data.Offset + HeaderSize, sealedLength), _header, HeaderSize, _receiveScratch, 0);
            if (length < 0) { _dropped++; return; }                           // forged or corrupted: never reaches the game
            link.Seen(sequence);
            _handler?.Invoke(new TransportEvent(TransportEvent.Kind.Data, ev.PeerId, new ArraySegment<byte>(_receiveScratch, 0, length)));
        }

        private void StartHandshake(int peerId)
        {
            if (!_links.TryGetValue(peerId, out var link)) { link = new Link(true); _links[peerId] = link; }
            link.PrivateKey = X25519.NewPrivateKey();
            link.Random = NebulaCrypto.Random(32);
            link.StartedAt = _clock.Elapsed.TotalSeconds;
            var body = Concat(new byte[] { TagClientHello, WireVersion }, X25519.PublicKey(link.PrivateKey), link.Random);
            link.Transcript = Slice(body, 1, body.Length - 1);
            _inner.Send(peerId, Delivery.ReliableOrdered, new ArraySegment<byte>(body));
            _inner.Flush();
        }

        private bool AcceptClientHello(int peerId, Link link, ArraySegment<byte> data)
        {
            if (data.Count != 2 + 64 || data.Array[data.Offset + 1] != WireVersion)
                return Fail(peerId, link, "the client's key exchange is from another version of Nebula");
            var clientPublic = Slice(data, 2, 32);
            var clientRandom = Slice(data, 34, 32);
            var priv = X25519.NewPrivateKey();
            var serverPublic = X25519.PublicKey(priv);
            var serverRandom = NebulaCrypto.Random(32);
            var shared = X25519.Agree(priv, clientPublic);
            if (shared == null) return Fail(peerId, link, "the client's key share is not a usable point");

            var transcript = Concat(Slice(data, 1, data.Count - 1), new byte[] { WireVersion }, serverPublic, serverRandom);
            Derive(link, clientRandom, serverRandom, shared, server: true);
            link.Transcript = transcript;

            var signature = _identity.Sign(Concat(Encoding.UTF8.GetBytes(HandshakeLabel + " server"), NebulaCrypto.Sha256(transcript)));
            var body = Concat(new byte[] { TagServerHello, WireVersion }, serverPublic, serverRandom,
                Length16(_identity.Certificate), Length16(signature));
            _inner.Send(peerId, Delivery.ReliableOrdered, new ArraySegment<byte>(body));
            _inner.Flush();
            return true;
        }

        private void AcceptServerHello(int peerId, Link link, ArraySegment<byte> data)
        {
            try
            {
                int at = data.Offset + 1;
                var array = data.Array;
                if (data.Count < 2 + 64 + 4 || array[at] != WireVersion) { Fail(peerId, link, "the gateway's key exchange is from another version of Nebula"); return; }
                at++;
                var serverPublic = Slice(array, at, 32); at += 32;
                var serverRandom = Slice(array, at, 32); at += 32;
                int certificateLength = (array[at] | (array[at + 1] << 8)); at += 2;
                var certificate = Slice(array, at, certificateLength); at += certificateLength;
                int signatureLength = (array[at] | (array[at + 1] << 8)); at += 2;
                var signature = Slice(array, at, signatureLength); at += signatureLength;
                if (at > data.Offset + data.Count) { Fail(peerId, link, "the gateway's key exchange is truncated"); return; }

                string fingerprint = TransportIdentity.FingerprintOf(certificate);
                if (!string.IsNullOrEmpty(_client.Fingerprint))
                {
                    if (!TransportIdentity.FingerprintMatches(_client.Fingerprint, fingerprint))
                    {
                        Fail(peerId, link, $"the gateway presented certificate {fingerprint}, which is not the pinned {_client.Fingerprint}");
                        return;
                    }
                }
                else NebulaLog.Warn($"transport encryption: no GatewayFingerprint is pinned, so the link is encrypted but the gateway is not authenticated (its fingerprint is {fingerprint})");

                var transcript = Concat(link.Transcript, new byte[] { WireVersion }, serverPublic, serverRandom);
                if (!TransportIdentity.Verify(certificate, Concat(Encoding.UTF8.GetBytes(HandshakeLabel + " server"), NebulaCrypto.Sha256(transcript)), signature))
                {
                    Fail(peerId, link, "the gateway's certificate did not sign its key exchange");
                    return;
                }
                var shared = X25519.Agree(link.PrivateKey, serverPublic);
                if (shared == null) { Fail(peerId, link, "the gateway's key share is not a usable point"); return; }
                Derive(link, link.Random, serverRandom, shared, server: false);
                link.Transcript = transcript;
                link.PeerFingerprint = fingerprint;

                var finished = Concat(new byte[] { TagFinished }, NebulaCrypto.HmacSha256(link.FinishedKey, Concat(Encoding.UTF8.GetBytes("client finished"), NebulaCrypto.Sha256(transcript))));
                _inner.Send(peerId, Delivery.ReliableOrdered, new ArraySegment<byte>(finished));
                _inner.Flush();
                link.Announced = true;
                _handler?.Invoke(new TransportEvent(TransportEvent.Kind.Connected, peerId, default));
            }
            catch (Exception e)
            {
                Fail(peerId, link, "the gateway's key exchange could not be read: " + e.Message);
            }
        }

        private void CheckFinished(int peerId, Link link, ArraySegment<byte> data)
        {
            if (_client != null || link.FinishedSeen) return;
            var expected = NebulaCrypto.HmacSha256(link.FinishedKey, Concat(Encoding.UTF8.GetBytes("client finished"), NebulaCrypto.Sha256(link.Transcript)));
            var presented = data.Count == 33 ? Slice(data, 1, 32) : null;
            if (!NebulaCrypto.FixedTimeEquals(expected, presented)) { Fail(peerId, link, "the client's finished message does not match the key exchange"); return; }
            link.FinishedSeen = true;
        }

        private void Derive(Link link, byte[] clientRandom, byte[] serverRandom, byte[] shared, bool server)
        {
            var material = NebulaCrypto.Hkdf(Concat(clientRandom, serverRandom), shared, HandshakeLabel, 32 + 32 + 4 + 4 + 32);
            var clientToServerKey = Slice(material, 0, 32);
            var serverToClientKey = Slice(material, 32, 32);
            var clientToServerSalt = Slice(material, 64, 4);
            var serverToClientSalt = Slice(material, 68, 4);
            link.FinishedKey = Slice(material, 72, 32);
            link.SendKey = server ? serverToClientKey : clientToServerKey;
            link.ReceiveKey = server ? clientToServerKey : serverToClientKey;
            link.SendSalt = server ? serverToClientSalt : clientToServerSalt;
            link.ReceiveSalt = server ? clientToServerSalt : serverToClientSalt;
            link.Keyed = true;
        }

        private void CheckTimeouts()
        {
            if (_client == null || _links.Count == 0) return;
            List<int> expired = null;
            foreach (var pair in _links)
            {
                var link = pair.Value;
                if (link.Keyed || link.StartedAt <= 0 || _clock.Elapsed.TotalSeconds - link.StartedAt < _client.TimeoutSeconds) continue;
                (expired ?? (expired = new List<int>())).Add(pair.Key);
            }
            if (expired == null) return;
            foreach (int peer in expired) Fail(peer, _links[peer], "the gateway did not answer the key exchange (is encryption turned off there?)");
        }

        private bool Fail(int peerId, Link link, string reason)
        {
            SecurityError = reason;
            NebulaLog.Warn($"[{Name}] encrypted link with peer {peerId} refused: {reason}");
            if (link != null) link.Open = false;
            _links.Remove(peerId);
            _inner.Disconnect(peerId);
            // LiteNetLib reports the disconnect on a later poll; a client waiting to connect must not wait for it.
            _handler?.Invoke(new TransportEvent(TransportEvent.Kind.Disconnected, peerId, default));
            return false;
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        private static byte[] Nonce(byte[] salt, uint sequence)
        {
            var nonce = new byte[ChaCha20Poly1305Managed.NonceSize];
            Buffer.BlockCopy(salt, 0, nonce, 0, 4);
            WriteUInt(nonce, 8, sequence);
            return nonce;
        }

        private static void WriteUInt(byte[] b, int at, uint v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }

        private static uint ReadUInt(byte[] b, int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

        private static byte[] Length16(byte[] value)
        {
            var framed = new byte[2 + value.Length];
            framed[0] = (byte)value.Length;
            framed[1] = (byte)(value.Length >> 8);
            Buffer.BlockCopy(value, 0, framed, 2, value.Length);
            return framed;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (var part in parts) total += part.Length;
            var all = new byte[total];
            int at = 0;
            foreach (var part in parts) { Buffer.BlockCopy(part, 0, all, at, part.Length); at += part.Length; }
            return all;
        }

        private static byte[] Slice(byte[] source, int at, int length)
        {
            var slice = new byte[length];
            Buffer.BlockCopy(source, at, slice, 0, length);
            return slice;
        }

        private static byte[] Slice(ArraySegment<byte> source, int at, int length) => Slice(source.Array, source.Offset + at, length);

        /// <summary>One peer's half of the encrypted link.</summary>
        private sealed class Link
        {
            public Link(bool initiator) { Initiator = initiator; Open = true; }

            public readonly bool Initiator;
            public bool Open, Keyed, Announced, FinishedSeen;
            public byte[] PrivateKey, Random, Transcript, SendKey, ReceiveKey, SendSalt, ReceiveSalt, FinishedKey;
            public string PeerFingerprint;
            public double StartedAt;
            public uint SendSequence;

            // Replay window: the highest sequence accepted and a bitmap of the 64 below it, because the
            // unreliable channel legitimately reorders and a repeat must never decrypt twice.
            private uint _highest;
            private ulong _window;
            private bool _any;

            public bool Accept(uint sequence)
            {
                if (!_any) return true;
                if (sequence > _highest) return true;
                ulong behind = _highest - sequence;
                if (behind >= 64) return false;
                return (_window & (1UL << (int)behind)) == 0;
            }

            public void Seen(uint sequence)
            {
                if (!_any) { _any = true; _highest = sequence; _window = 1; return; }
                if (sequence > _highest)
                {
                    uint shift = sequence - _highest;
                    _window = shift >= 64 ? 0UL : _window << (int)shift;
                    _window |= 1UL;
                    _highest = sequence;
                    return;
                }
                ulong behind = _highest - sequence;
                if (behind < 64) _window |= 1UL << (int)behind;
            }
        }
    }
}
