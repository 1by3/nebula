using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// The scene-placed entities resident in this process, by <see cref="NetworkIdentity.SceneId"/>. A
    /// <see cref="NetworkIdentity"/> authored into a scene (a door, a lift, a turret) registers itself here when its
    /// scene loads and leaves when it unloads. The worker that owns the container it stands in spawns it; every other
    /// process binds the spawn it receives to its own copy of the object instead of instantiating a prefab, and when
    /// the scene is not resident the spawn waits here-abouts until it is (see the worker's and client's pending
    /// records). The object itself is never created or destroyed by the network: it belongs to its scene.
    /// </summary>
    public static class SceneEntities
    {
        private static readonly Dictionary<uint, NetworkIdentity> ById = new Dictionary<uint, NetworkIdentity>();
        private static readonly List<NetworkIdentity> Snapshot = new List<NetworkIdentity>();

        /// <summary>A scene entity became resident (its scene loaded). It is not spawned yet.</summary>
        public static event Action<NetworkIdentity> Registered;
        /// <summary>A scene entity is about to leave (its scene is unloading or the object was destroyed). Still bound if it was.</summary>
        public static event Action<NetworkIdentity> Unregistering;

        public static int Count => ById.Count;

        /// <summary>The resident scene entities. Safe to spawn from while iterating: it is a copy taken now.</summary>
        public static IReadOnlyList<NetworkIdentity> All
        {
            get
            {
                Snapshot.Clear();
                foreach (var kv in ById) if (kv.Value != null) Snapshot.Add(kv.Value);
                return Snapshot;
            }
        }

        /// <summary>The resident scene entity with this id, or null while its scene is not loaded here.</summary>
        public static NetworkIdentity Find(uint sceneId)
        {
            return sceneId != 0 && ById.TryGetValue(sceneId, out var e) && e != null ? e : null;
        }

        internal static void Register(NetworkIdentity identity)
        {
            if (identity == null || identity.SceneId == 0) return;
            if (ById.TryGetValue(identity.SceneId, out var existing) && existing != null && existing != identity)
            {
                NebulaLog.Error($"scene entity id {identity.SceneId} is used by both '{existing.name}' and '{identity.name}'; re-save the scene to reassign ids. '{identity.name}' will not be networked");
                return;
            }
            ById[identity.SceneId] = identity;
            Registered?.Invoke(identity);
        }

        internal static void Unregister(NetworkIdentity identity)
        {
            if (identity == null || identity.SceneId == 0) return;
            if (!ById.TryGetValue(identity.SceneId, out var existing) || existing != identity) return;
            try { Unregistering?.Invoke(identity); }
            finally { ById.Remove(identity.SceneId); }
        }

        internal static void Clear()
        {
            ById.Clear();
        }
    }
}
