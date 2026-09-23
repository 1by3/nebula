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
        public void Forget()
        {
            IsRegistered = false;
            _listedInDocument = null;
        }

        /// <summary>The document this worker last saw its own row in (<see cref="IControlPlane.DocumentId"/>), or null before it has.</summary>
        private string _listedInDocument;

        /// <summary>
        /// Whether the control plane has declared this worker dead: a document this worker has been listed in no
        /// longer lists it. The orchestrator removes the row of a worker whose heartbeats stopped and deals its
        /// containers to someone else, so the leases this worker still simulates may be restored elsewhere from their
        /// last checkpoint (<c>docs/persistence-durability.md</c> D10). A row missing from a <i>replacement</i>
        /// document is a control plane that came back empty, not a verdict: that is <see cref="ReclaimContainers"/>'
        /// case (<c>docs/control-plane-availability.md</c> D1b). Call before <see cref="RegisterAgainIfForgotten"/>,
        /// which can put the row straight back on an in-process control plane.
        /// </summary>
        public bool IsDeclaredDead(IControlPlane controlPlane)
        {
            if (!IsRegistered || controlPlane == null) return false;
            string document = controlPlane.DocumentId ?? "";
            if (controlPlane.FindWorker(WorkerId) != null) { _listedInDocument = document; return false; }
            return _listedInDocument == document;
        }

        /// <summary>
        /// Whether this worker must stop acting as the owner of its leases: it has been declared dead
        /// (<see cref="IsDeclaredDead"/>), or its own row, as its mirror of the control plane shows it, has not had a
        /// heartbeat for more than <paramref name="timeoutSeconds"/> — the same test, on the same clock, the
        /// orchestrator applies before it declares a worker dead. A fenced worker writes nothing to the persistence
        /// store, reads nothing back and hands nothing over; it keeps simulating, and it is unfenced by its next
        /// heartbeat landing (<c>docs/persistence-durability.md</c> D8). False before the worker has seen its own row
        /// in the current document: nothing is leased to it there yet.
        /// </summary>
        public bool IsFenced(IControlPlane controlPlane, float timeoutSeconds)
        {
            if (!IsRegistered || controlPlane == null) return false;
            var row = controlPlane.FindWorker(WorkerId);
            if (row == null) return IsDeclaredDead(controlPlane);
            _listedInDocument = controlPlane.DocumentId ?? "";
            return !controlPlane.IsWorkerAlive(row, timeoutSeconds);
        }

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
                var placement = ContainerRegistry.PlacementOf(c);
                leases.Add(new LeaseInfo
                {
                    ContainerId = c.ContainerId, HasBounds = true,
                    ParentId = placement.ParentId, Center = placement.Center, BoundsSize = placement.Size,
                    Authority = placement.Authority, OwnPhysicsFrame = placement.OwnPhysicsFrame, FrameInterest = placement.FrameInterest,
                    Instance = c.Instance,
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
                    controlPlane.EnsureRuntimeContainer(c.ContainerId, ContainerRegistry.PlacementOf(c), WorkerId, c.Instance);
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
