using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nebula.Tls;
using Nebula.WebRtc;
using NUnit.Framework;

namespace Nebula.Services.Tests;

public sealed class AcmeTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Base64Url(byte[] data) => AcmeClient.Base64Url(data);

    private static byte[] FromBase64Url(string text)
    {
        text = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
    }

    /// <summary>
    /// A small ACME server: one account, one order, HTTP-01 checked by fetching the key authorization from the client's
    /// challenge port, certificates signed by a throwaway CA. It verifies nonces, URLs and every JWS signature.
    /// </summary>
    private sealed class FakeAcme : IAsyncDisposable
    {
        private const string Token = "token-abc123";
        public readonly X509Certificate2 Ca;
        public readonly int Port = FreePort();
        public int ChallengePort;
        public int Orders;
        public string RequestedType, RequestedValue, RequestedProfile, Failure;
        private readonly ConcurrentDictionary<string, bool> _nonces = new();
        private readonly WebApplication _app;
        private ECDsa? _accountKey;
        private string? _thumbprint, _chainPem;
        private string _authorizationStatus = "pending", _orderStatus = "pending";

        public string Base => $"http://127.0.0.1:{Port}";

        public FakeAcme()
        {
            var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var caRequest = new CertificateRequest("CN=Nebula Test CA", caKey, HashAlgorithmName.SHA256);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            Ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            RequestedType = RequestedValue = RequestedProfile = Failure = null!;
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, Port));
            _app = builder.Build();
            _app.Run(Handle);
        }

        public Task StartAsync() => _app.StartAsync();

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();

        private async Task Handle(HttpContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string nonce = Guid.NewGuid().ToString("N");
            _nonces[nonce] = true;
            response.Headers["Replay-Nonce"] = nonce;
            string path = request.Path.Value ?? "";
            if (request.Method == "GET" && path == "/directory")
            {
                await Json(response, 200, new JsonObject
                {
                    ["newNonce"] = Base + "/nonce", ["newAccount"] = Base + "/account", ["newOrder"] = Base + "/order",
                    ["meta"] = new JsonObject { ["profiles"] = new JsonObject { ["classic"] = "90 days", ["shortlived"] = "160 hours" } },
                });
                return;
            }
            if (request.Method == "HEAD" && path == "/nonce") return;
            if (request.Method != "POST") { response.StatusCode = 404; return; }

            if (request.ContentType != "application/jose+json") { await Reject(response, "content type " + request.ContentType); return; }
            var jws = JsonNode.Parse(await new StreamReader(request.Body).ReadToEndAsync())!;
            string protectedPart = jws["protected"]!.GetValue<string>(), payloadPart = jws["payload"]!.GetValue<string>();
            var header = JsonNode.Parse(FromBase64Url(protectedPart))!;
            if (header["alg"]?.GetValue<string>() != "ES256") { await Reject(response, "alg"); return; }
            if (header["url"]?.GetValue<string>() != Base + path) { await Reject(response, "url " + header["url"]); return; }
            if (!_nonces.TryRemove(header["nonce"]!.GetValue<string>(), out _)) { await Reject(response, "nonce"); return; }
            ECDsa key;
            if (header["jwk"] is JsonNode jwk)
            {
                string x = jwk["x"]!.GetValue<string>(), y = jwk["y"]!.GetValue<string>();
                key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = FromBase64Url(x), Y = FromBase64Url(y) } });
                if (path != "/account") { await Reject(response, "jwk outside newAccount"); return; }
                _accountKey = key;
                _thumbprint = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes("{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}")));
            }
            else
            {
                if (_accountKey == null || header["kid"]?.GetValue<string>() != Base + "/acct/1") { await Reject(response, "kid"); return; }
                key = _accountKey;
            }
            if (!key.VerifyData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), FromBase64Url(jws["signature"]!.GetValue<string>()), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                await Reject(response, "signature");
                return;
            }
            var payload = payloadPart.Length == 0 ? null : JsonNode.Parse(FromBase64Url(payloadPart));

            switch (path)
            {
                case "/account":
                    response.Headers.Location = Base + "/acct/1";
                    await Json(response, 201, new JsonObject { ["status"] = "valid" });
                    return;
                case "/order":
                {
                    Orders++;
                    var identifier = payload!["identifiers"]![0]!;
                    RequestedType = identifier["type"]!.GetValue<string>();
                    RequestedValue = identifier["value"]!.GetValue<string>();
                    RequestedProfile = payload["profile"]?.GetValue<string>()!;
                    _authorizationStatus = "pending";
                    _orderStatus = "pending";
                    response.Headers.Location = Base + "/order/1";
                    await Json(response, 201, Order());
                    return;
                }
                case "/order/1":
                    await Json(response, 200, Order());
                    return;
                case "/authz/1":
                    await Json(response, 200, new JsonObject
                    {
                        ["status"] = _authorizationStatus,
                        ["identifier"] = new JsonObject { ["type"] = RequestedType, ["value"] = RequestedValue },
                        ["challenges"] = new JsonArray(Challenge()),
                    });
                    return;
                case "/chall/1":
                {
                    // Validate as a CA does: fetch the key authorization from the address being certified.
                    using var http = new HttpClient();
                    string answer = await http.GetStringAsync($"http://127.0.0.1:{ChallengePort}/.well-known/acme-challenge/{Token}");
                    if (answer == Token + "." + _thumbprint) { _authorizationStatus = "valid"; _orderStatus = "ready"; }
                    else { _authorizationStatus = "invalid"; Failure = "wrong key authorization: " + answer; }
                    await Json(response, 200, Challenge());
                    return;
                }
                case "/finalize/1":
                {
                    if (_orderStatus != "ready") { await Reject(response, "finalize before ready"); return; }
                    var csr = CertificateRequest.LoadSigningRequest(FromBase64Url(payload!["csr"]!.GetValue<string>()), HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
                    using var leaf = csr.Create(Ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(160), RandomNumberGenerator.GetBytes(8));
                    _chainPem = leaf.ExportCertificatePem() + "\n" + Ca.ExportCertificatePem() + "\n";
                    _orderStatus = "valid";
                    await Json(response, 200, Order());
                    return;
                }
                case "/cert/1":
                    response.ContentType = "application/pem-certificate-chain";
                    await response.WriteAsync(_chainPem!);
                    return;
            }
            response.StatusCode = 404;
        }

        private JsonObject Order() => new JsonObject
        {
            ["status"] = _orderStatus,
            ["identifiers"] = new JsonArray(new JsonObject { ["type"] = RequestedType, ["value"] = RequestedValue }),
            ["profile"] = RequestedProfile,
            ["authorizations"] = new JsonArray(Base + "/authz/1"),
            ["finalize"] = Base + "/finalize/1",
            ["certificate"] = _orderStatus == "valid" ? Base + "/cert/1" : null,
        };

        private JsonObject Challenge() => new JsonObject { ["type"] = "http-01", ["url"] = Base + "/chall/1", ["token"] = Token, ["status"] = _authorizationStatus };

        private async Task Reject(HttpResponse response, string why)
        {
            Failure ??= why;
            await Json(response, 400, new JsonObject { ["type"] = "urn:ietf:params:acme:error:malformed", ["detail"] = why });
        }

        private static async Task Json(HttpResponse response, int status, JsonNode body)
        {
            response.StatusCode = status;
            response.ContentType = "application/json";
            await response.WriteAsync(body.ToJsonString());
        }
    }

    private static bool TrustedByTestCa(X509Certificate2 certificate, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate) && certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Any(e => e.EnumerateIPAddresses().Contains(IPAddress.Loopback));
    }

    [Test, Timeout(60000)]
    public async Task TheGatewayGetsAnIpAddressCertificateOverAcmeAndServesHttpsWithIt()
    {
        await using var acme = new FakeAcme();
        await acme.StartAsync();
        string store = Path.Combine(Path.GetTempPath(), "nebula-tls-" + Guid.NewGuid().ToString("N"));
        try
        {
            acme.ChallengePort = FreePort();
            var certificates = new WebCertificates("127.0.0.1", acme.Base + "/directory", store, null, WebCertificates.DefaultProfile("127.0.0.1"), acme.ChallengePort);
            using (var rtc = new WebRtcServerTransport("test-web", "127.0.0.1"))
            {
                rtc.Listen(0);
                int webPort = FreePort();
                using var http = new GatewayHttpServer((ushort)webPort, rtc, null, certificates);
                http.Start();
                certificates.Start();
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (certificates.Current == null)
                {
                    Assert.That(acme.Failure, Is.Null, "the ACME server refused a request");
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "no certificate arrived");
                    await Task.Delay(100);
                }
                Assert.That(acme.RequestedType, Is.EqualTo("ip"));
                Assert.That(acme.RequestedValue, Is.EqualTo("127.0.0.1"));
                Assert.That(acme.RequestedProfile, Is.EqualTo("shortlived"));

                // Trust the test CA inside the TLS handshake's own chain build. Leaving it to a validation callback is not
                // enough on Windows: its chain engine can throw for a chain whose root is not in the machine's store
                // before the callback ever runs.
                var trust = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
                trust.CustomTrustStore.Add(acme.Ca);
                using var handler = new SocketsHttpHandler
                {
                    SslOptions =
                    {
                        CertificateChainPolicy = trust,
                        RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                            errors == System.Net.Security.SslPolicyErrors.None && certificate is X509Certificate2 leaf && TrustedByTestCa(leaf, acme.Ca),
                    },
                };
                using var client = new HttpClient(handler);
                var preflight = new HttpRequestMessage(HttpMethod.Options, $"https://127.0.0.1:{webPort}{GatewayHttpServer.SignalingPath}");
                Assert.That((await client.SendAsync(preflight)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            }
            Assert.That(File.Exists(Path.Combine(store, "certificate.pem")) && File.Exists(Path.Combine(store, "account-key.pem")));

            // A restart uses the stored certificate instead of ordering another one.
            using var restarted = new WebCertificates("127.0.0.1", acme.Base + "/directory", store, null, "shortlived", FreePort());
            restarted.Start();
            Assert.That(restarted.Current, Is.Not.Null);
            await Task.Delay(500);
            Assert.That(acme.Orders, Is.EqualTo(1));
        }
        finally
        {
            try { Directory.Delete(store, true); } catch (IOException) { }
        }
    }
}
