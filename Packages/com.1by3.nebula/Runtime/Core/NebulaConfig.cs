using System.Collections.Generic;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Project-wide Nebula settings. Workers and clients load Resources/NebulaConfig. Builds export its settings
    /// to nebula-services.json for the standalone orchestrator and gateway. Command-line switches override fields at runtime.
    /// </summary>
    [CreateAssetMenu(menuName = "Nebula/Config", fileName = "NebulaConfig")]
    public sealed partial class NebulaConfig : ScriptableObject
    {
        [Header("Scene")]
        [Tooltip("Scene containing the Container volumes and gameplay. Loaded by workers and clients; builds export its containers for the services.")]
        public string GameScene = "Arena";

        [Header("World partition (optional)")]
        [Tooltip("Baked container manifest of an authored partitioned world (Nebula > World). Leave empty for a single scene or a RuntimeWorld.")]
        public WorldContainerManifest WorldManifest;
        /// <summary>
        /// Definition of a procedural world whose containers are registered while the mesh runs. Nebula loads its
        /// floating-origin frame without authored cell scenes. Leave <see cref="WorldManifest"/> unset: a baked
        /// manifest takes precedence when both are assigned.
        /// </summary>
        [Tooltip("World definition for a procedural runtime-container world. Loads the floating origin without authored cell scenes; leave WorldManifest empty.")]
        public WorldDefinition RuntimeWorld;
        [Tooltip("Clients keep this many cells around the local pawn loaded (1 = the 3x3x3 block).")]
        public int ClientLoadRadiusCells = 1;
        [Tooltip("Workers keep this many rings of neighboring cells loaded around every cell they control. Ghosts and boundary physics need this geometry.")]
        public int WorkerLoadRingCells = 1;
        [Tooltip("The floating origin moves once the pawn (client) or the centroid of the leased cells (worker) is more than this many cells from the origin cell.")]
        public int OriginShiftThresholdCells = 4;

        [Header("Networking")]
        public string GatewayAddress = "127.0.0.1";
        public ushort GatewayPort = 7000;
        [Tooltip("Workers listen on WorkerBasePort + workerIndex.")]
        public ushort WorkerBasePort = 7100;
        [Tooltip("Address workers advertise to peers and the gateway.")]
        public string WorkerAdvertiseAddress = "127.0.0.1";

        [Header("Web clients")]
        [Tooltip("The standalone gateway also accepts clients from a Web build: WebRTC signaling over HTTP on WebPort and data channels over UDP on WebRtcPort. It serves the web build too when a Web folder sits next to it. -nebula-web false turns this off.")]
        public bool WebClients = true;
        [Tooltip("TCP port of the gateway's HTTP server (WebRTC signaling and the web build). 0 = the same number as GatewayPort. A browser client connects to this port. -nebula-web-port overrides.")]
        public ushort WebPort = 0;
        [Tooltip("UDP port browsers' WebRTC traffic arrives on. 0 = GatewayPort + 1. -nebula-webrtc-port overrides.")]
        public ushort WebRtcPort = 0;

        [Header("Control plane")]
        [Tooltip("Address of the orchestrator that hosts the control plane (its dashboard, e.g. http://127.0.0.1:7080/), for a worker or gateway started by hand. Processes the orchestrator launches get it on the command line (-nebula-control-plane).")]
        public string ControlPlaneUrl = "http://127.0.0.1:7080/";
        [Tooltip("Shared secret every worker and gateway presents to the orchestrator. Empty = none (fine on a development machine or a private network). -nebula-token overrides.")]
        public string MeshToken = "";
        [Tooltip("Keep the control plane in this process instead of talking to an orchestrator. Only meaningful when orchestrator, gateway and worker share a process (tests, single-process runs).")]
        public bool UseLocalControlPlane = false;
        [Tooltip("Where the orchestrator keeps the control plane and saved entities: sqlite:<file> or postgres://user:password@host/db (standalone orchestrator), file:<folder> (Unity orchestrator), or memory. Empty = a default next to the process. -nebula-database overrides.")]
        public string DatabaseUrl = "";

        [Header("Authentication")]
        [Tooltip("OpenID Connect providers whose ID tokens the gateway accepts, as issuer URLs separated by commas (for example https://accounts.google.com or https://your-tenant.auth0.com/). The gateway fetches each provider's signing keys from its discovery document. Empty = no sign-in tokens are accepted. -nebula-auth-issuers overrides.")]
        public string AuthIssuers = "";
        [Tooltip("The audience (aud claim) a token must carry: the client id the provider issued your game. Empty skips the check, which lets a token minted for another app of the same provider join. -nebula-auth-audience overrides.")]
        public string AuthAudience = "";
        [Tooltip("Let clients that present no token join with an anonymous identity the gateway issues and the client keeps for its next connection. Off = every client needs an ID token from AuthIssuers. -nebula-auth-anonymous overrides.")]
        public bool AuthAnonymous = true;
        [Tooltip("Secret the anonymous identity tokens are signed with; every gateway of a mesh must use the same one. Empty = derived from MeshToken, or, with no mesh token either, a random key kept in nebula-auth.key next to the gateway. -nebula-auth-key overrides; the standalone gateway also reads NEBULA_AUTH_KEY.")]
        public string AuthSigningKey = "";
        [Tooltip("Seconds a worker keeps a player's pawn after the player's connection drops, so the player can reconnect (through any gateway of the mesh) and continue with the same session and pawn. 0 despawns at once.")]
        public float SessionReclaimSeconds = 30f;
        [Tooltip("Coordinate one connection per player through the gateway fleet. A replacement waits until the previous gateway confirms disconnection; if that cannot be coordinated, the new join is rejected. Off lets one identity hold several pawns at once. -nebula-single-session overrides.")]
        public bool SingleSessionPerPlayer = true;
        [Tooltip("When a gateway is asked to drain, how many seconds its clients are told they have to reconnect before it closes their links.")]
        public float GatewayDrainReconnectSeconds = 10f;

        [Header("Orchestrator")]
        [Tooltip("How many worker processes the orchestrator starts with. Autoscaling then moves the count between MinWorkers and MaxWorkers.")]
        public int WorkerCount = 4;
        [Tooltip("The most workers the orchestrator will ever run (dashboard and autoscale stop here). Worker indices are 16-bit, so up to 65535.")]
        public int MaxWorkers = 32;
        [Tooltip("How containers are dealt to workers: 'auto' (the cost policy: by reported load along a space-filling curve), 'baked' (the opt-out: evenly by count, sticky), 'cost' (the same as auto, named explicitly). -nebula-assignment overrides.")]
        public string AssignmentPolicy = "auto";
        [Tooltip("Cost policy: how far above the average a worker's cost may be before containers are re-dealt (0.3 = 30 %).")]
        public float CostRebalanceThreshold = 0.3f;
        [Tooltip("Cost policy: what one player / bot / server-driven entity / other entity costs, and what a leased container costs by itself.")]
        public CostWeights CostWeights = CostWeights.Default;

        /// <summary>The default of <see cref="CostLinkBudgetMbps"/> in bytes per second: 100 Mbit/s.</summary>
        public const double DefaultCostLinkBytesPerSec = 100.0 * 1000.0 * 1000.0 / 8.0;

        /// <summary><see cref="CostLinkBudgetMbps"/> in bytes per second; the default when it is 0 or less.</summary>
        public double CostLinkBytesPerSec => CostLinkBudgetMbps > 0f ? CostLinkBudgetMbps * 1000.0 * 1000.0 / 8.0 : DefaultCostLinkBytesPerSec;
        [Tooltip("Cost telemetry: the outbound budget one worker's traffic is weighed against, in megabits per second. It decides nothing about what is sent; it is the yardstick that lets a container's bytes be compared with its simulation time, so the dashboard and the scaler can say whether a hot container is hot in simulation, in replication or in gateway relay.")]
        public float CostLinkBudgetMbps = 100f;
        [Tooltip("Keep the worker count between MinWorkers and MaxWorkers from how busy the workers are: add one when the busiest worker's tick time stays over ScaleOutUtilization of the tick budget, remove one when the mesh's mean stays under ScaleInUtilization. Every change is checked against a dry run of the assignment policy first.")]
        public bool AutoScale = true;
        [Tooltip("The fewest workers autoscaling will leave running. 0 allows scaling to zero (the first player then waits for a worker to boot).")]
        public int MinWorkers = 1;
        [Tooltip("Grow when the busiest worker spends more than this fraction of its tick budget simulating (0.7 = 11.7 ms of a 16.7 ms tick), for ScaleHoldSeconds.")]
        public float ScaleOutUtilization = 0.7f;
        [Tooltip("Shrink when the mesh's mean utilization stays under this fraction of the tick budget for ScaleHoldSeconds and the survivors would stay under ScaleOutUtilization.")]
        public float ScaleInUtilization = 0.3f;
        [Tooltip("How long a condition must hold before a worker is added or retired. Only one change happens per hold.")]
        public float ScaleHoldSeconds = 30f;
        [Tooltip("Length of the rolling per-worker tick-time window whose 90th percentile is the scaling signal.")]
        public float ScaleWindowSeconds = 20f;
        [Tooltip("The smallest predicted drop in the busiest worker's utilization that makes another worker (or a re-deal) worth it. Below this the orchestrator reports the hot container as unsplittable instead of growing.")]
        public float ScaleMinGain = 0.1f;
        [Tooltip("How long a retired worker is kept in the idle pool, ready to be taken back without a boot, on a host where that is cheaper than recreating it (a cloud VM). 0 = the host's own answer: Hetzner keeps it for the rest of the hour it has already been billed, a local process is killed at once.")]
        public float IdlePoolSeconds = 0f;
        [Tooltip("Cost policy: a container with a player within this many metres of a seam that would move is left alone for a pass (see ContainerHint, phase 3).")]
        public float SeamGraceMeters = 10f;
        [Tooltip("Unity executable used to spawn workers. nebula start supplies this path to the standalone orchestrator with -nebula-worker-exe.")]
        public string WorkerExecutable = "";
        [Tooltip("Where workers run: 'process' (child processes of the orchestrator) or 'hetzner' (one cloud VM per worker). -nebula-host overrides.")]
        public string WorkerHost = "process";
        [Tooltip("Directory the orchestrator serves at /build/ (the Linux server tarball worker VMs download). Empty = next to the executable.")]
        public string BuildArtifactDir = "";
        public bool OrchestratorSpawnsGateway = true;
        public float WorkerHeartbeatSeconds = 1f;
        public float WorkerTimeoutSeconds = 5f;
        [Tooltip("Seconds to wait after a worker is declared dead before a replacement is launched.")]
        public float DeadWorkerReplaceDelaySeconds = 8f;
        [Tooltip("Seconds a retiring worker gets to hand its entities over before it is killed regardless.")]
        public float WorkerDrainTimeoutSeconds = 10f;
        [Tooltip("Port of the Nebula Dashboard the orchestrator serves (http://localhost:<port>/). 0 disables it.")]
        public ushort DashboardPort = 7080;

        [Header("Gateway extension")]
        [Tooltip("Your own server code inside the standalone gateway: a class library that references the gateway's Nebula.Services.dll and implements Nebula.IGatewayExtension. This is where a game installs an interest policy, tags clients with their team and feeds fog-of-war changes. Give the assembly's file name (it sits next to the gateway executable, so it ships with the build and the deploy tarball) or an absolute path. Empty = no extension. -nebula-gateway-extension overrides.")]
        public string GatewayExtension = "";
        [Tooltip("Which class in that assembly to load, when it holds more than one IGatewayExtension. Empty = the only one. -nebula-gateway-extension-type overrides.")]
        public string GatewayExtensionType = "";
        [Tooltip("Settings passed to the extension, as key=value;key=value. It reads them with context.Option(key); -nebula-ext-<key> on the gateway's command line overrides one.")]
        public string GatewayExtensionOptions = "";

        [Header("Meshing")]
        [Tooltip("Entities within this many metres of a neighbouring container are ghosted to its worker ahead of time.")]
        public float GhostBandMargin = 4f;
        [Tooltip("An entity must be this far inside a new container before authority transfers. This prevents repeated transfers near a boundary.")]
        public float HandoverHysteresis = 0.35f;
        [Tooltip("Ghosts stay resident this long after leaving the band.")]
        public float GhostLingerSeconds = 2f;
        [Tooltip("Ticks of pose and [SyncHistory] state every worker keeps per entity, for lag compensation and time-sensitive validation (NetworkIdentity.StateAt). About half a second at 60 Hz by default. 0 turns recording off. -nebula-state-history overrides.")]
        public int StateHistoryTicks = StateHistory.DefaultWindowTicks;
        [Tooltip("How many times an AuthorityRpc may be forwarded after the target entity changes worker before it is rejected with RejectedHopLimit. Also the number of authority changes a call's epoch may lag behind the entity before it is rejected as stale. -nebula-authority-call-hops overrides.")]
        public int AuthorityCallMaxHops = 3;

        [Header("Persistence")]
        [Tooltip("Where entities carrying a PersistentEntity are stored: 'auto' (the orchestrator uses its database, a worker asks the orchestrator, a single-process run uses a local file), 'database', 'remote', 'local' (a file next to the process), 'memory' or 'off'. -nebula-persistence-mode overrides.")]
        public string PersistenceMode = "auto";
        [Tooltip("File the local store writes to. Empty = <persistentDataPath>/nebula-persistence.bin. -nebula-persistence-file overrides.")]
        public string PersistenceLocalFile = "";
        [Tooltip("Seconds between checkpoints of an authoritative persistent entity. A change or a move saves sooner; PersistentEntity.CheckpointSeconds overrides it per prefab.")]
        public float PersistenceCheckpointSeconds = 5f;
        [Tooltip("After gaining a container lease, how long a worker waits before restoring that container's persisted entities. Gives the previous owner's handover time to arrive so nothing comes back twice.")]
        public float PersistenceRestoreGraceSeconds = 3f;

        [Header("Scene entities")]
        [Tooltip("After a worker gains a container lease, how long it waits before spawning the unspawned scene entities standing in it. Gives the previous owner's handover time to arrive so an entity is not spawned twice.")]
        public float SceneEntityGraceSeconds = 2f;

        [Header("Interest management")]
        [Tooltip("How far a client hears about an entity, in metres. A prefab's RelevanceRadius overrides it per entity. This bounds what a client is sent, not what the mesh simulates. -nebula-interest-radius overrides.")]
        public float InterestRadius = 120f;
        [Tooltip("Extra metres an entity must travel past InterestRadius before it leaves a client's set. Stops an entity on the boundary from spawning and despawning repeatedly.")]
        public float InterestExitMargin = 16f;
        [Tooltip("Seconds an entity must stay past the exit radius before the client is told to despawn it.")]
        public float InterestLingerSeconds = 1f;
        [Tooltip("Edge of one interest region, in metres. With a world definition this is snapped to an integer division of the cell size so region edges fall on cell edges. -nebula-interest-cell overrides.")]
        public float InterestCellSize = 64f;
        [Tooltip("Regions are infinite columns: height does not take part in the region key. Right for a surface world; turn it off for a space or volume game.")]
        public bool InterestPlanar = true;
        [Tooltip("How often each client's interest set is re-evaluated, in times per second. Evaluations are staggered across ticks, and a client is also evaluated at once when its focus crosses a region edge.")]
        public float InterestEvalHz = 4f;
        [Tooltip("Extra metres of regions a gateway subscribes around a focus, so entities are already arriving before the client can see them. Must cover the distance a focus travels between two evaluations.")]
        public float InterestSubscribeMargin = 32f;
        [Tooltip("Seconds a region stays subscribed after the last client needed it, so a player pacing along a region edge does not make a worker start and stop sending.")]
        public float InterestRegionLingerSeconds = 3f;
        [Tooltip("Seconds a gateway keeps a worker connection after the last reason to hold it has gone.")]
        public float InterestLinkLingerSeconds = 10f;
        [Tooltip("Seconds between the audit snapshots a gateway sends of its whole subscription, whatever the deltas said.")]
        public float InterestResyncSeconds = 30f;
        [Tooltip("Ceiling on a prefab's RelevanceRadius, in metres. Also how far a wide entity may reach, which bounds the work of matching it against every client. It is never uncapped: a value of 0 or less is reported by Validate and replaced with InterestRadius.")]
        public float InterestMaxRadius = 1024f;
        [Tooltip("Most places one client may be interested in at once (an RTS camera plus owned units, a spectator's target). Foci beyond this are dropped.")]
        public int InterestMaxFoci = 8;
        [Tooltip("How far a client's focus hint may sit from its pawn, in metres. A further hint is clamped to this distance; a hint is an input, never authority.")]
        public float InterestHintMaxDistance = 60f;
        [Tooltip("Most focus hints accepted from one client per second. The rest are dropped.")]
        public float InterestHintMaxHz = 5f;
        [Tooltip("Most entities one client's policy may subscribe by id (party members, quest targets).")]
        public int InterestMaxExplicitPerClient = 16;
        [Tooltip("The fastest a player is expected to travel, in metres per second. Only used to check that InterestSubscribeMargin covers one evaluation of travel.")]
        public float InterestMaxFocusSpeed = 12f;
        [Tooltip("A worker warns when one container holds more than this many entities: the world wants partitioning, because one container is one worker's simulation budget. 0 turns the warning off.")]
        public int PartitionWarnEntities = 2000;
        [Tooltip("A worker warns when filtering its entities for the gateways takes longer than this many milliseconds per tick. 0 turns the warning off.")]
        public float PartitionWarnFilterMs = 2f;

        [Header("Chunked world")]
        [Tooltip("Turnkey unbounded chunked world: with a RuntimeWorld assigned, Nebula builds the chunk grid from its cell size, leases chunks around every pawn on workers, keeps the floating origin near the pawn on clients, and raises NebulaChunks.Loaded/Unloading on every role. The game only supplies chunk content.")]
        public bool ChunkedWorld = false;
        [Tooltip("Chunks are columns: the grid has one layer at y = 0 and RuntimeWorld's CellSize.y is the column height. Right for a surface world; turn it off for a volumetric one.")]
        public bool ChunkPlanar = true;
        [Tooltip("Seconds a chunk nobody needs stays leased before the worker retires it (its persistent contents are checkpointed and come back).")]
        public float ChunkRetireSeconds = 30f;

        [Header("Rate tiers inside the interest set")]
        [Tooltip("Entities within this many metres of a client's pawn get every tick of the world-state stream.")]
        public float InterestNearRadius = 30f;
        [Tooltip("Entities between the near and far radius get every InterestMidDivisor-th tick.")]
        public float InterestFarRadius = 80f;
        [Tooltip("Send every Nth tick for entities between the near and far radius (1 = every tick).")]
        public int InterestMidDivisor = 4;
        [Tooltip("Send every Nth tick for entities beyond the far radius (1 = every tick). Clients without a pawn yet get this rate for everything.")]
        public int InterestFarDivisor = 12;

        [Header("Client")]
        [Tooltip("Render delay for remote entities, in ticks.")]
        public int InterpolationDelayTicks = 3;
        [Tooltip("Extra ticks of input lead on top of half the measured RTT, before the adaptive adjustment.")]
        public int InputLeadMarginTicks = 2;
        [Tooltip("Lead (in ticks) the client tries to keep its inputs arriving at the worker with. Below this the client sends further ahead at once; well above it, it slowly relaxes.")]
        public int InputLeadTargetTicks = 3;
        [Tooltip("Upper bound on the adaptive input lead adjustment, in ticks.")]
        public int InputLeadMaxAdjustTicks = 30;

        [Header("Prefabs")]
        [Tooltip("Every prefab that can be spawned over the network. The index is the prefab id on the wire.")]
        public List<GameObject> NetworkPrefabs = new List<GameObject>();

        /// <summary>
        /// Report the settings that do not work together as soon as they are typed, rather than at the first
        /// missing entity in a running mesh. The same check runs in the setup window, in <c>nebula doctor</c> and
        /// in each role's start-up log (<see cref="Validate(List{ConfigIssue})"/>).
        /// </summary>
        private void OnValidate()
        {
            // Editor-only and rare, so a list per edit is cheaper than static state a play session would have to reset.
            var issues = new List<ConfigIssue>();
            Validate(issues);
            foreach (var issue in issues)
            {
                if (issue.Severity == ConfigSeverity.Error) NebulaLog.Error($"NebulaConfig.{issue.Field}: {issue.Message}");
                else if (issue.Severity == ConfigSeverity.Warning) NebulaLog.Warn($"NebulaConfig.{issue.Field}: {issue.Message}");
            }
        }

        /// <summary>The world definition's cell size, from a baked manifest or a runtime world; 0 when the game has neither.</summary>
        private float WorldCellSize()
        {
            var world = WorldManifest != null && WorldManifest.World != null ? WorldManifest.World : RuntimeWorld;
            if (world == null) return 0f;
            var size = world.CellSize;
            return Mathf.Max(0f, Mathf.Max(size.x, size.z));
        }

        /// <summary>Whether the world's cells are centred on <c>coord × CellSize</c> (baked cells) rather than starting there (a runtime grid).</summary>
        private bool WorldCellsCentred() => WorldManifest != null && WorldManifest.World != null;

        public static NebulaConfig Load()
        {
            var cfg = Resources.Load<NebulaConfig>("NebulaConfig");
            if (cfg == null)
            {
                NebulaLog.Warn("Resources/NebulaConfig not found; using defaults");
                cfg = CreateInstance<NebulaConfig>();
            }
            return cfg;
        }
    }
}
