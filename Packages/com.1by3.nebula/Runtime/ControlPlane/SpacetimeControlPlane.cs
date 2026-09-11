using System;
using System.Collections.Generic;
using Nebula.Spacetime;
using SpacetimeDB;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// <see cref="IControlPlane"/> over the SpacetimeDB module in Assets/Nebula/SpacetimeDB/Module~. Subscribes to
    /// the whole (tiny) database and mirrors it into plain lists; writes go through reducers. The connection is
    /// pumped from <see cref="Tick"/> so every callback lands on the main thread.
    /// </summary>
    public sealed class SpacetimeControlPlane : IControlPlane
    {
        private readonly string _uri;
        private readonly string _database;
        private DbConnection _conn;
        private bool _subscribed;
        private bool _dirty;
        private float _nextReconnect;
        private DateTime _serverNow = DateTime.UtcNow;
        private double _serverNowAtLocal;

        private readonly List<WorkerInfo> _workers = new List<WorkerInfo>();
        private readonly List<LeaseInfo> _leases = new List<LeaseInfo>();
        private readonly List<GatewayInfo> _gateways = new List<GatewayInfo>();

        public SpacetimeControlPlane(string uri, string database)
        {
            _uri = uri;
            _database = database;
        }

        public bool IsConnected => _conn != null && _conn.IsActive && _subscribed;
        public DateTime Now => _serverNow + TimeSpan.FromSeconds(Time.realtimeSinceStartupAsDouble - _serverNowAtLocal);
        public event Action Changed;
        public IReadOnlyList<WorkerInfo> Workers => _workers;
        public IReadOnlyList<LeaseInfo> Leases => _leases;
        public IReadOnlyList<GatewayInfo> Gateways => _gateways;
        private readonly Dictionary<string, string> _settings = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> Settings => _settings;

        public void Connect()
        {
            _subscribed = false;
            try
            {
                _conn = DbConnection.Builder()
                    .WithUri(_uri)
                    .WithDatabaseName(_database)
                    .OnConnect(OnConnected)
                    .OnConnectError(e => NebulaLog.Error($"control plane connect error: {e.Message}"))
                    .OnDisconnect((c, e) =>
                    {
                        NebulaLog.Warn($"control plane disconnected{(e != null ? ": " + e.Message : "")}; mesh keeps running on its last known topology");
                        _subscribed = false;
                        _nextReconnect = Time.realtimeSinceStartup + 2f;
                    })
                    .Build();
                NebulaLog.Info($"control plane: connecting to {_uri}/{_database}");
            }
            catch (Exception e)
            {
                NebulaLog.Error($"control plane: {e.Message}");
                _nextReconnect = Time.realtimeSinceStartup + 2f;
            }
        }

        private void OnConnected(DbConnection conn, Identity identity, string token)
        {
            NebulaLog.Info($"control plane: connected as {identity}");
            conn.Db.Worker.OnInsert += (ctx, row) => _dirty = true;
            conn.Db.Worker.OnUpdate += (ctx, oldRow, newRow) => _dirty = true;
            conn.Db.Worker.OnDelete += (ctx, row) => _dirty = true;
            conn.Db.ContainerLease.OnInsert += (ctx, row) => _dirty = true;
            conn.Db.ContainerLease.OnUpdate += (ctx, oldRow, newRow) => _dirty = true;
            conn.Db.ContainerLease.OnDelete += (ctx, row) => _dirty = true;
            conn.Db.Gateway.OnInsert += (ctx, row) => _dirty = true;
            conn.Db.Gateway.OnUpdate += (ctx, oldRow, newRow) => _dirty = true;
            conn.Db.Gateway.OnDelete += (ctx, row) => _dirty = true;
            conn.Db.Orchestrator.OnInsert += (ctx, row) => _dirty = true;
            conn.Db.Orchestrator.OnUpdate += (ctx, oldRow, newRow) => _dirty = true;
            conn.Db.GameSetting.OnInsert += (ctx, row) => _dirty = true;
            conn.Db.GameSetting.OnUpdate += (ctx, oldRow, newRow) => _dirty = true;
            conn.Db.GameSetting.OnDelete += (ctx, row) => _dirty = true;
            conn.OnUnhandledReducerError += (ctx, e) => NebulaLog.Warn($"control plane reducer failed: {e.Message}");
            conn.SubscriptionBuilder()
                .OnApplied(ctx =>
                {
                    _subscribed = true;
                    _dirty = true;
                    NebulaLog.Info("control plane: subscription applied");
                })
                .OnError((ctx, e) => NebulaLog.Error($"control plane subscription error: {e.Message}"))
                .SubscribeToAllTables();
        }

        public void Tick()
        {
            if (_conn != null)
            {
                try { _conn.FrameTick(); }
                catch (Exception e) { NebulaLog.Error($"control plane tick: {e.Message}"); }
            }
            if ((_conn == null || !_conn.IsActive) && _nextReconnect > 0f && Time.realtimeSinceStartup >= _nextReconnect)
            {
                _nextReconnect = 0f;
                Connect();
            }
            if (_dirty && _conn != null)
            {
                _dirty = false;
                Rebuild();
                Changed?.Invoke();
            }
        }

        private void Rebuild()
        {
            DateTime latest = _serverNow;
            _workers.Clear();
            foreach (var w in _conn.Db.Worker.Iter())
            {
                var hb = ToDateTime(w.LastHeartbeat);
                if (hb > latest) latest = hb;
                _workers.Add(new WorkerInfo
                {
                    WorkerId = w.WorkerId,
                    WorkerIndex = w.WorkerIndex,
                    Address = w.Address,
                    Port = w.Port,
                    Status = w.Status,
                    LastHeartbeat = hb,
                    TickCount = w.TickCount,
                    TickMs = w.TickMs,
                    EntityCount = w.EntityCount,
                    AuthoritativeCount = w.AuthoritativeCount,
                    GhostCount = w.GhostCount,
                    PlayerCount = w.PlayerCount,
                    BotCount = w.BotCount,
                    ServerDrivenCount = w.ServerDrivenCount,
                });
            }
            _leases.Clear();
            foreach (var l in _conn.Db.ContainerLease.Iter())
            {
                var at = ToDateTime(l.UpdatedAt);
                if (at > latest) latest = at;
                _leases.Add(new LeaseInfo { ContainerId = l.ContainerId, WorkerId = l.WorkerId ?? "", Epoch = l.Epoch, State = l.State, UpdatedAt = at });
            }
            _gateways.Clear();
            foreach (var g in _conn.Db.Gateway.Iter())
            {
                var hb = ToDateTime(g.LastHeartbeat);
                if (hb > latest) latest = hb;
                _gateways.Add(new GatewayInfo { GatewayId = g.GatewayId, Address = g.Address, Port = g.Port, LastHeartbeat = hb });
            }
            foreach (var o in _conn.Db.Orchestrator.Iter())
            {
                var hb = ToDateTime(o.LastHeartbeat);
                if (hb > latest) latest = hb;
            }
            _settings.Clear();
            foreach (var s in _conn.Db.GameSetting.Iter())
            {
                var at = ToDateTime(s.UpdatedAt);
                if (at > latest) latest = at;
                _settings[s.Key] = s.Value ?? "";
            }
            if (latest > _serverNow)
            {
                _serverNow = latest;
                _serverNowAtLocal = Time.realtimeSinceStartupAsDouble;
            }
        }

        private static DateTime ToDateTime(Timestamp ts) => ts.ToStd().UtcDateTime;

        private bool Ready(string op)
        {
            if (IsConnected) return true;
            NebulaLog.Debugf($"control plane not connected; dropping {op}");
            return false;
        }

        public void RegisterWorker(string workerId, uint workerIndex, string address, ushort port)
        {
            if (Ready("RegisterWorker")) _conn.Reducers.RegisterWorker(workerId, workerIndex, address, port);
        }

        public void HeartbeatWorker(string workerId, string status, in WorkerStats s)
        {
            if (Ready("HeartbeatWorker"))
                _conn.Reducers.HeartbeatWorker(workerId, status, s.TickCount, s.TickMs, s.EntityCount, s.AuthoritativeCount, s.GhostCount, s.PlayerCount, s.BotCount, s.ServerDrivenCount);
        }

        public void UnregisterWorker(string workerId)
        {
            if (Ready("UnregisterWorker")) _conn.Reducers.UnregisterWorker(workerId);
        }

        public void RegisterGateway(string gatewayId, string address, ushort port)
        {
            if (Ready("RegisterGateway")) _conn.Reducers.RegisterGateway(gatewayId, address, port);
        }

        public void HeartbeatGateway(string gatewayId)
        {
            if (Ready("HeartbeatGateway")) _conn.Reducers.HeartbeatGateway(gatewayId);
        }

        public void UnregisterGateway(string gatewayId)
        {
            if (Ready("UnregisterGateway")) _conn.Reducers.UnregisterGateway(gatewayId);
        }

        public void HeartbeatOrchestrator(string orchestratorId, uint desiredWorkers)
        {
            if (Ready("HeartbeatOrchestrator")) _conn.Reducers.HeartbeatOrchestrator(orchestratorId, desiredWorkers);
        }

        public void SetSetting(string key, string value)
        {
            if (Ready("SetSetting")) _conn.Reducers.SetGameSetting(key, value ?? "");
        }

        public void EnsureContainer(string containerId)
        {
            if (Ready("EnsureContainer")) _conn.Reducers.EnsureContainer(containerId);
        }

        public void AssignContainer(string containerId, string workerId)
        {
            if (Ready("AssignContainer")) _conn.Reducers.AssignContainer(containerId, workerId);
        }

        public void SetLeaseState(string containerId, string state)
        {
            if (Ready("SetLeaseState")) _conn.Reducers.SetLeaseState(containerId, state);
        }

        public void ReleaseContainer(string containerId)
        {
            if (Ready("ReleaseContainer")) _conn.Reducers.ReleaseContainer(containerId);
        }

        public void RemoveContainer(string containerId)
        {
            if (Ready("RemoveContainer")) _conn.Reducers.RemoveContainer(containerId);
        }

        public void ResetControlPlane()
        {
            if (Ready("ResetControlPlane")) _conn.Reducers.ResetControlPlane();
        }

        public void Dispose()
        {
            try { _conn?.Disconnect(); } catch { }
            _conn = null;
            _subscribed = false;
        }
    }
}
