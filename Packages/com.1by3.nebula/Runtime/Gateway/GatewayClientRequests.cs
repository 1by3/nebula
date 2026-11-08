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
    /// Client requests at the gateway (<c>docs/client-requests.md</c>): a <see cref="MsgId.ClientRequest"/> from a client is
    /// admitted (size, existence, interest, rate: <see cref="ClientRequestGate"/>), stamped with who the gateway knows the
    /// client to be, and sent to the worker that owns the entity, wherever that is. A request that is dropped is logged with
    /// its reason, and the client is told when it asked for a reply.
    /// </summary>
    public sealed partial class NebulaGateway
    {
        private ClientRequestGate _requestGate;
        private double _nextRequestSweep;

        /// <summary>The admission rules and their counters (<see cref="ClientRequestGate.Admitted"/>, <see cref="ClientRequestGate.Refused"/>).</summary>
        public ClientRequestGate RequestGate => _requestGate ??= Config != null ? ClientRequestGate.From(Config) : new ClientRequestGate(1024, 10f, 20, 40f, 80);

        /// <summary>Client requests this gateway has sent on to a worker since it started.</summary>
        public long ClientRequestsRouted { get; private set; }

        private void OnClientRequest(ClientConn c, ClientRequestMsg msg)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= _nextRequestSweep)
            {
                _nextRequestSweep = now + 10;
                RequestGate.Sweep(now);
            }
            bool known = _entities.TryGetValue(msg.NetId, out var rec);
            bool inInterest = known && c.Visible.Contains(msg.NetId);
            var verdict = RequestGate.Admit(msg.Args?.Length ?? 0, known, inInterest, ref c.RequestBucket, msg.NetId, now);
            if (verdict == ClientRequestOutcome.Accepted)
            {
                if (!_workersByIndex.TryGetValue(rec.OwnerWorkerIndex, out var worker) || !worker.Ready) verdict = ClientRequestOutcome.RejectedUnreachable;
                else
                {
                    var routed = new ClientRequestRoutedMsg
                    {
                        // The stamp comes from the connection and the session, never from the message.
                        ClientId = c.ClientId,
                        Identity = c.Identity,
                        Claims = c.Claims,
                        PawnNetId = c.PawnNetId,
                        CallId = msg.CallId,
                        Flags = msg.Flags,
                        Hops = 0,
                        NetId = msg.NetId,
                        BehaviourIndex = msg.BehaviourIndex,
                        MethodHash = msg.MethodHash,
                        Args = msg.Args ?? Array.Empty<byte>(),
                    };
                    if (TryGetPawnPosition(c, out var pawnPosition)) { routed.HasPawnPosition = true; routed.PawnPosition = pawnPosition; }
                    _writer.Reset();
                    routed.Write(_writer);
                    Send(worker.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
                    // A person is waiting on this: do not hold it for the end of the loop.
                    if (FlushRpcsImmediately) _transport.Flush();
                    ClientRequestsRouted++;
                    return;
                }
            }
            LogDroppedRequest(c, msg, verdict);
            if (msg.WantsReply) ReplyToClientRequest(c, msg.CallId, verdict, 0);
        }

        /// <summary>
        /// A worker's answer to a request: passed to the client it names, if this gateway holds that client. A worker that
        /// does not know which gateway the client is on sends it to every gateway it is linked to, so most gateways drop it here.
        /// </summary>
        private void OnClientRequestReply(WorkerConn w, ClientRequestReplyMsg msg)
        {
            if (!_clientsById.TryGetValue(msg.ClientId, out var c) || !c.Welcomed) return;
            ReplyToClientRequest(c, msg.CallId, msg.Outcome, msg.Code);
        }

        private void ReplyToClientRequest(ClientConn c, uint callId, ClientRequestOutcome outcome, ushort code)
        {
            _writer.Reset();
            new ClientRequestReplyMsg { ClientId = c.ClientId, CallId = callId, Outcome = outcome, Code = code }.Write(_writer);
            AppendReliable(c, _writer.ToSegment());
            if (FlushRpcsImmediately)
            {
                FlushReliable(c);
                _transport.Flush();
            }
        }

        // One line a second per client at most: a client that floods requests must not also flood the log.
        private void LogDroppedRequest(ClientConn c, ClientRequestMsg msg, ClientRequestOutcome why)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < c.NextRequestLog) return;
            c.NextRequestLog = now + 1;
            NebulaLog.Warn($"client request dropped: {why} (client {c.ClientId} '{c.Name}', entity #{msg.NetId}, behaviour {msg.BehaviourIndex}, method {msg.MethodHash:x}, {msg.Args?.Length ?? 0} bytes; at most one line a second per client)");
        }
    }
}
