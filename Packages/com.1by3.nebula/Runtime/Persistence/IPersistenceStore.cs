using System;
using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// One persisted entity as the store holds it: enough to bring the entity back on any worker (prefab or scene
    /// object, container and pose, authority epoch) plus the opaque state blob the entity itself wrote
    /// (<see cref="PersistentStateCodec"/>: opted-in NetworkVariables and behaviour state, keyed by name).
    /// Plain data on purpose: nothing here depends on the storage backend.
    /// </summary>
    public sealed class PersistedEntityRecord
    {
        /// <summary>Stable identity of the entity across runs and workers (<see cref="PersistentEntity.Key"/>).</summary>
        public string Key = "";
        /// <summary>Prefab index at save time (<see cref="NetworkPrefabs"/>); <see cref="ushort.MaxValue"/> for a scene entity.</summary>
        public ushort PrefabId = ushort.MaxValue;
        /// <summary>Prefab asset name, so a restore can recover from a reordered prefab list.</summary>
        public string PrefabName = "";
        /// <summary>Non-zero: the record belongs to a scene entity with this <see cref="NetworkIdentity.SceneId"/>.</summary>
        public uint SceneId;
        /// <summary>Static container the entity was in (its <see cref="Container.ContainerId"/>), or "" when it was in none or inside a carrier.</summary>
        public string ContainerId = "";
        /// <summary>When the entity was inside a dynamic container: the persistence key of the carrier. "" otherwise.</summary>
        public string CarrierKey = "";
        /// <summary>Pose in the container's local space (world space when there was no container).</summary>
        public Vector3 LocalPosition;
        public Quaternion LocalRotation = Quaternion.identity;
        public Vector3 Velocity;
        /// <summary>Authority epoch at save time. The store rejects a save whose epoch is older than what it holds.</summary>
        public uint Epoch;
        /// <summary>Spawned with <see cref="NebulaWorker.SpawnServerDriven"/>.</summary>
        public bool ServerDriven;
        /// <summary>A client owned the entity when it was saved. Such records are not restored automatically (there is no client to own the result); the game restores them on demand.</summary>
        public bool Owned;
        /// <summary>GameObject name at save time (cosmetic).</summary>
        public string Name = "";
        /// <summary>Opaque state blob written by <see cref="PersistentStateCodec"/>.</summary>
        public byte[] State = Array.Empty<byte>();
        /// <summary>Bumped by the store on every accepted save.</summary>
        public ulong Version;
        public DateTime SavedAt;
        /// <summary>Id of the worker that wrote the record.</summary>
        public string SavedBy = "";

        public PersistedEntityRecord Clone()
        {
            var c = (PersistedEntityRecord)MemberwiseClone();
            c.State = State != null ? (byte[])State.Clone() : Array.Empty<byte>();
            return c;
        }

        public override string ToString() => $"{Key}({(SceneId != 0 ? "scene " + SceneId : PrefabName)},{(CarrierKey != "" ? "in " + CarrierKey : ContainerId)},e{Epoch},v{Version})";
    }

    /// <summary>
    /// Long-term storage for entities that opted into persistence (<see cref="PersistentEntity"/>). One store per
    /// process; the worker's <see cref="NebulaPersistence"/> writes checkpoints through it and reads containers back
    /// when it gains a lease. Use <see cref="SpacetimePersistenceStore"/> for a mesh (a SpacetimeDB database of its
    /// own next to the control plane), <see cref="LocalPersistenceStore"/> for single-process runs and tests.
    /// <para>
    /// The contract is request/response on purpose (no subscriptions), so a relational backend fits behind it. Every
    /// callback lands on the main thread from <see cref="Tick"/>. Writes are fire-and-forget: a save is accepted by
    /// the backend when its epoch is not older than the stored one, otherwise dropped, and the caller never waits.
    /// The store is never on the per-tick path: if it is unreachable the mesh keeps simulating and saves are retried
    /// on the next checkpoint.
    /// </para>
    /// </summary>
    public interface IPersistenceStore : IDisposable
    {
        /// <summary>Connected and ready to answer loads. Saves issued before this are queued until it is true.</summary>
        bool IsConnected { get; }
        /// <summary>Short backend name for logs and the dashboard ("spacetime", "local", "memory").</summary>
        string Backend { get; }
        /// <summary>How many records the store holds as far as this process knows (-1 when unknown).</summary>
        int KnownCount { get; }

        void Connect();
        /// <summary>Pump the backend and deliver callbacks on the main thread. Call once per frame.</summary>
        void Tick();

        /// <summary>Insert or update. The backend keeps the record only when <see cref="PersistedEntityRecord.Epoch"/> is at least the stored epoch.</summary>
        void Save(PersistedEntityRecord record);
        /// <summary>Delete the record for <paramref name="key"/>, if any.</summary>
        void Delete(string key);

        /// <summary>The record for <paramref name="key"/> (null when there is none).</summary>
        void Load(string key, Action<PersistedEntityRecord> onLoaded);
        /// <summary>Every record whose <see cref="PersistedEntityRecord.ContainerId"/> is <paramref name="containerId"/> and that is not inside a carrier.</summary>
        void LoadContainer(string containerId, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded);
        /// <summary>Every record whose <see cref="PersistedEntityRecord.CarrierKey"/> is <paramref name="carrierKey"/>.</summary>
        void LoadCarried(string carrierKey, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded);
        /// <summary>Every record that matches <paramref name="predicate"/>. For tools and game directors, not for the per-lease restore path.</summary>
        void LoadWhere(Func<PersistedEntityRecord, bool> predicate, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded);

        /// <summary>Delete every record (dashboard / dev reset).</summary>
        void Clear();
    }
}
