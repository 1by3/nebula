namespace Nebula
{
    /// <summary>
    /// The deployment compatibility rules, in one place so the gateway, the tests and a game's own tooling all
    /// answer the same way. Design record: <c>docs/compatibility-policy.md</c>; version table:
    /// <c>docs/protocol-versions.md</c>.
    /// <para>
    /// Client to gateway accepts a window of protocol versions
    /// (<see cref="HelloMsg.MinProtocolVersion"/>..<see cref="HelloMsg.ProtocolVersion"/>, N-1 and N). Gateway to
    /// worker and worker to worker require <see cref="HelloMsg.ProtocolVersion"/> exactly, because those processes
    /// are upgraded together by the operator and a half-upgraded mesh has no client to keep happy.
    /// </para>
    /// </summary>
    public static class ProtocolCompatibility
    {
        /// <summary>Is this client's protocol inside the gateway's window?</summary>
        public static bool ClientProtocolAccepted(ushort version) =>
            version >= HelloMsg.MinProtocolVersion && version <= HelloMsg.ProtocolVersion;

        /// <summary>The window as a string fit for a log line or a refusal reason ("18", or "18-19" once it spans two).</summary>
        public static string WindowText() => HelloMsg.MinProtocolVersion == HelloMsg.ProtocolVersion
            ? HelloMsg.ProtocolVersion.ToString()
            : HelloMsg.MinProtocolVersion + "-" + HelloMsg.ProtocolVersion;

        /// <summary>
        /// The oldest game content version a gateway configured with <paramref name="server"/> and
        /// <paramref name="configuredMin"/> admits: the server's own number when no minimum is configured, which
        /// makes the default an exact match.
        /// </summary>
        public static uint MinContentVersion(uint server, uint configuredMin) =>
            configuredMin == 0 || configuredMin > server ? server : configuredMin;

        /// <summary>
        /// Is this client's game content version one the gateway runs? A gateway that declares no content version
        /// (<paramref name="server"/> 0) does not check, so a game that does not version its content is unaffected.
        /// </summary>
        public static bool ContentVersionAccepted(uint client, uint server, uint configuredMin) =>
            server == 0 || (client <= server && client >= MinContentVersion(server, configuredMin));
    }
}
