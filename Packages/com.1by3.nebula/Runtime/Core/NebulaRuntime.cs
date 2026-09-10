using System;

namespace Nebula
{
    /// <summary>Where RPCs and state changes go once a behaviour has produced them. Implemented by the worker and the client.</summary>
    public interface IRpcSink
    {
        void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, uint targetClientId);
        void SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args);
        void SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args);
    }

    /// <summary>Process-wide role context. A process is either a worker (server) or a client, never both.</summary>
    public static class NebulaRuntime
    {
        public static bool IsServer { get; internal set; }
        public static bool IsClient { get; internal set; }
        public static uint LocalClientId { get; internal set; }
        public static string LocalWorkerId { get; internal set; } = "";
        public static ushort LocalWorkerIndex { get; internal set; }
        public static IRpcSink RpcSink { get; internal set; }
        public static NebulaConfig Config { get; internal set; }

        internal static void Reset()
        {
            IsServer = false;
            IsClient = false;
            LocalClientId = 0;
            LocalWorkerId = "";
            LocalWorkerIndex = 0;
            RpcSink = null;
        }
    }
}
