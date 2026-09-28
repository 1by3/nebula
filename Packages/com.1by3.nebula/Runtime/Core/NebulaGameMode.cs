using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Server-side game hooks, the equivalent of subclassing Mirror's NetworkManager. Put exactly one subclass in the
    /// game scene. Called on whichever worker the gateway picked for the player.
    /// </summary>
    public abstract class NebulaGameMode : MonoBehaviour
    {
        /// <summary>
        /// Create and spawn the player's entity inside <paramref name="container"/>. Return the spawned identity.
        /// Override this or the <see cref="PlayerInfo"/> overload (which also gets the player's identity across sessions).
        /// </summary>
        public virtual NetworkIdentity OnSpawnPlayer(NebulaWorker worker, ulong clientId, string playerName, Container container)
        {
            NebulaLog.Error($"{GetType().Name} overrides neither OnSpawnPlayer overload; client {clientId} gets no pawn");
            return null;
        }

        /// <summary>
        /// The same hook with everything the gateway knows about the player, including <see cref="PlayerInfo.Identity"/>,
        /// which is stable across sessions. Override this one to key saved characters or accounts on the identity.
        /// The default calls the four-argument overload.
        /// </summary>
        public virtual NetworkIdentity OnSpawnPlayer(NebulaWorker worker, in PlayerInfo player, Container container) => OnSpawnPlayer(worker, player.ClientId, player.Name, container);

        /// <summary>
        /// Begin creating a player, optionally waiting for game-owned storage or content loading.
        /// Call <paramref name="complete"/> on the Unity main thread with a factory that creates the pawn.
        /// Nebula invokes the factory only if this join is still current and connected, at most once.
        /// The default completes synchronously through <see cref="OnSpawnPlayer(NebulaWorker, in PlayerInfo, Container)"/>.
        /// </summary>
        public virtual void BeginSpawnPlayer(NebulaWorker worker, PlayerInfo player, Container container,
            System.Action<System.Func<NetworkIdentity>> complete)
        {
            complete(() => OnSpawnPlayer(worker, player, container));
        }

        /// <summary>
        /// The player is gone for good: the reclaim grace ran out, or the player left on purpose
        /// (<see cref="NebulaClient.Leave"/>) or was kicked. The worker despawns the entity after this returns; a
        /// persistent pawn keeps its record for the player's next session.
        /// </summary>
        public virtual void OnPlayerDespawn(NebulaWorker worker, NetworkIdentity player) { }

        /// <summary>
        /// The player's link dropped, or the gateway's link to this worker did, and the reclaim grace has started:
        /// the worker keeps <paramref name="player"/> for <see cref="NebulaConfig.SessionReclaimSeconds"/> in case the
        /// player comes back. Act on the drop here: drop what the pawn holds, get it out of a seat, save a checkpoint.
        /// <see cref="NetworkIdentity.IsOwnerConnected"/> is already false. Called on the worker that owns the pawn,
        /// once per drop: a pawn handed to another worker during the grace carries the fact with it, and the new owner
        /// does not call it again. Followed by <see cref="OnPlayerReconnected"/> if the player comes back in time, or by
        /// <see cref="OnPlayerDespawn"/> when the grace runs out. Not called when the player leaves on purpose or is
        /// kicked (straight to <see cref="OnPlayerDespawn"/>), nor with a grace of 0. The default does nothing.
        /// </summary>
        public virtual void OnPlayerDisconnected(NebulaWorker worker, NetworkIdentity player) { }

        /// <summary>
        /// The player came back within the reclaim grace and has <paramref name="player"/> again: the session was
        /// reclaimed, through any gateway of the mesh, or the gateway whose link to this worker dropped is back.
        /// <see cref="NetworkIdentity.IsOwnerConnected"/> is already true. Called on the worker that owns the pawn;
        /// <see cref="NebulaGameMode.OnSpawnPlayer(NebulaWorker, in PlayerInfo, Container)"/> is not called again. The
        /// default does nothing.
        /// </summary>
        public virtual void OnPlayerReconnected(NebulaWorker worker, NetworkIdentity player) { }

        /// <summary>Called once the worker is listening and registered with the control plane.</summary>
        public virtual void OnWorkerStarted(NebulaWorker worker) { }

        /// <summary>
        /// Called for each authoritative entity before worker container membership queries.
        /// Procedural worlds may shift their origin here so distant regions resolve container boundaries precisely.
        /// The default does nothing. Keep this per-entity simulation callback inexpensive.
        /// </summary>
        public virtual void PrepareSpatialFrame(NetworkIdentity entity) { }
    }

    /// <summary>Who is joining, as the gateway told the worker (<see cref="NebulaGameMode.OnSpawnPlayer(NebulaWorker, in PlayerInfo, Container)"/>).</summary>
    public readonly struct PlayerInfo
    {
        /// <summary>
        /// The player's session id: unique across every gateway of the mesh, and kept across a reconnection with a
        /// session token (the player continues with the same pawn). A new session gets a new id.
        /// </summary>
        public readonly ulong ClientId;
        /// <summary>The display name the client asked for.</summary>
        public readonly string Name;
        /// <summary>The player's identity across sessions (<see cref="PlayerIdentity"/>): from the OpenID token they presented, or the anonymous token the gateway issued them.</summary>
        public readonly string Identity;
        /// <summary>The client is a headless bot.</summary>
        public readonly bool IsBot;

        public PlayerInfo(ulong clientId, string name, string identity, bool isBot)
        {
            ClientId = clientId;
            Name = name ?? "";
            Identity = identity ?? "";
            IsBot = isBot;
        }
    }
}
