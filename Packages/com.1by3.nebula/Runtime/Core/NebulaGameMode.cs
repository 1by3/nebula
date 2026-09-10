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

        /// <summary>A convenient uniform spawn point inside a container's floor area.</summary>
        public static Vector3 RandomPointIn(Container container, float margin = 2f, float y = 0f)
        {
            var half = container.Size * 0.5f;
            float x = Random.Range(-half.x + margin, half.x - margin);
            float z = Random.Range(-half.z + margin, half.z - margin);
            var local = container.Center + new Vector3(x, 0f, z);
            var world = container.ToWorld(local);
            world.y = y;
            return world;
        }
    }
}
