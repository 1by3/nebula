using System;
using System.Data.Common;
using System.IO;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Nebula
{
    /// <summary>
    /// The standalone orchestrator's database: SQLite (a file next to the orchestrator, the default for a local
    /// mesh) or PostgreSQL (for a deployed mesh), behind one ADO.NET surface. It holds three tables:
    /// <c>nebula_control_plane</c> (the control plane document, see <see cref="SqlControlPlaneStorage"/>) and
    /// <c>nebula_entity</c> (saved entities, see <see cref="SqlPersistenceStore"/>), and
    /// <c>nebula_gateway_session</c> (gateway admission claims), <c>nebula_scope</c> (scope-key claims). <see cref="EnsureSchema"/>
    /// creates them if they are missing; every SQL statement is written once and differs only in the few type
    /// names the two engines disagree on.
    /// </summary>
    public sealed class NebulaDatabase : IDisposable
    {
        /// <summary>"sqlite" or "postgres".</summary>
        public string Provider { get; }
        /// <summary>The URL as configured, password masked.</summary>
        public string Display { get; }
        private readonly string _connectionString;
        private readonly object _schemaGate = new object();
        private bool _schemaReady;

        private NebulaDatabase(string provider, string connectionString, string display)
        {
            Provider = provider;
            _connectionString = connectionString;
            Display = display;
        }

        /// <summary>Prepare the database behind <paramref name="url"/>. Nothing is connected yet; <see cref="Open"/> does that.</summary>
        public static NebulaDatabase Open(DatabaseUrl url)
        {
            switch (url.Scheme)
            {
                case "sqlite":
                {
                    string path = Path.GetFullPath(url.Target);
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var b = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true };
                    return new NebulaDatabase("sqlite", b.ToString(), "sqlite:" + path);
                }
                case "postgres":
                    return new NebulaDatabase("postgres", ToNpgsql(url.Target), url.Display);
                default:
                    throw new ArgumentException($"no database engine for '{url.Scheme}:'");
            }
        }

        /// <summary><c>postgres://user:password@host:port/db?sslmode=require</c> to an Npgsql connection string; a <c>Host=...;</c> string passes through.</summary>
        private static string ToNpgsql(string url)
        {
            if (!url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)) return url;
            var uri = new Uri(url);
            var b = new NpgsqlConnectionStringBuilder { Host = uri.Host };
            if (uri.Port > 0) b.Port = uri.Port;
            string db = uri.AbsolutePath.Trim('/');
            if (db.Length > 0) b.Database = Uri.UnescapeDataString(db);
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                int colon = uri.UserInfo.IndexOf(':');
                b.Username = Uri.UnescapeDataString(colon < 0 ? uri.UserInfo : uri.UserInfo.Substring(0, colon));
                if (colon >= 0) b.Password = Uri.UnescapeDataString(uri.UserInfo.Substring(colon + 1));
            }
            if (!string.IsNullOrEmpty(uri.Query))
            {
                foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = Uri.UnescapeDataString(pair.Substring(0, eq)), value = Uri.UnescapeDataString(pair.Substring(eq + 1));
                    // libpq spellings the URL form is usually written with.
                    if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase)) b.SslMode = Enum.Parse<SslMode>(value, true);
                    else if (key.Equals("application_name", StringComparison.OrdinalIgnoreCase)) b.ApplicationName = value;
                    else b[key] = value;
                }
            }
            if (string.IsNullOrEmpty(b.ApplicationName)) b.ApplicationName = "nebula-orchestrator";
            return b.ToString();
        }

        /// <summary>An open connection with the schema in place. Throws when the database cannot be reached.</summary>
        public DbConnection Open()
        {
            DbConnection c = Provider == "sqlite" ? new SqliteConnection(_connectionString) : new NpgsqlConnection(_connectionString);
            try
            {
                c.Open();
                if (Provider == "sqlite") Execute(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
                EnsureSchema(c);
                return c;
            }
            catch
            {
                c.Dispose();
                throw;
            }
        }

        /// <summary>Type names where the engines differ.</summary>
        public string Blob => Provider == "sqlite" ? "BLOB" : "BYTEA";
        public string Double => Provider == "sqlite" ? "REAL" : "DOUBLE PRECISION";

        public void EnsureSchema(DbConnection c)
        {
            lock (_schemaGate)
            {
                if (_schemaReady) return;
                Execute(c, "CREATE TABLE IF NOT EXISTS nebula_control_plane (id INTEGER PRIMARY KEY, json TEXT NOT NULL, updated_at BIGINT NOT NULL)");
                Execute(c, "CREATE TABLE IF NOT EXISTS nebula_gateway_session (identity TEXT PRIMARY KEY, state TEXT NOT NULL)");
                // The uniqueness constraint behind scope activation (docs/scope-activation.md D5): the primary key
                // is the scope key, so exactly one of two racing activations inserts and both read the same row.
                Execute(c, "CREATE TABLE IF NOT EXISTS nebula_scope (scope_key TEXT PRIMARY KEY, definition TEXT NOT NULL, created_at BIGINT NOT NULL)");
                Execute(c, $@"CREATE TABLE IF NOT EXISTS nebula_entity (
                    entity_key TEXT PRIMARY KEY,
                    prefab_id INTEGER NOT NULL,
                    prefab_name TEXT NOT NULL,
                    scene_id BIGINT NOT NULL,
                    container_id TEXT NOT NULL,
                    carrier_key TEXT NOT NULL,
                    pos_x {Double} NOT NULL, pos_y {Double} NOT NULL, pos_z {Double} NOT NULL,
                    rot_x {Double} NOT NULL, rot_y {Double} NOT NULL, rot_z {Double} NOT NULL, rot_w {Double} NOT NULL,
                    vel_x {Double} NOT NULL, vel_y {Double} NOT NULL, vel_z {Double} NOT NULL,
                    epoch BIGINT NOT NULL,
                    server_driven BOOLEAN NOT NULL,
                    owned BOOLEAN NOT NULL,
                    name TEXT NOT NULL,
                    state {Blob},
                    version BIGINT NOT NULL,
                    saved_at BIGINT NOT NULL,
                    saved_by TEXT NOT NULL,
                    scope_key TEXT NOT NULL DEFAULT '')");
                Execute(c, "CREATE INDEX IF NOT EXISTS nebula_entity_container ON nebula_entity (container_id)");
                Execute(c, "CREATE INDEX IF NOT EXISTS nebula_entity_carrier ON nebula_entity (carrier_key)");
                // The scope key (EntityLocation.ScopeKey) was added after the table existed in deployed databases.
                // SQLite has no ADD COLUMN IF NOT EXISTS, so the failure of a repeated add is the "already there" signal
                // on both engines; a row from before the column reads as the public world.
                try { Execute(c, "ALTER TABLE nebula_entity ADD COLUMN scope_key TEXT NOT NULL DEFAULT ''"); }
                catch (DbException) { }
                // Added with the column, so "has this scope anything saved?" (IPersistenceStore.CountRecords) is an
                // index count rather than a table scan; it has to come after the ALTER on a database that predates it.
                Execute(c, "CREATE INDEX IF NOT EXISTS nebula_entity_scope ON nebula_entity (scope_key)");
                _schemaReady = true;
            }
        }

        public static int Execute(DbConnection c, string sql, params (string name, object value)[] args)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, args);
            return cmd.ExecuteNonQuery();
        }

        public static DbCommand Command(DbConnection c, string sql, params (string name, object value)[] args)
        {
            var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, args);
            return cmd;
        }

        private static void Bind(DbCommand cmd, (string name, object value)[] args)
        {
            foreach (var (name, value) in args)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }

        public void Dispose()
        {
            if (Provider == "sqlite") SqliteConnection.ClearAllPools();
        }
    }
}
