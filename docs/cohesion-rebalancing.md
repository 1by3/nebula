# Cohesion-aware rebalancing along game-defined boundaries — design

Status: landed with NEB-235 (project "Scoped worlds and interaction contracts"). Conformance scenario 13 of
`docs/conformance-suite.md`. Builds on `docs/cohesion-hints.md` (NEB-223) and `docs/cost-telemetry.md` (NEB-225).
User pages: `website/content/docs/guides/cohesion.mdx`, `website/content/docs/guides/orchestrator-and-dashboard.mdx`.

## 0. The problem

A faction war draws two hundred players into one city. The city was authored as districts with an `x2` cost
multiplier and `seam` hints, and somewhere in it a docked fleet is one cohesion group. Interest management does
nothing for this: culling bounds who *hears* about an entity, not what it costs to simulate it. Only re-dealing
authority does — and a re-deal that separates the fleet, or that hands a district over in the middle of the fight
happening inside it, is worse than the overload it was trying to fix.

NEB-223 taught the planner to *respect* those constraints (a group's containers are one item; a held item does not
move). What was missing is the other half: cutting along the boundaries the game authored, saying why each cut fell
where it did, and saying so plainly when there is no cut left to make. Without the last part the operator watches a
worker sit at 95 % while the dashboard says "steady".

## 1. Decisions

**D1. A "boundary" is a container edge the game already authored; Nebula never subdivides a container.** This is
the model that was already there, and it is worth writing down because "split the hot container" reads as if
Nebula could cut one in half. It cannot, and it will not learn to:

| Thing | What it is today | Where |
|---|---|---|
| a district | one `Container` — a baked cell, a scene container, a runtime container | `Runtime/Containers/Container.cs` |
| "cutting here is expensive" | `ContainerHint.SeamCost` (`seam=0.5`), charged against the run target where two neighbours on the Morton curve both carry it | `ContainerHint.cs`, `CostBalancedAssignmentPolicy.Compute` |
| "this is heavier than it looks" | `ContainerHint.CostMultiplier` (`x2`) | `ContainerHint.cs` |
| "deal these together" | `ContainerHint.AffinityGroup` (`group=hub`) | folded by `MergeGroups` |
| "these entities may not be separated" | a cohesion group reported by the workers | `AssignmentInput.Cohesion` |
| "do not move this yet" | a hold, in seconds still to run | `AssignmentInput.Holds` |

So "splitting the city" means **assigning its districts to different workers along a boundary between two of them**,
never sharding a district. A world whose hot area is a single container has authored no boundary there, and the
honest answer is to say so (D5) rather than to shuffle containers pointlessly. The alternative the game refuses —
copying the city and splitting the two sides of the war between the copies — is not something the planner may
choose on its own; that is a scope the game asks for (`docs/scope-activation.md`).

**D2. The re-deal is the existing cut, not a new algorithm.** `CostBalancedAssignmentPolicy` already orders
containers along a Morton curve, folds every binding into one item with a union-find, and cuts the curve into one
contiguous run of roughly equal cost per worker, charging itself `seam × target` for cutting between two
neighbours that both said a seam there is expensive. That *is* "re-deal along the hinted boundaries", and a second
mechanism beside it would have been a second thing to keep honest. What NEB-235 adds is the accounting around it:
every move carries the sentence that explains it (D3), and the pass says why it could not relieve the busiest
worker when it did not (D5). `MinGain`, `MinGainFloor` and `Threshold` are unchanged: a move still has to buy a
tenth of a tick on a worker that is already at `MinGainFloor` before it is made.

**D3. Every move carries an explanation, and explaining is a second interface.** `AssignmentMove` (container, from,
to, reason) is produced next to every change the policy emits, and `IExplainsAssignment` is what a policy
implements to offer them. It is a separate interface rather than a member of `IAssignmentPolicy` for the same
reason `Predict` is an extension method: Unity's Mono has no default interface methods, and a game's own policy
must keep compiling untouched. The orchestrator asks `policy is IExplainsAssignment` and falls back to the bare
`assign c -> w` line it always logged.

The sentence names the three things an operator asks: which boundary the cut fell on ("the boundary between c1 and
c2"), what the numbers were ("4 item(s), 20 of a 15 target; busiest worker 0.81 -> 0.44 of a tick"), and what
constrained it ("cohesion 7 spans c1, c2 and is never cut, so they move together"). A group moves as one item, so
every container of it carries the same sentence and the dashboard reads them as one decision. Moves the
orchestrator then drops because their container is held (`AssignmentInput.DropHeldChanges`) are dropped from the
published list too, so the dashboard never claims a move that did not happen.

**D4. The constraints travel into the dry run.** `AssignmentPlanner.Predict` built its hypothetical
`AssignmentInput` from a hand-written subset of fields, which did not include `Cohesion`. A dry run was therefore
free to split a group the real deal may not split, so `peak(N+1)` could promise a relief that never arrives and the
mesh would grow for nothing — exactly the pointless shuffling this item exists to stop. `Predict` now carries
`Cohesion`, `Cost` and `Holds` across. Holds change nothing there (a dry run has no leases, so no item is held) and
are carried only so the subset stops being a subset. `AssignmentPlan.Moves` carries the explanations of the dry run
as well, so a predicted layout is readable rather than a bare list of ids.

**D5. Saturation is a typed report, and it is a statement, never an action.** `SaturationReport`
(`Runtime/Orchestrator/AssignmentReport.cs`) is the row: the heaviest container of the item, its scope key, every
container of the item, the worker carrying it, its utilization, a `SaturationCause` and the sentence. The scope key
is on it so admission reporting can group the rows per scope without another table, which is the same reason
`ContainerCost` carries one (`docs/cost-telemetry.md`, D9).

A pass emits a row when:

* an item does not fit one worker at all — the `UnsplittableGroup` case of `docs/cohesion-hints.md` D9, which is
  saturated whoever holds it; or
* the busiest worker is at or above `SaturationUtilization` (0.7, the scale-out line), **nothing moved off it this
  pass**, and its heaviest single item is itself at or above that line. The last clause is what keeps the report
  honest: if the heaviest item is small, the worker is merely unbalanced and a later pass will fix it, which is not
  saturation.

The cause is read off the item that will not move, in this order — a group spanning it (`CohesionGroup` /
`AffinityGroup`, "cohesion 7 spans c0, c1"), a reservation (`Dedicated`), a live hold (`Held`, "held for another
4.2 s"), and otherwise `NoBoundary` ("no boundary hint: c0 carries 0.90 of a tick on its own and the game authored
no boundary inside it"). Group first because it is the permanent reason; a hold expires by itself.

Nothing is done about any of it. Splitting the group is what the hint forbids, moving the held container is what
the hold forbids, and subdividing a container is D1. The report is the whole of the answer.

**D6. The scaler names the constraint next to the component.** `WorkerScaler` already said *what* a blocking
container is expensive in (`docs/cost-telemetry.md`, D8). It now also says *why it may not be split*, appending the
saturation sentence and recording `ScaleDecision.BlockedCause` / `BlockedReason` beside the untouched
`BlockedComponent` / `BlockedSaturation`:

```
blocked: c0 carries 0.95 of a tick on its own and cannot be split
  (mostly simulation: 15.80 ms/tick, 0.95 of the tick budget)
  - cohesion 7 spans c0, c1, so cutting between them would split it; the item needs 0.95 of a tick and is kept
  whole; add a hint or split the cell
```

The planner reports against the mesh as it is, not against a dry run, so the blocked branch deals the real input
once (a pure function) and reads the rows back. That happens only on a pass that is already blocked. A policy that
does not implement `IExplainsAssignment` leaves the sentence exactly as it was.

**D7. The dashboard gets an Assignment plan card.** `/api/state` gains an `assignment` block — `policy`,
`rebalances`, `moves` (container, from, to, reason) and `saturated` (container, scope, worker, utilization, cause,
reason) — and the card renders the moves as a table with the saturation rows as a warning above it. It hides itself
when nothing has moved and nothing is saturated, so a mesh that never rebalances looks exactly as it did. The
orchestrator's own `assign c -> w` log lines carry the reason in brackets, and `LastMoves` / `Saturated` expose the
same rows to game code and to the Cloud dashboard.

## 2. What this does not do

* It does not subdivide a container, shard a physics scene across machines, or copy a world to spread players over
  the copies. D1.
* It does not detect boundaries. A seam, an affinity group and a cost multiplier are authored; a cohesion group is
  asserted by the game. Nebula never guesses one.
* It does not break a group or a hold to relieve a worker, not even when the worker is over its tick budget. It
  reports, and the operator decides.
* It does not move a container closer to "the middle of the fight" than the planner already does: the seam grace
  the policy has a TODO for still needs a per-container "nearest player to each face" figure from the worker, which
  the occupancy report does not carry. A hold is the mechanism a game has for that today.

## 3. Where it lives

| Piece | File |
|---|---|
| `AssignmentMove`, `SaturationReport`, `SaturationCause`, `IExplainsAssignment` | `Runtime/Orchestrator/AssignmentReport.cs` |
| The explained moves and the saturation pass | `Runtime/Orchestrator/AssignmentPolicy.cs` (`Deal`, `Move`, `ReportSaturation`) |
| Constraints in the dry run, `AssignmentPlan.Moves` | `Runtime/Orchestrator/AssignmentPlanner.cs` |
| The blocked reason | `Runtime/Orchestrator/WorkerScaler.cs` (`DescribeSaturation`) |
| The `assignment` block, the log lines | `Runtime/Orchestrator/NebulaOrchestrator.cs` (`WriteAssignmentState`, `Rebalance`) |
| The Assignment plan card | `Runtime/Orchestrator/Resources/NebulaDashboard.html` |
| Conformance (tier A, both builds) | `Tests/EditMode/ConformanceRebalanceTests.cs` |
