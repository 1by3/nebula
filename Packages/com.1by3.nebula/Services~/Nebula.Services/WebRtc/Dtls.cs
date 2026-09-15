using System;
using System.Collections.Generic;
using System.Threading;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;
using TlsChain = Org.BouncyCastle.Tls.Certificate;

namespace Nebula.WebRtc
{
    /// <summary>
    /// A self-signed ECDSA P-256 certificate and its key. WebRTC endpoints do not trust certificates through a CA:
    /// each checks the other's against the SHA-256 fingerprint in the SDP it received over signaling.
    /// </summary>
    internal sealed class DtlsIdentity
    {
        public BcTlsCrypto Crypto { get; }
        public AsymmetricKeyParameter PrivateKey { get; }
        public TlsChain Chain { get; }
        /// <summary>SHA-256 of the certificate as SDP writes it: upper-case hex pairs joined by colons.</summary>
        public string Fingerprint { get; }

        private DtlsIdentity(BcTlsCrypto crypto, AsymmetricKeyParameter privateKey, TlsChain chain, string fingerprint)
        {
            Crypto = crypto;
            PrivateKey = privateKey;
            Chain = chain;
            Fingerprint = fingerprint;
        }

        public static DtlsIdentity Create()
        {
            var random = new SecureRandom();
            var keys = new ECKeyPairGenerator();
            keys.Init(new ECKeyGenerationParameters(SecObjectIdentifiers.SecP256r1, random));
            var pair = keys.GenerateKeyPair();
            var name = new Org.BouncyCastle.Asn1.X509.X509Name("CN=nebula");
            var generator = new X509V3CertificateGenerator();
            generator.SetSerialNumber(new BigInteger(62, random).Add(BigInteger.One));
            generator.SetIssuerDN(name);
            generator.SetSubjectDN(name);
            generator.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            generator.SetNotAfter(DateTime.UtcNow.AddYears(1));
            generator.SetPublicKey(pair.Public);
            byte[] der = generator.Generate(new Asn1SignatureFactory("SHA256WITHECDSA", pair.Private, random)).GetEncoded();
            var crypto = new BcTlsCrypto(random);
            return new DtlsIdentity(crypto, pair.Private, new TlsChain(new TlsCertificate[] { new BcTlsCertificate(crypto, der) }), FingerprintOf(der));
        }

        public static string FingerprintOf(byte[] der) => BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(der)).Replace('-', ':');

        public TlsCredentialedSigner Signer(TlsContext context) =>
            new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(context), Crypto, PrivateKey, Chain, SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
    }

    /// <summary>What both ends of a WebRTC DTLS session agree on: DTLS 1.2, ECDHE with ECDSA and AES-GCM, and a certificate matching the SDP fingerprint.</summary>
    internal static class DtlsPolicy
    {
        public static readonly int[] CipherSuites =
        {
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        };

        public static readonly IList<SignatureAndHashAlgorithm> ClientSignatureAlgorithms = new List<SignatureAndHashAlgorithm>
        {
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa),
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.rsa),
        };

        public static void CheckPeer(TlsChain chain, string expectedFingerprint)
        {
            if (chain == null || chain.IsEmpty) throw new TlsFatalAlert(AlertDescription.bad_certificate, "the peer sent no certificate");
            string actual = DtlsIdentity.FingerprintOf(chain.GetCertificateAt(0).GetEncoded());
            if (!string.Equals(actual, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new TlsFatalAlert(AlertDescription.bad_certificate, "the peer's certificate does not match its SDP fingerprint");
        }
    }

    /// <summary>The gateway's side of a browser's DTLS handshake. It asks for the browser's certificate and checks it against the offer.</summary>
    internal sealed class DtlsServer : DefaultTlsServer
    {
        private readonly DtlsIdentity _identity;
        private readonly string _peerFingerprint;

        public DtlsServer(DtlsIdentity identity, string peerFingerprint) : base(identity.Crypto)
        {
            _identity = identity;
            _peerFingerprint = peerFingerprint;
        }

        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();
        protected override int[] GetSupportedCipherSuites() => DtlsPolicy.CipherSuites;
        protected override TlsCredentialedSigner GetECDsaSignerCredentials() => _identity.Signer(m_context);
        public override int GetHandshakeTimeoutMillis() => 15000;

        public override CertificateRequest GetCertificateRequest() =>
            new CertificateRequest(new[] { ClientCertificateType.ecdsa_sign, ClientCertificateType.rsa_sign }, DtlsPolicy.ClientSignatureAlgorithms, null);

        public override void NotifyClientCertificate(TlsChain clientCertificate) => DtlsPolicy.CheckPeer(clientCertificate, _peerFingerprint);
    }

    /// <summary>The browser's side of the handshake, for tests that stand in for a browser.</summary>
    internal sealed class DtlsClient : DefaultTlsClient
    {
        private readonly DtlsIdentity _identity;
        private readonly string _peerFingerprint;

        public DtlsClient(DtlsIdentity identity, string peerFingerprint) : base(identity.Crypto)
        {
            _identity = identity;
            _peerFingerprint = peerFingerprint;
        }

        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();
        protected override int[] GetSupportedCipherSuites() => DtlsPolicy.CipherSuites;
        public override int GetHandshakeTimeoutMillis() => 15000;
        public override TlsAuthentication GetAuthentication() => new Authentication(this);

        private sealed class Authentication : TlsAuthentication
        {
            private readonly DtlsClient _client;
            public Authentication(DtlsClient client) { _client = client; }
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate) => DtlsPolicy.CheckPeer(serverCertificate.Certificate, _client._peerFingerprint);
            public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest) => _client._identity.Signer(_client.m_context);
        }
    }

    /// <summary>
    /// The datagram side of one DTLS session on a shared socket: the socket's receive thread queues the session's
    /// datagrams here and the session's thread reads them. Receiving from a closed queue throws, so a handshake in
    /// progress stops at once instead of retrying until it times out.
    /// </summary>
    internal sealed class DatagramQueue : DatagramTransport
    {
        /// <summary>UDP payload limit handed to DTLS: 1280 (the IPv6 minimum MTU) less IPv4 and UDP headers.</summary>
        public const int SendLimit = 1252;
        private const int Capacity = 512;
        private readonly Queue<byte[]> _queue = new Queue<byte[]>();
        private readonly Action<byte[], int, int> _send;
        private bool _closed;

        public DatagramQueue(Action<byte[], int, int> send) { _send = send; }

        public bool IsClosed { get { lock (_queue) return _closed; } }

        public void Enqueue(byte[] datagram)
        {
            lock (_queue)
            {
                if (_closed || _queue.Count >= Capacity) return;
                _queue.Enqueue(datagram);
                Monitor.PulseAll(_queue);
            }
        }

        /// <summary>Wait until a datagram is queued (or the queue closes) without taking it.</summary>
        public bool WaitForData(int millis)
        {
            lock (_queue)
            {
                if (_queue.Count == 0 && !_closed) Monitor.Wait(_queue, millis);
                return _queue.Count > 0 || _closed;
            }
        }

        public int GetReceiveLimit() => 1500;
        public int GetSendLimit() => SendLimit;

        public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);

        public int Receive(Span<byte> buffer, int waitMillis)
        {
            byte[] datagram;
            lock (_queue)
            {
                if (_queue.Count == 0 && !_closed) Monitor.Wait(_queue, Math.Max(1, waitMillis));
                if (_queue.Count == 0)
                {
                    if (_closed) throw new ObjectDisposedException(nameof(DatagramQueue), "the session is closed");
                    return -1;
                }
                datagram = _queue.Dequeue();
            }
            int n = Math.Min(datagram.Length, buffer.Length);
            datagram.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public void Send(byte[] buf, int off, int len) => _send(buf, off, len);

        public void Send(ReadOnlySpan<byte> buffer)
        {
            var copy = buffer.ToArray();
            _send(copy, 0, copy.Length);
        }

        public void Close()
        {
            lock (_queue)
            {
                _closed = true;
                Monitor.PulseAll(_queue);
            }
        }
    }
}
