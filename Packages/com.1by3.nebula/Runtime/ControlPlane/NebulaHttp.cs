using System;
using System.Net;
using System.Net.Http;

namespace Nebula
{
    /// <summary>
    /// <c>HttpClient</c> requests from a worker or gateway to the orchestrator: the persistence client
    /// (<see cref="RemotePersistenceStore"/>), the worker's telemetry and a gateway's session requests all reach the
    /// same dashboard address. (The control-plane mirror's long poll and heartbeats use their own connections,
    /// through <see cref="BlockingHttpClient"/>, so they do not count against this limit.)
    /// <para>
    /// Under Unity's Mono runtime every request to one address shares one <c>ServicePoint</c>, whose connection limit
    /// defaults to two for the whole process, so checkpoints, restores and telemetry would queue behind one
    /// another. Every request built here
    /// raises that address's limit to <see cref="ConnectionsPerEndpoint"/> first, on every request rather than once, so
    /// the limit holds even if the runtime replaces a service point that sat idle. The limit is only headroom: the clients
    /// themselves bound how many requests they have in flight, so the process never opens more than a handful of
    /// connections. On .NET the handler has no such limit and this does nothing.
    /// </para>
    /// </summary>
    internal static class NebulaHttp
    {
        /// <summary>
        /// Connections one process may hold open to one orchestrator address through <c>HttpClient</c>: the persistence
        /// sender and readers, telemetry, and a gateway's session requests, with room to spare.
        /// </summary>
        public const int ConnectionsPerEndpoint = 32;

        /// <summary>A request to <paramref name="url"/> that presents <paramref name="token"/> (the mesh token) when there is one.</summary>
        public static HttpRequestMessage Request(HttpMethod method, string url, string token)
        {
            var request = new HttpRequestMessage(method, url);
            if (token != null) request.Headers.TryAddWithoutValidation(ControlPlaneHost.TokenHeader, token);
            AllowConnections(request.RequestUri);
            return request;
        }

        /// <summary>Raise the connection limit of <paramref name="uri"/>'s service point to <see cref="ConnectionsPerEndpoint"/>.</summary>
        public static void AllowConnections(Uri uri)
        {
#if !NEBULA_SERVICE
            if (uri == null || !uri.IsAbsoluteUri) return;
            try
            {
#pragma warning disable CS0618, SYSLIB0014 // obsolete on .NET, where it is not needed; it is what Mono's HttpClient obeys
                var point = ServicePointManager.FindServicePoint(uri);
                if (point.ConnectionLimit < ConnectionsPerEndpoint) point.ConnectionLimit = ConnectionsPerEndpoint;
#pragma warning restore CS0618, SYSLIB0014
            }
            catch (Exception) { /* a platform without service points: nothing to raise */ }
#endif
        }
    }
}
