# Chunk state — design

Status: design of record for **NEB-276**. No wire change to any existing message; two new worker-message kinds
(`ChunkStateService.RequestMessageKind` 65010 and `ReplyMessageKind` 65011) and one reserved network prefab id
(`NetworkPrefabs.ChunkStatePrefabId`, 0xFF00). Decisions made without asking are marked **D#**. Builds on
`docs/scoped-chunk-grids.md` (chunks as runtime containers), `docs/scope-lifecycle.md` (retire and restore),
`docs/persistence-durability.md` (fencing, the epoch stamp) and `docs/lifecycle-hooks.md`. User-facing page:
`website/content/docs/guides/procedural-object-state.mdx`. Tests: `Tests/EditMode/ChunkStateTests.cs`.

## 0. Problem

A game places resource nodes (ore rocks, crystals, plants) deterministically from a chunk's seed, on every role. A
chunk holds dozens of them, the world holds millions, and almost none are ever touched. The few that are touched
must stay touched: a mined rock stays mined when the chunk unloads, when its scope retires, and when the Editor's
play loop stops and starts; and it grows back an hour later even if nobody was there to see the hour pass.

Making every node a `PersistentEntity` is out of the question: an untouched node would cost an entity, a record,
interest management and a handover. Keeping the edits in the game's own database moves the hard parts — who may
change a node, how clients hear about it, when the record is restored relative to the lease — back into every game.
Nebula needs a general "procedural world with edits" primitive: **sparse per-object state keyed by a stable,
game-chosen id, grouped per chunk**, that replicates to nearby clients, persists with the chunk, expires lazily, costs
nothing when untouched, and can be changed safely from any worker in the mesh.

Nothing here knows what a node is. An entry is a number, an optional expiry time and a few optional bytes; what they
mean is the game's.

## 1. The surface

```csharp
// Read, on any role.
bool ChunkState.TryGet(Container chunk, ulong objectId, out ObjectState state);
event ObjectStateChangedHandler ChunkState.Changed;            // (in ObjectStateChange change)
long ChunkState.NowUnixMs { get; }

// Write, on a worker; applied by the chunk's lease holder.
worker.ChunkStates.CompareAndSet(chunk, objectId, ObjectState? expected, ObjectState? replacement, Action<ChunkStateResult> onDone);
worker.ChunkStates.Set(chunk, objectId, state, onDone);
worker.ChunkStates.Clear(chunk, objectId, onDone);
worker.ChunkStates.TryCompareAndSetLocal(containerId, objectId, expected, replacement, out ChunkStateResult result);

public readonly struct ObjectState { uint Value; long ExpiresAtUnixMs; byte[] Payload; }
```

`ChunkState` (static) is the read side and the change event. `ChunkStateService` (per worker, `NebulaWorker.ChunkStates`)
is the write side. `ChunkStateEntity` is the component on the built-in prefab that carries a chunk's entries; a game
never touches it.

## 2. Decisions

**D1 One entity per touched chunk, not a new store or a new channel.** A chunk's entries live on one server-driven
`PersistentEntity` spawned in the chunk's container, keyed `chunkstate:<containerId>`. Everything the feature needs is
then already true of an entity in a container:

- the lease-landing restore (`NebulaPersistence.PumpRestores` → `IPersistenceStore.LoadContainers`) brings the record
  back whenever any worker gains the chunk's lease, after a release, a worker failure, a rebalance or a scope
  restore, with no new code path;
- the retire sequence checkpoints it with the rest of the part (`CheckpointContainer`), and `EmptyContainer` saves it
  when a chunk is released;
- interest management delivers it to the clients near the chunk, ghosts it to neighbouring workers, and hands it
  over with the lease;
- the epoch rule, fencing and the restore grace protect it like any other record.

The alternative — a per-chunk table in the store, replicated by a new gateway message — would duplicate every one of
those, and each is a source of subtle bugs (`docs/persistence-durability.md` exists because of them). The cost of an
entity is one record and one small spawn per *touched* chunk, which is the unit that is rare.

This is not the "relational data in the entity store" that `docs/lifecycle-hooks.md` warns about: the entries are
the chunk's own state, read and written only through the chunk, never queried across chunks.

**D2 Keyed by container, not by grid.** The API takes a `Container` or a container id, not a grid coordinate. A chunk
is a runtime container, so `rt_<id>` already names it uniquely across scopes, and the record's `ContainerId` is what
`LoadContainers` matches on. Keying by container also makes the feature work in a static cell or a runtime container
that is not part of a chunk grid, without a second code path. Carried (dynamic) containers are refused with
`Rejected`: their records ride their carrier (`CarrierKey`), and a ship's interior is not procedurally generated
state.

**D3 A built-in prefab in a reserved id range.** Prefab ids are indexes into the game's `NebulaConfig.NetworkPrefabs`,
the same on every process. Nebula now reserves ids from `NetworkPrefabs.BuiltInBase` (0xFF00) for prefabs it provides;
`NetworkPrefabs.Get` builds them on first use, in every process, from code (`ChunkStateEntity.Template`: an inactive,
hidden, never-saved GameObject with `NetworkIdentity`, `PersistentEntity` and `ChunkStateEntity`). Appending built-ins
to the game's list was rejected: their ids would shift whenever the game's list changed length, and a record written
by one build would name a different prefab in the next. A reserved range keeps the id fixed; the record also carries
the prefab's name (`NebulaChunkState`), as every record does. A game lists nothing and authors nothing.

**D4 Entries are small, sorted, and expire at an absolute UTC time.** An entry is `{ uint Value; long
ExpiresAtUnixMs; byte[] Payload }` with a payload of at most `ChunkState.MaxPayloadBytes` (64). An absent entry means
*untouched*: the game's generator decides what that looks like, so an untouched object needs no bytes anywhere.

Expiry is stored as UTC Unix milliseconds, never as `NetworkTime` ticks: the tick is a `uint` at 60 Hz from
2026-01-01 and wraps in 2028, and a chunk may stay unloaded for longer than that. Expiry is evaluated lazily: a read
compares the time; each copy removes due entries when its earliest expiry passes (one comparison per frame, and none
at all for a chunk with no timed entries); a record loaded after the time has passed simply comes back without the
entry. Nothing is scheduled while a chunk is unloaded, and nothing ever ticks for it.

Clocks: a worker uses its system clock, as the simulation tick already assumes workers' clocks agree. A client uses
its own clock corrected by how far `NetworkTime.LatestServerTick` is from the tick its clock would give; the
difference of two wrapping ticks is wrap-safe, and one larger than a day is ignored as nonsense. So a client with a
wrong clock still sees entries expire when the server does, late by its latency. `ChunkState.Clock` is the internal
test seam.

**D5 An emptied chunk costs nothing, and the emptying still reaches clients.** When a chunk's last entry is cleared
or expires, its entity is despawned with `keepPersisted: false`, which deletes its record. The despawn waits
`ChunkStateService.EmptyLingerSeconds` (1 s): despawning in the same frame would send the entity's despawn before the
tick published its emptied map, and a client would hear the entry *forgotten* rather than *cleared* (D10).

During that second — and for a restored chunk whose every entry expired while it was unloaded — the entity may be
checkpointed or released. `PersistentEntity.DiscardRecord` (internal) makes that checkpoint a delete instead of a
write, so no empty record is ever left behind. It is set whenever the map is empty and cleared when it is not. A
fenced worker deletes nothing (`docs/persistence-durability.md` D8): `SaveNow` returns before the discard, and the
linger despawn is skipped while fenced.

**D6 The map replicates whole, as one NetworkVariable.** The survey suggested the per-tick sync-state channel
(`WriteSyncState(writer, full)`) so a change would send only a delta. It would be wrong for late joiners: the gateway
caches only chunks flagged *full* (`NebulaGateway.EntityRecord.StoreKeyframe`), and a reliable behaviour's chunk is flagged full
only on its first send and on keyframe ticks. A client that subscribed after a delta would be given the stale spawn
keyframe and never the delta. The NetworkVariable path is correct by construction: `EntityVarsMsg` carries all of an
entity's variables, the gateway replaces its cached copy (`LastSpawn.Vars`) on every update, ghosts read it, and a
handover carries it.

The price is that a change resends the chunk's whole map. That is the right trade: writes are rare (a player mining),
maps are small (13 bytes per entry), and correctness for a late joiner is not negotiable. The map is capped at
`ChunkState.MaxEncodedBytes` (48 KB, about 3,700 plain entries), under the 64 KB that a length-prefixed variable blob
(`NetworkWriter.WriteBytes`) and a persisted state entry (`PersistentStateCodec`) can hold, with room for the rest of a
spawn message. A write that would exceed it is `Rejected` and changes nothing. The encoding is versioned, sorted by
object id (one map, one byte string), and length-prefixed inside the variable so a malformed or newer blob is skipped
with a warning instead of desynchronising the variables after it.

**D7 Bookkeeping, not occupancy.** The entity must never keep a scope alive or look like load:

- `NetworkIdentity.ExcludeFromOccupancy` (internal) takes it out of worker telemetry's per-container counts and cost,
  which is where the default retire policy's `Entities > 0` and the assignment planner read occupancy
  (`docs/scope-lifecycle.md` D6–D7). Without it, the first mined rock would have stopped its scope from ever retiring.
- Its cost weight is 0.
- `WorkerScopeLifecycle.IsBusy` treats it like any persistent entity: busy only while it has unsaved changes, which
  lasts until the next checkpoint (at most `NebulaPersistence.MinSaveIntervalSeconds` after a write). With persistence
  off it stays busy, which is right: a retire would lose its entries.
- Its container is pinned (`ContainerPinned`), so container resolution never moves it into a nested or neighbouring
  box, and its record always names the chunk. It follows the chunk's lease through ordinary handover.
- `RuntimeGridAllocator.MustKeep` needs no change: the entity stands at its chunk's centre, so it never "stands in
  live space outside" its chunk.

**D8 Relevance: interest radius plus half the chunk's diagonal.** The entity stands at the chunk's centre, but a
client needs the state of every object it can see, and an object can be anywhere in the chunk. A client within
`InterestRadius` of any point of the chunk is within `InterestRadius + halfDiagonal` of its centre, so that is the
entity's `RelevanceRadius` (horizontal diagonal when interest is planar, so a tall planar column does not inflate it).
It is clamped to `InterestMaxRadius`, with one warning. `AlwaysRelevant` was rejected: it would send every touched
chunk of every scope to every client, which grows with the world rather than with what the client can see. The
radius is above `InterestRadius`, so the entity is a *wide* entity at the gateway, which costs more to match; that is
bounded by the number of touched chunks near someone, not by the world.

**D9 Every write is applied by the chunk's lease holder, one at a time.** The worker that wants to change an object
(the one simulating the mining player) is often not the one that holds the chunk. Writes are routed:

1. `Check(containerId)` on the caller. If this worker holds the lease and the chunk is ready, the write is applied at
   once and the callback runs before the call returns (the local fast path; `TryCompareAndSetLocal` and
   `TrySetLocal` do only this and never send).
2. Otherwise the owner is found from the container's lease — the registered container's `OwnerWorkerId`, or the
   control plane's lease row when the container is not registered here — and the write is sent to it as a worker
   message. No entity or ghost needs to exist; `AuthorityRpc` was rejected for exactly that reason, since it needs a
   copy of the target on the caller. A chunk with no lease row is `Unavailable` at once.
3. The receiver checks again. If the lease moved, it answers *not owner* and the caller looks the lease up again and
   resends, at most `MaxAttempts` (4) times.

"Ready" is what makes creating the chunk's entity safe. The lease holder waits (holding the write, up to its
deadline) while any of these holds, because each is a moment at which a new entity could become a second copy of one
that exists:

- the chunk's saved records have not been read yet (`NebulaPersistence.IsContainerRestored`);
- a record for the key is held back because its saver may still hand the entity over
  (`NebulaPersistence.IsAwaitingHandover`, the `RestorePlan.Wait` case);
- this worker holds the entity as a ghost, so its authority is on its way;
- the worker is fenced.

Compare-and-set is the primitive: it applies only if the entry is still what the caller last saw (`null` means
untouched; an expired entry counts as untouched; equality is value, expiry and payload), and a conflict returns the
entry that is there, so the caller decides again with fresh information. Because one worker applies the writes for a
chunk, in the order they arrive, two workers consuming the last unit cannot both succeed. `TimedOut` means the answer
was lost, not that nothing happened; a compare-and-set is safe to repeat after it, an unconditional `Set` may not be.

**D10 Changes are events with a kind.** `ChunkState.Changed` fires on every copy (authority, ghost, client) for
`Set` (new or changed, including every entry of a chunk whose state has just arrived), `Cleared` (removed by a
write), `Expired` (its time passed) and `Forgotten` (this process no longer holds the chunk's state: it left the
client's interest, or the chunk unloaded). `Forgotten` is separate because it is not a change to the object: a client
whose own chunk window is wider than its interest radius may keep drawing a mined rock as mined. A process that runs
several workers (tests, the Editor) raises one change per copy; `IsAuthority` says which copy raised it. A replica
diffs the incoming map against its own, so an entry it already expired locally does not fire again when the
authority's removal arrives.

## 3. What this does not do

- **No writes to an unloaded chunk.** A write needs a lease holder. A game that must change a chunk nobody has
  loaded can lease it first (`NebulaChunks.EnsureAt`) or edit the record offline through the store.
- **Reads away from the lease holder may be stale or missing.** A worker without a ghost of the chunk's entity reads
  nothing; a ghost is a tick behind. Compare-and-set is what makes decisions safe; a read is for display.
- **Persistence off.** With no store the entries still replicate and still route, but they are lost when the chunk
  is released, and a lease that moves between two workers before a handover lands can briefly let the new holder
  create a second entity for the chunk (there is no record to wait for). Run persistence where the entries matter.
- **Whole-map resend.** A chunk near the size limit resends up to 48 KB per change to every gateway and ghost that
  holds it. The limit is a ceiling, not a target.
- **Renaming `ChunkStateEntity`** would orphan saved entries: the persisted state entry is named after the behaviour
  type (`PersistentStateCodec.StateNameOf`). Treat the type name as part of the save format.
