using System;

namespace Nebula.WebRtc
{
    /// <summary>CRC-32 (the STUN FINGERPRINT attribute) and CRC-32C (the SCTP packet checksum), table driven.</summary>
    internal static class Crc
    {
        private static readonly uint[] Ieee = Table(0xEDB88320u);
        private static readonly uint[] Castagnoli = Table(0x82F63B78u);
        private static readonly byte[] ZeroChecksum = new byte[4];

        private static uint[] Table(uint polynomial)
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? polynomial ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        private static uint Update(uint[] table, uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        public static uint Crc32(ReadOnlySpan<byte> data) => ~Update(Ieee, 0xFFFFFFFFu, data);

        /// <summary>CRC-32C of an SCTP packet, computed as if its checksum field (bytes 8 to 11) were zero.</summary>
        public static uint SctpChecksum(ReadOnlySpan<byte> packet)
        {
            uint crc = Update(Castagnoli, 0xFFFFFFFFu, packet.Slice(0, 8));
            crc = Update(Castagnoli, crc, ZeroChecksum);
            return ~Update(Castagnoli, crc, packet.Slice(12));
        }
    }
}
