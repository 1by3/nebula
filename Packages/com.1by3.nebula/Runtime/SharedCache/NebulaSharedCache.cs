using System.Threading;
using System.Threading.Tasks;

namespace Nebula
{
    /// <summary>
    /// A cache that every worker of a mesh shares, for data that is expensive to compute and the same every time:
    /// baked terrain, path data, generated layouts. A worker that needs such data asks the cache first
    /// (<see cref="TryGetAsync"/>) and, when it had to compute the data, stores it (<see cref="PutAsync"/>) so that no
    /// other worker, including a replacement for one that was shut down, has to compute it again.
    /// <para>
    /// The cache is content-addressed and write-once. Choose a key that names the exact inputs of the data, such as
    /// <c>terrain.v3.&lt;hash of the seed and settings&gt;</c>: the same key must always mean the same bytes. A key that
    /// is already stored is never replaced, and two workers that store the same key at once both succeed. To change
    /// what an existing key stands for, change the key (for example by bumping a version in it).
    /// </para>
    /// <para>
    /// Both calls are safe from any thread. They return at once, do their work on the thread pool and complete there,
    /// so they never block a tick and need no pump on the main thread. They do not throw for a backend failure: a
    /// read that cannot be answered gives null, a write that cannot be stored is dropped, and the worker computes the
    /// data itself. Treat the cache as an optimization, never as the only copy.
    /// </para>
    /// <para>
    /// Where the data lives depends on how the process runs, and the choice is made by the bootstrap
    /// (<see cref="NebulaConfig.SharedCacheMode"/>): in the Editor (including Multiplayer Play Mode) and in a
    /// single-process run it is a folder on the local disk; in a mesh started with <c>nebula start</c> and in
    /// Nebula Cloud it is stored by the orchestrator, in its database, and reached over HTTP. The cache is separate
    /// from persisted entities: clearing persistence does not clear it.
    /// </para>
    /// </summary>
    public static class NebulaSharedCache
    {
        private static volatile ISharedCacheClient _client;

        /// <summary>True when this process has a cache. When false, <see cref="TryGetAsync"/> always gives null and <see cref="PutAsync"/> does nothing.</summary>
        public static bool IsAvailable => _client != null;

        /// <summary>"folder", "memory", "remote", "sqlite" or "postgres", or "off" when there is no cache.</summary>
        public static string Backend => _client != null ? _client.Backend : "off";

        /// <summary>The counters of this process's cache client; all zero when there is no cache.</summary>
        public static SharedCacheStats Stats => _client != null ? _client.Stats : default;

        /// <summary>
        /// The object stored under <paramref name="key"/>, or null when there is none, the cache is unavailable, the
        /// request fails or times out, or <paramref name="ct"/> is cancelled. A key must be 1 to
        /// <see cref="SharedCacheKey.MaxKeyLength"/> characters from letters, digits and <c>. _ - :</c>, starting with a
        /// letter or digit; any other key is logged and answered with null.
        /// </summary>
        public static Task<byte[]> TryGetAsync(string key, CancellationToken ct = default)
        {
            var client = _client;
            return client != null ? client.TryGetAsync(key, ct) : Task.FromResult<byte[]>(null);
        }

        /// <summary>
        /// Store <paramref name="bytes"/> under <paramref name="key"/> unless that key is already stored. The task completes
        /// when the object is stored, found to be stored already, or given up on; it never faults for a backend
        /// failure. Do not change <paramref name="bytes"/> until the task completes. An empty object, one over
        /// <see cref="NebulaConfig.SharedCacheMaxObjectBytes"/>, or an invalid key is logged and dropped.
        /// </summary>
        public static Task PutAsync(string key, byte[] bytes, CancellationToken ct = default)
        {
            var client = _client;
            return client != null ? client.PutAsync(key, bytes, ct) : Task.CompletedTask;
        }

        /// <summary>
        /// Make <paramref name="client"/> this process's cache. The bootstrap calls this; call it yourself only to
        /// supply your own <see cref="ISharedCacheClient"/>, for example in a test. Pass null to turn the cache off.
        /// The previous client is not disposed.
        /// </summary>
        public static void Install(ISharedCacheClient client) => _client = client;

        /// <summary>Turn the cache off if <paramref name="client"/> is the one installed.</summary>
        public static void Uninstall(ISharedCacheClient client)
        {
            if (ReferenceEquals(_client, client)) _client = null;
        }
    }
}
