namespace Nebula
{
    /// <summary>
    /// Where an <see cref="InstanceBoundary"/> crossing stands: ready to commit, or the first reason it is not.
    /// <see cref="InstanceBoundary.TryGetPreparation"/> reports it.
    /// </summary>
    public enum InstanceCrossingStatus
    {
        /// <summary>The boundary is not preparing a crossing for this entity: it is out of range, in another scope, or not an authoritative player entity on this worker.</summary>
        None,
        /// <summary><see cref="InstanceBoundary.RequireRequest"/> is on and no <see cref="InstanceBoundary.Request"/> for this entity is live.</summary>
        NotRequested,
        /// <summary><see cref="InstanceBoundary.CanEnter"/> refused entry.</summary>
        Refused,
        /// <summary>The destination container is not there yet: the instance's lease has not arrived, or, leaving, no container of the host scope that holds the point is leased.</summary>
        NoDestination,
        /// <summary>The destination container exists, but no worker holds an active lease on it yet.</summary>
        DestinationNotLeased,
        /// <summary>The worker that owns the destination has not acknowledged the preparation yet; its content may still be loading.</summary>
        PendingWorker,
        /// <summary>This worker owns the destination, and its content is still loading (<see cref="IInstanceContentLoader"/>).</summary>
        PendingContent,
        /// <summary>The entity's owning client has not acknowledged the preparation yet; its content may still be loading.</summary>
        PendingClient,
        /// <summary>The preparation failed. <see cref="InstanceCrossingState.Error"/> says why. The boundary prepares again once it expires.</summary>
        Failed,
        /// <summary>The crossing is prepared and can commit when the entity crosses the boundary's face.</summary>
        Ready,
    }

    /// <summary>
    /// The state of one entity's crossing through an <see cref="InstanceBoundary"/>, as
    /// <see cref="InstanceBoundary.TryGetPreparation"/> and <see cref="InstanceBoundary.CrossingReady"/> report it.
    /// <para>
    /// A ready crossing is not a reservation: the commit still checks admission, the entity's authority and the
    /// destination's lease, as <see cref="NebulaWorker.TryCommitTransfer"/> does.
    /// </para>
    /// </summary>
    public readonly struct InstanceCrossingState
    {
        /// <summary>Whether the crossing is ready, or the first reason it is not.</summary>
        public InstanceCrossingStatus Status { get; }
        /// <summary>True when the entity is inside the boundary's instance and the crossing leads back out to the host scope; false when it leads in.</summary>
        public bool Leaving { get; }
        /// <summary>Why the preparation failed, when <see cref="Status"/> is <see cref="InstanceCrossingStatus.Failed"/>; otherwise null.</summary>
        public string Error { get; }
        /// <summary>Whether <see cref="Status"/> is <see cref="InstanceCrossingStatus.Ready"/>.</summary>
        public bool IsReady => Status == InstanceCrossingStatus.Ready;

        internal InstanceCrossingState(InstanceCrossingStatus status, bool leaving, string error)
        {
            Status = status;
            Leaving = leaving;
            Error = error;
        }

        /// <inheritdoc/>
        public override string ToString() => Error == null ? $"{Status}{(Leaving ? " (leaving)" : "")}" : $"{Status}{(Leaving ? " (leaving)" : "")}: {Error}";
    }
}
