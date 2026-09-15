using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>Loads instance geometry into separate physics scenes. Query an entity's PhysicsScene for gameplay collision tests.</summary>
    public static class InstanceScenes
    {
        private static readonly Dictionary<ulong, Scene> Scenes = new Dictionary<ulong, Scene>();
        private static readonly Dictionary<ulong, GameObject> Content = new Dictionary<ulong, GameObject>();
        private static ulong _view;

        /// <summary>The physics scene for a loaded scope. An unknown private scope returns an invalid scene.</summary>
        public static PhysicsScene PhysicsFor(ulong instanceId) => instanceId == 0 ? Physics.defaultPhysicsScene :
            Scenes.TryGetValue(instanceId, out var scene) ? scene.GetPhysicsScene() : default;

        internal static void Reset()
        {
            Scenes.Clear();
            Content.Clear();
            _view = 0;
        }

        /// <summary>Load this container's geometry. Returns false when its resource is missing or contains network identities.</summary>
        public static bool Prepare(Container container)
        {
            if (container == null || container.InstanceId == 0) return true;
            if (container.IsDynamic) return Prepare(container.Enclosing);
            if (Content.ContainsKey(container.RuntimeId)) return true;
            var info = container.Instance;
            GameObject prefab = string.IsNullOrEmpty(info.ContentResource) ? null : Resources.Load<GameObject>(info.ContentResource);
            if (!string.IsNullOrEmpty(info.ContentResource) && prefab == null) return false;
            // Saved entities are spawned through the worker API; static content must not create duplicate scene IDs.
            if (prefab != null && prefab.GetComponentInChildren<NetworkIdentity>(true) != null) return false;
            if (!Scenes.TryGetValue(info.InstanceId, out var scene))
            {
                scene = SceneManager.CreateScene("Nebula instance " + info.InstanceId,
                    new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                Scenes.Add(info.InstanceId, scene);
            }
            container.transform.SetParent(null, true);
            SceneManager.MoveGameObjectToScene(container.gameObject, scene);
            GameObject content = prefab != null ? UnityEngine.Object.Instantiate(prefab, container.transform, false) : new GameObject("Content");
            if (prefab == null) content.transform.SetParent(container.transform, false);
            Content.Add(container.RuntimeId, content);
            SetVisible(content, !NebulaRuntime.IsClient || info.InstanceId == _view);
            return true;
        }

        internal static void SetView(ulong instanceId)
        {
            _view = instanceId;
            foreach (var pair in Content)
            {
                var container = ContainerRegistry.GetRuntime(pair.Key);
                if (container != null) SetVisible(pair.Value, container.InstanceId == instanceId);
            }
        }

        private static void SetVisible(GameObject content, bool visible)
        {
            if (content == null) return;
            foreach (var renderer in content.GetComponentsInChildren<Renderer>(true)) renderer.forceRenderingOff = !visible;
        }

        internal static void Simulate(float deltaTime)
        {
            foreach (var scene in Scenes.Values)
                if (scene.IsValid() && scene.isLoaded) scene.GetPhysicsScene().Simulate(deltaTime);
        }

        internal static void Release(Container container)
        {
            if (container.InstanceId == 0) return;
            Content.Remove(container.RuntimeId);
            foreach (var other in ContainerRegistry.Runtime)
                if (other != container && other.InstanceId == container.InstanceId) return;
            if (Scenes.TryGetValue(container.InstanceId, out var scene))
            {
                Scenes.Remove(container.InstanceId);
                if (scene.IsValid() && Application.isPlaying) SceneManager.UnloadSceneAsync(scene);
            }
        }
    }
}
