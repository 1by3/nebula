using UnityEngine;

namespace Nebula
{
    /// <summary>Who is the source of truth for a synced component (NGO's <c>AuthorityModes</c>).</summary>
    public enum AuthorityMode : byte
    {
        /// <summary>The authoritative worker writes the state; every client (the owner included) follows it.</summary>
        Server = 0,
        /// <summary>
        /// The owning client writes the state and sends it to the worker, which applies it and re-broadcasts it to
        /// everyone else (NGO's <c>ClientNetworkTransform</c>/<c>OwnerNetworkAnimator</c>). The worker still hands the
        /// entity over between containers as usual; the owner's stream simply follows it to the new worker. Entities
        /// with no owning client (NPCs) fall back to server authority.
        /// </summary>
        Owner = 1,
    }

    /// <summary>
    /// Base for components that replicate through the per-tick sync channel and support NGO's server/owner authority
    /// choice: <see cref="NetworkTransform"/> and <see cref="NetworkAnimator"/>. Derived classes detect changes in
    /// <see cref="AuthorityTick"/> (called on whichever copy is the source of truth, once per tick) and implement the
    /// write/read pair; this class decides who is authoritative and pumps the owner -> worker leg over a ServerRpc.
    /// </summary>
    public abstract class NetworkSyncBehaviour : NetworkBehaviour
    {
        [Tooltip("Server: the authoritative worker drives this component. Owner: the owning client drives it and the worker relays.")]
        [SerializeField] private AuthorityMode _authority = AuthorityMode.Server;

        private float _ownerSendAccumulator;
        private uint _ownerSends;
        private static readonly NetworkWriter OwnerWriter = new NetworkWriter(512);

        public AuthorityMode Authority
        {
            get => _authority;
            set => _authority = value;
        }

        public sealed override bool HasSyncState => true;

        /// <summary>Whether owner authority is in effect (the mode is Owner and a client owns the entity).</summary>
        public bool IsOwnerAuthoritative => _authority == AuthorityMode.Owner && OwnerClientId != 0;

        /// <summary>This copy is the source of truth for the synced state right now.</summary>
        public bool IsSyncAuthority => IsOwnerAuthoritative ? IsOwner : HasAuthority;

        /// <summary>This worker holds the entity but the owner drives the state: apply what arrives and re-broadcast it.</summary>
        protected bool IsRelayingWorker => IsOwnerAuthoritative && HasAuthority;

        /// <summary>Once per tick on the source of truth: compare against what was last sent and <see cref="NetworkBehaviour.MarkSyncDirty"/> as needed.</summary>
        protected abstract void AuthorityTick(uint tick, float deltaTime);

        /// <summary>
        /// Worker side of owner authority: an owner chunk just arrived. Apply it to the local object immediately (the
        /// worker is the simulation; nothing to interpolate) and queue the same deltas for re-broadcast. Default: the
        /// ordinary <see cref="NetworkBehaviour.ReadSyncState"/> at the current tick.
        /// </summary>
        protected virtual void ApplyOwnerState(NetworkReader reader, bool full)
        {
            ReadSyncState(reader, NetworkTime.Tick, full);
        }

        public override void NetworkTick(uint tick, float deltaTime)
        {
            if (IsSyncAuthority) AuthorityTick(tick, deltaTime);
        }

        protected virtual void Update()
        {
            if (!IsSpawned || !IsClient || !IsOwnerAuthoritative || !IsOwner) return;
            _ownerSendAccumulator += Time.unscaledDeltaTime;
            if (_ownerSendAccumulator < NetworkTime.TickInterval) return;
            // One tick's worth per interval; a long frame catches up on the next ones rather than bursting.
            _ownerSendAccumulator = Mathf.Min(_ownerSendAccumulator - NetworkTime.TickInterval, NetworkTime.TickInterval);
            AuthorityTick(NetworkTime.Tick, NetworkTime.TickInterval);
            if (!SyncDirty) return;
            bool full = _ownerSends == 0 || _ownerSends % NetworkIdentity.SyncKeyframeInterval == 0;
            _ownerSends++;
            OwnerWriter.Reset();
            WriteSyncState(OwnerWriter, full);
            ServerRpc(RpcOwnerSyncState, OwnerWriter.ToArray(), full);
            SyncDirty = false;
            OnSyncStateSent();
        }

        [ServerRpc]
        private void RpcOwnerSyncState(byte[] chunk, bool full)
        {
            if (!IsRelayingWorker) return; // mode changed, or an NPC: the worker is the authority and ignores clients
            var reader = new NetworkReader(chunk);
            ApplyOwnerState(reader, full);
            MarkSyncDirty();
        }

        public override void OnGainedAuthority()
        {
            _ownerSends = 0;
        }
    }
}
