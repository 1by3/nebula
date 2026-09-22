# Per-entity cost hints and per-container cost telemetry

Goal: let a game say what one entity costs, and let an operator see what one container costs, split
into the three things a container can be expensive in. Today the mesh balances on
`CostWeights`, which is per *category* — player, bot, server-driven, other — and reports per
container only a head count. That is enough for a shooter where every NPC is the same NPC, and not
enough for a world with ambient chickens and raid bosses in it: both are "server-driven", and they
are not the same machine. And when the scaler refuses to grow because one container carries a whole
tick, "it cannot be split" is the same sentence whether that container is full of expensive AI or
full of players whose state has to be sent to everyone — and those two want opposite fixes.

Decisions (2026-09-21): the entity weight is a multiplier on the category weight, so a world that
sets nothing behaves exactly as before; the weight travels with the entity; the container's
simulation time is measured, not apportioned; the three components are weighed against budgets of
their own, never against each other directly; the rows are a typed struct the orchestrator keeps per
lease, not a log line.

## Design

### D1. Three components, kept apart

A container costs its worker three distinguishable things:

| Component | What it is | Measured as | What fixes it |
| --- | --- | --- | --- |
| **simulation** | the `NetworkTick` callbacks the worker ran for the container's own authoritative entities | ms per simulated tick | split the container, or make its entities cheaper |
| **replication** | world state, netvars and sync state leaving the worker for gateways (and for neighbour workers' ghosts), counted once per destination | bytes per second | tighter interest: smaller radius, fewer always-relevant entities, focus hints |
| **gateway** | owner state, which a gateway forwards to exactly one client each | bytes per second | more gateways, or fewer players in one place |

Interest management (`docs/interest-management.md`) bounds the second and the third. It does
nothing for the first: a cell with five hundred NPCs in it costs the same to simulate whether
anybody is watching or not. Keeping them apart is the whole point — a single "load" number cannot
tell an operator which of the three knobs to reach for.

### D2. The entity weight is a multiplier, not a new category

`NetworkIdentity.CostWeight` (inspector-settable, default `1`) multiplies the entity's category
weight from `NebulaConfig.CostWeights`. A raid boss at `8` costs eight server-driven entities; an
ambient critter at `0.25` costs a quarter of one. At `1` everywhere the weighted sum is *exactly*
the head count times the category weights, so no existing world changes behaviour by upgrading.
The alternative — more categories — would have meant a new config field per kind of entity and a
wire change for each; a multiplier is one float and the game decides what it means.

Weights are clamped to `[0, NebulaCost.MaxWeight]` (1024). A NaN reads as 1.

### D3. Precedence

Highest first:

1. a weight carried in with the entity's spawn/handover state, or one set with
   `identity.SetCostWeight(w)`. Either **pins** the weight: nothing recomputes it afterwards.
2. `NebulaCost.EntityWeight`, a `Func<NetworkIdentity, float>` the game installs once at start-up.
   Returning a negative number (or NaN) declines, leaving the authored value; an exception is
   logged and treated as declining.
3. `NetworkIdentity.CostWeight`, the value on the prefab or the scene object.

The weight is computed **once per entity**, in `NebulaWorker.Spawn`, and on an explicit
`SetCostWeight`. Never per tick: the callback may be as expensive as a `GetComponent`, and an
entity's cost is a property of what it is, not of what it is doing this frame.

### D4. The weight travels

`EntitySpawnMsg` carries `CostWeight` as an f16 (protocol 18). The same struct is the payload of
`AuthorityTransfer` and of a ghost spawn, so one field covers handover, ghosting and the client
spawn. The receiving worker **takes** the value and pins it rather than re-asking its own callback:
a boss that costs 12 on one side of a seam must cost 12 on the other, or the planner would see load
appear and disappear every time something crossed. A `0` on the wire means "no opinion" and leaves
what the receiver already has; that is also what an older sender writes, so nothing becomes free by
accident.

### D5. Simulation time is measured, not apportioned

Attributing a worker's tick time to its containers in proportion to their cost would make the
telemetry a restatement of the weights: a container full of static props would "cost" what its
occupant count says, which is the very thing the operator is trying to check. So the worker times
it. `NebulaWorker.Tick` already loops over its authoritative entities by nesting depth; two
`Stopwatch.GetTimestamp` reads around each entity's `NetworkTick` calls charge that entity's time to
`e.Container`. At 500 entities and 60 Hz this is 60 000 timestamp reads a second, about 0.02 ms of a
16.7 ms tick.

What is **not** attributed: the physics step, the ghost pass, container membership, the interest
pass, the publish loop, and the engine's own frame work. None of those belong to one container.
The consequence is documented rather than papered over: **a worker's container rows do not add up
to its utilization.** `ContainerCost.TickShare` is each row's share of the simulation time that
*could* be attributed, and the rows of one worker sum to 1.

### D6. Bytes are attributed exactly, where the send already names an entity

- World state: an entry is a fixed `EntityStateEntry.WireSize` and goes to every gateway in the
  entity's mask, so `WireSize × subscribers` is charged to its container, per entry.
- Netvars: the message is built per entity; its length times the subscriber count is charged.
- Sync state: built and sent per destination, charged once per destination.
- Owner state: built per entity for one client — charged to the **gateway** component.

Spawns and despawns are not charged. They are event traffic, not a rate, and a container's steady
cost is what the scaler and the dashboard are asking about.

### D7. Budgets, not shares

To say which component dominates, they have to be comparable, and milliseconds are not bytes. The
natural-looking answer — each container's share of its worker's total for that component — is wrong
in exactly the case that matters: the only container on a worker takes 100 % of all three, so it is
"dominant in everything" precisely when the scaler is asking why it is hot.

So each component is measured against a budget of its own, and the largest saturation wins:

- simulation against the tick period (`WorkerLoadTracker.TickPeriodMs`, 16.7 ms at 60 Hz);
- replication and gateway relay against `NebulaConfig.CostLinkBudgetMbps` (default 100 Mbit/s).

`CostLinkBudgetMbps` decides nothing about what is sent. It is a yardstick, and the only thing that
changes when it is wrong is which of two close components is named first. Ties go to simulation,
then replication, so the answer is stable between passes.

### D8. The scaler names the component

When `WorkerScaler` refuses to grow because a dry run at N+1 does not lower the peak, it already
names the container. It now also names what that container is expensive in, from the container's
cost row:

```
blocked: chunk_4_0_2 carries 0.81 of a tick on its own and cannot be split
  (mostly simulation: 13.40 ms/tick, 0.80 of the tick budget); add a hint or split the cell
```

and `ScaleDecision.BlockedComponent` / `BlockedSaturation` carry the same thing in typed form, into
`/api/state` as `scale.blockedComponent` and `scale.blockedSaturation`. With no cost row on file the
reason is exactly the sentence it was before.

### D9. One typed row per container, which is one row per lease

`ContainerCost` (`Runtime/Orchestrator/ContainerCost.cs`) is the row:

```csharp
public struct ContainerCost
{
    public string ContainerId, ScopeKey, WorkerId;
    public float  EntityCostSum;          // Σ category weight × entity weight, without CostWeights.Base
    public float  TickShareMs, TickShare; // measured ms/tick, and its share of this worker's attributed time
    public long   BytesOutPerSec;         // replication
    public long   GatewayBytesPerSec;     // relay
    public int    GhostCount;
    public CostComponent Dominant;        // Simulation | Replication | Gateway
    public float  DominantSaturation;     // 1 = the whole of that component's budget
    public double ReceivedAt;
}
```

A container has exactly one owning lease, so keying the rows by container id *is* keying them by
lease. `MeshTelemetry` keeps the latest row per container and drops a worker's rows wholesale when
it sends a new document, when it is forgotten, and after `ExpireSeconds`;
`NebulaOrchestrator.CostOf(containerId)` and `MeshTelemetry.CopyContainerCost(dict)` hand them out.
Grouping by `ScopeKey` gives a per-scope signal without another table — which is what admission
reporting (NEB-236) needs, and why the rows carry the scope key at all.

### D10. The wire and the JSON stay additive

The rows ride the telemetry document the worker already posts every second
(`POST /api/telemetry`), as extra keys on the `containers` array it already writes:

```json
{"id":"arena","players":3,"bots":1,"serverDriven":0,"other":0,"ghosts":2,
 "scope":"","cost":14.5,"tickMs":4.25,"bytesOut":90000,"gatewayBytes":12000}
```

No new endpoint for the worker, no new cadence, and a reader that does not know the new keys reads
the document exactly as before. A document *without* them (an older worker) is not an error: the
row's cost fields read as zero and `ContainerLoad.HasEntityCost` is false, so `CostWeights.Of`
falls back to counting heads, which is what it always did.

### D11. Where the numbers surface

- `GET /api/cost` — `{tickPeriodMs, linkBytesPerSec, containers:[row…]}`, heaviest first. Served
  straight off the telemetry store on the listener thread, like `/api/map`, so polling it never
  waits on the orchestrator's frame.
- `GET /api/state` — the same rows under `cost`, plus `scale.blockedComponent` /
  `scale.blockedSaturation`.
- The dashboard's **Container cost** table, with the blocked container highlighted.

### D12. The planner uses the weights

`CostWeights.Of(load)` prefers the worker's reported `EntityCostSum` when there is one. That is the
point of the whole exercise: the cost policy, the utilization attribution
(`WorkerLoadTracker.Attribute`) and the scaler's dry runs all go through `Of`, so weighting a boss
changes where it is placed and when the mesh grows, not only what the dashboard prints.

## Configuration

| Field | Default | Meaning |
| --- | --- | --- |
| `NetworkIdentity.CostWeight` | 1 | multiplier on this entity's category weight |
| `NebulaCost.EntityWeight` | unset | `Func<NetworkIdentity,float>`; negative declines |
| `NebulaConfig.CostLinkBudgetMbps` | 100 | the yardstick bytes are measured against when the dominant component is chosen |

## Tests

- `Tests/EditMode/EntityCostWeightTests.cs` — precedence (authored / callback / `SetCostWeight`),
  clamping, a throwing callback, the f16 round trip through `EntitySpawnMsg` and the carry on the
  receiving side, and the weighted sum in the telemetry document.
- `Tests/EditMode/ContainerCostTests.cs` (also compiled into the service tests) — parsing the rows
  out of a document, a document without them, `CostWeights.Of` with and without a reported sum, the
  dominant component against its budget, `TickShare` over one worker's rows, row lifetime in
  `MeshTelemetry`, and the `/api/cost` JSON.
- `Tests/EditMode/WorkerScalerTests.NamesWhatTheUnsplittableContainerIsActuallyExpensiveIn` — the
  reason string and `BlockedComponent` for a simulation-bound and a replication-bound container, and
  the unchanged sentence when no row has arrived.

## Not in scope

Deciding simulation tiers, throttling NPCs, or running any AI. Nebula measures and reports; what to
do about an expensive entity is the game's business. Splitting a hot container automatically is
cohesion-aware rebalancing (NEB-235); reporting admission capacity per scope is NEB-236.
