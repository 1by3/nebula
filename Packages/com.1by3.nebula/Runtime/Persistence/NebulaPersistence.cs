using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The worker's persistence service: it checkpoints every authoritative entity carrying a
    /// <see cref="PersistentEntity"/> into an <see cref="IPersistenceStore"/>, and brings those entities back when
    /// this worker gains the lease of a container they were saved in. Owned by <see cref="NebulaWorker"/> and reached
    /// as <c>worker.Persistence</c>; null when persistence is off.
    /// <para>
    /// It is never on the per-tick path. Checkpoints run from the worker's <c>Update</c>, at most
    /// <see cref="MaxSavesPerFrame"/> of them per frame, and a store that is unreachable only means the saves are
    /// retried later: the mesh keeps simulating either way.
    /// </para><para>
    /// Restores are deliberately timid, because a persisted entity coming back must never become a second copy of one
    /// that is still alive somewhere. A freshly leased container waits
    /// <see cref="NebulaConfig.PersistenceRestoreGraceSeconds"/> so the previous owner's handover wins; a record whose
    /// key is already alive here is skipped; a record written moments ago by a worker that is still alive waits
    /// another grace period, in case that worker is about to hand the entity over; and if a handover arrives after a
    /// restore anyway, the incoming entity wins and the restored duplicate is despawned <i>without</i> deleting the
    /// record.
    /// </para>
    /// </summary>
    public sealed class NebulaPersistence
    {
        /// <summary>Most saves issued in one frame, so a container full of dirty entities cannot spike the frame.</summary>
        public const int MaxSavesPerFrame = 64;
        /// <summary>An entity saves at most this often, however often it changes.</summary>
        public const float MinSaveIntervalSeconds = 0.5f;
        /// <summary>Metres an entity must move since its last save before the move alone is worth a checkpoint.</summary>
        public const float PoseMoveThreshold = 0.25f;
        /// <summary>Degrees an entity must turn since its last save before the turn alone is worth a checkpoint.</summary>
        public const float PoseTurnThresholdDegrees = 5f;

        private readonly NebulaWorker _worker;
        private readonly NebulaConfig _config;
        private readonly IPersistenceStore _store;

        /// <summary>
        /// Seconds on a monotonic clock, sampled once per <see cref="Update"/> pass. Defaults to
        /// <c>UnityEngine.Time.unscaledTime</c>; a conformance test injects its own so the checkpoint scheduler
        /// (<see cref="MinSaveIntervalSeconds"/>, <see cref="MaxSavesPerFrame"/>, the checkpoint interval) can be
        /// driven deterministically without waiting on the wall clock. See docs/persistence-durability.md.
        /// </summary>
        internal Func<float> Now = () => UnityEngine.Time.unscaledTime;

        /// <summary>
        /// Asked before a leased container's records are read: false holds the restore for another frame. The worker
        /// points this at <see cref="WorkerScopeLifecycle.MayRestore"/>, which keeps a scope's parts back until
        /// <c>NebulaLifecycle.OnScopeActivating</c> has been raised for the scope (<c>docs/lifecycle-hooks.md</c>).
        /// Null — the default — restores as soon as the grace period is over.
        /// </summary>
        internal Func<string, bool> RestoreGate;

        /// <summary>Every persistent entity alive in this process, authoritative or ghost, by key.</summary>
        private readonly Dictionary<string, NetworkIdentity> _byKey = new Dictionary<string, NetworkIdentity>();
        /// <summary>Checkpoint candidates, in spawn order; the cursor walks them across frames.</summary>
        private readonly List<PersistentEntity> _tracked = new List<PersistentEntity>();
        /// <summary>Entities this worker restored rather than received: they lose against a handover for the same key.</summary>
        private readonly HashSet<PersistentEntity> _restored = new HashSet<PersistentEntity>();
        /// <summary>Static containers this worker leases, and when the lease appeared.</summary>
        private readonly Dictionary<string, float> _leasedSince = new Dictionary<string, float>();
        /// <summary>Containers whose records have been asked for, so a lease that stays put is loaded once.</summary>
        private readonly Dictionary<string, object> _loadRequested = new Dictionary<string, object>();
        /// <summary>Containers whose load came back and was judged, and how many entities each one brought back (see <see cref="ContainerRestored"/>).</summary>
        private readonly Dictionary<string, int> _restoreComplete = new Dictionary<string, int>();
        /// <summary>Records held back because their saver may still hand the entity over; re-judged after another grace.</summary>
        private readonly Dictionary<string, PersistedEntityRecord> _waiting = new Dictionary<string, PersistedEntityRecord>();
        private readonly Dictionary<string, float> _waitingUntil = new Dictionary<string, float>();
        /// <summary>Records of scene entities whose object is here but unspawned: applied by the worker's scene pass.</summary>
        private readonly Dictionary<uint, PersistedEntityRecord> _sceneRecords = new Dictionary<uint, PersistedEntityRecord>();
        private readonly List<string> _scratchKeys = new List<string>();
        private readonly NetworkWriter _stateWriter = new NetworkWriter(512);

        private int _cursor;

        internal NebulaPersistence(NebulaWorker worker, NebulaConfig config, IPersistenceStore store)
        {
            _worker = worker;
            _config = config;
            _store = store;
            _worker.EntitySpawned += OnEntitySpawned;
            _worker.EntityDespawned += OnEntityDespawned;
            _worker.AuthorityReceived += OnAuthorityReceived;
            ContainerRegistry.LeasesChanged += OnLeasesChanged;
            OnLeasesChanged();
        }

        /// <summary>The store behind the service, for game code that wants to read or write records directly.</summary>
        public IPersistenceStore Store => _store;
        /// <summary>The store is reachable. When false the mesh keeps running and saves are retried.</summary>
        public bool IsConnected => _store != null && _store.IsConnected;
        /// <summary>Persistent entities alive in this process (authoritative and ghosts).</summary>
        public int TrackedCount => _byKey.Count;
        /// <summary>Records this service has written since the process started.</summary>
        public int SavedCount { get; private set; }
        /// <summary>Entities this service has brought back since the process started.</summary>
        public int RestoredCount { get; private set; }

        /// <summary>
        /// How long the oldest currently-dirty tracked entity has been waiting for its next checkpoint, in seconds;
        /// 0 when nothing is dirty. A live reading of how much of the documented durability window
        /// (docs/persistence-durability.md) is in use on this worker right now; reported per worker on the
        /// heartbeat (<see cref="WorkerInfo.OldestDirtySeconds"/>) for the orchestrator dashboard.
        /// </summary>
        public float OldestDirtyAgeSeconds
        {
            get
            {
                float now = Now();
                float oldest = 0f;
                for (int i = 0; i < _tracked.Count; i++)
                {
                    var pe = _tracked[i];
                    if (pe == null || !pe.IsDirty || pe.DirtySince <= 0f) continue;
                    float age = now - pe.DirtySince;
                    if (age > oldest) oldest = age;
                }
                return oldest;
            }
        }

        /// <summary>An entity was brought back from the store and spawned on this worker.</summary>
        public event Action<NetworkIdentity> EntityRestored;

        /// <summary>
        /// Every persisted record of a container this worker has just gained has been read and dealt with: the
        /// container id, and how many entities were actually brought back (0 when there was nothing saved). Raised
        /// once per lease, on the main thread, after the grace period and the load. This is the seam the scope
        /// lifecycle waits on before a restored scope admits clients (<c>docs/scope-lifecycle.md</c>); NEB-242
        /// exposes it to games as <c>OnContainerRestored</c>.
        /// </summary>
        public event Action<string, int> ContainerRestored;

        /// <summary>The container's records have been read and restored since this worker gained its lease.</summary>
        public bool IsContainerRestored(string containerId) => !string.IsNullOrEmpty(containerId) && _restoreComplete.ContainsKey(containerId);

        /// <summary>How many entities that restore brought back; 0 when it has not finished or there was nothing saved.</summary>
        public int RestoredCountFor(string containerId) =>
            !string.IsNullOrEmpty(containerId) && _restoreComplete.TryGetValue(containerId, out int n) ? n : 0;

        /// <summary>
        /// Save every authoritative persistent entity in <paramref name="containerId"/> right now, whatever the
        /// checkpoint schedule says, and return how many were saved. The forced checkpoint of the retire sequence:
        /// the saves are issued here and <see cref="IPersistenceStore.WhenWritten"/> is the barrier that says they
        /// reached the store. Does not despawn anything.
        /// </summary>
        public int CheckpointContainer(string containerId)
        {
            if (string.IsNullOrEmpty(containerId) || _store == null) return 0;
            int saved = 0;
            for (int i = 0; i < _tracked.Count; i++)
            {
                var pe = _tracked[i];
                var identity = pe != null ? pe.Identity : null;
                if (identity == null || !identity.IsSpawned || !identity.HasAuthority) continue;
                var container = identity.Container;
                if (container == null || !string.Equals(container.ContainerId, containerId, StringComparison.Ordinal)) continue;
                SaveNow(identity);
                saved++;
            }
            return saved;
        }

        /// <summary>The live entity saving under <paramref name="key"/> in this process, or null.</summary>
        public NetworkIdentity Find(string key)
        {
            return !string.IsNullOrEmpty(key) && _byKey.TryGetValue(key, out var e) && e != null ? e : null;
        }

        /// <summary>Read one record. <paramref name="onLoaded"/> runs on the main thread with null when there is none.</summary>
        public void Load(string key, Action<PersistedEntityRecord> onLoaded) => _store.Load(key, onLoaded);

        /// <summary>Delete the record for <paramref name="key"/>. The live entity, if any, keeps running unsaved.</summary>
        public void Delete(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _store.Delete(key);
            _waiting.Remove(key);
            _waitingUntil.Remove(key);
        }

        // ---------------------------------------------------------------------------------------- saving

        /// <summary>
        /// Build the record for <paramref name="identity"/> as it is right now: prefab or scene id, container and
        /// pose, and the state blob (<see cref="PersistentStateCodec"/>). Null when the entity is not persistent.
        /// </summary>
        public PersistedEntityRecord BuildRecord(NetworkIdentity identity)
        {
            if (identity == null) return null;
            var pe = identity.Persistent;
            if (pe == null) return null;

            var record = new PersistedEntityRecord
            {
                Key = pe.EnsureKey(),
                PrefabId = identity.PrefabId,
                PrefabName = PrefabNameOf(identity),
                SceneId = identity.SceneId,
                ScopeKey = identity.ScopeKey,
                Epoch = identity.Epoch,
                ServerDriven = identity.IsServerDriven,
                Owned = identity.OwnerClientId != 0,
                Name = identity.name,
                SavedBy = _worker.WorkerId,
            };

            var container = identity.Container;
            if (container != null && container.IsDynamic)
            {
                // Inside a ship: the record rides its carrier, so the pair comes back together wherever the ship is.
                var carrier = container.Carrier != null ? container.Carrier.Persistent : null;
                if (carrier != null) record.CarrierKey = carrier.EnsureKey();
                else NebulaLog.Debugf($"persistence: {identity} is inside {container.ContainerId}, whose carrier is not persistent; saving its pose only");
            }
            else if (container != null) record.ContainerId = container.ContainerId;

            if (pe.PersistPose)
            {
                record.LocalPosition = identity.LocalPosition;
                record.LocalRotation = identity.LocalRotation;
            }
            if (pe.PersistVelocity) record.Velocity = identity.Motion.Velocity;

            _stateWriter.Reset();
            PersistentStateCodec.Write(_stateWriter, identity);
            record.State = _stateWriter.ToArray();
            return record;
        }

        /// <summary>
        /// Checkpoint <paramref name="identity"/> now, whatever its timer says. Use it at a moment the game knows is
        /// worth saving (a player logging out, a quest completed); the periodic checkpoint covers everything else.
        /// </summary>
        public void SaveNow(NetworkIdentity identity)
        {
            if (identity == null) return;
            var pe = identity.Persistent;
            if (pe == null) return;
            if (!identity.HasAuthority)
            {
                NebulaLog.Warn($"persistence: SaveNow({identity}) on a copy this worker does not own; ignored");
                return;
            }
            var record = BuildRecord(identity);
            if (record == null) return;
            _store.Save(record);
            SavedCount++;
            pe.HasBeenSaved = true;
            pe.IsDirty = false;
            pe.DirtySince = 0f;
            pe.LastSavedAt = Now();
            pe.LastSavedPosition = identity.transform.position;
            pe.LastSavedRotation = identity.transform.rotation;
            if (!_byKey.ContainsKey(record.Key)) _byKey[record.Key] = identity;
        }

        private static string PrefabNameOf(NetworkIdentity identity)
        {
            if (identity.IsSceneEntity) return "";
            var prefab = NetworkPrefabs.Get(identity.PrefabId);
            return prefab != null ? prefab.name : "";
        }

        // ---------------------------------------------------------------------------------------- applying

        /// <summary>
        /// Put a record's state back onto <paramref name="identity"/>. Works both before the entity is spawned (the
        /// restore path) and on an entity this worker already owns, which is what a rejoining player's pawn wants:
        /// the game spawns the pawn, asks for the record with <see cref="Load"/> and applies it when the answer
        /// arrives. Pass <paramref name="applyPose"/> false to keep the entity where it is and take only its state.
        /// </summary>
        public void Apply(PersistedEntityRecord record, NetworkIdentity identity, bool applyPose = true)
        {
            if (record == null || identity == null) return;
            identity.Initialize();
            var pe = identity.Persistent;
            if (pe != null)
            {
                pe.Key = record.Key;
                pe.HasBeenSaved = true;
                pe.LastSavedVersion = record.Version;
                pe.LastSavedAt = Now();
                pe.IsDirty = false;
                pe.DirtySince = 0f;
            }
            PersistentStateCodec.Read(record.State, identity);
            if (identity.IsSpawned && identity.HasAuthority)
            {
                // Reading a blob does not dirty the variables (a fresh spawn sends them all anyway); on a live entity
                // the restored values still have to reach the gateway and the ghosts.
                var vars = identity.AllVars;
                for (int i = 0; i < vars.Length; i++) if (vars[i].Persist) vars[i].Dirty = true;
                identity.MarkVarsDirty();
            }
            if (applyPose && (pe == null || pe.PersistPose))
            {
                var container = ResolveContainer(record);
                identity.SetContainer(container);
                identity.SetLocalPose(container, record.LocalPosition, record.LocalRotation);
                if (pe != null)
                {
                    pe.LastSavedPosition = identity.transform.position;
                    pe.LastSavedRotation = identity.transform.rotation;
                }
            }
            if (pe == null || pe.PersistVelocity) identity.Motion.Velocity = record.Velocity;
            if (!string.IsNullOrEmpty(record.Key)) _byKey[record.Key] = identity;
        }

        /// <summary>
        /// Instantiate and spawn the entity a record describes, on this worker, with authority. Null when the prefab
        /// is unknown to this build or the scene object is not resident. The entity comes back at
        /// <c>record.Epoch + 1</c>, so anything still holding the old epoch is stale everywhere.
        /// </summary>
        public NetworkIdentity Restore(PersistedEntityRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Key)) return null;
            var existing = Find(record.Key);
            if (existing != null) return existing;

            var container = ResolveContainer(record);
            NetworkIdentity identity;
            if (record.SceneId != 0)
            {
                identity = SceneEntities.Find(record.SceneId);
                if (identity == null) return null;
                if (identity.IsSpawned || identity.NetId != 0) return null;
            }
            else
            {
                identity = InstantiateFor(record, container);
                if (identity == null) return null;
            }

            Apply(record, identity);
            if (identity.Persistent != null) _restored.Add(identity.Persistent);
            _worker.SpawnRestored(identity, container, record);
            RestoredCount++;
            EntityRestored?.Invoke(identity);
            return identity;
        }

        private static NetworkIdentity InstantiateFor(PersistedEntityRecord record, Container container)
        {
            ushort id = record.PrefabId;
            var prefab = NetworkPrefabs.Get(id);
            if (prefab == null || (record.PrefabName != "" && prefab.name != record.PrefabName))
            {
                // The prefab list was reordered since the save; the name is what the record really meant.
                ushort byName = FindPrefabByName(record.PrefabName);
                if (byName == ushort.MaxValue)
                {
                    NebulaLog.Warn($"persistence: cannot restore {record.Key}: no prefab '{record.PrefabName}' (id {record.PrefabId}) in this build");
                    return null;
                }
                id = byName;
            }
            return NetworkPrefabs.Instantiate(id, Vector3.zero, Quaternion.identity, container != null ? container.transform : null);
        }

        private static ushort FindPrefabByName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return ushort.MaxValue;
            for (int i = 0; i < NetworkPrefabs.Count; i++)
            {
                var p = NetworkPrefabs.Get((ushort)i);
                if (p != null && p.name == prefabName) return (ushort)i;
            }
            return ushort.MaxValue;
        }

        private Container ResolveContainer(PersistedEntityRecord record)
        {
            if (!string.IsNullOrEmpty(record.CarrierKey))
            {
                var carrier = Find(record.CarrierKey);
                return carrier != null ? carrier.Carried : null;
            }
            return string.IsNullOrEmpty(record.ContainerId) ? null : ContainerRegistry.FindById(record.ContainerId);
        }

        // ---------------------------------------------------------------------------------------- restore planning

        /// <summary>What the service decided to do with one record when a container's records came back.</summary>
        public enum RestorePlan
        {
            /// <summary>Bring it back here and now.</summary>
            Restore,
            /// <summary>A client owned it: there is no client to own the result, so the game restores it on demand.</summary>
            SkipOwned,
            /// <summary>An entity with that key is already alive in this process.</summary>
            SkipAlive,
            /// <summary>It lives inside a carrier and comes back with it, not with the container.</summary>
            SkipCarried,
            /// <summary>Its saver is still alive and the record is fresh: it may yet arrive by handover.</summary>
            Wait,
        }

        /// <summary>
        /// Whether a record should be restored, given what is alive here and whether the worker that wrote it is
        /// still running. A pure decision, so the rule that keeps a persisted entity from becoming a second copy of a
        /// live one can be reasoned about (and tested) on its own.
        /// </summary>
        public static RestorePlan Plan(PersistedEntityRecord record, bool aliveHere, bool saverAlive, double recordAgeSeconds, float checkpointSeconds)
        {
            if (record == null) return RestorePlan.SkipAlive;
            if (!string.IsNullOrEmpty(record.CarrierKey)) return RestorePlan.SkipCarried;
            if (record.Owned) return RestorePlan.SkipOwned;
            if (aliveHere) return RestorePlan.SkipAlive;
            if (saverAlive && recordAgeSeconds < 2.0 * Mathf.Max(0.1f, checkpointSeconds)) return RestorePlan.Wait;
            return RestorePlan.Restore;
        }

        // ---------------------------------------------------------------------------------------- the frame pass

        /// <summary>Called once per frame by the worker: restores what is due, then checkpoints what is dirty.</summary>
        internal void Update()
        {
            float now = Now();
            PumpRestores(now);
            PumpWaiting(now);
            PumpCheckpoints(now);
        }

        private void PumpRestores(float now)
        {
            if (!_store.IsConnected) return;
            foreach (var kv in _leasedSince)
            {
                if (now - kv.Value < _config.PersistenceRestoreGraceSeconds) continue;
                // The scope's activation hook comes first when there is one (docs/lifecycle-hooks.md D4). The gate
                // is asked again every frame and opens on its own deadline, so nothing can wedge a restore here.
                if (RestoreGate != null && !RestoreGate(kv.Key)) continue;
                if (_loadRequested.ContainsKey(kv.Key)) continue;
                string containerId = kv.Key;
                var request = new object();
                _loadRequested[containerId] = request;
                _store.LoadContainer(containerId, records =>
                {
                    if (!_loadRequested.TryGetValue(containerId, out var current) || !ReferenceEquals(current, request)) return;
                    OnContainerRecords(containerId, records);
                });
            }
        }

        private void OnContainerRecords(string containerId, IReadOnlyList<PersistedEntityRecord> records)
        {
            if (records == null || records.Count == 0) { CompleteRestore(containerId, 0); return; }
            var container = ContainerRegistry.FindById(containerId);
            if (container == null || !container.IsOwnedBy(_worker.WorkerId)) return; // the lease moved on while we asked
            int restored = 0;
            float now = Now();
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record == null || string.IsNullOrEmpty(record.Key)) continue;
                switch (Judge(record, now))
                {
                    case RestorePlan.Restore:
                        // A scene object is never instantiated: its record waits for the worker's scene pass to spawn it.
                        if (record.SceneId != 0) { _sceneRecords[record.SceneId] = record; break; }
                        if (Restore(record) != null) restored++;
                        break;
                    case RestorePlan.Wait:
                        _waiting[record.Key] = record;
                        _waitingUntil[record.Key] = now + _config.PersistenceRestoreGraceSeconds;
                        break;
                }
            }
            if (restored > 0) NebulaLog.Info($"persistence: restored {restored} persisted entities into {containerId}");
            CompleteRestore(containerId, restored);
        }

        /// <summary>
        /// The container's records have been read and judged. Records held back for a handover that may still
        /// arrive (<see cref="RestorePlan.Wait"/>) do not delay this: the restore of what is saved has happened,
        /// and a record that is waiting is one the mesh is about to hand over anyway.
        /// </summary>
        private void CompleteRestore(string containerId, int restored)
        {
            if (string.IsNullOrEmpty(containerId) || _restoreComplete.ContainsKey(containerId)) return;
            _restoreComplete[containerId] = restored;
            try { ContainerRestored?.Invoke(containerId, restored); }
            catch (Exception e) { NebulaLog.Error($"ContainerRestored handler threw: {e}"); }
            // The game's hook, raised from the same call: after the restore, and before the worker's next pass turns
            // this into the scope's Restored acknowledgement (docs/lifecycle-hooks.md D2).
            NebulaLifecycle.RaiseContainerRestored(ContainerRegistry.FindById(containerId), restored);
        }

        private RestorePlan Judge(PersistedEntityRecord record, float now)
        {
            bool aliveHere = Find(record.Key) != null;
            double age = Math.Max(0.0, (DateTime.UtcNow - record.SavedAt).TotalSeconds);
            return Plan(record, aliveHere, IsSaverAlive(record), age, CheckpointSecondsFor(null));
        }

        private bool IsSaverAlive(PersistedEntityRecord record)
        {
            if (string.IsNullOrEmpty(record.SavedBy) || record.SavedBy == _worker.WorkerId) return false;
            var cp = _worker.ControlPlane;
            if (cp == null || !cp.IsConnected) return false;
            var row = cp.FindWorker(record.SavedBy);
            return row != null && cp.IsWorkerAlive(row, _config.WorkerTimeoutSeconds);
        }

        private void PumpWaiting(float now)
        {
            if (_waiting.Count == 0) return;
            _scratchKeys.Clear();
            foreach (var kv in _waitingUntil) if (now >= kv.Value) _scratchKeys.Add(kv.Key);
            for (int i = 0; i < _scratchKeys.Count; i++)
            {
                string key = _scratchKeys[i];
                _waitingUntil.Remove(key);
                if (!_waiting.TryGetValue(key, out var record)) continue;
                _waiting.Remove(key);
                if (Find(key) != null) continue; // it arrived by handover after all: nothing to do
                var container = ResolveContainer(record);
                if (container == null || !container.IsOwnedBy(_worker.WorkerId)) continue;
                if (record.SceneId != 0) { _sceneRecords[record.SceneId] = record; continue; }
                if (Restore(record) != null) NebulaLog.Info($"persistence: restored {record.Key} into {record.ContainerId} after its saver did not hand it over");
            }
            _scratchKeys.Clear();
        }

        private void PumpCheckpoints(float now)
        {
            if (_tracked.Count == 0) return;
            int budget = MaxSavesPerFrame;
            int examined = 0;
            while (examined < _tracked.Count && budget > 0)
            {
                if (_cursor >= _tracked.Count) _cursor = 0;
                var pe = _tracked[_cursor];
                examined++;
                if (pe == null)
                {
                    _tracked.RemoveAt(_cursor);
                    continue;
                }
                _cursor++;
                var identity = pe.Identity;
                if (identity == null || !identity.IsSpawned || !identity.HasAuthority) continue;
                if (!IsDue(pe, identity, now)) continue;
                SaveNow(identity);
                budget--;
            }
        }

        private bool IsDue(PersistentEntity pe, NetworkIdentity identity, float now)
        {
            float since = now - pe.LastSavedAt;
            if (!pe.HasBeenSaved && pe.LastSavedAt == 0f) return true;
            if (since >= CheckpointSecondsFor(pe)) return true;
            if (since < MinSaveIntervalSeconds) return false;
            if (pe.IsDirty || PersistentStateCodec.HasDirtyVars(identity)) return true;
            if (!pe.PersistPose) return false;
            var t = identity.transform;
            if ((t.position - pe.LastSavedPosition).sqrMagnitude >= PoseMoveThreshold * PoseMoveThreshold) return true;
            return Quaternion.Angle(t.rotation, pe.LastSavedRotation) >= PoseTurnThresholdDegrees;
        }

        private float CheckpointSecondsFor(PersistentEntity pe)
        {
            float configured = _config != null ? _config.PersistenceCheckpointSeconds : 5f;
            if (configured <= 0f) configured = 5f;
            return pe != null && pe.CheckpointSeconds > 0f ? pe.CheckpointSeconds : configured;
        }

        // ---------------------------------------------------------------------------------------- worker hooks

        private void OnLeasesChanged()
        {
            // SyncRuntime removes retired boxes before this event. Clear their lease bookkeeping even when
            // the old container no longer exists in either registry list.
            var lost = new List<string>();
            foreach (var id in _leasedSince.Keys)
            {
                var container = ContainerRegistry.FindById(id);
                if (container == null || !container.IsOwnedBy(_worker.WorkerId)) lost.Add(id);
            }
            foreach (var id in lost)
            {
                _leasedSince.Remove(id);
                _loadRequested.Remove(id);
                _restoreComplete.Remove(id);
            }
            float now = Now();
            DateLeases(ContainerRegistry.All, now);
            DateLeases(ContainerRegistry.Runtime, now); // runtime boxes are leased like baked ones; carried containers come back with their carrier
        }

        private void DateLeases(IReadOnlyList<Container> containers, float now)
        {
            for (int i = 0; i < containers.Count; i++)
            {
                var c = containers[i];
                if (c.IsDynamic) continue;
                if (c.IsOwnedBy(_worker.WorkerId))
                {
                    if (!_leasedSince.ContainsKey(c.ContainerId)) _leasedSince[c.ContainerId] = now;
                }
                else if (_leasedSince.Remove(c.ContainerId)) { _loadRequested.Remove(c.ContainerId); _restoreComplete.Remove(c.ContainerId); }
            }
        }

        private void OnEntitySpawned(NetworkIdentity identity)
        {
            var pe = identity != null ? identity.Persistent : null;
            if (pe == null) return;
            string key = pe.Key;
            if (string.IsNullOrEmpty(key) && identity.HasAuthority) key = pe.EnsureKey();
            if (string.IsNullOrEmpty(key)) return;

            if (_byKey.TryGetValue(key, out var other) && other != null && other != identity)
            {
                // Two lives of one key: the copy this worker restored loses against the one the mesh handed it.
                var otherPe = other.Persistent;
                if (otherPe != null && _restored.Contains(otherPe) && !_restored.Contains(pe))
                {
                    NebulaLog.Warn($"persistence: {key} arrived as {identity} while the restored {other} was alive; despawning the restored copy");
                    _worker.Despawn(other, keepPersisted: true);
                }
                else if (_restored.Contains(pe))
                {
                    NebulaLog.Warn($"persistence: {key} was restored as {identity} but {other} is already here; despawning the restored copy");
                    _worker.Despawn(identity, keepPersisted: true);
                    return;
                }
            }
            _byKey[key] = identity;
            if (!_tracked.Contains(pe)) _tracked.Add(pe);
            _waiting.Remove(key);
            _waitingUntil.Remove(key);

            // A carrier brings its cargo back with it.
            if (identity.HasAuthority && identity.Carried != null && _store.IsConnected)
            {
                _store.LoadCarried(key, records => OnCarriedRecords(key, records));
            }
        }

        private void OnCarriedRecords(string carrierKey, IReadOnlyList<PersistedEntityRecord> records)
        {
            if (records == null || records.Count == 0) return;
            var carrier = Find(carrierKey);
            if (carrier == null || !carrier.HasAuthority || carrier.Carried == null) return;
            int restored = 0;
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record == null || record.Owned || string.IsNullOrEmpty(record.Key)) continue;
                if (Find(record.Key) != null) continue;
                if (Restore(record) != null) restored++;
            }
            if (restored > 0) NebulaLog.Info($"persistence: restored {restored} persisted entities into {carrier.Carried.ContainerId}");
        }

        private void OnEntityDespawned(NetworkIdentity identity)
        {
            var pe = identity != null ? identity.Persistent : null;
            if (pe == null) return;
            _tracked.Remove(pe);
            _restored.Remove(pe);
            string key = pe.Key;
            if (!string.IsNullOrEmpty(key) && _byKey.TryGetValue(key, out var held) && held == identity) _byKey.Remove(key);
        }

        /// <summary>
        /// Authority is about to leave for another worker: the last word on this entity while we still hold it, so
        /// the next owner's first checkpoint is an update of ours rather than a save racing an older one.
        /// </summary>
        internal void OnHandoverOut(NetworkIdentity identity)
        {
            if (identity != null && identity.Persistent != null && identity.HasAuthority) SaveNow(identity);
        }

        private void OnAuthorityReceived(NetworkIdentity identity, string fromWorkerId)
        {
            var pe = identity != null ? identity.Persistent : null;
            if (pe == null) return;
            pe.LastSavedAt = Now();
            pe.LastSavedPosition = identity.transform.position;
            pe.LastSavedRotation = identity.transform.rotation;
            if (!_tracked.Contains(pe)) _tracked.Add(pe);
            if (!string.IsNullOrEmpty(pe.Key)) _byKey[pe.Key] = identity;
        }

        /// <summary>The worker is about to hand authority over, or about to despawn an entity for good.</summary>
        internal void OnDespawning(NetworkIdentity identity, bool keepPersisted)
        {
            var pe = identity != null ? identity.Persistent : null;
            if (pe == null) return;
            if (keepPersisted)
            {
                if (identity.HasAuthority) SaveNow(identity);
                return;
            }
            if (!string.IsNullOrEmpty(pe.Key))
            {
                _store.Delete(pe.Key);
                _waiting.Remove(pe.Key);
                _waitingUntil.Remove(pe.Key);
                NebulaLog.Debugf($"persistence: forgot {pe.Key}");
            }
        }

        /// <summary>
        /// The worker is about to spawn an unspawned scene entity: if a record for it came back with the container's
        /// lease, apply it first and spawn at the saved epoch + 1. Returns 0 when there is nothing saved for it.
        /// </summary>
        internal uint PrepareSceneEntity(NetworkIdentity identity)
        {
            if (identity == null || identity.SceneId == 0) return 0;
            if (!_sceneRecords.TryGetValue(identity.SceneId, out var record)) return 0;
            _sceneRecords.Remove(identity.SceneId);
            if (identity.Persistent == null) return 0;
            Apply(record, identity);
            _restored.Add(identity.Persistent);
            RestoredCount++;
            NebulaLog.Info($"persistence: scene entity {record.Key} restored from its saved state (epoch {record.Epoch + 1})");
            return record.Epoch + 1;
        }

        /// <summary>
        /// The worker is going away: save everything dirty and give the store a few ticks to get the writes out.
        /// Nothing is deleted - the entities live on in the store and come back wherever their containers land.
        /// </summary>
        internal void Shutdown()
        {
            _worker.EntitySpawned -= OnEntitySpawned;
            _worker.EntityDespawned -= OnEntityDespawned;
            _worker.AuthorityReceived -= OnAuthorityReceived;
            ContainerRegistry.LeasesChanged -= OnLeasesChanged;

            int saved = 0;
            for (int i = 0; i < _tracked.Count; i++)
            {
                var pe = _tracked[i];
                var identity = pe != null ? pe.Identity : null;
                if (identity == null || !identity.IsSpawned || !identity.HasAuthority) continue;
                SaveNow(identity);
                saved++;
            }
            for (int i = 0; i < 4; i++) _store.Tick();
            if (saved > 0) NebulaLog.Info($"persistence: saved {saved} entities on shutdown");
        }
    }
}
