namespace Nebula
{
    /// <summary>Whether an instance part's content is still loading, ready, or failed. <see cref="IInstanceContentLoader.State"/> reports it.</summary>
    public enum InstanceContentState
    {
        /// <summary>The content is still being built or loaded. A crossing into it waits.</summary>
        Loading,
        /// <summary>The content is complete. A crossing into it can be acknowledged.</summary>
        Ready,
        /// <summary>The content cannot be completed. A crossing into it is refused, as for a missing content resource.</summary>
        Failed,
    }

    /// <summary>
    /// Content of an instance part that finishes loading after <see cref="InstanceScenes.Prepare"/> returns: geometry
    /// a component builds over several frames, or assets it loads asynchronously.
    /// <para>
    /// Implement it on a component of the part's content prefab (<see cref="InstanceTemplate.Part.ContentResource"/>),
    /// or on one the game adds under the part's container in <c>Awake</c>. When the content is created, Nebula
    /// collects the active components under the container that implement this interface. A portal link
    /// (<see cref="Portal"/>) opens, and a client says it holds the far window (<see cref="MsgId.PortalWarm"/>), and
    /// a client acknowledges a crossing prepared with <see cref="NebulaWorker.PrepareTransfer"/> into the part, and the
    /// destination worker marks itself ready, only once every one of them reports
    /// <see cref="InstanceContentState.Ready"/>. One that reports <see cref="InstanceContentState.Failed"/> refuses the
    /// crossing and keeps a link from opening. Content with none of them is ready as soon as it exists.
    /// </para>
    /// <para>
    /// Nebula reads <see cref="State"/> once a frame on a client, and once a tick on a worker, only while a
    /// preparation or a link waits for it. Keep it cheap and free of allocations. A crossing prepared with
    /// <see cref="NebulaWorker.PrepareTransfer"/> still expires at its preparation timeout; a <see cref="Portal"/> link
    /// keeps waiting, reporting <see cref="PortalWait.FarWorkerContent"/>, and stops polling once it is open.
    /// </para>
    /// </summary>
    public interface IInstanceContentLoader
    {
        /// <summary>Whether this content is still loading, complete, or cannot be completed.</summary>
        InstanceContentState State { get; }

        /// <summary>
        /// How far loading has got, from 0 to 1. Only <see cref="InstanceScenes.ContentProgress"/> reads it, to show a
        /// loading indicator; <see cref="State"/> alone decides readiness.
        /// </summary>
        float Progress { get; }
    }
}
