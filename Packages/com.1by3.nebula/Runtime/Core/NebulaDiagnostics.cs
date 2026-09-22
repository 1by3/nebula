namespace Nebula
{
    /// <summary>
    /// Process-wide counters for rule violations Nebula rejects at the call site, so tests and the worker's
    /// <c>profile</c> log line can see how often game code hits them.
    /// </summary>
    public static class NebulaDiagnostics
    {
        /// <summary>
        /// How many <c>AuthorityRpc</c> sends this process discarded because the caller held neither an
        /// authoritative nor a ghost copy of the entity (a client, an unspawned entity). Each one is also logged
        /// through <see cref="NebulaLog.Warn"/>. <see cref="NebulaWorker.RejectedAuthorityRpcSends"/> reads it.
        /// </summary>
        public static int RejectedAuthorityRpcSends { get; internal set; }

        internal static void ResetForNewSession() => RejectedAuthorityRpcSends = 0;
    }
}
