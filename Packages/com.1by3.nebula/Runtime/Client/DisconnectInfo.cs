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
        /// <summary>
        /// The server removed this player (<c>NebulaWorker.Kick</c> or <c>NebulaGateway.Kick</c>).
        /// <see cref="DisconnectInfo.Message"/> is the server's reason and <see cref="DisconnectInfo.Code"/> the game's
        /// own code. The session is over, the session token is cleared, and the client does not reconnect by itself.
        /// </summary>
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
        /// <summary>
        /// The gateway is shutting down and no other gateway was serving: the server is going away, or restarting.
        /// The client reconnects on its normal schedule with its session token, so a server back within
        /// <see cref="NebulaConfig.SessionReclaimSeconds"/> can give the pawn back.
        /// </summary>
        ServerShutdown = 8,
        /// <summary>The game called <see cref="NebulaClient.Disconnect"/> or <see cref="NebulaClient.Leave"/>, directly or through <see cref="NebulaClient.ConnectTo"/>.</summary>
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
    /// Why the local player's pawn went away while the client stayed connected
    /// (<see cref="NebulaClient.LocalPlayerLost"/>).
    /// </summary>
    public enum LocalPlayerLossCause : byte
    {
        /// <summary>
        /// The server removed the pawn: game code on its worker despawned it. Whether the player gets another one is
        /// up to the game.
        /// </summary>
        Despawned = 0,
        /// <summary>
        /// The worker that simulated the pawn failed. The gateway is placing the player again
        /// (<see cref="NebulaClient.JoinHoldReason"/> is <see cref="JoinHoldReason.Recovering"/>), from the pawn's
        /// last checkpoint when it has one, and <see cref="NebulaClient.LocalPlayerSpawned"/> follows when it is back.
        /// </summary>
        WorkerLost = 1,
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
        /// <summary>
        /// For <see cref="DisconnectReason.Kicked"/>: the code the server gave, a number of the game's own that Nebula
        /// passes through unchanged. 0 for every other reason, and when the server gave none.
        /// </summary>
        public readonly ushort Code;

        public DisconnectInfo(DisconnectReason reason, string message, bool willRetry)
            : this(reason, message, willRetry, 0) { }

        /// <summary>A disconnect with a server <paramref name="code"/> (<see cref="Code"/>).</summary>
        public DisconnectInfo(DisconnectReason reason, string message, bool willRetry, ushort code)
        {
            Reason = reason;
            Message = message ?? "";
            WillRetry = willRetry;
            Code = code;
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
