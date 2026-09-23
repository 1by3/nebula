# Scope lifecycle — design

Status: design of record for **NEB-240**, protocol **v18** (one appended `JoinStatus` byte, no version bump).
Decisions made without asking are marked **D#**. Builds directly on `docs/scope-activation.md` (NEB-233), which
gave a scope a row, a key, container ids and `RemoveScope`, and on `docs/persistence-durability.md` (NEB-224),
which gave the checkpoint scheduler its clock seam. User-facing page:
`website/content/docs/guides/scopes.mdx` §"Retiring and re-activating a scope". Conformance tests:
`Tests/EditMode/ConformanceScopeLifecycleTests.cs`, `Tests/EditMode/ConformanceScopeCheckpointTests.cs` and
`Services~/Nebula.Services.Tests/ConformanceScopeAdmissionTests.cs` (all `[Category("Conformance")]`, scenario 3
of `docs/conformance-suite.md`).

## 0. Problem

A raid instance a matchmaker activated on Tuesday is still holding lease rows on Friday. Nothing in Nebula ever
took it away: `RemoveScope` exists, and per container `RuntimeContainerIdleSeconds` / `ReleaseRuntimeContainer`
exist, but *deciding* that a whole scope is finished with, saving what is in it, letting go of every part together,
and refusing to let anybody in while it comes back, was left to the game. The instancing guide said as much in so
many words: "automatic empty-instance expiration and suspension are not currently provided."

The parts games were being asked to write are not game logic. "Is anybody interested in any part of this scope?",
"all its parts go together or none of them do" and "nobody may enter until every part has been restored" are
properties of the mesh, and getting them subtly wrong produces exactly the failures a server-meshing library
exists to prevent — a scope half-retired with a player still standing in it, or a player admitted into a room whose
chest has not been loaded yet, who then watches it pop into existence beside them.

## 1. The state machine

The orchestrator is the **single writer** of a scope's state, for the same reason it is the single writer of a
lease: it already aggregates leases and load, everything it writes is mirrored to every other role by the ordinary
document, and one writer is what makes an ordering guarantee possible at all.

```
   (no row) ──activate──▶ Active ──ShouldRetire──▶ Retiring ──every part checkpointed──▶ Retired
                            ▲                                                              │
                            └──── every part restored ──── Restoring ◀──── activate ───────┘
```

| State | Admits clients | Lease rows | What is happening |
|---|---|---|---|
| `Active` | yes | one per part | ordinary life; the idle sweep judges it |
| `Retiring` | **no** | still there, still owned | each part runs the retire sequence (§3) |
| `Retired` | no | **gone** | the row is kept so the key keeps its identity |
| `Restoring` | **no** | back, same ids | each part is loading its records |

**D1 There is no `Activating` and no `Idle` state.** A first activation goes straight to `Active`: nothing was ever
checkpointed under a key that has no row, so there is nothing to restore and NEB-233's behaviour — including
`PrepareInstance` owning its box from the first change anyone sees — is unchanged. `Idle` would be a *stored* state
that the sweep would have to write and un-write as occupancy flickers, i.e. a control-plane write per flicker, to
say something the row already implies: idleness is `ScopeLifecycle.IdleSeconds(scope)`, derived, free and always
current. A state exists here only when something waits on it.

**D2 A retired row is kept; only `RemoveScope` deletes one.** The key has to keep its identity: the isolation id,
the container ids and the durable claim are all derived from or held against the key, and a re-activation must be
idempotent with respect to a run that retired the scope an hour ago. Keeping the row also means the dashboard can
show what a mesh has been asked for rather than only what is currently hot. `RemoveScope` stays the explicit,
game-driven way out (scope-activation D11) and is unchanged.

**D3 An activation that arrives mid-retire is refused, not queued.** Bringing the scope back while the checkpoint
is still writing its records would race the restore against the save. The activation changes nothing at all, is
logged, and the caller — which is polling the row anyway, because activation is fire and forget — retries. The next
activation, of a `Retired` row, restores.

## 2. Deciding that a scope is idle

**D4 Idle age is the minimum over the scope's parts, and each part's age is its lease row's age.** The lease row's
`UpdatedAt` is *already* the mesh-wide "when did anybody last want this box" clock: every worker re-stamps a runtime
container it wants but does not own (`NebulaWorker.RuntimeTouchSeconds`), and `RuntimeContainerIdleSeconds` is
documented as exactly this. Taking the minimum is what makes "all parts retire together" safe from the other
direction: one busy part keeps the whole scope hot, so a retire can never take a room out from under a player
standing in the cellar.

**D5 The owning worker keeps a busy part hot, and "busy" means a retire would lose something.**
`WorkerScopeLifecycle.KeepHot` re-stamps the lease of a scope part it owns, at most every
`NebulaWorker.RuntimeTouchSeconds`, while `IsBusy(container)` holds. A part is busy when it holds an authoritative
entity, directly or riding in a vehicle in it at any depth (a pilot in a ship parked in the part: emptying the part
takes the riders too, see `docs/dynamic-worlds.md`, "Retiring a chunk under a carrier"), that is

* owned by a client (`OwnerClientId != 0`) — an interested client is in it; or
* not persistent at all — its state cannot be brought back, so retiring would destroy it; or
* persistent with unsaved changes (`PersistentEntity.IsDirty` or `PersistentStateCodec.HasDirtyVars`).

That is the reading of "no interested client, no non-persistent, no dirty entity" that makes the default policy's
promise — *it comes back identical* — true rather than hopeful. It is deliberately conservative: a scope whose game
spawns transient NPCs never goes idle by itself, and a game that is happy to discard them says so through the
policy hook (§D6) rather than by Nebula guessing.

**D6 The policy is a hook with a time-based default.** `ScopeLifecycle.ShouldRetire` is a
`ScopeRetirePolicy` — `bool (in ScopeRetireContext)` — consulted for every `Active` scope on the orchestrator's
sweep. The context carries the row, the idle age (D4), the occupancy the workers' telemetry reports for the scope's
containers, and the configured threshold. The default, `ScopeLifecycle.RetireWhenIdle`, is:

```csharp
if (RetireAfterSeconds <= 0f) return false;          // the knob turns it off
if (Players > 0 || Entities > 0) return false;       // something is in it
return IdleSeconds >= RetireAfterSeconds;
```

A policy that throws is logged and treated as "keep the scope": it is the game's code, and the failure mode of a
wrong answer here is destroying a world. The hook is a static rather than an instance callback because there is one
orchestrator per mesh and the decision must not depend on which of its objects a game happened to reach.

**D7 Two independent readings, and a clock seam.** The occupancy term comes from the per-container counts the
workers already post on the telemetry path (`MeshTelemetry.CopyOccupancy`, the same table the assignment planner
uses); the age term comes from the lease rows. They are belt and braces: telemetry can be stale or absent, and the
lease clock can only be *too young*, never too old. `LocalControlPlane.Clock` is the seam (internal, defaulting to
`DateTime.UtcNow`) that lets a test age a scope by minutes without waiting; nothing in Nebula sets it.

**`ScopeIdleRetireSeconds`** is the knob, in both `NebulaConfig` copies, with `-nebula-scope-idle-retire`. It
defaults to **300 s** and 0 turns retiring off. It never applies to the public world, which has no scope row.

## 3. The retire sequence

Ordered, and the order is the point. Steps 2–4 run on the worker that owns the part; steps 1 and 5 are the
orchestrator's.

1. **Mark `Retiring`.** Admission stops here, *before* anything is saved (§4), so no client is admitted into a
   scope that is about to empty itself.
2. **The before-retire window.** `WorkerScopeLifecycle.BeforeRetire` — `Func<Container, CancellationToken, Task>`,
   awaited with `BeforeRetireTimeoutSeconds` (10 s) and cancelled if it overruns. **This is the seam NEB-242
   exposes publicly as `OnBeforeRetire(container, cancelToken)`**; it is `internal` here because the issue that
   owns the hook surface should decide how a game registers one. Its *position* and its *window* are settled now,
   which is what the rest of the sequence needed.
3. **Force the checkpoint.** `NebulaPersistence.CheckpointContainer(containerId)` saves every authoritative
   persistent entity in the part, riders of its vehicles included (each saved aboard its carrier), regardless of
   the checkpoint schedule, and `IPersistenceStore.WhenWritten` is then
   the barrier that says the saves reached the store. This is the one place in Nebula that waits on that barrier;
   everywhere else a save is fire and forget (`docs/persistence-durability.md` D3), which is right for a periodic
   checkpoint and wrong for the last one a container will ever get.
4. **Empty the part and acknowledge.** `NebulaWorker.EmptyContainer` despawns the contents, riders before their
   carriers — persistent entities with `keepPersisted` so their records stand, everything else for good — and the
   worker writes
   `AckScopePart(key, containerId, ScopePhase.Checkpointed, saved)`.
5. **Release the leases and mark `Retired`**, once **every** container of the scope has acknowledged. Only then:
   the checkpoint in step 3 has to happen while the owner still holds the lease, so deleting the rows earlier
   would be deleting the thing doing the saving.

**D8 The acknowledgement is a row on the scope, not a reply.** `ScopeInfo.Acks` is one `ScopeAck`
(`ContainerId`, `Phase`, `Count`, `WorkerId`) per container at most, written by the worker through the ordinary
control-plane op `AckScopePart` and cleared on every state change, so an ack always belongs to the step that is
running now. Acks from the wrong phase, or for a container the scope does not own, are dropped. This is the same
argument as scope-activation D1: the progress of a retire is shared state that a restarting orchestrator, a second
gateway and the dashboard all have to see, not a per-caller answer.

**D9 Every step has a deadline.** `ScopeLifecycle.StepTimeoutSeconds` (30 s) finishes a step whose parts never
answered, with a warning. A worker that died mid-retire must not leave a scope stuck in `Retiring` — nobody could
ever enter it again — or in `Restoring` — nobody could ever join it. `ScopeLifecycle.NextState` is the pure
function that decides "every part, or the deadline", so the ordering is one testable thing rather than a shape of
the orchestrator's loop.

## 4. Re-activation and admission

Activating a `Retired` key puts the row in `Restoring` and recreates the lease rows. The ids are derived from the
key (scope-activation D3), so they are the same ids the records name, and the *ordinary* lease-landing restore
takes it from there: each worker that lands a part waits out `PersistenceRestoreGraceSeconds`, loads the
container's records and brings them back. Nothing new restores anything.

**D10 "Restore completed" is a new signal, not a new mechanism.** `NebulaPersistence.ContainerRestored`
(`Action<string containerId, int restored>`) fires once per lease, on the main thread, after the load has been
judged — including when the container had nothing saved in it, which is the case that would otherwise wedge a
scope forever. `IsContainerRestored` / `RestoredCountFor` are the same fact as a query. **This is the seam NEB-242
exposes as `OnContainerRestored`.** The worker turns it into `AckScopePart(..., ScopePhase.Restored, n)`; when
every part has acknowledged, the orchestrator marks the scope `Active`.

Records held back because their previous owner may still hand the entity over (`RestorePlan.Wait`) do not delay
the signal: the restore of what is *saved* has happened, and a record that is waiting is one the mesh is about to
deliver by handover anyway.

**D11 Admission refusal is the hold that already exists, with a reason.** A client whose `Hello` names a scope that
is not `Active` is held in `JoinState.Starting` — welcomed, never dropped, retried every few seconds, placed with
no reconnect the moment the scope admits again — which is exactly what scope-activation §5 already did for a scope
with no live owner. The gateway checks `ScopeLifecycle.Admits` *before* it collects spawn candidates, because
during `Retiring` and `Restoring` the lease rows are present and owned: the state, not the leases, is what refuses.

`JoinStatusMsg` gains one appended byte, `JoinHoldReason` (`WorldStarting`, `ScopeNotReady`, `ScopeRestoring`,
`ScopeRetiring`), readable on the client as `NebulaClient.JoinHoldReason`, so a game can say "your instance is
coming back" instead of "please wait". **D12 The protocol version is not bumped again**: 18 is already unreleased
and already breaking, and `JoinStatusMsg.Read` tolerates a message that ends before the byte, reading it as
`WorldStarting` — the only reason a gateway from before this item ever held a join.

`IsScopeReady` is unchanged and still answers only "does every part have a live owner". The two questions are
different and a caller usually wants both: `Admits` is "may a client go in", `IsScopeReady` is "is there a worker
simulating it".

## 5. What the dashboard shows

`/api/state` gains a `scopes` array — `key`, `state`, `parts`, `ready`, `players`, `entities`, `idleSeconds`,
`stateSeconds`, `ageSeconds`, `acked`, `containers` — and `scopeIdleRetireSeconds`. `NebulaDashboard.html` shows
them in a Scopes card that is hidden until a mesh activates one, so a mesh that uses no scopes looks exactly as it
did. The card is deliberately read-only: retiring a scope by hand from a dashboard is a destructive act on a live
world, and the hook (§D6) is the supported way to change the policy.

## 6. Ordering guarantees, stated plainly

1. **No admission during a retire.** `Retiring` is written before any entity is saved and before any lease is
   deleted, and the gateway refuses on the state.
2. **All parts retire together.** The leases are deleted, and the scope becomes `Retired`, only after *every*
   container has acknowledged its checkpoint (or the deadline passed, with a warning).
3. **Every checkpoint is durable before its part is emptied.** The worker waits on `WhenWritten` before it
   despawns anything and before it acknowledges.
4. **All parts restore before admission.** The scope becomes `Active`, and therefore admits anybody, only after
   every container has acknowledged its restore (or the deadline passed, with a warning).
5. **Re-activation is idempotent and identical.** The container ids, the isolation id and the durable claim are
   derived from the key, so the scope that comes back is the same scope, holding the records the retire wrote.

## 7. Non-goals (from the issue, kept)

Nebula does not summarise a scope's contents, does not decide what is "important enough to keep hot" beyond the
hook, and **never deletes a persisted record** — a retire keeps every record it wrote, and `RemoveScope` deletes
rows and claims, not records. Transient (non-persistent) contents are lost by a retire, which is why the default
busy test (D5) refuses to retire a part that holds any.

## 8. Seams left for the issues that follow

- **Lifecycle hooks (NEB-242)** exposes the two seams named above: `WorkerScopeLifecycle.BeforeRetire` becomes
  `OnBeforeRetire(container, cancelToken)` (§3 step 2, already ordered and already given a cancellable window), and
  `NebulaPersistence.ContainerRestored` becomes `OnContainerRestored` (§D10, already raised once per lease,
  including for an empty container). Neither needs the sequence to change.
- **Scoped chunk grids (NEB-239)** get idle retirement for free the moment their parts are ordinary scope
  containers: D4 aggregates over `ScopeInfo.ContainerIds`, whatever produced them. A grid whose parts come and go
  will want `ContainerIds` to be the *current* set at the moment of the sweep, which it already is.
