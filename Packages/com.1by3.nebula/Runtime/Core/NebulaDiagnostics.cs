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

        /// <summary>
        /// How many handovers this worker could not carry out as a unit because a member of the entity's
        /// <see cref="NetworkIdentity.CohesionGroup"/> was not owned here (<c>docs/cohesion-hints.md</c>, D4). Each
        /// one is also logged through <see cref="NebulaLog.Warn"/>. A group is never split silently.
        /// </summary>
        public static int SplitCohesionGroups { get; internal set; }

        /// <summary>
        /// How many portal links closed because the band never emptied within <see cref="Portal.CloseTimeoutSeconds"/>
        /// (<c>docs/portals.md</c> D9, D36). Each one is also logged through <see cref="NebulaLog.Warn"/>.
        /// </summary>
        public static int PortalCloseOverruns { get; internal set; }

        /// <summary>How many crossings portals refused: an entity that met the aperture while its link was not open, or that was not admitted (<c>docs/portals.md</c> D17, D36).</summary>
        public static int PortalRefusals { get; internal set; }

        /// <summary>How many portal links are open on this worker right now (<c>docs/portals.md</c> D36).</summary>
        public static int OpenPortalLinks { get; internal set; }

        /// <summary>How many of this worker's open portal links lead into a container another worker owns (<c>docs/portals.md</c> D36).</summary>
        public static int SplitPortalLinks { get; internal set; }

        /// <summary>
        /// How many portal proxies stand in physics scenes on this process right now (<c>docs/portals.md</c> D26, D27, D36): kinematic copies of bodies in
        /// a band and static copies in a throat, not counting aperture colliders. A worker holds at most <see cref="Portal.MaxProxiesPerPortal"/> body copies per portal.
        /// </summary>
        public static int PortalProxies { get; internal set; }

        /// <summary>How many body proxies a portal did not build because it was at <see cref="Portal.MaxProxiesPerPortal"/> (<c>docs/portals.md</c> D26).</summary>
        public static int PortalProxiesDropped { get; internal set; }

        internal static void ResetForNewSession()
        {
            PortalProxies = 0;
            PortalProxiesDropped = 0;
            PortalCloseOverruns = 0;
            PortalRefusals = 0;
            OpenPortalLinks = 0;
            SplitPortalLinks = 0;
            RejectedAuthorityRpcSends = 0;
            SplitCohesionGroups = 0;
        }
    }
}
