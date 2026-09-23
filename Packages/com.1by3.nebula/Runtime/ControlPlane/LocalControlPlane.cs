using System;
using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// The control plane's state machine: registrations, heartbeats, leases with epochs and settings, kept in plain
    /// lists in this process. Changes are applied synchronously and <see cref="Changed"/> fires on the next
    /// <see cref="Tick"/>. On its own it serves single-process runs and tests; <see cref="ControlPlaneHost"/> wraps
    /// it on the orchestrator, where it is the source of truth every <see cref="RemoteControlPlane"/> mirrors.
    /// </summary>
    public sealed partial class LocalControlPlane : IControlPlane
    {
        private readonly List<WorkerInfo> _workers = new List<WorkerInfo>();
        private readonly List<LeaseInfo> _leases = new List<LeaseInfo>();
        private readonly List<GatewayInfo> _gateways = new List<GatewayInfo>();
        private bool _dirty;

        public bool IsConnected { get; private set; }
        /// <summary>
        /// The clock every row is stamped with. A seam, not a setting: the lifecycle's idle age is measured in
        /// lease-row ages, so a test that has to age a scope by minutes drives this instead of waiting
        /// (<c>docs/scope-lifecycle.md</c> D7). Defaults to the wall clock and nothing in Nebula changes it.
        /// </summary>
        internal Func<DateTime> Clock = () => DateTime.UtcNow;

        public DateTime Now => Clock();
        /// <summary>Identity of this control-plane document, preserved on import and replaced on reset.</summary>
        public string DocumentId { get; private set; } = NewDocumentId();
        private static string NewDocumentId() => Guid.NewGuid().ToString("N");
        public event Action Changed;
        public IReadOnlyList<WorkerInfo> Workers => _workers;
        public IReadOnlyList<LeaseInfo> Leases => _leases;
        public IReadOnlyList<GatewayInfo> Gateways => _gateways;
        private readonly Dictionary<string, string> _settings = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> Settings => _settings;
        /// <summary>Bumped by every change, so a mirror can tell whether it is behind.</summary>
        public long Version { get; private set; }

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

        private void Touch()
        {
            _dirty = true;
            Version++;
        }

        /// <summary>The whole state as one document (see <see cref="ControlPlaneJson"/>).</summary>
        public string ToJson() => ControlPlaneJson.Write(Version, Now, _workers, _leases, _gateways, _settings, _scopes, DocumentId);

        /// <summary>Replace the whole state with <paramref name="snapshot"/> (a stored document coming back at startup).</summary>
        public void Import(ControlPlaneJson.Snapshot snapshot)
        {
            _workers.Clear();
            _leases.Clear();
            _gateways.Clear();
            _settings.Clear();
            ImportScopes(null);
            if (snapshot != null)
            {
                _workers.AddRange(snapshot.Workers);
                _leases.AddRange(snapshot.Leases);
                _gateways.AddRange(snapshot.Gateways);
                ImportScopes(snapshot.Scopes);
                foreach (var kv in snapshot.Settings) _settings[kv.Key] = kv.Value;
                if (snapshot.Version > Version) Version = snapshot.Version;
                // A stored document keeps its identity across the restart; one written before identities existed
                // gets a new one, which costs every worker one reconciliation pass that writes nothing.
                if (!string.IsNullOrEmpty(snapshot.DocumentId)) DocumentId = snapshot.DocumentId;
            }
            Touch();
        }

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
            w.HasGlobalEntities = stats.HasGlobalEntities;
            w.OldestDirtySeconds = stats.OldestDirtySeconds;
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

        public void RegisterGateway(string gatewayId, string address, ushort port, uint incarnation = 0)
        {
            GatewayInfo g = null;
            foreach (var x in _gateways) if (x.GatewayId == gatewayId) g = x;
            if (g == null) { g = new GatewayInfo { GatewayId = gatewayId }; _gateways.Add(g); }
            // A fresh process under an old id: a drain asked of the previous incarnation does not apply to it.
            if (incarnation != 0 && incarnation != g.Incarnation) { g.DrainRequested = false; g.Stats = default; }
            g.Incarnation = incarnation;
            g.Address = address;
            g.Port = port;
            g.LastHeartbeat = Now;
            Touch();
        }

        public void HeartbeatGateway(string gatewayId, in GatewayStats stats)
        {
            foreach (var g in _gateways) if (g.GatewayId == gatewayId) { g.LastHeartbeat = Now; g.Stats = stats; }
            Touch();
        }

        public void UnregisterGateway(string gatewayId)
        {
            _gateways.RemoveAll(g => g.GatewayId == gatewayId);
            Touch();
        }

        public void SetGatewayDraining(string gatewayId, bool draining)
        {
            foreach (var g in _gateways) if (g.GatewayId == gatewayId) g.DrainRequested = draining;
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

        public void EnsureContainer(string containerId, ContainerAuthority authority = ContainerAuthority.Auto, bool ownPhysicsFrame = false, FrameInterestMode frameInterest = FrameInterestMode.WithCarrier)
        {
            if (this.FindLease(containerId) != null) return;
            _leases.Add(new LeaseInfo { ContainerId = containerId, WorkerId = "", Epoch = 0, State = LeaseState.Orphaned, UpdatedAt = Now, Authority = authority, OwnPhysicsFrame = ownPhysicsFrame, FrameInterest = frameInterest });
            Touch();
        }

        public void EnsureRuntimeContainer(string containerId, ContainerPlacement placement, string workerId, InstanceContainerInfo instance = null)
        {
            if (this.FindLease(containerId) != null) return;
            // An inherited container is simulated by its parent's owner: its row only carries the box.
            bool owned = !string.IsNullOrEmpty(workerId) && !placement.IsInherited;
            _leases.Add(new LeaseInfo
            {
                ContainerId = containerId,
                WorkerId = owned ? workerId : "",
                Epoch = owned ? 1UL : 0UL,
                State = placement.IsInherited ? LeaseState.Inherited : owned ? LeaseState.Active : LeaseState.Orphaned,
                UpdatedAt = Now,
                HasBounds = true,
                ParentId = placement.ParentId ?? "",
                Center = placement.Center,
                BoundsSize = placement.Size,
                Authority = placement.Authority,
                OwnPhysicsFrame = placement.OwnPhysicsFrame,
                FrameInterest = placement.FrameInterest,
                Instance = instance?.Copy(),
            });
            Touch();
        }

        public void TouchContainer(string containerId)
        {
            var l = this.FindLease(containerId);
            if (l == null) return;
            l.UpdatedAt = Now;
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
            if (l.State == LeaseState.Pinned) return; // only PinContainer moves a pinned lease
            if (l.WorkerId == workerId && l.State == LeaseState.Active) return;
            l.WorkerId = workerId;
            l.Epoch += 1;
            l.State = LeaseState.Active;
            l.UpdatedAt = Now;
            Touch();
        }

        public void PinContainer(string containerId, string workerId)
        {
            var l = this.FindLease(containerId);
            if (l == null || (l.WorkerId == workerId && l.State == LeaseState.Pinned)) return;
            l.WorkerId = workerId;
            l.Epoch += 1;
            l.State = LeaseState.Pinned;
            l.UpdatedAt = Now;
            Touch();
        }

        public void SetContainerHint(string containerId, in ContainerHint hint)
        {
            var l = this.FindLease(containerId);
            if (l == null)
            {
                // Hinting a container before anything leased it is legal: the row is the hint's home.
                l = new LeaseInfo { ContainerId = containerId, WorkerId = "", Epoch = 0, State = LeaseState.Orphaned, UpdatedAt = Now };
                _leases.Add(l);
            }
            bool wanted = !hint.IsDefault;
            if (l.HasHint == wanted && l.Hint == hint) return;
            l.HasHint = wanted;
            l.Hint = wanted ? hint : ContainerHint.Default;
            l.UpdatedAt = Now;
            Touch();
        }

        public void SetContainerCapacity(string containerId, float saturation, CostComponent dominant, bool atCapacity, SaturationCause cause = SaturationCause.None)
        {
            var l = this.FindLease(containerId);
            if (l == null) return;
            if (float.IsNaN(saturation) || saturation < 0f) saturation = 0f;
            if (l.HasCapacity && l.AtCapacity == atCapacity && l.Dominant == dominant && l.SaturationCause == cause &&
                Math.Abs(l.Saturation - saturation) < 0.0005f) return;
            l.HasCapacity = true;
            l.Saturation = saturation;
            l.Dominant = dominant;
            l.AtCapacity = atCapacity;
            l.SaturationCause = cause;
            // Deliberately not l.UpdatedAt: that is the idle clock the scope lifecycle retires on.
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
            DocumentId = NewDocumentId();
            _workers.Clear();
            _leases.Clear();
            _gateways.Clear();
            _settings.Clear();
            ClearScopes();
            Touch();
        }
    }
}
