# Worker autoscaling and spatial balancing

Goal: a developer states a minimum and maximum number of workers and the orchestrator keeps the
mesh healthy between them, adding and retiring workers and re-dealing containers as load moves.
The balancing itself is game-specific, so Nebula ships one planner driven by a small set of
per-container hints, with `IAssignmentPolicy` kept as the escape hatch.

Decisions (2026-09-15): the scaling signal is measured tick time; scale to zero is allowed with a
dashboard warning; retired VMs sit in an idle pool until their billing hour ends and are reused
before anything new is launched; the cost policy becomes the default for baked worlds too; hints
can be set at bake time (World window) and at runtime (container API).

## Why the old scaler was not enough

Where this started, before any of the phases below shipped:

| Piece | Before | Problem |
| --- | --- | --- |
| `NebulaConfig.AutoScale` + `ScaleOutCostPerWorker` / `ScaleInCostPerWorker` / `ScaleHoldSeconds` | Average abstract cost per worker against two lines; off by default | Nobody can tune numbers in weight units; the average hides a single hot worker |
| `AutoScale()` in `NebulaOrchestrator` | Independent of the assignment policy | Adds a worker even when no split would relieve the hotspot |
| `BakedAssignmentPolicy` (default for baked worlds) | Deals by count, sticky | The shooter's 500-NPC cell gets no relief |
| `CostBalancedAssignmentPolicy` | Morton order, equal-cost contiguous runs, 30 % hysteresis | Only chosen once a runtime container exists; knows nothing the game knows about its map |
| `IWorkerHost.Kill` | Deletes the VM at once | Hetzner bills the hour; a worker retired after 5 minutes wastes 55 |

## Design

### 1. Signal: worker utilization from tick time

Every heartbeat already carries `WorkerStats.TickMs` (a 5 % EMA of simulated-tick wall time).
Utilization of a worker is `TickMs / tickPeriodMs`. The orchestrator keeps, per worker, a short
window (`ScaleWindowSeconds`, default 20 s) and uses the 90th percentile so a GC spike does not
launch a machine while a sustained 80 % does.

Two lines, both fractions of the tick budget:

- `ScaleOutUtilization` (default 0.7): the busiest worker above this for `ScaleHoldSeconds` means grow.
- `ScaleInUtilization` (default 0.3): the mesh as a whole below this, and a dry run of the planner
  at N-1 keeping every worker under `ScaleOutUtilization`, means shrink.

Utilization is per container as well, so the planner can move load: a worker's tick time is
attributed to its containers proportionally to their cost-weighted occupancy (the existing
`CostWeights` and `MeshTelemetry.CopyOccupancy`). Weights therefore stop being a scaling knob and
become a distribution key. `WorkerStats` gains nothing; `WorkerInfo.TickMs` is what is read.

The old `ScaleOutCostPerWorker` / `ScaleInCostPerWorker` fields are removed (alpha, no migration).

### 2. One planner: scaling asks the policy, not an average

`IAssignmentPolicy.Compute(AssignmentInput)` already lays out containers for the eligible workers.
The scaler calls it with hypothetical worker lists:

```
plan(N)   = Policy.Compute(input with N eligible workers, dry run)
peak(N)   = max over workers of predicted utilization (sum of its containers' attributed load)
grow   if peak(N) > ScaleOutUtilization held  and peak(N+1) < peak(N) - MinGain  and N < Max
shrink if mean    < ScaleInUtilization held   and peak(N-1) < ScaleOutUtilization and N > Min
```

If `peak(N+1)` is not meaningfully lower (one container holds the load and cannot be split), the
scaler does not grow; it publishes `scale.blockedBy = "<containerId>"` and the dashboard shows
"hot container cannot be split". That is the developer's cue to add a hint or split the cell.

Which worker to retire: the one whose containers are cheapest to hand over (fewest players near
seams, lowest attributed load), computed by the same dry run. Never a worker holding a
`Dedicated` container.

`AssignmentInput` gains `Utilization` (per container, attributed) and `Hints` (below);
`IAssignmentPolicy` gains `Predict(input, workerCount)` with a default implementation that runs
`Compute` against synthetic worker ids so existing custom policies keep working.

### 3. Hints: where the per-game nuance lives

```csharp
public struct ContainerHint
{
    public float CostMultiplier;   // 1 = as measured; >1 makes the planner treat it as heavier
    public string AffinityGroup;   // containers sharing a group are kept on one worker when possible
    public float SeamCost;         // 0..1; how expensive a worker boundary through this container's neighbours is
    public bool Dedicated;         // this container gets a worker to itself (a hub, a raid)
}
```

- **Bake time**: `WorldContainerManifest.Entry` carries a `ContainerHint`; the World window edits
  it per cell (multi-select, paint). Stored in the manifest, read by `ContainerRegistry.Load`.
- **Runtime**: `Container.Hint` property and `IControlPlane.SetContainerHint(id, hint)` so the game
  (or the dashboard) changes it while the mesh runs; the row lives next to the lease so every
  orchestrator restart sees it. `RequestRuntimeContainer(id, bounds, hint)` overload for chunk worlds.
- The built-in policy honours them: `Dedicated` reserves a worker before dealing; affinity groups
  are dealt as one item; `SeamCost` is added to a run's cost when the cut falls between two
  neighbours that both carry it; the cost multiplier scales attributed utilization.
- Games with needs beyond this implement `IAssignmentPolicy` as today.

Worked examples: a city hub cell marked `Dedicated`; a battle royale raising `SeamCost` on the
cells inside the circle each phase; an instanced dungeon whose rooms share an `AffinityGroup`;
Holospace leaving everything default.

### 4. Cost policy by default, for baked worlds too

`AssignmentPolicy = "auto"` now means the cost policy always, and `"baked"` is an explicit opt-out
for games that want count-based sticky dealing. The rebalance threshold (30 %) already limits
churn; the planner additionally refuses a re-deal whose predicted gain is below `MinGain` (10 %
of budget) and never moves a container with a player within `SeamGraceMeters` of the moving seam
in the same pass (it waits). Handover during play is accepted as the price; the overlay shows it.

### 5. Scale to zero and the cold-start warning

`MinWorkers = 0` is legal. With zero workers every lease is released and the gateway holds joining
clients in a "world starting" state (a new `JoinState.Starting` in the join reply) instead of
rejecting them; the first join sets `DesiredWorkers = max(1, Min)`. The dashboard shows a
persistent warning pill when `MinWorkers == 0`: "Scale to zero is on: the first player after an
idle period waits for a worker to boot (about N s on this host)". `IWorkerHost.TypicalBootSeconds`
supplies N (process host ~3 s, Hetzner ~60-90 s measured).

### 6. Idle pool: retire into the pool, launch from the pool

`IWorkerHost` gains `Park(handle)` and `Unpark(handle)`. A retired worker (drained, no leases) is
parked instead of killed. `ProcessWorkerHost` kills on park (a process costs nothing to restart).
`HetznerWorkerHost` keeps the VM, records `BilledUntil = launchedAt + ceil(uptime / 1 h) * 1 h`,
and deletes it in `Tick` once that passes with no unpark. A scale-out first unparks, so a worker
that was retired minutes ago comes back in seconds with its image warm. The dashboard lists parked
workers with their remaining paid time. A parked worker keeps heartbeating but is `Status =
"parked"` and never eligible for leases.

Config: `IdlePoolSeconds` (default 0 = host default: Hetzner uses billing granularity, process 0).

### 7. Config surface (after)

```
MinWorkers = 1, MaxWorkers = 32           // WorkerCount stays as the initial count when AutoScale is off
AutoScale = true                          // default on
ScaleOutUtilization = 0.7, ScaleInUtilization = 0.3
ScaleHoldSeconds = 30, ScaleWindowSeconds = 20
IdlePoolSeconds = 0 (host default)
AssignmentPolicy = auto | baked | cost    // auto = cost
CostRebalanceThreshold, CostWeights      // unchanged
CostLinkBudgetMbps = 100                 // added 2026-09-21; yardstick only, see docs/cost-telemetry.md
```

`nebula start --workers N` keeps meaning a fixed count (sets Min = Max = N); `--min/--max` added.
`nebula.json` mirrors the fields. Dashboard: utilization bar per worker, the scaling decision
line ("holding: peak 0.74 for 12/30 s"), blocked-by reason, parked pool, scale-to-zero warning.

## Phases (all six shipped 2026-09-15)

1. **Signal and dashboard** (no behaviour change): utilization window per worker, per-container
   attribution, `AssignmentInput.Utilization`, dashboard bars. Tests: attribution maths, percentile window.
2. **Planner-driven scaler**: replace `AutoScale()` with the dry-run version, `Predict`, retire
   choice, blocked-by reporting; remove the cost lines; cost policy default for baked worlds.
   Tests: `AssignmentPolicyTests` gains predict cases; a pure `ScalerTests` over synthetic histories.
3. **Hints**: struct, manifest field, World window editing, control-plane row, runtime API, policy
   support. Tests per hint kind.
4. **Idle pool**: `Park`/`Unpark` on the hosts, Hetzner billing clock, dashboard pool. Verified by
   a Hetzner trial: retire, see the VM survive, scale out, see it return without a create call.
5. **Scale to zero**: `JoinState.Starting`, gateway hold, warning pill, `TypicalBootSeconds`.
6. **Docs**: `guides/orchestrator-and-dashboard.mdx` and `guides/runtime-containers.mdx` rewritten
   around min/max; CLI reference regenerated; this file gets a "what shipped" table.

Exit test: shooter with 500 NPCs and `nebula start --min 1 --max 4`: starts at 1, grows until the
hot cell is dealt out, holds; kill the bots and it shrinks back to 1 with the spare VM parked and
then deleted at the hour. Holospace sample with `--min 0 --max 3`: idle mesh has no workers, the
first bot join boots one.

### Exit test run (2026-09-15, shooter, process host, post-fix)

Re-run after the scaler fixes (cold workers excluded from the shrink branch until `MinSamples`
heartbeats, the re-deal branch taken only when the policy would actually move something,
`MinGainFloor`, and the `IsInFlight` settle check). Library rebuilt into the shooter first
(`nebula build` in `nebula-shootergame`, the `file:` reference embeds it), then
`nebula start --min 1 --max 4 --npcs 500 --yes`, polling `/api/state` every 10 s:

| Time | Workers | Busiest | Mean | Decision |
| --- | --- | --- | --- | --- |
| 10:23:04 | 1 (84 containers) | 0.97 | 0.97 | `holding: peak 0.97 for 3/30 s` |
| 10:23:14-10:23:24 | 1 | 0.90 -> 0.80 | same | `holding: peak 0.80 for 22/30 s` |
| 10:23:32 | 1 -> 2 | 0.78 | 0.78 | `growing to 2: peak 0.78 over 0.70 for 30 s, predicted peak 0.77 -> 0.39` |
| 10:23:34-10:23:44 | 2 | 0.78 | 0.51 -> 0.72 | `holding` while w2's window fills; the cold worker is kept out of the peak |
| 10:23:54-10:28:55 | 2 (26/58 containers) | 0.44-0.66 | 0.37-0.63 | `steady` for 5 min, no flapping, `blockedBy` empty throughout |
| 10:28:45 | 2 | - | - | the `npcs` setting lowered to 50 through `POST /api/settings` |
| 10:29:05-10:29:25 | 2 | 0.12 | 0.09 | `holding: mean 0.09 for 20/30 s` |
| 10:29:34 | 2 -> 1 | 0.12 | 0.09 | `shrinking to 1: mean 0.09 under 0.30 for 30 s, w2 is the cheapest to hand over` |
| 10:29:35 | 1 (84 containers) | 0.12 | 0.12 | `waiting for the mesh to settle` - one poll only, cleared by the next |
| 10:29:45-10:33:05 | 1 (84 containers) | 0.13-0.17 | same | `steady` at the floor for 3.5 min |

No regression against the previous run, and two things improved:

- **The heartbeat relaunch loop is gone.** The earlier run declared the single cold worker dead four
  times in a row (`missed heartbeats for 5.0s`) while it loaded 77 cell scenes and spawned 500 NPCs.
  This run's orchestrator log contains no `missed heartbeats`, `dead` or `relaunch` line at all.
- **One grow instead of two, and no shrink right after it.** The previous run went 1 -> 2 -> 3 and
  then shrank 3 -> 2 within a minute, because a worker launched seconds earlier read as 0 % busy and
  dragged the mean under `ScaleInUtilization`. With cold workers excluded until their window fills,
  the mesh settled at 2 and stayed there.

`waiting for the mesh to settle` appeared exactly once, on the poll during the handover, and cleared
on the next one; no `not shrinking yet` line appeared, `scale.blockedBy` was empty for the whole run,
and the orchestrator log carried no errors. The whole autoscale log for the run is two lines, the
grow and the shrink. The process host kills a retired worker rather than parking it
(`SupportsParking = false`), so `parked` stayed 0 by design - the parked half of the exit test still
needs the Hetzner trial. `nebula stop` left no Nebula process behind.

## Open points

- Attributing tick time by occupancy is a proxy; a container full of static props costs less than
  one full of NPCs with paths. `CostMultiplier` is the manual correction; a per-container timer on
  the worker is the follow-up if the proxy proves too coarse.
- Predict runs the policy several times per pass. Fine at hundreds of containers; revisit if
  Holospace reaches tens of thousands of live chunks.
- Mixed host sizes (a big VM for the hub) are not modelled; every worker has the same budget.

## What shipped (2026-09-15)

| Piece | Before | Now |
| --- | --- | --- |
| Scaling signal | Average abstract cost per worker against `ScaleOutCostPerWorker` / `ScaleInCostPerWorker`, off by default | Per-worker utilization `TickMs / 16.7 ms`, a `ScaleWindowSeconds` rolling window, 90th percentile. `WorkerLoadTracker` takes an injected clock, so it is testable without Unity |
| Per-container load | Nothing | `WorkerLoadTracker.Attribute` splits a worker's tick time across its containers by cost-weighted occupancy (`CostWeights`, `MeshTelemetry.CopyOccupancy`); `AssignmentInput.Utilization` carries it to the policy. Weights stopped being a scaling knob and became a distribution key |
| Scaling decision | `AutoScale()` added a worker on an average, independent of the policy | `WorkerScaler` dry-runs the policy at N-1 / N / N+1 (`AssignmentPlanner.Predict`, dealing to `sim-0..sim-n`; an extension method, because Unity's Mono has no default interface methods), grows only when the predicted peak drops by `ScaleMinGain`, shrinks only when the survivors stay under `ScaleOutUtilization`, and makes one change per hold. `NebulaOrchestrator.AutoScale` just wires it |
| Blocked growth | Grew anyway | `scale.blockedBy = "<containerId>"` and "hot container cannot be split" on the dashboard when no split would help |
| When the dry run cannot be believed | - | Two cases where the plan says nothing and the measurement decides instead, both reported in the reason line: a plan with `Unassigned > 0` (the `baked` policy places runtime containers from the lease rows, and a dry run has none, so an all-runtime world predicted a peak of 0 for ever), and a "re-deal would fix it" verdict that the policy's real `Compute` would not actually act on (its balance rule is satisfied in cost units, so the re-deal never comes). Both grow on the measured peak once the hold is up |
| Re-dealing a quiet mesh | The `ScaleMinGain` veto compared predicted utilization whenever any was reported | The veto only applies above `CostBalancedAssignmentPolicy.MinGainFloor` (0.35 of a tick). Below it no re-deal can show a gain of `ScaleMinGain` however lopsided the mesh is, so the cost-unit balance rule decides and a 7:1 split is dealt out |
| The settle check | `_retiring.Count > 0 \|\| _managed.Count(m => !m.Retiring) != DesiredWorkers` | `WorkerScaler.IsInFlight`, a pure function counting `!Retiring && !Parked` like the reconciler does. `Park` clears `Retiring`, so the old count left a parking host "waiting for the mesh to settle" for ever after the first shrink |
| Retire choice | The last worker | The worker cheapest to hand over, from the same dry run; `WorkerScaler.CanRetire` (wired to `NebulaOrchestrator.MayRetire`) never retires a worker holding a `Dedicated` container, and `WorkerScaler.IsWarm` (wired to `WorkerLoadTracker.IsWarm`, `MinSamples` heartbeats inside the window) keeps a worker whose window is still filling out of the peak, the mean and the retiree choice - a machine launched seconds ago reads as 0 % busy and used to be the first one retired again. A cold worker also holds the shrink branch until it has reported |
| Default policy | `BakedAssignmentPolicy` for baked worlds: count-based, sticky | `AssignmentPolicy = auto` means the cost policy everywhere; it cuts the Morton curve from scratch when nobody owns anything and refuses a re-deal below `ScaleMinGain`. `baked` is the explicit opt-out |
| Per-game nuance | `IAssignmentPolicy` or nothing | `ContainerHint` (`CostMultiplier`, `AffinityGroup`, `SeamCost`, `Dedicated`) with a compact string form, set at bake time (`WorldContainerManifest.Entry.Hint`, ctrl-click multi-select in the World window) and at runtime (`Container.Hint`, `IControlPlane.SetContainerHint`, `RequestRuntimeContainer(id, bounds, hint)`, `POST /api/containers/hint`). The runtime value wins; the row lives beside the lease so a restart sees it. `RequestRuntimeContainer(id, bounds)` names no hint and therefore never writes one, so the call made on every approach cannot erase a hint set from the dashboard; only the three-argument overload writes (`NebulaWorker.ShouldWriteHint` is the rule) |
| Retiring a worker | `IWorkerHost.Kill` deleted the VM at once, wasting the billed hour | `Park` / `Unpark` / `SupportsParking` / `TypicalBootSeconds`, and a `WorkerHostBase` whose `Park` kills so a host written before parking keeps working. Hetzner keeps the VM until two minutes before the paid hour ends and a scale-out unparks the most recently parked worker first; the process host still kills, since a process costs nothing to restart. The two-minute margin is taken off the billed deadline only, so an `IdlePoolSeconds` shorter than it (60 s, say) is honoured in full rather than meaning "delete on the next tick". Resuming a parked worker from the dashboard checks the ceiling first and answers 409 like `POST /api/workers/add`, instead of raising a desired count that is clamped straight back and retiring the worker it had just resumed |
| An empty mesh | Workers ran regardless; a client with nowhere to spawn was left silent | `MinWorkers = 0` is legal. `JoinState` / `JoinStatusMsg` (protocol 9 -> 10) hold the client in "world starting, about N s" instead, the gateway counts them (`HeartbeatGateway(gatewayId, pendingJoins)`), and `WakeForDemand` sets `DesiredWorkers = max(1, MinWorkers)` at once, bypassing the hold. `WorkerScaler.FloorWorkers` is the shared rule, so a shrink cannot race the wake-up |
| Config | `AutoScale` off, two cost lines | `AutoScale` on, plus `MinWorkers`, `MaxWorkers`, `ScaleOutUtilization`, `ScaleInUtilization`, `ScaleHoldSeconds`, `ScaleWindowSeconds`, `ScaleMinGain`, `SeamGraceMeters`, `IdlePoolSeconds`; `-nebula-min-workers`, `-nebula-max-workers`, `-nebula-autoscale`, `-nebula-idle-pool`; `mesh.minWorkers` / `mesh.maxWorkers` / `mesh.idlePoolSeconds` in `nebula.json`. The cost lines are gone (alpha, no migration) |
| CLI | `--workers N` only | `--workers N` still fixes the count (min = max = N); `--min` / `--max` open the band and the mesh starts at the floor. `nebula status` warns about a pending join and about scale to zero |
| Dashboard | Worker cards and a desired count | A utilization bar and figure per worker, the decision line ("holding: peak 0.74 for 12/30 s"), blocked-by, an editable min/max band (`POST /api/scale/limits`), an idle-pool card with Resume / Delete, a scale-to-zero warning pill, a Joining column, per-container hints edited in place, and a `scale` object in `/api/state` |
| Tests | 209 | 219 EditMode and 48 service tests: `WorkerLoadTrackerTests`, `WorkerScalerTests` (including the settle check with a parked worker, the two unbelievable dry runs and the cold-window rule), `AssignmentPolicyTests` (predict, the `ScaleMinGain` gate and its floor), `ContainerHintTests` (including the no-hint overload), `IdlePoolTests` (including an idle pool shorter than the billing margin), `ScaleToZeroTests`, plus service tests for the hint round trip, the 409 on resuming at the ceiling, and a real-UDP `GatewayHoldsTheJoinWhenNoWorkerIsRunning` |
| Docs | - | `guides/configuration.mdx` gains an Autoscaling table with every field, its switch and its `nebula.json` key; `orchestrator-and-dashboard.mdx`, `runtime-containers.mdx`, `world-partition.mdx`, `docs/cli.md`, and the generated CLI and API reference |

## Addendum (2026-09-21): what a hot container is hot in

`CostWeights` is per category, and the per-container report was a head count, so "cell_3_0_1 cannot
be split" said nothing about *why*. Per-entity cost hints and per-container cost telemetry
(`docs/cost-telemetry.md`) change three things here:

- **The weights are per entity.** `NetworkIdentity.CostWeight` multiplies the category weight and
  `NebulaCost.EntityWeight` can decide it at spawn. The worker tallies the weighted sum per
  container and reports it; `CostWeights.Of` prefers that sum over counting heads, so the cost
  policy, `WorkerLoadTracker.Attribute` and the scaler's dry runs all see it. A world that sets no
  weights gets exactly the numbers it got before.
- **Simulation time is measured.** The worker times each entity's `NetworkTick` and charges it to
  the entity's container, so a container of static props and a container of pathing NPCs no longer
  cost the same merely for holding the same number of entities. The rest of the tick belongs to no
  container and is left out, which is why the rows add up to less than a worker's utilization.
- **The blocked reason names the component.** `ScaleDecision.BlockedComponent` is `simulation`,
  `replication` or `gateway`, from the container's cost row, and the reason string says which:
  three different fixes, one sentence apart.

This closes the first follow-up below in part: occupancy reports still carry no positions, so seam
grace is still unenforced, but they no longer carry only counts.

## Addendum (2026-09-22): explained moves and saturation (NEB-235)

Design of record: `docs/cohesion-rebalancing.md`. Three things changed around the planner; the cut
itself, `MinGain`, `MinGainFloor` and `Threshold` are untouched.

- **Every move is explained.** The cost policy emits an `AssignmentMove` (container, from, to,
  reason) beside every change, offered through the second interface `IExplainsAssignment` so a
  game's own `IAssignmentPolicy` keeps compiling. The reason names the boundary the cut fell on, the
  before/after peak, and the group that kept containers together. The orchestrator logs it in
  brackets after `assign c -> w`, publishes it under `assignment.moves` and shows it on the
  dashboard's **Assignment plan** card.
- **Saturation is typed and reported.** When the busiest worker is at or above
  `CostBalancedAssignmentPolicy.SaturationUtilization` (0.7), nothing moved off it, and its heaviest
  item is itself that hot, the pass emits a `SaturationReport`: the container, its scope key, the
  item, the utilization and a `SaturationCause` — `CohesionGroup`, `AffinityGroup`, `Held`,
  `Dedicated` or `NoBoundary`. The scaler appends its sentence to the blocked reason and records
  `ScaleDecision.BlockedCause` / `BlockedReason` beside the unchanged `BlockedComponent`. This
  closes the last follow-up below for cohesion and affinity groups: an oversize group is no longer
  silent.
- **Dry runs carry the constraints.** `AssignmentPlanner.Predict` built its hypothetical input from a
  subset of fields that left out `Cohesion`, so a dry run could split a group the real deal may not,
  predict a relief that never arrives, and grow the mesh for nothing. It now carries `Cohesion`,
  `Cost` and `Holds`, and `AssignmentPlan.Moves` carries the dry run's explanations.

## Follow-ups

- **Seam grace is configured, not enforced.** `SeamGraceMeters` needs a per-container distance
  figure from the worker; occupancy reports carry counts, not positions. Tracked as a TODO on the field.
- **Seam cost is a cut charge, not a grace period.** It steers where a run boundary falls; it does
  not delay a handover already in flight.
- **Nothing drives park, unpark and the reconciler end to end.** The pieces are pure functions and are tested as
  such (`IsInFlight`, `PickWorkerToUnpark`, `NextFreeIndex`, `EligibleWorkers`, the billing clock, the host contract
  against a fake parking host), but no test runs a `NebulaOrchestrator` over a parking host and watches a worker go
  into the pool and come back. The worker host is picked from `NebulaConfig.WorkerHost` inside `Initialize`, so that
  needs an injection point for the host first.
- **A Hetzner trial for the idle pool.** The billing clock and the rules are unit-tested, but
  nothing has talked to the Hetzner API: retire a worker and see the VM survive, scale out and see
  it return with no create call, leave it and see it deleted a couple of minutes before the hour,
  kill the orchestrator while a worker is parked and see the next run's sweep delete it.
- **A parked worker is told nothing.** It holds no leases and keeps heartbeating, and the
  orchestrator labels it `parked`. A worker that wants to shed memory while parked needs a
  control-plane flag, which nothing asks for yet.
- **Demand is counted, not identified.** The orchestrator wakes on "somebody is waiting", not on
  where they want to spawn, so the first worker gets whatever the policy deals it and the player may
  then be handed over. A join that named a container would let the wake pick the lease to activate first.
- **No wake without a gateway**, because the signal rides the gateway heartbeat. A `POST /api/wake`
  for a matchmaker or a queue is the obvious addition.
- **The boot estimate is the host's constant**, not a measurement of the launch actually happening.
- **A cold worker can miss its heartbeats.** Loading 77 cell scenes and spawning 500 NPCs blows
  through `WorkerTimeoutSeconds` (5 s) and the orchestrator relaunches the worker, repeatedly, until
  the load is spread over more workers. Either the load needs to yield or a starting worker needs a
  longer grace.
- **Dedicated is per worker, not per size.** An `AffinityGroup` larger than one worker's budget is
  still dealt to one worker and simply runs hot - it is now reported (`SaturationReport`, the
  addendum above) rather than silent, but nothing makes it smaller. Mixed host sizes are unmodelled.
