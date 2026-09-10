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
        public NetworkIdentity LocalPlayer { get; private set; }
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

        private ITransport _transport;
        private int _gatewayPeer = -1;
        private readonly Dictionary<ulong, NetworkIdentity> _entities = new Dictionary<ulong, NetworkIdentity>();
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
        /// <summary>After raising the lead, reports for inputs sent with the old lead keep arriving for about an RTT; ignore them.</summary>
        private float _leadHoldUntil;
        private float _leadRelaxAt;

        /// <summary>Last connection failure or disconnect reason, for the title screen. Empty while healthy.</summary>
        public string LastError { get; private set; } = "";
        /// <summary>Whether the client is trying to be connected (set by <see cref="Connect"/>/<see cref="ConnectTo"/>, cleared by <see cref="Disconnect"/>).</summary>
        public bool WantsConnection { get; private set; }

        /// <param name="autoConnect">Connect to <see cref="NebulaConfig.GatewayAddress"/> at once (bots, scripted clients);
        /// false leaves the client idle until <see cref="ConnectTo"/> is called from the title screen.</param>
        public void Initialize(NebulaConfig config, bool autoConnect = true)
        {
            Config = config;
            PlayerName = CommandLine.Get("nebula-name", Environment.UserName);
            NebulaRuntime.RpcSink = this;
            _transport = new LiteNetTransport("client");
            _transport.StartClient();
            if (autoConnect) Connect();
        }

        /// <summary>Connect to a gateway chosen at runtime (title screen). Replaces the configured address for reconnects.</summary>
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

        /// <summary>Leave the gateway and stop reconnecting; the title screen comes back.</summary>
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
            _transport?.Dispose();
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
            _transport.Poll(HandleTransportEvent);

            if (ConnectionState == State.Disconnected && WantsConnection && Time.unscaledTime >= _nextConnectAttempt)
            {
                _nextConnectAttempt = Time.unscaledTime + 2f;
                Connect();
            }
            if (ConnectionState == State.Disconnected || ConnectionState == State.Connecting) return;
            _frames++;
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
                if (rtt >= 0) RttMs = rtt;
            }

            // Server clock estimate: anchor on the newest tick heard, advance with real time.
            double elapsedTicks = (Time.unscaledTimeAsDouble - _serverTickAnchorTime) * NetworkTime.TickRate;
            _serverTickEstimate = _serverTickAnchor + elapsedTicks;
            NetworkTime.LatestServerTick = _latestServerTick;

            // Remote entities render a few ticks behind the newest snapshot; the local player is predicted.
            double targetRender = _serverTickEstimate - Config.InterpolationDelayTicks;
            if (Math.Abs(targetRender - _renderTick) > 10) _renderTick = targetRender;
            else _renderTick += (targetRender - _renderTick) * 0.1;
            NetworkTime.RenderTick = _renderTick;
            foreach (var e in _entities.Values)
            {
                if (!e.IsLocalPlayer && e.Interpolator != null && e.Interpolator.Sample(_renderTick, out var pos, out var rot))
                {
                    e.transform.SetPositionAndRotation(pos, rot);
                    e.Velocity = e.Interpolator.LatestVelocity;
                }
                // The local player too: its server-authoritative children (a NetworkTransform on a turret, say) interpolate.
                e.RemoteTick(_renderTick);
            }
        }

        private void FixedUpdate()
        {
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
        }

        private void ReportTelemetry()
        {
            var predicted = LocalPlayer != null ? LocalPlayer.Predicted : null;
            int corrections = predicted != null ? predicted.Corrections - _lastCorrections : 0;
            if (predicted != null) _lastCorrections = predicted.Corrections;
            float seconds = TelemetryIntervalSeconds;
            NebulaLog.Info($"client {_frames / seconds:0} fps entities {_entities.Count} in {_packetsIn / seconds:0} pkt/s {_bytesIn / seconds / 1024f:0.0} KB/s {_stateEntriesIn / seconds:0} states/s (msgs: state {_statePacketsIn / seconds:0} rpc {_rpcPacketsIn / seconds:0} vars {_varsPacketsIn / seconds:0}) rtt {RttMs}ms lead {InputLeadTicks} (adj {InputLeadAdjustTicks}, worker saw {LastReportedInputLead}) corrections {corrections}{(predicted != null && corrections > 0 ? $" last {predicted.LastCorrectionMagnitude:0.00}m" : "")}");
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
                    new HelloMsg { Role = PeerRole.Client, Id = PlayerName, Index = 0, Flags = CommandLine.Has("nebula-bot") ? HelloFlags.Bot : HelloFlags.None }.Write(_writer);
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
                    NoteServerTick(w.ServerTick);
                    SetState(State.InGame);
                    NebulaLog.Info($"welcome: clientId={ClientId} serverTick={w.ServerTick}");
                    break;
                }
                case MsgId.Pong:
                {
                    var p = PongMsg.Read(r);
                    NoteServerTick(p.ServerTick);
                    break;
                }
                case MsgId.ContainerOwnership:
                {
                    foreach (var e in ContainerOwnershipMsg.Read(r))
                        ContainerRegistry.ApplyLease(e.ContainerId, e.WorkerId, e.WorkerIndex, e.Epoch);
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
            var container = ContainerRegistry.Get(msg.ContainerIndex);
            if (_entities.TryGetValue(msg.NetId, out var e))
            {
                if (msg.Epoch < e.Epoch) return;
                ushort oldWorker = e.OwnerWorkerIndex;
                e.Epoch = msg.Epoch;
                e.OwnerWorkerIndex = msg.OwnerWorkerIndex;
                e.OwnerClientId = msg.OwnerClientId;
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

            e = NetworkPrefabs.Instantiate(msg.PrefabId, Vector3.zero, Quaternion.identity, container != null ? container.transform : null);
            if (e == null) return;
            e.NetId = msg.NetId;
            e.Epoch = msg.Epoch;
            e.OwnerClientId = msg.OwnerClientId;
            e.OwnerIsBot = (msg.Flags & EntityFlags.OwnerIsBot) != 0;
            e.IsServerDriven = (msg.Flags & EntityFlags.ServerDriven) != 0;
            e.OwnerWorkerIndex = msg.OwnerWorkerIndex;
            e.HasAuthority = false;
            e.IsLocalPlayer = msg.OwnerClientId != 0 && msg.OwnerClientId == ClientId;
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
            if (!e.IsLocalPlayer)
            {
                e.Interpolator = e.gameObject.AddComponent<RemoteInterpolator>();
                e.Interpolator.Push(_latestServerTick, e.transform.position, e.transform.rotation, e.Velocity);
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
            if (!_entities.TryGetValue(msg.NetId, out var e)) return;
            if (msg.Epoch < e.Epoch) return;
            _entities.Remove(msg.NetId);
            if (LocalPlayer == e) LocalPlayer = null;
            e.InvokeDespawn();
            EntityDespawned?.Invoke(e);
            Destroy(e.gameObject);
        }

        private void OnEntityVars(EntityVarsMsg msg)
        {
            if (!_entities.TryGetValue(msg.NetId, out var e) || msg.Epoch < e.Epoch) return;
            _reader.Set(new ArraySegment<byte>(msg.Vars));
            e.ReadVars(_reader);
            e.ClearDirty();
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
            _stateEntriesIn += count;
            for (int i = 0; i < count; i++)
            {
                var entry = EntityStateEntry.Read(r);
                if (!_entities.TryGetValue(entry.NetId, out var e) || entry.Epoch < e.Epoch) continue;
                var container = ContainerRegistry.Get(entry.ContainerIndex);
                if (container != e.Container) e.SetContainer(container);
                if (e.OwnerWorkerIndex != workerIndex)
                {
                    ushort old = e.OwnerWorkerIndex;
                    e.OwnerWorkerIndex = workerIndex;
                    AuthorityChangesSeen++;
                    EntityAuthorityChanged?.Invoke(e, old, workerIndex);
                }
                if (e.IsLocalPlayer) continue; // predicted; reconciled through OwnerState
                Vector3 world = container != null ? container.ToWorld(entry.LocalPosition) : entry.LocalPosition;
                Quaternion rot = container != null ? container.Rotation * entry.LocalRotation : entry.LocalRotation;
                e.Interpolator?.Push(tick, world, rot, entry.Velocity);
            }
        }

        private void OnEntityState(EntitySyncMsg msg)
        {
            if (!_entities.TryGetValue(msg.NetId, out var e) || msg.Epoch < e.Epoch) return;
            NoteServerTick(msg.Tick);
            // Behaviours the local client is itself authoritative for (owner mode) ignore their own echo inside ReadSyncState.
            _reader.Set(new ArraySegment<byte>(msg.Chunks));
            e.ReadSyncState(_reader, msg.Tick, ContainerRegistry.Get(msg.ContainerIndex));
        }

        private void OnOwnerState(OwnerStateMsg msg)
        {
            if (LocalPlayer == null || LocalPlayer.NetId != msg.NetId || LocalPlayer.Predicted == null) return;
            if (msg.Epoch < LocalPlayer.Epoch) return;
            if (msg.InputLead != OwnerStateMsg.NoInputLead) NoteInputLead(msg.InputLead);
            if (msg.Tick == 0) return;
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
                Destroy(e.gameObject);
            }
            _entities.Clear();
            LocalPlayer = null;
        }

        public NetworkIdentity Find(ulong netId) => _entities.TryGetValue(netId, out var e) ? e : null;

        // ---------------------------------------------------------------------------------------- IRpcSink

        void IRpcSink.SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, uint targetClientId)
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
