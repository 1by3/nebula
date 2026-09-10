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
        /// <summary>The host believes the worker is up (heartbeats are still the source of truth for liveness).</summary>
        Running,
        /// <summary>The process exited or the machine is gone.</summary>
        Exited,
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

        /// <summary>Main thread, every frame: apply results of background work to the handles and refresh their state.</summary>
        void Tick();

        /// <summary>Extra host-specific fields for the dashboard's worker entry.</summary>
        void WriteHandleJson(IWorkerHandle handle, JsonWriter w);
    }
}
