using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Wire message ids. One byte header on every packet. The same entity message shapes are used on all three
    /// links (client-gateway, gateway-worker, worker-worker); only the id tells the receiver which role sent it.
    /// </summary>
    public enum MsgId : byte
    {
        // Handshake / keepalive
        Hello = 1,
        Welcome = 2,
        Ping = 3,
        Pong = 4,

        // Entity replication (worker -> gateway -> clients)
        EntitySpawn = 10,
        EntityDespawn = 11,
        EntityVars = 12,
        EntityRpc = 13,
        WorldState = 14,
        OwnerState = 15,
        ContainerOwnership = 16,
        /// <summary>Per-behaviour sync chunks (NetworkTransform/NetworkAnimator) for one entity and tick.</summary>
        EntityState = 17,
        /// <summary>
        /// Gateway -> client: several reliable messages in one packet: <c>[count:ushort]{[len:ushort][message]}</c>.
        /// A big mesh produces hundreds of small reliable messages a second (an RPC per shot, a vars update per
        /// hit); one packet each was most of a client's packet rate.
        /// </summary>
        Batch = 18,

        // Client -> gateway -> worker
        ClientInput = 20,
        ServerRpc = 21,

        // Gateway -> worker
        SpawnPlayer = 30,
        DespawnPlayer = 31,

        // Worker <-> worker (lateral link)
        GhostSpawn = 40,
        GhostState = 41,
        GhostVars = 42,
        GhostDespawn = 43,
        AuthorityTransfer = 44,
        ForwardInput = 45,
        AuthorityRpc = 46,
        GhostSyncState = 47,
        /// <summary>
        /// Game-defined message between two workers: <c>[kind:ushort][payload]</c>. Registered per kind with
        /// <see cref="NebulaWorker.RegisterMessageHandler"/>; sent with <see cref="NebulaWorker.SendToWorker"/>.
        /// </summary>
        WorkerMessage = 48,
    }

    public enum PeerRole : byte
    {
        Unknown = 0,
        Client = 1,
        Gateway = 2,
        Worker = 3,
    }

    [Flags]
    public enum HelloFlags : byte
    {
        None = 0,
        /// <summary>The client is a headless bot (-nebula-bot); workers report it separately from human players.</summary>
        Bot = 1,
    }

    [Flags]
    public enum EntityFlags : byte
    {
        None = 0,
        /// <summary>The owning client is a bot. Travels with the entity through ghosting and handover.</summary>
        OwnerIsBot = 1,
        /// <summary>No owning client: the authoritative worker drives the entity itself. Travels with the entity.</summary>
        /// <summary>No owning client; the authoritative worker drives the entity. Travels with it through ghosting and handover.</summary>
        ServerDriven = 2,
    }

    public struct HelloMsg
    {
        public const ushort ProtocolVersion = 6;
        public PeerRole Role;
        public string Id;
        public uint Index;
        public HelloFlags Flags;
        public ushort Version;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.Hello);
            w.WriteUShort(ProtocolVersion);
            w.WriteByte((byte)Role);
            w.WriteString(Id);
            w.WriteUInt(Index);
            w.WriteByte((byte)Flags);
        }

        public static HelloMsg Read(NetworkReader r)
        {
            var m = new HelloMsg();
            m.Version = r.ReadUShort();
            m.Role = (PeerRole)r.ReadByte();
            m.Id = r.ReadString();
            m.Index = r.ReadUInt();
            m.Flags = (HelloFlags)r.ReadByte();
            return m;
        }
    }

    public struct WelcomeMsg
    {
        public uint ClientId;
        public byte TickRate;
        public uint ServerTick;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.Welcome);
            w.WriteUInt(ClientId);
            w.WriteByte(TickRate);
            w.WriteUInt(ServerTick);
        }

        public static WelcomeMsg Read(NetworkReader r) => new WelcomeMsg { ClientId = r.ReadUInt(), TickRate = r.ReadByte(), ServerTick = r.ReadUInt() };
    }

    public struct PingMsg
    {
        public double ClientTime;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.Ping);
            w.WriteDouble(ClientTime);
        }

        public static PingMsg Read(NetworkReader r) => new PingMsg { ClientTime = r.ReadDouble() };
    }

    public struct PongMsg
    {
        public double ClientTime;
        public uint ServerTick;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.Pong);
            w.WriteDouble(ClientTime);
            w.WriteUInt(ServerTick);
        }

        public static PongMsg Read(NetworkReader r) => new PongMsg { ClientTime = r.ReadDouble(), ServerTick = r.ReadUInt() };
    }

    /// <summary>
    /// Full description of an entity. Sent as EntitySpawn (to gateway/clients), GhostSpawn (to a neighbour worker) and
    /// as the payload of AuthorityTransfer. Receivers treat a spawn for a known netId as an update (owner/epoch/container/vars),
    /// never as a duplicate instantiation.
    /// </summary>
    public struct EntitySpawnMsg
    {
        public ulong NetId;
        public ushort PrefabId;
        public uint OwnerClientId;
        public ushort ContainerIndex;
        public uint Epoch;
        public ushort OwnerWorkerIndex;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 Velocity;
        public EntityFlags Flags;
        /// <summary>Non-zero: bind the receiver's own copy of the scene object with this <see cref="NetworkIdentity.SceneId"/> instead of instantiating <see cref="PrefabId"/>.</summary>
        public uint SceneId;
        public byte[] Vars;
        /// <summary>Keyframe from every sync behaviour (<see cref="NetworkIdentity.WriteSyncSnapshot"/>); empty when the prefab has none.</summary>
        public byte[] State;

        public static EntitySpawnMsg From(NetworkIdentity id, NetworkWriter scratch)
        {
            scratch.Reset();
            id.WriteVars(scratch);
            var vars = scratch.ToArray();
            scratch.Reset();
            byte[] state = Array.Empty<byte>();
            if (id.HasSyncState)
            {
                id.WriteSyncSnapshot(scratch);
                state = scratch.ToArray();
            }
            return new EntitySpawnMsg
            {
                NetId = id.NetId,
                PrefabId = id.PrefabId,
                SceneId = id.SceneId,
                OwnerClientId = id.OwnerClientId,
                Flags = (id.OwnerIsBot ? EntityFlags.OwnerIsBot : EntityFlags.None) | (id.IsServerDriven ? EntityFlags.ServerDriven : EntityFlags.None),
                ContainerIndex = id.ContainerIndex,
                Epoch = id.Epoch,
                OwnerWorkerIndex = NebulaRuntime.LocalWorkerIndex,
                LocalPosition = id.LocalPosition,
                LocalRotation = id.LocalRotation,
                Velocity = id.Velocity,
                Vars = vars,
                State = state,
            };
        }

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            WriteBody(w);
        }

        public void WriteBody(NetworkWriter w)
        {
            w.WriteULong(NetId);
            w.WriteUShort(PrefabId);
            w.WriteUInt(OwnerClientId);
            w.WriteUShort(ContainerIndex);
            w.WriteUInt(Epoch);
            w.WriteUShort(OwnerWorkerIndex);
            w.WriteVector3(LocalPosition);
            w.WriteQuaternion(LocalRotation);
            w.WriteVector3(Velocity);
            w.WriteByte((byte)Flags);
            w.WriteUInt(SceneId);
            w.WriteBytes(Vars);
            w.WriteBytes(State);
        }

        public static EntitySpawnMsg Read(NetworkReader r)
        {
            return new EntitySpawnMsg
            {
                NetId = r.ReadULong(),
                PrefabId = r.ReadUShort(),
                OwnerClientId = r.ReadUInt(),
                ContainerIndex = r.ReadUShort(),
                Epoch = r.ReadUInt(),
                OwnerWorkerIndex = r.ReadUShort(),
                LocalPosition = r.ReadVector3(),
                LocalRotation = r.ReadQuaternion(),
                Velocity = r.ReadVector3(),
                Flags = (EntityFlags)r.ReadByte(),
                SceneId = r.ReadUInt(),
                Vars = r.ReadBytes(),
                State = r.ReadBytes(),
            };
        }
    }

    public struct EntityDespawnMsg
    {
        public ulong NetId;
        public uint Epoch;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
        }

        public static EntityDespawnMsg Read(NetworkReader r) => new EntityDespawnMsg { NetId = r.ReadULong(), Epoch = r.ReadUInt() };
    }

    public struct EntityVarsMsg
    {
        public ulong NetId;
        public uint Epoch;
        public byte[] Vars;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteBytes(Vars);
        }

        public static EntityVarsMsg Read(NetworkReader r) => new EntityVarsMsg { NetId = r.ReadULong(), Epoch = r.ReadUInt(), Vars = r.ReadBytes() };
    }

    /// <summary>
    /// One entity's sync chunks for one tick (see <see cref="SyncStateCodec"/>). Sent as EntityState (worker -> gateway
    /// -> clients) and GhostSyncState (worker -> worker). <see cref="Reliable"/> tells the gateway which channel to
    /// re-emit it on; the container index lets world-space chunks be resolved in the same frame as the pose entry
    /// for that tick even when the two packets arrive out of order.
    /// </summary>
    public struct EntitySyncMsg
    {
        public ulong NetId;
        public uint Epoch;
        public uint Tick;
        public ushort ContainerIndex;
        public bool Reliable;
        public byte[] Chunks;

        public Delivery Delivery => Reliable ? Delivery.ReliableOrdered : Delivery.Sequenced;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteUInt(Tick);
            w.WriteUShort(ContainerIndex);
            w.WriteBool(Reliable);
            w.WriteBytes(Chunks);
        }

        public static EntitySyncMsg Read(NetworkReader r) => new EntitySyncMsg
        {
            NetId = r.ReadULong(),
            Epoch = r.ReadUInt(),
            Tick = r.ReadUInt(),
            ContainerIndex = r.ReadUShort(),
            Reliable = r.ReadBool(),
            Chunks = r.ReadBytes(),
        };
    }

    public struct EntityRpcMsg
    {
        public ulong NetId;
        public uint Epoch;
        public byte BehaviourIndex;
        public uint MethodHash;
        /// <summary>For ClientRpc: 0 = every client, otherwise only that client. For ServerRpc: the sending client (filled by the gateway).</summary>
        public uint ClientId;
        public byte[] Args;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteByte(BehaviourIndex);
            w.WriteUInt(MethodHash);
            w.WriteUInt(ClientId);
            w.WriteBytes(Args);
        }

        public static EntityRpcMsg Read(NetworkReader r) => new EntityRpcMsg
        {
            NetId = r.ReadULong(),
            Epoch = r.ReadUInt(),
            BehaviourIndex = r.ReadByte(),
            MethodHash = r.ReadUInt(),
            ClientId = r.ReadUInt(),
            Args = r.ReadBytes(),
        };
    }

    /// <summary>One entity's pose inside a WorldState/GhostState batch.</summary>
    public struct EntityStateEntry
    {
        /// <summary>
        /// Serialized size of one entry, for sizing Sequenced batches: id, epoch, container, position (3 floats),
        /// rotation (smallest-three, 4 bytes) and velocity (3 halves). Rotation and velocity are the two fields that
        /// tolerate compression: a pawn's yaw to a hundredth of a degree and its speed to three decimals.
        /// </summary>
        public const int WireSize = 8 + 4 + 2 + 12 + 4 + 6;

        public ulong NetId;
        public uint Epoch;
        public ushort ContainerIndex;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 Velocity;

        public void Write(NetworkWriter w)
        {
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteUShort(ContainerIndex);
            w.WriteVector3(LocalPosition);
            w.WriteCompressedQuaternion(LocalRotation);
            w.WriteHalf(Velocity.x);
            w.WriteHalf(Velocity.y);
            w.WriteHalf(Velocity.z);
        }

        public static EntityStateEntry Read(NetworkReader r) => new EntityStateEntry
        {
            NetId = r.ReadULong(),
            Epoch = r.ReadUInt(),
            ContainerIndex = r.ReadUShort(),
            LocalPosition = r.ReadVector3(),
            LocalRotation = r.ReadCompressedQuaternion(),
            Velocity = new Vector3(r.ReadHalf(), r.ReadHalf(), r.ReadHalf()),
        };
    }

    /// <summary>Batched transforms for one tick from one worker. Unreliable/sequenced.</summary>
    public static class WorldStateMsg
    {
        public static int Begin(NetworkWriter w, MsgId id, uint tick, ushort workerIndex)
        {
            w.WriteByte((byte)id);
            w.WriteUInt(tick);
            w.WriteUShort(workerIndex);
            return w.ReserveUShort();
        }

        public static void End(NetworkWriter w, int countSlot, ushort count) => w.PatchUShort(countSlot, count);

        public static void ReadHeader(NetworkReader r, out uint tick, out ushort workerIndex, out ushort count)
        {
            tick = r.ReadUInt();
            workerIndex = r.ReadUShort();
            count = r.ReadUShort();
        }
    }

    /// <summary>
    /// Authoritative post-simulation state of a predicted entity, for its owner's reconciliation. Sent every tick,
    /// whether or not the owner's input for that tick had arrived, so the owner always has something to reconcile
    /// against and learns how early its inputs are landing (<see cref="InputLead"/>).
    /// </summary>
    public struct OwnerStateMsg
    {
        public ulong NetId;
        public uint Epoch;
        /// <summary>The tick this state is the result of.</summary>
        public uint Tick;
        /// <summary>Tick of the newest real (not repeated) input the worker has simulated.</summary>
        public uint LastInputTick;
        /// <summary>
        /// Worst (smallest) lead of the owner's inputs since the previous report: frame tick minus the worker's tick
        /// when the frame arrived. Inputs need a lead of at least 1 to be simulated; <see cref="NoInputLead"/> when
        /// nothing arrived.
        /// </summary>
        public sbyte InputLead;
        public uint OwnerClientId;
        public byte[] State;

        public const sbyte NoInputLead = sbyte.MinValue;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.OwnerState);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteUInt(Tick);
            w.WriteUInt(LastInputTick);
            w.WriteSByte(InputLead);
            w.WriteUInt(OwnerClientId);
            w.WriteBytes(State);
        }

        public static OwnerStateMsg Read(NetworkReader r) => new OwnerStateMsg
        {
            NetId = r.ReadULong(),
            Epoch = r.ReadUInt(),
            Tick = r.ReadUInt(),
            LastInputTick = r.ReadUInt(),
            InputLead = r.ReadSByte(),
            OwnerClientId = r.ReadUInt(),
            State = r.ReadBytes(),
        };
    }

    public struct ContainerOwnershipEntry
    {
        public ushort ContainerIndex;
        public string ContainerId;
        public ushort WorkerIndex;
        public string WorkerId;
        public ulong Epoch;
        public string State;
    }

    public static class ContainerOwnershipMsg
    {
        public static void Write(NetworkWriter w, IList<ContainerOwnershipEntry> entries)
        {
            w.WriteByte((byte)MsgId.ContainerOwnership);
            w.WriteUShort((ushort)entries.Count);
            foreach (var e in entries)
            {
                w.WriteUShort(e.ContainerIndex);
                w.WriteString(e.ContainerId);
                w.WriteUShort(e.WorkerIndex);
                w.WriteString(e.WorkerId);
                w.WriteULong(e.Epoch);
                w.WriteString(e.State);
            }
        }

        public static List<ContainerOwnershipEntry> Read(NetworkReader r)
        {
            int n = r.ReadUShort();
            var list = new List<ContainerOwnershipEntry>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new ContainerOwnershipEntry
                {
                    ContainerIndex = r.ReadUShort(),
                    ContainerId = r.ReadString(),
                    WorkerIndex = r.ReadUShort(),
                    WorkerId = r.ReadString(),
                    Epoch = r.ReadULong(),
                    State = r.ReadString(),
                });
            }
            return list;
        }
    }

    /// <summary>A short run of recent inputs (redundant against loss). The gateway stamps ClientId before forwarding.</summary>
    public struct ClientInputMsg
    {
        public struct Frame
        {
            public uint Tick;
            public byte[] Payload;
        }

        public uint ClientId;
        public List<Frame> Frames;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteUInt(ClientId);
            w.WriteByte((byte)Frames.Count);
            foreach (var f in Frames)
            {
                w.WriteUInt(f.Tick);
                w.WriteBytes(f.Payload);
            }
        }

        public static ClientInputMsg Read(NetworkReader r)
        {
            var m = new ClientInputMsg { ClientId = r.ReadUInt() };
            int n = r.ReadByte();
            m.Frames = new List<Frame>(n);
            for (int i = 0; i < n; i++)
            {
                m.Frames.Add(new Frame { Tick = r.ReadUInt(), Payload = r.ReadBytes() });
            }
            return m;
        }
    }

    public struct SpawnPlayerMsg
    {
        public uint ClientId;
        public ushort ContainerIndex;
        public string Name;
        public bool IsBot;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.SpawnPlayer);
            w.WriteUInt(ClientId);
            w.WriteUShort(ContainerIndex);
            w.WriteString(Name);
            w.WriteByte(IsBot ? (byte)1 : (byte)0);
        }

        public static SpawnPlayerMsg Read(NetworkReader r) => new SpawnPlayerMsg { ClientId = r.ReadUInt(), ContainerIndex = r.ReadUShort(), Name = r.ReadString(), IsBot = r.ReadByte() != 0 };
    }

    public struct DespawnPlayerMsg
    {
        public uint ClientId;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.DespawnPlayer);
            w.WriteUInt(ClientId);
        }

        public static DespawnPlayerMsg Read(NetworkReader r) => new DespawnPlayerMsg { ClientId = r.ReadUInt() };
    }

    /// <summary>
    /// The authority event. Carries the sender's exact final state so the receiver snaps to it and never computes a
    /// starting state of its own, plus the not-yet-simulated inputs so the input stream continues without a gap.
    /// </summary>
    public struct AuthorityTransferMsg
    {
        public EntitySpawnMsg Entity;
        public uint NewEpoch;
        public byte[] PendingInputs;
        /// <summary>Per-behaviour handover-only state (<see cref="NetworkIdentity.WriteHandoverState"/>).</summary>
        public byte[] HandoverState;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.AuthorityTransfer);
            Entity.WriteBody(w);
            w.WriteUInt(NewEpoch);
            w.WriteBytes(PendingInputs);
            w.WriteBytes(HandoverState);
        }

        public static AuthorityTransferMsg Read(NetworkReader r) => new AuthorityTransferMsg
        {
            Entity = EntitySpawnMsg.Read(r),
            NewEpoch = r.ReadUInt(),
            PendingInputs = r.ReadBytes(),
            HandoverState = r.ReadBytes(),
        };
    }
}
