using System;

namespace Nebula.Hosting
{
    /// <summary>What the orchestrator asks a host to run. The host turns this into a process or a machine.</summary>
    public sealed class WorkerLaunchSpec
    {
        public string WorkerId;
        public uint Index;
        public ushort Port;
        /// <summary>Extra <c>-nebula-*</c> switches every role gets (control plane, gateway address, verbosity).</summary>
        public string CommonArgs = "";
    }

    public enum WorkerHandleState
    {
        /// <summary>Requested; the machine is booting or the process is starting. Not yet on the control plane.</summary>
        Launching,
        /// <summary>The host reports that the worker is running. The orchestrator still uses heartbeats to determine whether it is responsive.</summary>
        Running,
        /// <summary>The process exited or the machine is gone.</summary>
        Exited,
        /// <summary>
        /// Retired into the idle pool: the instance still exists and still costs what it costs, but the orchestrator
        /// deals it no containers. <see cref="IWorkerHost.Unpark"/> brings it back; the host drops it by itself once
        /// keeping it stops being free (see <see cref="HetznerWorkerHost"/>).
        /// </summary>
        Parked,
        /// <summary>The host could not launch it at all (no executable, cloud API error, quota).</summary>
        Failed,
    }

    /// <summary>One launched worker as seen by its host. Only ever touched from the main thread.</summary>
    public interface IWorkerHandle
    {
        string WorkerId { get; }
        WorkerHandleState State { get; }
        /// <summary>Short human description for logs and the dashboard: "pid 1234", "hetzner srv 5551 10.0.1.4".</summary>
        string Describe { get; }
        /// <summary>Address the worker was told to advertise, when the host knows it before the worker registers.</summary>
        string Address { get; }
        /// <summary>Why <see cref="State"/> is Exited/Failed, when known.</summary>
        string Reason { get; }
        /// <summary>
        /// While <see cref="State"/> is <see cref="WorkerHandleState.Parked"/>: how much longer the host will keep
        /// this instance before deleting it (the rest of the hour Hetzner has already charged for). Negative when the
        /// host does not park, or when it keeps parked instances indefinitely.
        /// </summary>
        float ParkedSecondsRemaining { get; }
    }

    /// <summary>
    /// Where worker instances live. The orchestrator decides <i>how many</i> workers to run and which containers
    /// they own; a host decides <i>where</i> one runs and how to start and stop it. The reference host launches
    /// local child processes (<see cref="ProcessWorkerHost"/>); cloud hosts create one VM per worker
    /// (<see cref="HetznerWorkerHost"/>). Liveness is judged from control-plane heartbeats regardless of host; the
    /// host's <see cref="IWorkerHandle.State"/> only lets the orchestrator react sooner when a process exits or a
    /// machine disappears. Implementations must be safe to call every frame; slow work happens off the main thread
    /// and is surfaced from <see cref="Tick"/>.
    /// </summary>
    public interface IWorkerHost : IDisposable
    {
        /// <summary>"process", "hetzner", ... shown on the dashboard.</summary>
        string Name { get; }

        /// <summary>
        /// Called once before the first launch. Hosts use it to look up cloud resources and to sweep leftovers from a
        /// previous orchestrator run (a crashed orchestrator must not leave paid machines behind).
        /// </summary>
        void Initialize(Action<string, string> log);

        /// <summary>True once <see cref="Initialize"/> finished its asynchronous part and launches can proceed.</summary>
        bool IsReady { get; }

        /// <summary>Set when <see cref="Initialize"/> failed permanently (bad token, missing network); the orchestrator reports it and launches nothing.</summary>
        string InitializationError { get; }

        IWorkerHandle Launch(WorkerLaunchSpec spec);

        /// <summary>Stop the worker immediately (crash semantics: kill the process, delete the machine).</summary>
        void Kill(IWorkerHandle handle);

        /// <summary>
        /// True when <see cref="Park"/> is cheaper than <see cref="Kill"/> plus a fresh <see cref="Launch"/>, so the
        /// orchestrator parks a retired worker into the idle pool instead of killing it. False on hosts where an
        /// instance costs nothing to recreate (<see cref="ProcessWorkerHost"/>) and on hosts written before parking
        /// existed (<see cref="WorkerHostBase"/> answers false), which keeps the old behaviour exactly.
        /// </summary>
        bool SupportsParking { get; }

        /// <summary>
        /// Roughly how long this host takes to get a freshly launched worker simulating: the cost of a cold start,
        /// used for the scale-to-zero warning and for the dashboard's idle-pool copy. Process host ~3 s, a cloud VM
        /// that boots an image and downloads a build ~75 s.
        /// </summary>
        float TypicalBootSeconds { get; }

        /// <summary>
        /// Retire the worker into the idle pool: the orchestrator has drained it and will deal it nothing more, but
        /// wants it back cheaply if load returns. A host that cannot park just kills the instance.
        /// </summary>
        /// <param name="idlePoolSeconds">
        /// How long the orchestrator would like it kept (<see cref="NebulaConfig.IdlePoolSeconds"/>); 0 means "as
        /// long as it is free", which is the host's own answer (Hetzner: the rest of the hour it has been billed).
        /// </param>
        void Park(IWorkerHandle handle, float idlePoolSeconds);

        /// <summary>
        /// Bring a parked instance back. True when the handle is <see cref="WorkerHandleState.Running"/> again and
        /// the worker it holds keeps its id and index; false when the host has already dropped it, in which case the
        /// orchestrator launches a fresh worker instead.
        /// </summary>
        bool Unpark(IWorkerHandle handle);

        /// <summary>Main thread, every frame: apply results of background work to the handles and refresh their state.</summary>
        void Tick();

        /// <summary>Extra host-specific fields for the dashboard's worker entry.</summary>
        void WriteHandleJson(IWorkerHandle handle, JsonWriter w);
    }

    /// <summary>
    /// Base class for hosts that do not park: <see cref="Park"/> kills, <see cref="Unpark"/> fails, and the
    /// orchestrator never parks into them. Deriving from this keeps a host source-compatible when
    /// <see cref="IWorkerHost"/> grows optional members.
    /// </summary>
    public abstract class WorkerHostBase : IWorkerHost
    {
        public abstract string Name { get; }
        public abstract bool IsReady { get; }
        public abstract string InitializationError { get; }
        public abstract void Initialize(Action<string, string> log);
        public abstract IWorkerHandle Launch(WorkerLaunchSpec spec);
        public abstract void Kill(IWorkerHandle handle);
        public abstract void Tick();
        public abstract void WriteHandleJson(IWorkerHandle handle, JsonWriter w);
        public abstract void Dispose();

        public virtual bool SupportsParking => false;
        public virtual float TypicalBootSeconds => 5f;
        public virtual void Park(IWorkerHandle handle, float idlePoolSeconds) => Kill(handle);
        public virtual bool Unpark(IWorkerHandle handle) => false;
    }
}
