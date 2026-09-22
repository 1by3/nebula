using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Runs authoritative game simulation for the containers assigned to this worker. It creates ghost copies near
    /// boundaries, transfers entities when they cross into another worker's container, and sends entity state to the
    /// gateway. One worker can control any number of containers. A transfer between two containers on the same worker
    /// does not use the network.
    /// </summary>
    public sealed partial class NebulaWorker : MonoBehaviour, IRpcSink, IWorkerMessaging
    {
        private sealed class Peer
        {
            public int PeerId;
            public PeerRole Role;
            public string Id = "";
            public uint Index;
            public uint Incarnation;
            /// <summary>Gateways: id + incarnation (<see cref="PlayerSessions.GatewayKey"/>), what sessions are keyed on.</summary>
            public string Key = "";
            public bool HelloReceived;
            public bool Outbound;
            /// <summary>Gateways: this link's bit in the <see cref="RegionPublisher"/> mask, or -1 when no bit was free.</summary>
            public int GatewayBit = -1;
            /// <summary>Gateways: the region set this link subscribes here. Null until the link is registered.</summary>
            public RegionSubscriptionReceiver Subscription;
            /// <summary>Gateways: state entries and bytes sent to this link since it came up, for interest statistics.</summary>
            public long EntriesSent;
            public long BytesSent;
        }

        /// <summary>
        /// Byte budget for one Sequenced world/ghost-state packet. Sequenced delivery cannot fragment, and a peer's
        /// MTU starts at LiteNetLib's floor (508 bytes) until discovery finishes, so batches are cut by size rather
        /// than by entry count. Entry size depends on the transform's selected axes and precision.
        /// </summary>
        public const int StateBatchBytes = WorldStateMsg.BatchBytes;

        public NebulaConfig Config { get; private set; }
        public IControlPlane ControlPlane { get; private set; }
        /// <summary>
        /// Long-term storage for the entities that opted into it (<see cref="PersistentEntity"/>): checkpoints of
        /// what this worker owns, and restores of what the containers it leases held. Null when persistence is off
        /// (<c>-nebula-persistence-mode off</c>, or no store handed to <see cref="Initialize"/>).
        /// </summary>
        public NebulaPersistence Persistence { get; private set; }

        /// <summary>
        /// This worker's half of the scope lifecycle: the idle clock of the scope parts it owns, the retire
        /// sequence for a scope the orchestrator is retiring, and the restore acknowledgement a restored scope
        /// waits on. See <c>docs/scope-lifecycle.md</c>.
        /// </summary>
        public WorkerScopeLifecycle ScopeLifecycleAgent => _scopeLifecycle ??= new WorkerScopeLifecycle(this);
        public string WorkerId { get; private set; }
        /// <summary>This start of the process (<see cref="HelloMsg.Incarnation"/>), so peers can tell a restart from a reconnect.</summary>
        public uint Incarnation { get; private set; }
        /// <summary>Player sessions this worker knows about and which gateway currently speaks for each.</summary>
        public PlayerSessions Sessions => _sessions;
        public ushort WorkerIndex { get; private set; }
        public ushort Port { get; private set; }
        public bool IsListening { get; private set; }
        public uint CurrentTick { get; private set; }

        // stats
        public ulong TickCount { get; private set; }
        public float TickMs { get; private set; }
        public int HandoversOut { get; private set; }
        public int HandoversIn { get; private set; }
        public int LocalHandovers { get; private set; }
        /// <summary>
        /// AuthorityRpc sends this process discarded because the caller held neither an authoritative nor a ghost
        /// copy of the target (see <see cref="NebulaDiagnostics.RejectedAuthorityRpcSends"/>). Reported as
        /// <c>rpcRejected</c> on the profile log line.
        /// </summary>
        public int RejectedAuthorityRpcSends => NebulaDiagnostics.RejectedAuthorityRpcSends;
        public int GhostsSent { get; private set; }
        public int GhostsHeld { get; private set; }
        public int AuthoritativeCount => _authoritative.Count;
        public int EntityCount => _entities.Count;
        /// <summary>AuthorityRpc calls from other workers this worker has applied since it started (see the cross-worker call contract).</summary>
        public long AuthorityCallsApplied => _callRouter?.Applied ?? 0;
        /// <summary>AuthorityRpc calls this worker has forwarded to the entity's new owner since it started.</summary>
        public long AuthorityCallsForwarded => _callRouter?.Forwarded ?? 0;
        /// <summary>AuthorityRpc calls this worker has rejected (stale epoch, unknown entity, hop limit, duplicate, unreachable) since it started.</summary>
        public long AuthorityCallsRejected => _callRouter?.Rejected ?? 0;
        /// <summary>AuthorityRpc calls this worker sent with a reply requested and is still waiting on.</summary>
        public int AuthorityCallsPending => _callTracker?.PendingCount ?? 0;
        /// <summary>Authoritative entities owned by human clients / by bot clients / by nobody (server-driven), as of the last heartbeat.</summary>
        public int PlayerCount { get; private set; }
        public int BotCount { get; private set; }
        public int ServerDrivenCount { get; private set; }

        private ITransport _transport;
        private readonly Dictionary<int, Peer> _peers = new Dictionary<int, Peer>();
        private readonly Dictionary<string, Peer> _workerPeersById = new Dictionary<string, Peer>();
        private readonly Dictionary<uint, Peer> _workerPeersByIndex = new Dictionary<uint, Peer>();
        private readonly List<Peer> _gateways = new List<Peer>();
        /// <summary>Which gateway speaks for each player session, and since which generation (see <see cref="PlayerSessions"/>).</summary>
        private readonly PlayerSessions _sessions = new PlayerSessions();
        private readonly List<ulong> _expiredSessions = new List<ulong>();
        private byte[] _peerKey;
        private readonly HashSet<string> _dialing = new HashSet<string>();

        private readonly Dictionary<ulong, NetworkIdentity> _entities = new Dictionary<ulong, NetworkIdentity>();
        private readonly List<NetworkIdentity> _authoritative = new List<NetworkIdentity>();
        private readonly Dictionary<ulong, object> _pendingPlayerSpawns = new Dictionary<ulong, object>();
        private readonly Dictionary<ulong, NetworkIdentity> _players = new Dictionary<ulong, NetworkIdentity>();
        /// <summary>Clients the gateway told us are bots, so Spawn() can tag their pawns without a game-code API change.</summary>
        private readonly HashSet<ulong> _botClients = new HashSet<ulong>();
        /// <summary>Client id -> the player's identity across sessions, as the gateway told us in SpawnPlayer.</summary>
        private readonly Dictionary<ulong, string> _playerIdentities = new Dictionary<ulong, string>();
        /// <summary>netId -> (workerId -> time last seen inside that worker's band)</summary>
        private readonly Dictionary<ulong, Dictionary<string, float>> _ghostTargets = new Dictionary<ulong, Dictionary<string, float>>();
        private readonly Dictionary<ulong, HashSet<string>> _inheritedGhosts = new Dictionary<ulong, HashSet<string>>();
        /// <summary>Entities we handed off recently: netId -> new owner. Inputs that still arrive here are forwarded.</summary>
        private readonly Dictionary<ulong, string> _handedOff = new Dictionary<ulong, string>();
        /// <summary>
        /// The cross-worker call contract (docs/cross-worker-calls.md): the rules that apply, forward or reject an
        /// incoming AuthorityRpc and the ledger of call ids applied here, and the calls this worker sent with a
        /// reply requested. Created in <see cref="Initialize"/>, once the worker index and incarnation are known.
        /// </summary>
        private AuthorityCallRouter _callRouter;
        private AuthorityCallTracker _callTracker;
        private readonly List<NetworkIdentity> _scratchEntities = new List<NetworkIdentity>();
        private readonly List<string> _scratchStrings = new List<string>();

        // Scene entities (see SceneEntities): spawns and handovers that arrived for a scene object whose cell is not
        // loaded here yet wait by scene id, and are applied when the object registers. Containers this worker leases
        // are dated so a freshly leased one gets its grace period before its unspawned scene entities are spawned.
        private struct PendingTransfer { public Peer From; public AuthorityTransferMsg Msg; }
        private readonly Dictionary<uint, EntitySpawnMsg> _pendingSceneGhosts = new Dictionary<uint, EntitySpawnMsg>();
        private readonly Dictionary<uint, PendingTransfer> _pendingSceneTransfers = new Dictionary<uint, PendingTransfer>();
        private readonly Dictionary<Container, float> _ownedSince = new Dictionary<Container, float>();
        private const float ScenePassSeconds = 0.25f;
        private float _nextScenePass;

        // Dynamic and runtime containers: a ghost or a handover for an entity inside a carrier that has not arrived
        // here yet, or in a runtime container whose lease has not, waits by the container reference and is applied
        // when that container registers. Reliable ordering normally delivers the carrier first; this covers a
        // carrier that was never ghosted here, and a runtime container the control plane is still delivering.
        private readonly Dictionary<ContainerRef, List<EntitySpawnMsg>> _pendingGhostsByCarrier = new Dictionary<ContainerRef, List<EntitySpawnMsg>>();
        private readonly Dictionary<ContainerRef, List<PendingTransfer>> _pendingTransfersByCarrier = new Dictionary<ContainerRef, List<PendingTransfer>>();
        private readonly List<Container> _neighborScratch = new List<Container>();
        private readonly List<NetworkIdentity> _contentsScratch = new List<NetworkIdentity>();
        /// <summary>
        /// Snapshots of one container's contents for the recursive whole-subtree walks (a handoff, an
        /// evacuation). One shared scratch list cannot serve those: the recursion would refill the list the
        /// caller is still walking. Pooled, so a nested ship costs one list per level once and nothing after.
        /// </summary>
        private readonly Stack<List<NetworkIdentity>> _contentsPool = new Stack<List<NetworkIdentity>>();
        private readonly HashSet<string> _seenLeases = new HashSet<string>();
        private readonly List<ulong> _scratchIds = new List<ulong>();
        /// <summary>Runtime containers asked for before this worker was registered (a game mode's OnWorkerStarted); sent once it is.</summary>
        private readonly Dictionary<ulong, (Bounds Bounds, ContainerHint Hint, bool WriteHint, InstanceContainerInfo Instance)> _pendingRuntimeRequests = new Dictionary<ulong, (Bounds, ContainerHint, bool, InstanceContainerInfo)>();

        /// <summary>netId -> the entities ghosted to each worker this tick, rebuilt in <see cref="UpdateGhostBand"/>; lists are pooled.</summary>
        private readonly Dictionary<string, List<NetworkIdentity>> _ghostByWorker = new Dictionary<string, List<NetworkIdentity>>();
        private readonly Stack<List<NetworkIdentity>> _ghostListPool = new Stack<List<NetworkIdentity>>();
        /// <summary>Workers to re-ghost to, reused by <see cref="ResumeInheritedGhosts"/> so a handover allocates no list.</summary>
        private readonly List<ulong> _resumeCompleted = new List<ulong>();

        private readonly NetworkWriter _writer = new NetworkWriter(4096);
        /// <summary>The ghost band's sequenced batch, separate from <see cref="_writer"/> so a reliable message mid-batch needs no copy.</summary>
        private readonly NetworkWriter _ghostBatch = new NetworkWriter(1024);
        private readonly NetworkWriter _scratch = new NetworkWriter(1024);
        private readonly NetworkReader _reader = new NetworkReader();
        private readonly Stopwatch _tickWatch = new Stopwatch();

        // Tick profile: where the tick goes, logged every ProfileIntervalSeconds (see NebulaProfiler).
        private const float ProfileIntervalSeconds = 5f;
        private static readonly ProfileSection ProfPoll = NebulaProfiler.Section("poll");
        private static readonly ProfileSection ProfGhosts = NebulaProfiler.Section("ghosts");
        private static readonly ProfileSection ProfSyncTransforms = NebulaProfiler.Section("physics.sync");
        private static readonly ProfileSection ProfSimulate = NebulaProfiler.Section("simulate");
        private static readonly ProfileSection ProfRecordPose = NebulaProfiler.Section("poses");
        private static readonly ProfileSection ProfContainers = NebulaProfiler.Section("containers");
        private static readonly ProfileSection ProfBand = NebulaProfiler.Section("band");
        private static readonly ProfileSection ProfPublish = NebulaProfiler.Section("publish");
        private static readonly ProfileSection ProfGap = NebulaProfiler.Section("gap");
        private long _lastTickEnd;
        private float _nextProfileReport;
        private int _profileTicks;
        private int _profileFrames;
        private float _profileMaxMs;
        private double _profileTotalMs;
        private int _profileDuplicateTicks;
        private int _profileSkippedTicks;
        private uint _lastSimulatedTick;
        private int _lastGcCount;

        private ulong _nextSequence;
        /// <summary>
        /// The control-plane row for this worker, and the rule that puts it back when the control plane comes
        /// back without it (<see cref="WorkerRegistration"/>, docs/control-plane-availability.md D1).
        /// </summary>
        private readonly WorkerRegistration _registration = new WorkerRegistration();
        private bool _registered => _registration.IsRegistered;
        private float _nextHeartbeat;
        private float _nextUnownedWarning;
        private NebulaGameMode _gameMode;
        private WorkerTelemetry _telemetry;
        private WorkerScopeLifecycle _scopeLifecycle;
        /// <summary>What this worker reports to the dashboard's World map; null when telemetry is off (see <see cref="WorkerTelemetry.Create"/>).</summary>
        public WorkerTelemetry Telemetry => _telemetry;
        private readonly ContainerCostMeter _costMeter = new ContainerCostMeter();
        /// <summary>
        /// What each container this worker leases costs it, measured per tick (see <see cref="ContainerCostMeter"/>).
        /// Always on: it is two stopwatch reads and a field add per authoritative entity per tick, and the rows it
        /// produces are what the scaler and the dashboard explain a hot container with (docs/cost-telemetry.md).
        /// </summary>
        public ContainerCostMeter CostMeter => _costMeter;
        public IEnumerable<NetworkIdentity> Entities => _entities.Values;
        public IReadOnlyList<NetworkIdentity> Authoritative => _authoritative;

        public event Action<NetworkIdentity> EntitySpawned;
        public event Action<NetworkIdentity> EntityDespawned;
        public event Action<NetworkIdentity, string> AuthorityHandedOff;
        public event Action<NetworkIdentity, string> AuthorityReceived;

        // ---------------------------------------------------------------------------------------- worker messages
        //
        // Entities are the unit of authority, and AuthorityRpc reaches whichever worker owns one. Some things a game
        // wants to say to a neighbour are not about an entity this worker holds: "does this ray hit anything you
        // own", "is anyone standing in that room". Worker messages are the raw lateral channel for those: a game
        // picks a kind, registers a handler, and sends a payload it writes itself to a worker by id or index.

        /// <summary>Handler for a game-defined worker message. <paramref name="reader"/> is positioned at the payload.</summary>
        public delegate void WorkerMessageHandler(string fromWorkerId, ushort fromWorkerIndex, NetworkReader reader);

        private readonly Dictionary<ushort, WorkerMessageHandler> _messageHandlers = new Dictionary<ushort, WorkerMessageHandler>();
        private readonly NetworkWriter _messageWriter = new NetworkWriter(1024);

        /// <summary>
        /// Reaches whichever worker has authority over a <see cref="PersistentEntity"/> key, with a reply - even
        /// when this worker holds no ghost of the entity. See <see cref="EntityRequests"/>.
        /// </summary>
        private EntityRequests _entityRequests;

        /// <summary>Answer entity requests of <paramref name="kind"/> whenever this worker has authority over the target key. See <see cref="EntityRequests.RegisterHandler"/>.</summary>
        public void RegisterEntityRequestHandler(ushort kind, EntityRequests.Handler handler) => _entityRequests.RegisterHandler(kind, handler);

        /// <summary>Ask whichever worker has authority over <paramref name="persistentKey"/> and get a reply. See <see cref="EntityRequests.Request"/>.</summary>
        public uint RequestEntity(string persistentKey, ushort kind, byte[] payload, Action<EntityRequestResult> onDone, float timeoutSeconds = 5f) =>
            _entityRequests.Request(persistentKey, kind, payload, onDone, timeoutSeconds);

        /// <summary>Receive worker messages of <paramref name="kind"/>. One handler per kind; registering again replaces it.</summary>
        public void RegisterMessageHandler(ushort kind, WorkerMessageHandler handler)
        {
            if (handler == null) _messageHandlers.Remove(kind);
            else _messageHandlers[kind] = handler;
        }

        public void UnregisterMessageHandler(ushort kind) => _messageHandlers.Remove(kind);

        /// <summary>Whether the direct connection to <paramref name="workerId"/> is ready.</summary>
        public bool IsWorkerConnected(string workerId) => !string.IsNullOrEmpty(workerId) && _workerPeersById.TryGetValue(workerId, out var p) && p.HelloReceived;

        /// <summary>IDs of the workers that currently have a direct connection to this worker.</summary>
        public IEnumerable<string> ConnectedWorkerIds
        {
            get { foreach (var p in _workerPeersById.Values) if (p.HelloReceived) yield return p.Id; }
        }

        /// <summary>Indices of the workers that currently have a direct connection to this worker.</summary>
        public IEnumerable<ushort> ConnectedWorkerIndices
        {
            get { foreach (var p in _workerPeersById.Values) if (p.HelloReceived) yield return (ushort)p.Index; }
        }

        /// <summary>
        /// Send a game-defined message to <paramref name="workerId"/>. <paramref name="write"/> serialises the payload;
        /// the handler registered for <paramref name="kind"/> on the other side reads it back. False (and nothing
        /// sent) when that worker is not connected. Sending to this worker's own id invokes the handler directly.
        /// </summary>
        public bool SendToWorker(string workerId, ushort kind, Action<NetworkWriter> write, Delivery delivery = Delivery.ReliableOrdered)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            if (workerId == WorkerId)
            {
                if (!_messageHandlers.TryGetValue(kind, out var local)) return false;
                _messageWriter.Reset();
                write(_messageWriter);
                _reader.Set(_messageWriter.ToSegment());
                try { local(WorkerId, WorkerIndex, _reader); }
                catch (Exception ex) { NebulaLog.Error($"worker message {kind} handler threw: {ex}"); }
                return true;
            }
            if (!_workerPeersById.TryGetValue(workerId, out var peer) || !peer.HelloReceived) return false;
            _messageWriter.Reset();
            _messageWriter.WriteByte((byte)MsgId.WorkerMessage);
            _messageWriter.WriteUShort(kind);
            write(_messageWriter);
            _transport.Send(peer.PeerId, delivery, _messageWriter.ToSegment());
            return true;
        }

        /// <summary>As <see cref="SendToWorker(string, ushort, Action{NetworkWriter}, Delivery)"/>, addressed by worker index.</summary>
        public bool SendToWorker(ushort workerIndex, ushort kind, Action<NetworkWriter> write, Delivery delivery = Delivery.ReliableOrdered)
        {
            if (workerIndex == WorkerIndex) return SendToWorker(WorkerId, kind, write, delivery);
            return _workerPeersByIndex.TryGetValue(workerIndex, out var peer) && SendToWorker(peer.Id, kind, write, delivery);
        }

        private void OnWorkerMessage(Peer from, NetworkReader r)
        {
            ushort kind = r.ReadUShort();
            if (!_messageHandlers.TryGetValue(kind, out var handler))
            {
                NebulaLog.Warn($"worker message {kind} from {from.Id} has no handler");
                return;
            }
            try { handler(from.Id, (ushort)from.Index, r); }
            catch (Exception ex) { NebulaLog.Error($"worker message {kind} handler threw: {ex}"); }
        }

        // ---------------------------------------------------------------------------------------- lifecycle

        public void Initialize(NebulaConfig config, IControlPlane controlPlane)
        {
            Initialize(config, controlPlane, null);
        }

        /// <summary>
        /// Start the worker with a persistence store: entities carrying a <see cref="PersistentEntity"/> are
        /// checkpointed into it and restored from it (see <see cref="Persistence"/>). A null store turns persistence
        /// off; an unreachable one only delays the saves, it never stops the simulation.
        /// </summary>
        public void Initialize(NebulaConfig config, IControlPlane controlPlane, IPersistenceStore persistenceStore)
        {
            Config = config;
            ControlPlane = controlPlane;
            WorkerIndex = (ushort)CommandLine.GetInt("nebula-worker-index", 1);
            WorkerId = CommandLine.Get("nebula-worker-id", $"w{WorkerIndex}");
            Incarnation = SessionIds.NewIncarnation();
            _peerKey = string.IsNullOrEmpty(config.MeshToken) ? null : MeshPeerAuth.DeriveKey(config.MeshToken);
            Port = (ushort)CommandLine.GetInt("nebula-port", config.WorkerBasePort + WorkerIndex);
            NebulaRuntime.LocalWorkerId = WorkerId;
            NebulaRuntime.LocalWorkerIndex = WorkerIndex;
            NebulaRuntime.RpcSink = this;
            StateHistory.WindowTicks = Mathf.Clamp(config.StateHistoryTicks, 0, StateHistory.MaxWindowTicks);
            _callRouter = new AuthorityCallRouter(config.AuthorityCallMaxHops);
            _callTracker = new AuthorityCallTracker(WorkerIndex, AuthorityCallId.InitialSequence(Incarnation));
            InitializeInterest();

            _gameMode = FindFirstObjectByType<NebulaGameMode>();
            if (_gameMode == null) NebulaLog.Warn("No NebulaGameMode in the scene; players cannot be spawned");

            _transport = new LiteNetTransport($"worker:{WorkerId}");
            _transport.Listen(Port);
            IsListening = true;
            _entityRequests = new EntityRequests(this, key => Persistence?.Find(key), () => ConnectedWorkerIndices);
            // The row this worker asks the control plane for, settled before anything can read the document: the
            // port is known only now, and OnControlPlaneChanged may fire before the first Update.
            _registration.WorkerId = WorkerId;
            _registration.WorkerIndex = WorkerIndex;
            _registration.Address = Config.WorkerAdvertiseAddress;
            _registration.Port = Port;
            ControlPlane.Changed += OnControlPlaneChanged;
            ContainerRegistry.LeasesChanged += OnLeasesChanged;
            ContainerRegistry.DynamicRegistered += OnLateContainerRegistered;
            ContainerRegistry.RuntimeRegistered += OnLateContainerRegistered;
            ContainerRegistry.WorkerIdByIndex = ResolveWorkerId;
            SceneEntities.Registered += OnSceneEntityRegistered;
            SceneEntities.Unregistering += OnSceneEntityUnregistering;
            OnLeasesChanged();
            if (persistenceStore != null)
            {
                Persistence = new NebulaPersistence(this, config, persistenceStore);
                // A scope's parts do not restore before the game has been told the scope is coming to life
                // (NebulaLifecycle.OnScopeActivating); the gate is free when nothing is listening.
                Persistence.RestoreGate = ScopeLifecycleAgent.MayRestore;
                NebulaLog.Info($"persistence: {persistenceStore.Backend} store, checkpoint every {config.PersistenceCheckpointSeconds:0.#}s");
            }
            var boot = NebulaBootstrap.Instance;
            _telemetry = WorkerTelemetry.Create(boot != null && boot.Orchestrator != null ? boot.Orchestrator.Telemetry : null);
            NebulaLog.Info($"worker {WorkerId} (index {WorkerIndex}) listening on udp/{Port}; telemetry {(_telemetry == null ? "off" : _telemetry.Url == "" ? "to the orchestrator in this process" : "to " + _telemetry.Url)}");
            _gameMode?.OnWorkerStarted(this);
        }

        /// <summary>
        /// Keep the control-plane lease of every carried container this worker's carriers hold in step: a dynamic
        /// container's lease follows its carrier (this worker assigns it to itself) unless the orchestrator has
        /// pinned it to a worker of its own, in which case the row is left alone. Called when authority over a
        /// carrier is gained and whenever the control plane changes; a no-op when nothing is out of date.
        /// </summary>
        private void SyncCarriedLeases()
        {
            if (!_registered || !ControlPlane.IsConnected) return;
            for (int i = 0; i < _authoritative.Count; i++)
            {
                var carried = _authoritative[i].Carried;
                if (carried == null || !carried.IsDynamic) continue;
                var lease = ControlPlane.FindLease(carried.ContainerId);
                if (lease == null) { ControlPlane.EnsureContainer(carried.ContainerId); ControlPlane.AssignContainer(carried.ContainerId, WorkerId); continue; }
                if (lease.State == LeaseState.Pinned) continue;
                if (lease.WorkerId != WorkerId || lease.State != LeaseState.Active) ControlPlane.AssignContainer(carried.ContainerId, WorkerId);
            }
        }

        // ---------------------------------------------------------------------------------------- runtime containers

        /// <summary>
        /// Ask the mesh for a runtime container: a static box, named by an id the game chose, that is not in the baked
        /// set (a chunk of a landscape that is decided at runtime). <paramref name="frameBounds"/> is the box in this
        /// process's frame. The lease row is created already assigned to this worker, so the container is simulated
        /// here from the first change anyone sees; if another worker asked first, its row stands and this call is a
        /// no-op. Every process registers the container from the row (<see cref="ContainerRegistry.SyncRuntime"/>),
        /// and <see cref="ContainerRegistry.RuntimeRegistered"/> fires here once it has. Idempotent: call it every
        /// time an entity approaches the box.
        /// <para>
        /// This overload says nothing about the hint, so it never touches one: a hint set from the dashboard or by
        /// the three-argument overload survives every later approach. Use that overload to change it.
        /// </para>
        /// </summary>
        public void RequestRuntimeContainer(ulong id, Bounds frameBounds) => Request(id, frameBounds, ContainerHint.Default, writeHint: false, instance: null);

        /// <summary>
        /// Ask for a runtime container that belongs to a scope rather than to the public world: a chunk of a scoped
        /// grid (<c>docs/scoped-chunk-grids.md</c>). <paramref name="instance"/> is what makes the lease row carry
        /// the scope — its isolation id, its key and the part id the coordinate is — so every role that mirrors the
        /// row registers the container in that scope and nothing in it ever ghosts, spawns or is announced across
        /// the scope boundary. It is written only when the row is created; a row that exists keeps the scope it was
        /// born with.
        /// </summary>
        public void RequestRuntimeContainer(ulong id, Bounds frameBounds, InstanceContainerInfo instance) =>
            Request(id, frameBounds, ContainerHint.Default, writeHint: false, instance: instance);

        /// <summary>
        /// Ask for a runtime container and tell the planner what kind of box it is in the same breath
        /// (<see cref="ContainerHint"/>): a chunk holding a boss arena is <c>Dedicated</c>, the rooms of one dungeon
        /// share an <c>AffinityGroup</c>. The hint is written to the lease row, so every orchestrator sees it and a
        /// restart does not lose it. A default hint writes nothing. Idempotent like the two-argument overload; the
        /// hint is re-applied when it differs from the row, so a game may raise and lower it as the box heats up.
        /// </summary>
        public void RequestRuntimeContainer(ulong id, Bounds frameBounds, in ContainerHint hint) => Request(id, frameBounds, hint, writeHint: true, instance: null);

        /// <summary>
        /// Should this approach write the hint row? Only a caller that actually named a hint may, and only when what
        /// the row says differs from what was asked for - the call is made every time an entity comes near the box,
        /// and a write per tick would be a control-plane write per tick. A caller that named none leaves the row
        /// alone whatever is in it, so the dashboard's hint (or an earlier explicit one) is not erased by the next
        /// approach. Pure function.
        /// </summary>
        /// <param name="explicitHint">The caller passed a hint (the three-argument overload), rather than defaulting.</param>
        /// <param name="rowHasHint">The lease row carries a hint today.</param>
        /// <param name="rowHint">What the row says (ignored when <paramref name="rowHasHint"/> is false).</param>
        /// <param name="wanted">The hint the caller asked for.</param>
        public static bool ShouldWriteHint(bool explicitHint, bool rowHasHint, in ContainerHint rowHint, in ContainerHint wanted)
        {
            if (!explicitHint) return false;
            if (rowHasHint != !wanted.IsDefault) return true;
            return rowHasHint && rowHint != wanted;
        }

        private void Request(ulong id, Bounds frameBounds, in ContainerHint hint, bool writeHint, InstanceContainerInfo instance)
        {
            if (!_registered || !ControlPlane.IsConnected)
            {
                _pendingRuntimeRequests[id] = (frameBounds, hint, writeHint, instance); // OnWorkerStarted runs before registration; ask as soon as we can
                return;
            }
            string containerId = ContainerRegistry.RuntimeContainerId(id);
            var lease = ControlPlane.FindLease(containerId);
            if (lease == null)
            {
                // The box on the lease row is absolute, and a scope with an origin frame of its own converts
                // through that frame, not through the public world's (docs/scope-frames.md D4).
                ControlPlane.EnsureRuntimeContainer(containerId, ContainerRegistry.ToAbsolute(frameBounds, instance?.InstanceId ?? 0UL), WorkerId, instance);
                if (writeHint && !hint.IsDefault) ControlPlane.SetContainerHint(containerId, hint);
                return;
            }
            if (ShouldWriteHint(writeHint, lease.HasHint, lease.Hint, hint)) ControlPlane.SetContainerHint(containerId, hint);
            // Somebody else owns it and we still want it: say so now and then, so the owner's idle clock does not run out.
            if (lease.WorkerId != WorkerId && (ControlPlane.Now - lease.UpdatedAt).TotalSeconds >= RuntimeTouchSeconds) ControlPlane.TouchContainer(containerId);
        }

        /// <summary>How often a worker re-stamps a runtime container it wants but does not own (see <see cref="RuntimeContainerIdleSeconds"/>).</summary>
        public const float RuntimeTouchSeconds = 15f;

        /// <summary>
        /// Seconds since anyone in the mesh last asked for or changed a runtime container: its lease row's age.
        /// Every worker that wants the box re-stamps the row while an entity of its is near it, so the owner can
        /// retire a box only once this exceeds its grace period. Infinity when the container is unknown.
        /// </summary>
        public double RuntimeContainerIdleSeconds(ulong id)
        {
            var lease = ControlPlane.FindLease(ContainerRegistry.RuntimeContainerId(id));
            return lease == null ? double.PositiveInfinity : (ControlPlane.Now - lease.UpdatedAt).TotalSeconds;
        }

        private void FlushRuntimeRequests()
        {
            if (_pendingRuntimeRequests.Count == 0 || !_registered || !ControlPlane.IsConnected) return;
            foreach (var kv in _pendingRuntimeRequests) Request(kv.Key, kv.Value.Bounds, kv.Value.Hint, kv.Value.WriteHint, kv.Value.Instance);
            _pendingRuntimeRequests.Clear();
        }

        /// <summary>
        /// Retire a runtime container this worker owns: its lease row is deleted, so every process forgets the box.
        /// Persistent entities still inside are checkpointed and despawned (they come back when the box is asked
        /// for again); anything else inside is despawned for good. Returns false when the container is not here or
        /// belongs to another worker.
        /// </summary>
        public bool ReleaseRuntimeContainer(ulong id)
        {
            var c = ContainerRegistry.GetRuntime(id);
            if (c == null || !c.IsOwnedBy(WorkerId)) return false;
            if (!_registered || !ControlPlane.IsConnected) return false;
            EmptyContainer(c);
            ControlPlane.RemoveContainer(c.ContainerId);
            return true;
        }

        /// <summary>
        /// Despawn everything this worker is authoritative for inside <paramref name="container"/>, keeping the
        /// records of persistent entities (they come back when the box is asked for again) and losing everything
        /// else. The contents half of <see cref="ReleaseRuntimeContainer"/>, split out because the scope lifecycle
        /// empties a part without deleting its lease row — the orchestrator deletes the rows, and only once every
        /// part has reported its checkpoint done (<c>docs/scope-lifecycle.md</c>).
        /// </summary>
        public void EmptyContainer(Container container)
        {
            if (container == null) return;
            _contentsScratch.Clear();
            _contentsScratch.AddRange(container.Entities);
            foreach (var e in _contentsScratch)
            {
                if (e == null || !e.HasAuthority) continue;
                Despawn(e, keepPersisted: e.Persistent != null);
            }
            _contentsScratch.Clear();
        }

        /// <summary>Worker id for a worker index: this worker, or a connected peer. Dynamic containers derive their owner through this.</summary>
        private string ResolveWorkerId(ushort index)
        {
            if (index == WorkerIndex) return WorkerId;
            return _workerPeersByIndex.TryGetValue(index, out var p) ? p.Id : "";
        }

        private void OnDestroy()
        {
            _pendingPlayerSpawns.Clear();
            _entityRequests?.Dispose();
            // Save what we own before anything is torn down; the records stay, the entities come back elsewhere.
            Persistence?.Shutdown();
            ContainerRegistry.LeasesChanged -= OnLeasesChanged;
            ContainerRegistry.DynamicRegistered -= OnLateContainerRegistered;
            ContainerRegistry.RuntimeRegistered -= OnLateContainerRegistered;
            SceneEntities.Registered -= OnSceneEntityRegistered;
            SceneEntities.Unregistering -= OnSceneEntityUnregistering;
            if (ControlPlane != null)
            {
                ControlPlane.Changed -= OnControlPlaneChanged;
                if (_registered && ControlPlane.IsConnected)
                {
                    try { ControlPlane.UnregisterWorker(WorkerId); } catch (Exception e) { NebulaLog.Warn($"unregister failed: {e.Message}"); }
                }
                _registration.Forget();
            }
            _transport?.Dispose();
            _telemetry?.Dispose();
        }

        private void Update()
        {
            ProfPoll.Begin();
            _transport.Poll(HandleTransportEvent);
            ProfPoll.End();
            _profileFrames++;
            if (Time.unscaledTime >= _nextProfileReport)
            {
                _nextProfileReport = Time.unscaledTime + ProfileIntervalSeconds;
                ReportProfile();
            }

            if (_registration.Register(ControlPlane))
            {
                _nextHeartbeat = 0f;
                NebulaLog.Info($"registered with control plane as {WorkerId}");
                FlushRuntimeRequests();
            }
            if (_registered && Time.unscaledTime >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.unscaledTime + Config.WorkerHeartbeatSeconds;
                ControlPlane.HeartbeatWorker(WorkerId, WorkerStatus.Ready, CollectStats());
            }
            if (_registered) _telemetry?.Update(this);
            if (_registered) ScopeLifecycleAgent.Update();
            ExpireSessions();
            if (_registered && Time.unscaledTime >= _nextScenePass)
            {
                _nextScenePass = Time.unscaledTime + ScenePassSeconds;
                SpawnSceneEntities();
            }
            // Checkpoints and restores run here, off the tick, bounded per frame.
            Persistence?.Update();
            _entityRequests?.Update();
            _callRouter?.Ledger.Expire(CurrentTick);
            _callTracker?.Expire(CurrentTick);
        }

        // ---------------------------------------------------------------------------------------- scene entities

        private void OnLeasesChanged()
        {
            float now = Time.unscaledTime;
            DateOwned(ContainerRegistry.All, now);
            DateOwned(ContainerRegistry.Runtime, now);
        }

        private void DateOwned(IReadOnlyList<Container> containers, float now)
        {
            for (int i = 0; i < containers.Count; i++)
            {
                var c = containers[i];
                if (c.IsOwnedBy(WorkerId)) { if (!_ownedSince.ContainsKey(c)) _ownedSince[c] = now; }
                else _ownedSince.Remove(c);
            }
        }

        /// <summary>
        /// Spawn the resident scene entities standing in containers this worker leases and nobody has spawned yet.
        /// A lease must be <see cref="NebulaConfig.SceneEntityGraceSeconds"/> old first: when a lease moves, the
        /// previous owner hands the entity over (or its ghost is already here), and that binding must win over a
        /// second life. Nothing is spawned into an unloaded cell: the object is not here to spawn.
        /// </summary>
        private void SpawnSceneEntities()
        {
            if (SceneEntities.Count == 0) return;
            float now = Time.unscaledTime;
            foreach (var e in SceneEntities.All)
            {
                if (e.IsSpawned || e.NetId != 0) continue;
                if (_pendingSceneTransfers.ContainsKey(e.SceneId) || _pendingSceneGhosts.ContainsKey(e.SceneId)) continue;
                var c = ContainerRegistry.Find(e.transform.position);
                if (c == null || !c.IsOwnedBy(WorkerId)) continue;
                if (!_ownedSince.TryGetValue(c, out float since) || now - since < Config.SceneEntityGraceSeconds) continue;
                // A persistent scene object comes back as it was saved, at the epoch after the one that saved it.
                uint epoch = Persistence != null ? Persistence.PrepareSceneEntity(e) : 0;
                if (epoch != 0) Spawn(e, c, 0, false, epoch);
                else Spawn(e, c);
            }
        }

        /// <summary>A scene object became resident: apply the handover or ghost that was waiting for it.</summary>
        private void OnSceneEntityRegistered(NetworkIdentity e)
        {
            if (_pendingSceneTransfers.TryGetValue(e.SceneId, out var transfer))
            {
                _pendingSceneTransfers.Remove(e.SceneId);
                _pendingSceneGhosts.Remove(e.SceneId);
                OnAuthorityTransfer(transfer.From, transfer.Msg);
                return;
            }
            if (_pendingSceneGhosts.TryGetValue(e.SceneId, out var ghost))
            {
                _pendingSceneGhosts.Remove(e.SceneId);
                InstantiateGhost(ghost);
            }
        }

        /// <summary>
        /// A bound scene object is leaving with its scene. The authority despawns it for the mesh (a re-lease spawns
        /// it again); a ghost is simply forgotten.
        /// </summary>
        private void OnSceneEntityUnregistering(NetworkIdentity e)
        {
            if (e.NetId == 0 || !_entities.ContainsKey(e.NetId)) return;
            if (e.HasAuthority)
            {
                NebulaLog.Info($"scene entity {e} unloaded with its cell; despawning");
                Despawn(e, keepPersisted: true); // the cell left, the object did not cease to exist
            }
            else RemoveLocal(e);
        }

        /// <summary>
        /// The resident scene object a spawn refers to, ready to bind. Null, and the spawn remembered, while its
        /// scene is not loaded here. A stale binding to another id (a duplicate we spawned because the previous
        /// owner's handover outran the grace period, or a ghost of a life that ended) is dropped first: the incoming
        /// one is the life the rest of the mesh knows.
        /// </summary>
        private NetworkIdentity BindSceneEntity(EntitySpawnMsg msg)
        {
            var identity = SceneEntities.Find(msg.SceneId);
            if (identity == null)
            {
                _pendingSceneGhosts[msg.SceneId] = msg;
                return null;
            }
            if (identity.NetId != 0 && identity.NetId != msg.NetId)
            {
                NebulaLog.Warn($"scene entity {identity} is being rebound to #{msg.NetId}; dropping the local life");
                if (identity.HasAuthority) Despawn(identity); else RemoveLocal(identity);
            }
            return identity;
        }

        private void DropPendingScene(ulong netId)
        {
            uint sceneId = 0;
            foreach (var kv in _pendingSceneGhosts) if (kv.Value.NetId == netId) { sceneId = kv.Key; break; }
            if (sceneId != 0) _pendingSceneGhosts.Remove(sceneId);
            sceneId = 0;
            foreach (var kv in _pendingSceneTransfers) if (kv.Value.Msg.Entity.NetId == netId) { sceneId = kv.Key; break; }
            if (sceneId != 0) _pendingSceneTransfers.Remove(sceneId);
        }

        /// <summary>
        /// Every container's owner is connected to this worker (or is this worker). Game code that spawns into
        /// containers it does not own should wait for this: the immediate handover needs a target.
        /// </summary>
        public bool MeshReady
        {
            get
            {
                return OwnersConnected(ContainerRegistry.All) && OwnersConnected(ContainerRegistry.Runtime);
            }
        }

        private bool OwnersConnected(IReadOnlyList<Container> containers)
        {
            for (int i = 0; i < containers.Count; i++)
            {
                string owner = containers[i].OwnerWorkerId;
                if (string.IsNullOrEmpty(owner)) return false;
                if (owner != WorkerId && !(_workerPeersById.TryGetValue(owner, out var p) && p.HelloReceived)) return false;
            }
            return true;
        }

        /// <summary>Registered with the control plane; the mesh knows about this worker.</summary>
        public bool IsRegistered => _registered;

        private WorkerStats CollectStats()
        {
            int players = 0, bots = 0, serverDriven = 0;
            for (int i = 0; i < _authoritative.Count; i++)
            {
                var e = _authoritative[i];
                if (e.IsServerDriven) serverDriven++;
                else if (e.OwnerClientId == 0) continue;
                else if (e.OwnerIsBot) bots++;
                else players++;
            }
            PlayerCount = players;
            BotCount = bots;
            ServerDrivenCount = serverDriven;
            return new WorkerStats
            {
                TickCount = TickCount,
                TickMs = TickMs,
                EntityCount = (uint)_entities.Count,
                AuthoritativeCount = (uint)_authoritative.Count,
                GhostCount = (uint)(_entities.Count - _authoritative.Count),
                PlayerCount = (uint)players,
                BotCount = (uint)bots,
                ServerDrivenCount = (uint)serverDriven,
                HasGlobalEntities = HasGlobalEntities,
                OldestDirtySeconds = Persistence?.OldestDirtyAgeSeconds ?? 0f,
            };
        }

        /// <summary>
        /// Most ticks the loop is allowed to simulate in one FixedUpdate to catch up with the wall clock. Beyond
        /// this the missing tick numbers are skipped (everything runs a little slow for a moment) rather than
        /// simulated back-to-back, which would only push the next frame further behind.
        /// </summary>
        public const int MaxCatchUpTicks = 2;

        private void FixedUpdate()
        {
            // The tick number comes from the wall clock, not from counting FixedUpdates (see NetworkTime): every
            // worker agrees on it with no tick master. So this loop simulates exactly the tick numbers that have
            // elapsed since the last one, at most MaxCatchUpTicks of them. Unity's own catch-up (several
            // FixedUpdates after a slow frame) then finds nothing left to do instead of simulating one tick number
            // twice, which used to move every NPC two steps under one label and push two samples with the same
            // tick to every ghost.
            uint derived = NetworkTime.DerivedTick;
            if (_lastSimulatedTick == 0) { RunTick(derived); return; }
            if (derived <= _lastSimulatedTick) { _profileDuplicateTicks++; return; }
            uint behind = derived - _lastSimulatedTick;
            if (behind > MaxCatchUpTicks)
            {
                _profileSkippedTicks += (int)(behind - MaxCatchUpTicks);
                _lastSimulatedTick = derived - MaxCatchUpTicks;
            }
            while (_lastSimulatedTick < derived) RunTick(_lastSimulatedTick + 1);
        }

        private void RunTick(uint tick)
        {
            // Everything between two ticks that is not ours: the physics step, Update (transport poll, control
            // plane), the engine's own frame work. Reported as 'gap' so the tick's share of the core is visible.
            long start = Stopwatch.GetTimestamp();
            if (_lastTickEnd != 0) ProfGap.Elapsed += start - _lastTickEnd;
            ProfGap.Calls++;
            _tickWatch.Restart();
            Tick(tick);
            _tickWatch.Stop();
            _lastTickEnd = Stopwatch.GetTimestamp();
            _lastSimulatedTick = tick;
            float ms = (float)_tickWatch.Elapsed.TotalMilliseconds;
            TickMs = TickMs <= 0f ? ms : Mathf.Lerp(TickMs, ms, 0.05f);
            _profileTicks++;
            _profileTotalMs += ms;
            if (ms > _profileMaxMs) _profileMaxMs = ms;
        }

        /// <summary>
        /// One line every few seconds with the tick budget and where it went: ticks simulated in the window (60/s
        /// when keeping up), average and worst tick, how many FixedUpdates found no new tick to simulate ('dup':
        /// Unity catching up after a slow frame) and how many tick numbers were skipped ('skip': the process fell
        /// further behind the wall clock than <see cref="MaxCatchUpTicks"/>), and every profiler section in ms per
        /// tick (see <see cref="NebulaProfiler"/>).
        /// </summary>
        private void ReportProfile()
        {
            if (_profileTicks == 0) return;
            float avg = (float)(_profileTotalMs / _profileTicks);
            int gc = GC.CollectionCount(0);
            int gcs = gc - _lastGcCount;
            _lastGcCount = gc;
            NebulaLog.Info($"profile {_profileTicks} ticks/{ProfileIntervalSeconds:0}s {_profileFrames} frames avg {avg:0.0}ms max {_profileMaxMs:0.0}ms dup {_profileDuplicateTicks} skip {_profileSkippedTicks} gc {gcs} auth {_authoritative.Count} ghosts {_entities.Count - _authoritative.Count} rpcRejected {NebulaDiagnostics.RejectedAuthorityRpcSends} | {NebulaProfiler.ReportAndReset(_profileTicks)}");
            _profileTicks = 0;
            _profileFrames = 0;
            _profileMaxMs = 0f;
            _profileTotalMs = 0;
            _profileDuplicateTicks = 0;
            _profileSkippedTicks = 0;
        }

        // ---------------------------------------------------------------------------------------- the tick

        private void Tick(uint tick)
        {
            CurrentTick = tick;
            NetworkTime.Tick = tick;
            TickCount++;
            _costMeter.CountTick();
            float dt = NetworkTime.TickInterval;
            UpdateInstancePreparations();
            ContainerRegistry.RefreshCaches();

            // 1. Ghosts follow the stream they are driven by (kinematic: no solve of their own).
            ProfGhosts.Begin();
            double renderTick = tick - 1.0;
            NetworkTime.RenderTick = renderTick;
            foreach (var e in _entities.Values)
            {
                if (e.HasAuthority) continue;
                e.RemoteTick(renderTick);
            }
            ProfGhosts.End();
            // Ghost colliders must be where the stream says before anyone raycasts against them this tick.
            ProfSyncTransforms.Begin();
            Physics.SyncTransforms();
            ProfSyncTransforms.End();

            // 2. Simulate what we own: carriers before their contents (by nesting depth), with the physics scene
            // brought up to date in between. A pawn standing in a ship casts against the hull's colliders, and those
            // have to be where the hull moved to this tick: otherwise the cockpit of a ship at speed sweeps through
            // the pawn a tick late and the depenetration shoves the pawn out of the ship.
            ProfSimulate.Begin();
            // However deep the nesting goes (design D71): a constant here would tick the bottom of a deep ship
            // out of order, or - worse - in the same pass as the hull it is standing on. There cannot be more
            // distinct depths than entities, so the set we own is the bound, and the loop leaves as soon as
            // nothing deeper is left.
            for (int depth = 0; depth <= _authoritative.Count; depth++)
            {
                bool deeper = false, carrierTicked = false;
                for (int i = 0; i < _authoritative.Count; i++)
                {
                    var e = _authoritative[i];
                    int d = e.Container != null ? e.Container.NestingDepth : 0;
                    if (d > depth) { deeper = true; continue; }
                    if (d < depth) continue;
                    var behaviours = e.Behaviours;
                    // Two stopwatch reads per entity per tick, so what a container costs is measured rather than
                    // guessed from what is standing in it (docs/cost-telemetry.md, D5).
                    long simStart = Stopwatch.GetTimestamp();
                    for (int b = 0; b < behaviours.Length; b++)
                    {
                        try { behaviours[b].NetworkTick(tick, dt); }
                        catch (Exception ex) { NebulaLog.Error($"NetworkTick on {e} threw: {ex}"); }
                    }
                    _costMeter.AddSimulation(e.Container, Stopwatch.GetTimestamp() - simStart);
                    if (e.Carried != null) carrierTicked = true;
                }
                if (!deeper) break;
                if (carrierTicked)
                {
                    ProfSyncTransforms.Begin();
                    Physics.SyncTransforms();
                    ProfSyncTransforms.End();
                }
            }
            ProfSimulate.End();
            InstanceScenes.Simulate(dt);

            // Remember where what we simulate ended up this tick, for lag-compensated hit tests and time-sensitive
            // validation (docs/state-history.md). Ghosts record themselves when the owner's stream is applied, with
            // the owner's tick, so a ghost entry is never tagged with a tick this worker invented. An identity whose
            // object was destroyed behind our back (game code, a scene unload) is dropped here rather than allowed to
            // throw: an exception at this point would skip the publish below and blind every client.
            ProfRecordPose.Begin();
            foreach (var e in _entities.Values)
            {
                if (e == null) { _scratchEntities.Add(e); continue; }
                if (e.HasAuthority) e.RecordAuthoritativeState(tick);
            }
            if (_scratchEntities.Count > 0) PurgeDestroyed();
            ProfRecordPose.End();

            // 3. Container membership (with hysteresis) and authority transfers.
            ProfContainers.Begin();
            _scratchEntities.Clear();
            _scratchEntities.AddRange(_authoritative);
            foreach (var e in _scratchEntities)
            {
                if (!e.HasAuthority) continue; // handed over as the contents of a carrier earlier in this pass
                _gameMode?.PrepareSpatialFrame(e);
                InstanceBoundary.Tick(this, e);
                // A carrier never resolves into the container it carries (its origin is inside its own box).
                var resolved = ContainerRegistry.Resolve(e.transform.position, e.Container, Config.HandoverHysteresis, e.Carried);
                if (resolved != e.Container)
                {
                    var previous = e.Container;
                    e.SetContainer(resolved);
                    if (resolved != null && (resolved.OwnerWorkerId == WorkerId))
                    {
                        LocalHandovers++;
                        NebulaLog.Debugf($"local handover {e} {previous?.ContainerId} -> {resolved.ContainerId}");
                    }
                }
                var owner = e.Container != null ? e.Container.OwnerWorkerId : "";
                if (string.IsNullOrEmpty(owner) || owner == WorkerId) continue;
                if (_workerPeersById.TryGetValue(owner, out var peer) && peer.HelloReceived)
                {
                    TransferAuthority(e, peer);
                }
                else if (Time.unscaledTime >= _nextUnownedWarning)
                {
                    _nextUnownedWarning = Time.unscaledTime + 5f;
                    NebulaLog.Warn($"{e} is in {e.Container.ContainerId} owned by '{owner}' but that worker is not connected; keeping authority");
                }
            }
            ProfContainers.End();

            foreach (var e in _authoritative) e.PrepareReplication(tick);

            // 4. Ghost band: create neighboring copies before an entity can cross.
            ProfBand.Begin();
            UpdateGhostBand(tick);
            ProfBand.End();

            // 5. Decide who hears about what (interest management), stream to the gateway(s), clear dirty state.
            ProfInterest.Begin();
            _filterWatch.Restart();
            CacheOrigin();
            UpdateInterestIndex();
            float unscaled = Time.unscaledTime;
            EvaluateWideEntities(unscaled);
            _publisher.BuildGroups(_index);
            BuildPublishMasks();
            _filterWatch.Stop();
            float filterMs = (float)_filterWatch.Elapsed.TotalMilliseconds;
            InterestFilterMs = InterestFilterMs <= 0f ? filterMs : Mathf.Lerp(InterestFilterMs, filterMs, 0.05f);
            CheckPartitionWarning(unscaled);
            ProfInterest.End();

            ProfPublish.Begin();
            PublishToGateways(tick);
            foreach (var e in _authoritative) e.ClearDirty();
            _transport.Flush();
            ProfPublish.End();
        }

        /// <summary>Forget identities in <see cref="_scratchEntities"/> whose objects were destroyed without a despawn.</summary>
        private void PurgeDestroyed()
        {
            _scratchIds.Clear();
            foreach (var kv in _entities) if (kv.Value == null) _scratchIds.Add(kv.Key);
            NebulaLog.Warn($"{_scratchIds.Count} entity object(s) were destroyed without a despawn; forgetting them");
            foreach (var id in _scratchIds) { _entities.Remove(id); InterestRemove(id); }
            _authoritative.RemoveAll(e => e == null);
            _scratchEntities.Clear();
            _scratchIds.Clear();
        }

        // ---------------------------------------------------------------------------------------- spawning API

        /// <summary>Allocate an id and make <paramref name="identity"/> live on this worker. Call after instantiating a network prefab.</summary>
        public void Spawn(NetworkIdentity identity, Container container, ulong ownerClientId = 0)
        {
            Spawn(identity, container, ownerClientId, false);
        }

        /// <summary>
        /// Spawn a server-driven entity: one with no owning client whose <see cref="PredictedBehaviour{TInput}"/>
        /// (if any) gets its input from <c>GatherServerInput</c> on whichever worker holds authority. It ghosts and
        /// hands over like any other entity and costs nothing beyond the entity itself. The container need not be
        /// owned by this worker: the mesh hands the entity to its owner on the next tick (see <see cref="MeshReady"/>).
        /// </summary>
        public void SpawnServerDriven(NetworkIdentity identity, Container container)
        {
            Spawn(identity, container, 0, true);
        }

        private void Spawn(NetworkIdentity identity, Container container, ulong ownerClientId, bool serverDriven)
        {
            Spawn(identity, container, ownerClientId, serverDriven, 1);
        }

        /// <summary>
        /// Spawn an entity being brought back from the persistence store, at the epoch that follows the one the
        /// record was saved with, so anything still holding the old epoch is stale everywhere.
        /// </summary>
        internal void SpawnRestored(NetworkIdentity identity, Container container, PersistedEntityRecord record)
        {
            Spawn(identity, container, 0, record.ServerDriven, record.Epoch + 1);
        }

        /// <summary>
        /// The one spawn path. <paramref name="epoch"/> is 1 for a new entity and <c>record.Epoch + 1</c> for one
        /// restored from the store.
        /// </summary>
        private void Spawn(NetworkIdentity identity, Container container, ulong ownerClientId, bool serverDriven, uint epoch)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (identity.IsSpawned) throw new InvalidOperationException($"{identity} is already spawned");
            if (identity.PrefabId == ushort.MaxValue && !identity.IsSceneEntity)
            {
                NebulaLog.Error($"{identity.name} has no prefab id; instantiate it through NetworkPrefabs or register the prefab in NebulaConfig");
            }
            identity.Initialize();
            identity.NetId = ((ulong)WorkerIndex << 48) | (++_nextSequence);
            identity.Epoch = epoch == 0 ? 1 : epoch;
            identity.OwnerClientId = ownerClientId;
            identity.OwnerIdentity = ownerClientId != 0 && _playerIdentities.TryGetValue(ownerClientId, out var playerIdentity) ? playerIdentity : "";
            identity.OwnerIsBot = ownerClientId != 0 && _botClients.Contains(ownerClientId);
            identity.IsServerDriven = serverDriven && ownerClientId == 0;
            identity.OwnerWorkerIndex = WorkerIndex;
            identity.RecomputeCostWeight(); // once, here: never per tick (see NebulaCost)
            identity.HasAuthority = true;
            identity.SetContainer(container ?? ContainerRegistry.Find(identity.transform.position));
            _entities[identity.NetId] = identity;
            _authoritative.Add(identity);
            if (ownerClientId != 0) _players[ownerClientId] = identity;
            if (!identity.gameObject.activeSelf) identity.gameObject.SetActive(true);
            identity.InvokeSpawn();
            foreach (var b in identity.Behaviours) b.OnGainedAuthority();
            identity.ClearDirty();
            PhysicsIslands.CheckOnAuthority(identity);
            if (identity.Carried != null) SyncCarriedLeases();

            // Index it before announcing: which gateways hear about a spawn is decided from where it landed.
            // If passengers were already waiting for this id, their reseating is published after the announce.
            InterestAddAndAnnounce(identity, null);
            EntitySpawned?.Invoke(identity);
            if (ownerClientId != 0) NebulaLog.Info($"spawned player {identity} for client {ownerClientId} in {identity.Container?.ContainerId}");
            else NebulaLog.Debugf($"spawned {identity}");
        }

        public NetworkIdentity SpawnPrefab(GameObject prefab, Vector3 position, Quaternion rotation, Container container, ulong ownerClientId = 0)
        {
            var id = NetworkPrefabs.IdOf(prefab);
            var identity = NetworkPrefabs.Instantiate(id, position, rotation, container != null ? container.transform : null);
            Spawn(identity, container, ownerClientId);
            return identity;
        }

        /// <summary>
        /// Remove an entity from the mesh. A persistent entity (<see cref="PersistentEntity"/>) is also forgotten by
        /// the store: this is the entity ceasing to exist, not merely leaving this process. Pass
        /// <paramref name="keepPersisted"/> true when the world should keep it (a player disconnecting, a cell
        /// unloading): its record is checkpointed one last time and left in place.
        /// </summary>
        public void Despawn(NetworkIdentity identity, bool keepPersisted = false)
        {
            if (identity == null || !_entities.ContainsKey(identity.NetId)) return;
            if (!identity.HasAuthority)
            {
                NebulaLog.Warn($"Despawn({identity}) called on a ghost; only the authority can despawn");
                return;
            }
            Persistence?.OnDespawning(identity, keepPersisted);
            var despawn = new EntityDespawnMsg { NetId = identity.NetId, Epoch = identity.Epoch };
            // Everyone who could know it: its region's subscribers, its owner's gateway, explicit subscribers, and
            // every link when it is global. A gateway that never heard of it simply ignores the despawn.
            ulong despawnMask = PublishMaskOf(identity);
            _writer.Reset();
            despawn.Write(_writer, MsgId.EntityDespawn);
            SendToMask(despawnMask, Delivery.ReliableOrdered);
            var carried = identity.Carried;
            if (carried != null && carried.IsDynamic && _registered && ControlPlane.IsConnected) ControlPlane.RemoveContainer(carried.ContainerId);
            if (_ghostTargets.TryGetValue(identity.NetId, out var targets))
            {
                foreach (var workerId in targets.Keys)
                {
                    if (_workerPeersById.TryGetValue(workerId, out var p)) { _writer.Reset(); despawn.Write(_writer, MsgId.GhostDespawn); Send(p, Delivery.ReliableOrdered); }
                }
                _ghostTargets.Remove(identity.NetId);
            }
            RemoveLocal(identity);
        }

        /// <summary>A snapshot of a container's contents from the pool, safe to walk while the walk recurses.</summary>
        private List<NetworkIdentity> BorrowContents(Container box)
        {
            var list = _contentsPool.Count > 0 ? _contentsPool.Pop() : new List<NetworkIdentity>(8);
            list.Clear();
            list.AddRange(box.Entities);
            return list;
        }

        private void ReturnContents(List<NetworkIdentity> list)
        {
            list.Clear();
            _contentsPool.Push(list);
        }

        /// <summary>
        /// Put down everything still riding in an entity that is leaving the world, in the container the entity
        /// itself sat in and at the pose it is standing in right now (<c>SetContainer</c> reparents with the world
        /// position kept). A passenger survives its carrier — an always-relevant crate whose ship is destroyed is
        /// global on its own account again — and the placement it gets back is published by
        /// <see cref="InterestRemove"/>, so this has to happen first: a spawn that still named the carrier would
        /// give every gateway a container reference that no longer resolves, and <c>ScopeContainer</c> fails
        /// closed, so the gateway would cache a record no client could ever be shown (design D86).
        /// <para>
        /// Only the direct contents are reparented. Anything deeper names a carrier that is still there, so every
        /// spawn in a surviving subtree names an existing container and the carrier-first order still holds.
        /// </para>
        /// </summary>
        private void EvacuateCarried(NetworkIdentity identity)
        {
            var box = identity.Carried;
            if (box == null || box.Entities.Count == 0) return;
            var replacement = identity.Container;
            var contents = BorrowContents(box);
            try
            {
                for (int i = 0; i < contents.Count; i++)
                {
                    var inner = contents[i];
                    if (inner == null || inner == identity) continue;
                    try { inner.SetContainer(replacement); }
                    catch (Exception ex) { NebulaLog.Error($"could not put {inner} down while {identity} left the world: {ex.Message}"); }
                }
            }
            finally { ReturnContents(contents); }
        }

        private void RemoveLocal(NetworkIdentity identity)
        {
            _entities.Remove(identity.NetId);
            _authoritative.Remove(identity);
            EvacuateCarried(identity);
            InterestRemove(identity.NetId);
            _handedOff.Remove(identity.NetId);
            if (identity.OwnerClientId != 0 && _players.TryGetValue(identity.OwnerClientId, out var p) && p == identity) _players.Remove(identity.OwnerClientId);
            identity.InvokeDespawn();
            EntityDespawned?.Invoke(identity);
            if (identity.IsSceneEntity) identity.Unbind(); // the object belongs to its scene
            else Destroy(identity.gameObject);
        }

        public NetworkIdentity Find(ulong netId) => _entities.TryGetValue(netId, out var e) ? e : null;

        /// <summary>
        /// What the entity <paramref name="netId"/> looked like at server tick <paramref name="tick"/>, from this
        /// worker's own recorded history (<see cref="NetworkIdentity.StateAt"/>). Answers for anything this worker
        /// holds, authoritative or ghosted; false when it holds no copy or the tick is outside the recorded window
        /// (<see cref="NebulaConfig.StateHistoryTicks"/>). Nothing is extrapolated. See
        /// <c>docs/state-history.md</c>.
        /// </summary>
        public bool TryGetStateAt(ulong netId, uint tick, out HistoricalState state)
        {
            var e = Find(netId);
            if (e != null) return e.TryGetStateAt(tick, out state);
            state = default;
            return false;
        }

        public NetworkIdentity FindPlayer(ulong clientId) => _players.TryGetValue(clientId, out var e) ? e : null;

        // ---------------------------------------------------------------------------------------- ghost band

        private void UpdateGhostBand(uint tick)
        {
            ResumeInheritedGhosts();
            float now = Time.unscaledTime;
            float margin = Config.GhostBandMargin;

            // Entities in static containers first, then the contents of carriers by nesting depth: whatever is
            // inside a ship is ghosted wherever the ship is ghosted, so the neighbour holds the whole subtree warm
            // before the ship can cross, and that needs the ship's targets decided first.
            // Bounded by the set we own rather than by a constant (design D71): a shuttle nine deep must still
            // be held warm by the neighbour before the carrier it is in can cross.
            for (int depth = 0; depth <= _authoritative.Count; depth++)
            {
                bool deeper = false;
                for (int i = 0; i < _authoritative.Count; i++)
                {
                    var e = _authoritative[i];
                    var c = e.Container;
                    if (c == null) continue;
                    int d = c.NestingDepth;
                    if (d > depth) { deeper = true; continue; }
                    if (d < depth) continue;
                    var pos = e.transform.position;
                    Dictionary<string, float> targets = null;
                    ContainerRegistry.NeighborsOf(c, _neighborScratch);
                    foreach (var n in _neighborScratch)
                    {
                        var owner = n.OwnerWorkerId;
                        if (string.IsNullOrEmpty(owner) || owner == WorkerId) continue;
                        if (c.DistanceToSeam(pos, n) > margin) continue;
                        Ghost(e, owner, ref targets, now);
                    }
                    // Inside a carrier: follow the carrier's ghosts.
                    if (c.IsDynamic && c.Carrier != null && _ghostTargets.TryGetValue(c.Carrier.NetId, out var carrierTargets))
                    {
                        foreach (var kv in carrierTargets) Ghost(e, kv.Key, ref targets, now);
                    }
                }
                if (!deeper) break;
            }

            // Expire ghosts that left the band a while ago, and index what is left by target worker. Indexing is
            // what keeps this pass proportional to the ghosts that exist rather than to peers x ghosts: the stream
            // loop below then walks one list per peer instead of the whole table per peer.
            foreach (var list in _ghostByWorker.Values) { list.Clear(); _ghostListPool.Push(list); }
            _ghostByWorker.Clear();
            _scratchIds.Clear();
            foreach (var kv in _ghostTargets)
            {
                var e = Find(kv.Key);
                bool authoritative = e != null && e.HasAuthority;
                _scratchStrings.Clear();
                foreach (var t in kv.Value)
                {
                    if (GhostTargetExpired(authoritative, _workerPeersById.ContainsKey(t.Key), now, t.Value, Config.GhostLingerSeconds)) _scratchStrings.Add(t.Key);
                    else if (_workerPeersById.TryGetValue(t.Key, out var live) && live.HelloReceived) Index(t.Key, e);
                }
                foreach (var w in _scratchStrings)
                {
                    kv.Value.Remove(w);
                    if (authoritative && _workerPeersById.TryGetValue(w, out var p) && p.HelloReceived)
                    {
                        _writer.Reset();
                        new EntityDespawnMsg { NetId = e.NetId, Epoch = e.Epoch }.Write(_writer, MsgId.GhostDespawn);
                        Send(p, Delivery.ReliableOrdered);
                    }
                }
                _scratchStrings.Clear();
                if (kv.Value.Count == 0) _scratchIds.Add(kv.Key); // net ids, not their decimal strings
            }
            for (int i = 0; i < _scratchIds.Count; i++) _ghostTargets.Remove(_scratchIds[i]);
            _scratchIds.Clear();

            // Stream state and vars to each target worker. The sequenced batch is built in its own writer, so a
            // reliable message in the middle of it does not have to copy the batch out and back (which allocated a
            // byte[] per entity per peer per tick).
            foreach (var peer in _workerPeersById.Values)
            {
                if (!peer.HelloReceived || !_ghostByWorker.TryGetValue(peer.Id, out var targets)) continue;
                int slot = -1;
                ushort count = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    var e = targets[i];
                    if (e == null || !e.HasAuthority) continue;
                    // Variables before the state entry of the same tick: applying the entry is what makes the
                    // receiving worker record the tick in its state history (docs/state-history.md), and a
                    // [SyncHistory] variable should be snapshotted with the value this tick's stream carries, not
                    // with the previous one. On a reliable link that ordering holds; the unreliable batch travels on
                    // another channel and can still cross, which is why the bound is stated as one tick.
                    if (e.VarsDirty)
                    {
                        _scratch.Reset();
                        e.WriteVars(_scratch);
                        _writer.Reset();
                        new EntityVarsMsg { NetId = e.NetId, Epoch = e.Epoch, Vars = _scratch.ToArray() }.Write(_writer, MsgId.GhostVars);
                        Send(peer, Delivery.ReliableOrdered);
                    }
                    if (e.HasReplicationState)
                    {
                        var entry = e.ReplicationState;
                        if (entry.Reliable)
                        {
                            _writer.Reset();
                            int one = WorldStateMsg.Begin(_writer, MsgId.GhostState, tick, WorkerIndex);
                            entry.Write(_writer);
                            WorldStateMsg.End(_writer, one, 1);
                            Send(peer, Delivery.ReliableOrdered);
                        }
                        else
                        {
                            if (slot < 0) { _ghostBatch.Reset(); slot = WorldStateMsg.Begin(_ghostBatch, MsgId.GhostState, tick, WorkerIndex); }
                            entry.Write(_ghostBatch);
                            count++;
                            if (_ghostBatch.Length + EntityStateEntry.WireSize > StateBatchBytes)
                            {
                                WorldStateMsg.End(_ghostBatch, slot, count);
                                Send(peer, Delivery.Sequenced, _ghostBatch);
                                slot = -1; count = 0;
                            }
                        }
                    }
                    if (e.HasSyncState)
                    {
                        // The keyframe decision is per tick, not per destination (SyncEverSent only advances in
                        // ClearDirty), so this peer gets the same chunks the gateways get; a ghost that joined late
                        // got its keyframe in GhostSpawn anyway.
                        SendSyncState(e, tick, MsgId.GhostSyncState, peer);
                    }
                }
                if (slot >= 0)
                {
                    WorldStateMsg.End(_ghostBatch, slot, count);
                    Send(peer, Delivery.Sequenced, _ghostBatch);
                }
            }

            GhostsHeld = _entities.Count - _authoritative.Count;

            void Index(string workerId, NetworkIdentity entity)
            {
                if (entity == null) return;
                if (!_ghostByWorker.TryGetValue(workerId, out var list))
                {
                    list = _ghostListPool.Count > 0 ? _ghostListPool.Pop() : new List<NetworkIdentity>(32);
                    _ghostByWorker[workerId] = list;
                }
                list.Add(entity);
            }
        }

        /// <summary>
        /// Whether a ghost target has lapsed: the entity is not ours any more, the peer holding it is gone, or it
        /// has been out of the band for longer than the linger. Pure, so the bookkeeping is testable without a mesh.
        /// </summary>
        internal static bool GhostTargetExpired(bool authoritative, bool peerKnown, float now, float lastSeen, float lingerSeconds) =>
            !authoritative || !peerKnown || now - lastSeen > lingerSeconds;

        /// <summary>Make sure <paramref name="owner"/> holds a ghost of <paramref name="e"/> and refresh its band timestamp.</summary>
        private void Ghost(NetworkIdentity e, string owner, ref Dictionary<string, float> targets, float now)
        {
            if (!_workerPeersById.TryGetValue(owner, out var peer) || !peer.HelloReceived) return;
            if (targets == null && !_ghostTargets.TryGetValue(e.NetId, out targets))
            {
                targets = new Dictionary<string, float>();
                _ghostTargets[e.NetId] = targets;
            }
            if (!targets.ContainsKey(owner))
            {
                SendSpawn(peer, EntitySpawnMsg.From(e, _scratch), MsgId.GhostSpawn);
                GhostsSent++;
                NebulaLog.Debugf($"ghost {e} -> {owner}");
            }
            targets[owner] = now;
        }

        /// <summary>
        /// Refresh the band timestamp of every inherited ghost target we already have an entry for, so the first
        /// <see cref="UpdateGhostBand"/> after a handover cannot expire (and immediately re-create) a copy the
        /// neighbour never lost. Targets we have no entry for are left alone: <see cref="ResumeInheritedGhosts"/>
        /// creates them, and creating one here would suppress the snapshot spawn that opens the new owner's stream.
        /// </summary>
        private void SeedInheritedTargets(ulong netId, HashSet<string> inherited, float now)
        {
            if (inherited == null || inherited.Count == 0) return;
            if (!_ghostTargets.TryGetValue(netId, out var targets)) return;
            foreach (var worker in inherited) if (targets.ContainsKey(worker)) targets[worker] = now;
        }

        private void ResumeInheritedGhosts()
        {
            if (_inheritedGhosts.Count == 0) return;
            _resumeCompleted.Clear();
            float now = Time.unscaledTime;
            foreach (var pending in _inheritedGhosts)
            {
                var entity = Find(pending.Key);
                if (entity == null || !entity.HasAuthority) { _resumeCompleted.Add(pending.Key); continue; }
                Dictionary<string, float> targets = null;
                foreach (var peer in _workerPeersById.Values)
                    if (peer.HelloReceived && pending.Value.Remove(peer.Id)) Ghost(entity, peer.Id, ref targets, now);
                if (pending.Value.Count == 0) _resumeCompleted.Add(pending.Key);
            }
            for (int i = 0; i < _resumeCompleted.Count; i++) _inheritedGhosts.Remove(_resumeCompleted[i]);
            _resumeCompleted.Clear();
        }

        // ---------------------------------------------------------------------------------------- handover

        /// <summary>Members of cohesion groups still to hand to the same target this handoff (<see cref="CollectCohesionMembers"/>).</summary>
        private readonly List<NetworkIdentity> _cohesionPending = new List<NetworkIdentity>();
        /// <summary>Groups already expanded this handoff, so a group is collected once however many members move.</summary>
        private readonly HashSet<uint> _cohesionExpanded = new HashSet<uint>();
        /// <summary>True while the queue is being drained, so a member's own transfer does not start a second drain.</summary>
        private bool _cohesionDraining;

        /// <summary>
        /// Record everything that will follow <paramref name="carrier"/> to the new owner: its authoritative
        /// contents, to any depth, unless an interior is pinned to a worker of its own — those stay here, are
        /// really orphaned by the handoff, and get their own placement back like any other survivor. The walk
        /// mirrors the transfer's own recursion exactly, so the set is what actually leaves.
        /// </summary>
        private void CollectHandoverFollowers(NetworkIdentity carrier)
        {
            var box = carrier.Carried;
            if (box == null || box.Entities.Count == 0 || box.IsPinned) return;
            for (int i = 0; i < box.Entities.Count; i++)
            {
                var inner = box.Entities[i];
                if (inner == null || !inner.HasAuthority || !_handover.Add(inner.NetId)) continue;
                CollectHandoverFollowers(inner);
            }
        }

        /// <summary>
        /// Hand one entity, and everything riding in it, to <paramref name="target"/>.
        /// <para>
        /// Taking this entity out of the interest index is not an orphaning when its passengers are coming too:
        /// they are still aboard and follow on the same ordered stream, so the set they are in keeps
        /// <see cref="InterestRemove"/> from publishing their own placement in between (design D85). The set is
        /// held by a scope frame rather than by a counter the happy path decrements, because persistence, a
        /// subscriber of <see cref="AuthorityHandedOff"/> and a nested transfer can all throw: a depth left
        /// standing would make the <i>next</i> top-level handoff skip its own collection and republish its
        /// passengers as orphans that never existed (design D87). The exception itself is not swallowed.
        /// </para>
        /// </summary>
        private void TransferAuthority(NetworkIdentity e, Peer target)
        {
            bool outermost = _handover.Depth == 0 && !_cohesionDraining;
            try
            {
                // Collect before authority changes, and keep collection and the first transfer inside cleanup:
                // persistence or a handoff subscriber may throw before the queue starts draining.
                if (e.CohesionGroup != 0 && _cohesionExpanded.Add(e.CohesionGroup)) CollectCohesionMembers(e, target);
                using (var frame = _handover.Begin())
                {
                    if (frame.IsOutermost) CollectHandoverFollowers(e);
                    TransferAuthorityInScope(e, target);
                }
                // A queued carrier opens its own handover scope so its passengers are collected too.
                if (!outermost) return;
                _cohesionDraining = true;
                // Grows while it is walked: a member of one group may belong to another that is expanded in turn.
                for (int i = 0; i < _cohesionPending.Count; i++)
                {
                    var member = _cohesionPending[i];
                    if (member != null && member.HasAuthority) TransferAuthority(member, target);
                }
            }
            finally
            {
                if (outermost)
                {
                    _cohesionDraining = false;
                    _cohesionPending.Clear();
                    _cohesionExpanded.Clear();
                }
            }
        }

        /// <summary>
        /// Queue the other members of <paramref name="e"/>'s cohesion group that this worker owns, so they leave with
        /// it. A member this worker holds only as a ghost cannot be included: the group is about to be split across
        /// two workers, which is a reported failure of the transfer and never a silent split
        /// (<see cref="NebulaDiagnostics.SplitCohesionGroups"/>, <c>docs/cohesion-hints.md</c> D4). The transfer
        /// itself still goes ahead: refusing it would strand the entity in a container this worker no longer leases.
        /// </summary>
        private void CollectCohesionMembers(NetworkIdentity e, Peer target)
        {
            var members = CohesionGroups.Members(e.CohesionGroup);
            string foreign = null;
            int split = 0;
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (member == null || member == e) continue;
                // The group table is process-wide, so in a test mesh that runs two workers in one process it also
                // holds the other worker's copies. This worker only ever deals with the copy it knows under that
                // net id; in a live mesh, which has one copy per process, the check always passes.
                if (Find(member.NetId) != member) continue;
                if (member.HasAuthority && _authoritative.Contains(member))
                {
                    if (!_cohesionPending.Contains(member)) _cohesionPending.Add(member);
                    continue;
                }
                // A ghost whose owner is the worker the entity is going to is not a split: the group is meeting up.
                if (member.OwnerWorkerIndex == target.Index) continue;
                split++;
                if (foreign == null) foreign = member.ToString();
            }
            if (split == 0) return;
            NebulaDiagnostics.SplitCohesionGroups++;
            NebulaLog.Warn($"cohesion group {e.CohesionGroup} cannot be handed over as a unit: {e} leaves, but {split} member(s) of the group are not owned here (for example {foreign}). The group is split across workers until they meet again; keep a group inside one worker's containers or use a container hold (https://nebula.1by3.co/docs/guides/cohesion).");
        }

        /// <summary>One transfer's work, always inside the handover scope <see cref="TransferAuthority"/> opened.</summary>
        private void TransferAuthorityInScope(NetworkIdentity e, Peer target)
        {
            // A persistent entity is checkpointed one last time while this worker is still its authority.
            Persistence?.OnHandoverOut(e);
            // Create a ghost if the neighboring worker has not seen this entity yet.
            if (!_ghostTargets.TryGetValue(e.NetId, out var targets) || !targets.ContainsKey(target.Id))
            {
                SendSpawn(target, EntitySpawnMsg.From(e, _scratch), MsgId.GhostSpawn);
            }
            _ghostTargets.Remove(e.NetId);

            uint newEpoch = e.Epoch + 1;
            var entity = EntitySpawnMsg.From(e, _scratch);
            entity.Epoch = newEpoch;
            entity.OwnerWorkerIndex = (ushort)target.Index;
            var ghostWorkers = targets != null ? new List<string>(targets.Keys) : new List<string>();
            if (_inheritedGhosts.TryGetValue(e.NetId, out var stillPending))
            {
                foreach (var worker in stillPending) if (!ghostWorkers.Contains(worker)) ghostWorkers.Add(worker);
                _inheritedGhosts.Remove(e.NetId);
            }
            if (!ghostWorkers.Contains(WorkerId)) ghostWorkers.Add(WorkerId);
            byte[] pending = Array.Empty<byte>();
            if (e.Predicted != null)
            {
                _scratch.Reset();
                e.Predicted.WritePendingInputs(_scratch);
                pending = _scratch.ToArray();
            }
            _scratch.Reset();
            e.WriteHandoverState(_scratch);
            var handoverState = _scratch.ToArray();
            // Gateways that follow this entity by name or by session must not lose it when it changes worker: they
            // ride along in the transfer so the new owner announces to them, and they are redirected from here so
            // they link the new owner before the old link goes quiet (design §5).
            ulong followMask = StickyMask(e);
            var transfer = new AuthorityTransferMsg
            {
                Entity = entity,
                NewEpoch = newEpoch,
                PendingInputs = pending,
                HandoverState = handoverState,
                GhostWorkers = ghostWorkers.ToArray(),
                InterestGateways = InterestGatewayKeys(followMask),
            };
            if (e.OwnerClientId != 0 && _sessions.TryGet(e.OwnerClientId, out var session)) { transfer.SessionGeneration = session.Generation; transfer.SessionGateway = session.Gateway; }
            _writer.Reset();
            transfer.Write(_writer);
            Send(target, Delivery.ReliableOrdered);
            SendRedirect(e, (ushort)target.Index, followMask);
            InterestRemove(e.NetId);

            // Become the ghost. The object stays; its transform will now be driven by the new owner's stream.
            e.Epoch = newEpoch;
            e.OwnerWorkerIndex = (ushort)target.Index;
            _authoritative.Remove(e);
            e.SetAuthority(false);
            EnsureInterpolator(e).Push(CurrentTick, e.Container, e.LocalPosition, e.LocalRotation, e.Motion.Velocity);
            SetGhostPhysics(e, true);
            _handedOff[e.NetId] = target.Id;
            HandoversOut++;
            AuthorityHandedOff?.Invoke(e, target.Id);
            NebulaLog.Info($"handover OUT {e} -> {target.Id} (epoch {newEpoch}, tick {CurrentTick})");

            // A carrier takes its contents with it, in the same tick and on the same ordered channel, so the
            // receiver applies the ship before the passengers and nobody aboard is ever simulated apart from it.
            // Unless the interior is pinned to a worker of its own: then the contents stay where the lease says.
            var carried = e.Carried;
            if (carried != null && carried.Entities.Count > 0 && !carried.IsPinned)
            {
                var contents = BorrowContents(carried);
                // A nested transfer that throws must still give the snapshot back, or a deep ship that fails
                // once leaks one list per level for the lifetime of the worker.
                try
                {
                    for (int i = 0; i < contents.Count; i++)
                        if (contents[i] != null && contents[i].HasAuthority) TransferAuthority(contents[i], target);
                }
                finally { ReturnContents(contents); }
            }
        }

        private void OnAuthorityTransfer(Peer from, AuthorityTransferMsg msg)
        {
            var e = Find(msg.Entity.NetId);
            if (e == null && msg.Entity.SceneId != 0 && SceneEntities.Find(msg.Entity.SceneId) == null)
            {
                // The lease that sent this also made the streamer load the cell; the object arrives shortly.
                _pendingSceneTransfers[msg.Entity.SceneId] = new PendingTransfer { From = from, Msg = msg };
                _pendingSceneGhosts.Remove(msg.Entity.SceneId);
                NebulaLog.Info($"handover IN  scene entity {msg.Entity.SceneId} #{msg.Entity.NetId} <- {from.Id} waits for its cell");
                return;
            }
            if (e == null && msg.Entity.Container.MayArriveLater && ContainerRegistry.Resolve(msg.Entity.Container) == null)
            {
                Pend(_pendingTransfersByCarrier, msg.Entity.Container, new PendingTransfer { From = from, Msg = msg });
                NebulaLog.Info($"handover IN  #{msg.Entity.NetId} <- {from.Id} waits for container {msg.Entity.Container}");
                return;
            }
            if (e == null) e = InstantiateGhost(msg.Entity);
            if (e == null) return;
            if (msg.NewEpoch < e.Epoch || (msg.NewEpoch == e.Epoch && e.HasAuthority))
            {
                NebulaLog.Warn($"stale authority transfer for {e} (epoch {msg.NewEpoch} <= {e.Epoch}); ignored");
                return;
            }
            ApplySpawnData(e, msg.Entity, msg.NewEpoch);
            if (e.Predicted != null && msg.PendingInputs != null && msg.PendingInputs.Length > 0)
            {
                _reader.Set(new ArraySegment<byte>(msg.PendingInputs));
                e.Predicted.ReadPendingInputs(_reader);
            }
            if (msg.HandoverState != null && msg.HandoverState.Length > 0)
            {
                _reader.Set(new ArraySegment<byte>(msg.HandoverState));
                e.ReadHandoverState(_reader);
            }
            e.OwnerWorkerIndex = WorkerIndex;
            e.Interpolator?.Clear();
            SetGhostPhysics(e, false);
            _handedOff.Remove(e.NetId);
            if (!_authoritative.Contains(e)) _authoritative.Add(e);
            if (e.OwnerClientId != 0)
            {
                _players[e.OwnerClientId] = e;
                // The session came with the pawn: the owner's gateway is trusted here from the first input.
                _sessions.Adopt(e.OwnerClientId, msg.SessionGeneration, msg.SessionGateway);
            }
            e.SetAuthority(true);
            PhysicsIslands.CheckOnAuthority(e);
            // Inherit the previous owner's subscribers. The new owner opens each ordered stream with a snapshot,
            // including for motionless entities that will never emit another pose update.
            if (msg.GhostWorkers != null)
            {
                var inherited = new HashSet<string>();
                foreach (var worker in msg.GhostWorkers)
                    if (worker != WorkerId) inherited.Add(worker);
                // Seed the band bookkeeping with a fresh timestamp before anything can expire it: without this the
                // first UpdateGhostBand after a handover could find an entity with no (or a stale) target time,
                // despawn the neighbour's ghost and immediately respawn it - a one-tick hole in a copy the
                // neighbour is about to need.
                SeedInheritedTargets(e.NetId, inherited, Time.unscaledTime);
                _inheritedGhosts[e.NetId] = inherited;
                ResumeInheritedGhosts();
            }
            HandoversIn++;
            AuthorityReceived?.Invoke(e, from.Id);
            NebulaLog.Info($"handover IN  {e} <- {from.Id} (epoch {msg.NewEpoch}, tick {CurrentTick})");
            if (e.Carried != null) SyncCarriedLeases();

            // Tell the gateways that want it that we own it now (a spawn for a known id is an update): the ones
            // subscribing the region it landed in, the ones following it by name or session here, and the ones the
            // previous owner said were following it. Not every gateway, as before v17.
            InterestAddAndAnnounce(e, msg.InterestGateways);
        }

        /// <summary>Fallback for a bare Rigidbody with no <see cref="NetworkRigidbody"/> (which does this itself, plus velocity).</summary>
        private static void SetGhostPhysics(NetworkIdentity e, bool ghost)
        {
            if (e.GetComponent<NetworkRigidbody>() != null) return;
            var rb = e.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = ghost;
        }

        private static RemoteInterpolator EnsureInterpolator(NetworkIdentity e)
        {
            if (e.Interpolator == null)
            {
                e.Interpolator = e.GetComponent<RemoteInterpolator>() ?? e.gameObject.AddComponent<RemoteInterpolator>();
            }
            return e.Interpolator;
        }

        // ---------------------------------------------------------------------------------------- ghosts (receiving)

        private static void Pend<T>(Dictionary<ContainerRef, List<T>> pending, ContainerRef container, T item)
        {
            if (!pending.TryGetValue(container, out var list)) pending[container] = list = new List<T>();
            list.Add(item);
        }

        /// <summary>A carrier's or a runtime container is resolvable now: apply the ghosts and handovers that were waiting for it.</summary>
        private void OnLateContainerRegistered(Container container)
        {
            var key = container.Ref;
            if (_pendingGhostsByCarrier.TryGetValue(key, out var ghosts))
            {
                _pendingGhostsByCarrier.Remove(key);
                foreach (var msg in ghosts) OnGhostSpawn(null, msg);
            }
            if (_pendingTransfersByCarrier.TryGetValue(key, out var transfers))
            {
                _pendingTransfersByCarrier.Remove(key);
                foreach (var t in transfers) OnAuthorityTransfer(t.From, t.Msg);
            }
        }

        private NetworkIdentity InstantiateGhost(EntitySpawnMsg msg)
        {
            var container = ContainerRegistry.Resolve(msg.Container);
            if (container == null && msg.Container.MayArriveLater)
            {
                Pend(_pendingGhostsByCarrier, msg.Container, msg);
                return null;
            }
            var identity = msg.SceneId != 0
                ? BindSceneEntity(msg)
                : NetworkPrefabs.Instantiate(msg.PrefabId, Vector3.zero, Quaternion.identity, container != null ? container.transform : null);
            if (identity == null) return null;
            identity.NetId = msg.NetId;
            identity.HasAuthority = false;
            ApplySpawnData(identity, msg, msg.Epoch);
            EnsureInterpolator(identity).Push(CurrentTick, identity.Container, identity.LocalPosition, identity.LocalRotation, identity.Motion.Velocity);
            SetGhostPhysics(identity, true);
            _entities[identity.NetId] = identity;
            if (msg.OwnerClientId != 0 && !_players.ContainsKey(msg.OwnerClientId)) _players[msg.OwnerClientId] = identity;
            identity.gameObject.SetActive(true);
            identity.InvokeSpawn();
            identity.ClearDirty();
            EntitySpawned?.Invoke(identity);
            return identity;
        }

        private void ApplySpawnData(NetworkIdentity e, EntitySpawnMsg msg, uint epoch)
        {
            e.Epoch = epoch;
            e.OwnerClientId = msg.OwnerClientId;
            e.OwnerIdentity = msg.OwnerIdentity ?? "";
            e.OwnerIsBot = (msg.Flags & EntityFlags.OwnerIsBot) != 0;
            e.IsServerDriven = (msg.Flags & EntityFlags.ServerDriven) != 0;
            e.OwnerWorkerIndex = msg.OwnerWorkerIndex;
            // The cohesion group travels with the entity, so this worker expands the same group the previous owner
            // did when it hands the entity on (docs/cohesion-hints.md, D3).
            e.JoinCohesionGroup(msg.CohesionGroup);
            // The cost weight travels with the entity, so a boss costs the same on the worker it hands over to
            // (docs/cost-telemetry.md, D4). Zero is valid; a negative value means the field was absent.
            if (msg.CostWeight >= 0f) e.ApplyCarriedCostWeight(msg.CostWeight);
            var container = ContainerRegistry.Resolve(msg.Container);
            if (container == null && msg.Container.MayArriveLater)
            {
                // Its container is not here (yet): keep the pose in the container we last knew, rather than a wrong one.
                NebulaLog.Warn($"{e}: spawn data names container {msg.Container}, unknown here; keeping its current container");
                container = e.Container;
            }
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
        }

        private void OnGhostSpawn(Peer from, EntitySpawnMsg msg)
        {
            var e = Find(msg.NetId);
            if (e == null)
            {
                InstantiateGhost(msg);
                return;
            }
            if (e.HasAuthority)
            {
                if (msg.Epoch > e.Epoch)
                {
                    NebulaLog.Warn($"{(from != null ? from.Id : "a peer")} claims {e} at epoch {msg.Epoch} > ours {e.Epoch}; yielding authority");
                    _authoritative.Remove(e);
                    InterestRemove(e.NetId);
                    e.SetAuthority(false);
                    SetGhostPhysics(e, true);
                }
                else return;
            }
            if (msg.Epoch < e.Epoch) return;
            ApplySpawnData(e, msg, msg.Epoch);
            EnsureInterpolator(e).Push(CurrentTick, e.Container, e.LocalPosition, e.LocalRotation, e.Motion.Velocity);
        }

        private void OnGhostState(Peer from, NetworkReader r)
        {
            WorldStateMsg.ReadHeader(r, out uint tick, out ushort workerIndex, out ushort count);
            for (int i = 0; i < count; i++)
            {
                var entry = EntityStateEntry.Read(r);
                var e = Find(entry.NetId);
                if (e == null || e.HasAuthority || entry.Epoch < e.Epoch) continue;
                e.ReceiveState(tick, workerIndex, entry);
            }
        }

        /// <summary>
        /// One EntitySyncMsg per delivery class that has chunks due this tick. Deltas ride the behaviour's own channel;
        /// keyframes are decided per tick inside <see cref="NetworkIdentity.WriteSyncState"/>, so calling this once
        /// per destination in the same tick sends every destination the same chunks.
        /// </summary>
        private void SendSyncState(NetworkIdentity e, uint tick, MsgId id, Peer to)
        {
            for (int d = 0; d < 2; d++)
            {
                var delivery = d == 0 ? Delivery.ReliableOrdered : Delivery.Sequenced;
                _scratch.Reset();
                if (e.WriteSyncState(_scratch, tick, delivery) == 0) continue;
                _writer.Reset();
                new EntitySyncMsg
                {
                    NetId = e.NetId,
                    Epoch = e.Epoch,
                    Tick = tick,
                    Container = e.ContainerRef,
                    Reliable = delivery == Delivery.ReliableOrdered,
                    Chunks = _scratch.ToArray(),
                }.Write(_writer, id);
                _costMeter.AddReplication(e.Container, _writer.Length); // once per destination: this is what it costs
                Send(to, delivery);
            }
        }

        private void OnGhostSyncState(Peer from, EntitySyncMsg msg)
        {
            var e = Find(msg.NetId);
            if (e == null || e.HasAuthority || msg.Epoch < e.Epoch) return;
            _reader.Set(new ArraySegment<byte>(msg.Chunks));
            e.ReadSyncState(_reader, msg.Tick, ContainerRegistry.Resolve(msg.Container) ?? e.Container);
        }

        private void OnGhostVars(Peer from, EntityVarsMsg msg)
        {
            var e = Find(msg.NetId);
            if (e == null || e.HasAuthority || msg.Epoch < e.Epoch) return;
            _reader.Set(new ArraySegment<byte>(msg.Vars));
            e.ReadVars(_reader);
            e.ClearDirty();
        }

        private void OnGhostDespawn(Peer from, EntityDespawnMsg msg)
        {
            _pendingGhostsByCarrier.Remove(ContainerRef.Dynamic(msg.NetId));
            _pendingTransfersByCarrier.Remove(ContainerRef.Dynamic(msg.NetId));
            var e = Find(msg.NetId);
            if (e == null) { DropPendingScene(msg.NetId); return; }
            if (e.HasAuthority || msg.Epoch < e.Epoch) return;
            RemoveLocal(e);
        }

        // ---------------------------------------------------------------------------------------- gateway traffic

        /// <summary>
        /// Send this tick's state to the gateways, filtered by interest. Entities are grouped by the
        /// exact set of gateways that hear about them, each group's batches are written once and sent to every
        /// gateway in that set, and netvars, sync state and owner state use the same mask lookup: the work is
        /// O(entities + batches × subscribers) rather than O(entities × gateways). Nothing here allocates.
        /// </summary>
        private void PublishToGateways(uint tick)
        {
            if (_gateways.Count == 0) return;
            for (int d = 0; d < 2; d++)
            {
                bool reliable = d == 1;
                var delivery = reliable ? Delivery.ReliableOrdered : Delivery.Sequenced;
                GroupDirtyByMask(reliable);
                for (int m = 0; m < _maskOrder.Count; m++)
                {
                    ulong mask = _maskOrder[m];
                    var group = _byMask[mask];
                    int slot = -1;
                    ushort count = 0;
                    for (int i = 0; i < group.Count; i++)
                    {
                        var e = group[i];
                        if (slot < 0) { _writer.Reset(); slot = WorldStateMsg.Begin(_writer, MsgId.WorldState, tick, WorkerIndex); }
                        e.ReplicationState.Write(_writer);
                        count++;
                        CountEntry(e, mask);
                        if (_writer.Length + EntityStateEntry.WireSize > StateBatchBytes)
                        {
                            WorldStateMsg.End(_writer, slot, count);
                            SendToMask(mask, delivery);
                            slot = -1; count = 0;
                        }
                    }
                    if (slot >= 0)
                    {
                        WorldStateMsg.End(_writer, slot, count);
                        SendToMask(mask, delivery);
                    }
                }
            }
            ReleaseMaskLists();

            for (int i = 0; i < _authoritative.Count; i++)
            {
                var e = _authoritative[i];
                ulong mask = MaskOfEntity(e.NetId);
                if (e.VarsDirty && (mask != 0 || _unmaskedGateways.Count > 0))
                {
                    _scratch.Reset();
                    e.WriteVars(_scratch);
                    _writer.Reset();
                    new EntityVarsMsg { NetId = e.NetId, Epoch = e.Epoch, Vars = _scratch.ToArray() }.Write(_writer, MsgId.EntityVars);
                    _costMeter.AddReplication(e.Container, (long)_writer.Length * SubscriberCount(mask));
                    SendToMask(mask, Delivery.ReliableOrdered);
                }
                if (e.HasSyncState)
                {
                    // The keyframe decision is per tick, not per destination, so every gateway in the mask gets the
                    // same chunks; a gateway that has just subscribed got its keyframe in the spawn.
                    for (int bit = 0; bit < RegionPublisher.MaxGateways && mask != 0; bit++)
                        if ((mask & (1UL << bit)) != 0 && _gatewayBits[bit] != null) SendSyncState(e, tick, MsgId.EntityState, _gatewayBits[bit]);
                    for (int u = 0; u < _unmaskedGateways.Count; u++) SendSyncState(e, tick, MsgId.EntityState, _unmaskedGateways[u]);
                }
                // Every tick, even when the owner's input for it had not arrived and the last one was repeated: the
                // owner must still see what the worker actually simulated, and the lead report is what lets it fix
                // late inputs. (Sending only on consumed ticks left a late owner blind until it got lucky.)
                if (e.OwnerClientId != 0 && e.Predicted != null)
                {
                    // Owner state is for one client, so it goes to that client's gateway only.
                    var session = SessionGatewayOf(e);
                    if (session == null) continue;
                    _scratch.Reset();
                    e.Predicted.WriteOwnerState(_scratch);
                    _writer.Reset();
                    new OwnerStateMsg
                    {
                        NetId = e.NetId,
                        Epoch = e.Epoch,
                        Tick = tick,
                        LastInputTick = e.Predicted.LastProcessedInputTick,
                        InputLead = e.Predicted.TakeInputLead(),
                        OwnerClientId = e.OwnerClientId,
                        Container = e.ContainerRef,
                        State = _scratch.ToArray(),
                    }.Write(_writer);
                    // Owner state has exactly one destination client, so it is the gateway's relay cost rather
                    // than replication: it grows with players in the container, not with entities in it.
                    _costMeter.AddGateway(e.Container, _writer.Length);
                    Send(session, Delivery.Sequenced);
                }
            }
        }

        private void OnSpawnPlayer(Peer gateway, SpawnPlayerMsg msg)
        {
            var claim = _sessions.Register(msg.ClientId, msg.Generation, gateway.Key, out string lostBy);
            if (claim == PlayerSessions.Claim.Stale)
            {
                NebulaLog.Warn($"stale claim of session {msg.ClientId} by gateway {gateway.Id} (generation {msg.Generation}); ignored");
                return;
            }
            if (claim == PlayerSessions.Claim.Reclaimed)
            {
                NebulaLog.Info($"session {msg.ClientId} '{msg.Name}' now speaks through gateway {gateway.Id}");
                // The gateway that held it may still have the old link open (the player connected again elsewhere
                // instead of leaving first): it has to let that client go, and must not despawn the pawn.
                if (lostBy.Length > 0 && lostBy != gateway.Key) EndSessionOnGateway(lostBy, msg.ClientId, msg.Generation, SingleSessionReason);
            }
            if (msg.IsBot) _botClients.Add(msg.ClientId); else _botClients.Remove(msg.ClientId);
            if (!string.IsNullOrEmpty(msg.Identity)) _playerIdentities[msg.ClientId] = msg.Identity; else _playerIdentities.Remove(msg.ClientId);
            if (_players.TryGetValue(msg.ClientId, out var existing) && existing != null)
            {
                if (existing.HasAuthority)
                {
                    // Re-announce; the gateway may have lost track of it.
                    SendSpawn(gateway, EntitySpawnMsg.From(existing, _scratch), MsgId.EntitySpawn);
                    return;
                }
            }
            var container = ContainerRegistry.Resolve(msg.Container)
                ?? (ContainerRegistry.Count > 0 ? ContainerRegistry.All[0] : ContainerRegistry.Runtime.Count > 0 ? ContainerRegistry.Runtime[0] : null);
            if (_gameMode == null || container == null)
            {
                NebulaLog.Error($"cannot spawn player {msg.ClientId}: gameMode={(_gameMode != null)} container={container}");
                return;
            }
            if (claim == PlayerSessions.Claim.Repeat && _pendingPlayerSpawns.ContainsKey(msg.ClientId)) return;
            var pending = new object();
            _pendingPlayerSpawns[msg.ClientId] = pending;
            _gameMode.BeginSpawnPlayer(this, new PlayerInfo(msg.ClientId, msg.Name, msg.Identity, msg.IsBot), container,
                create => CompletePlayerSpawn(msg.ClientId, pending, container, create));
        }

        private void CompletePlayerSpawn(ulong clientId, object pending, Container container, Func<NetworkIdentity> create)
        {
            if (this == null || !_pendingPlayerSpawns.TryGetValue(clientId, out var current) || current != pending) return;
            _pendingPlayerSpawns.Remove(clientId);
            if (!_sessions.TryGet(clientId, out var session) || session.Orphaned) return;
            var identity = create?.Invoke();
            if (identity == null) NebulaLog.Error($"game mode returned no entity for client {clientId}");
            else if (!identity.IsSpawned) Spawn(identity, identity.GetComponentInParent<Container>() ?? container, clientId);
            else if (identity.OwnerClientId != clientId) NebulaLog.Error($"game mode spawned {identity} but not for client {clientId}");
        }

        private void OnDespawnPlayer(Peer gateway, DespawnPlayerMsg msg)
        {
            if (!_sessions.Release(msg.ClientId, msg.Generation, Time.unscaledTime))
            {
                NebulaLog.Info($"stale despawn of session {msg.ClientId} from gateway {gateway.Id} (generation {msg.Generation}); the session moved on");
                return;
            }
            _pendingPlayerSpawns.Remove(msg.ClientId);
            var e = FindPlayer(msg.ClientId);
            if (e == null) { _sessions.Remove(msg.ClientId); _playerIdentities.Remove(msg.ClientId); return; }
            if (!e.HasAuthority)
            {
                _sessions.Remove(msg.ClientId);
                _playerIdentities.Remove(msg.ClientId);
                if (_handedOff.TryGetValue(e.NetId, out var to) && _workerPeersById.TryGetValue(to, out var peer))
                {
                    _writer.Reset();
                    msg.Write(_writer);
                    Send(peer, Delivery.ReliableOrdered);
                }
                return;
            }
            // The pawn stays for the reclaim grace (SessionReclaimSeconds): the player may be reconnecting, here or
            // through another gateway. ExpireSessions despawns it when nobody came back.
            if (Config.SessionReclaimSeconds <= 0) DespawnPlayer(msg.ClientId);
        }

        /// <summary>What a replaced connection is told, here and at the gateway.</summary>
        private const string SingleSessionReason = "this player connected again somewhere else";

        /// <summary>Tell a gateway that a session is not its to speak for any more (<see cref="EndSessionMsg"/>).</summary>
        private void EndSessionOnGateway(string gatewayKey, ulong clientId, ulong generation, string reason)
        {
            if (string.IsNullOrEmpty(gatewayKey)) return;
            foreach (var g in _gateways)
            {
                if (g.Key != gatewayKey) continue;
                _writer.Reset();
                new EndSessionMsg { ClientId = clientId, Generation = generation, Reason = reason }.Write(_writer);
                Send(g, Delivery.ReliableOrdered);
                return;
            }
        }

        /// <summary>The player is gone for good: tell the game, drop the pawn (a persistent pawn keeps its record for the next connection).</summary>
        private void DespawnPlayer(ulong clientId)
        {
            _pendingPlayerSpawns.Remove(clientId);
            _sessions.Remove(clientId);
            _playerIdentities.Remove(clientId);
            var e = FindPlayer(clientId);
            if (e == null || !e.HasAuthority) return;
            _gameMode?.OnPlayerDespawn(this, e);
            // A client disconnecting is not the entity ceasing to exist: a persistent pawn keeps its record so
            // the player finds it again on the next connection.
            Despawn(e, keepPersisted: e.Persistent != null);
        }

        /// <summary>Sessions nobody reclaimed within <see cref="NebulaConfig.SessionReclaimSeconds"/> (their gateway went away, or their client left) lose their pawn.</summary>
        private void ExpireSessions()
        {
            _expiredSessions.Clear();
            _sessions.Expire(Time.unscaledTime, Config.SessionReclaimSeconds, _expiredSessions);
            foreach (var id in _expiredSessions)
            {
                if (FindPlayer(id) is NetworkIdentity e && e.HasAuthority) NebulaLog.Info($"session {id} was not reclaimed; despawning {e}");
                DespawnPlayer(id);
            }
        }

        private void OnClientInput(Peer from, ClientInputMsg msg)
        {
            if (from.Role == PeerRole.Gateway && !_sessions.Accept(msg.ClientId, from.Key)) return;
            var e = FindPlayer(msg.ClientId);
            if (e == null) return;
            if (e.HasAuthority)
            {
                if (e.Predicted == null) return;
                uint newest = 0;
                foreach (var f in msg.Frames)
                {
                    _reader.Set(new ArraySegment<byte>(f.Payload));
                    e.Predicted.ServerReceiveInput(f.Tick, _reader);
                    if (f.Tick > newest) newest = f.Tick;
                }
                // How early the owner's newest input landed relative to the tick we last simulated. Older frames in
                // the packet are redundancy against loss and say nothing about the owner's lead. Received in Update,
                // so an input needs a lead of at least 1 to be picked up by the next FixedUpdate.
                if (newest != 0) e.Predicted.NoteInputLead((long)newest - CurrentTick);
            }
            else if (_handedOff.TryGetValue(e.NetId, out var to) && _workerPeersById.TryGetValue(to, out var peer) && peer != from)
            {
                _writer.Reset();
                msg.Write(_writer, MsgId.ForwardInput);
                Send(peer, Delivery.Sequenced);
            }
        }

        private void OnServerRpc(Peer from, EntityRpcMsg msg)
        {
            if (from.Role == PeerRole.Gateway && !_sessions.Accept(msg.ClientId, from.Key)) return;
            var e = Find(msg.NetId);
            if (e == null) return;
            if (e.HasAuthority)
            {
                if (e.OwnerClientId != msg.ClientId)
                {
                    NebulaLog.Warn($"ServerRpc on {e} from client {msg.ClientId} which does not own it");
                    return;
                }
                InvokeRpc(e, msg);
            }
            else if (_handedOff.TryGetValue(e.NetId, out var to) && _workerPeersById.TryGetValue(to, out var peer) && peer != from)
            {
                _writer.Reset();
                msg.Write(_writer, MsgId.ServerRpc);
                Send(peer, Delivery.ReliableOrdered);
            }
        }

        /// <summary>
        /// An AuthorityRpc from another worker, decided by the call contract (<see cref="AuthorityCallRouter"/>):
        /// applied here once when this worker has authority, forwarded (hop + 1) to the worker this one believes has
        /// it now, or rejected with a reason that is logged and, when the sender asked, sent back to it.
        /// </summary>
        private void OnAuthorityRpc(Peer from, AuthorityCallMsg msg)
        {
            var e = Find(msg.NetId);
            Peer next = null;
            AuthorityCallTarget target;
            if (e == null) target = AuthorityCallTarget.Unknown;
            else if (e.HasAuthority) target = AuthorityCallTarget.Authoritative(e.Epoch);
            else
            {
                next = ForwardPeerFor(e, from);
                target = AuthorityCallTarget.Ghost(e.Epoch, next != null);
            }
            var decision = _callRouter.Decide(msg.CallId, msg.Epoch, msg.Hops, target, CurrentTick);
            switch (decision.Action)
            {
                case AuthorityCallAction.Apply:
                    InvokeRpc(e, msg.BehaviourIndex, msg.MethodHash, msg.Args);
                    if (msg.WantsReply) ReplyToCall(msg.CallId, AuthorityCallOutcome.Accepted, e.Epoch, msg.Hops);
                    break;
                case AuthorityCallAction.Forward:
                    msg.Hops++;
                    _writer.Reset();
                    msg.Write(_writer);
                    Send(next, Delivery.ReliableOrdered);
                    break;
                default:
                    NebulaLog.Warn($"AuthorityRpc {msg.CallId:x} for #{msg.NetId} from {from.Id} rejected: {decision.Outcome} (call epoch {msg.Epoch}, entity {(e != null ? e.ToString() : "unknown here")}, {msg.Hops} hops)");
                    if (msg.WantsReply) ReplyToCall(msg.CallId, decision.Outcome, e != null ? e.Epoch : 0, msg.Hops);
                    break;
            }
        }

        /// <summary>
        /// Where a call for the ghost <paramref name="e"/> goes next: the worker we handed it to if we were its
        /// last authority, else the worker our ghost names as owner. Never back to <paramref name="from"/>, and
        /// only over a link that has said hello. Null when there is no such worker.
        /// </summary>
        private Peer ForwardPeerFor(NetworkIdentity e, Peer from)
        {
            Peer next = null;
            if (_handedOff.TryGetValue(e.NetId, out var to)) _workerPeersById.TryGetValue(to, out next);
            if (next == null) _workerPeersByIndex.TryGetValue(e.OwnerWorkerIndex, out next);
            if (next == null || next == from || !next.HelloReceived) return null;
            return next;
        }

        /// <summary>Tell the worker that minted <paramref name="callId"/> what became of its call; settled in-process when that worker is this one.</summary>
        private void ReplyToCall(ulong callId, AuthorityCallOutcome outcome, uint epoch, byte hops)
        {
            var reply = new AuthorityCallReplyMsg { CallId = callId, Outcome = outcome, Epoch = epoch, Hops = hops };
            ushort origin = AuthorityCallId.WorkerIndexOf(callId);
            if (origin == WorkerIndex)
            {
                _callTracker.Complete(callId, reply.ToResult());
                return;
            }
            if (!_workerPeersByIndex.TryGetValue(origin, out var peer) || !peer.HelloReceived) return; // the sender times out
            _writer.Reset();
            reply.Write(_writer);
            Send(peer, Delivery.ReliableOrdered);
        }

        private void OnAuthorityRpcReply(Peer from, AuthorityCallReplyMsg msg)
        {
            if (!_callTracker.Complete(msg.CallId, msg.ToResult()))
                NebulaLog.Info($"late AuthorityRpc reply {msg.CallId:x} ({msg.Outcome}) from {from.Id}; the call had already settled");
        }

        private void InvokeRpc(NetworkIdentity e, EntityRpcMsg msg) => InvokeRpc(e, msg.BehaviourIndex, msg.MethodHash, msg.Args);

        private void InvokeRpc(NetworkIdentity e, byte behaviourIndex, uint methodHash, byte[] args)
        {
            if (behaviourIndex >= e.Behaviours.Length) return;
            _reader.Set(new ArraySegment<byte>(args));
            RpcRegistry.Invoke(e.Behaviours[behaviourIndex], methodHash, _reader);
        }

        // ---------------------------------------------------------------------------------------- IRpcSink

        void IRpcSink.SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius)
        {
            var msg = new EntityRpcMsg { NetId = identity.NetId, Epoch = identity.Epoch, BehaviourIndex = behaviourIndex, MethodHash = methodHash, ClientId = targetClientId, Radius = radius, Args = ToArray(args) };
            _writer.Reset();
            msg.Write(_writer, MsgId.EntityRpc);
            // Targeted at one client: its gateway alone. Otherwise the gateways that hear about the entity at all —
            // an RPC for an entity a gateway has never been told about has nowhere to land.
            if (targetClientId != 0)
            {
                var session = SessionGatewayOf(targetClientId);
                if (session != null) Send(session, Delivery.ReliableOrdered);
                return;
            }
            SendToMask(PublishMaskOf(identity), Delivery.ReliableOrdered);
        }

        void IRpcSink.SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args)
        {
            NebulaLog.Warn("ServerRpc sent from a worker; ignored");
        }

        void IRpcSink.SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args)
        {
            SendAuthorityCall(identity, behaviourIndex, methodHash, args, null, 0f);
        }

        ulong IRpcSink.SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<AuthorityCallResult> onDone, float timeoutSeconds)
        {
            return SendAuthorityCall(identity, behaviourIndex, methodHash, args, onDone, timeoutSeconds);
        }

        /// <summary>
        /// Mint a call id and send the call to the worker this one believes has authority. With
        /// <paramref name="onDone"/> the call asks for a reply and is tracked until one arrives or
        /// <paramref name="timeoutSeconds"/> passes; without it the outcome is only logged where it is decided.
        /// </summary>
        private ulong SendAuthorityCall(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<AuthorityCallResult> onDone, float timeoutSeconds)
        {
            var target = ForwardPeerFor(identity, null);
            if (target == null)
            {
                NebulaLog.Warn($"AuthorityRpc on {identity} rejected: {AuthorityCallOutcome.RejectedUnreachable} (owner worker {identity.OwnerWorkerIndex} not connected)");
                onDone?.Invoke(new AuthorityCallResult(0, AuthorityCallOutcome.RejectedUnreachable, identity.Epoch, 0));
                return 0;
            }
            ulong callId = _callTracker.Mint();
            var msg = new AuthorityCallMsg
            {
                CallId = callId,
                Hops = 0,
                Flags = onDone != null ? AuthorityCallFlags.WantsReply : AuthorityCallFlags.None,
                NetId = identity.NetId,
                Epoch = identity.Epoch,
                BehaviourIndex = behaviourIndex,
                MethodHash = methodHash,
                Args = ToArray(args),
            };
            if (onDone != null)
            {
                uint ticks = (uint)Math.Max(1, Math.Ceiling(timeoutSeconds * NetworkTime.TickRate));
                _callTracker.Track(callId, CurrentTick + ticks, onDone);
            }
            _writer.Reset();
            msg.Write(_writer);
            Send(target, Delivery.ReliableOrdered);
            return callId;
        }

        private static byte[] ToArray(ArraySegment<byte> seg)
        {
            var arr = new byte[seg.Count];
            if (seg.Array != null) Buffer.BlockCopy(seg.Array, seg.Offset, arr, 0, seg.Count);
            return arr;
        }

        // ---------------------------------------------------------------------------------------- peers & control plane

        private void OnControlPlaneChanged()
        {
            // The control plane came back without this worker (a restarted orchestrator with no document, a
            // database restored from a backup taken before this mesh, a failover to a replica that never had it).
            // Nothing else in the mesh knows this process exists or what it simulates, so say both again — before
            // the lease sweep below, which would otherwise read a document that has just been emptied and forget
            // the very ownership the reclaim is derived from.
            if (_registration.RegisterAgainIfForgotten(ControlPlane))
            {
                _nextHeartbeat = 0f;
                NebulaLog.Warn($"worker {WorkerId} is no longer on the control plane; registering again");
            }
            if (_registered)
            {
                int reclaimed = _registration.ReclaimContainers(ControlPlane);
                if (reclaimed > 0) NebulaLog.Warn($"worker {WorkerId} re-claimed {reclaimed} container(s) the control plane had no row for");
            }
            // Runtime containers come and go with their lease rows; register them before their leases are applied.
            ContainerRegistry.SyncRuntime(ControlPlane.Leases);
            // Leases -> container ownership.
            _seenLeases.Clear();
            foreach (var lease in ControlPlane.Leases)
            {
                ushort idx = ushort.MaxValue;
                var w = ControlPlane.FindWorker(lease.WorkerId);
                if (w != null) idx = (ushort)w.WorkerIndex;
                string owner = LeaseState.IsOwning(lease.State) ? lease.WorkerId : "";
                ContainerRegistry.ApplyLease(lease.ContainerId, owner, idx, lease.Epoch, lease.State);
                _seenLeases.Add(lease.ContainerId);
            }
            foreach (var c in ContainerRegistry.Dynamic) if (!_seenLeases.Contains(c.ContainerId)) ContainerRegistry.ForgetLease(c.ContainerId);
            ContainerRegistry.NotifyLeasesChanged();
            SyncCarriedLeases();
            // Peers: the lower index dials the higher one so each pair has exactly one link.
            foreach (var w in ControlPlane.Workers)
            {
                if (w.WorkerId == WorkerId || w.Status == WorkerStatus.Dead) continue;
                if (!ControlPlane.IsWorkerAlive(w, Config.WorkerTimeoutSeconds)) continue;
                if (_workerPeersById.ContainsKey(w.WorkerId) || _dialing.Contains(w.WorkerId)) continue;
                if (WorkerIndex < w.WorkerIndex)
                {
                    int peerId = _transport.Connect(w.Address, w.Port);
                    _peers[peerId] = new Peer { PeerId = peerId, Role = PeerRole.Worker, Id = w.WorkerId, Index = w.WorkerIndex, Outbound = true };
                    _dialing.Add(w.WorkerId);
                    NebulaLog.Info($"dialing peer {w.WorkerId} at {w.Address}:{w.Port}");
                }
            }
        }

        private void HandleTransportEvent(TransportEvent ev)
        {
            switch (ev.Type)
            {
                case TransportEvent.Kind.Connected:
                {
                    if (!_peers.TryGetValue(ev.PeerId, out var peer))
                    {
                        peer = new Peer { PeerId = ev.PeerId };
                        _peers[ev.PeerId] = peer;
                    }
                    // Both sides introduce themselves; the inbound side learns who this is from the Hello.
                    _writer.Reset();
                    new HelloMsg { Role = PeerRole.Worker, Id = WorkerId, Index = WorkerIndex, Incarnation = Incarnation, Token = MeshPeerAuth.Issue(Config.MeshToken, PeerRole.Worker, WorkerId, Incarnation) }.Write(_writer);
                    Send(peer, Delivery.ReliableOrdered);
                    break;
                }
                case TransportEvent.Kind.Disconnected:
                {
                    if (_peers.TryGetValue(ev.PeerId, out var peer))
                    {
                        _peers.Remove(ev.PeerId);
                        OnPeerLost(peer);
                    }
                    break;
                }
                case TransportEvent.Kind.Data:
                {
                    if (!_peers.TryGetValue(ev.PeerId, out var peer)) return;
                    _reader.Set(ev.Data);
                    try { Dispatch(peer, _reader); }
                    catch (Exception e) { NebulaLog.Error($"bad packet from {peer.Role}/{peer.Id}: {e}"); }
                    break;
                }
            }
        }

        private void OnPeerLost(Peer peer)
        {
            if (peer.Role == PeerRole.Gateway)
            {
                _gateways.Remove(peer);
                RemoveGatewayLink(peer);
                // Its sessions wait for a reclaim (the same gateway coming back, or another one the clients moved to).
                bool stillHere = false;
                foreach (var g in _gateways) if (g.Key == peer.Key) stillHere = true;
                if (!stillHere) _sessions.GatewayLost(peer.Key, Time.unscaledTime);
                NebulaLog.Warn($"gateway {peer.Id} disconnected" + (stillHere ? "" : "; its players' pawns are kept for " + Config.SessionReclaimSeconds + " s"));
            }
            else if (peer.Role == PeerRole.Worker)
            {
                _workerPeersById.Remove(peer.Id);
                _workerPeersByIndex.Remove(peer.Index);
                _dialing.Remove(peer.Id);
                NebulaLog.Warn($"peer worker {peer.Id} disconnected");
                // Ghosts it was driving are orphans now; their authority went with the process.
                _scratchEntities.Clear();
                foreach (var e in _entities.Values) if (!e.HasAuthority && e.OwnerWorkerIndex == peer.Index) _scratchEntities.Add(e);
                foreach (var e in _scratchEntities) RemoveLocal(e);
                foreach (var kv in _ghostTargets) kv.Value.Remove(peer.Id);
                _scratchIds.Clear();
                foreach (var kv in _handedOff) if (kv.Value == peer.Id) _scratchIds.Add(kv.Key);
                for (int i = 0; i < _scratchIds.Count; i++) _handedOff.Remove(_scratchIds[i]);
                _scratchIds.Clear();
            }
        }

        private void Dispatch(Peer peer, NetworkReader r)
        {
            var id = (MsgId)r.ReadByte();
            if (id == MsgId.Hello)
            {
                var hello = HelloMsg.Read(r);
                if (hello.Role != PeerRole.Gateway && hello.Role != PeerRole.Worker)
                {
                    NebulaLog.Warn($"peer '{hello.Id}' with role {hello.Role} refused: workers only talk to gateways and workers");
                    _transport.Disconnect(peer.PeerId);
                    return;
                }
                if (_peerKey != null && !MeshPeerAuth.Verify(_peerKey, hello.Role, hello.Id, hello.Incarnation, hello.Token, JsonWebToken.UnixNow(), out string authError))
                {
                    NebulaLog.Warn($"{hello.Role} '{hello.Id}' refused: {authError}");
                    _transport.Disconnect(peer.PeerId);
                    return;
                }
                peer.Role = hello.Role;
                peer.Id = hello.Id;
                peer.Index = hello.Index;
                peer.Incarnation = hello.Incarnation;
                peer.HelloReceived = true;
                if (hello.Role == PeerRole.Gateway)
                {
                    peer.Key = PlayerSessions.GatewayKey(hello.Id, hello.Incarnation);
                    _gateways.Add(peer);
                    _sessions.GatewayReturned(peer.Key);
                    // Nothing is announced here beyond the always-relevant entities and the pawns of the sessions
                    // this gateway speaks for: it is told what it subscribes, and it has not subscribed yet.
                    AddGatewayLink(peer);
                    NebulaLog.Info($"gateway {peer.Id} (incarnation {hello.Incarnation:x8}) connected as interest link {peer.GatewayBit}");
                }
                else if (hello.Role == PeerRole.Worker)
                {
                    _workerPeersById[peer.Id] = peer;
                    _workerPeersByIndex[peer.Index] = peer;
                    _dialing.Remove(peer.Id);
                    NebulaLog.Info($"peer worker {peer.Id} (index {peer.Index}) connected");
                }
                return;
            }
            if (!peer.HelloReceived) return;

            switch (id)
            {
                case MsgId.SpawnPlayer: OnSpawnPlayer(peer, SpawnPlayerMsg.Read(r)); break;
                case MsgId.InstancePrepare: OnInstancePrepare(peer, InstancePreparationMsg.Read(r)); break;
                case MsgId.InstanceReady: OnInstanceReady(peer, InstancePreparationMsg.Read(r)); break;
                case MsgId.DespawnPlayer: OnDespawnPlayer(peer, DespawnPlayerMsg.Read(r)); break;
                case MsgId.ClientInput:
                case MsgId.ForwardInput: OnClientInput(peer, ClientInputMsg.Read(r)); break;
                case MsgId.ServerRpc: OnServerRpc(peer, EntityRpcMsg.Read(r)); break;
                case MsgId.InterestSubscribe: OnInterestSubscribe(peer, InterestSubscribeMsg.Read(r)); break;
                case MsgId.GhostSpawn: OnGhostSpawn(peer, EntitySpawnMsg.Read(r)); break;
                case MsgId.GhostState: OnGhostState(peer, r); break;
                case MsgId.GhostVars: OnGhostVars(peer, EntityVarsMsg.Read(r)); break;
                case MsgId.GhostSyncState: OnGhostSyncState(peer, EntitySyncMsg.Read(r)); break;
                case MsgId.GhostDespawn: OnGhostDespawn(peer, EntityDespawnMsg.Read(r)); break;
                case MsgId.AuthorityTransfer: OnAuthorityTransfer(peer, AuthorityTransferMsg.Read(r)); break;
                case MsgId.AuthorityRpc: if (peer.Role == PeerRole.Worker) OnAuthorityRpc(peer, AuthorityCallMsg.Read(r)); break;
                case MsgId.AuthorityRpcReply: if (peer.Role == PeerRole.Worker) OnAuthorityRpcReply(peer, AuthorityCallReplyMsg.Read(r)); break;
                case MsgId.WorkerMessage: if (peer.Role == PeerRole.Worker) OnWorkerMessage(peer, r); break;
                case MsgId.Ping:
                {
                    var ping = PingMsg.Read(r);
                    _writer.Reset();
                    new PongMsg { ClientTime = ping.ClientTime, ServerTick = CurrentTick }.Write(_writer);
                    Send(peer, Delivery.Sequenced);
                    break;
                }
                default:
                    NebulaLog.Warn($"worker got unexpected message {id} from {peer.Role}/{peer.Id}");
                    break;
            }
        }

        private void SendSpawn(Peer to, EntitySpawnMsg msg, MsgId id)
        {
            _writer.Reset();
            msg.Write(_writer, id);
            Send(to, Delivery.ReliableOrdered);
        }

        private void Send(Peer to, Delivery delivery)
        {
            _transport.Send(to.PeerId, delivery, _writer.ToSegment());
        }

        /// <summary>Send what <paramref name="from"/> holds, for the paths that build a batch in a writer of their own.</summary>
        private void Send(Peer to, Delivery delivery, NetworkWriter from)
        {
            _transport.Send(to.PeerId, delivery, from.ToSegment());
        }
    }
}
