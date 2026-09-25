using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Put this on a prefab or a scene object to make the entity outlive the process that simulates it. The worker's
    /// <see cref="NebulaPersistence"/> checkpoints every authoritative entity that carries one, and brings it back
    /// when a worker gains the lease of the container it was saved in.
    /// <para>
    /// What is saved: the entity's prefab (or scene id), its pose in its container, every
    /// <see cref="PersistAttribute"/> NetworkVariable and whatever each behaviour writes in
    /// <see cref="NetworkBehaviour.WritePersistentState"/> (see <see cref="PersistentStateCodec"/>). What identifies
    /// it across runs and workers is <see cref="Key"/>, which travels with the entity through handover, so a save
    /// written by one worker is the same record the next worker updates.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PersistentEntity : NetworkBehaviour
    {
        [SerializeField]
        [Tooltip("Stable identity of this entity in the store. Leave empty to generate one at the first save ('<prefab>:<guid>'); a scene object defaults to 'scene:<sceneId>'.")]
        private string _key = "";

        [Tooltip("Seconds between checkpoints of this entity. 0 = NebulaConfig.PersistenceCheckpointSeconds.")]
        public float CheckpointSeconds;
        [Tooltip("Save the entity's pose in its container, and put it back there on restore.")]
        public bool PersistPose = true;
        [Tooltip("Save the entity's velocity as well (a rolling barrel, a drifting ship).")]
        public bool PersistVelocity;

        /// <summary>
        /// Stable identity of this entity in the store. Settable by game code until the first save (a player's
        /// inventory chest, a record the game restores by name); after that it is fixed, because the record in the
        /// store is keyed by it.
        /// </summary>
        public string Key
        {
            get => _key;
            set
            {
                string v = value ?? "";
                if (v == _key) return;
                if (HasBeenSaved)
                {
                    NebulaLog.Warn($"PersistentEntity.Key on {name} is already saved as '{_key}'; ignoring the change to '{v}'");
                    return;
                }
                _key = v;
            }
        }

        /// <summary><see cref="Time.unscaledTime"/> of the last accepted checkpoint, 0 when never saved in this process.</summary>
        public float LastSavedAt { get; internal set; }
        /// <summary>Something changed since the last checkpoint: the entity is due a save at the next opportunity.</summary>
        public bool IsDirty { get; internal set; }
        /// <summary>A record for this entity has been written (here or by the worker that handed it over).</summary>
        public bool HasBeenSaved { get; internal set; }
        /// <summary>Store version of the last record this process wrote or received with the handover. Informational.</summary>
        public ulong LastSavedVersion { get; internal set; }

        /// <summary>Pose at the last checkpoint, so the service can save again once the entity has actually moved.</summary>
        internal Vector3 LastSavedPosition;
        internal Quaternion LastSavedRotation = Quaternion.identity;

        /// <summary>
        /// <see cref="Time.unscaledTime"/> when this entity first became dirty since its last checkpoint. 0 while
        /// not dirty. Feeds <see cref="NebulaPersistence.OldestDirtyAgeSeconds"/>, the telemetry proxy for how much
        /// of the documented durability window (docs/persistence-durability.md) is in use right now.
        /// </summary>
        internal float DirtySince;

        /// <summary>
        /// Brought back from the store at a new epoch and not saved under it yet. The first checkpoint is taken as
        /// soon as the save budget allows, so the record carries the new epoch and a late save from the worker that
        /// held the previous life is refused by the store (<c>docs/persistence-durability.md</c> D9). Set by a
        /// restore and by <see cref="NebulaPersistence.Apply"/> on an entity this worker owns or is about to spawn.
        /// </summary>
        internal bool StampPending;

        /// <summary>
        /// The lowest epoch this entity may spawn at, because a record was applied to it before it was spawned
        /// (<see cref="NebulaPersistence.Apply"/>): the record's epoch + 1. The worker spawns it at that epoch or
        /// higher, whichever spawn call the game makes, and clears it. 0 when no record was applied.
        /// </summary>
        internal uint AdoptedEpoch;

        /// <summary>
        /// The entity holds nothing worth keeping right now: a checkpoint deletes its record instead of writing one.
        /// Set by Nebula's chunk state entity while its map is empty (docs/chunk-state.md D5), so a chunk whose
        /// last entry was cleared or expired costs no record, even if it is released before the entity despawns.
        /// </summary>
        internal bool DiscardRecord;

        /// <summary>Ask for a checkpoint soon (coalesced with every other change since the last save).</summary>
        public void MarkDirty()
        {
            if (!IsDirty) DirtySince = Time.unscaledTime;
            IsDirty = true;
        }

        /// <summary>
        /// The key this entity saves under, generating one the first time it is needed: <c>scene:&lt;sceneId&gt;</c>
        /// for a scene object, <c>&lt;prefab&gt;:&lt;guid&gt;</c> for anything spawned from a prefab.
        /// </summary>
        public string EnsureKey()
        {
            if (!string.IsNullOrEmpty(_key)) return _key;
            var identity = Identity;
            if (identity != null && identity.IsSceneEntity) _key = $"scene:{identity.SceneId}";
            else _key = $"{CleanName()}:{Guid.NewGuid():N}";
            return _key;
        }

        private string CleanName()
        {
            string n = name;
            int clone = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return clone > 0 ? n.Substring(0, clone) : n;
        }

        /// <summary>The identity travels with authority: the next worker updates the same record instead of writing a second one.</summary>
        public override void WriteHandoverState(NetworkWriter writer)
        {
            writer.WriteString(_key);
            writer.WriteBool(HasBeenSaved);
            writer.WriteULong(LastSavedVersion);
        }

        /// <inheritdoc/>
        public override void ReadHandoverState(NetworkReader reader)
        {
            _key = reader.ReadString();
            HasBeenSaved = reader.ReadBool();
            LastSavedVersion = reader.ReadULong();
        }

        /// <summary>Version of the <see cref="WritePersistentState"/> chunk: 1 holds the entity's runtime extent.</summary>
        private const byte PersistVersion = 1;

        /// <summary>
        /// Saves the entity's extent when it was set at runtime (<see cref="NetworkIdentity.ExtentChangedAtRuntime"/>),
        /// so a structure edited since it spawned comes back measured by its edited box (<c>docs/entity-extents.md</c>
        /// D7). Writes nothing otherwise, so an entity with an authored extent, or none, saves the record it always did.
        /// </summary>
        public override void WritePersistentState(NetworkWriter writer)
        {
            var identity = Identity;
            if (identity == null || !identity.ExtentChangedAtRuntime) return;
            identity.TryGetExtent(out var extent);
            writer.WriteByte(PersistVersion);
            writer.WriteByte((byte)identity.ExtentSource);
            writer.WriteVector3(extent.center);
            writer.WriteVector3(extent.size);
        }

        /// <summary>Restores the runtime extent <see cref="WritePersistentState"/> saved, before the entity spawns.</summary>
        public override void ReadPersistentState(NetworkReader reader)
        {
            if (reader.Remaining == 0) return;
            byte version = reader.ReadByte();
            if (version > PersistVersion)
            {
                NebulaLog.Warn($"{this}: the saved extent is version {version} and this build reads {PersistVersion}; the prefab's extent is kept");
                return;
            }
            var source = (EntityExtentSource)reader.ReadByte();
            var center = reader.ReadVector3();
            var size = reader.ReadVector3();
            Identity?.ApplyCarriedExtent(source, new Bounds(center, size));
        }

        public override string ToString() => $"PersistentEntity({(_key == "" ? "unkeyed" : _key)})";
    }
}
