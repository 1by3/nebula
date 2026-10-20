using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Where the shared cache keeps its objects: a folder (<see cref="FolderSharedCacheStore"/>), the orchestrator's
    /// database (<c>SqlSharedCacheStore</c> in the standalone orchestrator), or memory (<see cref="MemorySharedCacheStore"/>).
    /// <para>
    /// A store is write-once: <see cref="Put"/> never replaces an object that is already there, because a key names
    /// its content. Every method is synchronous, safe to call from any thread and may block on disk or the network, so
    /// call it from a worker thread, never from a tick. A store throws when its backend fails; the caller
    /// (<see cref="SharedCacheHost"/> or a client) turns that into a miss or a refused write.
    /// </para>
    /// </summary>
    public interface ISharedCacheStore : IDisposable
    {
        /// <summary>"folder", "memory", "sqlite" or "postgres".</summary>
        string Backend { get; }

        /// <summary>
        /// The object stored under <paramref name="key"/>. False on a miss, and also when the stored bytes no longer
        /// match their checksum (the entry is removed, so the next put can replace it). Refreshes the entry's last-read
        /// time when it is more than <see cref="SharedCacheKey.TouchIntervalSeconds"/> old.
        /// </summary>
        bool TryGet(string key, out byte[] bytes, out string checksum);

        /// <summary>
        /// Store <paramref name="bytes"/> under <paramref name="key"/> unless the key is already stored. True when this
        /// call wrote it, false when the key was already there (also the answer to a racing put). The caller has
        /// verified <paramref name="checksum"/> (<see cref="SharedCacheKey.Checksum"/>) against <paramref name="bytes"/>.
        /// </summary>
        bool Put(string key, byte[] bytes, string checksum);

        /// <summary>Remove every entry that was last read before <paramref name="cutoffUtc"/>. Returns how many were removed.</summary>
        int RemoveUnreadSince(DateTime cutoffUtc);

        /// <summary>Remove every entry. Returns how many were removed. Persisted entities are not affected.</summary>
        int Clear();

        /// <summary>How many objects the store holds.</summary>
        long Count();

        /// <summary>The total size of the objects the store holds, in bytes.</summary>
        long TotalBytes();
    }

    /// <summary>
    /// <see cref="ISharedCacheStore"/> in memory: nothing survives the process. For tests and for runs that keep
    /// nothing (<c>-nebula-shared-cache-mode memory</c>).
    /// </summary>
    public sealed class MemorySharedCacheStore : ISharedCacheStore
    {
        private sealed class Entry
        {
            public byte[] Bytes;
            public string Checksum;
            public DateTime LastRead;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public string Backend => "memory";

        public bool TryGet(string key, out byte[] bytes, out string checksum)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var e)) { bytes = null; checksum = null; return false; }
                if ((DateTime.UtcNow - e.LastRead).TotalSeconds > SharedCacheKey.TouchIntervalSeconds) e.LastRead = DateTime.UtcNow;
                bytes = e.Bytes;
                checksum = e.Checksum;
                return true;
            }
        }

        public bool Put(string key, byte[] bytes, string checksum)
        {
            lock (_gate)
            {
                if (_entries.ContainsKey(key)) return false;
                _entries[key] = new Entry { Bytes = bytes, Checksum = checksum, LastRead = DateTime.UtcNow };
                return true;
            }
        }

        public int RemoveUnreadSince(DateTime cutoffUtc)
        {
            lock (_gate)
            {
                var stale = new List<string>();
                foreach (var kv in _entries) if (kv.Value.LastRead < cutoffUtc) stale.Add(kv.Key);
                foreach (var k in stale) _entries.Remove(k);
                return stale.Count;
            }
        }

        public int Clear()
        {
            lock (_gate)
            {
                int n = _entries.Count;
                _entries.Clear();
                return n;
            }
        }

        public long Count() { lock (_gate) return _entries.Count; }

        public long TotalBytes()
        {
            lock (_gate)
            {
                long total = 0;
                foreach (var e in _entries.Values) total += e.Bytes.Length;
                return total;
            }
        }

        public void Dispose() { }
    }
}
