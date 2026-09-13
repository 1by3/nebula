// Nebula persistence module.
//
// This SpacetimeDB module is the long-term store for entities that opted into persistence
// (Nebula's PersistentEntity component). It is a database of its own next to the control plane
// (SpacetimeDB/Module~) so that wiping the ephemeral registry never touches saved worlds.
//
// One row per persisted entity, keyed by the game-stable persistence key. Workers write
// checkpoints through SaveEntity and read the table back when they gain a container lease.
// The reducers hold the only write rules: a save whose authority epoch is older than the stored
// one is dropped (a stale worker cannot resurrect an old state), and every accepted save bumps
// the row version.

using SpacetimeDB;

/// <summary>Defines the SpacetimeDB table and reducers used by Nebula persistence.</summary>
public static partial class Module
{
    // ---------------------------------------------------------------- tables

    /// One persisted entity: enough to bring it back on any worker (prefab or scene object,
    /// container and pose, authority epoch) plus the opaque state blob the entity wrote.
    [SpacetimeDB.Table(Accessor = "persisted_entity", Public = true)]
    public partial struct PersistedEntity
    {
        /// Stable identity of the entity across runs and workers.
        [SpacetimeDB.PrimaryKey]
        public string Key;
        /// Prefab index at save time; ushort.MaxValue for a scene entity.
        public ushort PrefabId;
        /// Prefab asset name, so a restore survives a reordered prefab list.
        public string PrefabName;
        /// Non-zero: the record belongs to a scene entity with this scene id.
        public uint SceneId;
        /// Static container the entity was in, or "" when it was in none or inside a carrier.
        [SpacetimeDB.Index.BTree]
        public string ContainerId;
        /// Persistence key of the carrier when the entity was inside a dynamic container, "" otherwise.
        [SpacetimeDB.Index.BTree]
        public string CarrierKey;
        /// Pose in the container's local space (world space when there was no container).
        public float PosX;
        public float PosY;
        public float PosZ;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        public float VelX;
        public float VelY;
        public float VelZ;
        /// Authority epoch at save time. A save with an older epoch is rejected.
        public uint Epoch;
        /// Entity was spawned server-driven (no owning client).
        public bool ServerDriven;
        /// A client owned the entity when it was saved.
        public bool Owned;
        /// GameObject name at save time (cosmetic).
        public string Name;
        /// Opaque state blob (Nebula's PersistentStateCodec).
        public byte[] State;
        /// Bumped on every accepted save.
        public ulong Version;
        public Timestamp SavedAt;
        /// Id of the worker that wrote the row.
        public string SavedBy;
    }

    // -------------------------------------------------------------- reducers

    /// Insert or update one record. Dropped when the stored row carries a newer authority epoch.
    [SpacetimeDB.Reducer]
    public static void SaveEntity(ReducerContext ctx, string key, ushort prefabId, string prefabName, uint sceneId,
        string containerId, string carrierKey,
        float posX, float posY, float posZ, float rotX, float rotY, float rotZ, float rotW,
        float velX, float velY, float velZ,
        uint epoch, bool serverDriven, bool owned, string name, byte[] state, string savedBy)
    {
        if (ctx.Db.persisted_entity.Key.Find(key) is { } row)
        {
            // Stale writer: an older authority epoch never overwrites a newer one.
            if (row.Epoch > epoch) return;
            row.PrefabId = prefabId;
            row.PrefabName = prefabName;
            row.SceneId = sceneId;
            row.ContainerId = containerId;
            row.CarrierKey = carrierKey;
            row.PosX = posX;
            row.PosY = posY;
            row.PosZ = posZ;
            row.RotX = rotX;
            row.RotY = rotY;
            row.RotZ = rotZ;
            row.RotW = rotW;
            row.VelX = velX;
            row.VelY = velY;
            row.VelZ = velZ;
            row.Epoch = epoch;
            row.ServerDriven = serverDriven;
            row.Owned = owned;
            row.Name = name;
            row.State = state;
            row.Version += 1;
            row.SavedAt = ctx.Timestamp;
            row.SavedBy = savedBy;
            ctx.Db.persisted_entity.Key.Update(row);
        }
        else
        {
            ctx.Db.persisted_entity.Insert(new PersistedEntity
            {
                Key = key,
                PrefabId = prefabId,
                PrefabName = prefabName,
                SceneId = sceneId,
                ContainerId = containerId,
                CarrierKey = carrierKey,
                PosX = posX,
                PosY = posY,
                PosZ = posZ,
                RotX = rotX,
                RotY = rotY,
                RotZ = rotZ,
                RotW = rotW,
                VelX = velX,
                VelY = velY,
                VelZ = velZ,
                Epoch = epoch,
                ServerDriven = serverDriven,
                Owned = owned,
                Name = name,
                State = state,
                Version = 1,
                SavedAt = ctx.Timestamp,
                SavedBy = savedBy,
            });
        }
    }

    /// Forget one entity. A no-op when there is no row for the key.
    [SpacetimeDB.Reducer]
    public static void DeleteEntity(ReducerContext ctx, string key)
    {
        ctx.Db.persisted_entity.Key.Delete(key);
    }

    /// Dev helper: wipe the whole store (the dashboard's "Clear persistence" button).
    [SpacetimeDB.Reducer]
    public static void ClearPersistence(ReducerContext ctx)
    {
        foreach (var e in ctx.Db.persisted_entity.Iter()) ctx.Db.persisted_entity.Key.Delete(e.Key);
    }
}
