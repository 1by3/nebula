# Lifecycle hooks — design

Status: design of record for **NEB-242**. No wire change; one additive store method and one additive HTTP endpoint
(`GET /api/store/count`). Decisions made without asking are marked **D#**. Builds on `docs/scope-lifecycle.md`
(NEB-240), which ordered the retire and restore sequences and left two internal seams for this item, and on
`docs/scope-activation.md` (NEB-233). User-facing page: `website/content/docs/guides/lifecycle-hooks.mdx`.
Conformance tests: `Tests/EditMode/ConformanceLifecycleHooksTests.cs` (scenario 11 of `docs/conformance-suite.md`)
and `Services~/Nebula.Services.Tests/StorageAndHostTests.cs` for the store count in SQL and over HTTP.

## 0. Problem

A frontier settlement holds forty NPC colonists. The last player leaves, the region goes idle, and NEB-240 retires
it: every colonist is checkpointed and the box is emptied. When a player comes back an hour later, the game does not
want forty hour-old entity checkpoints re-spawned where they stood — it wants to advance the settlement by an hour
(crops grew, two colonists died, a caravan arrived) and rebuild it from **one row in its own database**.

That needs three moments and one guarantee, and only Nebula has them:

- a call **before** anything is checkpointed, in which the game can collapse the box into its own state;
- a call on activation that says **whether entity records exist** at all, so the game can choose between its summary
  and the records;
- a call **after the restore has finished**, so spawning from a summary cannot race records coming back and produce
  two of every colonist.

Nothing about this is a population model. Nebula must not own the summary, the schedule or the simulation of a
sleeping region: persistence saves entities, not worlds. What Nebula owns is the instants — when a lease lands, when
a restore finished, when a retire is about to write — and the order between them. This item exposes exactly those.

## 1. The surface

```csharp
NebulaLifecycle.OnScopeActivating  += (ScopeInfo scope, bool hasRecords) => { … };
NebulaLifecycle.OnContainerRestored += (Container container, int restored) => { … };
NebulaLifecycle.OnBeforeRetire     += async (Container container, CancellationToken cancel) => { … };
NebulaLifecycle.OnRetired          += (Container container) => { … };
```

**D1 One static hook set, not events on the worker.** A game registers these once at boot — before a
`NebulaWorker` exists, and from code (a bootstrap, a `ScriptableObject`, a game mode) that has no reference to one.
There is one worker per process, so an instance event would only add the question "which worker's?" to every
registration. This is the convention the other game hooks already follow: `ScopeLifecycle.ShouldRetire`,
`NebulaCost.EntityWeight`, `PhysicsIslands.IsCohesive`. They are C# events rather than assignable delegates because
several systems in a game want the same instant and none of them should be able to unregister another; the
single-answer hooks (a policy, a weight) stay assignable fields for the opposite reason.

`NebulaLifecycle.Reset()` clears every handler and runs from `NebulaStatics` at the start of a play session, because
"enter play mode without domain reload" would otherwise leave the previous session's subscribers attached.

**D2 A handler that throws is logged and ignored, always.** Every raise catches per handler, so one game bug cannot
stop a restore being reported or leave a scope half-retired — the failure mode the whole of `docs/scope-lifecycle.md`
exists to prevent. The asynchronous hook (D3) extends this: a faulted task is logged and the others are still waited
for. This is the same rule the retire policy already has ("a policy that throws is logged and treated as keep the
scope"), applied to hooks whose failure must not change what the mesh does at all.

**D3 `OnBeforeRetire` is the NEB-240 seam, exposed unchanged.** It is `Func<Container, CancellationToken, Task>`,
raised at step 2 of the retire sequence — after the scope is marked `Retiring` (so nobody is being admitted) and
before the forced checkpoint (so the game's write happens before Nebula's). Every handler is started at once and the
sequence waits for `Task.WhenAll` of them, bounded by `WorkerScopeLifecycle.BeforeRetireTimeoutSeconds` (10 s, below
the orchestrator's 30 s step deadline). A window that overruns is **cancelled through the token, logged as a warning,
and the retire proceeds**: a game's hung task must never strand a scope in `Retiring`, where nobody could ever enter
it again. Neither the position nor the window is new; NEB-240 settled both and this item only gives them a public
name.

**D4 `OnScopeActivating` fires before any of the scope's containers restores, and the restore waits for it.** The
hook is raised by the worker's scope pass the first time that worker holds a part of a scope that is not retiring,
and again after the scope has retired and come back (or after this worker has lost every part of it and gained one
again). `hasRecords` comes from the store (D5). Because the answer is asynchronous, the ordering has to be a real
gate rather than a hope: `NebulaPersistence.RestoreGate` — a predicate the worker points at
`WorkerScopeLifecycle.MayRestore` — holds a leased container's load for as long as its scope's hook has not been
raised. The gate is only consulted when something is subscribed, so a mesh with no handler restores exactly as it
did before this item, with no store query and no delay.

The gate has a deadline of its own, `WorkerScopeLifecycle.ActivatingTimeoutSeconds` (10 s): a store that never
answers logs a warning and the restore goes ahead anyway. That is the one documented case in which the hook is not
raised before the restore, and it is deliberate for the same reason as every other deadline in the sequence — a
scope that can never come back is worse than an ordering a game can re-check.

**D5 "Are there records?" is a count on the store, not a list.** `IPersistenceStore.CountRecords(scopeKey,
containerId, onCounted)` is new on the interface, implemented by all three stores:

| Store | How |
|---|---|
| `SqlPersistenceStore` (SQLite, PostgreSQL) | `SELECT COUNT(*) FROM nebula_entity WHERE scope_key = @s` (plus `container_id` when one is given), with a new `nebula_entity_scope` index beside the existing container and carrier ones |
| `LocalPersistenceStore` | a walk of the in-memory records, like every other read it serves |
| `RemotePersistenceStore` | `GET /api/store/count?scope=&container=`, which returns `{"ok":true,"count":n}` — **only the number travels** |

The scope key is matched exactly (the empty key is the public world), a non-empty container id narrows the count to
one part, and records inside a carrier count like any other: the question is "does this scope have a past?", and a
crate inside a cart is part of one. The endpoint is additive and an older orchestrator answers 404, which a worker
treats as any other failed fetch (it retries); no protocol version changes, and no persisted row changes.

Counting rather than listing is what makes the hook affordable on the path that brings a scope to life. A game that
wants the records themselves already has `IPersistenceStore.LoadWhere` (§5).

**D6 `OnRetired` fires when the lease has gone, not when the worker is done.** The worker acknowledges its part's
checkpoint and the *orchestrator* then releases the lease rows and marks the scope `Retired` (step 5). The worker
sees that as the part disappearing from the scope it is retiring, and raises `OnRetired(container)` there. So the
hook means "the mesh has finished with this box" and nothing a handler does can disturb the retire. A part whose
retire was abandoned half-way — the scope went back to `Active`, the lease moved to another worker — is dropped
silently: it was never retired.

**D7 `OnContainerRestored` is every container, not only a scope's.** It is raised from
`NebulaPersistence.CompleteRestore`, which already fires once per lease after the records have been read and judged,
including for a container that had nothing saved in it. A public-world container's restore is the same event and the
same guarantee, so the hook is the general "this box is now as the store left it" signal rather than a scope-only
one. It carries the live `Container` (null only if the lease left again while the records were in flight) and the
number of entities that actually came back. Records held back for a handover that may still arrive
(`RestorePlan.Wait`) do not delay it — see `docs/scope-lifecycle.md` D10.

## 2. Ordering guarantees, stated plainly

These are the deliverable. Each one is asserted in `ConformanceLifecycleHooksTests`.

1. **`OnScopeActivating` precedes every restore of that scope on that worker.** The gate (D4) holds the load until
   the hook has been raised. Exception, logged: the store did not answer within `ActivatingTimeoutSeconds`.
2. **`OnContainerRestored` follows the restore.** The records have been read, judged and spawned before it is
   raised; a handler may spawn without making duplicates of what was saved.
3. **`OnBeforeRetire` completes before anything is saved.** The forced checkpoint is not issued until every handler's
   task has finished, faulted or been cancelled. Nothing is despawned before it either.
4. **`OnRetired` follows the release of the leases**, and therefore follows the checkpoint, the store's write
   barrier and the emptying of the part.
5. **Each fires once per activation or per retire**, not once per frame; `OnContainerRestored` is once per lease.

The three guarantees NEB-240 already made still hold underneath these: no admission during a retire, all parts
retire together, all parts restore before admission.

## 3. What a game does with them

The settlement of §0, in the shape the hooks are meant for:

```csharp
NebulaLifecycle.OnBeforeRetire += async (container, cancel) =>
{
    var colonists = MyGame.ColonistsIn(container);
    await MyDatabase.WriteSettlementAsync(container.ScopeKey, Summarise(colonists), cancel);
};

NebulaLifecycle.OnScopeActivating += (scope, hasRecords) =>
{
    // No entity records: this scope has never run, or the game collapsed it into a summary.
    _seedFromSummary = !hasRecords;
};

NebulaLifecycle.OnContainerRestored += (container, restored) =>
{
    if (!_seedFromSummary) return;                       // the records came back; nothing to seed
    var state = MyDatabase.ReadSettlement(container.ScopeKey);
    MyGame.SpawnColonists(container, state.Advance(DateTime.UtcNow));
};
```

Nebula never sees `state`. It is a row in the game's own database, keyed by a string the game chose
(`docs/location-contract.md` D2: Nebula never parses a scope key).

## 4. Non-goals (from the issue, kept)

No population model, no summary format, no offline simulation. No scheduler that ticks sleeping regions — a game
that wants one runs it in its own service and reads the store (§5). No relational game data in the entity store:
`nebula_entity` holds entities, and a settlement row belongs in the game's own tables.

## 5. Offline reads

The records of a scope nothing is simulating are ordinary records. **`IPersistenceStore.LoadWhere` is the offline
read** — a predicate over every record, answered on the main thread — with `Load` for one key and `LoadContainers`
for a list of boxes; `CountRecords` (D5) answers the cheap "is there anything there?" without moving any of them. A tool, a
game director or a web service reaches the same store through `RemotePersistenceStore` over the orchestrator's
`/api/store` endpoints, with the mesh token, and needs no worker and no activation.

Two things it is not. It is not a query language: `LoadWhere` filters in the caller for the local and remote stores
and only `CountRecords` is pushed down to SQL, so it is for tools and directors, never the per-lease restore path.
And it is not a way to change a live scope: a record written behind a worker that owns the entity loses to that
worker's next checkpoint, which is the epoch rule doing its job (`docs/persistence-durability.md`). Write to a scope
that is retired, or through the entity.

## 6. Seams left for the issues that follow

- **Scoped chunk grids (NEB-239)**: a grid's parts are ordinary scope containers, so each one's landing raises
  `OnContainerRestored` and the grid's scope raises `OnScopeActivating` once, whatever produced the parts. A grid
  that wants per-chunk "has this chunk anything saved?" has `CountRecords(scopeKey, containerId, …)` already.
- Nothing here needs the retire or restore sequence to change again.
