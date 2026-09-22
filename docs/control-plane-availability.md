# Control-plane and entity-store availability (NEB-227)

Status: landed. User-facing page: `website/content/docs/deploy/availability.mdx`. Synthetic layer:
`Services~/Nebula.Services.Tests/ScaleAvailabilityTests.cs` (`[Category("Scale")]`, one scenario also
`[Category("Docker")]`). Drill: `Tools/restore-drill.ps1` + `Services~/Nebula.RestoreDrill`. Artifacts:
`Logs/scale/synthetic-orchestrator-*.csv`, `Logs/restore-drill/<timestamp>.{json,txt}`.
Related: `docs/scale-suite.md` (S6, D7b, D7c — this record closes both gaps), `docs/persistence-durability.md`
(what a worker loses, which is a different question), `docs/architecture.md`.

## 0. The question

A studio about to put years of work behind Nebula asks three things about the boxes that hold the world's
bookkeeping:

1. **The orchestrator restarts.** Does the mesh survive it, and for how long is it degraded?
2. **The database under it fails over.** Does the mesh survive that, and what is lost?
3. **The database is restored from a backup.** Does the world come back, and can that be rehearsed before it has
   to be done for real?

Each of the three now has a measured answer and something that runs. What is **not** in scope is running the
database: Nebula does not ship a PostgreSQL operator, a replication topology or a backup schedule, and this
record says so rather than implying otherwise.

## D1. A worker re-registers; it did not before

`NebulaGateway` already noticed when its own control-plane row was gone and registered again
(`OnControlPlaneChanged`). `NebulaWorker` did not: it registered once at startup
(`if (!_registered && ControlPlane.IsConnected)`) and never again. A control plane that came back **with** its
document was therefore invisible to the mesh, and one that came back **empty** — a reset orchestrator, a restore
from a backup taken before this mesh started, a failover to a replica that never had the document — converged on
no workers and no leases until every worker process was restarted by hand. That was `docs/scale-suite.md` D7b,
pinned by a test asserting the broken behaviour.

`Runtime/Worker/WorkerRegistration.cs` is the rule, split out of `NebulaWorker` so it is testable and so the
synthetic mesh drives the *real* decision code rather than a fixture's imitation of it. It holds the row's
identity and three operations:

- `Register` — the first registration, from the update loop.
- `RegisterAgainIfForgotten` — from `OnControlPlaneChanged` only, never on a timer. A `RemoteControlPlane` learns
  of its own registration when the next document arrives, so a poll would re-register on every frame of that
  round trip. A *change* means the document that just landed is one this worker is not in.
- `ReclaimContainers` — put back the lease rows for the containers this worker is still simulating.

The file is compiled into `Nebula.Services` beside `AuthorityCallContract.cs`, so it runs against the services'
container registry and against the Unity one from the same source.

**D1a. Only a worker knows what a worker simulates.** A gateway needs no reclaim: everything it does is derived
from leases it reads. A worker is the other way round. After an empty restart, the control plane has no record
that container `c2` is being simulated at all, and the only process in the mesh that knows is the one holding its
entities. So the reclaim is not a nicety on top of the re-registration — without it the re-registration produces
a mesh of workers that own nothing and a planner that deals the containers out again, to workers that do not have
the entities.

**D1b. The reclaim is a standing reconciliation, not a one-off.** The obvious shape — "when you notice your row
is gone, also put your leases back" — is wrong, and building the process-level scenario is what showed it. A
`RemoteControlPlane` queues its writes while the orchestrator is away and flushes them when it returns, so a
replacement orchestrator can have this worker's registration back *from a queued write* in the very first
document that shows the leases gone. The transition never happens on a document this worker can see, and a
reclaim hung off it stranded the containers permanently. `ReclaimContainers` therefore runs on **every**
control-plane change and writes only what is missing. It de-duplicates itself: a container it has asked for goes
into a pending set and is not asked for again until its row appears, so a slow round trip costs one write per
container, not one per change (measured: exactly 4 writes for 4 containers).

**D1c. Epochs start again at 1, and that is correct.** A reclaimed baked container is written with
`EnsureContainer` + `AssignContainer`, which gives it epoch 1 in the new document. An epoch only ever has to
increase *within* one control-plane document: every peer reads epochs out of the document it mirrors and none of
them remembers the previous one's. A runtime container is re-announced with `EnsureRuntimeContainer` and its box,
because the box exists nowhere else in the mesh.

**D1d. What is not reclaimed.** A dynamic container follows its carrier and its row is written by whoever owns
the carrier. A container whose row *is* there is left alone, whatever it says: a worker that overwrote a lease it
had lost would take a container back off the worker it was given to. And nothing reclaims a container the worker
is not simulating — the sweep is driven by `Container.OwnerWorkerId`, which is the worker's own view of what it
holds.

## D2. Gateways were checked and needed nothing

`NebulaGateway.OnControlPlaneChanged` re-registers on a missing row and re-reads ownership on every change; its
`Incarnation` already distinguishes a restarted gateway process from a stale drain request. The measured
cold-restart scenario confirms it: the gateway is back on the control plane without any change to gateway code.
Gateway **session** claims across a gateway loss are a different problem and belong to NEB-229 (`docs/scale-suite.md`
D7a).

## D3. Fast restart, not hot standby

**Decision:** one orchestrator. No second process, no leader election, no shared-lock dance. The deliverable is a
*measured* restart, and a bound that says when a restart is slow enough to matter.

The reasoning is in the numbers rather than in taste. A worker or gateway mirror keeps its last known topology
and queues its writes while the orchestrator is unreachable, and reports itself disconnected only after
`RemoteControlPlane.DisconnectAfterSeconds` (15 s = the 10 s long poll plus 5 s). Inside that window a restart is
not merely survivable, it is **invisible**: no mirror changes state, no lease moves, no client is touched,
because a gateway's client links never depended on the control plane. A hot standby would buy the difference
between 2.6 s and 0 s of a stall during which nothing was wrong anyway, at the cost of a second source of truth
for a document that only one process may write.

`ScaleThresholds.OrchestratorRestartSeconds` is therefore **15 s, derived**: past it the restart stops being
invisible and starts being an outage a mirror can see. What the mesh cannot do during the stall is exactly what
the orchestrator decides: nothing declares a worker dead, nothing starts a replacement, nothing rebalances, and
no new container assignment is made.

**D3a. The one thing to get right.** A replacement orchestrator must be given the same storage, and the operator
must not pass `-nebula-reset` (its default is *on*, because registrations normally describe processes that are no
longer running). A restart that resets is the cold path of D1 — survivable now, but it re-deals epochs and costs
the mesh a stall it did not need.

## D4. What a database failover costs

`ControlPlaneHost` writes the whole document to storage a second after every change, in a background task, and
never blocks the mesh on it. A save that throws is logged once, the `_storageDirty` flag is put back, and the
next interval tries again. There is nothing to replay because the contract is *whole-document replace*: the next
successful save carries everything, including whatever changed while the database was gone.

So a failover costs exactly one thing: **the window in which a crash would have lost the changes made since the
last successful save**. It does not cost rows, it does not cost clients, and it does not cost the simulation.
`ControlPlaneHost.StorageError` is now public so an operator (and the failover scenario) can watch that window
rather than guess at it.

`ScaleThresholds.DatabaseFailoverSeconds` is **60 s, provisional and deliberately loose**: what it bounds is a
container restart on a development machine, and a managed PostgreSQL failover is usually slower. The assertion
that matters beside it is not the number — it is that nothing was lost and nothing threw out of `Tick`.

**D4a. Saved entities are a different store with the same answer.** `SqlPersistenceStore` runs its jobs on one
writer thread with a retry, and `NebulaPersistence` checkpoints on a schedule. A failover delays checkpoints; it
does not drop them, and the loss bound if the *worker* dies during one is `docs/persistence-durability.md` D2,
unchanged by any of this.

## D5. The synthetic layer goes one level deeper than S6

`ScaleFailureTests`'s S6 restarts the control plane as an **object**: `LocalControlPlane.ResetControlPlane()` and
an import of a document taken earlier. That is the right shape for "what do the gateways and the clients do while
it is away" — and it is what measures that no client is disconnected and no session changes, which needs a real
gateway with real clients on it. It proves nothing about the orchestrator *process*.

`ScaleAvailabilityTests` is the process level. The control plane is the real `ControlPlaneHost` served over the
real `OrchestratorHttpServer` and stored in the real `SqlControlPlaneStorage` over SQLite; the workers and the
gateway are real `RemoteControlPlane` mirrors over loopback HTTP; a restart disposes the host and the listener
and stands a replacement up on the same port and the same database. The two layers are complementary and the
suite keeps both.

**D5a. Three things the scenario has to do to be honest.**

- **Wait for the port.** On Windows a second socket can bind a port the first has not finished releasing, and the
  stopped server goes on answering `503 orchestrator shutting down`. Without the wait, the "restart" measures two
  servers fighting over an address.
- **Demand a live round trip.** The first version of the scenario recorded 0.03 s, because the mirrors still held
  the old document and the replacement had restored the same leases from the database — every condition was true
  before a single packet moved. The replacement now writes a marker setting and the scenario waits for every
  mirror to read it back. The honest number is 2.59 s.
- **Prove the wipe.** The same rule in the restore drill (D7): a drill whose wipe left the data in place passes
  on the leftovers.

**D5b. What the synthetic layer still cannot show.** One machine, one loopback: no second host, no real network
partition, no packet loss, no clock skew, and no managed database's failover behaviour. The mirrors are driven by
a test loop rather than by a Unity update, and the fixture's `MirrorWorker` runs the prologue of
`NebulaWorker.OnControlPlaneChanged` rather than the whole of it (there is no simulation behind it to restore).
What it does show is every decision the control-plane code makes, over real HTTP, against a real database file.

## D6. PostgreSQL failover, and what happens without Docker

The failover scenario (`APostgresFailoverStallsTheControlPlaneStoreAndLosesNothing`) starts
`postgres:16-alpine`, drives a real `ControlPlaneHost` against it, restarts the container — a primary going away
mid-life and coming back with the same data, which is the shape of a failover the orchestrator can tell apart
from a slow query — and measures the stall until the store is writable again. It asserts that the orchestrator
lost no rows, kept its worker, and put the document back on the first save that succeeded.

It shells out to `docker` rather than taking a Testcontainers dependency: the scenario needs two verbs, and a
test that is skipped on most machines should not add a package every build has to restore. It is tagged
`[Category("Docker")]` **and** `[Category("Scale")]`, so an ordinary `dotnet test` run never reaches it, and it
calls `Assert.Ignore` with the reason when `docker version` fails or the image cannot be pulled.

**On this machine it was skipped.** `docker version` reports a client but no server (`failed to connect to the
docker API at npipe:////./pipe/dockerDesktopLinuxEngine`): Docker Desktop is installed and not running. The
scenario's skip path is what ran, and it printed its reason. **The PostgreSQL leg has therefore not been
executed here**, and `DatabaseFailoverSeconds` has no measurement behind it yet — it is provisional in the
strongest sense, a budget rather than an observation.

## D7. The restore drill

`Tools/restore-drill.ps1` rehearses the thing nobody rehearses. It needs no player build, no Unity and no
running mesh, and by default it runs against a throwaway SQLite file under `Logs/restore-drill/` so it cannot
touch a mesh's own database by accident.

Steps, each timed into the artifact: **seed** a known world → **snapshot** → **back up** → **wipe** → *snapshot
the wiped database and insist it is empty* → **restore** → **snapshot** → **compare**. It prints `PASS`/`FAIL`
and exits non-zero on a failure.

**D7a. The verification goes through Nebula's own store code.** `Services~/Nebula.RestoreDrill` reads every
record through `SqlPersistenceStore.LoadWhere` and the document through `SqlControlPlaneStorage.Load` — the same
classes a worker and an orchestrator read through, the same column list, the same epoch rule, the same decoder. A
drill written in SQL proves the bytes came back. This one proves the **mesh** can read what came back, so a
schema migration the store cannot read is a failed drill and not a green one.

The comparison is a SHA-256 digest per record over every field including the opaque state blob, plus a digest of
the control plane's durable parts (leases by container, workers, settings). Two fields are deliberately outside
the digests: `PersistedEntityRecord.SavedAt`, which a restore-by-import legitimately changes, and the document's
`now`, which is refreshed on every read and would make every snapshot differ while saying nothing.

**D7b. Three backup routes, and the artifact says which one ran.**

| Route | Used for | How |
|---|---|---|
| `sqlite-vacuum` | SQLite | `VACUUM INTO` from the connection Nebula already opens: SQLite's own online backup, with the write-ahead log folded in, while the mesh keeps running. A file copy is not safe with WAL on, and a `sqlite3` CLI is not something a studio should have to install. The restore clears the connection pools, deletes the `-wal`/`-shm` sidecars and copies the file into place. |
| `pg_dump` | PostgreSQL, when `pg_dump` and `pg_restore` are on `PATH` | `pg_dump --format=custom` / `pg_restore --clean --if-exists`. |
| `nebula` | Any backend; the fallback when the PostgreSQL client tools are missing, and `-Method nebula` on demand | Every record and the document as one JSON file, written and read back through the store. Slower than the engine's dump and not a production backup — but the only route that is portable between SQLite and PostgreSQL. |

A drill that silently fell back to a different mechanism than the one production uses would be reassuring about
the wrong thing, so the route is chosen once, printed, and written into the artifact.

**D7c. Non-goal: a schedule.** The drill rehearses a restore. It does not take your backups, does not keep them,
and does not tell you when the last good one was. That is the operator's job and the user-facing page says so.

## D8. Measured on this machine

A Windows development machine, one run each. Absolute values are machine-dependent; the shapes are not.

| Scenario | Artifact | Result |
|---|---|---|
| `orchestrator-restart` (S6b) | `Logs/scale/synthetic-orchestrator-restart.csv` | 4 leases, 2 workers, 1 gateway; the orchestrator process down and whole again on the same SQLite database in **2.59 s**; every lease back with the same owner, state and epoch; **0** mirrors reported themselves disconnected; 0 re-registrations needed; 1 write queued across the restart |
| `orchestrator-cold-restart` (S6b, empty database) | `Logs/scale/synthetic-orchestrator-cold-restart.csv` | replacement came back with 0 leases; converged in **2.61 s** to 2 workers and 4 leases, **owners identical**; 4 re-registrations, **4** containers re-claimed (one write each) |
| `control-plane-cold-restart` (D7b, in-process) | `Logs/scale/synthetic-control-plane-cold-restart.csv` | 2 workers and 1 gateway back, 4/4 leases re-claimed, **0** clients disconnected |
| `postgres-failover` (S6c) | — | **skipped: no Docker daemon on this machine** (D6) |
| restore drill, SQLite, `sqlite-vacuum` | `Logs/restore-drill/20260922-142825.json` | 250 records + 8 leases; seed 1.02 s, backup 0.63 s, wipe 0.67 s, restore 0.60 s, compare 0.63 s, **5.69 s total**; 0 missing, 0 extra, 0 changed, control-plane digest matches — **PASS** |
| restore drill, SQLite, `nebula` route | `Logs/restore-drill/20260922-142836.json` | 50 records + 8 leases, **5.43 s total** — **PASS** |
| restore drill, negative control | — | comparing the *before* snapshot against the *wiped* one reports 50 missing, control plane differs, and exits **1** |

The whole `[Category("Scale")]` suite, including the two new scenarios, is green: **15 tests, 14 run, 1 skipped,
0 failed**.

## D9. What was verified, and what was not

**Verified by running it.**

- `dotnet test --filter "TestCategory=Scale"`: 14 passed, 1 skipped (the Docker scenario), 0 failed.
- The turned-around D7b test in `ScaleFailureTests` passes with workers and leases coming back.
- `Tools/restore-drill.ps1` end to end on SQLite, both the `sqlite-vacuum` and the `nebula` routes, PASS, with
  the artifacts above.
- The drill's failure path, by comparing a populated snapshot against an emptied one: it names what differs and
  exits non-zero.
- `Tools/typecheck.ps1`: unchanged from the base commit (43 errors, all of them pre-existing, in
  `UnityEngine.TestRunner`'s generated assembly info and in `Samples~/LagCompensatedHitscan/Tests`, which cannot
  resolve NUnit from a worktree without the Editor's test packages). No Nebula runtime assembly has an error.

**Not verified.**

- **The PostgreSQL failover scenario has never executed.** Docker Desktop is installed on this machine but its
  daemon is not running, so the scenario took its `Assert.Ignore` path. Everything about it — the container
  arguments, the wait for the first connection, the stall measurement, the assertions — is written against
  `NebulaDatabase`'s PostgreSQL path and the `docker` CLI, and has not been run against a live PostgreSQL.
- **The drill has not been run against PostgreSQL**, for the same reason, so the `pg_dump` route is untried. The
  `nebula` route *has* run and is the fallback that route falls back to.
- **No real worker process re-registered.** The re-registration and the reclaim run in the services build of
  `WorkerRegistration`, driven by the fixtures and by `MirrorWorker`; `NebulaWorker` calls the same class from
  `OnControlPlaneChanged`, and that call site is verified by compilation only, because a Unity worker needs a
  player build (`docs/scale-suite.md` D11).
- **No multi-machine test.** Everything is loopback (D5b).

## Non-goals

Not a hot standby, and not leader election (D3). Not a replication topology, a backup schedule, a retention
policy or a PostgreSQL operator: Nebula does not run the database for the customer, and the restore drill
rehearses a restore rather than performing a backup (D7c). Not a gateway-loss story — that is NEB-229 and
`docs/scale-suite.md` D7a. Not a change to what a *worker* kill loses, which stays
`docs/persistence-durability.md` D2.
