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
        public virtual NetworkIdentity OnSpawnPlayer(NebulaWorker worker, uint clientId, string playerName, Container container)
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

        /// <summary>The player's client went away; the worker despawns the entity after this returns.</summary>
        public virtual void OnPlayerDespawn(NebulaWorker worker, NetworkIdentity player) { }

        /// <summary>Called once the worker is listening and registered with the control plane.</summary>
        public virtual void OnWorkerStarted(NebulaWorker worker) { }
    }

    /// <summary>Who is joining, as the gateway told the worker (<see cref="NebulaGameMode.OnSpawnPlayer(NebulaWorker, in PlayerInfo, Container)"/>).</summary>
    public readonly struct PlayerInfo
    {
        /// <summary>This session's connection id. Changes every time the player connects.</summary>
        public readonly uint ClientId;
        /// <summary>The display name the client asked for.</summary>
        public readonly string Name;
        /// <summary>The player's identity across sessions (<see cref="PlayerIdentity"/>): from the OpenID token they presented, or the anonymous token the gateway issued them.</summary>
        public readonly string Identity;
        /// <summary>The client is a headless bot.</summary>
        public readonly bool IsBot;

        public PlayerInfo(uint clientId, string name, string identity, bool isBot)
        {
            ClientId = clientId;
            Name = name ?? "";
            Identity = identity ?? "";
            IsBot = isBot;
        }
    }
}
