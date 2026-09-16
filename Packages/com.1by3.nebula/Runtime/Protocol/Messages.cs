using System;
using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

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
        /// <summary>
        /// Gateway -> client: how the join is going (<see cref="JoinStatusMsg"/>). Sent once the client is welcomed
        /// and again whenever the answer changes, so a mesh that has scaled to zero can hold the client in a
        /// "world starting" state instead of leaving it welcomed with no pawn and no explanation.
        /// </summary>
        JoinStatus = 5,
        /// <summary>
        /// Gateway -> client: the join was refused (<see cref="JoinRejectedMsg"/>: the token in <c>Hello</c> did not
        /// verify, or the mesh requires one). The gateway disconnects right after sending it.
        /// </summary>
        JoinRejected = 6,
        /// <summary>
        /// Gateway -> client: this gateway is being taken out of service (<see cref="GatewayDrainingMsg"/>). The
        /// client should reconnect, presenting its session token, so another gateway takes over its session
        /// without the worker noticing more than a pause in input.
        /// </summary>
        GatewayDraining = 7,

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
        InstancePrepare = 19,

        // Client -> gateway -> worker
        ClientInput = 20,
        ServerRpc = 21,
        InstanceReady = 22,

        // Gateway -> worker
        SpawnPlayer = 30,
        DespawnPlayer = 31,

        // Worker <-> worker
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
        public const ushort ProtocolVersion = 13;
        public PeerRole Role;
        public string Id;
        public uint Index;
        public HelloFlags Flags;
        /// <summary>
        /// Client only: the token that says who the player is. An OpenID Connect ID token from a provider the mesh
        /// trusts (<see cref="NebulaConfig.AuthIssuers"/>), the token a gateway issued in an earlier
        /// <see cref="WelcomeMsg"/>, or empty to ask for a new anonymous identity. For a gateway or worker peer it
        /// is the infrastructure credential instead (<see cref="MeshPeerAuth"/>), proving the peer holds the mesh token.
        /// </summary>
        public string Token;
        /// <summary>
        /// Client only: the session token from the last <see cref="WelcomeMsg"/>, presented when reconnecting so
        /// the gateway (any gateway of the mesh) reclaims the same session and the worker keeps the pawn. Empty on a
        /// first connection.
        /// </summary>
        public string Session;
        /// <summary>
        /// Gateway and worker peers: a number that changes every time the process starts, so a peer that sees the
        /// same id twice can tell a restart from a reconnect. Clients send 0.
        /// </summary>
        public uint Incarnation;
        public ushort Version;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.Hello);
            w.WriteUShort(ProtocolVersion);
            w.WriteByte((byte)Role);
            w.WriteString(Id);
            w.WriteUInt(Index);
            w.WriteByte((byte)Flags);
            w.WriteString(Token ?? "");
            w.WriteString(Session ?? "");
            w.WriteUInt(Incarnation);
        }

        public static HelloMsg Read(NetworkReader r)
        {
            var m = new HelloMsg();
            m.Version = r.ReadUShort();
            m.Role = (PeerRole)r.ReadByte();
            m.Id = r.ReadString();
            m.Index = r.ReadUInt();
            m.Flags = (HelloFlags)r.ReadByte();
            m.Token = r.ReadString() ?? "";
            m.Session = r.ReadString() ?? "";
            m.Incarnation = r.ReadUInt();
            return m;
        }
    }

    public struct WelcomeMsg
    {
        /// <summary>
        /// The client's session id: unique across every gateway of the mesh and stable for as long as the session
        /// lives, including across a reconnection through another gateway (see <see cref="SessionToken"/>).
        /// </summary>
        public ulong ClientId;
        public byte TickRate;
        public uint ServerTick;
        /// <summary>The player's identity for this and every later session (<see cref="PlayerIdentity"/>).</summary>
        public string Identity;
        /// <summary>
        /// Set when the client presented no token: the anonymous token the gateway issued for <see cref="Identity"/>.
        /// The client keeps it and presents it in its next <see cref="HelloMsg"/> to be the same player again.
        /// Empty when the client's own token was accepted.
        /// </summary>
        public string Token;
        /// <summary>
        /// A signed token naming this session (<see cref="SessionTokens"/>). The client keeps it for the life of the
        /// process and presents it in <see cref="HelloMsg.Session"/> when it reconnects, to any gateway of the
        /// mesh, to get the same <see cref="ClientId"/> and pawn back.
        /// </summary>
        public string SessionToken;
        /// <summary>True when the session was reclaimed from a token: the pawn, when the worker still holds it, is the same one.</summary>
        public bool Reclaimed;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.Welcome);
            w.WriteULong(ClientId);
            w.WriteByte(TickRate);
            w.WriteUInt(ServerTick);
            w.WriteString(Identity ?? "");
            w.WriteString(Token ?? "");
            w.WriteString(SessionToken ?? "");
            w.WriteByte(Reclaimed ? (byte)1 : (byte)0);
        }

        public static WelcomeMsg Read(NetworkReader r) => new WelcomeMsg
        {
            ClientId = r.ReadULong(), TickRate = r.ReadByte(), ServerTick = r.ReadUInt(), Identity = r.ReadString() ?? "", Token = r.ReadString() ?? "",
            SessionToken = r.ReadString() ?? "", Reclaimed = r.ReadByte() != 0,
        };
    }

    /// <summary>
    /// Gateway -> client: the gateway is draining and will close this link. The client reconnects (through the
    /// address it connected to, which a load balancer maps to another gateway) within <see cref="ReconnectWithinSeconds"/>,
    /// presenting its session token so the session and pawn carry over.
    /// </summary>
    public struct GatewayDrainingMsg
    {
        public ushort ReconnectWithinSeconds;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.GatewayDraining);
            w.WriteUShort(ReconnectWithinSeconds);
        }

        public static GatewayDrainingMsg Read(NetworkReader r) => new GatewayDrainingMsg { ReconnectWithinSeconds = r.ReadUShort() };
    }

    /// <summary>Gateway -> client: the join was refused; <see cref="Reason"/> is fit to show the player. The gateway disconnects after sending it.</summary>
    public struct JoinRejectedMsg
    {
        public string Reason;
        /// <summary>
        /// The refusal is about this gateway, not the client (it is draining or not ready): the client should try
        /// again shortly, and a load balancer will hand it to another gateway. False means the client's credentials
        /// were refused and retrying with the same ones is pointless.
        /// </summary>
        public bool Retry;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.JoinRejected);
            w.WriteString(Reason ?? "");
            w.WriteByte(Retry ? (byte)1 : (byte)0);
        }

        public static JoinRejectedMsg Read(NetworkReader r) => new JoinRejectedMsg { Reason = r.ReadString() ?? "", Retry = r.ReadByte() != 0 };
    }

    /// <summary>How far a client's join has got. Reported by the gateway in <see cref="JoinStatusMsg"/>.</summary>
    public enum JoinState : byte
    {
        /// <summary>Not connected, or not welcomed yet.</summary>
        None = 0,
        /// <summary>
        /// Welcomed, but there is nowhere to spawn yet: the mesh has no worker holding an active lease. With
        /// <see cref="NebulaConfig.MinWorkers"/> at 0 this is the normal first join after an idle period - a worker
        /// is booting and the gateway places the player as soon as it registers, with no reconnect.
        /// </summary>
        Starting = 1,
        /// <summary>The player has a pawn on a worker.</summary>
        Joined = 2,
    }

    /// <summary>Gateway -> client: the join's state and, while <see cref="JoinState.Starting"/>, a rough wait in seconds (0 = unknown).</summary>
    public struct JoinStatusMsg
    {
        public JoinState State;
        /// <summary>Roughly how long the client should expect to wait, in seconds; 0 when nobody can say.</summary>
        public ushort EstimatedSeconds;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.JoinStatus);
            w.WriteByte((byte)State);
            w.WriteUShort(EstimatedSeconds);
        }

        public static JoinStatusMsg Read(NetworkReader r) => new JoinStatusMsg { State = (JoinState)r.ReadByte(), EstimatedSeconds = r.ReadUShort() };
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
        public ulong OwnerClientId;
        /// <summary>The container the pose is expressed in (static index, or the carrier's net id for a dynamic container).</summary>
        public ContainerRef Container;
        public uint Epoch;
        public ushort OwnerWorkerIndex;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 Velocity;
        public EntityFlags Flags;
        public Vector3 LocalScale;
        /// <summary>Non-zero: bind the receiver's own copy of the scene object with this <see cref="NetworkIdentity.SceneId"/> instead of instantiating <see cref="PrefabId"/>.</summary>
        public uint SceneId;
        public byte[] Vars;
        /// <summary>Keyframe from every sync behaviour (<see cref="NetworkIdentity.WriteSyncSnapshot"/>); empty when the prefab has none.</summary>
        public byte[] State;
        /// <summary>The owning player's <see cref="PlayerIdentity"/>; empty for entities no player owns. Travels with the entity through ghosting and handover.</summary>
        public string OwnerIdentity;

#if !NEBULA_SERVICE
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
                OwnerIdentity = id.OwnerIdentity,
                Flags = (id.OwnerIsBot ? EntityFlags.OwnerIsBot : EntityFlags.None) | (id.IsServerDriven ? EntityFlags.ServerDriven : EntityFlags.None),
                Container = id.ContainerRef,
                Epoch = id.Epoch,
                OwnerWorkerIndex = NebulaRuntime.LocalWorkerIndex,
                LocalPosition = id.LocalPosition,
                LocalRotation = id.LocalRotation,
                LocalScale = id.transform.localScale,
                Velocity = id.Motion.Velocity,
                Vars = vars,
                State = state,
            };
        }

#endif
        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            WriteBody(w);
        }

        public void WriteBody(NetworkWriter w)
        {
            w.WriteULong(NetId);
            w.WriteUShort(PrefabId);
            w.WriteULong(OwnerClientId);
            Container.Write(w);
            w.WriteUInt(Epoch);
            w.WriteUShort(OwnerWorkerIndex);
            w.WriteVector3(LocalPosition);
            w.WriteQuaternion(LocalRotation);
            w.WriteVector3(LocalScale);
            w.WriteVector3(Velocity);
            w.WriteByte((byte)Flags);
            w.WriteUInt(SceneId);
            w.WriteBytes(Vars);
            w.WriteBytes(State);
            w.WriteString(OwnerIdentity ?? "");
        }

        public static EntitySpawnMsg Read(NetworkReader r)
        {
            return new EntitySpawnMsg
            {
                NetId = r.ReadULong(),
                PrefabId = r.ReadUShort(),
                OwnerClientId = r.ReadULong(),
                Container = ContainerRef.Read(r),
                Epoch = r.ReadUInt(),
                OwnerWorkerIndex = r.ReadUShort(),
                LocalPosition = r.ReadVector3(),
                LocalRotation = r.ReadQuaternion(),
                LocalScale = r.ReadVector3(),
                Velocity = r.ReadVector3(),
                Flags = (EntityFlags)r.ReadByte(),
                SceneId = r.ReadUInt(),
                Vars = r.ReadBytes(),
                State = r.ReadBytes(),
                OwnerIdentity = r.ReadString() ?? "",
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
    /// re-emit it on; the container reference lets world-space chunks be resolved in the same frame as the pose entry
    /// for that tick even when the two packets arrive out of order.
    /// </summary>
    public struct EntitySyncMsg
    {
        public ulong NetId;
        public uint Epoch;
        public uint Tick;
        public ContainerRef Container;
        public bool Reliable;
        public byte[] Chunks;

        public Delivery Delivery => Reliable ? Delivery.ReliableOrdered : Delivery.Sequenced;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteUInt(Tick);
            Container.Write(w);
            w.WriteBool(Reliable);
            w.WriteBytes(Chunks);
        }

        public static EntitySyncMsg Read(NetworkReader r) => new EntitySyncMsg
        {
            NetId = r.ReadULong(),
            Epoch = r.ReadUInt(),
            Tick = r.ReadUInt(),
            Container = ContainerRef.Read(r),
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
        public ulong ClientId;
        /// <summary>For a ClientRpc broadcast: deliver only to clients whose pawn is within this many metres of the entity (0 = everyone). See ClientRpcAttribute.Radius.</summary>
        public float Radius;
        public byte[] Args;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteByte(BehaviourIndex);
            w.WriteUInt(MethodHash);
            w.WriteULong(ClientId);
            w.WriteFloat(Radius);
            w.WriteBytes(Args);
        }

        public static EntityRpcMsg Read(NetworkReader r) => new EntityRpcMsg
        {
            NetId = r.ReadULong(),
            Epoch = r.ReadUInt(),
            BehaviourIndex = r.ReadByte(),
            MethodHash = r.ReadUInt(),
            ClientId = r.ReadULong(),
            Radius = r.ReadFloat(),
            Args = r.ReadBytes(),
        };
    }

    /// <summary>Selected axes and encoding of a root transform update.</summary>
    [Flags]
    public enum TransformFields : ushort
    {
        None = 0, PositionX = 1, PositionY = 2, PositionZ = 4,
        RotationX = 8, RotationY = 16, RotationZ = 32,
        ScaleX = 64, ScaleY = 128, ScaleZ = 256,
        Velocity = 512, Teleport = 1024, Half = 2048, Quaternion = 4096,
        Compressed = 8192, Reliable = 16384, Location = 32768,
        Position = PositionX | PositionY | PositionZ,
        Rotation = RotationX | RotationY | RotationZ,
        Scale = ScaleX | ScaleY | ScaleZ,
        Axes = Position | Rotation | Scale,
    }

    /// <summary>One entity's selected transform fields inside a WorldState/GhostState batch.</summary>
    public struct EntityStateEntry
    {
        /// <summary>
        /// Largest serialized size of one entry, for sizing Sequenced batches: id, epoch, container reference (two
        /// bytes for a static container, ten for a dynamic one), field mask, position, rotation, scale, and velocity.
        /// Only selected axes are serialized. Position, rotation, and scale precision follows NetworkTransform;
        /// velocity uses three half floats when enabled.
        /// </summary>
        public const int WireSize = 8 + 4 + ContainerRef.MaxWireSize + 2 + 12 + 16 + 12 + 6;

        public ulong NetId;
        public uint Epoch;
        public ContainerRef Container;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 Velocity;
        public Vector3 LocalScale;
        public TransformFields Fields;
        public bool Reliable => (Fields & TransformFields.Reliable) != 0;

#if !NEBULA_SERVICE
        public static EntityStateEntry Snapshot(NetworkIdentity e) => new EntityStateEntry
        {
            NetId = e.NetId, Epoch = e.Epoch, Container = e.ContainerRef,
            LocalPosition = e.LocalPosition, LocalRotation = e.LocalRotation,
            LocalScale = e.transform.localScale, Velocity = e.Motion.Velocity,
            Fields = TransformFields.Axes | TransformFields.Quaternion | TransformFields.Compressed | TransformFields.Velocity,
        };

#endif
        private void WriteVector(NetworkWriter w, Vector3 v, int shift)
        {
            for (int i = 0; i < 3; i++)
                if (((int)Fields & (1 << (shift + i))) != 0)
                {
                    if ((Fields & TransformFields.Half) != 0) w.WriteHalf(v[i]); else w.WriteFloat(v[i]);
                }
        }

        private Vector3 ReadVector(NetworkReader r, int shift)
        {
            var v = Vector3.zero;
            for (int i = 0; i < 3; i++)
                if (((int)Fields & (1 << (shift + i))) != 0)
                    v[i] = (Fields & TransformFields.Half) != 0 ? r.ReadHalf() : r.ReadFloat();
            return v;
        }

        internal static Vector3 MergeVector(Vector3 previous, Vector3 next, TransformFields fields, int shift)
        {
            for (int i = 0; i < 3; i++) if (((int)fields & (1 << (shift + i))) != 0) previous[i] = next[i];
            return previous;
        }

        internal void Merge(ref Vector3 position, ref Quaternion rotation, ref Vector3 scale, ref Vector3 velocity)
        {
            position = MergeVector(position, LocalPosition, Fields, 0);
            if ((Fields & TransformFields.Rotation) != 0)
                rotation = (Fields & TransformFields.Quaternion) != 0 ? LocalRotation :
                    Quaternion.Euler(MergeVector(rotation.eulerAngles, LocalRotation.eulerAngles, Fields, 3));
            scale = MergeVector(scale, LocalScale, Fields, 6);
            velocity = (Fields & TransformFields.Velocity) != 0 ? Velocity : Vector3.zero;
        }

        public void Write(NetworkWriter w)
        {
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            Container.Write(w);
            w.WriteUShort((ushort)Fields);
            WriteVector(w, LocalPosition, 0);
            if ((Fields & TransformFields.Rotation) != 0)
            {
                if ((Fields & TransformFields.Quaternion) == 0) WriteVector(w, LocalRotation.eulerAngles, 3);
                else if ((Fields & TransformFields.Compressed) != 0) w.WriteCompressedQuaternion(LocalRotation);
                else if ((Fields & TransformFields.Half) != 0)
                { w.WriteHalf(LocalRotation.x); w.WriteHalf(LocalRotation.y); w.WriteHalf(LocalRotation.z); w.WriteHalf(LocalRotation.w); }
                else w.WriteQuaternion(LocalRotation);
            }
            WriteVector(w, LocalScale, 6);
            if ((Fields & TransformFields.Velocity) != 0)
            { w.WriteHalf(Velocity.x); w.WriteHalf(Velocity.y); w.WriteHalf(Velocity.z); }
        }

        public static EntityStateEntry Read(NetworkReader r)
        {
            var e = new EntityStateEntry { NetId = r.ReadULong(), Epoch = r.ReadUInt(), Container = ContainerRef.Read(r) };
            e.Fields = (TransformFields)r.ReadUShort();
            e.LocalPosition = e.ReadVector(r, 0);
            e.LocalRotation = Quaternion.identity;
            if ((e.Fields & TransformFields.Rotation) != 0)
            {
                if ((e.Fields & TransformFields.Quaternion) == 0) e.LocalRotation = Quaternion.Euler(e.ReadVector(r, 3));
                else if ((e.Fields & TransformFields.Compressed) != 0) e.LocalRotation = r.ReadCompressedQuaternion();
                else if ((e.Fields & TransformFields.Half) != 0) e.LocalRotation = new Quaternion(r.ReadHalf(), r.ReadHalf(), r.ReadHalf(), r.ReadHalf()).normalized;
                else e.LocalRotation = r.ReadQuaternion();
            }
            e.LocalScale = e.ReadVector(r, 6);
            if ((e.Fields & TransformFields.Velocity) != 0) e.Velocity = new Vector3(r.ReadHalf(), r.ReadHalf(), r.ReadHalf());
            return e;
        }
    }

    /// <summary>Batched transforms for one tick from one worker. Unreliable/sequenced.</summary>
    public static class WorldStateMsg
    {
        public const int BatchBytes = 500;

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
        public ulong OwnerClientId;
        /// <summary>The container the state's pose is expressed in, so the owner reconciles in the right frame on the tick it crosses a seam.</summary>
        public ContainerRef Container;
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
            w.WriteULong(OwnerClientId);
            Container.Write(w);
            w.WriteBytes(State);
        }

        public static OwnerStateMsg Read(NetworkReader r) => new OwnerStateMsg
        {
            NetId = r.ReadULong(),
            Epoch = r.ReadUInt(),
            Tick = r.ReadUInt(),
            LastInputTick = r.ReadUInt(),
            InputLead = r.ReadSByte(),
            OwnerClientId = r.ReadULong(),
            Container = ContainerRef.Read(r),
            State = r.ReadBytes(),
        };
    }

    public struct ContainerOwnershipEntry
    {
        public InstanceContainerInfo Instance;
        public ushort ContainerIndex;
        public string ContainerId;
        public ushort WorkerIndex;
        public string WorkerId;
        public ulong Epoch;
        public string State;
        /// <summary>Runtime containers: the box (absolute coordinates) a client registers the container with. See <see cref="LeaseInfo.HasBounds"/>.</summary>
        public bool HasBounds;
        public Vector3 BoundsCenter;
        public Vector3 BoundsSize;
    }

    public static class ContainerOwnershipMsg
    {
        private const byte FlagBounds = 1;

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
                w.WriteBool(e.Instance != null);
                e.Instance?.Write(w);
                w.WriteByte(e.HasBounds ? FlagBounds : (byte)0);
                if (e.HasBounds)
                {
                    w.WriteVector3(e.BoundsCenter);
                    w.WriteVector3(e.BoundsSize);
                }
            }
        }

        public static List<ContainerOwnershipEntry> Read(NetworkReader r)
        {
            int n = r.ReadUShort();
            var list = new List<ContainerOwnershipEntry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new ContainerOwnershipEntry
                {
                    ContainerIndex = r.ReadUShort(),
                    ContainerId = r.ReadString(),
                    WorkerIndex = r.ReadUShort(),
                    WorkerId = r.ReadString(),
                    Epoch = r.ReadULong(),
                    State = r.ReadString(),
                };
                e.Instance = r.ReadBool() ? InstanceContainerInfo.Read(r) : null;
                byte flags = r.ReadByte();
                if ((flags & FlagBounds) != 0)
                {
                    e.HasBounds = true;
                    e.BoundsCenter = r.ReadVector3();
                    e.BoundsSize = r.ReadVector3();
                }
                list.Add(e);
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

        public ulong ClientId;
        public List<Frame> Frames;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteULong(ClientId);
            w.WriteByte((byte)Frames.Count);
            foreach (var f in Frames)
            {
                w.WriteUInt(f.Tick);
                w.WriteBytes(f.Payload);
            }
        }

        public static ClientInputMsg Read(NetworkReader r)
        {
            var m = new ClientInputMsg { ClientId = r.ReadULong() };
            int n = r.ReadByte();
            m.Frames = new List<Frame>(n);
            for (int i = 0; i < n; i++)
            {
                m.Frames.Add(new Frame { Tick = r.ReadUInt(), Payload = r.ReadBytes() });
            }
            return m;
        }
    }

    /// <summary>
    /// Gateway -> worker: a client wants a pawn. Doubles as the claim of a session by a gateway: a worker that already
    /// holds a pawn for <see cref="ClientId"/> re-announces it to the sender and, when <see cref="Generation"/> is
    /// newer than the one it knew, routes the session through the sender from now on (<see cref="PlayerSessions"/>).
    /// </summary>
    public struct SpawnPlayerMsg
    {
        /// <summary>The mesh-wide session id (<see cref="WelcomeMsg.ClientId"/>).</summary>
        public ulong ClientId;
        /// <summary>The container the gateway picked for the pawn (baked or runtime).</summary>
        public ContainerRef Container;
        public string Name;
        public bool IsBot;
        /// <summary>The player's <see cref="PlayerIdentity"/>, as the gateway established it from the client's token.</summary>
        public string Identity;
        /// <summary>
        /// The session's connection generation: increases every time a client (re)claims the session. A worker
        /// ignores a claim or a despawn carrying an older generation than the one it holds, which fences a gateway
        /// that lost the client from undoing what the gateway that gained it did.
        /// </summary>
        public ulong Generation;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.SpawnPlayer);
            w.WriteULong(ClientId);
            Container.Write(w);
            w.WriteString(Name);
            w.WriteByte(IsBot ? (byte)1 : (byte)0);
            w.WriteString(Identity ?? "");
            w.WriteULong(Generation);
        }

        public static SpawnPlayerMsg Read(NetworkReader r) => new SpawnPlayerMsg { ClientId = r.ReadULong(), Container = ContainerRef.Read(r), Name = r.ReadString(), IsBot = r.ReadByte() != 0, Identity = r.ReadString() ?? "", Generation = r.ReadULong() };
    }

    /// <summary>Gateway -> worker: the session is over (or the gateway gave up waiting for it to reconnect). Fenced by <see cref="Generation"/> like <see cref="SpawnPlayerMsg"/>.</summary>
    public struct DespawnPlayerMsg
    {
        public ulong ClientId;
        public ulong Generation;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.DespawnPlayer);
            w.WriteULong(ClientId);
            w.WriteULong(Generation);
        }

        public static DespawnPlayerMsg Read(NetworkReader r) => new DespawnPlayerMsg { ClientId = r.ReadULong(), Generation = r.ReadULong() };
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
        public string[] GhostWorkers;
        /// <summary>The owner's session as the sender knew it (<see cref="PlayerSessions"/>), so the receiver accepts the owner's gateway at once. 0/"" for an unowned entity.</summary>
        public ulong SessionGeneration;
        public string SessionGateway;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.AuthorityTransfer);
            Entity.WriteBody(w);
            w.WriteUInt(NewEpoch);
            w.WriteBytes(PendingInputs);
            w.WriteBytes(HandoverState);
            w.WriteUShort((ushort)(GhostWorkers?.Length ?? 0));
            if (GhostWorkers != null) foreach (var worker in GhostWorkers) w.WriteString(worker);
            w.WriteULong(SessionGeneration);
            w.WriteString(SessionGateway ?? "");
        }

        public static AuthorityTransferMsg Read(NetworkReader r) => new AuthorityTransferMsg
        {
            Entity = EntitySpawnMsg.Read(r),
            NewEpoch = r.ReadUInt(),
            PendingInputs = r.ReadBytes(),
            HandoverState = r.ReadBytes(),
            GhostWorkers = ReadGhostWorkers(r),
            SessionGeneration = r.ReadULong(),
            SessionGateway = r.ReadString() ?? "",
        };

        private static string[] ReadGhostWorkers(NetworkReader r)
        {
            var workers = new string[r.ReadUShort()];
            for (int i = 0; i < workers.Length; i++) workers[i] = r.ReadString();
            return workers;
        }
    }
}
