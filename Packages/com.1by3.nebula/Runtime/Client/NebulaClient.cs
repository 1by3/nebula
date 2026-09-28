using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The game client. Talks only to the gateway, sends inputs, receives snapshots tagged (tick, epoch, owner) and
    /// predicts its own player. It is never a party to the handover protocol: which worker produced a snapshot is
    /// metadata used for epoch filtering and the debug overlay, nothing else.
    /// </summary>
    public sealed class NebulaClient : MonoBehaviour, IRpcSink
    {
        public enum State { Disconnected, Connecting, Connected, InGame }

        public NebulaConfig Config { get; private set; }
        public State ConnectionState { get; private set; }
        public ulong ClientId { get; private set; }
        public string PlayerName { get; private set; } = "";
        /// <summary>
        /// The local player's identity across sessions (<see cref="PlayerIdentity"/>), from the gateway's Welcome:
        /// derived from the ID token in <see cref="AuthToken"/>, or from the anonymous token the gateway issued
        /// (kept in PlayerPrefs and presented again next time). Empty until welcomed.
        /// </summary>
        public string Identity { get; private set; } = "";
        /// <summary>
        /// An OpenID Connect ID token to present at the next connection: what your sign-in flow got from a provider
        /// the mesh trusts (<see cref="NebulaConfig.AuthIssuers"/>). Set it before <see cref="ConnectTo"/>. Empty
        /// (the default) presents the saved anonymous token, or asks the gateway for a new anonymous identity.
        /// <c>-nebula-auth-token</c> (or <c>?nebula-auth-token=</c> in a web build) seeds it.
        /// </summary>
        public string AuthToken { get; set; } = "";
        /// <summary>
        /// The session token from the last Welcome (<see cref="WelcomeMsg.SessionToken"/>), presented on every
        /// reconnection so the gateway that answers, whichever one of the mesh it is, gives this client its session
        /// and pawn back. Kept for the life of the process; <see cref="Disconnect"/> keeps it too, so a deliberate
        /// reconnect continues the session. Clear it to start a new session on the next connection.
        /// </summary>
        public string SessionToken { get; set; } = "";
        /// <summary>
        /// The simulation scope this client asks to be placed in, as the opaque key the game chose
        /// (<see cref="EntityLocation.ScopeKey"/>). Empty, the default, is the public world. Set it before
        /// connecting — a travel menu or a matchmaking reply hands the key over — and the gateway spawns the player
        /// into that scope's containers instead of the public ones. The scope must already have been activated
        /// (<see cref="IControlPlane.ActivateScope"/>); the gateway holds the join until it is ready. Seeded from
        /// <c>-nebula-scope</c>. Changing it takes effect on the next connection.
        /// </summary>
        public string ScopeKey { get; set; } = CommandLine.Get("nebula-scope", "");
        /// <summary>True when the last Welcome reclaimed the session (the pawn is the one from before the reconnection).</summary>
        public bool SessionReclaimed { get; private set; }
        public NetworkIdentity LocalPlayer { get; private set; }
        /// <summary>
        /// How far the join has got, as the gateway sees it. A mesh with <see cref="NebulaConfig.MinWorkers"/> at 0
        /// has nowhere to spawn the first player after an idle period, so the gateway holds the join in
        /// <see cref="JoinState.Starting"/> while a worker boots and completes it with no reconnect; show a
        /// "world starting" screen while this is <see cref="JoinState.Starting"/>.
        /// </summary>
        public JoinState Join { get; private set; }
        /// <summary>Roughly how many seconds the gateway expects the <see cref="JoinState.Starting"/> hold to last; 0 when unknown.</summary>
        public int JoinEstimatedSeconds { get; private set; }
        /// <summary>
        /// Why the gateway is holding the join, while <see cref="Join"/> is <see cref="JoinState.Starting"/>: the
        /// world is booting, or the scope this client named is not ready, is restoring or is retiring. Read it in a
        /// <see cref="JoinStateChanged"/> handler to say something more useful than "please wait".
        /// </summary>
        public JoinHoldReason JoinHoldReason { get; private set; }
        /// <summary>
        /// Why the last join was refused, in typed form (<see cref="JoinRejectReason"/>). The reason string is for
        /// the player; this is what the game's code branches on - a full station wants a queue or another instance,
        /// a bad token wants a sign-in screen. See <c>docs/capacity-admission.md</c>.
        /// </summary>
        public JoinRejectReason JoinRejectReason { get; private set; }
        /// <summary>
        /// How full the target was when it refused the join, for <see cref="Nebula.JoinRejectReason.AtCapacity"/>:
        /// 1 = the whole of the dominant cost component's budget. 0 when the gateway did not say.
        /// </summary>
        public float JoinRejectSaturation { get; private set; }
        /// <summary>
        /// The inclusive protocol range reported by the gateway on the last refused join.
        /// Use it with <see cref="Nebula.JoinRejectReason.ProtocolUnsupported"/> to identify a version mismatch.
        /// A (0, 0) value means no range has been reported.
        /// </summary>
        public (ushort Min, ushort Max) ServerProtocolWindow { get; private set; }
        /// <summary>
        /// The gateway's game content version from the last refused join.
        /// Zero means content version checks are disabled or no version has been reported.
        /// </summary>
        public uint ServerContentVersion { get; private set; }
        /// <summary>
        /// The protocol version accepted in the last welcome, or <see cref="HelloMsg.ProtocolVersion"/>
        /// when the welcome omits it. Zero before the first welcome.
        /// </summary>
        public ushort NegotiatedProtocolVersion { get; private set; }
        public int RttMs { get; private set; } = -1;
        public double EstimatedServerTick => _serverTickEstimate;
        public uint PredictedTick => _predictTick;
        public int InputLeadTicks { get; private set; }
        /// <summary>
        /// Adaptive part of <see cref="InputLeadTicks"/>. Half the RTT plus a fixed margin is a guess that ignores the
        /// gateway's and worker's frame loops; the worker reports how early inputs really land and this closes the gap.
        /// </summary>
        public int InputLeadAdjustTicks { get; private set; }
        /// <summary>Worst input lead the worker reported most recently (ticks). Diagnostics.</summary>
        public int LastReportedInputLead { get; private set; }
        public int InputLeadIncreases { get; private set; }
        public int EntityCount => _entities.Count;
        /// <summary>Replicas this client holds: with interest management this follows what is near it, not the world.</summary>
        public int ReplicaCount => _entities.Count;
        /// <summary>Spawns and despawns received since the last telemetry report; also in the stats line.</summary>
        public int SpawnsReceived { get; private set; }
        public int DespawnsReceived { get; private set; }

        /// <summary>
        /// Where the player is looking, in this client's own Unity world space, when that is not simply where the
        /// pawn is: a free camera, an RTS view, a spectator. Set it and the client sends the gateway a
        /// <see cref="ClientFocusHintMsg"/> at up to <see cref="NebulaConfig.InterestHintMaxHz"/>, converting the
        /// point to absolute world coordinates on the way out (<see cref="AbsoluteFocusHint"/>), so a
        /// floating-origin shift between two hints does not move the place the gateway thinks you are watching.
        /// <para>
        /// The gateway treats it as an input and never as authority: it is refused unless the server allowed
        /// this client a focus (<c>NebulaGateway.SetClientFocusMode</c>), and by default it is clamped to
        /// <see cref="NebulaConfig.InterestHintMaxDistance"/> of the pawn. A game cannot widen its own interest
        /// by lying about where it is looking.
        /// </para>
        /// </summary>
        public Vector3? FocusHint
        {
            get => _hasFocusHint ? _focusHint : (Vector3?)null;
            set
            {
                if (value == null) { ClearFocusHint(); return; }
                _focusHint = value.Value;
                _hasFocusHint = true;
            }
        }

        /// <summary>
        /// Withdraw the focus hint: interest returns to the pawn. Going quiet is not enough — the gateway keeps
        /// the last hint it accepted — so the clear is said out loud, reliably, under a new generation that
        /// makes any hint still in flight stale.
        /// </summary>
        public void ClearFocusHint()
        {
            if (!_hasFocusHint) return;
            _hasFocusHint = false;
            _focusHintGeneration++;
            _focusClearPending = true;
        }

        /// <summary>
        /// <see cref="FocusHint"/> in absolute world coordinates: the frame position with the floating origin of
        /// the local pawn's scope added back, in double, exactly as a worker resolves its own entities
        /// (<c>WorkerInterest.ToAbsolute</c>). A scoped chunk grid moves its own origin, not the public one, so the
        /// hint is converted through the scope's frame (<c>docs/scope-frames.md</c> D4b, NEB-338). Without a world
        /// definition or a scope frame the origin never moves and the two spaces are the same. All zero when there
        /// is no hint.
        /// </summary>
        public void AbsoluteFocusHint(out double x, out double y, out double z)
        {
            if (!_hasFocusHint) { x = y = z = 0; return; }
            ToAbsolute(_focusHint, LocalPlayer != null ? LocalPlayer.InstanceId : 0UL, out x, out y, out z);
        }

        /// <summary>
        /// What this client's <b>content</b> follows: which cells or chunks are kept loaded, and where the
        /// floating origin sits. Null (the default) means the local pawn.
        /// <para>
        /// A strategy game does not want that. Its camera flies over a front the commander has no pawn near —
        /// or has no pawn at all — and content anchored to the pawn would leave the camera looking at unloaded
        /// terrain however much the gateway is willing to send. Handing the camera's transform to
        /// <see cref="SetContentAnchor"/> moves the window with the camera instead.
        /// </para>
        /// </summary>
        public Transform ContentAnchor { get; private set; }

        /// <summary>
        /// The transform content is actually anchored to right now: <see cref="ContentAnchor"/> when one is set
        /// and still alive, the local pawn otherwise, and null before either exists. Read by
        /// <c>NebulaWorldStreaming</c> (baked cell scenes) and <c>NebulaChunkedWorld</c> (runtime chunks), so
        /// one setting moves both.
        /// </summary>
        public Transform ActiveContentAnchor =>
            ContentAnchor != null ? ContentAnchor : LocalPlayer != null ? LocalPlayer.transform : null;

        /// <summary>
        /// Anchor content to this transform — the active strategy camera, a selected unit, a cinematic rig —
        /// instead of to the local pawn. Pass null to go back to the pawn. It is deliberately a transform and
        /// not the focus hint: the hint is an optional, rate-limited, server-validated <i>request</i> about what
        /// to be sent, while the anchor is a local decision about what to keep in memory, sampled every frame so
        /// load/unload hysteresis and origin shifts have something continuous to measure against.
        /// <para>
        /// Both are usually the same place, and a game that drives a free camera normally sets
        /// <see cref="FocusHint"/> to the camera's aim point and the anchor to the camera. Doing so is safe
        /// across an origin shift: the hint is converted to absolute coordinates as it is sent
        /// (<see cref="AbsoluteFocusHint"/>), so moving the origin under the camera does not move the point the
        /// gateway thinks anyone is watching. The pawn's prediction is unaffected too — it reconciles in the
        /// same frame everything else is in, and an origin shift moves that frame for all of them at once.
        /// </para>
        /// </summary>
        public void SetContentAnchor(Transform anchor) => ContentAnchor = anchor;

        /// <summary>
        /// A point in this client's frame of <paramref name="scope"/> as absolute world coordinates of that scope: the
        /// scope's own origin when it has a frame, the public floating origin otherwise.
        /// </summary>
        private static void ToAbsolute(Vector3 frame, ulong scope, out double x, out double y, out double z)
        {
            var a = ContainerRegistry.ToAbsolutePrecise(frame, scope);
            x = a.X; y = a.Y; z = a.Z;
        }

        private Vector3 _focusHint;
        private bool _hasFocusHint;
        private byte _focusHintGeneration;
        /// <summary>A clear that still has to reach the gateway (it may have been requested before the welcome).</summary>
        private bool _focusClearPending;
        private float _nextFocusHintAt;
        /// <summary>
        /// The view a replica was last spawned under. A despawn names the view it ends, so a despawn
        /// of an older view — one sent before the entity re-entered the set, still in flight — cannot kill the
        /// replica the newer spawn created.
        /// </summary>
        private readonly Dictionary<ulong, ushort> _viewSeq = new Dictionary<ulong, ushort>();
        public IEnumerable<NetworkIdentity> Entities => _entities.Values;
        public int AuthorityChangesSeen { get; private set; }

        public event Action<NetworkIdentity> EntitySpawned;
        public event Action<NetworkIdentity> EntityDespawned;
        public event Action<NetworkIdentity> LocalPlayerSpawned;
        public event Action<NetworkIdentity, ushort, ushort> EntityAuthorityChanged; // entity, oldWorker, newWorker
        public event Action ContainerOwnershipChanged;
        public event Action<State> ConnectionStateChanged;
        /// <summary>The gateway refused the join (the reason is fit to show the player); see <see cref="LastError"/>. The client stops reconnecting unless the refused token was a saved anonymous one, which it forgets and retries without.</summary>
        public event Action<string> JoinRejected;
        /// <summary>
        /// The same refusal, typed: the reason code and how saturated the target was
        /// (<see cref="JoinRejectedMsg"/>). Raised after <see cref="JoinRejected"/>, on the main thread. A game that
        /// wants to put the player in a docking queue when a station is full listens here.
        /// </summary>
        public event Action<JoinRejectedMsg> JoinRefused;
        /// <summary>The join's state changed: (state, estimated seconds). Raised on the main thread.</summary>
        public event Action<JoinState, int> JoinStateChanged;
        /// <summary>The gateway is draining and asked the client to reconnect (it does so by itself, with its session token); the argument is the seconds it was given. Raised after <see cref="Disconnected"/> and <see cref="Reconnecting"/>, with the client already <see cref="State.Disconnected"/>.</summary>
        public event Action<int> GatewayDraining;
        /// <summary>
        /// This player connected again somewhere else and that newer connection took the session and the pawn
        /// (<see cref="NebulaConfig.SingleSessionPerPlayer"/>). The client has left the world and does not reconnect
        /// by itself: show the reason (the argument, also in <see cref="LastError"/>) and let the player choose to
        /// take the session back with <see cref="Connect"/>. Raised on the main thread.
        /// </summary>
        public event Action<string> SessionReplaced;
        /// <summary>
        /// The connection to the gateway ended, or the first attempt to connect failed. The argument says why
        /// (<see cref="DisconnectInfo.Reason"/>), with a readable message, and whether the client will try again by
        /// itself (<see cref="DisconnectInfo.WillRetry"/>). Raised once per outage: attempts that fail again while the
        /// client retries update <see cref="LastDisconnect"/> without raising it. It is raised again, with
        /// <see cref="DisconnectInfo.WillRetry"/> false, when the client stops trying: a refusal, a replaced session,
        /// or <see cref="Disconnect"/>. Raised on the main thread, after <see cref="ConnectionStateChanged"/>.
        /// </summary>
        public event Action<DisconnectInfo> Disconnected;
        /// <summary>
        /// The client lost a connection it had been welcomed on, or its gateway is draining, and it has started
        /// reconnecting: <see cref="IsReconnecting"/> is now true. Raised once per outage, after
        /// <see cref="Disconnected"/>. Show a "reconnecting" message rather than the main menu.
        /// </summary>
        public event Action Reconnecting;
        /// <summary>
        /// A reconnection succeeded: the gateway welcomed the client again. The argument is
        /// <see cref="SessionReclaimed"/>: true when the same session and pawn came back, false when the worker
        /// had already let the pawn go and the player starts afresh. Raised on the main thread, after
        /// <see cref="ConnectionStateChanged"/>.
        /// </summary>
        public event Action<bool> Reconnected;
        /// <summary>
        /// The client stopped reconnecting after <see cref="NebulaConfig.ReconnectGiveUpSeconds"/>:
        /// <see cref="WantsConnection"/> is false, <see cref="ConnectionState"/> is <see cref="State.Disconnected"/>,
        /// and <see cref="Disconnected"/> has been raised with <see cref="DisconnectInfo.WillRetry"/> false and the
        /// reason of the last failure. Call <see cref="Connect"/> to try again.
        /// </summary>
        public event Action ReconnectGaveUp;
        /// <summary>
        /// The local player's pawn was despawned while the connection stayed up. The cause says whether the worker
        /// that simulated it failed (<see cref="LocalPlayerLossCause.WorkerLost"/>: the gateway is placing the player
        /// again and <see cref="LocalPlayerSpawned"/> follows) or the server removed it
        /// (<see cref="LocalPlayerLossCause.Despawned"/>). Raised after <see cref="EntityDespawned"/>, while the pawn's
        /// object still exists; <see cref="LocalPlayer"/> is already null. Not raised when the connection ends: the
        /// whole world is cleared then, and <see cref="Disconnected"/> says why.
        /// </summary>
        public event Action<NetworkIdentity, LocalPlayerLossCause> LocalPlayerLost;
        /// <summary>
        /// No state has arrived from the server for <see cref="NebulaConfig.ClientStallSeconds"/> while the client is
        /// in the world and its link is up: the worker simulating the player has probably stopped. Entities stand
        /// still until state arrives again (<see cref="ServerResumed"/>) or the mesh recovers the player. Show a
        /// "server not responding" notice rather than disconnecting. Raised on the main thread.
        /// </summary>
        public event Action ServerStalled;
        /// <summary>
        /// A stall reported by <see cref="ServerStalled"/> is over. Raised exactly once after each
        /// <see cref="ServerStalled"/>: when state arrives again, or when the client stops watching because the join
        /// went back to <see cref="JoinState.Starting"/> (a recovery) or the connection ended. Check
        /// <see cref="Join"/> and <see cref="ConnectionState"/> to tell which.
        /// </summary>
        public event Action ServerResumed;

        private ITransport _transport;
        private int _gatewayPeer = -1;
        private readonly Dictionary<ulong, NetworkIdentity> _entities = new Dictionary<ulong, NetworkIdentity>();
        /// <summary>
        /// Spawns of scene entities whose cell is not loaded here, by scene id (see <see cref="SceneEntities"/>). The
        /// gateway sends every entity to every client, so the record is kept current with the variable updates that
        /// keep arriving and is bound the moment the cell streams in. Indexed by net id too, for those updates.
        /// </summary>
        private readonly Dictionary<uint, EntitySpawnMsg> _pendingScene = new Dictionary<uint, EntitySpawnMsg>();
        private readonly Dictionary<ulong, uint> _pendingSceneByNetId = new Dictionary<ulong, uint>();
        /// <summary>
        /// Spawns of entities inside a dynamic container (a passenger on a ship) whose carrier has not spawned here
        /// yet, by the carrier's net id. The gateway replays its cache to a late joiner in no particular order, so
        /// the passenger can arrive before the ship; it binds the moment the ship's container registers.
        /// </summary>
        private readonly Dictionary<ContainerRef, List<EntitySpawnMsg>> _pendingByCarrier = new Dictionary<ContainerRef, List<EntitySpawnMsg>>();
        private readonly HashSet<ulong> _runtimeKeep = new HashSet<ulong>();
        private readonly List<ulong> _retiredRuntimeEntities = new List<ulong>();
        private readonly HashSet<string> _seenLeases = new HashSet<string>();
        private readonly NetworkWriter _writer = new NetworkWriter(2048);
        private readonly NetworkWriter _inputWriter = new NetworkWriter(256);
        private readonly NetworkReader _reader = new NetworkReader();
        private readonly NetworkReader _batchReader = new NetworkReader();
        private readonly List<ClientInputMsg.Frame> _recentInputs = new List<ClientInputMsg.Frame>();
        private ClientInputMsg _inputMsg = new ClientInputMsg { Frames = new List<ClientInputMsg.Frame>() };

        private double _serverTickEstimate;
        private double _serverTickAnchor;
        private double _serverTickAnchorTime;
        private uint _latestServerTick;
        private uint _predictTick;
        private float _nextPing;
        /// <summary>Round trip measured from ping/pong (smoothed), or -1; used when the transport has no measurement of its own.</summary>
        private double _pongRttMs = -1;
        private float _nextConnectAttempt;

        // Telemetry: one "[nebula] client ..." line every 5 s (packets and bytes in, frames, lead, RTT, corrections).
        private const float TelemetryIntervalSeconds = 5f;
        private float _nextTelemetry;
        private long _bytesIn;
        private int _packetsIn;
        private int _stateEntriesIn;
        private int _statePacketsIn;
        private int _rpcPacketsIn;
        private int _varsPacketsIn;
        private int _frames;
        private int _lastCorrections;
        private double _renderTick;
        private double _renderOffset;
        private bool _hasRenderOffset;
        /// <summary>How fast the render clock's offset closes on its target: the fraction of the remaining error removed per second (a 1/3 s time constant).</summary>
        private const double RenderOffsetRatePerSecond = 3.0;
        private float _maxFrameMs;
        private int _fixedThisFrame, _maxFixedPerFrame;
        /// <summary>Gaps in the local pawn's own state stream (which the gateway sends every tick) longer than this many ticks.</summary>
        private const uint StateGapThresholdTicks = 3;
        private uint _lastOwnStateTick;
        private int _stateGaps;
        private uint _worstStateGap;
        /// <summary>Age of world-state packets on arrival, ms, measured against the wall clock the workers derive their ticks from (exact on one machine, clock offset elsewhere: read the spread, not the absolute).</summary>
        private double _ageMin = double.MaxValue, _ageMax, _ageSum;
        private int _ageCount;
        /// <summary>How far behind the newest tick heard the world-state stream of the slowest worker currently runs (ticks), over the last two seconds.</summary>
        public int StreamLagTicks => Math.Max(_lagBucket, _lagPreviousBucket);
        /// <summary>The render delay in use: <see cref="NebulaConfig.InterpolationDelayTicks"/> plus the measured stream lag.</summary>
        public int RenderDelayTicks { get; private set; }
        private const int MaxStreamLagTicks = 6;
        private int _lagBucket, _lagPreviousBucket;
        private float _lagBucketEnds;
        // The carrier the local pawn rides (a ship): frame-to-frame jumps of its rendered hull beyond what its velocity explains.
        private NetworkIdentity _carrier;
        private Vector3 _carrierLastPos;
        private ContainerRef _carrierLastContainer;
        private uint _carrierLastEpoch;
        private int _carrierHitches, _carrierContainerChanges, _carrierEpochChanges;
        private float _carrierWorstJump;
        /// <summary>After raising the lead, reports for inputs sent with the old lead keep arriving for about an RTT; ignore them.</summary>
        private float _leadHoldUntil;
        private float _leadRelaxAt;

        /// <summary>Last connection failure or disconnect reason, for connection UI. Empty while healthy.</summary>
        public string LastError { get; private set; } = "";
        /// <summary>
        /// Seconds since state last arrived from the server: entity state, owner state, variables, maps, RPCs, spawns
        /// or despawns, anything a worker produced. 0 before the first state since the client joined, and while it
        /// is not in the world. A healthy server sends the local player's own pawn state every tick, so anything above
        /// a few tenths of a second is worth noticing; see <see cref="ServerStalled"/>.
        /// </summary>
        public float SecondsSinceServerState => ConnectionState == State.InGame && _hasServerState ? Math.Max(0f, Now - _lastServerStateAt) : 0f;
        /// <summary>True between <see cref="ServerStalled"/> and <see cref="ServerResumed"/>.</summary>
        public bool IsServerStalled { get; private set; }
        /// <summary>When state last arrived from the server; see <see cref="SecondsSinceServerState"/>.</summary>
        private float _lastServerStateAt;
        /// <summary>State has arrived since the client joined: the stall watch has something to measure from.</summary>
        private bool _hasServerState;
        /// <summary>
        /// How the last connection, or the last attempt to connect, ended: the same value <see cref="Disconnected"/>
        /// carried, updated by every failed retry too. <see cref="DisconnectReason.Unknown"/> with an empty message
        /// before the first disconnect.
        /// </summary>
        public DisconnectInfo LastDisconnect { get; private set; } = new DisconnectInfo(DisconnectReason.Unknown, "", false);
        /// <summary>
        /// True while the client is reconnecting after losing a connection it had been welcomed on, or after its
        /// gateway asked it to move: from the drop until the next welcome (<see cref="Reconnected"/>), until it gives
        /// up (<see cref="ReconnectGaveUp"/>), or until the connection ends for good (a refusal, a replaced session,
        /// <see cref="Disconnect"/>). <see cref="ConnectionState"/> keeps moving between Disconnected, Connecting and
        /// Connected meanwhile. False while a first connection is still being attempted.
        /// </summary>
        public bool IsReconnecting { get; private set; }
        /// <summary>How many reconnection attempts the client has started in the current outage (1 during the first). 0 when not reconnecting.</summary>
        public int ReconnectAttempt => IsReconnecting ? _retries : 0;
        /// <summary>Seconds since the connection dropped, while <see cref="IsReconnecting"/>; 0 otherwise.</summary>
        public float ReconnectElapsedSeconds => IsReconnecting ? Math.Max(0f, Now - _reconnectStartedAt) : 0f;
        /// <summary>The current outage has been reported through <see cref="Disconnected"/>; failed retries are not reported again.</summary>
        private bool _outageReported;
        /// <summary>Retries dialled since the connection dropped or the game asked to connect; what the backoff counts.</summary>
        private int _retries;
        /// <summary>The client was welcomed since the game last asked it to connect, so a drop is a reconnection, not a failed first connection.</summary>
        private bool _hadSession;
        private float _reconnectStartedAt;
        /// <summary>Replaces <c>Time.unscaledTime</c> for the reconnect schedule, so a test can move time by hand.</summary>
        internal Func<float> ClockForTests;
        private float Now => ClockForTests != null ? ClockForTests() : Time.unscaledTime;
        private ReconnectBackoff Backoff => Config == null ? ReconnectBackoff.Default
            : new ReconnectBackoff(Config.ReconnectFirstDelaySeconds, Config.ReconnectBackoffFactor, Config.ReconnectMaxDelaySeconds, Config.ReconnectGiveUpSeconds);
        /// <summary>
        /// Why the gateway said it is about to close the link (a refusal it asked the client to retry, say), so the
        /// transport's Disconnected that follows is reported with that reason instead of a bare "closed by server".
        /// </summary>
        private bool _hasAnnouncedEnd;
        private DisconnectReason _announcedReason;
        private string _announcedMessage = "";
        /// <summary>Whether the client is trying to be connected (set by <see cref="Connect"/>/<see cref="ConnectTo"/>, cleared by <see cref="Disconnect"/>).</summary>
        public bool WantsConnection { get; private set; }
        /// <summary>
        /// Whether the client connected on its own at startup because the command line supplied the connection
        /// (<c>-nebula-gateway</c>, <c>-nebula-bot</c>, or <c>-nebula-connect</c>). Connection UI such as
        /// <see cref="NebulaTitleScreen"/> stays hidden when this is true.
        /// </summary>
        public bool ConnectsAutomatically { get; private set; }

        /// <param name="autoConnect">Connect to <see cref="NebulaConfig.GatewayAddress"/> at once (bots, scripted clients);
        /// false leaves the client idle until game code, or <see cref="NebulaTitleScreen"/>, calls <see cref="ConnectTo"/>.</param>
        public void Initialize(NebulaConfig config, bool autoConnect = true) => Initialize(config, autoConnect, autoConnect);

        /// <param name="connectNow">False with <paramref name="autoConnect"/> true: the client connects on its own, but
        /// later (<see cref="Connect"/>), once the bootstrap knows where. Connection UI stays hidden meanwhile.</param>
        internal void Initialize(NebulaConfig config, bool autoConnect, bool connectNow)
        {
            Config = config;
            ConnectsAutomatically = autoConnect;
            PlayerName = CommandLine.Get("nebula-name", DefaultPlayerName());
            AuthToken = CommandLine.Get("nebula-auth-token", "");
            // Bots share one PlayerPrefs store per machine and must not all become the same player.
            _keepsIdentity = !CommandLine.Has("nebula-bot");
            _storedToken = _keepsIdentity ? PlayerPrefs.GetString(StoredTokenPref, "") : "";
            NebulaRuntime.RpcSink = this;
#if UNITY_WEBGL && !UNITY_EDITOR
            // A browser has no UDP sockets: a web build reaches the gateway over WebRTC data channels, which
            // DTLS already encrypts, so the encryption setting does not apply to this transport.
            _transport = new WebRtcClientTransport("client");
#else
            ITransport udp = new LiteNetTransport("client", Milliseconds(config.ClientDisconnectTimeoutSeconds, LiteNetTransport.DefaultDisconnectTimeoutMs),
                Milliseconds(config.ClientPingIntervalSeconds, LiteNetTransport.DefaultPingIntervalMs));
            if (CommandLine.GetBool("nebula-encrypt", config.ClientEncryption))
                udp = EncryptedTransport.ForClient(udp, new ClientEncryption { Fingerprint = CommandLine.Get("nebula-gateway-fingerprint", config.GatewayFingerprint) });
            _transport = udp;
#endif
            _transport.StartClient();
            SceneEntities.Registered += OnSceneEntityRegistered;
            SceneEntities.Unregistering += OnSceneEntityUnregistering;
            ContainerRegistry.DynamicRegistered += OnLateContainerRegistered;
            ContainerRegistry.RuntimeRegistered += OnLateContainerRegistered;
            if (connectNow) Connect();
        }

        private static int Milliseconds(float seconds, int fallback)
            => float.IsNaN(seconds) || seconds <= 0f ? fallback : (int)Math.Min(int.MaxValue / 2, Math.Round(seconds * 1000.0));

        private static string DefaultPlayerName()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "player"; // a browser does not say who is signed in
#else
            try { return Environment.UserName; }
            catch (Exception) { return "player"; }
#endif
        }

        /// <summary>
        /// Connect to a gateway chosen at runtime, for example from your own connection UI. Replaces the configured
        /// address for reconnects. A blank <paramref name="playerName"/> keeps the current <see cref="PlayerName"/>.
        /// </summary>
        public void ConnectTo(string address, ushort port, string playerName)
        {
            if (!string.IsNullOrWhiteSpace(playerName)) PlayerName = playerName.Trim();
            Config.GatewayAddress = address.Trim();
            Config.GatewayPort = port;
            if (ConnectionState != State.Disconnected) Disconnect();
            LastError = "";
            _outageReported = false; // a new address: its first failure is news even if the old one was failing
            _retries = 0;
            Connect();
        }

        public void Connect()
        {
            if (IsLeaving) FinishLeave(false);
            if (!WantsConnection)
            {
                // A fresh start by the game, not a retry.
                _outageReported = false;
                _retries = 0;
            }
            WantsConnection = true;
            if (ConnectionState != State.Disconnected) return;
            SetState(State.Connecting);
            _hasAnnouncedEnd = false;
            _gatewayPeer = _transport.Connect(Config.GatewayAddress, Config.GatewayPort);
            NebulaLog.Info($"connecting to gateway {Config.GatewayAddress}:{Config.GatewayPort} as '{PlayerName}'");
        }

        private const string StoredTokenPref = "nebula.identityToken";
        private bool _keepsIdentity = true;
        private string _storedToken = "";
        private bool _presentedStoredToken;

        private void RememberIssuedToken(string token)
        {
            _storedToken = token;
            if (!_keepsIdentity) return;
            try { PlayerPrefs.SetString(StoredTokenPref, token); PlayerPrefs.Save(); }
            catch (Exception e) { NebulaLog.Warn($"could not save the identity token: {e.Message}"); }
        }

        /// <summary>
        /// Drop the saved anonymous identity: the next connection without an <see cref="AuthToken"/> gets a new
        /// one, and the old player's entities and records stay behind. A "reset progress" or "play as guest" button.
        /// </summary>
        public void ForgetStoredToken()
        {
            _storedToken = "";
            _presentedStoredToken = false;
            if (!_keepsIdentity) return;
            try { PlayerPrefs.DeleteKey(StoredTokenPref); PlayerPrefs.Save(); } catch { }
        }

        /// <summary>
        /// Leave the gateway and stop reconnecting. The client stays idle until the next <see cref="Connect"/> or
        /// <see cref="ConnectTo"/>. To the server this looks like a lost link: the worker keeps the pawn for
        /// <see cref="NebulaConfig.SessionReclaimSeconds"/> so the player can come back to it. Use <see cref="Leave"/>
        /// when the player is leaving for good.
        /// </summary>
        public void Disconnect()
        {
            bool active = WantsConnection || ConnectionState != State.Disconnected;
            if (IsLeaving) FinishLeave(false);
            EndConnection();
            if (active) ReportDisconnect(DisconnectReason.ClientRequested, "the client disconnected", false);
        }

        /// <summary>How long <see cref="Leave"/> waits, by default, for the gateway to confirm the goodbye.</summary>
        public const float DefaultLeaveTimeoutSeconds = 0.5f;

        /// <summary>
        /// Leave the world for good: the player chose to quit, or to go back to the menu. Unlike
        /// <see cref="Disconnect"/>, the session ends at once instead of waiting out
        /// <see cref="NebulaConfig.SessionReclaimSeconds"/>: the client sends a goodbye, the worker calls
        /// <see cref="NebulaGameMode.OnPlayerDespawn"/> and removes the pawn (a persistent pawn keeps its record), and
        /// other players stop seeing it. The session token is cleared, so the next <see cref="Connect"/> starts a new
        /// session.
        /// <para>
        /// The client is <see cref="State.Disconnected"/> when this returns and raises <see cref="Disconnected"/> with
        /// <see cref="DisconnectReason.ClientRequested"/>, but it keeps the link open until the gateway closes it,
        /// which is how it knows the goodbye arrived, or until <paramref name="timeoutSeconds"/> has passed.
        /// <see cref="IsLeaving"/> is true until then, and <see cref="LeaveFinished"/> says which it was. Before
        /// quitting, wait for it: <c>yield return client.LeaveAndWait();</c> then <c>Application.Quit()</c>. A goodbye
        /// that does not arrive costs nothing but the grace: the server treats the player as disconnected.
        /// </para>
        /// <para>
        /// With no welcomed connection there is nobody to say goodbye to: it acts like <see cref="Disconnect"/> and
        /// raises <see cref="LeaveFinished"/> with false at once.
        /// </para>
        /// </summary>
        public void Leave(float timeoutSeconds = DefaultLeaveTimeoutSeconds)
        {
            if (IsLeaving) return;
            bool active = WantsConnection || ConnectionState != State.Disconnected;
            bool goodbye = _transport != null && _gatewayPeer >= 0 && ConnectionState == State.InGame && NegotiatedProtocolVersion >= GoodbyeProtocolVersion;
            if (goodbye)
            {
                _writer.Reset();
                new GoodbyeMsg().Write(_writer);
                _transport.Send(_gatewayPeer, Delivery.ReliableOrdered, _writer.ToSegment());
                _transport.Flush();
                // The link stays open until the gateway closes it: the close is the acknowledgement.
                _leavingPeer = _gatewayPeer;
                _gatewayPeer = -1;
                _leaveDeadline = Now + Math.Max(0f, float.IsNaN(timeoutSeconds) ? 0f : timeoutSeconds);
                NebulaLog.Info("leaving: goodbye sent");
            }
            // The session is over on the server; a token for it would only bring back an empty session.
            SessionToken = "";
            EndConnection();
            if (active) ReportDisconnect(DisconnectReason.ClientRequested, "the player left", false);
            if (!goodbye) RaiseLeaveFinished(false);
        }

        /// <summary>
        /// <see cref="Leave"/>, then wait until the gateway has confirmed the goodbye or
        /// <paramref name="timeoutSeconds"/> has passed. Run it as a coroutine before quitting:
        /// <c>yield return client.LeaveAndWait(); Application.Quit();</c>. The client keeps polling its transport in
        /// <c>Update</c> meanwhile, so the frame loop must keep running.
        /// </summary>
        public System.Collections.IEnumerator LeaveAndWait(float timeoutSeconds = DefaultLeaveTimeoutSeconds)
        {
            Leave(timeoutSeconds);
            while (IsLeaving) yield return null;
        }

        /// <summary>
        /// True from <see cref="Leave"/> until the gateway has confirmed the goodbye or the timeout has passed; see
        /// <see cref="LeaveFinished"/>.
        /// </summary>
        public bool IsLeaving => _leavingPeer >= 0;

        /// <summary>
        /// A <see cref="Leave"/> is over. True when the gateway confirmed the goodbye (it closed the link) and the
        /// session has ended; false when there was no connection to say goodbye on, or the gateway did not answer in
        /// time, in which case the server treats the player as disconnected and ends the session after the reclaim
        /// grace. Raised on the main thread.
        /// </summary>
        public event Action<bool> LeaveFinished;

        /// <summary>The first protocol with <see cref="MsgId.Goodbye"/> and <see cref="MsgId.Kicked"/>.</summary>
        private const ushort GoodbyeProtocolVersion = 22;
        /// <summary>The link a goodbye was sent on, kept open until the gateway closes it or the leave times out; -1 when not leaving.</summary>
        private int _leavingPeer = -1;
        private float _leaveDeadline;

        /// <summary>End a <see cref="Leave"/>: close the link if the gateway has not, and say how it went.</summary>
        private void FinishLeave(bool confirmed)
        {
            if (_leavingPeer < 0) return;
            if (!confirmed)
            {
                try { _transport?.Disconnect(_leavingPeer); } catch { }
                NebulaLog.Warn("leaving: the gateway did not confirm the goodbye in time; closing the link");
            }
            else NebulaLog.Info("leaving: the gateway ended the session");
            _leavingPeer = -1;
            RaiseLeaveFinished(confirmed);
        }

        private void RaiseLeaveFinished(bool confirmed)
        {
            try { LeaveFinished?.Invoke(confirmed); }
            catch (Exception e) { NebulaLog.Error($"LeaveFinished handler threw: {e}"); }
        }

        /// <summary>Stop wanting a connection, close the link, clear the world and move to <see cref="State.Disconnected"/>.</summary>
        private void EndConnection()
        {
            WantsConnection = false;
            IsReconnecting = false;
            _hadSession = false;
            DropGatewayLink();
            ClearWorld();
            SetState(State.Disconnected);
        }

        /// <summary>
        /// Record how the connection ended and raise <see cref="Disconnected"/>, unless this is a retry failing again
        /// in an outage already reported. A handler that throws is logged and does not break the client.
        /// </summary>
        private void ReportDisconnect(DisconnectReason reason, string message, bool willRetry, ushort code = 0)
        {
            var info = new DisconnectInfo(reason, message, willRetry, code);
            LastDisconnect = info;
            if (willRetry && _outageReported) return;
            _outageReported = willRetry;
            try { Disconnected?.Invoke(info); }
            catch (Exception e) { NebulaLog.Error($"Disconnected handler threw: {e}"); }
        }

        /// <summary>The gateway said why it is about to close the link; report that reason when the link closes.</summary>
        private void AnnounceEnd(DisconnectReason reason, string message)
        {
            _hasAnnouncedEnd = true;
            _announcedReason = reason;
            _announcedMessage = message ?? "";
        }

        /// <summary>Close the link to the gateway, if there is one, without touching the state or the world.</summary>
        private void DropGatewayLink()
        {
            if (_gatewayPeer < 0) return;
            try { _transport?.Disconnect(_gatewayPeer); } catch { }
            _gatewayPeer = -1;
        }

        private void OnDestroy()
        {
            SceneEntities.Registered -= OnSceneEntityRegistered;
            SceneEntities.Unregistering -= OnSceneEntityUnregistering;
            ContainerRegistry.DynamicRegistered -= OnLateContainerRegistered;
            ContainerRegistry.RuntimeRegistered -= OnLateContainerRegistered;
            _transport?.Dispose();
            _transport = null;
        }

        private void SetState(State s)
        {
            if (ConnectionState == s) return;
            ConnectionState = s;
            ConnectionStateChanged?.Invoke(s);
        }

        // ---------------------------------------------------------------------------------------- reconnection

        /// <summary>Give up when the give-up time has passed, and otherwise dial the next attempt when it is due.</summary>
        internal void TickConnection()
        {
            if (IsLeaving && Now >= _leaveDeadline) FinishLeave(false);
            if (IsReconnecting && Backoff.ShouldGiveUp(Now - _reconnectStartedAt))
            {
                GiveUpReconnecting();
                return;
            }
            if (ConnectionState != State.Disconnected || !WantsConnection || Now < _nextConnectAttempt) return;
            _retries++;
            // Only a fallback: the attempt's own failure schedules the next one.
            _nextConnectAttempt = Now + Backoff.DelayAfter(_retries);
            if (IsReconnecting) NebulaLog.Info($"reconnecting: attempt {_retries}, {ReconnectElapsedSeconds:0.#} s since the drop");
            Connect();
        }

        /// <summary>A drop the client will retry: schedule the attempt, and enter the reconnecting state if the client had a session.</summary>
        private void ScheduleRetry(float delaySeconds)
        {
            _nextConnectAttempt = Now + delaySeconds;
            if (IsReconnecting || !_hadSession) return;
            IsReconnecting = true;
            _reconnectStartedAt = Now;
            _retries = 0;
        }

        private void RaiseReconnectingIfNew(bool wasReconnecting)
        {
            if (wasReconnecting || !IsReconnecting) return;
            try { Reconnecting?.Invoke(); }
            catch (Exception e) { NebulaLog.Error($"Reconnecting handler threw: {e}"); }
        }

        // ---------------------------------------------------------------------------------------- stall watch

        /// <summary>
        /// Raise <see cref="ServerStalled"/> once state has been missing for <see cref="NebulaConfig.ClientStallSeconds"/>.
        /// The watch runs only while the client is in the world (<see cref="JoinState.Joined"/>) and has had state
        /// since it joined, so a join in progress, and a client in a scope that sends nothing, never look stalled.
        /// </summary>
        internal void TickStallWatch()
        {
            float limit = Config != null ? Config.ClientStallSeconds : 0f;
            bool watching = limit > 0f && ConnectionState == State.InGame && Join == JoinState.Joined && _hasServerState;
            if (!watching)
            {
                EndStall();
                return;
            }
            if (IsServerStalled || Now - _lastServerStateAt < limit) return;
            IsServerStalled = true;
            NebulaLog.Warn($"no state from the server for {Now - _lastServerStateAt:0.0} s; the server may have stalled");
            try { ServerStalled?.Invoke(); }
            catch (Exception e) { NebulaLog.Error($"ServerStalled handler threw: {e}"); }
        }

        /// <summary>State arrived from the server: restart the stall clock, and end a stall.</summary>
        private void NoteServerState()
        {
            _lastServerStateAt = Now;
            _hasServerState = true;
            EndStall();
        }

        private void EndStall()
        {
            if (!IsServerStalled) return;
            IsServerStalled = false;
            NebulaLog.Info("state from the server again");
            try { ServerResumed?.Invoke(); }
            catch (Exception e) { NebulaLog.Error($"ServerResumed handler threw: {e}"); }
        }

        /// <summary>Messages carrying something a worker produced: what the stall watch counts as server state.</summary>
        private static bool IsServerState(MsgId id)
        {
            switch (id)
            {
                case MsgId.EntitySpawn:
                case MsgId.EntityDespawn:
                case MsgId.EntityVars:
                case MsgId.EntityMaps:
                case MsgId.EntityRpc:
                case MsgId.WorldState:
                case MsgId.EntityState:
                case MsgId.OwnerState:
                    return true;
                default:
                    return false;
            }
        }

        private void GiveUpReconnecting()
        {
            float elapsed = ReconnectElapsedSeconds;
            var last = LastDisconnect;
            EndConnection();
            string cause = last.Message.EndsWith(" (retrying)") ? last.Message.Substring(0, last.Message.Length - " (retrying)".Length) : last.Message;
            LastError = $"gave up reconnecting after {elapsed:0} s" + (cause.Length > 0 ? $": {cause}" : "");
            NebulaLog.Warn(LastError);
            ReportDisconnect(last.Reason, LastError, false);
            try { ReconnectGaveUp?.Invoke(); }
            catch (Exception e) { NebulaLog.Error($"ReconnectGaveUp handler threw: {e}"); }
        }

        // ---------------------------------------------------------------------------------------- frame loop

        private void Update()
        {
            if (_transport == null) return; // not initialised (a stray component), or torn down
            _transport.Poll(HandleTransportEvent);

            TickConnection();
            TickStallWatch();
            if (ConnectionState == State.Disconnected || ConnectionState == State.Connecting) return;
            _frames++;
            float frameMs = Time.unscaledDeltaTime * 1000f;
            if (frameMs > _maxFrameMs) _maxFrameMs = frameMs;
            if (_fixedThisFrame > _maxFixedPerFrame) _maxFixedPerFrame = _fixedThisFrame;
            _fixedThisFrame = 0;
            if (Time.unscaledTime >= _nextTelemetry)
            {
                _nextTelemetry = Time.unscaledTime + TelemetryIntervalSeconds;
                ReportTelemetry();
            }

            // A focus hint only matters while the game is actually supplying one, and only at the rate the
            // gateway accepts; anything faster is dropped there, so sending it would be pure waste.
            if (_focusClearPending && ConnectionState == State.InGame)
            {
                // Reliable, and ahead of any new hint: the generation it carries is what makes older hints stale.
                _focusClearPending = false;
                _writer.Reset();
                new ClientFocusHintMsg { Generation = _focusHintGeneration, Clear = true }.Write(_writer);
                _transport.Send(_gatewayPeer, Delivery.ReliableOrdered, _writer.ToSegment());
            }
            if (_hasFocusHint && Time.unscaledTime >= _nextFocusHintAt)
            {
                float hz = Config != null && Config.InterestHintMaxHz > 0 ? Config.InterestHintMaxHz : 5f;
                _nextFocusHintAt = Time.unscaledTime + 1f / hz;
                _writer.Reset();
                // Absolute, not the frame position: the origin may shift between two hints, and a gateway that
                // read the frame position would think the camera jumped a cell every time it did.
                AbsoluteFocusHint(out double ax, out double ay, out double az);
                new ClientFocusHintMsg { X = ax, Y = ay, Z = az, Generation = _focusHintGeneration }.Write(_writer);
                _transport.Send(_gatewayPeer, Delivery.Sequenced, _writer.ToSegment());
            }

            if (Time.unscaledTime >= _nextPing)
            {
                _nextPing = Time.unscaledTime + 0.5f;
                _writer.Reset();
                new PingMsg { ClientTime = Time.unscaledTimeAsDouble }.Write(_writer);
                _transport.Send(_gatewayPeer, Delivery.Sequenced, _writer.ToSegment());
                int rtt = _transport.RoundTripMs(_gatewayPeer);
                // A transport that cannot measure (some browsers report no candidate-pair RTT) falls back on ping/pong.
                if (rtt < 0 && _pongRttMs >= 0) rtt = (int)Math.Round(_pongRttMs);
                if (rtt >= 0) RttMs = rtt;
            }

            // Server clock estimate: anchor on the newest tick heard, advance with real time.
            double elapsedTicks = (Time.unscaledTimeAsDouble - _serverTickAnchorTime) * NetworkTime.TickRate;
            _serverTickEstimate = _serverTickAnchor + elapsedTicks;
            NetworkTime.LatestServerTick = _latestServerTick;

            // Remote entities render a few ticks behind the newest snapshot; the local player is predicted. The
            // delay counts from the tick the newest snapshot carried, not from the estimated server clock (which
            // is half an RTT ahead of it): on a 40 ms link the old way left under two ticks of buffer, so every
            // bit of jitter tipped the interpolator into extrapolating and pawns skipped.
            double halfRttTicks = RttMs > 0 ? RttMs * 0.5 / 1000.0 * NetworkTime.TickRate : 0.5;
            // Streams from different workers arrive with different lateness (each worker publishes its tick at its own
            // point in its frame): the clock anchors on the newest tick heard, so the configured delay alone would leave
            // the slowest worker's entities with almost no buffer. Interpolate behind the slowest current stream.
            RenderDelayTicks = Config.InterpolationDelayTicks + Math.Min(StreamLagTicks, MaxStreamLagTicks);
            double targetRender = _serverTickEstimate - halfRttTicks - RenderDelayTicks;
            // The render clock advances with real time; only its offset from the target is smoothed, at a rate in
            // seconds. Smoothing the tick itself toward a target that moves every frame lags behind it by
            // (1 - k) / k frame-steps: at 60 fps with k = 0.1 that was nine ticks, a quarter second of extra
            // latency, and once the lag crossed the snap threshold every remote entity jumped a dozen ticks ahead
            // twice a second. Fast clients never saw it; a client rendering a busy scene saw nothing else.
            double now = Time.unscaledTimeAsDouble * NetworkTime.TickRate;
            double desiredOffset = targetRender - now;
            if (!_hasRenderOffset || Math.Abs(desiredOffset - _renderOffset) > 10) { _renderOffset = desiredOffset; _hasRenderOffset = true; }
            else _renderOffset += (desiredOffset - _renderOffset) * Math.Min(1.0, RenderOffsetRatePerSecond * Time.unscaledDeltaTime);
            _renderTick = now + _renderOffset;
            NetworkTime.RenderTick = _renderTick;
            foreach (var e in _entities.Values)
            {
                // A replica whose object is gone without a despawn (destroyed by game code, or with a parent) has
                // nothing left to present; it is dropped below instead of throwing here every frame.
                if (e == null) { _destroyedScratch.Add(e); continue; }
                // The local player too: its server-authoritative children (a NetworkTransform on a turret, say) interpolate.
                e.RemoteTick(_renderTick);
            }
            if (_destroyedScratch.Count > 0) DropDestroyed();
            TrackCarrier();
            FollowLocalScope();
            // Physics frames: sample their motion and put every frame root at its frame's world pose, now that the
            // carriers were interpolated, so everything that runs after this (cameras) sees one world (D11, D14).
            PhysicsFrames.UpdateStates(NetworkTime.Tick, Time.deltaTime);
            PhysicsFrames.SyncAllContent();
            PhysicsFrames.PoseForRender();
        }

        /// <summary>The scope whose instance content this client last showed.</summary>
        private ulong _viewScope;

        /// <summary>
        /// Show the instance content of the scope the local pawn is in. The pawn's own container change does this
        /// already; a pawn riding in a ship changes scope without changing container at all — the ship moved — so
        /// the scope is read through the carrier chain every frame and the view follows it
        /// (docs/scope-activation.md D18).
        /// </summary>
        private void FollowLocalScope()
        {
            ulong scope = LocalPlayer != null ? LocalPlayer.InstanceId : 0;
            if (LocalPlayer == null || scope == _viewScope) return;
            _viewScope = scope;
            InstanceScenes.SetView(scope);
        }

        /// <summary>Telemetry for the hull under the local pawn: a rendered jump the reported velocity does not explain is a hitch the pilot sees.</summary>
        private void TrackCarrier()
        {
            var carrier = LocalPlayer != null && LocalPlayer.Container != null ? LocalPlayer.Container.Carrier : null;
            if (carrier != _carrier)
            {
                _carrier = carrier;
                if (carrier == null) return;
                _carrierLastPos = carrier.transform.position;
                _carrierLastContainer = carrier.ContainerRef;
                _carrierLastEpoch = carrier.Epoch;
                return;
            }
            if (carrier == null) return;
            var pos = carrier.transform.position;
            float moved = (pos - _carrierLastPos).magnitude;
            float expected = carrier.Motion.Velocity.magnitude * Time.unscaledDeltaTime;
            if (moved > expected * 2f + 0.05f)
            {
                _carrierHitches++;
                if (moved - expected > _carrierWorstJump) _carrierWorstJump = moved - expected;
            }
            _carrierLastPos = pos;
            if (carrier.ContainerRef != _carrierLastContainer) { _carrierContainerChanges++; _carrierLastContainer = carrier.ContainerRef; }
            if (carrier.Epoch != _carrierLastEpoch) { _carrierEpochChanges++; _carrierLastEpoch = carrier.Epoch; }
        }

        private void FixedUpdate()
        {
            _fixedThisFrame++;
            if (_transport == null) return; // torn down (see OnDestroy); Unity can still call FixedUpdate this frame
            if (ConnectionState != State.InGame || LocalPlayer == null || LocalPlayer.Predicted == null) return;

            // Inputs must reach the worker before it simulates that tick: lead by half the RTT plus a margin, plus
            // whatever the worker's lead reports say is still missing (see OnOwnerState).
            int halfRttTicks = RttMs > 0 ? Mathf.CeilToInt(RttMs * 0.5f / 1000f * NetworkTime.TickRate) : 1;
            InputLeadTicks = halfRttTicks + Config.InputLeadMarginTicks + InputLeadAdjustTicks;
            uint target = (uint)Math.Round(_serverTickEstimate) + (uint)InputLeadTicks;
            uint first;
            int steps = 1;
            if (_predictTick == 0 || target > _predictTick + 8)
            {
                // Way behind: jump. The worker's next lead reports describe the ticks we skipped, so ignore them.
                first = target;
                _leadHoldUntil = Time.unscaledTime + Mathf.Max(0.1f, RttMs / 1000f) + 0.2f;
            }
            else if (target + 4 < _predictTick) return;                                     // way ahead: stall one tick
            else
            {
                // A little behind (the lead just grew): predict two ticks per FixedUpdate until caught up. Simply
                // advancing by one would keep pace with the target and never close the gap.
                first = _predictTick + 1;
                if (target > _predictTick + 1) steps = 2;
            }

            // The local pawn casts against the hull it rides. The interpolated hull moved in Update, and PhysX has
            // not seen that yet (transforms reach it at the physics step, after this): bring it up to date, as the
            // worker does between a carrier's tick and its contents', or the deck is a frame behind under the pawn.
            if (ContainerRegistry.Dynamic.Count > 0) Physics.SyncTransforms();

            // A pawn inside a physics frame predicts in the frame's own coordinates, exactly as its worker simulates
            // it: the frame root goes back to the identity pose for the step (docs/container-tree.md D11).
            PhysicsFrames.BeginSimulation(LocalPlayer.Container != null ? LocalPlayer.Container.InnerSpace : null);
            try
            {
                for (int i = 0; i < steps; i++)
                {
                    _predictTick = first + (uint)i;
                    NetworkTime.Tick = _predictTick;
                    _inputWriter.Reset();
                    LocalPlayer.Predicted.ClientPredictTick(_predictTick, _inputWriter);
                    _recentInputs.Add(new ClientInputMsg.Frame { Tick = _predictTick, Payload = _inputWriter.ToArray() });
                    while (_recentInputs.Count > 3) _recentInputs.RemoveAt(0);
                }
            }
            finally { PhysicsFrames.EndSimulation(); }

            _inputMsg.ClientId = ClientId;
            _inputMsg.Frames.Clear();
            _inputMsg.Frames.AddRange(_recentInputs);
            _writer.Reset();
            _inputMsg.Write(_writer, MsgId.ClientInput);
            _transport.Send(_gatewayPeer, Delivery.Sequenced, _writer.ToSegment());
            _transport.Flush();
        }

        private void ReportTelemetry()
        {
            var predicted = LocalPlayer != null ? LocalPlayer.Predicted : null;
            int corrections = predicted != null ? predicted.Corrections - _lastCorrections : 0;
            if (predicted != null) _lastCorrections = predicted.Corrections;
            float seconds = TelemetryIntervalSeconds;
            NebulaLog.Info($"client {_frames / seconds:0} fps replicas {_entities.Count} (+{SpawnsReceived / seconds:0.0}/s -{DespawnsReceived / seconds:0.0}/s) entities {_entities.Count} in {_packetsIn / seconds:0} pkt/s {_bytesIn / seconds / 1024f:0.0} KB/s {_stateEntriesIn / seconds:0} states/s (msgs: state {_statePacketsIn / seconds:0} rpc {_rpcPacketsIn / seconds:0} vars {_varsPacketsIn / seconds:0}) rtt {RttMs}ms lead {InputLeadTicks} (adj {InputLeadAdjustTicks}, worker saw {LastReportedInputLead}) corrections {corrections}{(predicted != null && corrections > 0 ? $" last {predicted.LastCorrectionMagnitude:0.00}m" : "")} | full-rate interp: depth {(RemoteInterpolator.Samples > 0 ? RemoteInterpolator.DepthSum / RemoteInterpolator.Samples : 0):0.0} ticks, starved {(RemoteInterpolator.Samples > 0 ? 100.0 * RemoteInterpolator.Starved / RemoteInterpolator.Samples : 0):0.0}% worst +{RemoteInterpolator.MaxOvershoot:0.0} | frame max {_maxFrameMs:0.0}ms fixed/frame max {_maxFixedPerFrame} | gaps>{StateGapThresholdTicks}t {_stateGaps} worst {_worstStateGap}t | render delay {RenderDelayTicks}t (stream lag {StreamLagTicks}t) | snapshot age ms min {(_ageCount > 0 ? _ageMin : 0):0.0} avg {(_ageCount > 0 ? _ageSum / _ageCount : 0):0.0} max {_ageMax:0.0}{(_carrier != null ? $" | carrier hitches {_carrierHitches} worst +{_carrierWorstJump:0.00}m, container changes {_carrierContainerChanges}, epoch changes {_carrierEpochChanges}" : "")}");
            SpawnsReceived = DespawnsReceived = 0;
            _carrierHitches = _carrierContainerChanges = _carrierEpochChanges = 0; _carrierWorstJump = 0;
            _ageMin = double.MaxValue; _ageMax = 0; _ageSum = 0; _ageCount = 0;
            RemoteInterpolator.ResetStats();
            _maxFrameMs = 0;
            _maxFixedPerFrame = 0;
            _stateGaps = 0;
            _worstStateGap = 0;
            _frames = 0;
            _packetsIn = 0;
            _bytesIn = 0;
            _stateEntriesIn = 0;
            _statePacketsIn = 0;
            _rpcPacketsIn = 0;
            _varsPacketsIn = 0;
        }

        private void NoteServerTick(uint tick)
        {
            if (tick <= _latestServerTick) return;
            _latestServerTick = tick;
            // Snapshots arrive ~half an RTT after they were produced.
            double halfRtt = RttMs > 0 ? RttMs * 0.5 / 1000.0 * NetworkTime.TickRate : 0.5;
            double now = Time.unscaledTimeAsDouble;
            double fresh = tick + halfRtt;
            double current = _serverTickAnchor + (now - _serverTickAnchorTime) * NetworkTime.TickRate;
            if (_serverTickAnchorTime == 0 || Math.Abs(fresh - current) > 3) _serverTickAnchor = fresh;
            else _serverTickAnchor = current + (fresh - current) * 0.1;
            _serverTickAnchorTime = now;
        }

        // ---------------------------------------------------------------------------------------- transport

        private void HandleTransportEvent(TransportEvent ev)
        {
            switch (ev.Type)
            {
                case TransportEvent.Kind.Connected:
                    SetState(State.Connected);
                    _writer.Reset();
                    string token = !string.IsNullOrEmpty(AuthToken) ? AuthToken : _storedToken;
                    _presentedStoredToken = string.IsNullOrEmpty(AuthToken) && token.Length > 0;
                    new HelloMsg { Role = PeerRole.Client, Id = PlayerName, Index = 0, Flags = CommandLine.Has("nebula-bot") ? HelloFlags.Bot : HelloFlags.None, Token = token, Session = SessionToken ?? "", ScopeKey = ScopeKey ?? "", GameContentVersion = Config != null ? Config.GameContentVersion : 0u }.Write(_writer);
                    _transport.Send(_gatewayPeer, Delivery.ReliableOrdered, _writer.ToSegment());
                    break;
                case TransportEvent.Kind.Disconnected:
                {
                    // The gateway closing the link a goodbye was sent on is its acknowledgement.
                    if (_leavingPeer >= 0 && ev.PeerId == _leavingPeer)
                    {
                        _leavingPeer = -1;
                        NebulaLog.Info("leaving: the gateway ended the session");
                        RaiseLeaveFinished(true);
                        break;
                    }
                    // Only the current link's close counts: a link this client already dropped (a drain, a
                    // refusal, Disconnect) or an encrypted link's second report of the same close is old news.
                    if (_gatewayPeer < 0 || ev.PeerId != _gatewayPeer) break;
                    _gatewayPeer = -1;
                    if (!WantsConnection) break; // we hung up ourselves
                    DisconnectReason reason;
                    if (_hasAnnouncedEnd)
                    {
                        reason = _announcedReason;
                        LastError = _announcedMessage;
                        _hasAnnouncedEnd = false;
                    }
                    else
                    {
                        reason = DisconnectInfo.FromTransport(ev.Reason, ConnectionState != State.Connecting);
                        string security = TransportSecurity.ErrorOf(_transport);
                        LastError = !string.IsNullOrEmpty(security)
                            ? $"encrypted connection refused: {security}"
                            : ConnectionState == State.Connecting
                            ? $"could not reach {Config.GatewayAddress}:{Config.GatewayPort} (retrying)"
                            : "disconnected from gateway (retrying)";
                    }
                    NebulaLog.Warn($"{LastError} [{reason}]");
                    ClearWorld();
                    bool wasReconnecting = IsReconnecting;
                    ScheduleRetry(Backoff.DelayAfter(IsReconnecting || !_hadSession ? _retries : 0));
                    SetState(State.Disconnected);
                    ReportDisconnect(reason, LastError, true);
                    RaiseReconnectingIfNew(wasReconnecting);
                    break;
                }
                case TransportEvent.Kind.Data:
                    // Only the current link speaks for the world: a link this client has dropped (a drain, a refusal,
                    // a kick, Leave) may still deliver what was in flight, and it must not rebuild a world the client
                    // cleared, or change its join, in a client that is now Disconnected or on its next link.
                    if (ev.PeerId != _gatewayPeer) break;
                    _packetsIn++;
                    _bytesIn += ev.Data.Count;
                    _reader.Set(ev.Data);
                    try { Dispatch(_reader); }
                    catch (Exception e) { NebulaLog.Error($"bad packet from gateway: {e}"); }
                    break;
            }
        }

        private void Dispatch(NetworkReader r)
        {
            var id = (MsgId)r.ReadByte();
            if (IsServerState(id)) NoteServerState();
            switch (id)
            {
                case MsgId.InstancePrepare:
                {
                    var preparation = InstancePreparationMsg.Read(r);
                    var destination = preparation.Destination.Resolve();
                    preparation.Success = destination != null && destination.LeaseEpoch == preparation.LeaseEpoch && InstanceScenes.Prepare(destination);
                    _writer.Reset(); preparation.Write(_writer, MsgId.InstanceReady);
                    _transport.Send(_gatewayPeer, Delivery.ReliableOrdered, _writer.ToSegment());
                    break;
                }
                case MsgId.Batch:
                {
                    // Handlers reset _reader for nested payloads, so the envelope is walked with its own reader.
                    _batchReader.Set(r.ReadSegment(r.Remaining));
                    int n = _batchReader.ReadUShort();
                    // A message that ends the link (a drain notice, a refusal, a kick) ends the batch with it: what
                    // follows it belongs to the link the client has just dropped.
                    int link = _gatewayPeer;
                    for (int i = 0; i < n && _gatewayPeer == link; i++)
                    {
                        var seg = _batchReader.ReadSegment(_batchReader.ReadUShort());
                        _reader.Set(seg);
                        Dispatch(_reader);
                    }
                    break;
                }
                case MsgId.Welcome:
                {
                    var w = WelcomeMsg.Read(r);
                    ClientId = w.ClientId;
                    NebulaRuntime.LocalClientId = ClientId;
                    Identity = w.Identity ?? "";
                    NebulaRuntime.LocalIdentity = Identity;
                    if (!string.IsNullOrEmpty(w.Token)) RememberIssuedToken(w.Token);
                    if (!string.IsNullOrEmpty(w.SessionToken)) SessionToken = w.SessionToken;
                    SessionReclaimed = w.Reclaimed;
                    // A gateway that does not write the field speaks exactly the protocol this build sent it.
                    NegotiatedProtocolVersion = w.NegotiatedVersion != 0 ? w.NegotiatedVersion : HelloMsg.ProtocolVersion;
                    NoteServerTick(w.ServerTick);
                    _outageReported = false;
                    _retries = 0;
                    _hadSession = true;
                    bool reconnected = IsReconnecting;
                    IsReconnecting = false;
                    SetState(State.InGame);
                    NebulaLog.Info($"welcome: clientId={ClientId} identity={(Identity.Length > 12 ? Identity.Substring(0, 12) : Identity)} serverTick={w.ServerTick}" + (w.Reclaimed ? " (session reclaimed)" : ""));
                    if (reconnected)
                    {
                        try { Reconnected?.Invoke(w.Reclaimed); }
                        catch (Exception e) { NebulaLog.Error($"Reconnected handler threw: {e}"); }
                    }
                    break;
                }
                case MsgId.JoinRejected:
                {
                    var rejected = JoinRejectedMsg.Read(r);
                    // A refusal the client does not retry ends the connection here, whether or not the gateway has
                    // closed the link yet: the Disconnected the transport reports afterwards is one we asked for.
                    bool stops = false;
                    if (rejected.Code == JoinRejectReason.ProtocolUnsupported || rejected.Code == JoinRejectReason.ContentVersionMismatch || rejected.Code == JoinRejectReason.EncryptionRequired)
                    {
                        // Build or encryption settings must change before retrying. Keep the saved identity:
                        // the gateway refused the connection before checking those credentials.
                        LastError = "cannot join: " + rejected.Reason;
                        stops = true;
                    }
                    else if (rejected.Code == JoinRejectReason.AtCapacity || rejected.Code == JoinRejectReason.Denied)
                    {
                        // Not about this client's credentials and not about this gateway: reconnecting on a timer
                        // would hammer a destination that is already full. The game decides what happens next -
                        // a queue, another instance, a different scope - which is the whole point of the typed code.
                        LastError = "join refused: " + rejected.Reason;
                        stops = true;
                    }
                    else if (rejected.Retry)
                    {
                        // The gateway, not this client, is the problem (draining, not ready): try again shortly; a
                        // load balancer hands the retry to another gateway.
                        LastError = "gateway unavailable: " + rejected.Reason + " (retrying)";
                        NebulaLog.Warn(LastError);
                        _nextConnectAttempt = Now + 1f;
                        AnnounceEnd(DisconnectReason.JoinRefused, LastError);
                    }
                    else if (_presentedStoredToken)
                    {
                        // The mesh no longer honours the saved anonymous token (a new signing key, or anonymous
                        // players were turned off): start over as a new player rather than loop on the same token.
                        NebulaLog.Warn($"gateway rejected the saved identity token ({rejected.Reason}); reconnecting for a new identity");
                        ForgetStoredToken();
                        AnnounceEnd(DisconnectReason.JoinRefused, $"the saved identity was refused ({rejected.Reason}); reconnecting as a new player");
                    }
                    else
                    {
                        LastError = "join rejected: " + rejected.Reason;
                        stops = true;
                    }
                    if (stops)
                    {
                        WantsConnection = false;
                        IsReconnecting = false;
                        _hadSession = false;
                        NebulaLog.Warn(LastError);
                        // Before the refusal's fields are set: clearing the world resets the join's state.
                        DropGatewayLink();
                        ClearWorld();
                    }
                    JoinRejectReason = rejected.Code;
                    JoinRejectSaturation = rejected.Saturation;
                    ServerProtocolWindow = (rejected.SupportedMinVersion, rejected.SupportedMaxVersion);
                    ServerContentVersion = rejected.ServerContentVersion;
                    if (stops)
                    {
                        SetState(State.Disconnected);
                        ReportDisconnect(DisconnectReason.JoinRefused, LastError, false);
                    }
                    JoinRejected?.Invoke(rejected.Reason);
                    JoinRefused?.Invoke(rejected);
                    break;
                }
                case MsgId.SessionReplaced:
                {
                    var replaced = SessionReplacedMsg.Read(r);
                    // Someone is playing this player now. Reconnecting on our own would take the session back from
                    // them, and they would take it back from us: stay out until the player says otherwise. The
                    // session token names a session that is no longer ours, so it goes too.
                    LastError = replaced.Reason;
                    NebulaLog.Warn("left the world: " + replaced.Reason);
                    SessionToken = "";
                    EndConnection();
                    ReportDisconnect(DisconnectReason.SessionReplaced, replaced.Reason, false);
                    try { SessionReplaced?.Invoke(replaced.Reason); }
                    catch (Exception e) { NebulaLog.Error($"SessionReplaced handler threw: {e}"); }
                    break;
                }
                case MsgId.GatewayDraining:
                {
                    var draining = GatewayDrainingMsg.Read(r);
                    bool wasReconnecting = IsReconnecting;
                    if (draining.ServerShutdown)
                    {
                        // The whole server is going away (or restarting): reconnect on the normal schedule, keeping
                        // the session token, so a server that comes back in time gives the pawn back.
                        LastError = "the server is shutting down (retrying)";
                        NebulaLog.Warn("the server is shutting down; reconnecting with the session token");
                        DropGatewayLink();
                        ClearWorld();
                        ScheduleRetry(Backoff.DelayAfter(0));
                        SetState(State.Disconnected);
                        ReportDisconnect(DisconnectReason.ServerShutdown, LastError, true);
                        RaiseReconnectingIfNew(wasReconnecting);
                        break;
                    }
                    // Reconnect now, keeping the session token: the next gateway reclaims the session and the pawn.
                    NebulaLog.Warn($"gateway is draining; reconnecting within {draining.ReconnectWithinSeconds} s with the session token");
                    DropGatewayLink();
                    ClearWorld();
                    ScheduleRetry(0.2f);
                    SetState(State.Disconnected);
                    ReportDisconnect(DisconnectReason.GatewayDraining, "the gateway is draining; reconnecting with the session token", true);
                    RaiseReconnectingIfNew(wasReconnecting);
                    // Last, once the client is in the state it reports: a handler may call Connect or Disconnect.
                    try { GatewayDraining?.Invoke(draining.ReconnectWithinSeconds); }
                    catch (Exception e) { NebulaLog.Error($"GatewayDraining handler threw: {e}"); }
                    break;
                }
                case MsgId.Kicked:
                {
                    var kicked = KickedMsg.Read(r);
                    // The session is over and the server does not want this player back now: no retry, and no session
                    // token to come back with. The identity token is kept; a kick is not a refused sign-in.
                    LastError = string.IsNullOrEmpty(kicked.Reason) ? "removed by the server" : kicked.Reason;
                    NebulaLog.Warn($"removed by the server (code {kicked.Code}): {LastError}");
                    SessionToken = "";
                    EndConnection();
                    ReportDisconnect(DisconnectReason.Kicked, LastError, false, kicked.Code);
                    break;
                }
                case MsgId.JoinStatus:
                {
                    var j = JoinStatusMsg.Read(r);
                    if (j.State == Join && j.EstimatedSeconds == JoinEstimatedSeconds && j.Reason == JoinHoldReason) break;
                    if (j.State == JoinState.Joined && Join != JoinState.Joined)
                    {
                        // The stall watch starts afresh with every join: the first state after it arms it.
                        _hasServerState = false;
                        _lastServerStateAt = Now;
                    }
                    Join = j.State;
                    JoinEstimatedSeconds = j.EstimatedSeconds;
                    JoinHoldReason = j.Reason;
                    if (Join != JoinState.Joined) EndStall();
                    if (Join == JoinState.Starting)
                        NebulaLog.Info($"the join is held ({j.Reason})" + (JoinEstimatedSeconds > 0 ? $", about {JoinEstimatedSeconds} s" : ""));
                    else if (Join == JoinState.Joined) NebulaLog.Info("joined the world");
                    try { JoinStateChanged?.Invoke(Join, JoinEstimatedSeconds); }
                    catch (Exception e) { NebulaLog.Error($"JoinStateChanged handler threw: {e}"); }
                    break;
                }
                case MsgId.Pong:
                {
                    var p = PongMsg.Read(r);
                    double measured = (Time.unscaledTimeAsDouble - p.ClientTime) * 1000.0;
                    if (measured >= 0) _pongRttMs = _pongRttMs < 0 ? measured : _pongRttMs + (measured - _pongRttMs) * 0.2;
                    NoteServerTick(p.ServerTick);
                    break;
                }
                case MsgId.ContainerOwnership:
                {
                    ApplyContainerOwnership(ContainerOwnershipMsg.Read(r));
                    break;
                }
                case MsgId.EntitySpawn: OnEntitySpawn(EntitySpawnMsg.Read(r)); break;
                case MsgId.EntityDespawn: OnEntityDespawn(EntityDespawnMsg.Read(r)); break;
                case MsgId.EntityVars: _varsPacketsIn++; OnEntityVars(EntityVarsMsg.Read(r)); break;
                case MsgId.EntityMaps: OnEntityMaps(EntityMapsMsg.Read(r)); break;
                case MsgId.EntityRpc: _rpcPacketsIn++; OnEntityRpc(EntityRpcMsg.Read(r)); break;
                case MsgId.WorldState: _statePacketsIn++; OnWorldState(r); break;
                case MsgId.EntityState: OnEntityState(EntitySyncMsg.Read(r)); break;
                case MsgId.OwnerState: OnOwnerState(OwnerStateMsg.Read(r)); break;
                default: NebulaLog.Warn($"client got unexpected {id}"); break;
            }
        }

        /// <summary>
        /// Apply an ownership message. A runtime lease disappearing is also a despawn boundary: the owning worker
        /// retires every entity in the container before removing the row. Remove those replicas here as part of
        /// the same message so a delayed or lost entity-despawn packet cannot leave an evacuated, permanently
        /// stale client object behind.
        /// <para>
        /// A <see cref="ContainerOwnershipUpdate.Full"/> message is the complete set of containers this client
        /// should know about and anything else is forgotten; a delta only adds, changes and removes what it names,
        /// which is how a client of a large world hears about its own surroundings instead of the whole lease table.
        /// </para>
        /// </summary>
        internal void ApplyContainerOwnership(in ContainerOwnershipUpdate update)
        {
            var entries = update.Upserts;
            // Runtime containers come and go with their lease rows: register the ones that carry a box, forget the rest.
            _runtimeKeep.Clear();
            if (!update.Full)
            {
                // A delta says nothing about the containers it leaves out, so they stay.
                foreach (var c in ContainerRegistry.Runtime) _runtimeKeep.Add(c.RuntimeId);
                foreach (var id in ContainerRegistry.PendingRuntimeIds) _runtimeKeep.Add(id);
                if (update.Removes != null)
                    foreach (var id in update.Removes)
                        if (ContainerRegistry.TryParseRuntimeId(id, out ulong retired)) _runtimeKeep.Remove(retired);
            }
            if (entries != null) foreach (var e in entries)
            {
                if (!e.HasBounds || !ContainerRegistry.TryParseRuntimeId(e.ContainerId, out ulong runtimeId)) continue;
                _runtimeKeep.Add(runtimeId);
                if (ContainerRegistry.GetRuntime(runtimeId) == null)
                    ContainerRegistry.RegisterRuntime(runtimeId, e.PlacementOrRoot, e.Instance);
            }

            // Capture occupants before PruneRuntime evacuates them into a neighbouring box. Runtime retirement
            // guarantees that these entities were despawned by their authority; the snapshot is the durable proof.
            // The local pawn is the exception: the gateway keeps it in this client's set wherever it is, so a box
            // leaving the window only means our copy has not heard about its move yet. Despawning it here would
            // leave the client pawn-less, because the gateway believes the client still has it and never resends it.
            _retiredRuntimeEntities.Clear();
            foreach (var pair in _entities)
            {
                if (pair.Value == LocalPlayer) continue;
                var container = pair.Value != null ? pair.Value.Container : null;
                if (container != null && container.IsRuntime && !_runtimeKeep.Contains(container.RuntimeId))
                    _retiredRuntimeEntities.Add(pair.Key);
            }
            ContainerRegistry.PruneRuntime(_runtimeKeep);
            foreach (var netId in _retiredRuntimeEntities)
                if (_entities.TryGetValue(netId, out var entity))
                    OnEntityDespawn(new EntityDespawnMsg { NetId = netId, Epoch = entity.Epoch });
            _retiredRuntimeEntities.Clear();

            _seenLeases.Clear();
            if (entries != null) foreach (var e in entries)
            {
                ContainerRegistry.ApplyLease(e.ContainerId, e.WorkerId, e.WorkerIndex, e.Epoch, e.State);
                _seenLeases.Add(e.ContainerId);
            }
            if (update.Full)
            {
                foreach (var c in ContainerRegistry.Dynamic) if (!_seenLeases.Contains(c.ContainerId)) ContainerRegistry.ForgetLease(c.ContainerId);
            }
            else if (update.Removes != null)
            {
                foreach (var id in update.Removes) ContainerRegistry.ForgetLease(id);
            }
            ContainerRegistry.NotifyLeasesChanged();
            ContainerOwnershipChanged?.Invoke();
        }

        // ---------------------------------------------------------------------------------------- entities

        private void OnEntitySpawn(EntitySpawnMsg msg)
        {
            if (msg.ViewSeq != 0)
            {
                // Re-entering the set is a new view of the same net id, so the despawn of the old one must not
                // apply to it. An in-place update (an authority transfer, a container change) keeps its view.
                _viewSeq.TryGetValue(msg.NetId, out ushort held);
                if (msg.ViewSeq > held) { _viewSeq[msg.NetId] = msg.ViewSeq; SpawnsReceived++; }
            }
            var container = ContainerRegistry.Resolve(msg.Container);
            if (container == null && msg.Container.MayArriveLater)
            {
                if (_entities.ContainsKey(msg.NetId))
                {
                    // Known entity moved into a container we do not have: keep it where it is until that arrives.
                    NebulaLog.Warn($"entity #{msg.NetId} is inside container {msg.Container}, unknown here; holding");
                }
                HoldForCarrier(msg);
                return;
            }
            if (_entities.TryGetValue(msg.NetId, out var e))
            {
                if (msg.Epoch < e.Epoch) return;
                ushort oldWorker = e.OwnerWorkerIndex;
                if (msg.Epoch > e.Epoch) e.HasStateTick = false;
                e.Epoch = msg.Epoch;
                e.OwnerWorkerIndex = msg.OwnerWorkerIndex;
                e.OwnerClientId = msg.OwnerClientId;
                e.OwnerIdentity = msg.OwnerIdentity ?? "";
                e.OwnerIsBot = (msg.Flags & EntityFlags.OwnerIsBot) != 0;
                e.IsServerDriven = (msg.Flags & EntityFlags.ServerDriven) != 0;
                e.SetOwnerConnected((msg.Flags & EntityFlags.OwnerDisconnected) == 0);
                if (container != e.Container) e.SetContainer(container);
                if (msg.Vars != null && msg.Vars.Length > 0)
                {
                    _reader.Set(new ArraySegment<byte>(msg.Vars));
                    e.ReadVars(_reader);
                }
                // A spawn for an entity already held (a new owner's announcement) carries the maps in full; the
                // copy is replaced, with a change for each key that differs (docs/replicated-collections.md D7).
                e.ReadMaps(msg.Maps);
                if (msg.State != null && msg.State.Length > 0)
                {
                    _reader.Set(new ArraySegment<byte>(msg.State));
                    e.ReadSyncState(_reader, 0, e.Container);
                }
                if (oldWorker != msg.OwnerWorkerIndex)
                {
                    AuthorityChangesSeen++;
                    EntityAuthorityChanged?.Invoke(e, oldWorker, msg.OwnerWorkerIndex);
                }
                return;
            }

            if (msg.SceneId != 0)
            {
                e = SceneEntities.Find(msg.SceneId);
                if (e == null)
                {
                    HoldSceneSpawn(msg);
                    return;
                }
                if (e.NetId != 0)
                {
                    // Bound to an earlier life whose despawn we have not seen (or will not: a worker died). This is the one.
                    _entities.Remove(e.NetId);
                    e.Unbind();
                }
            }
            else e = NetworkPrefabs.Instantiate(msg.PrefabId, Vector3.zero, Quaternion.identity, container != null ? container.transform : null);
            if (e == null) return;
            e.NetId = msg.NetId;
            e.Epoch = msg.Epoch;
            e.OwnerClientId = msg.OwnerClientId;
            e.OwnerIdentity = msg.OwnerIdentity ?? "";
            e.OwnerIsBot = (msg.Flags & EntityFlags.OwnerIsBot) != 0;
            e.IsServerDriven = (msg.Flags & EntityFlags.ServerDriven) != 0;
            e.SetOwnerConnected((msg.Flags & EntityFlags.OwnerDisconnected) == 0);
            e.OwnerWorkerIndex = msg.OwnerWorkerIndex;
            e.HasAuthority = false;
            e.IsLocalPlayer = msg.OwnerClientId != 0 && msg.OwnerClientId == ClientId;
            e.SetContainer(container);
            e.SetLocalPose(container, msg.LocalPosition, msg.LocalRotation);
            e.transform.localScale = msg.LocalScale;
            e.HasStateTick = false;
            e.Motion.Velocity = msg.Velocity;
            if (msg.Vars != null && msg.Vars.Length > 0)
            {
                _reader.Set(new ArraySegment<byte>(msg.Vars));
                e.ReadVars(_reader);
            }
            e.ReadMaps(msg.Maps);
            if (msg.State != null && msg.State.Length > 0)
            {
                _reader.Set(new ArraySegment<byte>(msg.State));
                e.ReadSyncState(_reader, 0, e.Container);
            }
            e.ClearDirty();
            if (!e.IsLocalPlayer)
            {
                e.Interpolator = e.gameObject.AddComponent<RemoteInterpolator>();
                e.Interpolator.Push(_latestServerTick, e.Container, e.LocalPosition, e.LocalRotation, e.Motion.Velocity);
                var rb = e.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
            }
            _entities[e.NetId] = e;
            e.gameObject.SetActive(true);
            e.InvokeSpawn();
            EntitySpawned?.Invoke(e);
            if (e.IsLocalPlayer)
            {
                LocalPlayer = e;
                e.Predicted?.ResetPrediction();
                _predictTick = 0;
                LocalPlayerSpawned?.Invoke(e);
                NebulaLog.Info($"local player spawned: {e}");
            }
        }

        private void OnEntityDespawn(EntityDespawnMsg msg)
        {
            // A despawn for a view we have already left behind is stale: the entity left the set and came back,
            // and this message was in flight for the earlier visit. Killing the new replica would leave a hole
            // in the world that nothing would ever fill, because the gateway believes the client has it.
            if (msg.ViewSeq != 0 && _viewSeq.TryGetValue(msg.NetId, out ushort current) && msg.ViewSeq < current) return;
            _viewSeq.Remove(msg.NetId);
            DespawnsReceived++;
            _pendingByCarrier.Remove(ContainerRef.Dynamic(msg.NetId));
            foreach (var kv in _pendingByCarrier) kv.Value.RemoveAll(held => held.NetId == msg.NetId && msg.Epoch >= held.Epoch);
            if (!_entities.TryGetValue(msg.NetId, out var e))
            {
                if (_pendingSceneByNetId.TryGetValue(msg.NetId, out uint sceneId) && _pendingScene.TryGetValue(sceneId, out var held) && msg.Epoch >= held.Epoch)
                {
                    _pendingScene.Remove(sceneId);
                    _pendingSceneByNetId.Remove(msg.NetId);
                }
                return;
            }
            if (msg.Epoch < e.Epoch) return;
            _entities.Remove(msg.NetId);
            bool wasLocal = LocalPlayer == e;
            if (wasLocal) LocalPlayer = null;
            EvacuateCarried(e);
            e.InvokeDespawn();
            EntityDespawned?.Invoke(e);
            if (wasLocal)
            {
                // The gateway says it is placing the player again before it despawns a pawn lost with its worker.
                var cause = Join == JoinState.Starting && JoinHoldReason == JoinHoldReason.Recovering ? LocalPlayerLossCause.WorkerLost : LocalPlayerLossCause.Despawned;
                NebulaLog.Info($"local player lost ({cause})");
                try { LocalPlayerLost?.Invoke(e, cause); }
                catch (Exception ex) { NebulaLog.Error($"LocalPlayerLost handler threw: {ex}"); }
            }
            if (e.IsSceneEntity) e.Unbind(); // the object belongs to its scene
            else if (Application.isPlaying) Destroy(e.gameObject);
            else DestroyImmediate(e.gameObject); // edit-mode tests and editor tooling
        }

        /// <summary>
        /// Put down everything riding in <paramref name="carrier"/>'s dynamic container before its object is
        /// destroyed: into the container the carrier itself was in, the way the worker does
        /// (<c>NebulaWorker.EvacuateCarried</c>). Riders are parented under the carrier, so without this a ship
        /// leaving the client's view destroyed the passengers' replicas with it and left dead entries in the entity
        /// table (the next frame's <see cref="NetworkIdentity.RemoteTick"/> and a later despawn threw on them). The
        /// riders' own despawns or updates, if any, arrive separately.
        /// </summary>
        private void EvacuateCarried(NetworkIdentity carrier)
        {
            var box = carrier.Carried;
            if (box == null || box.Entities.Count == 0) return;
            _ridersScratch.Clear();
            _ridersScratch.AddRange(box.Entities);
            var replacement = carrier.Container;
            foreach (var rider in _ridersScratch)
            {
                if (rider == null || rider == carrier) continue;
                try { rider.SetContainer(replacement); }
                catch (Exception ex) { NebulaLog.Error($"could not put {rider} down while {carrier} left this client's view: {ex.Message}"); }
            }
            _ridersScratch.Clear();
        }

        private readonly List<NetworkIdentity> _ridersScratch = new List<NetworkIdentity>();
        private readonly List<NetworkIdentity> _destroyedScratch = new List<NetworkIdentity>();
        private readonly List<ulong> _destroyedIds = new List<ulong>();

        /// <summary>Forget replicas whose objects were destroyed without a despawn, once, with a warning.</summary>
        private void DropDestroyed()
        {
            _destroyedIds.Clear();
            foreach (var kv in _entities) if (kv.Value == null) _destroyedIds.Add(kv.Key);
            foreach (ulong id in _destroyedIds)
            {
                _entities.Remove(id);
                NebulaLog.Warn($"replica {id} was destroyed without a despawn; dropped from this client's entity table");
            }
            _destroyedScratch.Clear();
            _destroyedIds.Clear();
        }

        private void OnEntityVars(EntityVarsMsg msg)
        {
            if (!_entities.TryGetValue(msg.NetId, out var e))
            {
                // A held scene entity: the whole block is sent each time, so the record's copy just gets replaced.
                if (_pendingSceneByNetId.TryGetValue(msg.NetId, out uint sceneId) && _pendingScene.TryGetValue(sceneId, out var held) && msg.Epoch >= held.Epoch)
                {
                    held.Vars = msg.Vars;
                    _pendingScene[sceneId] = held;
                }
                return;
            }
            if (msg.Epoch < e.Epoch) return;
            _reader.Set(new ArraySegment<byte>(msg.Vars));
            e.ReadVars(_reader);
            e.ClearDirty();
        }

        private void OnEntityMaps(EntityMapsMsg msg)
        {
            if (!_entities.TryGetValue(msg.NetId, out var e))
            {
                // A held spawn keeps a full copy; the delta is folded into it, since it will not be sent again.
                if (_pendingSceneByNetId.TryGetValue(msg.NetId, out uint sceneId) && _pendingScene.TryGetValue(sceneId, out var held) && msg.Epoch >= held.Epoch)
                {
                    held.Maps = NetworkMapCache.Fold(held.Maps, msg.Maps);
                    _pendingScene[sceneId] = held;
                }
                foreach (var list in _pendingByCarrier.Values)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].NetId == msg.NetId && msg.Epoch >= list[i].Epoch) { var h = list[i]; h.Maps = NetworkMapCache.Fold(h.Maps, msg.Maps); list[i] = h; }
                return;
            }
            if (msg.Epoch < e.Epoch) return;
            e.ReadMaps(msg.Maps);
        }

        // ---------------------------------------------------------------------------------------- scene entities

        private void HoldForCarrier(EntitySpawnMsg msg)
        {
            if (!_pendingByCarrier.TryGetValue(msg.Container, out var list)) _pendingByCarrier[msg.Container] = list = new List<EntitySpawnMsg>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].NetId != msg.NetId) continue;
                if (msg.Epoch >= list[i].Epoch) list[i] = msg;
                return;
            }
            list.Add(msg);
        }

        /// <summary>A carrier's or a runtime container is resolvable now: the spawns that were waiting for it bind.</summary>
        private void OnLateContainerRegistered(Container container)
        {
            var key = container.Ref;
            if (!_pendingByCarrier.TryGetValue(key, out var list)) return;
            _pendingByCarrier.Remove(key);
            foreach (var msg in list) OnEntitySpawn(msg);
        }

        private void HoldSceneSpawn(EntitySpawnMsg msg)
        {
            if (_pendingScene.TryGetValue(msg.SceneId, out var held))
            {
                if (msg.Epoch < held.Epoch) return;
                _pendingSceneByNetId.Remove(held.NetId);
            }
            _pendingScene[msg.SceneId] = msg;
            _pendingSceneByNetId[msg.NetId] = msg.SceneId;
        }

        /// <summary>A scene object became resident: the spawn that was waiting for it binds now.</summary>
        private void OnSceneEntityRegistered(NetworkIdentity e)
        {
            if (!_pendingScene.TryGetValue(e.SceneId, out var held)) return;
            _pendingScene.Remove(e.SceneId);
            _pendingSceneByNetId.Remove(held.NetId);
            OnEntitySpawn(held);
        }

        /// <summary>
        /// A bound scene object is leaving with its scene. The entity lives on in the mesh, so its spawn is held
        /// again (variables keep it current) and binds when the cell comes back.
        /// </summary>
        private void OnSceneEntityUnregistering(NetworkIdentity e)
        {
            if (e.NetId == 0 || !_entities.TryGetValue(e.NetId, out var bound) || bound != e) return;
            _entities.Remove(e.NetId);
            if (LocalPlayer == e) LocalPlayer = null;
            _writer.Reset();
            e.WriteMapsFull(_writer);
            var maps = _writer.Length > 0 ? _writer.ToArray() : null;
            _writer.Reset();
            e.WriteVars(_writer);
            HoldSceneSpawn(new EntitySpawnMsg
            {
                NetId = e.NetId,
                PrefabId = e.PrefabId,
                SceneId = e.SceneId,
                OwnerClientId = e.OwnerClientId,
                Container = e.ContainerRef,
                Epoch = e.Epoch,
                OwnerWorkerIndex = e.OwnerWorkerIndex,
                LocalPosition = e.LocalPosition,
                LocalRotation = e.LocalRotation,
                LocalScale = e.transform.localScale,
                Velocity = e.Motion.Velocity,
                Flags = (e.OwnerIsBot ? EntityFlags.OwnerIsBot : EntityFlags.None) | (e.IsServerDriven ? EntityFlags.ServerDriven : EntityFlags.None),
                Vars = _writer.ToArray(),
                State = Array.Empty<byte>(),
                Maps = maps,
            });
            e.InvokeDespawn();
            EntityDespawned?.Invoke(e);
            e.Unbind();
        }

        private void OnEntityRpc(EntityRpcMsg msg)
        {
            if (!_entities.TryGetValue(msg.NetId, out var e) || msg.Epoch < e.Epoch) return;
            if (msg.BehaviourIndex >= e.Behaviours.Length) return;
            _reader.Set(new ArraySegment<byte>(msg.Args));
            RpcRegistry.Invoke(e.Behaviours[msg.BehaviourIndex], msg.MethodHash, _reader);
        }

        private void OnWorldState(NetworkReader r)
        {
            WorldStateMsg.ReadHeader(r, out uint tick, out ushort workerIndex, out ushort count);
            NoteServerTick(tick);
            int lag = (int)Math.Min(_latestServerTick - tick, 30);
            if (Time.unscaledTime >= _lagBucketEnds) { _lagPreviousBucket = _lagBucket; _lagBucket = 0; _lagBucketEnds = Time.unscaledTime + 1f; }
            if (lag > _lagBucket) _lagBucket = lag;
            double age = (NetworkTime.DerivedTickExact - tick) * NetworkTime.TickInterval * 1000.0;
            if (age < _ageMin) _ageMin = age;
            if (age > _ageMax) _ageMax = age;
            _ageSum += age; _ageCount++;
            _stateEntriesIn += count;
            for (int i = 0; i < count; i++)
            {
                var entry = EntityStateEntry.Read(r);
                if (!_entities.TryGetValue(entry.NetId, out var e) || entry.Epoch < e.Epoch) continue;
                if (e == LocalPlayer)
                {
                    // The own pawn is sent every tick: a gap here is a packet lost or delayed somewhere on the path.
                    if (_lastOwnStateTick != 0 && tick > _lastOwnStateTick + StateGapThresholdTicks)
                    {
                        _stateGaps++;
                        if (tick - _lastOwnStateTick > _worstStateGap) _worstStateGap = tick - _lastOwnStateTick;
                    }
                    if (tick > _lastOwnStateTick) _lastOwnStateTick = tick;
                }
                ushort oldWorker = e.OwnerWorkerIndex;
                if (!e.ReceiveState(tick, workerIndex, entry)) continue;
                if (oldWorker != workerIndex)
                {
                    AuthorityChangesSeen++;
                    EntityAuthorityChanged?.Invoke(e, oldWorker, workerIndex);
                }
            }
        }

        private void OnEntityState(EntitySyncMsg msg)
        {
            if (!_entities.TryGetValue(msg.NetId, out var e) || msg.Epoch < e.Epoch) return;
            NoteServerTick(msg.Tick);
            // Behaviours the local client is itself authoritative for (owner mode) ignore their own echo inside ReadSyncState.
            _reader.Set(new ArraySegment<byte>(msg.Chunks));
            // The channel matters to a behaviour this client has just left the audience of (docs/sync-audience.md D9).
            e.ReadSyncState(_reader, msg.Tick, ContainerRegistry.Resolve(msg.Container) ?? e.Container, msg.Reliable);
        }

        private void OnOwnerState(OwnerStateMsg msg)
        {
            if (LocalPlayer == null || LocalPlayer.NetId != msg.NetId || LocalPlayer.Predicted == null) return;
            if (msg.Epoch < LocalPlayer.Epoch) return;
            LocalPlayer.Epoch = msg.Epoch;
            if (msg.InputLead != OwnerStateMsg.NoInputLead) NoteInputLead(msg.InputLead);
            if (msg.Tick == 0) return;
            // The state is in the worker's container frame for that tick; move there first, or a seam crossing
            // would reconcile against the wrong origin for a tick and snap the pawn across the map.
            var container = ContainerRegistry.Resolve(msg.Container);
            if (container == null && msg.Container.MayArriveLater) return; // the container is not here yet; the next report will do
            if (container != LocalPlayer.Container) LocalPlayer.SetContainer(container);
            _reader.Set(new ArraySegment<byte>(msg.State));
            // A correction replays inputs, which is simulation: in the pawn's frame at the identity pose (D11).
            PhysicsFrames.BeginSimulation(container != null ? container.InnerSpace : null);
            try { LocalPlayer.Predicted.ClientReconcile(msg.Tick, _reader); }
            finally { PhysicsFrames.EndSimulation(); }
        }

        /// <summary>
        /// Adapt the input lead to what the worker actually sees. Late (below target) is fixed at once by the full
        /// shortfall, then held for about an RTT so reports about inputs sent with the old lead do not stack up;
        /// generously early is relaxed one tick at a time, slowly, so the lead settles just above the target.
        /// </summary>
        private void NoteInputLead(int lead)
        {
            LastReportedInputLead = lead;
            float now = Time.unscaledTime;
            if (now < _leadHoldUntil) return; // reports about inputs sent before the last change (or before a tick jump)
            int target = Config.InputLeadTargetTicks;
            float hold = Mathf.Max(0.1f, RttMs / 1000f) + 0.1f;
            if (lead < target)
            {
                // Fix the shortfall at once, capped per step: one wild report (a tick jump the worker had not seen
                // yet) must not push the lead to the ceiling and then take half a minute to come back down.
                int add = Math.Min(Math.Min(target - lead, MaxLeadStep), Config.InputLeadMaxAdjustTicks - InputLeadAdjustTicks);
                if (add <= 0) return;
                InputLeadAdjustTicks += add;
                InputLeadIncreases++;
                _leadHoldUntil = now + hold;
                _leadRelaxAt = now + 3f;
            }
            else if (lead > target + 3 && InputLeadAdjustTicks > 0 && now >= _leadRelaxAt)
            {
                // Comfortably early: come down, faster the further above target, once a second.
                int sub = Math.Min(InputLeadAdjustTicks, Math.Max(1, (lead - target - 3) / 2));
                InputLeadAdjustTicks -= sub;
                _leadRelaxAt = now + 1f;
                _leadHoldUntil = now + hold;
            }
        }

        private const int MaxLeadStep = 8;

        private void ClearWorld()
        {
            foreach (var e in _entities.Values)
            {
                if (e == null) continue;
                e.InvokeDespawn();
                EntityDespawned?.Invoke(e);
                if (e.IsSceneEntity) e.Unbind();
                else if (Application.isPlaying) Destroy(e.gameObject);
                else DestroyImmediate(e.gameObject); // edit-mode tests and editor tooling
            }
            _entities.Clear();
            _viewSeq.Clear();
            _pendingScene.Clear();
            _pendingSceneByNetId.Clear();
            _pendingByCarrier.Clear();
            LocalPlayer = null;
            _hasRenderOffset = false;
            _hasServerState = false;
            EndStall();
            if (Join != JoinState.None)
            {
                Join = JoinState.None;
                JoinEstimatedSeconds = 0;
                JoinHoldReason = JoinHoldReason.None;
                JoinRejectReason = JoinRejectReason.None;
                JoinRejectSaturation = 0f;
                try { JoinStateChanged?.Invoke(Join, 0); }
                catch (Exception e) { NebulaLog.Error($"JoinStateChanged handler threw: {e}"); }
            }
        }

        public NetworkIdentity Find(ulong netId) => _entities.TryGetValue(netId, out var e) ? e : null;

        // ---------------------------------------------------------------------------------------- IRpcSink

        void IRpcSink.SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius)
        {
            NebulaLog.Warn("ClientRpc sent from a client; ignored");
        }

        void IRpcSink.SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args)
        {
            if (ConnectionState != State.InGame) return;
            var arr = new byte[args.Count];
            Buffer.BlockCopy(args.Array, args.Offset, arr, 0, args.Count);
            _writer.Reset();
            new EntityRpcMsg { NetId = identity.NetId, Epoch = identity.Epoch, BehaviourIndex = behaviourIndex, MethodHash = methodHash, ClientId = ClientId, Args = arr }.Write(_writer, MsgId.ServerRpc);
            _transport.Send(_gatewayPeer, Delivery.ReliableOrdered, _writer.ToSegment());
        }

        void IRpcSink.SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args)
        {
            NebulaLog.Warn("AuthorityRpc sent from a client; ignored");
        }

        ulong IRpcSink.SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<AuthorityCallResult> onDone, float timeoutSeconds)
        {
            NebulaLog.Warn("AuthorityRpc sent from a client; ignored");
            onDone?.Invoke(new AuthorityCallResult(0, AuthorityCallOutcome.RejectedUnreachable, 0, 0));
            return 0;
        }
    }
}
