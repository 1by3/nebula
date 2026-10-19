using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The shared cache (NEB-481) on the Unity side: the folder store the Editor and single-process runs use, the
    /// worker clients, and the orchestrator's HttpListener-based server answering <c>/cache/{key}</c> with raw bytes.
    /// The SQL store and the Kestrel server are covered by the services tests.
    /// </summary>
    public class SharedCacheTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "nebula-shared-cache-editmode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }

        private static byte[] Bytes(int length, byte seed = 7)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)(seed + i * 31);
            return bytes;
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static T Wait<T>(Task<T> task)
        {
            Assert.That(task.Wait(TimeSpan.FromSeconds(20)), Is.True, "the call finished");
            return task.Result;
        }

        [Test]
        public void TheFolderStoreIsWriteOnceAndKeepsWhatItStored()
        {
            var folder = Path.Combine(_directory, "cache");
            var bytes = Bytes(250_000);
            using (var store = new FolderSharedCacheStore(folder))
            {
                Assert.That(store.TryGet("terrain.v1.a", out _, out _), Is.False);
                Assert.That(store.Put("terrain.v1.a", bytes, SharedCacheKey.Checksum(bytes)), Is.True);
                var other = Bytes(250_000, 1);
                Assert.That(store.Put("terrain.v1.a", other, SharedCacheKey.Checksum(other)), Is.False, "a stored key is never replaced");
                Assert.That(store.Count(), Is.EqualTo(1));
            }
            // A new process opening the same folder finds the object.
            using (var again = new FolderSharedCacheStore(folder))
            {
                Assert.That(again.TryGet("terrain.v1.a", out var got, out string sum), Is.True);
                Assert.That(got, Is.EqualTo(bytes));
                Assert.That(sum, Is.EqualTo(SharedCacheKey.Checksum(bytes)));
                Assert.That(again.RemoveUnreadSince(DateTime.UtcNow.AddDays(1)), Is.EqualTo(1));
                Assert.That(again.Count(), Is.EqualTo(0));
            }
        }

        [Test]
        public void TheFolderStoreRacesOfOneKeyAllSucceed()
        {
            using (var store = new FolderSharedCacheStore(Path.Combine(_directory, "cache")))
            {
                var bytes = Bytes(100_000);
                string sum = SharedCacheKey.Checksum(bytes);
                int stored = 0;
                var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => { if (store.Put("race", bytes, sum)) Interlocked.Increment(ref stored); })).ToArray();
                Assert.DoesNotThrow(() => Task.WaitAll(tasks));
                Assert.That(stored, Is.EqualTo(1));
                Assert.That(store.TryGet("race", out var got, out _), Is.True);
                Assert.That(got, Is.EqualTo(bytes));
            }
        }

        [Test]
        public void TheLocalClientStoresAndReadsWithoutBlockingTheCaller()
        {
            using (var store = new FolderSharedCacheStore(Path.Combine(_directory, "cache")))
            using (var client = new LocalSharedCacheClient(store))
            {
                var bytes = Bytes(5000);
                Assert.That(Wait(client.TryGetAsync("k")), Is.Null, "a miss is null");
                Assert.That(client.PutAsync("k", bytes).Wait(TimeSpan.FromSeconds(20)), Is.True);
                Assert.That(Wait(client.TryGetAsync("k")), Is.EqualTo(bytes));
                Assert.That(client.Stats.Hits, Is.EqualTo(1));
                Assert.That(client.Stats.Misses, Is.EqualTo(1));
                Assert.That(client.Stats.Puts, Is.EqualTo(1));
                // Invalid keys and oversized objects are dropped, never thrown.
                Assert.That(Wait(client.TryGetAsync("not a key")), Is.Null);
                Assert.DoesNotThrow(() => client.PutAsync("not a key", bytes).Wait());
            }
        }

        [Test]
        public void TheFacadeIsAMissWhenNoCacheIsInstalled()
        {
            NebulaSharedCache.Install(null);
            Assert.That(NebulaSharedCache.IsAvailable, Is.False);
            Assert.That(NebulaSharedCache.Backend, Is.EqualTo("off"));
            Assert.That(Wait(NebulaSharedCache.TryGetAsync("k")), Is.Null);
            Assert.That(NebulaSharedCache.PutAsync("k", Bytes(10)).IsCompleted, Is.True);
        }

        [Test]
        public void AWorkerReachesTheOrchestratorsCacheOverTheUnityHttpServer()
        {
            int port = FreePort();
            using (var http = new OrchestratorHttpServer("localhost", (ushort)port, "<html></html>"))
            using (var store = new MemorySharedCacheStore())
            {
                var host = new SharedCacheHost(store, "secret", maxObjectBytes: 20 * 1024 * 1024, maxConcurrent: 4, expiryDays: 30);
                http.MapDirectPrefix(SharedCacheHost.Prefix, host.MaxObjectBytes, req => host.TryHandle(req, out var answer) ? answer : OrchestratorHttpServer.Response.Error(404, "no"));
                http.Start();
                string url = $"http://localhost:{port}/";
                using (var worker = new RemoteSharedCacheClient(url, "secret", 20 * 1024 * 1024))
                {
                    var bytes = Bytes(12 * 1024 * 1024);
                    Assert.That(Wait(worker.TryGetAsync("water.v1.x")), Is.Null, "404 is a miss");
                    Assert.That(worker.PutAsync("water.v1.x", bytes).Wait(TimeSpan.FromSeconds(30)), Is.True);
                    Assert.That(Wait(worker.TryGetAsync("water.v1.x")), Is.EqualTo(bytes), "bigger than the general 8 MiB request limit");
                    Assert.That(store.Count(), Is.EqualTo(1));
                    Assert.That(host.Stats.Puts, Is.EqualTo(1));
                }
                using (var wrongToken = new RemoteSharedCacheClient(url, "nope"))
                {
                    Assert.That(Wait(wrongToken.TryGetAsync("water.v1.x")), Is.Null);
                    Assert.That(wrongToken.Stats.Errors, Is.EqualTo(1));
                }
                using (var small = new RemoteSharedCacheClient(url, "secret", maxObjectBytes: 1000))
                {
                    small.PutAsync("small", Bytes(1001)).Wait();
                    Assert.That(store.Count(), Is.EqualTo(1), "the client does not send an object over its own limit");
                }
            }
        }

        [Test]
        public void AnObjectOverTheHostLimitIsRefusedWith413()
        {
            int port = FreePort();
            using (var http = new OrchestratorHttpServer("localhost", (ushort)port, "<html></html>"))
            using (var store = new MemorySharedCacheStore())
            {
                var host = new SharedCacheHost(store, null, maxObjectBytes: 10_000);
                http.MapDirectPrefix(SharedCacheHost.Prefix, host.MaxObjectBytes, req => host.TryHandle(req, out var answer) ? answer : OrchestratorHttpServer.Response.Error(404, "no"));
                http.Start();
                // The worker thinks 100 000 bytes is fine; the host's limit is 10 000.
                using (var worker = new RemoteSharedCacheClient($"http://localhost:{port}/", null, maxObjectBytes: 100_000))
                {
                    worker.PutAsync("big", Bytes(10_001)).Wait();
                    Assert.That(store.Count(), Is.EqualTo(0));
                    Assert.That(worker.Stats.Puts, Is.EqualTo(0));
                    worker.PutAsync("fits", Bytes(10_000)).Wait();
                    Assert.That(store.Count(), Is.EqualTo(1));
                }
            }
        }
    }
}
