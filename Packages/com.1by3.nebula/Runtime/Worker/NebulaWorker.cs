using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The sim worker: a headless Unity process leased authority over a set of containers. It simulates the entities
    /// in those containers at the tick rate, ghosts entities approaching a neighbouring container to that container's
    /// worker over the lateral link, hands authority over when they cross, and streams its authoritative entities to
    /// the gateway. One process can own any number of containers; adjacent containers on the same worker hand over
    /// locally with no network traffic at all.
    /// </summary>
    public sealed class NebulaWorker : MonoBehaviour, IRpcSink, IWorkerMessaging
    {
        private sealed class Peer
        {
            public int PeerId;
            public PeerRole Role;
            public string Id = "";
            public uint Index;
            public bool HelloReceived;
            public bool Outbound;
        }

        /// <summary>
        /// Byte budget for one Sequenced world/ghost-state packet. Sequenced delivery cannot fragment, and a peer's
        /// MTU starts at LiteNetLib's floor (508 bytes) until discovery finishes, so batches are cut by size rather
        /// than by entry count. At 36 bytes per entry this is 13 entries per packet.
        /// </summary>
        public const int StateBatchBytes = 500;

        public NebulaConfig Config { get; private set; }
        public IControlPlane ControlPlane { get; private set; }
        public string WorkerId { get; private set; }
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
        public int GhostsSent { get; private set; }
        public int GhostsHeld { get; private set; }
        public int AuthoritativeCount => _authoritative.Count;
        public int EntityCount => _entities.Count;
        /// <summary>Authoritative entities owned by human clients / by bot clients / by nobody (server-driven), as of the last heartbeat.</summary>
        public int PlayerCount { get; private set; }
        public int BotCount { get; private set; }
        public int ServerDrivenCount { get; private set; }

        private ITransport _transport;
        private readonly Dictionary<int, Peer> _peers = new Dictionary<int, Peer>();
        private readonly Dictionary<string, Peer> _workerPeersById = new Dictionary<string, Peer>();
        private readonly Dictionary<uint, Peer> _workerPeersByIndex = new Dictionary<uint, Peer>();
        private readonly List<Peer> _gateways = new List<Peer>();
        private readonly HashSet<string> _dialing = new HashSet<string>();

        private readonly Dictionary<ulong, NetworkIdentity> _entities = new Dictionary<ulong, NetworkIdentity>();
        private readonly List<NetworkIdentity> _authoritative = new List<NetworkIdentity>();
        private readonly Dictionary<uint, NetworkIdentity> _players = new Dictionary<uint, NetworkIdentity>();
        /// <summary>Clients the gateway told us are bots, so Spawn() can tag their pawns without a game-code API change.</summary>
        private readonly HashSet<uint> _botClients = new HashSet<uint>();
        /// <summary>netId -> (workerId -> time last seen inside that worker's band)</summary>
        private readonly Dictionary<ulong, Dictionary<string, float>> _ghostTargets = new Dictionary<ulong, Dictionary<string, float>>();
        /// <summary>Entities we handed off recently: netId -> new owner. Inputs that still arrive here are forwarded.</summary>
        private readonly Dictionary<ulong, string> _handedOff = new Dictionary<ulong, string>();
        private readonly List<NetworkIdentity> _scratchEntities = new List<NetworkIdentity>();
        private readonly List<string> _scratchStrings = new List<string>();

        // Scene entities (see SceneEntities): spawns and handovers that arrived for a scene object whose cell is not
        // loaded here yet wait by scene id, and are applied when the object registers. Containers this worker leases
        // are dated so a freshly leased one gets its grace period before its unspawned scene entities are spawned.
        private struct PendingTransfer { public Peer From; public AuthorityTransferMsg Msg; }
        private readonly Dictionary<uint, EntitySpawnMsg> _pendingSceneGhosts = new Dictionary<uint, EntitySpawnMsg>();
        private readonly Dictionary<uint, PendingTransfer> _pendingSceneTransfers = new Dictionary<uint, PendingTransfer>();
        private readonly Dictionary<ushort, float> _ownedSince = new Dictionary<ushort, float>();
        private const float ScenePassSeconds = 0.25f;
        private float _nextScenePass;

        // Dynamic containers (see DynamicContainer): a ghost or a handover for an entity inside a carrier that has
        // not arrived here yet waits by the carrier's net id and is applied when the carrier's container registers.
        // Reliable ordering normally delivers the carrier first; this covers a carrier that was never ghosted here.
        private readonly Dictionary<ulong, List<EntitySpawnMsg>> _pendingGhostsByCarrier = new Dictionary<ulong, List<EntitySpawnMsg>>();
        private readonly Dictionary<ulong, List<PendingTransfer>> _pendingTransfersByCarrier = new Dictionary<ulong, List<PendingTransfer>>();
        private readonly List<Container> _neighborScratch = new List<Container>();
        private readonly List<NetworkIdentity> _contentsScratch = new List<NetworkIdentity>();
        private readonly HashSet<string> _seenLeases = new HashSet<string>();

        private readonly NetworkWriter _writer = new NetworkWriter(4096);
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
        private bool _registered;
        private float _nextHeartbeat;
        private float _nextUnownedWarning;
        private NebulaGameMode _gameMode;
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

        /// <summary>Receive worker messages of <paramref name="kind"/>. One handler per kind; registering again replaces it.</summary>
        public void RegisterMessageHandler(ushort kind, WorkerMessageHandler handler)
        {
            if (handler == null) _messageHandlers.Remove(kind);
            else _messageHandlers[kind] = handler;
        }

        public void UnregisterMessageHandler(ushort kind) => _messageHandlers.Remove(kind);

        /// <summary>Whether the lateral link to <paramref name="workerId"/> is up (both sides have said hello).</summary>
        public bool IsWorkerConnected(string workerId) => !string.IsNullOrEmpty(workerId) && _workerPeersById.TryGetValue(workerId, out var p) && p.HelloReceived;

        /// <summary>Ids of every worker this one currently has a lateral link to.</summary>
        public IEnumerable<string> ConnectedWorkerIds
        {
            get { foreach (var p in _workerPeersById.Values) if (p.HelloReceived) yield return p.Id; }
        }

        /// <summary>Indices of every worker this one currently has a lateral link to (the addresses <see cref="WorkerQuery"/> takes).</summary>
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
            Config = config;
            ControlPlane = controlPlane;
            WorkerIndex = (ushort)CommandLine.GetInt("nebula-worker-index", 1);
            WorkerId = CommandLine.Get("nebula-worker-id", $"w{WorkerIndex}");
            Port = (ushort)CommandLine.GetInt("nebula-port", config.WorkerBasePort + WorkerIndex);
            NebulaRuntime.LocalWorkerId = WorkerId;
            NebulaRuntime.LocalWorkerIndex = WorkerIndex;
            NebulaRuntime.RpcSink = this;

            _gameMode = FindFirstObjectByType<NebulaGameMode>();
            if (_gameMode == null) NebulaLog.Warn("No NebulaGameMode in the scene; players cannot be spawned");

            _transport = new LiteNetTransport($"worker:{WorkerId}");
            _transport.Listen(Port);
            IsListening = true;
            ControlPlane.Changed += OnControlPlaneChanged;
            ContainerRegistry.LeasesChanged += OnLeasesChanged;
            ContainerRegistry.DynamicRegistered += OnDynamicContainerRegistered;
            ContainerRegistry.WorkerIdByIndex = ResolveWorkerId;
            SceneEntities.Registered += OnSceneEntityRegistered;
            SceneEntities.Unregistering += OnSceneEntityUnregistering;
            OnLeasesChanged();
            NebulaLog.Info($"worker {WorkerId} (index {WorkerIndex}) listening on udp/{Port}");
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

        /// <summary>Worker id for a worker index: this worker, or a connected peer. Dynamic containers derive their owner through this.</summary>
        private string ResolveWorkerId(ushort index)
        {
            if (index == WorkerIndex) return WorkerId;
            return _workerPeersByIndex.TryGetValue(index, out var p) ? p.Id : "";
        }

        private void OnDestroy()
        {
            ContainerRegistry.LeasesChanged -= OnLeasesChanged;
            ContainerRegistry.DynamicRegistered -= OnDynamicContainerRegistered;
            SceneEntities.Registered -= OnSceneEntityRegistered;
            SceneEntities.Unregistering -= OnSceneEntityUnregistering;
            if (ControlPlane != null)
            {
                ControlPlane.Changed -= OnControlPlaneChanged;
                if (_registered && ControlPlane.IsConnected)
                {
                    try { ControlPlane.UnregisterWorker(WorkerId); } catch (Exception e) { NebulaLog.Warn($"unregister failed: {e.Message}"); }
                }
            }
            _transport?.Dispose();
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

            if (!_registered && ControlPlane.IsConnected)
            {
                ControlPlane.RegisterWorker(WorkerId, WorkerIndex, Config.WorkerAdvertiseAddress, Port);
                _registered = true;
                _nextHeartbeat = 0f;
                NebulaLog.Info($"registered with control plane as {WorkerId}");
            }
            if (_registered && Time.unscaledTime >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.unscaledTime + Config.WorkerHeartbeatSeconds;
                ControlPlane.HeartbeatWorker(WorkerId, WorkerStatus.Ready, CollectStats());
            }
            if (_registered && Time.unscaledTime >= _nextScenePass)
            {
                _nextScenePass = Time.unscaledTime + ScenePassSeconds;
                SpawnSceneEntities();
            }
        }

        // ---------------------------------------------------------------------------------------- scene entities

        private void OnLeasesChanged()
        {
            float now = Time.unscaledTime;
            foreach (var c in ContainerRegistry.All)
            {
                if (c.IsOwnedBy(WorkerId)) { if (!_ownedSince.ContainsKey(c.Index)) _ownedSince[c.Index] = now; }
                else _ownedSince.Remove(c.Index);
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
                if (!_ownedSince.TryGetValue(c.Index, out float since) || now - since < Config.SceneEntityGraceSeconds) continue;
                Spawn(e, c);
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
                Despawn(e);
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
                foreach (var c in ContainerRegistry.All)
                {
                    string owner = c.OwnerWorkerId;
                    if (string.IsNullOrEmpty(owner)) return false;
                    if (owner != WorkerId && !(_workerPeersById.TryGetValue(owner, out var p) && p.HelloReceived)) return false;
                }
                return true;
            }
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
            NebulaLog.Info($"profile {_profileTicks} ticks/{ProfileIntervalSeconds:0}s {_profileFrames} frames avg {avg:0.0}ms max {_profileMaxMs:0.0}ms dup {_profileDuplicateTicks} skip {_profileSkippedTicks} gc {gcs} auth {_authoritative.Count} ghosts {_entities.Count - _authoritative.Count} | {NebulaProfiler.ReportAndReset(_profileTicks)}");
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
            float dt = NetworkTime.TickInterval;
            ContainerRegistry.RefreshCaches();

            // 1. Ghosts follow the stream they are driven by (kinematic: no solve of their own).
            ProfGhosts.Begin();
            double renderTick = tick - 1.0;
            NetworkTime.RenderTick = renderTick;
            foreach (var e in _entities.Values)
            {
                if (e.HasAuthority) continue;
                if (e.Interpolator != null && e.Interpolator.Sample(renderTick, out var container, out var pos, out var rot))
                {
                    // Container-local, applied under the container's transform: a passenger ghost lands where the
                    // ship ghost is this tick whichever of the two this loop reaches first.
                    if (container != e.Container) e.SetContainer(container);
                    e.SetLocalPose(container, pos, rot);
                    e.Velocity = e.Interpolator.LatestVelocity;
                }
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
            for (int depth = 0; depth <= MaxNestingDepth; depth++)
            {
                bool deeper = false, carrierTicked = false;
                for (int i = 0; i < _authoritative.Count; i++)
                {
                    var e = _authoritative[i];
                    int d = e.Container != null ? e.Container.NestingDepth : 0;
                    if (d > depth) { deeper = true; continue; }
                    if (d < depth) continue;
                    var behaviours = e.Behaviours;
                    for (int b = 0; b < behaviours.Length; b++)
                    {
                        try { behaviours[b].NetworkTick(tick, dt); }
                        catch (Exception ex) { NebulaLog.Error($"NetworkTick on {e} threw: {ex}"); }
                    }
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

            // Remember where everything ended up this tick (ghosts included) for lag-compensated hit tests.
            ProfRecordPose.Begin();
            foreach (var e in _entities.Values) e.RecordPose(tick);
            ProfRecordPose.End();

            // 3. Container membership (with hysteresis) and authority transfers.
            ProfContainers.Begin();
            _scratchEntities.Clear();
            _scratchEntities.AddRange(_authoritative);
            foreach (var e in _scratchEntities)
            {
                if (!e.HasAuthority) continue; // handed over as the contents of a carrier earlier in this pass
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

            // 4. Ghost band: pre-warm neighbours before anything can cross.
            ProfBand.Begin();
            UpdateGhostBand(tick);
            ProfBand.End();

            // 5. Stream to the gateway(s) and clear dirty state.
            ProfPublish.Begin();
            PublishToGateways(tick);
            foreach (var e in _authoritative) e.ClearDirty();
            ProfPublish.End();
        }

        // ---------------------------------------------------------------------------------------- spawning API

        /// <summary>Allocate an id and make <paramref name="identity"/> live on this worker. Call after instantiating a network prefab.</summary>
        public void Spawn(NetworkIdentity identity, Container container, uint ownerClientId = 0)
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

        private void Spawn(NetworkIdentity identity, Container container, uint ownerClientId, bool serverDriven)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (identity.IsSpawned) throw new InvalidOperationException($"{identity} is already spawned");
            if (identity.PrefabId == ushort.MaxValue && !identity.IsSceneEntity)
            {
                NebulaLog.Error($"{identity.name} has no prefab id; instantiate it through NetworkPrefabs or register the prefab in NebulaConfig");
            }
            identity.Initialize();
            identity.NetId = ((ulong)WorkerIndex << 48) | (++_nextSequence);
            identity.Epoch = 1;
            identity.OwnerClientId = ownerClientId;
            identity.OwnerIsBot = ownerClientId != 0 && _botClients.Contains(ownerClientId);
            identity.IsServerDriven = serverDriven && ownerClientId == 0;
            identity.OwnerWorkerIndex = WorkerIndex;
            identity.HasAuthority = true;
            identity.SetContainer(container ?? ContainerRegistry.Find(identity.transform.position));
            _entities[identity.NetId] = identity;
            _authoritative.Add(identity);
            if (ownerClientId != 0) _players[ownerClientId] = identity;
            if (!identity.gameObject.activeSelf) identity.gameObject.SetActive(true);
            identity.InvokeSpawn();
            foreach (var b in identity.Behaviours) b.OnGainedAuthority();
            identity.ClearDirty();
            if (identity.Carried != null) SyncCarriedLeases();

            var msg = EntitySpawnMsg.From(identity, _scratch);
            foreach (var g in _gateways) SendSpawn(g, msg, MsgId.EntitySpawn);
            EntitySpawned?.Invoke(identity);
            if (ownerClientId != 0) NebulaLog.Info($"spawned player {identity} for client {ownerClientId} in {identity.Container?.ContainerId}");
            else NebulaLog.Debugf($"spawned {identity}");
        }

        public NetworkIdentity SpawnPrefab(GameObject prefab, Vector3 position, Quaternion rotation, Container container, uint ownerClientId = 0)
        {
            var id = NetworkPrefabs.IdOf(prefab);
            var identity = NetworkPrefabs.Instantiate(id, position, rotation, container != null ? container.transform : null);
            Spawn(identity, container, ownerClientId);
            return identity;
        }

        public void Despawn(NetworkIdentity identity)
        {
            if (identity == null || !_entities.ContainsKey(identity.NetId)) return;
            if (!identity.HasAuthority)
            {
                NebulaLog.Warn($"Despawn({identity}) called on a ghost; only the authority can despawn");
                return;
            }
            var despawn = new EntityDespawnMsg { NetId = identity.NetId, Epoch = identity.Epoch };
            foreach (var g in _gateways) { _writer.Reset(); despawn.Write(_writer, MsgId.EntityDespawn); Send(g, Delivery.ReliableOrdered); }
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

        private void RemoveLocal(NetworkIdentity identity)
        {
            _entities.Remove(identity.NetId);
            _authoritative.Remove(identity);
            _handedOff.Remove(identity.NetId);
            if (identity.OwnerClientId != 0 && _players.TryGetValue(identity.OwnerClientId, out var p) && p == identity) _players.Remove(identity.OwnerClientId);
            identity.InvokeDespawn();
            EntityDespawned?.Invoke(identity);
            if (identity.IsSceneEntity) identity.Unbind(); // the object belongs to its scene
            else Destroy(identity.gameObject);
        }

        public NetworkIdentity Find(ulong netId) => _entities.TryGetValue(netId, out var e) ? e : null;

        public NetworkIdentity FindPlayer(uint clientId) => _players.TryGetValue(clientId, out var e) ? e : null;

        // ---------------------------------------------------------------------------------------- ghost band

        private void UpdateGhostBand(uint tick)
        {
            float now = Time.unscaledTime;
            float margin = Config.GhostBandMargin;

            // Entities in static containers first, then the contents of carriers by nesting depth: whatever is
            // inside a ship is ghosted wherever the ship is ghosted, so the neighbour holds the whole subtree warm
            // before the ship can cross, and that needs the ship's targets decided first.
            for (int depth = 0; depth <= MaxNestingDepth; depth++)
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

            // Expire ghosts that left the band a while ago, and stream to the rest.
            _scratchStrings.Clear();
            foreach (var kv in _ghostTargets)
            {
                var e = Find(kv.Key);
                bool authoritative = e != null && e.HasAuthority;
                foreach (var t in kv.Value)
                {
                    bool expired = !authoritative || now - t.Value > Config.GhostLingerSeconds || !_workerPeersById.ContainsKey(t.Key);
                    if (expired) _scratchStrings.Add(t.Key);
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
            }
            // Drop empty target sets.
            _scratchEntities.Clear();
            foreach (var kv in _ghostTargets) if (kv.Value.Count == 0) _scratchStrings.Add(kv.Key.ToString());
            if (_scratchStrings.Count > 0)
            {
                foreach (var key in _scratchStrings) _ghostTargets.Remove(ulong.Parse(key));
                _scratchStrings.Clear();
            }

            // Stream state and vars to each target worker.
            foreach (var peer in _workerPeersById.Values)
            {
                if (!peer.HelloReceived) continue;
                int slot = -1;
                ushort count = 0;
                foreach (var kv in _ghostTargets)
                {
                    if (!kv.Value.ContainsKey(peer.Id)) continue;
                    var e = Find(kv.Key);
                    if (e == null || !e.HasAuthority) continue;
                    if (slot < 0)
                    {
                        _writer.Reset();
                        slot = WorldStateMsg.Begin(_writer, MsgId.GhostState, tick, WorkerIndex);
                    }
                    Entry(e).Write(_writer);
                    count++;
                    if (_writer.Length + EntityStateEntry.WireSize > StateBatchBytes)
                    {
                        WorldStateMsg.End(_writer, slot, count);
                        Send(peer, Delivery.Sequenced);
                        slot = -1;
                        count = 0;
                    }
                    if (e.VarsDirty)
                    {
                        _scratch.Reset();
                        e.WriteVars(_scratch);
                        var vars = new EntityVarsMsg { NetId = e.NetId, Epoch = e.Epoch, Vars = _scratch.ToArray() };
                        // Vars go on the reliable channel; keep the state batch writer intact by using the scratch writer.
                        var saved = _writer.ToArray();
                        _writer.Reset();
                        vars.Write(_writer, MsgId.GhostVars);
                        Send(peer, Delivery.ReliableOrdered);
                        _writer.Reset();
                        _writer.WriteRaw(new ArraySegment<byte>(saved));
                    }
                    if (e.HasSyncState)
                    {
                        // The keyframe decision is per tick, not per destination (SyncEverSent only advances in
                        // ClearDirty), so this peer gets the same chunks the gateways get; a ghost that joined late
                        // got its keyframe in GhostSpawn anyway.
                        var saved = _writer.ToArray();
                        SendSyncState(e, tick, MsgId.GhostSyncState, peer);
                        _writer.Reset();
                        _writer.WriteRaw(new ArraySegment<byte>(saved));
                    }
                }
                if (slot >= 0)
                {
                    WorldStateMsg.End(_writer, slot, count);
                    Send(peer, Delivery.Sequenced);
                }
            }

            GhostsHeld = _entities.Count - _authoritative.Count;
        }

        /// <summary>Deepest container nesting the ghost band walks (a shuttle in a hangar in a carrier is depth 3).</summary>
        public const int MaxNestingDepth = 8;

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

        private static EntityStateEntry Entry(NetworkIdentity e) => new EntityStateEntry
        {
            NetId = e.NetId,
            Epoch = e.Epoch,
            Container = e.ContainerRef,
            LocalPosition = e.LocalPosition,
            LocalRotation = e.LocalRotation,
            Velocity = e.Velocity,
        };

        // ---------------------------------------------------------------------------------------- handover

        private void TransferAuthority(NetworkIdentity e, Peer target)
        {
            // Pre-warm if the neighbour has never seen this entity (it normally has: the band did it).
            if (!_ghostTargets.TryGetValue(e.NetId, out var targets) || !targets.ContainsKey(target.Id))
            {
                SendSpawn(target, EntitySpawnMsg.From(e, _scratch), MsgId.GhostSpawn);
            }
            _ghostTargets.Remove(e.NetId);

            uint newEpoch = e.Epoch + 1;
            var entity = EntitySpawnMsg.From(e, _scratch);
            entity.Epoch = newEpoch;
            entity.OwnerWorkerIndex = (ushort)target.Index;
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
            _writer.Reset();
            new AuthorityTransferMsg { Entity = entity, NewEpoch = newEpoch, PendingInputs = pending, HandoverState = handoverState }.Write(_writer);
            Send(target, Delivery.ReliableOrdered);

            // Become the ghost. The object stays; its transform will now be driven by the new owner's stream.
            e.Epoch = newEpoch;
            e.OwnerWorkerIndex = (ushort)target.Index;
            _authoritative.Remove(e);
            e.SetAuthority(false);
            EnsureInterpolator(e).Push(CurrentTick, e.Container, e.LocalPosition, e.LocalRotation, e.Velocity);
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
                _contentsScratch.Clear();
                _contentsScratch.AddRange(carried.Entities);
                foreach (var inner in _contentsScratch)
                    if (inner != null && inner.HasAuthority) TransferAuthority(inner, target);
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
            if (e == null && msg.Entity.Container.IsDynamic && ContainerRegistry.Resolve(msg.Entity.Container) == null)
            {
                Pend(_pendingTransfersByCarrier, msg.Entity.Container.NetId, new PendingTransfer { From = from, Msg = msg });
                NebulaLog.Info($"handover IN  #{msg.Entity.NetId} <- {from.Id} waits for carrier #{msg.Entity.Container.NetId}");
                return;
            }
            if (e == null) e = InstantiateGhost(msg.Entity);
            if (e == null) return;
            if (msg.NewEpoch <= e.Epoch && e.HasAuthority)
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
            if (e.OwnerClientId != 0) _players[e.OwnerClientId] = e;
            e.SetAuthority(true);
            HandoversIn++;
            AuthorityReceived?.Invoke(e, from.Id);
            NebulaLog.Info($"handover IN  {e} <- {from.Id} (epoch {msg.NewEpoch}, tick {CurrentTick})");
            if (e.Carried != null) SyncCarriedLeases();

            // Tell the gateway we own it now (a spawn for a known id is an update).
            var spawn = EntitySpawnMsg.From(e, _scratch);
            foreach (var g in _gateways) SendSpawn(g, spawn, MsgId.EntitySpawn);
        }

        /// <summary>Fallback for a bare Rigidbody with no <see cref="NetworkRigidbody"/> (which does this itself, plus velocity).</summary>
        private static void SetGhostPhysics(NetworkIdentity e, bool ghost)
        {
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

        private static void Pend<T>(Dictionary<ulong, List<T>> pending, ulong carrierNetId, T item)
        {
            if (!pending.TryGetValue(carrierNetId, out var list)) pending[carrierNetId] = list = new List<T>();
            list.Add(item);
        }

        /// <summary>A carrier's container is resolvable now: apply the ghosts and handovers that were waiting for it.</summary>
        private void OnDynamicContainerRegistered(Container container)
        {
            ulong netId = container.CarrierNetId;
            if (_pendingGhostsByCarrier.TryGetValue(netId, out var ghosts))
            {
                _pendingGhostsByCarrier.Remove(netId);
                foreach (var msg in ghosts) OnGhostSpawn(null, msg);
            }
            if (_pendingTransfersByCarrier.TryGetValue(netId, out var transfers))
            {
                _pendingTransfersByCarrier.Remove(netId);
                foreach (var t in transfers) OnAuthorityTransfer(t.From, t.Msg);
            }
        }

        private NetworkIdentity InstantiateGhost(EntitySpawnMsg msg)
        {
            var container = ContainerRegistry.Resolve(msg.Container);
            if (container == null && msg.Container.IsDynamic)
            {
                Pend(_pendingGhostsByCarrier, msg.Container.NetId, msg);
                return null;
            }
            var identity = msg.SceneId != 0
                ? BindSceneEntity(msg)
                : NetworkPrefabs.Instantiate(msg.PrefabId, Vector3.zero, Quaternion.identity, container != null ? container.transform : null);
            if (identity == null) return null;
            identity.NetId = msg.NetId;
            identity.HasAuthority = false;
            ApplySpawnData(identity, msg, msg.Epoch);
            EnsureInterpolator(identity).Push(CurrentTick, identity.Container, identity.LocalPosition, identity.LocalRotation, identity.Velocity);
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
            e.OwnerIsBot = (msg.Flags & EntityFlags.OwnerIsBot) != 0;
            e.IsServerDriven = (msg.Flags & EntityFlags.ServerDriven) != 0;
            e.OwnerWorkerIndex = msg.OwnerWorkerIndex;
            var container = ContainerRegistry.Resolve(msg.Container);
            if (container == null && msg.Container.IsDynamic)
            {
                // Its carrier is not here (yet): keep the pose in the container we last knew, rather than a wrong one.
                NebulaLog.Warn($"{e}: spawn data names carrier #{msg.Container.NetId}, unknown here; keeping its current container");
                container = e.Container;
            }
            e.SetContainer(container);
            e.SetLocalPose(container, msg.LocalPosition, msg.LocalRotation);
            e.Velocity = msg.Velocity;
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
                    e.SetAuthority(false);
                    SetGhostPhysics(e, true);
                }
                else return;
            }
            if (msg.Epoch < e.Epoch) return;
            ApplySpawnData(e, msg, msg.Epoch);
            EnsureInterpolator(e).Push(CurrentTick, e.Container, e.LocalPosition, e.LocalRotation, e.Velocity);
        }

        private void OnGhostState(Peer from, NetworkReader r)
        {
            WorldStateMsg.ReadHeader(r, out uint tick, out ushort workerIndex, out ushort count);
            for (int i = 0; i < count; i++)
            {
                var entry = EntityStateEntry.Read(r);
                var e = Find(entry.NetId);
                if (e == null || e.HasAuthority || entry.Epoch < e.Epoch) continue;
                var container = ContainerRegistry.Resolve(entry.Container);
                if (container == null && entry.Container.IsDynamic) continue; // its carrier has not arrived here yet
                EnsureInterpolator(e).Push(tick, container, entry.LocalPosition, entry.LocalRotation, entry.Velocity);
                e.OwnerWorkerIndex = workerIndex;
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
            _pendingGhostsByCarrier.Remove(msg.NetId);
            _pendingTransfersByCarrier.Remove(msg.NetId);
            var e = Find(msg.NetId);
            if (e == null) { DropPendingScene(msg.NetId); return; }
            if (e.HasAuthority || msg.Epoch < e.Epoch) return;
            RemoveLocal(e);
        }

        // ---------------------------------------------------------------------------------------- gateway traffic

        private void PublishToGateways(uint tick)
        {
            if (_gateways.Count == 0) return;
            int slot = -1;
            ushort count = 0;
            for (int i = 0; i < _authoritative.Count; i++)
            {
                var e = _authoritative[i];
                if (slot < 0)
                {
                    _writer.Reset();
                    slot = WorldStateMsg.Begin(_writer, MsgId.WorldState, tick, WorkerIndex);
                }
                Entry(e).Write(_writer);
                count++;
                if (_writer.Length + EntityStateEntry.WireSize > StateBatchBytes)
                {
                    WorldStateMsg.End(_writer, slot, count);
                    foreach (var g in _gateways) Send(g, Delivery.Sequenced);
                    slot = -1;
                    count = 0;
                }
            }
            if (slot >= 0)
            {
                WorldStateMsg.End(_writer, slot, count);
                foreach (var g in _gateways) Send(g, Delivery.Sequenced);
            }

            for (int i = 0; i < _authoritative.Count; i++)
            {
                var e = _authoritative[i];
                if (e.VarsDirty)
                {
                    _scratch.Reset();
                    e.WriteVars(_scratch);
                    _writer.Reset();
                    new EntityVarsMsg { NetId = e.NetId, Epoch = e.Epoch, Vars = _scratch.ToArray() }.Write(_writer, MsgId.EntityVars);
                    foreach (var g in _gateways) Send(g, Delivery.ReliableOrdered);
                }
                if (e.HasSyncState)
                {
                    foreach (var g in _gateways) SendSyncState(e, tick, MsgId.EntityState, g);
                }
                // Every tick, even when the owner's input for it had not arrived and the last one was repeated: the
                // owner must still see what the worker actually simulated, and the lead report is what lets it fix
                // late inputs. (Sending only on consumed ticks left a late owner blind until it got lucky.)
                if (e.OwnerClientId != 0 && e.Predicted != null)
                {
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
                    foreach (var g in _gateways) Send(g, Delivery.Sequenced);
                }
            }
        }

        private void OnSpawnPlayer(Peer gateway, SpawnPlayerMsg msg)
        {
            if (msg.IsBot) _botClients.Add(msg.ClientId); else _botClients.Remove(msg.ClientId);
            if (_players.TryGetValue(msg.ClientId, out var existing) && existing != null)
            {
                if (existing.HasAuthority)
                {
                    // Re-announce; the gateway may have lost track of it.
                    SendSpawn(gateway, EntitySpawnMsg.From(existing, _scratch), MsgId.EntitySpawn);
                    return;
                }
            }
            var container = ContainerRegistry.Get(msg.ContainerIndex) ?? (ContainerRegistry.Count > 0 ? ContainerRegistry.All[0] : null);
            if (_gameMode == null || container == null)
            {
                NebulaLog.Error($"cannot spawn player {msg.ClientId}: gameMode={(_gameMode != null)} container={container}");
                return;
            }
            var identity = _gameMode.OnSpawnPlayer(this, msg.ClientId, msg.Name, container);
            if (identity == null) NebulaLog.Error($"game mode returned no entity for client {msg.ClientId}");
            else if (!identity.IsSpawned) Spawn(identity, container, msg.ClientId);
            else if (identity.OwnerClientId != msg.ClientId) NebulaLog.Error($"game mode spawned {identity} but not for client {msg.ClientId}");
        }

        private void OnDespawnPlayer(Peer gateway, DespawnPlayerMsg msg)
        {
            var e = FindPlayer(msg.ClientId);
            if (e == null) return;
            if (e.HasAuthority)
            {
                _gameMode?.OnPlayerDespawn(this, e);
                Despawn(e);
            }
            else if (_handedOff.TryGetValue(e.NetId, out var to) && _workerPeersById.TryGetValue(to, out var peer))
            {
                _writer.Reset();
                msg.Write(_writer);
                Send(peer, Delivery.ReliableOrdered);
            }
        }

        private void OnClientInput(Peer from, ClientInputMsg msg)
        {
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

        private void OnAuthorityRpc(Peer from, EntityRpcMsg msg)
        {
            var e = Find(msg.NetId);
            if (e == null) return;
            if (e.HasAuthority)
            {
                InvokeRpc(e, msg);
            }
            else if (_handedOff.TryGetValue(e.NetId, out var to) && _workerPeersById.TryGetValue(to, out var peer) && peer != from)
            {
                _writer.Reset();
                msg.Write(_writer, MsgId.AuthorityRpc);
                Send(peer, Delivery.ReliableOrdered);
            }
        }

        private void InvokeRpc(NetworkIdentity e, EntityRpcMsg msg)
        {
            if (msg.BehaviourIndex >= e.Behaviours.Length) return;
            _reader.Set(new ArraySegment<byte>(msg.Args));
            RpcRegistry.Invoke(e.Behaviours[msg.BehaviourIndex], msg.MethodHash, _reader);
        }

        // ---------------------------------------------------------------------------------------- IRpcSink

        void IRpcSink.SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, uint targetClientId)
        {
            var msg = new EntityRpcMsg { NetId = identity.NetId, Epoch = identity.Epoch, BehaviourIndex = behaviourIndex, MethodHash = methodHash, ClientId = targetClientId, Args = ToArray(args) };
            _writer.Reset();
            msg.Write(_writer, MsgId.EntityRpc);
            foreach (var g in _gateways) Send(g, Delivery.ReliableOrdered);
        }

        void IRpcSink.SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args)
        {
            NebulaLog.Warn("ServerRpc sent from a worker; ignored");
        }

        void IRpcSink.SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args)
        {
            Peer target = null;
            if (_handedOff.TryGetValue(identity.NetId, out var to)) _workerPeersById.TryGetValue(to, out target);
            if (target == null) _workerPeersByIndex.TryGetValue(identity.OwnerWorkerIndex, out target);
            if (target == null || !target.HelloReceived)
            {
                NebulaLog.Warn($"AuthorityRpc on {identity}: owner worker {identity.OwnerWorkerIndex} not connected");
                return;
            }
            var msg = new EntityRpcMsg { NetId = identity.NetId, Epoch = identity.Epoch, BehaviourIndex = behaviourIndex, MethodHash = methodHash, ClientId = 0, Args = ToArray(args) };
            _writer.Reset();
            msg.Write(_writer, MsgId.AuthorityRpc);
            Send(target, Delivery.ReliableOrdered);
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
                    new HelloMsg { Role = PeerRole.Worker, Id = WorkerId, Index = WorkerIndex }.Write(_writer);
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
                NebulaLog.Warn($"gateway {peer.Id} disconnected");
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
                _scratchStrings.Clear();
                foreach (var kv in _handedOff) if (kv.Value == peer.Id) _scratchStrings.Add(kv.Key.ToString());
                foreach (var k in _scratchStrings) _handedOff.Remove(ulong.Parse(k));
                _scratchStrings.Clear();
            }
        }

        private void Dispatch(Peer peer, NetworkReader r)
        {
            var id = (MsgId)r.ReadByte();
            if (id == MsgId.Hello)
            {
                var hello = HelloMsg.Read(r);
                peer.Role = hello.Role;
                peer.Id = hello.Id;
                peer.Index = hello.Index;
                peer.HelloReceived = true;
                if (hello.Role == PeerRole.Gateway)
                {
                    _gateways.Add(peer);
                    NebulaLog.Info($"gateway {peer.Id} connected; announcing {_authoritative.Count} entities");
                    foreach (var e in _authoritative) SendSpawn(peer, EntitySpawnMsg.From(e, _scratch), MsgId.EntitySpawn);
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
                case MsgId.DespawnPlayer: OnDespawnPlayer(peer, DespawnPlayerMsg.Read(r)); break;
                case MsgId.ClientInput:
                case MsgId.ForwardInput: OnClientInput(peer, ClientInputMsg.Read(r)); break;
                case MsgId.ServerRpc: OnServerRpc(peer, EntityRpcMsg.Read(r)); break;
                case MsgId.GhostSpawn: OnGhostSpawn(peer, EntitySpawnMsg.Read(r)); break;
                case MsgId.GhostState: OnGhostState(peer, r); break;
                case MsgId.GhostVars: OnGhostVars(peer, EntityVarsMsg.Read(r)); break;
                case MsgId.GhostSyncState: OnGhostSyncState(peer, EntitySyncMsg.Read(r)); break;
                case MsgId.GhostDespawn: OnGhostDespawn(peer, EntityDespawnMsg.Read(r)); break;
                case MsgId.AuthorityTransfer: OnAuthorityTransfer(peer, AuthorityTransferMsg.Read(r)); break;
                case MsgId.AuthorityRpc: OnAuthorityRpc(peer, EntityRpcMsg.Read(r)); break;
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
    }
}
