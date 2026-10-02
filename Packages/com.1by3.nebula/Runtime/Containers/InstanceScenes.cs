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
        /// <summary>The <see cref="IInstanceContentLoader"/>s found under each prepared container, by runtime ID. A container with none has no entry.</summary>
        private static readonly Dictionary<ulong, IInstanceContentLoader[]> Loaders = new Dictionary<ulong, IInstanceContentLoader[]>();
        private static readonly List<IInstanceContentLoader> LoaderScratch = new List<IInstanceContentLoader>();
        /// <summary>Renderers this class turned off under each hidden container's content, by runtime ID.</summary>
        private static readonly Dictionary<ulong, List<Renderer>> HiddenContent = new Dictionary<ulong, List<Renderer>>();
        /// <summary>How many live <see cref="KeepDrawn"/> requests each scope has. A scope with none has no entry.</summary>
        private static readonly Dictionary<ulong, int> DrawnRequests = new Dictionary<ulong, int>();
        private static ulong _view;

        /// <summary>
        /// How long a destination worker or a client keeps waiting for content to load before it stops answering a
        /// preparation. The source worker's own timeout (<see cref="NebulaWorker.PrepareTransfer"/>) is what ends the
        /// crossing; this only bounds the bookkeeping of a side that does not know that timeout.
        /// </summary>
        internal const float ContentWaitLimitSeconds = 120f;

        /// <summary>The physics scene for a loaded scope. An unknown private scope returns an invalid scene.</summary>
        public static PhysicsScene PhysicsFor(ulong instanceId) => instanceId == 0 ? Physics.defaultPhysicsScene :
            Scenes.TryGetValue(instanceId, out var scene) ? scene.GetPhysicsScene() : default;

        internal static void Reset()
        {
            Scenes.Clear();
            Content.Clear();
            Loaders.Clear();
            HiddenRenderers.Clear(HiddenContent);
            DrawnRequests.Clear();
            _view = 0;
        }

        /// <summary>
        /// Load this container's geometry. Returns false when its resource is missing or contains network identities.
        /// <para>
        /// True means the content was created, not that it has finished loading: content with an
        /// <see cref="IInstanceContentLoader"/> may still be building. <see cref="ContentState"/> reports that.
        /// </para>
        /// </summary>
        public static bool Prepare(Container container)
        {
            if (container == null || container.InstanceId == 0) return true;
            if (container.IsDynamic) return Prepare(container.Enclosing);
            // A child box lives in its parent's scene, under its parent's transform: preparing the parent is enough.
            if (container.FixedParent != null) return Prepare(container.FixedParent);
            if (Content.ContainsKey(container.RuntimeId)) return true;
            var info = container.Instance;
            GameObject prefab = string.IsNullOrEmpty(info.ContentResource) ? null : Resources.Load<GameObject>(info.ContentResource);
            if (!string.IsNullOrEmpty(info.ContentResource) && prefab == null) return false;
            // Saved entities are spawned through the worker API; static content must not create duplicate scene IDs.
            if (prefab != null && prefab.GetComponentInChildren<NetworkIdentity>(true) != null) return false;
            // The Editor outside play mode cannot create a runtime scene, and an EditMode test standing a scope's
            // containers up (conformance tier B) does not need one: the container stays in the active scene and
            // everything this method is really about — the resource check above — has already run. Loaders already
            // under the container are still collected, so readiness behaves as it does in play mode.
            if (!Application.isPlaying) { CollectLoaders(container); return true; }
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
            AdoptContent(container, content);
            return true;
        }

        /// <summary>
        /// Record the content <see cref="Prepare"/> created for a container, collect its loaders, and hide it on a
        /// client when its scope is not drawn. A seam for EditMode tests, which cannot create a runtime scene.
        /// </summary>
        internal static void AdoptContent(Container container, GameObject content)
        {
            Content.Add(container.RuntimeId, content);
            CollectLoaders(container);
            SetVisible(container.RuntimeId, content, !NebulaRuntime.IsClient || Drawn(container.InstanceId));
        }

        /// <summary>
        /// Whether a prepared container's content has finished loading: <see cref="InstanceContentState.Failed"/> when
        /// any <see cref="IInstanceContentLoader"/> under it has failed, <see cref="InstanceContentState.Loading"/> while
        /// any is still loading, and <see cref="InstanceContentState.Ready"/> otherwise. Content with no loaders, a
        /// container in the public world, and a container that has not been prepared are
        /// <see cref="InstanceContentState.Ready"/>. A child or carried container reports the content that
        /// <see cref="Prepare"/> loads for it.
        /// </summary>
        /// <param name="container">A container passed to <see cref="Prepare"/>.</param>
        public static InstanceContentState ContentState(Container container)
        {
            var loaders = LoadersOf(container);
            if (loaders == null) return InstanceContentState.Ready;
            var state = InstanceContentState.Ready;
            foreach (var loader in loaders)
            {
                var current = StateOf(loader);
                if (current == InstanceContentState.Failed) return current;
                if (current == InstanceContentState.Loading) state = current;
            }
            return state;
        }

        /// <summary>
        /// How far a prepared container's content has loaded, from 0 to 1: the mean of its loaders'
        /// <see cref="IInstanceContentLoader.Progress"/>, counting a ready loader as 1. It is 1 when the content has no
        /// loaders. Use it for a loading indicator; <see cref="ContentState"/> decides readiness.
        /// </summary>
        /// <param name="container">A container passed to <see cref="Prepare"/>.</param>
        public static float ContentProgress(Container container)
        {
            var loaders = LoadersOf(container);
            if (loaders == null) return 1f;
            float sum = 0f;
            int count = 0;
            foreach (var loader in loaders)
            {
                if (loader is UnityEngine.Object unityObject && unityObject == null) continue;
                count++;
                if (StateOf(loader) == InstanceContentState.Ready) { sum += 1f; continue; }
                float progress;
                try { progress = loader.Progress; }
                catch (Exception) { progress = 0f; }
                sum += float.IsNaN(progress) ? 0f : Mathf.Clamp01(progress);
            }
            return count == 0 ? 1f : sum / count;
        }

        /// <summary>A loader's state. A destroyed loader no longer holds anything back; one that throws has failed.</summary>
        private static InstanceContentState StateOf(IInstanceContentLoader loader)
        {
            if (loader is UnityEngine.Object unityObject && unityObject == null) return InstanceContentState.Ready;
            try { return loader.State; }
            catch (Exception e)
            {
                NebulaLog.Error($"an instance content loader threw while reporting its state; its content is treated as failed: {e}");
                return InstanceContentState.Failed;
            }
        }

        /// <summary>The loaders of the container whose content <see cref="Prepare"/> creates for this one, or null when there are none.</summary>
        private static IInstanceContentLoader[] LoadersOf(Container container)
        {
            if (Loaders.Count == 0) return null;
            var holder = ContentHolder(container);
            return holder != null && Loaders.TryGetValue(holder.RuntimeId, out var loaders) ? loaders : null;
        }

        /// <summary>Follow <see cref="Prepare"/>'s redirections to the container that holds the content, or null for the public world.</summary>
        private static Container ContentHolder(Container container)
        {
            // Bounded like any walk up the container tree: a chain longer than every container there is has looped.
            int limit = ContainerRegistry.Dynamic.Count + 8;
            for (int hops = 0; container != null && hops <= limit; hops++)
            {
                if (container.InstanceId == 0) return null;
                if (container.IsDynamic) container = container.Enclosing;
                else if (container.FixedParent != null) container = container.FixedParent;
                else return container;
            }
            return null;
        }

        /// <summary>Find the active loaders under a container whose content was just created.</summary>
        private static void CollectLoaders(Container container)
        {
            LoaderScratch.Clear();
            container.GetComponentsInChildren(false, LoaderScratch);
            if (LoaderScratch.Count == 0) Loaders.Remove(container.RuntimeId);
            else Loaders[container.RuntimeId] = LoaderScratch.ToArray();
            LoaderScratch.Clear();
        }

        // -------------------------------------------------------------------------------- which scopes are drawn

        /// <summary>
        /// Keep a scope's content drawn on this client while the local pawn is in another scope: the static content
        /// <see cref="Prepare"/> loads for its instance parts, and the content under the
        /// <see cref="World.ChunkContext.Root"/> of its scoped grid's chunks. A client normally draws only the pawn's
        /// own scope and the public world, and keeps other resident scopes hidden; use this to show the other side of
        /// an open doorway. Content created or loaded while the request is live starts drawn.
        /// <para>
        /// Requests are counted: match each call with one <see cref="StopKeepingDrawn"/>. The scope is hidden again
        /// only when its last request is withdrawn. Nebula turns on only the renderers it turned off itself, so
        /// renderers the game turned off stay off. A request outlives the scope's content: when the
        /// content is released Nebula forgets its renderers but keeps the request, so content of the scope prepared
        /// again starts drawn.
        /// </para>
        /// <para>
        /// Rendering only. It loads nothing, and changes no physics scene, interest, ownership or message: content
        /// that is not resident on this client stays absent. A worker records the request and ignores it. The
        /// public world (<paramref name="instanceId"/> 0) is always drawn, so a request for it does nothing.
        /// </para>
        /// </summary>
        /// <param name="instanceId">The scope's instance ID, as <see cref="Container.InstanceId"/> reports it.</param>
        public static void KeepDrawn(ulong instanceId)
        {
            if (instanceId == 0) return;
            DrawnRequests.TryGetValue(instanceId, out int count);
            DrawnRequests[instanceId] = count + 1;
            if (count == 0) ApplyScope(instanceId);
        }

        /// <summary>
        /// Withdraw one <see cref="KeepDrawn"/> request for a scope. When it was the last one and the local pawn is in
        /// another scope, the scope's content is hidden again. Returns false, and changes nothing, when the scope has
        /// no request.
        /// </summary>
        /// <param name="instanceId">The scope's instance ID passed to <see cref="KeepDrawn"/>.</param>
        public static bool StopKeepingDrawn(ulong instanceId)
        {
            if (!DrawnRequests.TryGetValue(instanceId, out int count)) return false;
            if (count > 1) { DrawnRequests[instanceId] = count - 1; return true; }
            DrawnRequests.Remove(instanceId);
            ApplyScope(instanceId);
            return true;
        }

        /// <summary>
        /// Whether a scope's content is drawn on this client: true for the public world, for the local pawn's scope,
        /// and for a scope with a live <see cref="KeepDrawn"/> request; false for any other scope. Always true on a
        /// process that is not a client, which hides nothing.
        /// </summary>
        /// <param name="instanceId">A scope's instance ID; 0 is the public world.</param>
        public static bool IsDrawn(ulong instanceId) => !NebulaRuntime.IsClient || Drawn(instanceId);

        /// <summary>Whether a scope is drawn by the view rules alone: the public world, the pawn's scope, or a kept scope.</summary>
        internal static bool Drawn(ulong instanceId) => instanceId == 0 || instanceId == _view || IsKeptDrawn(instanceId);

        /// <summary>Whether a scope has a live <see cref="KeepDrawn"/> request.</summary>
        internal static bool IsKeptDrawn(ulong instanceId) => DrawnRequests.Count > 0 && DrawnRequests.ContainsKey(instanceId);

        internal static void SetView(ulong instanceId)
        {
            _view = instanceId;
            foreach (var pair in Content)
            {
                var container = ContainerRegistry.GetRuntime(pair.Key);
                if (container != null) SetVisible(pair.Key, pair.Value, Drawn(container.InstanceId));
            }
            // A scoped grid's chunks hold content the game builds, not content prepared here: hidden the same way.
            World.NebulaChunks.SetView(instanceId);
        }

        /// <summary>A scope's requests changed: show or hide its content to match, on a client only.</summary>
        private static void ApplyScope(ulong instanceId)
        {
            if (!NebulaRuntime.IsClient) return;
            bool visible = Drawn(instanceId);
            foreach (var pair in Content)
            {
                var container = ContainerRegistry.GetRuntime(pair.Key);
                if (container != null && container.InstanceId == instanceId) SetVisible(pair.Key, pair.Value, visible);
            }
            World.NebulaChunks.ApplyScope(instanceId);
        }

        /// <summary>
        /// Show or hide one container's content. Hiding turns off the renderers that are drawn and remembers them;
        /// showing turns those back on, so renderers the game turned off itself stay off.
        /// </summary>
        private static void SetVisible(ulong runtimeId, GameObject content, bool visible)
        {
            if (content == null) return;
            if (visible) HiddenRenderers.Show(HiddenContent, runtimeId);
            else HiddenRenderers.Hide(HiddenContent, runtimeId, content.transform);
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
            Loaders.Remove(container.RuntimeId);
            HiddenRenderers.Forget(HiddenContent, container.RuntimeId);
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
