using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The client's half of <c>[ClientRequestRpc]</c> (<c>docs/client-requests.md</c>): requests go to the gateway as one
    /// <see cref="MsgId.ClientRequest"/> and a reply, when asked for, comes back as a <see cref="MsgId.ClientRequestReply"/>.
    /// </summary>
    public sealed partial class NebulaClient
    {
        private readonly ClientRequestTracker _requests = new ClientRequestTracker();

        /// <summary>Requests sent with a reply wanted that have not been answered yet.</summary>
        public int PendingClientRequests => _requests.PendingCount;
        /// <summary>Client requests sent since this client started.</summary>
        public long ClientRequestsSent { get; private set; }

        uint IRpcSink.SendClientRequest(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<ClientRequestResult> onDone, float timeoutSeconds)
        {
            if (ConnectionState != State.InGame)
            {
                onDone?.Invoke(new ClientRequestResult(0, ClientRequestOutcome.Disconnected));
                return 0;
            }
            var arr = new byte[args.Count];
            Buffer.BlockCopy(args.Array, args.Offset, arr, 0, args.Count);
            uint callId = onDone != null ? _requests.Mint() : 0;
            if (onDone != null) _requests.Track(callId, Time.unscaledTimeAsDouble + Math.Max(0.1f, timeoutSeconds), onDone);
            _writer.Reset();
            new ClientRequestMsg
            {
                CallId = callId,
                Flags = onDone != null ? ClientRequestFlags.WantsReply : ClientRequestFlags.None,
                NetId = identity.NetId,
                BehaviourIndex = behaviourIndex,
                MethodHash = methodHash,
                Args = arr,
            }.Write(_writer);
            _transport.Send(_gatewayPeer, Delivery.ReliableOrdered, _writer.ToSegment());
            ClientRequestsSent++;
            return callId;
        }

        private void OnClientRequestReply(ClientRequestReplyMsg msg)
        {
            if (!_requests.Complete(msg.CallId, msg.ToResult()))
                NebulaLog.Debugf($"late reply to client request {msg.CallId} ({msg.Outcome}); it had already settled");
        }

        /// <summary>Settle the requests that wait for a reply that cannot come any more.</summary>
        private void FailClientRequests() => _requests.FailAll(ClientRequestOutcome.Disconnected);
    }
}
