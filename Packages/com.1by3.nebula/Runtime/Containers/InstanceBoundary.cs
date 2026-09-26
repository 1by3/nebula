using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Prepares a private area as players approach and crosses its bounds without changing their absolute pose.
    /// <para>
    /// The boundary works in the scope around it, its <see cref="HostInstanceId"/>: the public world when it stands in
    /// the shared scene, or the scope of the container it is parented under, such as a chunk of a scoped grid whose
    /// content the game builds at runtime. Only entities in that scope, or in the boundary's own instance, cross it,
    /// and an occupant who leaves returns to a container of that scope. Positions are compared in absolute
    /// coordinates, so a host scope with an origin frame of its own crosses at the same place on every worker.
    /// </para>
    /// </summary>
    public sealed class InstanceBoundary : MonoBehaviour
    {
        /// <summary>Private layout created at this component's world position. Must be available on workers.</summary>
        public InstanceTemplate Template;
        /// <summary>Crossing volume relative to the component's position. Rotation and scale do not transform the bounds.</summary>
        public Bounds Interior = new Bounds(Vector3.zero, new Vector3(10, 5, 10));
        /// <summary>Distance added on each side of Interior to begin preparing a crossing before entry.</summary>
        public float PreparationDistance = 8;
        /// <summary>Use the player's stable OwnerIdentity as the key when no ResolveKey callback is installed.</summary>
        public bool Personal = true;
        /// <summary>Key used when Personal is false and no ResolveKey callback is installed.</summary>
        public string SharedKey = "shared";
        /// <summary>Server-side destination key override, for housing assignments or party runs.</summary>
        public Func<NetworkIdentity, string> ResolveKey;
        /// <summary>Server-side admission check for entry into the resolved key. When unset, entry is allowed. Occupants can exit without passing this check.</summary>
        public Func<NetworkIdentity, string, bool> CanEnter;

        /// <summary>
        /// The instance origin is this boundary's absolute position rounded to this many meters, so every worker and
        /// every origin frame names the same instance bounds, however its transform's float numbers came out.
        /// </summary>
        public const double OriginResolution = 0.001;

        private sealed class Crossing
        {
            public InstanceTransfer Transfer;
            public Double3 LastPosition;
            public bool HasPosition;
        }
        private readonly Dictionary<ulong, Crossing> _crossings = new Dictionary<ulong, Crossing>();
        private static readonly HashSet<InstanceBoundary> Active = new HashSet<InstanceBoundary>();
        private Container _host;
        private bool _hostResolved;
        private bool _warnedPrepare, _warnedObserve;

        private void OnEnable()
        {
            Active.Add(this);
            _hostResolved = false;
        }

        private void OnDisable() { Active.Remove(this); _crossings.Clear(); }

        private void OnTransformParentChanged() => _hostResolved = false;

        /// <summary>
        /// The scope this boundary stands in: the <see cref="Container.InstanceId"/> of the nearest container among its
        /// parents (a scoped grid's chunk, when the game parents its content under <c>ChunkContext.Root</c>), or 0, the
        /// public world, when it has none.
        /// </summary>
        public ulong HostInstanceId
        {
            get
            {
                // A destroyed container compares equal to null while the reference is still set: look again.
                if (!_hostResolved || (_host == null && !ReferenceEquals(_host, null)))
                {
                    _host = transform.parent != null ? transform.parent.GetComponentInParent<Container>(true) : null;
                    _hostResolved = true;
                }
                return _host != null ? _host.InstanceId : 0;
            }
        }

        /// <summary>This boundary's position in absolute coordinates: its host scope's origin frame taken away.</summary>
        public Double3 AbsolutePosition => ContainerRegistry.ToAbsolutePrecise(transform.position, HostInstanceId);

        /// <summary>Where the instance is prepared: <see cref="AbsolutePosition"/> rounded to <see cref="OriginResolution"/>.</summary>
        public Double3 InstanceOrigin
        {
            get
            {
                var p = AbsolutePosition;
                return new Double3(Round(p.X), Round(p.Y), Round(p.Z));
            }
        }

        private static double Round(double v) => Math.Round(v / OriginResolution) * OriginResolution;

        /// <summary>An entity's position in absolute coordinates, read in its scope's own space (out of any physics frame).</summary>
        private static Double3 AbsoluteOf(NetworkIdentity entity) =>
            ContainerRegistry.ToAbsolutePrecise(entity.ToScope(entity.transform.position), entity.InstanceId);

        internal static void Tick(NebulaWorker worker, NetworkIdentity entity)
        {
            // A pinned entity keeps its container (docs/frame-bodies.md D8): no boundary moves it into another scope.
            if (entity.OwnerClientId == 0 || !entity.HasAuthority || entity.ContainerPinned) return;
            foreach (var boundary in Active) if (boundary != null) boundary.Cross(worker, entity);
        }

        private void Cross(NebulaWorker worker, NetworkIdentity entity)
        {
            if (Template == null) return;
            string key = ResolveKey != null ? ResolveKey(entity) : Personal ? entity.OwnerIdentity : SharedKey;
            if (string.IsNullOrEmpty(key)) return;
            ulong scope = NebulaWorker.InstanceKey(Template.TemplateId + "/" + key);
            ulong host = HostInstanceId;
            // Only the host scope's entities and this boundary's own occupants: a boundary on one world never catches
            // someone standing at the same numbers on another.
            if (entity.InstanceId != host && entity.InstanceId != scope) return;
            bool privateSide = entity.InstanceId == scope;
            var absolute = AbsoluteOf(entity);
            var local = (absolute - InstanceOrigin).ToVector3();
            bool inside = Interior.Contains(local);
            var nearby = Interior;
            nearby.Expand(Mathf.Max(0, PreparationDistance) * 2);
            if (!privateSide && !nearby.Contains(local)) { _crossings.Remove(entity.NetId); return; }
            if (!_crossings.TryGetValue(entity.NetId, out var crossing))
                _crossings.Add(entity.NetId, crossing = new Crossing());
            bool allowed = privateSide || CanEnter == null || CanEnter(entity, key);
            // Where the entity stands on the other side, in that scope's own frame.
            var there = ContainerRegistry.ToFrame(absolute, privateSide ? host : scope);
            Container destination = null;
            if (allowed)
            {
                // Leaving: a container of the host scope that holds the point, never the nearest box of some other
                // scope. None while that ground is not leased here yet: the occupant waits inside.
                if (privateSide) destination = ContainerRegistry.FindInSpace(there, null, host, entity, null);
                else destination = PrepareEntry(worker, key, host);
                if (destination != null && LeaseState.IsOwning(destination.LeaseState) &&
                    (crossing.Transfer == null || crossing.Transfer.Finished || crossing.Transfer.Destination != destination))
                    crossing.Transfer = worker.PrepareTransfer(entity, destination);
            }
            if (inside != privateSide)
            {
                if (allowed && worker.TryCommitTransfer(crossing.Transfer, there, entity.transform.rotation))
                    crossing.Transfer = null;
                else if (crossing.HasPosition)
                    // Held where it last stood, read back through its scope's frame as it is now: an origin shift
                    // while it waits moves the frame, not the entity.
                    entity.transform.position = entity.FromScope(ContainerRegistry.ToFrame(crossing.LastPosition, entity.InstanceId));
            }
            crossing.LastPosition = AbsoluteOf(entity);
            crossing.HasPosition = true;
        }

        /// <summary>Prepare the instance at this boundary's origin and return its entry part, or null while it isn't there yet or can't be prepared.</summary>
        private Container PrepareEntry(NebulaWorker worker, string key, ulong host)
        {
            if (host != 0 && Template.ObservePublic && !_warnedObserve)
            {
                _warnedObserve = true;
                NebulaLog.Warn($"InstanceBoundary '{name}' stands in a scoped grid, but its template '{Template.TemplateId}' has ObservePublic on: occupants would see the public world, not this scope. Turn ObservePublic off for boundaries in scoped grids.");
            }
            try
            {
                return worker.PrepareInstance(Template, key, InstanceOrigin)[0].Resolve();
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                // A template or key the control plane refuses must not throw out of the worker's tick: nobody crosses.
                if (!_warnedPrepare)
                {
                    _warnedPrepare = true;
                    NebulaLog.Warn($"InstanceBoundary '{name}' could not prepare '{Template.TemplateId}/{key}': {e.Message}");
                }
                return null;
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position + Interior.center, Interior.size);
        }
    }
}
