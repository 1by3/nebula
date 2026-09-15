using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Nebula
{
    /// <summary>Outcome of checking the token a client presented in <c>Hello</c> (see <see cref="OidcTokenValidator"/>).</summary>
    public struct AuthResult
    {
        /// <summary>The token was valid; <see cref="Identity"/>, <see cref="Issuer"/> and <see cref="Subject"/> are set.</summary>
        public bool Ok;
        /// <summary>The player's stable identity (<see cref="PlayerIdentity.Derive"/>).</summary>
        public string Identity;
        /// <summary>The token's <c>iss</c> claim: the OpenID provider, or <see cref="PlayerIdentity.AnonymousIssuer"/> for a gateway-issued token.</summary>
        public string Issuer;
        /// <summary>The token's <c>sub</c> claim: the provider's user id.</summary>
        public string Subject;
        /// <summary>Why the token was rejected (empty when <see cref="Ok"/>).</summary>
        public string Error;

        public static AuthResult Fail(string error) => new AuthResult { Error = error ?? "invalid token" };

        public static AuthResult Accept(string issuer, string subject) => new AuthResult
        {
            Ok = true,
            Issuer = issuer,
            Subject = subject,
            Identity = PlayerIdentity.Derive(issuer, subject),
        };
    }

    /// <summary>
    /// A player's identity across sessions: 64 lowercase hex characters, the SHA-256 of the token's issuer and
    /// subject. The same account at the same provider always maps to the same identity, and two providers can
    /// never collide. It is what <see cref="NetworkIdentity.OwnerIdentity"/> and <see cref="NebulaClient.Identity"/>
    /// carry, and what a game keys player records on (for example a <see cref="PersistentEntity.Key"/>).
    /// </summary>
    public static class PlayerIdentity
    {
        /// <summary>The <c>iss</c> of tokens the gateway issues itself to clients that presented none.</summary>
        public const string AnonymousIssuer = "nebula";

        public static string Derive(string issuer, string subject)
        {
            var bytes = Encoding.UTF8.GetBytes((issuer ?? "") + "\0" + (subject ?? ""));
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>Compact JSON Web Token (JWT) encoding: split, decode and sign. No policy lives here.</summary>
    public static class JsonWebToken
    {
        public static long UnixNow() => (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        /// <summary>Split <c>header.payload.signature</c> and decode the two JSON objects. Does not verify anything.</summary>
        public static bool TryParse(string token, out Dictionary<string, object> header, out Dictionary<string, object> payload, out byte[] signedBytes, out byte[] signature, out string error)
        {
            header = payload = null;
            signedBytes = signature = null;
            error = null;
            if (string.IsNullOrEmpty(token)) { error = "empty token"; return false; }
            var parts = token.Split('.');
            if (parts.Length != 3) { error = "not a compact JWT"; return false; }
            try
            {
                if (!PersistenceJson.TryParseObject(Encoding.UTF8.GetString(Base64UrlDecode(parts[0])), out header, out error)) return false;
                if (!PersistenceJson.TryParseObject(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])), out payload, out error)) return false;
                signature = Base64UrlDecode(parts[2]);
            }
            catch (Exception e) { error = "malformed token: " + e.Message; return false; }
            signedBytes = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            return true;
        }

        /// <summary>An HS256 token over <paramref name="payloadJson"/>.</summary>
        public static string SignHs256(string payloadJson, byte[] key)
        {
            string head = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
            string body = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
            using (var mac = new HMACSHA256(key))
                return head + "." + body + "." + Base64UrlEncode(mac.ComputeHash(Encoding.ASCII.GetBytes(head + "." + body)));
        }

        public static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Base64UrlDecode(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException("bad base64url length");
            }
            return Convert.FromBase64String(s);
        }

        /// <summary>A claim as a string (numbers and booleans are formatted; missing = null).</summary>
        public static string Claim(Dictionary<string, object> claims, string name) => claims != null && claims.TryGetValue(name, out var v) ? PersistenceJson.AsString(v) : null;

        /// <summary>A numeric claim (<c>exp</c>, <c>iat</c>), or null when missing or not a number.</summary>
        public static long? NumericClaim(Dictionary<string, object> claims, string name)
        {
            if (claims == null || !claims.TryGetValue(name, out var v)) return null;
            if (v is double d) return (long)d;
            if (v is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            return null;
        }

        /// <summary>True when the <c>aud</c> claim (a string or an array of strings) names <paramref name="audience"/>.</summary>
        public static bool HasAudience(Dictionary<string, object> claims, string audience)
        {
            if (claims == null || !claims.TryGetValue("aud", out var v)) return false;
            if (v is string s) return s == audience;
            if (v is List<object> list)
                foreach (var item in list) if (item is string a && a == audience) return true;
            return false;
        }
    }

    /// <summary>
    /// Identities the gateway hands to clients that present no token: an HS256 token with <c>iss</c>
    /// <see cref="PlayerIdentity.AnonymousIssuer"/> and a random <c>sub</c>, which the client keeps and presents on
    /// its next connection to get the same <see cref="AuthResult.Identity"/> back. Every gateway of a mesh must
    /// use the same key (see <see cref="NebulaConfig.AuthSigningKey"/>).
    /// </summary>
    public sealed class AnonymousIdentityIssuer
    {
        private readonly byte[] _key;

        public AnonymousIdentityIssuer(byte[] key)
        {
            if (key == null || key.Length < 16) throw new ArgumentException("the signing key must be at least 16 bytes");
            _key = key;
        }

        /// <summary>A key from a configured secret (the mesh token, or a secret of its own).</summary>
        public static byte[] DeriveKey(string secret)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes("nebula-anonymous-identities:" + secret));
        }

        /// <summary>A random key kept in <paramref name="path"/>, created on first use, so identities survive a restart with nothing configured.</summary>
        public static byte[] LoadOrCreateKeyFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var existing = JsonWebToken.Base64UrlDecode(File.ReadAllText(path).Trim());
                    if (existing.Length >= 16) return existing;
                }
            }
            catch (Exception e) { NebulaLog.Warn($"auth: could not read {path}: {e.Message}; creating a new key"); }
            var key = RandomKey();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, JsonWebToken.Base64UrlEncode(key));
            }
            catch (Exception e) { NebulaLog.Warn($"auth: could not write {path}: {e.Message}; anonymous identities will not survive a restart"); }
            return key;
        }

        public static byte[] RandomKey()
        {
            var key = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            return key;
        }

        /// <summary>A new identity: the token to give the client, and the subject it was minted for.</summary>
        public string Issue(out string subject)
        {
            var random = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            subject = PlayerIdentity.Hex(random);
            string payload = "{\"iss\":\"" + PlayerIdentity.AnonymousIssuer + "\",\"sub\":\"" + subject + "\",\"iat\":" + JsonWebToken.UnixNow().ToString(CultureInfo.InvariantCulture) + "}";
            return JsonWebToken.SignHs256(payload, _key);
        }

        /// <summary>True when <paramref name="token"/> claims to be one of ours (so it is checked here, not against an OpenID provider).</summary>
        public static bool IsAnonymousToken(string token)
        {
            return JsonWebToken.TryParse(token, out _, out var payload, out _, out _, out _) && JsonWebToken.Claim(payload, "iss") == PlayerIdentity.AnonymousIssuer;
        }

        public AuthResult Verify(string token)
        {
            if (!JsonWebToken.TryParse(token, out var header, out var payload, out var signed, out var signature, out var error)) return AuthResult.Fail(error);
            if (JsonWebToken.Claim(header, "alg") != "HS256") return AuthResult.Fail("unexpected algorithm for an anonymous token");
            if (JsonWebToken.Claim(payload, "iss") != PlayerIdentity.AnonymousIssuer) return AuthResult.Fail("not an anonymous token");
            string subject = JsonWebToken.Claim(payload, "sub");
            if (string.IsNullOrEmpty(subject)) return AuthResult.Fail("anonymous token has no subject");
            byte[] expected;
            using (var mac = new HMACSHA256(_key)) expected = mac.ComputeHash(signed);
            if (!FixedTimeEquals(expected, signature)) return AuthResult.Fail("anonymous token signature does not match this mesh");
            return AuthResult.Accept(PlayerIdentity.AnonymousIssuer, subject);
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    /// <summary>
    /// Checks OpenID Connect ID tokens against the issuers a mesh trusts (<see cref="NebulaConfig.AuthIssuers"/>):
    /// signature (RS256/384/512 and ES256/384/512 with the provider's published keys), issuer, expiry, and audience.
    /// Keys come from the issuer's discovery document and JWKS endpoint, fetched on a background thread and cached;
    /// a token with an unknown key id triggers one refresh (key rotation). <see cref="Validate"/> answers at once
    /// when the key is cached and otherwise once the fetch lands, always from <see cref="Tick"/> on the calling
    /// thread, so the gateway never blocks on the network.
    /// </summary>
    public sealed class OidcTokenValidator : IDisposable
    {
        /// <summary>Fetch a URL's body (run on a worker thread). Tests substitute their own.</summary>
        public delegate string Fetcher(string url);

        private sealed class Issuer
        {
            public string Name;
            public readonly Dictionary<string, AsymmetricAlgorithm> Keys = new Dictionary<string, AsymmetricAlgorithm>();
            public readonly List<AsymmetricAlgorithm> KeysWithoutId = new List<AsymmetricAlgorithm>();
            public bool Fetching;
            public DateTime LastFetch = DateTime.MinValue;
            public readonly List<(string token, Action<AuthResult> done)> Waiting = new List<(string, Action<AuthResult>)>();
        }

        private readonly Dictionary<string, Issuer> _issuers = new Dictionary<string, Issuer>();
        private readonly string _audience;
        private readonly Fetcher _fetch;
        private readonly ConcurrentQueue<Action> _completions = new ConcurrentQueue<Action>();
        private static HttpClient _http;

        /// <summary>Tolerance on <c>exp</c>, <c>nbf</c> and <c>iat</c>.</summary>
        public TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
        /// <summary>How soon after a fetch an unknown key id may trigger another (a flood of bad tokens must not hammer the provider).</summary>
        public TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);
        /// <summary>Tokens waiting for a key fetch.</summary>
        public int PendingCount { get { int n = 0; foreach (var i in _issuers.Values) n += i.Waiting.Count; return n; } }
        public int IssuerCount => _issuers.Count;

        /// <param name="issuers">Trusted <c>iss</c> values (the provider URLs; a trailing slash is ignored).</param>
        /// <param name="audience">Required <c>aud</c> (the client id the provider issued the game). Empty skips the check.</param>
        /// <param name="fetch">How discovery documents and key sets are downloaded; null uses HTTP.</param>
        public OidcTokenValidator(IEnumerable<string> issuers, string audience, Fetcher fetch = null)
        {
            _audience = audience ?? "";
            _fetch = fetch ?? HttpGet;
            if (issuers != null)
                foreach (var raw in issuers)
                {
                    string name = Normalize(raw);
                    if (name.Length > 0 && !_issuers.ContainsKey(name)) _issuers[name] = new Issuer { Name = name };
                }
        }

        /// <summary>Split a comma- or space-separated issuer list (<see cref="NebulaConfig.AuthIssuers"/>).</summary>
        public static List<string> ParseIssuerList(string spec)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(spec)) return list;
            foreach (var part in spec.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries)) list.Add(part.Trim());
            return list;
        }

        private static string Normalize(string issuer) => (issuer ?? "").Trim().TrimEnd('/');

        /// <summary>Install a key set by hand (tests, or a provider with no discovery endpoint). Replaces the issuer's cached keys.</summary>
        public void AddKeys(string issuer, string jwksJson)
        {
            string name = Normalize(issuer);
            if (!_issuers.TryGetValue(name, out var iss)) _issuers[name] = iss = new Issuer { Name = name };
            InstallKeys(iss, jwksJson);
            iss.LastFetch = DateTime.UtcNow;
        }

        /// <summary>
        /// Check <paramref name="token"/>. <paramref name="done"/> runs synchronously when the answer is known now,
        /// otherwise from a later <see cref="Tick"/> once the issuer's keys arrive.
        /// </summary>
        public void Validate(string token, Action<AuthResult> done)
        {
            if (!JsonWebToken.TryParse(token, out var header, out var payload, out _, out _, out var error)) { done(AuthResult.Fail(error)); return; }
            string issuerName = Normalize(JsonWebToken.Claim(payload, "iss"));
            if (issuerName.Length == 0) { done(AuthResult.Fail("token has no issuer")); return; }
            if (!_issuers.TryGetValue(issuerName, out var issuer)) { done(AuthResult.Fail($"issuer '{issuerName}' is not trusted by this mesh")); return; }
            var claimsError = CheckClaims(payload);
            if (claimsError != null) { done(AuthResult.Fail(claimsError)); return; }

            string kid = JsonWebToken.Claim(header, "kid");
            if (HasKeyFor(issuer, kid) || (issuer.Fetching == false && DateTime.UtcNow - issuer.LastFetch < MinRefreshInterval))
            {
                done(VerifySignature(issuer, token));
                return;
            }
            issuer.Waiting.Add((token, done));
            StartFetch(issuer);
        }

        /// <summary>Deliver the results of finished key fetches. Call from the thread that calls <see cref="Validate"/>.</summary>
        public void Tick()
        {
            while (_completions.TryDequeue(out var action)) action();
        }

        public void Dispose()
        {
            foreach (var iss in _issuers.Values)
            {
                foreach (var k in iss.Keys.Values) k.Dispose();
                foreach (var k in iss.KeysWithoutId) k.Dispose();
                iss.Keys.Clear();
                iss.KeysWithoutId.Clear();
            }
        }

        // ---------------------------------------------------------------------------------------- claims

        private string CheckClaims(Dictionary<string, object> payload)
        {
            long now = JsonWebToken.UnixNow();
            long skew = (long)ClockSkew.TotalSeconds;
            var exp = JsonWebToken.NumericClaim(payload, "exp");
            if (exp == null) return "token has no expiry";
            if (exp.Value + skew < now) return "token has expired";
            var nbf = JsonWebToken.NumericClaim(payload, "nbf");
            if (nbf != null && nbf.Value - skew > now) return "token is not valid yet";
            if (string.IsNullOrEmpty(JsonWebToken.Claim(payload, "sub"))) return "token has no subject";
            if (_audience.Length > 0 && !JsonWebToken.HasAudience(payload, _audience)) return "token was not issued for this game (audience)";
            return null;
        }

        // ---------------------------------------------------------------------------------------- keys

        private static bool HasKeyFor(Issuer issuer, string kid)
        {
            if (string.IsNullOrEmpty(kid)) return issuer.Keys.Count > 0 || issuer.KeysWithoutId.Count > 0;
            return issuer.Keys.ContainsKey(kid) || issuer.KeysWithoutId.Count > 0;
        }

        private AuthResult VerifySignature(Issuer issuer, string token)
        {
            if (!JsonWebToken.TryParse(token, out var header, out var payload, out var signed, out var signature, out var error)) return AuthResult.Fail(error);
            string alg = JsonWebToken.Claim(header, "alg") ?? "";
            if (!TryHashFor(alg, out var hash, out bool ec)) return AuthResult.Fail($"unsupported token algorithm '{alg}'");
            string kid = JsonWebToken.Claim(header, "kid");
            var candidates = new List<AsymmetricAlgorithm>();
            if (!string.IsNullOrEmpty(kid) && issuer.Keys.TryGetValue(kid, out var exact)) candidates.Add(exact);
            else if (string.IsNullOrEmpty(kid)) candidates.AddRange(issuer.Keys.Values);
            candidates.AddRange(issuer.KeysWithoutId);
            if (candidates.Count == 0) return AuthResult.Fail(string.IsNullOrEmpty(kid) ? "issuer published no keys" : $"token signed with an unknown key '{kid}'");
            foreach (var key in candidates)
            {
                try
                {
                    if (!ec && key is RSA rsa && rsa.VerifyData(signed, signature, hash, RSASignaturePadding.Pkcs1)) return AuthResult.Accept(issuer.Name, JsonWebToken.Claim(payload, "sub"));
                    if (ec && key is ECDsa ecdsa && ecdsa.VerifyData(signed, signature, hash)) return AuthResult.Accept(issuer.Name, JsonWebToken.Claim(payload, "sub"));
                }
                catch (CryptographicException) { }
            }
            return AuthResult.Fail("token signature does not verify");
        }

        private static bool TryHashFor(string alg, out HashAlgorithmName hash, out bool ec)
        {
            ec = alg.StartsWith("ES", StringComparison.Ordinal);
            switch (alg)
            {
                case "RS256": case "ES256": hash = HashAlgorithmName.SHA256; return true;
                case "RS384": case "ES384": hash = HashAlgorithmName.SHA384; return true;
                case "RS512": case "ES512": hash = HashAlgorithmName.SHA512; return true;
                default: hash = default; return false;
            }
        }

        private void StartFetch(Issuer issuer)
        {
            if (issuer.Fetching) return;
            issuer.Fetching = true;
            string name = issuer.Name;
            var fetch = _fetch;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string jwks = null, error = null;
                try
                {
                    string discovery = fetch(name + "/.well-known/openid-configuration");
                    if (!PersistenceJson.TryParseObject(discovery, out var doc, out error)) throw new FormatException("discovery document: " + error);
                    string jwksUri = PersistenceJson.GetString(doc, "jwks_uri");
                    if (jwksUri.Length == 0) throw new FormatException("discovery document has no jwks_uri");
                    jwks = fetch(jwksUri);
                }
                catch (Exception e) { error = e.Message; }
                _completions.Enqueue(() => FinishFetch(issuer, jwks, error));
            });
        }

        private void FinishFetch(Issuer issuer, string jwks, string error)
        {
            issuer.Fetching = false;
            issuer.LastFetch = DateTime.UtcNow;
            if (jwks != null)
            {
                try { InstallKeys(issuer, jwks); NebulaLog.Info($"auth: {issuer.Name}: {issuer.Keys.Count + issuer.KeysWithoutId.Count} signing key(s)"); }
                catch (Exception e) { error = e.Message; }
            }
            if (error != null) NebulaLog.Warn($"auth: could not fetch keys for {issuer.Name}: {error}");
            var waiting = new List<(string, Action<AuthResult>)>(issuer.Waiting);
            issuer.Waiting.Clear();
            foreach (var (token, done) in waiting)
                done(error != null && issuer.Keys.Count == 0 && issuer.KeysWithoutId.Count == 0 ? AuthResult.Fail("could not reach the identity provider") : VerifySignature(issuer, token));
        }

        private static void InstallKeys(Issuer issuer, string jwksJson)
        {
            if (!PersistenceJson.TryParseObject(jwksJson, out var doc, out var error)) throw new FormatException("key set: " + error);
            if (!doc.TryGetValue("keys", out var keysObj) || !(keysObj is List<object> keys)) throw new FormatException("key set has no 'keys' array");
            foreach (var k in issuer.Keys.Values) k.Dispose();
            foreach (var k in issuer.KeysWithoutId) k.Dispose();
            issuer.Keys.Clear();
            issuer.KeysWithoutId.Clear();
            foreach (var entry in keys)
            {
                if (!(entry is Dictionary<string, object> jwk)) continue;
                string use = PersistenceJson.GetString(jwk, "use");
                if (use.Length > 0 && use != "sig") continue;
                AsymmetricAlgorithm key;
                try { key = ImportJwk(jwk); }
                catch (Exception e) { NebulaLog.Warn($"auth: {issuer.Name}: skipping key '{PersistenceJson.GetString(jwk, "kid")}': {e.Message}"); continue; }
                if (key == null) continue;
                string kid = PersistenceJson.GetString(jwk, "kid");
                if (kid.Length > 0) issuer.Keys[kid] = key; else issuer.KeysWithoutId.Add(key);
            }
        }

        private static AsymmetricAlgorithm ImportJwk(Dictionary<string, object> jwk)
        {
            switch (PersistenceJson.GetString(jwk, "kty"))
            {
                case "RSA":
                {
                    var rsa = RSA.Create();
                    rsa.ImportParameters(new RSAParameters
                    {
                        Modulus = JsonWebToken.Base64UrlDecode(PersistenceJson.GetString(jwk, "n")),
                        Exponent = JsonWebToken.Base64UrlDecode(PersistenceJson.GetString(jwk, "e")),
                    });
                    return rsa;
                }
                case "EC":
                {
                    ECCurve curve;
                    switch (PersistenceJson.GetString(jwk, "crv"))
                    {
                        case "P-256": curve = ECCurve.NamedCurves.nistP256; break;
                        case "P-384": curve = ECCurve.NamedCurves.nistP384; break;
                        case "P-521": curve = ECCurve.NamedCurves.nistP521; break;
                        default: return null;
                    }
                    var ecdsa = ECDsa.Create();
                    ecdsa.ImportParameters(new ECParameters
                    {
                        Curve = curve,
                        Q = new ECPoint
                        {
                            X = JsonWebToken.Base64UrlDecode(PersistenceJson.GetString(jwk, "x")),
                            Y = JsonWebToken.Base64UrlDecode(PersistenceJson.GetString(jwk, "y")),
                        },
                    });
                    return ecdsa;
                }
                default: return null; // oct and unknown types are never used for ID tokens
            }
        }

        private static string HttpGet(string url)
        {
            if (_http == null) _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using (var response = _http.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{url}: HTTP {(int)response.StatusCode}");
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }
    }
}
