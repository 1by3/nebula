#nullable enable
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The store leg of conformance scenario 17 (<c>docs/conformance-suite.md</c>): <b>a worker that gains hundreds of
/// containers at once restores every one of them with a bounded number of requests</b>, and so does a worker that
/// spawns hundreds of carriers at once when it reads what rode in them; nothing on the persistence path can put more
/// than a fixed number of requests in flight. Tier A: the production <see cref="RemotePersistenceStore"/> against a
/// real <see cref="OrchestratorHttpServer"/> and <see cref="PersistenceHost"/>, and the production
/// <see cref="SqlPersistenceStore"/> on SQLite (PostgreSQL runs the same statements). The worker half —
/// <see cref="NebulaPersistence"/> batching the containers it gains and the carriers it spawns — is
/// <c>Tests/EditMode/ConformanceRestoreBurstTests.cs</c>, which needs Unity.
/// <para>
/// The failure this pins: a worker that was dealt a few hundred containers fired one load per container, all at
/// once. The burst starved the worker's thread pool, the control-plane heartbeat that shares it stopped completing,
/// and the orchestrator declared the worker dead and dealt its containers to the next worker, which got the same
/// burst.
/// </para>
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceRestoreBurstTests
{
    private const int Containers = 600;
    private const int Carriers = 600;
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-restore-burst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
    }

    [TearDown]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch { }
    }

    private static void WaitUntil(Func<bool> condition, Action? pump = null, double seconds = 20)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            pump?.Invoke();
            if (sw.Elapsed.TotalSeconds > seconds) Assert.Fail("condition not met within " + seconds + " s");
            Thread.Sleep(2);
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

    private static string ChunkId(int i) => "chunk:" + i;

    private static PersistedEntityRecord Record(string key, string container, string carrier = "") => new()
    {
        Key = key, PrefabId = 1, PrefabName = "Crate", ContainerId = container, CarrierKey = carrier,
        LocalPosition = new Vector3(1, 2, 3), LocalRotation = Quaternion.identity, Epoch = 1, SavedBy = "w0", State = new byte[] { 7 },
    };

    /// <summary>Two records in every even container, none in the odd ones, one carried record, and records in containers nobody asks for.</summary>
    private static List<PersistedEntityRecord> World()
    {
        var records = new List<PersistedEntityRecord>();
        for (int i = 0; i < Containers; i += 2)
        {
            records.Add(Record($"e{i}a", ChunkId(i)));
            records.Add(Record($"e{i}b", ChunkId(i)));
        }
        records.Add(Record("cargo", "", carrier: "e0a"));
        for (int i = 0; i < 50; i++) records.Add(Record($"far{i}", "elsewhere:" + i));
        return records;
    }

    private static List<string> AskedFor()
    {
        var ids = new List<string>();
        for (int i = 0; i < Containers; i++) ids.Add(ChunkId(i));
        ids.Add(ChunkId(3));        // asked twice: answered once
        ids.Add("never-saved");     // nothing there: an empty entry, not a missing one
        return ids;
    }

    private static void AssertWorld(IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>> loaded)
    {
        Assert.That(loaded.Count, Is.EqualTo(Containers + 1), "one entry per distinct id asked for");
        for (int i = 0; i < Containers; i++)
        {
            Assert.That(loaded.TryGetValue(ChunkId(i), out var records), Is.True, $"{ChunkId(i)} is answered");
            var keys = records!.Select(r => r.Key).ToArray();
            if (i % 2 == 0) Assert.That(keys, Is.EquivalentTo(new[] { $"e{i}a", $"e{i}b" }), ChunkId(i));
            else Assert.That(keys, Is.Empty, ChunkId(i));
            Assert.That(records.All(r => r.ContainerId == ChunkId(i) && r.CarrierKey == ""), Is.True, "only the container's own, uncarried records");
        }
        Assert.That(loaded["never-saved"], Is.Empty);
    }

    private static string ShipKey(int i) => "ship:" + i;

    /// <summary>Two records aboard every even carrier, none aboard the odd ones, the carriers' own records, and cargo of carriers nobody asks for.</summary>
    private static List<PersistedEntityRecord> Fleet()
    {
        var records = new List<PersistedEntityRecord>();
        for (int i = 0; i < Carriers; i++)
        {
            records.Add(Record(ShipKey(i), "hangar"));   // the carrier itself: in a container, not aboard anything
            if (i % 2 != 0) continue;
            records.Add(Record($"cargo{i}a", "", carrier: ShipKey(i)));
            records.Add(Record($"cargo{i}b", "", carrier: ShipKey(i)));
        }
        for (int i = 0; i < 50; i++) records.Add(Record($"far{i}", "", carrier: "elsewhere:" + i));
        return records;
    }

    private static List<string> CarriersAskedFor()
    {
        var keys = new List<string>();
        for (int i = 0; i < Carriers; i++) keys.Add(ShipKey(i));
        keys.Add(ShipKey(3));       // asked twice: answered once
        keys.Add("never-saved");    // nothing aboard: an empty entry, not a missing one
        keys.Add("");               // no carrier at all: an empty entry, never the world's uncarried records
        return keys;
    }

    private static void AssertFleet(IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>> loaded)
    {
        Assert.That(loaded.Count, Is.EqualTo(Carriers + 2), "one entry per distinct key asked for");
        for (int i = 0; i < Carriers; i++)
        {
            Assert.That(loaded.TryGetValue(ShipKey(i), out var records), Is.True, $"{ShipKey(i)} is answered");
            var keys = records!.Select(r => r.Key).ToArray();
            if (i % 2 == 0) Assert.That(keys, Is.EquivalentTo(new[] { $"cargo{i}a", $"cargo{i}b" }), ShipKey(i));
            else Assert.That(keys, Is.Empty, ShipKey(i));
            Assert.That(records.All(r => r.CarrierKey == ShipKey(i)), Is.True, "only what rides in that carrier");
        }
        Assert.That(loaded["never-saved"], Is.Empty);
        Assert.That(loaded[""], Is.Empty);
    }

    [Test]
    public void SqlStoreAnswersHundredsOfContainersInOneBoundedJob()
    {
        using var db = NebulaDatabase.Open(DatabaseUrl.Parse("sqlite:" + Path.Combine(directory, "world.db"), ""));
        var store = new SqlPersistenceStore(db);
        store.Connect();
        try
        {
            foreach (var r in World()) store.Save(r);
            int answers = 0;
            IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>>? loaded = null;
            store.LoadContainers(AskedFor(), r => { answers++; loaded = r; });
            WaitUntil(() => loaded != null, store.Tick);
            for (int i = 0; i < 5; i++) { store.Tick(); Thread.Sleep(2); }
            Assert.That(answers, Is.EqualTo(1), "answered once, although the ids span three chunked queries");
            AssertWorld(loaded!);
            Assert.That(PersistenceHost.MaxContainersPerLoad, Is.LessThan(Containers), "the list is longer than one query's worth");

            IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>>? none = null;
            store.LoadContainers(Array.Empty<string>(), r => none = r);
            WaitUntil(() => none != null, store.Tick);
            Assert.That(none!, Is.Empty);
        }
        finally { store.Dispose(); }
    }

    [Test]
    public void SqlStoreAnswersHundredsOfCarriersInOneBoundedJob()
    {
        using var db = NebulaDatabase.Open(DatabaseUrl.Parse("sqlite:" + Path.Combine(directory, "fleet.db"), ""));
        var store = new SqlPersistenceStore(db);
        store.Connect();
        try
        {
            foreach (var r in Fleet()) store.Save(r);
            int answers = 0;
            IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>>? loaded = null;
            store.LoadCarried(CarriersAskedFor(), r => { answers++; loaded = r; });
            WaitUntil(() => loaded != null, store.Tick);
            for (int i = 0; i < 5; i++) { store.Tick(); Thread.Sleep(2); }
            Assert.That(answers, Is.EqualTo(1), "answered once, although the keys span three chunked queries");
            AssertFleet(loaded!);
            Assert.That(PersistenceHost.MaxCarriersPerLoad, Is.LessThan(Carriers), "the list is longer than one query's worth");

            IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>>? none = null;
            store.LoadCarried(Array.Empty<string>(), r => none = r);
            WaitUntil(() => none != null, store.Tick);
            Assert.That(none!, Is.Empty);
        }
        finally { store.Dispose(); }
    }

    [Test]
    public void RemoteStoreAsksForHundredsOfCarriersInAFewRequests()
    {
        int port = FreePort();
        var http = new OrchestratorHttpServer("localhost", (ushort)port, "<html></html>");
        var backing = new LocalPersistenceStore();
        backing.Connect();
        foreach (var r in Fleet()) backing.Save(r);
        var host = new PersistenceHost(backing, null);
        http.Start();
        var remote = new RemotePersistenceStore($"http://localhost:{port}/");
        int carriedRequests = 0;
        int largest = 0;
        try
        {
            void Pump()
            {
                http.Pump(req =>
                {
                    if (req.Path.EndsWith("/carried", StringComparison.Ordinal))
                    {
                        carriedRequests++;
                        PersistenceJson.TryParseObject(req.Body, out var body, out _);
                        largest = Math.Max(largest, body != null && body.TryGetValue("keys", out var keys) && keys is List<object> list ? list.Count : 0);
                    }
                    return host.TryHandle(req, out var response) ? response : OrchestratorHttpServer.Response.Error(404, "no");
                });
                backing.Tick();
                remote.Tick();
            }
            remote.Connect();
            int answers = 0;
            IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>>? loaded = null;
            remote.LoadCarried(CarriersAskedFor(), r => { answers++; loaded = r; });
            WaitUntil(() => loaded != null, Pump);
            for (int i = 0; i < 20; i++) { Pump(); Thread.Sleep(2); }

            // The distinct keys worth reading: every carrier, plus "never-saved" (the empty key is never sent).
            int expectedRequests = (Carriers + 1 + PersistenceHost.MaxCarriersPerLoad - 1) / PersistenceHost.MaxCarriersPerLoad;
            Assert.That(carriedRequests, Is.EqualTo(expectedRequests), "one request per MaxCarriersPerLoad keys, not one per carrier");
            Assert.That(largest, Is.LessThanOrEqualTo(PersistenceHost.MaxCarriersPerLoad));
            Assert.That(answers, Is.EqualTo(1), "the requests are merged into one answer");
            AssertFleet(loaded!);
            Assert.That(remote.PendingReads, Is.Zero);
        }
        finally
        {
            remote.Dispose();
            backing.Dispose();
            http.Dispose();
        }
    }

    [Test]
    public void RemoteStoreAsksForHundredsOfContainersInAFewRequests()
    {
        int port = FreePort();
        var http = new OrchestratorHttpServer("localhost", (ushort)port, "<html></html>");
        var backing = new LocalPersistenceStore();
        backing.Connect();
        foreach (var r in World()) backing.Save(r);
        var host = new PersistenceHost(backing, null);
        http.Start();
        var remote = new RemotePersistenceStore($"http://localhost:{port}/");
        int containerRequests = 0;
        int largest = 0;
        try
        {
            void Pump()
            {
                http.Pump(req =>
                {
                    if (req.Path.EndsWith("/containers", StringComparison.Ordinal))
                    {
                        containerRequests++;
                        PersistenceJson.TryParseObject(req.Body, out var body, out _);
                        largest = Math.Max(largest, body != null && body.TryGetValue("ids", out var ids) && ids is List<object> list ? list.Count : 0);
                    }
                    return host.TryHandle(req, out var response) ? response : OrchestratorHttpServer.Response.Error(404, "no");
                });
                backing.Tick();
                remote.Tick();
            }
            remote.Connect();
            int answers = 0;
            IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>>? loaded = null;
            remote.LoadContainers(AskedFor(), r => { answers++; loaded = r; });
            WaitUntil(() => loaded != null, Pump);
            for (int i = 0; i < 20; i++) { Pump(); Thread.Sleep(2); }

            int expectedRequests = (Containers + 1 + PersistenceHost.MaxContainersPerLoad - 1) / PersistenceHost.MaxContainersPerLoad;
            Assert.That(containerRequests, Is.EqualTo(expectedRequests), "one request per MaxContainersPerLoad ids, not one per container");
            Assert.That(largest, Is.LessThanOrEqualTo(PersistenceHost.MaxContainersPerLoad));
            Assert.That(answers, Is.EqualTo(1), "the requests are merged into one answer");
            AssertWorld(loaded!);
            Assert.That(remote.PendingReads, Is.Zero);
        }
        finally
        {
            remote.Dispose();
            backing.Dispose();
            http.Dispose();
        }
    }

    [Test]
    public void RemoteStoreNeverHasMoreThanItsReadersInFlight()
    {
        // The orchestrator answers straight from Kestrel's threads here (no main-thread pump), slowly, and counts how
        // many reads are open at once. Whatever the worker asks for in one go, that number must stay at the
        // store's reader count: the burst waits in the store's queue, not on the thread pool or the socket.
        int port = FreePort();
        var http = new OrchestratorHttpServer("localhost", (ushort)port, "<html></html>");
        int open = 0, mostOpen = 0, served = 0;
        OrchestratorHttpServer.Response Slow(Func<OrchestratorHttpServer.Response> answer)
        {
            int now = Interlocked.Increment(ref open);
            int seen;
            while ((seen = Volatile.Read(ref mostOpen)) < now && Interlocked.CompareExchange(ref mostOpen, now, seen) != seen) { }
            Thread.Sleep(3);
            Interlocked.Increment(ref served);
            Interlocked.Decrement(ref open);
            return answer();
        }
        http.MapDirect("GET", PersistenceHost.Prefix + "/record", req => Slow(() => OrchestratorHttpServer.Response.Json(200, PersistedRecordJson.WriteOne(Record(req.GetQuery("key"), "c")))));
        http.MapDirect("POST", PersistenceHost.Prefix + "/carried", req => Slow(() => OrchestratorHttpServer.Response.Json(200, PersistedRecordJson.WriteList(Array.Empty<PersistedEntityRecord>()))));
        http.MapDirect("GET", PersistenceHost.Prefix + "/count", req => Slow(() => OrchestratorHttpServer.Response.Json(200, "{\"ok\":true,\"count\":4}")));
        http.MapDirect("POST", PersistenceHost.Prefix + "/containers", req => Slow(() => OrchestratorHttpServer.Response.Json(200, PersistedRecordJson.WriteList(Array.Empty<PersistedEntityRecord>()))));
        http.Start();
        var remote = new RemotePersistenceStore($"http://localhost:{port}/");
        try
        {
            remote.Connect();
            const int Burst = 200;
            int records = 0, carried = 0, counts = 0, containerAnswers = 0;
            for (int i = 0; i < Burst; i++)
            {
                string key = "k" + i;
                remote.Load(key, r => { if (r != null && r.Key == key) records++; });
                remote.LoadCarried(new[] { "ship" + i }, r => carried++);
                remote.CountRecords("", "c" + i, n => { if (n == 4) counts++; });
            }
            var ids = Enumerable.Range(0, 1000).Select(i => "c" + i).ToList();
            remote.LoadContainers(ids, r => { if (r.Count == ids.Count) containerAnswers++; });
            Assert.That(remote.PendingReads, Is.GreaterThan(RemotePersistenceStore.MaxConcurrentReads), "the burst is queued in the store");

            WaitUntil(() => records == Burst && carried == Burst && counts == Burst && containerAnswers == 1, remote.Tick, seconds: 60);
            Assert.That(Volatile.Read(ref mostOpen), Is.LessThanOrEqualTo(RemotePersistenceStore.MaxConcurrentReads),
                "never more reads in flight than the store has reader threads");
            Assert.That(Volatile.Read(ref mostOpen), Is.GreaterThanOrEqualTo(1));
            Assert.That(served, Is.EqualTo(3 * Burst + 4), "every read sent once: 1000 container ids travel as four requests");
            Assert.That(remote.PendingReads, Is.Zero);
        }
        finally
        {
            remote.Dispose();
            http.Dispose();
        }
    }

    [Test]
    public void HostRefusesAnOversizedOrMalformedContainerRequest()
    {
        var backing = new LocalPersistenceStore();
        backing.Connect();
        var host = new PersistenceHost(backing, null);
        try
        {
            var tooMany = new StringBuilder("{\"ids\":[");
            for (int i = 0; i <= PersistenceHost.MaxContainersPerLoad; i++) tooMany.Append(i > 0 ? "," : "").Append("\"c").Append(i).Append('"');
            tooMany.Append("]}");
            var oversized = new OrchestratorHttpServer.Request { Method = "POST", Path = PersistenceHost.Prefix + "/containers", Body = tooMany.ToString() };
            Assert.That(host.TryHandle(oversized, out var refused), Is.True);
            Assert.That(refused.Status, Is.EqualTo(400));
            Assert.That(refused.Body, Does.Contain(PersistenceHost.MaxContainersPerLoad.ToString()));

            var notStrings = new OrchestratorHttpServer.Request { Method = "POST", Path = PersistenceHost.Prefix + "/containers", Body = "{\"ids\":[1,2]}" };
            Assert.That(host.TryHandle(notStrings, out var malformed), Is.True);
            Assert.That(malformed.Status, Is.EqualTo(400));

            backing.Save(Record("a", "c1"));
            backing.Save(Record("b", "c2"));
            var ok = new OrchestratorHttpServer.Request { Method = "POST", Path = PersistenceHost.Prefix + "/containers", Body = "{\"ids\":[\"c1\",\"c2\",\"c3\"]}" };
            Assert.That(host.TryHandle(ok, out var pending), Is.True);
            Assert.That(pending.IsPending, Is.True, "answered from the store's next tick");
            backing.Tick();
            Assert.That(ok.Completion.Task.IsCompleted, Is.True);
            var answer = ok.Completion.Task.Result;
            Assert.That(answer.Status, Is.EqualTo(200));
            Assert.That(PersistedRecordJson.ParseList(answer.Body).Select(r => r.Key), Is.EquivalentTo(new[] { "a", "b" }));

            var gone = new OrchestratorHttpServer.Request { Method = "GET", Path = PersistenceHost.Prefix + "/container", Query = "id=c1" };
            Assert.That(host.TryHandle(gone, out var notFound), Is.True);
            Assert.That(notFound.Status, Is.EqualTo(404), "the one-container endpoint is gone: containers are read in batches");
        }
        finally { backing.Dispose(); }
    }

    [Test]
    public void HostRefusesAnOversizedOrMalformedCarrierRequest()
    {
        var backing = new LocalPersistenceStore();
        backing.Connect();
        var host = new PersistenceHost(backing, null);
        try
        {
            var tooMany = new StringBuilder("{\"keys\":[");
            for (int i = 0; i <= PersistenceHost.MaxCarriersPerLoad; i++) tooMany.Append(i > 0 ? "," : "").Append("\"s").Append(i).Append('"');
            tooMany.Append("]}");
            var oversized = new OrchestratorHttpServer.Request { Method = "POST", Path = PersistenceHost.Prefix + "/carried", Body = tooMany.ToString() };
            Assert.That(host.TryHandle(oversized, out var refused), Is.True);
            Assert.That(refused.Status, Is.EqualTo(400));
            Assert.That(refused.Body, Does.Contain(PersistenceHost.MaxCarriersPerLoad.ToString()));

            var notStrings = new OrchestratorHttpServer.Request { Method = "POST", Path = PersistenceHost.Prefix + "/carried", Body = "{\"keys\":[1,2]}" };
            Assert.That(host.TryHandle(notStrings, out var malformed), Is.True);
            Assert.That(malformed.Status, Is.EqualTo(400));

            backing.Save(Record("a", "", carrier: "s1"));
            backing.Save(Record("b", "", carrier: "s2"));
            backing.Save(Record("s1", "c1"));
            var ok = new OrchestratorHttpServer.Request { Method = "POST", Path = PersistenceHost.Prefix + "/carried", Body = "{\"keys\":[\"s1\",\"s2\",\"s3\",\"\"]}" };
            Assert.That(host.TryHandle(ok, out var pending), Is.True);
            Assert.That(pending.IsPending, Is.True, "answered from the store's next tick");
            backing.Tick();
            Assert.That(ok.Completion.Task.IsCompleted, Is.True);
            var answer = ok.Completion.Task.Result;
            Assert.That(answer.Status, Is.EqualTo(200));
            Assert.That(PersistedRecordJson.ParseList(answer.Body).Select(r => r.Key), Is.EquivalentTo(new[] { "a", "b" }),
                "what rides in the carriers asked for; the empty key is no carrier and brings back nothing");

            var gone = new OrchestratorHttpServer.Request { Method = "GET", Path = PersistenceHost.Prefix + "/carried", Query = "key=s1" };
            Assert.That(host.TryHandle(gone, out var notFound), Is.True);
            Assert.That(notFound.Status, Is.EqualTo(404), "the one-carrier endpoint is gone: carriers are read in batches");
        }
        finally { backing.Dispose(); }
    }
}
