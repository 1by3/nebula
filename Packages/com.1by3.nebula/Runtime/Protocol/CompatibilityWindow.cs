namespace Nebula
{
    /// <summary>
    /// Checks client protocol and game content versions against the gateway's supported range.
    /// <para>
    /// The window is <see cref="HelloMsg.MinProtocolVersion"/> to <see cref="HelloMsg.ProtocolVersion"/>, which is 19 to
    /// 20 today. Gateway-to-worker and worker-to-worker connections require <see cref="HelloMsg.ProtocolVersion"/> exactly.
    /// </para>
    /// </summary>
    public static class ProtocolCompatibility
    {
        /// <summary>Returns whether the client's protocol is within the gateway's inclusive supported range.</summary>
        public static bool ClientProtocolAccepted(ushort version) =>
            version >= HelloMsg.MinProtocolVersion && version <= HelloMsg.ProtocolVersion;

        /// <summary>Formats the supported protocol range for logs and refusal messages.</summary>
        public static string WindowText() => HelloMsg.MinProtocolVersion == HelloMsg.ProtocolVersion
            ? HelloMsg.ProtocolVersion.ToString()
            : HelloMsg.MinProtocolVersion + "-" + HelloMsg.ProtocolVersion;

        /// <summary>
        /// Returns the minimum accepted content version. A zero minimum or one greater than
        /// <paramref name="server"/> uses the server's version, requiring an exact match.
        /// </summary>
        public static uint MinContentVersion(uint server, uint configuredMin) =>
            configuredMin == 0 || configuredMin > server ? server : configuredMin;

        /// <summary>
        /// Returns whether the client's content version is within the configured inclusive range.
        /// A <paramref name="server"/> value of zero accepts every content version.
        /// </summary>
        public static bool ContentVersionAccepted(uint client, uint server, uint configuredMin) =>
            server == 0 || (client <= server && client >= MinContentVersion(server, configuredMin));
    }
}
