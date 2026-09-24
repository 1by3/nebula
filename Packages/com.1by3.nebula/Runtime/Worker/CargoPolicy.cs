namespace Nebula
{
    /// <summary>
    /// What happens to the entities riding in a carrier's container when the carrier is despawned with
    /// <see cref="NebulaWorker.Despawn(NetworkIdentity, bool, CargoPolicy)"/> (<c>docs/frame-bodies.md</c> D5).
    /// </summary>
    public enum CargoPolicy
    {
        /// <summary>
        /// Put every rider down in the container the carrier was in, at the pose it has now. What
        /// <see cref="NebulaWorker.Despawn(NetworkIdentity, bool)"/> always does.
        /// </summary>
        SetDown,

        /// <summary>
        /// Keep the persistent cargo aboard: every rider this worker is authoritative for that has a
        /// <see cref="PersistentEntity"/>, no owning client, and sits directly in the box of the carrier or of another
        /// stowed rider is checkpointed aboard and despawned, deepest first, so its record names its carrier and it
        /// comes back when the carrier is restored. Everything else aboard (a player's pawn, a transient entity) is put
        /// down as with <see cref="SetDown"/>. Needs a persistent carrier despawned with <c>keepPersisted</c>.
        /// </summary>
        Stow,
    }
}
