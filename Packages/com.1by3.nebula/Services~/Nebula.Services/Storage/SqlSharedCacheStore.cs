using System;
using System.Data.Common;

namespace Nebula
{
    /// <summary>
    /// <see cref="ISharedCacheStore"/> in the orchestrator's database (<see cref="NebulaDatabase"/>: SQLite or
    /// PostgreSQL), table <c>shared_cache</c>. It is separate from <c>nebula_entity</c>, so persistence's
    /// <see cref="IPersistenceStore.Clear"/> and a world wipe leave it alone. Each call uses its own pooled connection,
    /// so reads and writes run in parallel with persistence's writer thread; a write is one
    /// <c>INSERT … ON CONFLICT DO NOTHING</c>, which makes the table write-once and lets two racing puts both succeed.
    /// </summary>
    public sealed class SqlSharedCacheStore : ISharedCacheStore
    {
        private readonly NebulaDatabase _db;

        public SqlSharedCacheStore(NebulaDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public string Backend => _db.Provider;

        public bool TryGet(string key, out byte[] bytes, out string checksum)
        {
            bytes = null;
            checksum = null;
            long lastRead;
            using (var c = _db.Open())
            {
                using (var cmd = NebulaDatabase.Command(c, "SELECT bytes, checksum, last_read_at FROM shared_cache WHERE cache_key = @key", ("@key", key)))
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read()) return false;
                    bytes = reader.GetFieldValue<byte[]>(0);
                    checksum = reader.GetString(1);
                    lastRead = reader.GetInt64(2);
                }
                if (!SharedCacheKey.SameChecksum(SharedCacheKey.Checksum(bytes), checksum))
                {
                    // Altered in the database: drop it so the next put can store the real object.
                    NebulaDatabase.Execute(c, "DELETE FROM shared_cache WHERE cache_key = @key", ("@key", key));
                    bytes = null;
                    checksum = null;
                    return false;
                }
                long now = Now();
                if (now - lastRead > SharedCacheKey.TouchIntervalSeconds)
                    NebulaDatabase.Execute(c, "UPDATE shared_cache SET last_read_at = @now WHERE cache_key = @key", ("@now", now), ("@key", key));
            }
            return true;
        }

        public bool Put(string key, byte[] bytes, string checksum)
        {
            long now = Now();
            using (var c = _db.Open())
            {
                int written = NebulaDatabase.Execute(c,
                    "INSERT INTO shared_cache (cache_key, bytes, checksum, size, created_at, last_read_at) VALUES (@key, @bytes, @checksum, @size, @now, @now) ON CONFLICT (cache_key) DO NOTHING",
                    ("@key", key), ("@bytes", bytes), ("@checksum", checksum), ("@size", (long)bytes.Length), ("@now", now));
                return written > 0;
            }
        }

        public int RemoveUnreadSince(DateTime cutoffUtc)
        {
            using (var c = _db.Open())
                return NebulaDatabase.Execute(c, "DELETE FROM shared_cache WHERE last_read_at < @cutoff", ("@cutoff", new DateTimeOffset(DateTime.SpecifyKind(cutoffUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()));
        }

        public int Clear()
        {
            using (var c = _db.Open())
                return NebulaDatabase.Execute(c, "DELETE FROM shared_cache");
        }

        public long Count()
        {
            using (var c = _db.Open())
            using (var cmd = NebulaDatabase.Command(c, "SELECT COUNT(*) FROM shared_cache"))
                return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public long TotalBytes()
        {
            using (var c = _db.Open())
            using (var cmd = NebulaDatabase.Command(c, "SELECT COALESCE(SUM(size), 0) FROM shared_cache"))
                return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public void Dispose() { }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
