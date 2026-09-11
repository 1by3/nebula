using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// In-process control plane with the exact semantics of the SpacetimeDB module (see Module~/Lib.cs), for
    /// single-process runs and tests. Changes are applied synchronously and <see cref="Changed"/> fires on the next
    /// <see cref="Tick"/>, mirroring the subscription push of the real thing.
    /// </summary>
    public sealed class LocalControlPlane : IControlPlane
    {
        private readonly List<WorkerInfo> _workers = new List<WorkerInfo>();
        private readonly List<LeaseInfo> _leases = new List<LeaseInfo>();
        private readonly List<GatewayInfo> _gateways = new List<GatewayInfo>();
        private bool _dirty;

        public bool IsConnected { get; private set; }
        public DateTime Now => DateTime.UtcNow;
        public event Action Changed;
        public IReadOnlyList<WorkerInfo> Workers => _workers;
        public IReadOnlyList<LeaseInfo> Leases => _leases;
        public IReadOnlyList<GatewayInfo> Gateways => _gateways;
        private readonly Dictionary<string, string> _settings = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> Settings => _settings;

        public void Connect()
        {
            IsConnected = true;
            _dirty = true;
        }

        public void Tick()
        {
            if (!_dirty) return;
            _dirty = false;
            Changed?.Invoke();
        }

        public void Dispose() => IsConnected = false;

        private void Touch() => _dirty = true;

        public void RegisterWorker(string workerId, uint workerIndex, string address, ushort port)
        {
            var w = this.FindWorker(workerId);
            if (w == null) { w = new WorkerInfo { WorkerId = workerId }; _workers.Add(w); }
            w.WorkerIndex = workerIndex;
            w.Address = address;
            w.Port = port;
            w.Status = WorkerStatus.Starting;
            w.LastHeartbeat = Now;
            Touch();
        }

        public void HeartbeatWorker(string workerId, string status, in WorkerStats stats)
        {
            var w = this.FindWorker(workerId);
            if (w == null) return;
            w.Status = status;
            w.LastHeartbeat = Now;
            w.TickCount = stats.TickCount;
            w.TickMs = stats.TickMs;
            w.EntityCount = stats.EntityCount;
            w.AuthoritativeCount = stats.AuthoritativeCount;
            w.GhostCount = stats.GhostCount;
            w.PlayerCount = stats.PlayerCount;
            w.BotCount = stats.BotCount;
            w.ServerDrivenCount = stats.ServerDrivenCount;
            Touch();
        }

        public void UnregisterWorker(string workerId)
        {
            _workers.RemoveAll(w => w.WorkerId == workerId);
            foreach (var l in _leases)
            {
                if (l.WorkerId == workerId) { l.WorkerId = ""; l.State = LeaseState.Orphaned; l.UpdatedAt = Now; }
            }
            Touch();
        }

        public void RegisterGateway(string gatewayId, string address, ushort port)
        {
            GatewayInfo g = null;
            foreach (var x in _gateways) if (x.GatewayId == gatewayId) g = x;
            if (g == null) { g = new GatewayInfo { GatewayId = gatewayId }; _gateways.Add(g); }
            g.Address = address;
            g.Port = port;
            g.LastHeartbeat = Now;
            Touch();
        }

        public void HeartbeatGateway(string gatewayId)
        {
            foreach (var g in _gateways) if (g.GatewayId == gatewayId) g.LastHeartbeat = Now;
            Touch();
        }

        public void UnregisterGateway(string gatewayId)
        {
            _gateways.RemoveAll(g => g.GatewayId == gatewayId);
            Touch();
        }

        public void HeartbeatOrchestrator(string orchestratorId, uint desiredWorkers) { }

        public void SetSetting(string key, string value)
        {
            value = value ?? "";
            if (_settings.TryGetValue(key, out var old) && old == value) return;
            _settings[key] = value;
            Touch();
        }

        public void EnsureContainer(string containerId)
        {
            if (this.FindLease(containerId) != null) return;
            _leases.Add(new LeaseInfo { ContainerId = containerId, WorkerId = "", Epoch = 0, State = LeaseState.Orphaned, UpdatedAt = Now });
            Touch();
        }

        public void AssignContainer(string containerId, string workerId)
        {
            var l = this.FindLease(containerId);
            if (l == null)
            {
                _leases.Add(new LeaseInfo { ContainerId = containerId, WorkerId = workerId, Epoch = 1, State = LeaseState.Active, UpdatedAt = Now });
                Touch();
                return;
            }
            if (l.WorkerId == workerId && l.State == LeaseState.Active) return;
            l.WorkerId = workerId;
            l.Epoch += 1;
            l.State = LeaseState.Active;
            l.UpdatedAt = Now;
            Touch();
        }

        public void SetLeaseState(string containerId, string state)
        {
            var l = this.FindLease(containerId);
            if (l == null) return;
            l.State = state;
            l.UpdatedAt = Now;
            Touch();
        }

        public void ReleaseContainer(string containerId)
        {
            var l = this.FindLease(containerId);
            if (l == null) return;
            l.WorkerId = "";
            l.State = LeaseState.Orphaned;
            l.UpdatedAt = Now;
            Touch();
        }

        public void RemoveContainer(string containerId)
        {
            if (_leases.RemoveAll(l => l.ContainerId == containerId) > 0) Touch();
        }

        public void ResetControlPlane()
        {
            _workers.Clear();
            _leases.Clear();
            _gateways.Clear();
            _settings.Clear();
            Touch();
        }
    }
}
