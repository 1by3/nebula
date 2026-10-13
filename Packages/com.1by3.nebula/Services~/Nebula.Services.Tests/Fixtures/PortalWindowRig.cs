using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// A public-world container with a door in it and a room behind the door, for the tests of a portal's windows (<c>docs/portals.md</c> D20 to D24).
/// The door's near face stands at (10, 0, 10) in the public world's container <c>c0</c>, facing +z; the room is a scope of one part centred on
/// (500, 0, 0), and its far face stands on the room's -x wall at (480, 0, 0), facing +x. So the door is an offset portal, which the window maths does not
/// care about (each side's window is measured in its own face's axes): the near window is x ∈ [0.5, 19.5], y ∈ [-10.2, 10.2], z ∈ [-38, 10] of the public
/// world, the far window x ∈ [480, 528], y ∈ [-10.2, 10.2], z ∈ [-9.5, 9.5] of the room. The real <see cref="NebulaGateway"/> on a real socket, a
/// <see cref="FakeWorker"/> that publishes the portal and its link exactly as <c>NebulaWorker.UpdatePortals</c> does, and four clients: one outside in the
/// door's near window (the thrower), one beside it that may not look through, one in the room's far window, and one outside but away from the door.
/// </summary>
public sealed class PortalWindowRig : IDisposable
{
    public const string Room = "interior/window-room";
    public const string Inner = "interior/window-inner";
    public const ulong Crate = 7001, Rubble = 7002, Rock = 7003, Pebble = 7004, InnerCrate = 7005;

    public readonly Fleet Fleet;
    public FakeWorker Worker => Fleet.Worker;
    public readonly FakeClient Thrower, Stranger, Distant, Inside;
    public readonly ulong ThrowerPawn, StrangerPawn, DistantPawn, InsidePawn;
    private readonly string _directory;

    public static ContainerRef RoomRef(string key = Room) => ContainerRef.Runtime(ScopeKeys.Hash(key + "/room"));
    public static string RoomId(string key = Room) => ContainerRegistry.RuntimeContainerId(ScopeKeys.Hash(key + "/room"));
    public static ulong ScopeOf(string key) => ScopeKeys.Hash(key);
    public static readonly ulong DoorId = PortalIds.Of("c0", "door");

    /// <summary>The room's centre for a scope key: where its one part stands.</summary>
    public static Vector3 CenterOf(string key) => key == Room ? new Vector3(500, 0, 0) : key == Inner ? new Vector3(900, 0, 0) : new Vector3(100_000, 0, 0);

    public static ScopeDefinition RoomDefinition(Vector3 center) => new()
    {
        Kind = ScopeKind.Parts,
        Parts = { new ScopePart { PartId = "room", Center = center, Size = new(40, 40, 40) } },
    };

    /// <summary>The door: a template portal in <c>c0</c>, whose far face is on the room's wall.</summary>
    public static PortalRecord Door(string id = "door", Double3? nearOrigin = null, Double3? farOrigin = null, string scope = Room) => new()
    {
        PortalId = PortalIds.Of("c0", id), Id = id, NearScope = 0, NearContainerId = "c0", NearContainer = new ContainerRef(0),
        NearOrigin = nearOrigin ?? new Double3(10, 0, 10), NearRotation = Quaternion.identity,
        FarKind = PortalFarKind.Template, TemplateId = "room", FarScope = 0,
        FarOrigin = farOrigin ?? new Double3(CenterOf(scope).x - 20, 0, 0), FarRotation = Quaternion.Euler(0f, 90f, 0f),
        ApertureKind = PortalApertureKind.Box, HalfWidth = 1.5f, HalfHeight = 2.2f, BandMargin = 0.5f, BandDepth = 2f, WindowMargin = 8f, WindowDepth = 48f,
    };

    public static PortalLinkRecord Link(PortalRecord door, string scope, PortalState state, IEnumerable<ulong> observers, IEnumerable<ulong>? members = null, uint generation = 1) => new()
    {
        LinkId = PortalIds.Link(door.PortalId, "party"), PortalId = door.PortalId, PartyHash = PortalIds.PartyHash("party"),
        FarScope = ScopeOf(scope), FarEntryId = RoomId(scope), FarEntry = RoomRef(scope),
        State = state, Wait = PortalWait.None, Generation = generation, Progress = 255,
        Members = (members ?? Array.Empty<ulong>()).ToList(), Observers = observers.ToList(),
    };

    /// <summary>The window of the door's far side, for placing things inside or outside it.</summary>
    public static PortalWindow FarWindow(PortalRecord door, string scope = Room) => door.FarWindow(ScopeOf(scope));

    /// <summary>A point of the far window in the face's axes, as a position local to the room's one part.</summary>
    public static Vector3 InRoom(PortalRecord door, double x, double y, double z, string scope = Room)
    {
        var abs = FarWindow(door, scope).ToAbsolute(new Double3(x, y, z));
        return new Vector3((float)(abs.X - CenterOf(scope).x), (float)(abs.Y - CenterOf(scope).y), (float)(abs.Z - CenterOf(scope).z));
    }

    /// <summary>A point of the door's near window in the face's axes, as a position local to <c>c0</c> (centred on (32, 0, 0)).</summary>
    public static Vector3 InStreet(PortalRecord door, double x, double y, double z)
    {
        var abs = door.NearWindow.ToAbsolute(new Double3(x, y, z));
        return new Vector3((float)(abs.X - 32), (float)abs.Y, (float)abs.Z);
    }

    public PortalWindowRig(IEnumerable<string>? extraScopes = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-portal-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        var path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "c0", Index = 0, Size = new(64, 64, 64), transform = new ContainerFrame { position = new(32, 0, 0) } } },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);

        Fleet = new Fleet(gateways: 1);
        foreach (var key in new[] { Room, Inner }.Concat(extraScopes ?? Array.Empty<string>()))
            Fleet.Plane.ActivateScope(new ScopeActivationRequest { ScopeKey = key, Definition = RoomDefinition(CenterOf(key)), PreferredWorkerId = Worker.WorkerId, Requester = "test" });
        Worker.DeferSpawns = true;
        Thrower = Fleet.Connect(0, "thrower");
        Stranger = Fleet.Connect(0, "stranger");
        Distant = Fleet.Connect(0, "distant");
        Inside = Fleet.Connect(0, "inside", scope: Room);
        Assert.That(Fleet.Run(() => Worker.Claims.Count >= 4, seconds: 20), Is.True, "all four clients were placed");
        var door = Door();
        ulong ClaimOf(string name) => Worker.Claims.First(c => c.Name == name).ClientId;
        ThrowerPawn = Worker.Spawn(5001, InStreet(door, 0, 0, -8), new ContainerRef(0), owner: ClaimOf("thrower")).NetId;
        StrangerPawn = Worker.Spawn(5002, InStreet(door, 1, 0, -8), new ContainerRef(0), owner: ClaimOf("stranger")).NetId;
        DistantPawn = Worker.Spawn(5003, new Vector3(18, 0, 2), new ContainerRef(0), owner: ClaimOf("distant")).NetId;   // x = 50 in the street: beyond the near window's width
        InsidePawn = Worker.Spawn(5004, InRoom(door, 0, 0, 5), RoomRef(), owner: ClaimOf("inside")).NetId;
        // A crate and a pile of rubble in the room, one inside the far window and one beside it; a rock and a pebble in the street, likewise.
        Worker.Spawn(Crate, InRoom(door, 2, 1, 6), RoomRef());
        Worker.Spawn(Rubble, InRoom(door, 15, 1, 6), RoomRef());
        Worker.Spawn(Rock, InStreet(door, 2, 1, -6), new ContainerRef(0));
        Worker.Spawn(Pebble, new Vector3(8, 0, 2), new ContainerRef(0));   // x = 40 in the street, 30 m off the door's side
        Fleet.OnPump = () => Worker.PublishStates();
        Assert.That(Fleet.Run(() => Thrower.Replicas.Contains(Rock) && Thrower.Replicas.Contains(Pebble) && Inside.Replicas.Contains(Crate) && Inside.Replicas.Contains(Rubble) && Inside.Replicas.Contains(InsidePawn), seconds: 20), Is.True,
            "each client holds the scope it stands in");
    }

    public ulong IdOf(FakeClient client) => client.Welcome!.Value.ClientId;

    /// <summary>Publish the door and an Open link through it that the named clients may look through.</summary>
    public PortalLinkRecord OpenDoor(params FakeClient[] observers) => PublishLink(Door(), Room, PortalState.Open, observers);

    public PortalLinkRecord PublishLink(PortalRecord door, string scope, PortalState state, IEnumerable<FakeClient> observers, bool publishDoor = true)
    {
        var link = Link(door, scope, state, observers.Select(IdOf));
        if (publishDoor) Worker.PublishPortals(new[] { door });
        Worker.PublishPortalLinks(new[] { link });
        return link;
    }

    public void Dispose()
    {
        Fleet.Dispose();
        ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
