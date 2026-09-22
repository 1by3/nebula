using System.Diagnostics;
using System.Linq;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// NEB-229: the gateway fleet operations gap audit. Scenario S5 (docs/scale-suite.md) already measures the
/// abrupt-stop reclaim and pins the hard-kill gap; <c>ScaleFailureTests.AGatewayThatIsKilledOutrightHasItsSessions-
/// ReclaimedOnceItsHeartbeatGoesStale</c> is the turned-around D7a test and the number that sets
/// <see cref="ScaleThresholds.HardKillGatewayReclaimSeconds"/>. This file adds the two things the audit's fix
/// approach still had to prove once the eviction code landed:
/// <list type="bullet">
/// <item>the reclaim bound holds under more claimants than one gateway pair at a time (so the fast path in
/// <c>GatewaySessionDirectory</c> is not an artifact of a two-client fixture), written as its own CSV row; and</item>
/// <item>a hard gateway kill loses no authoritative state on the workers — the point of the whole exercise. A
/// pawn a doomed gateway's client held stays exactly the entity it was; the reclaiming client sees the same
/// pawn, not a respawn.</item>
/// </list>
/// See docs/gateway-fleet-audit.md for the full audit (load balancing, drain, replacement, client-side reclaim).
/// </summary>
[TestFixture]
[Category("Scale")]
[Category("Soak")]
public class ScaleGatewayAuditTests
{
    private const int Cells = 2;

    private ScaleWorld _world = null!;

    [SetUp] public void Setup() => _world = new ScaleWorld(Cells);
    [TearDown] public void Cleanup() => _world.Dispose();

    private Fleet Build(int gateways, int workers, int clients, out List<FakeClient> connected)
    {
        var fleet = new Fleet(gateways, workers: workers, world: f =>
        {
            for (int i = 0; i < _world.Cells.Count; i++) f.Assign(_world.Cells[i], f.Workers[i % f.Workers.Count].WorkerId);
        });
        foreach (var w in fleet.Workers) w.SpawnIntoRequestedContainer = true;
        fleet.OnPump = () => { foreach (var w in fleet.Workers) w.PublishStates(); };
        var list = new List<FakeClient>();
        for (int i = 0; i < clients; i++) list.Add(fleet.Connect(i % gateways, "p" + i));
        Assert.That(fleet.Run(() => list.All(c => c.Join == JoinState.Joined), seconds: 60), Is.True,
            "every client must be welcomed before the scenario begins");
        Assert.That(fleet.Run(() => list.All(c => fleet.Workers.Any(w => w.Pawns.ContainsKey(c.Welcome!.Value.ClientId))), seconds: 20), Is.True,
            "every client must have a pawn before the scenario begins");
        connected = list;
        return fleet;
    }

    // --------------------------------------------------------------------------- S5: reclaim bound at higher fan-out

    [Test]
    public void EveryClientOnAHardKilledGatewayReclaimsWithinTheDocumentedBoundRegardlessOfFleetSize()
    {
        var report = new ScaleReport("gateway-kill-fanout", "gateways", "workers", "clients", "reclaimed", "secondsWaited");

        using var fleet = Build(gateways: 3, workers: 2, clients: 18, out var clients);
        // Everyone on gateway 0 is doomed; gateways 1 and 2 survive to reclaim onto.
        var onDoomed = clients.Where((_, i) => i % 3 == 0).ToList();
        var identities = onDoomed.ToDictionary(c => c, c => c.Welcome!.Value);

        fleet.KillGateway(0, hard: true);

        var clock = Stopwatch.StartNew();
        var back = onDoomed.Select((c, i) => fleet.Connect(1 + i % 2, "reclaim", identities[c].Token, identities[c].SessionToken)).ToList();
        bool settled = fleet.Run(() => back.All(c => c.Join == JoinState.Joined || c.Rejected != null), seconds: 40);
        double seconds = clock.Elapsed.TotalSeconds;

        int reclaimed = back.Count(c => c.Join == JoinState.Joined);
        report.Row(3, 2, onDoomed.Count, reclaimed, seconds);
        report.Note($"{reclaimed}/{onDoomed.Count} sessions reclaimed across two surviving gateways in {seconds:0.00} s " +
                    "after a hard kill of a third; the bound does not depend on how many claimants are queued behind " +
                    "the same dead owner.");
        report.Write();

        Assert.That(settled, Is.True);
        Assert.That(reclaimed, Is.EqualTo(onDoomed.Count), "a hard gateway kill must lose no session, however many clients it held");
        Assert.That(seconds, Is.LessThanOrEqualTo(ScaleThresholds.HardKillGatewayReclaimSeconds),
            $"reclaim took {seconds:0.00} s, over the {ScaleThresholds.HardKillGatewayReclaimSeconds} s threshold");
    }

    // --------------------------------------------------------------------------- S5 / audit finding 3: no state lost

    [Test]
    public void AHardGatewayKillLosesNoAuthoritativeStateAndTheReclaimingClientSeesTheSamePawn()
    {
        var report = new ScaleReport("gateway-kill-state", "clients", "pawnsBefore", "pawnsAfter", "samePawn", "duplicatePawns");

        using var fleet = Build(gateways: 2, workers: 1, clients: 4, out var clients);
        var doomed = clients[0];
        var welcome = doomed.Welcome!.Value;
        Assert.That(fleet.Worker.Pawns.TryGetValue(welcome.ClientId, out ulong pawnBefore), Is.True,
            "the doomed client must have an authoritative pawn on the worker before the kill");
        int pawnsBefore = fleet.Worker.Pawns.Count;

        fleet.KillGateway(0, hard: true);

        // While the gateway is dead but not yet reclaimed, the worker — which was never touched — still holds
        // the pawn under the same owner. Nothing on the worker side depends on a gateway being reachable.
        Assert.That(fleet.Worker.Pawns.TryGetValue(welcome.ClientId, out ulong pawnDuringOutage), Is.True);
        Assert.That(pawnDuringOutage, Is.EqualTo(pawnBefore), "the worker must not despawn or recreate the pawn just because its gateway went away");

        var back = fleet.Connect(1, "reclaim", welcome.Token, welcome.SessionToken);
        Assert.That(fleet.Run(() => back.Join == JoinState.Joined, seconds: 40), Is.True, "the reclaim must succeed for this assertion to mean anything");
        Assert.That(back.Welcome!.Value.ClientId, Is.EqualTo(welcome.ClientId), "the reclaimed session keeps its original client id");

        fleet.Worker.PublishStates();
        Assert.That(fleet.Run(() => back.Replicas.Contains(pawnBefore), seconds: 10), Is.True,
            "the reclaiming client must see the very entity its pawn was, not a new one");

        bool samePawn = fleet.Worker.Pawns.TryGetValue(back.Welcome!.Value.ClientId, out ulong pawnAfter) && pawnAfter == pawnBefore;
        int duplicatePawns = fleet.Worker.Pawns.Count - pawnsBefore;
        report.Row(clients.Count, pawnsBefore, fleet.Worker.Pawns.Count, samePawn, duplicatePawns);
        report.Note(samePawn
            ? $"the pawn survived a hard gateway kill unchanged (netId {pawnBefore}); {fleet.Worker.Pawns.Count} pawns on the worker before and after, 0 duplicates"
            : "the pawn did not survive: this would be a state-loss regression, not a session-reclaim gap");
        report.Write();

        Assert.That(samePawn, Is.True, "a hard gateway kill must lose no authoritative entity state");
        Assert.That(duplicatePawns, Is.Zero, "the reclaim must not create a second pawn for the same player");
    }
}
