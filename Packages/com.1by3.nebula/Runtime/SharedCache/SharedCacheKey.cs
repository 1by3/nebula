using System;
using System.Security.Cryptography;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// The rules every part of the shared cache applies to a key and to a stored object: the longest key and object,
    /// which characters a key may use, and how an object's checksum is written. A key is chosen by the caller,
    /// usually a namespace plus a hash of the inputs that produced the object, such as <c>terrain.v3.9f2c1a…</c>.
    /// </summary>
    public static class SharedCacheKey
    {
        /// <summary>Longest key, in characters.</summary>
        public const int MaxKeyLength = 200;

        /// <summary>
        /// Largest object a cache accepts by default, in bytes (32 MiB). <see cref="NebulaConfig.SharedCacheMaxObjectBytes"/>
        /// changes it; set the same value on the orchestrator and on every worker.
        /// </summary>
        public const int DefaultMaxObjectBytes = 32 * 1024 * 1024;

        /// <summary>Days a key may go unread before the orchestrator removes it, by default.</summary>
        public const int DefaultExpiryDays = 30;

        /// <summary>
        /// Seconds an entry's last-read time may lag behind the real time before a read refreshes it. Reads inside
        /// this window do not write to the store, so a busy key costs no database writes.
        /// </summary>
        public const int TouchIntervalSeconds = 24 * 60 * 60;

        /// <summary>
        /// True when <paramref name="key"/> can be used: 1 to <see cref="MaxKeyLength"/> characters from
        /// <c>A-Z a-z 0-9 . _ - :</c>, starting with a letter or digit. Anything else is refused with a reason in
        /// <paramref name="error"/>.
        /// </summary>
        public static bool TryValidate(string key, out string error)
        {
            if (string.IsNullOrEmpty(key)) { error = "the key is empty"; return false; }
            if (key.Length > MaxKeyLength) { error = $"the key is {key.Length} characters; the limit is {MaxKeyLength}"; return false; }
            if (!IsAlphanumeric(key[0])) { error = "the key must start with a letter or digit"; return false; }
            for (int i = 1; i < key.Length; i++)
            {
                char c = key[i];
                if (IsAlphanumeric(c) || c == '.' || c == '_' || c == '-' || c == ':') continue;
                error = $"the key may only contain letters, digits and . _ - : (found '{c}')";
                return false;
            }
            error = null;
            return true;
        }

        private static bool IsAlphanumeric(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        /// <summary>The checksum of <paramref name="bytes"/>: SHA-256 as 64 lowercase hexadecimal characters.</summary>
        public static string Checksum(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes ?? Array.Empty<byte>()));
        }

        /// <summary>True when the two checksums are the same value, ignoring letter case.</summary>
        public static bool SameChecksum(string a, string b) =>
            !string.IsNullOrEmpty(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        internal static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
