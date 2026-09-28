using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Forwarded token claims (NEB-357): the gateway copies the claims <see cref="NebulaConfig.ForwardedClaims"/> names
/// from a <b>verified</b> sign-in token into the session it claims on a worker, and takes the player's name from
/// <see cref="NebulaConfig.NameClaim"/>. The unit half tests the selection, the limits and the wire codecs; the fleet
/// half runs real gateways (UDP, in-process control plane) against a fake worker and reads what the worker was told.
/// The worker side (the pawn's <c>OwnerClaims</c>, handover, ghosts) is <c>Tests/EditMode/ForwardedClaimsWorkerTests.cs</c>.
/// </summary>
[TestFixture]
public class ForwardedClaimsTests
{
    private const string Issuer = "https://issuer.example.com";
    private const string Audience = "my-game-client-id";
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-claims-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "c0", Index = 0, Size = new(64, 64, 64), transform = new ContainerFrame { position = new(32, 0, 0) } } },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown] public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static Dictionary<string, object> Parse(string json)
    {
        Assert.That(PersistenceJson.TryParseObject(json, out var payload, out var error), Is.True, error);
        return payload;
    }

    // ---------------------------------------------------------------------------------------- selection

    [Test]
    public void OnlyListedScalarClaimsAreSelectedAndFormattedAsStrings()
    {
        var payload = Parse("{\"iss\":\"x\",\"sub\":\"u1\",\"is_admin\":true,\"level\":42,\"ratio\":0.5,\"username\":\"alice\"," +
                            "\"email\":\"alice@example.com\",\"roles\":[\"admin\"],\"org\":{\"id\":1},\"nothing\":null}");
        var dropped = new List<string>();
        var claims = PlayerClaims.Select(payload, PlayerClaims.ParseNames("is_admin, level ratio;username,roles,org,nothing,missing"), dropped);

        Assert.That(claims["is_admin"], Is.EqualTo("true"));
        Assert.That(claims["level"], Is.EqualTo("42"));
        Assert.That(claims["ratio"], Is.EqualTo("0.5"));
        Assert.That(claims["username"], Is.EqualTo("alice"));
        Assert.That(claims.ContainsKey("email"), Is.False, "a claim nobody listed is never forwarded");
        Assert.That(claims.ContainsKey("sub"), Is.False);
        Assert.That(claims.ContainsKey("missing"), Is.False);
        Assert.That(claims.Count, Is.EqualTo(4), "arrays, objects and null are not forwarded");
        Assert.That(dropped, Is.EquivalentTo(new[] { "roles", "org", "nothing" }));
        Assert.That(claims["is_admin"], Is.EqualTo(JsonWebToken.Claim(payload, "is_admin")), "formatted the way JsonWebToken.Claim formats it");
    }

    [Test]
    public void TheLimitsCapTheNamesAndDropLongValues()
    {
        var names = PlayerClaims.ParseNames(string.Join(",", Enumerable.Range(0, 40).Select(i => "c" + i)) + ",c3,c3," + new string('n', PlayerClaims.MaxNameLength + 1));
        Assert.That(names.Count, Is.EqualTo(PlayerClaims.MaxCount), "at most 16 names");
        Assert.That(names, Is.Unique);

        var json = new StringBuilder("{");
        for (int i = 0; i < 40; i++) json.Append($"\"c{i}\":\"v{i}\",");
        json.Append("\"long\":\"").Append('x', PlayerClaims.MaxValueLength + 1).Append("\",\"edge\":\"").Append('y', PlayerClaims.MaxValueLength).Append("\"}");
        var payload = Parse(json.ToString());

        var claims = PlayerClaims.Select(payload, names);
        Assert.That(claims.Count, Is.EqualTo(PlayerClaims.MaxCount));
        Assert.That(claims.ContainsKey("c16"), Is.False);

        var dropped = new List<string>();
        var sized = PlayerClaims.Select(payload, new[] { "long", "edge" }, dropped);
        Assert.That(sized.ContainsKey("long"), Is.False, "a value over 256 characters is dropped, not cut short");
        Assert.That(sized["edge"].Length, Is.EqualTo(PlayerClaims.MaxValueLength));
        Assert.That(dropped, Is.EqualTo(new[] { "long" }));
    }

    [Test]
    public void NoPayloadOrNoNamesMeansNoClaims()
    {
        Assert.That(PlayerClaims.Select(null!, new[] { "a" }), Is.SameAs(PlayerClaims.Empty));
        Assert.That(PlayerClaims.Select(Parse("{\"a\":\"b\"}"), new string[0]), Is.SameAs(PlayerClaims.Empty));
        Assert.That(PlayerClaims.ParseNames(""), Is.Empty);
    }

    [Test]
    public void AnAnonymousTokenCarriesNoClaims()
    {
        var issuer = new AnonymousIdentityIssuer(AnonymousIdentityIssuer.DeriveKey("k"));
        var result = issuer.Verify(issuer.Issue(out _));
        Assert.That(result.Ok, Is.True);
        Assert.That(result.Claims, Is.Null, "the gateway minted it: there is nothing verified to forward");
    }

    // ---------------------------------------------------------------------------------------- wire

    private static readonly IReadOnlyDictionary<string, string> Sample = PlayerClaims.From(new Dictionary<string, string> { ["is_admin"] = "true", ["username"] = "alice" });

    [Test]
    public void SpawnPlayerCarriesTheClaims()
    {
        var w = new NetworkWriter();
        new SpawnPlayerMsg { ClientId = 7, Name = "alice", Identity = "id", Generation = 3, Claims = Sample }.Write(w);
        var r = new NetworkReader(w.ToArray());
        Assert.That((MsgId)r.ReadByte(), Is.EqualTo(MsgId.SpawnPlayer));
        var back = SpawnPlayerMsg.Read(r);
        Assert.That(back.Claims, Is.EquivalentTo(Sample));
        Assert.That(back.Generation, Is.EqualTo(3UL));

        w.Reset();
        new SpawnPlayerMsg { ClientId = 7, Name = "guest" }.Write(w);
        r = new NetworkReader(w.ToArray()); r.ReadByte();
        Assert.That(SpawnPlayerMsg.Read(r).Claims, Is.Empty);
    }

    [Test]
    public void AGhostSpawnCarriesTheOwnersClaimsAndAClientNeverSeesThem()
    {
        var spawn = new EntitySpawnMsg { NetId = 9, OwnerClientId = 7, Epoch = 2, Vars = Array.Empty<byte>(), State = Array.Empty<byte>(), OwnerIdentity = "id", OwnerClaims = Sample };
        var w = new NetworkWriter();
        spawn.Write(w, MsgId.GhostSpawn);
        var r = new NetworkReader(w.ToArray()); r.ReadByte();
        var back = EntitySpawnMsg.Read(r);
        Assert.That(back.OwnerClaims, Is.EquivalentTo(Sample), "a standalone spawn writes the sections before the claims so the claims can follow");
        Assert.That(back.Maps, Is.Null);
        Assert.That(back.Audience, Is.Null);

        Assert.That(spawn.ForClient().OwnerClaims, Is.Null);

        w.Reset();
        spawn.OwnerClaims = null;
        spawn.Write(w, MsgId.EntitySpawn);
        r = new NetworkReader(w.ToArray()); r.ReadByte();
        Assert.That(EntitySpawnMsg.Read(r).OwnerClaims, Is.Null);
    }

    [Test]
    public void AHandoverCarriesTheOwnersClaimsAndTheFieldsAfterThem()
    {
        var transfer = new AuthorityTransferMsg
        {
            Entity = new EntitySpawnMsg { NetId = 9, OwnerClientId = 7, Epoch = 2, Vars = Array.Empty<byte>(), State = Array.Empty<byte>(), OwnerIdentity = "id", OwnerClaims = Sample },
            NewEpoch = 3, SessionGeneration = 5, SessionGateway = "g#1", Crossing = true,
        };
        var w = new NetworkWriter();
        transfer.Write(w);
        var r = new NetworkReader(w.ToArray()); r.ReadByte();
        var back = AuthorityTransferMsg.Read(r);
        Assert.That(back.Entity.OwnerClaims, Is.EquivalentTo(Sample));
        Assert.That(back.NewEpoch, Is.EqualTo(3u));
        Assert.That(back.SessionGeneration, Is.EqualTo(5UL));
        Assert.That(back.SessionGateway, Is.EqualTo("g#1"));
        Assert.That(back.Crossing, Is.True);
    }

    [Test]
    public void TheDecoderRefusesASectionOverTheLimits()
    {
        var w = new NetworkWriter();
        w.WriteByte(PlayerClaims.MaxCount + 1);
        Assert.Throws<FormatException>(() => PlayerClaims.Read(new NetworkReader(w.ToArray())));

        w.Reset();
        w.WriteByte(1); w.WriteString("name"); w.WriteString(new string('x', PlayerClaims.MaxValueLength + 1));
        Assert.Throws<FormatException>(() => PlayerClaims.Read(new NetworkReader(w.ToArray())));

        var many = new Dictionary<string, string>();
        for (int i = 0; i < 30; i++) many["k" + i] = "v";
        many["long"] = new string('x', PlayerClaims.MaxValueLength + 1);
        w.Reset();
        PlayerClaims.Write(w, many);
        var back = PlayerClaims.Read(new NetworkReader(w.ToArray()));
        Assert.That(back.Count, Is.EqualTo(PlayerClaims.MaxCount), "the writer never sends more than the reader accepts");
        Assert.That(back.ContainsKey("long"), Is.False);
    }

    // ---------------------------------------------------------------------------------------- the gateway

    private static string B64(byte[] b) => JsonWebToken.Base64UrlEncode(b);

    private static string Jwks(RSA rsa, string kid)
    {
        var p = rsa.ExportParameters(false);
        return "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"" + kid + "\",\"n\":\"" + B64(p.Modulus!) + "\",\"e\":\"" + B64(p.Exponent!) + "\"}]}";
    }

    private static string Token(RSA rsa, string sub, string extra)
    {
        string payload = "{\"iss\":\"" + Issuer + "\",\"sub\":\"" + sub + "\",\"aud\":\"" + Audience + "\",\"exp\":" + (JsonWebToken.UnixNow() + 3600) +
                         ",\"iat\":" + (JsonWebToken.UnixNow() - 5) + (extra.Length > 0 ? "," + extra : "") + "}";
        string head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":\"k1\"}"));
        string body = B64(Encoding.UTF8.GetBytes(payload));
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(head + "." + body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return head + "." + body + "." + B64(sig);
    }

    private static Fleet SignInFleet(RSA rsa, int gateways = 1, bool singleSession = true, string nameClaim = "username")
    {
        var fleet = new Fleet(gateways, singleSession: singleSession, configure: c =>
        {
            c.AuthIssuers = Issuer;
            c.AuthAudience = Audience;
            c.ForwardedClaims = "is_admin, username, level";
            c.NameClaim = nameClaim;
        });
        foreach (var gateway in fleet.Gateways) gateway.TokenValidator!.AddKeys(Issuer, Jwks(rsa, "k1"));
        return fleet;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TheWorkerIsToldTheListedClaimsOfAVerifiedTokenAndItsNameClaim(bool singleSession)
    {
        using var rsa = RSA.Create(2048);
        using var fleet = SignInFleet(rsa, singleSession: singleSession);
        var alice = fleet.Connect(0, "hello-name", Token(rsa, "alice-sub", "\"is_admin\":true,\"username\":\"Alice\",\"level\":7,\"email\":\"a@example.com\""));
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count == 1), Is.True, alice.Rejected?.Reason);

        var claim = fleet.Worker.Claims[0];
        Assert.That(claim.Claims["is_admin"], Is.EqualTo("true"));
        Assert.That(claim.Claims["username"], Is.EqualTo("Alice"));
        Assert.That(claim.Claims["level"], Is.EqualTo("7"));
        Assert.That(claim.Claims.ContainsKey("email"), Is.False, "an unlisted claim stays at the gateway");
        Assert.That(claim.Claims.Count, Is.EqualTo(3));
        Assert.That(claim.Name, Is.EqualTo("Alice"), "the verified name claim, not the name the client asked for");
        Assert.That(claim.Identity, Is.EqualTo(PlayerIdentity.Derive(Issuer, "alice-sub")));
    }

    [Test]
    public void AnAnonymousPlayerHasNoClaimsAndKeepsItsOwnName()
    {
        using var rsa = RSA.Create(2048);
        using var fleet = SignInFleet(rsa);
        fleet.Connect(0, "guest-name");
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count == 1), Is.True);
        Assert.That(fleet.Worker.Claims[0].Claims, Is.Empty);
        Assert.That(fleet.Worker.Claims[0].Name, Is.EqualTo("guest-name"));
    }

    [Test]
    public void AVerifiedTokenWithoutTheNameClaimKeepsTheHelloName()
    {
        using var rsa = RSA.Create(2048);
        using var fleet = SignInFleet(rsa);
        fleet.Connect(0, "bob-hello", Token(rsa, "bob-sub", "\"is_admin\":false"));
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count == 1), Is.True);
        Assert.That(fleet.Worker.Claims[0].Name, Is.EqualTo("bob-hello"));
        Assert.That(fleet.Worker.Claims[0].Claims["is_admin"], Is.EqualTo("false"));
    }

    [Test]
    public void WithNoNameClaimTheHelloNameIsUsed()
    {
        using var rsa = RSA.Create(2048);
        using var fleet = SignInFleet(rsa, nameClaim: "");
        fleet.Connect(0, "hello-name", Token(rsa, "alice-sub", "\"username\":\"Alice\""));
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count == 1), Is.True);
        Assert.That(fleet.Worker.Claims[0].Name, Is.EqualTo("hello-name"));
        Assert.That(fleet.Worker.Claims[0].Claims["username"], Is.EqualTo("Alice"), "forwarding a claim does not make it the name");
    }

    [Test]
    public void AReconnectionThroughAnotherGatewayCarriesTheClaimsOfItsNewToken()
    {
        using var rsa = RSA.Create(2048);
        using var fleet = SignInFleet(rsa, gateways: 2);
        var first = fleet.Connect(0, "ann", Token(rsa, "ann-sub", "\"is_admin\":true,\"username\":\"Ann\""));
        Assert.That(fleet.Run(() => first.Join == JoinState.Joined), Is.True, first.Rejected?.Reason);
        var welcome = first.Welcome!.Value;
        Assert.That(fleet.Worker.Claims[0].Claims["is_admin"], Is.EqualTo("true"));

        first.Disconnect();
        Assert.That(fleet.Run(() => fleet.Worker.Despawns.Count == 1), Is.True);

        // The session token reclaims the session; the sign-in token is checked again, and its claims are the ones
        // that count now (an admin role taken away between the two connections is gone).
        var second = fleet.Connect(1, "ann", Token(rsa, "ann-sub", "\"is_admin\":false,\"username\":\"Ann\""), welcome.SessionToken);
        Assert.That(fleet.Run(() => second.Join == JoinState.Joined && fleet.Worker.Claims.Count == 2), Is.True, second.Rejected?.Reason);
        Assert.That(second.Welcome!.Value.ClientId, Is.EqualTo(welcome.ClientId), "the same session");
        var reclaim = fleet.Worker.Claims[1];
        Assert.That(reclaim.ClientId, Is.EqualTo(welcome.ClientId));
        Assert.That(reclaim.Claims["is_admin"], Is.EqualTo("false"));
        Assert.That(reclaim.Name, Is.EqualTo("Ann"));
    }

    [Test]
    public void TheGatewaysJoinEventCarriesTheClaims()
    {
        using var rsa = RSA.Create(2048);
        using var fleet = SignInFleet(rsa);
        var joined = new List<NebulaGateway.GatewayClientInfo>();
        fleet.Gateways[0].ClientJoined += joined.Add;
        fleet.Connect(0, "x", Token(rsa, "carol-sub", "\"username\":\"Carol\",\"level\":2"));
        Assert.That(fleet.Run(() => joined.Count == 1), Is.True);
        Assert.That(joined[0].Name, Is.EqualTo("Carol"));
        Assert.That(joined[0].Claims["level"], Is.EqualTo("2"));
    }
}
