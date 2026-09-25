using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Base class for replicated gameplay components. Use <see cref="IsServer"/>, <see cref="IsClient"/>, and
    /// <see cref="IsOwner"/> to inspect the current role. Use <see cref="HasAuthority"/> to check whether this
    /// worker controls the entity, and <see cref="IsGhost"/> to check whether it holds a non-authoritative copy.
    /// Override the lifecycle methods to respond to spawn, despawn, and authority changes.
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
        public ulong OwnerClientId => Identity != null ? Identity.OwnerClientId : 0;
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

        // ---- persistent state ---------------------------------------------------------------------------
        //
        // State that outlives the process: written into the entity's record whenever the worker checkpoints it (see
        // PersistentEntity and NebulaPersistence) and read back when the entity is restored, possibly on another
        // worker, in another run, from another build. [Persist] NetworkVariables are saved without any of this; the
        // pair below is for whatever else a behaviour needs in order to come back the same (a timer, an inventory).

        /// <summary>
        /// Called while the worker builds this entity's persistence record. Write what the entity needs to come back
        /// as it was and that no <see cref="PersistAttribute"/> variable already covers. Must be mirrored exactly by
        /// <see cref="ReadPersistentState"/>. Writing nothing (the default) costs nothing: an entry is only added when
        /// bytes were written.
        /// <para>
        /// Unlike <see cref="WriteHandoverState"/>, this is read back by a later run and possibly a later build, so
        /// treat it as a save format and version it yourself if you expect it to change.
        /// </para>
        /// </summary>
        public virtual void WritePersistentState(NetworkWriter writer) { }

        /// <summary>
        /// Called on a restored entity before it is spawned, with exactly what <see cref="WritePersistentState"/>
        /// wrote. The reader is bounded to this behaviour's own chunk, so reading too much fails here (and is logged)
        /// instead of corrupting the rest of the record.
        /// </summary>
        public virtual void ReadPersistentState(NetworkReader reader) { }

        /// <summary>
        /// Ask for a checkpoint soon: something worth saving changed that Nebula cannot see by itself (state written
        /// by <see cref="WritePersistentState"/>). A no-op on an entity with no <see cref="PersistentEntity"/>.
        /// </summary>
        protected void MarkPersistDirty() => Identity?.Persistent?.MarkDirty();

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

        // ---- sync audience ------------------------------------------------------------------------------
        //
        // Which clients receive this behaviour's sync state (docs/sync-audience.md). Workers holding a ghost always
        // receive it; the audience only narrows the clients. Filtering happens at the gateway, on every path a chunk
        // can take to a client: the spawn, deltas, keyframes and the gateway's cached keyframes for late joiners.

        /// <summary>The audience this behavior was spawned with, read once from <see cref="SyncAudience"/> when the identity starts.</summary>
        internal SyncAudience Audience;
        /// <summary>Custom only: the sorted client ids the authority last chose (or that arrived with the entity).</summary>
        internal ulong[] AudienceMembers = Array.Empty<ulong>();
        /// <summary>Custom only: evaluate the audience at the next tick instead of waiting for the refresh.</summary>
        internal bool AudienceDirty = true;
        /// <summary>Custom only: the tick at or after which the audience is evaluated again.</summary>
        internal uint AudienceNextTick;
        /// <summary>The audience changed or its owner came back: the next chunk of this behavior is a keyframe for every destination.</summary>
        internal bool AudienceKeyframe;
        /// <summary>The member cap has been reported for this behavior.</summary>
        internal bool AudienceCapWarned;
        /// <summary>
        /// Client side: the gateway said this client left the audience (<see cref="OnSyncStateCleared"/>) at this
        /// tick. Chunks in sequenced packets no newer than it were sent before the leave, and are dropped.
        /// </summary>
        internal bool SyncCleared;
        internal uint SyncClearedTick;

        /// <summary>
        /// Which clients receive this behavior's sync state (<see cref="WriteSyncState"/>). The default,
        /// <c>SyncAudience.Everyone</c>, costs nothing. A worker that holds a ghost of the entity
        /// receives the state whatever the audience; only clients are filtered. Override it with a constant: Nebula
        /// reads it once, when the entity is spawned or instantiated, so it is configuration and not state that can
        /// change at run time. An entity's root <see cref="NetworkTransform"/> always replicates to everyone.
        /// <para>
        /// <see cref="NetworkVariable{T}"/>s are not covered: an entity's variables go to every client that holds it.
        /// Keep state a client must not see in sync state with a restricted audience.
        /// </para>
        /// </summary>
        public virtual SyncAudience SyncAudience => SyncAudience.Everyone;

        /// <summary>
        /// For the <c>Custom</c> audience only, on the authoritative worker: whether the client with session ID
        /// <paramref name="clientId"/> should receive this behavior's sync state. <paramref name="pawn"/> is that client's
        /// player entity as this worker holds it (simulated here or a ghost), so a rule can measure the distance to it.
        /// Nebula asks about every client whose pawn this worker holds: when the entity gains authority, every
        /// <see cref="SyncAudienceRefreshTicks"/> ticks, and at the next tick after <see cref="MarkSyncAudienceDirty"/>.
        /// A client whose pawn this worker does not hold is not asked, and is not in the audience. Keep it cheap and
        /// free of side effects. By default it returns false for every client.
        /// </summary>
        /// <param name="clientId">The client's session ID. It stays the same when the client reconnects (<see cref="NetworkIdentity.OwnerClientId"/>).</param>
        /// <param name="pawn">That client's player entity on this worker.</param>
        protected virtual bool IsInSyncAudience(ulong clientId, NetworkIdentity pawn) => false;

        /// <summary>
        /// For the <c>Custom</c> audience only: how often, in ticks, the authority evaluates the audience again
        /// without being asked. The default is 30, half a second at the 60 Hz tick rate. Return 0 to evaluate only when
        /// the entity gains authority and after <see cref="MarkSyncAudienceDirty"/>. That suits a rule that changes
        /// only on events, such as a player opening or closing a container.
        /// </summary>
        public virtual uint SyncAudienceRefreshTicks => 30;

        /// <summary>
        /// For the <c>Custom</c> audience only, on the authority: the answer of <see cref="IsInSyncAudience"/>
        /// may have changed, so evaluate it at the next tick instead of waiting for the refresh. Calling it often is
        /// cheap: the audience is evaluated at most once per tick.
        /// </summary>
        protected void MarkSyncAudienceDirty() => AudienceDirty = true;

        /// <summary>
        /// Client side: this client has just left the behavior's audience. For example, it no longer owns the entity,
        /// or a Custom rule stopped choosing it. None of the behavior's sync state arrives until the client joins
        /// again, and then a keyframe arrives through <see cref="ReadSyncState"/>. Drop what you hold of that state and
        /// clear any UI that shows it. Not called when the entity itself leaves the client (see <see cref="OnNetworkDespawn"/>).
        /// </summary>
        public virtual void OnSyncStateCleared() { }

        /// <summary>Custom only: ask the game's predicate about one client, keeping any exception inside this behavior.</summary>
        internal bool EvaluateAudienceMember(ulong clientId, NetworkIdentity pawn)
        {
            try { return IsInSyncAudience(clientId, pawn); }
            catch (Exception ex)
            {
                NebulaLog.Error($"IsInSyncAudience on {GetType().Name} of {(Identity != null ? Identity.name : name)} threw: {ex.Message}");
                return false;
            }
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

        private void SendRpc(RpcKind kind, string methodName, object[] args, ulong targetClientId = 0)
        {
            var m = RpcRegistry.Require(GetType(), methodName);
            if (m.Kind != kind)
                throw new InvalidOperationException($"{GetType().Name}.{methodName} is a {m.Kind} RPC, sent as {kind}");
            if (!IsSpawned)
            {
                if (kind == RpcKind.Authority) NebulaDiagnostics.RejectedAuthorityRpcSends++;
                NebulaLog.Warn($"RPC {methodName} on unspawned {GetType().Name} ignored");
                return;
            }
            // An AuthorityRpc may only leave a worker that simulates the target or holds a ghost of it. On a worker
            // every spawned copy is one or the other, so this catches a client (or a misconfigured process) calling
            // it. Checked before the sink so the rule holds without one, and counted for the profile line and tests.
            if (kind == RpcKind.Authority && !HasAuthority && !IsGhost)
            {
                NebulaDiagnostics.RejectedAuthorityRpcSends++;
                NebulaLog.Warn($"AuthorityRpc {methodName} on {GetType().Name} {NetId}: this process holds neither an authoritative nor a ghost copy of the entity, so it cannot address the entity's worker; discarded. Send AuthorityRpc from a worker that simulates or ghosts the target (https://nebula.1by3.co/docs/concepts/distributed-physics)");
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
                    sink.SendClientRpc(Identity, BehaviourIndex, m.Hash, payload, targetClientId, targetClientId == 0 ? m.Radius : 0f);
                    break;
                case RpcKind.Server:
                    if (!IsClient) { NebulaLog.Warn($"ServerRpc {methodName} can only be sent from a client"); return; }
                    if (!IsOwner) { NebulaLog.Warn($"ServerRpc {methodName} requires ownership of {NetId}"); return; }
                    sink.SendServerRpc(Identity, BehaviourIndex, m.Hash, payload);
                    break;
                case RpcKind.Authority:
                    // IsServer is implied: HasAuthority || IsGhost was checked above and both require a worker.
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

        /// <summary>
        /// The reply-requesting form of the AuthorityRpc path. Same checks as <see cref="SendRpc"/>; a call that is
        /// refused before it leaves this process still settles <paramref name="onDone"/>, so a caller always hears
        /// exactly once.
        /// </summary>
        private ulong SendAuthorityRpcWithReply(string methodName, object[] args, Action<AuthorityCallResult> onDone, float timeoutSeconds)
        {
            if (onDone == null) throw new ArgumentNullException(nameof(onDone));
            var m = RpcRegistry.Require(GetType(), methodName);
            if (m.Kind != RpcKind.Authority)
                throw new InvalidOperationException($"{GetType().Name}.{methodName} is a {m.Kind} RPC, sent as {RpcKind.Authority}");
            if (!IsSpawned)
            {
                NebulaDiagnostics.RejectedAuthorityRpcSends++;
                NebulaLog.Warn($"RPC {methodName} on unspawned {GetType().Name} ignored");
                onDone(new AuthorityCallResult(0, AuthorityCallOutcome.RejectedUnreachable, 0, 0));
                return 0;
            }
            // The same rule as the fire-and-forget path: only a worker that simulates or ghosts the target may call.
            if (!HasAuthority && !IsGhost)
            {
                NebulaDiagnostics.RejectedAuthorityRpcSends++;
                NebulaLog.Warn($"AuthorityRpc {methodName} on {GetType().Name} {NetId}: this process holds neither an authoritative nor a ghost copy of the entity, so it cannot address the entity's worker; discarded. Send AuthorityRpc from a worker that simulates or ghosts the target (https://nebula.1by3.co/docs/concepts/distributed-physics)");
                onDone(new AuthorityCallResult(0, AuthorityCallOutcome.RejectedUnreachable, 0, 0));
                return 0;
            }
            RpcWriter.Reset();
            RpcRegistry.WriteArgs(RpcWriter, m, args);
            var payload = RpcWriter.ToSegment();
            if (HasAuthority)
            {
                // We are the authority: run it right here, once. No call id is minted for a call that never
                // reaches the wire.
                RpcRegistry.Invoke(this, m.Hash, new NetworkReader(payload));
                onDone(new AuthorityCallResult(0, AuthorityCallOutcome.Accepted, Identity.Epoch, 0));
                return 0;
            }
            var sink = NebulaRuntime.RpcSink;
            if (sink == null)
            {
                onDone(new AuthorityCallResult(0, AuthorityCallOutcome.RejectedUnreachable, 0, 0));
                return 0;
            }
            return sink.SendAuthorityRpc(Identity, BehaviourIndex, m.Hash, payload, onDone, timeoutSeconds);
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

        /// <summary>The default wait for an <c>AuthorityRpcWithReply</c> outcome before it settles as <see cref="AuthorityCallOutcome.TimedOut"/>.</summary>
        public const float DefaultAuthorityCallTimeoutSeconds = 5f;

        /// <summary>
        /// As <see cref="AuthorityRpc(Action)"/>, and also tells you what became of the call. <paramref name="onDone"/>
        /// runs exactly once, on the worker's main thread: with <see cref="AuthorityCallOutcome.Accepted"/> once the
        /// worker with authority has run the method, with the reason when a worker on the path rejected it, or with
        /// <see cref="AuthorityCallOutcome.TimedOut"/> when no answer arrived within <paramref name="timeoutSeconds"/>.
        /// Returns the call id, or 0 when the call was applied locally or refused before it was sent. The delivery
        /// contract is described in the RPC guide.
        /// </summary>
        protected ulong AuthorityRpcWithReply(Action method, Action<AuthorityCallResult> onDone, float timeoutSeconds = DefaultAuthorityCallTimeoutSeconds) =>
            SendAuthorityRpcWithReply(method.Method.Name, Array.Empty<object>(), onDone, timeoutSeconds);
        /// <inheritdoc cref="AuthorityRpcWithReply(Action, Action{AuthorityCallResult}, float)"/>
        protected ulong AuthorityRpcWithReply<T1>(Action<T1> method, T1 a1, Action<AuthorityCallResult> onDone, float timeoutSeconds = DefaultAuthorityCallTimeoutSeconds) =>
            SendAuthorityRpcWithReply(method.Method.Name, new object[] { a1 }, onDone, timeoutSeconds);
        /// <inheritdoc cref="AuthorityRpcWithReply(Action, Action{AuthorityCallResult}, float)"/>
        protected ulong AuthorityRpcWithReply<T1, T2>(Action<T1, T2> method, T1 a1, T2 a2, Action<AuthorityCallResult> onDone, float timeoutSeconds = DefaultAuthorityCallTimeoutSeconds) =>
            SendAuthorityRpcWithReply(method.Method.Name, new object[] { a1, a2 }, onDone, timeoutSeconds);
        /// <inheritdoc cref="AuthorityRpcWithReply(Action, Action{AuthorityCallResult}, float)"/>
        protected ulong AuthorityRpcWithReply<T1, T2, T3>(Action<T1, T2, T3> method, T1 a1, T2 a2, T3 a3, Action<AuthorityCallResult> onDone, float timeoutSeconds = DefaultAuthorityCallTimeoutSeconds) =>
            SendAuthorityRpcWithReply(method.Method.Name, new object[] { a1, a2, a3 }, onDone, timeoutSeconds);
        /// <inheritdoc cref="AuthorityRpcWithReply(Action, Action{AuthorityCallResult}, float)"/>
        protected ulong AuthorityRpcWithReply<T1, T2, T3, T4>(Action<T1, T2, T3, T4> method, T1 a1, T2 a2, T3 a3, T4 a4, Action<AuthorityCallResult> onDone, float timeoutSeconds = DefaultAuthorityCallTimeoutSeconds) =>
            SendAuthorityRpcWithReply(method.Method.Name, new object[] { a1, a2, a3, a4 }, onDone, timeoutSeconds);
    }
}
