using System.Collections;
using System.Reflection;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The window rule on ground that is a frame with regions of its own (<c>docs/portals.md</c> D20, <c>docs/container-tree.md</c> D18, NEB-426): a bunker's door
/// stands on a planet that is a carrier, and a player inside looks out at the ground in front of it. The ground's entities are bucketed in the planet's own
/// coordinates and its rows are fixed in the planet's box, so the window must be a box in the planet's frame as well as in the scope's space, must keep the
/// rows of exactly that ground and the planet they are fixed in (and nothing else of the planet), and must authorize what stands in it through the frame. The
/// real <see cref="NebulaGateway"/> in this process, its lease rows and records written in by hand as <c>InstanceTests</c> does: a <see cref="FakeWorker"/>
/// buckets entities by scope space alone and cannot stand in for a planet.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class PortalWindowFrameTests
{
    private const ulong PlanetId = 77, RockNear = 78, RockFar = 79, PawnId = 9001, ClientId = 42, Scope = 5151;
    private const string PlanetBox = "planet#77";
    private const string Bunker = "rt_5100", Ground = "rt_9000001", FarGround = "rt_9000002";
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private NebulaGateway _gateway = null!;
    private object _client = null!, _pawn = null!;

    private static object Field(object target, string name) => target.GetType().GetField(name, Private | BindingFlags.Public)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private | BindingFlags.Public)!.SetValue(target, value);
    private object Call(string name, params object[] args) => typeof(NebulaGateway).GetMethod(name, Private)!.Invoke(_gateway, args)!;
    private static object Nested(string name) => Activator.CreateInstance(typeof(NebulaGateway).GetNestedType(name, BindingFlags.NonPublic)!, true)!;
    private IDictionary Dictionary(string name) => (IDictionary)typeof(NebulaGateway).GetField(name, Private)!.GetValue(_gateway)!;

    private object Record(ulong netId, ContainerRef container, Vector3 at)
    {
        var rec = Nested("EntityRecord");
        Set(rec, "NetId", netId);
        Set(rec, "Container", container);
        Set(rec, "OwnerWorkerIndex", (ushort)1);
        var spawn = (EntitySpawnMsg)Field(rec, "LastSpawn");
        spawn.NetId = netId; spawn.Container = container; spawn.LocalPosition = at; spawn.LocalRotation = Quaternion.identity; spawn.LocalScale = Vector3.one;
        Set(rec, "LastSpawn", spawn);
        Dictionary("_entities").Add(netId, rec);
        return rec;
    }

    [SetUp]
    public void SetUp()
    {
        typeof(ContainerRegistry).GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { new ServiceManifest
        { Containers = new() { new Container { ContainerId = "space", Index = 0, Size = new(400_000, 400_000, 400_000) } } } });
        // The bunker: one room of the scope the player is in.
        // And the planet's two ground chunks, which this process also resolves (as a worker's registry would): the scope of what stands in them is the scope the planet is in.
        ContainerRegistry.SyncRuntime(new[]
        {
            new LeaseInfo { ContainerId = Bunker, HasBounds = true, BoundsCenter = new Vector3(-50_000, 0, 0), BoundsSize = new(40, 40, 40), Instance = new() { InstanceId = Scope } },
            new LeaseInfo { ContainerId = Ground, HasBounds = true, BoundsCenter = new Vector3(100_010, 0, 10), BoundsSize = new(64, 64, 64) },
            new LeaseInfo { ContainerId = FarGround, HasBounds = true, BoundsCenter = new Vector3(105_000, 0, 10), BoundsSize = new(64, 64, 64) },
        });
        _gateway = new NebulaGateway();
        typeof(NebulaGateway).GetProperty("Config")!.SetValue(_gateway, new NebulaConfig());
        typeof(NebulaGateway).GetField("_transport", Private)!.SetValue(_gateway, new CaptureTransport());
        Call("InitializeInterest");

        // The planet 100 km out, a carrier whose box is a frame with regions of its own, and two chunks of its ground fixed in it: one under the door and one 5 km away.
        Dictionary("_ownershipById").Add(PlanetBox, new ContainerOwnershipEntry { ContainerId = PlanetBox, ContainerIndex = ContainerRef.RuntimeIndex, Epoch = 1, State = LeaseState.Active });
        Dictionary("_ownershipById").Add(Ground, new ContainerOwnershipEntry
        {
            ContainerId = Ground, ContainerIndex = ContainerRef.RuntimeIndex, Epoch = 1, State = LeaseState.Active, HasPlacement = true,
            Placement = new ContainerPlacement { ParentId = PlanetBox, Center = new Double3(10, 0, 10), Size = new Vector3(64, 64, 64) },
        });
        Dictionary("_ownershipById").Add(FarGround, new ContainerOwnershipEntry
        {
            ContainerId = FarGround, ContainerIndex = ContainerRef.RuntimeIndex, Epoch = 1, State = LeaseState.Active, HasPlacement = true,
            Placement = new ContainerPlacement { ParentId = PlanetBox, Center = new Double3(5000, 0, 10), Size = new Vector3(64, 64, 64) },
        });
        ((HashSet<string>)typeof(NebulaGateway).GetField("_ownRegionFrames", Private)!.GetValue(_gateway)!).Add(PlanetBox);
        ((HashSet<ulong>)typeof(NebulaGateway).GetField("_ownRegionCarriers", Private)!.GetValue(_gateway)!).Add(PlanetId);
        Call("RefreshAbsolute", Record(PlanetId, new ContainerRef(0), new Vector3(100_000, 0, 0)));

        // Rocks on the ground: one in front of the door (frame-local 10, 1, 4: the planet's origin is at (100000, 0, 0) with no turn), one 5 km along.
        var near = Record(RockNear, ContainerRef.Runtime(9_000_001), new Vector3(0, 1, -6));
        var far = Record(RockFar, ContainerRef.Runtime(9_000_002), new Vector3(0, 1, 0));
        Call("RefreshAbsolute", near);
        Call("RefreshAbsolute", far);

        // The player in the bunker, standing in the far window of a door whose near face is on the ground.
        _pawn = Record(PawnId, ContainerRef.Runtime(5100), new Vector3(0, 0, 0));
        Set(_pawn, "OwnerClientId", ClientId);
        Call("RefreshAbsolute", _pawn);
        _client = Nested("ClientConn");
        Set(_client, "ClientId", ClientId); Set(_client, "PeerId", 7); Set(_client, "Welcomed", true); Set(_client, "PawnNetId", PawnId); Set(_client, "LastScope", Scope);
        Dictionary("_clientsById").Add(ClientId, _client);
    }

    [TearDown] public void TearDown() => ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());

    private void OpenDoor(Double3 doorAt)
    {
        // The door on the planet's ground, facing +z into the bunker, whose far face is on the bunker's wall at (-50020, 0, 0) facing +x.
        var portal = new PortalRecord
        {
            PortalId = 1, Id = "door", NearScope = 0, NearContainerId = Ground, NearContainer = ContainerRef.Runtime(9_000_001),
            NearOrigin = doorAt, NearRotation = Quaternion.identity, FarKind = PortalFarKind.Template, TemplateId = "bunker",
            FarOrigin = new Double3(-50_020, 0, 0), FarRotation = Quaternion.Euler(0f, 90f, 0f),
            ApertureKind = PortalApertureKind.Box, HalfWidth = 1.5f, HalfHeight = 2.2f, BandMargin = 0.5f, BandDepth = 2f, WindowMargin = 8f, WindowDepth = 48f,
        };
        var entry = Nested("PortalEntry");
        Set(entry, "Record", portal);
        Dictionary("_portals").Add(portal.PortalId, entry);
        var link = Nested("LinkEntry");
        Set(link, "Record", new PortalLinkRecord { LinkId = 2, PortalId = 1, FarScope = Scope, FarEntryId = Bunker, State = PortalState.Open });
        ((HashSet<ulong>)Field(link, "Observers")).Add(ClientId);
        Dictionary("_portalLinks").Add(2UL, link);
        ((IList)Field(entry, "Links")).Add(link);
    }

    /// <summary>One evaluation of the client, as the gateway runs it, and the rows it was then sent.</summary>
    private List<string> EvaluatedRows()
    {
        Call("EvaluateClient", _client, 0.0);
        return ((HashSet<string>)Field(_client, "KnownContainers")).ToList();
    }

    private sealed class CaptureTransport : ITransport
    {
        public string Name => "test"; public bool IsRunning => true; public int LocalPort => 0;
        public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload) { }
        public void Dispose() { } public void Listen(int port) { } public void StartClient() { } public int Connect(string host, int port) => 0;
        public void Disconnect(int peerId) { } public bool IsConnected(int peerId) => true; public int RoundTripMs(int peerId) => 0;
        public void Poll(Action<TransportEvent> handler) { } public void Flush() { } public void Stop() { }
    }

    [Test]
    public void AWindowOntoAPlanetsGroundKeepsTheGroundAtTheDoorAndThePlanetItIsFixedInAndNothingElseOfIt()
    {
        // The door's near face on the ground at frame-local (10, 0, 10), so at (100010, 0, 10) in the scope's space; the player is in the bunker's far window.
        OpenDoor(new Double3(100_010, 0, 10));
        // The bunker's wall is where the far window starts; the player stands 5 m in.
        Set(_pawn, "LastSpawn", WithPosition(_pawn, new Vector3(-50_020 + 5 - -50_000, 0, 0)));
        Call("RefreshAbsolute", _pawn);
        Call("RefreshWindows", _client, true);

        var windows = (IList)Field(_client, "Windows");
        Assert.That(windows.Count, Is.EqualTo(1), "the player looks out through the door");
        var frames = (IList)Field(_client, "FrameWindows");
        Assert.That(frames.Count, Is.EqualTo(1), "and the window reaches into the planet's frame, where the ground's entities are bucketed");
        Assert.That((ulong)Field(frames[0]!, "FrameKey"), Is.EqualTo(RegionKeys.FrameKeyOf(ContainerRef.Dynamic(PlanetId))));
        Assert.That(((HashSet<ulong>)Field(_client, "WindowCarriers")).ToArray(), Is.EquivalentTo(new[] { PlanetId }), "the planet the ground is fixed in, and no more");

        var rows = EvaluatedRows();
        Assert.That(rows, Does.Contain(Ground), "the ground in front of the door is kept");
        Assert.That(rows, Does.Not.Contain(FarGround), "the rest of the planet's ground is not");

        Assert.That((bool)Call("CanObserve", _client, Dictionary("_entities")[PlanetId]!), Is.True, "the player may hold the planet the ground is fixed in");
        Assert.That((bool)Call("CanObserve", _client, Dictionary("_entities")[RockNear]!), Is.True, "and the rock on the ground in front of the door, through the frame");
        Assert.That((bool)Call("CanObserve", _client, Dictionary("_entities")[RockFar]!), Is.False, "but not the rock 5 km along the same planet");

        // The window is a focus in the planet's own coordinates, in the scope the ground is in.
        var query = new InterestQuery();
        query.Reset(InterestSettings.Default);
        Call("AddPortalWindows", _client, query);
        Assert.That(query.Foci.Any(f => f.HasScope && f.Scope == 0 && f.Space == RegionKeys.FrameKeyOf(ContainerRef.Dynamic(PlanetId))), Is.True, "a box in the planet's regions");
        Assert.That(query.Foci.Any(f => f.HasScope && f.Scope == 0 && f.Space == 0), Is.True, "and one in the scope's own");

        // When the link closes there is no window, no carrier, no row.
        var link = Dictionary("_portalLinks")[2UL]!;
        var record = (PortalLinkRecord)Field(link, "Record");
        record.State = PortalState.Closed;
        Set(link, "Record", record);
        Call("RefreshWindows", _client, true);
        Assert.That(((IList)Field(_client, "Windows")).Count, Is.EqualTo(0));
        Assert.That(((IList)Field(_client, "FrameWindows")).Count, Is.EqualTo(0));
        Assert.That(((HashSet<ulong>)Field(_client, "WindowCarriers")).Count, Is.EqualTo(0), "back inside, nothing is kept by name");
        Assert.That((bool)Call("CanObserve", _client, Dictionary("_entities")[PlanetId]!), Is.False);
    }

    /// <summary>
    /// A door 10⁸ m out in its scope, where a float is 8 m apart: the room's part is placed by its lease row's centre in double, and a player
    /// 0.3 m inside its far face, at the floor 1.26 m below the aperture's centre, is read there and is inside the far window. Narrowed to a
    /// float, the point fell 1.6 m behind the face and the player inside saw nothing through the open door (NEB-431).
    /// </summary>
    [Test]
    public void APlayerJustInsideAFaceTenToTheEighthMetresOutIsInsideItsFarWindow()
    {
        const ulong Room = 5200, RoomScope = 6262;
        var face = new Double3(-69_812_950.421, -287_858.201, -89_203_062.32);
        var center = face + new Double3(0, 1, 12);
        var size = new Vector3(20, 8, 24);
        ContainerRegistry.SyncRuntime(new[] { new LeaseInfo { ContainerId = "rt_5200", HasBounds = true, BoundsCenter = center.ToVector3(), BoundsSize = size, Instance = new() { InstanceId = RoomScope } } });
        Dictionary("_ownershipById").Add("rt_5200", new ContainerOwnershipEntry
        {
            ContainerId = "rt_5200", ContainerIndex = ContainerRef.RuntimeIndex, Epoch = 1, State = LeaseState.Active, HasBounds = true, BoundsSize = size,
            HasPlacement = true, Placement = ContainerPlacement.Root(center, size),
        });
        var pawn = Record(9002, ContainerRef.Runtime(Room), (face + new Double3(0, -1.26, 0.3) - center).ToVector3());
        Call("RefreshAbsolute", pawn);

        var point = (Double3)Call("ScopePointOf", pawn);
        var window = new PortalRecord
        {
            NearOrigin = face, NearRotation = Quaternion.Euler(54.7f, 0f, 0f), FarOrigin = face, FarRotation = Quaternion.identity,
            HalfWidth = 1.5f, HalfHeight = 1.3f, WindowMargin = 8f, WindowDepth = 48f,
        }.FarWindow(RoomScope);
        var local = window.ToLocal(point);
        Assert.That(local.X, Is.EqualTo(0).Within(2e-3), "across the face");
        Assert.That(local.Y, Is.EqualTo(-1.26).Within(2e-3), "at the floor");
        Assert.That(local.Z, Is.EqualTo(0.3).Within(2e-3), "0.3 m inside the face, where the player stands");
        Assert.That(window.Contains(point), Is.True, "inside the far window");
    }

    private object WithPosition(object rec, Vector3 at)
    {
        var spawn = (EntitySpawnMsg)Field(rec, "LastSpawn");
        spawn.LocalPosition = at;
        return spawn;
    }
}
