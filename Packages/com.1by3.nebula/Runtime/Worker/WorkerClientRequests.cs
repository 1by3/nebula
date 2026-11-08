using System;

namespace Nebula
{
    /// <summary>
    /// The worker's half of client requests (<c>docs/client-requests.md</c>): a <see cref="MsgId.ClientRequestRouted"/> is applied here
    /// when this worker has authority over the entity, passed on when the entity has moved, and rejected with a reason when it cannot
    /// be either. The handler runs once, with the sender the gateway stamped; whatever it does is its own business and its own checks.
    /// </summary>
    public sealed partial class NebulaWorker
    {
        /// <summary>Client requests this worker applied (ran a handler for) since it started.</summary>
        public long ClientRequestsApplied { get; private set; }
        /// <summary>Client requests this worker passed on to the entity's new owner since it started.</summary>
        public long ClientRequestsForwarded { get; private set; }
        /// <summary>Client requests this worker rejected since it started, by outcome (index = <see cref="ClientRequestOutcome"/>).</summary>
        public long[] ClientRequestsRejected { get; } = new long[16];

        private void OnClientRequestRouted(Peer from, ClientRequestRoutedMsg msg)
        {
            // Only the mesh stamps a request: the gateway that holds the client, or a worker passing one on. A client's own bytes never get here.
            if (from.Role != PeerRole.Gateway && from.Role != PeerRole.Worker) return;
            // Defence in depth: the gateway already refused a payload over the limit, and this worker does not rely on it.
            int maxBytes = Math.Max(1, Config != null ? Config.ClientRequestMaxBytes : 1024);
            if ((msg.Args?.Length ?? 0) > maxBytes) { RejectClientRequest(from, msg, ClientRequestOutcome.RejectedTooLarge, null); return; }

            var e = Find(msg.NetId);
            Peer next = null;
            if (e != null && !e.HasAuthority) next = ForwardPeerFor(e, from);
            var decision = ClientRequestRouting.Decide(e != null, e != null && e.HasAuthority, next != null, msg.Hops, Config != null ? Config.AuthorityCallMaxHops : 3);
            switch (decision.Action)
            {
                case AuthorityCallAction.Apply:
                    ApplyClientRequest(from, msg, e);
                    break;
                case AuthorityCallAction.Forward:
                    msg.Hops++;
                    _writer.Reset();
                    msg.Write(_writer);
                    Send(next, Delivery.ReliableOrdered);
                    ClientRequestsForwarded++;
                    break;
                default:
                    RejectClientRequest(from, msg, ClientRequestRouting.OutcomeOf(decision.Outcome), e);
                    break;
            }
        }

        private void ApplyClientRequest(Peer from, ClientRequestRoutedMsg msg, NetworkIdentity e)
        {
            ClientRequestsApplied++;
            if (msg.BehaviourIndex >= e.Behaviours.Length)
            {
                RejectClientRequest(from, msg, ClientRequestOutcome.RejectedNoHandler, e);
                return;
            }
            var context = new ClientRequestContext(msg, e, this);
            _reader.Set(new ArraySegment<byte>(msg.Args ?? Array.Empty<byte>()));
            var outcome = RpcRegistry.InvokeClientRequest(e.Behaviours[msg.BehaviourIndex], msg.MethodHash, _reader, context);
            if (outcome != ClientRequestOutcome.Accepted)
            {
                ClientRequestsApplied--;
                RejectClientRequest(from, msg, outcome, e);
                return;
            }
            if (msg.WantsReply) ReplyToClientRequest(from, msg, ClientRequestOutcome.Accepted, context.ReplyCode);
        }

        private void RejectClientRequest(Peer from, ClientRequestRoutedMsg msg, ClientRequestOutcome why, NetworkIdentity e)
        {
            ClientRequestsRejected[(int)why]++;
            NebulaLog.Warn($"client request from client {msg.ClientId} for #{msg.NetId} rejected: {why} (behaviour {msg.BehaviourIndex}, method {msg.MethodHash:x}, {msg.Hops} hops, entity {(e != null ? e.ToString() : "unknown here")})");
            if (msg.WantsReply) ReplyToClientRequest(from, msg, why, 0);
        }

        /// <summary>
        /// The answer goes back to the gateway the request came through. A request a worker passed on arrives from a worker, which says
        /// nothing about the client's gateway, so it goes to the one this worker knows holds the session if it knows one, else to every
        /// linked gateway: only the one that holds the client passes it on.
        /// </summary>
        private void ReplyToClientRequest(Peer from, ClientRequestRoutedMsg msg, ClientRequestOutcome outcome, ushort code)
        {
            _writer.Reset();
            new ClientRequestReplyMsg { ClientId = msg.ClientId, CallId = msg.CallId, Outcome = outcome, Code = code }.Write(_writer);
            if (from.Role == PeerRole.Gateway) { Send(from, Delivery.ReliableOrdered); return; }
            var session = SessionGatewayOf(msg.ClientId);
            if (session != null) { Send(session, Delivery.ReliableOrdered); return; }
            for (int i = 0; i < _gateways.Count; i++) Send(_gateways[i], Delivery.ReliableOrdered);
        }
    }
}
