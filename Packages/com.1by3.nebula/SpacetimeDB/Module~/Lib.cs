// Nebula control plane module.
//
// This SpacetimeDB module is the *control plane* only: node registry, container
// ownership leases and authority epochs. It is deliberately tiny and low-write.
// It must never carry per-tick game state - that flows over the lateral links
// (worker<->worker) and through the gateway (worker<->client). See docs/architecture.md.
//
// Tables are written by the orchestrator (leases) and by workers/gateways
// (registration + heartbeat). Everybody else only subscribes.

using SpacetimeDB;

public static partial class Module
{
    // ---------------------------------------------------------------- tables

    /// A sim worker process. One row per running Nebula worker.
    [SpacetimeDB.Table(Accessor = "worker", Public = true)]
    public partial struct Worker
    {
        [SpacetimeDB.PrimaryKey]
        public string WorkerId;
        /// Small dense index handed out by the orchestrator. Used for entity id
        /// allocation (high bits) and debug colouring.
        public uint WorkerIndex;
        public string Address;
        public ushort Port;
        /// starting | ready | draining | dead
        public string Status;
        public Timestamp LastHeartbeat;
        public ulong TickCount;
        public float TickMs;
        /// Every entity resident on the worker (authoritative + ghosts).
        public uint EntityCount;
        public uint AuthoritativeCount;
        public uint GhostCount;
        /// Authoritative entities owned by a human client / by a bot client / by nobody (server-driven).
        public uint PlayerCount;
        public uint BotCount;
        public uint ServerDrivenCount;
    }

    /// Authority lease for one container. Exactly one row per container in the
    /// manifest. WorkerId == "" means unassigned/orphaned.
    [SpacetimeDB.Table(Accessor = "container_lease", Public = true)]
    public partial struct ContainerLease
    {
        [SpacetimeDB.PrimaryKey]
        public string ContainerId;
        public string WorkerId;
        /// Monotonic per-container authority epoch. Bumped on every (re)assignment.
        public ulong Epoch;
        /// assigning | active | draining | orphaned
        public string State;
        public Timestamp UpdatedAt;
    }

    /// A client-facing gateway process.
    [SpacetimeDB.Table(Accessor = "gateway", Public = true)]
    public partial struct Gateway
    {
        [SpacetimeDB.PrimaryKey]
        public string GatewayId;
        public string Address;
        public ushort Port;
        public Timestamp LastHeartbeat;
    }

    /// The orchestrator. Single row in practice; lets workers and gateways tell
    /// whether somebody is driving the mesh.
    [SpacetimeDB.Table(Accessor = "orchestrator", Public = true)]
    public partial struct Orchestrator
    {
        [SpacetimeDB.PrimaryKey]
        public string OrchestratorId;
        public Timestamp LastHeartbeat;
        public uint DesiredWorkers;
    }

    /// A mesh-wide live setting, written by whoever is allowed to change the mesh at
    /// runtime (game code through a worker, the dashboard) and seeded by the orchestrator
    /// at start (-nebula-settings). Nebula gives them no meaning: they are the game's
    /// low-volume coordination channel (e.g. ShooterGame keeps its NPC total here).
    [SpacetimeDB.Table(Accessor = "game_setting", Public = true)]
    public partial struct GameSetting
    {
        [SpacetimeDB.PrimaryKey]
        public string Key;
        public string Value;
        /// Bumped on every change.
        public ulong Version;
        public Timestamp UpdatedAt;
    }

    // -------------------------------------------------------------- reducers

    [SpacetimeDB.Reducer]
    public static void RegisterWorker(ReducerContext ctx, string workerId, uint workerIndex, string address, ushort port)
    {
        if (ctx.Db.worker.WorkerId.Find(workerId) is { } row)
        {
            row.WorkerIndex = workerIndex;
            row.Address = address;
            row.Port = port;
            row.Status = "starting";
            row.LastHeartbeat = ctx.Timestamp;
            ctx.Db.worker.WorkerId.Update(row);
        }
        else
        {
            ctx.Db.worker.Insert(new Worker
            {
                WorkerId = workerId,
                WorkerIndex = workerIndex,
                Address = address,
                Port = port,
                Status = "starting",
                LastHeartbeat = ctx.Timestamp,
                TickCount = 0,
                TickMs = 0,
                EntityCount = 0,
                AuthoritativeCount = 0,
                GhostCount = 0,
                PlayerCount = 0,
                BotCount = 0,
                ServerDrivenCount = 0,
            });
        }
    }

    [SpacetimeDB.Reducer]
    public static void HeartbeatWorker(ReducerContext ctx, string workerId, string status, ulong tickCount, float tickMs,
        uint entityCount, uint authoritativeCount, uint ghostCount, uint playerCount, uint botCount, uint serverDrivenCount)
    {
        if (ctx.Db.worker.WorkerId.Find(workerId) is { } row)
        {
            row.Status = status;
            row.LastHeartbeat = ctx.Timestamp;
            row.TickCount = tickCount;
            row.TickMs = tickMs;
            row.EntityCount = entityCount;
            row.AuthoritativeCount = authoritativeCount;
            row.GhostCount = ghostCount;
            row.PlayerCount = playerCount;
            row.BotCount = botCount;
            row.ServerDrivenCount = serverDrivenCount;
            ctx.Db.worker.WorkerId.Update(row);
        }
    }

    [SpacetimeDB.Reducer]
    public static void UnregisterWorker(ReducerContext ctx, string workerId)
    {
        ctx.Db.worker.WorkerId.Delete(workerId);
        // Any lease this worker held becomes orphaned; the orchestrator reassigns it.
        foreach (var lease in ctx.Db.container_lease.Iter())
        {
            if (lease.WorkerId == workerId)
            {
                var l = lease;
                l.WorkerId = "";
                l.State = "orphaned";
                l.UpdatedAt = ctx.Timestamp;
                ctx.Db.container_lease.ContainerId.Update(l);
            }
        }
    }

    [SpacetimeDB.Reducer]
    public static void RegisterGateway(ReducerContext ctx, string gatewayId, string address, ushort port)
    {
        if (ctx.Db.gateway.GatewayId.Find(gatewayId) is { } row)
        {
            row.Address = address;
            row.Port = port;
            row.LastHeartbeat = ctx.Timestamp;
            ctx.Db.gateway.GatewayId.Update(row);
        }
        else
        {
            ctx.Db.gateway.Insert(new Gateway
            {
                GatewayId = gatewayId,
                Address = address,
                Port = port,
                LastHeartbeat = ctx.Timestamp,
            });
        }
    }

    [SpacetimeDB.Reducer]
    public static void HeartbeatGateway(ReducerContext ctx, string gatewayId)
    {
        if (ctx.Db.gateway.GatewayId.Find(gatewayId) is { } row)
        {
            row.LastHeartbeat = ctx.Timestamp;
            ctx.Db.gateway.GatewayId.Update(row);
        }
    }

    [SpacetimeDB.Reducer]
    public static void UnregisterGateway(ReducerContext ctx, string gatewayId)
    {
        ctx.Db.gateway.GatewayId.Delete(gatewayId);
    }

    [SpacetimeDB.Reducer]
    public static void HeartbeatOrchestrator(ReducerContext ctx, string orchestratorId, uint desiredWorkers)
    {
        if (ctx.Db.orchestrator.OrchestratorId.Find(orchestratorId) is { } row)
        {
            row.LastHeartbeat = ctx.Timestamp;
            row.DesiredWorkers = desiredWorkers;
            ctx.Db.orchestrator.OrchestratorId.Update(row);
        }
        else
        {
            ctx.Db.orchestrator.Insert(new Orchestrator
            {
                OrchestratorId = orchestratorId,
                LastHeartbeat = ctx.Timestamp,
                DesiredWorkers = desiredWorkers,
            });
        }
    }

    /// Make sure a lease row exists for a container from the manifest.
    [SpacetimeDB.Reducer]
    public static void EnsureContainer(ReducerContext ctx, string containerId)
    {
        if (ctx.Db.container_lease.ContainerId.Find(containerId) is null)
        {
            ctx.Db.container_lease.Insert(new ContainerLease
            {
                ContainerId = containerId,
                WorkerId = "",
                Epoch = 0,
                State = "orphaned",
                UpdatedAt = ctx.Timestamp,
            });
        }
    }

    /// Orchestrator: authoritatively assign a container to a worker. Bumps the
    /// container epoch so stale owners can be rejected.
    [SpacetimeDB.Reducer]
    public static void AssignContainer(ReducerContext ctx, string containerId, string workerId)
    {
        if (ctx.Db.container_lease.ContainerId.Find(containerId) is { } row)
        {
            if (row.WorkerId == workerId && row.State == "active")
            {
                return; // no-op, do not burn an epoch
            }
            row.WorkerId = workerId;
            row.Epoch += 1;
            row.State = "active";
            row.UpdatedAt = ctx.Timestamp;
            ctx.Db.container_lease.ContainerId.Update(row);
        }
        else
        {
            ctx.Db.container_lease.Insert(new ContainerLease
            {
                ContainerId = containerId,
                WorkerId = workerId,
                Epoch = 1,
                State = "active",
                UpdatedAt = ctx.Timestamp,
            });
        }
    }

    [SpacetimeDB.Reducer]
    public static void SetLeaseState(ReducerContext ctx, string containerId, string state)
    {
        if (ctx.Db.container_lease.ContainerId.Find(containerId) is { } row)
        {
            row.State = state;
            row.UpdatedAt = ctx.Timestamp;
            ctx.Db.container_lease.ContainerId.Update(row);
        }
    }

    [SpacetimeDB.Reducer]
    public static void ReleaseContainer(ReducerContext ctx, string containerId)
    {
        if (ctx.Db.container_lease.ContainerId.Find(containerId) is { } row)
        {
            row.WorkerId = "";
            row.State = "orphaned";
            row.UpdatedAt = ctx.Timestamp;
            ctx.Db.container_lease.ContainerId.Update(row);
        }
    }

    /// Set (or create) one mesh-wide setting. A no-op when the value is unchanged.
    [SpacetimeDB.Reducer]
    public static void SetGameSetting(ReducerContext ctx, string key, string value)
    {
        if (ctx.Db.game_setting.Key.Find(key) is { } row)
        {
            if (row.Value == value) return;
            row.Value = value;
            row.Version += 1;
            row.UpdatedAt = ctx.Timestamp;
            ctx.Db.game_setting.Key.Update(row);
        }
        else
        {
            ctx.Db.game_setting.Insert(new GameSetting { Key = key, Value = value, Version = 1, UpdatedAt = ctx.Timestamp });
        }
    }

    /// Dev helper: wipe all registry state (used when restarting a local mesh).
    [SpacetimeDB.Reducer]
    public static void ResetControlPlane(ReducerContext ctx)
    {
        foreach (var s in ctx.Db.game_setting.Iter()) ctx.Db.game_setting.Key.Delete(s.Key);
        foreach (var w in ctx.Db.worker.Iter()) ctx.Db.worker.WorkerId.Delete(w.WorkerId);
        foreach (var g in ctx.Db.gateway.Iter()) ctx.Db.gateway.GatewayId.Delete(g.GatewayId);
        foreach (var o in ctx.Db.orchestrator.Iter()) ctx.Db.orchestrator.OrchestratorId.Delete(o.OrchestratorId);
        foreach (var l in ctx.Db.container_lease.Iter()) ctx.Db.container_lease.ContainerId.Delete(l.ContainerId);
    }
}
