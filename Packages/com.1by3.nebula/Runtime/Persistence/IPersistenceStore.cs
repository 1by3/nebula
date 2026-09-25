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
        /// <summary>
        /// Opaque scope key of the scope the entity was in (<see cref="NetworkIdentity.ScopeKey"/> at save time):
        /// <see cref="EntityLocation.PublicScope"/> (empty) for the public world, the instance key inside an
        /// instance. A record written before the key was recorded reads as the public world.
        /// </summary>
        public string ScopeKey = "";
        /// <summary>Static or runtime container the entity was in (its <see cref="Container.ContainerId"/>), or "" when it was in none or inside a carrier.</summary>
        public string ContainerId = "";
        /// <summary>When the entity was inside a dynamic container: the persistence key of the carrier. "" otherwise.</summary>
        public string CarrierKey = "";
        /// <summary>Pose in the container's local space (world space when there was no container).</summary>
        public Vector3 LocalPosition;
        public Quaternion LocalRotation = Quaternion.identity;

        /// <summary>
        /// The saved location as the contract's triple (<see cref="EntityLocation"/>): <see cref="ScopeKey"/>,
        /// <see cref="ContainerId"/> and the local pose. For an entity saved inside a carrier the container id is
        /// empty and <see cref="CarrierKey"/> names the carrier instead, because a dynamic container's id lives only
        /// as long as its carrier's net id. Setting it writes the three fields back and leaves <see cref="CarrierKey"/> alone.
        /// </summary>
        public EntityLocation Location
        {
            get => new EntityLocation(ScopeKey, ContainerId, LocalPosition, LocalRotation);
            set
            {
                ScopeKey = value.ScopeKey;
                ContainerId = value.ContainerId;
                LocalPosition = value.LocalPosition;
                LocalRotation = value.LocalRotation;
            }
        }
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
    /// when it gains leases. In a mesh the orchestrator owns the store (SQLite or PostgreSQL through the standalone
    /// services' <c>SqlPersistenceStore</c>, next to the control plane in the same database) and workers reach it
    /// through <see cref="RemotePersistenceStore"/>; <see cref="LocalPersistenceStore"/> serves single-process runs
    /// and tests.
    /// <para>
    /// The contract is request/response on purpose (no subscriptions), so a relational backend fits behind it. Every
    /// callback lands on the main thread from <see cref="Tick"/>. Writes are fire-and-forget (<see cref="WhenWritten"/> is the barrier for the few callers that must wait): a save is accepted by
    /// the backend when its epoch is not older than the stored one, otherwise dropped, and the caller never waits.
    /// The store is never on the per-tick path: if it is unreachable the mesh keeps simulating and saves are retried
    /// on the next checkpoint.
    /// </para>
    /// <para>
    /// Every callback runs exactly once. A store that gives up on a read answers it with the empty result and sets
    /// <see cref="PersistenceAnswer.Failed"/> for the duration of the callback; it never leaves a caller waiting.
    /// </para>
    /// </summary>
    public interface IPersistenceStore : IDisposable
    {
        /// <summary>Connected and ready to answer loads. Saves issued before this are queued until it is true.</summary>
        bool IsConnected { get; }
        /// <summary>Short backend name for logs and the dashboard ("sqlite", "postgres", "remote", "local", "memory").</summary>
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
        /// <summary>
        /// Calls <paramref name="onWritten"/> on the main thread once every save and delete issued before this call has
        /// reached the backend. It says the writes were delivered, not that each was kept (a stale epoch is still
        /// dropped): follow it with one <see cref="Load"/> when the outcome matters, instead of polling. When the store
        /// gave up on a write issued since the previous barrier, or on the barrier itself, the callback still runs,
        /// with <see cref="PersistenceAnswer.Failed"/> set.
        /// </summary>
        void WhenWritten(Action onWritten);

        /// <summary>The record for <paramref name="key"/> (null when there is none).</summary>
        void Load(string key, Action<PersistedEntityRecord> onLoaded);
        /// <summary>
        /// The records of several containers in one read: for each id in <paramref name="containerIds"/>, every
        /// record whose <see cref="PersistedEntityRecord.ContainerId"/> is that id and that is not inside a carrier.
        /// <paramref name="onLoaded"/> runs once, on the main thread, with an entry for every distinct id asked for
        /// (an empty list when nothing is saved there).
        /// <para>
        /// This is how a worker restores the containers it gains: however many leases arrive at once,
        /// <see cref="NebulaPersistence"/> asks for them in a few bounded reads
        /// (<see cref="NebulaPersistence.MaxContainersPerRestoreLoad"/> ids each, at most
        /// <see cref="NebulaPersistence.MaxRestoreLoadsInFlight"/> at a time) instead of one read per container. Ask
        /// for one id to read one box. A store may split a longer list into several backend queries
        /// (<see cref="PersistenceHost.MaxContainersPerLoad"/> ids each) and still answers once.
        /// </para>
        /// </summary>
        void LoadContainers(IReadOnlyList<string> containerIds, Action<IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>>> onLoaded);
        /// <summary>
        /// What several carriers hold, in one read: for each key in <paramref name="carrierKeys"/>, every record
        /// whose <see cref="PersistedEntityRecord.CarrierKey"/> is that key. <paramref name="onLoaded"/> runs once, on
        /// the main thread, with an entry for every distinct key asked for (an empty list when nothing is saved
        /// aboard; always empty for the empty key, which is no carrier).
        /// <para>
        /// This is how a worker brings back what rode in the carriers it spawns: however many carriers spawn at once
        /// (a container restore that brings back a hangar full of vehicles, a re-deal), <see cref="NebulaPersistence"/>
        /// asks for them in a few bounded reads (<see cref="NebulaPersistence.MaxCarriersPerRestoreLoad"/> keys each,
        /// at most <see cref="NebulaPersistence.MaxRestoreLoadsInFlight"/> at a time) instead of one read per
        /// carrier. Ask for one key to read one carrier. A store may split a longer list into several backend
        /// queries (<see cref="PersistenceHost.MaxCarriersPerLoad"/> keys each) and still answers once.
        /// </para>
        /// </summary>
        void LoadCarried(IReadOnlyList<string> carrierKeys, Action<IReadOnlyDictionary<string, IReadOnlyList<PersistedEntityRecord>>> onLoaded);
        /// <summary>
        /// Every record that matches <paramref name="predicate"/>. For tools and game directors, not for the
        /// per-lease restore path. This is the <b>offline read</b>: the records of a scope nothing is simulating are
        /// ordinary records and can be read, and written, without activating it (<c>docs/lifecycle-hooks.md</c> §5).
        /// </summary>
        void LoadWhere(Func<PersistedEntityRecord, bool> predicate, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded);

        /// <summary>
        /// How many records the store holds for a scope, <b>without reading them</b>. <paramref name="scopeKey"/> is
        /// matched exactly (<see cref="EntityLocation.PublicScope"/>, the empty key, is the public world) and
        /// <paramref name="containerId"/> narrows the count to one container when it is non-empty; records inside a
        /// carrier are counted like any other. Cheap on purpose — a <c>COUNT</c> on the backend, never a list — so
        /// that "is there anything saved here?" can be asked on the path that brings a scope to life. The answer
        /// lands on the main thread like every other read. Behind
        /// <c>NebulaLifecycle.OnScopeActivating</c>'s <c>hasRecords</c>.
        /// </summary>
        void CountRecords(string scopeKey, string containerId, Action<int> onCounted);

        /// <summary>Delete every record (dashboard / dev reset).</summary>
        void Clear();
    }

    /// <summary>
    /// Whether the answer a store is delivering right now is a stand-in for one the backend could not give.
    /// <para>
    /// Every read and every <see cref="IPersistenceStore.WhenWritten"/> is answered exactly once, even when the
    /// backend fails for good (a store gives up on a job after a few attempts). A read it could not answer gets the
    /// empty result — a null record, an empty list, an empty entry per container, a count of 0 — and a barrier is
    /// released, after the store has logged the error. While such a callback runs, <see cref="Failed"/> is true, so a
    /// caller that must not mistake "unreadable" for "nothing saved" can tell them apart. The orchestrator's
    /// <see cref="PersistenceHost"/> answers those requests with HTTP 503, and the worker retries.
    /// </para>
    /// </summary>
    public static class PersistenceAnswer
    {
        [ThreadStatic] private static bool _failed;

        /// <summary>True only inside a store callback whose answer is a stand-in for a failed backend call.</summary>
        public static bool Failed => _failed;

        /// <summary>Run <paramref name="callback"/> with <see cref="Failed"/> set to <paramref name="failed"/>. For store implementations.</summary>
        public static void Invoke(Action callback, bool failed)
        {
            if (!failed) { callback(); return; }
            bool outer = _failed;
            _failed = true;
            try { callback(); }
            finally { _failed = outer; }
        }
    }

    /// <summary>The answer of <see cref="IPersistenceStore.LoadContainers"/> as the stores build it.</summary>
    internal static class ContainerRecords
    {
        /// <summary>An entry, empty for now, for every distinct non-null id in <paramref name="containerIds"/>; <paramref name="distinct"/> lists them once each, in order.</summary>
        public static Dictionary<string, IReadOnlyList<PersistedEntityRecord>> For(IReadOnlyList<string> containerIds, out List<string> distinct)
        {
            var result = new Dictionary<string, IReadOnlyList<PersistedEntityRecord>>(StringComparer.Ordinal);
            distinct = new List<string>(containerIds != null ? containerIds.Count : 0);
            if (containerIds == null) return result;
            for (int i = 0; i < containerIds.Count; i++)
            {
                string id = containerIds[i];
                if (id == null || result.ContainsKey(id)) continue;
                result[id] = new List<PersistedEntityRecord>();
                distinct.Add(id);
            }
            return result;
        }

        /// <summary>File <paramref name="record"/> under its container when that container was asked for and the record is not inside a carrier.</summary>
        public static void Add(Dictionary<string, IReadOnlyList<PersistedEntityRecord>> result, PersistedEntityRecord record)
        {
            if (record == null || !string.IsNullOrEmpty(record.CarrierKey)) return;
            if (result.TryGetValue(record.ContainerId ?? "", out var list)) ((List<PersistedEntityRecord>)list).Add(record);
        }

        /// <summary>Empty every entry again: a store that retries a read starts over.</summary>
        public static void Reset(Dictionary<string, IReadOnlyList<PersistedEntityRecord>> result)
        {
            foreach (var list in result.Values) ((List<PersistedEntityRecord>)list).Clear();
        }
    }

    /// <summary>The answer of <see cref="IPersistenceStore.LoadCarried"/> as the stores build it.</summary>
    internal static class CarriedRecords
    {
        /// <summary>
        /// An entry, empty for now, for every distinct non-null key in <paramref name="carrierKeys"/>;
        /// <paramref name="distinct"/> lists the ones worth reading once each, in order. The empty key gets its
        /// (always empty) entry but is never read: it is what every record outside a carrier has.
        /// </summary>
        public static Dictionary<string, IReadOnlyList<PersistedEntityRecord>> For(IReadOnlyList<string> carrierKeys, out List<string> distinct)
        {
            var result = ContainerRecords.For(carrierKeys, out distinct);
            distinct.Remove("");
            return result;
        }

        /// <summary>File <paramref name="record"/> under its carrier when that carrier was asked for.</summary>
        public static void Add(Dictionary<string, IReadOnlyList<PersistedEntityRecord>> result, PersistedEntityRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.CarrierKey)) return;
            if (result.TryGetValue(record.CarrierKey, out var list)) ((List<PersistedEntityRecord>)list).Add(record);
        }
    }

    /// <summary>
    /// The stores' report of saves the epoch rule dropped. A dropped save is normally from a worker that lost an
    /// entity and does not know it yet. It can also come from a live entity whose epoch is behind its own record,
    /// and then every checkpoint of that entity is lost (NEB-325). So the first drop of each key is a warning with
    /// both epochs. Later drops of that key are debug lines, so a lagging worker cannot flood the log. There is one
    /// instance per store, so one per process. Thread-safe: <c>SqlPersistenceStore</c> reports from its writer thread.
    /// </summary>
    internal sealed class StaleSaveLog
    {
        /// <summary>Most keys remembered. Past this, drops of new keys are counted but not warned about.</summary>
        public const int MaxKeys = 4096;

        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private long _dropped;

        /// <summary>Saves dropped since the store was created, warned about or not.</summary>
        public long Dropped { get { lock (_gate) return _dropped; } }

        /// <summary>
        /// Count one dropped save of <paramref name="key"/>. True when it is the first for that key and the caller
        /// should warn with <see cref="Message"/>.
        /// </summary>
        public bool Drop(string key)
        {
            lock (_gate)
            {
                _dropped++;
                return _warned.Count < MaxKeys && _warned.Add(key ?? "");
            }
        }

        /// <summary>Count one dropped save, and warn when it is the first for its key.</summary>
        public void Report(string key, uint savedEpoch, uint storedEpoch, string savedBy)
        {
            if (Drop(key)) NebulaLog.Warn(Message(key, savedEpoch, storedEpoch, savedBy));
            else NebulaLog.Debugf($"persistence: stale save for {key} (epoch {savedEpoch} < {storedEpoch}); dropped");
        }

        public static string Message(string key, uint savedEpoch, uint storedEpoch, string savedBy) =>
            $"persistence: dropped a save of {key} at epoch {savedEpoch}{(string.IsNullOrEmpty(savedBy) ? "" : " from " + savedBy)} " +
            $"because the store holds epoch {storedEpoch}. Either a worker that lost this entity is still saving it, or " +
            "this entity was given its record's state without the record's epoch and none of its saves will land. " +
            "Later drops for this key are logged only with verbose logging.";
    }
}
