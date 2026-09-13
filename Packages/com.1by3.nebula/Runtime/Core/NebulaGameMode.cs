using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Server-side game hooks, the equivalent of subclassing Mirror's NetworkManager. Put exactly one subclass in the
    /// game scene. Called on whichever worker the gateway picked for the player.
    /// </summary>
    public abstract class NebulaGameMode : MonoBehaviour
    {
        /// <summary>Create and spawn the player's entity inside <paramref name="container"/>. Return the spawned identity.</summary>
        public abstract NetworkIdentity OnSpawnPlayer(NebulaWorker worker, uint clientId, string playerName, Container container);

        /// <summary>The player's client went away; the worker despawns the entity after this returns.</summary>
        public virtual void OnPlayerDespawn(NebulaWorker worker, NetworkIdentity player) { }

        /// <summary>Called once the worker is listening and registered with the control plane.</summary>
        public virtual void OnWorkerStarted(NebulaWorker worker) { }
    }
}
