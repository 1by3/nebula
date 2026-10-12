using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Rendering-layer slots (<c>docs/portals.md</c> D42). <see cref="NebulaConfig.ScopeRenderingLayers"/> reserves a mask of rendering-layer
    /// bits; each drawn scope (<see cref="DrawnScopes"/>) holds one of them while it is drawn, and Nebula stamps that bit, and only that bit of the
    /// reserved mask, on the renderers it manages: an instance's static content, a scoped grid chunk's content under its root, and an entity's
    /// renderers. The game owns lights, look and portal masks. Clients only.
    /// </summary>
    internal static class ScopeLayers
    {
        private static readonly Dictionary<ulong, uint> Slots = new Dictionary<ulong, uint>();
        private static readonly List<ulong> Scratch = new List<ulong>();
        private static readonly List<Renderer> RendererScratch = new List<Renderer>();
        private static uint _mask;
        private static uint _used;
        private static bool _warnedFull;

        /// <summary>The reserved bits, 0 when slots are off or this is not a client.</summary>
        internal static uint Mask => NebulaRuntime.IsClient && NebulaRuntime.Config != null ? NebulaRuntime.Config.ScopeRenderingLayers : 0u;

        /// <summary>Whether slots are on.</summary>
        internal static bool Enabled => Mask != 0;

        /// <summary>The single bit a scope holds, or 0.</summary>
        internal static uint BitOf(ulong scope) => Slots.Count > 0 && Slots.TryGetValue(scope, out var bit) ? bit : 0u;

        /// <summary>How many slots are held.</summary>
        internal static int Held => Slots.Count;

        /// <summary>Give each scope of <paramref name="drawn"/> a slot, the view (first) before the rest, and free the slot of every scope no longer in it.</summary>
        internal static void Sync(List<ulong> drawn)
        {
            uint mask = Mask;
            Scratch.Clear();
            if (mask != _mask)
            {
                // The reserved mask itself changed: every slot is void, and every managed renderer loses the old bits.
                foreach (var kv in Slots) Scratch.Add(kv.Key);
                uint old = _mask;
                Slots.Clear();
                _used = 0;
                _mask = mask;
                _warnedFull = false;
                for (int i = 0; i < Scratch.Count; i++) Announce(Scratch[i], 0u, old);
                Scratch.Clear();
            }
            if (mask == 0) return;
            foreach (var kv in Slots) if (!drawn.Contains(kv.Key)) Scratch.Add(kv.Key);
            for (int i = 0; i < Scratch.Count; i++)
            {
                ulong scope = Scratch[i];
                _used &= ~Slots[scope];
                Slots.Remove(scope);
                Announce(scope, 0u, mask);
            }
            Scratch.Clear();
            for (int i = 0; i < drawn.Count; i++)
            {
                ulong scope = drawn[i];
                if (Slots.ContainsKey(scope)) continue;
                uint free = mask & ~_used;
                if (free == 0)
                {
                    if (!_warnedFull)
                    {
                        _warnedFull = true;
                        NebulaLog.Warn("more scopes are drawn than NebulaConfig.ScopeRenderingLayers has bits for; the extra ones draw with no slot");
                    }
                    continue;
                }
                uint bit = free & (~free + 1u); // the lowest free bit
                Slots[scope] = bit;
                _used |= bit;
                Announce(scope, bit, mask);
            }
        }

        private static void Announce(ulong scope, uint bit, uint reserved)
        {
            Restamp(scope, bit, reserved);
            Portals.RaiseSlotChanged(scope, bit);
        }

        /// <summary>Stamp every managed renderer of a scope again, after its slot changed.</summary>
        private static void Restamp(ulong scope, uint bit, uint reserved)
        {
            InstanceScenes.StampContent(scope, bit, reserved);
            Nebula.World.NebulaChunks.StampRoots(scope, bit, reserved);
            foreach (var entity in NetworkIdentity.Live)
                if (entity != null && entity.InstanceId == scope) entity.StampRenderingLayer(bit, reserved);
        }

        /// <summary>The scope's current bit in the reserved mask, for stamping something new in it.</summary>
        internal static void StampTree(Transform root, ulong scope)
        {
            uint mask = _mask;
            if (mask == 0 || root == null) return;
            StampTree(root, BitOf(scope), mask);
        }

        /// <summary>Set the renderers under <paramref name="root"/> to carry exactly <paramref name="bit"/> of <paramref name="reserved"/>, leaving every other bit alone.</summary>
        internal static void StampTree(Transform root, uint bit, uint reserved)
        {
            if (root == null || reserved == 0) return;
            RendererScratch.Clear();
            root.GetComponentsInChildren(true, RendererScratch);
            for (int i = 0; i < RendererScratch.Count; i++) Stamp(RendererScratch[i], bit, reserved);
            RendererScratch.Clear();
        }

        /// <summary>Set one renderer's bits of <paramref name="reserved"/> to <paramref name="bit"/>; nothing outside the reserved bits changes.</summary>
        internal static void Stamp(Renderer renderer, uint bit, uint reserved)
        {
            if (renderer == null) return;
            uint now = renderer.renderingLayerMask;
            uint next = (now & ~reserved) | bit;
            if (next != now) renderer.renderingLayerMask = next;
        }

        internal static void Reset()
        {
            Slots.Clear();
            Scratch.Clear();
            _used = 0;
            _mask = 0;
            _warnedFull = false;
        }
    }
}
