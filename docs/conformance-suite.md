# Conformance suite — design

Status: every item of the project "Scoped worlds and interaction contracts" has landed and every scenario in §4 is
covered (rows 1–18). The multi-process tier D is partly built by the scale and failure suite (NEB-237, `docs/scale-suite.md`);
scenarios that still name tier D for their end-to-end half say so in their row. The table in §4 is the ledger. User-facing page: `website/content/docs/guides/conformance-suite.mdx`. Runner: `Tools/conformance.ps1`.

## 0. Purpose

Each contract and scope item in the project ships with a guarantee ("the same location triple resolves the same
container everywhere", "a cross-worker call is applied at most once across a handover"). The conformance suite is
the **exit criterion** for those guarantees: one deterministic scenario per item, on a small mesh, that fails when
the guarantee is broken and passes on every checkout, every machine, with no build, no live processes and no game
content. It is not a load test, not a soak, and not a substitute for `Tools/smoke-test.ps1`, which runs a real mesh
from a player build and is neither deterministic nor free.

Every conformance test is an ordinary NUnit test tagged `[Category("Conformance")]`. That is the whole contract with
the runner: the category is what `Tools/conformance.ps1` selects, in both places tests live.

## 1. Where the tests live

| Place | Compiled by | Runs in | What can be tested |
|---|---|---|---|
| `Packages/com.1by3.nebula/Tests/EditMode/*.cs`, asmdef `Nebula.Tests.EditMode` | the Unity Editor | Unity batchmode (`-testCategory Conformance`) | everything, including `NebulaWorker`, `NetworkIdentity`, behaviours |
| the same files, when pure C#, added to `Nebula.Services.Tests.csproj` with `Compile Include` (define `NEBULA_SERVICE`) | the .NET SDK | `dotnet test --filter TestCategory=Conformance` | anything with no `UnityEngine.Object`: wire codecs, interest, control plane, the standalone gateway, the fake-mesh fixtures |

A file compiled in both places uses `#if NEBULA_SERVICE using Nebula.ServicePrimitives; #else using UnityEngine; #endif`
for `Vector3`/`Quaternion`, exactly as `InterestWireTests.cs` does. Put the pure part of a scenario in a file of its
own when the worker-side part needs Unity, so the fast `-DotnetOnly` run still covers the wire and the gateway.

## 2. Harness tiers and the decision

Three ways to stand up a "small deterministic mesh" were on the table. All three exist today; the decision is which
one a scenario reaches for first, and what each one cannot prove.

### Tier A — pure C# over production types (preferred)

Plain NUnit over the production classes, no Unity, no sockets: `HandoverScopeTests`, `InterestIndexTests`,
`OrchestratorAssignmentTests`, `ControlPlaneJsonTests`. When a guarantee lives in a pure class (a location resolver,
an epoch check, an assignment planner, a codec), test the class. These run in both places and in milliseconds.

Also in this tier: the **fake-mesh fixtures** in `Services~/Nebula.Services.Tests/Fixtures/MeshFixtures.cs`. A
`FakeWorker` is built from the real interest code (`RegionSubscriptionReceiver`, `RegionPublisher`,
`InterestIndex<T>`, `CarriedTransition`, `HandoverScope`) and talks to the **real standalone gateway** over LiteNet on
loopback; `FakeClient` is a real client handshake; `Fleet` stands up several gateways and a control plane in one
process. `InstanceTests`, `InterestTests`, `GatewayFleetTests`, `CarriedSubtreeInterestTests` show the pattern.
This is the tier for anything the gateway or the control plane decides: subscriptions, session handoff, leases,
instance isolation, redirects.

Limits: no `NebulaWorker`, so nothing about how a worker builds or applies a message; a `FakeWorker` implements the
protocol, not the simulation. Loopback UDP is real, so a scenario must pump until quiet rather than count packets,
and two test processes on one machine can collide on a port (re-run once).

### Tier B — the in-process worker mesh (`ConformanceMesh`, Unity EditMode)

`Tests/EditMode/ConformanceMesh.cs`: two or more **real `NebulaWorker` components** in one Editor process, each with a
recording transport in place of a socket and a `Peer` record for every other worker. Every byte a worker sends is
what production code decided to send; `Pump()` delivers it, in order, into the receiving worker's own `Dispatch`, and
keeps going until the mesh is quiet. `ContainerRegistry` and `NetworkPrefabs` are process-wide, so both workers
resolve the same containers and instantiate ghosts from the same prefab table, as two processes loading the same
build would.

This generalises the technique `WorkerHandoverTests` already uses (one real worker, a recording transport, reflection
to reach `TransferAuthority`) to a sender **and** a receiver, so a handover is tested end to end: the sender's own
`TransferAuthorityInScope` builds the `AuthorityTransferMsg`, `Write` serialises it, the receiver's `Dispatch` parses
it and `OnAuthorityTransfer` applies it, hooks and all. Private members (`TransferAuthority`, `Dispatch`, the nested
`Peer`) are reached by reflection rather than by widening `NebulaWorker`'s surface (**D1**): the worker file is under
concurrent change by other issues, and `InternalsVisibleTo` already exists for anything a scenario needs to *read*.
A rename fails loudly at the reflection call.

Limits, stated once here and not repeated per test:

- **No tick loop of its own, no leases, no control plane.** A handover is triggered by `Worker.Transfer(entity, target)`, or
  by `Worker.Tick(tick)`, which runs one whole worker tick by reflection: the tick's `ContainerRegistry.Resolve`
  against the owners `SetOwner` / `ContainerRegistry.ApplyLease` handed out, then the ghost band and the interest
  pass. The scenario decides when a tick happens; nothing else runs one. The decision *when* the orchestrator moves
  a lease is not tested here.
- **The container registry is process-wide**, so a carrier's copy on a second worker re-registers the carrier's
  box and the first worker's copy loses it. A scenario that moves a carrier between workers asserts on the
  receiver only.
- **No gateway link.** Nothing is announced to a gateway, so a scenario about what gateways are told belongs in tier A
  (the fake mesh has the real gateway) or reads the recorded outbox directly, as `WorkerHandoverTests` does.
- **Delivery is in order and lossless.** Duplicates and reordering are injected explicitly (`Mesh.Replay`), not
  suffered. Timeouts and grace periods that read `Time.unscaledTime` see Editor time, so anything time-based needs a
  clock seam before it can be a conformance scenario.
- **One process.** Static state (`NebulaRuntime.IsServer`, `LocalWorkerIndex`, the prefab table, the container
  registry) is shared; `Worker.Act` points the "which worker am I" statics at the acting worker for each call, and
  `Dispose` resets everything. Anything that caches those statics across calls is a real limit of this tier.
- **Unity only.** `dotnet test` never sees these; `-DotnetOnly` skips them.

### Tier C — EditMode over Unity-side types without a mesh

Instantiate `NetworkIdentity` and behaviours and drive the codecs directly: `ControlPlaneAndRpcTests`,
`PersistenceTests`, `SyncComponentTests`. Good for one behaviour's contract (a codec, a hook order); it cannot say
anything about what a worker does with the bytes. Use it when tier B would only add ceremony.

### Scale and failure suite (tier D is now partly built)

`docs/scale-suite.md` (NEB-237) is the sibling of this document for the question this suite deliberately does not
answer: not "does the guarantee hold on a small deterministic mesh" but "what does a busy mesh do when a worker
dies, a gateway is lost without a drain, the control plane restarts, or everything comes back at once, and how
long does each take". It has two layers, and the boundary matters here because it is exactly this section's
tiering seen from the other side:

- its **synthetic** layer is tier A — the same fake mesh (`MeshFixtures.cs`: real gateways, real control plane,
  real interest code, real client handshakes) driven at hundreds of clients across several workers and scopes,
  with the pieces killed and restarted. It runs under `dotnet test --filter "TestCategory=Scale"` in about eighty
  seconds. The fixtures gained what it needed: `Fleet.StartWorker`/`KillWorker`, `StartGateway`/`KillGateway`,
  `RestartControlPlane`, and `FakeWorker.SpawnIntoRequestedContainer`.
- its **real-worker** layer is the tier D described below, as far as one exists: `Tools/scale-suite.ps1` starts a
  real mesh through the `nebula` CLI, drives the load-test client, kills real processes and scrapes the
  orchestrator API and the workers' `[nebula] profile` lines into CSV. It is a measurement runner, not a
  deterministic test, and it needs a player build.

The two are not the same suite and must not be run as one. `[Category("Scale")]` is what keeps them apart:
`Tools/conformance.ps1` selects `TestCategory=Conformance` and never sees a scale scenario. The one place they
meet is scenario 10 below — the worker-kill loss bound — which the scale suite reuses rather than re-deriving.

### Not built: a full multi-process conformance mesh (tier D, later)

Real worker builds, a real gateway and orchestrator, driven and observed from a script. `Tools/smoke-test.ps1`
and `Tools/scale-suite.ps1` are this with a player build and wall-clock waits; neither is deterministic and both
need a build. A conformance-grade
version would need a scripted worker (headless player build with a test game mode, or the `Nebula.LoadGen` model
extended to workers), deterministic scheduling across processes and a log/telemetry oracle. It is the only tier
that can prove restart and process-loss guarantees end to end (scenario 1's "survives restart", scenario 3's restore
after a real retire). Phase 1 does not build it; the scenarios that need it are marked in §4 and, until it exists,
prove the pure parts in tier A and the worker parts in tier B.

### Decision

Prefer A, then B, then C, in that order, for each scenario; say in the test's summary which tier it is and why the
higher one would not do. Never mock a production type that can be run for real in the chosen tier.

## 3. Runner

`Tools/conformance.ps1` runs `dotnet test --filter TestCategory=Conformance` on `Nebula.Services.slnx` and, unless
`-DotnetOnly`, the Unity Editor in batchmode with `-testPlatform EditMode -assemblyNames Nebula.Tests.EditMode
-testCategory Conformance`. It reads the TRX and the NUnit 3 XML, prints one row per tier with passed/failed/skipped
counts and the failed test names, then a TOTAL row and PASS/FAIL, and exits 0 only when every tier that ran passed and
at least one ran. It refuses the Unity tier when `Temp/UnityLockfile` exists (the project is open in an Editor) and
says so instead of hanging. Results are left under `Logs/conformance/` (Unity empties `Temp/` when the Editor exits, so nothing that must outlive the run goes there).

## 4. Scenario ledger

Status values: **covered** (test exists and is tagged), **in progress (NEB-xxx)** (the item's issue is writing the
test in the named file; the integrator flips the row), **pending item** (the item has not landed; no test yet).

| # | Guarantee | Item | Tier | Test file / name | Status |
|---|---|---|---|---|---|
| 1 | The same location triple resolves the same container on gateway, worker, orchestrator and client; survives handover, restart and a store round trip. | NEB-220 | A (+D for a real process restart) | `Tests/EditMode/ConformanceLocationTests.cs` (9 tests: two independently populated registries resolve the same container, unchanged by a handover, store round trip through `LocalPersistenceStore` and the JSON codec, pinned wire bytes, pre-contract rows read as the public world; the pure-C# ones also run in the service tests) | covered |
| 2 | Scope activation is idempotent across two concurrent requesters; a private key yields a distinct scope. | NEB-233 | A (control plane; fake mesh for the routing half) | `Tests/EditMode/ConformanceScopeActivationTests.cs` (12 tests: two requesters get one scope and one set of lease rows, a private key yields disjoint containers, sixteen threads racing the storage constraint on one key are all told the same definition, a second orchestrator run adopts the stored claim, a conflicting definition is refused without changing anything, readiness follows the leases, the document round trip and the `Hello` field; runs in both places). The gateway leg is `Services~/Nebula.Services.Tests/ConformanceScopeRoutingTests.cs` (3 tests: a client is routed into the scope it names, a client naming an unactivated scope is held rather than put in the public world, activating completes the held join with no reconnect) — it needs `MeshFixtures`, which only the service tests compile. | covered |
| 3 | A scope retires when idle, restores identically with its persisted entities, and admission is refused until the restore completes. | NEB-240 | A for the machine and the admission, B/C for the store; D for a real retire on a real mesh | `Tests/EditMode/ConformanceScopeLifecycleTests.cs` (9 tests: the whole sequence in order — idle by the lease clock, `Retiring` refuses before anything is saved, one acked part is not enough, the leases go and the row stays, re-activation restores under the same container ids and admits nobody until every part reports in; a busy part keeps the scope hot, occupancy and the knob veto, a game policy overrides, an unanswered step finishes on the deadline, an ack from the wrong phase is dropped, the document round trip; runs in both places). The store leg is `Tests/EditMode/ConformanceScopeCheckpointTests.cs` (tier B/C, Unity only: a real `NebulaPersistence` + `LocalPersistenceStore` force-checkpoints a real scope part, waits on `WhenWritten`, and every record comes back identical through `Apply`; an empty part still reports its restore, once). The gateway leg is `Services~/Nebula.Services.Tests/ConformanceScopeAdmissionTests.cs` (2 tests: a client is held and told `ScopeRetiring` / `ScopeNotReady` / `ScopeRestoring` in turn and placed with no reconnect once the restore completes; a public client is unaffected) — it needs `MeshFixtures`, which only the service tests compile. The orchestrator's loop and the workers' acks are what the tests stand in for; the state machine, the ordering and the admission rule are production code. | covered |
| 4 | Two scopes with overlapping chunk coordinates get distinct leases and records, with no cross-scope ghosts or interest leakage. | NEB-239 | A (fake mesh) + B (worker ghosts) | `Services~/Nebula.Services.Tests/ConformanceScopedGridTests.cs` (3 tests: two grid scopes at one coordinate get distinct lease rows carrying their own scope and part id, the grid definition round-trips through the scope row and is inferable from a container row alone, and a client in one scope receives neither the other scope's entities nor its chunk rows — not even the empty chunk its own window covers in the other world). The worker leg is `Tests/EditMode/ConformanceScopedGridTests.cs` (3 tests: an entity ghosts across its own scope's seam, never ghosts into a scope standing on exactly the same ground, and the two chunks are two containers with two lease keys) — it needs real `NebulaWorker`s, so only the Unity tier compiles it. | covered |
| 5 | A cross-worker call is applied at most once when the target hands over between send and apply; a stale-epoch call is rejected with a reason. | NEB-221 | A (the router, ledger and tracker are pure C#; a scripted three-worker handover) | `Tests/EditMode/ConformanceCallContractTests.cs` (19 tests: applied once across a mid-flight handover, replay rejected as a duplicate, stale epoch rejected with reason visible to the sender, hop bound, bounded ledger memory, pinned wire bytes; runs in both places) | covered |
| 6 | `StateAt(tick)` matches recorded poses on the authority and on a ghost within the documented bound. | NEB-222 | B (ghost side, two real workers) + C (the ring) | `Tests/EditMode/ConformanceStateHistoryTests.cs` (7 tier-B tests: authority and ghost agree tick for tick, a ghost is exactly one tick behind, outside the window is unavailable, `[SyncHistory]` variables snapshot per tick, the worker-level query, history survives a handover, a zero window records nothing) and `StateHistoryRingTests` in the same file (10 tier-C tests: capacity, oldest/newest, gaps, monotonicity, allocation-free recording) | covered |
| 7 | A cohesion group is handed over as a unit; the planner never splits it; an oversize group is reported. | NEB-223 | A (planner, telemetry) + B (handover) | `Tests/EditMode/ConformanceCohesionTests.cs` (12 tests: the cost policy deals a group as one item, keeps it whole through a re-deal, reports an oversize group instead of splitting it, a hold defers a move and holds the whole group but never strands an orphan, and a hold reported in seconds becomes a deadline on the orchestrator clock; runs in both places); `Tests/EditMode/ConformanceCohesionHandoverTests.cs` (8 tests on two and three real workers: any member takes the group along, out and back, leaving the group stops it, a member this worker does not own is counted and logged, a ghost on its way to the same worker is not a split, and the real telemetry document round trips through `MeshTelemetry`) | covered |
| 8 | A server-driven entity with handover state crosses workers and keeps every field: pose, velocity, NetworkVariables, handover state, `IsServerDriven`, one epoch bump; hooks fire once each in the documented order; a replayed transfer at the same epoch is ignored. | existing behaviour (NEB-238) | B, plus A for the wire | `Tests/EditMode/ConformanceHandoverStateTests.cs` (`AServerDrivenEntityCrossesWorkersWithEveryFieldIntact`, `AHandoverBackKeepsTheFieldsAndBumpsTheEpochAgain`, `AReplayedTransferAtTheSameEpochIsIgnored`); `Tests/EditMode/ConformanceHandoverWireTests.cs` (3 tests, both places) | covered |
| 9 | A diagnostic fires for a cross-container joint without a cohesion group. | NEB-234 | C | `Tests/EditMode/ConformancePhysicsDiagnosticTests.cs` (validator fires for a cross-container `HingeJoint` and names both containers, silent for a same-container joint, worker-side warning once per entity, `AuthorityRpc` from a copy that is neither authoritative nor ghost is rejected and counted). The cohesion-group exemption test was un-ignored by NEB-223 and passes. | covered |
| 10 | Loss after a worker kill never exceeds the documented durability window (`docs/persistence-durability.md`). | NEB-224 | C (a real `NebulaPersistence` + `LocalPersistenceStore`, clock seam `NebulaPersistence.Now`) | `Tests/EditMode/ConformancePersistenceDurabilityTests.cs` (kills a worker with 200 simultaneously dirty entities after exactly the frames the documented bound predicts; asserts 0 loss, and that the min-save throttle and per-frame budget terms are both real, not vacuous) | covered |
| 11 | The lifecycle hooks fire in the documented order: `OnScopeActivating` before any of the scope's containers restores, `OnContainerRestored` only after the restore, `OnBeforeRetire` complete before anything is checkpointed, `OnRetired` after the leases are released. | NEB-242 | B/C (the real `WorkerScopeLifecycle` and `NebulaPersistence` over `LocalPersistenceStore` and `LocalControlPlane`) + A for the store count | `Tests/EditMode/ConformanceLifecycleHooksTests.cs` (7 tests: an activation says whether records exist and the scope's restore is held behind it, a first activation is told there is nothing saved, no handler means no gate and no store query, the before-retire window finishes before the first save and `OnRetired` only after the lease has gone, a window that overruns is cancelled and the retire proceeds, a handler that throws is logged and the others still run, and the store counts a scope's records without activating it). The store leg in SQL and over HTTP is `Services~/Nebula.Services.Tests/StorageAndHostTests.cs` (`SqlPersistenceStoreCountsAScopesRecordsWithoutReadingThem`, and the count through `PersistenceHost` in `RemotePersistenceStoreGoesThroughThePersistenceHost`). Spawning a restored entity into a scope part needs play mode, so the restore's count is asserted against `NebulaPersistence.RestoredCountFor` — the number the scope's ack carries — rather than against a spawned entity; end to end is tier D. | covered |
| 12 | A join or transfer into a saturated target is rejected with a typed reason unless the admission hook admits it. | NEB-236 | A (fake mesh: gateway + control plane), plus unit tests for the derivation | `Services~/Nebula.Services.Tests/ConformanceCapacityAdmissionTests.cs` (6 tests: a join into a saturated scope is refused with `JoinRejectReason.AtCapacity`, the saturation on the wire, and the scope still has exactly the parts it was authored with; the hook admits one named arrival into the same full scope and refuses another with the game's own sentence; the hook can hold the join instead and the held client is placed with no reconnect once the container reports quiet again; a target with room does not even ask the hook; a public container at capacity refuses and admits again when it empties; a target no worker has reported admits as it always did) - it needs `MeshFixtures`, which only the service tests compile. The orchestrator's `PublishCapacity` pass is what the test stands in for; the derivation, the gateway's refusal, the wire field and the hook are production code. The derivation itself is `Tests/EditMode/CapacityAdmissionTests.cs` (16 tests, both places: the dominant component's saturation against its own budget, a threshold of 0, an unknown target, the lease round trip and that it leaves the idle clock alone, a scope as full as its worst part, the default policy, `AlwaysConsult`, a throwing policy, and the typed wire round trip with an older message still reading). | covered |
| 13 | A hot container is re-dealt along authored boundaries without splitting a cohesion group; a held container is not moved; an unsplittable one is reported with the reason. | NEB-235 | A (the policy, the planner and the scaler are pure C#) | `Tests/EditMode/ConformanceRebalanceTests.cs` (10 tests: a city of six districts on one worker is re-dealt across two without cutting the group that spans two of them and every move carries an explanation naming the boundary, a group move names the group that kept it together, a held district stays until the hold is no longer reported, a hot held district is reported with cause `Held`, a group that spans the whole of a hot worker is reported with cause `CohesionGroup` and the sentence "cohesion 7 spans c0, c1", a lone hot container with no authored boundary is reported with cause `NoBoundary` rather than shuffled, a lopsided but quiet mesh reports nothing, a dry run keeps a group whole so the predicted peak is honest, the scaler refuses to grow and names the group in `BlockedCause` / `BlockedReason` / `Reason`, and a policy that does not explain itself leaves the scaler's sentence untouched; runs in both places) | covered |
| 14 | One worker hosts two scopes with overlapping local coordinates; each shifts its origin independently, entities in each keep precision and physics isolation, and the scaler's dry run consolidates both onto one worker at low load. | NEB-241 | A for the scaler dry run and the region keys, B (`ConformanceMesh`) for the worker and the origin frames | `Tests/EditMode/ConformanceScopeFrameTests.cs` (7 tier-A tests: the dry run puts two low-load scopes on one synthetic worker, the scaler shrinks a two-worker mesh holding both, twelve worlds of one chunk plan exactly like one world of twelve chunks, and the scope-salted region ids are distinct per scope, invertible, and the identity in the public world; runs in both places). The worker leg is `Tests/EditMode/ConformanceScopeFrameWorkerTests.cs` (8 tier-B tests on one real `NebulaWorker`: each scope owns its origin and one shift leaves the other alone, both scopes sit on Unity's origin at once, an entity keeps its local pose and gains precision across its scope's shift, the other scope's entities do not move, `StateAt` still means the same place, the registry answers each scope separately even when the boxes coincide, a shift does not disturb the other scope's spatial hash, and a client in one scope keeps exactly one frame — its own). **Tier B does drive real origin shifts** (a scoped grid's shift needs no streamer and no play mode), but it cannot create the per-scope `PhysicsScene`s: `InstanceScenes.Prepare` only makes a runtime scene in play mode, so the isolation asserted there is the isolation that decides it (distinct isolation ids, scope-qualified registry queries, `PhysicsIslands.SameIsland`) and the live two-scene half stays covered by `Tests/PlayMode/InstancePhysicsTests.cs`. | covered |
| 15 | Two carriers whose interiors overlap never ride inside each other: the tick keeps running, at most one rides in the other, `InstanceId` / `ScopeKey` resolve, a placement that would close a carrier cycle of any length is refused, a corrupt chain degrades to "no scope" and is healed by the next tick, and all of it holds across a handover. | carrier-cycle fix (a worker overflowed its stack on every tick with twenty ships in one chunk) | B (`ConformanceMesh`, whole ticks via `Worker.Tick`) | `Tests/EditMode/ConformanceCarrierCycleTests.cs` (5 tests: two overlapping carriers nest one level and no more, twenty ships in one 64 m chunk form one chain with exactly one ship left in the chunk, refusal at every cycle length, a forced cycle reads as the public scope and one tick heals it, a carrier with an overlapping carrier aboard hands over to a second worker and neither resolves into the other there). Nothing is pure C#: the rule lives in `Container` and `NetworkIdentity`; the interest index's own cycle refusal is already pinned by `CarriedTransitionTests`. | covered |
| 16 | A crewed carrier crossing into a chunk of another grid scope: a group prepared for the ship and its crew becomes ready and commits with the crew in their seats; every rider's client follows the ship into the destination with no reconnect, also when only the ship crossed; an onlooker in the old scope loses it and is never sent the destination's row; no client is sent an update naming a container before its row. | crossing (`docs/scope-activation.md` §11) | A (fake mesh, real gateway) + B (the group commit) | `Services~/Nebula.Services.Tests/ConformanceCrossScopeCarrierTests.cs` (4 tests: a preparation into another scope sends the rider its destination row ahead of the request and the client answers ready, nobody else gets the row and no destination entity is revealed; a ship crossing alone takes its rider's client into space with no despawn while the onlooker loses it and never hears of the destination; a rider reconnecting after the crossing finds the ship in space by name; updates naming a chunk whose lease reaches the gateway late are held until the row arrives, so nobody loses the ship and no client is sent an unresolvable name) — it needs `MeshFixtures`, which only the service tests compile. The worker leg is `Tests/EditMode/ConformanceCrossScopeCrewTests.cs` (2 tier-B tests: a ship and two riders committed as one group arrive with the riders still seated, a new epoch each and their scope following the ship; a rider committed without its ship is put down in the destination). | covered |
| 17 | A worker that gains hundreds of containers at once restores every one of them with a bounded number of store requests: at most `NebulaPersistence.MaxRestoreLoadsInFlight` reads of at most `MaxContainersPerRestoreLoad` containers each, every container read once and judged with its own records, a container whose lease moved on while its read was out not restored from the stale answer; and the worker's persistence client never has more than `RemotePersistenceStore.MaxConcurrentReads` requests in flight, whatever it is asked for. | restore-burst fix (a worker re-dealt a few hundred containers fired one load per container, starved its thread pool, missed its control-plane heartbeats and was declared dead, and the next worker got the same burst) | C for the worker (a real `NebulaPersistence` over `LocalPersistenceStore` behind a pass-through that holds the answers), A for the store and the wire | `Tests/EditMode/ConformanceRestoreBurstTests.cs` (2 tests, Unity only: 600 containers dealt in one control-plane change are read in three batches, never more than two waiting, each container once, and every scene record lands against its own container; a container dealt away mid-read is skipped while the rest of its batch restores, and is read on its own when it comes back). The store and wire leg is `Services~/Nebula.Services.Tests/ConformanceRestoreBurstTests.cs` (4 tests: `SqlPersistenceStore` answers 601 container ids once from chunked `IN` queries on SQLite, carried and unrequested records excluded; `RemotePersistenceStore` sends them to a real `PersistenceHost` as three `POST /api/store/containers` requests and answers once; a burst of 600 single reads plus 1000 container ids never has more than `MaxConcurrentReads` requests open at a slow orchestrator; the host refuses an oversized or malformed request and no longer serves the one-container endpoint). Not covered: the thread-pool starvation itself, which only a Mono player shows; the tests pin the bound that prevents it. | covered |
| 18 | A chunk is never retired from under a client's pawn at any carrier depth (a pilot in a ship, a passenger in a shuttle in that ship's hangar), nor from under an authoritative entity filed under it that stands in a cell this worker wants or another worker has leased; the same chunk with an empty ship in unwanted space is retired. A retired chunk takes a vehicle's cargo with the vehicle: riders are despawned before their carriers, none is set down in the deleted box, each persistent one is saved aboard its carrier, and the forced checkpoint and the busy rule of a scope part count riders too. | crewed-carrier retire fix (a sample with fast ships outran its chunk leases; the allocator retired the chunk each ship was still filed under and despawned the ships from under their pilots) | B (`ConformanceMesh`, a real `RuntimeGridAllocator` over a `LocalControlPlane` with a driven clock) + C for the records (a real `NebulaPersistence` over `LocalPersistenceStore`) | `Tests/EditMode/ConformanceCrewedCarrierRetireTests.cs` (5 tests, 10 cases, Unity only: a ship 17 km past its only chunk keeps the chunk with its pilot aboard and loses it empty; in a retiring scope a client's pawn one and two carriers deep occupies the chunk and an empty ship does not; an uncrewed ship filed under a stale chunk keeps it while it stands in a wanted cell or in a cell another worker leased just now, and not in space nobody wants; releasing a chunk despawns a ship, the shuttle in its hangar, a crate in the shuttle and a transient drone, leaves nothing in the box and saves the shuttle and the crate aboard their carriers, and the forced checkpoint counts all three persistent entities; a part is busy with a transient drone or a client's pawn aboard and idle with only saved vehicles). Design: `docs/dynamic-worlds.md`, "Retiring a chunk under a carrier". | covered |

### Existing tests tagged into the suite

Only tests that pin one of the guarantees above as they stand were tagged; nothing was moved or rewritten.

| Test | Pins | Scenario |
|---|---|---|
| `ControlPlaneAndRpcTests.HandoverStateRoundTripsPerBehaviourAndIsolatesFaultyChunks` | the per-behaviour handover-state blob: chunk framing, order, and isolation of a faulty reader | 8 |
| `PersistenceTests.TheKeyTravelsWithTheHandoverSoTheNextWorkerUpdatesTheSameRecord` | a persistent entity's store key is part of its handover state, so the next worker updates the same record | 8 |

`SerializationTests.AuthorityTransferMessageCarriesHandoverState` was not tagged: `ConformanceHandoverWireTests`
supersedes it for the suite (every field, both places) and the old test stays as the serialization unit test it is.
`WorkerHandoverTests` (carrier subtree leaves as a unit) is close to scenario 7 but was **not** tagged by NEB-223.
It pins the carried-subtree mechanism, which cohesion deliberately does not reuse (`docs/cohesion-hints.md`, D4):
a carried subtree is containment inside one box on one ordered stream, a cohesion group is co-location of peers in
unrelated containers, and each member is an ordinary handover. Tagging it would make scenario 7 fail when the
carrier rules change for reasons that have nothing to do with cohesion. The interaction that does matter - a group
member that is itself a carrier still takes its passengers - is asserted in `ConformanceCohesionHandoverTests`
through the mesh, and `WorkerHandoverTests` stays the unit test of the carrier rule it always was.

## 5. Adding a scenario

1. Pick the tier per §2 and say why in the class summary.
2. Name the file `Tests/EditMode/Conformance<Item>Tests.cs`, tag the class `[Category("Conformance")]`, reference
   the scenario number and this document in the summary.
3. Pure C#? Add the file to `Nebula.Services.Tests.csproj` under the conformance `Compile Include` and use the
   `NEBULA_SERVICE` primitive switch.
4. `bash Tools/gen-meta.sh Packages/com.1by3.nebula` for the `.meta`.
5. Flip the row in §4 and the table in the user page; update the `## [Unreleased]` changelog entry.
6. `powershell -File Tools/conformance.ps1` must print PASS.
