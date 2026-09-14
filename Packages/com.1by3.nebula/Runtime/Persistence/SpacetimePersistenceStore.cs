using System;
using System.Collections.Generic;
using Nebula.Spacetime.Persistence;
using SpacetimeDB;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// <see cref="IPersistenceStore"/> over the Nebula persistence SpacetimeDB module
    /// (SpacetimeDB/PersistenceModule~), a database of its own next to the control plane.
    /// <para>
    /// Mirrors the whole <c>persisted_entity</c> table into a dictionary and answers every load from that mirror,
    /// so a restore never waits on a round trip. Writes go through reducers (the module holds the epoch rule).
    /// Saves issued while disconnected are queued (the latest one per key wins) and flushed on connect, and the
    /// connection retries every 2 s. Like <see cref="SpacetimeControlPlane"/> the connection is pumped from
    /// <see cref="Tick"/>, so every callback the caller gets lands on the main thread.
    /// </para>
    /// </summary>
    public sealed class SpacetimePersistenceStore : IPersistenceStore
    {
        // Scaling step (v1 mirrors the whole table): subscribe per container instead of SubscribeToAllTables --
        // one `SELECT * FROM persisted_entity WHERE container_id = '<id>'` query added when this worker gains a
        // lease and dropped when it releases it, plus a by-key query for on-demand loads. The mirror and the
        // queued-until-applied load path below stay exactly as they are; only the subscription set changes.
        private const float ReconnectSeconds = 2f;

        private readonly string _uri;
        private readonly string _database;
        private readonly Dictionary<string, PersistedEntityRecord> _mirror = new Dictionary<string, PersistedEntityRecord>();
        /// <summary>Latest save per key while disconnected, flushed once the connection is up.</summary>
        private readonly Dictionary<string, PersistedEntityRecord> _queuedSaves = new Dictionary<string, PersistedEntityRecord>();
        private readonly List<string> _queuedDeletes = new List<string>();
        /// <summary>Loads issued before the subscription applied; answered from the mirror once it has.</summary>
        private readonly List<Action> _pendingLoads = new List<Action>();
        /// <summary>Answers waiting to be handed back from <see cref="Tick"/>.</summary>
        private readonly List<Action> _callbacks = new List<Action>();
        private readonly List<Action> _draining = new List<Action>();

        private DbConnection _conn;
        private bool _subscribed;
        private bool _queuedClear;
        private float _nextReconnect;

        public SpacetimePersistenceStore(string uri, string database)
        {
            _uri = uri;
            _database = database;
        }

        public bool IsConnected => _conn != null && _conn.IsActive && _subscribed;
        public string Backend => "spacetime";
        public int KnownCount => _mirror.Count;

        public void Connect()
        {
            _subscribed = false;
            try
            {
                _conn = DbConnection.Builder()
                    .WithUri(_uri)
                    .WithDatabaseName(_database)
                    .OnConnect(OnConnected)
                    .OnConnectError(e => NebulaLog.Error($"persistence connect error: {e.Message}"))
                    .OnDisconnect((c, e) =>
                    {
                        NebulaLog.Warn($"persistence disconnected{(e != null ? ": " + e.Message : "")}; saves are queued until it is back");
                        _subscribed = false;
                        _nextReconnect = Time.realtimeSinceStartup + ReconnectSeconds;
                    })
                    .Build();
                NebulaLog.Info($"persistence: connecting to {_uri}/{_database}");
            }
            catch (Exception e)
            {
                NebulaLog.Error($"persistence: {e.Message}");
                _nextReconnect = Time.realtimeSinceStartup + ReconnectSeconds;
            }
        }

        private void OnConnected(DbConnection conn, Identity identity, string token)
        {
            NebulaLog.Info($"persistence: connected as {identity}");
            conn.Db.PersistedEntity.OnInsert += (ctx, row) => Mirror(row);
            conn.Db.PersistedEntity.OnUpdate += (ctx, oldRow, newRow) => Mirror(newRow);
            conn.Db.PersistedEntity.OnDelete += (ctx, row) => _mirror.Remove(row.Key);
            conn.OnUnhandledReducerError += (ctx, e) => NebulaLog.Warn($"persistence reducer failed: {e.Message}");
            conn.SubscriptionBuilder()
                .OnApplied(ctx =>
                {
                    _subscribed = true;
                    NebulaLog.Info($"persistence: subscription applied ({_mirror.Count} record(s))");
                    Flush();
                    // Loads that arrived before the mirror was filled can be answered now.
                    for (int i = 0; i < _pendingLoads.Count; i++) _callbacks.Add(_pendingLoads[i]);
                    _pendingLoads.Clear();
                })
                .OnError((ctx, e) => NebulaLog.Error($"persistence subscription error: {e.Message}"))
                .SubscribeToAllTables();
        }

        private void Mirror(PersistedEntity row) => _mirror[row.Key] = ToRecord(row);

        /// <summary>Send everything that piled up while the connection was down.</summary>
        private void Flush()
        {
            if (_queuedClear)
            {
                _queuedClear = false;
                _conn.Reducers.ClearPersistence();
            }
            for (int i = 0; i < _queuedDeletes.Count; i++) _conn.Reducers.DeleteEntity(_queuedDeletes[i]);
            _queuedDeletes.Clear();
            if (_queuedSaves.Count > 0)
            {
                NebulaLog.Info($"persistence: flushing {_queuedSaves.Count} queued save(s)");
                foreach (var kv in _queuedSaves) Send(kv.Value);
                _queuedSaves.Clear();
            }
        }

        public void Tick()
        {
            if (_conn != null)
            {
                try { _conn.FrameTick(); }
                catch (Exception e) { NebulaLog.Error($"persistence tick: {e.Message}"); }
            }
            if ((_conn == null || !_conn.IsActive) && _nextReconnect > 0f && Time.realtimeSinceStartup >= _nextReconnect)
            {
                _nextReconnect = 0f;
                Connect();
            }
            if (_callbacks.Count == 0) return;
            // Swap first: a callback may issue another load, which is answered on the next tick.
            _draining.Clear();
            _draining.AddRange(_callbacks);
            _callbacks.Clear();
            for (int i = 0; i < _draining.Count; i++)
            {
                try { _draining[i](); }
                catch (Exception e) { NebulaLog.Error($"persistence callback: {e}"); }
            }
            _draining.Clear();
        }

        public void Save(PersistedEntityRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Key)) return;
            if (IsConnected)
            {
                Send(record);
                return;
            }
            // Only the newest checkpoint of an entity is worth keeping while the store is unreachable.
            _queuedSaves[record.Key] = record.Clone();
        }

        private void Send(PersistedEntityRecord r)
        {
            var state = new List<byte>(r.State != null ? r.State.Length : 0);
            if (r.State != null) state.AddRange(r.State);
            _conn.Reducers.SaveEntity(
                r.Key, r.PrefabId, r.PrefabName ?? "", r.SceneId, r.ContainerId ?? "", r.CarrierKey ?? "",
                r.LocalPosition.x, r.LocalPosition.y, r.LocalPosition.z,
                r.LocalRotation.x, r.LocalRotation.y, r.LocalRotation.z, r.LocalRotation.w,
                r.Velocity.x, r.Velocity.y, r.Velocity.z,
                r.Epoch, r.ServerDriven, r.Owned, r.Name ?? "", state, r.SavedBy ?? "");
        }

        public void Delete(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _queuedSaves.Remove(key);
            if (IsConnected) _conn.Reducers.DeleteEntity(key);
            else if (!_queuedDeletes.Contains(key)) _queuedDeletes.Add(key);
        }

        public void Load(string key, Action<PersistedEntityRecord> onLoaded)
        {
            if (onLoaded == null) return;
            string k = key ?? "";
            Answer(() =>
            {
                PersistedEntityRecord r;
                _mirror.TryGetValue(k, out r);
                return r != null ? r.Clone() : null;
            }, onLoaded);
        }

        public void LoadContainer(string containerId, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded) =>
            LoadMatching(r => r.CarrierKey.Length == 0 && r.ContainerId == (containerId ?? ""), onLoaded);

        public void LoadCarried(string carrierKey, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded) =>
            LoadMatching(r => r.CarrierKey == (carrierKey ?? ""), onLoaded);

        public void LoadWhere(Func<PersistedEntityRecord, bool> predicate, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded)
        {
            if (predicate == null) { onLoaded?.Invoke(Array.Empty<PersistedEntityRecord>()); return; }
            LoadMatching(predicate, onLoaded);
        }

        private void LoadMatching(Func<PersistedEntityRecord, bool> match, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded)
        {
            if (onLoaded == null) return;
            Answer<IReadOnlyList<PersistedEntityRecord>>(() =>
            {
                var hits = new List<PersistedEntityRecord>();
                foreach (var kv in _mirror)
                {
                    if (match(kv.Value)) hits.Add(kv.Value.Clone());
                }
                return hits;
            }, onLoaded);
        }

        /// <summary>
        /// Read the mirror now and hand the answer back from <see cref="Tick"/>. The read happens when the caller
        /// asks, not when the answer is delivered, because the two are a frame apart and the caller's own next
        /// checkpoint can land in the mirror in between: a rejoining player's pawn asks for the record it is about
        /// to overwrite, and must be given the one the store held when it asked (this is also what
        /// <see cref="LocalPersistenceStore"/> does). Before the subscription has applied there is nothing to read
        /// yet, so the read waits together with the delivery.
        /// </summary>
        private void Answer<T>(Func<T> read, Action<T> deliver)
        {
            if (_subscribed)
            {
                var value = read();
                _callbacks.Add(() => deliver(value));
            }
            else _pendingLoads.Add(() => deliver(read()));
        }

        public void Clear()
        {
            _mirror.Clear();
            _queuedSaves.Clear();
            _queuedDeletes.Clear();
            if (IsConnected) _conn.Reducers.ClearPersistence();
            else _queuedClear = true;
        }

        private static PersistedEntityRecord ToRecord(PersistedEntity row)
        {
            byte[] state;
            if (row.State != null && row.State.Count > 0)
            {
                state = new byte[row.State.Count];
                row.State.CopyTo(state);
            }
            else state = Array.Empty<byte>();
            return new PersistedEntityRecord
            {
                Key = row.Key ?? "",
                PrefabId = row.PrefabId,
                PrefabName = row.PrefabName ?? "",
                SceneId = row.SceneId,
                ContainerId = row.ContainerId ?? "",
                CarrierKey = row.CarrierKey ?? "",
                LocalPosition = new Vector3(row.PosX, row.PosY, row.PosZ),
                LocalRotation = new Quaternion(row.RotX, row.RotY, row.RotZ, row.RotW),
                Velocity = new Vector3(row.VelX, row.VelY, row.VelZ),
                Epoch = row.Epoch,
                ServerDriven = row.ServerDriven,
                Owned = row.Owned,
                Name = row.Name ?? "",
                State = state,
                Version = row.Version,
                SavedAt = row.SavedAt.ToStd().UtcDateTime,
                SavedBy = row.SavedBy ?? "",
            };
        }

        public void Dispose()
        {
            try { _conn?.Disconnect(); } catch { }
            _conn = null;
            _subscribed = false;
            _mirror.Clear();
            _callbacks.Clear();
            _pendingLoads.Clear();
        }
    }
}
