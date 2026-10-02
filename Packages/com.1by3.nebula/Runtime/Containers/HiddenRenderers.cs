using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Turns off the renderers under a content root and remembers which, so showing it again turns on only those:
    /// renderers the game turned off itself stay off. Used by a client to keep another scope's content resident but
    /// not drawn (<see cref="InstanceScenes"/>, <see cref="World.NebulaChunks"/>). The lists are pooled, so hiding and
    /// showing the same content again allocates nothing.
    /// </summary>
    internal static class HiddenRenderers
    {
        private static readonly Stack<List<Renderer>> Pool = new Stack<List<Renderer>>();
        private static readonly List<Renderer> Scratch = new List<Renderer>();

        /// <summary>
        /// Hide the content under <paramref name="root"/>, recorded as <paramref name="id"/>: every renderer that is
        /// drawn is turned off and remembered. Again on content already hidden, it turns off renderers added since.
        /// </summary>
        internal static void Hide(Dictionary<ulong, List<Renderer>> hidden, ulong id, Transform root)
        {
            if (!hidden.TryGetValue(id, out var list))
            {
                list = Pool.Count > 0 ? Pool.Pop() : new List<Renderer>();
                hidden.Add(id, list);
            }
            if (root == null) return;
            Scratch.Clear();
            root.GetComponentsInChildren(true, Scratch);
            foreach (var renderer in Scratch)
            {
                if (renderer.forceRenderingOff) continue;
                renderer.forceRenderingOff = true;
                list.Add(renderer);
            }
            Scratch.Clear();
        }

        /// <summary>Show <paramref name="id"/>'s content again: turn on exactly the renderers <see cref="Hide"/> turned off.</summary>
        internal static void Show(Dictionary<ulong, List<Renderer>> hidden, ulong id)
        {
            if (!hidden.TryGetValue(id, out var list)) return;
            hidden.Remove(id);
            foreach (var renderer in list) if (renderer != null) renderer.forceRenderingOff = false;
            Recycle(list);
        }

        /// <summary>Forget <paramref name="id"/>'s content without touching its renderers: it is being destroyed.</summary>
        internal static void Forget(Dictionary<ulong, List<Renderer>> hidden, ulong id)
        {
            if (!hidden.TryGetValue(id, out var list)) return;
            hidden.Remove(id);
            Recycle(list);
        }

        /// <summary>Forget everything recorded in <paramref name="hidden"/>, for a new session.</summary>
        internal static void Clear(Dictionary<ulong, List<Renderer>> hidden)
        {
            foreach (var list in hidden.Values) Recycle(list);
            hidden.Clear();
        }

        private static void Recycle(List<Renderer> list)
        {
            list.Clear();
            Pool.Push(list);
        }
    }
}
