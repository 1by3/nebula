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
        public uint ClientId { get; private set; }
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
        /// <summary>The join's state changed: (state, estimated seconds). Raised on the main thread.</summary>
        public event Action<JoinState, int> JoinStateChanged;

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
        public void Initialize(NebulaConfig config, bool autoConnect = true)
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
            // A browser has no UDP sockets: a web build reaches the gateway over WebRTC data channels.
            _transport = new WebRtcClientTransport("client");
#else
            _transport = new LiteNetTransport("client");
#endif
            _transport.StartClient();
            SceneEntities.Registered += OnSceneEntityRegistered;
            SceneEntities.Unregistering += OnSceneEntityUnregistering;
            ContainerRegistry.DynamicRegistered += OnLateContainerRegistered;
            ContainerRegistry.RuntimeRegistered += OnLateContainerRegistered;
            if (autoConnect) Connect();
        }

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
            Connect();
        }

        public void Connect()
        {
            WantsConnection = true;
            if (ConnectionState != State.Disconnected) return;
            SetState(State.Connecting);
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

        /// <summary>Leave the gateway and stop reconnecting. The client stays idle until the next <see cref="Connect"/> or <see cref="ConnectTo"/>.</summary>
        public void Disconnect()
        {
            WantsConnection = false;
            if (_gatewayPeer >= 0)
            {
                try { _transport.Disconnect(_gatewayPeer); } catch { }
                _gatewayPeer = -1;
            }
            ClearWorld();
            SetState(State.Disconnected);
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

        // ---------------------------------------------------------------------------------------- frame loop

        private void Update()
        {
            if (_transport == null) return; // not initialised (a stray component), or torn down
            _transport.Poll(HandleTransportEvent);

            if (ConnectionState == State.Disconnected && WantsConnection && Time.unscaledTime >= _nextConnectAttempt)
            {
                _nextConnectAttempt = Time.unscaledTime + 2f;
                Connect();
            }
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
                // The local player too: its server-authoritative children (a NetworkTransform on a turret, say) interpolate.
                e.RemoteTick(_renderTick);
            }
            TrackCarrier();
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

            for (int i = 0; i < steps; i++)
            {
                _predictTick = first + (uint)i;
                NetworkTime.Tick = _predictTick;
                _inputWriter.Reset();
                LocalPlayer.Predicted.ClientPredictTick(_predictTick, _inputWriter);
                _recentInputs.Add(new ClientInputMsg.Frame { Tick = _predictTick, Payload = _inputWriter.ToArray() });
                while (_recentInputs.Count > 3) _recentInputs.RemoveAt(0);
            }

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
            NebulaLog.Info($"client {_frames / seconds:0} fps entities {_entities.Count} in {_packetsIn / seconds:0} pkt/s {_bytesIn / seconds / 1024f:0.0} KB/s {_stateEntriesIn / seconds:0} states/s (msgs: state {_statePacketsIn / seconds:0} rpc {_rpcPacketsIn / seconds:0} vars {_varsPacketsIn / seconds:0}) rtt {RttMs}ms lead {InputLeadTicks} (adj {InputLeadAdjustTicks}, worker saw {LastReportedInputLead}) corrections {corrections}{(predicted != null && corrections > 0 ? $" last {predicted.LastCorrectionMagnitude:0.00}m" : "")} | full-rate interp: depth {(RemoteInterpolator.Samples > 0 ? RemoteInterpolator.DepthSum / RemoteInterpolator.Samples : 0):0.0} ticks, starved {(RemoteInterpolator.Samples > 0 ? 100.0 * RemoteInterpolator.Starved / RemoteInterpolator.Samples : 0):0.0}% worst +{RemoteInterpolator.MaxOvershoot:0.0} | frame max {_maxFrameMs:0.0}ms fixed/frame max {_maxFixedPerFrame} | gaps>{StateGapThresholdTicks}t {_stateGaps} worst {_worstStateGap}t | render delay {RenderDelayTicks}t (stream lag {StreamLagTicks}t) | snapshot age ms min {(_ageCount > 0 ? _ageMin : 0):0.0} avg {(_ageCount > 0 ? _ageSum / _ageCount : 0):0.0} max {_ageMax:0.0}{(_carrier != null ? $" | carrier hitches {_carrierHitches} worst +{_carrierWorstJump:0.00}m, container changes {_carrierContainerChanges}, epoch changes {_carrierEpochChanges}" : "")}");
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
                    new HelloMsg { Role = PeerRole.Client, Id = PlayerName, Index = 0, Flags = CommandLine.Has("nebula-bot") ? HelloFlags.Bot : HelloFlags.None, Token = token }.Write(_writer);
                    _transport.Send(_gatewayPeer, Delivery.ReliableOrdered, _writer.ToSegment());
                    break;
                case TransportEvent.Kind.Disconnected:
                    if (!WantsConnection) break; // we hung up ourselves
                    LastError = ConnectionState == State.Connecting
                        ? $"could not reach {Config.GatewayAddress}:{Config.GatewayPort} (retrying)"
                        : "disconnected from gateway (retrying)";
                    NebulaLog.Warn(LastError);
                    ClearWorld();
                    SetState(State.Disconnected);
                    _nextConnectAttempt = Time.unscaledTime + 1f;
                    break;
                case TransportEvent.Kind.Data:
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
            switch (id)
            {
                case MsgId.Batch:
                {
                    // Handlers reset _reader for nested payloads, so the envelope is walked with its own reader.
                    _batchReader.Set(r.ReadSegment(r.Remaining));
                    int n = _batchReader.ReadUShort();
                    for (int i = 0; i < n; i++)
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
                    NoteServerTick(w.ServerTick);
                    SetState(State.InGame);
                    NebulaLog.Info($"welcome: clientId={ClientId} identity={(Identity.Length > 12 ? Identity.Substring(0, 12) : Identity)} serverTick={w.ServerTick}");
                    break;
                }
                case MsgId.JoinRejected:
                {
                    var rejected = JoinRejectedMsg.Read(r);
                    if (_presentedStoredToken)
                    {
                        // The mesh no longer honours the saved anonymous token (a new signing key, or anonymous
                        // players were turned off): start over as a new player rather than loop on the same token.
                        NebulaLog.Warn($"gateway rejected the saved identity token ({rejected.Reason}); reconnecting for a new identity");
                        ForgetStoredToken();
                    }
                    else
                    {
                        LastError = "join rejected: " + rejected.Reason;
                        WantsConnection = false;
                        NebulaLog.Warn(LastError);
                    }
                    JoinRejected?.Invoke(rejected.Reason);
                    break;
                }
                case MsgId.JoinStatus:
                {
                    var j = JoinStatusMsg.Read(r);
                    if (j.State == Join && j.EstimatedSeconds == JoinEstimatedSeconds) break;
                    Join = j.State;
                    JoinEstimatedSeconds = j.EstimatedSeconds;
                    if (Join == JoinState.Starting)
                        NebulaLog.Info("world starting" + (JoinEstimatedSeconds > 0 ? $", about {JoinEstimatedSeconds} s" : "") + ": no worker is running yet; holding the join");
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
                    var entries = ContainerOwnershipMsg.Read(r);
                    // Runtime containers come and go with their lease rows: register the ones that carry a box, forget the rest.
                    _runtimeKeep.Clear();
                    foreach (var e in entries)
                    {
                        if (!e.HasBounds || !ContainerRegistry.TryParseRuntimeId(e.ContainerId, out ulong runtimeId)) continue;
                        _runtimeKeep.Add(runtimeId);
                        if (ContainerRegistry.GetRuntime(runtimeId) == null) ContainerRegistry.RegisterRuntime(runtimeId, ContainerRegistry.ToFrame(new Bounds(e.BoundsCenter, e.BoundsSize)));
                    }
                    ContainerRegistry.PruneRuntime(_runtimeKeep);
                    _seenLeases.Clear();
                    foreach (var e in entries)
                    {
                        ContainerRegistry.ApplyLease(e.ContainerId, e.WorkerId, e.WorkerIndex, e.Epoch, e.State);
                        _seenLeases.Add(e.ContainerId);
                    }
                    foreach (var c in ContainerRegistry.Dynamic) if (!_seenLeases.Contains(c.ContainerId)) ContainerRegistry.ForgetLease(c.ContainerId);
                    ContainerRegistry.NotifyLeasesChanged();
                    ContainerOwnershipChanged?.Invoke();
                    break;
                }
                case MsgId.EntitySpawn: OnEntitySpawn(EntitySpawnMsg.Read(r)); break;
                case MsgId.EntityDespawn: OnEntityDespawn(EntityDespawnMsg.Read(r)); break;
                case MsgId.EntityVars: _varsPacketsIn++; OnEntityVars(EntityVarsMsg.Read(r)); break;
                case MsgId.EntityRpc: _rpcPacketsIn++; OnEntityRpc(EntityRpcMsg.Read(r)); break;
                case MsgId.WorldState: _statePacketsIn++; OnWorldState(r); break;
                case MsgId.EntityState: OnEntityState(EntitySyncMsg.Read(r)); break;
                case MsgId.OwnerState: OnOwnerState(OwnerStateMsg.Read(r)); break;
                default: NebulaLog.Warn($"client got unexpected {id}"); break;
            }
        }

        // ---------------------------------------------------------------------------------------- entities

        private void OnEntitySpawn(EntitySpawnMsg msg)
        {
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
                if (container != e.Container) e.SetContainer(container);
                if (msg.Vars != null && msg.Vars.Length > 0)
                {
                    _reader.Set(new ArraySegment<byte>(msg.Vars));
                    e.ReadVars(_reader);
                }
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
            if (LocalPlayer == e) LocalPlayer = null;
            e.InvokeDespawn();
            EntityDespawned?.Invoke(e);
            if (e.IsSceneEntity) e.Unbind(); // the object belongs to its scene
            else Destroy(e.gameObject);
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
            e.ReadSyncState(_reader, msg.Tick, ContainerRegistry.Resolve(msg.Container) ?? e.Container);
        }

        private void OnOwnerState(OwnerStateMsg msg)
        {
            if (LocalPlayer == null || LocalPlayer.NetId != msg.NetId || LocalPlayer.Predicted == null) return;
            if (msg.Epoch < LocalPlayer.Epoch) return;
            if (msg.InputLead != OwnerStateMsg.NoInputLead) NoteInputLead(msg.InputLead);
            if (msg.Tick == 0) return;
            // The state is in the worker's container frame for that tick; move there first, or a seam crossing
            // would reconcile against the wrong origin for a tick and snap the pawn across the map.
            var container = ContainerRegistry.Resolve(msg.Container);
            if (container == null && msg.Container.MayArriveLater) return; // the container is not here yet; the next report will do
            if (container != LocalPlayer.Container) LocalPlayer.SetContainer(container);
            _reader.Set(new ArraySegment<byte>(msg.State));
            LocalPlayer.Predicted.ClientReconcile(msg.Tick, _reader);
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
                else Destroy(e.gameObject);
            }
            _entities.Clear();
            _pendingScene.Clear();
            _pendingSceneByNetId.Clear();
            _pendingByCarrier.Clear();
            LocalPlayer = null;
            _hasRenderOffset = false;
            if (Join != JoinState.None)
            {
                Join = JoinState.None;
                JoinEstimatedSeconds = 0;
                try { JoinStateChanged?.Invoke(Join, 0); }
                catch (Exception e) { NebulaLog.Error($"JoinStateChanged handler threw: {e}"); }
            }
        }

        public NetworkIdentity Find(ulong netId) => _entities.TryGetValue(netId, out var e) ? e : null;

        // ---------------------------------------------------------------------------------------- IRpcSink

        void IRpcSink.SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, uint targetClientId, float radius)
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
    }
}
