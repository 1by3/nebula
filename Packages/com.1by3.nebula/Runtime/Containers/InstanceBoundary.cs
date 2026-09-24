using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>Prepares a private area as players approach and crosses its bounds without changing their world pose.</summary>
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

        private sealed class Crossing
        {
            public InstanceTransfer Transfer;
            public Vector3 LastPosition;
            public bool HasPosition;
        }
        private readonly Dictionary<ulong, Crossing> _crossings = new Dictionary<ulong, Crossing>();
        private static readonly HashSet<InstanceBoundary> Active = new HashSet<InstanceBoundary>();
        private void OnEnable() => Active.Add(this);
        private void OnDisable() { Active.Remove(this); _crossings.Clear(); }

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
            if (entity.InstanceId != 0 && entity.InstanceId != scope) return;
            bool privateSide = entity.InstanceId == scope;
            var local = entity.transform.position - transform.position;
            bool inside = Interior.Contains(local);
            var nearby = Interior;
            nearby.Expand(Mathf.Max(0, PreparationDistance) * 2);
            if (!privateSide && !nearby.Contains(local)) { _crossings.Remove(entity.NetId); return; }
            if (!_crossings.TryGetValue(entity.NetId, out var crossing))
                _crossings.Add(entity.NetId, crossing = new Crossing());
            bool allowed = privateSide || CanEnter == null || CanEnter(entity, key);
            Container destination = null;
            if (allowed)
            {
                if (privateSide) destination = ContainerRegistry.Find(entity.transform.position, subject: entity);
                else
                {
                    var parts = worker.PrepareInstance(Template, key, transform.position);
                    destination = parts[0].Resolve();
                }
                if (destination != null && LeaseState.IsOwning(destination.LeaseState) &&
                    (crossing.Transfer == null || crossing.Transfer.Finished || crossing.Transfer.Destination != destination))
                    crossing.Transfer = worker.PrepareTransfer(entity, destination);
            }
            if (inside != privateSide)
            {
                if (allowed && worker.TryCommitTransfer(crossing.Transfer, entity.transform.position, entity.transform.rotation))
                    crossing.Transfer = null;
                else if (crossing.HasPosition) entity.transform.position = crossing.LastPosition;
            }
            crossing.LastPosition = entity.transform.position;
            crossing.HasPosition = true;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position + Interior.center, Interior.size);
        }
    }
}
