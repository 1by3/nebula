using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>Reusable geometry and container bounds for a private area. Bounds are relative to the instance origin.</summary>
    [CreateAssetMenu(menuName = "Nebula/Instance Template")]
    public sealed class InstanceTemplate : ScriptableObject
    {
        /// <summary>One static runtime container in the instance layout.</summary>
        [Serializable]
        public sealed class Part
        {
            /// <summary>Nonempty identifier unique within the template. Participates in the stable runtime container ID.</summary>
            public string Id = "interior";
            /// <summary>Positive-size, axis-aligned bounds relative to the instance origin.</summary>
            public Bounds Bounds = new Bounds(Vector3.zero, new Vector3(20, 10, 20));
            /// <summary>Static prefab path under Resources, without an extension. Empty means no static content. Content must contain no NetworkIdentity components.</summary>
            [Tooltip("Resources path of static geometry, positioned relative to the center of this part's bounds.")]
            public string ContentResource = "";
        }

        /// <summary>Stable layout identifier combined with the game-selected instance key to derive a private scope.</summary>
        public string TemplateId = "interior";
        /// <summary>Containers in the layout. The boundary uses the first part as its entry destination.</summary>
        public Part[] Parts = { new Part() };
        /// <summary>Allow occupants to receive public entities within PublicView. Does not enable cross-instance interaction.</summary>
        public bool ObservePublic = true;
        /// <summary>
        /// Keep the scope an occupant came from loaded while it is inside: the world around the place it entered, the
        /// public world or a scoped grid's chunks, stays resident on its client, hidden, so leaving rebuilds nothing.
        /// Grants no visibility of that scope's entities. Off by default.
        /// </summary>
        [Tooltip("Keep the area the player entered from loaded, hidden, while inside, so leaving rebuilds nothing.")]
        public bool ObserveHost;
        /// <summary>Axis-aligned public observation bounds relative to the instance origin.</summary>
        public Bounds PublicView = new Bounds(Vector3.zero, new Vector3(60, 30, 60));
    }
}
