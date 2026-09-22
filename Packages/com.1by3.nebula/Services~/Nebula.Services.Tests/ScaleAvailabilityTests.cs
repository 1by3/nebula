using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The availability half of the scale and failure suite (<c>docs/scale-suite.md</c> S6, gaps D7b and D7c;
/// design record <c>docs/control-plane-availability.md</c>). <c>ScaleFailureTests</c> restarts the control plane
/// as an <i>object</i> — it resets a <see cref="LocalControlPlane"/> in place and re-imports a document. That is
/// the right shape for "what do the gateways and clients do while it is away", but it proves nothing about the
/// orchestrator <b>process</b>: no HTTP server goes down, no mirror loses its long poll, no queued write waits
/// for anything, and no storage is read back.
/// <para>
/// These scenarios go one layer down. The control plane is the real <see cref="ControlPlaneHost"/> served over
/// the real <see cref="OrchestratorHttpServer"/> and stored in the real <see cref="SqlControlPlaneStorage"/>;
/// the workers and gateways are real <see cref="RemoteControlPlane"/> mirrors over loopback HTTP. Restarting the
/// orchestrator means disposing the host and its listener and standing a new pair up on the same port and the
/// same database — which is what <c>systemd restart nebula-orchestrator</c> does.
/// </para>
/// <para>
/// What they still cannot show is a second machine, a real network partition, or the cost of a managed
/// database's failover. Scenario 3 covers the last of those with Docker when Docker is there, and says so
/// instead of pretending when it is not.
/// </para>
/// </summary>
[TestFixture]
[Category("Scale")]
public class ScaleAvailabilityTests
{
    private string _directory = null!;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-availability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
    }

    [TearDown]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------------------------------ the orchestrator

    /// <summary>
    /// One orchestrator process: the hosted control plane, its HTTP listener and its database. Disposing it is
    /// the process going down; building another one on the same port and database is the restart.
    /// </summary>
    private sealed class Orchestrator : IDisposable
    {
        public readonly ControlPlaneHost Host;
        public readonly OrchestratorHttpServer Http;
        private readonly NebulaDatabase _db;

        public Orchestrator(int port, string databaseUrl, bool restore)
        {
            _db = NebulaDatabase.Open(DatabaseUrl.Parse(databaseUrl, ""));
            Http = new OrchestratorHttpServer("localhost", (ushort)port, "<html></html>");
            Host = new ControlPlaneHost(new SqlControlPlaneStorage(_db), null, restore) { SaveIntervalSeconds = 0.05f };
            Host.Attach(Http);
            Http.Start();
            Host.Connect();
        }

        public void Pump()
        {
            Http.Pump(req => Host.TryHandle(req, out var r) ? r : OrchestratorHttpServer.Response.Error(404, "no"));
            Host.Tick();
        }

        public void Dispose()
        {
            Host.Dispose();
            Http.Dispose();
            _db.Dispose();
        }
    }

    /// <summary>
    /// A worker as the control plane sees it: a mirror of the hosted document, the <b>real</b>
    /// <see cref="WorkerRegistration"/>, and the prologue of <see cref="NebulaWorker"/>'s
    /// <c>OnControlPlaneChanged</c> — re-register when the row has gone, re-claim the containers, then apply the
    /// leases to the registry. Everything that decides anything here is production code; what the fixture
    /// supplies is the loop that calls it.
    /// </summary>
    private sealed class MirrorWorker : IDisposable
    {
        public readonly RemoteControlPlane Plane;
        public readonly WorkerRegistration Registration = new();
        public string WorkerId => Registration.WorkerId;
        public int Reregistrations, Reclaimed;

        public MirrorWorker(string url, string workerId, uint index)
        {
            Plane = new RemoteControlPlane(url);
            Registration.WorkerId = workerId;
            Registration.WorkerIndex = index;
            Registration.Address = "127.0.0.1";
            Registration.Port = (ushort)(7100 + index);
            Plane.Changed += OnChanged;
            Plane.Connect();
        }

        private void OnChanged()
        {
            if (Registration.RegisterAgainIfForgotten(Plane)) Reregistrations++;
            Reclaimed += Registration.ReclaimContainers(Plane);
            foreach (var lease in Plane.Leases)
            {
                string owner = LeaseState.IsOwning(lease.State) ? lease.WorkerId : "";
                ContainerRegistry.ApplyLease(lease.ContainerId, owner, 0, lease.Epoch, lease.State);
            }
        }

        public void Tick()
        {
            Registration.Register(Plane);
            Plane.Tick();
            if (Registration.IsRegistered) Plane.HeartbeatWorker(WorkerId, WorkerStatus.Ready, new WorkerStats());
        }

        public void Dispose() => Plane.Dispose();
    }

    /// <summary>
    /// Wait until nothing answers on <paramref name="port"/>: the old orchestrator's listener has let go and the
    /// replacement can have the address. Without it the measurement is a lie on Windows, where a second socket
    /// can bind a port the first has not finished releasing and the stopped server goes on answering 503.
    /// </summary>
    private static bool PortIsFree(int port, double seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, port);
            }
            catch (SocketException) { return true; }
            Thread.Sleep(20);
        }
        return false;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Pump everything until <paramref name="until"/> holds or the time is up; returns whether it held.</summary>
    private static bool Run(Func<bool> until, Action pump, double seconds)
    {
        var clock = Stopwatch.StartNew();
        bool ok;
        while (!(ok = until()) && clock.Elapsed.TotalSeconds < seconds) { pump(); Thread.Sleep(5); }
        return ok;
    }

    // ----------------------------------------------------------- S6b: the orchestrator process restarts

    /// <summary>
    /// Scenario 12 — <b>orchestrator fast restart with live workers</b>, the measurement NEB-227 owes S6.
    /// Two workers and a gateway are mirroring a hosted control plane over HTTP; the orchestrator process is
    /// disposed and a replacement is stood up on the same port and the same SQLite database. The assertions are
    /// the issue's: every lease comes back with the same owner, state and epoch, no mirror reports itself
    /// disconnected, and the recovery time is written to the artifact.
    /// </summary>
    [Test]
    [Category("Soak")]
    public void AnOrchestratorRestartOnTheSameDatabaseKeepsEveryLeaseAndIsMeasured()
    {
        var report = new ScaleReport("orchestrator-restart",
            "storage", "leasesBefore", "leasesAfter", "workers", "gateways", "recoverySeconds",
            "mirrorsReportedDisconnected", "reregistrations", "queuedWritesAtRestart");

        int port = FreePort();
        string url = $"http://localhost:{port}";
        string database = "sqlite:" + Path.Combine(_directory, "orchestrator.db");
        using var world = new ScaleWorld(4);

        var orchestrator = new Orchestrator(port, database, restore: true);
        var workers = new List<MirrorWorker>();
        var gateway = new RemoteControlPlane(url);
        try
        {
            for (uint i = 0; i < 2; i++) workers.Add(new MirrorWorker(url, "w" + (i + 1), i + 1));
            gateway.Connect();

            void Pump()
            {
                orchestrator?.Pump();
                foreach (var w in workers) w.Tick();
                gateway.Tick();
            }

            Assert.That(Run(() => workers.All(w => w.Plane.IsConnected) && gateway.IsConnected, Pump, 30), Is.True,
                "the mirrors never reached the orchestrator");
            gateway.RegisterGateway("gw1", "127.0.0.1", 7000, 1);
            for (int i = 0; i < world.Cells.Count; i++)
            {
                // The orchestrator's planner would do this; the point of the scenario is what survives it, so the
                // assignment is made directly on the host and mirrored out like any other.
                orchestrator.Host.EnsureContainer(world.Cells[i]);
                orchestrator.Host.AssignContainer(world.Cells[i], workers[i % workers.Count].WorkerId);
            }
            Assert.That(Run(() => workers.All(w => w.Plane.Leases.Count == world.Cells.Count) &&
                                  orchestrator.Host.Workers.Count == 2 && orchestrator.Host.Gateways.Count == 1, Pump, 30), Is.True,
                "the mesh was not whole before the restart");
            // The document has to be in the database, or the restart would be measuring an empty restore.
            string storedBefore = null!;
            using (var probe = NebulaDatabase.Open(DatabaseUrl.Parse(database, "")))
            {
                var storage = new SqlControlPlaneStorage(probe);
                Assert.That(Run(() => (storedBefore = storage.Load()!) != null && ControlPlaneJson.Parse(storedBefore).Leases.Count == world.Cells.Count, Pump, 30),
                    Is.True, "the control-plane document never reached the database");
            }

            var before = Leases(orchestrator.Host);
            int reregistrationsBefore = workers.Sum(w => w.Reregistrations);
            bool anyDisconnected = false;

            // ---- the restart itself
            string marker = Guid.NewGuid().ToString("N");
            var clock = Stopwatch.StartNew();
            orchestrator.Dispose();
            orchestrator = null;
            int queuedAtRestart = workers.Sum(w => w.Plane.PendingWrites) + gateway.PendingWrites;
            Assert.That(PortIsFree(port, 20), Is.True, "the stopped orchestrator never let go of its port");
            orchestrator = new Orchestrator(port, database, restore: true);
            // A marker written by the replacement, so "back" means a live round trip to the new process and not a
            // mirror still holding the old document. It is also what makes the recovery time honest.
            orchestrator.Host.SetSetting("nebula.restart.marker", marker);
            bool back = Run(() =>
            {
                anyDisconnected |= workers.Any(w => !w.Plane.IsConnected) || !gateway.IsConnected;
                return workers.All(w => w.Plane.Leases.Count == world.Cells.Count && w.Plane.GetSetting("nebula.restart.marker") == marker) &&
                       gateway.GetSetting("nebula.restart.marker") == marker &&
                       orchestrator!.Host.Workers.Count == 2 && orchestrator.Host.Gateways.Count == 1;
            }, Pump, 60);
            double recovery = clock.Elapsed.TotalSeconds;

            var after = Leases(orchestrator.Host);
            report.Row(orchestrator.Host.StorageBackend, before.Count, after.Count, orchestrator.Host.Workers.Count,
                orchestrator.Host.Gateways.Count, recovery, anyDisconnected, workers.Sum(w => w.Reregistrations) - reregistrationsBefore,
                queuedAtRestart);
            report.Note($"the orchestrator process went down and came back on the same {orchestrator.Host.StorageBackend} " +
                        $"database in {recovery:0.000} s; every lease came back with its owner, state and epoch, and no " +
                        "mirror reported itself disconnected (RemoteControlPlane holds its last known topology for " +
                        $"{RemoteControlPlane.DisconnectAfterSeconds:0} s and queues its writes meanwhile).");
            report.Write();

            Assert.That(back, Is.True, "the mesh did not come back after the orchestrator restart");
            Assert.That(after, Is.EqualTo(before), "every lease came back with the same owner, state and epoch");
            Assert.That(recovery, Is.LessThan(ScaleThresholds.OrchestratorRestartSeconds),
                "the orchestrator restart took longer than the window in which no mirror even notices");
            Assert.That(anyDisconnected, Is.False,
                "a mirror reported itself disconnected, so the restart was slower than RemoteControlPlane.DisconnectAfterSeconds");
        }
        finally
        {
            gateway.Dispose();
            foreach (var w in workers) w.Dispose();
            orchestrator?.Dispose();
        }
    }

    /// <summary>
    /// Scenario 13 — <b>the orchestrator comes back with an empty database</b> (docs/scale-suite.md D7b, at the
    /// process level): a restore from a backup taken before this mesh started, a failover to a replica that never
    /// had the document, or plain <c>-nebula-reset</c>. Nothing in the database says these workers exist, so the
    /// only way the mesh converges is the workers noticing and saying it again — which is what NEB-227 added.
    /// </summary>
    [Test]
    [Category("Soak")]
    public void AnOrchestratorThatCameBackEmptyIsPutRightByTheWorkersThatAreStillSimulating()
    {
        var report = new ScaleReport("orchestrator-cold-restart",
            "leasesBefore", "leasesAfter", "workersAfter", "recoverySeconds", "reregistrations", "reclaimedContainers", "ownersMatch");

        int port = FreePort();
        string url = $"http://localhost:{port}";
        using var world = new ScaleWorld(4);

        var orchestrator = new Orchestrator(port, "sqlite:" + Path.Combine(_directory, "first.db"), restore: true);
        var workers = new List<MirrorWorker>();
        try
        {
            for (uint i = 0; i < 2; i++) workers.Add(new MirrorWorker(url, "w" + (i + 1), i + 1));

            void Pump()
            {
                orchestrator?.Pump();
                foreach (var w in workers) w.Tick();
            }

            Assert.That(Run(() => workers.All(w => w.Plane.IsConnected), Pump, 30), Is.True, "the mirrors never connected");
            for (int i = 0; i < world.Cells.Count; i++)
            {
                orchestrator.Host.EnsureContainer(world.Cells[i]);
                orchestrator.Host.AssignContainer(world.Cells[i], workers[i % workers.Count].WorkerId);
            }
            Assert.That(Run(() => workers.All(w => w.Plane.Leases.Count == world.Cells.Count), Pump, 30), Is.True,
                "the leases never reached the workers");
            var ownersBefore = world.Cells.ToDictionary(id => id, id => ContainerRegistry.FindById(id)!.OwnerWorkerId);
            Assert.That(ownersBefore.Values.All(o => o.Length > 0), Is.True, "the workers did not learn what they own");

            // ---- a different database: everything the mesh knew about itself is gone
            var clock = Stopwatch.StartNew();
            orchestrator.Dispose();
            Assert.That(PortIsFree(port, 20), Is.True, "the stopped orchestrator never let go of its port");
            orchestrator = new Orchestrator(port, "sqlite:" + Path.Combine(_directory, "restored-empty.db"), restore: true);
            Assert.That(orchestrator.Host.Leases, Is.Empty, "the replacement orchestrator was supposed to come back with nothing");

            bool converged = Run(() => orchestrator!.Host.Workers.Count == workers.Count &&
                                       orchestrator.Host.Leases.Count == world.Cells.Count, Pump, 60);
            double recovery = clock.Elapsed.TotalSeconds;
            var ownersAfter = orchestrator.Host.Leases.ToDictionary(l => l.ContainerId, l => l.WorkerId);

            bool ownersMatch = ownersBefore.Count == ownersAfter.Count && ownersBefore.All(kv => ownersAfter.TryGetValue(kv.Key, out var w) && w == kv.Value);
            report.Row(ownersBefore.Count, orchestrator.Host.Leases.Count, orchestrator.Host.Workers.Count, recovery,
                workers.Sum(w => w.Reregistrations), workers.Sum(w => w.Reclaimed), ownersMatch);
            report.Note("an empty control plane is put right by the processes that are still running: each worker sees a " +
                        "document it is not in, registers again and re-claims the containers it is still simulating. The " +
                        "epochs start at 1 because this is a new document — an epoch only has to increase within one.");
            report.Write();

            Assert.That(converged, Is.True, "the mesh never converged on the empty orchestrator");
            Assert.That(ownersMatch, Is.True, "every container is owned again by the worker that still simulates it");
            Assert.That(workers.Sum(w => w.Reclaimed), Is.EqualTo(world.Cells.Count), "each container was re-claimed exactly once");
            Assert.That(recovery, Is.LessThan(ScaleThresholds.OrchestratorRestartSeconds),
                "the mesh took longer to put itself right than a restart that kept its database");
        }
        finally
        {
            foreach (var w in workers) w.Dispose();
            orchestrator?.Dispose();
        }
    }

    // ------------------------------------------------------------------ S6c: the database underneath fails over

    /// <summary>
    /// Scenario 14 — <b>PostgreSQL failover under a running orchestrator</b> (docs/scale-suite.md D7c). A plain
    /// container restart of the primary, which is the shape of a failover the orchestrator can tell apart from a
    /// long query: the connection is lost mid-life, writes fail for a while, and then the same data is there
    /// again. What is asserted is the contract in <see cref="IControlPlaneStorage"/> — a store that throws is
    /// logged and retried, the mesh keeps running on the in-memory state, and nothing is lost — plus a bound on
    /// the stall.
    /// <para>
    /// Skipped with a reason when Docker is not running, and tagged <c>Docker</c> as well as <c>Scale</c> so no
    /// ordinary run depends on it.
    /// </para>
    /// </summary>
    [Test]
    [Category("Docker")]
    [Category("Soak")]
    public void APostgresFailoverStallsTheControlPlaneStoreAndLosesNothing()
    {
        if (!Docker("version --format {{.Server.Version}}", out string version, 20))
            Assert.Ignore("Docker is not available on this machine (`docker version` failed), so the PostgreSQL " +
                          "failover scenario cannot run. It needs a Docker daemon that can run linux/amd64 " +
                          "containers; see docs/control-plane-availability.md D6.");
        TestContext.Out.WriteLine($"[scale:{ScaleReport.Layer}] postgres-failover: docker server {version.Trim()}");

        var report = new ScaleReport("postgres-failover",
            "leasesBefore", "leasesAfter", "workersAfter", "restartSeconds", "storeStallSeconds", "writeErrorsSeen", "documentMatches");

        int port = FreePort();
        string name = "nebula-failover-" + Guid.NewGuid().ToString("N")[..8];
        string database = $"postgres://nebula:nebula@localhost:{port}/nebula";
        if (!Docker($"run -d --name {name} -e POSTGRES_PASSWORD=nebula -e POSTGRES_USER=nebula -e POSTGRES_DB=nebula " +
                    $"-p {port}:5432 postgres:16-alpine", out string runOutput, 300))
            Assert.Ignore("Could not start a PostgreSQL container (`docker run postgres:16-alpine` failed: " +
                          runOutput.Trim() + "), so the failover scenario cannot run.");
        try
        {
            Assert.That(WaitForPostgres(database, 120), Is.True, "the PostgreSQL container never accepted a connection");

            using var world = new ScaleWorld(4);
            using var db = NebulaDatabase.Open(DatabaseUrl.Parse(database, ""));
            var storage = new SqlControlPlaneStorage(db);
            var host = new ControlPlaneHost(storage, null, restore: true) { SaveIntervalSeconds = 0.05f };
            try
            {
                host.Connect();
                host.RegisterWorker("w1", 1, "127.0.0.1", 7101);
                host.HeartbeatWorker("w1", WorkerStatus.Ready, new WorkerStats());
                foreach (string id in world.Cells) { host.EnsureContainer(id); host.AssignContainer(id, "w1"); }
                Assert.That(Run(() => Stored(storage)?.Leases.Count == world.Cells.Count, host.Tick, 60), Is.True,
                    "the document never reached PostgreSQL");
                var before = Leases(host);

                // ---- the failover: the primary goes away and comes back with the same data
                var clock = Stopwatch.StartNew();
                Assert.That(Docker($"restart {name}", out _, 180), Is.True, "the PostgreSQL container could not be restarted");
                double restart = clock.Elapsed.TotalSeconds;

                // The mesh keeps running while the store is gone: the host must not throw out of Tick, and it
                // must still answer with everything it held.
                int writeErrors = 0;
                var stall = Stopwatch.StartNew();
                bool backUp = Run(() =>
                {
                    if (host.StorageError != null) writeErrors++;
                    try { return Stored(storage)?.Leases.Count == world.Cells.Count; }
                    catch (Exception) { return false; }
                }, () => { host.SetSetting("failover.probe", stall.Elapsed.Ticks.ToString()); host.Tick(); },
                    ScaleThresholds.DatabaseFailoverSeconds);
                double stallSeconds = stall.Elapsed.TotalSeconds;

                var after = Leases(host);
                bool matches = Stored(storage)!.Leases.Count == before.Count;
                report.Row(before.Count, after.Count, host.Workers.Count, restart, stallSeconds, writeErrors, matches);
                report.Note($"the PostgreSQL primary was restarted in {restart:0.00} s; the control-plane store was " +
                            $"writable again {stallSeconds:0.00} s later. The orchestrator kept every row it held in " +
                            "memory, kept answering subscribers, and wrote the document again on the first save that " +
                            "succeeded: a whole-document replace has nothing to replay.");
                report.Write();

                Assert.That(backUp, Is.True, $"the control-plane store was still unreachable after {ScaleThresholds.DatabaseFailoverSeconds:0} s");
                Assert.That(after, Is.EqualTo(before), "the orchestrator lost rows across a failover it was supposed to ride out");
                Assert.That(host.Workers.Count, Is.EqualTo(1), "and it still knows its worker");
                Assert.That(matches, Is.True, "the document in the database does not match what the orchestrator holds");
            }
            finally
            {
                host.Dispose();
            }
        }
        finally
        {
            Docker($"rm -f {name}", out _, 120);
        }
    }

    private static ControlPlaneJson.Snapshot? Stored(IControlPlaneStorage storage)
    {
        string json = storage.Load();
        return string.IsNullOrEmpty(json) ? null : ControlPlaneJson.Parse(json);
    }

    private static bool WaitForPostgres(string database, double seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            try
            {
                using var db = NebulaDatabase.Open(DatabaseUrl.Parse(database, ""));
                using var c = db.Open();
                return true;
            }
            catch (Exception) { Thread.Sleep(500); }
        }
        return false;
    }

    /// <summary>
    /// Run <c>docker</c> and say whether it succeeded. Shelling out rather than taking a Testcontainers
    /// dependency: the suite needs exactly two verbs (<c>run</c> and <c>restart</c>), and a test that is skipped
    /// on most machines should not add a package every build has to restore.
    /// </summary>
    private static bool Docker(string arguments, out string output, double timeoutSeconds)
    {
        output = "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (process == null) return false;
            string stdout = process.StandardOutput.ReadToEnd(), stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit((int)(timeoutSeconds * 1000))) { try { process.Kill(true); } catch { } return false; }
            output = stdout + stderr;
            return process.ExitCode == 0;
        }
        catch (Exception e)
        {
            output = e.Message;
            return false;
        }
    }

    /// <summary>A lease document reduced to what a restart has to preserve exactly: who owns what, in what state, at what epoch.</summary>
    private static Dictionary<string, (string Worker, string State, ulong Epoch)> Leases(IControlPlane plane) =>
        plane.Leases.ToDictionary(l => l.ContainerId, l => (l.WorkerId, l.State, l.Epoch));
}
