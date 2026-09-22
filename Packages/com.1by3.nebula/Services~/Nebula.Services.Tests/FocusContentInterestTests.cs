using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// What a client is given so it can <b>build</b> the world it is looking at, as opposed to what it is given so
/// it can see the things in it (design §8, D60). The two are not the same question for a strategy game: its
/// camera flies to a front the pawn is nowhere near, and the chunks under it are mostly empty. An empty chunk
/// is named by no spawn, so nothing but its own container row can tell the client it exists — and until this
/// pass the rows were scoped to the pawn's window alone, which is exactly where the camera is not.
/// <para>
/// Every test here asks one of three things. Does a container row reach the client for every focus the server
/// authorized, including the empty ground under a remote camera? Does it reach it in the one order the client
/// can act on — rows before the spawns that name them, removals only after the entities are gone? And is it
/// still bounded and still isolated: nothing for a focus nobody authorized, nothing from another instance, and
/// never more rows in one evaluation than the mesh is sized for.
/// </para>
/// </summary>
[TestFixture]
public class FocusContentInterestTests
{
    private const float Cell = 64f;
    private string _directory = "";

    /// <summary>A line of 64 m cell containers long enough that a camera can be somewhere the pawn is not.</summary>
    private void LoadLine(int cells)
    {
        var containers = new List<Container>();
        for (int i = 0; i < cells; i++)
            containers.Add(new Container
            {
                ContainerId = "c" + i, Index = (ushort)i, Size = new Vector3(Cell, Cell, Cell),
                transform = new ContainerFrame { position = new Vector3(i * Cell + Cell / 2, 0, 0) },
            });
        Write(containers);
    }

    /// <summary>A square of cells: the shape in which a box focus can cover more ground than a budget allows.</summary>
    private void LoadGrid(int side)
    {
        var containers = new List<Container>();
        for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
                containers.Add(new Container
                {
                    ContainerId = $"c{x}_{z}", Index = (ushort)(z * side + x), Size = new Vector3(Cell, Cell, Cell),
                    transform = new ContainerFrame { position = new Vector3(x * Cell + Cell / 2, 0, z * Cell + Cell / 2) },
                });
        Write(containers);
    }

    private void Write(List<Container> containers)
    {
        var path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest { Containers = containers }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-focus-content-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
    }

    [TearDown]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    /// <summary>One worker owning the whole world, so nothing here is about who owns what.</summary>
    private static Fleet OneWorker(IEnumerable<string> containerIds, Action<NebulaConfig>? configure = null) =>
        new(1, world: f => { foreach (string id in containerIds) f.Assign(id, "w1"); }, configure: configure);

    private static IEnumerable<string> Line(int cells) { for (int i = 0; i < cells; i++) yield return "c" + i; }
    private static IEnumerable<string> Grid(int side)
    {
        for (int z = 0; z < side; z++) for (int x = 0; x < side; x++) yield return $"c{x}_{z}";
    }

    private static FakeClient Join(Fleet fleet, string name, Vector3 pawnAt = default)
    {
        fleet.Worker.PawnPlacement = _ => pawnAt;
        var client = fleet.Connect(0, name);
        Assert.That(fleet.Run(() => client.Join == JoinState.Joined), Is.True, $"{name} should get a pawn");
        return client;
    }

    /// <summary>Point a client's camera at a place until the mesh has had time to answer.</summary>
    private static void Watch(Fleet fleet, FakeClient client, double x, int steps = 16)
    {
        for (int i = 0; i < steps; i++) { client.SendFocusHint(x, 0, 0); fleet.RunFor(0.25); }
    }

    /// <summary>A policy that adds whatever a test wants, on top of the pawn and the client's accepted hint.</summary>
    private sealed class CameraPolicy : IInterestPolicy
    {
        public readonly List<InterestFocus> Extra = new();
        public bool DropPawnFocus;

        public void Collect(in InterestClient client, InterestQuery query)
        {
            if (client.HasPawn && !DropPawnFocus) query.AddFocus(client.PawnX, client.PawnY, client.PawnZ, 1f, client.PawnNetId);
            if (client.HasHint) query.AddFocus(client.HintX, client.HintY, client.HintZ);
            for (int i = 0; i < Extra.Count; i++) query.AddFocus(Extra[i]);
        }

        public bool Authorize(in InterestClient client, in InterestEntity entity) => true;
    }

    // ------------------------------------------------------------------------------------------- empty ground

    [Test]
    public void AFreeCameraIsSentTheEmptyChunksItIsLookingAtAndNotTheOnesInBetween()
    {
        LoadLine(24);
        using var fleet = OneWorker(Line(24));
        var client = Join(fleet, "com");
        fleet.RunFor(0.5);
        Assert.That(client.Containers, Does.Not.Contain("c20"), "nothing has asked for the far end yet");

        // The server — not the client — makes this one a commander camera, and it looks 1.3 km away at ground
        // that holds no entity at all. Scoped to the pawn, the client would be told about none of it.
        fleet.Gateways[0].SetClientFocusMode(client.Welcome!.Value.ClientId, FocusMode.Free);
        Watch(fleet, client, 20 * Cell + Cell / 2);

        Assert.That(client.Containers, Does.Contain("c20"), "the chunk under the camera, with nothing in it to name it");
        Assert.That(client.Containers, Does.Contain("c19"), "and its neighbours, out to the same NearCells window a pawn gets");
        Assert.That(client.Containers, Does.Contain("c0"), "while the pawn keeps its own window");
        Assert.That(client.Containers, Does.Not.Contain("c10"), "and the world between them is still not a lease table broadcast");
    }

    [Test]
    public void AFreeCameraGetsBothTheEmptyGroundAndTheEntitiesStandingOnIt()
    {
        LoadLine(24);
        using var fleet = OneWorker(Line(24));
        var client = Join(fleet, "com");
        // One entity at the far end, and the chunk beside it empty: the camera must get both kinds of answer.
        fleet.Worker.Spawn(7100, new Vector3(0, 0, 0), new ContainerRef(20));
        fleet.Gateways[0].SetClientFocusMode(client.Welcome!.Value.ClientId, FocusMode.Free);
        Watch(fleet, client, 20 * Cell + Cell / 2);

        Assert.That(client.Replicas, Does.Contain(7100ul), "the entity under the camera arrives");
        Assert.That(client.Containers, Does.Contain("c20"), "so does the chunk it stands in");
        Assert.That(client.Containers, Does.Contain("c21"), "and the empty chunk next to it, which no spawn would ever name");
    }

    [Test]
    public void TheCameraTakesTheWindowWithItAndGivesTheOldGroundBack()
    {
        LoadLine(24);
        using var fleet = OneWorker(Line(24));
        var client = Join(fleet, "com");
        fleet.Gateways[0].SetClientFocusMode(client.Welcome!.Value.ClientId, FocusMode.Free);
        Watch(fleet, client, 20 * Cell + Cell / 2);
        Assert.That(client.Containers, Does.Contain("c20"));

        // Pan to the other end. The window follows, and the ground nobody is looking at any more is given back
        // rather than accumulating for the rest of the session.
        Watch(fleet, client, 12 * Cell + Cell / 2);
        Assert.That(client.Containers, Does.Contain("c12"), "the new ground");
        Assert.That(fleet.Run(() => !client.Containers.Contains("c20"), seconds: 6), Is.True, "and the old ground is taken back");
        Assert.That(client.Containers, Does.Contain("c0"), "the pawn's own window is never taken away");
    }

    [Test]
    public void PolicyPointAndBoxFociGetTheSameWindowAndOverlappingOnesAreSentOnce()
    {
        LoadLine(24);
        var policy = new CameraPolicy();
        using var fleet = OneWorker(Line(24));
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        // An RTS selection at one place and a district box at another, overlapping in the middle: the kind of
        // thing a commander's HUD asks for, and nothing to do with where the pawn is standing.
        policy.Extra.Add(InterestFocus.Point(8 * Cell + Cell / 2, 0, 0));
        policy.Extra.Add(InterestFocus.Box(12 * Cell + Cell / 2, 0, 0, 128, 64, 64));
        fleet.Gateways[0].MarkInterestDirty(client.Welcome!.Value.ClientId);
        fleet.RunFor(1.5);

        Assert.That(client.Containers, Does.Contain("c8"), "a policy point focus gets a content window of its own");
        Assert.That(client.Containers, Does.Contain("c12"), "and so does a box focus");
        Assert.That(client.Containers, Does.Contain("c10"), "including the empty ground the two windows share");
        Assert.That(client.Containers, Does.Not.Contain("c20"), "and still nothing beyond any of them");
        Assert.That(client.DuplicateContainerRows, Is.Zero,
            "a container two foci both want is one row, not two: the windows are deduplicated");
    }

    // ------------------------------------------------------------------------------------------- ordering

    [Test]
    public void ARowArrivesBeforeTheSpawnItNamesAndIsTakenBackOnlyAfterTheDespawn()
    {
        LoadLine(24);
        using var fleet = OneWorker(Line(24));
        var client = Join(fleet, "com");
        fleet.Worker.Spawn(7200, new Vector3(0, 0, 0), new ContainerRef(20));
        fleet.Gateways[0].SetClientFocusMode(client.Welcome!.Value.ClientId, FocusMode.Free);
        Watch(fleet, client, 20 * Cell + Cell / 2);
        Assert.That(client.Replicas, Does.Contain(7200ul));
        Assert.That(client.Wire.IndexOf("container+ c20"), Is.GreaterThanOrEqualTo(0));
        Assert.That(client.Wire.IndexOf("container+ c20"), Is.LessThan(client.Wire.IndexOf("spawn 7200")),
            "the client cannot place a replica in a container it has not been given");

        // Look away again: the entity goes, and only then may the row it stood in be taken back.
        client.ClearFocusHint(1);
        Assert.That(fleet.Run(() => !client.Containers.Contains("c20"), seconds: 10), Is.True);
        Assert.That(client.Wire.LastIndexOf("despawn 7200"), Is.LessThan(client.Wire.LastIndexOf("container- c20")),
            "a row is taken back only once nothing the client still holds lives in it");
    }

    [Test]
    public void TheRowsEntitiesStandInAreNeverWithheldByTheWindowBudget()
    {
        // The budget applies to the windows and never to the rows a spawn names: an entity in the set always
        // has its container. Proved on the shape that exhausts the budget — a box focus over a whole district.
        LoadGrid(12);
        var policy = new CameraPolicy();
        using var fleet = OneWorker(Grid(12), configure: c => c.InterestMaxFoci = 1);
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        policy.DropPawnFocus = true;
        policy.Extra.Add(InterestFocus.Box(6 * Cell, 0, 6 * Cell, 12 * Cell, 64, 12 * Cell));
        fleet.Gateways[0].MarkInterestDirty(client.Welcome!.Value.ClientId);
        fleet.Worker.Spawn(7300, new Vector3(0, 0, 0), new ContainerRef((ushort)(6 * 12 + 6)));
        Assert.That(fleet.Run(() => client.Replicas.Contains(7300), seconds: 8), Is.True);

        Assert.That(client.Containers, Does.Contain("c6_6"), "the row the spawn names, whatever the budget did to the window");
        Assert.That(fleet.Gateways[0].ContainerBudgetHits, Is.GreaterThan(0), "and the budget really was reached");
        Assert.That(client.Containers.Count, Is.LessThanOrEqualTo(fleet.Gateways[0].ContainerRowCap + 4),
            "so the cap holds: a client is not sent the lease table of a district because a policy drew a box round it");
        Assert.That(fleet.Gateways[0].ContainerRowCap, Is.LessThan(144), "the world is bigger than one evaluation may send");
    }

    // ------------------------------------------------------------------------------------------- boundaries

    [Test]
    public void AHintNobodyAuthorizedBuysNoGroundAtAll()
    {
        LoadLine(24);
        using var fleet = OneWorker(Line(24));
        var client = Join(fleet, "ann");
        // The default mode. However hard it asks, the hint is worth InterestHintMaxDistance of the pawn, and
        // the ground it points at is not the client's to have.
        Watch(fleet, client, 20 * Cell + Cell / 2);
        Assert.That(client.Containers, Does.Not.Contain("c20"), "a clamped hint moves the window no further than it moves the set");
        Assert.That(client.Containers, Does.Contain("c0"));
    }

    [Test]
    public void APawnlessClientCannotScrapeTheMapByHinting()
    {
        LoadLine(24);
        using var fleet = OneWorker(Line(24));
        fleet.Worker.DeferSpawns = true;
        var client = fleet.Connect(0, "watcher");
        Assert.That(fleet.Run(() => client.Welcome != null), Is.True);

        Watch(fleet, client, 20 * Cell + Cell / 2);
        Assert.That(client.Containers, Is.Empty, "no pawn and no authorized focus is no window: not even one chunk");

        // And with the server's say-so it is a working spectator, ground included.
        fleet.Gateways[0].SetClientFocusMode(client.Welcome!.Value.ClientId, FocusMode.Free);
        Watch(fleet, client, 20 * Cell + Cell / 2);
        Assert.That(client.Containers, Does.Contain("c20"), "a spectator is a decision the server made, and then it gets the ground");
    }

    [Test]
    public void AFreeCameraIsNeverSentAnotherInstancesContainer()
    {
        LoadLine(24);
        using var fleet = OneWorker(Line(24));
        var client = Join(fleet, "com");
        // A private instance sitting exactly where the camera is pointed. Its entities are already isolated;
        // its lease row must be too, or the map it draws shows somebody else's dungeon.
        fleet.Plane.EnsureRuntimeContainer("rt_55", new Bounds(new Vector3(20 * Cell + Cell / 2, 0, 0), new Vector3(40, 40, 40)),
            "w1", new InstanceContainerInfo { InstanceId = 55 });
        fleet.RunFor(0.5);
        fleet.Worker.Spawn(7400, Vector3.zero, ContainerRef.Runtime(55));

        fleet.Gateways[0].SetClientFocusMode(client.Welcome!.Value.ClientId, FocusMode.Free);
        Watch(fleet, client, 20 * Cell + Cell / 2);

        Assert.That(client.Containers, Does.Contain("c20"), "the public ground under the camera is the client's to have");
        Assert.That(client.Containers, Does.Not.Contain("rt_55"), "and the instance standing in it is not");
        Assert.That(client.Replicas, Does.Not.Contain(7400ul));
    }

    [Test]
    public void AnUnknownContainerIsNeverSentHoweverHardAFocusPointsAtIt()
    {
        // A lease row the gateway does not hold is not a row it may invent: a focus over a chunk the control
        // plane has never heard of gets nothing, rather than an id the client would then try to resolve.
        LoadLine(24);
        var policy = new CameraPolicy();
        using var fleet = OneWorker(Line(4));
        fleet.Gateways[0].InterestPolicy = policy;
        var client = Join(fleet, "ann");
        policy.Extra.Add(InterestFocus.Point(20 * Cell + Cell / 2, 0, 0));
        fleet.Gateways[0].MarkInterestDirty(client.Welcome!.Value.ClientId);
        fleet.RunFor(1.5);

        Assert.That(client.Containers, Does.Not.Contain("c20"), "c20 exists in the manifest but nobody leases it");
        foreach (string id in client.Containers) Assert.That(fleet.ContainerIds, Does.Contain(id), "only leased rows are ever sent");
    }

    [Test]
    public void AnEnormousBoxFocusIsShrunkRatherThanWalkedCellByCell()
    {
        // A policy may ask for a box of any size; nothing downstream may then enumerate it. InterestMaxRadius
        // is the mesh's answer to "how far may one thing reach", so the box is shrunk to it about its centre
        // and the client is still served — which is the difference between a clamp and a hang.
        var settings = InterestSettings.Default;
        settings.MaxRadius = 500f;
        var query = new InterestQuery();
        query.Reset(settings);
        query.AddFocus(InterestFocus.Box(0, 0, 0, 1_000_000, 1_000_000, 1_000_000));

        Assert.That(query.BoxesClamped, Is.True);
        Assert.That(query.Foci[0].HalfX, Is.EqualTo(500).Within(1e-6));
        Assert.That(query.Foci[0].HalfZ, Is.EqualTo(500).Within(1e-6));
        Assert.That(query.Foci[0].X, Is.Zero, "shrunk about its centre, so it still covers what it was aimed at");

        query.Reset(settings);
        query.AddFocus(InterestFocus.Box(0, 0, 0, 100, 100, 100));
        Assert.That(query.BoxesClamped, Is.False, "and an ordinary box is left exactly as the policy asked for it");
    }

    [Test]
    public void TheContainerRowCapIsDerivedFromTheWindowAndTheFociAClientMayHold()
    {
        var settings = InterestSettings.Default;
        // 2 x NearCells + 1 = 7 cells on a side, planar, 8 foci.
        Assert.That(settings.NearCells(Cell), Is.EqualTo(3));
        Assert.That(settings.MaxContainerRows(Cell), Is.EqualTo(7 * 7 * 8));

        settings.MaxFoci = 1;
        Assert.That(settings.MaxContainerRows(Cell), Is.EqualTo(64), "with a floor, so a small world still gets a usable window");

        settings.Planar = false;
        settings.MaxFoci = 8;
        Assert.That(settings.MaxContainerRows(Cell), Is.EqualTo(7 * 7 * 7 * 8), "a world with height stacks windows too");

        settings.ClientLoadRadiusCells = 20;
        Assert.That(settings.MaxContainerRows(Cell), Is.EqualTo(4096), "with a ceiling, so a huge window is not a lease-table broadcast");
    }
}
