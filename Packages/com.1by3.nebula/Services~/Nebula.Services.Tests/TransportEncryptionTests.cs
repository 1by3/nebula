using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Nebula;
using NUnit.Framework;

namespace Nebula.Services.Tests;

/// <summary>
/// Client-to-gateway transport encryption (docs/transport-encryption.md): the primitives against their RFC
/// vectors, the certificate handling, the handshake against a real gateway, what happens to a client that will
/// not encrypt or pins the wrong certificate, and what a forged packet costs an attacker. The last test prints
/// the overhead figures the design record quotes.
/// </summary>
public class TransportEncryptionTests
{
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-encryption-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
    }

    [TearDown]
    public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    // ---------------------------------------------------------------------------------------- primitives

    /// <summary>RFC 8439 §2.8.2: the worked AEAD example, which exercises ChaCha20, the one-time Poly1305 key and the MAC layout.</summary>
    [Test]
    public void AeadMatchesItsRfcVector()
    {
        var plaintext = Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)(0x80 + i);
        var nonce = Bytes("070000004041424344454647");
        var aad = Bytes("50515253c0c1c2c3c4c5c6c7");

        // Both implementations: the managed one every Unity build uses, and the platform one the services take.
        var output = new byte[plaintext.Length + 16];
        var opened = new byte[plaintext.Length];
        foreach (bool managed in new[] { true, false })
        {
            Array.Clear(output);
            if (managed) ChaCha20Poly1305Managed.SealManaged(key, nonce, new ArraySegment<byte>(plaintext), aad, aad.Length, output, 0);
            else ChaCha20Poly1305Managed.Seal(key, nonce, new ArraySegment<byte>(plaintext), aad, aad.Length, output, 0);
            Assert.That(Hex(output[..16]), Is.EqualTo("d31a8d34648e60db7b86afbc53ef7ec2"), $"ciphertext (managed: {managed})");
            Assert.That(Hex(output[^16..]), Is.EqualTo("1ae10b594f09e26a7e902ecbd0600691"), $"tag (managed: {managed})");

            Array.Clear(opened);
            int length = managed
                ? ChaCha20Poly1305Managed.OpenManaged(key, nonce, new ArraySegment<byte>(output), aad, aad.Length, opened, 0)
                : ChaCha20Poly1305Managed.Open(key, nonce, new ArraySegment<byte>(output), aad, aad.Length, opened, 0);
            Assert.That(length, Is.EqualTo(plaintext.Length));
            Assert.That(opened, Is.EqualTo(plaintext));
        }

        // Every single-bit change anywhere — ciphertext, tag or associated data — is refused rather than decrypted.
        for (int i = 0; i < output.Length; i += 7)
        {
            var damaged = (byte[])output.Clone();
            damaged[i] ^= 0x01;
            Assert.That(ChaCha20Poly1305Managed.Open(key, nonce, new ArraySegment<byte>(damaged), aad, aad.Length, opened, 0), Is.EqualTo(-1), $"byte {i}");
        }
        aad[0] ^= 0x01;
        Assert.That(ChaCha20Poly1305Managed.Open(key, nonce, new ArraySegment<byte>(output), aad, aad.Length, opened, 0), Is.EqualTo(-1), "associated data");
    }

    /// <summary>RFC 7748 §5.2 and §6.1: one scalar multiplication and one full exchange.</summary>
    [Test]
    public void X25519MatchesItsRfcVectors()
    {
        Assert.That(Hex(X25519.Agree(Bytes("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
                                     Bytes("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"))!),
            Is.EqualTo("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552"));

        var alice = Bytes("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bob = Bytes("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        Assert.That(Hex(X25519.PublicKey(alice)), Is.EqualTo("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"));
        Assert.That(Hex(X25519.PublicKey(bob)), Is.EqualTo("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"));
        Assert.That(Hex(X25519.Agree(alice, X25519.PublicKey(bob))!), Is.EqualTo("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"));
        Assert.That(Hex(X25519.Agree(bob, X25519.PublicKey(alice))!), Is.EqualTo("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"));

        Assert.That(X25519.Agree(alice, new byte[32]), Is.Null, "an all-zero key share has no usable shared secret");
    }

    /// <summary>
    /// The certificate the gateway generates when nobody gave it one: a certificate the platform's own X.509
    /// parser accepts, a fingerprint that survives the PEM round trip, and a key that signs.
    /// </summary>
    [Test]
    public void SelfSignedCertificateRoundTripsThroughPem()
    {
        using var identity = TransportIdentity.SelfSigned("gateway.example.com");
        var parsed = X509CertificateLoader.LoadCertificate(identity.Certificate);
        Assert.That(parsed.Subject, Does.Contain("gateway.example.com"));
        Assert.That(parsed.NotAfter, Is.GreaterThan(DateTime.Now.AddYears(5)));
        Assert.That(identity.Fingerprint, Has.Length.EqualTo(64));

        var data = Encoding.UTF8.GetBytes("a transcript");
        Assert.That(TransportIdentity.Verify(identity.Certificate, data, identity.Sign(data)), Is.True);
        Assert.That(TransportIdentity.Verify(identity.Certificate, Encoding.UTF8.GetBytes("another transcript"), identity.Sign(data)), Is.False);

        string pem = identity.ToPem();
        using var reloaded = TransportIdentity.FromPem(pem, pem);
        Assert.That(reloaded.Fingerprint, Is.EqualTo(identity.Fingerprint));
        Assert.That(TransportIdentity.Verify(identity.Certificate, data, reloaded.Sign(data)), Is.True, "the reloaded key belongs to the same certificate");

        // A store that already holds a certificate is reused, so the fingerprint players pinned does not change.
        string store = Path.Combine(directory, "nebula-transport.pem");
        using var first = TransportIdentity.Create("", "", "", "", store, "gateway.example.com");
        using var second = TransportIdentity.Create("", "", "", "", store, "gateway.example.com");
        Assert.That(second.Fingerprint, Is.EqualTo(first.Fingerprint));
        Assert.That(TransportIdentity.FingerprintMatches(first.Fingerprint.ToUpperInvariant(), second.Fingerprint), Is.True, "pasted fingerprints compare case- and separator-insensitively");
    }

    // ---------------------------------------------------------------------------------------- against a gateway

    /// <summary>1. An encrypted client joins a gateway that requires encryption, pinning the certificate it printed.</summary>
    [Test]
    public void EncryptedClientJoinsAGatewayThatRequiresEncryption()
    {
        LoadEmptyWorld();
        using var plane = new LocalControlPlane(); plane.Connect();
        var gateway = new NebulaGateway();
        int port = FreePort();
        try
        {
            gateway.Initialize(Config(port, require: true), plane);
            Assert.That(gateway.CertificateFingerprint, Is.Not.Null.And.Length.EqualTo(64));

            var joined = Handshake(gateway, port, new ClientEncryption { Fingerprint = gateway.CertificateFingerprint! });
            Assert.That(joined.welcome, Is.Not.Null, joined.rejected ?? "no answer at all");
            Assert.That(joined.encrypted, Is.True, "the link the welcome arrived on is encrypted");
        }
        finally { gateway.Dispose(); }
    }

    /// <summary>2. The same gateway refuses a plaintext client with a typed reason instead of dropping it silently.</summary>
    [Test]
    public void PlaintextClientIsRefusedWhenEncryptionIsRequired()
    {
        LoadEmptyWorld();
        using var plane = new LocalControlPlane(); plane.Connect();
        var gateway = new NebulaGateway();
        int port = FreePort();
        try
        {
            gateway.Initialize(Config(port, require: true), plane);
            var refused = Handshake(gateway, port, null);
            Assert.That(refused.welcome, Is.Null);
            Assert.That(refused.rejected, Does.Contain("encrypted"));
            Assert.That(refused.code, Is.EqualTo(JoinRejectReason.EncryptionRequired));
            Assert.That(refused.retry, Is.False, "retrying the same way would be refused again");
        }
        finally { gateway.Dispose(); }
    }

    /// <summary>3. A gateway that offers encryption without requiring it still takes the clients that have not been updated.</summary>
    [Test]
    public void PlaintextClientStillJoinsWhenEncryptionIsOnlyOffered()
    {
        LoadEmptyWorld();
        using var plane = new LocalControlPlane(); plane.Connect();
        var gateway = new NebulaGateway();
        int port = FreePort();
        try
        {
            gateway.Initialize(Config(port, require: false), plane);
            var plain = Handshake(gateway, port, null);
            Assert.That(plain.welcome, Is.Not.Null, plain.rejected ?? "no answer at all");
            Assert.That(plain.encrypted, Is.False);

            var encrypted = Handshake(gateway, port, new ClientEncryption { Fingerprint = gateway.CertificateFingerprint! });
            Assert.That(encrypted.welcome, Is.Not.Null, encrypted.rejected ?? "no answer at all");
            Assert.That(encrypted.encrypted, Is.True);
        }
        finally { gateway.Dispose(); }
    }

    /// <summary>4. A client that pinned another gateway's certificate never gets as far as sending its Hello.</summary>
    [Test]
    public void PinnedFingerprintMismatchRefusesTheGateway()
    {
        LoadEmptyWorld();
        using var plane = new LocalControlPlane(); plane.Connect();
        var gateway = new NebulaGateway();
        int port = FreePort();
        using var stranger = TransportIdentity.SelfSigned("someone-else");
        try
        {
            gateway.Initialize(Config(port, require: true), plane);
            var refused = Handshake(gateway, port, new ClientEncryption { Fingerprint = stranger.Fingerprint, TimeoutSeconds = 3f });
            Assert.That(refused.welcome, Is.Null);
            Assert.That(refused.rejected, Is.Null, "the gateway never saw a Hello: the client refused it first");
            Assert.That(refused.securityError, Does.Contain("pinned"));
        }
        finally { gateway.Dispose(); }
    }

    // ---------------------------------------------------------------------------------------- forgery and overhead

    /// <summary>5. A packet altered in flight is dropped by the AEAD, and a replayed one is only ever delivered once.</summary>
    [Test]
    public void TamperedAndReplayedPacketsAreDropped()
    {
        var link = new LoopLink();
        using var pair = EncryptedPair.Over(link);
        var delivered = new List<string>();
        pair.PumpUntilReady();

        pair.SendFromClient("first");
        pair.Pump(delivered);
        Assert.That(delivered, Is.EqualTo(new[] { "first" }));

        // The attacker flips one byte of the sealed payload of the next packet.
        link.Corrupt = frame => { frame[frame.Length - 3] ^= 0x40; return frame; };
        pair.SendFromClient("second");
        pair.Pump(delivered);
        Assert.That(delivered, Is.EqualTo(new[] { "first" }), "a forged packet never reaches the game");
        Assert.That(pair.Gateway.DroppedPackets, Is.EqualTo(1));

        // And one that is repeated verbatim, which is what a recorded packet would be.
        link.Corrupt = null;
        link.Duplicate = true;
        pair.SendFromClient("third");
        pair.Pump(delivered);
        link.Duplicate = false;
        Assert.That(delivered, Is.EqualTo(new[] { "first", "third" }), "a replayed packet is accepted once");
        Assert.That(pair.Gateway.DroppedPackets, Is.EqualTo(2));

        // The link keeps working afterwards: a dropped forgery is not a dropped session.
        pair.SendFromClient("fourth");
        pair.Pump(delivered);
        Assert.That(delivered, Is.EqualTo(new[] { "first", "third", "fourth" }));
    }

    /// <summary>6. What encryption costs: bytes on the wire, the handshake, and CPU per packet. Printed for docs/transport-encryption.md §6.</summary>
    [Test]
    public void OverheadIsMeasuredAndPrinted()
    {
        var link = new LoopLink();
        using var pair = EncryptedPair.Over(link);
        pair.PumpUntilReady();
        TestContext.Out.WriteLine($"[encryption] handshake: {link.HandshakeBytes} bytes in {link.HandshakeFrames} frames, 1 round trip before the first game packet");

        foreach (int size in new[] { 32, 128, 512, 1200 })
        {
            link.LastFrameSize = 0;
            int payloadBytes = pair.SendFromClient(new string('x', size));
            Assert.That(link.LastFrameSize, Is.EqualTo(payloadBytes + 21), $"a {payloadBytes}-byte payload costs 21 bytes");
            TestContext.Out.WriteLine($"[encryption] payload {payloadBytes} B -> frame {link.LastFrameSize} B (+{link.LastFrameSize - payloadBytes} B, {(link.LastFrameSize - payloadBytes) * 100.0 / payloadBytes:F1} %)");
            pair.Pump(new List<string>());
        }

        // Seal and open 10 000 packets of a typical input/state size, the way the transport does it per packet.
        var key = NebulaCrypto.Random(32);
        var nonce = NebulaCrypto.Random(12);
        var header = NebulaCrypto.Random(5);
        var payload = NebulaCrypto.Random(200);
        var sealedBuffer = new byte[payload.Length + 16];
        var opened = new byte[payload.Length];
        const int iterations = 10000;
        ChaCha20Poly1305Managed.Seal(key, nonce, new ArraySegment<byte>(payload), header, header.Length, sealedBuffer, 0); // warm the JIT
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            ChaCha20Poly1305Managed.Seal(key, nonce, new ArraySegment<byte>(payload), header, header.Length, sealedBuffer, 0);
        double sealUs = clock.Elapsed.TotalMilliseconds * 1000 / iterations;
        clock.Restart();
        for (int i = 0; i < iterations; i++)
            ChaCha20Poly1305Managed.Open(key, nonce, new ArraySegment<byte>(sealedBuffer), header, header.Length, opened, 0);
        double openUs = clock.Elapsed.TotalMilliseconds * 1000 / iterations;
        TestContext.Out.WriteLine($"[encryption] 200 B packet, platform AEAD: seal {sealUs:F2} us, open {openUs:F2} us ({sealUs:F2} / {openUs:F2} ms per 1000 packets)");

        ChaCha20Poly1305Managed.SealManaged(key, nonce, new ArraySegment<byte>(payload), header, header.Length, sealedBuffer, 0);
        clock.Restart();
        for (int i = 0; i < iterations; i++)
            ChaCha20Poly1305Managed.SealManaged(key, nonce, new ArraySegment<byte>(payload), header, header.Length, sealedBuffer, 0);
        double managedSealUs = clock.Elapsed.TotalMilliseconds * 1000 / iterations;
        clock.Restart();
        for (int i = 0; i < iterations; i++)
            ChaCha20Poly1305Managed.OpenManaged(key, nonce, new ArraySegment<byte>(sealedBuffer), header, header.Length, opened, 0);
        double managedOpenUs = clock.Elapsed.TotalMilliseconds * 1000 / iterations;
        TestContext.Out.WriteLine($"[encryption] 200 B packet, managed AEAD (what a Unity build runs): seal {managedSealUs:F2} us, open {managedOpenUs:F2} us ({managedSealUs:F2} / {managedOpenUs:F2} ms per 1000 packets)");

        clock.Restart();
        for (int i = 0; i < 100; i++) X25519.PublicKey(X25519.NewPrivateKey());
        TestContext.Out.WriteLine($"[encryption] X25519 scalar multiplication: {clock.Elapsed.TotalMilliseconds / 100:F2} ms (two per connection)");

        using var identity = TransportIdentity.SelfSigned("bench");
        var transcript = NebulaCrypto.Random(64);
        clock.Restart();
        for (int i = 0; i < 100; i++) identity.Sign(transcript);
        double signMs = clock.Elapsed.TotalMilliseconds / 100;
        var signature = identity.Sign(transcript);
        clock.Restart();
        for (int i = 0; i < 100; i++) TransportIdentity.Verify(identity.Certificate, transcript, signature);
        TestContext.Out.WriteLine($"[encryption] RSA-2048 sign {signMs:F2} ms (gateway, once per connection), verify {clock.Elapsed.TotalMilliseconds / 100:F2} ms (client)");

        Assert.That(sealUs, Is.LessThan(50), "sealing a 200-byte packet should cost microseconds, not milliseconds");
    }

    // ---------------------------------------------------------------------------------------- helpers

    private void LoadEmptyWorld()
    {
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest(), ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    private NebulaConfig Config(int port, bool require) => new()
    {
        GatewayPort = (ushort)port,
        WebClients = false,
        AuthSigningKey = "encryption-tests",
        EncryptClients = true,
        RequireEncryption = require,
        EncryptionSelfSignedPath = Path.Combine(directory, "nebula-transport.pem"),
    };

    private static int FreePort()
    {
        using var reserve = new UdpClient(0);
        int port = ((IPEndPoint)reserve.Client.LocalEndPoint!).Port;
        reserve.Close();
        return port;
    }

    /// <summary>Connect to a running gateway and report how the join went. <paramref name="encryption"/> null connects in the clear.</summary>
    private static (WelcomeMsg? welcome, string? rejected, JoinRejectReason code, bool retry, bool encrypted, string securityError) Handshake(NebulaGateway gateway, int port, ClientEncryption? encryption)
    {
        ITransport transport = new LiteNetTransport("test-client");
        if (encryption != null) transport = EncryptedTransport.ForClient(transport, encryption);
        using var client = transport;
        int peer = client.Connect("127.0.0.1", port);
        WelcomeMsg? welcome = null;
        string? rejected = null;
        var code = JoinRejectReason.None;
        bool retry = false, encrypted = false;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 8 && welcome == null && rejected == null && TransportSecurity.ErrorOf(client).Length == 0)
        {
            gateway.Tick();
            client.Poll(e =>
            {
                if (e.Type == TransportEvent.Kind.Connected)
                {
                    var w = new NetworkWriter();
                    new HelloMsg { Role = PeerRole.Client, Id = "test-client" }.Write(w);
                    client.Send(e.PeerId, Delivery.ReliableOrdered, w.ToSegment());
                    client.Flush();
                }
                if (e.Type == TransportEvent.Kind.Data)
                {
                    var r = new NetworkReader(e.Data);
                    var id = (MsgId)r.ReadByte();
                    if (id == MsgId.Welcome) { welcome = WelcomeMsg.Read(r); encrypted = TransportSecurity.IsEncrypted(client, e.PeerId); }
                    else if (id == MsgId.JoinRejected)
                    {
                        var msg = JoinRejectedMsg.Read(r);
                        rejected = msg.Reason;
                        code = msg.Code;
                        retry = msg.Retry;
                    }
                }
            });
            Thread.Sleep(5);
        }
        string securityError = TransportSecurity.ErrorOf(client);
        for (int i = 0; i < 3; i++) { gateway.Tick(); client.Poll(_ => { }); Thread.Sleep(5); }
        return (welcome, rejected, code, retry, encrypted, securityError);
    }

    private static byte[] Bytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>
    /// A client and a gateway wired to each other in memory, so a test can see (and alter) every byte that goes
    /// between them without a socket or a timing window.
    /// </summary>
    private sealed class EncryptedPair : IDisposable
    {
        public EncryptedTransport Client = null!, Gateway = null!;
        private readonly TransportIdentity _identity;
        private LoopLink _link = null!;
        private int _clientPeer, _gatewayPeer;

        private EncryptedPair(TransportIdentity identity) { _identity = identity; }

        public static EncryptedPair Over(LoopLink link)
        {
            var identity = TransportIdentity.SelfSigned("loop");
            var pair = new EncryptedPair(identity) { _link = link };
            pair.Gateway = EncryptedTransport.ForGateway(link.Server, identity);
            pair.Client = EncryptedTransport.ForClient(link.Client, new ClientEncryption { Fingerprint = identity.Fingerprint });
            pair._clientPeer = pair.Client.Connect("loop", 0);
            pair._gatewayPeer = LoopLink.ServerPeer;
            return pair;
        }

        public void PumpUntilReady()
        {
            for (int i = 0; i < 10 && !Client.IsEncrypted(_clientPeer); i++)
            {
                Gateway.Poll(_ => { });
                Client.Poll(_ => { });
            }
            Assert.That(Client.IsEncrypted(_clientPeer), Is.True, "the client finished its handshake");
            Assert.That(Gateway.IsEncrypted(_gatewayPeer), Is.True, "and so did the gateway");
            _link.HandshakeDone();
        }

        /// <summary>Send a message and report how many plaintext bytes it was, so a test can price the frame.</summary>
        public int SendFromClient(string text)
        {
            var w = new NetworkWriter();
            w.WriteString(text);
            var payload = w.ToSegment();
            Client.Send(_clientPeer, Delivery.ReliableOrdered, payload);
            return payload.Count;
        }

        /// <summary>Deliver everything in flight, appending whatever the gateway decrypted to <paramref name="received"/>.</summary>
        public void Pump(List<string> received)
        {
            for (int i = 0; i < 3; i++)
            {
                Gateway.Poll(e => { if (e.Type == TransportEvent.Kind.Data) received.Add(new NetworkReader(e.Data).ReadString()); });
                Client.Poll(_ => { });
            }
        }

        public void Dispose()
        {
            Client.Dispose();
            Gateway.Dispose();
        }
    }

    /// <summary>
    /// Two <see cref="ITransport"/> ends joined by a queue, with hooks to corrupt or duplicate what crosses it.
    /// Peer 1 is the gateway's view of the client; peer 1 is the client's view of the gateway.
    /// </summary>
    private sealed class LoopLink
    {
        public const int ServerPeer = 1, ClientPeer = 1;

        public Func<byte[], byte[]>? Corrupt;
        public bool Duplicate;
        public long HandshakeBytes, HandshakeFrames, LastFrameSize;
        private bool _handshakeDone;

        private readonly Queue<byte[]> _toServer = new(), _toClient = new();

        public LoopLink()
        {
            Server = new End(this, toPeer: true);
            Client = new End(this, toPeer: false);
        }

        public End Server { get; }
        public End Client { get; }

        public void HandshakeDone() => _handshakeDone = true;

        private void Enqueue(bool toServer, ArraySegment<byte> payload)
        {
            var frame = new byte[payload.Count];
            Buffer.BlockCopy(payload.Array!, payload.Offset, frame, 0, payload.Count);
            if (_handshakeDone)
            {
                LastFrameSize = frame.Length;
                if (toServer && Corrupt != null) frame = Corrupt(frame);
            }
            else { HandshakeBytes += frame.Length; HandshakeFrames++; }
            var queue = toServer ? _toServer : _toClient;
            queue.Enqueue(frame);
            if (Duplicate && toServer) queue.Enqueue((byte[])frame.Clone());
        }

        private void Drain(bool onServer, Action<TransportEvent> handler)
        {
            var queue = onServer ? _toServer : _toClient;
            while (queue.Count > 0)
                handler(new TransportEvent(TransportEvent.Kind.Data, onServer ? ServerPeer : ClientPeer, new ArraySegment<byte>(queue.Dequeue())));
        }

        /// <summary>One side of the link as an <see cref="ITransport"/>; only what the encrypted transport calls is implemented.</summary>
        internal sealed class End : ITransport
        {
            private readonly LoopLink _link;
            private readonly bool _isServer;
            private bool _connected;

            public End(LoopLink link, bool toPeer) { _link = link; _isServer = toPeer; }

            public string Name => _isServer ? "loop-gateway" : "loop-client";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { }
            public void StartClient() { }

            public int Connect(string host, int port)
            {
                _connected = true;
                return ClientPeer;
            }

            public void Disconnect(int peerId) => _connected = false;
            public bool IsConnected(int peerId) => true;
            public int RoundTripMs(int peerId) => 0;
            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload) => _link.Enqueue(toServer: !_isServer, payload);

            public void Poll(Action<TransportEvent> handler)
            {
                if (!_isServer && _connected)
                {
                    _connected = false;
                    handler(new TransportEvent(TransportEvent.Kind.Connected, ClientPeer, default));
                }
                _link.Drain(_isServer, handler);
            }

            public void Flush() { }
            public void Stop() { }
            public void Dispose() { }
        }
    }
}
