using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// A player whose pawn is lost with its worker is told it is being placed again (NEB-355): the gateway sends
/// <see cref="JoinHoldReason.Recovering"/> before it despawns the pawn, so the client can tell a recovery from the
/// server removing its pawn, and a protocol-21 client, which cannot read the new reason, is told the world is starting
/// as before. A real gateway over loopback UDP with fake workers.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceRecoveryHoldTests
{
    private string _directory = "";

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-recovery-tests-" + Guid.NewGuid().ToString("N"));
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

    private static FakeClient Join(Fleet fleet, string name, ushort version = 0)
    {
        var client = fleet.Connect(0, name);
        if (version != 0) client.AnnounceVersion = version;
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined && client.Replicas.Count > 0), Is.True, $"{name} should get a pawn");
        return client;
    }

    [Test]
    public void APawnLostWithItsWorkerIsHeldAsARecoveryBeforeItIsDespawned()
    {
        using var fleet = new Fleet(1);
        var ann = Join(fleet, "ann");
        var old = Join(fleet, "old", version: 21);

        fleet.KillWorker(fleet.Worker);
        Assert.That(fleet.Run(() => ann.Join == JoinState.Starting && old.Join == JoinState.Starting && ann.Despawned.Count > 0), Is.True);
        Assert.That(ann.JoinReason, Is.EqualTo(JoinHoldReason.Recovering), "getting you back in, not starting the world");
        int held = ann.Wire.IndexOf("join Starting Recovering");
        int despawned = ann.Wire.FindIndex(held + 1, w => w.StartsWith("despawn "));
        Assert.That(held, Is.GreaterThanOrEqualTo(0));
        Assert.That(ann.Wire.FindIndex(w => w.StartsWith("despawn ")), Is.EqualTo(despawned), "the hold arrives before any despawn");
        Assert.That(old.JoinReason, Is.EqualTo(JoinHoldReason.WorldStarting), "a protocol-21 client is told what it can read");
        Assert.That(old.LastError, Is.Empty);
    }

    [Test]
    public void TheRecoveringHoldEndsWithTheNextJoin()
    {
        using var fleet = new Fleet(1);
        var ann = Join(fleet, "ann");
        fleet.KillWorker(fleet.Worker);
        Assert.That(fleet.Run(() => ann.JoinReason == JoinHoldReason.Recovering), Is.True);

        // A replacement worker takes the container, and the gateway places the player there.
        var replacement = fleet.StartWorker();
        fleet.Assign("c0", replacement.WorkerId);
        Assert.That(fleet.Run(() => ann.Join == JoinState.Joined, seconds: 15), Is.True);
        Assert.That(ann.JoinReason, Is.EqualTo(JoinHoldReason.None));

        // The flag was cleared by the join; a second loss is a recovery of its own.
        fleet.KillWorker(replacement);
        Assert.That(fleet.Run(() => ann.Join == JoinState.Starting), Is.True);
        Assert.That(ann.JoinReason, Is.EqualTo(JoinHoldReason.Recovering), "this pawn was lost with its worker too");
    }
}
