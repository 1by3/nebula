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
        /// <list type="bullet">
        /// <item>A baked container is claimed with <see cref="IControlPlane.EnsureContainer"/> +
        /// <see cref="IControlPlane.AssignContainer"/>, which starts its epoch again at 1. An epoch only ever has
        /// to be increasing <i>within</i> one control-plane document, and this document is a new one: every peer
        /// reads the epochs out of it, none of them remembers the old document's.</item>
        /// <item>A runtime container is re-announced with its box
        /// (<see cref="IControlPlane.EnsureRuntimeContainer"/>), because nothing else in the mesh knows the box:
        /// the worker that asked for it is the only copy of that fact.</item>
        /// </list>
        /// Returns how many rows were written.
        /// </summary>
        public static int ReclaimContainers(IControlPlane controlPlane, string workerId)
        {
            if (controlPlane == null || !controlPlane.IsConnected || string.IsNullOrEmpty(workerId)) return 0;
            return Reclaim(controlPlane, workerId, ContainerRegistry.All) +
                   Reclaim(controlPlane, workerId, ContainerRegistry.Runtime);
        }

        private static int Reclaim(IControlPlane controlPlane, string workerId, IReadOnlyList<Container> containers)
        {
            int written = 0;
            for (int i = 0; i < containers.Count; i++)
            {
                var c = containers[i];
                // A dynamic container follows its carrier; its lease is written by whoever owns the carrier, and
                // the carrier's own container is in this same sweep.
                if (c == null || c.IsDynamic || c.OwnerWorkerId != workerId) continue;
                if (controlPlane.FindLease(c.ContainerId) != null) continue;
                if (c.IsRuntime)
                    controlPlane.EnsureRuntimeContainer(c.ContainerId, ContainerRegistry.ToAbsolute(c.WorldBounds, c.InstanceId), workerId, c.Instance);
                else
                {
                    controlPlane.EnsureContainer(c.ContainerId);
                    controlPlane.AssignContainer(c.ContainerId, workerId);
                }
                written++;
            }
            return written;
        }
    }
}
