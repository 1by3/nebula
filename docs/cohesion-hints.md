# Cohesion hints — design

Status: landed with NEB-223 (project "Scoped worlds and interaction contracts"). Conformance scenario 7 of
`docs/conformance-suite.md`. User page: `website/content/docs/guides/cohesion.mdx`.

## 0. The problem

Assignment and handover are Nebula's: the orchestrator decides which worker leases which container, and the worker
hands an entity over when it crosses into a container somebody else leases. Only the game knows which entities may
not be separated while that happens — the bodies of one physics island, a player and the crate they are carrying
mid-throw, the two sides of an interaction that is half applied. Containers already have `AffinityGroup` ("deal
these together") and `Dedicated` ("a worker of its own"); entities had nothing, and neither had "leave this alone
for the next ten seconds".

Two hints close that gap.

* An **entity cohesion group**: a number on `NetworkIdentity`. Every member is simulated by one worker, moves with
  the others in a handover, and their containers are dealt as one item.
* A **container hold**: "do not rebalance this container for N seconds", asked for by the worker that is running
  the thing that must not be interrupted.

Both are hints the game asserts, not facts Nebula discovers. Detecting islands automatically, guaranteeing a group
fits a worker, and constraints between anything outside a group are out of scope (and scenario NEB-235 —
rebalancing that cuts along these constraints rather than merely respecting them — is a later issue).

## 1. Decisions

**D1. A cohesion group is a number the game chooses, never inferred.** `NetworkIdentity.CohesionGroup` is a `uint`,
0 meaning "no group". It is authored in the inspector (a private `[SerializeField]`, so a prefab can carry one) or
set at runtime with `identity.JoinCohesionGroup(group)` / `LeaveCohesionGroup()`. Nebula never writes it and never
guesses it: it has no way to tell a constraint that matters from one that does not, and a wrong guess would pin
containers together for ever. The ids are the game's own namespace; `CohesionGroups` (`Runtime/Core`) is the
process-wide table of who is in which group, maintained by `Initialize`/`OnDestroy` and by the join and leave calls.

**D2. A group binds entities, not containers.** Members may sit in different containers, in the same one, or in
none. What follows from membership is derived: the containers a group's members occupy are what the planner keeps
together (D7), and the handover expansion is per entity (D4).

**D3. The group travels in the spawn data, one field, protocol 18.** `EntitySpawnMsg.CohesionGroup` is written last
in the body and read back by `ApplySpawnData`, so a spawn, a ghost spawn and an `AuthorityTransferMsg` (whose
payload is an `EntitySpawnMsg`) all carry it. No separate message and no handover-state blob: the group is a fact
about the entity, exactly like its interest group, and every holder learns it wherever the entity turns up. The
cost is four bytes per spawn. The consequence to document: a `JoinCohesionGroup` on an entity that is already
spawned reaches the other holders with the *next* spawn, ghost spawn or handover of that entity, not immediately.
The authoritative worker's value is the one that decides a handover, so that is enough for the guarantee — but a
game that joins a group in the middle of an interaction should join it on every copy (game code runs on all of
them), which is what the conformance tests do.

**D4. A handover of any member carries every member the sender owns.** `NebulaWorker.TransferAuthority` expands the
group before anything moves: the members this worker owns are queued, and drained after the entity that was asked
for has gone, each as a top-level handoff of its own. "Top-level" matters — a member that is itself a carrier has
to open its own `HandoverScope` so its passengers are collected and not republished as orphans (design D85/D87 of
the interest work). A group is expanded once per handoff however many members move (`_cohesionExpanded`), and a
member that belongs to a second group expands that one in turn, because the queue is walked while it grows.

This mirrors, rather than reuses, the carried-subtree mechanism. A carried subtree is a *containment* relation:
the passengers are inside the carrier's box, they move with it in the same scope, and their placement must not be
published in between. A cohesion group is a *co-location* relation between peers in unrelated containers: each
member is an ordinary handover with its own epoch bump, its own interest removal and its own redirect. Folding the
two into one mechanism would have meant teaching `HandoverScope` about a relation it has no business knowing.

**D5. A member that cannot come along is reported, never a silent split.** A copy the sender holds that it does not
own — a ghost of a member another worker is authoritative for — cannot be included in the transfer. That is a
failure of the cohesion promise: it is counted in `NebulaDiagnostics.SplitCohesionGroups` and logged once per
handover, naming the group and a member. A ghost whose owner is the worker the entity is *going to* is not a split:
the group is meeting up, which is the normal end of a group that was briefly apart.

The transfer itself still goes ahead. Refusing it would strand the entity on a worker that no longer leases its
container, which is worse than a split the next pass repairs: the planner puts the group's containers back on one
worker (D7), and the members follow the containers.

**D6. `PhysicsIslands.IsCohesive` is satisfied by group membership.** Two entities in the same non-zero group are
one island, so `Nebula > Validate Project` and the worker's authority check no longer warn about a joint between
them (`CohesionGroups.Same`). The `IsCohesive` delegate stays as the escape hatch for a game whose guarantee comes
from somewhere else. This is the whole of NEB-234's hook.

**D7. The planner deals a group's containers as one item.** Workers report, in their telemetry document, one row
per cohesion group they own members of: the group id, how many members, and which of their containers those
members are in (under `"in"`, not `"containers"`: the occupancy reader finds the per-container counts by scanning
for the document's first `"containers"` key, so no block written before it may carry one). The orchestrator unions the rows of every live worker into `CohesionGroupInfo` and hands them to
the policy as `AssignmentInput.Cohesion`. `CostBalancedAssignmentPolicy` folds the containers of every binding —
affinity groups from the hints and cohesion groups from the telemetry — into one item with a union-find, so a
container named by both ends up in one item with everything either of them names. From there the existing
machinery applies unchanged: summed cost, the lowest member's Morton key, "owned" only when every member is on one
worker, and one entry in the change list per container.

A group whose members are all in one container binds nothing: it is already on one worker.

**D8. A hold is reported as seconds remaining and expires on the orchestrator's clock.** `NebulaWorker.HoldContainer(container, seconds)` records a deadline on the worker's own
`Time.unscaledTime`, clamped to `MaxHoldSeconds` (120); repeating the call extends but never shortens it, and zero
seconds releases it. What goes on the wire is the *remaining seconds*, in the telemetry document the worker already
posts, and `MeshTelemetry.AcceptCohesion` turns that into `receivedAt + remaining` on the orchestrator's clock.

The clock assumption is therefore only that each process's monotonic clock runs at roughly one second per second:
no wall-clock synchronisation, no NTP, no skew handling. What a hold really costs is one telemetry hop of latency
(at most `WorkerTelemetry.IdleIntervalSeconds`, one second, plus the post), which shortens the hold as the
orchestrator sees it by that much — immaterial for a hint measured in seconds, and always in the safe direction for
the *start* of the hold, because the planner only learns about a hold after the worker asked for it. A hold also
dies with its worker: the rows are keyed by worker id and dropped when it stops reporting or is forgotten, so a
crashed worker cannot pin a container.

The planner skips a move for a held container it already owns; an orphan is placed whatever its hold says, since a
hold defers a move and cannot leave a container unowned. Holding one member of an item holds the whole item —
honouring the hold on one container while its group moved would be exactly the split the group exists to prevent.
`AssignmentInput.DropHeldChanges` runs on the orchestrator after `Compute` as the backstop for a policy the game
wrote itself.

**D9. A group that does not fit one worker is reported, not split.** After merging, the policy adds up the measured
utilization of an item's containers. Above `MaxGroupUtilization` (1, a whole tick budget) it emits an
`UnsplittableGroup` row — which group, which containers, what it costs, what the limit was — into
`CostBalancedAssignmentPolicy.Unsplittable` and into the `Note` the orchestrator logs and the dashboard shows. The
group is still dealt whole: splitting it is precisely what the hint forbids, and a planner that silently ignored a
hint it could not honour would be worse than an overloaded worker the operator can see. Measured utilization is the
yardstick, so a mesh that has not reported any (a fresh start, a dry run) reports nothing rather than guessing from
cost units, which have no per-worker budget to be a fraction of.

**D10. The dashboard shows holds, groups and what did not fit.** The state document carries a `cohesion` object:
`holds` (container, worker, seconds left), `groups` (id, members, containers, workers, and whether it is on more
than one worker right now) and `unsplittable` (the D9 rows). The Cohesion card renders them and hides itself when
there is nothing to say.

## 2. What this does not do

* It does not detect islands, joints or interactions. A group exists because the game said so.
* It does not guarantee a group fits a worker, or make one smaller. D9 reports; NEB-235 is where the planner will
  learn to cut along these constraints instead of merely respecting them.
* It says nothing about entities outside a group: two entities in different groups, or one in a group and one not,
  are unrelated as far as assignment and handover are concerned.
* A hold is not a lease or a pin. It expires, it dies with its worker, and it never keeps a container unowned. The
  permanent forms remain `ContainerHint.Dedicated` and a pinned carried container.

## 3. Where it lives

| Piece | File |
|---|---|
| The group on the entity, join and leave | `Runtime/Core/NetworkIdentity.cs` |
| The process-wide group table | `Runtime/Core/CohesionGroups.cs` |
| Island exemption | `Runtime/Core/PhysicsIslands.cs` |
| The wire field | `Runtime/Protocol/Messages.cs` (`EntitySpawnMsg.CohesionGroup`) |
| Handover expansion and the split report | `Runtime/Worker/NebulaWorker.cs` (`TransferAuthority`, `CollectCohesionMembers`) |
| Holds and the reported spans | `Runtime/Worker/WorkerCohesion.cs` |
| The telemetry document | `Runtime/Worker/WorkerTelemetry.cs` (`WriteCohesion`) |
| Parsing, deadlines, merging | `Runtime/Orchestrator/MeshTelemetry.cs` |
| Planner input, item merging, the reports | `Runtime/Orchestrator/AssignmentPolicy.cs` |
| Dashboard JSON and card | `Runtime/Orchestrator/NebulaOrchestrator.cs`, `Resources/NebulaDashboard.html` |
| Conformance (tier A, both builds) | `Tests/EditMode/ConformanceCohesionTests.cs` |
| Conformance (tier B, real workers) | `Tests/EditMode/ConformanceCohesionHandoverTests.cs` |
