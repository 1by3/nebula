using System;

namespace Nebula
{
    /// <summary>Where RPCs and state changes go once a behaviour has produced them. Implemented by the worker and the client.</summary>
    public interface IRpcSink
    {
        void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius);
        /// <summary>
        /// As <see cref="SendClientRpc(NetworkIdentity, byte, uint, ArraySegment{byte}, ulong, float)"/>, on the given
        /// delivery class (<see cref="ClientRpcAttribute.Delivery"/>). The default forwards to the reliable overload, so
        /// a sink written before delivery classes existed keeps compiling and sends everything reliable.
        /// </summary>
        void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius, Delivery delivery)
            => SendClientRpc(identity, behaviourIndex, methodHash, args, targetClientId, radius);
        void SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args);
        void SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args);
        /// <summary>
        /// As <see cref="SendAuthorityRpc(NetworkIdentity, byte, uint, ArraySegment{byte})"/>, with the outcome
        /// reported to <paramref name="onDone"/> exactly once: the reply from the worker that decided the call, or
        /// <see cref="AuthorityCallOutcome.TimedOut"/> after <paramref name="timeoutSeconds"/>. Returns the call id,
        /// or 0 when nothing was sent (the callback still runs, with the reason).
        /// </summary>
        ulong SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<AuthorityCallResult> onDone, float timeoutSeconds);
        /// <summary>
        /// Send a <c>[ClientRequestRpc]</c> from a client to the worker with authority over <paramref name="identity"/>, through
        /// its gateway. With <paramref name="onDone"/> the request asks for a reply and the outcome is reported exactly once: the
        /// reply, or <see cref="ClientRequestOutcome.TimedOut"/> after <paramref name="timeoutSeconds"/>, or
        /// <see cref="ClientRequestOutcome.Disconnected"/>. Returns the request id, or 0 when nothing was sent (the callback still runs,
        /// with the reason). The default is for a sink that is not a client: it refuses.
        /// </summary>
        uint SendClientRequest(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<ClientRequestResult> onDone, float timeoutSeconds)
        {
            NebulaLog.Warn("ClientRequest can only be sent from a client; ignored");
            onDone?.Invoke(new ClientRequestResult(0, ClientRequestOutcome.Disconnected));
            return 0;
        }
    }

    /// <summary>Process-wide role context. A process is either a worker (server) or a client, never both.</summary>
    public static class NebulaRuntime
    {
        public static bool IsServer { get; internal set; }
        public static bool IsClient { get; internal set; }
        public static ulong LocalClientId { get; internal set; }
        /// <summary>Client side: the local player's identity across sessions (<see cref="PlayerIdentity"/>), once welcomed. Empty before that and on workers.</summary>
        public static string LocalIdentity { get; internal set; } = "";
        public static string LocalWorkerId { get; internal set; } = "";
        public static ushort LocalWorkerIndex { get; internal set; }
        public static IRpcSink RpcSink { get; internal set; }
        public static NebulaConfig Config { get; internal set; }

        internal static void Reset()
        {
            IsServer = false;
            IsClient = false;
            LocalClientId = 0;
            LocalIdentity = "";
            LocalWorkerId = "";
            LocalWorkerIndex = 0;
            RpcSink = null;
            StateHistory.ResetWindow();
            SimulationBatches.Reset();
        }
    }
}
