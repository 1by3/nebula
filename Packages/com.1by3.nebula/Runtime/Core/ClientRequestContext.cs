using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Who sent a <see cref="ClientRequestRpcAttribute">client request</see>, handed to the handler as its first parameter. The
    /// sender fields are stamped by the gateway from the connection and the session: a client cannot choose or change them. The
    /// arguments that follow the context are the client's, and are not to be trusted.
    /// </summary>
    public sealed class ClientRequestContext
    {
        /// <summary>The sender's session id (the id a pawn's <see cref="NetworkIdentity.OwnerClientId"/> carries).</summary>
        public ulong ClientId { get; }
        /// <summary>
        /// The sender's verified identity across sessions (<see cref="PlayerIdentity"/>): the account or provider subject of a
        /// verified sign-in token, or an anonymous identity the mesh issued. Key anything that must outlive a session on this, not on <see cref="ClientId"/>.
        /// </summary>
        public string Identity { get; }
        /// <summary>The claims of the sender's verified token that the mesh forwards (<see cref="NebulaConfig.ForwardedClaims"/>); empty for an anonymous player. As of the sign-in: a role granted since is not here until the player signs in again.</summary>
        public IReadOnlyDictionary<string, string> Claims { get; }
        /// <summary>The entity the request is for. On the worker with authority.</summary>
        public NetworkIdentity Entity { get; }
        /// <summary>The net id of the sender's pawn, or 0 if it has none. The pawn may live on another worker: see <see cref="TryGetSenderPawn"/>.</summary>
        public ulong SenderPawnNetId { get; }
        /// <summary>True when <see cref="SenderPosition"/> is known.</summary>
        public bool HasSenderPosition { get; }
        /// <summary>
        /// Where the gateway last saw the sender's pawn, in the scope's own absolute space: the space of
        /// <see cref="NetworkIdentity.ScopePositionPrecise"/>, so a distance check against <see cref="Entity"/> needs no pawn on this worker.
        /// One interest update old at worst, and the sender can move meanwhile: allow for that in a range check.
        /// </summary>
        public Vector3 SenderPosition { get; }
        /// <summary>The client's id for this request; the same number comes back in its reply.</summary>
        public uint CallId { get; }
        /// <summary>True when the client asked for a reply and will hear <see cref="ReplyCode"/> and the outcome.</summary>
        public bool WantsReply { get; }
        /// <summary>How many workers forwarded the request to reach this one (the entity changed worker in flight).</summary>
        public int Hops { get; }

        /// <summary>
        /// Your own verdict for the client, if it asked for a reply: set it in the handler ("done", "too far", "not yours"). Sent with
        /// <see cref="ClientRequestOutcome.Accepted"/> in <see cref="ClientRequestResult.Code"/>. 0 by default. Ignored when no reply was asked for.
        /// </summary>
        public ushort ReplyCode { get; set; }

        private readonly NebulaWorker _worker;

        internal ClientRequestContext(in ClientRequestRoutedMsg msg, NetworkIdentity entity, NebulaWorker worker)
        {
            _worker = worker;
            ClientId = msg.ClientId;
            Identity = msg.Identity ?? "";
            Claims = msg.Claims ?? PlayerClaims.Empty;
            Entity = entity;
            SenderPawnNetId = msg.PawnNetId;
            HasSenderPosition = msg.HasPawnPosition;
            SenderPosition = msg.PawnPosition;
            CallId = msg.CallId;
            WantsReply = msg.WantsReply;
            Hops = msg.Hops;
        }

        /// <summary>
        /// The sender's pawn, when this worker holds a copy of it (authoritative or ghost). Often it does not: the pawn is wherever its
        /// player is, which may be another worker than the entity's. Use <see cref="SenderPosition"/> for a range check.
        /// </summary>
        public bool TryGetSenderPawn(out NetworkIdentity pawn)
        {
            pawn = null;
            if (SenderPawnNetId == 0 || _worker == null) return false;
            pawn = _worker.Find(SenderPawnNetId);
            return pawn != null;
        }
    }
}
