using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// Registers a worker with the control plane and restores missing container assignments after the
    /// control-plane document is replaced. Existing assignments are preserved.
    /// </summary>
    public sealed class WorkerRegistration
    {
        public string WorkerId = "";
        public uint WorkerIndex;
        public string Address = "";
        public ushort Port;

        /// <summary>Whether registration has been requested since construction or the last <see cref="Forget"/> call.</summary>
        public bool IsRegistered { get; private set; }

        /// <summary>Total registration requests issued by this instance, including requests queued for remote delivery.</summary>
        public int RegistrationCount { get; private set; }

        /// <summary>
        /// Requests the initial worker registration when the control plane is connected.
        /// Does nothing if registration has already been requested.
        /// </summary>
        /// <returns>True if this call issued a registration request.</returns>
        public bool Register(IControlPlane controlPlane)
        {
            if (IsRegistered || controlPlane == null || !controlPlane.IsConnected) return false;
            controlPlane.RegisterWorker(WorkerId, WorkerIndex, Address, Port);
            IsRegistered = true;
            RegistrationCount++;
            // The document this worker registered into is the one its leases will be dealt in; nothing to reclaim there.
            _reconciledDocument = controlPlane.DocumentId ?? "";
            return true;
        }

        /// <summary>
        /// Requests registration again if the connected control plane no longer lists this worker.
        /// Call from <see cref="IControlPlane.Changed"/> after initial registration; polling can issue duplicate
        /// requests while a remote control plane is waiting for the updated document.
        /// </summary>
        /// <returns>True if this call issued a registration request. The worker can then send a heartbeat immediately.</returns>
        public bool RegisterAgainIfForgotten(IControlPlane controlPlane)
        {
            if (!IsRegistered || controlPlane == null || !controlPlane.IsConnected) return false;
            if (controlPlane.FindWorker(WorkerId) != null) return false;
            controlPlane.RegisterWorker(WorkerId, WorkerIndex, Address, Port);
            RegistrationCount++;
            return true;
        }

        /// <summary>Clears the local registration flag without unregistering the worker on the control plane.</summary>
        public void Forget() => IsRegistered = false;

        /// <summary>
        /// Requests missing leases, which assign containers to workers, for containers this worker still owns.
        /// Call on each control-plane change before applying the updated leases. Reconciliation runs once for
        /// each new <see cref="IControlPlane.DocumentId"/>; removals within the same document are preserved.
        /// <para>Existing leases and dynamic containers are skipped. Runtime containers are reclaimed with their
        /// bounds and instance metadata; other containers are ensured and assigned to this worker.</para>
        /// </summary>
        /// <returns>The number of containers for which this call issued reclaim requests.</returns>
        public int ReclaimContainers(IControlPlane controlPlane)
        {
            if (!IsRegistered || controlPlane == null || !controlPlane.IsConnected) return 0;
            string document = controlPlane.DocumentId ?? "";
            // A claim is answered by its row appearing; until then it is outstanding and must not be written again.
            if (_claimed.Count > 0) _claimed.RemoveWhere(id => controlPlane.FindLease(id) != null);
            if (document == _reconciledDocument) return 0;
            _reconciledDocument = document;
            _claimed.Clear(); // claims against the old document were answered by it or died with it
            return Reclaim(controlPlane, ContainerRegistry.All) + Reclaim(controlPlane, ContainerRegistry.Runtime);
        }

        /// <summary>The document this worker last reconciled its containers against (<see cref="IControlPlane.DocumentId"/>).</summary>
        private string _reconciledDocument = "";

        /// <summary>Containers claimed whose lease row has not come back yet: one write each, not one per change.</summary>
        private readonly HashSet<string> _claimed = new HashSet<string>();

        // A remote write is only acknowledged when its lease appears in the mirror. Keep the existing
        // runtime objects until then: pruning them would evacuate their occupants and unload their scenes.
        // Only claims from a new document are protected; ordinary same-document retirement still prunes.
        internal void SyncRuntime(IControlPlane controlPlane)
        {
            if (_claimed.Count == 0)
            {
                ContainerRegistry.SyncRuntime(controlPlane.Leases);
                return;
            }
            var leases = new List<LeaseInfo>(controlPlane.Leases);
            foreach (var c in ContainerRegistry.Runtime)
            {
                if (c == null || !_claimed.Contains(c.ContainerId) || controlPlane.FindLease(c.ContainerId) != null) continue;
                var bounds = ContainerRegistry.ToAbsolute(c.WorldBounds, c.InstanceId);
                leases.Add(new LeaseInfo
                {
                    ContainerId = c.ContainerId, HasBounds = true,
                    BoundsCenter = bounds.center, BoundsSize = bounds.size, Instance = c.Instance,
                });
            }
            ContainerRegistry.SyncRuntime(leases);
        }

        private int Reclaim(IControlPlane controlPlane, IReadOnlyList<Container> containers)
        {
            int written = 0;
            for (int i = 0; i < containers.Count; i++)
            {
                var c = containers[i];
                // A dynamic container follows its carrier; its lease is written by whoever owns the carrier, and
                // the carrier's own container is in this same sweep.
                if (c == null || c.IsDynamic || c.OwnerWorkerId != WorkerId) continue;
                if (controlPlane.FindLease(c.ContainerId) != null) continue;
                if (!_claimed.Add(c.ContainerId)) continue; // asked already; the write is in flight
                if (c.IsRuntime)
                    controlPlane.EnsureRuntimeContainer(c.ContainerId, ContainerRegistry.ToAbsolute(c.WorldBounds, c.InstanceId), WorkerId, c.Instance);
                else
                {
                    controlPlane.EnsureContainer(c.ContainerId);
                    controlPlane.AssignContainer(c.ContainerId, WorkerId);
                }
                written++;
            }
            return written;
        }
    }
}
