using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nebula;
using NUnit.Framework;

namespace Nebula.Services.Tests;

/// <summary>Player identity: OpenID token checks, anonymous identities, and the gateway handshake that uses them.</summary>
public class AuthTests
{
    private const string Issuer = "https://issuer.example.com";
    private const string Audience = "my-game-client-id";
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-auth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
    }

    [TearDown] public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    /// <summary>An empty world: the gateway needs a manifest loaded before it accepts clients.</summary>
    private void LoadEmptyWorld()
    {
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest(), ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    // ---------------------------------------------------------------------------------------- helpers

    private static string B64(byte[] b) => JsonWebToken.Base64UrlEncode(b);

    private static string Jwks(RSA rsa, string kid)
    {
        var p = rsa.ExportParameters(false);
        return "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"" + kid + "\",\"n\":\"" + B64(p.Modulus!) + "\",\"e\":\"" + B64(p.Exponent!) + "\"}]}";
    }

    private static string Jwks(ECDsa ecdsa, string kid)
    {
        var p = ecdsa.ExportParameters(false);
        return "{\"keys\":[{\"kty\":\"EC\",\"crv\":\"P-256\",\"kid\":\"" + kid + "\",\"x\":\"" + B64(p.Q.X!) + "\",\"y\":\"" + B64(p.Q.Y!) + "\"}]}";
    }

    private static string SignRs256(RSA rsa, string kid, string payload)
    {
        string head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}"));
        string body = B64(Encoding.UTF8.GetBytes(payload));
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(head + "." + body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return head + "." + body + "." + B64(sig);
    }

    private static string SignEs256(ECDsa ecdsa, string kid, string payload)
    {
        string head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}"));
        string body = B64(Encoding.UTF8.GetBytes(payload));
        var sig = ecdsa.SignData(Encoding.ASCII.GetBytes(head + "." + body), HashAlgorithmName.SHA256);
        return head + "." + body + "." + B64(sig);
    }

    private static string Payload(string sub = "user-1", long? exp = null, string? aud = Audience, string iss = Issuer)
    {
        long e = exp ?? JsonWebToken.UnixNow() + 3600;
        return "{\"iss\":\"" + iss + "\",\"sub\":\"" + sub + "\"" + (aud != null ? ",\"aud\":\"" + aud + "\"" : "") + ",\"exp\":" + e + ",\"iat\":" + (JsonWebToken.UnixNow() - 5) + "}";
    }

    private static AuthResult ValidateNow(OidcTokenValidator v, string token)
    {
        AuthResult? result = null;
        v.Validate(token, r => result = r);
        var deadline = Stopwatch.StartNew();
        while (result == null && deadline.Elapsed.TotalSeconds < 5) { v.Tick(); Thread.Sleep(5); }
        Assert.That(result, Is.Not.Null, "the validator never answered");
        return result!.Value;
    }

    // ---------------------------------------------------------------------------------------- identity

    [Test]
    public void IdentityIsStableAndSeparatesIssuers()
    {
        string a = PlayerIdentity.Derive("https://a.example", "user-1");
        Assert.That(a, Has.Length.EqualTo(64));
        Assert.That(a, Is.EqualTo(PlayerIdentity.Derive("https://a.example", "user-1")));
        Assert.That(a, Is.Not.EqualTo(PlayerIdentity.Derive("https://b.example", "user-1")), "the same subject at another provider is another player");
        Assert.That(a, Is.Not.EqualTo(PlayerIdentity.Derive("https://a.example", "user-2")));
    }

    // ---------------------------------------------------------------------------------------- anonymous tokens

    [Test]
    public void AnonymousTokenRoundTripsAndRejectsTampering()
    {
        var key = AnonymousIdentityIssuer.DeriveKey("mesh-secret");
        var issuer = new AnonymousIdentityIssuer(key);
        string token = issuer.Issue(out string subject);
        Assert.That(AnonymousIdentityIssuer.IsAnonymousToken(token), Is.True);

        var ok = issuer.Verify(token);
        Assert.That(ok.Ok, Is.True, ok.Error);
        Assert.That(ok.Issuer, Is.EqualTo(PlayerIdentity.AnonymousIssuer));
        Assert.That(ok.Subject, Is.EqualTo(subject));
        Assert.That(ok.Identity, Is.EqualTo(PlayerIdentity.Derive(PlayerIdentity.AnonymousIssuer, subject)));
        Assert.That(new AnonymousIdentityIssuer(key).Verify(token).Identity, Is.EqualTo(ok.Identity), "any gateway with the same key agrees");

        // Another mesh's key.
        Assert.That(new AnonymousIdentityIssuer(AnonymousIdentityIssuer.DeriveKey("other")).Verify(token).Ok, Is.False);
        // A changed subject with the old signature.
        var parts = token.Split('.');
        string forgedBody = B64(Encoding.UTF8.GetBytes("{\"iss\":\"nebula\",\"sub\":\"somebody-else\"}"));
        Assert.That(issuer.Verify(parts[0] + "." + forgedBody + "." + parts[2]).Ok, Is.False);
        // Two issues are two players.
        issuer.Issue(out string second);
        Assert.That(second, Is.Not.EqualTo(subject));
    }

    [Test]
    public void KeyFileIsCreatedOnceAndReused()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nebula-auth-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "nebula-auth.key");
        try
        {
            var first = AnonymousIdentityIssuer.LoadOrCreateKeyFile(path);
            Assert.That(File.Exists(path));
            Assert.That(AnonymousIdentityIssuer.LoadOrCreateKeyFile(path), Is.EqualTo(first));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------------------------------- OpenID tokens

    [Test]
    public void Rs256TokenIsAcceptedWithTheProvidersKey()
    {
        using var rsa = RSA.Create(2048);
        using var v = new OidcTokenValidator(new[] { Issuer + "/" }, Audience, _ => throw new InvalidOperationException("no fetch expected"));
        v.AddKeys(Issuer, Jwks(rsa, "k1"));
        var r = ValidateNow(v, SignRs256(rsa, "k1", Payload()));
        Assert.That(r.Ok, Is.True, r.Error);
        Assert.That(r.Issuer, Is.EqualTo(Issuer));
        Assert.That(r.Subject, Is.EqualTo("user-1"));
        Assert.That(r.Identity, Is.EqualTo(PlayerIdentity.Derive(Issuer, "user-1")));
    }

    [Test]
    public void Es256TokenIsAccepted()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var v = new OidcTokenValidator(new[] { Issuer }, Audience);
        v.AddKeys(Issuer, Jwks(ecdsa, "e1"));
        var r = ValidateNow(v, SignEs256(ecdsa, "e1", Payload("ec-user")));
        Assert.That(r.Ok, Is.True, r.Error);
        Assert.That(r.Subject, Is.EqualTo("ec-user"));
    }

    [Test]
    public void BadTokensAreRejectedWithAReason()
    {
        using var rsa = RSA.Create(2048);
        using var other = RSA.Create(2048);
        using var v = new OidcTokenValidator(new[] { Issuer }, Audience);
        v.AddKeys(Issuer, Jwks(rsa, "k1"));

        Assert.That(ValidateNow(v, "not-a-token").Error, Does.Contain("JWT"));
        Assert.That(ValidateNow(v, SignRs256(rsa, "k1", Payload(exp: JsonWebToken.UnixNow() - 3600))).Error, Does.Contain("expired"));
        Assert.That(ValidateNow(v, SignRs256(rsa, "k1", Payload(aud: "another-app"))).Error, Does.Contain("audience"));
        Assert.That(ValidateNow(v, SignRs256(rsa, "k1", Payload(aud: null))).Error, Does.Contain("audience"));
        Assert.That(ValidateNow(v, SignRs256(rsa, "k1", Payload(iss: "https://evil.example"))).Error, Does.Contain("not trusted"));
        Assert.That(ValidateNow(v, SignRs256(other, "k1", Payload())).Error, Does.Contain("signature"));
        // alg=none style tricks: a token that claims HS256 with the issuer's public key as the secret.
        string head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"kid\":\"k1\"}"));
        string body = B64(Encoding.UTF8.GetBytes(Payload()));
        Assert.That(ValidateNow(v, head + "." + body + "." + B64(new byte[32])).Error, Does.Contain("unsupported"));
        // Skew tolerance: a token that expired 10 s ago is still fine.
        Assert.That(ValidateNow(v, SignRs256(rsa, "k1", Payload(exp: JsonWebToken.UnixNow() - 10))).Ok, Is.True);
    }

    [Test]
    public void AudienceMayBeAnArrayAndIsOptional()
    {
        using var rsa = RSA.Create(2048);
        using var strict = new OidcTokenValidator(new[] { Issuer }, Audience);
        strict.AddKeys(Issuer, Jwks(rsa, "k1"));
        string payload = "{\"iss\":\"" + Issuer + "\",\"sub\":\"u\",\"aud\":[\"other\",\"" + Audience + "\"],\"exp\":" + (JsonWebToken.UnixNow() + 60) + "}";
        Assert.That(ValidateNow(strict, SignRs256(rsa, "k1", payload)).Ok, Is.True);

        using var lax = new OidcTokenValidator(new[] { Issuer }, "");
        lax.AddKeys(Issuer, Jwks(rsa, "k1"));
        Assert.That(ValidateNow(lax, SignRs256(rsa, "k1", Payload(aud: "whatever"))).Ok, Is.True, "no configured audience = no audience check");
    }

    [Test]
    public void KeysAreFetchedThroughDiscoveryAndRefreshedForAnUnknownKid()
    {
        using var rsa1 = RSA.Create(2048);
        using var rsa2 = RSA.Create(2048);
        int discoveryHits = 0, jwksHits = 0;
        string current = Jwks(rsa1, "k1");
        using var v = new OidcTokenValidator(new[] { Issuer }, Audience, url =>
        {
            if (url == Issuer + "/.well-known/openid-configuration") { discoveryHits++; return "{\"issuer\":\"" + Issuer + "\",\"jwks_uri\":\"" + Issuer + "/keys\"}"; }
            if (url == Issuer + "/keys") { jwksHits++; return current; }
            throw new InvalidOperationException("unexpected url " + url);
        });
        v.MinRefreshInterval = TimeSpan.Zero;

        // First token: nothing cached, so the answer arrives after a fetch on a worker thread.
        bool answeredSynchronously = false;
        v.Validate(SignRs256(rsa1, "k1", Payload()), _ => answeredSynchronously = true);
        Assert.That(answeredSynchronously, Is.False);
        Assert.That(v.PendingCount, Is.EqualTo(1));
        var deadline = Stopwatch.StartNew();
        while (v.PendingCount > 0 && deadline.Elapsed.TotalSeconds < 5) { v.Tick(); Thread.Sleep(5); }
        Assert.That(v.PendingCount, Is.EqualTo(0));
        Assert.That((discoveryHits, jwksHits), Is.EqualTo((1, 1)));

        // Cached: no fetch.
        Assert.That(ValidateNow(v, SignRs256(rsa1, "k1", Payload("u2"))).Ok, Is.True);
        Assert.That(jwksHits, Is.EqualTo(1));

        // The provider rotated its key: the unknown kid triggers one refresh and the token then verifies.
        current = Jwks(rsa2, "k2");
        var rotated = ValidateNow(v, SignRs256(rsa2, "k2", Payload("u3")));
        Assert.That(rotated.Ok, Is.True, rotated.Error);
        Assert.That(jwksHits, Is.EqualTo(2));

        // A fetch failure fails the waiting token but keeps the cached keys for everyone else.
        current = "not json";
        Assert.That(ValidateNow(v, SignRs256(rsa1, "nope", Payload())).Ok, Is.False);
        Assert.That(ValidateNow(v, SignRs256(rsa2, "k2", Payload())).Ok, Is.True);
    }

    // ---------------------------------------------------------------------------------------- handshake

    [Test]
    public void GatewayIssuesAnonymousIdentitiesAndHonoursThemAgain()
    {
        LoadEmptyWorld();
        using var plane = new LocalControlPlane(); plane.Connect();
        var gateway = new NebulaGateway();
        using var reserve = new UdpClient(0);
        int port = ((IPEndPoint)reserve.Client.LocalEndPoint).Port; reserve.Close();
        try
        {
            gateway.Initialize(new NebulaConfig { GatewayPort = (ushort)port, AuthSigningKey = "test-key", WebClients = false }, plane);
            var first = Handshake(gateway, port, "");
            Assert.That(first.welcome, Is.Not.Null, first.rejected);
            Assert.That(first.welcome!.Value.Identity, Has.Length.EqualTo(64));
            Assert.That(first.welcome.Value.Token, Is.Not.Empty, "a client with no token gets one to keep");

            var again = Handshake(gateway, port, first.welcome.Value.Token);
            Assert.That(again.welcome, Is.Not.Null, again.rejected);
            Assert.That(again.welcome!.Value.Identity, Is.EqualTo(first.welcome.Value.Identity), "the kept token is the same player");
            Assert.That(again.welcome.Value.Token, Is.Empty, "and no new token is issued");
            Assert.That(again.welcome.Value.ClientId, Is.Not.EqualTo(first.welcome.Value.ClientId), "while the connection id is per session");

            var forged = Handshake(gateway, port, new AnonymousIdentityIssuer(AnonymousIdentityIssuer.DeriveKey("another-mesh")).Issue(out _));
            Assert.That(forged.welcome, Is.Null);
            Assert.That(forged.rejected, Does.Contain("signature"));

            var stranger = Handshake(gateway, port, "eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiJodHRwczovL3guZXhhbXBsZSJ9.AAAA");
            Assert.That(stranger.welcome, Is.Null);
            Assert.That(stranger.rejected, Does.Contain("does not accept sign-in tokens"));
        }
        finally { gateway.Dispose(); }
    }

    [Test]
    public void GatewayAcceptsAnOpenIdTokenAndCanRequireOne()
    {
        LoadEmptyWorld();
        using var plane = new LocalControlPlane(); plane.Connect();
        using var rsa = RSA.Create(2048);
        var gateway = new NebulaGateway();
        using var reserve = new UdpClient(0);
        int port = ((IPEndPoint)reserve.Client.LocalEndPoint).Port; reserve.Close();
        try
        {
            gateway.Initialize(new NebulaConfig { GatewayPort = (ushort)port, AuthIssuers = Issuer, AuthAudience = Audience, AuthAnonymous = false, WebClients = false }, plane);
            Assert.That(gateway.AnonymousIdentities, Is.Null);
            gateway.TokenValidator!.AddKeys(Issuer, Jwks(rsa, "k1"));

            var anonymous = Handshake(gateway, port, "");
            Assert.That(anonymous.welcome, Is.Null);
            Assert.That(anonymous.rejected, Does.Contain("requires signing in"));

            var signedIn = Handshake(gateway, port, SignRs256(rsa, "k1", Payload("alice")));
            Assert.That(signedIn.welcome, Is.Not.Null, signedIn.rejected);
            Assert.That(signedIn.welcome!.Value.Identity, Is.EqualTo(PlayerIdentity.Derive(Issuer, "alice")));
            Assert.That(signedIn.welcome.Value.Token, Is.Empty);

            var expired = Handshake(gateway, port, SignRs256(rsa, "k1", Payload("alice", exp: JsonWebToken.UnixNow() - 3600)));
            Assert.That(expired.rejected, Does.Contain("expired"));
        }
        finally { gateway.Dispose(); }
    }

    /// <summary>Connect a raw client, send Hello with <paramref name="token"/>, and collect the gateway's answer.</summary>
    private static (WelcomeMsg? welcome, string? rejected) Handshake(NebulaGateway gateway, int port, string token)
    {
        using var client = new LiteNetTransport("test-client");
        client.Connect("127.0.0.1", port);
        WelcomeMsg? welcome = null;
        string? rejected = null;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 5 && welcome == null && rejected == null)
        {
            gateway.Tick();
            client.Poll(e =>
            {
                if (e.Type == TransportEvent.Kind.Connected)
                {
                    var w = new NetworkWriter();
                    new HelloMsg { Role = PeerRole.Client, Id = "test-client", Token = token }.Write(w);
                    client.Send(e.PeerId, Delivery.ReliableOrdered, w.ToSegment());
                }
                if (e.Type == TransportEvent.Kind.Data)
                {
                    var r = new NetworkReader(e.Data);
                    var id = (MsgId)r.ReadByte();
                    if (id == MsgId.Welcome) welcome = WelcomeMsg.Read(r);
                    else if (id == MsgId.JoinRejected) rejected = JoinRejectedMsg.Read(r).Reason;
                }
            });
            Thread.Sleep(5);
        }
        // Let the gateway drop a rejected link before the next handshake reuses the port.
        for (int i = 0; i < 3; i++) { gateway.Tick(); client.Poll(_ => { }); Thread.Sleep(5); }
        return (welcome, rejected);
    }
}
