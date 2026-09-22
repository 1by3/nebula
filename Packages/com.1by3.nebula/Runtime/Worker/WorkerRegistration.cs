using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// A worker's registration row on the control plane, and what has to be said again when that row is gone.
    /// <para>
    /// A control plane that restarts <i>with</i> its storage needs none of this: every row comes back and the
    /// mesh never notices. A control plane that comes back <b>empty</b> — a fresh orchestrator with
    /// <c>-nebula-reset</c>, a database restored from a backup taken before this mesh started, a failover to a
    /// replica that never had the document — does: the rows describing which processes exist and what they
    /// simulate live only in the running processes, and a worker that registered once at startup would sit
    /// there simulating containers nobody can route to. See <c>docs/control-plane-availability.md</c> D1.
    /// </para>
    /// <para>
    /// <see cref="NebulaGateway"/> already had half of this shape (it re-registers from
    /// <c>OnControlPlaneChanged</c> when its own row has gone); this is the same rule for a worker, plus the part
    /// a gateway does not need — re-claiming the containers, because a worker is the only process that knows it
    /// is still simulating them.
    /// </para>
    /// </summary>
    public sealed class WorkerRegistration
    {
        public string WorkerId = "";
        public uint WorkerIndex;
        public string Address = "";
        public ushort Port;

        /// <summary>The row has been asked for at least once since this process started.</summary>
        public bool IsRegistered { get; private set; }

        /// <summary>How many times the row has been written: 1 at startup, one more per re-registration.</summary>
        public int RegistrationCount { get; private set; }

        /// <summary>
        /// The first registration, from the worker's update loop. Does nothing once the row has been asked for:
        /// noticing that it went away is <see cref="RegisterAgainIfForgotten"/>'s job, and only it can tell the
        /// difference between "not there yet" and "not there any more".
        /// </summary>
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
        /// Call this when the mirrored control-plane document <b>changed</b> — never on a timer. A
        /// <see cref="RemoteControlPlane"/> learns of its own registration only when the next document arrives, so
        /// a poll would re-register on every frame of the round trip; a change means the document that just landed
        /// is one this worker is not in. Registering twice is harmless either way (the row is keyed by id), which
        /// is what makes the rare double write on an unrelated change a non-event.
        /// <para>Returns true when it wrote the row again, which is the caller's cue to
        /// <see cref="ReclaimContainers"/> and heartbeat at once.</para>
        /// </summary>
        public bool RegisterAgainIfForgotten(IControlPlane controlPlane)
        {
            if (!IsRegistered || controlPlane == null || !controlPlane.IsConnected) return false;
            if (controlPlane.FindWorker(WorkerId) != null) return false;
            controlPlane.RegisterWorker(WorkerId, WorkerIndex, Address, Port);
            RegistrationCount++;
            return true;
        }

        /// <summary>Forget the row without touching the control plane (the process is shutting down).</summary>
        public void Forget() => IsRegistered = false;

        /// <summary>
        /// Put back the lease rows for every container this worker is still simulating and that the control plane
        /// no longer has a row for. Only the missing ones: a row that is there is the control plane's to decide,
        /// and a worker that overwrote a lease it had lost would take a container back off whoever was given it.
        /// <para>
        /// Call it on every control-plane change; it decides for itself when a missing row means <i>lost</i>. The
        /// signal is <see cref="IControlPlane.DocumentId"/>, not the worker's own row: a mirror flushes writes it
        /// queued while the orchestrator was away, so a replacement can have this worker's registration back —
        /// from a queued write — in the very document that first shows the leases gone, and a reclaim hung off
        /// "my row went missing" strands the containers for good. A document this worker has not reconciled with
        /// yet is reconciled once, whatever else it contains. A row missing from a document it <i>has</i> reconciled
        /// with was removed on purpose — a retiring scope, a runtime container the orchestrator dropped — and is
        /// left alone, because a worker that put it back would be fighting the orchestrator's own lifecycle
        /// (<c>docs/control-plane-availability.md</c> D1b).
        /// </para>
        /// <list type="bullet">
        /// <item>A baked container is claimed with <see cref="IControlPlane.EnsureContainer"/> +
        /// <see cref="IControlPlane.AssignContainer"/>, which starts its epoch again at 1. An epoch only ever has
        /// to be increasing <i>within</i> one control-plane document, and this document is a new one: every peer
        /// reads the epochs out of it, none of them remembers the old document's.</item>
        /// <item>A runtime container is re-announced with its box
        /// (<see cref="IControlPlane.EnsureRuntimeContainer"/>), because nothing else in the mesh knows the box:
        /// the worker that asked for it is the only copy of that fact.</item>
        /// </list>
        /// Returns how many rows were written this call.
        /// </summary>
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
