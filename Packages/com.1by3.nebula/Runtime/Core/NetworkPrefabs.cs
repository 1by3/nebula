using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Prefab id table shared by every process. Ids are the index in <see cref="NebulaConfig.NetworkPrefabs"/>.
    /// Ids from <see cref="BuiltInBase"/> up are reserved for prefabs Nebula itself provides (such as the entity
    /// behind <see cref="Nebula.World.ChunkState"/>): every process builds them the same way, so a game never
    /// lists them and they never shift when the game's list changes.
    /// </summary>
    public static class NetworkPrefabs
    {
        /// <summary>First id reserved for Nebula's built-in prefabs. A game's list is never this long.</summary>
        public const ushort BuiltInBase = 0xFF00;

        /// <summary>The built-in prefab of <see cref="Nebula.World.ChunkStateEntity"/>.</summary>
        public const ushort ChunkStatePrefabId = BuiltInBase;

        /// <summary>Whether <paramref name="prefabId"/> is one of Nebula's built-in prefabs rather than an entry of the game's list.</summary>
        public static bool IsBuiltIn(ushort prefabId) => prefabId >= BuiltInBase && prefabId != ushort.MaxValue;

        private static GameObject BuiltIn(ushort prefabId) =>
            prefabId == ChunkStatePrefabId ? Nebula.World.ChunkStateEntity.Template : null;

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

        /// <summary>The prefab registered under <paramref name="prefabId"/>, a built-in one included; null when there is none.</summary>
        public static GameObject Get(ushort prefabId)
        {
            if (IsBuiltIn(prefabId)) return BuiltIn(prefabId);
            return prefabId < Prefabs.Count ? Prefabs[prefabId] : null;
        }

        public static bool TryGetId(GameObject prefab, out ushort id)
        {
            if (Ids.TryGetValue(prefab, out id)) return true;
            if (prefab != null && prefab == BuiltIn(ChunkStatePrefabId)) { id = ChunkStatePrefabId; return true; }
            return false;
        }

        public static ushort IdOf(GameObject prefab)
        {
            if (TryGetId(prefab, out var id)) return id;
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
            var source = prefab.GetComponent<NetworkIdentity>();
            if (source != null && source.SceneId != 0) return InstantiateClearingSceneId(prefab, prefabId, source.SceneId, position, rotation, parent);
            var go = Object.Instantiate(prefab, position, rotation, parent);
            // A built-in prefab is a hidden object that is never saved; its copies are ordinary scene objects.
            if (IsBuiltIn(prefabId)) { go.hideFlags = HideFlags.None; go.name = prefab.name; }
            var identity = go.GetComponent<NetworkIdentity>();
            identity.PrefabId = prefabId;
            identity.Initialize();
            return identity;
        }

        private static readonly HashSet<ushort> WarnedSceneId = new HashSet<ushort>();
        private static Transform _inactiveHolder;

        /// <summary>
        /// A prefab made by dragging a saved scene object keeps that object's <see cref="NetworkIdentity.SceneId"/>.
        /// Every copy would then claim to be that scene object: clients and neighbours wait for it forever and the
        /// owner never gets its player. A copy of a prefab is never a scene entity, so the id is dropped. The copy is
        /// made under an inactive holder so its Awake cannot register the stale id before it is cleared.
        /// </summary>
        private static NetworkIdentity InstantiateClearingSceneId(GameObject prefab, ushort prefabId, uint sceneId, Vector3 position, Quaternion rotation, Transform parent)
        {
            if (WarnedSceneId.Add(prefabId))
                NebulaLog.Warn($"Network prefab '{prefab.name}' has SceneId {sceneId}; spawned copies ignore it. Set SceneId to 0 on the prefab asset (Nebula > Validate Project reports this)");
            if (_inactiveHolder == null)
            {
                var holder = new GameObject("Nebula prefab holder") { hideFlags = HideFlags.HideAndDontSave };
                holder.SetActive(false);
                _inactiveHolder = holder.transform;
            }
            var go = Object.Instantiate(prefab, position, rotation, _inactiveHolder);
            var identity = go.GetComponent<NetworkIdentity>();
            identity.SceneId = 0;
            go.transform.SetParent(parent, true);
            identity.PrefabId = prefabId;
            identity.Initialize();
            return identity;
        }
    }
}
