using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// Mesh-wide session ids. A gateway draws a random 32-bit incarnation when it starts and numbers its sessions from
    /// 1, so two gateways (or two starts of the same gateway) never hand out the same id without any coordination:
    /// <c>id = incarnation &lt;&lt; 32 | sequence</c>. Workers key player state by this id, and it stays the same when
    /// the client reconnects through another gateway with its session token.
    /// </summary>
    public static class SessionIds
    {
        public static ulong Make(uint incarnation, uint sequence) => ((ulong)incarnation << 32) | sequence;
        public static uint Incarnation(ulong sessionId) => (uint)(sessionId >> 32);
        public static uint Sequence(ulong sessionId) => (uint)sessionId;

        /// <summary>A fresh incarnation: random, never 0 (0 is "no incarnation" on the wire).</summary>
        public static uint NewIncarnation()
        {
            var bytes = new byte[4];
            uint value = 0;
            while (value == 0)
            {
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                value = BitConverter.ToUInt32(bytes, 0);
            }
            return value;
        }
    }

    /// <summary>What a session token says about the session it names.</summary>
    public struct SessionClaims
    {
        public ulong SessionId;
        public string Identity;
        public string Name;
        public bool IsBot;
        /// <summary>The connection generation the session had when the token was issued (<see cref="SpawnPlayerMsg.Generation"/>).</summary>
        public ulong Generation;
        public long ExpiresAt;
    }

    /// <summary>
    /// Issues and checks the session tokens a gateway gives every welcomed client (<see cref="WelcomeMsg.SessionToken"/>).
    /// A token is an HS256 JWT over the session id, identity, display name and generation, signed with a key every
    /// gateway of the mesh shares (derived from the player signing key, so one configured secret covers both), which
    /// is what lets a client reconnect through <i>another</i> gateway and get the same session back. A token says
    /// nothing about whether the session still exists: that is the worker's <see cref="PlayerSessions"/> to decide.
    /// </summary>
    public sealed class SessionTokens
    {
        public const string Issuer = "nebula-session";
        private readonly byte[] _key;

        public SessionTokens(byte[] key)
        {
            if (key == null || key.Length < 16) throw new ArgumentException("the session key must be at least 16 bytes");
            _key = key;
        }

        /// <summary>The session key for a mesh whose player signing key is <paramref name="playerKey"/> (a different key for a different purpose, from one secret).</summary>
        public static byte[] DeriveKey(byte[] playerKey)
        {
            using (var sha = SHA256.Create())
            {
                var label = Encoding.UTF8.GetBytes("nebula-sessions:");
                var input = new byte[label.Length + playerKey.Length];
                Buffer.BlockCopy(label, 0, input, 0, label.Length);
                Buffer.BlockCopy(playerKey, 0, input, label.Length, playerKey.Length);
                return sha.ComputeHash(input);
            }
        }

        public string Issue(in SessionClaims claims)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"iss\":\"").Append(Issuer).Append('"');
            sb.Append(",\"sid\":\"").Append(claims.SessionId.ToString(CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"idn\":").Append(JsonWriter.Quote(claims.Identity ?? ""));
            sb.Append(",\"nm\":").Append(JsonWriter.Quote(claims.Name ?? ""));
            sb.Append(",\"bot\":").Append(claims.IsBot ? "1" : "0");
            sb.Append(",\"gen\":\"").Append(claims.Generation.ToString(CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"iat\":").Append(JsonWebToken.UnixNow().ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"exp\":").Append(claims.ExpiresAt.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            return JsonWebToken.SignHs256(sb.ToString(), _key);
        }

        /// <summary>True when the token was signed for this mesh and has not expired; <paramref name="error"/> says why otherwise.</summary>
        public bool Verify(string token, out SessionClaims claims, out string error)
        {
            claims = default;
            if (!JsonWebToken.TryParse(token, out var header, out var payload, out var signed, out var signature, out error)) return false;
            if (JsonWebToken.Claim(header, "alg") != "HS256") { error = "unexpected algorithm for a session token"; return false; }
            if (JsonWebToken.Claim(payload, "iss") != Issuer) { error = "not a session token"; return false; }
            byte[] expected;
            using (var mac = new HMACSHA256(_key)) expected = mac.ComputeHash(signed);
            if (!FixedTimeEquals(expected, signature)) { error = "session token signature does not match this mesh"; return false; }
            if (!ulong.TryParse(JsonWebToken.Claim(payload, "sid"), NumberStyles.None, CultureInfo.InvariantCulture, out claims.SessionId) || claims.SessionId == 0) { error = "session token has no session id"; return false; }
            ulong.TryParse(JsonWebToken.Claim(payload, "gen"), NumberStyles.None, CultureInfo.InvariantCulture, out claims.Generation);
            claims.Identity = JsonWebToken.Claim(payload, "idn") ?? "";
            claims.Name = JsonWebToken.Claim(payload, "nm") ?? "";
            claims.IsBot = JsonWebToken.NumericClaim(payload, "bot") == 1;
            claims.ExpiresAt = JsonWebToken.NumericClaim(payload, "exp") ?? 0;
            if (claims.ExpiresAt != 0 && claims.ExpiresAt < JsonWebToken.UnixNow()) { error = "session token expired"; return false; }
            return true;
        }

        internal static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    /// <summary>
    /// How gateways and workers prove to each other that they belong to the mesh: <see cref="HelloMsg.Token"/> on an
    /// infrastructure link carries <c>unixSeconds.base64url(HMAC-SHA256(key, role|id|incarnation|unixSeconds))</c>
    /// with a key derived from the mesh token (<see cref="NebulaConfig.MeshToken"/>). A peer without the mesh token
    /// cannot mint one, and a captured token is only good for the same role, id and incarnation for
    /// <see cref="MaxAgeSeconds"/>. With no mesh token configured (a local mesh) every peer is accepted, as before.
    /// Players are authenticated separately (<see cref="OidcTokenValidator"/>, <see cref="AnonymousIdentityIssuer"/>).
    /// </summary>
    public static class MeshPeerAuth
    {
        public const long MaxAgeSeconds = 300;

        public static byte[] DeriveKey(string meshToken)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes("nebula-peer-auth:" + meshToken));
        }

        public static string Issue(string meshToken, PeerRole role, string id, uint incarnation) =>
            string.IsNullOrEmpty(meshToken) ? "" : Issue(DeriveKey(meshToken), role, id, incarnation, JsonWebToken.UnixNow());

        public static string Issue(byte[] key, PeerRole role, string id, uint incarnation, long unixSeconds)
        {
            var mac = Mac(key, role, id, incarnation, unixSeconds);
            return unixSeconds.ToString(CultureInfo.InvariantCulture) + "." + JsonWebToken.Base64UrlEncode(mac);
        }

        /// <summary>True when <paramref name="token"/> proves the peer holds the mesh token, or when the mesh has none.</summary>
        public static bool Verify(string meshToken, PeerRole role, string id, uint incarnation, string token, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(meshToken)) return true;
            return Verify(DeriveKey(meshToken), role, id, incarnation, token, JsonWebToken.UnixNow(), out error);
        }

        public static bool Verify(byte[] key, PeerRole role, string id, uint incarnation, string token, long now, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(token)) { error = "no mesh credential"; return false; }
            int dot = token.IndexOf('.');
            if (dot <= 0 || !long.TryParse(token.Substring(0, dot), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long issued)) { error = "malformed mesh credential"; return false; }
            if (Math.Abs(now - issued) > MaxAgeSeconds) { error = "mesh credential is too old (clocks differ by more than 5 minutes?)"; return false; }
            byte[] presented;
            try { presented = JsonWebToken.Base64UrlDecode(token.Substring(dot + 1)); }
            catch (FormatException) { error = "malformed mesh credential"; return false; }
            if (!SessionTokens.FixedTimeEquals(Mac(key, role, id, incarnation, issued), presented)) { error = "mesh credential does not match this mesh's token"; return false; }
            return true;
        }

        private static byte[] Mac(byte[] key, PeerRole role, string id, uint incarnation, long unixSeconds)
        {
            string message = ((byte)role).ToString(CultureInfo.InvariantCulture) + "|" + (id ?? "") + "|" + incarnation.ToString(CultureInfo.InvariantCulture) + "|" + unixSeconds.ToString(CultureInfo.InvariantCulture);
            using (var mac = new HMACSHA256(key)) return mac.ComputeHash(Encoding.UTF8.GetBytes(message));
        }
    }
}
