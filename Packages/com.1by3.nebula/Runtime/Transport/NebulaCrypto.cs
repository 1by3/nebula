using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// Provides random bytes, SHA-256 hashes, hash-based message authentication codes (HMAC), and key derivation
    /// for encrypted transport connections. Randomness, hashing, and HMAC use the platform's cryptography APIs.
    ///
    /// <para>The associated <see cref="X25519"/> and managed <see cref="ChaCha20Poly1305Managed"/>
    /// implementations are not constant-time. The transport generates a new key pair for each connection.</para>
    /// </summary>
    public static class NebulaCrypto
    {
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] Random(int bytes)
        {
            var b = new byte[bytes];
            Rng.GetBytes(b);
            return b;
        }

        /// <summary>Compares equal-length arrays without stopping at a differing byte. Returns false for null arrays or unequal lengths.</summary>
        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data);
        }

        public static byte[] HmacSha256(byte[] key, byte[] data)
        {
            using (var mac = new HMACSHA256(key)) return mac.ComputeHash(data);
        }

        /// <summary>Derives <paramref name="length"/> bytes using the HMAC-based extract-and-expand key derivation function (HKDF) with SHA-256, as specified in RFC 5869.</summary>
        public static byte[] Hkdf(byte[] salt, byte[] ikm, string info, int length)
        {
            byte[] prk = HmacSha256(salt ?? new byte[32], ikm);
            var infoBytes = Encoding.UTF8.GetBytes(info ?? "");
            var okm = new byte[length];
            byte[] block = new byte[0];
            int done = 0;
            for (byte counter = 1; done < length; counter++)
            {
                var input = new byte[block.Length + infoBytes.Length + 1];
                Buffer.BlockCopy(block, 0, input, 0, block.Length);
                Buffer.BlockCopy(infoBytes, 0, input, block.Length, infoBytes.Length);
                input[input.Length - 1] = counter;
                block = HmacSha256(prk, input);
                int take = Math.Min(block.Length, length - done);
                Buffer.BlockCopy(block, 0, okm, done, take);
                done += take;
            }
            return okm;
        }
    }

    /// <summary>
    /// Implements X25519 key agreement from RFC 7748 §5 using <see cref="BigInteger"/> arithmetic.
    /// This managed implementation is not constant-time.
    /// </summary>
    public static class X25519
    {
        public const int KeySize = 32;
        private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        private static readonly BigInteger A24 = 121665;

        /// <summary>Generates a random 32-byte private scalar with the X25519 clamping rules applied.</summary>
        public static byte[] NewPrivateKey()
        {
            var k = NebulaCrypto.Random(KeySize);
            Clamp(k);
            return k;
        }

        public static byte[] PublicKey(byte[] privateKey)
        {
            var basePoint = new byte[KeySize];
            basePoint[0] = 9;
            return Agree(privateKey, basePoint);
        }

        /// <summary>Computes the shared secret from two 32-byte keys. Returns null when agreement produces an all-zero result.</summary>
        public static byte[] Agree(byte[] privateKey, byte[] peerPublicKey)
        {
            if (privateKey == null || privateKey.Length != KeySize || peerPublicKey == null || peerPublicKey.Length != KeySize)
                throw new ArgumentException("X25519 keys are 32 bytes");
            var scalar = (byte[])privateKey.Clone();
            Clamp(scalar);
            var u = (byte[])peerPublicKey.Clone();
            u[31] &= 0x7f; // the high bit of the u-coordinate is ignored
            BigInteger x1 = FromBytes(u);

            BigInteger x2 = BigInteger.One, z2 = BigInteger.Zero, x3 = x1, z3 = BigInteger.One;
            int swap = 0;
            for (int t = 254; t >= 0; t--)
            {
                int bit = (scalar[t >> 3] >> (t & 7)) & 1;
                if ((swap ^ bit) != 0)
                {
                    var tx = x2; x2 = x3; x3 = tx;
                    var tz = z2; z2 = z3; z3 = tz;
                }
                swap = bit;

                BigInteger a = Mod(x2 + z2), aa = Mod(a * a);
                BigInteger b = Mod(x2 - z2), bb = Mod(b * b);
                BigInteger e = Mod(aa - bb);
                BigInteger c = Mod(x3 + z3), d = Mod(x3 - z3);
                BigInteger da = Mod(d * a), cb = Mod(c * b);
                BigInteger sum = Mod(da + cb), difference = Mod(da - cb);
                x3 = Mod(sum * sum);
                z3 = Mod(x1 * Mod(difference * difference));
                x2 = Mod(aa * bb);
                z2 = Mod(e * Mod(aa + A24 * e));
            }
            if (swap != 0)
            {
                var tx = x2; x2 = x3; x3 = tx;
                var tz = z2; z2 = z3; z3 = tz;
            }
            BigInteger result = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
            var bytes = ToBytes(result);
            bool allZero = true;
            for (int i = 0; i < bytes.Length; i++) allZero &= bytes[i] == 0;
            return allZero ? null : bytes;
        }

        private static void Clamp(byte[] k)
        {
            k[0] &= 248;
            k[31] &= 127;
            k[31] |= 64;
        }

        private static BigInteger Mod(BigInteger v)
        {
            v %= P;
            return v.Sign < 0 ? v + P : v;
        }

        private static BigInteger FromBytes(byte[] little)
        {
            var padded = new byte[KeySize + 1]; // a trailing zero keeps BigInteger from reading a sign bit
            Buffer.BlockCopy(little, 0, padded, 0, KeySize);
            return new BigInteger(padded);
        }

        private static byte[] ToBytes(BigInteger v)
        {
            var bytes = v.ToByteArray();
            var little = new byte[KeySize];
            Buffer.BlockCopy(bytes, 0, little, 0, Math.Min(bytes.Length, KeySize));
            return little;
        }
    }

    /// <summary>
    /// Implements ChaCha20-Poly1305 authenticated encryption with associated data (AEAD), as specified in
    /// RFC 8439. It uses a 32-byte key, a 12-byte nonce, and a 16-byte authentication tag. Ciphertext has the
    /// same length as plaintext; the tag adds 16 bytes. A nonce must not be reused with the same key.
    /// </summary>
    public static class ChaCha20Poly1305Managed
    {
        public const int KeySize = 32;
        public const int NonceSize = 12;
        public const int TagSize = 16;

        /// <summary>
        /// Encrypt <paramref name="plaintext"/> into <paramref name="output"/> (ciphertext then the 16-byte tag).
        /// <paramref name="output"/> needs <c>plaintext.Count + 16</c> bytes from <paramref name="outputOffset"/>.
        /// </summary>
        public static int Seal(byte[] key, byte[] nonce, ArraySegment<byte> plaintext, byte[] aad, int aadLength, byte[] output, int outputOffset)
        {
#if NEBULA_SERVICE
            // The services run on a .NET that has the primitive, and its hardware path is an order of magnitude
            // cheaper per packet than anything managed. Unity has neither the type nor the intrinsics, so the
            // managed implementation below is what a player build uses, and both are tested against RFC 8439.
            if (System.Security.Cryptography.ChaCha20Poly1305.IsSupported)
            {
                Check(key, nonce);
                using (var aead = new System.Security.Cryptography.ChaCha20Poly1305(key))
                {
                    aead.Encrypt(nonce, new ReadOnlySpan<byte>(plaintext.Array, plaintext.Offset, plaintext.Count),
                        new Span<byte>(output, outputOffset, plaintext.Count),
                        new Span<byte>(output, outputOffset + plaintext.Count, TagSize),
                        new ReadOnlySpan<byte>(aad, 0, aadLength));
                }
                return plaintext.Count + TagSize;
            }
#endif
            return SealManaged(key, nonce, plaintext, aad, aadLength, output, outputOffset);
        }

        /// <summary>Encrypts the payload and appends its 16-byte authentication tag using the managed implementation used by Unity builds.</summary>
        public static int SealManaged(byte[] key, byte[] nonce, ArraySegment<byte> plaintext, byte[] aad, int aadLength, byte[] output, int outputOffset)
        {
            Check(key, nonce);
            var state = Initial(key, nonce, 1);
            Cipher(state, plaintext.Array, plaintext.Offset, plaintext.Count, output, outputOffset);
            var tag = Tag(key, nonce, aad, aadLength, output, outputOffset, plaintext.Count);
            Buffer.BlockCopy(tag, 0, output, outputOffset + plaintext.Count, TagSize);
            return plaintext.Count + TagSize;
        }

        /// <summary>
        /// Verifies the authentication tag and decrypts into <paramref name="output"/>. Returns the plaintext
        /// length, or -1 if the input is shorter than a tag or authentication fails. Output is valid only on success.
        /// </summary>
        public static int Open(byte[] key, byte[] nonce, ArraySegment<byte> sealedInput, byte[] aad, int aadLength, byte[] output, int outputOffset)
        {
#if NEBULA_SERVICE
            if (System.Security.Cryptography.ChaCha20Poly1305.IsSupported)
            {
                Check(key, nonce);
                if (sealedInput.Count < TagSize) return -1;
                int size = sealedInput.Count - TagSize;
                using (var aead = new System.Security.Cryptography.ChaCha20Poly1305(key))
                {
                    try
                    {
                        aead.Decrypt(nonce, new ReadOnlySpan<byte>(sealedInput.Array, sealedInput.Offset, size),
                            new ReadOnlySpan<byte>(sealedInput.Array, sealedInput.Offset + size, TagSize),
                            new Span<byte>(output, outputOffset, size), new ReadOnlySpan<byte>(aad, 0, aadLength));
                    }
                    catch (System.Security.Cryptography.AuthenticationTagMismatchException) { return -1; }
                }
                return size;
            }
#endif
            return OpenManaged(key, nonce, sealedInput, aad, aadLength, output, outputOffset);
        }

        /// <summary>Verifies and decrypts with the managed implementation. Returns the plaintext length, or -1 for a truncated tag or failed authentication.</summary>
        public static int OpenManaged(byte[] key, byte[] nonce, ArraySegment<byte> sealedInput, byte[] aad, int aadLength, byte[] output, int outputOffset)
        {
            Check(key, nonce);
            if (sealedInput.Count < TagSize) return -1;
            int length = sealedInput.Count - TagSize;
            var expected = Tag(key, nonce, aad, aadLength, sealedInput.Array, sealedInput.Offset, length);
            var actual = new byte[TagSize];
            Buffer.BlockCopy(sealedInput.Array, sealedInput.Offset + length, actual, 0, TagSize);
            if (!NebulaCrypto.FixedTimeEquals(expected, actual)) return -1;
            var state = Initial(key, nonce, 1);
            Cipher(state, sealedInput.Array, sealedInput.Offset, length, output, outputOffset);
            return length;
        }

        private static void Check(byte[] key, byte[] nonce)
        {
            if (key == null || key.Length != KeySize) throw new ArgumentException("ChaCha20-Poly1305 takes a 32-byte key");
            if (nonce == null || nonce.Length != NonceSize) throw new ArgumentException("ChaCha20-Poly1305 takes a 12-byte nonce");
        }

        private static byte[] Tag(byte[] key, byte[] nonce, byte[] aad, int aadLength, byte[] ciphertext, int offset, int length)
        {
            var block = new byte[64];
            Block(Initial(key, nonce, 0), new uint[16], block, 0);
            var polyKey = new byte[32];
            Buffer.BlockCopy(block, 0, polyKey, 0, 32);

            var poly = new Poly1305(polyKey);
            if (aadLength > 0)
            {
                poly.Update(aad, 0, aadLength);
                poly.Pad16(aadLength);
            }
            poly.Update(ciphertext, offset, length);
            poly.Pad16(length);
            var lengths = new byte[16];
            WriteLE64(lengths, 0, (ulong)aadLength);
            WriteLE64(lengths, 8, (ulong)length);
            poly.Update(lengths, 0, 16);
            return poly.Finish();
        }

        private static uint[] Initial(byte[] key, byte[] nonce, uint counter)
        {
            var s = new uint[16];
            s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
            for (int i = 0; i < 8; i++) s[4 + i] = ReadLE32(key, i * 4);
            s[12] = counter;
            for (int i = 0; i < 3; i++) s[13 + i] = ReadLE32(nonce, i * 4);
            return s;
        }

        private static void Cipher(uint[] state, byte[] input, int inputOffset, int length, byte[] output, int outputOffset)
        {
            var key = new byte[64];
            var working = new uint[16];
            int done = 0;
            while (done < length)
            {
                Block(state, working, key, 0);
                state[12]++;
                int n = Math.Min(64, length - done);
                for (int i = 0; i < n; i++) output[outputOffset + done + i] = (byte)(input[inputOffset + done + i] ^ key[i]);
                done += n;
            }
        }

        private static void Block(uint[] state, uint[] x, byte[] output, int offset)
        {
            Array.Copy(state, x, 16);
            for (int i = 0; i < 10; i++)
            {
                QuarterRound(x, 0, 4, 8, 12); QuarterRound(x, 1, 5, 9, 13);
                QuarterRound(x, 2, 6, 10, 14); QuarterRound(x, 3, 7, 11, 15);
                QuarterRound(x, 0, 5, 10, 15); QuarterRound(x, 1, 6, 11, 12);
                QuarterRound(x, 2, 7, 8, 13); QuarterRound(x, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++) WriteLE32(output, offset + i * 4, x[i] + state[i]);
        }

        private static void QuarterRound(uint[] x, int a, int b, int c, int d)
        {
            x[a] += x[b]; x[d] = Rotate(x[d] ^ x[a], 16);
            x[c] += x[d]; x[b] = Rotate(x[b] ^ x[c], 12);
            x[a] += x[b]; x[d] = Rotate(x[d] ^ x[a], 8);
            x[c] += x[d]; x[b] = Rotate(x[b] ^ x[c], 7);
        }

        private static uint Rotate(uint v, int n) => (v << n) | (v >> (32 - n));

        internal static uint ReadLE32(byte[] b, int i) => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

        internal static void WriteLE32(byte[] b, int i, uint v)
        {
            b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); b[i + 2] = (byte)(v >> 16); b[i + 3] = (byte)(v >> 24);
        }

        private static void WriteLE64(byte[] b, int i, ulong v)
        {
            for (int k = 0; k < 8; k++) b[i + k] = (byte)(v >> (8 * k));
        }

        /// <summary>Poly1305 (RFC 8439 §2.5) in 26-bit limbs, the arrangement that keeps every product inside a ulong.</summary>
        private sealed class Poly1305
        {
            private readonly uint[] _r = new uint[5];
            private readonly uint[] _pad = new uint[4];
            private readonly uint[] _h = new uint[5];
            private readonly byte[] _buffer = new byte[16];
            private int _buffered;

            public Poly1305(byte[] key)
            {
                uint t0 = ReadLE32(key, 0), t1 = ReadLE32(key, 4), t2 = ReadLE32(key, 8), t3 = ReadLE32(key, 12);
                _r[0] = t0 & 0x3ffffff;
                _r[1] = ((t0 >> 26) | (t1 << 6)) & 0x3ffff03;
                _r[2] = ((t1 >> 20) | (t2 << 12)) & 0x3ffc0ff;
                _r[3] = ((t2 >> 14) | (t3 << 18)) & 0x3f03fff;
                _r[4] = (t3 >> 8) & 0x00fffff;
                for (int i = 0; i < 4; i++) _pad[i] = ReadLE32(key, 16 + i * 4);
            }

            public void Update(byte[] data, int offset, int length)
            {
                int i = 0;
                if (_buffered > 0)
                {
                    int take = Math.Min(16 - _buffered, length);
                    Buffer.BlockCopy(data, offset, _buffer, _buffered, take);
                    _buffered += take;
                    i += take;
                    if (_buffered == 16) { Block(_buffer, 0, true); _buffered = 0; }
                }
                for (; length - i >= 16; i += 16) Block(data, offset + i, true);
                if (length - i > 0)
                {
                    Buffer.BlockCopy(data, offset + i, _buffer, 0, length - i);
                    _buffered = length - i;
                }
            }

            /// <summary>Feed the zero bytes RFC 8439 pads each AEAD section with, so sections cannot run together.</summary>
            public void Pad16(int written)
            {
                int remainder = written & 15;
                if (remainder == 0) return;
                var zeros = new byte[16 - remainder];
                Update(zeros, 0, zeros.Length);
            }

            public byte[] Finish()
            {
                if (_buffered > 0)
                {
                    _buffer[_buffered] = 1;
                    for (int i = _buffered + 1; i < 16; i++) _buffer[i] = 0;
                    Block(_buffer, 0, false);
                    _buffered = 0;
                }
                ulong c;
                c = _h[1] >> 26; _h[1] &= 0x3ffffff; _h[2] += (uint)c;
                c = _h[2] >> 26; _h[2] &= 0x3ffffff; _h[3] += (uint)c;
                c = _h[3] >> 26; _h[3] &= 0x3ffffff; _h[4] += (uint)c;
                c = _h[4] >> 26; _h[4] &= 0x3ffffff; _h[0] += (uint)c * 5;
                c = _h[0] >> 26; _h[0] &= 0x3ffffff; _h[1] += (uint)c;

                // h - p, kept only when it did not borrow: the canonical representative below 2^130 - 5.
                long g0 = _h[0] + 5; long carry = g0 >> 26; g0 &= 0x3ffffff;
                long g1 = _h[1] + carry; carry = g1 >> 26; g1 &= 0x3ffffff;
                long g2 = _h[2] + carry; carry = g2 >> 26; g2 &= 0x3ffffff;
                long g3 = _h[3] + carry; carry = g3 >> 26; g3 &= 0x3ffffff;
                long g4 = _h[4] + carry - (1L << 26);
                bool useG = g4 >= 0;
                uint h0 = useG ? (uint)g0 : _h[0];
                uint h1 = useG ? (uint)g1 : _h[1];
                uint h2 = useG ? (uint)g2 : _h[2];
                uint h3 = useG ? (uint)g3 : _h[3];
                uint h4 = useG ? (uint)g4 : _h[4];

                ulong f0 = (h0 | ((ulong)h1 << 26)) & 0xffffffff;
                ulong f1 = ((h1 >> 6) | ((ulong)h2 << 20)) & 0xffffffff;
                ulong f2 = ((h2 >> 12) | ((ulong)h3 << 14)) & 0xffffffff;
                ulong f3 = ((h3 >> 18) | ((ulong)h4 << 8)) & 0xffffffff;
                f0 += _pad[0];
                f1 += _pad[1] + (f0 >> 32);
                f2 += _pad[2] + (f1 >> 32);
                f3 += _pad[3] + (f2 >> 32);

                var tag = new byte[16];
                WriteLE32(tag, 0, (uint)f0);
                WriteLE32(tag, 4, (uint)f1);
                WriteLE32(tag, 8, (uint)f2);
                WriteLE32(tag, 12, (uint)f3);
                return tag;
            }

            private void Block(byte[] m, int offset, bool full)
            {
                uint hibit = full ? (uint)(1 << 24) : 0;
                uint t0 = ReadLE32(m, offset), t1 = ReadLE32(m, offset + 4), t2 = ReadLE32(m, offset + 8), t3 = ReadLE32(m, offset + 12);
                _h[0] += t0 & 0x3ffffff;
                _h[1] += ((t0 >> 26) | (t1 << 6)) & 0x3ffffff;
                _h[2] += ((t1 >> 20) | (t2 << 12)) & 0x3ffffff;
                _h[3] += ((t2 >> 14) | (t3 << 18)) & 0x3ffffff;
                _h[4] += (t3 >> 8) | hibit;

                ulong s1 = _r[1] * 5UL, s2 = _r[2] * 5UL, s3 = _r[3] * 5UL, s4 = _r[4] * 5UL;
                ulong d0 = _h[0] * (ulong)_r[0] + _h[1] * s4 + _h[2] * s3 + _h[3] * s2 + _h[4] * s1;
                ulong d1 = _h[0] * (ulong)_r[1] + _h[1] * (ulong)_r[0] + _h[2] * s4 + _h[3] * s3 + _h[4] * s2;
                ulong d2 = _h[0] * (ulong)_r[2] + _h[1] * (ulong)_r[1] + _h[2] * (ulong)_r[0] + _h[3] * s4 + _h[4] * s3;
                ulong d3 = _h[0] * (ulong)_r[3] + _h[1] * (ulong)_r[2] + _h[2] * (ulong)_r[1] + _h[3] * (ulong)_r[0] + _h[4] * s4;
                ulong d4 = _h[0] * (ulong)_r[4] + _h[1] * (ulong)_r[3] + _h[2] * (ulong)_r[2] + _h[3] * (ulong)_r[1] + _h[4] * (ulong)_r[0];

                ulong c = d0 >> 26; _h[0] = (uint)(d0 & 0x3ffffff);
                d1 += c; c = d1 >> 26; _h[1] = (uint)(d1 & 0x3ffffff);
                d2 += c; c = d2 >> 26; _h[2] = (uint)(d2 & 0x3ffffff);
                d3 += c; c = d3 >> 26; _h[3] = (uint)(d3 & 0x3ffffff);
                d4 += c; c = d4 >> 26; _h[4] = (uint)(d4 & 0x3ffffff);
                _h[0] += (uint)(c * 5);
                c = _h[0] >> 26; _h[0] &= 0x3ffffff;
                _h[1] += (uint)c;
            }
        }
    }
}
