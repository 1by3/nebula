// Scalar settings consumed by the shared service loops. Unity exports these fields by name.
namespace Nebula
{
    public sealed class NebulaConfig
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
        public int WorkerCount = 4;
        public int MaxWorkers = 32;
        public string AssignmentPolicy = "auto";
        public float CostRebalanceThreshold = 0.3f;
        public CostWeights CostWeights = CostWeights.Default;
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
        public float GhostBandMargin = 4f;
        public float HandoverHysteresis = 0.35f;
        public float GhostLingerSeconds = 2f;
        public string PersistenceMode = "auto";
        public string PersistenceLocalFile = "";
        public float PersistenceCheckpointSeconds = 5f;
        public float PersistenceRestoreGraceSeconds = 3f;
        public float SceneEntityGraceSeconds = 2f;
        public float InterestNearRadius = 30f;
        public float InterestFarRadius = 80f;
        public int InterestMidDivisor = 4;
        public int InterestFarDivisor = 12;
        public int InterpolationDelayTicks = 3;
        public int InputLeadMarginTicks = 2;
        public int InputLeadTargetTicks = 3;
        public int InputLeadMaxAdjustTicks = 30;
    }
}
