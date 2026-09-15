using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Nebula keeps its registries in static state. With the Editor's "Enter Play Mode without domain reload" that
    /// state would survive from one play session into the next, still holding the previous session's (destroyed)
    /// containers, identities and event subscribers. This clears all of it before the first scene of a session
    /// loads, so every session starts as a fresh process would.
    /// </summary>
    internal static class NebulaStatics
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession()
        {
            ContainerRegistry.ResetForNewSession();
            InstanceScenes.Reset();
            SceneEntities.ResetForNewSession();
            NetworkIdentity.Live.Clear();
            DynamicContainer.ResetForNewSession();
            NebulaWorld.ResetForNewSession();
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = false;
            NebulaRuntime.LocalClientId = 0;
            NebulaRuntime.LocalWorkerId = "";
            NebulaRuntime.LocalWorkerIndex = 0;
            NebulaRuntime.RpcSink = null;
            NebulaRuntime.Config = null;
            NetworkTime.Tick = 0;
            NetworkTime.LatestServerTick = 0;
            NetworkTime.RenderTick = 0;
        }
    }
}
