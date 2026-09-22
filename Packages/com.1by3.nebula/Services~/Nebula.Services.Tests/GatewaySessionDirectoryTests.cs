using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nebula;
using NUnit.Framework;

namespace Nebula.Services.Tests;

public class GatewaySessionDirectoryTests
{
    private sealed class Store : IGatewaySessionStore
    {
        private readonly Dictionary<string, string> _values = new();
        public bool FailWrites;
        public void ClearSessions() => _values.Clear();
        public string LoadSession(string identity) => _values.TryGetValue(identity, out var value) ? value : null;
        public void SaveSession(string identity, string value)
        {
            if (FailWrites) throw new InvalidOperationException("unavailable");
            if (value == null) _values.Remove(identity); else _values[identity] = value;
        }
    }

    [Test]
    public void ReleasedClaimsExpireFromMemoryAndStorage()
    {
        var now = DateTime.UtcNow;
        var store = new Store();
        var directory = new GatewaySessionDirectory(_ => true, () => now) { Store = store };
        var request = Claim("a", "first");
        directory.Handle(request);
        request.Operation = "reserve";
        request.Worker = "w1";
        directory.Handle(request);
        request.Operation = "release";
        directory.Handle(request);
        Assert.That(store.LoadSession("player"), Is.Not.Null);
        now = now.AddHours(25);
        directory.Handle(new() { Operation = "poll", Gateway = "a" });
        Assert.That(store.LoadSession("player"), Is.Null);
    }

    [Test]
    public void CoordinatorRestartCannotForgetAnExistingConnection()
    {
        var store = new Store();
        var before = new GatewaySessionDirectory(_ => true) { Store = store };
        var original = Claim("a", "original");
        before.Handle(original);
        var after = new GatewaySessionDirectory(_ => true) { Store = store };
        Assert.That(after.Handle(Claim("b", "replacement", 2)).Status, Is.EqualTo("pending"));
        Assert.That(after.Handle(new() { Operation = "poll", Gateway = "a" }).Revocations.Single().Claim, Is.EqualTo("original"));
    }

    [Test]
    public void StorageFailureDoesNotGrantAnUnrecordedSession()
    {
        var store = new Store { FailWrites = true };
        var directory = new GatewaySessionDirectory(_ => true) { Store = store };
        var first = Claim("a", "first");
        Assert.That(directory.Handle(first).Status, Is.EqualTo("error"));
        Assert.That(directory.Handle(first).Status, Is.EqualTo("error"));
        store.FailWrites = false;
        Assert.That(directory.Handle(first).Status, Is.EqualTo("granted"));
        store.FailWrites = true;
        Assert.That(directory.Handle(Claim("b", "next", 2)).Status, Is.EqualTo("error"));
        Assert.That(directory.Handle(new() { Operation = "poll", Gateway = "a" }).Revocations, Is.Empty);
    }

    private static GatewaySessionRequest Claim(string gateway, string claim, ulong session = 1) => new()
    {
        Operation = "claim", Identity = "player", Gateway = gateway, Claim = claim, SessionId = session
    };

    [Test]
    public void ReplacementWaitsForTheOldGatewayToAcknowledgeDisconnect()
    {
        var directory = new GatewaySessionDirectory(_ => true);
        var first = Claim("a", "first", ulong.MaxValue - 7);
        var granted = directory.Handle(first);
        var next = Claim("b", "next", 22);
        Assert.That(directory.Handle(next).Status, Is.EqualTo("pending"));
        Assert.That(directory.Handle(next).Status, Is.EqualTo("pending"), "a retry is not permission to take over");
        var revocations = directory.Handle(new() { Operation = "poll", Gateway = "a" });
        Assert.That(revocations.Revocations.Single().Claim, Is.EqualTo("first"));
        var release = GatewaySessionDirectory.Copy(first);
        release.Operation = "release";
        directory.Handle(release);
        var replacement = directory.Handle(next);
        Assert.That(replacement.Status, Is.EqualTo("granted"));
        Assert.That(replacement.Session.SessionId, Is.EqualTo(first.SessionId));
        Assert.That(replacement.Session.Generation, Is.GreaterThan(granted.Session.Generation));
        Assert.That(directory.Handle(next).Session.Generation, Is.EqualTo(replacement.Session.Generation), "idempotent retry");
    }

    [Test]
    public void AnUnreachableOldGatewayNeverTimesOutIntoPermission()
    {
        var now = DateTime.UtcNow;
        var directory = new GatewaySessionDirectory(_ => true, () => now);
        directory.Handle(Claim("a", "old"));
        var next = Claim("b", "new", 2);
        Assert.That(directory.Handle(next).Status, Is.EqualTo("pending"));
        now = now.AddDays(1);
        Assert.That(directory.Handle(next).Status, Is.EqualTo("pending"));
        Assert.That(directory.Handle(Claim("a", "old")).Status, Is.EqualTo("granted"));
    }

    [Test]
    public void ConcurrentGatewaysCannotBothGetAnAdmission()
    {
        var directory = new GatewaySessionDirectory(_ => true);
        var replies = new GatewaySessionReply[32];
        Parallel.For(0, replies.Length, i => replies[i] = directory.Handle(Claim("g" + i, "c" + i, (ulong)i + 1)));
        Assert.That(replies.Count(r => r.Status == "granted"), Is.EqualTo(1));
        Assert.That(replies.Count(r => r.Status == "pending"), Is.EqualTo(1));
    }

    [Test]
    public void PendingSpawnStaysOnItsReservedWorkerAcrossGatewayTakeover()
    {
        var directory = new GatewaySessionDirectory(_ => true);
        var first = Claim("a", "first");
        directory.Handle(first);
        var reserve = GatewaySessionDirectory.Copy(first);
        reserve.Operation = "reserve"; reserve.Worker = "w1"; reserve.Container = ContainerRef.Runtime(ulong.MaxValue);
        directory.Handle(reserve);
        var next = Claim("b", "next", 2);
        directory.Handle(next);
        var release = GatewaySessionDirectory.Copy(first); release.Operation = "release";
        directory.Handle(release);
        var secondReserve = GatewaySessionDirectory.Copy(next);
        secondReserve.Operation = "reserve"; secondReserve.Worker = "w2"; secondReserve.Container = new ContainerRef(2);
        var result = directory.Handle(secondReserve);
        Assert.That(result.Session.Worker, Is.EqualTo("w1"));
        Assert.That(result.Session.Container, Is.EqualTo(reserve.Container));
    }

    [Test]
    public void StaleAcknowledgmentCannotReleaseTheNewConnection()
    {
        var directory = new GatewaySessionDirectory(_ => true);
        var first = Claim("same-gateway", "first");
        directory.Handle(first);
        var next = Claim("same-gateway", "next", 2);
        directory.Handle(next);
        first.Operation = "release";
        directory.Handle(first);
        Assert.That(directory.Handle(first).Status, Is.EqualTo("stale"));
        Assert.That(directory.Handle(next).Status, Is.EqualTo("granted"));
    }

    [Test]
    public void CancelledPendingJoinLeavesTheOriginalConnectionAlone()
    {
        var directory = new GatewaySessionDirectory(_ => true);
        var first = Claim("a", "first"); directory.Handle(first);
        var next = Claim("b", "next", 2); directory.Handle(next);
        next.Operation = "cancel"; directory.Handle(next);
        Assert.That(directory.Handle(new() { Operation = "poll", Gateway = "a" }).Revocations, Is.Empty);
        Assert.That(directory.Handle(first).Status, Is.EqualTo("granted"));
    }

    [Test]
    public void ConfirmedDeadWorkerAllowsANewSpawnReservation()
    {
        bool alive = true;
        var directory = new GatewaySessionDirectory(_ => alive);
        var request = Claim("a", "first"); directory.Handle(request);
        request.Operation = "reserve"; request.Worker = "w1"; directory.Handle(request);
        alive = false;
        request.Worker = "w2";
        Assert.That(directory.Handle(request).Session.Worker, Is.EqualTo("w2"));
    }

    // --------------------------------------------------------------------------- NEB-229 / D7a: stale-owner eviction

    [Test]
    public void AClaimIsGrantedImmediatelyWhenTheOwningGatewayIsAlreadyStale()
    {
        bool gatewayAlive = false; // "a" has already stopped heartbeating before "b" ever asks
        var directory = new GatewaySessionDirectory(_ => true, gatewayAlive: _ => gatewayAlive);
        var first = Claim("a", "first"); directory.Handle(first);
        var second = Claim("b", "second", 2);
        var reply = directory.Handle(second);
        Assert.That(reply.Status, Is.EqualTo("granted"), "a claim on a gateway that is confirmed gone must not wait out the 15 s pending window");
        Assert.That(reply.Session!.Gateway, Is.EqualTo("b"));
    }

    [Test]
    public void APendingClaimIsGrantedAsSoonAsItsOwnerIsConfirmedStale()
    {
        bool gatewayAlive = true;
        var directory = new GatewaySessionDirectory(_ => true, gatewayAlive: _ => gatewayAlive);
        var first = Claim("a", "first"); directory.Handle(first);
        var second = Claim("b", "second", 2);
        Assert.That(directory.Handle(second).Status, Is.EqualTo("pending"), "the owner was still alive when the claim arrived");
        gatewayAlive = false; // "a" is killed while "b" is waiting
        var retry = directory.Handle(second);
        Assert.That(retry.Status, Is.EqualTo("granted"), "a retry must not wait for the 15 s pending window once the owner is confirmed gone");
        Assert.That(retry.Session!.Gateway, Is.EqualTo("b"));
    }

    [Test]
    public void AClaimStillWaitsWhenTheOwningGatewayIsMerelySlowNotGone()
    {
        var directory = new GatewaySessionDirectory(_ => true, gatewayAlive: _ => true);
        var first = Claim("a", "first"); directory.Handle(first);
        var second = Claim("b", "second", 2);
        Assert.That(directory.Handle(second).Status, Is.EqualTo("pending"), "a merely-slow owner must not be evicted; only a confirmed-gone one is");
    }

    [Test]
    public void ADifferentIncarnationUnderTheSameGatewayIdIsTreatedAsGone()
    {
        // The owner's key is "gw#1" (incarnation 1). By the time "b" claims, the control plane reports "gw" is
        // back but as incarnation 2 (a restarted process under the same id): the process that made the original
        // claim no longer exists, so its claim is evictable even though something named "gw" is heartbeating.
        var directory = new GatewaySessionDirectory(_ => true, gatewayAlive: key => key != "gw#1");
        var first = Claim("gw#1", "first"); directory.Handle(first);
        var second = Claim("b", "second", 2);
        Assert.That(directory.Handle(second).Status, Is.EqualTo("granted"));
    }

    [Test]
    public void ANullGatewayAlivePredicateNeverEvictsAnOwner()
    {
        // The pre-NEB-229 default: nobody wired a liveness check, so a claim on a live-looking owner waits exactly
        // as it always did. This is what keeps every caller that constructs the directory without the new
        // parameter compiling and behaving unchanged.
        var directory = new GatewaySessionDirectory(_ => true);
        var first = Claim("a", "first"); directory.Handle(first);
        var second = Claim("b", "second", 2);
        Assert.That(directory.Handle(second).Status, Is.EqualTo("pending"));
    }

    [Test]
    public void EnvelopePreservesEveryBitOfSessionAndContainerIds()
    {
        var request = Claim("gateway", "claim", ulong.MaxValue);
        request.Generation = ulong.MaxValue - 1;
        request.Container = ContainerRef.Runtime(ulong.MaxValue - 2);
        var copy = GatewaySessionWire.ReadRequest(GatewaySessionWire.Write(request));
        Assert.That(copy.SessionId, Is.EqualTo(request.SessionId));
        Assert.That(copy.Generation, Is.EqualTo(request.Generation));
        Assert.That(copy.Container, Is.EqualTo(request.Container));
    }
}
