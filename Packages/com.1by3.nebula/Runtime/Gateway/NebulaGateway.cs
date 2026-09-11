using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The one address clients connect to. It holds a link to every worker, forwards each client's inputs to whichever
    /// worker currently owns that client's entity, and fans the workers' replication streams out to the clients,
    /// de-duplicating by authority epoch so a handover is invisible to the client. Nothing here is authoritative:
    /// if the gateway dies, clients reconnect and the workers re-announce their entities.
    /// </summary>
    public sealed class NebulaGateway : MonoBehaviour
    {
        private sealed class ClientConn
        {
            public int PeerId;
            public uint ClientId;
            public string Name = "";
            public bool IsBot;
            public bool Welcomed;
            public ulong PawnNetId;
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

        private sealed class WorkerConn
        {
            public int PeerId;
            public string WorkerId = "";
            public ushort Index;
            public bool Ready;
            public bool Outbound;
        }

        private sealed class EntityRecord
        {
            public ulong NetId;
            public uint Epoch;
            public ushort OwnerWorkerIndex;
            public uint OwnerClientId;
            /// <summary>The container of the newest pose (static index, or a carrier's net id for a dynamic container).</summary>
            public ContainerRef Container;
            public EntitySpawnMsg LastSpawn;
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
        public string GatewayId { get; private set; }
        public int ClientCount => _clientsById.Count;
        public int WorkerCount => _workersById.Count;
        public int EntityCount => _entities.Count;

        private ITransport _transport;
        private readonly Dictionary<int, ClientConn> _clientsByPeer = new Dictionary<int, ClientConn>();
        private readonly Dictionary<uint, ClientConn> _clientsById = new Dictionary<uint, ClientConn>();
        private readonly Dictionary<int, WorkerConn> _workersByPeer = new Dictionary<int, WorkerConn>();
        private readonly Dictionary<string, WorkerConn> _workersById = new Dictionary<string, WorkerConn>();
        private readonly Dictionary<ushort, WorkerConn> _workersByIndex = new Dictionary<ushort, WorkerConn>();
        private readonly HashSet<string> _dialing = new HashSet<string>();
        private readonly Dictionary<ulong, EntityRecord> _entities = new Dictionary<ulong, EntityRecord>();
        private readonly List<ContainerOwnershipEntry> _ownership = new List<ContainerOwnershipEntry>();
        private readonly List<ulong> _scratchIds = new List<ulong>();

        private readonly NetworkWriter _writer = new NetworkWriter(4096);
        private readonly NetworkReader _reader = new NetworkReader();
        private readonly NetworkWriter _scratch = new NetworkWriter(1024);
        private uint _nextClientId = 1;
        private bool _registered;
        private float _nextHeartbeat;

        public void Initialize(NebulaConfig config, IControlPlane controlPlane)
        {
            Config = config;
            ControlPlane = controlPlane;
            GatewayId = CommandLine.Get("nebula-gateway-id", "gw1");
            _transport = new LiteNetTransport("gateway");
            _transport.Listen(config.GatewayPort);
            ControlPlane.Changed += OnControlPlaneChanged;
            NebulaLog.Info($"gateway {GatewayId} listening on udp/{config.GatewayPort}");
        }

        private void OnDestroy()
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
        }

        private void Update()
        {
            _transport.Poll(HandleTransportEvent);
            foreach (var c in _clientsById.Values) { FlushWorldState(c); FlushReliable(c); }

            if (!_registered && ControlPlane.IsConnected)
            {
                ControlPlane.RegisterGateway(GatewayId, Config.GatewayAddress, Config.GatewayPort);
                _registered = true;
            }
            if (_registered && Time.unscaledTime >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.unscaledTime + Config.WorkerHeartbeatSeconds;
                ControlPlane.HeartbeatGateway(GatewayId);
            }

            // Players without a pawn get one as soon as a worker is available.
            foreach (var c in _clientsById.Values)
            {
                if (!c.Welcomed || c.PawnNetId != 0 || Time.unscaledTime < c.NextSpawnAttempt) continue;
                TryRequestSpawn(c);
            }
        }

        // ---------------------------------------------------------------------------------------- control plane

        private void OnControlPlaneChanged()
        {
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
                        new HelloMsg { Role = PeerRole.Gateway, Id = GatewayId, Index = 0 }.Write(_writer);
                        _transport.Send(ev.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
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
                        if (_workersByPeer.TryGetValue(ev.PeerId, out var w)) DispatchWorker(w, _reader);
                        else DispatchClient(ev.PeerId, _reader);
                    }
                    catch (Exception e) { NebulaLog.Error($"bad packet from peer {ev.PeerId}: {e}"); }
                    break;
                }
            }
        }

        // ---------------------------------------------------------------------------------------- workers

        private void DispatchWorker(WorkerConn w, NetworkReader r)
        {
            var id = (MsgId)r.ReadByte();
            if (id == MsgId.Hello)
            {
                var hello = HelloMsg.Read(r);
                w.WorkerId = hello.Id;
                w.Index = (ushort)hello.Index;
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
                BroadcastToClients(Delivery.ReliableOrdered);
                if (rec.OwnerClientId != 0 && _clientsById.TryGetValue(rec.OwnerClientId, out var c) && c.PawnNetId == netId)
                {
                    c.PawnNetId = 0;
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
            rec.SeedKeyframes(msg.State);
            if (msg.OwnerClientId != 0 && _clientsById.TryGetValue(msg.OwnerClientId, out var c))
            {
                c.PawnNetId = msg.NetId;
            }
            _writer.Reset();
            msg.Write(_writer, MsgId.EntitySpawn);
            BroadcastToClients(Delivery.ReliableOrdered);
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
            BroadcastToClients(Delivery.ReliableOrdered);
        }

        private void OnEntityVars(WorkerConn w, EntityVarsMsg msg, NetworkReader r)
        {
            if (!_entities.TryGetValue(msg.NetId, out var rec) || msg.Epoch < rec.Epoch || rec.OwnerWorkerIndex != w.Index) return;
            rec.LastSpawn.Vars = msg.Vars;
            rec.LastSpawn.Epoch = msg.Epoch;
            _writer.Reset();
            msg.Write(_writer, MsgId.EntityVars);
            BroadcastToClients(Delivery.ReliableOrdered);
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
            BroadcastToClients(msg.Delivery);
        }

        private void OnEntityRpc(WorkerConn w, NetworkReader r)
        {
            var msg = EntityRpcMsg.Read(r);
            if (!_entities.TryGetValue(msg.NetId, out var rec) || msg.Epoch < rec.Epoch) return;
            _writer.Reset();
            msg.Write(_writer, MsgId.EntityRpc);
            if (msg.ClientId == 0) BroadcastToClients(Delivery.ReliableOrdered);
            else if (_clientsById.TryGetValue(msg.ClientId, out var c) && c.Welcomed) AppendReliable(c, _writer.ToSegment());
        }

        private readonly List<EntityStateEntry> _scratchEntries = new List<EntityStateEntry>();

        private void OnWorldState(WorkerConn w, NetworkReader r)
        {
            // Keep only the entries this worker is still the authority for (drops a stale sender after a handover),
            // remember every entity's latest pose, then give each client the subset it is interested in.
            WorldStateMsg.ReadHeader(r, out uint tick, out ushort workerIndex, out ushort count);
            _scratchEntries.Clear();
            for (int i = 0; i < count; i++)
            {
                var entry = EntityStateEntry.Read(r);
                if (!_entities.TryGetValue(entry.NetId, out var rec)) continue;
                if (entry.Epoch < rec.Epoch || rec.OwnerWorkerIndex != w.Index) continue;
                rec.Container = entry.Container;
                rec.LastSpawn.Container = entry.Container;
                rec.LastSpawn.LocalPosition = entry.LocalPosition;
                rec.LastSpawn.LocalRotation = entry.LocalRotation;
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
                    if (!WantsThisTick(entry, tick, hasPawn, pawnPos, c.PawnNetId)) continue;
                    AppendWorldState(c, tick, w.Index, entry);
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
                c.Pending ??= new NetworkWriter(NebulaWorker.StateBatchBytes + 64);
                c.Pending.Reset();
                c.PendingSlot = WorldStateMsg.Begin(c.Pending, MsgId.WorldState, tick, workerIndex);
                c.PendingCount = 0;
                c.PendingTick = tick;
                c.PendingWorker = workerIndex;
            }
            entry.Write(c.Pending);
            c.PendingCount++;
            if (c.Pending.Length + EntityStateEntry.WireSize > NebulaWorker.StateBatchBytes) FlushWorldState(c);
        }

        private void FlushWorldState(ClientConn c)
        {
            if (c.PendingSlot < 0) return;
            WorldStateMsg.End(c.Pending, c.PendingSlot, c.PendingCount);
            _transport.Send(c.PeerId, Delivery.Sequenced, c.Pending.ToSegment());
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
                var pos = WorldPosition(entry.Container, entry.LocalPosition, 0);
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
                var carrierPos = WorldPosition(carrier.Container, carrier.LastSpawn.LocalPosition, depth + 1);
                var carrierRot = WorldRotation(carrier.Container, carrier.LastSpawn.LocalRotation, depth + 1);
                return carrierPos + carrierRot * local;
            }
            var c = ContainerRegistry.Get(container.Index);
            return c != null ? c.ToWorld(local) : local;
        }

        private Quaternion WorldRotation(ContainerRef container, Quaternion local, int depth)
        {
            if (container.IsDynamic)
            {
                if (depth > 8 || !_entities.TryGetValue(container.NetId, out var carrier)) return local;
                return WorldRotation(carrier.Container, carrier.LastSpawn.LocalRotation, depth + 1) * local;
            }
            var c = ContainerRegistry.Get(container.Index);
            return c != null ? c.Rotation * local : local;
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
            _transport.Send(c.PeerId, Delivery.Sequenced, _writer.ToSegment());
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
                    var w = new WorkerConn { PeerId = peerId, WorkerId = hello.Id, Index = (ushort)hello.Index, Ready = true };
                    _workersByPeer[peerId] = w;
                    _workersById[w.WorkerId] = w;
                    _workersByIndex[w.Index] = w;
                    return;
                }
                if (c == null)
                {
                    c = new ClientConn { PeerId = peerId, ClientId = _nextClientId++ };
                    _clientsByPeer[peerId] = c;
                    _clientsById[c.ClientId] = c;
                }
                c.Name = string.IsNullOrEmpty(hello.Id) ? $"player{c.ClientId}" : hello.Id;
                c.IsBot = (hello.Flags & HelloFlags.Bot) != 0;
                c.Welcomed = true;
                NebulaLog.Info($"client {c.ClientId} '{c.Name}'{(c.IsBot ? " (bot)" : "")} connected");

                _writer.Reset();
                new WelcomeMsg { ClientId = c.ClientId, TickRate = NetworkTime.TickRate, ServerTick = NetworkTime.DerivedTick }.Write(_writer);
                _transport.Send(peerId, Delivery.ReliableOrdered, _writer.ToSegment());
                _writer.Reset();
                ContainerOwnershipMsg.Write(_writer, _ownership);
                _transport.Send(peerId, Delivery.ReliableOrdered, _writer.ToSegment());
                // Carriers before their contents: a passenger's spawn names the ship's container, which the client
                // can only resolve once it has the ship (it holds the spawn otherwise, but this keeps that rare).
                _replayOrder.Clear();
                _replayOrder.AddRange(_entities.Values);
                _replayOrder.Sort((a, b) => CarrierDepth(a).CompareTo(CarrierDepth(b)));
                foreach (var rec in _replayOrder)
                {
                    rec.RefreshSpawnState(_scratch);
                    _writer.Reset();
                    rec.LastSpawn.Write(_writer, MsgId.EntitySpawn);
                    _transport.Send(peerId, Delivery.ReliableOrdered, _writer.ToSegment());
                }
                TryRequestSpawn(c);
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
                    _transport.Send(w.PeerId, Delivery.Sequenced, _writer.ToSegment());
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
                    _transport.Send(w.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
                    break;
                }
                case MsgId.Ping:
                {
                    var ping = PingMsg.Read(r);
                    _writer.Reset();
                    new PongMsg { ClientTime = ping.ClientTime, ServerTick = NetworkTime.DerivedTick }.Write(_writer);
                    _transport.Send(peerId, Delivery.Sequenced, _writer.ToSegment());
                    break;
                }
                default:
                    NebulaLog.Warn($"gateway got unexpected {id} from client {c.ClientId}");
                    break;
            }
        }

        private void OnClientLost(ClientConn c)
        {
            _clientsByPeer.Remove(c.PeerId);
            _clientsById.Remove(c.ClientId);
            NebulaLog.Info($"client {c.ClientId} '{c.Name}' disconnected");
            if (c.PawnNetId != 0 && _entities.TryGetValue(c.PawnNetId, out var rec) && _workersByIndex.TryGetValue(rec.OwnerWorkerIndex, out var w))
            {
                _writer.Reset();
                new DespawnPlayerMsg { ClientId = c.ClientId }.Write(_writer);
                _transport.Send(w.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
            }
        }

        private void TryRequestSpawn(ClientConn c)
        {
            c.NextSpawnAttempt = Time.unscaledTime + 3f;
            // Any container with an active lease whose worker we are connected to.
            var candidates = new List<Container>();
            foreach (var container in ContainerRegistry.All)
            {
                if (string.IsNullOrEmpty(container.OwnerWorkerId)) continue;
                if (!_workersById.TryGetValue(container.OwnerWorkerId, out var w) || !w.Ready) continue;
                candidates.Add(container);
            }
            if (candidates.Count == 0)
            {
                NebulaLog.Debugf($"no container available to spawn client {c.ClientId} yet");
                return;
            }
            var pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            var worker = _workersById[pick.OwnerWorkerId];
            c.SpawnWorkerId = worker.WorkerId;
            _writer.Reset();
            new SpawnPlayerMsg { ClientId = c.ClientId, ContainerIndex = pick.Index, Name = c.Name, IsBot = c.IsBot }.Write(_writer);
            _transport.Send(worker.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
            NebulaLog.Info($"asked {worker.WorkerId} to spawn client {c.ClientId} in {pick.ContainerId}");
        }

        private void BroadcastToClients(Delivery delivery)
        {
            var seg = _writer.ToSegment();
            foreach (var c in _clientsById.Values)
            {
                if (!c.Welcomed) continue;
                if (delivery == Delivery.ReliableOrdered) AppendReliable(c, seg);
                else _transport.Send(c.PeerId, delivery, seg);
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
            _transport.Send(c.PeerId, Delivery.ReliableOrdered, c.Reliable.ToSegment());
            c.ReliableSlot = -1;
            c.ReliableCount = 0;
        }
    }
}
