using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Nebula.ServicePrimitives;

namespace Nebula
{
    /// <summary>
    /// <see cref="IPersistenceStore"/> in the orchestrator's database (<see cref="NebulaDatabase"/>: SQLite or
    /// PostgreSQL), table <c>nebula_entity</c>. One writer thread runs every save, delete and query in order on one
    /// connection, so the epoch rule (a save older than what is stored is dropped) is one conditional upsert and a
    /// later save never overtakes an earlier one. Answers are handed back from <see cref="Tick"/> on the main
    /// thread. When the database is unreachable the queue waits and the connection is retried; the mesh keeps
    /// simulating meanwhile, and workers keep their own queues (<see cref="RemotePersistenceStore"/>).
    /// </summary>
    public sealed class SqlPersistenceStore : IPersistenceStore
    {
        public const float RetrySeconds = 2f;
        /// <summary>Seconds between refreshes of <see cref="KnownCount"/> while writes keep coming.</summary>
        public const float CountIntervalSeconds = 2f;
        /// <summary>Times a job that throws is retried (after a reconnect) before the store gives up on it.</summary>
        private const int MaxAttempts = 3;

        private sealed class Job
        {
            public string Name;
            public Action<DbConnection> Run;
            /// <summary>Writer thread, when the store gives up on the job: deliver the stand-in answer (<see cref="PersistenceAnswer.Failed"/>).</summary>
            public Action GiveUp;
            /// <summary>A save, delete or clear: giving up on it fails the next <see cref="WhenWritten"/>.</summary>
            public bool IsWrite;
            public int Attempts;
        }

        private readonly NebulaDatabase _db;
        private readonly object _gate = new object();
        private readonly LinkedList<Job> _jobs = new LinkedList<Job>();
        private readonly List<Action> _callbacks = new List<Action>();
        private readonly List<Action> _draining = new List<Action>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Thread _thread;
        private volatile bool _running;
        private volatile bool _connected;
        private volatile int _count = -1;
        private volatile string _error;
        private string _loggedError;
        private bool _loggedConnected;
        private bool _countDirty = true;
        private double _nextCount;
        /// <summary>Writer thread: a write queued since the last barrier was given up.</summary>
        private bool _writeGivenUp;

        public SqlPersistenceStore(NebulaDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public bool IsConnected => _running && _connected;
        public string Backend => _db.Provider;
        public int KnownCount => _count;
        /// <summary>Jobs waiting for the writer thread.</summary>
        public int PendingJobs { get { lock (_gate) return _jobs.Count; } }
        /// <summary>Wait after a failure before the next attempt (<see cref="RetrySeconds"/>; tests shorten it).</summary>
        internal float RetryDelaySeconds = RetrySeconds;

        public void Connect()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "nebula-persistence-sql" };
            _thread.Start();
            NebulaLog.Info($"persistence: {_db.Provider} store at {_db.Display}");
        }

        public void Tick()
        {
            string error = _error;
            if (error != _loggedError)
            {
                _loggedError = error;
                if (error != null) NebulaLog.Warn($"persistence: {_db.Display}: {error}");
            }
            if (_connected && !_loggedConnected)
            {
                _loggedConnected = true;
                NebulaLog.Info($"persistence: {_db.Provider} store ready ({_count} record(s))");
            }
            lock (_gate)
            {
                if (_callbacks.Count == 0) return;
                _draining.AddRange(_callbacks);
                _callbacks.Clear();
            }
            for (int i = 0; i < _draining.Count; i++)
            {
                try { _draining[i](); }
                catch (Exception e) { NebulaLog.Error($"persistence callback: {e}"); }
            }
            _draining.Clear();
        }

        public void Dispose()
        {
            if (!_running) return;
            // Let queued checkpoints land before the process goes.
            lock (_gate)
            {
                _running = false;
                Monitor.PulseAll(_gate);
            }
            _thread?.Join(TimeSpan.FromSeconds(5));
            lock (_gate) _callbacks.Clear();
        }

        // ---------------------------------------------------------------------------------------- writes

        public void Save(PersistedEntityRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Key)) return;
            var r = record.Clone();
            r.SavedAt = DateTime.UtcNow;
            Enqueue("save " + r.Key, c =>
            {
                NebulaDatabase.Execute(c, @"INSERT INTO nebula_entity (entity_key, prefab_id, prefab_name, scene_id, container_id, carrier_key,
                    pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w, vel_x, vel_y, vel_z, epoch, server_driven, owned, name, state, version, saved_at, saved_by, scope_key)
                    VALUES (@key, @prefab_id, @prefab_name, @scene_id, @container_id, @carrier_key,
                    @pos_x, @pos_y, @pos_z, @rot_x, @rot_y, @rot_z, @rot_w, @vel_x, @vel_y, @vel_z, @epoch, @server_driven, @owned, @name, @state, 1, @saved_at, @saved_by, @scope_key)
                    ON CONFLICT (entity_key) DO UPDATE SET
                    prefab_id = excluded.prefab_id, prefab_name = excluded.prefab_name, scene_id = excluded.scene_id,
                    container_id = excluded.container_id, carrier_key = excluded.carrier_key, scope_key = excluded.scope_key,
                    pos_x = excluded.pos_x, pos_y = excluded.pos_y, pos_z = excluded.pos_z,
                    rot_x = excluded.rot_x, rot_y = excluded.rot_y, rot_z = excluded.rot_z, rot_w = excluded.rot_w,
                    vel_x = excluded.vel_x, vel_y = excluded.vel_y, vel_z = excluded.vel_z,
                    epoch = excluded.epoch, server_driven = excluded.server_driven, owned = excluded.owned, name = excluded.name,
                    state = excluded.state, version = nebula_entity.version + 1, saved_at = excluded.saved_at, saved_by = excluded.saved_by
                    WHERE excluded.epoch >= nebula_entity.epoch",
                    ("@key", r.Key), ("@prefab_id", (int)r.PrefabId), ("@prefab_name", r.PrefabName ?? ""), ("@scene_id", (long)r.SceneId),
                    ("@container_id", r.ContainerId ?? ""), ("@carrier_key", r.CarrierKey ?? ""),
                    ("@pos_x", (double)r.LocalPosition.x), ("@pos_y", (double)r.LocalPosition.y), ("@pos_z", (double)r.LocalPosition.z),
                    ("@rot_x", (double)r.LocalRotation.x), ("@rot_y", (double)r.LocalRotation.y), ("@rot_z", (double)r.LocalRotation.z), ("@rot_w", (double)r.LocalRotation.w),
                    ("@vel_x", (double)r.Velocity.x), ("@vel_y", (double)r.Velocity.y), ("@vel_z", (double)r.Velocity.z),
                    ("@epoch", (long)r.Epoch), ("@server_driven", r.ServerDriven), ("@owned", r.Owned), ("@name", r.Name ?? ""),
                    ("@state", r.State != null && r.State.Length > 0 ? r.State : Array.Empty<byte>()),
                    ("@saved_at", ControlPlaneJson.ToUnixMs(r.SavedAt)), ("@saved_by", r.SavedBy ?? ""), ("@scope_key", r.ScopeKey ?? ""));
                _countDirty = true;
            }, isWrite: true);
        }

        public void Delete(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            Enqueue("delete " + key, c =>
            {
                NebulaDatabase.Execute(c, "DELETE FROM nebula_entity WHERE entity_key = @key", ("@key", key));
                _countDirty = true;
            }, isWrite: true);
        }

        public void WhenWritten(Action onWritten)
        {
            if (onWritten == null) return;
            // Jobs run in order on one connection, so this one runs after every write queued before it. A write given
            // up on since the previous barrier fails this one: those writes never reached the database.
            Enqueue("barrier", c =>
            {
                bool failed = _writeGivenUp;
                _writeGivenUp = false;
                Deliver(onWritten, failed);
            }, giveUp: () =>
            {
                _writeGivenUp = false;
                Deliver(onWritten, true);
            });
        }

        public void Clear()
        {
            Enqueue("clear", c =>
            {
                NebulaDatabase.Execute(c, "DELETE FROM nebula_entity");
                _countDirty = true;
            }, isWrite: true);
        }

        // ---------------------------------------------------------------------------------------- reads

        private const string Columns = "entity_key, prefab_id, prefab_name, scene_id, container_id, carrier_key, pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w, vel_x, vel_y, vel_z, epoch, server_driven, owned, name, state, version, saved_at, saved_by, scope_key";

        public void Load(string key, Action<PersistedEntityRecord> onLoaded)
        {
            if (onLoaded == null) return;
            string k = key ?? "";
            Enqueue("load " + k, c =>
            {
                PersistedEntityRecord record = null;
                using (var cmd = NebulaDatabase.Command(c, $"SELECT {Columns} FROM nebula_entity WHERE entity_key = @key", ("@key", k)))
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read()) record = Read(reader);
                }
                Deliver(() => onLoaded(record));
            }, giveUp: () => Deliver(() => onLoaded(null), true));
        }

        /// <summary>
        /// One job on the writer thread, one <c>SELECT ... WHERE container_id IN (...)</c> per
        /// <see cref="PersistenceHost.MaxContainersPerLoad"/> ids (the <c>nebula_entity_container</c> index answers
        /// it), answered once when every chunk has been read.
        /// </summary>
        public void LoadContainers(IReadOnlyList<string> containerIds, Action<IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>>> onLoaded)
        {
            if (onLoaded == null) return;
            var result = ContainerRecords.For(containerIds, out var ids);
            if (ids.Count == 0) { Deliver(() => onLoaded(result)); return; }
            Enqueue($"containers ({ids.Count})", c =>
            {
                // A retried job starts over: drop what a failed attempt had already filed.
                foreach (var list in result.Values) ((List<PersistedEntityRecord>)list).Clear();
                int chunkSize = PersistenceHost.MaxContainersPerLoad;
                var sql = new StringBuilder();
                for (int start = 0; start < ids.Count; start += chunkSize)
                {
                    int count = Math.Min(chunkSize, ids.Count - start);
                    var args = new (string, object)[count];
                    sql.Clear().Append("SELECT ").Append(Columns).Append(" FROM nebula_entity WHERE carrier_key = '' AND container_id IN (");
                    for (int i = 0; i < count; i++)
                    {
                        if (i > 0) sql.Append(", ");
                        string name = "@c" + i.ToString(CultureInfo.InvariantCulture);
                        sql.Append(name);
                        args[i] = (name, ids[start + i]);
                    }
                    sql.Append(')');
                    using (var cmd = NebulaDatabase.Command(c, sql.ToString(), args))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read()) ContainerRecords.Add(result, Read(reader));
                    }
                }
                Deliver(() => onLoaded(result));
            }, giveUp: () =>
            {
                foreach (var list in result.Values) ((List<PersistedEntityRecord>)list).Clear();
                Deliver(() => onLoaded(result), true);
            });
        }

        public void LoadCarried(string carrierKey, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded) =>
            Query("carried " + carrierKey, $"SELECT {Columns} FROM nebula_entity WHERE carrier_key = @k", new[] { ("@k", (object)(carrierKey ?? "")) }, null, onLoaded);

        public void LoadWhere(Func<PersistedEntityRecord, bool> predicate, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded) =>
            Query("all", $"SELECT {Columns} FROM nebula_entity", Array.Empty<(string, object)>(), predicate, onLoaded);

        /// <summary>A <c>COUNT</c> on the scope key (and the container id when one is given): no row is read back.</summary>
        public void CountRecords(string scopeKey, string containerId, Action<int> onCounted)
        {
            if (onCounted == null) return;
            string scope = scopeKey ?? "";
            string container = containerId ?? "";
            string sql = container.Length == 0
                ? "SELECT COUNT(*) FROM nebula_entity WHERE scope_key = @s"
                : "SELECT COUNT(*) FROM nebula_entity WHERE scope_key = @s AND container_id = @c";
            var args = container.Length == 0
                ? new[] { ("@s", (object)scope) }
                : new[] { ("@s", (object)scope), ("@c", (object)container) };
            Enqueue("count " + scope, c =>
            {
                long n;
                using (var cmd = NebulaDatabase.Command(c, sql, args))
                {
                    object scalar = cmd.ExecuteScalar();
                    n = scalar == null || scalar is DBNull ? 0L : Convert.ToInt64(scalar);
                }
                Deliver(() => onCounted((int)n));
            }, giveUp: () => Deliver(() => onCounted(0), true));
        }

        private void Query(string name, string sql, (string, object)[] args, Func<PersistedEntityRecord, bool> filter, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded)
        {
            if (onLoaded == null) return;
            Enqueue(name, c =>
            {
                var hits = new List<PersistedEntityRecord>();
                using (var cmd = NebulaDatabase.Command(c, sql, args))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var r = Read(reader);
                        if (filter == null || filter(r)) hits.Add(r);
                    }
                }
                Deliver(() => onLoaded(hits));
            }, giveUp: () => Deliver(() => onLoaded(Array.Empty<PersistedEntityRecord>()), true));
        }

        private static PersistedEntityRecord Read(DbDataReader r)
        {
            var record = new PersistedEntityRecord
            {
                Key = r.GetString(0),
                PrefabId = (ushort)r.GetInt32(1),
                PrefabName = r.GetString(2),
                SceneId = (uint)r.GetInt64(3),
                ContainerId = r.GetString(4),
                CarrierKey = r.GetString(5),
                LocalPosition = new Vector3((float)r.GetDouble(6), (float)r.GetDouble(7), (float)r.GetDouble(8)),
                LocalRotation = new Quaternion((float)r.GetDouble(9), (float)r.GetDouble(10), (float)r.GetDouble(11), (float)r.GetDouble(12)),
                Velocity = new Vector3((float)r.GetDouble(13), (float)r.GetDouble(14), (float)r.GetDouble(15)),
                Epoch = (uint)r.GetInt64(16),
                ServerDriven = r.GetBoolean(17),
                Owned = r.GetBoolean(18),
                Name = r.GetString(19),
                State = r.IsDBNull(20) ? Array.Empty<byte>() : (byte[])r.GetValue(20),
                Version = (ulong)r.GetInt64(21),
                SavedAt = ControlPlaneJson.FromUnixMs(r.GetInt64(22)),
                SavedBy = r.GetString(23),
                ScopeKey = r.GetString(24),
            };
            return record;
        }

        // ---------------------------------------------------------------------------------------- writer thread

        private void Enqueue(string name, Action<DbConnection> run, Action giveUp = null, bool isWrite = false)
        {
            lock (_gate)
            {
                _jobs.AddLast(new Job { Name = name, Run = run, GiveUp = giveUp, IsWrite = isWrite });
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Hand <paramref name="callback"/> to <see cref="Tick"/>; <paramref name="failed"/> runs it under <see cref="PersistenceAnswer.Failed"/>.</summary>
        private void Deliver(Action callback, bool failed = false)
        {
            Action run = failed ? () => PersistenceAnswer.Invoke(callback, true) : callback;
            lock (_gate) _callbacks.Add(run);
        }

        private void Loop()
        {
            DbConnection conn = null;
            while (true)
            {
                Job job;
                lock (_gate)
                {
                    // One bounded wait, not a loop: an idle pass still refreshes the count below.
                    if (_jobs.Count == 0 && _running) Monitor.Wait(_gate, TimeSpan.FromSeconds(CountIntervalSeconds));
                    if (_jobs.Count == 0 && !_running) break;
                    job = _jobs.Count > 0 ? _jobs.First.Value : null;
                    if (job != null) _jobs.RemoveFirst();
                }
                try
                {
                    if (conn == null)
                    {
                        conn = _db.Open();
                        _connected = true;
                        _error = null;
                        _countDirty = true;
                    }
                    if (job != null)
                    {
                        job.Run(conn);
                        job = null; // done: a failure below (the count refresh) must not run it, or answer it, twice
                    }
                    if (_countDirty && _clock.Elapsed.TotalSeconds >= _nextCount)
                    {
                        _nextCount = _clock.Elapsed.TotalSeconds + CountIntervalSeconds;
                        _countDirty = false;
                        using var cmd = NebulaDatabase.Command(conn, "SELECT COUNT(*) FROM nebula_entity");
                        _count = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                }
                catch (Exception e)
                {
                    _connected = false;
                    try { conn?.Dispose(); } catch { }
                    conn = null;
                    if (job != null)
                    {
                        job.Attempts++;
                        if (job.Attempts < MaxAttempts)
                        {
                            _error = $"{job.Name} failed ({e.Message}); reconnecting";
                            lock (_gate) _jobs.AddFirst(job);
                        }
                        else
                        {
                            // Give up, but still answer: an in-process caller would otherwise wait forever.
                            _error = $"{job.Name} given up after {job.Attempts} attempts: {e.Message}";
                            if (job.IsWrite) _writeGivenUp = true;
                            job.GiveUp?.Invoke();
                        }
                    }
                    else _error = e.Message;
                    lock (_gate) { if (_running) Monitor.Wait(_gate, TimeSpan.FromSeconds(RetryDelaySeconds)); }
                }
            }
            try { conn?.Dispose(); } catch { }
        }
    }
}
