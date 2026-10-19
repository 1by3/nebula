using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Nebula;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The shared cache (NEB-481): write-once objects that workers share through the orchestrator. The stores (SQLite
/// here; PostgreSQL shares every statement with it, and a folder), the HTTP endpoints, and the worker clients.
/// </summary>
[TestFixture]
public class SharedCacheTests
{
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-shared-cache-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static byte[] Bytes(int length, byte seed = 7)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(seed + i * 31);
        return bytes;
    }

    private NebulaDatabase OpenDatabase() => NebulaDatabase.Open(DatabaseUrl.Parse("sqlite:" + Path.Combine(directory, "cache.db"), ""));

    /// <summary>Every store implementation runs the same contract.</summary>
    private static IEnumerable<TestCaseData> Stores()
    {
        yield return new TestCaseData("sqlite");
        yield return new TestCaseData("folder");
        yield return new TestCaseData("memory");
    }

    private ISharedCacheStore CreateStore(string kind) => kind switch
    {
        "sqlite" => new SqlSharedCacheStore(OpenDatabase()),
        "folder" => new FolderSharedCacheStore(Path.Combine(directory, "folder")),
        _ => new MemorySharedCacheStore(),
    };

    [TestCaseSource(nameof(Stores))]
    public void PutThenGetReturnsTheBytes(string kind)
    {
        using var store = CreateStore(kind);
        var bytes = Bytes(3 * 1024 * 1024);
        string sum = SharedCacheKey.Checksum(bytes);
        Assert.That(store.Put("terrain.v1.abc", bytes, sum), Is.True);
        Assert.That(store.TryGet("terrain.v1.abc", out var got, out var gotSum), Is.True);
        Assert.That(got, Is.EqualTo(bytes));
        Assert.That(gotSum, Is.EqualTo(sum));
        Assert.That(store.Count(), Is.EqualTo(1));
        Assert.That(store.TotalBytes(), Is.GreaterThanOrEqualTo(bytes.Length));
    }

    [TestCaseSource(nameof(Stores))]
    public void AMissIsFalse(string kind)
    {
        using var store = CreateStore(kind);
        Assert.That(store.TryGet("nothing.here", out var got, out _), Is.False);
        Assert.That(got, Is.Null);
    }

    [TestCaseSource(nameof(Stores))]
    public void AKeyIsWriteOnce(string kind)
    {
        using var store = CreateStore(kind);
        var first = Bytes(100, 1);
        var second = Bytes(100, 2);
        Assert.That(store.Put("k", first, SharedCacheKey.Checksum(first)), Is.True);
        Assert.That(store.Put("k", second, SharedCacheKey.Checksum(second)), Is.False, "the key is stored already: a no-op");
        store.TryGet("k", out var got, out _);
        Assert.That(got, Is.EqualTo(first), "the first object stays");
    }

    [TestCaseSource(nameof(Stores))]
    public void RacingPutsOfOneKeyAllSucceedAndOneWrites(string kind)
    {
        using var store = CreateStore(kind);
        var bytes = Bytes(200_000);
        string sum = SharedCacheKey.Checksum(bytes);
        int stored = 0;
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() => { if (store.Put("race", bytes, sum)) Interlocked.Increment(ref stored); })).ToArray();
        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
        Assert.That(stored, Is.EqualTo(1), "exactly one racer wrote; the others found the key stored");
        store.TryGet("race", out var got, out _);
        Assert.That(got, Is.EqualTo(bytes));
        Assert.That(store.Count(), Is.EqualTo(1));
    }

    [TestCaseSource(nameof(Stores))]
    public void ExpirySweepRemovesOnlyKeysUnreadSinceTheCutoff(string kind)
    {
        using var store = CreateStore(kind);
        var bytes = Bytes(64);
        string sum = SharedCacheKey.Checksum(bytes);
        store.Put("old", bytes, sum);
        store.Put("new", bytes, sum);
        // Nothing is older than a cutoff in the past.
        Assert.That(store.RemoveUnreadSince(DateTime.UtcNow.AddDays(-30)), Is.EqualTo(0));
        Assert.That(store.Count(), Is.EqualTo(2));
        // Everything was last read before a cutoff in the future.
        Assert.That(store.RemoveUnreadSince(DateTime.UtcNow.AddDays(1)), Is.EqualTo(2));
        Assert.That(store.Count(), Is.EqualTo(0));
        Assert.That(store.TryGet("old", out _, out _), Is.False);
    }

    [TestCaseSource(nameof(Stores))]
    public void ClearRemovesEverything(string kind)
    {
        using var store = CreateStore(kind);
        var bytes = Bytes(64);
        store.Put("a", bytes, SharedCacheKey.Checksum(bytes));
        store.Put("b", bytes, SharedCacheKey.Checksum(bytes));
        Assert.That(store.Clear(), Is.EqualTo(2));
        Assert.That(store.Count(), Is.EqualTo(0));
    }

    [Test]
    public void ASqlEntryThatWasAlteredIsDroppedAndCanBeStoredAgain()
    {
        using var db = OpenDatabase();
        using var store = new SqlSharedCacheStore(db);
        var bytes = Bytes(500);
        store.Put("k", bytes, SharedCacheKey.Checksum(bytes));
        using (var c = db.Open()) NebulaDatabase.Execute(c, "UPDATE shared_cache SET bytes = @b WHERE cache_key = @k", ("@b", Bytes(500, 99)), ("@k", "k"));
        Assert.That(store.TryGet("k", out _, out _), Is.False, "the stored bytes no longer match their checksum");
        Assert.That(store.Count(), Is.EqualTo(0), "the bad entry is removed");
        Assert.That(store.Put("k", bytes, SharedCacheKey.Checksum(bytes)), Is.True);
        Assert.That(store.TryGet("k", out var got, out _), Is.True);
        Assert.That(got, Is.EqualTo(bytes));
    }

    [Test]
    public void AFolderEntryThatWasAlteredIsDroppedAndCanBeStoredAgain()
    {
        var folder = Path.Combine(directory, "folder");
        using var store = new FolderSharedCacheStore(folder);
        var bytes = Bytes(500);
        store.Put("k", bytes, SharedCacheKey.Checksum(bytes));
        string file = Directory.GetFiles(folder, "*.nsc", SearchOption.AllDirectories).Single();
        var raw = File.ReadAllBytes(file);
        raw[^1] ^= 0xFF;
        File.WriteAllBytes(file, raw);
        Assert.That(store.TryGet("k", out _, out _), Is.False);
        Assert.That(File.Exists(file), Is.False, "the bad file is removed");
        Assert.That(store.Put("k", bytes, SharedCacheKey.Checksum(bytes)), Is.True);
    }

    [Test]
    public void ReadingRefreshesTheLastReadTimeOnlyWhenItIsOlderThanADay()
    {
        using var db = OpenDatabase();
        using var store = new SqlSharedCacheStore(db);
        var bytes = Bytes(64);
        store.Put("k", bytes, SharedCacheKey.Checksum(bytes));
        long LastRead()
        {
            using var c = db.Open();
            using var cmd = NebulaDatabase.Command(c, "SELECT last_read_at FROM shared_cache WHERE cache_key = 'k'");
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        long written = LastRead();
        using (var c = db.Open()) NebulaDatabase.Execute(c, "UPDATE shared_cache SET last_read_at = @t", ("@t", written - 3600));
        store.TryGet("k", out _, out _);
        Assert.That(LastRead(), Is.EqualTo(written - 3600), "an hour is inside the window: no write");
        using (var c = db.Open()) NebulaDatabase.Execute(c, "UPDATE shared_cache SET last_read_at = @t", ("@t", written - 3 * 86400));
        store.TryGet("k", out _, out _);
        Assert.That(LastRead(), Is.GreaterThanOrEqualTo(written), "three days is outside the window: refreshed");
    }

    [Test]
    public void ClearingPersistenceLeavesTheCacheAlone()
    {
        using var db = OpenDatabase();
        using var cache = new SqlSharedCacheStore(db);
        var persistence = new SqlPersistenceStore(db);
        persistence.Connect();
        try
        {
            var bytes = Bytes(1000);
            cache.Put("terrain.keep", bytes, SharedCacheKey.Checksum(bytes));
            persistence.Save(new PersistedEntityRecord { Key = "e1", PrefabName = "Crate", ContainerId = "c1", Epoch = 1 });
            bool written = false;
            persistence.WhenWritten(() => written = true);
            var sw = Stopwatch.StartNew();
            while (!written && sw.Elapsed.TotalSeconds < 10) { persistence.Tick(); Thread.Sleep(5); }
            Assert.That(persistence.KnownCount, Is.GreaterThanOrEqualTo(0));

            persistence.Clear();
            written = false;
            persistence.WhenWritten(() => written = true);
            sw.Restart();
            while (!written && sw.Elapsed.TotalSeconds < 10) { persistence.Tick(); Thread.Sleep(5); }
            Assert.That(written, Is.True, "the clear was written");

            Assert.That(cache.TryGet("terrain.keep", out var got, out _), Is.True, "persistence Clear (dev reset, world wipe) does not clear the cache");
            Assert.That(got, Is.EqualTo(bytes));
        }
        finally { persistence.Dispose(); }
    }

    [TestCase("")]
    [TestCase("has space")]
    [TestCase("slash/inside")]
    [TestCase("percent%20")]
    [TestCase("..")]
    [TestCase(".hidden")]
    [TestCase("emojié")]
    public void UnsafeKeysAreRefused(string key) => Assert.That(SharedCacheKey.TryValidate(key, out _), Is.False);

    [Test]
    public void KeysHaveALengthLimitAndASafeCharset()
    {
        Assert.That(SharedCacheKey.TryValidate("terrain.v3:9f2c-1A_b", out _), Is.True);
        Assert.That(SharedCacheKey.TryValidate(new string('a', SharedCacheKey.MaxKeyLength), out _), Is.True);
        Assert.That(SharedCacheKey.TryValidate(new string('a', SharedCacheKey.MaxKeyLength + 1), out string error), Is.False);
        Assert.That(error, Does.Contain("limit"));
    }

    // ------------------------------------------------------------------------------------------ HTTP

    private sealed class Mesh : IDisposable
    {
        public readonly OrchestratorHttpServer Http;
        public readonly SharedCacheHost Host;
        public readonly ISharedCacheStore Store;
        public readonly string Url;
        public Mesh(ISharedCacheStore store, string? token = null, int maxBytes = 1024 * 1024, int maxConcurrent = 8)
        {
            int port = FreePort();
            Url = $"http://localhost:{port}/";
            Store = store;
            Http = new OrchestratorHttpServer("localhost", (ushort)port, "<html></html>");
            Host = new SharedCacheHost(store, token, maxBytes, maxConcurrent, 30);
            Http.MapDirectPrefix(SharedCacheHost.Prefix, Host.MaxObjectBytes, req => Host.TryHandle(req, out var r) ? r : OrchestratorHttpServer.Response.Error(404, "no"));
            Http.Start();
        }
        public void Dispose() { Http.Dispose(); Store.Dispose(); }
    }

    [Test]
    public async Task AWorkerStoresAndReadsThroughTheOrchestrator()
    {
        using var mesh = new Mesh(new SqlSharedCacheStore(OpenDatabase()), token: "secret");
        using var worker = new RemoteSharedCacheClient(mesh.Url, "secret", 1024 * 1024);
        var bytes = Bytes(300_000);
        Assert.That(await worker.TryGetAsync("water.v1.x"), Is.Null, "a miss is null (HTTP 404)");
        await worker.PutAsync("water.v1.x", bytes);
        Assert.That(await worker.TryGetAsync("water.v1.x"), Is.EqualTo(bytes));

        // A second worker, as after the first was replaced, finds it.
        using var replacement = new RemoteSharedCacheClient(mesh.Url, "secret", 1024 * 1024);
        Assert.That(await replacement.TryGetAsync("water.v1.x"), Is.EqualTo(bytes));

        // Re-putting is a no-op success that keeps the first object.
        await replacement.PutAsync("water.v1.x", Bytes(300_000, 9));
        Assert.That(await worker.TryGetAsync("water.v1.x"), Is.EqualTo(bytes));

        var stats = worker.Stats;
        Assert.That(stats.Misses, Is.EqualTo(1));
        Assert.That(stats.Puts, Is.EqualTo(1));
        Assert.That(stats.Hits, Is.EqualTo(2));
        Assert.That(stats.BytesWritten, Is.EqualTo(bytes.Length));
        Assert.That(replacement.Stats.PutsExisting, Is.EqualTo(1));
        Assert.That(mesh.Host.Stats.Puts, Is.EqualTo(1));
        Assert.That(mesh.Host.Stats.PutsExisting, Is.EqualTo(1));
    }

    [Test]
    public async Task RacingWorkersBothSucceed()
    {
        using var mesh = new Mesh(new SqlSharedCacheStore(OpenDatabase()));
        using var a = new RemoteSharedCacheClient(mesh.Url);
        using var b = new RemoteSharedCacheClient(mesh.Url);
        var bytes = Bytes(100_000);
        await Task.WhenAll(a.PutAsync("race", bytes), b.PutAsync("race", bytes));
        Assert.That(a.Stats.Errors + b.Stats.Errors, Is.EqualTo(0));
        Assert.That(a.Stats.Puts + b.Stats.Puts, Is.EqualTo(1));
        Assert.That(a.Stats.PutsExisting + b.Stats.PutsExisting, Is.EqualTo(1));
        Assert.That(await a.TryGetAsync("race"), Is.EqualTo(bytes));
    }

    private static async Task<HttpResponseMessage> RawPut(string url, string key, byte[] body, string? checksum, string? token = null)
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Put, url + "cache/" + key) { Content = new ByteArrayContent(body) };
        if (checksum != null) request.Headers.TryAddWithoutValidation(SharedCacheHost.ChecksumHeader, checksum);
        if (token != null) request.Headers.TryAddWithoutValidation("X-Nebula-Token", token);
        return await http.SendAsync(request);
    }

    [Test]
    public async Task ACorruptPayloadIsRefusedAndNothingIsStored()
    {
        using var mesh = new Mesh(new MemorySharedCacheStore());
        var bytes = Bytes(5000);
        using (var wrong = await RawPut(mesh.Url, "bad", bytes, SharedCacheKey.Checksum(Bytes(5000, 1))))
            Assert.That((int)wrong.StatusCode, Is.EqualTo(400), "the body does not match its checksum header");
        using (var missing = await RawPut(mesh.Url, "bad", bytes, null))
            Assert.That((int)missing.StatusCode, Is.EqualTo(400), "the checksum header is required");
        Assert.That(mesh.Store.Count(), Is.EqualTo(0));
        using (var good = await RawPut(mesh.Url, "good", bytes, SharedCacheKey.Checksum(bytes)))
            Assert.That((int)good.StatusCode, Is.EqualTo(201));
        using (var again = await RawPut(mesh.Url, "good", bytes, SharedCacheKey.Checksum(bytes)))
            Assert.That((int)again.StatusCode, Is.EqualTo(200), "already stored");
    }

    [Test]
    public async Task AnOversizedObjectIsRefusedWith413()
    {
        using var mesh = new Mesh(new MemorySharedCacheStore(), maxBytes: 10_000);
        var big = Bytes(10_001);
        using (var response = await RawPut(mesh.Url, "big", big, SharedCacheKey.Checksum(big)))
            Assert.That((int)response.StatusCode, Is.EqualTo(413));
        var ok = Bytes(10_000);
        using (var response = await RawPut(mesh.Url, "fits", ok, SharedCacheKey.Checksum(ok)))
            Assert.That((int)response.StatusCode, Is.EqualTo(201));
        Assert.That(mesh.Store.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task TheLimitCanBeRaisedAboveTheGeneralRequestLimit()
    {
        // The general request body limit is 8 MiB; the cache takes objects up to its own limit.
        using var mesh = new Mesh(new MemorySharedCacheStore(), maxBytes: 20 * 1024 * 1024);
        using var worker = new RemoteSharedCacheClient(mesh.Url, null, 20 * 1024 * 1024);
        var bytes = Bytes(12 * 1024 * 1024);
        await worker.PutAsync("large", bytes);
        Assert.That(worker.Stats.Puts, Is.EqualTo(1));
        Assert.That(await worker.TryGetAsync("large"), Is.EqualTo(bytes));
    }

    [Test]
    public async Task ARequestWithoutTheMeshTokenIsRefused()
    {
        using var mesh = new Mesh(new MemorySharedCacheStore(), token: "secret");
        var bytes = Bytes(100);
        using (var response = await RawPut(mesh.Url, "k", bytes, SharedCacheKey.Checksum(bytes)))
            Assert.That((int)response.StatusCode, Is.EqualTo(401));
        using var worker = new RemoteSharedCacheClient(mesh.Url, "wrong");
        await worker.PutAsync("k", bytes);
        Assert.That(await worker.TryGetAsync("k"), Is.Null);
        Assert.That(mesh.Store.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task AGetCarriesTheChecksumHeaderAndAnUnknownKeyIs404()
    {
        using var mesh = new Mesh(new MemorySharedCacheStore());
        var bytes = Bytes(777);
        mesh.Store.Put("k", bytes, SharedCacheKey.Checksum(bytes));
        using var http = new HttpClient();
        using (var hit = await http.GetAsync(mesh.Url + "cache/k"))
        {
            Assert.That((int)hit.StatusCode, Is.EqualTo(200));
            Assert.That(hit.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/octet-stream"));
            Assert.That(hit.Headers.GetValues(SharedCacheHost.ChecksumHeader).Single(), Is.EqualTo(SharedCacheKey.Checksum(bytes)));
            Assert.That(await hit.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
        }
        using (var miss = await http.GetAsync(mesh.Url + "cache/unknown"))
            Assert.That((int)miss.StatusCode, Is.EqualTo(404));
        using (var bad = await http.GetAsync(mesh.Url + "cache/bad%20key"))
            Assert.That((int)bad.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task AnUnreachableOrchestratorGivesNullAndNeverThrows()
    {
        using var worker = new RemoteSharedCacheClient($"http://localhost:{FreePort()}/") { Timeout = TimeSpan.FromSeconds(5) };
        var sw = Stopwatch.StartNew();
        Assert.That(await worker.TryGetAsync("k"), Is.Null);
        Assert.DoesNotThrowAsync(() => worker.PutAsync("k", Bytes(100)));
        Assert.That(worker.Stats.Errors, Is.GreaterThanOrEqualTo(1));
        // The second call is answered from the failure backoff, not by another connection attempt.
        Assert.That(await worker.TryGetAsync("k2"), Is.Null);
        Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(5));
    }

    [Test]
    public async Task AnObjectOverTheClientLimitIsDroppedWithoutARequest()
    {
        using var mesh = new Mesh(new MemorySharedCacheStore(), maxBytes: 1_000_000);
        using var worker = new RemoteSharedCacheClient(mesh.Url, null, maxObjectBytes: 1000);
        await worker.PutAsync("big", Bytes(1001));
        await worker.PutAsync("empty", Array.Empty<byte>());
        await worker.PutAsync("bad key", Bytes(10));
        Assert.That(await worker.TryGetAsync("bad key"), Is.Null);
        Assert.That(mesh.Store.Count(), Is.EqualTo(0));
        Assert.That(mesh.Host.Stats.Puts + mesh.Host.Stats.Errors, Is.EqualTo(0));
    }

    /// <summary>A store that blocks reads until released and counts how many run at once.</summary>
    private sealed class SlowStore : ISharedCacheStore
    {
        public readonly ManualResetEventSlim Release = new(false);
        public int Running, MaxRunning;
        public string Backend => "slow";
        public bool TryGet(string key, out byte[] bytes, out string checksum)
        {
            int now = Interlocked.Increment(ref Running);
            int seen;
            while ((seen = MaxRunning) < now && Interlocked.CompareExchange(ref MaxRunning, now, seen) != seen) { }
            Release.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Decrement(ref Running);
            bytes = new byte[] { 1 };
            checksum = SharedCacheKey.Checksum(bytes);
            return true;
        }
        public bool Put(string key, byte[] bytes, string checksum) => true;
        public int RemoveUnreadSince(DateTime cutoffUtc) => 0;
        public int Clear() => 0;
        public long Count() => 0;
        public long TotalBytes() => 0;
        public void Dispose() { }
    }

    [Test]
    public async Task TheHostRefusesRequestsOverItsConcurrencyCapWith503()
    {
        var store = new SlowStore();
        var host = new SharedCacheHost(store, null, 1000, maxConcurrent: 2);
        var statuses = new System.Collections.Concurrent.ConcurrentBag<int>();
        var tasks = Enumerable.Range(0, 5).Select(i => Task.Run(() =>
        {
            host.TryHandle(new OrchestratorHttpServer.Request { Method = "GET", Path = "/cache/k" + i }, out var response);
            statuses.Add(response.Status);
        })).ToArray();
        var sw = Stopwatch.StartNew();
        while (statuses.Count < 3 && sw.Elapsed.TotalSeconds < 10) await Task.Delay(5);
        Assert.That(statuses.Count(s => s == 503), Is.EqualTo(3), "two run, three are turned away at once");
        store.Release.Set();
        await Task.WhenAll(tasks);
        Assert.That(store.MaxRunning, Is.EqualTo(2));
        Assert.That(statuses.Count(s => s == 200), Is.EqualTo(2));
    }

    [Test]
    public async Task AWorkerRunsAtMostItsConcurrencyCapOfCallsAtOnce()
    {
        var store = new SlowStore();
        using var worker = new LocalSharedCacheClient(store, maxConcurrent: 3);
        var calls = Enumerable.Range(0, 10).Select(i => worker.TryGetAsync("k" + i)).ToArray();
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref store.Running) < 3 && sw.Elapsed.TotalSeconds < 10) await Task.Delay(5);
        await Task.Delay(100);
        Assert.That(store.Running, Is.EqualTo(3), "the other seven wait for a slot");
        store.Release.Set();
        var results = await Task.WhenAll(calls);
        Assert.That(results.All(r => r != null), Is.True);
        Assert.That(store.MaxRunning, Is.EqualTo(3));
    }

    [Test]
    public async Task TheHostSweepsExpiredKeysAndClearsOnRequest()
    {
        var store = new MemorySharedCacheStore();
        var host = new SharedCacheHost(store, null, 1000, 4, expiryDays: 30);
        var bytes = Bytes(10);
        store.Put("fresh", bytes, SharedCacheKey.Checksum(bytes));
        Assert.That(host.Sweep(), Is.EqualTo(0), "read inside the window");
        var expiring = new SharedCacheHost(store, null, 1000, 4, expiryDays: 0);
        Assert.That(expiring.Sweep(), Is.EqualTo(0), "0 days keeps everything");
        Assert.That(host.Clear(), Is.EqualTo(1));
        Assert.That(store.Count(), Is.EqualTo(0));
        await Task.CompletedTask;
    }

    [Test]
    public async Task TheCacheNeedsNoPumpAndCompletesOffTheCallersThread()
    {
        using var store = new MemorySharedCacheStore();
        using var worker = new LocalSharedCacheClient(store);
        var bytes = Bytes(1000);
        var both = Task.WhenAll(worker.PutAsync("t", bytes), worker.TryGetAsync("nope"));
        Assert.That(await Task.WhenAny(both, Task.Delay(5000)), Is.SameAs(both), "completes without a Tick pump");
        Assert.That(await worker.TryGetAsync("t"), Is.EqualTo(bytes));
        NebulaSharedCache.Install(worker);
        try
        {
            Assert.That(NebulaSharedCache.IsAvailable, Is.True);
            Assert.That(await NebulaSharedCache.TryGetAsync("t"), Is.EqualTo(bytes));
            Assert.That(NebulaSharedCache.Backend, Is.EqualTo("memory"));
        }
        finally { NebulaSharedCache.Uninstall(worker); }
        Assert.That(NebulaSharedCache.IsAvailable, Is.False);
        Assert.That(await NebulaSharedCache.TryGetAsync("t"), Is.Null, "no store: a miss");
        Assert.DoesNotThrowAsync(() => NebulaSharedCache.PutAsync("t", bytes));
    }
}
