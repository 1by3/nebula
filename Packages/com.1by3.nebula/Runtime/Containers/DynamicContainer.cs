using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Obsolete, and does nothing. A <see cref="Container"/> on an entity's root, next to its
    /// <see cref="NetworkIdentity"/>, is carried by that entity on its own now (<see cref="Container.FrameMode"/> is
    /// <see cref="ContainerFrameMode.Entity"/>): it registers when the entity spawns and unregisters when it despawns,
    /// and <see cref="NetworkIdentity.Carried"/> reaches it. Remove this component with
    /// <b>Nebula &gt; Migrate &gt; Remove DynamicContainer</b>, which strips it from every prefab and open scene. It is
    /// kept for one release so projects still load, and is no longer a <see cref="NetworkBehaviour"/>, so it takes no
    /// behaviour slot on the entity.
    /// </summary>
    [Obsolete("A Container on an entity's root is carried by the entity without this component. Remove it with Nebula > Migrate > Remove DynamicContainer.")]
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class DynamicContainer : MonoBehaviour
    {
        /// <summary>The <see cref="Container"/> on the same object.</summary>
        public Container Volume => GetComponent<Container>();

        /// <summary>The entities inside the carried container on this process. Use <see cref="Container.Entities"/>.</summary>
        public IReadOnlyList<NetworkIdentity> Contents => Volume != null ? Volume.Entities : (IReadOnlyList<NetworkIdentity>)Array.Empty<NetworkIdentity>();

        /// <summary>Whether the container on this object is registered as carried. Use <see cref="Container.IsDynamic"/>.</summary>
        public bool IsRegistered => Volume != null && Volume.IsDynamic;

        /// <summary>The component on the carrier that owns <paramref name="body"/>, or null. Use <see cref="Container.OfRigidbody"/>.</summary>
        public static DynamicContainer OfRigidbody(Rigidbody body)
        {
            var carried = Container.OfRigidbody(body);
            return carried != null ? carried.GetComponent<DynamicContainer>() : null;
        }

        /// <summary>Use <see cref="Container.IsCarrierGeometry"/>.</summary>
        public static bool IsCarrierGeometry(Collider collider) => Container.IsCarrierGeometry(collider);
    }
}
