using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

[TestFixture]
public class ServiceTests
{
    private string directory;
    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-service-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
    }
    [TearDown] public void Cleanup() { Directory.Delete(directory, true); }
    private ServiceManifest Load(ServiceManifest m)
    {
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(m, ServiceManifest.Json));
        return ServiceManifest.Load(path);
    }
    [Test]
    public void ActualUnityWorldExportKeepsWireOrderAndAbsoluteCoordinates()
    {
        var m = ServiceManifest.Load(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "unity-world.json"));
        Assert.That(ContainerRegistry.IsGridded, Is.True);
        Assert.That(m.Containers.Select(c => c.ContainerId), Is.EqualTo(new[] { "z-first", "a-second" }));
        Assert.That(ContainerRef.Of(m.Containers[1]).Index, Is.EqualTo(1));
        Assert.That(m.Containers[1].ToWorld(Vector3.zero).x, Is.EqualTo(210));
    }
    // ------------------------------------------------------------------ container hints and the cost policy

    /// <summary>A row of equal boxes along x, so the Morton order of their centres is the row order.</summary>
    private List<string> HintRow(int count)
    {
        var containers = new List<Container>();
        for (int x = 0; x < count; x++)
            containers.Add(new Container { ContainerId = "c" + x, Index = (ushort)x, Size = new(64, 64, 64), transform = new ContainerFrame { position = new((x + 0.5f) * 64f, 0, 0) } });
        ServiceManifest.Load(WriteManifest(new ServiceManifest { Containers = containers }));
        return containers.Select(c => c.ContainerId).ToList();
    }

    private string WriteManifest(ServiceManifest m)
    {
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(m, ServiceManifest.Json));
        return path;
    }

    private static AssignmentInput HintInput(int workers, List<LeaseInfo> leases, Dictionary<string, ContainerHint>? hints = null) => new()
    {
        Baked = ContainerRegistry.All,
        Runtime = ContainerRegistry.Runtime,
        Eligible = Enumerable.Range(1, workers).Select(i => new WorkerInfo { WorkerId = "w" + i, WorkerIndex = (uint)i, Status = WorkerStatus.Ready }).ToList(),
        Leases = leases,
        Occupancy = new Dictionary<string, ContainerLoad>(),
        Hints = hints ?? new Dictionary<string, ContainerHint>(),
    };

    private static Dictionary<string, string> HintApply(List<LeaseInfo> leases, List<KeyValuePair<string, string>> changes)
    {
        var map = leases.ToDictionary(l => l.ContainerId, l => l.State == LeaseState.Active ? l.WorkerId : "");
        foreach (var c in changes) map[c.Key] = c.Value;
        return map;
    }

    private static List<LeaseInfo> Orphans(List<string> ids) =>
        ids.Select(id => new LeaseInfo { ContainerId = id, WorkerId = "", State = LeaseState.Orphaned, Epoch = 1 }).ToList();

    [Test]
    public void HintCostMultiplierMovesTheCut()
    {
        var ids = HintRow(4);
        var leases = Orphans(ids);
        var policy = new CostBalancedAssignmentPolicy();
        var plain = HintApply(leases, policy.Compute(HintInput(2, leases)));
        Assert.That(ids.Count(i => plain[i] == plain[ids[0]]), Is.EqualTo(2), "an unhinted row splits down the middle");

        var hints = new Dictionary<string, ContainerHint> { [ids[0]] = new() { CostMultiplier = 3f } };
        var weighted = HintApply(leases, policy.Compute(HintInput(2, leases, hints)));
        Assert.That(weighted[ids[0]], Is.Not.EqualTo(weighted[ids[1]]), "the heavy container takes a worker of its own");
        Assert.That(weighted[ids[1]], Is.EqualTo(weighted[ids[3]]));
    }

    [Test]
    public void HintDedicatedReservesAWorker()
    {
        var ids = HintRow(5);
        var leases = Orphans(ids);
        var policy = new CostBalancedAssignmentPolicy();
        var hints = new Dictionary<string, ContainerHint> { [ids[2]] = new() { Dedicated = true } };
        var map = HintApply(leases, policy.Compute(HintInput(3, leases, hints)));
        string hub = map[ids[2]];
        Assert.That(ids.Where(i => i != ids[2]).All(i => map[i] != hub), Is.True, "nothing shares the dedicated worker");
        Assert.That(ids.Where(i => i != ids[2]).Select(i => map[i]).Distinct().Count(), Is.EqualTo(2));
        Assert.That(policy.Note, Is.Empty);
    }

    [Test]
    public void HintDedicatedWithoutRoomIsReported()
    {
        var ids = HintRow(3);
        var leases = Orphans(ids);
        var policy = new CostBalancedAssignmentPolicy();
        var hints = new Dictionary<string, ContainerHint> { [ids[0]] = new() { Dedicated = true } };
        var map = HintApply(leases, policy.Compute(HintInput(1, leases, hints)));
        Assert.That(ids.Select(i => map[i]).Distinct().Count(), Is.EqualTo(1));
        Assert.That(policy.Note, Does.Contain("dedicated"));
    }

    [Test]
    public void HintAffinityGroupIsDealtAsOne()
    {
        var ids = HintRow(4);
        var leases = Orphans(ids);
        var policy = new CostBalancedAssignmentPolicy();
        var hints = new Dictionary<string, ContainerHint>
        {
            [ids[1]] = new() { AffinityGroup = "dungeon" },
            [ids[2]] = new() { AffinityGroup = "dungeon" },
        };
        var map = HintApply(leases, policy.Compute(HintInput(2, leases, hints)));
        Assert.That(map[ids[1]], Is.EqualTo(map[ids[2]]), "the group lands on one worker");
        Assert.That(ids.Select(i => map[i]).Distinct().Count(), Is.EqualTo(2), "both workers are used");
    }

    [Test]
    public void HintSeamCostMovesTheCutElsewhere()
    {
        var ids = HintRow(4);
        var leases = Orphans(ids);
        var policy = new CostBalancedAssignmentPolicy();
        var plain = HintApply(leases, policy.Compute(HintInput(2, leases)));
        Assert.That(plain[ids[1]], Is.Not.EqualTo(plain[ids[2]]), "without hints the cut falls in the middle");

        var hints = new Dictionary<string, ContainerHint>
        {
            [ids[1]] = new() { SeamCost = 1f },
            [ids[2]] = new() { SeamCost = 1f },
        };
        var hinted = HintApply(leases, policy.Compute(HintInput(2, leases, hints)));
        Assert.That(hinted[ids[1]], Is.EqualTo(hinted[ids[2]]), "the contested pair is not split");
        Assert.That(ids.Select(i => hinted[i]).Distinct().Count(), Is.EqualTo(2), "both workers still have work");
    }

    [Test]
    public void HintsSurviveTheServiceManifest()
    {
        var hint = new ContainerHint { CostMultiplier = 2f, AffinityGroup = "hub", SeamCost = 0.5f, Dedicated = true };
        var m = ServiceManifest.Load(WriteManifest(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "cell", Index = 0, Size = new(10, 10, 10), Hint = hint } },
        }));
        Assert.That(m.Containers[0].Hint, Is.EqualTo(hint), "the baked hint reaches the standalone orchestrator");
    }

    [Test]
    public void PredictWeighsHintedContainersTheSameWayComputeDoes()
    {
        var ids = HintRow(4);
        var leases = Orphans(ids);
        var hints = new Dictionary<string, ContainerHint> { [ids[0]] = new() { CostMultiplier = 4f } };
        var input = HintInput(2, leases, hints);
        input.Utilization = ids.ToDictionary(i => i, _ => 0.1f);
        var plan = new CostBalancedAssignmentPolicy().Predict(input, 2);
        Assert.That(AssignmentPlanner.UtilizationOf(input, ids[0]), Is.EqualTo(0.4f).Within(1e-4f));
        Assert.That(AssignmentPlanner.UtilizationOf(input, ids[1]), Is.EqualTo(0.1f).Within(1e-4f));
        Assert.That(plan.HeaviestContainer, Is.EqualTo(ids[0]));
    }

    [Test]
    public void RuntimeNeighborsAreAddedAndRemoved()
    {
        Load(new ServiceManifest { Containers = new() { new() { ContainerId = "cell", Index = 0, Size = new(10, 10, 10) } } });
        ContainerRegistry.SyncRuntime(new[] { new LeaseInfo { ContainerId = "rt_1", HasBounds = true, BoundsCenter = new(10, 0, 0), BoundsSize = new(10, 10, 10) } });
        Assert.That(ContainerRegistry.All[0].Neighbors.Count, Is.EqualTo(1));
        ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
        Assert.That(ContainerRegistry.All[0].Neighbors, Is.Empty);
        Assert.That(new Quaternion().normalized.w, Is.EqualTo(1));
    }
    [Test]
    public void ActualUnityExportLoadsAndMatchesUnityRotation()
    {
        var m = ServiceManifest.Load(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "unity-scene.json"));
        Assert.That(ContainerRegistry.IsGridded, Is.False);
        Assert.That(m.Config.InterestFarDivisor, Is.EqualTo(17));
        Assert.That(m.Containers.Select(c => c.ContainerId), Is.EqualTo(new[] { "a", "b" }));
        var frame = m.Containers[0].transform;
        Assert.That(MathF.Abs(Quaternion.Dot(frame.rotation, Quaternion.Euler(20, 70, 15))), Is.GreaterThan(0.999999f));
        Assert.That(m.Containers[0].ToLocal(m.Containers[0].ToWorld(new Vector3(1, 2, 3))).x, Is.EqualTo(1).Within(0.0001));
    }
    [Test]
    public void PersistenceTemplateUsesIndexAndFallsBackToNameAfterReordering()
    {
        Load(new ServiceManifest
        {
            Schemas = new() {
            new() {Name="prefab:0",DisplayName="Player",PrefabId=0},
            new() {Name="prefab:1",DisplayName="Crate",PrefabId=1},
            new() {Name="prefab:2",DisplayName="Player",PrefabId=2}
        }
        });
        Assert.That(ServiceManifest.TemplateOf(new PersistedEntityRecord { PrefabId = 2, PrefabName = "Player" }), Is.EqualTo("prefab:2"));
        Assert.That(ServiceManifest.TemplateOf(new PersistedEntityRecord { PrefabId = 0, PrefabName = "Crate" }), Is.EqualTo("prefab:1"));
        Assert.That(ServiceManifest.TemplateOf(new PersistedEntityRecord { PrefabId = 1, PrefabName = "" }), Is.EqualTo("prefab:1"));
    }
    [Test]
    public void ExportedEnumRetainsNamesAndWireValues()
    {
        Load(new ServiceManifest { Schemas = new() { new() { Name = "ExamplePlayer", Fields = new() { new() { Name = "Player.Mode", Type = "Example.Mode", EnumUnderlyingType = "System.Int32", EnumNames = new[] { "Idle", "Moving" }, EnumValues = new[] { "0", "7" } } } } } });
        var type = ServiceManifest.SchemaOf("ExamplePlayer")["Player.Mode"];
        Assert.That(Enum.GetNames(type), Is.EqualTo(new[] { "Idle", "Moving" }));
        var w = new NetworkWriter(); NetworkSerialization.WriteObject(w, type, Enum.Parse(type, "Moving"));
        Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 7, 0, 0, 0 }));
        Assert.That(ServiceManifest.SchemaOf("ExamplePlayer")["Player.Mode"], Is.SameAs(type));
    }
    [Test]
    public void ServiceLoopShutsDownOnCancellation()
    {
        Load(new ServiceManifest { Config = new NebulaConfig { GatewayPort = 0, UseLocalControlPlane = true } });
        CommandLine.Override(new Dictionary<string, string> { { "nebula-service-manifest", ServiceManifest.PathOnDisk }, { "nebula-local-control-plane", "true" } });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.That(ServiceHost.Run("gateway", cancellation.Token), Is.Zero);
    }
    [Test]
    public void ServicesDoNotReferenceUnityAssemblies()
    {
        Assert.That(typeof(NebulaGateway).BaseType, Is.EqualTo(typeof(object)));
        Assert.That(typeof(NebulaOrchestrator).BaseType, Is.EqualTo(typeof(object)));
        Assert.That(typeof(NebulaGateway).Assembly.GetReferencedAssemblies().Any(a => a.Name.StartsWith("Unity")), Is.False);
    }
    [Test]
    public void ManifestPreservesWireOrderAndContainerTransforms()
    {
        var m = Load(new ServiceManifest
        {
            Containers = new() {
            new Container {ContainerId="z-first",Index=0,Size=new(10,10,10),transform=new() {position=new(100,3,-10),rotation=Quaternion.Euler(0,90,0),lossyScale=new(2,3,4)}},
            new Container {ContainerId="a-second",Index=1,Size=new(10,10,10)}
        }
        });
        var c = ContainerRegistry.Resolve(new ContainerRef(0));
        Assert.That(c.ContainerId, Is.EqualTo("z-first"));
        var p = c.ToWorld(new Vector3(1, 2, 3));
        Assert.That(p.x, Is.EqualTo(112).Within(0.001));
        Assert.That(p.y, Is.EqualTo(9).Within(0.001));
        Assert.That(p.z, Is.EqualTo(-12).Within(0.001));
        Assert.That(c.ToLocal(p).x, Is.EqualTo(1).Within(0.001));
        Assert.That(c.ToLocal(p).y, Is.EqualTo(2).Within(0.001));
        Assert.That(c.ToLocal(p).z, Is.EqualTo(3).Within(0.001));
    }
    [Test]
    public void MatrixPreservesShearedContainerFrame()
    {
        var c = new Container { transform = new() { Matrix = new float[] { 2, 1, 0, 10, 0, 3, 0, 20, 0, 0, 4, 30, 0, 0, 0, 1 } } };
        var world = c.ToWorld(new Vector3(1, 2, 3));
        Assert.That(world.x, Is.EqualTo(14));
        var local = c.ToLocal(world);
        Assert.That(local.x, Is.EqualTo(1).Within(0.0001));
        Assert.That(local.y, Is.EqualTo(2).Within(0.0001));
        Assert.That(local.z, Is.EqualTo(3).Within(0.0001));
    }
    [Test]
    public void RejectsMismatchedWireIndices()
    {
        Assert.Throws<InvalidDataException>(() => Load(new ServiceManifest { Containers = new() { new Container { ContainerId = "room", Index = 7 } } }));
    }
    [Test]
    public void RuntimeLeaseLifecycleAndOwnership()
    {
        Load(new ServiceManifest());
        var leases = new List<LeaseInfo> { new() { ContainerId = "rt_42", HasBounds = true, BoundsCenter = new(20, 0, 0), BoundsSize = new(8, 8, 8) } };
        ContainerRegistry.SyncRuntime(leases);
        ContainerRegistry.ApplyLease("rt_42", "w3", 3, 9, LeaseState.Active);
        var c = ContainerRef.Runtime(42).Resolve();
        Assert.That(c.ToWorld(Vector3.zero).x, Is.EqualTo(20));
        Assert.That(c.OwnerWorkerId, Is.EqualTo("w3"));
        Assert.That(c.LeaseEpoch, Is.EqualTo(9));
        ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
        Assert.That(ContainerRef.Runtime(42).Resolve(), Is.Null);
    }
    [Test]
    public void ExportedPersistenceSchemaResolvesBuiltInTypes()
    {
        Load(new ServiceManifest { Schemas = new() { new() { Name = "ExamplePlayer", Fields = new() { new() { Name = "Player.Health", Type = "System.Int32" }, new() { Name = "Player.Position", Type = "UnityEngine.Vector3" }, new() { Name = "Player.Custom", Type = "Example.Custom" } } } } });
        var schema = ServiceManifest.SchemaOf("ExamplePlayer");
        Assert.That(schema["Player.Health"], Is.EqualTo(typeof(int)));
        Assert.That(schema["Player.Position"], Is.EqualTo(typeof(Vector3)));
        Assert.That(schema["Player.Custom"], Is.Null);
    }
    [Test]
    public void QuaternionAndHalfWireEncodingMatchesKnownValues()
    {
        var w = new NetworkWriter();
        w.WriteHalf(1); w.WriteHalf(-2); w.WriteQuaternion(Quaternion.identity);
        Assert.That(w.ToArray(), Is.EqualTo(new byte[] { 0, 60, 0, 192, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63 }));
        w.Reset(); w.WriteCompressedQuaternion(Quaternion.Euler(20, 70, 15));
        var q = new NetworkReader(w.ToArray()).ReadCompressedQuaternion();
        Assert.That(Math.Abs(Quaternion.Dot(q, Quaternion.Euler(20, 70, 15))), Is.GreaterThan(0.99999));
        var angles = q.eulerAngles;
        Assert.That(angles.x, Is.EqualTo(20).Within(0.2));
        Assert.That(angles.y, Is.EqualTo(70).Within(0.2));
        Assert.That(angles.z, Is.EqualTo(15).Within(0.2));
    }
    [Test]
    public void CommandLineOverridesExportedSettings()
    {
        CommandLine.Override(new Dictionary<string, string> { { "nebula-workers", "2" }, { "nebula-gateway", "example.test:7555" }, { "nebula-dashboard-port", "0" }, { "nebula-worker-exe", "worker.exe" } });
        var c = new NebulaConfig(); ServiceHost.ApplyOverrides(c);
        Assert.That(c.WorkerCount, Is.EqualTo(2)); Assert.That(c.GatewayPort, Is.EqualTo(7555)); Assert.That(c.DashboardPort, Is.Zero); Assert.That(c.WorkerExecutable, Is.EqualTo("worker.exe"));
    }
    [Test]
    public void OrchestratorServesEmbeddedDashboardAndStopsListener()
    {
        Load(new ServiceManifest());
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var c = new NebulaConfig { UseLocalControlPlane = true, WorkerCount = 0, OrchestratorSpawnsGateway = false, DashboardPort = (ushort)port };
        using var plane = new LocalControlPlane(); plane.Connect();
        var orch = new NebulaOrchestrator();
        try
        {
            orch.Initialize(c, plane); orch.Tick(); Thread.Sleep(1100); orch.Tick();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var html = http.GetStringAsync($"http://localhost:{port}/").GetAwaiter().GetResult();
            Assert.That(html, Does.Contain("<html"));
            var state = http.GetStringAsync($"http://localhost:{port}/api/state").GetAwaiter().GetResult();
            Assert.That(JsonDocument.Parse(state).RootElement.GetProperty("desiredWorkers").GetInt32(), Is.Zero);
        }
        finally { orch.Dispose(); }
        socket.Start(); socket.Stop();
    }
    [Test]
    public void TheDashboardShowsTheContainerTreeAndNothingDemotedIsDealt()
    {
        // docs/container-tree.md D6-D8: the services never hold a carrier, so a room fixed in one is known only by its row.
        // In a carrier without a physics frame it is simulated as inherited (D7) and must not be dealt; in a framed one a
        // leased room has a worker of its own, and an inherited room inside that follows it.
        Load(new ServiceManifest());
        using var plane = new LocalControlPlane(); plane.Connect();
        plane.EnsureContainer("hopper#9");
        plane.AssignContainer("hopper#9", "w1"); plane.SetLeaseState("hopper#9", LeaseState.Active);
        plane.EnsureContainer("liner#10", ContainerAuthority.Auto, ownPhysicsFrame: true);
        plane.AssignContainer("liner#10", "w1"); plane.SetLeaseState("liner#10", LeaseState.Active);
        plane.EnsureRuntimeContainer("rt_5", ContainerPlacement.Child("hopper#9", new Vector3(0, 1, 0), new Vector3(4, 3, 4)), "");
        plane.EnsureRuntimeContainer("rt_6", ContainerPlacement.Child("liner#10", new Vector3(0, 1, -8), new Vector3(6, 4, 6), ContainerAuthority.Leased), "");
        plane.EnsureRuntimeContainer("rt_7", ContainerPlacement.Child("rt_6", new Vector3(0, 0, 1), new Vector3(2, 2, 2), ContainerAuthority.Inherited), "");
        plane.AssignContainer("rt_6", "w2"); plane.SetLeaseState("rt_6", LeaseState.Active);
        ContainerRegistry.SyncRuntime(plane.Leases);

        var workers = new List<WorkerInfo> { new WorkerInfo { WorkerId = "w1", WorkerIndex = 1 }, new WorkerInfo { WorkerId = "w2", WorkerIndex = 2 } };
        var dealt = NebulaOrchestrator.ComputeRuntimeAssignment(plane.Leases, workers).Select(kv => kv.Key).ToList();
        Assert.That(dealt, Does.Not.Contain("rt_5"), "demoted: the carrier's worker simulates it");
        Assert.That(dealt, Does.Not.Contain("rt_7"), "inherited");

        var orch = new NebulaOrchestrator();
        try
        {
            orch.Initialize(new NebulaConfig { UseLocalControlPlane = true, WorkerCount = 0, OrchestratorSpawnsGateway = false, DashboardPort = 0 }, plane);
            var state = JsonDocument.Parse(orch.BuildStateJson()).RootElement;
            var rows = state.GetProperty("containers").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);

            var demoted = rows["rt_5"];
            Assert.That(demoted.GetProperty("mode").GetString(), Is.EqualTo("demoted"));
            Assert.That(demoted.GetProperty("leased").GetBoolean(), Is.False);
            Assert.That(demoted.GetProperty("worker").GetString(), Is.EqualTo("w1"), "simulated by the carrier's worker");
            Assert.That(demoted.GetProperty("ownerFrom").GetString(), Is.EqualTo("hopper#9"));
            Assert.That(demoted.GetProperty("parent").GetString(), Is.EqualTo("hopper#9"));
            Assert.That(demoted.GetProperty("depth").GetInt32(), Is.EqualTo(1));

            var engine = rows["rt_6"];
            Assert.That(engine.GetProperty("mode").GetString(), Is.EqualTo("leased"));
            Assert.That(engine.GetProperty("authority").GetString(), Is.EqualTo("leased"));
            Assert.That(engine.GetProperty("worker").GetString(), Is.EqualTo("w2"));
            Assert.That(engine.GetProperty("ownerFrom").GetString(), Is.Empty);

            var closet = rows["rt_7"];
            Assert.That(closet.GetProperty("mode").GetString(), Is.EqualTo("inherited"));
            Assert.That(closet.GetProperty("worker").GetString(), Is.EqualTo("w2"), "follows the engine room, not the ship");
            Assert.That(closet.GetProperty("ownerFrom").GetString(), Is.EqualTo("rt_6"));
            Assert.That(closet.GetProperty("depth").GetInt32(), Is.EqualTo(2));

            var carried = state.GetProperty("carried").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);
            Assert.That(carried["liner#10"].GetProperty("frame").GetBoolean(), Is.True);
            Assert.That(carried["hopper#9"].GetProperty("frame").GetBoolean(), Is.False);

            // The map places nothing it cannot: the rooms fixed in ships come from the workers' telemetry.
            var geometry = JsonDocument.Parse(MeshTelemetry.BuildGeometryJson(null)).RootElement;
            Assert.That(geometry.GetProperty("containers").EnumerateArray().Select(c => c.GetProperty("id").GetString()), Is.Empty);
        }
        finally { orch.Dispose(); }
    }
    [Test]
    public void ResumingAParkedWorkerAtTheCeilingIsRefusedInsteadOfRetiringItAgain()
    {
        // UnparkWorker raised the desired count and then clamped it, so at MaxWorkers the resumed worker came back
        // only to be picked as the surplus one on the very next pass. The ceiling is now checked first, and the
        // dashboard gets the same 409 POST /api/workers/add answers.
        Load(new ServiceManifest());
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var c = new NebulaConfig { UseLocalControlPlane = true, WorkerCount = 1, MinWorkers = 1, MaxWorkers = 1, OrchestratorSpawnsGateway = false, DashboardPort = (ushort)port };
        using var plane = new LocalControlPlane(); plane.Connect();
        var orch = new NebulaOrchestrator();
        try
        {
            orch.Initialize(c, plane); orch.Tick(); Thread.Sleep(1100); orch.Tick();
            Assert.That(orch.DesiredWorkers, Is.EqualTo(1));
            Assert.That(orch.AtWorkerCeiling, Is.True);
            Assert.That(orch.UnparkWorker(null), Is.False, "nothing may be resumed into a full mesh");
            Assert.That(orch.DesiredWorkers, Is.EqualTo(1), "and the desired count is untouched");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // POSTs are queued for the orchestrator's own thread, so keep ticking while the request is in flight.
            int Post(string path)
            {
                var call = http.PostAsync($"http://localhost:{port}{path}", new StringContent("{}"));
                var deadline = Stopwatch.StartNew();
                while (!call.IsCompleted && deadline.Elapsed.TotalSeconds < 5) { orch.Tick(); Thread.Sleep(5); }
                return (int)call.GetAwaiter().GetResult().StatusCode;
            }
            Assert.That(Post("/api/workers/unpark"), Is.EqualTo(409));
            Assert.That(Post("/api/workers/add"), Is.EqualTo(409), "the two paths answer alike");
        }
        finally { orch.Dispose(); }
        socket.Start(); socket.Stop();
    }
    [Test]
    public void GatewayAcceptsClientHelloOverRealUdp()
    {
        Load(new ServiceManifest());
        using var plane = new LocalControlPlane(); plane.Connect();
        var gateway = new NebulaGateway();
        using var client = new LiteNetTransport("test-client");
        using var reserve = new UdpClient(0);
        int port = ((IPEndPoint)reserve.Client.LocalEndPoint).Port; reserve.Close();
        bool welcomed = false;
        try
        {
            gateway.Initialize(new NebulaConfig { GatewayPort = (ushort)port }, plane);
            client.Connect("127.0.0.1", port);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < 5 && !welcomed)
            {
                gateway.Tick();
                client.Poll(e =>
                {
                    if (e.Type == TransportEvent.Kind.Connected)
                    {
                        var w = new NetworkWriter();
                        new HelloMsg { Role = PeerRole.Client, Id = "test-client" }.Write(w);
                        client.Send(e.PeerId, Delivery.ReliableOrdered, w.ToSegment());
                    }
                    if (e.Type == TransportEvent.Kind.Data)
                    {
                        var r = new NetworkReader(e.Data);
                        if ((MsgId)r.ReadByte() == MsgId.Welcome) welcomed = true;
                    }
                });
                Thread.Sleep(5);
            }
            Assert.That(welcomed, Is.True);
            Assert.That(gateway.ClientCount, Is.EqualTo(1));
        }
        finally { gateway.Dispose(); }
        using var rebound = new UdpClient(port);
    }

    /// <summary>
    /// Scale to zero: with no worker registered there is nowhere to spawn, so the gateway holds the join in
    /// JoinState.Starting (with the host's boot estimate) instead of leaving the client welcomed and silent, counts
    /// it as pending, and reports that on its control-plane heartbeat so the orchestrator wakes the mesh.
    /// </summary>
    [Test]
    public void GatewayHoldsTheJoinWhenNoWorkerIsRunning()
    {
        Load(new ServiceManifest());
        using var plane = new LocalControlPlane(); plane.Connect();
        plane.SetSetting(MeshSettings.BootSeconds, "75");
        var gateway = new NebulaGateway();
        using var client = new LiteNetTransport("test-client");
        using var reserve = new UdpClient(0);
        int port = ((IPEndPoint)reserve.Client.LocalEndPoint).Port; reserve.Close();
        var status = new JoinStatusMsg { State = JoinState.None };
        try
        {
            gateway.Initialize(new NebulaConfig { GatewayPort = (ushort)port, WorkerHeartbeatSeconds = 0.05f }, plane);
            client.Connect("127.0.0.1", port);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < 5 && (status.State != JoinState.Starting || plane.Gateways.Count == 0 || plane.Gateways[0].PendingJoins == 0))
            {
                gateway.Tick();
                client.Poll(e =>
                {
                    if (e.Type == TransportEvent.Kind.Connected)
                    {
                        var w = new NetworkWriter();
                        new HelloMsg { Role = PeerRole.Client, Id = "test-client" }.Write(w);
                        client.Send(e.PeerId, Delivery.ReliableOrdered, w.ToSegment());
                    }
                    if (e.Type == TransportEvent.Kind.Data)
                    {
                        var r = new NetworkReader(e.Data);
                        if ((MsgId)r.ReadByte() == MsgId.JoinStatus) status = JoinStatusMsg.Read(r);
                    }
                });
                Thread.Sleep(5);
            }
            Assert.That(status.State, Is.EqualTo(JoinState.Starting), "the client is held, not dropped");
            Assert.That(status.EstimatedSeconds, Is.EqualTo(75), "and told roughly how long this host takes to boot a worker");
            Assert.That(gateway.PendingJoinCount, Is.EqualTo(1));
            Assert.That(plane.Gateways[0].PendingJoins, Is.EqualTo(1u), "the orchestrator sees the demand on the heartbeat");
        }
        finally { gateway.Dispose(); }
        using var rebound2 = new UdpClient(port);
    }
}
