# Conformance suite — design

Status: phase 1 (harness, runner, scenario 8) landed with NEB-238. Scenarios for the other items of the project
"Scoped worlds and interaction contracts" are added by the issue that lands each item; the table in §4 is the
ledger. User-facing page: `website/content/docs/guides/conformance-suite.mdx`. Runner: `Tools/conformance.ps1`.

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

- **No tick loop, no leases, no control plane.** A handover is triggered by `Worker.Transfer(entity, target)` instead
  of by the tick's `ContainerRegistry.Resolve` against lease ownership. The decision *when* to hand over is the
  orchestrator's and the tick's; this tier tests *what a handover carries and does*, not when it happens.
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

### Not built: a multi-process mesh (tier D, later)

Real worker builds, a real gateway and orchestrator, driven and observed from a script. `Tools/smoke-test.ps1` is
this with a player build and wall-clock waits; it is not deterministic and needs a build. A conformance-grade
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
| 4 | Two scopes with overlapping chunk coordinates get distinct leases and records, with no cross-scope ghosts or interest leakage. | NEB-239 | A (fake mesh) + B (worker ghosts) | — | pending item |
| 5 | A cross-worker call is applied at most once when the target hands over between send and apply; a stale-epoch call is rejected with a reason. | NEB-221 | A (the router, ledger and tracker are pure C#; a scripted three-worker handover) | `Tests/EditMode/ConformanceCallContractTests.cs` (19 tests: applied once across a mid-flight handover, replay rejected as a duplicate, stale epoch rejected with reason visible to the sender, hop bound, bounded ledger memory, pinned wire bytes; runs in both places) | covered |
| 6 | `StateAt(tick)` matches recorded poses on the authority and on a ghost within the documented bound. | NEB-222 | B (ghost side, two real workers) + C (the ring) | `Tests/EditMode/ConformanceStateHistoryTests.cs` (7 tier-B tests: authority and ghost agree tick for tick, a ghost is exactly one tick behind, outside the window is unavailable, `[SyncHistory]` variables snapshot per tick, the worker-level query, history survives a handover, a zero window records nothing) and `StateHistoryRingTests` in the same file (10 tier-C tests: capacity, oldest/newest, gaps, monotonicity, allocation-free recording) | covered |
| 7 | A cohesion group is handed over as a unit; the planner never splits it; an oversize group is reported. | NEB-223 | A (planner, telemetry) + B (handover) | `Tests/EditMode/ConformanceCohesionTests.cs` (12 tests: the cost policy deals a group as one item, keeps it whole through a re-deal, reports an oversize group instead of splitting it, a hold defers a move and holds the whole group but never strands an orphan, and a hold reported in seconds becomes a deadline on the orchestrator clock; runs in both places); `Tests/EditMode/ConformanceCohesionHandoverTests.cs` (8 tests on two and three real workers: any member takes the group along, out and back, leaving the group stops it, a member this worker does not own is counted and logged, a ghost on its way to the same worker is not a split, and the real telemetry document round trips through `MeshTelemetry`) | covered |
| 8 | A server-driven entity with handover state crosses workers and keeps every field: pose, velocity, NetworkVariables, handover state, `IsServerDriven`, one epoch bump; hooks fire once each in the documented order; a replayed transfer at the same epoch is ignored. | existing behaviour (NEB-238) | B, plus A for the wire | `Tests/EditMode/ConformanceHandoverStateTests.cs` (`AServerDrivenEntityCrossesWorkersWithEveryFieldIntact`, `AHandoverBackKeepsTheFieldsAndBumpsTheEpochAgain`, `AReplayedTransferAtTheSameEpochIsIgnored`); `Tests/EditMode/ConformanceHandoverWireTests.cs` (3 tests, both places) | covered |
| 9 | A diagnostic fires for a cross-container joint without a cohesion group. | NEB-234 | C | `Tests/EditMode/ConformancePhysicsDiagnosticTests.cs` (validator fires for a cross-container `HingeJoint` and names both containers, silent for a same-container joint, worker-side warning once per entity, `AuthorityRpc` from a copy that is neither authoritative nor ghost is rejected and counted). The cohesion-group exemption test was un-ignored by NEB-223 and passes. | covered |
| 10 | Loss after a worker kill never exceeds the documented durability window (`docs/persistence-durability.md`). | NEB-224 | C (a real `NebulaPersistence` + `LocalPersistenceStore`, clock seam `NebulaPersistence.Now`) | `Tests/EditMode/ConformancePersistenceDurabilityTests.cs` (kills a worker with 200 simultaneously dirty entities after exactly the frames the documented bound predicts; asserts 0 loss, and that the min-save throttle and per-frame budget terms are both real, not vacuous) | covered |

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
