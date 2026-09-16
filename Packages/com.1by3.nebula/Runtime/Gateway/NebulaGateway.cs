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
    public sealed class NebulaGateway
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
            public readonly HashSet<ulong> Visible = new HashSet<ulong>();
            /// <summary>What the client was last told about its join (<see cref="JoinStatusMsg"/>); only changes are sent.</summary>
            public JoinState Join;
            public ushort JoinEstimate;
            public float NextSpawnAttempt;
            public string SpawnWorkerId = "";
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
            public ulong OwnerClientId;
            /// <summary>The container of the newest pose (static index, or a carrier's net id for a dynamic container).</summary>
            public ContainerRef Container;
            public EntitySpawnMsg LastSpawn;
            public uint LastStateTick;
            public bool HasStateTick;
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

        private readonly NetworkWriter _writer = new NetworkWriter(4096);
        private readonly NetworkReader _reader = new NetworkReader();
        private readonly NetworkWriter _scratch = new NetworkWriter(1024);
        private readonly NetworkWriter _visibilityWriter = new NetworkWriter(1024);

        private Container ScopeContainer(ContainerRef reference)
        {
            for (int depth = 0; reference.IsDynamic && depth < 16; depth++)
            {
                if (!_entities.TryGetValue(reference.NetId, out var carrier)) return null;
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
                .Contains(WorldPosition(entity.Container, entity.LastSpawn.LocalPosition, 0));
        }

        private bool ReconcileVisibility(ClientConn client, EntityRecord entity)
        {
            bool visible = CanObserve(client, entity);
            if (visible && client.Visible.Add(entity.NetId))
            {
                entity.RefreshSpawnState(_scratch);
                _visibilityWriter.Reset();
                entity.LastSpawn.Write(_visibilityWriter, MsgId.EntitySpawn);
                AppendReliable(client, _visibilityWriter.ToSegment());
            }
            else if (!visible && client.Visible.Remove(entity.NetId))
            {
                _visibilityWriter.Reset();
                new EntityDespawnMsg { NetId = entity.NetId, Epoch = entity.Epoch }.Write(_visibilityWriter, MsgId.EntityDespawn);
                AppendReliable(client, _visibilityWriter.ToSegment());
            }
            return visible;
        }

        private void ReconcileView(ClientConn client)
        {
            // Preserve replicas visible on both sides of a crossing, including the public hallway.
            foreach (var entity in _entities.Values) ReconcileVisibility(client, entity);
        }

        private void BroadcastEntity(EntityRecord entity, Delivery delivery)
        {
            var segment = _writer.ToSegment();
            foreach (var client in _clientsById.Values)
            {
                if (!client.Welcomed || !client.Visible.Contains(entity.NetId) || !CanObserve(client, entity)) continue;
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
            foreach (var c in _clientsById.Values) { FlushWorldState(c); FlushReliable(c); }
            _transport.Flush();
            ReportWorldStateStats();
            DropRejectedClients();

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
                if (!c.Welcomed || c.PawnNetId != 0 || Time.unscaledTime < c.NextSpawnAttempt) continue;
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
            };
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
            else { _clientPacketsOut++; _clientBytesOut += payload.Count; }
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
            if (self != null && (self.Incarnation == 0 || self.Incarnation == Incarnation))
            {
                if (self.DrainRequested && !Draining) Drain();
                else if (!self.DrainRequested && Draining) StopDraining();
            }

            ContainerRegistry.SyncRuntime(ControlPlane.Leases);
            _ownership.Clear();
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
                _ownership.Add(new ContainerOwnershipEntry
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
                });
            }
            _writer.Reset();
            ContainerOwnershipMsg.Write(_writer, _ownership);
            BroadcastToClients(Delivery.ReliableOrdered);

            foreach (var w in ControlPlane.Workers)
            {
                if (w.Status == WorkerStatus.Dead || !ControlPlane.IsWorkerAlive(w, Config.WorkerTimeoutSeconds)) continue;
                if (_workersById.ContainsKey(w.WorkerId) || _dialing.Contains(w.WorkerId)) continue;
                int peerId = _transport.Connect(w.Address, w.Port);
                _workersByPeer[peerId] = new WorkerConn { PeerId = peerId, WorkerId = w.WorkerId, Index = (ushort)w.WorkerIndex, Outbound = true };
                _dialing.Add(w.WorkerId);
                NebulaLog.Info($"dialing worker {w.WorkerId} at {w.Address}:{w.Port}");
            }
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
                _entities.Remove(netId);
                _writer.Reset();
                new EntityDespawnMsg { NetId = netId, Epoch = rec.Epoch }.Write(_writer, MsgId.EntityDespawn);
                foreach (var client in _clientsById.Values)
                    if (client.Visible.Remove(netId)) AppendReliable(client, _writer.ToSegment());
                if (rec.OwnerClientId != 0 && _clientsById.TryGetValue(rec.OwnerClientId, out var c) && c.PawnNetId == netId)
                {
                    c.PawnNetId = 0;
                    SendJoinStatus(c, JoinState.Starting);
                    c.NextSpawnAttempt = Time.unscaledTime + 1f; // give the orchestrator a moment to reassign
                }
            }
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
            rec.Epoch = msg.Epoch;
            rec.OwnerWorkerIndex = w.Index;
            rec.OwnerClientId = msg.OwnerClientId;
            rec.Container = msg.Container;
            msg.OwnerWorkerIndex = w.Index;
            rec.LastSpawn = msg;
            rec.HasStateTick = false;
            rec.SeedKeyframes(msg.State);
            if (msg.OwnerClientId != 0 && _clientsById.TryGetValue(msg.OwnerClientId, out var c))
            {
                c.PawnNetId = msg.NetId;
                SendJoinStatus(c, JoinState.Joined);
            }
            foreach (var client in _clientsById.Values)
            {
                bool alreadyVisible = client.Visible.Contains(rec.NetId);
                if (ReconcileVisibility(client, rec) && alreadyVisible)
                {
                    _writer.Reset();
                    msg.Write(_writer, MsgId.EntitySpawn);
                    AppendReliable(client, _writer.ToSegment());
                }
                if (client.PawnNetId == rec.NetId) ReconcileView(client);
            }
        }

        private void OnEntityDespawn(WorkerConn w, EntityDespawnMsg msg)
        {
            if (!_entities.TryGetValue(msg.NetId, out var rec)) return;
            if (msg.Epoch < rec.Epoch || rec.OwnerWorkerIndex != w.Index) return;
            _entities.Remove(msg.NetId);
            if (rec.OwnerClientId != 0 && _clientsById.TryGetValue(rec.OwnerClientId, out var c) && c.PawnNetId == msg.NetId)
            {
                c.PawnNetId = 0;
                c.NextSpawnAttempt = Time.unscaledTime + 0.5f;
            }
            _writer.Reset();
            msg.Write(_writer, MsgId.EntityDespawn);
            foreach (var client in _clientsById.Values)
                if (client.Visible.Remove(msg.NetId)) AppendReliable(client, _writer.ToSegment());
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
                // A spatial RPC (a tracer, a footstep): only clients whose pawn is within its radius of the entity.
                var at = WorldPosition(rec.Container, rec.LastSpawn.LocalPosition, 0);
                float r2 = msg.Radius * msg.Radius;
                var seg = _writer.ToSegment();
                foreach (var c in _clientsById.Values)
                {
                    if (!c.Welcomed || !c.Visible.Contains(rec.NetId) || !CanObserve(c, rec) || !TryGetPawnPosition(c, out var pawnPos)) continue;
                    if ((pawnPos - at).sqrMagnitude <= r2) AppendReliable(c, seg);
                }
            }
            else if (msg.ClientId == 0) BroadcastEntity(rec, Delivery.ReliableOrdered);
            else if (_clientsById.TryGetValue(msg.ClientId, out var c) && c.Visible.Contains(rec.NetId) && CanObserve(c, rec)) AppendReliable(c, _writer.ToSegment());
        }

        private readonly List<EntityStateEntry> _scratchEntries = new List<EntityStateEntry>();

        // Verbose relay statistics, logged once a second: how much world state arrives and why entries are dropped.
        private int _wsPackets, _wsEntries, _wsUnknown, _wsStale, _wsWrongOwner, _wsSent;
        private float _nextWsReport;

        private void ReportWorldStateStats()
        {
            if (!NebulaLog.Verbose || Time.unscaledTime < _nextWsReport) return;
            _nextWsReport = Time.unscaledTime + 1f;
            NebulaLog.Debugf($"worldstate: {_wsPackets} packets {_wsEntries} entries in; dropped unknown={_wsUnknown} stale={_wsStale} wrongOwner={_wsWrongOwner}; {_wsSent} entries sent to {_clientsById.Count} client(s); {_entities.Count} entities known");
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
                    var world = WorldPosition(rec.Container, rec.LastSpawn.LocalPosition, 0);
                    var rotation = WorldRotation(rec.Container, rec.LastSpawn.LocalRotation, 0);
                    rec.LastSpawn.LocalPosition = ContainerPosition(entry.Container, world, 0);
                    rec.LastSpawn.LocalRotation = Quaternion.Inverse(WorldRotation(entry.Container, Quaternion.identity, 0)) * rotation;
                }
                bool changedScope = ScopeContainer(rec.Container)?.InstanceId != ScopeContainer(entry.Container)?.InstanceId;
                rec.Container = entry.Container;
                rec.LastSpawn.Container = entry.Container;
                entry.Merge(ref rec.LastSpawn.LocalPosition, ref rec.LastSpawn.LocalRotation, ref rec.LastSpawn.LocalScale, ref rec.LastSpawn.Velocity);
                if (changedScope)
                    foreach (var observer in _clientsById.Values) ReconcileView(observer);
                _scratchEntries.Add(entry);
            }
            if (_scratchEntries.Count == 0) return;

            foreach (var c in _clientsById.Values)
            {
                if (!c.Welcomed) continue;
                bool hasPawn = TryGetPawnPosition(c, out var pawnPos);
                for (int i = 0; i < _scratchEntries.Count; i++)
                {
                    var entry = _scratchEntries[i];
                    if (!ReconcileVisibility(c, _entities[entry.NetId])) continue;
                    if (entry.Reliable)
                    {
                        _writer.Reset();
                        int slot = WorldStateMsg.Begin(_writer, MsgId.WorldState, tick, w.Index);
                        entry.Write(_writer);
                        WorldStateMsg.End(_writer, slot, 1);
                        AppendReliable(c, _writer.ToSegment());
                        _wsSent++;
                        continue;
                    }
                    if (!WantsThisTick(entry, tick, hasPawn, pawnPos, c.PawnNetId)) continue;
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
        private bool WantsThisTick(in EntityStateEntry entry, uint tick, bool hasPawn, Vector3 pawnPos, ulong pawnNetId)
        {
            int divisor;
            if (entry.NetId == pawnNetId) return true;
            if (!hasPawn) divisor = Config.InterestFarDivisor;
            else
            {
                var position = _entities.TryGetValue(entry.NetId, out var record) ? record.LastSpawn.LocalPosition : entry.LocalPosition;
                var pos = WorldPosition(entry.Container, position, 0);
                float d2 = (pos - pawnPos).sqrMagnitude;
                if (d2 <= Config.InterestNearRadius * Config.InterestNearRadius) return true;
                divisor = d2 <= Config.InterestFarRadius * Config.InterestFarRadius ? Config.InterestMidDivisor : Config.InterestFarDivisor;
            }
            if (divisor <= 1) return true;
            return (tick + (uint)(entry.NetId % (ulong)divisor)) % (uint)divisor == 0;
        }

        private bool TryGetPawnPosition(ClientConn c, out Vector3 position)
        {
            position = default;
            if (c.PawnNetId == 0 || !_entities.TryGetValue(c.PawnNetId, out var rec)) return false;
            position = WorldPosition(rec.Container, rec.LastSpawn.LocalPosition, 0);
            return true;
        }

        /// <summary>
        /// A container-local position as a world position. The gateway holds no entities, so a dynamic container's
        /// frame is rebuilt from its carrier's newest pose (itself container-local, hence the recursion): a
        /// DynamicContainer's frame is its carrier's root transform, which is what makes this possible here.
        /// </summary>
        private Vector3 WorldPosition(ContainerRef container, Vector3 local, int depth)
        {
            if (container.IsDynamic)
            {
                if (depth > 8 || !_entities.TryGetValue(container.NetId, out var carrier)) return local;
                var inParent = carrier.LastSpawn.LocalPosition + carrier.LastSpawn.LocalRotation * Vector3.Scale(carrier.LastSpawn.LocalScale, local);
                return WorldPosition(carrier.Container, inParent, depth + 1);
            }
            var c = ContainerRegistry.Resolve(container);
            return c != null ? c.ToWorld(local) : local;
        }

        private Quaternion WorldRotation(ContainerRef container, Quaternion local, int depth)
        {
            if (container.IsDynamic)
            {
                if (depth > 8 || !_entities.TryGetValue(container.NetId, out var carrier)) return local;
                return WorldRotation(carrier.Container, carrier.LastSpawn.LocalRotation, depth + 1) * local;
            }
            var c = ContainerRegistry.Resolve(container);
            return c != null ? c.Rotation * local : local;
        }

        private Vector3 ContainerPosition(ContainerRef container, Vector3 world, int depth)
        {
            if (container.IsDynamic)
            {
                if (depth > 8 || !_entities.TryGetValue(container.NetId, out var carrier)) return world;
                var parent = ContainerPosition(carrier.Container, world, depth + 1);
                var relative = Quaternion.Inverse(carrier.LastSpawn.LocalRotation) * (parent - carrier.LastSpawn.LocalPosition);
                var scale = carrier.LastSpawn.LocalScale;
                return new Vector3(scale.x != 0 ? relative.x / scale.x : 0, scale.y != 0 ? relative.y / scale.y : 0, scale.z != 0 ? relative.z / scale.z : 0);
            }
            var c = ContainerRegistry.Resolve(container);
            return c != null ? c.ToLocal(world) : world;
        }

        /// <summary>How many carriers an entity's container sits inside (0 in a static container), so a late joiner gets carriers before their contents.</summary>
        private int CarrierDepth(EntityRecord rec)
        {
            int depth = 0;
            var r = rec.Container;
            while (r.IsDynamic && depth < 8 && _entities.TryGetValue(r.NetId, out var carrier))
            {
                depth++;
                r = carrier.Container;
            }
            return depth;
        }

        private readonly List<EntityRecord> _replayOrder = new List<EntityRecord>();

        private void OnOwnerState(WorkerConn w, NetworkReader r)
        {
            var msg = OwnerStateMsg.Read(r);
            if (!_entities.TryGetValue(msg.NetId, out var rec) || msg.Epoch < rec.Epoch || rec.OwnerWorkerIndex != w.Index) return;
            if (!_clientsById.TryGetValue(msg.OwnerClientId, out var c)) return;
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
        /// A client presented a session token: when it is ours, unexpired and for the identity that just
        /// authenticated, the session id is taken over (with a newer generation) instead of the provisional one, and
        /// a link that still holds that session on this gateway is dropped. The pawn, if the worker still has it,
        /// is found through the session id on the worker's next announcement or in what this gateway already knows.
        /// </summary>
        private void TryReclaim(ClientConn c, string session)
        {
            if (session.Length == 0 || _sessions == null) return;
            if (!_sessions.Verify(session, out var claims, out string error))
            {
                NebulaLog.Info($"client '{c.Name}': session token ignored ({error}); starting a new session");
                return;
            }
            if (claims.Identity != c.Identity)
            {
                NebulaLog.Warn($"client '{c.Name}': session token belongs to another identity; starting a new session");
                return;
            }
            if (_clientsById.TryGetValue(claims.SessionId, out var previous) && previous != c)
            {
                NebulaLog.Info($"client {claims.SessionId} '{c.Name}' reconnected while its old link is still open; dropping the old link");
                _clientsByPeer.Remove(previous.PeerId);
                _clientsById.Remove(previous.ClientId);
                _transport.Disconnect(previous.PeerId);
                if (previous.PawnNetId != 0) c.PawnNetId = previous.PawnNetId;
            }
            _clientsById.Remove(c.ClientId);
            c.ClientId = claims.SessionId;
            c.Generation = NextGeneration(claims.Generation);
            c.Reclaimed = true;
            _clientsById[c.ClientId] = c;
            if (c.PawnNetId == 0)
            {
                foreach (var rec in _entities.Values)
                    if (rec.OwnerClientId == c.ClientId) { c.PawnNetId = rec.NetId; break; }
            }
            _recentlyLost.RemoveAll(kv => kv.Key == c.ClientId);
        }

        /// <summary>The client is who it says it is: welcome it, replay the world, and ask a worker for a pawn (or for the one it had).</summary>
        private void WelcomeClient(ClientConn c, in AuthResult auth, string issuedToken, string session)
        {
            int peerId = c.PeerId;
            c.Identity = auth.Identity ?? "";
            c.Generation = NextGeneration(0);
            TryReclaim(c, session);
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
            _writer.Reset();
            ContainerOwnershipMsg.Write(_writer, _ownership);
            Send(peerId, Delivery.ReliableOrdered, _writer.ToSegment());
            // Carriers before their contents: a passenger's spawn names the ship's container, which the client
            // can only resolve once it has the ship (it holds the spawn otherwise, but this keeps that rare).
            _replayOrder.Clear();
            _replayOrder.AddRange(_entities.Values);
            _replayOrder.Sort((a, b) => CarrierDepth(a).CompareTo(CarrierDepth(b)));
            foreach (var rec in _replayOrder)
            {
                if (!CanObserve(c, rec)) continue;
                c.Visible.Add(rec.NetId);
                rec.RefreshSpawnState(_scratch);
                _writer.Reset();
                rec.LastSpawn.Write(_writer, MsgId.EntitySpawn);
                Send(peerId, Delivery.ReliableOrdered, _writer.ToSegment());
            }
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
            _clientsByPeer.Remove(c.PeerId);
            _clientsById.Remove(c.ClientId);
            NebulaLog.Info($"client {c.ClientId} '{c.Name}' disconnected");
            if (!c.Welcomed) return;
            // The worker keeps the pawn for SessionReclaimSeconds in case the client comes back (here or elsewhere);
            // the despawn carries the generation so a gateway that has since claimed the session is not undone.
            if (c.PawnNetId != 0 && _entities.TryGetValue(c.PawnNetId, out var rec) && _workersByIndex.TryGetValue(rec.OwnerWorkerIndex, out var w))
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
            _writer.Reset();
            new SpawnPlayerMsg { ClientId = c.ClientId, Container = container, Name = c.Name, IsBot = c.IsBot, Identity = c.Identity, Generation = c.Generation }.Write(_writer);
            Send(worker.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
        }

        private void TryRequestSpawn(ClientConn c)
        {
            c.NextSpawnAttempt = Time.unscaledTime + 3f;
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

        private void BroadcastToClients(Delivery delivery)
        {
            var seg = _writer.ToSegment();
            foreach (var c in _clientsById.Values)
            {
                if (!c.Welcomed) continue;
                if (delivery == Delivery.ReliableOrdered) AppendReliable(c, seg);
                else Send(c.PeerId, delivery, seg);
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
