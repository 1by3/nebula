using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The fake-mesh leg of conformance scenario 2 (<c>docs/conformance-suite.md</c>): a client is routed into an
/// activated scope <b>by key</b>, and a client that asks for a scope nobody activated is held rather than dropped
/// into the public world. Tier A: the real <see cref="NebulaGateway"/> on a real socket over an in-process
/// <see cref="LocalControlPlane"/>, with a <see cref="FakeWorker"/> to be spawned into. The idempotency half of the
/// scenario is <c>Tests/EditMode/ConformanceScopeActivationTests.cs</c>, which needs no gateway.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceScopeRoutingTests
{
    private const string Key = "interior/raid-7";
    private const string Part = "interior";
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-scope-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "c0", Index = 0, Size = new(64, 64, 64), transform = new ContainerFrame { position = new(32, 0, 0) } } },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown]
    public void Cleanup()
    {
        ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static ScopeDefinition Room() => new()
    {
        Kind = ScopeKind.Parts,
        ObservePublic = false,
        Parts = { new ScopePart { PartId = Part, Center = new(500, 0, 0), Size = new(40, 40, 40) } },
    };

    private static ContainerRef ScopeContainer => ContainerRef.Runtime(ScopeKeys.Hash(Key + "/" + Part));

    [Test]
    public void AClientIsRoutedIntoTheScopeItNamesAndNoOtherClientIsPutThere()
    {
        using var fleet = new Fleet(gateways: 1);
        fleet.Plane.ActivateScope(new ScopeActivationRequest
        { ScopeKey = Key, Definition = Room(), PreferredWorkerId = fleet.Worker.WorkerId, Requester = "matchmaker" });
        Assert.That(fleet.Plane.IsScopeReady(Key), Is.True, "the worker that was asked owns the scope's container at once");

        var traveller = fleet.Connect(0, "traveller", scope: Key);
        var local = fleet.Connect(0, "local");

        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count >= 2), Is.True, "both clients were placed");
        var byName = fleet.Worker.Claims.ToDictionary(c => c.Name, c => c.Container);
        Assert.That(byName["traveller"], Is.EqualTo(ScopeContainer), "the key decided where the player went");
        Assert.That(byName["local"], Is.EqualTo(new ContainerRef(0)), "and a client that named no scope stays in the public world");
        Assert.That(traveller.Disconnected, Is.False);
        Assert.That(local.Disconnected, Is.False);
    }

    [Test]
    public void AClientAskingForAScopeNobodyActivatedIsHeldRatherThanPlacedInThePublicWorld()
    {
        using var fleet = new Fleet(gateways: 1);
        var lost = fleet.Connect(0, "lost", scope: "interior/never-activated");

        fleet.RunFor(1.0);

        Assert.That(fleet.Worker.Claims, Is.Empty, "the gateway never falls back to a container of another scope");
        Assert.That(lost.Disconnected, Is.False, "the join is held, not refused");
        Assert.That(lost.Join, Is.EqualTo(JoinState.Starting));
    }

    [Test]
    public void ActivatingTheScopeCompletesAHeldJoinWithNoReconnect()
    {
        using var fleet = new Fleet(gateways: 1);
        var traveller = fleet.Connect(0, "traveller", scope: Key);
        fleet.RunFor(0.5);
        Assert.That(fleet.Worker.Claims, Is.Empty);

        fleet.Plane.ActivateScope(new ScopeActivationRequest
        { ScopeKey = Key, Definition = Room(), PreferredWorkerId = fleet.Worker.WorkerId });

        // The gateway retries a held join every few seconds; the scope's container arrives through the lease rows.
        Assert.That(fleet.Run(() => fleet.Worker.Claims.Count > 0, seconds: 10), Is.True, "the held client was placed once the scope existed");
        Assert.That(fleet.Worker.Claims[0].Container, Is.EqualTo(ScopeContainer));
        Assert.That(traveller.Disconnected, Is.False, "and it never had to reconnect");
    }
}
