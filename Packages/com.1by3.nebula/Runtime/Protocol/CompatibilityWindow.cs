using System;

namespace Nebula
{
    /// <summary>
    /// Checks client protocol and game content versions against the gateway's supported range.
    /// <para>
    /// The window is <see cref="HelloMsg.MinProtocolVersion"/> to <see cref="HelloMsg.ProtocolVersion"/>, narrowed to
    /// <see cref="HelloMsg.TickRateVersion"/> and up on a mesh that does not run at the default tick rate
    /// (<see cref="MinClientProtocol"/>). Gateway-to-worker and worker-to-worker connections require
    /// <see cref="HelloMsg.ProtocolVersion"/> exactly.
    /// </para>
    /// </summary>
    public static class ProtocolCompatibility
    {
        /// <summary>Returns whether the client's protocol is within the gateway's inclusive supported range.</summary>
        public static bool ClientProtocolAccepted(ushort version) =>
            version >= HelloMsg.MinProtocolVersion && version <= HelloMsg.ProtocolVersion;

        /// <summary>
        /// The oldest client protocol a gateway whose mesh runs at <paramref name="tickRate"/> admits: the window's
        /// minimum at the default rate, and <see cref="HelloMsg.TickRateVersion"/> at any other, because an older client
        /// would count every tick at 60 Hz.
        /// </summary>
        public static ushort MinClientProtocol(int tickRate) =>
            tickRate == NetworkTime.DefaultTickRate ? HelloMsg.MinProtocolVersion : Math.Max(HelloMsg.MinProtocolVersion, HelloMsg.TickRateVersion);

        /// <summary>Whether a client of <paramref name="version"/> may join a mesh running at <paramref name="tickRate"/>.</summary>
        public static bool ClientProtocolAccepted(ushort version, int tickRate) =>
            version >= MinClientProtocol(tickRate) && version <= HelloMsg.ProtocolVersion;

        /// <summary>Formats the supported protocol range for logs and refusal messages.</summary>
        public static string WindowText() => WindowText(NetworkTime.DefaultTickRate);

        /// <summary>The supported range at <paramref name="tickRate"/>, for logs and refusal messages.</summary>
        public static string WindowText(int tickRate)
        {
            ushort min = MinClientProtocol(tickRate);
            return min == HelloMsg.ProtocolVersion ? HelloMsg.ProtocolVersion.ToString() : min + "-" + HelloMsg.ProtocolVersion;
        }

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
