using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Conformance scenario 56 (<c>docs/portals.md</c> D19, D23): <b>a crate thrown through an open portal stays for its thrower, with no despawn, no respawn
/// and no blink, while an onlooker who may not look through it loses it and is never told where it went.</b> The worker commits the crossing (the entity
/// changes scope, its epoch advances and its region key is the new scope's); the network gateway re-authorizes everybody who holds it before anything about
/// the destination is relayed. A client that may look through the link the crate went through, from inside the near window, passes: its record is re-filed
/// under its new region key in place, never evicted and fetched again (the HVR-683 spike blinked a crossing crate out for 0.3 s in 2 of 6 crossings).
/// Tier A: the real <see cref="NebulaGateway"/>, a <see cref="FakeWorker"/> that moves the crate between containers as a committed crossing does
/// (<see cref="FakeWorker.MoveTo"/>) and publishes its states.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformancePortalCrossingGatewayTests
{
    private const ulong Thrown = 7010;

    private static int Count(List<ulong> list, ulong id) => list.Count(x => x == id);

    [Test]
    public void ACrateThrownThroughTheDoorStaysForItsThrowerEverySixCrossingsAndTheOnlookerLosesIt()
    {
        using var rig = new PortalWindowRig();
        var door = PortalWindowRig.Door();
        rig.OpenDoor(rig.Thrower, rig.Inside);   // the stranger stands in the same window but may not look through
        var street = new ContainerRef(0);
        rig.Worker.Spawn(Thrown, PortalWindowRig.InStreet(door, 0, 1, -0.5), street);
        Assert.That(rig.Fleet.Run(() => rig.Thrower.Replicas.Contains(Thrown) && rig.Stranger.Replicas.Contains(Thrown) && rig.Inside.Replicas.Contains(Thrown), seconds: 10), Is.True,
            "in the street the crate is the street's: the stranger sees it, the thrower sees it, and the room's player sees it through the door");
        Assert.That(rig.Fleet.Run(() => rig.Thrower.Containers.Contains(PortalWindowRig.RoomId()) && rig.Thrower.Replicas.Contains(PortalWindowRig.Crate), seconds: 10), Is.True);
        int spawnsBefore = Count(rig.Thrower.Spawned, Thrown);

        for (int crossing = 1; crossing <= 6; crossing++)
        {
            bool into = crossing % 2 == 1;
            // The commit: the crate is put into the other scope at the pose carried through the link (a few centimetres past the face).
            if (into) rig.Worker.MoveTo(Thrown, PortalWindowRig.RoomRef(), PortalWindowRig.InRoom(door, 0, 1, 0.05));
            else rig.Worker.MoveTo(Thrown, street, PortalWindowRig.InStreet(door, 0, 1, -0.05));
            var wanted = into ? PortalWindowRig.RoomRef() : street;
            Assert.That(rig.Fleet.Run(() => rig.Thrower.ContainerOf.TryGetValue(Thrown, out var at) && at == wanted, seconds: 10), Is.True,
                $"crossing {crossing}: the thrower is told the crate is in {wanted}");
            rig.Fleet.RunFor(0.3);
            Assert.That(rig.Thrower.Replicas, Does.Contain(Thrown), $"crossing {crossing}: still in the thrower's hands");
            Assert.That(rig.Thrower.Despawned, Does.Not.Contain(Thrown), $"crossing {crossing}: never despawned, not for a moment");
            Assert.That(Count(rig.Thrower.Spawned, Thrown), Is.EqualTo(spawnsBefore), $"crossing {crossing}: and never spawned again: it was re-filed in place");
            Assert.That(rig.Thrower.UnresolvableNames, Is.Empty, $"crossing {crossing}: every message naming a container came after that container's row");
            Assert.That(rig.Inside.Despawned, Does.Not.Contain(Thrown), $"crossing {crossing}: the player in the room watches it come and go too");
        }
        Assert.That(rig.Thrower.LastError, Is.Empty);
    }

    [Test]
    public void AnOnlookerWhoMayNotLookThroughLosesTheCrateAndIsNeverToldWhereItWent()
    {
        using var rig = new PortalWindowRig();
        var door = PortalWindowRig.Door();
        rig.OpenDoor(rig.Thrower, rig.Inside);
        rig.Worker.Spawn(Thrown, PortalWindowRig.InStreet(door, 0, 1, -0.5), new ContainerRef(0));
        Assert.That(rig.Fleet.Run(() => rig.Stranger.Replicas.Contains(Thrown) && rig.Thrower.Replicas.Contains(Thrown), seconds: 10), Is.True);
        int wireBefore = rig.Stranger.Wire.Count;

        rig.Worker.MoveTo(Thrown, PortalWindowRig.RoomRef(), PortalWindowRig.InRoom(door, 0, 1, 0.05));

        Assert.That(rig.Fleet.Run(() => !rig.Stranger.Replicas.Contains(Thrown), seconds: 10), Is.True, "the onlooker loses the crate at once");
        rig.Fleet.RunFor(1.0);
        Assert.That(rig.Stranger.Despawned, Does.Contain(Thrown));
        Assert.That(rig.Stranger.ContainerOf[Thrown], Is.EqualTo(new ContainerRef(0)), "the last it heard, it was in the street");
        Assert.That(rig.Stranger.Wire.Skip(wireBefore).Any(w => w.Contains(PortalWindowRig.RoomId())), Is.False, "it is never told the room exists, let alone that the crate went there");
        Assert.That(rig.Stranger.Containers, Does.Not.Contain(PortalWindowRig.RoomId()));
        Assert.That(rig.Stranger.UnresolvableNames, Is.Empty);
        Assert.That(rig.Thrower.Replicas, Does.Contain(Thrown), "the thrower, who may look through, kept it");
    }

    [Test]
    public void ACrateThatCrossedIsLostWhenTheLinkClosesAfterwards()
    {
        using var rig = new PortalWindowRig();
        var door = PortalWindowRig.Door();
        var link = rig.OpenDoor(rig.Thrower, rig.Inside);
        rig.Worker.Spawn(Thrown, PortalWindowRig.InStreet(door, 0, 1, -0.5), new ContainerRef(0));
        Assert.That(rig.Fleet.Run(() => rig.Thrower.Replicas.Contains(Thrown), seconds: 10), Is.True);
        rig.Worker.MoveTo(Thrown, PortalWindowRig.RoomRef(), PortalWindowRig.InRoom(door, 0, 1, 0.05));
        Assert.That(rig.Fleet.Run(() => rig.Thrower.ContainerOf.TryGetValue(Thrown, out var at) && at == PortalWindowRig.RoomRef(), seconds: 10), Is.True);

        link.State = PortalState.Closed;
        rig.Worker.PublishPortalLinks(new[] { link });
        Assert.That(rig.Fleet.Run(() => !rig.Thrower.Replicas.Contains(Thrown), seconds: 5), Is.True, "the window that authorised it is gone, and so is the crate with it");
    }
}
