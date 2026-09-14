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
}
