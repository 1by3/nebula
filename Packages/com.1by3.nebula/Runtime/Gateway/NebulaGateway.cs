using System;
using System.Collections.Generic;
using System.Diagnostics;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// Accepts client connections and maintains a connection to every worker. It forwards each client's inputs to the
    /// worker currently owns that client's entity, and fans the workers' replication streams out to the clients,
    /// de-duplicating by authority epoch so a handover is invisible to the client. Nothing here is authoritative:
    /// if the gateway dies, clients reconnect and the workers re-announce their entities.
    /// <para>
    /// Any number of gateways can serve one mesh. Each has a persistent id and an incarnation that changes on every
    /// start; sessions get mesh-wide ids (<see cref="SessionIds"/>) and a signed session token
    /// (<see cref="SessionTokens"/>) with which a client can reconnect through any gateway and keep its pawn. A
    /// gateway asked to drain refuses new clients and tells the ones it has to reconnect elsewhere. It reports its
    /// load on every control-plane heartbeat (<see cref="GatewayStats"/>) for whoever sizes the fleet.
    /// </para>
    /// The CLI runs this routing loop in a standalone .NET executable using exported container geometry.
    /// The Unity component remains available for compatibility and in-process tests.
    /// </summary>
    public sealed partial class NebulaGateway
#if !NEBULA_SERVICE
        : MonoBehaviour
#endif
    {
        private sealed class ClientConn
        {
            public int PeerId;
            /// <summary>The mesh-wide session id (<see cref="SessionIds"/>); the same across a reconnection with a session token.</summary>
            public ulong ClientId;
            /// <summary>The session's connection generation: bumped on every (re)claim, fences stale gateways at the worker.</summary>
            public ulong Generation;
            public string Name = "";
            public bool IsBot;
            public bool Welcomed;
            /// <summary>The session was reclaimed from a token rather than started fresh.</summary>
            public bool Reclaimed;
            /// <summary>The player's identity across sessions (<see cref="PlayerIdentity"/>), set when the Hello's token was accepted.</summary>
            public string Identity = "";
            /// <summary>Hello received; its token is being checked against an OpenID provider's keys.</summary>
            public bool AuthPending;
            /// <summary>Non-zero: the join was refused and the link is dropped at this time (after the rejection has been delivered).</summary>
            public float DisconnectAt;
            public ulong PawnNetId;
            /// <summary>The interest set: exactly the entities this client has replicas of.</summary>
            public readonly HashSet<ulong> Visible = new HashSet<ulong>();
            /// <summary>The set's state machine (hysteresis, linger, authorization). Created on the first evaluation.</summary>
            public ClientInterest<EntityRecord, InterestSource> Interest;
            /// <summary>Reused between evaluations, so a policy that asks for the same things allocates nothing.</summary>
            public InterestQuery Query;
            /// <summary>Regions this client's foci cover at the subscribe radius: what the gateway asks workers for.</summary>
            public readonly HashSet<ulong> Regions = new HashSet<ulong>();
            /// <summary>Scratch for the region diff, kept per client so the diff allocates nothing.</summary>
            public readonly HashSet<ulong> NextRegions = new HashSet<ulong>();
            /// <summary>Per-netId view sequence, so a late despawn of an older view cannot kill a re-entered replica.</summary>
            public readonly Dictionary<ulong, ushort> ViewSeq = new Dictionary<ulong, ushort>();
            /// <summary>Container rows this client has been sent, so ownership travels as a delta and not as the lease table.</summary>
            public readonly HashSet<string> KnownContainers = new HashSet<string>();
            /// <summary>An interest limit this client ran into (foci, box size, container rows) has been reported; said once, not four times a second.</summary>
            public bool InterestLimitWarned;
            /// <summary>Evaluate at the next tick rather than waiting for this client's turn in <see cref="InterestSchedule"/> (pawn, instance, carrier, focus mode, focus region or policy changed).</summary>
            public bool InterestDirty = true;
            /// <summary>A game-set tag a policy filters on (<see cref="NebulaGateway.SetClientTag"/>).</summary>
            public byte Team;
            /// <summary>Game-set flags a policy filters on (<see cref="NebulaGateway.SetClientTags"/>).</summary>
            public ulong Tags;
            /// <summary>What the server lets this client's focus hint do (<see cref="NebulaGateway.SetClientFocusMode"/>). Never set by the client.</summary>
            public FocusMode FocusMode = FocusMode.PawnClamped;
            /// <summary>The client's validated focus hint in absolute world coordinates, if it has sent one (<see cref="FocusHintFilter"/>).</summary>
            public bool HasHint;
            public double HintX, HintY, HintZ;
            /// <summary>The newest hint generation seen (<see cref="ClientFocusHintMsg.Generation"/>); older hints are late arrivals.</summary>
            public byte HintGeneration;
            /// <summary>False until the first hint message: a reconnecting client's counter does not restart at zero.</summary>
            public bool HintGenerationKnown;
            /// <summary>Bytes sent to this client since the last heartbeat, for the per-client figure in <see cref="GatewayStats"/>.</summary>
            public long BytesOut;
            /// <summary>What the client was last told about its join (<see cref="JoinStatusMsg"/>); only changes are sent.</summary>
            public JoinState Join;
            public ushort JoinEstimate;
            public float NextSpawnAttempt;
            /// <summary>
            /// When the gateway first found itself unable to place this client's pawn (no record, or a record
            /// whose owner it cannot reach). Zero while all is well. Interest subscribes the pawn by name on
            /// every live worker while it is set, and gives up and asks for a new pawn if nobody answers.
            /// </summary>
            public double PawnLostSince;
            /// <summary>Whether the by-name recovery above is running, so it is announced once and not four times a second.</summary>
            public bool PawnRecovering;
            public string SpawnWorkerId = "";
            public ContainerRef SpawnContainer = ContainerRef.None;
            public string CoordinationClaim = "";
            public Action RetryCoordination;
            public bool CoordinationInFlight;
            public double CoordinationDeadline, NextCoordination;
            /// <summary>World-state entries filtered for this client, coalesced across a worker's batches of one tick (see FlushWorldState).</summary>
            public NetworkWriter Pending;
            public int PendingSlot = -1;
            public ushort PendingCount;
            public uint PendingTick;
            public ushort PendingWorker;
            /// <summary>Reliable messages waiting to go out as one MsgId.Batch packet (see FlushReliable).</summary>
            public NetworkWriter Reliable;
            public int ReliableSlot = -1;
            public ushort ReliableCount;
        }

        /// <summary>Flush a client's reliable batch once it holds this many bytes (ReliableOrdered fragments above the MTU, so this is about latency, not size).</summary>
        private const int ReliableBatchBytes = 1100;
        /// <summary>How long a session token stays valid. The worker's reclaim grace (<see cref="NebulaConfig.SessionReclaimSeconds"/>) is what decides whether the pawn is still there.</summary>
        public const long SessionTokenLifetimeSeconds = 24 * 3600;

        private sealed class WorkerConn
        {
            public int PeerId;
            public string WorkerId = "";
            public ushort Index;
            public uint Incarnation;
            public bool Ready;
            public bool Outbound;
        }

        private sealed class EntityRecord
        {
            public ulong NetId;
            public uint Epoch;
            public ushort OwnerWorkerIndex;
            /// <summary>
            /// Set while <see cref="OwnerWorkerIndex"/> is only what an <see cref="EntityRedirectMsg"/> said and
            /// no spawn from that worker has confirmed it. A chain of handovers can outrun the gateway's dialling
            /// (the second redirect is sent over a link the gateway does not have yet), which leaves the record
            /// pointing at a worker that no longer owns the entity. For a client's own pawn that is fatal, so
            /// interest recovers it by name until somebody answers (see <see cref="NebulaGateway.RecoverPawn"/>).
            /// </summary>
            public bool OwnerUnconfirmed;
            public ulong OwnerClientId;
            /// <summary>The container of the newest pose (static index, or a carrier's net id for a dynamic container).</summary>
            public ContainerRef Container;
            public EntitySpawnMsg LastSpawn;
            public uint LastStateTick;
            public bool HasStateTick;
            /// <summary>
            /// Absolute world position, cached and refreshed only when a pose or container changes. Interest asks
            /// for it several times per client per second; resolving the container chain each time would put the
            /// cost back into the loop interest management exists to take it out of.
            /// </summary>
            public double AbsX, AbsY, AbsZ;
            /// <summary>
            /// The region this record is bucketed in (0 when it is wide or global). A cache of
            /// <c>InterestIndex.TryGetPlacement</c>, so for a carried entity it is the <b>root carrier's</b>
            /// region and not one derived from the entity's own prefab settings.
            /// </summary>
            public ulong Region;
            /// <summary>Where the index actually holds it; see <see cref="Region"/> for what "actually" means.</summary>
            public InterestPlacement Placement;
            /// <summary>From the spawn message, clamped by the gateway's <see cref="InterestSettings.MaxRadius"/>.</summary>
            public float RelevanceRadius;
            public bool AlwaysRelevant;
            public byte InterestGroup;
            /// <summary>The clients that hold a replica: the exact audience of every message about this entity.</summary>
            public readonly List<ClientConn> Observers = new List<ClientConn>();
            /// <summary>Newest keyframe per behaviour index, assembled into LastSpawn.State for late joiners.</summary>
            public Dictionary<byte, byte[]> SyncKeyframes;

            public void SeedKeyframes(byte[] state)
            {
                SyncKeyframes = null;
                if (state == null || state.Length == 0) return;
                var r = new NetworkReader(state);
                SyncStateCodec.ReadEnvelope(r, (index, flags, chunk) => StoreKeyframe(index, chunk));
            }

            public void StoreKeyframe(byte index, ArraySegment<byte> chunk)
            {
                if (SyncKeyframes == null) SyncKeyframes = new Dictionary<byte, byte[]>();
                var copy = new byte[chunk.Count];
                Buffer.BlockCopy(chunk.Array, chunk.Offset, copy, 0, chunk.Count);
                SyncKeyframes[index] = copy;
            }

            public void RefreshSpawnState(NetworkWriter scratch)
            {
                if (SyncKeyframes == null) return;
                scratch.Reset();
                int at = SyncStateCodec.BeginEnvelope(scratch);
                byte n = 0;
                foreach (var kv in SyncKeyframes)
                {
                    SyncStateCodec.WriteRawChunk(scratch, kv.Key, SyncStateCodec.ChunkFlags.Full, new ArraySegment<byte>(kv.Value));
                    n++;
                }
                SyncStateCodec.EndEnvelope(scratch, at, n);
                LastSpawn.State = scratch.ToArray();
            }
        }

        public NebulaConfig Config { get; private set; }
        public IControlPlane ControlPlane { get; private set; }
        /// <summary>The gateway's persistent id (<c>-nebula-gateway-id</c>, "gw1" by default). Unique per gateway of a mesh.</summary>
        public string GatewayId { get; private set; }
        /// <summary>This start of the process (<see cref="SessionIds.NewIncarnation"/>): part of every session id it issues, and reported to workers and the control plane.</summary>
        public uint Incarnation { get; private set; }
        public int ClientCount => _clientsById.Count;
        /// <summary>
        /// Welcomed clients with nowhere to spawn yet: the mesh has no worker holding an active lease, so they are
        /// held in <see cref="JoinState.Starting"/> rather than rejected. Reported on every control-plane heartbeat;
        /// the orchestrator treats it as demand and wakes a mesh that has scaled to zero.
        /// </summary>
        public int PendingJoinCount
        {
            get
            {
                int n = 0;
                foreach (var c in _clientsById.Values) if (c.Welcomed && c.PawnNetId == 0) n++;
                return n;
            }
        }
        public int WorkerCount => _workersById.Count;
        public int EntityCount => _entities.Count;
        /// <summary>Registered with the control plane, connected to it, and not draining: fit to take clients.</summary>
        public bool IsReady => _registered && ControlPlane != null && ControlPlane.IsConnected && !Draining;
        /// <summary>Taking itself out of service: new clients are refused, existing ones were told to reconnect elsewhere.</summary>
        public bool Draining { get; private set; }
        /// <summary>
        /// The loop period the host runs <see cref="Tick"/> at, for the loop-lag figure in <see cref="GatewayStats"/>.
        /// A gap between two ticks longer than this counts as lag. Unity: one frame; the standalone service sets its own.
        /// </summary>
        public double LoopPeriodSeconds { get; set; } = 1.0 / 60;
        /// <summary>What was last reported to the control plane (<see cref="GatewayStats"/>).</summary>
        public GatewayStats LastStats => _lastStats;

        private ITransport _transport;
        private readonly Dictionary<int, ClientConn> _clientsByPeer = new Dictionary<int, ClientConn>();
        private readonly Dictionary<ulong, ClientConn> _clientsById = new Dictionary<ulong, ClientConn>();
        private readonly Dictionary<int, WorkerConn> _workersByPeer = new Dictionary<int, WorkerConn>();
        private readonly Dictionary<string, WorkerConn> _workersById = new Dictionary<string, WorkerConn>();
        private readonly Dictionary<ushort, WorkerConn> _workersByIndex = new Dictionary<ushort, WorkerConn>();
        private readonly HashSet<string> _dialing = new HashSet<string>();
        private readonly Dictionary<ulong, EntityRecord> _entities = new Dictionary<ulong, EntityRecord>();
        private readonly List<ContainerOwnershipEntry> _ownership = new List<ContainerOwnershipEntry>();
        private readonly List<ulong> _scratchIds = new List<ulong>();
        /// <summary>Sessions whose link dropped recently, with when: counted as reconnecting until the worker's grace has passed.</summary>
        private readonly List<KeyValuePair<ulong, float>> _recentlyLost = new List<KeyValuePair<ulong, float>>();
        /// <summary>
        /// Peers of clients whose session has moved on (<see cref="EndPlayerLink"/>): already forgotten, so nothing
        /// is despawned on their behalf, and closed at the given time so the notice has gone out first.
        /// </summary>
        private readonly List<KeyValuePair<int, float>> _closingPeers = new List<KeyValuePair<int, float>>();

        private readonly NetworkWriter _writer = new NetworkWriter(4096);
        private readonly NetworkReader _reader = new NetworkReader();
        private readonly NetworkWriter _scratch = new NetworkWriter(1024);

        /// <summary>
        /// The static or runtime container a reference ultimately sits in, walking out through every dynamic
        /// carrier. This is the simulation scope instance isolation is decided on, so the walk has no depth cap
        /// through every carrier: a crate deep inside a ship inside a private instance must resolve to that instance and
        /// not to whatever an exhausted walk happened to be holding. The bound is the number of records the
        /// gateway holds — container references come off the wire, and a chain longer than that has revisited
        /// one, which resolves to null and fails closed in <see cref="CanObserve"/>.
        /// </summary>
        private Container ScopeContainer(ContainerRef reference)
        {
            for (int hops = 0; reference.IsDynamic; hops++)
            {
                if (hops > _entities.Count || !_entities.TryGetValue(reference.NetId, out var carrier)) return null;
                reference = carrier.Container;
            }
            return ContainerRegistry.Resolve(reference);
        }

        private bool CanObserve(ClientConn client, EntityRecord entity)
        {
            if (!client.Welcomed) return false;
            if (entity.NetId == client.PawnNetId) return true;
            var target = ScopeContainer(entity.Container);
            // Unknown runtime containers must never fall back to public visibility.
            if (target == null && !entity.Container.IsNone) return false;
            var source = _entities.TryGetValue(client.PawnNetId, out var pawn) ? ScopeContainer(pawn.Container) : null;
            ulong scope = source?.InstanceId ?? 0;
            if ((target?.InstanceId ?? 0) == scope) return true;
            if (target != null && target.InstanceId != 0) return false;
            var view = source?.Instance;
            return view != null && view.ObservePublic && new Bounds(view.ObservationCenter, view.ObservationSize)
                .Contains(WorldPosition(entity.Container, entity.LastSpawn.LocalPosition));
        }

        /// <summary>
        /// Re-evaluate one client's whole interest set now. Interest normally runs on its own schedule
        /// (<see cref="TickInterest"/>); this is the immediate path for the things that invalidate a set outright
        /// — the pawn moved instance, a carrier changed, a test wants the answer without pumping ticks.
        /// </summary>
        private void ReconcileView(ClientConn client)
        {
            if (!client.Welcomed) return;
            EvaluateClient(client, InterestNow);
        }

        /// <summary>
        /// Send what is already in <see cref="_writer"/> to the clients that hold a replica of this entity. Every
        /// message about an entity goes through here or through <see cref="EntityRecord.Observers"/> directly, so
        /// per-tick cost is Σ(entries × observers) — what is near the players — and never clients × world.
        /// </summary>
        private void BroadcastEntity(EntityRecord entity, Delivery delivery)
        {
            var segment = _writer.ToSegment();
            for (int i = entity.Observers.Count - 1; i >= 0; i--)
            {
                var client = entity.Observers[i];
                if (!client.Welcomed) continue;
                if (delivery == Delivery.ReliableOrdered) AppendReliable(client, segment);
                else Send(client.PeerId, delivery, segment);
            }
        }
        private uint _nextSequence = 1;
        private bool _registered;
        private float _nextHeartbeat;
        private OidcTokenValidator _oidc;
        private AnonymousIdentityIssuer _anonymous;
        private SessionTokens _sessions;
        private byte[] _peerKey;

        // Load figures for the heartbeat (GatewayStats): traffic counted per direction and per side, CPU from the
        // process clock, loop lag from a stopwatch around Tick.
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTickAt = -1, _statsSince;
        private long _clientPacketsIn, _clientPacketsOut, _clientBytesIn, _clientBytesOut, _workerBytesIn, _workerBytesOut;
        private long _maxClientBytesOut;
        private float _maxLoopLagMs;
        private TimeSpan _lastCpu;
        private GatewayStats _lastStats;

        /// <summary>
        /// The OpenID token checker built from <see cref="NebulaConfig.AuthIssuers"/>, or null when the mesh trusts no
        /// provider. Replace it (before the first client) to supply keys by hand or a different fetcher.
        /// </summary>
        public OidcTokenValidator TokenValidator { get => _oidc; set => _oidc = value; }
        /// <summary>Issues and checks anonymous identities, or null when <see cref="NebulaConfig.AuthAnonymous"/> is off.</summary>
        public AnonymousIdentityIssuer AnonymousIdentities => _anonymous;
        /// <summary>Issues and checks the session tokens clients reconnect with.</summary>
        public SessionTokens Sessions => _sessions;

        /// <param name="browserTransport">A second transport clients arrive on, already listening: the standalone
        /// gateway's WebRTC listener for web builds. Null accepts UDP clients only.</param>
        /// <param name="gatewayId">This gateway's id; null reads <c>-nebula-gateway-id</c> ("gw1" by default).</param>
        public void Initialize(NebulaConfig config, IControlPlane controlPlane, ITransport browserTransport = null, string gatewayId = null)
        {
            Config = config;
            ControlPlane = controlPlane;
            GatewayId = gatewayId ?? CommandLine.Get("nebula-gateway-id", "gw1");
            Incarnation = SessionIds.NewIncarnation();
            _peerKey = string.IsNullOrEmpty(config.MeshToken) ? null : MeshPeerAuth.DeriveKey(config.MeshToken);
            var udp = new LiteNetTransport("gateway");
            udp.Listen(config.GatewayPort);
            _transport = browserTransport != null ? new MultiTransport(udp, browserTransport) : (ITransport)udp;
            InitializeInterest();
            ControlPlane.Changed += OnControlPlaneChanged;
            NebulaLog.Info($"gateway {GatewayId} (incarnation {Incarnation:x8}) listening on udp/{config.GatewayPort}" + (_peerKey == null ? "; no mesh token: any worker is trusted" : ""));
            InitializeAuth(config);
            _statsSince = _clock.Elapsed.TotalSeconds;
            _lastCpu = ProcessorTime();
        }

        /// <summary>
        /// Who may join and how they are identified: tokens from the configured OpenID providers, anonymous
        /// identities the gateway issues itself, or both (the default: anonymous only, since no issuer is configured).
        /// The same player key also signs session tokens, so one secret shared by every gateway covers both.
        /// </summary>
        private void InitializeAuth(NebulaConfig config)
        {
            var issuers = OidcTokenValidator.ParseIssuerList(config.AuthIssuers);
            if (issuers.Count > 0)
            {
                _oidc = new OidcTokenValidator(issuers, config.AuthAudience);
                NebulaLog.Info($"auth: accepting ID tokens from {string.Join(", ", issuers)}" + (string.IsNullOrEmpty(config.AuthAudience) ? " (no audience check: set AuthAudience to your client id)" : $" for audience '{config.AuthAudience}'"));
            }
            byte[] key;
            string source;
            if (!string.IsNullOrEmpty(config.AuthSigningKey)) { key = AnonymousIdentityIssuer.DeriveKey(config.AuthSigningKey); source = "AuthSigningKey"; }
            else if (!string.IsNullOrEmpty(config.MeshToken))
            {
                key = AnonymousIdentityIssuer.DeriveKey(config.MeshToken);
                source = "the mesh token";
                NebulaLog.Warn("auth: the player signing key is derived from the mesh token, so anonymous identities and session tokens change whenever the mesh token does; set AuthSigningKey (NEBULA_AUTH_KEY) to a secret of its own");
            }
            else
            {
#if NEBULA_SERVICE
                string path = System.IO.Path.Combine(AppContext.BaseDirectory, "nebula-auth.key");
#else
                string path = System.IO.Path.Combine(Application.persistentDataPath, "nebula-auth.key");
#endif
                key = AnonymousIdentityIssuer.LoadOrCreateKeyFile(path);
                source = path;
            }
            _sessions = new SessionTokens(SessionTokens.DeriveKey(key));
            if (config.AuthAnonymous)
            {
                _anonymous = new AnonymousIdentityIssuer(key);
                NebulaLog.Info($"auth: anonymous identities on, signing key from {source}");
            }
            else if (_oidc == null) NebulaLog.Warn("auth: anonymous identities are off and no AuthIssuers are configured: no client can join");
            else NebulaLog.Info($"auth: anonymous identities off; every client needs an ID token (session key from {source})");
        }

#if NEBULA_SERVICE
        public void Dispose()
#else
        private void OnDestroy()
#endif
        {
            if (ControlPlane != null)
            {
                ControlPlane.Changed -= OnControlPlaneChanged;
                if (_registered && ControlPlane.IsConnected)
                {
                    try { ControlPlane.UnregisterGateway(GatewayId); } catch (Exception e) { NebulaLog.Warn($"unregister failed: {e.Message}"); }
                }
            }
            _transport?.Dispose();
            if (SessionCoordinator != null)
                foreach (var client in _sessionClients.Values)
                    SessionCoordinator.SessionRequest(SessionRequestFor(client, "release"), _ => { });
            _oidc?.Dispose();
        }

#if NEBULA_SERVICE
        public void Tick()
#else
        private void Update()
#endif
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (_lastTickAt >= 0)
            {
                float lag = (float)((now - _lastTickAt - LoopPeriodSeconds) * 1000);
                if (lag > _maxLoopLagMs) _maxLoopLagMs = lag;
            }
            _lastTickAt = now;

            _transport.Poll(HandleTransportEvent);
            _oidc?.Tick();
            TickInterest();
            foreach (var c in _clientsById.Values) { FlushWorldState(c); FlushReliable(c); }
            _transport.Flush();
            ReportWorldStateStats();
            DropRejectedClients();
            CloseReplacedLinks();
            TickSessionCoordination();

            if (!_registered && ControlPlane.IsConnected)
            {
                ControlPlane.RegisterGateway(GatewayId, Config.GatewayAddress, Config.GatewayPort, Incarnation);
                _registered = true;
            }
            if (_registered && Time.unscaledTime >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.unscaledTime + Config.WorkerHeartbeatSeconds;
                ControlPlane.HeartbeatGateway(GatewayId, CollectStats());
            }

            // Players without a pawn get one as soon as a worker is available.
            foreach (var c in _clientsById.Values)
            {
                if (!c.Welcomed || c.DisconnectAt != 0 || c.PawnNetId != 0 || Time.unscaledTime < c.NextSpawnAttempt) continue;
                TryRequestSpawn(c);
            }
        }

        // ---------------------------------------------------------------------------------------- load report

        private static TimeSpan ProcessorTime()
        {
            try { return Process.GetCurrentProcess().TotalProcessorTime; } catch { return TimeSpan.Zero; }
        }

        private static ulong WorkingSet()
        {
            try { return (ulong)Math.Max(0L, Process.GetCurrentProcess().WorkingSet64); } catch { return 0; }
        }

        /// <summary>
        /// Exceptions thrown by a game extension running in this gateway, since the process started: policy
        /// calls, client events, posted work. It is a total and not a rate, because the number that matters is
        /// "is it still zero". Set by <c>GatewayExtensionHost</c>; reported as <see cref="GatewayStats.ExtensionErrors"/>.
        /// </summary>
        public uint ExtensionErrors;

        /// <summary>The numbers for one heartbeat: rates since the previous one, then the counters start over.</summary>
        private GatewayStats CollectStats()
        {
            double now = _clock.Elapsed.TotalSeconds;
            double interval = Math.Max(1e-3, now - _statsSince);
            var cpu = ProcessorTime();
            uint active = 0, joining = 0;
            foreach (var c in _clientsById.Values)
            {
                if (c.Welcomed && c.PawnNetId != 0) active++;
                else if (!c.Welcomed && c.DisconnectAt == 0) joining++;
            }
            _recentlyLost.RemoveAll(kv => Time.unscaledTime - kv.Value > Config.SessionReclaimSeconds);
            uint workers = 0;
            foreach (var w in _workersById.Values) if (w.Ready) workers++;
            var stats = new GatewayStats
            {
                PendingJoins = (uint)PendingJoinCount,
                ActiveClients = active,
                JoiningClients = joining,
                ReconnectingClients = (uint)_recentlyLost.Count,
                PacketsInPerSecond = (float)(_clientPacketsIn / interval),
                PacketsOutPerSecond = (float)(_clientPacketsOut / interval),
                BytesInPerSecond = (float)(_clientBytesIn / interval),
                BytesOutPerSecond = (float)(_clientBytesOut / interval),
                WorkerBytesInPerSecond = (float)(_workerBytesIn / interval),
                WorkerBytesOutPerSecond = (float)(_workerBytesOut / interval),
                Cpu = (float)Math.Max(0.0, (cpu - _lastCpu).TotalSeconds / interval),
                MemoryBytes = WorkingSet(),
                LoopLagMs = Math.Max(0f, _maxLoopLagMs),
                WorkerConnections = workers,
                Ready = IsReady,
                Draining = Draining,
                ExtensionErrors = ExtensionErrors,
            };
            FillInterestStats(ref stats, interval);
            foreach (var c in _clientsById.Values) c.BytesOut = 0;
            _maxClientBytesOut = 0;
            _statsSince = now;
            _lastCpu = cpu;
            _clientPacketsIn = _clientPacketsOut = _clientBytesIn = _clientBytesOut = _workerBytesIn = _workerBytesOut = 0;
            _maxLoopLagMs = 0;
            _lastStats = stats;
            return stats;
        }

        /// <summary>Every send goes through here so the heartbeat can say how much left for clients and for workers.</summary>
        private void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
        {
            if (_workersByPeer.ContainsKey(peerId)) _workerBytesOut += payload.Count;
            else
            {
                _clientPacketsOut++;
                _clientBytesOut += payload.Count;
                // Per-client bytes is the figure that says whether one client is being sent the world; the total
                // divided by the client count hides exactly the case worth catching.
                if (_clientsByPeer.TryGetValue(peerId, out var to))
                {
                    to.BytesOut += payload.Count;
                    if (to.BytesOut > _maxClientBytesOut) _maxClientBytesOut = to.BytesOut;
                }
            }
            _transport.Send(peerId, delivery, payload);
        }

        // ---------------------------------------------------------------------------------------- draining

        /// <summary>
        /// Take this gateway out of service: refuse new clients and tell the connected ones to reconnect within
        /// <see cref="NebulaConfig.GatewayDrainReconnectSeconds"/> (a load balancer hands them to another gateway,
        /// where their session token gets them their pawn back). Called when the control plane carries a drain
        /// request for this gateway (<see cref="IControlPlane.SetGatewayDraining"/>), or directly.
        /// </summary>
        public void Drain()
        {
            if (Draining) return;
            Draining = true;
            ushort within = (ushort)Mathf.Clamp(Mathf.RoundToInt(Config.GatewayDrainReconnectSeconds), 1, ushort.MaxValue);
            NebulaLog.Warn($"gateway {GatewayId} draining: {_clientsById.Count} client(s) told to reconnect within {within} s");
            _writer.Reset();
            new GatewayDrainingMsg { ReconnectWithinSeconds = within }.Write(_writer);
            _toDrop.Clear();
            foreach (var c in _clientsById.Values)
            {
                if (c.Welcomed) AppendReliable(c, _writer.ToSegment());
                else if (c.DisconnectAt == 0) _toDrop.Add(c);
            }
            foreach (var c in _toDrop) Reject(c, "gateway is draining", true);
        }

        /// <summary>Cancel a drain (the fleet changed its mind): new clients are accepted again.</summary>
        public void StopDraining()
        {
            if (!Draining) return;
            Draining = false;
            NebulaLog.Info($"gateway {GatewayId} back in service");
        }

        // ---------------------------------------------------------------------------------------- control plane

        private void OnControlPlaneChanged()
        {
            var self = ControlPlane.FindGateway(GatewayId);
            if (_registered && self == null && ControlPlane.IsConnected)
            {
                // The orchestrator forgot us (it restarted with a reset, or pruned the row while the link was down):
                // register again, or the fleet never sees this gateway's clients and demand.
                NebulaLog.Warn($"gateway {GatewayId} is no longer on the control plane; registering again");
                ControlPlane.RegisterGateway(GatewayId, Config.GatewayAddress, Config.GatewayPort, Incarnation);
                _nextHeartbeat = 0;
            }
            if (self != null && (self.Incarnation == 0 || self.Incarnation == Incarnation))
            {
                if (self.DrainRequested && !Draining) Drain();
                else if (!self.DrainRequested && Draining) StopDraining();
            }

            ContainerRegistry.SyncRuntime(ControlPlane.Leases);
            _ownership.Clear();
            _ownershipById.Clear();
            foreach (var lease in ControlPlane.Leases)
            {
                // Dynamic containers have no registry entry here (the gateway holds no entities); their leases are
                // still relayed so clients that do hold the carrier can show who is pinned to it.
                var c = ContainerRegistry.FindById(lease.ContainerId);
                bool dynamic = c == null && ContainerRegistry.IsDynamicId(lease.ContainerId);
                if (c == null && !dynamic) continue;
                var w = ControlPlane.FindWorker(lease.WorkerId);
                ushort idx = w != null ? (ushort)w.WorkerIndex : ushort.MaxValue;
                string owner = LeaseState.IsOwning(lease.State) ? lease.WorkerId : "";
                if (c != null) ContainerRegistry.ApplyLease(lease.ContainerId, owner, idx, lease.Epoch, lease.State);
                var entry = new ContainerOwnershipEntry
                {
                    ContainerIndex = c != null ? c.Index : ContainerRef.DynamicIndex,
                    ContainerId = lease.ContainerId,
                    WorkerIndex = idx,
                    WorkerId = owner,
                    Epoch = lease.Epoch,
                    State = lease.State,
                    HasBounds = lease.HasBounds,
                    BoundsCenter = lease.BoundsCenter,
                    BoundsSize = lease.BoundsSize,
                    Instance = lease.Instance,
                };
                _ownership.Add(entry);
                if (!string.IsNullOrEmpty(entry.ContainerId)) _ownershipById[entry.ContainerId] = entry;
            }
            // Ownership is a per-client delta now (design §8): only the rows a client already holds are refreshed,
            // and a client learns of a new container when something it can see needs it.
            RefreshOwnership();
            // Leases moved, so a region may belong to a different worker: re-resolve, and let the subscription
            // pass link the new owner before the link linger lets the old one go.
            _regionWorkers.Clear();
            _subscriptionsDirty = true;
        }

        // ---------------------------------------------------------------------------------------- transport

        private void HandleTransportEvent(TransportEvent ev)
        {
            switch (ev.Type)
            {
                case TransportEvent.Kind.Connected:
                {
                    if (_workersByPeer.TryGetValue(ev.PeerId, out var w))
                    {
                        _writer.Reset();
                        new HelloMsg { Role = PeerRole.Gateway, Id = GatewayId, Index = 0, Incarnation = Incarnation, Token = MeshPeerAuth.Issue(Config.MeshToken, PeerRole.Gateway, GatewayId, Incarnation) }.Write(_writer);
                        Send(ev.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
                    }
                    // Inbound links are clients until proven otherwise; they must send Hello first.
                    break;
                }
                case TransportEvent.Kind.Disconnected:
                {
                    if (_workersByPeer.TryGetValue(ev.PeerId, out var w)) OnWorkerLost(w);
                    else if (_clientsByPeer.TryGetValue(ev.PeerId, out var c)) OnClientLost(c);
                    break;
                }
                case TransportEvent.Kind.Data:
                {
                    _reader.Set(ev.Data);
                    try
                    {
                        if (_workersByPeer.TryGetValue(ev.PeerId, out var w)) { _workerBytesIn += ev.Data.Count; DispatchWorker(w, _reader); }
                        else { _clientPacketsIn++; _clientBytesIn += ev.Data.Count; DispatchClient(ev.PeerId, _reader); }
                    }
                    catch (Exception e) { NebulaLog.Error($"bad packet from peer {ev.PeerId}: {e}"); }
                    break;
                }
            }
        }

        // ---------------------------------------------------------------------------------------- workers

        /// <summary>A worker's Hello must carry a credential minted with the mesh token (when the mesh has one).</summary>
        private bool AcceptWorkerHello(int peerId, in HelloMsg hello)
        {
            if (_peerKey == null) return true;
            if (MeshPeerAuth.Verify(_peerKey, PeerRole.Worker, hello.Id, hello.Incarnation, hello.Token, JsonWebToken.UnixNow(), out string error)) return true;
            NebulaLog.Warn($"worker '{hello.Id}' refused: {error}");
            _transport.Disconnect(peerId);
            return false;
        }

        private void DispatchWorker(WorkerConn w, NetworkReader r)
        {
            var id = (MsgId)r.ReadByte();
            if (id == MsgId.Hello)
            {
                var hello = HelloMsg.Read(r);
                if (hello.Role != PeerRole.Worker || !AcceptWorkerHello(w.PeerId, hello)) return;
                w.WorkerId = hello.Id;
                w.Index = (ushort)hello.Index;
                w.Incarnation = hello.Incarnation;
                w.Ready = true;
                _workersById[w.WorkerId] = w;
                _workersByIndex[w.Index] = w;
                _dialing.Remove(w.WorkerId);
                NebulaLog.Info($"worker {w.WorkerId} (index {w.Index}) connected");
                // A worker announces nothing on our Hello any more (design §5): what we hear about is exactly
                // what we subscribe, so the first thing over a new link is our subscription.
                if (_links.TryGetValue(w.WorkerId, out var link)) { link.Index = w.Index; link.Linked = false; FlushSubscription(link); }
                return;
            }
            if (!w.Ready) return;
            switch (id)
            {
                case MsgId.EntitySpawn: OnEntitySpawn(w, EntitySpawnMsg.Read(r)); break;
                case MsgId.InstancePrepare:
                {
                    var preparation = InstancePreparationMsg.Read(r);
                    if (!_entities.TryGetValue(preparation.EntityId, out var pawn) || pawn.OwnerWorkerIndex != w.Index ||
                        !_clientsById.TryGetValue(pawn.OwnerClientId, out var client) || client.PawnNetId != pawn.NetId) break;
                    preparation.SourceWorker = w.Index;
                    _writer.Reset(); preparation.Write(_writer, MsgId.InstancePrepare);
                    AppendReliable(client, _writer.ToSegment());
                    break;
                }
                case MsgId.EntityDespawn: OnEntityDespawn(w, EntityDespawnMsg.Read(r)); break;
                case MsgId.EntityVars: OnEntityVars(w, EntityVarsMsg.Read(r), r); break;
                case MsgId.EntityRpc: OnEntityRpc(w, r); break;
                case MsgId.WorldState: OnWorldState(w, r); break;
                case MsgId.EntityState: OnEntityState(w, EntitySyncMsg.Read(r)); break;
                case MsgId.OwnerState: OnOwnerState(w, r); break;
                case MsgId.EndSession: OnEndSession(EndSessionMsg.Read(r)); break;
                case MsgId.InterestResync: OnInterestResync(w, InterestResyncMsg.Read(r)); break;
                case MsgId.EntityForget: OnEntityForget(w, EntityForgetMsg.Read(r)); break;
                case MsgId.EntityRedirect: OnEntityRedirect(w, EntityRedirectMsg.Read(r)); break;
                default: NebulaLog.Warn($"gateway got unexpected {id} from worker {w.WorkerId}"); break;
            }
        }

        private void OnWorkerLost(WorkerConn w)
        {
            _workersByPeer.Remove(w.PeerId);
            if (!string.IsNullOrEmpty(w.WorkerId))
            {
                _workersById.Remove(w.WorkerId);
                _workersByIndex.Remove(w.Index);
                _dialing.Remove(w.WorkerId);
            }
            NebulaLog.Warn($"worker {w.WorkerId} disconnected; dropping its entities");
            _scratchIds.Clear();
            foreach (var rec in _entities.Values) if (rec.OwnerWorkerIndex == w.Index) _scratchIds.Add(rec.NetId);
            foreach (var netId in _scratchIds)
            {
                var rec = _entities[netId];
                ulong owner = rec.OwnerClientId;
                ForgetEntity(netId);
                if (owner != 0 && _clientsById.TryGetValue(owner, out var c) && c.PawnNetId == netId)
                {
                    c.PawnNetId = 0;
                    c.InterestDirty = true;
                    SendJoinStatus(c, JoinState.Starting);
                    c.NextSpawnAttempt = Time.unscaledTime + 1f; // give the orchestrator a moment to reassign
                }
            }
            // The link is gone, so whatever the worker believed about our subscription is gone with it; the
            // replacement (or the same worker coming back) gets a Full snapshot on its next Hello.
            if (!string.IsNullOrEmpty(w.WorkerId) && _links.TryGetValue(w.WorkerId, out var link)) { link.Linked = false; link.Sub.Reset(); }
            _regionWorkers.Clear();
            _subscriptionsDirty = true;
        }

        private void OnEntitySpawn(WorkerConn w, EntitySpawnMsg msg)
        {
            if (_entities.TryGetValue(msg.NetId, out var rec))
            {
                if (msg.Epoch < rec.Epoch) return;
            }
            else
            {
                rec = new EntityRecord { NetId = msg.NetId };
                _entities[msg.NetId] = rec;
            }
            bool containerChanged = rec.Container != msg.Container;
            rec.Epoch = msg.Epoch;
            rec.OwnerWorkerIndex = w.Index;
            // A spawn from a worker is the one thing that confirms who owns an entity; a redirect only promised.
            rec.OwnerUnconfirmed = false;
            rec.OwnerClientId = msg.OwnerClientId;
            rec.Container = msg.Container;
            msg.OwnerWorkerIndex = w.Index;
            rec.LastSpawn = msg;
            rec.HasStateTick = false;
            rec.SeedKeyframes(msg.State);
            // The prefab's interest facts travel in the spawn (the standalone gateway has no prefabs) and are
            // clamped here: a prefab may not reach further than this mesh's InterestMaxRadius, which
            // InterestSettings.Validate guarantees is a real ceiling (>= InterestRadius > 0) and never "off".
            rec.RelevanceRadius = Math.Min(msg.RelevanceRadius, _interest.MaxRadius);
            rec.AlwaysRelevant = (msg.InterestFlags & EntityInterestFlags.AlwaysRelevant) != 0;
            rec.InterestGroup = msg.InterestGroup;
            IndexEntity(rec);
            if (msg.OwnerClientId != 0 && _clientsById.TryGetValue(msg.OwnerClientId, out var c))
            {
                c.PawnNetId = msg.NetId;
                c.InterestDirty = true;
                SendJoinStatus(c, JoinState.Joined);
            }
            // The observers it already has are told about the new state in place; everyone else learns of it only
            // if it is near them, which is one test per client whose focus regions cover its region.
            if (rec.Observers.Count > 0)
            {
                // A handover announcement is also how an entity's container changes (the new owner names the
                // cell it landed in). The row has to be there before the spawn that names it, or the client
                // cannot resolve the frame and holds the entity where it was - for its own pawn, for ever.
                if (containerChanged) SendOwnershipForContainerChange(rec);
                _writer.Reset();
                msg.Write(_writer, MsgId.EntitySpawn);
                BroadcastEntity(rec, Delivery.ReliableOrdered);
            }
            // A carrier can arrive after the passengers that named it: indexing it has just reseated them all,
            // so they are offered to the clients of its region here and not left waiting for a state entry.
            ConsiderCarried(rec);
        }

        private void OnEntityDespawn(WorkerConn w, EntityDespawnMsg msg)
        {
            if (!_entities.TryGetValue(msg.NetId, out var rec)) return;
            if (msg.Epoch < rec.Epoch || rec.OwnerWorkerIndex != w.Index) return;
            if (rec.OwnerClientId != 0 && _clientsById.TryGetValue(rec.OwnerClientId, out var c) && c.PawnNetId == msg.NetId)
            {
                c.PawnNetId = 0;
                c.InterestDirty = true;
                c.NextSpawnAttempt = Time.unscaledTime + 0.5f;
            }
            ForgetEntity(msg.NetId);
        }

        private void OnEntityVars(WorkerConn w, EntityVarsMsg msg, NetworkReader r)
        {
            if (!_entities.TryGetValue(msg.NetId, out var rec) || msg.Epoch < rec.Epoch || rec.OwnerWorkerIndex != w.Index) return;
            rec.LastSpawn.Vars = msg.Vars;
            rec.LastSpawn.Epoch = msg.Epoch;
            _writer.Reset();
            msg.Write(_writer, MsgId.EntityVars);
            BroadcastEntity(rec, Delivery.ReliableOrdered);
        }

        private void OnEntityState(WorkerConn w, EntitySyncMsg msg)
        {
            if (!_entities.TryGetValue(msg.NetId, out var rec) || msg.Epoch < rec.Epoch || rec.OwnerWorkerIndex != w.Index) return;
            // Remember keyframes so a late joiner's spawn carries the newest full state of each behaviour.
            _reader.Set(new ArraySegment<byte>(msg.Chunks));
            SyncStateCodec.ReadEnvelope(_reader, (index, flags, chunk) =>
            {
                if ((flags & SyncStateCodec.ChunkFlags.Full) != 0) rec.StoreKeyframe(index, chunk);
            });
            _writer.Reset();
            msg.Write(_writer, MsgId.EntityState);
            BroadcastEntity(rec, msg.Delivery);
        }

        private void OnEntityRpc(WorkerConn w, NetworkReader r)
        {
            var msg = EntityRpcMsg.Read(r);
            if (!_entities.TryGetValue(msg.NetId, out var rec) || msg.Epoch < rec.Epoch || rec.OwnerWorkerIndex != w.Index) return;
            _writer.Reset();
            msg.Write(_writer, MsgId.EntityRpc);
            if (msg.ClientId == 0 && msg.Radius > 0f)
            {
                // A spatial RPC (a tracer, a footstep): of the clients that hold a replica, the ones whose pawn is
                // within its radius. The observer list is the candidate set, so the radius only narrows it.
                double r2 = (double)msg.Radius * msg.Radius;
                var seg = _writer.ToSegment();
                for (int i = rec.Observers.Count - 1; i >= 0; i--)
                {
                    var c = rec.Observers[i];
                    if (!c.Welcomed || c.PawnNetId == 0 || !_entities.TryGetValue(c.PawnNetId, out var pawn)) continue;
                    var root = RootOf(pawn);
                    double dx = root.AbsX - rec.AbsX, dy = root.AbsY - rec.AbsY, dz = root.AbsZ - rec.AbsZ;
                    if (dx * dx + dy * dy + dz * dz <= r2) AppendReliable(c, seg);
                }
            }
            else if (msg.ClientId == 0) BroadcastEntity(rec, Delivery.ReliableOrdered);
            else if (_clientsById.TryGetValue(msg.ClientId, out var c) && c.Visible.Contains(rec.NetId)) AppendReliable(c, _writer.ToSegment());
        }

        private readonly List<EntityStateEntry> _scratchEntries = new List<EntityStateEntry>();

        // Verbose relay statistics, logged once a second: how much world state arrives and why entries are dropped.
        private int _wsPackets, _wsEntries, _wsUnknown, _wsStale, _wsWrongOwner, _wsSent;
        private float _nextWsReport;

        private void ReportWorldStateStats()
        {
            if (!NebulaLog.Verbose || Time.unscaledTime < _nextWsReport) return;
            _nextWsReport = Time.unscaledTime + 1f;
            long setSum = 0, setMax = 0;
            foreach (var c in _clientsById.Values) { setSum += c.Visible.Count; if (c.Visible.Count > setMax) setMax = c.Visible.Count; }
            int clients = Math.Max(1, _clientsById.Count);
            NebulaLog.Debugf($"worldstate: {_wsPackets} packets {_wsEntries} entries in; dropped unknown={_wsUnknown} stale={_wsStale} wrongOwner={_wsWrongOwner}; {_wsSent} entries sent to {_clientsById.Count} client(s); {_entities.Count} entities known");
            NebulaLog.Debugf($"interest: set {setSum / clients}/{setMax} per client, cache {_entities.Count}, regions {_subscribedRegions.Count}, links {_links.Count} ({DescribeLinkReasons()}), spawns {_spawnsSent} despawns {_despawnsSent} since the last heartbeat, eval {(_evalCount > 0 ? _evalMsSum / _evalCount : 0):0.00}/{_evalMsMax:0.00} ms");
            _wsPackets = _wsEntries = _wsUnknown = _wsStale = _wsWrongOwner = _wsSent = 0;
        }

        private void OnWorldState(WorkerConn w, NetworkReader r)
        {
            // Keep only the entries this worker is still the authority for (drops a stale sender after a handover),
            // remember every entity's latest pose, then give each client the subset it is interested in.
            WorldStateMsg.ReadHeader(r, out uint tick, out ushort workerIndex, out ushort count);
            _wsPackets++;
            _wsEntries += count;
            _scratchEntries.Clear();
            for (int i = 0; i < count; i++)
            {
                var entry = EntityStateEntry.Read(r);
                if (!_entities.TryGetValue(entry.NetId, out var rec)) { _wsUnknown++; continue; }
                if (entry.Epoch < rec.Epoch) { _wsStale++; continue; }
                if (rec.OwnerWorkerIndex != w.Index) { _wsWrongOwner++; continue; }
                if (entry.Epoch == rec.Epoch && rec.HasStateTick && tick <= rec.LastStateTick) continue;
                rec.HasStateTick = true;
                rec.LastStateTick = tick;
                rec.Epoch = entry.Epoch;
                rec.LastSpawn.Epoch = entry.Epoch;
                if (rec.Container != entry.Container && (entry.Fields & TransformFields.Location) == 0)
                {
                    var world = WorldPosition(rec.Container, rec.LastSpawn.LocalPosition);
                    var rotation = WorldRotation(rec.Container, rec.LastSpawn.LocalRotation);
                    rec.LastSpawn.LocalPosition = ContainerPosition(entry.Container, world);
                    rec.LastSpawn.LocalRotation = Quaternion.Inverse(WorldRotation(entry.Container, Quaternion.identity)) * rotation;
                }
                bool changedScope = ScopeContainer(rec.Container)?.InstanceId != ScopeContainer(entry.Container)?.InstanceId;
                bool changedCarrier = rec.Container.IsDynamic != entry.Container.IsDynamic || rec.Container.NetId != entry.Container.NetId;
                bool changedContainer = rec.Container != entry.Container;
                rec.Container = entry.Container;
                rec.LastSpawn.Container = entry.Container;
                entry.Merge(ref rec.LastSpawn.LocalPosition, ref rec.LastSpawn.LocalRotation, ref rec.LastSpawn.LocalScale, ref rec.LastSpawn.Velocity);
                // A scope or carrier change invalidates a decision that was made on the old one; a plain move only
                // has to be rebucketed, and only when its region key actually changed.
                if (changedCarrier) RelinkCarrier(rec);
                // The observers that already hold this entity need the new container's lease row, or they cannot
                // resolve the frame the pose that follows is expressed in. Entering a set is not the only way an
                // entity comes to name a container a client has never heard of: walking into the next chunk is.
                if (changedContainer) SendOwnershipForContainerChange(rec);
                RebucketIfMoved(rec);
                if (changedScope || changedCarrier)
                {
                    for (int o = rec.Observers.Count - 1; o >= 0; o--) rec.Observers[o].InterestDirty = true;
                    if (rec.OwnerClientId != 0 && _clientsById.TryGetValue(rec.OwnerClientId, out var moved)) moved.InterestDirty = true;
                    // The passengers move with it: a ship that changed scope or carrier reseated its whole
                    // subtree, and nothing else offers those records to the clients of where it now sits.
                    ConsiderCarried(rec);
                }
                _scratchEntries.Add(entry);
            }
            if (_scratchEntries.Count == 0) return;

            // One pass over the entries, fanning each out to its own observers: the clients × all-entities loop
            // this used to be is what interest management replaces.
            for (int i = 0; i < _scratchEntries.Count; i++)
            {
                var entry = _scratchEntries[i];
                if (!_entities.TryGetValue(entry.NetId, out var rec)) continue;
                if (entry.Reliable)
                {
                    _writer.Reset();
                    int slot = WorldStateMsg.Begin(_writer, MsgId.WorldState, tick, w.Index);
                    entry.Write(_writer);
                    WorldStateMsg.End(_writer, slot, 1);
                    var reliable = _writer.ToSegment();
                    for (int o = rec.Observers.Count - 1; o >= 0; o--) { AppendReliable(rec.Observers[o], reliable); _wsSent++; }
                    continue;
                }
                for (int o = rec.Observers.Count - 1; o >= 0; o--)
                {
                    var c = rec.Observers[o];
                    if (!c.Welcomed) continue;
                    if (!WantsThisTick(rec, entry, tick, c)) continue;
                    AppendWorldState(c, tick, w.Index, entry);
                    _wsSent++;
                }
            }
        }

        /// <summary>
        /// A worker sends its tick as several small batches (one Sequenced packet each). Re-emitting each batch after
        /// interest filtering would keep the packet count while dropping the entries, so the kept entries are
        /// coalesced per client into full packets, flushed when the tick or the worker changes and after every
        /// transport poll (so nothing waits longer than a frame).
        /// </summary>
        private void AppendWorldState(ClientConn c, uint tick, ushort workerIndex, in EntityStateEntry entry)
        {
            if (c.PendingSlot >= 0 && (c.PendingTick != tick || c.PendingWorker != workerIndex)) FlushWorldState(c);
            if (c.PendingSlot < 0)
            {
                c.Pending ??= new NetworkWriter(WorldStateMsg.BatchBytes + 64);
                c.Pending.Reset();
                c.PendingSlot = WorldStateMsg.Begin(c.Pending, MsgId.WorldState, tick, workerIndex);
                c.PendingCount = 0;
                c.PendingTick = tick;
                c.PendingWorker = workerIndex;
            }
            entry.Write(c.Pending);
            c.PendingCount++;
            if (c.Pending.Length + EntityStateEntry.WireSize > WorldStateMsg.BatchBytes) FlushWorldState(c);
        }

        private void FlushWorldState(ClientConn c)
        {
            if (c.PendingSlot < 0) return;
            WorldStateMsg.End(c.Pending, c.PendingSlot, c.PendingCount);
            Send(c.PeerId, Delivery.Sequenced, c.Pending.ToSegment());
            c.PendingSlot = -1;
            c.PendingCount = 0;
        }

        /// <summary>
        /// Interest management, by distance from the client's pawn: every tick nearby, every few ticks at mid range,
        /// a trickle far away (the client's interpolator bridges the gaps; see NebulaConfig). The rate is spread by
        /// net id so a reduced-rate tier still sends a steady stream rather than a burst every Nth tick. A client
        /// with no pawn yet (spectating the title screen) gets the far rate for everything; the client's own pawn
        /// always gets every tick (it is what reconciliation compares against).
        /// </summary>
        private bool WantsThisTick(EntityRecord rec, in EntityStateEntry entry, uint tick, ClientConn c)
        {
            // A client with no pawn has PawnNetId 0, which must not make every record match "this is my pawn".
            if (c.PawnNetId != 0 && rec.NetId == c.PawnNetId) return true;
            // Distance is measured from the client's cached foci to the record's cached absolute position: no
            // container walk, no transform maths, per (entity, observer) pair.
            double best = double.MaxValue;
            var foci = c.Interest?.Foci;
            if (foci != null)
                for (int i = 0; i < foci.Count; i++)
                {
                    double d2 = foci[i].SqrDistanceTo(rec.AbsX, rec.AbsY, rec.AbsZ);
                    if (d2 < best) best = d2;
                }
            int divisor;
            if (best == double.MaxValue) divisor = _interest.FarDivisor;
            else if (best <= (double)_interest.NearRadius * _interest.NearRadius) return true;
            else divisor = best <= (double)_interest.FarRadius * _interest.FarRadius ? _interest.MidDivisor : _interest.FarDivisor;
            if (divisor <= 1) return true;
            return (tick + (uint)(rec.NetId % (ulong)divisor)) % (uint)divisor == 0;
        }

        private bool TryGetPawnPosition(ClientConn c, out Vector3 position)
        {
            position = default;
            if (c.PawnNetId == 0 || !_entities.TryGetValue(c.PawnNetId, out var rec)) return false;
            var root = RootOf(rec);
            position = new Vector3((float)root.AbsX, (float)root.AbsY, (float)root.AbsZ);
            return true;
        }

        /// <summary>
        /// A container-local position as a world position. The gateway holds no entities, so a dynamic container's
        /// frame is rebuilt from its carrier's newest pose (itself container-local, hence the walk): a
        /// DynamicContainer's frame is its carrier's root transform, which is what makes this possible here.
        /// <para>
        /// Every carrier in the chain is folded in, however deep. A coordinate that stopped partway
        /// out would be in some intermediate ship's frame while being used as a world position, which is a
        /// silently wrong region key and a silently wrong observation-window test. Container references come off
        /// the wire rather than through the cycle-refusing index, so the bound is the number of records held.
        /// Iterative, so a corrupt chain costs a loop rather than the stack.
        /// </para>
        /// </summary>
        private Vector3 WorldPosition(ContainerRef container, Vector3 local)
        {
            for (int hops = 0; container.IsDynamic; hops++)
            {
                if (hops > _entities.Count || !_entities.TryGetValue(container.NetId, out var carrier)) return local;
                local = carrier.LastSpawn.LocalPosition + carrier.LastSpawn.LocalRotation * Vector3.Scale(carrier.LastSpawn.LocalScale, local);
                container = carrier.Container;
            }
            var c = ContainerRegistry.Resolve(container);
            return c != null ? c.ToWorld(local) : local;
        }

        /// <inheritdoc cref="WorldPosition"/>
        private Quaternion WorldRotation(ContainerRef container, Quaternion local)
        {
            for (int hops = 0; container.IsDynamic; hops++)
            {
                if (hops > _entities.Count || !_entities.TryGetValue(container.NetId, out var carrier)) return local;
                local = carrier.LastSpawn.LocalRotation * local;
                container = carrier.Container;
            }
            var c = ContainerRegistry.Resolve(container);
            return c != null ? c.Rotation * local : local;
        }

        /// <summary>
        /// The inverse of <see cref="WorldPosition"/>: a world position in one container's local frame. The
        /// carriers are collected outwards and then applied inwards, which is the same order the recursion it
        /// replaces unwound in, with the same unbounded depth and the same records-held bound.
        /// </summary>
        private Vector3 ContainerPosition(ContainerRef container, Vector3 world)
        {
            _carrierChain.Clear();
            var at = container;
            while (at.IsDynamic)
            {
                if (_carrierChain.Count > _entities.Count || !_entities.TryGetValue(at.NetId, out var carrier)) break;
                _carrierChain.Add(carrier);
                at = carrier.Container;
            }
            Vector3 local = world;
            if (!at.IsDynamic)
            {
                var c = ContainerRegistry.Resolve(at);
                if (c != null) local = c.ToLocal(world);
            }
            for (int i = _carrierChain.Count - 1; i >= 0; i--)
            {
                var carrier = _carrierChain[i];
                var relative = Quaternion.Inverse(carrier.LastSpawn.LocalRotation) * (local - carrier.LastSpawn.LocalPosition);
                var scale = carrier.LastSpawn.LocalScale;
                local = new Vector3(scale.x != 0 ? relative.x / scale.x : 0, scale.y != 0 ? relative.y / scale.y : 0, scale.z != 0 ? relative.z / scale.z : 0);
            }
            _carrierChain.Clear();
            return local;
        }

        /// <summary>Carriers between a reference and the static container it sits in; reused by <see cref="ContainerPosition"/>.</summary>
        private readonly List<EntityRecord> _carrierChain = new List<EntityRecord>();

        private void OnOwnerState(WorkerConn w, NetworkReader r)
        {
            var msg = OwnerStateMsg.Read(r);
            if (!_entities.TryGetValue(msg.NetId, out var rec) || msg.Epoch < rec.Epoch || rec.OwnerWorkerIndex != w.Index) return;
            // Owner state is for the client's own prediction, so it only means anything while that client holds
            // a replica. An owned entity is always in its owner's set, so this only ever drops a stale message.
            if (!_clientsById.TryGetValue(msg.OwnerClientId, out var c) || !c.Visible.Contains(msg.NetId)) return;
            _writer.Reset();
            msg.Write(_writer);
            Send(c.PeerId, Delivery.Sequenced, _writer.ToSegment());
        }

        // ---------------------------------------------------------------------------------------- clients

        private void DispatchClient(int peerId, NetworkReader r)
        {
            var id = (MsgId)r.ReadByte();
            _clientsByPeer.TryGetValue(peerId, out var c);
            if (id == MsgId.Hello)
            {
                var hello = HelloMsg.Read(r);
                if (hello.Version != HelloMsg.ProtocolVersion)
                {
                    NebulaLog.Warn($"client protocol {hello.Version} != {HelloMsg.ProtocolVersion}; disconnecting");
                    _transport.Disconnect(peerId);
                    return;
                }
                if (hello.Role == PeerRole.Worker)
                {
                    // A worker dialled us (not the normal direction, but harmless): treat it as a worker link.
                    if (!AcceptWorkerHello(peerId, hello)) return;
                    var w = new WorkerConn { PeerId = peerId, WorkerId = hello.Id, Index = (ushort)hello.Index, Incarnation = hello.Incarnation, Ready = true };
                    _workersByPeer[peerId] = w;
                    _workersById[w.WorkerId] = w;
                    _workersByIndex[w.Index] = w;
                    return;
                }
                if (hello.Role == PeerRole.Gateway)
                {
                    NebulaLog.Warn($"gateway '{hello.Id}' dialled this gateway; disconnecting");
                    _transport.Disconnect(peerId);
                    return;
                }
                if (c == null)
                {
                    // A provisional id until the session is known: a reclaimed session keeps its old id instead.
                    c = new ClientConn { PeerId = peerId, ClientId = SessionIds.Make(Incarnation, _nextSequence++) };
                    _clientsByPeer[peerId] = c;
                    _clientsById[c.ClientId] = c;
                }
                if (c.Welcomed || c.AuthPending || c.DisconnectAt != 0) return; // one Hello per link
                c.Name = string.IsNullOrEmpty(hello.Id) ? $"player{SessionIds.Sequence(c.ClientId)}" : hello.Id;
                c.IsBot = (hello.Flags & HelloFlags.Bot) != 0;
                if (Draining) { Reject(c, "gateway is draining", true); return; }
                Authenticate(c, hello.Token ?? "", hello.Session ?? "");
                return;
            }
            if (c == null || !c.Welcomed) return;

            switch (id)
            {
                case MsgId.ClientInput:
                {
                    var msg = ClientInputMsg.Read(r);
                    msg.ClientId = c.ClientId;
                    if (c.PawnNetId == 0 || !_entities.TryGetValue(c.PawnNetId, out var rec)) return;
                    if (!_workersByIndex.TryGetValue(rec.OwnerWorkerIndex, out var w)) return;
                    _writer.Reset();
                    msg.Write(_writer, MsgId.ClientInput);
                    Send(w.PeerId, Delivery.Sequenced, _writer.ToSegment());
                    break;
                }
                case MsgId.InstanceReady:
                {
                    var preparation = InstancePreparationMsg.Read(r);
                    if (preparation.EntityId != c.PawnNetId || !_entities.TryGetValue(c.PawnNetId, out var pawn) ||
                        pawn.OwnerWorkerIndex != preparation.SourceWorker || !_workersByIndex.TryGetValue(preparation.SourceWorker, out var source)) break;
                    _writer.Reset(); preparation.Write(_writer, MsgId.InstanceReady);
                    Send(source.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
                    break;
                }
                case MsgId.ServerRpc:
                {
                    var msg = EntityRpcMsg.Read(r);
                    msg.ClientId = c.ClientId;
                    if (!_entities.TryGetValue(msg.NetId, out var rec) || rec.OwnerClientId != c.ClientId) return;
                    if (!_workersByIndex.TryGetValue(rec.OwnerWorkerIndex, out var w)) return;
                    _writer.Reset();
                    msg.Write(_writer, MsgId.ServerRpc);
                    Send(w.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
                    break;
                }
                case MsgId.ClientFocusHint: OnClientFocusHint(c, ClientFocusHintMsg.Read(r)); break;
                case MsgId.Ping:
                {
                    var ping = PingMsg.Read(r);
                    _writer.Reset();
                    new PongMsg { ClientTime = ping.ClientTime, ServerTick = NetworkTime.DerivedTick }.Write(_writer);
                    Send(peerId, Delivery.Sequenced, _writer.ToSegment());
                    break;
                }
                default:
                    NebulaLog.Warn($"gateway got unexpected {id} from client {c.ClientId}");
                    break;
            }
        }

        /// <summary>
        /// Tell a client how its join is going, when the answer changed. The estimate for a hold is what the
        /// orchestrator published for this worker host (<see cref="IWorkerHost.TypicalBootSeconds"/>, seeded as the
        /// <see cref="MeshSettings.BootSeconds"/> mesh setting), so the game can show "world starting, about N s".
        /// </summary>
        private void SendJoinStatus(ClientConn c, JoinState state)
        {
            ushort estimate = state == JoinState.Starting ? (ushort)Mathf.Clamp(ControlPlane.GetSettingInt(MeshSettings.BootSeconds, 0), 0, ushort.MaxValue) : (ushort)0;
            if (c.Join == state && c.JoinEstimate == estimate) return;
            c.Join = state;
            c.JoinEstimate = estimate;
            _writer.Reset();
            new JoinStatusMsg { State = state, EstimatedSeconds = estimate }.Write(_writer);
            Send(c.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
            if (state == JoinState.Starting)
                NebulaLog.Info($"client {c.ClientId} '{c.Name}' is waiting for the world to start" + (estimate > 0 ? $" (about {estimate} s)" : ""));
        }

        // ---------------------------------------------------------------------------------------- authentication

        /// <summary>
        /// Decide who this client is from the token in its Hello: none = a fresh anonymous identity (when allowed),
        /// one of ours = checked here, anything else = an ID token for <see cref="TokenValidator"/>, which may
        /// answer later once the provider's keys are fetched. <paramref name="session"/> is the session token of an
        /// earlier connection, honoured once the identity is known (<see cref="WelcomeClient"/>).
        /// </summary>
        private void Authenticate(ClientConn c, string token, string session)
        {
            if (token.Length == 0)
            {
                if (_anonymous == null) { Reject(c, "this game requires signing in"); return; }
                string issued = _anonymous.Issue(out string subject);
                WelcomeClient(c, AuthResult.Accept(PlayerIdentity.AnonymousIssuer, subject), issued, session);
                return;
            }
            if (AnonymousIdentityIssuer.IsAnonymousToken(token))
            {
                if (_anonymous == null) { Reject(c, "anonymous players are not allowed on this game"); return; }
                var result = _anonymous.Verify(token);
                if (result.Ok) WelcomeClient(c, result, "", session);
                else Reject(c, result.Error);
                return;
            }
            if (_oidc == null) { Reject(c, "this game does not accept sign-in tokens"); return; }
            c.AuthPending = true;
            int peerId = c.PeerId;
            _oidc.Validate(token, result =>
            {
                c.AuthPending = false;
                // The link may have gone away while the keys were fetched.
                if (!_clientsByPeer.TryGetValue(peerId, out var current) || current != c || c.Welcomed || c.DisconnectAt != 0) return;
                if (result.Ok) WelcomeClient(c, result, "", session);
                else Reject(c, result.Error);
            });
        }

        /// <summary>Tell the client why and drop the link a moment later, once the message has had time to go out.</summary>
        private void Reject(ClientConn c, string reason, bool retry = false)
        {
            NebulaLog.Warn($"client {c.ClientId} '{c.Name}' rejected: {reason}");
            _writer.Reset();
            new JoinRejectedMsg { Reason = reason, Retry = retry }.Write(_writer);
            Send(c.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
            c.DisconnectAt = Time.unscaledTime + 0.5f;
        }

        private readonly List<ClientConn> _toDrop = new List<ClientConn>();

        private void DropRejectedClients()
        {
            _toDrop.Clear();
            foreach (var c in _clientsById.Values)
                if (c.DisconnectAt != 0 && Time.unscaledTime >= c.DisconnectAt) _toDrop.Add(c);
            foreach (var c in _toDrop)
            {
                _clientsByPeer.Remove(c.PeerId);
                _clientsById.Remove(c.ClientId);
                ForgetClientInterest(c);
                _transport.Disconnect(c.PeerId);
            }
        }

        /// <summary>A generation that is newer than anything the session had before: the clock, but never below what the token said.</summary>
        private static ulong NextGeneration(ulong previous)
        {
            ulong now = (ulong)Math.Max(0L, (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
            return Math.Max(now, previous + 1);
        }

        /// <summary>
        /// Validate token-based reconnection when identity-wide session coordination is disabled.
        /// Returns zero when the token does not identify a session belonging to this authenticated player.
        /// </summary>
        private ulong EarlierSessionOf(ClientConn c, string session, out ulong generation, out string how)
        {
            generation = 0;
            how = "";
            if (session.Length > 0 && _sessions != null)
            {
                if (!_sessions.Verify(session, out var claims, out string error))
                    NebulaLog.Info($"client '{c.Name}': session token ignored ({error}); starting a new session");
                else if (claims.Identity != c.Identity)
                    NebulaLog.Warn($"client '{c.Name}': session token belongs to another identity; starting a new session");
                else
                {
                    generation = claims.Generation;
                    how = "session token";
                    return claims.SessionId;
                }
            }
            return 0;
        }

        /// <summary>
        /// Give the client the session this player already had, when there is one: its id is taken over (with a
        /// newer generation, which fences whoever held it at the worker) instead of the provisional one, and a link
        /// that still holds that session on this gateway is let go. The pawn, if the worker still has it, is found
        /// through the session id on the worker's next announcement or in what this gateway already knows.
        /// </summary>
        private void TryReclaim(ClientConn c, string session)
        {
            ulong sessionId = EarlierSessionOf(c, session, out ulong generation, out string how);
            if (sessionId == 0) return;
            if (_clientsById.TryGetValue(sessionId, out var previous) && previous != c)
            {
                NebulaLog.Info($"player {ShortIdentity(c.Identity)} connected again as '{c.Name}' ({how}); closing the earlier link of session {sessionId}");
                _clientsByPeer.Remove(previous.PeerId);
                _clientsById.Remove(previous.ClientId);
                ForgetClientInterest(previous);
                EndPlayerLink(previous, ReplacedReason);
                if (previous.PawnNetId != 0) c.PawnNetId = previous.PawnNetId;
            }
            else NebulaLog.Debugf($"client '{c.Name}' continues session {sessionId} ({how})");
            _clientsById.Remove(c.ClientId);
            c.ClientId = sessionId;
            c.Generation = NextGeneration(generation);
            c.Reclaimed = true;
            _clientsById[c.ClientId] = c;
            if (c.PawnNetId == 0)
            {
                foreach (var rec in _entities.Values)
                    if (rec.OwnerClientId == c.ClientId) { c.PawnNetId = rec.NetId; break; }
            }
            _recentlyLost.RemoveAll(kv => kv.Key == c.ClientId);
        }

        /// <summary>What a client is told when a newer connection for the same player took its session.</summary>
        private const string ReplacedReason = "this player connected again somewhere else";

        /// <summary>
        /// The session is not this link's any more: tell the client why, then close the link a moment later, once
        /// the notice has had time to go out. The caller has already forgotten the connection, so nothing is
        /// despawned on its behalf - the connection that took the session over holds the pawn now.
        /// </summary>
        private void EndPlayerLink(ClientConn c, string reason)
        {
            _writer.Reset();
            new SessionReplacedMsg { Reason = reason }.Write(_writer);
            Send(c.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
            _transport.Flush();
            _closingPeers.Add(new KeyValuePair<int, float>(c.PeerId, Time.unscaledTime + 0.5f));
        }

        private void CloseReplacedLinks()
        {
            for (int i = _closingPeers.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < _closingPeers[i].Value) continue;
                // A peer id the transport has since handed to a new link belongs to that client, not to this one.
                if (!_clientsByPeer.ContainsKey(_closingPeers[i].Key)) _transport.Disconnect(_closingPeers[i].Key);
                _closingPeers.RemoveAt(i);
            }
        }

        /// <summary>
        /// A worker says the session has moved to another gateway (<see cref="EndSessionMsg"/>) because the player
        /// connected again there. Let the client go without despawning its pawn, which the new connection holds.
        /// </summary>
        private void OnEndSession(EndSessionMsg msg)
        {
            if (!_clientsById.TryGetValue(msg.ClientId, out var c)) return;
            if (c.Generation > msg.Generation) return; // the player came back here since, so this news is stale
            NebulaLog.Info($"client {c.ClientId} '{c.Name}': {msg.Reason}; closing the link");
            _clientsByPeer.Remove(c.PeerId);
            _clientsById.Remove(c.ClientId);
            ForgetClientInterest(c);
            EndPlayerLink(c, string.IsNullOrEmpty(msg.Reason) ? ReplacedReason : msg.Reason);
        }

        /// <summary>The client is who it says it is: welcome it, replay the world, and ask a worker for a pawn (or for the one it had).</summary>
        private void WelcomeClient(ClientConn c, in AuthResult auth, string issuedToken, string session)
        {
            if (Config.SingleSessionPerPlayer)
            {
                BeginCoordinatedWelcome(c, auth, issuedToken, session);
                return;
            }
            FinishWelcomeClient(c, auth, issuedToken, session);
        }

        private void FinishWelcomeClient(ClientConn c, in AuthResult auth, string issuedToken, string session)
        {
            int peerId = c.PeerId;
            c.Identity = auth.Identity ?? "";
            if (c.CoordinationClaim.Length == 0)
            {
                c.Generation = NextGeneration(0);
                TryReclaim(c, session);
            }
            c.Welcomed = true;
            string how = auth.Issuer == PlayerIdentity.AnonymousIssuer ? (issuedToken.Length > 0 ? "new anonymous identity" : "anonymous") : auth.Issuer;
            NebulaLog.Info($"client {c.ClientId} '{c.Name}'{(c.IsBot ? " (bot)" : "")} connected as {ShortIdentity(c.Identity)} ({how}{(c.Reclaimed ? ", session reclaimed" : "")})");

            string sessionToken = _sessions.Issue(new SessionClaims
            {
                SessionId = c.ClientId, Identity = c.Identity, Name = c.Name, IsBot = c.IsBot, Generation = c.Generation,
                ExpiresAt = JsonWebToken.UnixNow() + SessionTokenLifetimeSeconds,
            });
            _writer.Reset();
            new WelcomeMsg { ClientId = c.ClientId, TickRate = NetworkTime.TickRate, ServerTick = NetworkTime.DerivedTick, Identity = c.Identity, Token = issuedToken, SessionToken = sessionToken, Reclaimed = c.Reclaimed }.Write(_writer);
            Send(peerId, Delivery.ReliableOrdered, _writer.ToSegment());
            // A session taken over through identity coordination rather than a token never went through
            // TryReclaim, so the pawn it already owns has to be found here. With interest management the pawn is
            // what gives the client a focus at all: without it the first evaluation would find nothing.
            if (c.PawnNetId == 0)
                foreach (var rec in _entities.Values)
                    if (rec.OwnerClientId == c.ClientId) { c.PawnNetId = rec.NetId; break; }
            // There is no replay: a reclaimed session starts with an empty set and its first evaluation is the
            // replay, so the client is told exactly what it should have and nothing else (design §5).
            c.Interest = null;
            c.Visible.Clear();
            c.ViewSeq.Clear();
            c.InterestDirty = true;
            _schedule.Add(c.ClientId);
            // Before the first evaluation, so a server extension's tags and focus mode are already in place when
            // it runs: the first thing this client is sent is decided by what that evaluation finds.
            RaiseClientEvent(ClientJoined, c, nameof(ClientJoined));
            SendOwnershipFull(c);
            ReconcileView(c);
            if (c.PawnNetId != 0)
            {
                // The pawn is still around: claim it from its worker, which re-announces it and routes the session here.
                SendJoinStatus(c, JoinState.Joined);
                if (_entities.TryGetValue(c.PawnNetId, out var pawn) && _workersByIndex.TryGetValue(pawn.OwnerWorkerIndex, out var owner))
                    SendClaim(c, owner, pawn.Container);
                return;
            }
            SendJoinStatus(c, JoinState.Starting);
            TryRequestSpawn(c);
        }

        private static string ShortIdentity(string identity) => identity.Length > 12 ? identity.Substring(0, 12) + ".." : identity;

        private void OnClientLost(ClientConn c)
        {
            ReleaseCoordinatedSession(c);
            _clientsByPeer.Remove(c.PeerId);
            _clientsById.Remove(c.ClientId);
            ForgetClientInterest(c);
            NebulaLog.Info($"client {c.ClientId} '{c.Name}' disconnected");
            if (!c.Welcomed) return;
            // The worker keeps the pawn for SessionReclaimSeconds in case the client comes back (here or elsewhere);
            // the despawn carries the generation so a gateway that has since claimed the session is not undone.
            WorkerConn w = null;
            if (c.PawnNetId != 0 && _entities.TryGetValue(c.PawnNetId, out var rec))
                _workersByIndex.TryGetValue(rec.OwnerWorkerIndex, out w);
            else if (!string.IsNullOrEmpty(c.SpawnWorkerId))
                _workersById.TryGetValue(c.SpawnWorkerId, out w);
            if (w != null)
            {
                _writer.Reset();
                new DespawnPlayerMsg { ClientId = c.ClientId, Generation = c.Generation }.Write(_writer);
                Send(w.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
                if (Config.SessionReclaimSeconds > 0) _recentlyLost.Add(new KeyValuePair<ulong, float>(c.ClientId, Time.unscaledTime));
            }
        }

        private void SendClaim(ClientConn c, WorkerConn worker, ContainerRef container)
        {
            c.SpawnWorkerId = worker.WorkerId;
            c.SpawnContainer = container;
            _writer.Reset();
            new SpawnPlayerMsg { ClientId = c.ClientId, Container = container, Name = c.Name, IsBot = c.IsBot, Identity = c.Identity, Generation = c.Generation }.Write(_writer);
            Send(worker.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
        }

        private void TryRequestSpawn(ClientConn c)
        {
            c.NextSpawnAttempt = Time.unscaledTime + 3f;
            if (c.CoordinationInFlight) return;
            if (c.CoordinationClaim.Length > 0 && !string.IsNullOrEmpty(c.SpawnWorkerId) &&
                _workersById.TryGetValue(c.SpawnWorkerId, out var reserved) && reserved.Ready)
            {
                ReserveCoordinatedSpawn(c, reserved, c.SpawnContainer);
                return;
            }
            // Any container with an active lease whose worker we are connected to.
            var candidates = new List<Container>();
            CollectSpawnCandidates(ContainerRegistry.All, candidates);
            CollectSpawnCandidates(ContainerRegistry.Runtime, candidates);
            if (candidates.Count == 0)
            {
                // Nothing to spawn into: with MinWorkers at 0 this is the normal first join after an idle period.
                // The client is held rather than dropped, the orchestrator sees the pending join on the next
                // heartbeat and boots a worker, and this retry (every 3 s) places the player with no reconnect.
                SendJoinStatus(c, JoinState.Starting);
                NebulaLog.Debugf($"no container available to spawn client {c.ClientId} yet; holding the join (world starting)");
                return;
            }
#if NEBULA_SERVICE
            var pick = candidates[System.Random.Shared.Next(candidates.Count)];
#else
            var pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
#endif
            var worker = _workersById[pick.OwnerWorkerId];
            if (c.CoordinationClaim.Length > 0)
            {
                ReserveCoordinatedSpawn(c, worker, pick.Ref);
                return;
            }
            SendClaim(c, worker, pick.Ref);
            NebulaLog.Info($"asked {worker.WorkerId} to spawn client {c.ClientId} in {pick.ContainerId}");
        }

        private void CollectSpawnCandidates(IReadOnlyList<Container> containers, List<Container> candidates)
        {
            for (int i = 0; i < containers.Count; i++)
            {
                var container = containers[i];
                if (string.IsNullOrEmpty(container.OwnerWorkerId)) continue;
                if (!_workersById.TryGetValue(container.OwnerWorkerId, out var w) || !w.Ready) continue;
                candidates.Add(container);
            }
        }

        /// <summary>
        /// Reliable messages for a client are coalesced into MsgId.Batch packets: appended here, sent when the
        /// batch is full or after the transport poll (see Update), so nothing waits longer than a frame and order is
        /// preserved (everything reliable to a client goes through this one path).
        /// </summary>
        private void AppendReliable(ClientConn c, ArraySegment<byte> message)
        {
            if (c.ReliableSlot < 0)
            {
                c.Reliable ??= new NetworkWriter(ReliableBatchBytes + 512);
                c.Reliable.Reset();
                c.Reliable.WriteByte((byte)MsgId.Batch);
                c.ReliableSlot = c.Reliable.ReserveUShort();
                c.ReliableCount = 0;
            }
            c.Reliable.WriteUShort((ushort)message.Count);
            c.Reliable.WriteRaw(message);
            c.ReliableCount++;
            if (c.Reliable.Length >= ReliableBatchBytes || c.ReliableCount == ushort.MaxValue) FlushReliable(c);
        }

        private void FlushReliable(ClientConn c)
        {
            if (c.ReliableSlot < 0) return;
            c.Reliable.PatchUShort(c.ReliableSlot, c.ReliableCount);
            Send(c.PeerId, Delivery.ReliableOrdered, c.Reliable.ToSegment());
            c.ReliableSlot = -1;
            c.ReliableCount = 0;
        }
    }
}
