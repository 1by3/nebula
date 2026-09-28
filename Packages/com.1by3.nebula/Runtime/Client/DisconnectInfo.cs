namespace Nebula
{
    /// <summary>
    /// Why a client's connection to the gateway ended, or why an attempt to connect failed. Carried by
    /// <see cref="DisconnectInfo"/> in <see cref="NebulaClient.Disconnected"/> and <see cref="NebulaClient.LastDisconnect"/>.
    /// Branch on it to choose what to show: a network problem on the player's side, a server that is down, or a
    /// decision the server made.
    /// </summary>
    public enum DisconnectReason : byte
    {
        /// <summary>The transport did not say why the link closed. A custom <see cref="ITransport"/> that reports no reason produces this.</summary>
        Unknown = 0,
        /// <summary>The connection was up and the gateway stopped answering for longer than the transport's disconnect timeout: the gateway, or the network between, went away.</summary>
        Timeout = 1,
        /// <summary>The player's own network is down: the client's machine has no route to the gateway.</summary>
        NetworkUnreachable = 2,
        /// <summary>The gateway could not be reached: nothing answered at its address, it refused the connection, or its name did not resolve.</summary>
        HostUnreachable = 3,
        /// <summary>The gateway closed the connection without saying why, or restarted and no longer knows this client.</summary>
        ClosedByServer = 4,
        /// <summary>The server removed this player. Reserved for a server notice that says so; nothing raises it yet.</summary>
        Kicked = 5,
        /// <summary>
        /// This player connected again somewhere else and the newer connection took the session
        /// (<see cref="NebulaClient.SessionReplaced"/>). The client does not reconnect by itself.
        /// </summary>
        SessionReplaced = 6,
        /// <summary>
        /// The gateway refused the join (<see cref="NebulaClient.JoinRefused"/>); <see cref="NebulaClient.JoinRejectReason"/>
        /// holds the typed reason. The client retries only when the gateway asked it to.
        /// </summary>
        JoinRefused = 7,
        /// <summary>The server is shutting down. Reserved for a server notice that says so; nothing raises it yet.</summary>
        ServerShutdown = 8,
        /// <summary>The game called <see cref="NebulaClient.Disconnect"/>, directly or through <see cref="NebulaClient.ConnectTo"/>.</summary>
        ClientRequested = 9,
        /// <summary>
        /// The gateway is being taken out of service and asked the client to reconnect
        /// (<see cref="NebulaClient.GatewayDraining"/>). The client reconnects at once with its session token.
        /// </summary>
        GatewayDraining = 10,
        /// <summary>The encrypted connection was refused: the key exchange failed or the gateway's certificate did not match the pinned fingerprint.</summary>
        EncryptionFailed = 11,
    }

    /// <summary>
    /// How a client's connection ended: the typed <see cref="Reason"/>, a <see cref="Message"/> for logs and the
    /// player, and whether the client will try again by itself (<see cref="WillRetry"/>).
    /// </summary>
    public readonly struct DisconnectInfo
    {
        /// <summary>Why the connection ended.</summary>
        public readonly DisconnectReason Reason;
        /// <summary>A readable description, the same text as <see cref="NebulaClient.LastError"/> when the client set one.</summary>
        public readonly string Message;
        /// <summary>True when the client keeps trying to connect; false when it has stopped and waits for <see cref="NebulaClient.Connect"/>.</summary>
        public readonly bool WillRetry;

        public DisconnectInfo(DisconnectReason reason, string message, bool willRetry)
        {
            Reason = reason;
            Message = message ?? "";
            WillRetry = willRetry;
        }

        public override string ToString() => $"{Reason}: {Message}" + (WillRetry ? " (retrying)" : "");

        /// <summary>
        /// The client-level reason for a closed transport link. The transport cannot tell a gateway that never
        /// answered from one that closed, so a link that was never up counts as unreachable unless the transport
        /// says the network itself is down or the handshake was refused.
        /// </summary>
        internal static DisconnectReason FromTransport(TransportDisconnectReason reason, bool wasConnected)
        {
            switch (reason)
            {
                case TransportDisconnectReason.Timeout:
                    return wasConnected ? DisconnectReason.Timeout : DisconnectReason.HostUnreachable;
                case TransportDisconnectReason.NetworkUnreachable: return DisconnectReason.NetworkUnreachable;
                case TransportDisconnectReason.HostUnreachable:
                case TransportDisconnectReason.Rejected:
                    return DisconnectReason.HostUnreachable;
                case TransportDisconnectReason.ClosedByRemote:
                    return wasConnected ? DisconnectReason.ClosedByServer : DisconnectReason.HostUnreachable;
                case TransportDisconnectReason.LocalRequest: return DisconnectReason.ClientRequested;
                case TransportDisconnectReason.SecurityFailure: return DisconnectReason.EncryptionFailed;
                default: return DisconnectReason.Unknown;
            }
        }
    }
}
