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
        /// <summary>
        /// Gateway -> client: a newer connection for this player took the session over
        /// (<see cref="SessionReplacedMsg"/>). The gateway disconnects right after sending it.
        /// </summary>
        SessionReplaced = 8,

        // Entity replication (worker -> gateway -> clients)
        EntitySpawn = 10,
        EntityDespawn = 11,
        EntityVars = 12,
        EntityRpc = 13,
        WorldState = 14,
        OwnerState = 15,
        ContainerOwnership = 16,
        /// <summary>Per-behavior sync chunks (NetworkTransform/NetworkAnimator) for one entity and tick.</summary>
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

        /// <summary>
        /// Client -> gateway: where the player is looking (<see cref="ClientFocusHintMsg"/>). A hint, never
        /// authority: the gateway drops a non-finite one, rate-limits it and clamps it to the pawn before a policy
        /// ever sees it (<see cref="FocusHintFilter"/>).
        /// </summary>
        ClientFocusHint = 23,

        // Gateway -> worker
        SpawnPlayer = 30,
        DespawnPlayer = 31,

        // Worker -> gateway
        /// <summary>The session moved to another gateway, so this one must let its client go (<see cref="EndSessionMsg"/>).</summary>
        EndSession = 32,

        /// <summary>Gateway -> worker: the set of regions this gateway wants entities from (<see cref="InterestSubscribeMsg"/>).</summary>
        InterestSubscribe = 33,
        /// <summary>Worker -> gateway: the last subscription could not be applied; send a Full snapshot (<see cref="InterestResyncMsg"/>).</summary>
        InterestResync = 34,
        /// <summary>Worker -> gateway: an explicitly subscribed entity now lives on another worker (<see cref="EntityRedirectMsg"/>).</summary>
        EntityRedirect = 35,
        /// <summary>Worker -> gateway: an entity left every region this gateway subscribes here (<see cref="EntityForgetMsg"/>).</summary>
        EntityForget = 36,
        /// <summary>
        /// Worker -> gateway: the client member sets of an entity's <see cref="Nebula.SyncAudience.Custom"/> behaviors
        /// changed (<see cref="SyncAudienceMsg"/>, protocol 20). Never forwarded to a client.
        /// </summary>
        SyncAudience = 37,
        /// <summary>
        /// Worker -> gateway -> client: the entries of an entity's <see cref="NetworkMap{TKey, TValue}"/>s that changed
        /// (<see cref="EntityMapsMsg"/>, protocol 21, docs/replicated-collections.md D4). The gateway applies it to its
        /// copy before relaying it, and sends it only to clients that negotiated 21 or later.
        /// </summary>
        EntityMaps = 38,

        // Worker <-> worker
        GhostSpawn = 40,
        GhostState = 41,
        GhostVars = 42,
        GhostDespawn = 43,
        AuthorityTransfer = 44,
        ForwardInput = 45,
        /// <summary>
        /// Worker -> worker: an <c>AuthorityRpc</c> for an entity the sender holds only a ghost of
        /// (<see cref="AuthorityCallMsg"/>). Carries a call id, a hop count and the epoch the sender observed, so
        /// the receiver can apply it once, forward it after a handover, or reject it with a reason.
        /// </summary>
        AuthorityRpc = 46,
        GhostSyncState = 47,
        /// <summary>
        /// Game-defined message between two workers: <c>[kind:ushort][payload]</c>. Registered per kind with
        /// <see cref="NebulaWorker.RegisterMessageHandler"/>; sent with <see cref="NebulaWorker.SendToWorker"/>.
        /// </summary>
        WorkerMessage = 48,
        /// <summary>
        /// Worker -> worker: the outcome of an <see cref="AuthorityRpc"/> that asked for one
        /// (<see cref="AuthorityCallReplyMsg"/>), sent to the worker that minted the call id.
        /// </summary>
        AuthorityRpcReply = 49,
        /// <summary>Worker -> worker: <see cref="EntityMaps"/> for a ghost (<see cref="EntityMapsMsg"/>).</summary>
        GhostMaps = 50,
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

    /// <summary>Per-prefab interest hints that travel with a spawn (the standalone gateway has no prefabs to read them from).</summary>
    [Flags]
    public enum EntityInterestFlags : byte
    {
        None = 0,
        /// <summary>The entity is in every client's set, whatever the distance (<see cref="NetworkIdentity.AlwaysRelevant"/>).</summary>
        AlwaysRelevant = 1,
    }

    public struct HelloMsg
    {
        /// <summary>The wire protocol version used by this build.</summary>
        public const ushort ProtocolVersion = 21;
        /// <summary>
        /// The oldest client protocol the gateway accepts: 20, the window being one version wide. Protocol 21 added
        /// replicated maps (<see cref="NetworkMap{TKey, TValue}"/>), additive for a client: a new message a gateway
        /// sends only to clients that negotiated 21, and a trailing spawn field a protocol-20 client does not read.
        /// Protocols 18 and 19 are refused. Gateway-to-worker and worker-to-worker connections require
        /// <see cref="ProtocolVersion"/> exactly.
        /// </summary>
        public const ushort MinProtocolVersion = 20;
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
        /// <summary>
        /// Client only: the simulation scope to be placed in, as an opaque key (<see cref="EntityLocation.ScopeKey"/>).
        /// Empty selects the public world. The gateway spawns
        /// the player only into containers of that scope, and holds the join while the scope is not ready
        /// (<c>docs/scope-activation.md</c> §5). It does not activate the scope: whoever sent the player to the key
        /// is the one that called <see cref="IControlPlane.ActivateScope"/>.
        /// </summary>
        public string ScopeKey;
        /// <summary>
        /// The protocol version the peer announces. <see cref="Write"/> substitutes
        /// <see cref="ProtocolVersion"/> when this value is zero.
        /// </summary>
        public ushort Version;
        /// <summary>
        /// The client's game content version, set by <see cref="NebulaConfig.GameContentVersion"/>.
        /// The gateway checks it against its configured range unless the gateway's content version is zero.
        /// An incompatible value is refused with <see cref="JoinRejectReason.ContentVersionMismatch"/>.
        /// </summary>
        public uint GameContentVersion;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.Hello);
            // A peer announces its own version when it has one. Until this field was written, every build sent
            // the constant, which is why a gateway could never admit anything but its exact own protocol.
            w.WriteUShort(Version != 0 ? Version : ProtocolVersion);
            w.WriteByte((byte)Role);
            w.WriteString(Id);
            w.WriteUInt(Index);
            w.WriteByte((byte)Flags);
            w.WriteString(Token ?? "");
            w.WriteString(Session ?? "");
            w.WriteUInt(Incarnation);
            w.WriteString(ScopeKey ?? "");
            w.WriteUInt(GameContentVersion);
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
            // Appended after the rest of v18 was settled: a Hello that ends here is the public world.
            m.ScopeKey = r.Remaining > 0 ? r.ReadString() ?? "" : "";
            // Appended for the compatibility policy: a Hello that ends here is a client that does not version
            // its game content, which a gateway that does not either accepts (docs/compatibility-policy.md).
            m.GameContentVersion = r.Remaining > 0 ? r.ReadUInt() : 0;
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
        /// <summary>
        /// The accepted protocol version from the client's <see cref="HelloMsg"/>. The gateway accepts
        /// protocol 18 only. Zero means the field was omitted; the client uses its announced version.
        /// </summary>
        public ushort NegotiatedVersion;

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
            w.WriteUShort(NegotiatedVersion);
        }

        public static WelcomeMsg Read(NetworkReader r)
        {
            var m = new WelcomeMsg
            {
                ClientId = r.ReadULong(), TickRate = r.ReadByte(), ServerTick = r.ReadUInt(), Identity = r.ReadString() ?? "", Token = r.ReadString() ?? "",
                SessionToken = r.ReadString() ?? "", Reclaimed = r.ReadByte() != 0,
            };
            // Appended for the compatibility policy: a welcome that ends here came from a gateway that only ever
            // spoke one protocol, so the negotiated version is the one the client sent.
            m.NegotiatedVersion = r.Remaining > 0 ? r.ReadUShort() : (ushort)0;
            return m;
        }
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
        /// Whether the client should retry automatically, such as when this gateway is draining.
        /// Reaching another gateway requires a configured load balancer or another routing mechanism.
        /// When false, inspect <see cref="Code"/> and <see cref="Reason"/> before reconnecting.
        /// </summary>
        public bool Retry;
        /// <summary>
        /// The typed refusal reason. Defaults to <see cref="JoinRejectReason.None"/> when omitted.
        /// </summary>
        public JoinRejectReason Code;
        /// <summary>
        /// How saturated the target was, for <see cref="JoinRejectReason.AtCapacity"/>: 1 = the whole of the
        /// dominant component's budget (<see cref="CapacityInfo.Saturation"/>). Sent as an f16; 0 when unknown.
        /// </summary>
        public float Saturation;
        /// <summary>
        /// The gateway's inclusive supported protocol range, supplied on every refusal.
        /// Both limits are 18. Zero means the corresponding field was omitted.
        /// </summary>
        public ushort SupportedMinVersion;
        /// <summary>
        /// The gateway's inclusive supported protocol range, supplied on every refusal.
        /// Both limits are 18. Zero means the corresponding field was omitted.
        /// </summary>
        public ushort SupportedMaxVersion;
        /// <summary>
        /// The gateway's configured game content version, supplied on every refusal.
        /// Zero means content version checks are disabled or the field was omitted.
        /// </summary>
        public uint ServerContentVersion;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.JoinRejected);
            w.WriteString(Reason ?? "");
            w.WriteByte(Retry ? (byte)1 : (byte)0);
            w.WriteByte((byte)Code);
            w.WriteHalf(Saturation);
            w.WriteUShort(SupportedMinVersion);
            w.WriteUShort(SupportedMaxVersion);
            w.WriteUInt(ServerContentVersion);
        }

        public static JoinRejectedMsg Read(NetworkReader r)
        {
            var m = new JoinRejectedMsg { Reason = r.ReadString() ?? "", Retry = r.ReadByte() != 0 };
            // A message that ends here came from a gateway that only ever refused a join for reasons the string
            // already carried; there is nothing typed to read and nothing is assumed.
            if (r.Remaining > 0) m.Code = (JoinRejectReason)r.ReadByte();
            if (r.Remaining > 0) m.Saturation = r.ReadHalf();
            // Appended for the compatibility policy (docs/compatibility-policy.md).
            if (r.Remaining > 0) m.SupportedMinVersion = r.ReadUShort();
            if (r.Remaining > 0) m.SupportedMaxVersion = r.ReadUShort();
            if (r.Remaining > 0) m.ServerContentVersion = r.ReadUInt();
            return m;
        }
    }

    /// <summary>
    /// Gateway -> client: the player this client signed in as connected again elsewhere, and that newer connection
    /// now holds the session and the pawn (<see cref="NebulaConfig.SingleSessionPerPlayer"/>). The gateway closes the
    /// link right after sending it. The client keeps its identity but must not reconnect by itself, or the two
    /// connections would take the player from each other in turn; <see cref="Reason"/> is fit to show the player.
    /// </summary>
    public struct SessionReplacedMsg
    {
        public string Reason;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.SessionReplaced);
            w.WriteString(Reason ?? "");
        }

        public static SessionReplacedMsg Read(NetworkReader r) => new SessionReplacedMsg { Reason = r.ReadString() ?? "" };
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

    /// <summary>
    /// Why a join is being held in <see cref="JoinState.Starting"/>. A reason, not an error: the gateway holds the
    /// client and places it as soon as the reason goes away, with no reconnect. See <c>docs/scope-lifecycle.md</c>.
    /// </summary>
    public enum JoinHoldReason : byte
    {
        /// <summary>The join is not held.</summary>
        None = 0,
        /// <summary>No worker holds an active lease yet: the mesh is booting one.</summary>
        WorldStarting = 1,
        /// <summary>The scope the client named has not been activated, or has no container with a live owner yet. Whoever sent the player to the key is the one that activates it.</summary>
        ScopeNotReady = 2,
        /// <summary>The scope is coming back from a retire and its persisted entities are still being restored.</summary>
        ScopeRestoring = 3,
        /// <summary>The scope is retiring: it is checkpointing and emptying itself and admits nobody.</summary>
        ScopeRetiring = 4,
        /// <summary>
        /// Every container the client could be placed in is at capacity and the game's admission hook asked for the
        /// client to wait rather than be refused (<see cref="NebulaAdmission"/>, <c>docs/capacity-admission.md</c>).
        /// This is the "docking queue" answer: the gateway keeps retrying and places the client as soon as room
        /// appears, with no reconnect.
        /// </summary>
        AtCapacity = 5,
    }

    /// <summary>
    /// Why a join was refused, in typed form. A reason string is for the player; this is for the game's client
    /// code, which has to tell "your token is bad" (pointless to retry) from "that station is full" (try a queue,
    /// another instance, or later). See <c>docs/capacity-admission.md</c>.
    /// </summary>
    public enum JoinRejectReason : byte
    {
        /// <summary>No typed reason was supplied. Read <see cref="JoinRejectedMsg.Reason"/> and <see cref="JoinRejectedMsg.Retry"/>.</summary>
        None = 0,
        /// <summary>The target the client asked for is at capacity and the admission hook refused it (<see cref="JoinRejectedMsg.Saturation"/> says how full).</summary>
        AtCapacity = 1,
        /// <summary>The admission hook refused this particular arrival for its own reasons, with the target below capacity (<see cref="NebulaAdmission.AlwaysConsult"/>).</summary>
        Denied = 2,
        /// <summary>
        /// The client's protocol is outside the gateway's supported range
        /// (<see cref="JoinRejectedMsg.SupportedMinVersion"/>..<see cref="JoinRejectedMsg.SupportedMaxVersion"/>).
        /// The client stops automatic retries; use a client and gateway with matching protocol versions.
        /// </summary>
        ProtocolUnsupported = 3,
        /// <summary>
        /// The client's <see cref="HelloMsg.GameContentVersion"/> is outside the gateway's configured range.
        /// The client stops automatic retries. The game defines the content version numbers.
        /// </summary>
        ContentVersionMismatch = 4,
        /// <summary>
        /// The mesh accepts encrypted client links only (<see cref="NebulaConfig.RequireEncryption"/>) and this
        /// one is in the clear. Retrying without turning encryption on will be refused again.
        /// </summary>
        EncryptionRequired = 5,
    }

    /// <summary>Gateway -> client: the join's state and, while <see cref="JoinState.Starting"/>, a rough wait in seconds (0 = unknown) and why.</summary>
    public struct JoinStatusMsg
    {
        public JoinState State;
        /// <summary>Roughly how long the client should expect to wait, in seconds; 0 when nobody can say.</summary>
        public ushort EstimatedSeconds;
        /// <summary>Why the join is held, while <see cref="JoinState.Starting"/>.</summary>
        public JoinHoldReason Reason;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.JoinStatus);
            w.WriteByte((byte)State);
            w.WriteUShort(EstimatedSeconds);
            w.WriteByte((byte)Reason);
        }

        public static JoinStatusMsg Read(NetworkReader r)
        {
            var m = new JoinStatusMsg { State = (JoinState)r.ReadByte(), EstimatedSeconds = r.ReadUShort() };
            // Appended after the rest of v18 was settled: a message that ends here came from a gateway that only
            // ever held a join because the world was starting.
            m.Reason = r.Remaining > 0 ? (JoinHoldReason)r.ReadByte()
                : m.State == JoinState.Starting ? JoinHoldReason.WorldStarting : JoinHoldReason.None;
            return m;
        }
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
    /// Full description of an entity. Sent as EntitySpawn (to gateway/clients), GhostSpawn (to a neighbor worker) and
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
        /// <summary>Keyframe from every sync behavior (<see cref="NetworkIdentity.WriteSyncSnapshot"/>); empty when the prefab has none.</summary>
        public byte[] State;
        /// <summary>The owning player's <see cref="PlayerIdentity"/>; empty for entities no player owns. Travels with the entity through ghosting and handover.</summary>
        public string OwnerIdentity;
        /// <summary>
        /// Meters this entity is relevant at, from its prefab (<see cref="NetworkIdentity.RelevanceRadius"/>);
        /// 0 means the mesh default. Sent as an f16, so it is precise to about a meter at the default ceiling.
        /// </summary>
        public float RelevanceRadius;
        /// <summary>Per-prefab interest flags (<see cref="NetworkIdentity.AlwaysRelevant"/>).</summary>
        public EntityInterestFlags InterestFlags;
        /// <summary>A game-defined bucket a policy can filter on (<see cref="NetworkIdentity.InterestGroup"/>).</summary>
        public byte InterestGroup;
        /// <summary>
        /// Gateway -> client: which view of this net id the spawn belongs to. It increases every time the gateway
        /// lets the entity into this client's set, so a despawn from an older view cannot kill a replica the
        /// client has just re-entered. Workers send 0.
        /// </summary>
        public ushort ViewSeq;
        /// <summary>
        /// The entity's cohesion group (<see cref="NetworkIdentity.CohesionGroup"/>), 0 for none. It travels with
        /// every spawn, ghost spawn and handover, so each worker holding a copy knows which group the entity is in
        /// and a handover of any member can take the rest with it (protocol 18, <c>docs/cohesion-hints.md</c>).
        /// </summary>
        public uint CohesionGroup;
        /// <summary>
        /// What the game says this entity costs to simulate, as a multiplier on its category weight
        /// (<see cref="NetworkIdentity.EffectiveCostWeight"/>, protocol 18). It travels with the entity so the
        /// worker it lands on reports the same cost for it. Sent as an f16; zero is a valid weight. A missing
        /// field reads as -1, which leaves the receiver's current weight unchanged.
        /// </summary>
        public float CostWeight;
        /// <summary>
        /// Worker to gateway and worker to worker: the entity's audience generation
        /// (<see cref="NetworkIdentity.SyncAudienceGeneration"/>), raised every time a <see cref="SyncAudience.Custom"/>
        /// member set changes. It travels with the entity through ghosting and handover, so it never goes back, and a
        /// gateway holds back a restricted chunk stamped with a newer generation than the member sets it has
        /// (protocol 20, trailing and optional, written only when it or <see cref="Audience"/> is set). A gateway
        /// never forwards it to a client.
        /// </summary>
        public uint AudienceGeneration;
        /// <summary>
        /// Worker to gateway and worker to worker: the member sets of the entity's Custom behaviors
        /// (<see cref="SyncAudienceCodec"/>), or null when it has none. A gateway never forwards it to a client.
        /// </summary>
        public byte[] Audience;
        /// <summary>
        /// Every <see cref="NetworkMap{TKey, TValue}"/> of the entity in full (a <see cref="NetworkMapCodec"/> section),
        /// or null when it has none (protocol 21, trailing and optional, docs/replicated-collections.md D5). A gateway
        /// keeps it current with each <see cref="MsgId.EntityMaps"/> and hands it to a late joiner.
        /// </summary>
        public byte[] Maps;

#if !NEBULA_SERVICE
        /// <param name="id">The entity.</param>
        /// <param name="scratch">A writer this reuses.</param>
        /// <param name="forGateway">
        /// True for a spawn sent to a gateway: chunks of <see cref="SyncAudience.WorkersOnly"/> behaviors are left
        /// out, since no client may have them. A ghost spawn or a handover carries every behavior.
        /// </param>
        public static EntitySpawnMsg From(NetworkIdentity id, NetworkWriter scratch, bool forGateway = false)
        {
            scratch.Reset();
            id.WriteVars(scratch);
            var vars = scratch.ToArray();
            scratch.Reset();
            byte[] state = Array.Empty<byte>();
            if (id.HasSyncState)
            {
                id.WriteSyncSnapshot(scratch, forGateway);
                state = scratch.ToArray();
            }
            byte[] maps = null;
            if (id.HasMaps)
            {
                scratch.Reset();
                id.WriteMapsFull(scratch);
                maps = scratch.ToArray();
            }
            byte[] audience = null;
            if (id.HasCustomAudience)
            {
                scratch.Reset();
                id.WriteAudienceSets(scratch);
                audience = scratch.ToArray();
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
                RelevanceRadius = id.RelevanceRadius,
                InterestFlags = id.AlwaysRelevant ? EntityInterestFlags.AlwaysRelevant : EntityInterestFlags.None,
                InterestGroup = id.InterestGroup,
                CohesionGroup = id.CohesionGroup,
                CostWeight = id.EffectiveCostWeight,
                AudienceGeneration = id.SyncAudienceGeneration,
                Audience = audience,
                Maps = maps,
            };
        }

#endif
        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            WriteBody(w);
        }

        public void WriteBody(NetworkWriter w) => WriteBody(w, embedded: false);

        /// <summary>
        /// Write the body. <paramref name="embedded"/> is true when more fields follow it in the same message (an
        /// <see cref="AuthorityTransferMsg"/>): the audience section is then always written, since a reader cannot
        /// tell it apart from the fields after it. A standalone spawn writes it only when it holds something.
        /// </summary>
        public void WriteBody(NetworkWriter w, bool embedded)
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
            w.WriteHalf(RelevanceRadius);
            w.WriteByte((byte)InterestFlags);
            w.WriteByte(InterestGroup);
            w.WriteUShort(ViewSeq);
            w.WriteUInt(CohesionGroup);
            w.WriteHalf(CostWeight);
            bool hasMaps = Maps != null && Maps.Length > 0;
            if (!embedded && AudienceGeneration == 0 && (Audience == null || Audience.Length == 0) && !hasMaps) return;
            w.WriteUInt(AudienceGeneration);
            w.WriteBytes(Audience ?? Array.Empty<byte>());
            if (!embedded && !hasMaps) return;
            var maps = Maps ?? Array.Empty<byte>();
            w.WriteInt(maps.Length);
            w.WriteRaw(new ArraySegment<byte>(maps));
        }

        /// <summary>This spawn as a client may see it: the fields only workers and gateways use are cleared.</summary>
        public EntitySpawnMsg ForClient()
        {
            var copy = this;
            copy.AudienceGeneration = 0;
            copy.Audience = null;
            return copy;
        }

        public static EntitySpawnMsg Read(NetworkReader r) => Read(r, embedded: false);

        /// <summary>Read a body written by <see cref="WriteBody(NetworkWriter, bool)"/> with the same <paramref name="embedded"/>.</summary>
        public static EntitySpawnMsg Read(NetworkReader r, bool embedded)
        {
            var msg = new EntitySpawnMsg
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
                RelevanceRadius = r.ReadHalf(),
                InterestFlags = (EntityInterestFlags)r.ReadByte(),
                InterestGroup = r.ReadByte(),
                ViewSeq = r.ReadUShort(),
                CohesionGroup = r.Remaining > 0 ? r.ReadUInt() : 0u,
                CostWeight = r.Remaining > 0 ? r.ReadHalf() : -1f,
            };
            if (embedded || r.Remaining > 0)
            {
                msg.AudienceGeneration = r.ReadUInt();
                msg.Audience = r.ReadBytes();
                if (msg.Audience.Length == 0) msg.Audience = null;
            }
            if (embedded || r.Remaining > 0)
            {
                var seg = r.ReadSegment(r.ReadInt());
                if (seg.Count > 0)
                {
                    msg.Maps = new byte[seg.Count];
                    Buffer.BlockCopy(seg.Array, seg.Offset, msg.Maps, 0, seg.Count);
                }
            }
            return msg;
        }
    }

    public struct EntityDespawnMsg
    {
        public ulong NetId;
        public uint Epoch;
        /// <summary>
        /// Gateway -> client: the view this despawn ends (see <see cref="EntitySpawnMsg.ViewSeq"/>). A client
        /// drops a despawn older than the view it currently holds, so a late leave cannot kill a re-entered
        /// replica. Workers send 0.
        /// </summary>
        public ushort ViewSeq;
        /// <summary>
        /// Worker -> worker (<see cref="MsgId.GhostDespawn"/>): the entity is a carrier that left the world together
        /// with what rides in it, because the container it was in was emptied (<see cref="NebulaWorker.EmptyContainer"/>).
        /// A worker holding the carrier as a ghost despawns the riders it owns aboard it the same way, instead of
        /// putting them down in the box around it (protocol 18, trailing and optional; written only when true;
        /// <c>docs/dynamic-worlds.md</c> D5). False everywhere else, which keeps the riders.
        /// </summary>
        public bool TakesRiders;
        /// <summary>
        /// With <see cref="TakesRiders"/>: the carrier's <see cref="PersistentEntity.Key"/>, empty when it is not
        /// persistent. A ghost does not know its entity's key, and a rider saved aboard it must name the carrier's
        /// real record so that it is restored with the carrier.
        /// </summary>
        public string CarrierKey;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteUShort(ViewSeq);
            if (!TakesRiders) return;
            w.WriteBool(true);
            w.WriteString(CarrierKey ?? "");
        }

        public static EntityDespawnMsg Read(NetworkReader r)
        {
            var m = new EntityDespawnMsg { NetId = r.ReadULong(), Epoch = r.ReadUInt(), ViewSeq = r.ReadUShort(), CarrierKey = "" };
            if (r.Remaining > 0) m.TakesRiders = r.ReadBool();
            if (m.TakesRiders && r.Remaining > 0) m.CarrierKey = r.ReadString() ?? "";
            return m;
        }
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
        /// <summary>
        /// Worker to gateway: the entity's audience generation when these chunks were written
        /// (<see cref="EntitySpawnMsg.AudienceGeneration"/>). A gateway forwards a restricted chunk only once it holds
        /// member sets at least this new, so a chunk written after a client left an audience cannot reach that client
        /// in a sequenced packet that overtook the reliable <see cref="SyncAudienceMsg"/>. Trailing and optional
        /// (protocol 20); written only when non-zero, and never sent to a client.
        /// </summary>
        public uint AudienceGeneration;

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
            if (AudienceGeneration != 0) w.WriteUInt(AudienceGeneration);
        }

        public static EntitySyncMsg Read(NetworkReader r) => new EntitySyncMsg
        {
            NetId = r.ReadULong(),
            Epoch = r.ReadUInt(),
            Tick = r.ReadUInt(),
            Container = ContainerRef.Read(r),
            Reliable = r.ReadBool(),
            Chunks = r.ReadBytes(),
            AudienceGeneration = r.Remaining >= 4 ? r.ReadUInt() : 0u,
        };
    }

    /// <summary>
    /// Worker to gateway (<see cref="MsgId.SyncAudience"/>, protocol 20): the member sets of an entity's
    /// <see cref="SyncAudience.Custom"/> behaviors changed. Sent reliably, before that tick's sync chunks, to every
    /// gateway the entity's sync state goes to. It carries every set whole rather than a delta, so applying it twice
    /// does no harm. The gateway sends each client that joined a set the newest keyframe it holds, and tells each
    /// client that left to drop its copy (<see cref="SyncStateCodec.ChunkFlags.Cleared"/>).
    /// </summary>
    public struct SyncAudienceMsg
    {
        public ulong NetId;
        public uint Epoch;
        /// <summary>The entity's audience generation after the change; see <see cref="EntitySpawnMsg.AudienceGeneration"/>.</summary>
        public uint Generation;
        /// <summary>Every Custom behavior's member set (<see cref="SyncAudienceCodec"/>).</summary>
        public byte[] Sets;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.SyncAudience);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteUInt(Generation);
            w.WriteBytes(Sets);
        }

        public static SyncAudienceMsg Read(NetworkReader r) => new SyncAudienceMsg
        {
            NetId = r.ReadULong(),
            Epoch = r.ReadUInt(),
            Generation = r.ReadUInt(),
            Sets = r.ReadBytes(),
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
        /// <summary>For a ClientRpc broadcast: deliver only to clients whose pawn is within this many meters of the entity (0 = everyone). See ClientRpcAttribute.Radius.</summary>
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

    /// <summary>Flags on an <see cref="AuthorityCallMsg"/>.</summary>
    [Flags]
    public enum AuthorityCallFlags : byte
    {
        None = 0,
        /// <summary>The sender wants an <see cref="AuthorityCallReplyMsg"/> with the outcome.</summary>
        WantsReply = 1,
    }

    /// <summary>
    /// An <c>AuthorityRpc</c> on the worker-to-worker link (<see cref="MsgId.AuthorityRpc"/>). Beside the fields an
    /// <see cref="EntityRpcMsg"/> carries, it names the call (<see cref="CallId"/>, minted by the sender: see
    /// <see cref="AuthorityCallId"/>), counts the forwards it has taken (<see cref="Hops"/>) and says whether the
    /// sender wants to hear the outcome. The contract is in <c>docs/cross-worker-calls.md</c>.
    /// </summary>
    public struct AuthorityCallMsg
    {
        /// <summary>The sender's id for this call; forwarding keeps it.</summary>
        public ulong CallId;
        /// <summary>How many workers have forwarded this call so far. 0 as sent.</summary>
        public byte Hops;
        public AuthorityCallFlags Flags;
        public ulong NetId;
        /// <summary>The entity's epoch on the sender's copy when the call was made.</summary>
        public uint Epoch;
        public byte BehaviourIndex;
        public uint MethodHash;
        public byte[] Args;

        public bool WantsReply => (Flags & AuthorityCallFlags.WantsReply) != 0;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.AuthorityRpc);
            w.WriteULong(CallId);
            w.WriteByte(Hops);
            w.WriteByte((byte)Flags);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            w.WriteByte(BehaviourIndex);
            w.WriteUInt(MethodHash);
            w.WriteBytes(Args);
        }

        public static AuthorityCallMsg Read(NetworkReader r) => new AuthorityCallMsg
        {
            CallId = r.ReadULong(),
            Hops = r.ReadByte(),
            Flags = (AuthorityCallFlags)r.ReadByte(),
            NetId = r.ReadULong(),
            Epoch = r.ReadUInt(),
            BehaviourIndex = r.ReadByte(),
            MethodHash = r.ReadUInt(),
            Args = r.ReadBytes(),
        };
    }

    /// <summary>
    /// The outcome of an <see cref="AuthorityCallMsg"/> that set <see cref="AuthorityCallFlags.WantsReply"/>
    /// (<see cref="MsgId.AuthorityRpcReply"/>), sent by the worker that decided it to the worker whose index the
    /// call id carries.
    /// </summary>
    public struct AuthorityCallReplyMsg
    {
        public ulong CallId;
        public AuthorityCallOutcome Outcome;
        /// <summary>The entity's epoch on the deciding worker, or 0 when it did not hold the entity.</summary>
        public uint Epoch;
        /// <summary>The call's hop count when it was decided.</summary>
        public byte Hops;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.AuthorityRpcReply);
            w.WriteULong(CallId);
            w.WriteByte((byte)Outcome);
            w.WriteUInt(Epoch);
            w.WriteByte(Hops);
        }

        public static AuthorityCallReplyMsg Read(NetworkReader r) => new AuthorityCallReplyMsg
        {
            CallId = r.ReadULong(),
            Outcome = (AuthorityCallOutcome)r.ReadByte(),
            Epoch = r.ReadUInt(),
            Hops = r.ReadByte(),
        };

        /// <summary>The result a sender's callback receives for this reply.</summary>
        public AuthorityCallResult ToResult() => new AuthorityCallResult(CallId, Outcome, Epoch, Hops);
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
        /// <summary>Runtime containers: the box a client registers the container with. See <see cref="LeaseInfo.HasBounds"/>.</summary>
        public bool HasBounds;
        public Vector3 BoundsCenter;
        public Vector3 BoundsSize;
        /// <summary>
        /// Runtime containers: where the box goes (parent, centre in double, authority, physics frame), carried in the
        /// message's trailing placement section so a protocol-18 reader that predates it still reads the float box.
        /// <see cref="HasPlacement"/> is false when the sender wrote none (a root with no frame, whose float box is all there is).
        /// </summary>
        public bool HasPlacement;
        public ContainerPlacement Placement;

        /// <summary>The placement to register with: the trailing one when present, otherwise a root at the float box.</summary>
        public ContainerPlacement PlacementOrRoot => HasPlacement ? Placement : ContainerPlacement.Root(new Bounds(BoundsCenter, BoundsSize));

        /// <summary>An entry for a lease row, box and placement included.</summary>
        public static ContainerOwnershipEntry Of(LeaseInfo lease, ushort containerIndex, ushort workerIndex)
        {
            var e = new ContainerOwnershipEntry
            {
                Instance = lease.Instance,
                ContainerIndex = containerIndex,
                ContainerId = lease.ContainerId,
                WorkerIndex = workerIndex,
                WorkerId = lease.WorkerId,
                Epoch = lease.Epoch,
                State = lease.State,
                HasBounds = lease.HasBounds,
                BoundsCenter = lease.BoundsCenter,
                BoundsSize = lease.BoundsSize,
            };
            if (lease.HasBounds && (!lease.IsRoot || lease.OwnPhysicsFrame || lease.Authority != ContainerAuthority.Auto || lease.FrameInterest != FrameInterestMode.WithCarrier || NeedsDouble(lease.Center)))
            {
                e.HasPlacement = true;
                e.Placement = lease.Placement;
            }
            return e;
        }

        /// <summary>A centre a float cannot carry exactly (far from the origin, or with a fraction a float would lose).</summary>
        private static bool NeedsDouble(Double3 c) => (double)(float)c.X != c.X || (double)(float)c.Y != c.Y || (double)(float)c.Z != c.Z;
    }

    /// <summary>
    /// What one <see cref="MsgId.ContainerOwnership"/> message says: either the complete set of containers a
    /// client should know about (<see cref="Full"/>) or a change to it. Interest management makes this per-client
    /// and incremental: a client is told about the containers overlapping its own window, so the
    /// client receives only the relevant part of a large world's container table.
    /// </summary>
    public struct ContainerOwnershipUpdate
    {
        /// <summary>The upserts are the complete set: the receiver forgets every container not named here.</summary>
        public bool Full;
        /// <summary>Containers added or changed.</summary>
        public List<ContainerOwnershipEntry> Upserts;
        /// <summary>Container ids the client should forget. Empty in a Full message.</summary>
        public List<string> Removes;
    }

    public static class ContainerOwnershipMsg
    {
        private const byte FlagBounds = 1;
        private const byte FlagFull = 1;

        /// <summary>Write a complete snapshot (the form a gateway sends on join and on every control-plane change).</summary>
        public static void Write(NetworkWriter w, IList<ContainerOwnershipEntry> entries) => Write(w, entries, true, null);

        /// <summary>
        /// Write a snapshot or a delta. A delta's removes are applied after its upserts, and a container is only
        /// ever removed once the entities inside it have been despawned.
        /// </summary>
        public static void Write(NetworkWriter w, IList<ContainerOwnershipEntry> entries, bool full, IList<string> removes)
        {
            w.WriteByte((byte)MsgId.ContainerOwnership);
            w.WriteByte(full ? FlagFull : (byte)0);
            w.WriteUShort((ushort)(entries?.Count ?? 0));
            if (entries != null) foreach (var e in entries)
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
            w.WriteUShort((ushort)(removes?.Count ?? 0));
            if (removes != null) foreach (var id in removes) w.WriteString(id ?? "");
            // Trailing placement section (docs/container-tree.md §6): parent, centre in double, authority and frame of
            // each runtime entry that has more to say than its float box. A reader that predates it stops before it.
            int placed = 0;
            if (entries != null) foreach (var e in entries) if (e.HasPlacement) placed++;
            if (placed == 0) return;
            w.WriteUShort((ushort)placed);
            for (int i = 0; entries != null && i < entries.Count; i++)
            {
                var e = entries[i];
                if (!e.HasPlacement) continue;
                w.WriteUShort((ushort)i);
                w.WriteString(e.Placement.ParentId ?? "");
                w.WriteDouble(e.Placement.Center.X);
                w.WriteDouble(e.Placement.Center.Y);
                w.WriteDouble(e.Placement.Center.Z);
                w.WriteByte((byte)e.Placement.Authority);
                w.WriteByte((byte)((e.Placement.OwnPhysicsFrame ? FlagFrame : 0) | (e.Placement.FrameInterest == FrameInterestMode.OwnRegions ? FlagOwnRegions : 0)));
            }
        }

        private const byte FlagFrame = 1;
        private const byte FlagOwnRegions = 2;

        public static ContainerOwnershipUpdate Read(NetworkReader r)
        {
            var update = new ContainerOwnershipUpdate { Full = (r.ReadByte() & FlagFull) != 0 };
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
            update.Upserts = list;
            int removed = r.ReadUShort();
            update.Removes = new List<string>(removed);
            for (int i = 0; i < removed; i++) update.Removes.Add(r.ReadString() ?? "");
            if (r.Remaining >= 2)
            {
                int placed = r.ReadUShort();
                for (int p = 0; p < placed; p++)
                {
                    int index = r.ReadUShort();
                    var placement = new ContainerPlacement
                    {
                        ParentId = r.ReadString() ?? "",
                        Center = new Double3(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()),
                        Authority = (ContainerAuthority)r.ReadByte(),
                    };
                    byte flags = r.ReadByte();
                    placement.OwnPhysicsFrame = (flags & FlagFrame) != 0;
                    placement.FrameInterest = (flags & FlagOwnRegions) != 0 ? FrameInterestMode.OwnRegions : FrameInterestMode.WithCarrier;
                    if (index >= list.Count) continue;
                    var e = list[index];
                    placement.Size = e.BoundsSize;
                    e.HasPlacement = true;
                    e.Placement = placement;
                    list[index] = e;
                }
            }
            return update;
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
    /// Worker -> gateway: this gateway no longer speaks for the session, because the player connected again through
    /// another gateway (<see cref="NebulaConfig.SingleSessionPerPlayer"/>). The gateway lets its client go with a
    /// <see cref="SessionReplacedMsg"/> and does not despawn the pawn, which the new connection has taken over.
    /// Fenced by <see cref="Generation"/> the same way <see cref="SpawnPlayerMsg"/> is: a gateway that has since
    /// claimed the session with a newer generation ignores it.
    /// </summary>
    public struct EndSessionMsg
    {
        public ulong ClientId;
        public ulong Generation;
        /// <summary>Why the session ended, fit to show the player.</summary>
        public string Reason;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.EndSession);
            w.WriteULong(ClientId);
            w.WriteULong(Generation);
            w.WriteString(Reason ?? "");
        }

        public static EndSessionMsg Read(NetworkReader r) => new EndSessionMsg { ClientId = r.ReadULong(), Generation = r.ReadULong(), Reason = r.ReadString() ?? "" };
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
        /// <summary>Per-behavior handover-only state (<see cref="NetworkIdentity.WriteHandoverState"/>).</summary>
        public byte[] HandoverState;
        public string[] GhostWorkers;
        /// <summary>The owner's session as the sender knew it (<see cref="PlayerSessions"/>), so the receiver accepts the owner's gateway at once. 0/"" for an unowned entity.</summary>
        public ulong SessionGeneration;
        public string SessionGateway;
        /// <summary>
        /// Gateway keys (<see cref="PlayerSessions.GatewayKey"/>) that were following this entity on the old owner
        /// without subscribing its region — an explicit per-entity subscription, or the session it speaks for.
        /// The new owner announces the spawn to these gateways as well as to the gateways its own regions cover,
        /// so a followed entity is not lost the moment it changes worker.
        /// </summary>
        public string[] InterestGateways;
        /// <summary>
        /// The entity is crossing a physics frame's boundary and the sender is not the frame's pose owner, so it hands
        /// the entity over unconverted for the pose owner to cross it at its exact pose (<c>docs/container-tree.md</c>
        /// D15). A trailing optional byte: an older reader stops before it.
        /// </summary>
        public bool Crossing;
        /// <summary>
        /// The owner's session was waiting for a reclaim on the sender (<see cref="PlayerSessions.Session.Orphan"/>):
        /// the player disconnected, or the gateway's link to the sender dropped. The receiver keeps it waiting, so a
        /// pawn handed over in the reclaim window is still removed if nobody comes back. With
        /// <see cref="SessionReclaimRemaining"/>, a trailing optional section after <see cref="Crossing"/>, written
        /// only for an orphan: an older reader stops before it.
        /// </summary>
        public PlayerSessions.OrphanKind SessionOrphan;
        /// <summary>
        /// Seconds of the reclaim grace (<see cref="NebulaConfig.SessionReclaimSeconds"/>) the session had left on the
        /// sender. A duration, not a time: the two workers' clocks differ, so the receiver resumes the countdown
        /// from its own clock.
        /// </summary>
        public float SessionReclaimRemaining;
        /// <summary>
        /// The entity's extent was set at runtime (<c>NetworkIdentity.ExtentChangedAtRuntime</c>), so the receiver
        /// takes <see cref="ExtentSource"/>, <see cref="ExtentCenter"/> and <see cref="ExtentSize"/> instead of its
        /// prefab's. A trailing optional section after the session section (<c>docs/entity-extents.md</c> D6),
        /// written only when set: an older reader stops before it. False leaves the receiver's copy as it is.
        /// </summary>
        public bool CarriesExtent;
        /// <summary>Where the extent comes from. For <see cref="EntityExtentSource.Colliders"/> the receiver computes the box from its own copy.</summary>
        public EntityExtentSource ExtentSource;
        /// <summary>Centre of the extent box in the entity's local space.</summary>
        public Vector3 ExtentCenter;
        /// <summary>Size of the extent box in the entity's local space.</summary>
        public Vector3 ExtentSize;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.AuthorityTransfer);
            Entity.WriteBody(w, embedded: true);
            w.WriteUInt(NewEpoch);
            w.WriteBytes(PendingInputs);
            w.WriteBytes(HandoverState);
            w.WriteUShort((ushort)(GhostWorkers?.Length ?? 0));
            if (GhostWorkers != null) foreach (var worker in GhostWorkers) w.WriteString(worker);
            w.WriteULong(SessionGeneration);
            w.WriteString(SessionGateway ?? "");
            w.WriteUShort((ushort)(InterestGateways?.Length ?? 0));
            if (InterestGateways != null) foreach (var key in InterestGateways) w.WriteString(key);
            bool orphan = SessionOrphan != PlayerSessions.OrphanKind.None;
            // Each trailing section is written when it or any section after it holds something, so a reader that
            // knows fewer sections stops where it always did and one that knows more reads their "none" values.
            if (Crossing || orphan || CarriesExtent) w.WriteByte(Crossing ? (byte)1 : (byte)0);
            if (!orphan && !CarriesExtent) return;
            w.WriteByte((byte)SessionOrphan);
            w.WriteFloat(orphan ? SessionReclaimRemaining : 0f);
            if (!CarriesExtent) return;
            w.WriteByte((byte)ExtentSource);
            w.WriteVector3(ExtentCenter);
            w.WriteVector3(ExtentSize);
        }

        public static AuthorityTransferMsg Read(NetworkReader r)
        {
            var msg = new AuthorityTransferMsg
            {
                Entity = EntitySpawnMsg.Read(r, embedded: true),
                NewEpoch = r.ReadUInt(),
                PendingInputs = r.ReadBytes(),
                HandoverState = r.ReadBytes(),
                GhostWorkers = ReadStrings(r),
                SessionGeneration = r.ReadULong(),
                SessionGateway = r.ReadString() ?? "",
                InterestGateways = ReadStrings(r),
            };
            msg.Crossing = r.Remaining > 0 && r.ReadByte() != 0;
            if (r.Remaining > 0)
            {
                msg.SessionOrphan = (PlayerSessions.OrphanKind)r.ReadByte();
                msg.SessionReclaimRemaining = r.ReadFloat();
            }
            if (r.Remaining > 0)
            {
                msg.CarriesExtent = true;
                msg.ExtentSource = (EntityExtentSource)r.ReadByte();
                msg.ExtentCenter = r.ReadVector3();
                msg.ExtentSize = r.ReadVector3();
            }
            return msg;
        }

        private static string[] ReadStrings(NetworkReader r)
        {
            int count = r.ReadUShort();
            if (count == 0) return Array.Empty<string>();
            var values = new string[count];
            for (int i = 0; i < count; i++) values[i] = r.ReadString();
            return values;
        }
    }

    [Flags]
    public enum InterestSubscribeFlags : byte
    {
        None = 0,
        /// <summary>The message replaces the worker's whole set instead of changing it.</summary>
        Full = 1,
        /// <summary>
        /// The last chunk of this update: the worker applies the staged changes, checks
        /// <see cref="InterestSubscribeMsg.SetCount"/> and <see cref="InterestSubscribeMsg.SetHash"/>, and only
        /// then adopts the new sequence number. A set too large for one message is split, and the chunks before
        /// this one change nothing the worker will keep if the commit never arrives or does not verify.
        /// </summary>
        Commit = 2,
    }

    /// <summary>
    /// Gateway -> worker: the set of regions this gateway wants entities from. The state is a set, so
    /// the message is idempotent: a delta applies only when <see cref="BaseSeq"/> is the worker's current sequence
    /// and the resulting (count, hash) matches, and the worker otherwise keeps what it has and asks for a resync.
    /// The grid travels with it so a worker can refuse to filter with ids the gateway did not mean.
    /// </summary>
    public struct InterestSubscribeMsg
    {
        /// <summary>Sequence this message brings the worker's set to.</summary>
        public uint Seq;
        /// <summary>Sequence the delta is against; ignored for a <see cref="InterestSubscribeFlags.Full"/> message.</summary>
        public uint BaseSeq;
        public float EdgeX, EdgeY, EdgeZ, OffsetX, OffsetY, OffsetZ;
        public bool Planar;
        public InterestSubscribeFlags Flags;
        public List<ulong> Add;
        public List<ulong> Remove;
        /// <summary>The regions the gateway's clients are focused on, always in full; small, and the worker matches wide entities against them.</summary>
        public List<ulong> FociRegions;
        /// <summary>Explicit per-entity subscriptions (policy extras), always in full.</summary>
        public List<ulong> Entities;
        /// <summary>Regions in the set after applying this message.</summary>
        public uint SetCount;
        /// <summary>Order-independent mix of the set after applying (see <see cref="RegionSubscription.Hash"/>).</summary>
        public ulong SetHash;

        public bool IsFull => (Flags & InterestSubscribeFlags.Full) != 0;
        public bool IsCommit => (Flags & InterestSubscribeFlags.Commit) != 0;

        /// <summary>The grid the sender made these region ids with.</summary>
        public InterestGrid Grid
        {
            get => new InterestGrid(EdgeX, EdgeY, EdgeZ, OffsetX, OffsetY, OffsetZ, Planar);
            set
            {
                EdgeX = (float)value.EdgeX; EdgeY = (float)value.EdgeY; EdgeZ = (float)value.EdgeZ;
                OffsetX = (float)value.OffsetX; OffsetY = (float)value.OffsetY; OffsetZ = (float)value.OffsetZ;
                Planar = value.Planar;
            }
        }

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.InterestSubscribe);
            w.WriteUInt(Seq);
            w.WriteUInt(BaseSeq);
            w.WriteFloat(EdgeX); w.WriteFloat(EdgeY); w.WriteFloat(EdgeZ);
            w.WriteFloat(OffsetX); w.WriteFloat(OffsetY); w.WriteFloat(OffsetZ);
            w.WriteBool(Planar);
            w.WriteByte((byte)Flags);
            WriteIds(w, Add);
            WriteIds(w, Remove);
            WriteIds(w, FociRegions);
            WriteIds(w, Entities);
            w.WriteUInt(SetCount);
            w.WriteULong(SetHash);
        }

        private static void WriteIds(NetworkWriter w, List<ulong> ids)
        {
            w.WriteUShort((ushort)(ids?.Count ?? 0));
            if (ids != null) foreach (ulong id in ids) w.WriteULong(id);
        }

        /// <summary>
        /// Read a subscription. Pass the previous message back in to reuse its lists: a gateway link sends one of
        /// these several times a second and the worker has no reason to allocate four lists each time.
        /// </summary>
        public static InterestSubscribeMsg Read(NetworkReader r, InterestSubscribeMsg reuse = default)
        {
            var m = new InterestSubscribeMsg
            {
                Seq = r.ReadUInt(),
                BaseSeq = r.ReadUInt(),
                EdgeX = r.ReadFloat(), EdgeY = r.ReadFloat(), EdgeZ = r.ReadFloat(),
                OffsetX = r.ReadFloat(), OffsetY = r.ReadFloat(), OffsetZ = r.ReadFloat(),
                Planar = r.ReadBool(),
                Flags = (InterestSubscribeFlags)r.ReadByte(),
                Add = ReadIds(r, reuse.Add),
                Remove = ReadIds(r, reuse.Remove),
                FociRegions = ReadIds(r, reuse.FociRegions),
                Entities = ReadIds(r, reuse.Entities),
            };
            m.SetCount = r.ReadUInt();
            m.SetHash = r.ReadULong();
            return m;
        }

        private static List<ulong> ReadIds(NetworkReader r, List<ulong> reuse)
        {
            int n = r.ReadUShort();
            var list = reuse ?? new List<ulong>(n);
            list.Clear();
            for (int i = 0; i < n; i++) list.Add(r.ReadULong());
            return list;
        }
    }

    /// <summary>
    /// Worker -> gateway: the last <see cref="InterestSubscribeMsg"/> could not be applied (wrong base sequence,
    /// or the result did not verify). The worker kept the set it had, at <see cref="HaveSeq"/>; the gateway
    /// answers with a Full snapshot.
    /// </summary>
    public struct InterestResyncMsg
    {
        public uint HaveSeq;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.InterestResync);
            w.WriteUInt(HaveSeq);
        }

        public static InterestResyncMsg Read(NetworkReader r) => new InterestResyncMsg { HaveSeq = r.ReadUInt() };
    }

    /// <summary>
    /// Worker -> gateway: an entity the gateway subscribed by id (a policy extra, or one it owns) is now on
    /// another worker. The gateway links that worker instead of asking every live one for the id.
    /// </summary>
    public struct EntityRedirectMsg
    {
        public ulong NetId;
        public ushort NewWorkerIndex;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.EntityRedirect);
            w.WriteULong(NetId);
            w.WriteUShort(NewWorkerIndex);
        }

        public static EntityRedirectMsg Read(NetworkReader r) => new EntityRedirectMsg { NetId = r.ReadULong(), NewWorkerIndex = r.ReadUShort() };
    }

    /// <summary>
    /// Worker -> gateway: the entity left every region this gateway subscribes on this worker. The gateway drops
    /// its cached record (and despawns it from whoever still observes it); no per-gateway per-entity "known" set
    /// exists on the worker, which is what keeps worker memory independent of the number of gateways.
    /// </summary>
    public struct EntityForgetMsg
    {
        public ulong NetId;
        public uint Epoch;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.EntityForget);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
        }

        public static EntityForgetMsg Read(NetworkReader r) => new EntityForgetMsg { NetId = r.ReadULong(), Epoch = r.ReadUInt() };
    }

    /// <summary>
    /// Client -> gateway: where the player is looking, as a hint for interest (a sniper scope, an RTS camera).
    /// The gateway validates it (<see cref="FocusHintFilter"/>) before any policy sees it: a hint is an input,
    /// never authority, so it cannot be used to see the whole map.
    /// </summary>
    public struct ClientFocusHintMsg
    {
        /// <summary>
        /// The hinted point in <b>absolute</b> world coordinates, in double — the same space region keys,
        /// container boxes and the gateway's own positions are in, and the only space both ends agree on. A
        /// client sends its floating-origin frame position plus its origin (<c>NebulaClient</c> does the sum),
        /// so a shift under the camera changes the frame position and the origin together and the point the
        /// gateway sees does not move. Floats would lose meters out at the edge of an unbounded world, which is
        /// exactly where a strategy camera is pointed, so this is three doubles and not a <c>Vector3</c>.
        /// </summary>
        public double X, Y, Z;
        /// <summary>
        /// Bumped by the client every time it clears its hint. Hints travel <see cref="Delivery.Sequenced"/> and
        /// the clear travels reliably, so a hint already in flight can arrive after the clear; without a
        /// generation it would quietly re-establish the focus the game just gave up.
        /// </summary>
        public byte Generation;
        /// <summary>The client withdraws its hint: interest returns to the pawn. The position is unused.</summary>
        public bool Clear;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.ClientFocusHint);
            w.WriteByte(Generation);
            w.WriteByte((byte)(Clear ? 1 : 0));
            if (Clear) return;
            w.WriteDouble(X);
            w.WriteDouble(Y);
            w.WriteDouble(Z);
        }

        public static ClientFocusHintMsg Read(NetworkReader r)
        {
            var msg = new ClientFocusHintMsg { Generation = r.ReadByte(), Clear = (r.ReadByte() & 1) != 0 };
            if (msg.Clear) return msg;
            msg.X = r.ReadDouble();
            msg.Y = r.ReadDouble();
            msg.Z = r.ReadDouble();
            return msg;
        }

        /// <summary>True when <paramref name="generation"/> is older than <paramref name="current"/> (wrapping).</summary>
        public static bool IsStale(byte generation, byte current) => (byte)(generation - current) >= 128;
    }
}
