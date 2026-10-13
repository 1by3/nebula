using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Conformance scenario 55 (<c>docs/portals.md</c> D20 to D24): <b>observation through an open portal, both ways, decided from link records alone.</b>
/// A client that may look through a link, whose pawn stands inside the window of its own side, is sent the other side's window: the container rows inside
/// it, the carriers of those rows, and every entity standing in it, and nothing outside it; the reverse from the room; nothing at all for a client that
/// may not look; windows do not chain (one portal deep) and a client looks through at most <see cref="NebulaGateway.MaxPortalWindows"/> at once, nearest
/// first; closing the link takes the far side away at once and a reconnect inside sees out again. Tier A: the real <see cref="NebulaGateway"/> on a real
/// socket over an in-process <see cref="LocalControlPlane"/>, with a <see cref="FakeWorker"/> that publishes the portal as <c>NebulaWorker.UpdatePortals</c>
/// does (<see cref="PortalWindowRig"/>); the worker's side of a crossing is <c>Tests/EditMode/PortalCrossingTests.cs</c>.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformancePortalWindowTests
{
    private const ulong Crate = PortalWindowRig.Crate, Rubble = PortalWindowRig.Rubble, Rock = PortalWindowRig.Rock, Pebble = PortalWindowRig.Pebble;

    [Test]
    public void AClientInTheNearWindowIsSentTheFarWindowsRowsAndEntitiesAndNothingOutsideIt()
    {
        using var rig = new PortalWindowRig();
        Assert.That(rig.Thrower.Containers, Does.Not.Contain(PortalWindowRig.RoomId()), "before the door opens it knows nothing of the room");
        rig.OpenDoor(rig.Thrower, rig.Inside);

        Assert.That(rig.Fleet.Run(() => rig.Thrower.Replicas.Contains(Crate), seconds: 10), Is.True, "the crate in the room's window is sent through the open door");
        Assert.That(rig.Thrower.Containers, Does.Contain(PortalWindowRig.RoomId()), "with the room's row");
        Assert.That(rig.Thrower.Wire.IndexOf("container+ " + PortalWindowRig.RoomId()), Is.LessThan(rig.Thrower.Wire.IndexOf("spawn " + Crate)),
            "ahead of the spawn that names it (D20)");
        Assert.That(rig.Thrower.ContainerOf[Crate], Is.EqualTo(PortalWindowRig.RoomRef()));
        rig.Fleet.RunFor(1.0);
        Assert.That(rig.Thrower.Replicas, Does.Not.Contain(Rubble), "the rubble beside the window is in the room, but not in the window");
        Assert.That(rig.Thrower.HeardAbout, Does.Not.Contain(Rubble));
        Assert.That(rig.Thrower.Replicas, Does.Contain(rig.InsidePawn), "the pawn of the player standing in the far window is inside it, and a pawn is an entity like another");
        Assert.That(rig.Thrower.Replicas, Does.Contain(Rock).And.Contain(Pebble), "while its own world is untouched");
        Assert.That(rig.Thrower.UnresolvableNames, Is.Empty);
        Assert.That(rig.Thrower.LastError, Is.Empty);
        Assert.That(rig.Fleet.Gateways[0].PortalWindowsOf(rig.IdOf(rig.Thrower)), Is.EqualTo(1));
    }

    [Test]
    public void TheReverseFromInsideTheRoomSendsTheStreetsWindow()
    {
        using var rig = new PortalWindowRig();
        Assert.That(rig.Inside.Containers, Does.Not.Contain("c0"));
        rig.OpenDoor(rig.Thrower, rig.Inside);

        Assert.That(rig.Fleet.Run(() => rig.Inside.Replicas.Contains(Rock), seconds: 10), Is.True, "the rock in the street's window is sent into the room");
        Assert.That(rig.Inside.Containers, Does.Contain("c0"), "with the street's row");
        Assert.That(rig.Inside.Wire.IndexOf("container+ c0"), Is.LessThan(rig.Inside.Wire.IndexOf("spawn " + Rock)));
        rig.Fleet.RunFor(1.0);
        Assert.That(rig.Inside.Replicas, Does.Not.Contain(Pebble), "and not the pebble beyond the window's width");
        Assert.That(rig.Inside.HeardAbout, Does.Not.Contain(Pebble));
        Assert.That(rig.Inside.Replicas, Does.Contain(Crate).And.Contain(Rubble), "everything in the room is the room's own");
        Assert.That(rig.Inside.Replicas, Does.Contain(rig.ThrowerPawn), "the street's window holds the thrower's pawn too");
        Assert.That(rig.Inside.UnresolvableNames, Is.Empty);
    }

    [Test]
    public void AClientThatMayNotLookThroughReceivesNothingThroughAndNeitherDoesOneOutsideTheWindow()
    {
        using var rig = new PortalWindowRig();
        rig.OpenDoor(rig.Thrower, rig.Inside, rig.Distant);   // the stranger is not on the list; the distant one is, but stands outside the window
        Assert.That(rig.Fleet.Run(() => rig.Thrower.Replicas.Contains(Crate), seconds: 10), Is.True);
        rig.Fleet.RunFor(1.0);

        Assert.That(rig.Stranger.Replicas, Does.Not.Contain(Crate), "not an observer: nothing through the door");
        Assert.That(rig.Stranger.HeardAbout, Does.Not.Contain(Crate));
        Assert.That(rig.Stranger.Containers, Does.Not.Contain(PortalWindowRig.RoomId()), "and it is never told the room exists");
        Assert.That(rig.Stranger.Wire.Any(w => w.Contains(PortalWindowRig.RoomId())), Is.False);
        Assert.That(rig.Stranger.Replicas, Does.Contain(Rock), "its own street is as it was");
        Assert.That(rig.Distant.Replicas, Does.Not.Contain(Crate), "an observer whose pawn is outside the near window sees nothing through");
        Assert.That(rig.Distant.Containers, Does.Not.Contain(PortalWindowRig.RoomId()));
        Assert.That(rig.Fleet.Gateways[0].PortalWindowsOf(rig.IdOf(rig.Stranger)), Is.EqualTo(0));
        Assert.That(rig.Fleet.Gateways[0].PortalWindowsOf(rig.IdOf(rig.Distant)), Is.EqualTo(0));

        // Anyone: a door every passer-by may look through.
        var door = PortalWindowRig.Door();
        var link = PortalWindowRig.Link(door, PortalWindowRig.Room, PortalState.Open, Array.Empty<ulong>());
        link.Flags = PortalLinkFlags.ObserverAnyone;
        rig.Worker.PublishPortalLinks(new[] { link });
        Assert.That(rig.Fleet.Run(() => rig.Stranger.Replicas.Contains(Crate), seconds: 10), Is.True, "an Anyone door is open to the stranger standing in its window");
        Assert.That(rig.Distant.Replicas, Does.Not.Contain(Crate), "but not to the one outside it");
        Assert.That(rig.Thrower.Replicas, Does.Contain(Crate), "and the list's own observers still see through it");
    }

    [Test]
    public void AClientSeesThroughOnlyWhileItsPawnIsInsideTheWindow()
    {
        using var rig = new PortalWindowRig();
        rig.OpenDoor(rig.Thrower, rig.Inside, rig.Distant);
        var door = PortalWindowRig.Door();
        Assert.That(rig.Fleet.Run(() => rig.Thrower.Replicas.Contains(Crate), seconds: 10), Is.True);
        Assert.That(rig.Distant.Replicas, Does.Not.Contain(Crate));

        rig.Worker.Move(rig.DistantPawn, PortalWindowRig.InStreet(door, 3, 0, -5));   // walks into the window
        Assert.That(rig.Fleet.Run(() => rig.Distant.Replicas.Contains(Crate), seconds: 10), Is.True, "and now it sees into the room");

        rig.Worker.Move(rig.DistantPawn, PortalWindowRig.InStreet(door, 15, 0, -5));  // and out of it again
        Assert.That(rig.Fleet.Run(() => !rig.Distant.Replicas.Contains(Crate), seconds: 10), Is.True, "and loses it when it leaves the window");
        Assert.That(rig.Fleet.Run(() => !rig.Distant.Containers.Contains(PortalWindowRig.RoomId()), seconds: 10), Is.True, "the row goes after its linger");
    }

    [Test]
    public void WindowsDoNotChainAClientIsOnlySentTheScopeOnTheOtherSideOfTheLinksItStandsInTheWindowOf()
    {
        using var rig = new PortalWindowRig();
        // A second door, in the room, to a third scope: its near face on the room's floor beside the inside client.
        var inner = PortalWindowRig.Door("inner", nearOrigin: new Double3(485, 0, 8), farOrigin: new Double3(880, 0, 0), scope: PortalWindowRig.Inner);
        inner.NearScope = PortalWindowRig.ScopeOf(PortalWindowRig.Room);
        inner.NearContainerId = PortalWindowRig.RoomId();
        inner.NearContainer = PortalWindowRig.RoomRef();
        inner.PortalId = PortalIds.Of(inner.NearContainerId, "inner");
        rig.Worker.Spawn(PortalWindowRig.InnerCrate, PortalWindowRig.InRoom(inner, 1, 1, 6, PortalWindowRig.Inner), PortalWindowRig.RoomRef(PortalWindowRig.Inner));
        var outer = rig.OpenDoor(rig.Thrower, rig.Inside);
        rig.PublishLink(inner, PortalWindowRig.Inner, PortalState.Open, new[] { rig.Thrower, rig.Inside });

        Assert.That(rig.Fleet.Run(() => rig.Inside.Replicas.Contains(PortalWindowRig.InnerCrate) && rig.Inside.Replicas.Contains(Rock), seconds: 10), Is.True,
            "from the room the street is one portal away and so is the inner room");
        Assert.That(rig.Thrower.Replicas.Contains(Crate) || rig.Fleet.Run(() => rig.Thrower.Replicas.Contains(Crate), seconds: 10), Is.True, "from the street the room is one portal away");
        rig.Fleet.RunFor(1.5);
        Assert.That(rig.Thrower.Replicas, Does.Not.Contain(PortalWindowRig.InnerCrate), "the inner room is two portals from the street, and an observer of both links");
        Assert.That(rig.Thrower.HeardAbout, Does.Not.Contain(PortalWindowRig.InnerCrate));
        Assert.That(rig.Thrower.Containers, Does.Not.Contain(PortalWindowRig.RoomId(PortalWindowRig.Inner)), "its row is never sent");
        Assert.That(rig.Fleet.Gateways[0].PortalWindowsOf(rig.IdOf(rig.Thrower)), Is.EqualTo(1));
        Assert.That(rig.Fleet.Gateways[0].PortalWindowsOf(rig.IdOf(rig.Inside)), Is.EqualTo(2), "while the player in the room looks both ways at once");
        Assert.That(outer.State, Is.EqualTo(PortalState.Open));
    }

    [Test]
    public void ClosingTheLinkTakesTheFarSideAwayAtOnceFromBothWays()
    {
        using var rig = new PortalWindowRig();
        var link = rig.OpenDoor(rig.Thrower, rig.Inside);
        Assert.That(rig.Fleet.Run(() => rig.Thrower.Replicas.Contains(Crate) && rig.Inside.Replicas.Contains(Rock), seconds: 10), Is.True);

        // A closing link still lets both sides look: only the band must empty first.
        link.State = PortalState.Closing;
        link.Wait = PortalWait.BandOccupied;
        rig.Worker.PublishPortalLinks(new[] { link });
        rig.Fleet.RunFor(1.0);
        Assert.That(rig.Thrower.Replicas, Does.Contain(Crate));
        Assert.That(rig.Inside.Replicas, Does.Contain(Rock));

        link.State = PortalState.Closed;
        link.Wait = PortalWait.None;
        rig.Worker.PublishPortalLinks(new[] { link });
        Assert.That(rig.Fleet.Run(() => !rig.Thrower.Replicas.Contains(Crate) && !rig.Inside.Replicas.Contains(Rock), seconds: 5), Is.True, "closing revokes in both directions");
        Assert.That(rig.Thrower.Despawned, Does.Contain(Crate));
        Assert.That(rig.Fleet.Run(() => !rig.Thrower.Containers.Contains(PortalWindowRig.RoomId()) && !rig.Inside.Containers.Contains("c0"), seconds: 5), Is.True, "and the rows follow after their linger");
        Assert.That(rig.Thrower.Replicas, Does.Contain(Rock), "the street is the street's own");
        Assert.That(rig.Inside.Replicas, Does.Contain(Crate));
        Assert.That(rig.Fleet.Gateways[0].PortalWindowsOf(rig.IdOf(rig.Thrower)), Is.EqualTo(0));

        // And it can open again.
        link.State = PortalState.Open;
        link.Generation = 2;
        rig.Worker.PublishPortalLinks(new[] { link });
        Assert.That(rig.Fleet.Run(() => rig.Thrower.Replicas.Contains(Crate) && rig.Inside.Replicas.Contains(Rock), seconds: 10), Is.True);
    }

    [Test]
    public void AClientReconnectingInsideTheRoomSeesOutAgain()
    {
        using var rig = new PortalWindowRig();
        rig.OpenDoor(rig.Thrower, rig.Inside);
        Assert.That(rig.Fleet.Run(() => rig.Inside.Replicas.Contains(Rock), seconds: 10), Is.True);
        string token = rig.Inside.Welcome!.Value.Token, session = rig.Inside.Welcome.Value.SessionToken;
        ulong id = rig.IdOf(rig.Inside);

        // The worker still holds the pawn (the reclaim grace) and re-announces it to whichever gateway claims it.
        rig.Worker.AdoptPawn(id, rig.InsidePawn);
        rig.Worker.DeferSpawns = false;
        rig.Inside.Disconnect();
        rig.Fleet.RunFor(0.5);
        var back = rig.Fleet.Connect(0, "inside", token: token, session: session, scope: PortalWindowRig.Room);

        Assert.That(rig.Fleet.Run(() => back.Replicas.Contains(rig.InsidePawn) && back.Replicas.Contains(Rock), seconds: 10), Is.True,
            "the link records live on the gateway, so no history of where the client came from is needed to see out");
        Assert.That(back.Containers, Does.Contain("c0"));
        Assert.That(back.Replicas, Does.Not.Contain(Pebble));
        Assert.That(back.UnresolvableNames, Is.Empty);
    }

    [Test]
    public void AClientLooksThroughAtMostMaxPortalWindowsAtOnceNearestFirst()
    {
        const int Doors = 6;
        var keys = Enumerable.Range(0, Doors).Select(i => $"interior/window-many-{i}").ToArray();
        using var rig = new PortalWindowRig(keys);
        var policy = new WindowPolicy();
        rig.Fleet.Gateways[0].InterestPolicy = policy;
        var doors = new List<PortalRecord>();
        var links = new List<PortalLinkRecord>();
        for (int i = 0; i < Doors; i++)
        {
            // All six doors' windows hold the thrower's pawn; door i is i metres farther from it than door 0. Each leads to a room of its own, with a crate in it.
            var door = PortalWindowRig.Door("many-" + i, nearOrigin: new Double3(10, 0, 10 + i), farOrigin: new Double3(PortalWindowRig.CenterOf(keys[i]).x - 20, 0, 0), scope: keys[i]);
            doors.Add(door);
            links.Add(PortalWindowRig.Link(door, keys[i], PortalState.Open, new[] { rig.IdOf(rig.Thrower) }));
            rig.Worker.Spawn(8100 + (ulong)i, PortalWindowRig.InRoom(door, 0, 1, 4, keys[i]), PortalWindowRig.RoomRef(keys[i]));
        }
        rig.Worker.PublishPortals(doors);
        rig.Worker.PublishPortalLinks(links);

        int limit = NebulaGateway.MaxPortalWindows;
        Assert.That(limit, Is.EqualTo(4));
        Assert.That(rig.Fleet.Run(() => Enumerable.Range(0, limit).All(i => rig.Thrower.Replicas.Contains(8100 + (ulong)i)), seconds: 15), Is.True, "the nearest four are looked through");
        rig.Fleet.RunFor(1.5);
        for (int i = limit; i < Doors; i++)
        {
            Assert.That(rig.Thrower.Replicas, Does.Not.Contain(8100 + (ulong)i), $"door {i} is the {i + 1}th nearest: not looked through");
            Assert.That(rig.Thrower.Containers, Does.Not.Contain(PortalWindowRig.RoomId(keys[i])));
        }
        Assert.That(rig.Fleet.Gateways[0].PortalWindowsOf(rig.IdOf(rig.Thrower)), Is.EqualTo(limit));
        Assert.That(policy.Windows(rig.IdOf(rig.Thrower)), Is.EqualTo(limit), "and a policy is told how many (InterestClient.PortalWindows)");
        Assert.That(rig.Fleet.Gateways[0].PortalWindowCount, Is.GreaterThanOrEqualTo(limit));
    }

    /// <summary>Records how many windows each client was last said to look through.</summary>
    private sealed class WindowPolicy : IInterestPolicy
    {
        private readonly Dictionary<ulong, int> _windows = new();
        public int Windows(ulong clientId) => _windows.TryGetValue(clientId, out int n) ? n : -1;

        public void Collect(in InterestClient client, InterestQuery query)
        {
            _windows[client.ClientId] = client.PortalWindows;
            if (client.HasPawn) query.AddFocus(client.PawnX, client.PawnY, client.PawnZ, 1f, client.PawnNetId, client.PawnSpace);
        }

        public bool Authorize(in InterestClient client, in InterestEntity entity) => true;
    }
}
