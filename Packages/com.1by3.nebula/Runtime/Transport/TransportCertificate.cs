using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// The gateway's transport certificate and the key that signs with it, plus the fingerprint a client pins.
    /// The certificate is an ordinary X.509 RSA certificate: either one the operator supplies as PEM (their own
    /// CA's, or the one the web port already uses) or one the gateway generates for itself on first run and keeps
    /// next to its other state. Encoding and decoding is done here, over <see cref="RSAParameters"/> and DER,
    /// because Unity's profile has neither <c>CertificateRequest</c> nor the PKCS#8 import helpers
    /// (<c>docs/transport-encryption.md</c> D3).
    /// </summary>
    public sealed class TransportIdentity : IDisposable
    {
        private TransportIdentity(byte[] certificate, RSA key, string fingerprint)
        {
            Certificate = certificate;
            Key = key;
            Fingerprint = fingerprint;
        }

        /// <summary>The DER certificate sent to every client during the handshake.</summary>
        public byte[] Certificate { get; }
        /// <summary>The private key; only the gateway has it.</summary>
        public RSA Key { get; }
        /// <summary>
        /// SHA-256 of the certificate's SubjectPublicKeyInfo as 64 lowercase hex characters: what a client pins with
        /// <c>NebulaConfig.GatewayFingerprint</c>. It is the fingerprint of the key, not of the certificate, so
        /// re-issuing the certificate for the same key does not invalidate what players pinned.
        /// </summary>
        public string Fingerprint { get; }

        public byte[] Sign(byte[] data) => Key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        public void Dispose() => Key.Dispose();

        /// <summary>The fingerprint a client would pin for a certificate it received.</summary>
        public static string FingerprintOf(byte[] certificateDer) => Hex(NebulaCrypto.Sha256(Der.SubjectPublicKeyInfo(certificateDer)));

        /// <summary>Check a signature the gateway made with the key in <paramref name="certificateDer"/>.</summary>
        public static bool Verify(byte[] certificateDer, byte[] data, byte[] signature)
        {
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(Der.PublicKeyFrom(certificateDer));
                return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }

        /// <summary>
        /// Build the identity a gateway presents. In order: the PEM strings, then the PEM files, then a self-signed
        /// certificate read from (or written to) <paramref name="selfSignedPath"/>.
        /// </summary>
        /// <param name="subject">Common name of a generated certificate: the address clients dial.</param>
        public static TransportIdentity Create(string certificatePem, string keyPem, string certificatePath, string keyPath, string selfSignedPath, string subject)
        {
            if (!string.IsNullOrEmpty(certificatePem) && !string.IsNullOrEmpty(keyPem)) return FromPem(certificatePem, keyPem);
            if (!string.IsNullOrEmpty(certificatePath))
            {
                if (!File.Exists(certificatePath)) throw new FileNotFoundException($"no transport certificate at {certificatePath}");
                string keyText = string.IsNullOrEmpty(keyPath) ? File.ReadAllText(certificatePath) : File.ReadAllText(keyPath);
                return FromPem(File.ReadAllText(certificatePath), keyText);
            }
            if (!string.IsNullOrEmpty(selfSignedPath) && File.Exists(selfSignedPath))
            {
                string stored = File.ReadAllText(selfSignedPath);
                try { return FromPem(stored, stored); }
                catch (Exception e) { NebulaLog.Warn($"transport encryption: regenerating {selfSignedPath} ({e.Message})"); }
            }
            var identity = SelfSigned(subject);
            if (!string.IsNullOrEmpty(selfSignedPath))
            {
                try
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(selfSignedPath));
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllText(selfSignedPath, identity.ToPem());
#if !UNITY_5_3_OR_NEWER
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(selfSignedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
                }
                catch (Exception e) { NebulaLog.Warn($"transport encryption: could not store the self-signed certificate in {selfSignedPath}, so its fingerprint changes on every restart: {e.Message}"); }
            }
            return identity;
        }

        /// <summary>A certificate and key in PEM form, the two blocks in either order and optionally in one file.</summary>
        public static TransportIdentity FromPem(string certificatePem, string keyPem)
        {
            byte[] certificate = Pem.First(certificatePem, "CERTIFICATE");
            if (certificate == null) throw new ArgumentException("no CERTIFICATE block in the transport certificate");
            var rsa = RSA.Create();
            try
            {
                byte[] pkcs1 = Pem.First(keyPem, "RSA PRIVATE KEY");
                byte[] pkcs8 = pkcs1 == null ? Pem.First(keyPem, "PRIVATE KEY") : null;
                if (pkcs1 == null && pkcs8 == null) throw new ArgumentException("no PRIVATE KEY block next to the transport certificate");
                rsa.ImportParameters(pkcs1 != null ? Der.PrivateKeyFromPkcs1(pkcs1) : Der.PrivateKeyFromPkcs8(pkcs8));
            }
            catch
            {
                rsa.Dispose();
                throw;
            }
            return new TransportIdentity(certificate, rsa, FingerprintOf(certificate));
        }

        /// <summary>A fresh 2048-bit RSA key in a self-signed certificate valid for ten years.</summary>
        public static TransportIdentity SelfSigned(string subject)
        {
            var rsa = RSA.Create();
            if (rsa.KeySize != 2048) rsa.KeySize = 2048;
            var parameters = rsa.ExportParameters(false);
            byte[] certificate = Der.SelfSignedCertificate(string.IsNullOrEmpty(subject) ? "nebula-gateway" : subject, parameters,
                data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            return new TransportIdentity(certificate, rsa, FingerprintOf(certificate));
        }

        /// <summary>The certificate and its key as one PEM document, which is what the self-signed store holds.</summary>
        public string ToPem() =>
            Pem.Write("CERTIFICATE", Certificate) + Pem.Write("RSA PRIVATE KEY", Der.Pkcs1From(Key.ExportParameters(true)));

        internal static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>Compare fingerprints the way a player would paste them: case and separators do not matter.</summary>
        public static bool FingerprintMatches(string expected, string actual)
        {
            string a = Normalize(expected), b = Normalize(actual);
            return a.Length > 0 && a == b;
        }

        private static string Normalize(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return "";
            var sb = new StringBuilder(fingerprint.Length);
            foreach (char ch in fingerprint)
                if (Uri.IsHexDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }
    }

    /// <summary>PEM blocks: base64 between <c>-----BEGIN x-----</c> lines.</summary>
    internal static class Pem
    {
        public static byte[] First(string text, string label)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string begin = "-----BEGIN " + label + "-----", end = "-----END " + label + "-----";
            int start = text.IndexOf(begin, StringComparison.Ordinal);
            if (start < 0) return null;
            start += begin.Length;
            int stop = text.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0) return null;
            var body = text.Substring(start, stop - start);
            var sb = new StringBuilder(body.Length);
            foreach (char ch in body)
                if (!char.IsWhiteSpace(ch)) sb.Append(ch);
            return Convert.FromBase64String(sb.ToString());
        }

        public static string Write(string label, byte[] der)
        {
            var sb = new StringBuilder();
            sb.Append("-----BEGIN ").Append(label).Append("-----\n");
            string base64 = Convert.ToBase64String(der);
            for (int i = 0; i < base64.Length; i += 64) sb.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
            sb.Append("-----END ").Append(label).Append("-----\n");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Just enough DER to read an X.509 certificate's public key and to write a v1 self-signed one. Nothing here
    /// parses untrusted input beyond the certificate a client receives during its own handshake, and a malformed
    /// one throws, which fails that handshake.
    /// </summary>
    internal static class Der
    {
        private const byte Integer = 0x02, BitString = 0x03, Null = 0x05, ObjectIdentifier = 0x06, Utf8String = 0x0c, Sequence = 0x30, Set = 0x31, UtcTime = 0x17;
        private static readonly byte[] RsaEncryption = { 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01 };       // 1.2.840.113549.1.1.1
        private static readonly byte[] Sha256WithRsa = { 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x0b };       // 1.2.840.113549.1.1.11
        private static readonly byte[] CommonName = { 0x55, 0x04, 0x03 };                                              // 2.5.4.3

        // ---- reading -------------------------------------------------------------------------------------

        /// <summary>The SubjectPublicKeyInfo of a certificate, as the DER bytes that get hashed into a fingerprint.</summary>
        public static byte[] SubjectPublicKeyInfo(byte[] certificate)
        {
            var certificateBody = new Reader(certificate).Sequence();
            var tbs = new Reader(new Reader(certificateBody).Sequence());
            if (tbs.PeekTag() == 0xa0) tbs.Skip();   // [0] version, absent from a v1 certificate
            tbs.Skip();                              // serialNumber
            tbs.Skip();                              // signature
            tbs.Skip();                              // issuer
            tbs.Skip();                              // validity
            tbs.Skip();                              // subject
            return tbs.Element();
        }

        public static RSAParameters PublicKeyFrom(byte[] certificate)
        {
            var spki = new Reader(SubjectPublicKeyInfo(certificate)).Sequence();
            var outer = new Reader(spki);
            outer.Skip();                            // AlgorithmIdentifier
            var bits = outer.Read(BitString);
            if (bits.Length < 1 || bits[0] != 0) throw new CryptographicException("unexpected public key encoding");
            var key = new Reader(new Reader(Slice(bits, 1)).Sequence());
            var modulus = key.Read(Integer);
            var exponent = key.Read(Integer);
            return new RSAParameters { Modulus = Unsigned(modulus), Exponent = Unsigned(exponent) };
        }

        public static RSAParameters PrivateKeyFromPkcs8(byte[] pkcs8)
        {
            var outer = new Reader(new Reader(pkcs8).Sequence());
            outer.Skip();                            // version
            outer.Skip();                            // privateKeyAlgorithm
            return PrivateKeyFromPkcs1(outer.Read(0x04));
        }

        public static RSAParameters PrivateKeyFromPkcs1(byte[] pkcs1)
        {
            var key = new Reader(new Reader(pkcs1).Sequence());
            key.Skip();                              // version
            byte[] n = Unsigned(key.Read(Integer));
            byte[] e = Unsigned(key.Read(Integer));
            byte[] d = key.Read(Integer), p = key.Read(Integer), q = key.Read(Integer), dp = key.Read(Integer), dq = key.Read(Integer), qi = key.Read(Integer);
            int half = (n.Length + 1) / 2;
            return new RSAParameters
            {
                Modulus = n,
                Exponent = e,
                D = Pad(d, n.Length),
                P = Pad(p, half),
                Q = Pad(q, half),
                DP = Pad(dp, half),
                DQ = Pad(dq, half),
                InverseQ = Pad(qi, half),
            };
        }

        // ---- writing -------------------------------------------------------------------------------------

        public static byte[] Pkcs1From(RSAParameters k) => Element(Sequence, Concat(
            Element(Integer, new byte[] { 0 }),
            Number(k.Modulus), Number(k.Exponent), Number(k.D), Number(k.P), Number(k.Q), Number(k.DP), Number(k.DQ), Number(k.InverseQ)));

        public static byte[] SelfSignedCertificate(string subject, RSAParameters publicKey, Func<byte[], byte[]> sign)
        {
            var serial = NebulaCrypto.Random(16);
            serial[0] &= 0x7f;
            var name = Element(Sequence, Element(Set, Element(Sequence, Concat(
                Element(ObjectIdentifier, CommonName), Element(Utf8String, Encoding.UTF8.GetBytes(subject))))));
            var now = DateTime.UtcNow.AddHours(-1);
            var validity = Element(Sequence, Concat(Time(now), Time(now.AddYears(10))));
            var algorithm = Element(Sequence, Concat(Element(ObjectIdentifier, Sha256WithRsa), Element(Null, new byte[0])));
            var spki = Element(Sequence, Concat(
                Element(Sequence, Concat(Element(ObjectIdentifier, RsaEncryption), Element(Null, new byte[0]))),
                Element(BitString, Concat(new byte[] { 0 }, Element(Sequence, Concat(Number(publicKey.Modulus), Number(publicKey.Exponent)))))));
            var tbs = Element(Sequence, Concat(Element(Integer, serial), algorithm, name, validity, name, spki));
            var signature = sign(tbs);
            return Element(Sequence, Concat(tbs, algorithm, Element(BitString, Concat(new byte[] { 0 }, signature))));
        }

        private static byte[] Time(DateTime utc) => Element(UtcTime, Encoding.ASCII.GetBytes(utc.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + "Z"));

        /// <summary>An unsigned big-endian value as a DER INTEGER: a leading zero when the high bit is set.</summary>
        private static byte[] Number(byte[] unsigned)
        {
            if (unsigned == null || unsigned.Length == 0) return Element(Integer, new byte[] { 0 });
            int start = 0;
            while (start < unsigned.Length - 1 && unsigned[start] == 0) start++;
            var trimmed = Slice(unsigned, start);
            return Element(Integer, (trimmed[0] & 0x80) != 0 ? Concat(new byte[] { 0 }, trimmed) : trimmed);
        }

        private static byte[] Element(byte tag, byte[] content)
        {
            var length = Length(content.Length);
            var element = new byte[1 + length.Length + content.Length];
            element[0] = tag;
            Buffer.BlockCopy(length, 0, element, 1, length.Length);
            Buffer.BlockCopy(content, 0, element, 1 + length.Length, content.Length);
            return element;
        }

        private static byte[] Length(int value)
        {
            if (value < 0x80) return new byte[] { (byte)value };
            var bytes = new List<byte>();
            for (int v = value; v > 0; v >>= 8) bytes.Insert(0, (byte)v);
            bytes.Insert(0, (byte)(0x80 | bytes.Count));
            return bytes.ToArray();
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

        private static byte[] Slice(byte[] source, int start)
        {
            var slice = new byte[source.Length - start];
            Buffer.BlockCopy(source, start, slice, 0, slice.Length);
            return slice;
        }

        private static byte[] Unsigned(byte[] integer)
        {
            int start = 0;
            while (start < integer.Length - 1 && integer[start] == 0) start++;
            return Slice(integer, start);
        }

        private static byte[] Pad(byte[] value, int length)
        {
            value = Unsigned(value);
            if (value.Length == length) return value;
            if (value.Length > length) throw new CryptographicException("RSA key component is longer than its modulus allows");
            var padded = new byte[length];
            Buffer.BlockCopy(value, 0, padded, length - value.Length, value.Length);
            return padded;
        }

        /// <summary>A cursor over one DER sequence's contents.</summary>
        private struct Reader
        {
            private readonly byte[] _data;
            private int _at, _end;

            public Reader(byte[] data)
            {
                _data = data;
                _at = 0;
                _end = data.Length;
            }

            public byte PeekTag()
            {
                if (_at >= _end) throw new CryptographicException("truncated DER");
                return _data[_at];
            }

            /// <summary>The contents of the next element, which must be a SEQUENCE.</summary>
            public byte[] Sequence() => Read(Sequence0);

            private const byte Sequence0 = 0x30;

            public byte[] Read(byte tag)
            {
                var (start, length) = Header(tag);
                var content = new byte[length];
                Buffer.BlockCopy(_data, start, content, 0, length);
                _at = start + length;
                return content;
            }

            /// <summary>The next element with its tag and length, as it appeared (what a fingerprint hashes).</summary>
            public byte[] Element()
            {
                int from = _at;
                var (start, length) = Header(0);
                _at = start + length;
                var element = new byte[_at - from];
                Buffer.BlockCopy(_data, from, element, 0, element.Length);
                return element;
            }

            public void Skip()
            {
                var (start, length) = Header(0);
                _at = start + length;
            }

            private (int, int) Header(byte expected)
            {
                if (_at + 2 > _end) throw new CryptographicException("truncated DER");
                byte tag = _data[_at];
                if (expected != 0 && tag != expected) throw new CryptographicException($"expected DER tag 0x{expected:x2}, found 0x{tag:x2}");
                int at = _at + 1;
                int length = _data[at++];
                if ((length & 0x80) != 0)
                {
                    int count = length & 0x7f;
                    if (count == 0 || count > 4 || at + count > _end) throw new CryptographicException("unsupported DER length");
                    length = 0;
                    for (int i = 0; i < count; i++) length = (length << 8) | _data[at++];
                }
                if (length < 0 || at + length > _end) throw new CryptographicException("truncated DER");
                return (at, length);
            }
        }
    }
}
