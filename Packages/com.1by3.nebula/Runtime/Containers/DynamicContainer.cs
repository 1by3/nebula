using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Makes the entity this sits on carry a <see cref="Container"/> of its own: a ship with an interior, a train
    /// car, a lift, a space station that drifts. The container exists wherever the carrier does (it is created when
    /// the entity spawns on a process and removed when it despawns there), it moves with the carrier's transform,
    /// and it is owned by whichever worker is authoritative for the carrier: there is no lease on the control plane
    /// for it. Entities inside it are parented under the carrier and replicated in its local space, so a full ship
    /// crossing a seam is one entity changing containers; everyone aboard is handed over with it, in the same tick,
    /// and their coordinates never change.
    /// <para>
    /// Put it on the entity's root next to the <see cref="NetworkIdentity"/>, with the <see cref="Container"/> that
    /// describes the interior box (in the root's local space). The root transform is the container's frame; a
    /// frame on a child object is not supported, because the gateway must be able to position the contents from
    /// the carrier's pose alone. On the wire the container is named by the carrier's net id
    /// (<see cref="ContainerRef"/>), so it needs no index and every process resolves it as soon as it has the carrier.
    /// </para>
    /// <para>
    /// Nesting works as for static containers: the smallest box holding a point wins, so a shuttle parked inside a
    /// carrier's hangar is a container inside a container, and the carrier's own position is resolved in the
    /// container around it, never in the box it carries. The carrier's <see cref="NetworkIdentity.Container"/> is
    /// what it sits in; <see cref="Volume"/> is what it carries.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(Container))]
    [DisallowMultipleComponent]
    public sealed class DynamicContainer : NetworkBehaviour
    {
        private static readonly Dictionary<Rigidbody, DynamicContainer> ByBody = new Dictionary<Rigidbody, DynamicContainer>();

        private Container _volume;

        /// <summary>The container this entity carries (the <see cref="Container"/> component on the same object).</summary>
        public Container Volume => _volume != null ? _volume : (_volume = GetComponent<Container>());

        /// <summary>The entities currently inside the carried container on this process (authoritative, ghosts or client copies).</summary>
        public IReadOnlyList<NetworkIdentity> Contents => Volume.Entities;

        /// <summary>Registered with the registry: <see cref="ContainerRegistry.Resolve(ContainerRef)"/> finds it by the carrier's net id.</summary>
        public bool IsRegistered => Volume.IsDynamic && Volume.Carrier == Identity;

        /// <summary>
        /// The dynamic container whose carrier owns <paramref name="body"/>, or null. Game movement code that treats
        /// colliders without a Rigidbody as the level can use this to walk on a vehicle's floor as well.
        /// </summary>
        public static DynamicContainer OfRigidbody(Rigidbody body)
        {
            return body != null && ByBody.TryGetValue(body, out var dc) ? dc : null;
        }

        /// <summary>Whether <paramref name="collider"/> belongs to a dynamic container's carrier (the hull of a vehicle, the floor of a lift).</summary>
        public static bool IsCarrierGeometry(Collider collider)
        {
            return collider != null && collider.attachedRigidbody != null && ByBody.ContainsKey(collider.attachedRigidbody);
        }

        private void Awake()
        {
            if (Identity != null && Identity.transform != transform)
                NebulaLog.Error($"DynamicContainer on '{name}' must sit on the entity root (the NetworkIdentity's object): the root transform is the container's frame");
        }

        public override void OnNetworkSpawn()
        {
            ContainerRegistry.RegisterDynamic(Volume, Identity);
            var body = GetComponent<Rigidbody>();
            if (body != null) ByBody[body] = this;
        }

        public override void OnNetworkDespawn()
        {
            var body = GetComponent<Rigidbody>();
            if (body != null) ByBody.Remove(body);
            ContainerRegistry.UnregisterDynamic(Volume);
        }

        private void OnDestroy()
        {
            // Destroyed without a despawn (scene torn down, play mode stopped): leave nothing behind in the registry.
            if (_volume != null && _volume.IsDynamic) ContainerRegistry.UnregisterDynamic(_volume);
            var body = GetComponent<Rigidbody>();
            if (body != null) ByBody.Remove(body);
        }
    }
}
