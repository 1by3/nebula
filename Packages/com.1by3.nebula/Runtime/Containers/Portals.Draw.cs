using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    public static partial class Portals
    {
        /// <summary>
        /// On a client: the scope the drawing is measured against, the <b>anchor</b> (<c>docs/portals.md</c> D40): the scope whose frame the
        /// local view's frame resolves to, which is the world the player first walked in from. Nothing of it is ever moved to draw.
        /// </summary>
        public static ulong Anchor => DrawnScopes.Anchor;

        /// <summary>
        /// On a client: the scopes drawn right now, the view first (<c>docs/portals.md</c> D40): the pawn's scope, every scope one open link
        /// away that the client may look through, the public world (0) and the scopes with an <see cref="InstanceScenes.KeepDrawn"/> request.
        /// Content of a scope the client looks through is shown without a <c>KeepDrawn</c> request.
        /// </summary>
        public static IReadOnlyList<ulong> DrawnScopeIds => Nebula.DrawnScopes.Drawn;

        /// <summary>
        /// A drawn scope's pose against the <see cref="Anchor"/>, composed in double through the link poses along the portals from the anchor
        /// (<c>docs/portals.md</c> D40, <see cref="ScopePose"/>). The identity for the anchor and for a scope nothing relates to it. This is
        /// where the scope is <i>drawn</i>: simulation, prediction and every pose a game reads stay in the scope's own upright space. While
        /// cameras draw, Nebula has posed the scope's root containers by it (and the cameras by the view's, when the view is not the anchor).
        /// </summary>
        public static ScopePose PoseOf(ulong scope) => Nebula.DrawnScopes.PoseOf(scope);

        /// <summary>
        /// A matrix that takes a point of <paramref name="scope"/>'s own upright space, in drawing space, to where it is drawn
        /// (<c>docs/portals.md</c> D41). For what a transform cannot carry: meshes a game draws itself with matrices it keeps
        /// (<c>Graphics.RenderMeshInstanced</c>, a scatter, a decal batch). Multiply the matrix you would draw an unposed instance with by this one
        /// (<c>RenderMatrixOf(scope) * instanceMatrix</c>), while <see cref="SceneRenderOrigin.Drawing"/> runs: "drawing space" is a scene position
        /// minus <see cref="SceneRenderOrigin.Offset"/>, which is what the roots Nebula posed are in. A matrix built from a transform read inside
        /// <c>Drawing</c> already includes the pose and must not be multiplied again. The identity for the anchor and for a scope nothing relates to it.
        /// </summary>
        public static Matrix4x4 RenderMatrixOf(ulong scope) => Nebula.DrawnScopes.RenderMatrixOf(scope);

        /// <summary>
        /// The rendering-layer bit (a single bit of <see cref="NebulaConfig.ScopeRenderingLayers"/>) a drawn scope holds, or 0 when slots are
        /// off, the scope is not drawn, or more scopes are drawn than there are bits (<c>docs/portals.md</c> D42). Nebula stamps this bit, and
        /// only this bit of the reserved mask, on its static content, its chunks' content and its entities' renderers; use it in a light's
        /// rendering-layer mask and a portal's masks.
        /// </summary>
        public static uint RenderingLayerOf(ulong scope)
        {
            Nebula.DrawnScopes.RefreshIfStale();
            return ScopeLayers.BitOf(scope);
        }

        /// <summary>
        /// Raised on a client when a scope gained a rendering-layer slot (arguments: the scope and its single layer bit) or lost it (the bit is
        /// 0), after Nebula restamped its renderers. A game re-aims its lights' rendering-layer masks here.
        /// </summary>
        public static event Action<ulong, uint> SlotChanged;

        internal static void RaiseSlotChanged(ulong scope, uint bit)
        {
            var handler = SlotChanged;
            if (handler == null) return;
            try { handler(scope, bit); }
            catch (Exception e) { NebulaLog.Error($"a Portals.SlotChanged handler threw: {e}"); }
        }

        /// <summary>
        /// Stamp the scope's rendering-layer bit on every renderer under <paramref name="root"/>: for content a game adds to a scope after Nebula
        /// stamped it (a prop built under a chunk's content root later, a renderer added to an entity). Does nothing when slots are off.
        /// Nothing outside the reserved bits changes.
        /// </summary>
        public static void StampRenderingLayer(Transform root, ulong scope) => ScopeLayers.StampTree(root, scope);
    }
}
