using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Nebula
{
    /// <summary>
    /// What a process has done with the shared cache since it started. Read it from
    /// <see cref="NebulaSharedCache.Stats"/> on a worker, or from <see cref="SharedCacheHost.Stats"/> on the orchestrator.
    /// </summary>
    public readonly struct SharedCacheStats
    {
        /// <summary>Reads that found an object.</summary>
        public readonly long Hits;
        /// <summary>Reads that found nothing.</summary>
        public readonly long Misses;
        /// <summary>Reads and writes that failed: the backend or the network was unavailable, timed out, or answered with something unusable.</summary>
        public readonly long Errors;
        /// <summary>Writes that stored a new object.</summary>
        public readonly long Puts;
        /// <summary>Writes that found the key already stored.</summary>
        public readonly long PutsExisting;
        /// <summary>Bytes returned by hits.</summary>
        public readonly long BytesRead;
        /// <summary>Bytes of the objects that writes stored.</summary>
        public readonly long BytesWritten;

        public SharedCacheStats(long hits, long misses, long errors, long puts, long putsExisting, long bytesRead, long bytesWritten)
        {
            Hits = hits; Misses = misses; Errors = errors; Puts = puts; PutsExisting = putsExisting; BytesRead = bytesRead; BytesWritten = bytesWritten;
        }

        public override string ToString() =>
            $"hits {Hits}, misses {Misses}, errors {Errors}, puts {Puts} (+{PutsExisting} already stored), read {BytesRead} B, written {BytesWritten} B";
    }

    /// <summary>Thread-safe counters behind <see cref="SharedCacheStats"/>.</summary>
    internal sealed class SharedCacheCounters
    {
        private long _hits, _misses, _errors, _puts, _putsExisting, _bytesRead, _bytesWritten;

        public void Hit(long bytes) { Interlocked.Increment(ref _hits); Interlocked.Add(ref _bytesRead, bytes); }
        public void Miss() => Interlocked.Increment(ref _misses);
        public void Error() => Interlocked.Increment(ref _errors);
        public void Put(long bytes) { Interlocked.Increment(ref _puts); Interlocked.Add(ref _bytesWritten, bytes); }
        public void PutExisting() => Interlocked.Increment(ref _putsExisting);

        public SharedCacheStats Snapshot() => new SharedCacheStats(
            Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), Interlocked.Read(ref _errors),
            Interlocked.Read(ref _puts), Interlocked.Read(ref _putsExisting), Interlocked.Read(ref _bytesRead), Interlocked.Read(ref _bytesWritten));
    }

    /// <summary>
    /// A worker's way to the shared cache. <see cref="NebulaSharedCache"/> holds the one a process uses; the
    /// bootstrap installs <see cref="LocalSharedCacheClient"/> or <see cref="RemoteSharedCacheClient"/>.
    /// </summary>
    public interface ISharedCacheClient : IDisposable
    {
        /// <summary>"folder", "memory", "remote", "sqlite" or "postgres": where the objects end up.</summary>
        string Backend { get; }

        /// <summary>What this client has done so far.</summary>
        SharedCacheStats Stats { get; }

        /// <summary>The object stored under <paramref name="key"/>, or null on a miss, an error, a timeout or a cancelled call.</summary>
        Task<byte[]> TryGetAsync(string key, CancellationToken ct = default);

        /// <summary>Store <paramref name="bytes"/> under <paramref name="key"/> unless it is already stored. Never throws for a backend failure.</summary>
        Task PutAsync(string key, byte[] bytes, CancellationToken ct = default);
    }

    /// <summary>
    /// What every client does around a backend call. Both calls do their work on the thread pool, so the task
    /// completes off the Unity main thread and the call returns before any disk or network work starts. A client lets
    /// at most <c>maxConcurrent</c> calls run at once (the rest wait), gives each call <see cref="Timeout"/> to finish,
    /// backs off for a few seconds after a failure so a stopped orchestrator does not make every call wait out its
    /// timeout, and writes one log line per call.
    /// </summary>
    public abstract class SharedCacheClientBase : ISharedCacheClient
    {
        /// <summary>Seconds a call may run once it has started.</summary>
        public const float DefaultTimeoutSeconds = 30f;
        /// <summary>Seconds after a failed call during which calls are answered as errors at once.</summary>
        public const float FailureBackoffSeconds = 5f;
        /// <summary>Calls a client runs at the same time, by default.</summary>
        public const int DefaultMaxConcurrent = 4;

        private readonly SemaphoreSlim _gate;
        private readonly SharedCacheCounters _counters = new SharedCacheCounters();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _backoffUntilMs;
        private volatile bool _disposed;

        /// <summary>Largest object this client reads or writes, in bytes.</summary>
        public int MaxObjectBytes { get; }

        /// <summary>Longest a call may run once it has started.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

        public abstract string Backend { get; }

        public SharedCacheStats Stats => _counters.Snapshot();

        protected SharedCacheClientBase(int maxObjectBytes, int maxConcurrent)
        {
            MaxObjectBytes = maxObjectBytes > 0 ? maxObjectBytes : SharedCacheKey.DefaultMaxObjectBytes;
            _gate = new SemaphoreSlim(Math.Max(1, maxConcurrent));
        }

        /// <summary>Read the object. Null for a miss; throw for a failure. Runs on the thread pool, inside the concurrency limit.</summary>
        protected abstract Task<byte[]> GetCoreAsync(string key, CancellationToken ct);

        /// <summary>Store the object. True when it was written, false when the key was already stored; throw for a failure.</summary>
        protected abstract Task<bool> PutCoreAsync(string key, byte[] bytes, string checksum, CancellationToken ct);

        public Task<byte[]> TryGetAsync(string key, CancellationToken ct = default)
        {
            if (!SharedCacheKey.TryValidate(key, out string invalid))
            {
                NebulaLog.Warn($"shared cache: get refused: {invalid}");
                return Task.FromResult<byte[]>(null);
            }
            if (_disposed || ct.IsCancellationRequested) return Task.FromResult<byte[]>(null);
            return Task.Run(() => GetGuardedAsync(key, ct));
        }

        public Task PutAsync(string key, byte[] bytes, CancellationToken ct = default)
        {
            if (!SharedCacheKey.TryValidate(key, out string invalid))
            {
                NebulaLog.Warn($"shared cache: put refused: {invalid}");
                return Task.CompletedTask;
            }
            if (bytes == null || bytes.Length == 0)
            {
                NebulaLog.Warn($"shared cache: put {key} refused: the object is empty");
                return Task.CompletedTask;
            }
            if (bytes.Length > MaxObjectBytes)
            {
                NebulaLog.Warn($"shared cache: put {key} refused: {bytes.Length} bytes is over the {MaxObjectBytes} byte limit");
                return Task.CompletedTask;
            }
            if (_disposed || ct.IsCancellationRequested) return Task.CompletedTask;
            return Task.Run(() => PutGuardedAsync(key, bytes, ct));
        }

        private async Task<byte[]> GetGuardedAsync(string key, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            if (BackingOff()) { _counters.Error(); NebulaLog.Info($"shared cache: get {key}: skipped, the backend failed a moment ago"); return null; }
            bool entered = false;
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                entered = true;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(Timeout);
                    byte[] bytes = await GetCoreAsync(key, timeout.Token).ConfigureAwait(false);
                    if (bytes != null && bytes.Length > MaxObjectBytes)
                    {
                        NebulaLog.Warn($"shared cache: get {key}: the stored object is {bytes.Length} bytes, over the {MaxObjectBytes} byte limit; treated as a miss");
                        bytes = null;
                    }
                    if (bytes == null) { _counters.Miss(); NebulaLog.Info($"shared cache: get {key}: miss, {watch.ElapsedMilliseconds} ms"); }
                    else { _counters.Hit(bytes.Length); NebulaLog.Info($"shared cache: get {key}: hit, {bytes.Length} bytes, {watch.ElapsedMilliseconds} ms"); }
                    return bytes;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception e)
            {
                Failed(e);
                _counters.Error();
                NebulaLog.Warn($"shared cache: get {key}: failed after {watch.ElapsedMilliseconds} ms: {Describe(e)}");
                return null;
            }
            finally
            {
                if (entered) _gate.Release();
            }
        }

        private async Task PutGuardedAsync(string key, byte[] bytes, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            if (BackingOff()) { _counters.Error(); NebulaLog.Info($"shared cache: put {key}: skipped, the backend failed a moment ago"); return; }
            bool entered = false;
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                entered = true;
                string checksum = SharedCacheKey.Checksum(bytes);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(Timeout);
                    bool written = await PutCoreAsync(key, bytes, checksum, timeout.Token).ConfigureAwait(false);
                    if (written) _counters.Put(bytes.Length); else _counters.PutExisting();
                    NebulaLog.Info($"shared cache: put {key}: {(written ? "stored" : "already stored")}, {bytes.Length} bytes, {watch.ElapsedMilliseconds} ms");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Failed(e);
                _counters.Error();
                NebulaLog.Warn($"shared cache: put {key}: failed after {watch.ElapsedMilliseconds} ms: {Describe(e)}");
            }
            finally
            {
                if (entered) _gate.Release();
            }
        }

        private bool BackingOff() => Interlocked.Read(ref _backoffUntilMs) > _clock.ElapsedMilliseconds;

        private void Failed(Exception e)
        {
            // A refused or oversized object is the caller's doing, not the backend being down.
            if (e is SharedCacheRefusedException) return;
            Interlocked.Exchange(ref _backoffUntilMs, _clock.ElapsedMilliseconds + (long)(FailureBackoffSeconds * 1000));
        }

        private static string Describe(Exception e) => e is OperationCanceledException ? "timed out" : e.GetBaseException().Message;

        public virtual void Dispose() => _disposed = true;
    }

    /// <summary>A backend refused one object (too large, failed its checksum). Not a sign that the backend is down.</summary>
    internal sealed class SharedCacheRefusedException : Exception
    {
        public SharedCacheRefusedException(string message) : base(message) { }
    }

    /// <summary>
    /// <see cref="ISharedCacheClient"/> over a store in this process: a folder under the project or the persistent data
    /// path, or the store an orchestrator in the same process hosts.
    /// </summary>
    public sealed class LocalSharedCacheClient : SharedCacheClientBase
    {
        private readonly ISharedCacheStore _store;

        public LocalSharedCacheClient(ISharedCacheStore store, int maxObjectBytes = SharedCacheKey.DefaultMaxObjectBytes, int maxConcurrent = DefaultMaxConcurrent)
            : base(maxObjectBytes, maxConcurrent)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public override string Backend => _store.Backend;

        protected override Task<byte[]> GetCoreAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_store.TryGet(key, out byte[] bytes, out _) ? bytes : null);
        }

        protected override Task<bool> PutCoreAsync(string key, byte[] bytes, string checksum, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_store.Put(key, bytes, checksum));
        }
    }

    /// <summary>
    /// <see cref="ISharedCacheClient"/> for a worker of a mesh or of Nebula Cloud: <c>GET</c> and <c>PUT</c>
    /// <c>/cache/{key}</c> on the orchestrator (<see cref="SharedCacheHost"/>), over the same address and mesh token
    /// that <see cref="RemotePersistenceStore"/> uses. The body is the raw object. Both ends check it against the
    /// SHA-256 in the <c>X-Nebula-Checksum</c> header, and a mismatch is a miss on a read and a refused write.
    /// </summary>
    public sealed class RemoteSharedCacheClient : SharedCacheClientBase
    {
        private readonly string _baseUrl;
        private readonly string _token;
        private readonly HttpClient _http;

        public RemoteSharedCacheClient(string url, string token = null, int maxObjectBytes = SharedCacheKey.DefaultMaxObjectBytes, int maxConcurrent = DefaultMaxConcurrent)
            : base(maxObjectBytes, maxConcurrent)
        {
            _baseUrl = (url ?? "").TrimEnd('/');
            _token = string.IsNullOrEmpty(token) ? null : token;
            _http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        }

        public override string Backend => "remote";

        /// <summary>The orchestrator address this client calls.</summary>
        public string Url => _baseUrl;

        protected override async Task<byte[]> GetCoreAsync(string key, CancellationToken ct)
        {
            if (_baseUrl.Length == 0) throw new InvalidOperationException("no orchestrator address (-nebula-control-plane <url>)");
            using (var request = NebulaHttp.Request(HttpMethod.Get, _baseUrl + SharedCacheHost.Prefix + "/" + key, _token))
            using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                string expected = null;
                if (response.Headers.TryGetValues(SharedCacheHost.ChecksumHeader, out var values)) foreach (string v in values) { expected = v; break; }
                if (!SharedCacheKey.SameChecksum(expected, SharedCacheKey.Checksum(bytes)))
                {
                    NebulaLog.Warn($"shared cache: get {key}: the bytes do not match the checksum the orchestrator sent; treated as a miss");
                    return null;
                }
                return bytes;
            }
        }

        protected override async Task<bool> PutCoreAsync(string key, byte[] bytes, string checksum, CancellationToken ct)
        {
            if (_baseUrl.Length == 0) throw new InvalidOperationException("no orchestrator address (-nebula-control-plane <url>)");
            using (var request = NebulaHttp.Request(HttpMethod.Put, _baseUrl + SharedCacheHost.Prefix + "/" + key, _token))
            {
                request.Headers.TryAddWithoutValidation(SharedCacheHost.ChecksumHeader, checksum);
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status == 201) return true;
                    if (status == 200) return false;
                    if (status == 400 || status == 413) throw new SharedCacheRefusedException($"the orchestrator refused the object (HTTP {status})");
                    throw new HttpRequestException($"HTTP {status}");
                }
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            try { _http.CancelPendingRequests(); } catch { }
            _http.Dispose();
        }
    }
}
