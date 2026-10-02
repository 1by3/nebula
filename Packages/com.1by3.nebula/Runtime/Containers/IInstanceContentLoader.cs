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
    /// collects the active components under the container that implement this interface. A client then acknowledges
    /// a crossing into the part, and the destination worker marks itself ready, only once every one of them reports
    /// <see cref="InstanceContentState.Ready"/>. One that reports <see cref="InstanceContentState.Failed"/> refuses the
    /// crossing. Content with none of them is ready as soon as it is created, as before.
    /// </para>
    /// <para>
    /// Nebula reads <see cref="State"/> once a frame on a client, and once a tick on a worker, only while a
    /// preparation waits for it. Keep it cheap and free of allocations. A crossing still expires at its preparation
    /// timeout (<see cref="NebulaWorker.PrepareTransfer"/>); an <see cref="InstanceBoundary"/> then prepares it again.
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
