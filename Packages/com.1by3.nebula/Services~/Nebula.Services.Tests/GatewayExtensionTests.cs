using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The gateway extension point, exercised through the <b>real</b> standalone gateway: <see cref="ServiceHost"/>
/// on its own thread, reading a service manifest off disk, talking to a hosted control plane over HTTP and
/// loading the sample extension assembly from a file name in that manifest — the same sequence
/// <c>nebula start</c> and a deployed mesh go through. Nothing here constructs a <see cref="NebulaGateway"/>,
/// because what is under test is precisely the part a game cannot reach when it does not own the process.
/// </summary>
[TestFixture]
public class GatewayExtensionTests
{
    private string _directory = "";

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-gwext-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
    }

    [TearDown]
    public void Cleanup()
    {
        CommandLine.Override(new Dictionary<string, string>());
        try { Directory.Delete(_directory, true); } catch { }
    }

    /// <summary>The sample extension as it sits beside a published gateway: one file, next to the running process.</summary>
    private static string SampleAssembly => "Nebula.SampleExtension.dll";

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var reserve = new UdpClient(0);
        return ((IPEndPoint)reserve.Client.LocalEndPoint!).Port;
    }

    // ------------------------------------------------------------------------------------------- harness

    /// <summary>
    /// A standalone gateway process, in this process: a control plane hosted over HTTP (the shape every real
    /// gateway uses — <c>RemoteControlPlane</c>, not the in-process one), one subscription-aware worker, and
    /// <see cref="ServiceHost.Run"/> on a thread of its own. The test thread pumps the control plane, the
    /// worker and the clients; the gateway runs its own loop, exactly as it does in production.
    /// </summary>
    private sealed class Standalone : IDisposable
    {
        public readonly OrchestratorHttpServer Http;
        public readonly ControlPlaneHost Plane;
        public readonly FakeWorker Worker;
        public readonly NebulaConfig Config;
        public readonly List<FakeClient> Clients = new();
        public int ExitCode = int.MinValue;

        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;

        public Standalone(string directory, Action<NebulaConfig> configure)
        {
            int planePort = FreeTcpPort();
            Http = new OrchestratorHttpServer("localhost", (ushort)planePort, "<html></html>");
            Plane = new ControlPlaneHost(new MemoryControlPlaneStorage(), null, restore: false);
            Plane.Attach(Http);
            Http.Start();
            Plane.Connect();

            Worker = new FakeWorker("", "w1", 1);
            Plane.RegisterWorker(Worker.WorkerId, 1, "127.0.0.1", (ushort)Worker.Port);
            Plane.HeartbeatWorker(Worker.WorkerId, WorkerStatus.Ready, new WorkerStats());

            // A line of 64 m containers, all on the one worker: the world the interest tests use.
            var containers = new List<Container>();
            for (int i = 0; i < 4; i++)
                containers.Add(new Container
                {
                    ContainerId = "c" + i, Index = (ushort)i, Size = new Vector3(64, 64, 64),
                    transform = new ContainerFrame { position = new Vector3(i * 64 + 32, 0, 0) },
                });
            foreach (var c in containers) { Plane.EnsureContainer(c.ContainerId); Plane.AssignContainer(c.ContainerId, Worker.WorkerId); }

            Config = new NebulaConfig
            {
                GatewayPort = (ushort)FreeUdpPort(),
                GatewayAddress = "127.0.0.1",
                WebClients = false,
                AuthSigningKey = "extension-tests",
                ControlPlaneUrl = $"http://localhost:{planePort}/",
                UseLocalControlPlane = false,
                GatewayExtension = SampleAssembly,
            };
            configure(Config);

            string manifest = Path.Combine(directory, "nebula-services.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new ServiceManifest { Containers = containers, Config = Config }, ServiceManifest.Json));
            ServiceManifest.Load(manifest);
            Worker.Grid = Config.ToInterestGrid();
            Worker.Settings = Config.ToInterestSettings();

            CommandLine.Override(new Dictionary<string, string> { { "nebula-service-manifest", manifest } });
            _thread = new Thread(() => ExitCode = ServiceHost.Run("gateway", _stop.Token)) { IsBackground = true, Name = "standalone-gateway" };
            _thread.Start();
        }

        /// <summary>One pass over everything the test owns. The gateway is not pumped here: it has its own loop.</summary>
        public void Pump()
        {
            Http.Pump(req => Plane.TryHandle(req, out var r) ? r : OrchestratorHttpServer.Response.Error(404, "no"));
            Plane.Tick();
            Plane.HeartbeatWorker(Worker.WorkerId, WorkerStatus.Ready, new WorkerStats());
            Worker.Poll();
            foreach (var c in Clients) c.Poll();
        }

        public bool Run(Func<bool> until, double seconds = 15)
        {
            var clock = Stopwatch.StartNew();
            bool ok = false;
            while (clock.Elapsed.TotalSeconds < seconds && !(ok = until())) { Pump(); Thread.Sleep(2); }
            return ok;
        }

        public void RunFor(double seconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds) { Pump(); Thread.Sleep(2); }
        }

        public FakeClient Connect(string name)
        {
            var client = new FakeClient(Config.GatewayPort, name);
            Clients.Add(client);
            return client;
        }

        /// <summary>The gateway's own heartbeat as the control plane received it: what an operator would see.</summary>
        public GatewayStats Stats => Plane.Gateways.Count > 0 ? Plane.Gateways[0].Stats : default;

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join(5000);
            foreach (var c in Clients) c.Dispose();
            Worker.Dispose();
            Plane.Dispose();
            Http.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------- the whole path

    [Test]
    public void AStandaloneGatewayLoadsTheConfiguredExtensionAndItsPolicyDecidesWhatClientsSee()
    {
        string fog = Path.Combine(_directory, "fog.txt");
        string events = Path.Combine(_directory, "events.txt");
        using var mesh = new Standalone(_directory, c => c.GatewayExtensionOptions = $"teams=2;fog-file={fog};events-file={events}");

        // Three things standing next to where the pawn will be: a neutral prop, one unit of each team.
        mesh.Worker.PawnPlacement = _ => Vector3.zero;
        mesh.Worker.Spawn(300, new Vector3(10, 0, 0), group: 0);
        mesh.Worker.Spawn(400, new Vector3(12, 0, 0), group: 1);
        mesh.Worker.Spawn(500, new Vector3(14, 0, 0), group: 2);

        var red = mesh.Connect("red1");
        Assert.That(mesh.Run(() => red.Join == JoinState.Joined), Is.True, "the standalone gateway should welcome the client and give it a pawn");
        Assert.That(mesh.Run(() => red.Replicas.Contains(300) && red.Replicas.Contains(400)), Is.True,
            "scenery and its own team's unit reach the client");
        mesh.RunFor(1.5);

        // The whole point: a policy that only exists inside a game assembly decided this.
        Assert.That(red.Replicas, Does.Not.Contain(500ul), "the other team's unit is refused by the extension's policy and never spawns on the client");
        Assert.That(red.HeardAbout, Does.Not.Contain(500ul), "and nothing about it arrives on any other channel either");

        // ClientJoined ran on the gateway loop, before the first evaluation, and set the team the policy read.
        Assert.That(File.Exists(events), Is.True, "the extension's ClientJoined handler ran");
        Assert.That(File.ReadAllText(events), Does.Contain("red1 team=1"));

        // A fog-of-war reveal computed off the gateway loop, marshalled back with Post: the entity the policy
        // was hiding appears, without the client having asked for anything.
        File.WriteAllText(fog, "1:500\n");
        Assert.That(mesh.Run(() => red.Replicas.Contains(500), 8), Is.True,
            "marking interest dirty after a fog change reveals the hidden unit promptly");

        // And the reveal is a filter, not a switch: take it away again and the entity goes.
        File.WriteAllText(fog, "1:\n");
        Assert.That(mesh.Run(() => !red.Replicas.Contains(500), 8), Is.True, "withdrawing the reveal despawns it again");

        Assert.That(mesh.Stats.ExtensionErrors, Is.Zero, "a healthy extension reports no errors in the heartbeat");

        red.Disconnect();
        Assert.That(mesh.Run(() => File.ReadAllText(events).Contains("leave")), Is.True, "ClientLeft reaches the extension when the client goes");
    }

    [Test]
    public void AThrowingPolicyDeniesEverythingAndTheGatewayStaysUp()
    {
        using var mesh = new Standalone(_directory, c => c.GatewayExtensionOptions = "fail-authorize=true");
        mesh.Worker.PawnPlacement = _ => Vector3.zero;
        mesh.Worker.Spawn(300, new Vector3(10, 0, 0), group: 0);

        var client = mesh.Connect("red1");
        Assert.That(mesh.Run(() => client.Welcome != null), Is.True, "the gateway still accepts clients");
        mesh.RunFor(2.0);

        // Fail closed: Authorize threw, so nothing is authorized - not the scenery, not even the client's own
        // pawn. A policy that cannot answer must hide the world rather than show it.
        Assert.That(client.Replicas, Is.Empty, "a throwing Authorize denies rather than allows");
        Assert.That(mesh.Run(() => mesh.Stats.ExtensionErrors > 0), Is.True,
            "and the failures are counted and reported in the gateway's heartbeat");

        // The gateway is still running: a second client is welcomed after all those exceptions.
        var second = mesh.Connect("blue1");
        Assert.That(mesh.Run(() => second.Welcome != null), Is.True, "an extension throwing on every entity does not take the gateway down");
    }

    // ------------------------------------------------------------------------------- start-up is fail-fast

    /// <summary>Run the standalone gateway to completion with a broken extension and return what it printed on stderr.</summary>
    private (int exit, string error) RunWithBrokenExtension(Action<NebulaConfig> configure)
    {
        var config = new NebulaConfig { GatewayPort = (ushort)FreeUdpPort(), WebClients = false, UseLocalControlPlane = true };
        configure(config);
        string manifest = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new ServiceManifest { Config = config }, ServiceManifest.Json));
        ServiceManifest.Load(manifest);
        CommandLine.Override(new Dictionary<string, string>
        {
            { "nebula-service-manifest", manifest }, { "nebula-local-control-plane", "true" },
        });
        var captured = new StringWriter();
        var original = Console.Error;
        Console.SetError(captured);
        try
        {
            // The token is only a backstop: a gateway that loaded a broken extension must not reach the loop.
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return (ServiceHost.Run("gateway", stop.Token), captured.ToString());
        }
        finally { Console.SetError(original); }
    }

    [Test]
    public void AMissingExtensionAssemblyStopsTheGatewayWithAClearError()
    {
        var (exit, error) = RunWithBrokenExtension(c => c.GatewayExtension = "NoSuchGame.Gateway.dll");
        Assert.That(exit, Is.EqualTo(1), "a configured extension that cannot be loaded must stop the gateway, not be skipped");
        Assert.That(error, Does.Contain("NoSuchGame.Gateway.dll"));
        Assert.That(error, Does.Contain("Looked for"), "the error names the places it looked");
    }

    [Test]
    public void AnExtensionTypeThatIsNotThereStopsTheGatewayWithAClearError()
    {
        var (exit, error) = RunWithBrokenExtension(c =>
        {
            c.GatewayExtension = SampleAssembly;
            c.GatewayExtensionType = "Game.Gateway.NoSuchPolicy";
        });
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(error, Does.Contain("Game.Gateway.NoSuchPolicy"));
        Assert.That(error, Does.Contain("Nebula.SampleExtension.TeamFogExtension"), "and names what the assembly actually holds");
    }

    [Test]
    public void AnAssemblyThatIsNotAnExtensionStopsTheGatewayWithAClearError()
    {
        // A real dll next to the gateway that was never meant to be an extension: it does not reference the
        // services assembly, so it cannot implement the interface, and saying so is more use than "no type".
        var (exit, error) = RunWithBrokenExtension(c => c.GatewayExtension = Path.GetFileName(typeof(NUnit.Framework.Assert).Assembly.Location));
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(error, Does.Contain("does not reference Nebula.Services.dll"));
    }

    [Test]
    public void NoExtensionConfiguredIsTheNormalCase()
    {
        var config = new NebulaConfig { GatewayPort = (ushort)FreeUdpPort(), WebClients = false, UseLocalControlPlane = true };
        string manifest = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new ServiceManifest { Config = config }, ServiceManifest.Json));
        ServiceManifest.Load(manifest);
        CommandLine.Override(new Dictionary<string, string>
        {
            { "nebula-service-manifest", manifest }, { "nebula-local-control-plane", "true" },
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.That(ServiceHost.Run("gateway", stop.Token), Is.Zero, "a gateway with no extension configured starts and runs as before");
    }

    // ------------------------------------------------------------------------------------- the stats field

    [Test]
    public void TheExtensionErrorCountSurvivesTheControlPlaneRoundTrip()
    {
        // The stored/served side (ControlPlaneJson). The heartbeat side (RemoteControlPlane over HTTP) is what
        // the end-to-end test above reads the count back through.
        var plane = new LocalControlPlane();
        plane.Connect();
        plane.RegisterGateway("gw1", "127.0.0.1", 7000);
        plane.HeartbeatGateway("gw1", new GatewayStats { ExtensionErrors = 7, ActiveClients = 1 });
        var parsed = ControlPlaneJson.Parse(plane.ToJson());
        Assert.That(parsed.Gateways[0].Stats.ExtensionErrors, Is.EqualTo(7u));
    }
}
