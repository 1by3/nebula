// Scalar settings consumed by the shared service loops. Unity exports these fields by name.
namespace Nebula
{
    public sealed partial class NebulaConfig
    {
        public string GameScene = "Arena";
        public int ClientLoadRadiusCells = 1;
        public int WorkerLoadRingCells = 1;
        public int OriginShiftThresholdCells = 4;
        public string GatewayAddress = "127.0.0.1";
        public ushort GatewayPort = 7000;
        public ushort WorkerBasePort = 7100;
        public string WorkerAdvertiseAddress = "127.0.0.1";
        public bool WebClients = true;
        public ushort WebPort = 0;
        public ushort WebRtcPort = 0;
        public string ControlPlaneUrl = "http://127.0.0.1:7080/";
        public string MeshToken = "";
        public bool UseLocalControlPlane = false;
        public string DatabaseUrl = "";
        public string AuthIssuers = "";
        public string AuthAudience = "";
        public bool AuthAnonymous = true;
        public string AuthSigningKey = "";
        public float SessionReclaimSeconds = 30f;
        public bool SingleSessionPerPlayer = true;
        public float GatewayDrainReconnectSeconds = 10f;
        public int WorkerCount = 4;
        public int MaxWorkers = 32;
        public string AssignmentPolicy = "auto";
        public float CostRebalanceThreshold = 0.3f;
        public CostWeights CostWeights = CostWeights.Default;

        /// <summary>The default of <see cref="CostLinkBudgetMbps"/> in bytes per second: 100 Mbit/s.</summary>
        public const double DefaultCostLinkBytesPerSec = 100.0 * 1000.0 * 1000.0 / 8.0;

        /// <summary><see cref="CostLinkBudgetMbps"/> in bytes per second; the default when it is 0 or less.</summary>
        public double CostLinkBytesPerSec => CostLinkBudgetMbps > 0f ? CostLinkBudgetMbps * 1000.0 * 1000.0 / 8.0 : DefaultCostLinkBytesPerSec;
        public float CostLinkBudgetMbps = 100f;
        public bool AutoScale = true;
        public int MinWorkers = 1;
        public float ScaleOutUtilization = 0.7f;
        public float ScaleInUtilization = 0.3f;
        public float ScaleHoldSeconds = 30f;
        public float ScaleWindowSeconds = 20f;
        public float ScaleMinGain = 0.1f;
        public float SeamGraceMeters = 10f;
        public float IdlePoolSeconds = 0f;
        public string WorkerExecutable = "";
        public string WorkerHost = "process";
        public string BuildArtifactDir = "";
        public bool OrchestratorSpawnsGateway = true;
        public float WorkerHeartbeatSeconds = 1f;
        public float WorkerTimeoutSeconds = 5f;
        public float DeadWorkerReplaceDelaySeconds = 8f;
        public float WorkerDrainTimeoutSeconds = 10f;
        public ushort DashboardPort = 7080;

        // The game's own code inside the standalone gateway (IGatewayExtension). It travels in the exported
        // manifest, so every gateway of a fleet loads the same extension from the same place inside its install.
        public string GatewayExtension = "";
        public string GatewayExtensionType = "";
        public string GatewayExtensionOptions = "";

        public float GhostBandMargin = 4f;
        public float HandoverHysteresis = 0.35f;
        public float GhostLingerSeconds = 2f;
        public int StateHistoryTicks = 32;
        public int AuthorityCallMaxHops = 3;
        public string PersistenceMode = "auto";
        public string PersistenceLocalFile = "";
        public float PersistenceCheckpointSeconds = 5f;
        public float PersistenceRestoreGraceSeconds = 3f;
        public float ScopeIdleRetireSeconds = 300f;
        public float SceneEntityGraceSeconds = 2f;
        public float InterestRadius = 120f;
        public float InterestExitMargin = 16f;
        public float InterestLingerSeconds = 1f;
        public float InterestCellSize = 64f;
        public bool InterestPlanar = true;
        public float InterestEvalHz = 4f;
        public float InterestSubscribeMargin = 32f;
        public float InterestRegionLingerSeconds = 3f;
        public float InterestLinkLingerSeconds = 10f;
        public float InterestResyncSeconds = 30f;
        public float InterestMaxRadius = 1024f;
        public int InterestMaxFoci = 8;
        public float InterestHintMaxDistance = 60f;
        public float InterestHintMaxHz = 5f;
        public int InterestMaxExplicitPerClient = 16;
        public float InterestMaxFocusSpeed = 12f;
        public int PartitionWarnEntities = 2000;
        public float PartitionWarnFilterMs = 2f;
        public bool ChunkedWorld = false;
        public bool ChunkPlanar = true;
        public float ChunkRetireSeconds = 30f;
        public float InterestNearRadius = 30f;
        public float InterestFarRadius = 80f;
        public int InterestMidDivisor = 4;
        public int InterestFarDivisor = 12;
        public int InterpolationDelayTicks = 3;
        public int InputLeadMarginTicks = 2;
        public int InputLeadTargetTicks = 3;
        public int InputLeadMaxAdjustTicks = 30;

        /// <summary>
        /// The services have no Unity asset references, so a world definition reaches them through the exported
        /// manifest instead: <c>ServiceManifest</c> sets this from <c>World.CellSize</c> when the game has one.
        /// It is what the interest grid is snapped to (design §3).
        /// </summary>
        public float WorldCellSizeMeters;
        /// <summary>Whether the exported world's cells are centred on their coordinate (a baked manifest) rather than starting there.</summary>
        public bool WorldCellsAreCentred;

        private float WorldCellSize() => WorldCellSizeMeters > 0 ? WorldCellSizeMeters : 0f;
        private bool WorldCellsCentred() => WorldCellsAreCentred;
    }
}
