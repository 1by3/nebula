using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>The orchestrator's database (SQLite here; PostgreSQL shares every statement) and the HTTP path workers use to reach it.</summary>
[TestFixture]
public class StorageAndHostTests
{
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteCleanup();
        try { Directory.Delete(directory, true); } catch { }
    }

    private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private static void WaitUntil(Func<bool> condition, Action? pump = null, double seconds = 10)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            pump?.Invoke();
            if (sw.Elapsed.TotalSeconds > seconds) Assert.Fail("condition not met within " + seconds + " s");
            Thread.Sleep(5);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static PersistedEntityRecord Record(string key, string container, string carrier = "", uint epoch = 1) => new()
    {
        Key = key, PrefabId = 3, PrefabName = "Crate", ContainerId = container, CarrierKey = carrier,
        LocalPosition = new Vector3(1.5f, -2.25f, 3.125f), LocalRotation = new Quaternion(0, 0.7071068f, 0, 0.7071068f), Velocity = new Vector3(0, 1, 0),
        Epoch = epoch, ServerDriven = true, Owned = false, Name = "crate " + key, State = new byte[] { 1, 2, 3, 250 }, SavedBy = "w1",
    };

    private static void AssertSame(PersistedEntityRecord expected, PersistedEntityRecord actual)
    {
        Assert.That(actual.Key, Is.EqualTo(expected.Key));
        Assert.That(actual.PrefabId, Is.EqualTo(expected.PrefabId));
        Assert.That(actual.PrefabName, Is.EqualTo(expected.PrefabName));
        Assert.That(actual.ContainerId, Is.EqualTo(expected.ContainerId));
        Assert.That(actual.CarrierKey, Is.EqualTo(expected.CarrierKey));
        Assert.That(actual.LocalPosition.x, Is.EqualTo(expected.LocalPosition.x));
        Assert.That(actual.LocalPosition.z, Is.EqualTo(expected.LocalPosition.z));
        Assert.That(actual.LocalRotation.y, Is.EqualTo(expected.LocalRotation.y).Within(1e-6));
        Assert.That(actual.Velocity.y, Is.EqualTo(expected.Velocity.y));
        Assert.That(actual.Epoch, Is.EqualTo(expected.Epoch));
        Assert.That(actual.ServerDriven, Is.EqualTo(expected.ServerDriven));
        Assert.That(actual.Owned, Is.EqualTo(expected.Owned));
        Assert.That(actual.Name, Is.EqualTo(expected.Name));
        Assert.That(actual.State, Is.EqualTo(expected.State));
        Assert.That(actual.SavedBy, Is.EqualTo(expected.SavedBy));
    }

    // ---------------------------------------------------------------------------------------- database

    [Test]
    public void DatabaseUrlParsesEveryScheme()
    {
        Assert.That(DatabaseUrl.Parse("", "sqlite:/tmp/x.db").Scheme, Is.EqualTo("sqlite"));
        Assert.That(DatabaseUrl.Parse("sqlite:///opt/nebula/data/nebula.db", "").Target, Is.EqualTo("/opt/nebula/data/nebula.db"));
        Assert.That(DatabaseUrl.Parse("memory", "").Scheme, Is.EqualTo("memory"));
        Assert.That(DatabaseUrl.Parse("file:C:/saves", "").Target, Is.EqualTo("C:/saves"));
        var pg = DatabaseUrl.Parse("postgres://nebula:hunter2@db.example.com:5432/world?sslmode=require", "");
        Assert.That(pg.Scheme, Is.EqualTo("postgres"));
        Assert.That(pg.Display, Is.EqualTo("postgres://nebula:***@db.example.com:5432/world?sslmode=require"));
        Assert.That(DatabaseUrl.Parse("Host=db;Username=u;Password=p;Database=d", "").Display, Does.Not.Contain("Password=p"));
        Assert.Throws<ArgumentException>(() => DatabaseUrl.Parse("mysql://x", ""));
    }

    [Test]
    public void PostgresUrlBecomesAnNpgsqlConnectionString()
    {
        var db = NebulaDatabase.Open(DatabaseUrl.Parse("postgres://nebula:p%40ss@db.example.com:5433/world?sslmode=require", ""));
        Assert.That(db.Provider, Is.EqualTo("postgres"));
        Assert.That(db.Display, Does.Not.Contain("p%40ss"));
        // The connection string is private; opening would need a server. Provider and masking are what a caller sees.
    }

    [Test]
    public void SqlControlPlaneStorageKeepsTheDocument()
    {
        using var db = NebulaDatabase.Open(DatabaseUrl.Parse("sqlite:" + Path.Combine(directory, "cp.db"), ""));
        using var storage = new SqlControlPlaneStorage(db);
        Assert.That(storage.Load(), Is.Null);
        storage.Save("{\"version\":1}");
        storage.Save("{\"version\":2}");
        Assert.That(storage.Load(), Is.EqualTo("{\"version\":2}"));
    }

    [Test]
    public void SqlPersistenceStoreSavesLoadsAndKeepsTheEpochRule()
    {
        using var db = NebulaDatabase.Open(DatabaseUrl.Parse("sqlite:" + Path.Combine(directory, "world.db"), ""));
        var store = new SqlPersistenceStore(db);
        store.Connect();
        store.Save(Record("a", "c1", epoch: 2));
        store.Save(Record("b", "c1", carrier: "truck"));
        store.Save(Record("c", "c2"));

        PersistedEntityRecord? a = null;
        store.Load("a", r => a = r);
        WaitUntil(() => a != null, store.Tick);
        AssertSame(Record("a", "c1", epoch: 2), a!);
        Assert.That(a!.Version, Is.EqualTo(1));
        Assert.That(store.IsConnected, Is.True);

        // Older epoch: dropped. Same epoch: accepted, version bumped.
        var stale = Record("a", "c1", epoch: 1); stale.Name = "stale";
        store.Save(stale);
        var fresh = Record("a", "c1", epoch: 2); fresh.Name = "fresh";
        store.Save(fresh);
        a = null;
        store.Load("a", r => a = r);
        WaitUntil(() => a != null, store.Tick);
        Assert.That(a!.Name, Is.EqualTo("fresh"));
        Assert.That(a.Version, Is.EqualTo(2));

        IReadOnlyList<PersistedEntityRecord>? inC1 = null, carried = null, all = null;
        store.LoadContainer("c1", r => inC1 = r);
        store.LoadCarried("truck", r => carried = r);
        store.LoadWhere(r => r.ContainerId == "c2", r => all = r);
        WaitUntil(() => inC1 != null && carried != null && all != null, store.Tick);
        Assert.That(inC1!.Select(r => r.Key), Is.EquivalentTo(new[] { "a" }));
        Assert.That(carried!.Select(r => r.Key), Is.EquivalentTo(new[] { "b" }));
        Assert.That(all!.Select(r => r.Key), Is.EquivalentTo(new[] { "c" }));

        store.Delete("b");
        PersistedEntityRecord? b = Record("b", "x");
        store.Load("b", r => b = r);
        WaitUntil(() => b == null, store.Tick);
        WaitUntil(() => store.KnownCount == 2, store.Tick);

        store.Clear();
        WaitUntil(() => store.KnownCount == 0, store.Tick);
        store.Dispose();

        // A second store on the same file sees what the first one wrote (nothing, after the clear).
        var again = new SqlPersistenceStore(db);
        again.Connect();
        again.Save(Record("z", "c9"));
        again.Dispose();
        var third = new SqlPersistenceStore(db);
        third.Connect();
        PersistedEntityRecord? z = null;
        third.Load("z", r => z = r);
        WaitUntil(() => z != null, third.Tick);
        third.Dispose();
    }

    // ---------------------------------------------------------------------------------------- hosts over HTTP

    [Test]
    public void RemoteControlPlaneMirrorsTheHostedOne()
    {
        int port = FreePort();
        var http = new OrchestratorHttpServer("localhost", (ushort)port, "<html></html>");
        var host = new ControlPlaneHost(new FileControlPlaneStorage(Path.Combine(directory, "cp.json")), "s3cret", restore: false);
        host.SaveIntervalSeconds = 0.05f;
        host.Attach(http);
        http.Start();
        host.Connect();
        host.EnsureContainer("cell-1");
        var remote = new RemoteControlPlane($"http://localhost:{port}", "s3cret");
        var wrongToken = new RemoteControlPlane($"http://localhost:{port}", "nope");
        int changes = 0;
        remote.Changed += () => changes++;
        try
        {
            void Pump()
            {
                http.Pump(req => host.TryHandle(req, out var r) ? r : OrchestratorHttpServer.Response.Error(404, "no"));
                host.Tick();
                remote.Tick();
                wrongToken.Tick();
            }
            remote.Connect();
            wrongToken.Connect();
            WaitUntil(() => remote.IsConnected && remote.Leases.Count == 1, Pump);
            Assert.That(remote.Leases[0].ContainerId, Is.EqualTo("cell-1"));
            Assert.That(changes, Is.EqualTo(1));

            // A worker registers and heartbeats through the mirror; the host applies both in order and every mirror sees it.
            remote.RegisterWorker("w1", 1, "10.0.0.5", 7101);
            remote.HeartbeatWorker("w1", WorkerStatus.Ready, new WorkerStats { TickCount = 42, TickMs = 1.5f, PlayerCount = 2 });
            remote.EnsureRuntimeContainer("rt_7", new Bounds(new Vector3(10, 0, 10), new Vector3(4, 4, 4)), "w1");
            remote.SetSetting("npcs", "12");
            WaitUntil(() => remote.Workers.Count == 1 && remote.Workers[0].Status == WorkerStatus.Ready && remote.Leases.Count == 2 && remote.Settings.ContainsKey("npcs"), Pump);
            Assert.That(host.Workers[0].TickCount, Is.EqualTo(42));
            Assert.That(remote.Workers[0].PlayerCount, Is.EqualTo(2));
            Assert.That(remote.Workers[0].TickMs, Is.EqualTo(1.5f));
            var rt = remote.Leases.First(l => l.ContainerId == "rt_7");
            Assert.That(rt.HasBounds && rt.BoundsCenter.x == 10 && rt.BoundsSize.y == 4 && rt.WorkerId == "w1" && rt.Epoch == 1, Is.True);
            Assert.That(remote.Settings["npcs"], Is.EqualTo("12"));

            // A balancing hint set through the mirror lands on the lease row and comes back to every subscriber.
            remote.SetContainerHint("rt_7", new ContainerHint { CostMultiplier = 2.5f, AffinityGroup = "raid", SeamCost = 0.5f, Dedicated = true });
            WaitUntil(() => remote.Leases.First(l => l.ContainerId == "rt_7").HasHint, Pump);
            var hinted = remote.Leases.First(l => l.ContainerId == "rt_7");
            Assert.That(hinted.Hint.EffectiveMultiplier, Is.EqualTo(2.5f));
            Assert.That(hinted.Hint.Group, Is.EqualTo("raid"));
            Assert.That(hinted.Hint.Dedicated, Is.True);
            Assert.That(host.FindLease("rt_7").Hint, Is.EqualTo(hinted.Hint), "the host is the source of truth the mirror agrees with");
            Assert.That(Math.Abs((remote.Now - host.Now).TotalSeconds), Is.LessThan(2));
            Assert.That(remote.PendingWrites, Is.Zero);

            // The wrong token gets nothing in and nothing out.
            Assert.That(wrongToken.IsConnected, Is.False);
            Assert.That(wrongToken.Workers, Is.Empty);
            wrongToken.RegisterWorker("evil", 9, "1.2.3.4", 1);
            WaitUntil(() => wrongToken.PendingWrites == 0, Pump);
            Assert.That(host.Workers.Any(w => w.WorkerId == "evil"), Is.False);

            // The document reached storage.
            WaitUntil(() => File.Exists(Path.Combine(directory, "cp.json")), Pump);
            var stored = ControlPlaneJson.Parse(File.ReadAllText(Path.Combine(directory, "cp.json")));
            WaitUntil(() => ControlPlaneJson.Parse(File.ReadAllText(Path.Combine(directory, "cp.json"))).Workers.Count == 1, Pump);

            remote.UnregisterWorker("w1");
            WaitUntil(() => remote.Workers.Count == 0 && remote.Leases.First(l => l.ContainerId == "rt_7").State == LeaseState.Orphaned, Pump);
        }
        finally
        {
            remote.Dispose();
            wrongToken.Dispose();
            host.Dispose();
            http.Dispose();
        }

        // A new host restores the stored document when asked to.
        var restored = new ControlPlaneHost(new FileControlPlaneStorage(Path.Combine(directory, "cp.json")), null, restore: true);
        restored.Connect();
        Assert.That(restored.Leases.Select(l => l.ContainerId), Is.EquivalentTo(new[] { "cell-1", "rt_7" }));
        Assert.That(restored.Settings["npcs"], Is.EqualTo("12"));
        restored.Dispose();
    }

    [Test]
    public void RemotePersistenceStoreGoesThroughThePersistenceHost()
    {
        int port = FreePort();
        var http = new OrchestratorHttpServer("localhost", (ushort)port, "<html></html>");
        var backing = new LocalPersistenceStore();
        backing.Connect();
        var host = new PersistenceHost(backing, null);
        http.Start();
        var remote = new RemotePersistenceStore($"http://localhost:{port}/");
        try
        {
            void Pump()
            {
                http.Pump(req => host.TryHandle(req, out var r) ? r : OrchestratorHttpServer.Response.Error(404, "no"));
                backing.Tick();
                remote.Tick();
            }
            remote.Connect();
            WaitUntil(() => remote.IsConnected, Pump);
            remote.Save(Record("a", "c1"));
            remote.Save(Record("b", "c1", carrier: "truck"));
            var newer = Record("a", "c1"); newer.Name = "newer";
            remote.Save(newer); // coalesced with the first save of "a": only the newest travels
            WaitUntil(() => backing.KnownCount == 2, Pump);

            PersistedEntityRecord? a = null;
            remote.Load("a", r => a = r);
            WaitUntil(() => a != null, Pump);
            AssertSame(newer, a!);

            IReadOnlyList<PersistedEntityRecord>? inC1 = null, carried = null, filtered = null;
            remote.LoadContainer("c1", r => inC1 = r);
            remote.LoadCarried("truck", r => carried = r);
            remote.LoadWhere(r => r.Key == "b", r => filtered = r);
            WaitUntil(() => inC1 != null && carried != null && filtered != null, Pump);
            Assert.That(inC1!.Select(r => r.Key), Is.EquivalentTo(new[] { "a" }));
            Assert.That(carried!.Select(r => r.Key), Is.EquivalentTo(new[] { "b" }));
            Assert.That(filtered!.Select(r => r.Key), Is.EquivalentTo(new[] { "b" }));

            remote.Delete("a");
            WaitUntil(() => backing.KnownCount == 1, Pump);
            WaitUntil(() => remote.KnownCount == 1, Pump, seconds: RemotePersistenceStore.StatusIntervalSeconds + 5);
            PersistedEntityRecord? gone = Record("a", "x");
            remote.Load("a", r => gone = r);
            WaitUntil(() => gone == null, Pump);
            remote.Clear();
            WaitUntil(() => backing.KnownCount == 0, Pump);
        }
        finally
        {
            remote.Dispose();
            backing.Dispose();
            http.Dispose();
        }
    }

    [Test]
    public void StoreEndpointsRejectAWrongTokenAndAnUnknownPath()
    {
        int port = FreePort();
        var http = new OrchestratorHttpServer("localhost", (ushort)port, "<html></html>");
        var backing = new LocalPersistenceStore();
        backing.Connect();
        var host = new PersistenceHost(backing, "tok");
        http.Start();
        try
        {
            using var client = new HttpClient();
            var pumping = true;
            var pump = new Thread(() => { while (pumping) { http.Pump(req => host.TryHandle(req, out var r) ? r : OrchestratorHttpServer.Response.Error(404, "no")); backing.Tick(); Thread.Sleep(2); } });
            pump.Start();
            try
            {
                var denied = client.GetAsync($"http://localhost:{port}/api/store/status").Result;
                Assert.That((int)denied.StatusCode, Is.EqualTo(401));
                var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/api/store/status");
                req.Headers.Add(ControlPlaneHost.TokenHeader, "tok");
                var ok = client.SendAsync(req).Result;
                Assert.That((int)ok.StatusCode, Is.EqualTo(200));
                Assert.That(ok.Content.ReadAsStringAsync().Result, Does.Contain("\"backend\":\"memory\""));
                var missing = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/api/store/nothing");
                missing.Headers.Add(ControlPlaneHost.TokenHeader, "tok");
                Assert.That((int)client.SendAsync(missing).Result.StatusCode, Is.EqualTo(404));
            }
            finally { pumping = false; pump.Join(); }
        }
        finally
        {
            backing.Dispose();
            http.Dispose();
        }
    }
}
