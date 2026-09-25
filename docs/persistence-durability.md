# Persistence durability window (NEB-224), and one copy after a death verdict (NEB-256)

Status: landed with NEB-224; D7–D12 landed with NEB-256; D13 landed with NEB-325. User-facing page: `website/content/docs/guides/persistence.mdx` §"Durability
window". Failure test: `Tests/EditMode/ConformancePersistenceDurabilityTests.cs` (conformance scenario 10, see
`docs/conformance-suite.md` §4). Telemetry: `WorkerStats.OldestDirtySeconds` / `WorkerInfo.OldestDirtySeconds` on
the existing worker heartbeat, surfaced on the orchestrator dashboard.

## 0. Purpose

Persistence checkpoints periodically rather than writing every change (`docs/architecture.md`'s "not a WAL"
stance, restated in this issue's non-goals). That means a worker that dies between two checkpoints loses whatever
changed since the last one. Games that build savable state on top of Nebula need a number to design around: how
much can be lost, in the worst case, given the knobs they set. This document derives that number from the
scheduler in `Runtime/Persistence/NebulaPersistence.cs`, not from intuition, and a conformance test
(scenario 10) keeps it honest against the actual code.

## D1. What "the durability window" means

The window is the worst-case elapsed time between **a change landing on an authoritative entity** (a `[Persist]`
`NetworkVariable` write, a call to `MarkPersistDirty()`/`PersistentEntity.MarkDirty()`, or a pose move past the
threshold) and **that change being durably committed** to the persistence backend, where "durably committed" means
the record would still be there if the *worker process* ended at that instant. A worker crash after the window has
elapsed loses nothing from that change; a crash inside the window can lose it.

This is a bound on **loss per change**, not a promise that nothing is ever lost — see the non-goals: no
write-ahead log, no transactional multi-entity saves. Two changes to two different entities in the same instant can
each be lost independently, and a change is only as durable as the single record it lives in (§D3 notes what
"durable" costs on each backend).

## D2. The bound, in terms of the code

`NebulaPersistence.Update()` (called once per worker frame, never from the tick loop) runs `PumpCheckpoints`, whose
`IsDue` decides which tracked entity gets a `SaveNow` this frame:

```csharp
private bool IsDue(PersistentEntity pe, NetworkIdentity identity, float now)
{
    float since = now - pe.LastSavedAt;
    if (!pe.HasBeenSaved && pe.LastSavedAt == 0f) return true;
    if (since >= CheckpointSecondsFor(pe)) return true;      // unconditional periodic save
    if (since < MinSaveIntervalSeconds) return false;         // throttle: never saves twice inside this window
    if (pe.IsDirty || PersistentStateCodec.HasDirtyVars(identity)) return true;  // dirty-triggered save
    ...
}
```

and `PumpCheckpoints` spends at most `MaxSavesPerFrame` saves per call, walking `_tracked` round-robin across
frames when there are more due entities than the budget allows.

Four terms, each tied to a knob:

| Term | Knob | Why it's in the bound |
| --- | --- | --- |
| **Checkpoint interval** | `NebulaConfig.PersistenceCheckpointSeconds` (`PersistentEntity.CheckpointSeconds` overrides it per entity), default 5 s | `IsDue`'s `since >= CheckpointSecondsFor(pe)` branch guarantees an **unconditional** save at least this often, regardless of the dirty flag. This is what bounds the case where a change lands just after a periodic save reset the entity's `LastSavedAt` and, for whatever reason, the dirty-triggered path does not fire sooner. |
| **Minimum save interval** | `NebulaPersistence.MinSaveIntervalSeconds`, `0.5f`, not configurable | `IsDue`'s `since < MinSaveIntervalSeconds → false` branch means a dirty change **cannot** trigger a save until this long has passed since the entity's last checkpoint, however urgently `MarkDirty()` was called. Worst case: the change lands the instant after a save, so the entity waits out almost the whole interval before it is even eligible again. |
| **Per-frame save budget** | `NebulaPersistence.MaxSavesPerFrame`, `64`, not configurable | Once due, an entity still has to be reached by the round-robin cursor. With `N` entities becoming due in the same window, the last of them waits `ceil(N / MaxSavesPerFrame)` frames. A worker's frame period is nominally `NetworkTime.TickInterval` (~16.7 ms at the default 60 Hz tick rate) when it is keeping up; a worker that is behind (visible as high `TickMs` / utilization on the dashboard) has a longer real frame period and this term grows with it. |
| **Store write latency** | backend-dependent — see §D3 | `_store.Save(record)` is fire-and-forget from the scheduler's point of view (`IPersistenceStore`'s own doc comment). The record still has to become durable in whatever the store is backed by. |

**The bound to design around:**

```
loss window  ≤  PersistenceCheckpointSeconds
              + MinSaveIntervalSeconds
              + ceil(N_dirty / MaxSavesPerFrame) × framePeriod
              + storeWriteLatency
```

Summing the checkpoint interval and the minimum save interval is deliberately conservative rather than tight (the
periodic path and the throttled dirty path are usually alternatives, not both incurred back-to-back), because a
number games design around should not depend on getting the interaction between the two exactly right. With the
defaults (`PersistenceCheckpointSeconds = 5`, `MinSaveIntervalSeconds = 0.5`) and a lightly loaded worker
(`N_dirty` small relative to 64, `framePeriod ≈ 16.7 ms`, an in-memory store), the bound is **about 5.6 seconds**.
Raising `PersistenceCheckpointSeconds` raises the bound by the same amount; it is the dominant term by design,
since it is the only one meant to be tuned per game.

`N_dirty` is the number of *other* tracked entities that also become due in the same window — not the total
entity count on the worker, most of which are not dirty at any given moment. A container full of NPCs whose AI
writes a `[Persist]` variable every tick is the case that makes this term matter; a mostly-static world keeps it
near zero.

## D3. The store-write-latency term, per backend

| Backend | Term | Why |
| --- | --- | --- |
| `LocalPersistenceStore`, no file (`memory` mode) | **0** | `Save` writes straight into the in-process dictionary; there is nothing further to lose once `SaveNow` returns, because the whole store dies with the process anyway (there is nothing to be durable *against*). This is the backend the failure test (§D5) uses, so its measured loss isolates the scheduler terms above. |
| `LocalPersistenceStore` with a file (`local` mode) | up to `LocalPersistenceStore.WriteIntervalSeconds` (default 1 s), or `WriteBarrierIntervalSeconds` (default 0.1 s) when a caller is waiting on `WhenWritten` | Records land in memory immediately but the backing file is rewritten on a debounce, so a process crash (this mode runs worker and store in one process) can still lose a change that only made it to memory. |
| `RemotePersistenceStore` (a worker in a mesh, talking to the orchestrator) | `RemotePersistenceStore.FlushIntervalSeconds` (0.25 s) the worker gathers a batch, plus the HTTP round trip and the orchestrator's own commit | The **worker's own queue** is where an unflushed save is lost if the *worker* is what crashes — once a batch is POSTed and the orchestrator's `PersistenceHost` accepts it, the record is the orchestrator's problem, not the worker's. The orchestrator's write itself (`SqlPersistenceStore` for SQLite/Postgres, described in `website/content/docs/guides/persistence.mdx`) is a single writer thread applying saves in order, typically sub-millisecond for SQLite and a network round trip for Postgres; it does not depend on the worker staying alive. |

A killed **orchestrator** (not a worker) is a different failure with its own bound (its database's own durability
guarantees, e.g. SQLite's `fsync` behaviour); this issue is scoped to a worker crash, per NEB-224's "kills a worker
with N dirty entities".

## D4. Worked example

Default config, a worker simulating 300 NPCs where 40 of them write a `[Persist]` variable in the same tick
(a wave of enemies all taking damage at once), talking to the orchestrator over `remote`:

```
5 s (PersistenceCheckpointSeconds)
+ 0.5 s (MinSaveIntervalSeconds)
+ ceil(40 / 64) × 16.7 ms = 1 frame ≈ 16.7 ms
+ 0.25 s (RemotePersistenceStore.FlushIntervalSeconds) + ~10 ms (LAN round trip + SQLite commit)
≈ 5.78 seconds
```

Lowering `PersistenceCheckpointSeconds` is the knob that actually moves this number for a game that needs a
tighter bound; the other three terms are small and mostly fixed by the architecture.

## D5. The failure test

`Tests/EditMode/ConformancePersistenceDurabilityTests.cs` (`[Category("Conformance")]`, scenario 10 in
`docs/conformance-suite.md` §4) is tier C: a real `NebulaPersistence` checkpointing into a real
`LocalPersistenceStore` (memory backend, so D3's term is 0 and the test isolates the scheduler terms), against a
bare `NebulaWorker` that is never started. It:

1. Tracks 200 entities (`> MaxSavesPerFrame`, so the per-frame budget has to spread saves across frames) and drains
   their first-ever checkpoint.
2. Marks all 200 dirty in the same instant — the scenario NEB-224 asks for.
3. Confirms nothing is saved before `MinSaveIntervalSeconds` has elapsed (the throttle term is real, not a
   formality), and that exactly one `MaxSavesPerFrame`'s worth is saved in the first frame after it (the per-frame
   budget term is real).
4. "Kills" the worker — stops calling `Update()` — after exactly the number of frames the per-frame-budget term
   predicts, and asserts the store holds every one of the 200 changes: **loss = 0**, which is `≤` the documented
   bound (whose checkpoint-interval and store-latency terms are not even exhausted in this scenario).

`NebulaPersistence.Now` is the clock seam this test needed: the scheduler read `Time.unscaledTime` directly, so
driving it through anything but the wall clock required injecting a `Func<float>` (defaulting to
`Time.unscaledTime`) that the test points at its own `float` instead. This is the smallest change that makes the
scheduler's timers deterministic; nothing about its logic changed.

**For NEB-237** (the scale and failure suite): this test's fixture (a real `NebulaPersistence` + `LocalPersistenceStore`
+ the `Now` clock seam) is the reusable part. A larger-scale run wants the same setup with a bigger `entityCount`
and CSV output of `(N_dirty, framesToKill, lost, boundSeconds)` rather than a single pass/fail; consider whether
that belongs as a mode of `Services~/Nebula.Persistence.Benchmarks` (which already benchmarks
`LocalPersistenceStore` write throughput under `PersistenceHost`) or as a variant of the EditMode test parameterised
over entity counts. Either way, reuse the clock seam rather than re-deriving the scheduler's timing.

## D6. Telemetry: oldest-dirty age per worker

`NebulaPersistence.OldestDirtyAgeSeconds` reports how long the oldest currently-dirty tracked entity has been
waiting for its next checkpoint (0 when nothing is dirty) — a live reading of how much of the documented window is
in use on a given worker right now. It travels on the existing heartbeat as an additive field
(`WorkerStats.OldestDirtySeconds` → `WorkerInfo.OldestDirtySeconds`, wired through `ControlPlaneJson`,
`LocalControlPlane` and `RemoteControlPlane`), so an older orchestrator or worker simply does not read it — no
protocol version bump. The orchestrator's `/api/status` exposes it as `workers[].oldestDirtySeconds`, and
`NebulaDashboard.html` shows it per worker next to tick time.

It is a proxy, not a duplicate of the bound: it does not know about a pose-move-triggered save that has no explicit
"became dirty at" timestamp, and it resets to 0 the moment a save lands even though the record still has to become
durable in the backend (§D3). It is enough to notice "this worker has been sitting on an unsaved change for longer
than `PersistenceCheckpointSeconds` should allow", which is the operational question the number exists to answer.

## D7. A worker declared dead while it is still running (NEB-256)

Status: landed with NEB-256. Conformance scenario 19 (`docs/conformance-suite.md` §4):
`Tests/EditMode/ConformanceDeclaredDeadWorkerTests.cs` and `Services~/Nebula.Services.Tests/DeclaredDeadWorkerTests.cs`.

D1–D6 bound what a worker **crash** loses. This is the other failure: the worker does not crash, it only stops
getting its heartbeats through. The orchestrator cannot tell the two apart. After `WorkerTimeoutSeconds` without a
heartbeat, `NebulaOrchestrator.ReapDeadWorkers` removes the row and the planner deals the containers to someone
else. That worker waits `PersistenceRestoreGraceSeconds` and restores the entities from their last checkpoint. The
restore's other guards (a key alive here, a saver that is still alive, a handover that wins over a restored copy)
all read the new owner's view, and in that view the old owner is dead. So in a real run the survivor logged
`restored 18 persisted entities into <anchor>` while the old owner's copies were still simulating. For a moment the
same entity had two authoritative copies, and the restored one was up to a checkpoint interval stale.

**The goal:** at most one authoritative copy of a persistent entity at a time, with the fewest moving parts. The
liveness model does not change. The orchestrator still decides who is alive, and it still decides the same way. The
rest of the mesh, the declared-dead worker included, now acts on that verdict. Four rules do it: D8 to D11.

## D8. The fence: a worker stops acting as an owner when it can no longer prove it is alive

`WorkerRegistration.IsFenced`, exposed as `NebulaWorker.IsFenced`, is true when the worker's own row, as its mirror
of the control plane shows it, has had no heartbeat for more than `WorkerTimeoutSeconds`, or when the row is gone
(D10). The test is `IControlPlane.IsWorkerAlive`, the same one the orchestrator applies, and it runs on the same
clock: a `RemoteControlPlane` keeps `Now` as the orchestrator's clock at the last document, plus the local time
since. A partitioned worker that receives no documents at all still sees `Now` advance past its last recorded
heartbeat. So it fences itself even though it cannot learn anything from the control plane.

**Timing.** The mirror's `LastHeartbeat` is never newer than the orchestrator's. The mirror's `Now` lags the
orchestrator's by at most one document's transit. So the worker fences no later than one transit after the
orchestrator could declare it dead. The survivor then waits `PersistenceRestoreGraceSeconds` (3 s by default) after
the verdict reaches it before it reads a record. The fence wins by the grace period minus that transit.

**What a fenced worker does.** It keeps simulating. It stops doing the three things that can make a second copy or
overwrite the survivor's:

- **It saves nothing.** `NebulaPersistence.Update` skips checkpoints, and `SaveNow` returns without writing but
  leaves the entity dirty. A save issued now would be queued, and it could land after the new owner's.
  `CheckpointContainer`, shutdown and a despawn's last save all go through `SaveNow`, so they are fenced too. A
  scope part is not retired while its worker is fenced, and `ReleaseRuntimeContainer` refuses: both would empty a
  container they could not save first.
- **It restores nothing.** The restore pumps do not run, and the scene pass spawns no scene entities. A fenced
  worker's view of its leases may be stale.
- **It hands nothing over.** The tick's container-crossing handover is skipped, and the entity keeps authority,
  the same way it does when the new owner is not connected. The receiver would hold a copy of something that may
  be restored elsewhere, and a handover bumps the epoch to the same value a restore gives it.

**Deletes are not fenced.** A delete has no epoch. If a delete lands late, the survivor's next periodic checkpoint
writes the record again, because that save is unconditional (D2). Holding the delete back instead would bring back
an entity the game destroyed when the fence turns out to be a false alarm.

**A false alarm costs nothing.** An orchestrator that is restarting or slow declares nobody dead while it is away
(`docs/control-plane-availability.md` D3). When the worker's next heartbeat lands, the fence lifts and the dirty
entities are saved on the next pass. A pause longer than `WorkerTimeoutSeconds` therefore holds back saves and
handovers until the next heartbeat. Before this change it was fully invisible up to
`RemoteControlPlane.DisconnectAfterSeconds` (15 s). That cost is deliberate: past `WorkerTimeoutSeconds` the worker
cannot know whether the orchestrator is away or deciding without it.

## D9. The survivor stamps the new epoch into the store before anything else

A restored entity comes back at `record.Epoch + 1` (`NebulaWorker.SpawnRestored`). The store keeps a save only when
its epoch is at least the stored one (`LocalPersistenceStore.Save`, and `SqlPersistenceStore`'s
`WHERE excluded.epoch >= nebula_entity.epoch`). Before this change the restored entity's first save waited for its
checkpoint schedule. Until then the record still carried the old epoch. The old owner's late saves were accepted and
were overwritten only at the next checkpoint.

Now `NebulaPersistence.Restore` and the scene-entity path set `PersistentEntity.StampPending`, and so does
`NebulaPersistence.Apply` on an entity this worker owns or is about to spawn (D13). `IsDue` puts that
entity first, so its first checkpoint happens on the next pass, within the per-frame budget. After that, a save the
old owner had queued before it fenced carries a lower epoch and the store refuses it. A queued save can still land
between the survivor's read and its stamp. That write is then overwritten by the stamp, which is the outcome the
mesh wants anyway. The stamp costs one save per restored entity, spread by `MaxSavesPerFrame` like any other.

## D10. The old owner learns the verdict: drop what was lost, without a save

`WorkerRegistration.IsDeclaredDead` is true when a document this worker has been listed in no longer lists it. A row
missing from a replacement document (a new `DocumentId`) is a control plane that came back empty. That is not a
verdict: it is the reclaim path of `docs/control-plane-availability.md` D1b, and it is left exactly as it was.
`NebulaWorker.OnControlPlaneChanged` reads the verdict before `RegisterAgainIfForgotten` can put the row back,
because on an in-process control plane that happens at once. It applies the new leases and then runs
`AbandonLostContainers`. Every container the worker still simulates that is now leased to **another** worker is
emptied through `EmptyContainer`, riders before their carriers, with persistence switched off for those despawns. No
save runs, because the survivor's copy is the one the mesh goes on with. No delete runs, because the record is the
survivor's. No handover runs, because the survivor may already have restored the entity. The despawns still go out
to gateways and ghost peers. The worker then registers again and joins the mesh as an empty worker.

**Containers the verdict left unassigned are kept.** The planner deals them in a later pass. By then this worker is
registered again, and the ordinary handover gives the new owner the live copy inside its grace period. So a restore
never happens for them.

**The price** is what changed on the old owner since its last checkpoint: exactly what a crash would have cost (D2).
The alternative was to hand the old copy over and let it win over the restored one. That would roll back whatever the
survivor had simulated since its restore, and it would still leave the window in which both copies existed.

## D11. Peers and gateways believe the verdict even while the link is up

`WorkerRoster` (`Runtime/ControlPlane`) reports a worker row that was in the last document and is missing from the
current one, when both are the same document. It reports each removal once.

- **A gateway** (`NebulaGateway.DropDeclaredDeadWorker`) closes the link and forgets that worker's entities, as if
  the link had broken. Its players are placed again, as after any worker death. Without this, the old copies stayed
  on the gateway beside the restored ones until the link linger ran out, or for good if a client's pawn was on that
  worker. Restored entities get new net ids, so the gateway had no way to tell the two copies apart. The link is not
  dialled again until the worker registers again, because `EnsureLink` needs a row.
- **A worker** (`NebulaWorker.DropDeclaredDeadPeer`) drops the peer link the same way. The peer's ghosts go with
  it. This matters for the restore: `NebulaPersistence` counts a ghost as "alive here" and would skip restoring that
  key, and then the ghost would be despawned the moment the dead worker cleaned up (D10).

## D12. Not done, and what the maintainer should decide

- **Freezing the simulation while fenced.** A fenced worker keeps simulating, and a client still linked to it can
  still see it. D11 removes that link as soon as the gateway sees the verdict, but a gateway that is partitioned
  along with the worker cannot. Freezing would change what an orchestrator restart longer than
  `WorkerTimeoutSeconds` looks like: every worker would stop. That is a liveness-model decision.
- **Aligning the two clocks of liveness.** `WorkerTimeoutSeconds` (5 s) and `RemoteControlPlane.DisconnectAfterSeconds`
  (15 s) disagree about how long silence is survivable. The fence follows the first one.
- **Fencing the receiver.** A worker still accepts a handover from a peer the control plane no longer lists, if the
  link is up and the sender is not fenced. D8 makes the sender refuse, and D11 drops the link, so this is defence in
  depth only.
- **A restored document that lost a fresh registration.** `ControlPlaneHost` stores the document about a second
  after each change. Suppose an orchestrator crashes, and its replacement comes back from storage with the same
  `DocumentId` but without a row that was written just before the crash. That missing row reads as a verdict (D10,
  D11). Gateways and peers drop their links to that worker, and the players it hosted are placed again. The worker
  re-registers and is dialled again. The cost is a disruption, not a second copy. Distinguishing the two would need an explicit "declared dead" record, not a missing row.
- **A delete followed by a late save.** A late save for a key the survivor has deleted in the meantime finds no
  record to compare epochs with, and it is accepted. Keeping tombstones with epochs would close this gap. It is a
  store-schema change.

## D13. A record applied to an entity brings its epoch (NEB-325)

The documented way to bring back a player is to spawn the pawn, read its record with `NebulaPersistence.Load` and
give it to the pawn with `NebulaPersistence.Apply`. Before this change `Apply` took the state and left the epoch
alone. A new pawn spawns at epoch 1, and its record carries the epoch of the pawn's previous life, which is higher
after any handover. So the store refused every save of the pawn for the whole session, and said so only in a debug
line. The Editor dev loop hit it on every Play once a record had passed epoch 1, because the pawn is always new. A
mesh hits it whenever a player comes back to a new pawn after their previous pawn was handed over.

`Apply` now does what a restore does, through the same two pieces:

- **The epoch.** An entity this worker owns moves to `record.Epoch + 1` when its epoch is lower. An entity that is
  not spawned yet keeps `record.Epoch + 1` in `PersistentEntity.AdoptedEpoch`, and `NebulaWorker`'s one spawn path
  spawns it at that epoch or higher, whichever public spawn call the game makes. `Restore` and the scene-entity path
  pass the same epoch to the spawn, so it is never raised twice. A copy this worker does not own takes the state only.
- **The stamp.** `StampPending` is set, so the next checkpoint pass saves the entity first (D9).

**Why this keeps the epoch rule's guarantee.** Only a worker that holds authority over the entity, or is about to
spawn it with authority, raises the epoch, and it has just read the record. `record.Epoch + 1` outranks saves at the
record's epoch or below, which come from lives of the entity older than the one it read. Those are the saves D9
refuses after a restore. A life that saved after the read, at a higher epoch, still wins: the applying worker's saves
are then refused, and the store warns. A fenced worker still writes nothing (D8). `Apply` cannot lift an epoch past
what a restore of the same record would.

**The live epoch change.** Raising the epoch of a spawned entity is the change a handover makes, without the
change of worker. The next state the entity sends carries the new epoch as a reliable location update, and gateways,
ghosts and clients take it as they take a handover's. Two things see the jump: an `AuthorityRpc` sent at the old
epoch can be rejected as stale when the jump is more than `MaxHops`, and a scope transfer prepared before the change
no longer commits. The guide tells games to apply the record right after the spawn, before either can happen.

**A refused save is a warning.** `LocalPersistenceStore` and `SqlPersistenceStore` log the first refused save of each
key as a warning with the key, both epochs and the saving worker, and later ones for that key as debug lines
(`StaleSaveLog`). Refusals are expected after a death verdict (D9), so the warning is once per key per store rather
than per save. `StaleSavesDropped` counts all of them.

**Not done: seeding lease epochs in the dev loop.** The issue suggested that the dev loop's in-memory control plane
start lease epochs above the highest epoch in the store. That would not have helped. The record's epoch is the
entity's epoch (`NetworkIdentity.Epoch`), not a container lease's, and a new entity spawns at 1 whatever its
container's lease epoch is.

## Non-goals (restated from the issue)

No write-ahead log of every change, no transactional multi-entity saves. The bound in §D2 is what a game designs
around instead: keep `PersistenceCheckpointSeconds` low enough for the worst acceptable loss, and use `SaveNow`
for a specific moment (a completed objective, a player logging out) that must not wait for the next periodic
checkpoint at all.
