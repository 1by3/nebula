using System;
using System.Globalization;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// Which kind of container an <see cref="EntityLocation.ContainerId"/> names. Nebula reads the kind from the
    /// form of the id alone (see <see cref="EntityLocation.ContainerKind"/>), so any process can classify a location
    /// without holding the container.
    /// </summary>
    public enum LocationContainerKind : byte
    {
        /// <summary>The id is empty: the entity is in no container and its pose is in world space.</summary>
        None,
        /// <summary>A container authored into a scene or baked into the world manifest, named by its authored <see cref="Container.ContainerId"/>.</summary>
        Static,
        /// <summary>A container the game registered while the mesh runs (<see cref="ContainerRegistry.RegisterRuntime"/>), named <c>rt_&lt;id&gt;</c>.</summary>
        Runtime,
        /// <summary>A container carried by an entity (on the entity's root, <see cref="NetworkIdentity.Carried"/>), named <c>label#netId</c>.</summary>
        Dynamic,
    }

    /// <summary>
    /// Where an entity durably is, independent of which worker holds it: an opaque scope key, a container id and a
    /// pose in that container's local space. A <see cref="NetworkIdentity"/> reports its current location as
    /// <see cref="NetworkIdentity.Location"/> and a <see cref="PersistedEntityRecord"/> its saved one as
    /// <see cref="PersistedEntityRecord.Location"/>; both are this type, and two processes given the same value
    /// resolve the same container (<see cref="Resolve"/>).
    /// <para>
    /// The scope key is the string the game chose for the entity's simulation scope: <see cref="PublicScope"/>
    /// (empty) for the public world, or the instance key (<c>TemplateId/key</c>, as passed to
    /// <see cref="NebulaWorker.PrepareInstance"/>) for a private instance. Nebula never parses or interprets it;
    /// it only compares it with ordinal string equality. The container id is the container's string id
    /// (<see cref="Container.ContainerId"/>), whose form tells the kind (<see cref="ContainerKind"/>). Equality is
    /// exact: ordinal on the strings and per component on the pose, never approximate.
    /// </para>
    /// <para>
    /// The value does not carry the authority epoch, the owning worker or the wire index of the container, which
    /// are the parts of an entity's placement that change on a handover, a lease change or a rebuild. The design of
    /// record is <c>docs/location-contract.md</c>.
    /// </para>
    /// </summary>
    public readonly struct EntityLocation : IEquatable<EntityLocation>
    {
        /// <summary>The scope key of the public world: the empty string.</summary>
        public const string PublicScope = "";

        private readonly string _scopeKey;
        private readonly string _containerId;

        /// <summary>Opaque key of the entity's simulation scope. <see cref="PublicScope"/> for the public world. Never null.</summary>
        public string ScopeKey => _scopeKey ?? PublicScope;
        /// <summary>String id of the container the pose is expressed in (<see cref="Container.ContainerId"/>), or "" when there is none. Never null.</summary>
        public string ContainerId => _containerId ?? "";
        /// <summary>Position in the container's local space (world space when <see cref="ContainerId"/> is empty).</summary>
        public readonly Vector3 LocalPosition;
        /// <summary>Rotation in the container's local space (world space when <see cref="ContainerId"/> is empty).</summary>
        public readonly Quaternion LocalRotation;

        /// <summary>A location in no container, at the world origin, in the public world.</summary>
        public static readonly EntityLocation None = new EntityLocation(PublicScope, "", default, Quaternion.identity);

        /// <summary>Build a location from its three parts. Null strings read as empty.</summary>
        public EntityLocation(string scopeKey, string containerId, Vector3 localPosition, Quaternion localRotation)
        {
            _scopeKey = scopeKey ?? PublicScope;
            _containerId = containerId ?? "";
            LocalPosition = localPosition;
            LocalRotation = localRotation;
        }

        /// <summary>
        /// The location of a pose inside <paramref name="container"/>: the container's scope key
        /// (<see cref="Container.ScopeKey"/>) and id, and the pose as given. A null container is "no container".
        /// </summary>
        public static EntityLocation Of(Container container, Vector3 localPosition, Quaternion localRotation)
        {
            return container == null
                ? new EntityLocation(PublicScope, "", localPosition, localRotation)
                : new EntityLocation(container.ScopeKey, container.ContainerId, localPosition, localRotation);
        }

        /// <summary>The location is in the public world (its scope key is empty).</summary>
        public bool IsPublic => ScopeKey.Length == 0;
        /// <summary>The location names a container (its container id is not empty).</summary>
        public bool HasContainer => ContainerId.Length > 0;

        /// <summary>
        /// The kind of container <see cref="ContainerId"/> names, from the id's form: empty is
        /// <see cref="LocationContainerKind.None"/>, <c>rt_</c> followed by a decimal 64-bit number is
        /// <see cref="LocationContainerKind.Runtime"/>, an id containing <c>#</c> is
        /// <see cref="LocationContainerKind.Dynamic"/>, anything else is <see cref="LocationContainerKind.Static"/>.
        /// </summary>
        public LocationContainerKind ContainerKind
        {
            get
            {
                string id = ContainerId;
                if (id.Length == 0) return LocationContainerKind.None;
                if (id.IndexOf('#') >= 0) return LocationContainerKind.Dynamic;
                if (id.Length > 3 && id.StartsWith("rt_", StringComparison.Ordinal)
                    && ulong.TryParse(id.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    return LocationContainerKind.Runtime;
                return LocationContainerKind.Static;
            }
        }

        /// <summary>
        /// The container this location names in this process, or null when there is none here. The lookup is by
        /// <see cref="ContainerId"/> in <see cref="ContainerRegistry"/>, which every role (worker, client, gateway,
        /// orchestrator) fills from the same scene, manifest and lease rows; a container found under the id but
        /// belonging to another scope (<see cref="Container.ScopeKey"/> differs from <see cref="ScopeKey"/>) is not
        /// returned. Null also when the container has not arrived yet: a dynamic container arrives with its
        /// carrier, a runtime container with its lease.
        /// </summary>
        public Container Resolve()
        {
            if (!HasContainer) return null;
            var container = ContainerRegistry.FindById(ContainerId);
            return container != null && string.Equals(container.ScopeKey, ScopeKey, StringComparison.Ordinal) ? container : null;
        }

        /// <summary>
        /// Serialize as <c>string scope_key, string container_id, vector3 local_position, quaternion local_rotation</c>
        /// (the primitive encodings of the wire protocol). The bytes are the same on every process and every
        /// release within a protocol major.
        /// </summary>
        public void Write(NetworkWriter writer)
        {
            writer.WriteString(ScopeKey);
            writer.WriteString(ContainerId);
            writer.WriteVector3(LocalPosition);
            writer.WriteQuaternion(LocalRotation);
        }

        /// <summary>Read a location written by <see cref="Write"/>.</summary>
        public static EntityLocation Read(NetworkReader reader)
        {
            string scope = reader.ReadString();
            string container = reader.ReadString();
            var position = reader.ReadVector3();
            var rotation = reader.ReadQuaternion();
            return new EntityLocation(scope, container, position, rotation);
        }

        /// <summary>Exact equality: ordinal on both strings, per component on the pose.</summary>
        public bool Equals(EntityLocation other)
        {
            return string.Equals(ScopeKey, other.ScopeKey, StringComparison.Ordinal)
                && string.Equals(ContainerId, other.ContainerId, StringComparison.Ordinal)
                && LocalPosition.x == other.LocalPosition.x && LocalPosition.y == other.LocalPosition.y && LocalPosition.z == other.LocalPosition.z
                && LocalRotation.x == other.LocalRotation.x && LocalRotation.y == other.LocalRotation.y
                && LocalRotation.z == other.LocalRotation.z && LocalRotation.w == other.LocalRotation.w;
        }

        public override bool Equals(object obj) => obj is EntityLocation other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = StringComparer.Ordinal.GetHashCode(ScopeKey);
                h = h * 397 ^ StringComparer.Ordinal.GetHashCode(ContainerId);
                h = h * 397 ^ LocalPosition.x.GetHashCode();
                h = h * 397 ^ LocalPosition.y.GetHashCode();
                h = h * 397 ^ LocalPosition.z.GetHashCode();
                h = h * 397 ^ LocalRotation.x.GetHashCode();
                h = h * 397 ^ LocalRotation.y.GetHashCode();
                h = h * 397 ^ LocalRotation.z.GetHashCode();
                h = h * 397 ^ LocalRotation.w.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(EntityLocation a, EntityLocation b) => a.Equals(b);
        public static bool operator !=(EntityLocation a, EntityLocation b) => !a.Equals(b);

        /// <summary><c>scope:container@(x, y, z)/(x, y, z, w)</c>, with <c>public</c> for the public world and <c>none</c> for no container. Invariant culture, round-trip float format.</summary>
        public override string ToString()
        {
            var ic = CultureInfo.InvariantCulture;
            return string.Format(ic, "{0}:{1}@({2}, {3}, {4})/({5}, {6}, {7}, {8})",
                IsPublic ? "public" : ScopeKey, HasContainer ? ContainerId : "none",
                LocalPosition.x.ToString("R", ic), LocalPosition.y.ToString("R", ic), LocalPosition.z.ToString("R", ic),
                LocalRotation.x.ToString("R", ic), LocalRotation.y.ToString("R", ic), LocalRotation.z.ToString("R", ic), LocalRotation.w.ToString("R", ic));
        }
    }
}
