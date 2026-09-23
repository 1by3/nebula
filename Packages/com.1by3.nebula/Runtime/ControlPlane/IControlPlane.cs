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
        /// <summary>The worker holds at least one always-relevant entity (see <see cref="WorkerStats.HasGlobalEntities"/>).</summary>
        public bool HasGlobalEntities;
        /// <summary>
        /// How long the oldest dirty persistent entity on this worker has been waiting for its next checkpoint, in
        /// seconds; 0 when persistence is off or nothing is dirty. See <see cref="WorkerStats.OldestDirtySeconds"/>
        /// and docs/persistence-durability.md.
        /// </summary>
        public float OldestDirtySeconds;
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
        /// <summary>
        /// This worker holds at least one always-relevant entity. A gateway keeps a link to such a
        /// worker even when it subscribes no region there, because a global entity belongs in every client's set
        /// and nothing else would make the gateway ask for it.
        /// </summary>
        public bool HasGlobalEntities;
        /// <summary>
        /// <see cref="NebulaPersistence.OldestDirtyAgeSeconds"/> on this worker, or 0 when persistence is off. An
        /// additive field on the existing heartbeat message: older orchestrators simply do not read it.
        /// </summary>
        public float OldestDirtySeconds;
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
        /// Runtime containers (<see cref="ContainerRegistry.RegisterRuntime(ulong, ContainerPlacement, InstanceContainerInfo)"/>)
        /// carry their box on the lease row, so every process can register the same box from the row alone. False
        /// for a baked or carried container.
        /// </summary>
        public bool HasBounds;
        /// <summary>
        /// The parent container's id, or empty for a root of its scope (<c>docs/container-tree.md</c> D5). A child's
        /// <see cref="Center"/> is local to its parent's frame.
        /// </summary>
        public string ParentId = "";
        /// <summary>Box centre, in double: absolute for a root, local to the parent's frame for a child.</summary>
        public Double3 Center;
        public Vector3 BoundsSize;
        /// <summary>
        /// Who simulates what is inside. On a runtime row <see cref="ContainerAuthority.Inherited"/> means the row only
        /// carries the box: the orchestrator never deals it and nobody reads an owner from it. On a carried
        /// container's row (<c>label#netId</c>) <see cref="ContainerAuthority.Leased"/> means the container was
        /// authored leased: the orchestrator re-deals it instead of letting it follow its carrier.
        /// </summary>
        public ContainerAuthority Authority;
        /// <summary>The container has its own physics frame (<c>docs/container-tree.md</c> §3).</summary>
        public bool OwnPhysicsFrame;
        /// <summary>How a framed container's contents are bucketed for interest.</summary>
        public FrameInterestMode FrameInterest;
        /// <summary><see cref="Center"/> narrowed to float, for callers that only ever held small boxes.</summary>
        public Vector3 BoundsCenter
        {
            get => Center.ToVector3();
            set => Center = Double3.From(value);
        }
        /// <summary>The row's box as stored (absolute for a root, parent-local for a child), when <see cref="HasBounds"/>.</summary>
        public Bounds Bounds => new Bounds(BoundsCenter, BoundsSize);
        /// <summary>Whether the row's box is a root of its scope.</summary>
        public bool IsRoot => string.IsNullOrEmpty(ParentId);
        /// <summary>The row's placement, when <see cref="HasBounds"/>.</summary>
        public ContainerPlacement Placement => new ContainerPlacement
        {
            ParentId = ParentId ?? "", Center = Center, Size = BoundsSize, Authority = Authority,
            OwnPhysicsFrame = OwnPhysicsFrame, FrameInterest = FrameInterest,
        };
        /// <summary>
        /// A balancing hint was set for this container while the mesh runs (<see cref="IControlPlane.SetContainerHint"/>).
        /// It travels beside the lease and is stored with it, so an orchestrator that restarts sees it again; when
        /// false the baked hint (<see cref="Container.Hint"/>) stands.
        /// </summary>
        public bool HasHint;
        /// <summary>The runtime hint, when <see cref="HasHint"/>.</summary>
        public ContainerHint Hint = ContainerHint.Default;
        /// <summary>
        /// The orchestrator has published a capacity reading for this container
        /// (<see cref="IControlPlane.SetContainerCapacity"/>, docs/capacity-admission.md). False means no worker has
        /// reported cost for it lately, and an unknown container is never at capacity.
        /// </summary>
        public bool HasCapacity;
        /// <summary>The dominant cost component's share of its own budget, when <see cref="HasCapacity"/>. 1 = the whole budget.</summary>
        public float Saturation;
        /// <summary>Which component <see cref="Saturation"/> is about.</summary>
        public CostComponent Dominant;
        /// <summary>
        /// <see cref="Saturation"/> reached <c>NebulaConfig.CapacitySaturation</c>. The orchestrator decides it, so
        /// every gateway of a mesh answers the same way without knowing the threshold.
        /// </summary>
        public bool AtCapacity;
        /// <summary>
        /// Why the planner cannot relieve it by moving anything (<see cref="SaturationReport.Cause"/>), or
        /// <see cref="Nebula.SaturationCause.None"/> when the reading is cost telemetry alone.
        /// </summary>
        public SaturationCause SaturationCause;
    }

    /// <summary>
    /// What a gateway reports about itself on every heartbeat. The orchestrator publishes it in <c>/api/state</c>;
    /// whoever runs the gateway fleet (a hosting platform, an operator) sizes the fleet from these numbers, since a
    /// connection count alone says nothing about replication cost.
    /// </summary>
    public struct GatewayStats
    {
        /// <summary>
        /// Clients welcomed by this gateway that are still waiting for somewhere to spawn
        /// (<see cref="JoinState.Starting"/>). The orchestrator reads it as demand: a pending join at zero workers
        /// wakes the mesh at once, and no scale-in goes below one worker while it is non-zero.
        /// </summary>
        public uint PendingJoins;
        /// <summary>Welcomed clients with a pawn.</summary>
        public uint ActiveClients;
        /// <summary>Links that sent Hello and are being authenticated or placed.</summary>
        public uint JoiningClients;
        /// <summary>Sessions whose link dropped and that are being held for a reconnect (<see cref="NebulaConfig.SessionReclaimSeconds"/>).</summary>
        public uint ReconnectingClients;
        /// <summary>Client-facing traffic over the last heartbeat interval.</summary>
        public float PacketsInPerSecond, PacketsOutPerSecond, BytesInPerSecond, BytesOutPerSecond;
        /// <summary>Worker-facing traffic over the last heartbeat interval.</summary>
        public float WorkerBytesInPerSecond, WorkerBytesOutPerSecond;
        /// <summary>Process CPU over the interval as a fraction of one core (2.0 = two cores busy).</summary>
        public float Cpu;
        /// <summary>Working set in bytes.</summary>
        public ulong MemoryBytes;
        /// <summary>Longest gap between two ticks beyond the expected loop period, in milliseconds, over the interval.</summary>
        public float LoopLagMs;
        /// <summary>Worker links that completed the handshake.</summary>
        public uint WorkerConnections;

        // Interest management (design §12). These are what tell you whether the gateway is actually scoping: a
        // cache or an interest set that grows with the world rather than with the players is the failure mode
        // interest management exists to prevent, and it is invisible in the traffic figures above until it hurts.

        /// <summary>Entities in one client's interest set, averaged over the welcomed clients.</summary>
        public float InterestSetAvg;
        /// <summary>The largest interest set of any client.</summary>
        public uint InterestSetMax;
        /// <summary>Entity records this gateway caches: everything its clients' subscribed regions cover.</summary>
        public uint CachedEntities;
        /// <summary>Distinct regions subscribed across every worker link.</summary>
        public uint SubscribedRegions;
        /// <summary>Worker links held, and why (a comma-separated summary: region, foci, global, explicit, spawn, owned).</summary>
        public uint WorkerLinks;
        public string WorkerLinkReasons;
        /// <summary>Entity spawns and despawns sent to clients over the interval, per second.</summary>
        public float SpawnsPerSecond, DespawnsPerSecond;
        /// <summary>Time one interest evaluation takes, in milliseconds, over the interval.</summary>
        public float InterestEvalMsAvg, InterestEvalMsMax;
        /// <summary>Client interest evaluations run per second: welcomed clients x InterestEvalHz when the rotation is keeping up.</summary>
        public float InterestEvalsPerSecond;
        /// <summary>Bytes sent to one client per second, averaged over the welcomed clients, and the worst of them.</summary>
        public float BytesPerClientAvg, BytesPerClientMax;
        /// <summary>
        /// Exceptions thrown by the game's gateway extension since this gateway started (a total, not a rate).
        /// Anything but zero means a policy call, a client event or posted work threw: the gateway carried on —
        /// a throwing <c>Authorize</c> denies rather than allows — but the game is not deciding what it thinks.
        /// </summary>
        public uint ExtensionErrors;
        /// <summary>Registered, connected to the control plane, and accepting clients.</summary>
        public bool Ready;
        /// <summary>The gateway is taking itself out of service: refusing new clients and asking the ones it has to reconnect elsewhere.</summary>
        public bool Draining;
    }

    /// <summary>Describes a gateway registered with the control plane.</summary>
    public sealed class GatewayInfo
    {
        public string GatewayId;
        /// <summary>Changes on every start of the gateway process (<see cref="HelloMsg.Incarnation"/>); 0 for a row written by an older gateway.</summary>
        public uint Incarnation;
        public string Address;
        public ushort Port;
        public DateTime LastHeartbeat;
        /// <summary>What the gateway last reported about itself.</summary>
        public GatewayStats Stats;
        /// <summary>
        /// Somebody asked this gateway to drain (<see cref="IControlPlane.SetGatewayDraining"/>). The gateway sees
        /// the flag on its own row and stops accepting clients; <see cref="GatewayStats.Draining"/> is its acknowledgement.
        /// </summary>
        public bool DrainRequested;
        /// <summary>Shorthand for <see cref="GatewayStats.PendingJoins"/>.</summary>
        public uint PendingJoins { get => Stats.PendingJoins; set => Stats.PendingJoins = value; }
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
        /// <summary>
        /// The row belongs to an inherited container (<see cref="ContainerAuthority.Inherited"/>): it carries a box and
        /// a parent, and whoever owns the parent simulates what is inside. Never dealt, never owned.
        /// </summary>
        public const string Inherited = "inherited";

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
        /// <summary>
        /// Identity of the document containing process registrations, container assignments, and shared settings.
        /// Preserved when that document is restored from storage; replaced when the control plane starts empty
        /// or is reset. <see cref="WorkerRegistration"/> uses changes to this identity to reclaim missing assignments.
        /// A remote mirror reports an empty string until it receives a document with an identity.
        /// </summary>
        string DocumentId { get; }
        event Action Changed;

        IReadOnlyList<WorkerInfo> Workers { get; }
        IReadOnlyList<LeaseInfo> Leases { get; }
        IReadOnlyList<GatewayInfo> Gateways { get; }
        /// <summary>
        /// The shared simulation scopes that have been activated (<see cref="ActivateScope"/>), one row per key,
        /// mirrored to every role. The public world has no row. Design of record: <c>docs/scope-activation.md</c>.
        /// </summary>
        IReadOnlyList<ScopeInfo> Scopes { get; }
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

        /// <param name="incarnation">This start of the gateway process (<see cref="GatewayInfo.Incarnation"/>). A re-registration with a new incarnation clears a stale drain request.</param>
        void RegisterGateway(string gatewayId, string address, ushort port, uint incarnation = 0);
        /// <param name="stats">What the gateway reports about itself (<see cref="GatewayStats"/>).</param>
        void HeartbeatGateway(string gatewayId, in GatewayStats stats);
        void UnregisterGateway(string gatewayId);
        /// <summary>
        /// Ask a gateway to take itself out of service (or cancel that). The gateway refuses new clients and tells the
        /// ones it has to reconnect; whoever runs the fleet removes and stops it once <see cref="GatewayStats.ActiveClients"/>
        /// reaches zero or a drain timeout passes. Exposed by the orchestrator as <c>POST /api/gateways/drain</c>.
        /// </summary>
        void SetGatewayDraining(string gatewayId, bool draining);

        void HeartbeatOrchestrator(string orchestratorId, uint desiredWorkers);

        /// <summary>Set (or create) one mesh-wide setting. Every subscriber sees it through <see cref="Changed"/>.</summary>
        void SetSetting(string key, string value);

        /// <summary>
        /// Ask the mesh for a shared simulation scope by key, creating its containers from
        /// <see cref="ScopeActivationRequest.Definition"/> the first time. Idempotent: the first caller creates the
        /// rows, every later caller with the same definition changes nothing, and a caller with a *different*
        /// definition under the same key is refused so two worlds never share one scope. A game-supplied unique key
        /// is how a private copy is asked for; there is no separate call.
        /// <para>
        /// Like every other control-plane write this is fire and forget: the answer is the
        /// <see cref="Scopes"/> row, which appears on this process (and on every other) once the orchestrator has
        /// applied it. Poll it with <see cref="ControlPlaneExtensions.FindScope"/> and ask whether it can be
        /// entered with <see cref="ControlPlaneExtensions.IsScopeReady"/>.
        /// </para>
        /// </summary>
        void ActivateScope(ScopeActivationRequest request);
        /// <summary>
        /// Move a scope's lifecycle state on (<see cref="ScopeState"/>). The orchestrator's idle sweep is the single
        /// writer of the machine; nothing else in a mesh should call it. See <c>docs/scope-lifecycle.md</c>.
        /// </summary>
        void SetScopeState(string scopeKey, string state);
        /// <summary>
        /// A worker reporting that it finished the step the scope is in for one of its containers: the checkpoint
        /// before a retire, or the restore after a re-activation (<see cref="ScopePhase"/>). The orchestrator waits
        /// for one ack per container before taking the next step.
        /// </summary>
        void AckScopePart(string scopeKey, string containerId, string phase, int count, string workerId);
        /// <summary>
        /// Drop a scope's row, its durable claim and the lease rows of its containers. The mechanism only: when a
        /// scope should go is the game's or NEB-240's decision, and entities still in it are not moved.
        /// </summary>
        void RemoveScope(string scopeKey);

        /// <summary>
        /// Make sure a lease row exists for a container, unassigned. <paramref name="authority"/> is recorded on a new
        /// row only: a carried container authored <see cref="ContainerAuthority.Leased"/> says so here, so the
        /// orchestrator re-deals it rather than letting it follow its carrier (<c>docs/container-tree.md</c> D6). The
        /// frame flags tell every gateway that a carried container has a physics frame of its own and how its contents
        /// are bucketed for interest (D18).
        /// </summary>
        void EnsureContainer(string containerId, ContainerAuthority authority = ContainerAuthority.Auto, bool ownPhysicsFrame = false, FrameInterestMode frameInterest = FrameInterestMode.WithCarrier);
        /// <summary>
        /// Make sure a lease row exists for a runtime container, carrying its placement (a root's absolute box, or a
        /// child's parent id and parent-local box; a <c>Bounds</c> converts to a root) and, when
        /// <paramref name="workerId"/> is given and the container is leased, already assigned to that worker (epoch
        /// 1, active), so the worker that asked for the container owns it from the first change anyone sees. An
        /// inherited container's row is never assigned. A no-op when the row exists: whoever asked first wins, and
        /// the second caller sees the row on the next change.
        /// </summary>
        void EnsureRuntimeContainer(string containerId, ContainerPlacement placement, string workerId, InstanceContainerInfo instance = null);
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
        /// <summary>
        /// Publish what the cost telemetry says about how full a container is, so a gateway can answer "is this
        /// target at capacity" from the document it already mirrors instead of asking the orchestrator
        /// (docs/capacity-admission.md). The orchestrator is the only writer. It does <b>not</b> stamp
        /// <see cref="LeaseInfo.UpdatedAt"/>: that is the mesh's idle clock, and a reading published every pass
        /// would keep every scope hot for ever.
        /// </summary>
        void SetContainerCapacity(string containerId, float saturation, CostComponent dominant, bool atCapacity, SaturationCause cause = SaturationCause.None);
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

        public static GatewayInfo FindGateway(this IControlPlane cp, string gatewayId)
        {
            foreach (var g in cp.Gateways) if (g.GatewayId == gatewayId) return g;
            return null;
        }

        public static LeaseInfo FindLease(this IControlPlane cp, string containerId)
        {
            foreach (var l in cp.Leases) if (l.ContainerId == containerId) return l;
            return null;
        }

        /// <summary>The scope row for <paramref name="scopeKey"/>, or null when the key has not been activated here yet.</summary>
        public static ScopeInfo FindScope(this IControlPlane cp, string scopeKey)
        {
            if (string.IsNullOrEmpty(scopeKey) || cp.Scopes == null) return null;
            foreach (var s in cp.Scopes) if (string.Equals(s.ScopeKey, scopeKey, StringComparison.Ordinal)) return s;
            return null;
        }

        /// <summary>
        /// Whether the scope can be entered: every container of it has a lease row in an owning state
        /// (<see cref="LeaseState.IsOwning"/>) held by a worker that is still heartbeating. It does <b>not</b> say
        /// that the worker has loaded the scope's content — that stays with the per-crossing handshake
        /// (<see cref="NebulaWorker.PrepareTransfer"/>). See <c>docs/scope-activation.md</c> §4.
        /// </summary>
        public static bool IsScopeReady(this IControlPlane cp, ScopeInfo scope, float workerTimeoutSeconds = 15f)
        {
            if (scope == null || scope.ContainerIds == null || scope.ContainerIds.Count == 0) return false;
            foreach (var containerId in scope.ContainerIds)
            {
                var lease = cp.FindLease(containerId);
                if (lease != null && lease.State == LeaseState.Inherited) continue; // ready when its parent is
                if (lease == null || !LeaseState.IsOwning(lease.State) || string.IsNullOrEmpty(lease.WorkerId)) return false;
                if (!cp.IsWorkerAlive(cp.FindWorker(lease.WorkerId), workerTimeoutSeconds)) return false;
            }
            return true;
        }

        /// <summary>
        /// How full a container is, as the orchestrator last published it (<see cref="NebulaCapacity.Of"/>).
        /// </summary>
        public static CapacityInfo CapacityOf(this IControlPlane cp, string containerId) => NebulaCapacity.Of(cp, containerId);

        /// <summary>
        /// How full a whole scope is: the worst of its parts (<see cref="NebulaCapacity.OfScope"/>).
        /// </summary>
        public static CapacityInfo ScopeCapacityOf(this IControlPlane cp, string scopeKey) => NebulaCapacity.OfScope(cp, scopeKey);

        /// <summary>Shorthand for <see cref="IsScopeReady(IControlPlane, ScopeInfo, float)"/> by key.</summary>
        public static bool IsScopeReady(this IControlPlane cp, string scopeKey, float workerTimeoutSeconds = 15f) =>
            cp.IsScopeReady(cp.FindScope(scopeKey), workerTimeoutSeconds);

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
