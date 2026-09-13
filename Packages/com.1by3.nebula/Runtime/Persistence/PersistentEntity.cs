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

        /// <summary>Ask for a checkpoint soon (coalesced with every other change since the last save).</summary>
        public void MarkDirty() => IsDirty = true;

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

        public override string ToString() => $"PersistentEntity({(_key == "" ? "unkeyed" : _key)})";
    }
}
