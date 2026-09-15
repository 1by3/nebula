using System;
using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>Describes a worker registered with the control plane.</summary>
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

    /// <summary>Describes the current worker assignment for one container.</summary>
    public sealed class LeaseInfo
    {
        public InstanceContainerInfo Instance;
        public string ContainerId;
        public string WorkerId;
        public ulong Epoch;
        public string State;
        public DateTime UpdatedAt;
        /// <summary>
        /// Runtime containers (<see cref="ContainerRegistry.RegisterRuntime"/>) carry their box on the lease row, in
        /// absolute world coordinates, so every process can register the same box from the row alone. False for a
        /// baked or carried container.
        /// </summary>
        public bool HasBounds;
        public Vector3 BoundsCenter;
        public Vector3 BoundsSize;
        /// <summary>The row's box, when <see cref="HasBounds"/>.</summary>
        public Bounds Bounds => new Bounds(BoundsCenter, BoundsSize);
        /// <summary>
        /// A balancing hint was set for this container while the mesh runs (<see cref="IControlPlane.SetContainerHint"/>).
        /// It travels beside the lease and is stored with it, so an orchestrator that restarts sees it again; when
        /// false the baked hint (<see cref="Container.Hint"/>) stands.
        /// </summary>
        public bool HasHint;
        /// <summary>The runtime hint, when <see cref="HasHint"/>.</summary>
        public ContainerHint Hint = ContainerHint.Default;
    }

    /// <summary>Describes a gateway registered with the control plane.</summary>
    public sealed class GatewayInfo
    {
        public string GatewayId;
        public string Address;
        public ushort Port;
        public DateTime LastHeartbeat;
        /// <summary>
        /// Clients welcomed by this gateway that are still waiting for somewhere to spawn
        /// (<see cref="JoinState.Starting"/>). The orchestrator reads it as demand: a pending join at zero workers
        /// wakes the mesh at once, and no scale-in goes below one worker while it is non-zero.
        /// </summary>
        public uint PendingJoins;
    }

    /// <summary>Provides the states used by a container lease.</summary>
    public static class LeaseState
    {
        public const string Assigning = "assigning";
        public const string Active = "active";
        public const string Draining = "draining";
        public const string Orphaned = "orphaned";
        /// <summary>
        /// Dynamic containers only: the orchestrator (or the dashboard) assigned this carried container to a worker of
        /// its own, so it no longer follows its carrier. Anything else means "follows the carrier" for such a lease.
        /// </summary>
        public const string Pinned = "pinned";

        /// <summary>The lease names a worker that currently simulates the container.</summary>
        public static bool IsOwning(string state) => state == Active || state == Draining || state == Pinned;
    }

    /// <summary>
    /// Mesh-wide settings Nebula itself writes, in the same key/value channel the game uses
    /// (<see cref="IControlPlane.Settings"/>). The <c>nebula.</c> prefix is reserved for them.
    /// </summary>
    public static class MeshSettings
    {
        /// <summary>
        /// Roughly how long this mesh's worker host takes to boot a worker, in seconds
        /// (<see cref="IWorkerHost.TypicalBootSeconds"/>). Seeded by the orchestrator; read by the gateway, which
        /// passes it to a client it is holding in <see cref="JoinState.Starting"/> so the game can say how long.
        /// </summary>
        public const string BootSeconds = "nebula.bootSeconds";
    }

    /// <summary>Provides the lifecycle states reported by a worker.</summary>
    public static class WorkerStatus
    {
        public const string Starting = "starting";
        public const string Ready = "ready";
        public const string Draining = "draining";
        public const string Dead = "dead";
    }

    /// <summary>
    /// Provides process registration, container assignments with epochs, and game-defined settings. Use
    /// <see cref="ControlPlaneHost"/> on the orchestrator and <see cref="RemoteControlPlane"/> on workers and
    /// gateways of a mesh. Use <see cref="LocalControlPlane"/> for tests and
    /// single-process runs. Call <see cref="Tick"/> to receive callbacks on the main thread. Do not use the control
    /// plane for per-tick entity state.
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
        /// <param name="pendingJoins">Welcomed clients with nowhere to spawn yet (<see cref="GatewayInfo.PendingJoins"/>).</param>
        void HeartbeatGateway(string gatewayId, uint pendingJoins);
        void UnregisterGateway(string gatewayId);

        void HeartbeatOrchestrator(string orchestratorId, uint desiredWorkers);

        /// <summary>Set (or create) one mesh-wide setting. Every subscriber sees it through <see cref="Changed"/>.</summary>
        void SetSetting(string key, string value);

        void EnsureContainer(string containerId);
        /// <summary>
        /// Make sure a lease row exists for a runtime container, carrying its box (absolute coordinates) and, when
        /// <paramref name="workerId"/> is given, already assigned to that worker (epoch 1, active), so the worker
        /// that asked for the container owns it from the first change anyone sees. A no-op when the row exists:
        /// whoever asked first wins, and the second caller sees the row on the next change.
        /// </summary>
        void EnsureRuntimeContainer(string containerId, Bounds bounds, string workerId, InstanceContainerInfo instance = null);
        /// <summary>
        /// Stamp a lease row's <see cref="LeaseInfo.UpdatedAt"/> without changing anything else: a worker that still
        /// wants a runtime container it does not own says so, and the owner reads the age before retiring the box.
        /// </summary>
        void TouchContainer(string containerId);
        /// <summary>Assign a container to a worker (epoch + 1, state active). Leaves a pinned lease alone.</summary>
        void AssignContainer(string containerId, string workerId);
        /// <summary>
        /// Pin a carried container to <paramref name="workerId"/>: worker, epoch and <see cref="LeaseState.Pinned"/>
        /// in one change, so no subscriber ever sees the lease assigned but not yet pinned (the carrier's worker
        /// would reclaim it). Unpin with <see cref="SetLeaseState"/>.
        /// </summary>
        void PinContainer(string containerId, string workerId);
        void SetLeaseState(string containerId, string state);
        /// <summary>
        /// Attach a balancing hint to a container, creating the lease row if it does not exist yet: the game (or the
        /// dashboard) telling the planner something it cannot measure while the mesh runs. The row outlives an
        /// orchestrator restart, and the value wins over whatever was baked. Setting <see cref="ContainerHint.Default"/>
        /// clears the runtime hint and lets the baked one stand again.
        /// </summary>
        void SetContainerHint(string containerId, in ContainerHint hint);
        void ReleaseContainer(string containerId);
        /// <summary>Delete a lease row outright (a dynamic container whose carrier despawned).</summary>
        void RemoveContainer(string containerId);
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
