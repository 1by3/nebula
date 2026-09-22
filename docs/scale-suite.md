# Scale and failure suite (NEB-237)

Status: landed with NEB-237. User-facing page: `website/content/docs/guides/scale-suite.mdx`. Synthetic layer:
`Services~/Nebula.Services.Tests/Scale*Tests.cs` (`[Category("Scale")]`). Real-worker layer:
`Tools/scale-suite.ps1`. Artifacts: `Logs/scale/`. Baseline: `docs/baselines/mesh-restart.csv`.
Related: `docs/conformance-suite.md` §2 (this is tier D, now partly built), `docs/persistence-durability.md`
(the worker-kill bound), `docs/cohesion-rebalancing.md` and `docs/cohesion-hints.md` (holds, saturation),
`docs/cost-telemetry.md` (the blocked component), `docs/autoscale.md`.

## 0. Purpose

The conformance suite proves a guarantee holds on a small deterministic mesh. This suite answers a different
question, the one a studio asks before committing years of work: **what happens to a busy mesh when something
breaks, and can it be measured again next month?** It is phrased entirely in workers, containers, scopes,
entities, sessions and milliseconds. Game-specific scenarios run on top of it, in the game's own repository, and
do not gate Nebula releases.

Every scenario has a threshold, produces a CSV artifact, and says which of the two layers measured it.

## D1. Two layers, never mixed

| | Synthetic (tier A) | Real workers (tier D) |
|---|---|---|
| What runs | the **real** `NebulaGateway`, the real control plane, the real interest code inside `FakeWorker`, real client handshakes over loopback UDP — all in one `dotnet test` process (`Services~/Nebula.Services.Tests/Fixtures/MeshFixtures.cs`) | real Unity player-build workers, a real gateway and orchestrator as separate processes, started by the `nebula` CLI, driven by `Services~/Nebula.LoadGen` |
| Runs with | `dotnet test --filter "TestCategory=Scale"` | `pwsh Tools/scale-suite.ps1` |
| Needs | nothing | a player build, `nebula` on `PATH`, free ports |
| Proves | what the gateways and the control plane decide: admission, sessions, subscriptions, leases, replica sets, per-client bandwidth, the planner's moves and reports | what a worker actually costs: tick time, simulation, scene restore, process lifetime, real sockets |
| Cannot prove | anything about simulation cost or a real scene load | anything deterministically; it is a measurement, not a test of a rule |
| Artifacts | `Logs/scale/synthetic-*.csv` | `Logs/scale/unity-*.csv` |

**D1a.** The layer is stamped on every artifact (first column) and on every line of runner output
(`[scale:synthetic]` / `[scale:unity]`). A CSV that does not say which layer produced it is worse than no CSV:
the two series measure different things and averaging them would be meaningless.

**D1b.** The synthetic layer is tagged `[Category("Scale")]`, so `Tools/conformance.ps1` (which selects
`TestCategory=Conformance`) never picks it up. Scenarios that run a mesh for seconds carry `[Category("Soak")]`
as well, so the ordinary `dotnet test --filter "TestCategory!=Soak"` run is unaffected: only the two pure
planner scenarios (about 12 ms together) stay in it.

## D2. Scenarios, status and thresholds

| # | Scenario | Layer | Threshold | Status |
|---|---|---|---|---|
| D3 | Sustained load: 120 clients, 4 workers, 2 gateways, 480 entities of scenery | synthetic | per-client bandwidth < 512 kB/s; no orphan updates; no duplicate views | covered — `ScaleLoadTests.SustainedLoadKeepsEveryClientInsideItsReplicaAndBandwidthBounds` |
| | the same with real workers | unity | worst worker tick ≤ 16.7 ms (one tick period at 60 Hz); load client exits 0 | runner written, **not run here** (§D11) |
| D4 | Burst: 150 clients join at once, no ramp | synthetic | nobody refused; one session each; one pawn each | covered — `ABurstOfJoinsIsAdmittedWithoutRejectingAnyoneOrLosingASession` |
| D5 | Many scopes: 4 keyed scopes of 20 clients plus 20 public clients | synthetic | zero cross-scope leaks; per-scope bandwidth recorded | covered — `ManyScopesCarryTheirOwnLoadAndNeverShowEachOtherAnything` |
| D6 | Worker kill: a worker dies with clients watching its container | synthetic | restore ≤ 2 s per container; zero duplicate entities; nobody disconnected; loss ≤ the NEB-224 bound | covered — `ScaleFailureTests.AWorkerKillOrphansItsContainersAndTheRestoreBringsThemBackWithNoDuplicateEntities`; the loss bound itself is conformance scenario 10 |
| D7 | Gateway lost without a drain | synthetic | abrupt stop: every session reclaimed, ≤ 10 s. Hard kill: **not recovered** | covered, and the hard-kill half is a pinned gap (§D9) |
| D8 | Control-plane restart and failover | synthetic | leases identical after the restart; stall ≤ 5 s; nobody disconnected; no session changed | covered for a restart that kept its storage; a real database failover is NEB-227 (§D9) |
| D9 | Whole-mesh restart | synthetic | monotonic curve, ≤ 2 s per container, within 6× the checked-in baseline | covered — `AWholeMeshRestartBringsTheContainersBackOneAtATimeAndTheCurveIsRecorded`, baseline `docs/baselines/mesh-restart.csv` |
| D10 | Autoscale and rebalance | synthetic | no move touches a held container or its cohesion group; a saturated container is reported with a typed cause and component | covered — `ScaleOperationsTests.ARebalanceMovesNothingThatIsHeldAndASaturatedContainerIsReportedWithItsReason` |
| D11 | Rolling upgrade | synthetic | a version mismatch is refused cleanly; a gateway process is replaced under connected clients with no session lost | covered, with the finding that **there is no compatibility window** (§D8) |

### Where the numbers come from

| Threshold | Value | Derivation |
|---|---|---|
| Worker-kill loss | `PersistenceCheckpointSeconds + MinSaveIntervalSeconds + ceil(N/64)×framePeriod + storeLatency` ≈ **5.6 s** at the defaults | **Derived** from the scheduler in `Runtime/Persistence/NebulaPersistence.cs`; see `docs/persistence-durability.md` D2. Asserted by conformance scenario 10, which reuses the `NebulaPersistence.Now` clock seam; this suite does not re-derive it. |
| Gateway reclaim | **≤ 10 s** | **Derived**: `GatewaySessionAdmission` gives a coordinated welcome a `CoordinationDeadline` of 10 s. Past it the join is refused, so anything slower is not a reclaim at all. The measured abrupt-stop reclaim is 0.05 s. |
| Restore per container | **≤ 2 s** | **Provisional, measured.** The synthetic restore is an in-process re-spawn (measured 0.31 s per container, of which 0.3 s is the fixture's own settle after a focus hint), so this budget has headroom for a scene load it has never seen. A real Unity number needs the tier-D run. |
| Control-plane stall | **≤ 5 s** | **Provisional, measured** (0.003 s in process). What is *not* provisional is the assertion beside it: no client is disconnected and no session changes, because a gateway's client links do not depend on the control plane. |
| Per-client bandwidth | **< 512 kB/s** | **Provisional.** It is a property of this fixture's world (how many entities sit inside one interest window), not a production budget. It exists to catch an interest regression that starts sending a client the world; measured 6.4 kB/s. |
| Worker tick (tier D) | **≤ 16.7 ms** | **Derived**: one tick period at the default 60 Hz tick rate, the same budget `ContainerCost` weighs a container's simulation share against. |
| Baseline slack | **6×** | **Provisional.** Absolute times depend on the machine; the baseline is compared as a shape (same containers, monotonic, each step inside the per-container budget and within 6× the recorded step). |

Numbers marked provisional are in `ScaleThresholds` (`Fixtures/ScaleHarness.cs`), which says the same thing at
the point of use, and are mirrored in `Tools/scale-suite.ps1`'s `$T` table.

## D3. What the fixtures gained

The fake mesh could stand a mesh up but not take one apart. Added to `Fleet`:

- `StartWorker(id?)` / `KillWorker(worker)` — a worker process appearing and dying. A kill drops the socket (so
  every gateway link goes) and unregisters from the control plane, which orphans every lease it held. Nothing is
  handed over and nothing is drained. Returns the orphaned container ids: what the restore has to place again.
- `StartGateway(configure?)` / `KillGateway(index, hard)` — see D7 for what the two kinds model.
- `RestartControlPlane(snapshot)` — `ResetControlPlane()` then `Import(...)` of a document taken earlier with
  `ToJson()`. Passing `null` models a restart that came back with nothing.
- `FakeWorker.SpawnIntoRequestedContainer` — put a pawn in the container the gateway's `SpawnPlayerMsg` named,
  as a real worker does. Off by default because the interest tests place pawns themselves; on for the scale
  suite, because a client that joined a scope must have its pawn inside that scope or per-scope isolation cannot
  be measured at all.

**D3a. Observing a container you do not stand in.** A client only holds entities near it, so "did the container
come back?" would otherwise be answered by wherever the gateway happened to re-spawn that client's pawn. The
failure scenarios instead give each client a **focus hint** at the centre of the cell it is watching, and the
fixture sets `FocusMode.Free` for it first. That is deliberate and faithful: a hint is clamped to
`InterestSettings.HintMaxDistance` from the pawn and is refused outright while a client has no pawn — which is
exactly the state a worker kill leaves its players in — and `FocusMode.Free` (an observer camera) is the
gateway's decision to make, never the client's. The fixture makes it the way a game would.

## D4. One world for every scenario

`Fixtures/ScaleWorld.cs` builds the same world every time: a row of equal 128 m containers along x, one per
worker, plus any number of keyed scopes activated on top. Keeping the shape fixed is what makes the numbers in
`Logs/scale/` comparable across scenarios and across runs. Scope parts sit at x ≈ 100 km, well clear of the
baked row, so nothing about their isolation depends on geometry: what keeps a scope's entities out of another
scope's clients is the scope, not the distance.

## D5. Artifacts

`ScaleReport` writes one CSV per scenario to `Logs/scale/<layer>-<scenario>.csv`, overwritten each run, and
echoes the path and its conclusions to the test output. `Logs/` is not checked in; the one artifact that is, is
the restore-curve baseline under `docs/baselines/`, which a run compares itself against and writes on a machine
that has none.

The repository root is found by looking for `Packages/com.1by3.nebula/package.json`, not for `.git`: in a git
worktree `.git` is a *file*, and a directory probe would walk past the root and write the artifacts where nobody
looks.

## D6. Measured on this machine

The numbers below are one run of the synthetic layer (12 scenarios, **1 m 19 s** wall clock) on a Windows
development machine. They are what the CSVs held; treat absolute values as machine-dependent.

| Scenario | Result |
|---|---|
| sustained-load | 120 clients, 221 replicas each on average (142–268), 5.4 kB/s each (max 6.4), gateway loop lag 14.4 ms, 0 orphan updates, 0 duplicate views |
| burst-load | 150 clients admitted in 3.6 s, 0 refused, 150 distinct sessions, 150 pawns |
| many-scopes | 4 scopes × 20 clients + 20 public; 8.4 kB/s per scoped client, 5.8 kB/s per public client, **0 cross-scope leaks** |
| worker-kill | 1 container orphaned; the gateways dropped its entities in 0.03 s; whole again in 1.07 s; 0 duplicate spawns; 40 clients, 40 pawns |
| gateway-stop (abrupt) | 12/12 sessions reclaimed on the surviving gateway in 0.05 s, same session ids |
| gateway-kill (hard) | **0/4 reclaimed**, refused after 10.05 s — see D7 |
| control-plane-restart | 4 leases back in 0.003 s, 0 disconnected, 0 session changes |
| control-plane-cold-restart | gateway re-registered; **0 workers, 0 leases** — see D7 |
| mesh-restart | 4 containers back in 1.25 s, 0.31 s each, curve in `docs/baselines/mesh-restart.csv` |
| autoscale-rebalance | 2 moves explained; under a hold, 0 moves; saturated `c0` reported as `no-boundary`, `Simulation`, 0.948 of the tick budget |
| rolling-upgrade | protocol 17 and 19 both disconnected; 18 admitted; session kept across a gateway replacement |

## D7. Gaps this suite measures rather than hides

Two of the issue's scenarios depend on work that is not done (NEB-227, control-plane availability; NEB-229, the
gateway audit). Both are **built against what exists**, and the current behaviour is pinned by a test that will
fail when it changes, so the issue that closes the gap is told to come back here.

**D7a. A hard gateway loss does not reclaim its sessions.** `GatewaySessionDirectory` grants a takeover only
when the previous gateway *releases* its claim. A gateway that is stopped cleanly does release (its `Dispose`
sends a `release` for every session it holds), and the measured reclaim is 0.05 s. A gateway that is *killed*
never does: every claim on it stays pending for 15 s, is re-pended by the next attempt, and nothing evicts it —
a gateway that has stopped heartbeating is not treated as gone. Every reclaim is therefore refused after the
10 s coordination deadline with `the other session could not be disconnected`. The operator workaround today is
to request a drain on the dead gateway's control-plane row, which clears the claim.
`AGatewayThatIsKilledOutrightStrandsItsSessionsUntilSomethingEvictsItsClaims` asserts this **negative** and says
in its failure message that closing the gap means updating this document and turning the assertion around.

**D7b. A control plane that comes back empty loses its workers.** `NebulaGateway.OnControlPlaneChanged`
re-registers the gateway when it notices its row has gone. `NebulaWorker` has no such path: it registers once
(`if (!_registered && ControlPlane.IsConnected)`) and never again. So a control plane restarted *with* its
storage keeps every lease, every session and every client (measured: 4/4 leases, 0.003 s, nobody disconnected),
while one restarted *without* it has no workers and no leases until every worker process is restarted. Durable
control-plane state and failover are NEB-227. `ARestartThatCameBackEmptyReclaimsItsGatewaysButNotItsWorkers`
pins both halves.

**D7c. Database failover itself is not exercised.** The synthetic layer restarts a `LocalControlPlane` in place
through the same snapshot/import path the orchestrator uses at startup, which is the right shape but not a real
failover: no storage is swapped, no connection is lost mid-write, no write is replayed. That needs NEB-227's
work and a real database. Status: **partially covered**.

## D8. There is no compatibility window

`NebulaGateway.DispatchClient` compares the peer's `HelloMsg.Version` to the `HelloMsg.ProtocolVersion` constant
for **exact equality** and disconnects on any difference. There is no minimum version, no negotiation and nothing
that reads N-1. `HelloMsg.Write` does not even serialise the instance's `Version` field — it always writes the
constant — so a peer built from this source *cannot* announce an older protocol. The test therefore lays the
`Hello` bytes out by hand; that is the finding, not a shortcut.

Consequently a rolling upgrade **across protocol versions is not possible today**, and the scenario tests what
the issue's guidance says to test instead: that a mismatch in either direction is refused cleanly (the link is
closed, nothing is welcomed, no worker is ever asked to spawn a pawn, and peers of the current version are
undisturbed). The half of a rolling upgrade that *does* work — replacing a gateway process under connected
clients at one protocol version, with every session kept — is asserted in the same test.

A real compatibility window would need a minimum-supported-version field on the gateway, a `Hello` that writes
the sender's own version, and per-version encoders. It is out of scope here; this document is where the case
for it is now written down.

## D9. The real-worker runner

`Tools/scale-suite.ps1` is the tier-D layer. It never reimplements mesh startup: `nebula stop` / `nebula start
--workers N --bots 0` / `nebula build` do that (`docs/cli.md`), exactly as `Tools/smoke-test.ps1` does. It then

1. starts the load client (`dotnet run --project Services~/Nebula.LoadGen -- --gateway 127.0.0.1:7000 --clients N
   --seconds S --ramp R --bot --csv <file>`), which writes its own per-second CSV and exits non-zero if any
   session changed or any pawn was lost;
2. samples `GET /api/state` and `GET /api/cost` every 5 s into `Logs/scale/unity-<scenario>-state.csv`
   (desired/live workers, players, containers, worst `tickMs`, worst `utilization`, worst `oldestDirtySeconds`,
   the hottest container with its dominant cost component and saturation, and the scaler's action and
   `blockedBy`);
3. scrapes each worker log's `[nebula] profile` line (`Runtime/Worker/NebulaWorker.cs`, every 5 s) into
   `unity-<scenario>-profile.csv` as `worker, sample, avgMs, maxMs, ticks, authoritative, ghosts`;
4. injects the failure by stopping a real process — the worker whose command line carries
   `-nebula-worker-id <id>`, the same way `smoke-test.ps1` does — or by `nebula stop` for the whole mesh;
5. asserts the thresholds and prints `PASS`/`FAIL` with a non-zero exit code.

Modes: `-DryRun` checks the preconditions and prints the plan without running anything; `-Synthetic` runs the
other layer (`dotnet test --filter TestCategory=Scale`) and reports its wall clock; the default is the real run.
`-Build` runs `nebula build --stop-mesh` first.

**D9a. `gateway-kill` is not a tier-D scenario.** The local CLI mesh starts one gateway, and a reclaim needs a
second one to reclaim onto. The runner says so and points at the synthetic measurement rather than pretending.
A real multi-gateway fleet is NEB-229 territory.

**D9b. Detecting that this machine cannot run it.** `Test-Preconditions` checks for the `nebula` CLI on `PATH`,
a player build at `Builds/Win64/Nebula.exe`, the load-test project, and `dotnet`; `-DryRun` also reports whether
the dashboard and gateway ports are in use. An open Unity Editor (`Temp/UnityLockfile`) is reported as a warning,
not a blocker: the CLI mirrors the project to a scratch directory and builds there.

## D10. Adding a scenario

1. Decide the layer by D1: if the guarantee is something a gateway or the control plane decides, it is
   synthetic; if it is what a worker costs, it is tier D.
2. Synthetic: a `[Test]` in `Scale{Load,Failure,Operations}Tests.cs`, `[Category("Soak")]` too if it runs a mesh
   for seconds. Open a `ScaleReport` with its columns, `Row(...)` every measurement, `Note(...)` the conclusion,
   `Write()` before the assertions so a failing run still leaves its CSV.
3. Put the threshold in `ScaleThresholds` **and** in the table above, with its derivation or the word
   provisional.
4. Tier D: a `Invoke-<Scenario>` function in `Tools/scale-suite.ps1` and a `ValidateSet` entry.
5. `dotnet test --filter "TestCategory=Scale"` must pass, and `pwsh Tools/scale-suite.ps1 -DryRun` must still
   describe the plan correctly.

## D11. What was verified, and what was not

The synthetic layer was run in full and is green: **12 scenarios, 0 failed, 1 m 19 s**. Its numbers are in D6.

The tier-D runner was verified in `-DryRun` and `-Synthetic` modes only. **No real mesh was run**: this checkout
has no player build (`Builds/Win64/Nebula.exe` does not exist) and producing one means driving the Unity Editor
for a full player build, which is minutes of work that proves nothing about the runner's logic that a dry run
does not. The dry run correctly reports the missing build and tells the operator to use `-Build` or
`-Synthetic`. The parts of the runner that have therefore **not** executed are the mesh lifecycle, the load
client launch, the API sampling, the profile scraping and the threshold assertions; they are written against the
API field names and log format recorded in D9 and read out of the code, not guessed.

## Non-goals

Not a conformance suite: nothing here is deterministic except the two planner scenarios, and a scale run that is
slow on a loaded machine is a measurement, not a failure. Not a benchmark of Unity simulation: that is what the
tier-D layer measures when someone runs it on a machine with a build. Not a replacement for
`Tools/smoke-test.ps1`, which answers "does a real mesh work at all"; this answers "what does it do when it
breaks".
