using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Project-wide Nebula settings. Lives at Resources/NebulaConfig so every role (worker, gateway, orchestrator,
    /// client) loads the same asset. Command-line switches override individual fields at runtime.
    /// </summary>
    [CreateAssetMenu(menuName = "Nebula/Config", fileName = "NebulaConfig")]
    public sealed class NebulaConfig : ScriptableObject
    {
        [Header("Scene")]
        [Tooltip("Scene containing the Container volumes and gameplay. Loaded by every role.")]
        public string GameScene = "Arena";

        [Header("World partition (optional)")]
        [Tooltip("Baked container manifest of a partitioned world (Nebula > World). Unset = the game scene is the whole world.")]
        public WorldContainerManifest WorldManifest;
        [Tooltip("Clients keep this many cells around the local pawn loaded (1 = the 3x3x3 block).")]
        public int ClientLoadRadiusCells = 1;
        [Tooltip("Workers keep this many rings of cells around every cell they lease loaded (seam physics and ghosts need the neighbours' geometry).")]
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

        [Header("Control plane (SpacetimeDB)")]
        public string SpacetimeUri = "http://127.0.0.1:3000";
        public string SpacetimeDatabase = "nebula";
        [Tooltip("Use an in-process control plane instead of SpacetimeDB. Only meaningful when orchestrator, gateway and worker share a process.")]
        public bool UseLocalControlPlane = false;

        [Header("Orchestrator")]
        [Tooltip("How many worker processes the orchestrator keeps running.")]
        public int WorkerCount = 4;
        [Tooltip("Executable used to spawn workers/gateway. Empty = this process's own executable.")]
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

        [Header("Meshing")]
        [Tooltip("Entities within this many metres of a neighbouring container are ghosted to its worker ahead of time.")]
        public float GhostBandMargin = 4f;
        [Tooltip("An entity must be this far inside a new container before authority flips (anti-thrash).")]
        public float HandoverHysteresis = 0.35f;
        [Tooltip("Ghosts stay resident this long after leaving the band.")]
        public float GhostLingerSeconds = 2f;

        [Header("Scene entities")]
        [Tooltip("After a worker gains a container lease, how long it waits before spawning the unspawned scene entities standing in it. Gives the previous owner's handover time to arrive so an entity is not spawned twice.")]
        public float SceneEntityGraceSeconds = 2f;

        [Header("Gateway interest management")]
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
