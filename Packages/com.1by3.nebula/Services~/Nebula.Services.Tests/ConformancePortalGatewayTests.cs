using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// Conformance scenario 50 (<c>docs/portals.md</c> D11, D13, D21, D45), the network gateway's half: <b>a portal's record and the state of
/// its links reach the clients that hold the container row of either face, and only those; a link says to each client only whether that client
/// is a member and whether it may look through; a member is sent the far row ahead of the link, and its <c>PortalWarm</c> goes back to the
/// worker that owns the portal only from a member.</b> Tier A: the real <see cref="NebulaGateway"/> on a real socket over an in-process
/// <see cref="LocalControlPlane"/>, with a <see cref="FakeWorker"/> that publishes portals exactly as <c>NebulaWorker.UpdatePortals</c> does.
/// The state machine behind the records is <c>Tests/EditMode/PortalLinkTests.cs</c>, and a crossing through an open link is
/// <c>Tests/EditMode/PortalCrossingTests.cs</c>; neither needs a gateway.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformancePortalGatewayTests
{
    private const string Key = "interior/portal-room";
    private const string Elsewhere = "interior/portal-elsewhere";
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-portal-gw-" + Guid.NewGuid().ToString("N"));
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

    private static ScopeDefinition Room(float x) => new()
    {
        Kind = ScopeKind.Parts,
        Parts = { new ScopePart { PartId = "room", Center = new(x, 0, 0), Size = new(40, 40, 40) } },
    };

    private static ContainerRef RoomRef(string key) => ContainerRef.Runtime(ScopeKeys.Hash(key + "/room"));
    private static string RoomId(string key) => ContainerRegistry.RuntimeContainerId(ScopeKeys.Hash(key + "/room"));

    private static readonly ulong DoorId = PortalIds.Of("c0", "door");

    /// <summary>A template portal in the public world's container c0 whose far side is the room of <see cref="Key"/>.</summary>
    private static PortalRecord Door() => new()
    {
        PortalId = DoorId, Id = "door", NearScope = 0, NearContainerId = "c0", NearContainer = new ContainerRef(0),
        NearOrigin = new Double3(10, 0, 10), NearRotation = Quaternion.identity,
        FarKind = PortalFarKind.Template, TemplateId = "room", FarRotation = Quaternion.identity,
        ApertureKind = PortalApertureKind.Box, HalfWidth = 1.5f, HalfHeight = 2.2f, BandMargin = 0.5f, BandDepth = 2f, WindowMargin = 8f, WindowDepth = 48f,
    };

    private static PortalLinkRecord Link(PortalState state, PortalWait wait, ulong[] members, ulong[] observers, uint generation = 1) => new()
    {
        LinkId = PortalIds.Link(DoorId, "party"), PortalId = DoorId, PartyHash = PortalIds.PartyHash("party"),
        FarScope = ScopeKeys.Hash(Key), FarEntryId = RoomId(Key), FarEntry = RoomRef(Key),
        State = state, Wait = wait, Generation = generation, Progress = state == PortalState.Open ? (byte)255 : (byte)100,
        Members = members.ToList(), Observers = observers.ToList(),
    };

    private static ulong IdOf(FakeClient c) => c.Welcome!.Value.ClientId;

    private static Fleet StartFleet()
    {
        var fleet = new Fleet(gateways: 1);
        fleet.Worker.SpawnIntoRequestedContainer = true;
        foreach (var (key, x) in new[] { (Key, 500f), (Elsewhere, 900f) })
            fleet.Plane.ActivateScope(new ScopeActivationRequest { ScopeKey = key, Definition = Room(x), PreferredWorkerId = fleet.Worker.WorkerId, Requester = "test" });
        return fleet;
    }

    private static bool Holds(FakeClient c, string container) => c.Welcome != null && c.Join == JoinState.Joined && c.Containers.Contains(container);

    [Test]
    public void APortalAndItsLinkReachClientsHoldingEitherFacesRowAndOnlyThose()
    {
        using var fleet = StartFleet();
        var near = fleet.Connect(0, "near");                      // stands in c0: holds the near face's row
        var far = fleet.Connect(0, "far", scope: Key);            // stands in the room: holds the far face's row
        var other = fleet.Connect(0, "other", scope: Elsewhere);  // holds neither
        Assert.That(fleet.Run(() => Holds(near, "c0") && Holds(far, RoomId(Key)) && other.Join == JoinState.Joined, seconds: 20), Is.True, "all three joined");

        var link = Link(PortalState.Open, PortalWait.None, new[] { IdOf(far) }, new[] { IdOf(far) });
        fleet.Worker.PublishPortals(new[] { Door() });
        fleet.Worker.PublishPortalLinks(new[] { link });
        Assert.That(fleet.Run(() => near.Links.Count == 1 && far.Links.Count == 1, seconds: 10), Is.True, "both faces' clients were told");
        fleet.RunFor(0.5);

        Assert.That(near.Portals.Keys, Is.EquivalentTo(new[] { DoorId }));
        Assert.That(far.Portals.Keys, Is.EquivalentTo(new[] { DoorId }));
        Assert.That(other.Portals, Is.Empty, "a client that holds neither row is told nothing");
        Assert.That(other.Links, Is.Empty);
        Assert.That(near.Portals[DoorId].NearContainerId, Is.EqualTo("c0"));
        Assert.That(near.Portals[DoorId].HalfWidth, Is.EqualTo(1.5f));
        Assert.That(near.Portals[DoorId].NearOrigin, Is.EqualTo(new Double3(10, 0, 10)));

        // What a link says to a client is its own part in it, never who else is in it.
        var seenByFar = far.Links[link.LinkId];
        Assert.That(seenByFar.Flags, Is.EqualTo(PortalLinkFlags.Member | PortalLinkFlags.Observer));
        Assert.That(seenByFar.Members, Is.Empty.Or.Null);
        Assert.That(seenByFar.Observers, Is.Empty.Or.Null);
        Assert.That(seenByFar.State, Is.EqualTo(PortalState.Open));
        Assert.That(seenByFar.FarScope, Is.EqualTo(link.FarScope));
        var seenByNear = near.Links[link.LinkId];
        Assert.That(seenByNear.Flags, Is.EqualTo(PortalLinkFlags.None), "a non-member gets no observer bit");
        Assert.That(seenByNear.IsObserver, Is.False);
        Assert.That(seenByNear.State, Is.EqualTo(PortalState.Open));

        // The gateway enforces the list it was given and runs no game code.
        var gateway = fleet.Gateways[0];
        Assert.That(gateway.PortalCount, Is.EqualTo(1));
        Assert.That(gateway.PortalLinkCount, Is.EqualTo(1));
        Assert.That(gateway.PortalObserves(IdOf(far), link.LinkId), Is.True);
        Assert.That(gateway.PortalObserves(IdOf(near), link.LinkId), Is.False);
        Assert.That(gateway.PortalObserves(IdOf(other), link.LinkId), Is.False);

        // The record goes behind the row that names it, never ahead of it.
        Assert.That(near.Wire.IndexOf("portal+ door"), Is.GreaterThan(near.Wire.IndexOf("container+ c0")));
        Assert.That(far.Wire.IndexOf("portal+ door"), Is.GreaterThan(far.Wire.IndexOf("container+ " + RoomId(Key))));
        Assert.That(far.LastError, Is.Empty);
    }

    [Test]
    public void AnObserverThatIsNotAMemberKnowsItMayLookAndAnAnyoneLinkIsOpenToEveryone()
    {
        using var fleet = StartFleet();
        var near = fleet.Connect(0, "near");
        var far = fleet.Connect(0, "far", scope: Key);
        Assert.That(fleet.Run(() => Holds(near, "c0") && Holds(far, RoomId(Key)), seconds: 20), Is.True);

        var link = Link(PortalState.Open, PortalWait.None, new[] { IdOf(far) }, new[] { IdOf(near) });
        fleet.Worker.PublishPortals(new[] { Door() });
        fleet.Worker.PublishPortalLinks(new[] { link });
        Assert.That(fleet.Run(() => near.Links.Count == 1 && far.Links.Count == 1, seconds: 10), Is.True);
        Assert.That(near.Links[link.LinkId].Flags, Is.EqualTo(PortalLinkFlags.Observer), "an observer that is not a member");
        Assert.That(far.Links[link.LinkId].Flags, Is.EqualTo(PortalLinkFlags.Member), "a member the game does not let look through");

        link.Flags = PortalLinkFlags.ObserverAnyone;
        link.Observers = new();
        fleet.Worker.PublishPortalLinks(new[] { link });
        Assert.That(fleet.Run(() => (near.Links[link.LinkId].Flags & PortalLinkFlags.ObserverAnyone) != 0, seconds: 10), Is.True);
        Assert.That(near.Links[link.LinkId].IsObserver, Is.True, "a door anyone may look through");
        Assert.That(fleet.Gateways[0].PortalObserves(IdOf(near), link.LinkId), Is.True);
    }

    [Test]
    public void AMembersFarRowIsSentAheadOfTheLinkAndItsWarmGoesBackOnlyFromAMember()
    {
        using var fleet = StartFleet();
        var member = fleet.Connect(0, "member");   // in the public world: has never been sent the room's row
        var stranger = fleet.Connect(0, "stranger");
        member.AutoWarm = true;
        Assert.That(fleet.Run(() => Holds(member, "c0") && Holds(stranger, "c0"), seconds: 20), Is.True);
        Assert.That(member.Containers, Does.Not.Contain(RoomId(Key)));

        var link = Link(PortalState.Preparing, PortalWait.ClientWarming, new[] { IdOf(member) }, new[] { IdOf(member) });
        fleet.Worker.PublishPortals(new[] { Door() });
        fleet.Worker.PublishPortalLinks(new[] { link });
        Assert.That(fleet.Run(() => fleet.Worker.PortalWarms.Count > 0, seconds: 10), Is.True, "the member prepared the far window and said so");

        Assert.That(member.Containers, Does.Contain(RoomId(Key)), "the far row was pinned for it");
        Assert.That(member.Wire.IndexOf("container+ " + RoomId(Key)), Is.LessThan(member.Wire.FindIndex(w => w.StartsWith("link "))), "ahead of the link that names it");
        Assert.That(member.UnresolvableNames, Is.Empty);
        var warm = fleet.Worker.PortalWarms[0];
        Assert.That(warm.LinkId, Is.EqualTo(link.LinkId));
        Assert.That(warm.Generation, Is.EqualTo(1u));
        Assert.That(warm.ClientId, Is.EqualTo(IdOf(member)), "the gateway stamps the client; a client cannot speak for another");

        // A client that is not a member of the link cannot ask the worker to count it warm.
        int before = fleet.Worker.PortalWarms.Count;
        stranger.SendPortalWarm(link.LinkId, 1);
        member.SendPortalWarm(12345, 1);
        fleet.RunFor(0.5);
        Assert.That(fleet.Worker.PortalWarms.Count, Is.EqualTo(before), "a warm from a non-member, or for a link nobody knows, goes nowhere");

        // The link opening reaches the member too.
        link.State = PortalState.Open;
        link.Wait = PortalWait.None;
        fleet.Worker.PublishPortalLinks(new[] { link });
        Assert.That(fleet.Run(() => member.Links[link.LinkId].State == PortalState.Open, seconds: 10), Is.True);
    }

    [Test]
    public void AMemberIsSentEveryContainerOfTheFarWindowTheLinkNamesAheadOfTheLink()
    {
        const string Wings = "interior/portal-wings";
        using var fleet = StartFleet();
        fleet.Plane.ActivateScope(new ScopeActivationRequest
        {
            ScopeKey = Wings,
            Definition = new ScopeDefinition
            {
                Kind = ScopeKind.Parts,
                Parts =
                {
                    new ScopePart { PartId = "room", Center = new(700, 0, 0), Size = new(40, 40, 40) },
                    new ScopePart { PartId = "wing", Center = new(700, 0, 40), Size = new(40, 40, 40) },
                },
            },
            PreferredWorkerId = fleet.Worker.WorkerId,
            Requester = "test",
        });
        var member = fleet.Connect(0, "member");
        Assert.That(fleet.Run(() => Holds(member, "c0"), seconds: 20), Is.True);
        string wingId = ContainerRegistry.RuntimeContainerId(ScopeKeys.Hash(Wings + "/wing"));
        var link = Link(PortalState.Preparing, PortalWait.ClientWarming, new[] { IdOf(member) }, new[] { IdOf(member) });
        link.FarScope = ScopeKeys.Hash(Wings);
        link.FarEntryId = RoomId(Wings);
        link.FarEntry = RoomRef(Wings);
        link.FarRowIds = new List<string> { RoomId(Wings), wingId };
        link.FarRows = new List<ContainerRef> { RoomRef(Wings), ContainerRef.Runtime(ScopeKeys.Hash(Wings + "/wing")) };

        fleet.Worker.PublishPortals(new[] { Door() });
        fleet.Worker.PublishPortalLinks(new[] { link });
        Assert.That(fleet.Run(() => member.Containers.Contains(RoomId(Wings)) && member.Containers.Contains(wingId), seconds: 10), Is.True,
            "the member holds the entry and the other container of the far window: it prepares both before it says it is warm (docs/portals.md D16)");
        Assert.That(member.UnresolvableNames, Is.Empty);
        Assert.That(member.Links[link.LinkId].FarRows.Count, Is.EqualTo(2), "and the link it is told of names them");
    }

    [Test]
    public void ALinkThatClosesIsStillDeliveredAfterItsMemberLosesThePin()
    {
        using var fleet = StartFleet();
        var member = fleet.Connect(0, "member");
        member.AutoWarm = true;
        Assert.That(fleet.Run(() => Holds(member, "c0"), seconds: 20), Is.True);
        var link = Link(PortalState.Open, PortalWait.None, new[] { IdOf(member) }, new[] { IdOf(member) });
        fleet.Worker.PublishPortals(new[] { Door() });
        fleet.Worker.PublishPortalLinks(new[] { link });
        Assert.That(fleet.Run(() => member.Links.Count == 1, seconds: 10), Is.True);

        link.State = PortalState.Closed;
        fleet.Worker.PublishPortalLinks(new[] { link });
        Assert.That(fleet.Run(() => member.Links[link.LinkId].State == PortalState.Closed, seconds: 10), Is.True, "the member learns the door closed");
    }

    [Test]
    public void ALateJoinerIsToldAndAWithdrawnPortalIsTakenBackFromEveryone()
    {
        using var fleet = StartFleet();
        var first = fleet.Connect(0, "first");
        Assert.That(fleet.Run(() => Holds(first, "c0"), seconds: 20), Is.True);
        var link = Link(PortalState.Open, PortalWait.None, Array.Empty<ulong>(), Array.Empty<ulong>());
        fleet.Worker.PublishPortals(new[] { Door() });
        fleet.Worker.PublishPortalLinks(new[] { link });
        Assert.That(fleet.Run(() => first.Links.Count == 1, seconds: 10), Is.True);

        var late = fleet.Connect(0, "late");
        Assert.That(fleet.Run(() => late.Links.Count == 1 && late.Portals.Count == 1, seconds: 20), Is.True, "records live on the gateway, so a client that joins later is told");

        fleet.Worker.PublishPortals(Array.Empty<PortalRecord>(), new[] { DoorId });
        fleet.Worker.PublishPortalLinks(Array.Empty<PortalLinkRecord>(), new[] { link.LinkId });
        Assert.That(fleet.Run(() => first.Portals.Count == 0 && late.Portals.Count == 0, seconds: 10), Is.True);
        Assert.That(first.Links, Is.Empty);
        Assert.That(late.Links, Is.Empty);
        Assert.That(fleet.Gateways[0].PortalCount, Is.EqualTo(0));
    }

    [Test]
    public void APortalGoesWithTheWorkerThatPublishedIt()
    {
        using var fleet = StartFleet();
        var client = fleet.Connect(0, "client");
        Assert.That(fleet.Run(() => Holds(client, "c0"), seconds: 20), Is.True);
        fleet.Worker.PublishPortals(new[] { Door() });
        fleet.Worker.PublishPortalLinks(new[] { Link(PortalState.Open, PortalWait.None, Array.Empty<ulong>(), Array.Empty<ulong>()) });
        Assert.That(fleet.Run(() => client.Links.Count == 1, seconds: 10), Is.True);

        fleet.KillWorker(fleet.Worker);
        Assert.That(fleet.Run(() => fleet.Gateways[0].PortalCount == 0, seconds: 15), Is.True, "its portals are dropped with its link");
    }

    [Test]
    public void AProtocol27ClientIsRefusedWithTheNewWindow()
    {
        using var fleet = new Fleet(gateways: 1);
        var old = fleet.Connect(0, "protocol-27");
        old.AnnounceVersion = 27;
        Assert.That(fleet.Run(() => old.Rejected != null, seconds: 15), Is.True, "a client of 27 is refused: it would predict into an aperture it does not know is solid");
        var rejected = old.Rejected!.Value;
        Assert.That(rejected.Code, Is.EqualTo(JoinRejectReason.ProtocolUnsupported));
        Assert.That(rejected.SupportedMinVersion, Is.EqualTo((ushort)28));
        Assert.That(rejected.SupportedMaxVersion, Is.EqualTo((ushort)28));
        Assert.That(old.Welcome, Is.Null);
        Assert.That(fleet.Worker.Claims, Is.Empty);
    }
}
