using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>Prefab id table shared by every process. Ids are the index in <see cref="NebulaConfig.NetworkPrefabs"/>.</summary>
    public static class NetworkPrefabs
    {
        private static readonly List<GameObject> Prefabs = new List<GameObject>();
        private static readonly Dictionary<GameObject, ushort> Ids = new Dictionary<GameObject, ushort>();

        public static void Register(IList<GameObject> prefabs)
        {
            Prefabs.Clear();
            Ids.Clear();
            if (prefabs == null) return;
            for (int i = 0; i < prefabs.Count; i++)
            {
                var p = prefabs[i];
                if (p == null) { Prefabs.Add(null); continue; }
                if (p.GetComponent<NetworkIdentity>() == null)
                    NebulaLog.Error($"Network prefab '{p.name}' has no NetworkIdentity");
                Prefabs.Add(p);
                Ids[p] = (ushort)i;
            }
        }

        public static int Count => Prefabs.Count;

        public static GameObject Get(ushort prefabId) => prefabId < Prefabs.Count ? Prefabs[prefabId] : null;

        public static bool TryGetId(GameObject prefab, out ushort id) => Ids.TryGetValue(prefab, out id);

        public static ushort IdOf(GameObject prefab)
        {
            if (Ids.TryGetValue(prefab, out var id)) return id;
            throw new System.InvalidOperationException($"'{prefab.name}' is not registered in NebulaConfig.NetworkPrefabs");
        }

        /// <summary>Instantiate a network prefab and bind its behaviours. Spawn hooks run when the caller spawns it.</summary>
        public static NetworkIdentity Instantiate(ushort prefabId, Vector3 position, Quaternion rotation, Transform parent)
        {
            var prefab = Get(prefabId);
            if (prefab == null)
            {
                NebulaLog.Error($"Unknown network prefab id {prefabId}");
                return null;
            }
            var go = Object.Instantiate(prefab, position, rotation, parent);
            var identity = go.GetComponent<NetworkIdentity>();
            identity.PrefabId = prefabId;
            identity.Initialize();
            return identity;
        }
    }
}
