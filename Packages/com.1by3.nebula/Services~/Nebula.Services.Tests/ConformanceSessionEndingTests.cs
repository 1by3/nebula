using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Deliberate session endings at the gateway (NEB-354): a real <see cref="NebulaGateway"/> over loopback UDP, a fake
/// worker that records what it is told, and clients that speak protocol 22 or 21. A client's goodbye ends the session
/// at once and a lost link does not; a kick from the gateway or from a worker reaches the client with its code and
/// ends the session; a protocol-21 client is told of a kick with a refusal it understands; a gateway that shuts down
/// tells its clients first, and says "the server is shutting down" only when no other gateway is serving. The worker
/// half is the EditMode suite's <c>ConformanceSessionEndingTests</c>.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceSessionEndingTests
{
    private string _directory = "";

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-ending-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        var path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "c0", Index = 0, Size = new(64, 64, 64), transform = new ContainerFrame { position = new(32, 0, 0) } } },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private static FakeClient Join(Fleet fleet, string name, int gateway = 0, ushort version = 0, string token = "")
    {
        var client = fleet.Connect(gateway, name, token);
        if (version != 0) client.AnnounceVersion = version;
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined), Is.True, $"{name} should get a pawn");
        return client;
    }

    [Test]
    public void AGoodbyeEndsTheSessionAtOnceAndTheGatewayClosesTheLink()
    {
        using var fleet = new Fleet(1);
        var left = new List<ulong>();
        fleet.Gateways[0].ClientLeft += info => left.Add(info.ClientId);
        var ann = Join(fleet, "ann");
        ulong session = ann.Welcome!.Value.ClientId;

        ann.SendGoodbye();
        Assert.That(fleet.Run(() => fleet.Worker.Despawns.Count > 0 && ann.Disconnected), Is.True,
            "the worker is told and the gateway closes the link, which is the client's acknowledgement");
        var despawn = fleet.Worker.Despawns.Single();
        Assert.That(despawn.ClientId, Is.EqualTo(session));
        Assert.That(despawn.EndNow, Is.True, "a goodbye skips the reclaim grace");
        Assert.That(left, Is.EqualTo(new[] { session }), "ClientLeft is raised once");

        // The player comes straight back: a new session, admitted at once, not a reclaim of the ended one.
        var again = Join(fleet, "ann", token: ann.Welcome.Value.Token);
        Assert.That(again.Welcome!.Value.Identity, Is.EqualTo(ann.Welcome.Value.Identity));
        Assert.That(again.Welcome.Value.ClientId, Is.Not.EqualTo(session));
        Assert.That(again.Welcome.Value.Reclaimed, Is.False);
    }

    [Test]
    public void ALostLinkStillWaitsOutTheGrace()
    {
        using var fleet = new Fleet(1);
        var ann = Join(fleet, "ann");
        ann.Disconnect();
        Assert.That(fleet.Run(() => fleet.Worker.Despawns.Count > 0), Is.True);
        Assert.That(fleet.Worker.Despawns.Single().EndNow, Is.False, "only a goodbye or a kick ends the session at once");
    }

    [Test]
    public void AKickTellsTheClientWhyEndsTheSessionAndClosesTheLink()
    {
        using var fleet = new Fleet(1);
        var ann = Join(fleet, "ann");
        var bob = Join(fleet, "bob");
        ulong session = ann.Welcome!.Value.ClientId;

        Assert.That(fleet.Gateways[0].Kick(session, 7, "no griefing"), Is.True);
        Assert.That(fleet.Run(() => ann.Kicked != null && ann.Disconnected && fleet.Worker.Despawns.Count > 0), Is.True);
        Assert.That(ann.Kicked!.Value.Code, Is.EqualTo(7));
        Assert.That(ann.Kicked.Value.Reason, Is.EqualTo("no griefing"));
        Assert.That(ann.Rejected, Is.Null);
        var despawn = fleet.Worker.Despawns.Single();
        Assert.That((despawn.ClientId, despawn.EndNow), Is.EqualTo((session, true)));
        Assert.That(bob.Kicked, Is.Null);
        Assert.That(bob.Disconnected, Is.False, "nobody else is touched");
        Assert.That(fleet.Gateways[0].Kick(session, 7, "again"), Is.False, "a session this gateway no longer holds");
        Assert.That(fleet.Gateways[0].Kick(12345, 0, ""), Is.False);
    }

    [Test]
    public void AProtocol21ClientIsToldOfAKickWithARefusalItUnderstands()
    {
        using var fleet = new Fleet(1);
        var old = Join(fleet, "old", version: 21);
        Assert.That(old.Welcome!.Value.NegotiatedVersion, Is.EqualTo((ushort)21));

        fleet.Gateways[0].Kick(old.Welcome.Value.ClientId, 3, "removed by a moderator");
        Assert.That(fleet.Run(() => old.Rejected != null && old.Disconnected), Is.True);
        Assert.That(old.Kicked, Is.Null, "a protocol-21 client cannot read Kicked, so it is never sent one");
        Assert.That(old.Rejected!.Value.Code, Is.EqualTo(JoinRejectReason.Denied), "a refusal it does not retry");
        Assert.That(old.Rejected.Value.Retry, Is.False);
        Assert.That(old.Rejected.Value.Reason, Is.EqualTo("removed by a moderator"));
        Assert.That(old.LastError, Is.Empty);
    }

    [Test]
    public void AWorkersKickReachesTheClientUnlessTheSessionMovedOn()
    {
        using var fleet = new Fleet(1);
        var ann = Join(fleet, "ann");
        var bob = Join(fleet, "bob");
        var annClaim = fleet.Worker.Claims.Last(c => c.ClientId == ann.Welcome!.Value.ClientId);
        var bobClaim = fleet.Worker.Claims.Last(c => c.ClientId == bob.Welcome!.Value.ClientId);

        // A kick naming an older generation than the gateway's is about a connection that has since been replaced.
        fleet.Worker.Kick("gw1", bobClaim.ClientId, bobClaim.Generation - 1, 1, "stale");
        fleet.Worker.Kick("gw1", annClaim.ClientId, annClaim.Generation, 9, "idle for too long");
        Assert.That(fleet.Run(() => ann.Kicked != null && ann.Disconnected), Is.True);
        Assert.That(ann.Kicked!.Value.Code, Is.EqualTo(9));
        Assert.That(ann.Kicked.Value.Reason, Is.EqualTo("idle for too long"));
        fleet.RunFor(0.3);
        Assert.That(fleet.Worker.Despawns, Is.Empty, "the worker that asked has ended the session itself and is not told again");
        Assert.That(bob.Kicked, Is.Null, "the stale kick is ignored");
        Assert.That(bob.Disconnected, Is.False);
    }

    [Test]
    public void AGatewayThatShutsDownAloneTellsItsClientsTheServerIsGoing()
    {
        using var fleet = new Fleet(1, configure: c => c.GatewayShutdownDrainSeconds = 0.3f);
        var ann = Join(fleet, "ann");
        var old = Join(fleet, "old", version: 21);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        fleet.KillGateway(0, hard: false);
        Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(2), "the wait is bounded by GatewayShutdownDrainSeconds");
        Assert.That(fleet.Run(() => ann.DrainWithin >= 0 && old.DrainWithin >= 0), Is.True, "every client was told before the gateway went");
        Assert.That(ann.ServerShutdown, Is.True, "no other gateway is serving: the server is going away");
        Assert.That(old.ServerShutdown, Is.False, "a protocol-21 client gets the plain drain notice it can read");
        Assert.That(old.LastError, Is.Empty);
        Assert.That(ann.DrainWithin, Is.EqualTo(7));
    }

    [Test]
    public void AGatewayThatShutsDownWithAnotherServingSaysItIsDraining()
    {
        using var fleet = new Fleet(2, configure: c => c.GatewayShutdownDrainSeconds = 0.3f);
        var ann = Join(fleet, "ann");
        Assert.That(fleet.Run(() => fleet.Plane.FindGateway("gw2") is { } g && g.Stats.Ready), Is.True);

        fleet.KillGateway(0, hard: false);
        Assert.That(fleet.Run(() => ann.DrainWithin >= 0), Is.True);
        Assert.That(ann.ServerShutdown, Is.False, "another gateway can take the client: a plain drain");
        Assert.That(ann.Kicked, Is.Null);
    }

    [Test]
    public void AGatewayWithTheShutdownDrainOffStopsWithoutTellingAnyone()
    {
        using var fleet = new Fleet(1);
        var ann = Join(fleet, "ann");
        fleet.KillGateway(0, hard: false);
        fleet.RunFor(0.3);
        Assert.That(ann.DrainWithin, Is.EqualTo(-1), "GatewayShutdownDrainSeconds 0 is the old abrupt stop");
    }
}
