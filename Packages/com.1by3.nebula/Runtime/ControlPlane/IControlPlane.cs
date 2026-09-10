using System;
using System.Collections.Generic;

namespace Nebula
{
    public sealed class WorkerInfo
    {
        public string WorkerId;
        public uint WorkerIndex;
        public string Address;
        public ushort Port;
        public string Status;
        public DateTime LastHeartbeat;
        public ulong TickCount;
        public float TickMs;
        /// <summary>Every entity resident on the worker (authoritative + ghosts).</summary>
        public uint EntityCount;
        public uint AuthoritativeCount;
        public uint GhostCount;
        /// <summary>Authoritative entities owned by a human client / by a bot client / by nobody (server-driven).</summary>
        public uint PlayerCount;
        public uint BotCount;
        public uint ServerDrivenCount;
    }

    /// <summary>What a worker reports about itself on every heartbeat.</summary>
    public struct WorkerStats
    {
        public ulong TickCount;
        public float TickMs;
        public uint EntityCount;
        public uint AuthoritativeCount;
        public uint GhostCount;
        public uint PlayerCount;
        public uint BotCount;
        public uint ServerDrivenCount;
    }

    public sealed class LeaseInfo
    {
        public string ContainerId;
        public string WorkerId;
        public ulong Epoch;
        public string State;
        public DateTime UpdatedAt;
    }

    public sealed class GatewayInfo
    {
        public string GatewayId;
        public string Address;
        public ushort Port;
        public DateTime LastHeartbeat;
    }

    public static class LeaseState
    {
        public const string Assigning = "assigning";
        public const string Active = "active";
        public const string Draining = "draining";
        public const string Orphaned = "orphaned";
    }

    public static class WorkerStatus
    {
        public const string Starting = "starting";
        public const string Ready = "ready";
        public const string Draining = "draining";
        public const string Dead = "dead";
    }

    /// <summary>
    /// Everything the mesh needs from the control plane: a node registry and container leases with epochs.
    /// Low write volume, subscription push, never on the per-tick hot path. <see cref="SpacetimeControlPlane"/>
    /// is the real implementation; <see cref="LocalControlPlane"/> is in-process for tests and single-process runs.
    /// All callbacks fire on the main thread from <see cref="Tick"/>.
    /// </summary>
    public interface IControlPlane : IDisposable
    {
        bool IsConnected { get; }
        /// <summary>Best estimate of the control plane's clock (for heartbeat age checks).</summary>
        DateTime Now { get; }
        event Action Changed;

        IReadOnlyList<WorkerInfo> Workers { get; }
        IReadOnlyList<LeaseInfo> Leases { get; }
        IReadOnlyList<GatewayInfo> Gateways { get; }
        /// <summary>
        /// Mesh-wide live settings: string key/values Nebula attaches no meaning to. The orchestrator seeds them
        /// (<c>-nebula-settings k=v,k=v</c>), the dashboard edits them, and game code on a worker may write them.
        /// They are the game's low-volume coordination channel across workers (e.g. a spawn budget every worker
        /// reconciles against); never per-tick state.
        /// </summary>
        IReadOnlyDictionary<string, string> Settings { get; }

        void Connect();
        void Tick();

        void RegisterWorker(string workerId, uint workerIndex, string address, ushort port);
        void HeartbeatWorker(string workerId, string status, in WorkerStats stats);
        void UnregisterWorker(string workerId);

        void RegisterGateway(string gatewayId, string address, ushort port);
        void HeartbeatGateway(string gatewayId);
        void UnregisterGateway(string gatewayId);

        void HeartbeatOrchestrator(string orchestratorId, uint desiredWorkers);

        /// <summary>Set (or create) one mesh-wide setting. Every subscriber sees it through <see cref="Changed"/>.</summary>
        void SetSetting(string key, string value);

        void EnsureContainer(string containerId);
        void AssignContainer(string containerId, string workerId);
        void SetLeaseState(string containerId, string state);
        void ReleaseContainer(string containerId);
        void ResetControlPlane();
    }

    public static class ControlPlaneExtensions
    {
        public static WorkerInfo FindWorker(this IControlPlane cp, string workerId)
        {
            foreach (var w in cp.Workers) if (w.WorkerId == workerId) return w;
            return null;
        }

        public static LeaseInfo FindLease(this IControlPlane cp, string containerId)
        {
            foreach (var l in cp.Leases) if (l.ContainerId == containerId) return l;
            return null;
        }

        public static string GetSetting(this IControlPlane cp, string key, string fallback = null)
        {
            return cp.Settings != null && cp.Settings.TryGetValue(key, out var v) ? v : fallback;
        }

        public static int GetSettingInt(this IControlPlane cp, string key, int fallback)
        {
            return cp.Settings != null && cp.Settings.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;
        }

        public static bool IsWorkerAlive(this IControlPlane cp, WorkerInfo w, float timeoutSeconds)
        {
            if (w == null || w.Status == WorkerStatus.Dead) return false;
            return (cp.Now - w.LastHeartbeat).TotalSeconds <= timeoutSeconds;
        }
    }
}
