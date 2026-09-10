using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Base class for meshed gameplay code. The surface is deliberately the one Mirror/NGO users know:
    /// <see cref="IsServer"/>/<see cref="IsClient"/>/<see cref="IsOwner"/>, spawn/despawn hooks, NetworkVariables
    /// and RPCs. The meshing-specific additions are small: <see cref="HasAuthority"/> (this worker owns the
    /// entity right now), <see cref="IsGhost"/> (this worker holds a kinematic replica owned by a neighbour),
    /// <see cref="OnGainedAuthority"/>/<see cref="OnLostAuthority"/>, and <see cref="AuthorityRpc"/>.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        private NetworkIdentity _identity;

        public NetworkIdentity Identity
        {
            get
            {
                if (_identity == null) _identity = GetComponentInParent<NetworkIdentity>();
                return _identity;
            }
            internal set => _identity = value;
        }

        public byte BehaviourIndex { get; internal set; }
        internal NetworkVariableBase[] Vars = Array.Empty<NetworkVariableBase>();

        public ulong NetId => Identity != null ? Identity.NetId : 0;
        public bool IsSpawned => Identity != null && Identity.IsSpawned;
        public bool IsServer => NebulaRuntime.IsServer;
        public bool IsClient => NebulaRuntime.IsClient;
        /// <summary>Client side: this is the local player's entity.</summary>
        public bool IsOwner => IsClient && Identity != null && Identity.IsLocalPlayer;
        /// <summary>Server side: this worker is authoritative for the entity right now.</summary>
        public bool HasAuthority => IsServer && Identity != null && Identity.HasAuthority;
        /// <summary>Server side: this worker holds a non-authoritative replica driven by a neighbouring worker.</summary>
        public bool IsGhost => IsServer && Identity != null && !Identity.HasAuthority;
        public uint OwnerClientId => Identity != null ? Identity.OwnerClientId : 0;
        public Container Container => Identity != null ? Identity.Container : null;

        // ---- lifecycle hooks --------------------------------------------------------------------------

        public virtual void OnNetworkSpawn() { }
        public virtual void OnNetworkDespawn() { }
        /// <summary>Called on a worker when it becomes authoritative for this entity (spawn, or handover in).</summary>
        public virtual void OnGainedAuthority() { }
        /// <summary>Called on a worker when authority moves to a neighbour. The object stays alive as a ghost.</summary>
        public virtual void OnLostAuthority() { }
        public virtual void OnContainerChanged(Container previous, Container current) { }
        /// <summary>
        /// Partitioned worlds: the floating origin moved by <paramref name="delta"/>. The transform was moved with
        /// its container; add the delta to any frame position this behaviour caches itself (a target, a path, a
        /// history buffer). Not called in single-scene games.
        /// </summary>
        public virtual void OnOriginShifted(Vector3 delta) { }
        /// <summary>Authoritative simulation step at the network tick rate. Not called for ghosts or on clients.</summary>
        public virtual void NetworkTick(uint tick, float deltaTime) { }

        /// <summary>
        /// Called on the outgoing worker while building an authority transfer, before <see cref="OnLostAuthority"/>.
        /// Write any state the next owner needs that is not already replicated (angular velocity, timers, RNG
        /// state...). Travels only with the handover, never in the per-tick stream. Must be mirrored exactly by
        /// <see cref="ReadHandoverState"/>.
        /// </summary>
        public virtual void WriteHandoverState(NetworkWriter writer) { }

        /// <summary>
        /// Called on the incoming worker after the entity's pose, velocity and NetworkVariables have been applied
        /// and before <see cref="OnGainedAuthority"/>. Read exactly what <see cref="WriteHandoverState"/> wrote.
        /// </summary>
        public virtual void ReadHandoverState(NetworkReader reader) { }

        // ---- per-tick sync state ------------------------------------------------------------------------
        //
        // A second replication channel next to NetworkVariables, for state that changes every tick and is better
        // sent as small deltas (NetworkTransform, NetworkAnimator). The authority marks it dirty; the worker gathers
        // one chunk per dirty behaviour into an EntityState message each tick (reliable or unreliable per behaviour,
        // with a full keyframe every NetworkIdentity.SyncKeyframeInterval ticks), and every non-authoritative copy
        // reads it with the tick it belongs to. The spawn message carries a full chunk so late joiners and fresh
        // ghosts start from the current state.

        internal bool SyncDirty;
        /// <summary>
        /// A chunk has gone out since this copy gained authority. Set from <see cref="NetworkIdentity.ClearDirty"/>
        /// once the tick's sends are done, never mid-tick: the worker writes the same tick to several destinations
        /// (each gateway, each worker holding a ghost) and every one of them must open with the same keyframe.
        /// </summary>
        internal bool SyncEverSent;
        /// <summary>A chunk was written this tick (any destination); promoted to <see cref="SyncEverSent"/> by ClearDirty.</summary>
        internal bool SyncWrittenThisTick;

        /// <summary>True when this behaviour replicates through <see cref="WriteSyncState"/>/<see cref="ReadSyncState"/>.</summary>
        public virtual bool HasSyncState => false;

        /// <summary>
        /// Delivery for this behaviour's sync chunks. Unreliable (sequenced) is right for transforms: a lost delta is
        /// healed by the next keyframe. Reliable is right for anything a receiver must not miss (animator triggers).
        /// </summary>
        public virtual Delivery SyncDelivery => Delivery.ReliableOrdered;

        /// <summary>Ask the worker to send this behaviour's sync state at the end of the tick.</summary>
        protected void MarkSyncDirty()
        {
            SyncDirty = true;
            Identity?.MarkSyncDirty();
        }

        /// <summary>
        /// Authority only. Write the state to replicate. When <paramref name="full"/> is true write everything (a
        /// keyframe: this is what a late joiner or a fresh ghost will start from); otherwise only what changed since
        /// the last write is needed, though writing everything is always correct.
        /// </summary>
        public virtual void WriteSyncState(NetworkWriter writer, bool full) { }

        /// <summary>
        /// Non-authoritative copies. <paramref name="tick"/> is the simulation tick the state belongs to (0 for a
        /// spawn snapshot); buffer it and present it from <see cref="RemoteTick"/>, or apply it at once.
        /// </summary>
        public virtual void ReadSyncState(NetworkReader reader, uint tick, bool full) { }

        /// <summary>
        /// The tick's sends are done (every peer and gateway got this tick's chunk): drop per-tick pending deltas.
        /// Called from <see cref="NetworkIdentity.ClearDirty"/>; the owning client calls it after its own send.
        /// </summary>
        protected internal virtual void OnSyncStateSent() { }

        /// <summary>
        /// Called on every copy this process does not simulate (ghosts on a worker: once per tick before physics
        /// syncs; entities on a client: once per frame) with the tick to present. Interpolating behaviours sample
        /// their buffers here. See <see cref="NetworkTime.RenderTick"/>.
        /// </summary>
        public virtual void RemoteTick(double renderTick) { }

        // ---- RPC senders --------------------------------------------------------------------------------

        private static readonly NetworkWriter RpcWriter = new NetworkWriter(512);

        private void SendRpc(RpcKind kind, string methodName, object[] args, uint targetClientId = 0)
        {
            var m = RpcRegistry.Require(GetType(), methodName);
            if (m.Kind != kind)
                throw new InvalidOperationException($"{GetType().Name}.{methodName} is a {m.Kind} RPC, sent as {kind}");
            if (!IsSpawned)
            {
                NebulaLog.Warn($"RPC {methodName} on unspawned {GetType().Name} ignored");
                return;
            }
            var sink = NebulaRuntime.RpcSink;
            if (sink == null) return;

            RpcWriter.Reset();
            RpcRegistry.WriteArgs(RpcWriter, m, args);
            var payload = RpcWriter.ToSegment();

            switch (kind)
            {
                case RpcKind.Client:
                    if (!IsServer) { NebulaLog.Warn($"ClientRpc {methodName} can only be sent from a worker"); return; }
                    if (!HasAuthority) { NebulaLog.Warn($"ClientRpc {methodName} sent from a ghost of {NetId}; ignored"); return; }
                    sink.SendClientRpc(Identity, BehaviourIndex, m.Hash, payload, targetClientId);
                    break;
                case RpcKind.Server:
                    if (!IsClient) { NebulaLog.Warn($"ServerRpc {methodName} can only be sent from a client"); return; }
                    if (!IsOwner) { NebulaLog.Warn($"ServerRpc {methodName} requires ownership of {NetId}"); return; }
                    sink.SendServerRpc(Identity, BehaviourIndex, m.Hash, payload);
                    break;
                case RpcKind.Authority:
                    if (!IsServer) { NebulaLog.Warn($"AuthorityRpc {methodName} can only be sent from a worker"); return; }
                    if (HasAuthority)
                    {
                        // We are the authority: run it right here.
                        RpcRegistry.Invoke(this, m.Hash, new NetworkReader(payload));
                    }
                    else
                    {
                        sink.SendAuthorityRpc(Identity, BehaviourIndex, m.Hash, payload);
                    }
                    break;
            }
        }

        protected void ClientRpc(Action method) => SendRpc(RpcKind.Client, method.Method.Name, Array.Empty<object>());
        protected void ClientRpc<T1>(Action<T1> method, T1 a1) => SendRpc(RpcKind.Client, method.Method.Name, new object[] { a1 });
        protected void ClientRpc<T1, T2>(Action<T1, T2> method, T1 a1, T2 a2) => SendRpc(RpcKind.Client, method.Method.Name, new object[] { a1, a2 });
        protected void ClientRpc<T1, T2, T3>(Action<T1, T2, T3> method, T1 a1, T2 a2, T3 a3) => SendRpc(RpcKind.Client, method.Method.Name, new object[] { a1, a2, a3 });
        protected void ClientRpc<T1, T2, T3, T4>(Action<T1, T2, T3, T4> method, T1 a1, T2 a2, T3 a3, T4 a4) => SendRpc(RpcKind.Client, method.Method.Name, new object[] { a1, a2, a3, a4 });

        /// <summary>A ClientRpc delivered only to the entity's owning client.</summary>
        protected void OwnerRpc(Action method) => SendRpc(RpcKind.Client, method.Method.Name, Array.Empty<object>(), OwnerClientId);
        protected void OwnerRpc<T1>(Action<T1> method, T1 a1) => SendRpc(RpcKind.Client, method.Method.Name, new object[] { a1 }, OwnerClientId);
        protected void OwnerRpc<T1, T2>(Action<T1, T2> method, T1 a1, T2 a2) => SendRpc(RpcKind.Client, method.Method.Name, new object[] { a1, a2 }, OwnerClientId);
        protected void OwnerRpc<T1, T2, T3>(Action<T1, T2, T3> method, T1 a1, T2 a2, T3 a3) => SendRpc(RpcKind.Client, method.Method.Name, new object[] { a1, a2, a3 }, OwnerClientId);

        protected void ServerRpc(Action method) => SendRpc(RpcKind.Server, method.Method.Name, Array.Empty<object>());
        protected void ServerRpc<T1>(Action<T1> method, T1 a1) => SendRpc(RpcKind.Server, method.Method.Name, new object[] { a1 });
        protected void ServerRpc<T1, T2>(Action<T1, T2> method, T1 a1, T2 a2) => SendRpc(RpcKind.Server, method.Method.Name, new object[] { a1, a2 });
        protected void ServerRpc<T1, T2, T3>(Action<T1, T2, T3> method, T1 a1, T2 a2, T3 a3) => SendRpc(RpcKind.Server, method.Method.Name, new object[] { a1, a2, a3 });

        protected void AuthorityRpc(Action method) => SendRpc(RpcKind.Authority, method.Method.Name, Array.Empty<object>());
        protected void AuthorityRpc<T1>(Action<T1> method, T1 a1) => SendRpc(RpcKind.Authority, method.Method.Name, new object[] { a1 });
        protected void AuthorityRpc<T1, T2>(Action<T1, T2> method, T1 a1, T2 a2) => SendRpc(RpcKind.Authority, method.Method.Name, new object[] { a1, a2 });
        protected void AuthorityRpc<T1, T2, T3>(Action<T1, T2, T3> method, T1 a1, T2 a2, T3 a3) => SendRpc(RpcKind.Authority, method.Method.Name, new object[] { a1, a2, a3 });
        protected void AuthorityRpc<T1, T2, T3, T4>(Action<T1, T2, T3, T4> method, T1 a1, T2 a2, T3 a3, T4 a4) => SendRpc(RpcKind.Authority, method.Method.Name, new object[] { a1, a2, a3, a4 });
    }
}
