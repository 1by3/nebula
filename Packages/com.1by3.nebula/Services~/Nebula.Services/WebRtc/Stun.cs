using System;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Nebula.WebRtc
{
    /// <summary>
    /// The part of STUN (RFC 8489) an ICE-lite endpoint (RFC 8445) needs: recognise a binding request, check its
    /// short-term credentials, and answer it. A browser's connectivity checks and consent refreshes (RFC 7675) are all
    /// binding requests; the gateway never sends its own. The request builder exists for tests.
    /// </summary>
    internal static class Stun
    {
        public const ushort BindingRequestType = 0x0001, BindingSuccessType = 0x0101;
        public const uint MagicCookie = 0x2112A442;
        private const ushort AttrUsername = 0x0006, AttrMessageIntegrity = 0x0008, AttrXorMappedAddress = 0x0020,
            AttrPriority = 0x0024, AttrUseCandidate = 0x0025, AttrFingerprint = 0x8028, AttrIceControlling = 0x802A;
        private const uint FingerprintXor = 0x5354554E;

        public struct Request
        {
            public byte[] TransactionId;
            public string Username;
            /// <summary>Offset of the MESSAGE-INTEGRITY attribute, or -1 when there is none.</summary>
            public int IntegrityOffset;
            public bool UseCandidate;
        }

        /// <summary>The first byte of a STUN message is 0 or 1 (RFC 7983), which is how it is told apart from DTLS on a shared socket.</summary>
        public static bool IsStun(ReadOnlySpan<byte> data) => data.Length >= 20 && data[0] < 2 && BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4)) == MagicCookie;

        public static bool TryParseBindingRequest(ReadOnlySpan<byte> data, out Request request)
        {
            request = default;
            request.IntegrityOffset = -1;
            if (!IsStun(data) || BinaryPrimitives.ReadUInt16BigEndian(data) != BindingRequestType) return false;
            int end = 20 + BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2));
            if (end > data.Length) return false;
            request.TransactionId = data.Slice(8, 12).ToArray();
            for (int p = 20; p + 4 <= end;)
            {
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p));
                int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p + 2));
                if (p + 4 + length > end) return false;
                // Everything after MESSAGE-INTEGRITY except FINGERPRINT is outside the integrity check and is ignored.
                if (request.IntegrityOffset < 0)
                {
                    if (type == AttrUsername) request.Username = Encoding.UTF8.GetString(data.Slice(p + 4, length));
                    else if (type == AttrUseCandidate) request.UseCandidate = true;
                    else if (type == AttrMessageIntegrity)
                    {
                        if (length != 20) return false;
                        request.IntegrityOffset = p;
                    }
                }
                p += 4 + ((length + 3) & ~3);
            }
            return true;
        }

        /// <summary>Whether MESSAGE-INTEGRITY matches <paramref name="key"/> (the receiver's ICE password).</summary>
        public static bool CheckIntegrity(ReadOnlySpan<byte> data, in Request request, byte[] key)
        {
            int at = request.IntegrityOffset;
            if (at < 0) return false;
            // The HMAC covers the message up to the attribute, with the header length set as if the message ended right after it.
            Span<byte> covered = at <= 1024 ? stackalloc byte[at] : new byte[at];
            data.Slice(0, at).CopyTo(covered);
            BinaryPrimitives.WriteUInt16BigEndian(covered.Slice(2), (ushort)(at - 20 + 24));
            Span<byte> mac = stackalloc byte[20];
            HMACSHA1.HashData(key, covered, mac);
            return CryptographicOperations.FixedTimeEquals(mac, data.Slice(at + 4, 20));
        }

        /// <summary>A binding success response carrying the address the request came from, signed with <paramref name="key"/>.</summary>
        public static byte[] BindingResponse(byte[] transactionId, IPEndPoint source, byte[] key)
        {
            var b = new byte[20 + 12 + 24 + 8];
            var span = b.AsSpan();
            BinaryPrimitives.WriteUInt16BigEndian(span, BindingSuccessType);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(4), MagicCookie);
            transactionId.CopyTo(span.Slice(8));
            int p = 20;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(p), AttrXorMappedAddress);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(p + 2), 8);
            span[p + 5] = 1; // IPv4
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(p + 6), (ushort)(source.Port ^ (MagicCookie >> 16)));
            uint address = BinaryPrimitives.ReadUInt32BigEndian(source.Address.MapToIPv4().GetAddressBytes());
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(p + 8), address ^ MagicCookie);
            p += 12;
            p = AppendIntegrityAndFingerprint(span, p, key);
            return b;
        }

        /// <summary>A binding request as a controlling ICE agent sends it (tests stand in for the browser with this).</summary>
        public static byte[] BindingRequest(byte[] transactionId, string username, byte[] key, bool useCandidate)
        {
            byte[] user = Encoding.UTF8.GetBytes(username);
            int userPadded = (user.Length + 3) & ~3;
            var b = new byte[20 + 4 + userPadded + 8 + 12 + (useCandidate ? 4 : 0) + 24 + 8];
            var span = b.AsSpan();
            BinaryPrimitives.WriteUInt16BigEndian(span, BindingRequestType);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(4), MagicCookie);
            transactionId.CopyTo(span.Slice(8));
            int p = 20;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(p), AttrUsername);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(p + 2), (ushort)user.Length);
            user.CopyTo(span.Slice(p + 4));
            p += 4 + userPadded;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(p), AttrPriority);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(p + 2), 4);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(p + 4), 0x6E7F1EFF);
            p += 8;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(p), AttrIceControlling);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(p + 2), 8);
            RandomNumberGenerator.Fill(span.Slice(p + 4, 8));
            p += 12;
            if (useCandidate)
            {
                BinaryPrimitives.WriteUInt16BigEndian(span.Slice(p), AttrUseCandidate);
                p += 4;
            }
            AppendIntegrityAndFingerprint(span, p, key);
            return b;
        }

        public static bool IsBindingSuccess(ReadOnlySpan<byte> data, byte[] transactionId) =>
            IsStun(data) && BinaryPrimitives.ReadUInt16BigEndian(data) == BindingSuccessType && data.Slice(8, 12).SequenceEqual(transactionId);

        private static int AppendIntegrityAndFingerprint(Span<byte> b, int p, byte[] key)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.Slice(2), (ushort)(p - 20 + 24));
            Span<byte> mac = stackalloc byte[20];
            HMACSHA1.HashData(key, b.Slice(0, p), mac);
            BinaryPrimitives.WriteUInt16BigEndian(b.Slice(p), AttrMessageIntegrity);
            BinaryPrimitives.WriteUInt16BigEndian(b.Slice(p + 2), 20);
            mac.CopyTo(b.Slice(p + 4));
            p += 24;
            BinaryPrimitives.WriteUInt16BigEndian(b.Slice(2), (ushort)(p - 20 + 8));
            uint crc = Crc.Crc32(b.Slice(0, p)) ^ FingerprintXor;
            BinaryPrimitives.WriteUInt16BigEndian(b.Slice(p), AttrFingerprint);
            BinaryPrimitives.WriteUInt16BigEndian(b.Slice(p + 2), 4);
            BinaryPrimitives.WriteUInt32BigEndian(b.Slice(p + 4), crc);
            return p + 8;
        }
    }
}
