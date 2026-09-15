using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Nebula.Tls
{
    internal sealed class AcmeException : Exception
    {
        public AcmeException(string message) : base(message) { }
    }

    /// <summary>
    /// An ACME client (RFC 8555) for one job: a certificate for the gateway's own address, proven with HTTP-01. It
    /// speaks the profiles extension (draft-ietf-acme-profiles), which Let's Encrypt requires for IP address
    /// certificates (RFC 8738), and signs requests with an ES256 account key.
    /// </summary>
    internal sealed class AcmeClient : IDisposable
    {
        public const string LetsEncrypt = "https://acme-v02.api.letsencrypt.org/directory";
        private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(120);

        private sealed class Response
        {
            public string Text, Location;
            private JsonNode _json;
            public JsonNode Json => _json ??= JsonNode.Parse(Text);
        }

        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string _directoryUrl;
        private readonly ECDsa _accountKey;
        private string _newNonceUrl, _newAccountUrl, _newOrderUrl, _accountUrl, _nonce;
        private JsonObject _profiles;

        public AcmeClient(string directoryUrl, ECDsa accountKey)
        {
            _directoryUrl = directoryUrl;
            _accountKey = accountKey;
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("nebula-gateway");
        }

        /// <summary>RFC 7638 thumbprint of the account key, the second half of every key authorization.</summary>
        public string Thumbprint
        {
            get
            {
                var q = _accountKey.ExportParameters(false).Q;
                string canonical = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + Base64Url(q.X) + "\",\"y\":\"" + Base64Url(q.Y) + "\"}";
                return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            }
        }

        /// <summary>
        /// Order, authorize and download a certificate for <paramref name="identifier"/> (an IP address or a DNS name).
        /// <paramref name="publish"/> must make each (token, key authorization) pair answer at
        /// <c>http://identifier/.well-known/acme-challenge/token</c> until <paramref name="unpublish"/> removes it.
        /// Returns the PEM chain (leaf first) and the certificate's new private key.
        /// </summary>
        public async Task<(string ChainPem, ECDsa Key)> IssueAsync(string identifier, string profile, string email,
            Action<string, string> publish, Action<string> unpublish, CancellationToken ct)
        {
            await LoadDirectoryAsync(ct);
            await EnsureAccountAsync(email, ct);

            bool ip = IPAddress.TryParse(identifier, out var address);
            var order = new JsonObject
            {
                ["identifiers"] = new JsonArray(new JsonObject { ["type"] = ip ? "ip" : "dns", ["value"] = ip ? address.ToString() : identifier }),
            };
            if (!string.IsNullOrEmpty(profile) && _profiles != null)
            {
                if (!_profiles.ContainsKey(profile))
                    throw new AcmeException($"the ACME server offers no '{profile}' profile (it offers {string.Join(", ", _profiles.Select(p => p.Key))})");
                order["profile"] = profile;
            }
            var created = await PostAsync(_newOrderUrl, order, ct);
            string orderUrl = created.Location ?? throw new AcmeException("the ACME server did not say where the new order is");
            var orderBody = created.Json;

            foreach (var authorization in orderBody["authorizations"]?.AsArray() ?? new JsonArray())
                await AuthorizeAsync(authorization.GetValue<string>(), publish, unpublish, ct);

            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(new X500DistinguishedName(""), key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            if (ip) names.AddIpAddress(address);
            else names.AddDnsName(identifier);
            // RFC 5280: with an empty subject the subject alternative name extension is critical.
            request.CertificateExtensions.Add(names.Build(critical: true));
            orderBody = await PollAsync(orderUrl, orderBody, status => status == "ready", "the order", ct);
            orderBody = (await PostAsync(orderBody["finalize"].GetValue<string>(), new JsonObject { ["csr"] = Base64Url(request.CreateSigningRequest()) }, ct)).Json;
            orderBody = await PollAsync(orderUrl, orderBody, status => status == "valid", "the order", ct);
            string chain = (await PostAsync(orderBody["certificate"].GetValue<string>(), null, ct)).Text;
            return (chain, key);
        }

        private async Task AuthorizeAsync(string url, Action<string, string> publish, Action<string> unpublish, CancellationToken ct)
        {
            var authorization = (await PostAsync(url, null, ct)).Json;
            if (Status(authorization) == "valid") return;
            string identifier = authorization["identifier"]?["value"]?.GetValue<string>() ?? url;
            var challenge = authorization["challenges"]?.AsArray().FirstOrDefault(c => c?["type"]?.GetValue<string>() == "http-01")
                ?? throw new AcmeException($"the ACME server offers no http-01 challenge for {identifier}");
            string token = challenge["token"].GetValue<string>();
            publish(token, token + "." + Thumbprint);
            try
            {
                await PostAsync(challenge["url"].GetValue<string>(), new JsonObject(), ct);
                await PollAsync(url, authorization, status => status == "valid", "the authorization of " + identifier, ct);
            }
            finally
            {
                unpublish(token);
            }
        }

        private async Task<JsonNode> PollAsync(string url, JsonNode body, Func<string, bool> done, string what, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + PollTimeout;
            while (true)
            {
                string status = Status(body);
                if (done(status)) return body;
                if (status is "invalid" or "revoked" or "deactivated" or "expired")
                    throw new AcmeException($"{what} is {status}: {ProblemIn(body)}");
                if (DateTime.UtcNow > deadline) throw new AcmeException($"{what} stayed {status} for {PollTimeout.TotalSeconds:0} s");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                body = (await PostAsync(url, null, ct)).Json;
            }
        }

        private async Task LoadDirectoryAsync(CancellationToken ct)
        {
            if (_newOrderUrl != null) return;
            var directory = JsonNode.Parse(await _http.GetStringAsync(_directoryUrl, ct));
            _newNonceUrl = directory["newNonce"]?.GetValue<string>();
            _newAccountUrl = directory["newAccount"]?.GetValue<string>();
            _newOrderUrl = directory["newOrder"]?.GetValue<string>();
            _profiles = directory["meta"]?["profiles"] as JsonObject;
            if (_newNonceUrl == null || _newAccountUrl == null || _newOrderUrl == null)
                throw new AcmeException($"{_directoryUrl} is not an ACME directory");
        }

        private async Task EnsureAccountAsync(string email, CancellationToken ct)
        {
            if (_accountUrl != null) return;
            var account = new JsonObject { ["termsOfServiceAgreed"] = true };
            if (!string.IsNullOrEmpty(email)) account["contact"] = new JsonArray("mailto:" + email);
            // Registering a key that already has an account returns that account.
            _accountUrl = (await PostAsync(_newAccountUrl, account, ct, withJwk: true)).Location
                ?? throw new AcmeException("the ACME server did not say where the account is");
        }

        /// <summary>A JWS-signed POST, or a POST-as-GET when <paramref name="payload"/> is null.</summary>
        private async Task<Response> PostAsync(string url, JsonNode payload, CancellationToken ct, bool withJwk = false)
        {
            for (int attempt = 0; ; attempt++)
            {
                var header = new JsonObject { ["alg"] = "ES256", ["nonce"] = await NonceAsync(ct), ["url"] = url };
                if (withJwk)
                {
                    var q = _accountKey.ExportParameters(false).Q;
                    header["jwk"] = new JsonObject { ["crv"] = "P-256", ["kty"] = "EC", ["x"] = Base64Url(q.X), ["y"] = Base64Url(q.Y) };
                }
                else header["kid"] = _accountUrl;
                string protectedPart = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()));
                string payloadPart = payload == null ? "" : Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
                byte[] signature = _accountKey.SignData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                var body = new JsonObject { ["protected"] = protectedPart, ["payload"] = payloadPart, ["signature"] = Base64Url(signature) };

                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
                using var response = await _http.PostAsync(url, content, ct);
                if (response.Headers.TryGetValues("Replay-Nonce", out var nonces)) _nonce = nonces.FirstOrDefault();
                string text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                {
                    var location = response.Headers.Location;
                    return new Response { Text = text, Location = location == null ? null : new Uri(new Uri(url), location).ToString() };
                }
                // A nonce can be refused (expired, or used by a concurrent request); the refusal carries a fresh one.
                if (attempt < 3 && text.Contains("urn:ietf:params:acme:error:badNonce", StringComparison.Ordinal)) continue;
                throw new AcmeException($"{url} answered {(int)response.StatusCode}: {Detail(text)}");
            }
        }

        private async Task<string> NonceAsync(CancellationToken ct)
        {
            if (_nonce != null)
            {
                string nonce = _nonce;
                _nonce = null;
                return nonce;
            }
            using var request = new HttpRequestMessage(HttpMethod.Head, _newNonceUrl);
            using var response = await _http.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Replay-Nonce", out var values)) return values.First();
            throw new AcmeException("the ACME server sent no nonce");
        }

        private static string Status(JsonNode body) => body?["status"]?.GetValue<string>() ?? "";

        /// <summary>The first problem detail inside an order or authorization (its own, or a challenge's).</summary>
        private static string ProblemIn(JsonNode body)
        {
            if (body?["error"]?["detail"] is JsonNode detail) return detail.GetValue<string>();
            foreach (var challenge in body?["challenges"]?.AsArray() ?? new JsonArray())
                if (challenge?["error"]?["detail"] is JsonNode challengeDetail) return challengeDetail.GetValue<string>();
            return "no detail given";
        }

        private static string Detail(string text)
        {
            try { return JsonNode.Parse(text)?["detail"]?.GetValue<string>() ?? text; }
            catch (Exception) { return text; }
        }

        public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public void Dispose() => _http.Dispose();
    }
}
