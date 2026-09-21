# Interest management — design

Status: design of record for protocol **v17** (breaking). Decisions made without asking are marked **D#**.
User-facing guide: `website/content/docs/guides/interest-management.mdx`. Baseline numbers: `docs/interest-baseline/`.

## 0. Problem

Before v17 visibility was instance scope only: every public-world client was spawned every entity, distance only
throttled root-transform rate, every gateway dialed every worker and cached every entity, and the relay was
O(clients × entries) per packet with O(clients × all entities) reconciles. The full lease table was broadcast to every
client. None of that survives an unbounded world.

Interest management bounds who **hears** about an entity, not who **simulates** it. One container is still one worker's
simulation budget (§11).

## 1. Two jobs, kept separate

| Job | Mechanism | Depends on the game's partitioning? |
|---|---|---|
| Indexing: "what is near this focus" | `InterestGrid`, a uniform spatial hash over origin-independent world positions | No |
| Subscription: what a gateway asks a worker for | one mechanism: region-filtered subscription (§5) | No (a whole container is just "all regions overlapping it") |

Flow: client foci → regions (+ explicit extras from policy) → gateway subscribes the union from the owning workers only
→ workers publish only subscribed regions per gateway → gateway fans out to the observers of each entity, applying
exact radius / hysteresis / LOD / authorization inside that candidate set.

## 2. Code layout

All core logic is pure C# (no `UnityEngine.Object`), in `Runtime/Interest/`, compiled into both the Unity runtime and
`Services~` so the standalone gateway, the Unity worker, the fake worker in the service tests and the load generator
run the **same** code:

- `InterestGrid` — region arithmetic (struct; cell size, offset, planar flag).
- `InterestIndex<T>` — region → dense list of items, swap-remove, item remembers (region, slot). Used by the gateway
  (entity records) and the worker (authoritative entities). Plus the *wide list* and *global list*.
- `RegionSubscription` — the idempotent subscription state machine, both ends (§5).
- `InterestSettings` — the numeric knobs resolved + validated from `NebulaConfig` (§9).
- `IInterestPolicy`, `DefaultInterestPolicy`, `InterestFocus`, `InterestQuery` (§7).
- `ClientInterest` — per-client set, hysteresis, evaluation (§4).
- `RegionPublisher` — worker-side bucketing by subscriber mask (§6).

## 3. The interest grid

- Region = cell of a uniform grid over **absolute** world coordinates (doubles), edge `InterestCellSize` (default 64 m).
  Region id = `RuntimeGrid.PackId(x, y, z)` (the pinned 3×21-bit packing; not changed). **D1** planar by default
  (`InterestPlanar = true`: y is always 0, regions are columns) because both reference worlds are surface worlds and the
  exact per-entity radius test is 3D anyway; set false for space/volume games.
- Origin independence: keys are computed from absolute position = container absolute centre (double; cell coord ×
  cell size, or the lease row's absolute box) + rotated local position. Wire positions are already container-local, so
  a floating-origin shift changes no key. The gateway never shifts its origin; workers convert through
  `WorldOrigin` in double. `InterestGrid.RegionOf(double,double,double)` is the only place keys are made.
- **Alignment.** When a world definition exists (baked `WorldManifest.World` or `RuntimeWorld`) the effective edge is
  snapped to `CellSize / max(1, round(CellSize / InterestCellSize))` per horizontal axis, and the grid offset is set so
  region edges coincide with cell edges (baked cells are *centred* on `coord × CellSize` → offset −CellSize/2;
  `RuntimeGrid` cells start at `coord × CellSize` → offset 0). A 64 m chunk world gets regions ≡ chunks; a 2048 m baked
  cell is subdivided 32×32; one big container is simply subdivided. No second mode.
- Both ends derive the grid from shared config. **D2** the subscribe message still carries (edge, offset, planar) and
  the worker rejects + logs a mismatch loudly instead of silently filtering with different ids. This is verification,
  not negotiation.
- **Carried entities (D3).** An entity inside a dynamic container is bucketed in its *root carrier's* region and
  inherits the root carrier's interest decision per client, so a ship and everything riding in it enter and leave as a
  unit at any speed. The index keeps carrier → children links; rebucketing a carrier moves its subtree. (Bucketing
  children by their own resolved position would let a seat straddle a region edge and pop separately from its ship.)
- **Wide and global lists.** An entity whose relevance radius exceeds `InterestRadius` (per-prefab override, clamped to
  `InterestMaxRadius`) is not found by widening every client's region scan; it lives in a small *wide list* checked
  directly (distance from focus ≤ its radius). `AlwaysRelevant` prefabs live in the *global list*. Both exist on the
  worker (to decide which gateways hear about them) and on the gateway (to decide which clients do).
- Rebucketing happens only when the key changes: the gateway recomputes a key when it relays a state entry with a
  position change (one multiply/floor per axis), the worker once per tick per authoritative entity.

## 4. Per-client interest (gateway)

- `ClientConn.Visible` becomes the interest set; `EntityRecord.Observers` (dense list of clients) is its inverse.
  **All** entity traffic — world state, netvars, SyncState, broadcast and spatial RPCs, despawns — fans out over
  `Observers`, so per-tick cost is Σ(entries × observers) ≈ clients × nearby entities. Targeted RPCs and owner state go
  to their client only if the entity is in its set (the owner's always is).
- Evaluation is bounded-rate: each client is evaluated at `InterestEvalHz` (default 4), staggered across ticks, and
  immediately when a focus crosses a region edge, its pawn/instance/carrier changes, or policy marks it dirty. An
  entity that *arrives* (spawn, or rebucket into a region) is tested once against only the clients whose focus regions
  cover that region (region → interested-clients map), never against all clients.
- Decision per (client, entity), in order: authorization (`CanObserve` instance rules, unknown container ⇒ never,
  then `policy.Authorize`) → always-set (own pawn, owned entities, policy extras, global list) → distance:
  enter when `d ≤ R`, leave when `d > R + InterestExitMargin` **and** it has been outside for `InterestLingerSeconds`.
  `R` = the entity's relevance radius (prefab override) or `InterestRadius`, distance = min over the client's foci,
  measured to the root carrier for carried entities. Authorization failure removes immediately (no linger) — it is a
  security boundary.
- Enter ⇒ `EntitySpawn` with refreshed state + keyframes (carriers before contents); leave ⇒ `EntityDespawn{NetId,
  Epoch}`. Epoch rules are unchanged. The client's despawn guard becomes `msg.Epoch < e.Epoch` **and** a per-netId
  visibility sequence so a late despawn cannot kill a re-entered replica (**D4**: `EntitySpawn`/`EntityDespawn` gain a
  `u16 ViewSeq` assigned by the gateway per client).
- Rate tiers stay as LOD inside the set (`InterestNearRadius` / `InterestFarRadius` / divisors), computed from the
  record's cached absolute position and the client's cached foci — no recursive container walk per pair.
- `ObservePublic` windows are an extra *box* source for clients inside that instance; private-instance isolation is the
  authorization step and is evaluated before anything spatial.
- Pawn-less clients have no default focus: they receive only global entities (replaces "everything at far rate").
  A policy may give them a focus (spectators).

## 5. Gateway ↔ worker subscription protocol

Messages (reliable-ordered on the gateway→worker link):

```
InterestSubscribe  (gw→w)  u32 Seq, u32 BaseSeq, grid{f32 edgeX, edgeY, edgeZ, f32 offX, offY, offZ, u8 planar},
                           u8 flags{Full, Commit}, u16 nAdd, u64[] add, u16 nRemove, u64[] remove,
                           u16 nFoci, u64[] fociRegions   (always the full foci list; it is small)
                           u16 nEntities, u64[] entityIds (explicit per-entity subscriptions, full list)
                           u32 setCount, u64 setHash      (count and XOR-mix hash of the region set after applying)
InterestResync     (w→gw)  u32 HaveSeq                     (worker could not apply: BaseSeq mismatch or hash mismatch)
EntityRedirect     (w→gw)  u64 NetId, u16 NewWorkerIndex   (an explicitly subscribed / owned entity moved to a worker)
EntityForget       (w→gw)  u64 NetId, u32 Epoch            (left every region this gateway subscribes on this worker)
```

- Chunks of one update share a `Seq` and only the chunk carrying `Commit` is applied; the foci and entity lists
  ride on that chunk (**D6**, phase 1). A worker stages the earlier chunks and rolls them back if the commit never
  arrives or does not verify.
- State is a **set**, so it is idempotent. Deltas apply only when `BaseSeq` equals the worker's current seq and the
  resulting (count, hash) matches; otherwise the worker keeps its old set, answers `InterestResync`, and the gateway
  sends a `Full` snapshot. Full snapshots are also sent on (re)connect, on worker incarnation change, and every
  `InterestResyncSeconds` (30 s) as a belt-and-braces audit. Large sets are chunked; only the last chunk commits.
- **Which workers.** For each needed region the gateway finds the containers overlapping the region box expanded by
  `GhostBandMargin + HandoverHysteresis` (baked grid / runtime hash / static list; if none, the nearest container, the
  same rule `ContainerRegistry.Find` uses for entities outside every box) and sends the region to those lease owners.
  Additional links: owners of containers within `InterestMaxRadius` of a focus get the **foci** only (so wide entities
  can reach us); workers advertising `HasGlobalEntities` in their heartbeat get an empty subscription; owners of pinned
  dynamic-container leases get the regions too (a pinned interior is simulated away from its territory's owner).
  A gateway dials a worker only while one of those reasons holds (plus `InterestLinkLingerSeconds`, 10 s) and
  disconnects otherwise. Workers no longer announce everything on a gateway `Hello`.
- **Pre-subscription margin.** Needed regions = regions intersecting a disc of `InterestRadius + InterestExitMargin +
  InterestSubscribeMargin` around each focus (default margin 32 m ≈ 3 s at sprint; validated ≥ one eval interval at
  `InterestMaxFocusSpeed`). Because regions are sent to every owner whose container overlaps the expanded box, a client
  approaching a worker boundary is already receiving from the next worker. Unsubscribe is delayed by
  `InterestRegionLingerSeconds` (3 s) so pacing along an edge does not thrash.
- **Worker filtering.** See §6. On a newly subscribed region the worker sends `EntitySpawn` for each entity bucketed
  there; when an entity rebuckets from region a to b the worker sends spawn to gateways with b∖a and `EntityForget` to
  gateways with a∖b. On unsubscribe nothing is sent: the gateway drops its own cache for that region (it knows the
  positions) and despawns from any remaining observers. No per-gateway per-entity "known" set exists on the worker.
- **Owned and explicit entities.** Entities with `OwnerClientId != 0` are always published to their session gateway
  (the worker already tracks it, and `AuthorityTransferMsg` already carries it). Policy extras (`party`, `quest target`)
  are explicit netId subscriptions: sent to every connected worker; while an id is unresolved the gateway temporarily
  links every live worker (**D5**, bounded by `InterestMaxExplicitPerClient`, default 16); once the record arrives it
  keeps only the owner. On authority transfer the explicit-subscriber gateways ride in `AuthorityTransferMsg`, the old
  owner sends `EntityRedirect`, and the gateway links the new owner.
- **Failure modes.**
  - *Worker death*: link drops → gateway removes that worker's records (observers get despawns) and re-resolves
    regions when leases move; the replacement worker gets a Full snapshot on connect.
  - *Lease migration / authority transfer mid-subscription*: regions are re-resolved on every control-plane change;
    the new owner is linked before the old one is dropped (link linger); epoch/owner-index checks already discard the
    loser's stale state. A spawn from the new owner for a known id is an in-place update (existing behaviour).
  - *Gateway drain*: a draining gateway keeps its subscriptions until its last client leaves, then sends an empty Full
    set and disconnects.
  - *Client reconnect / session takeover*: the interest set is per `ClientConn`; a reclaimed session starts empty and is
    evaluated immediately (spawns carry a fresh `ViewSeq`), so the client is told exactly what it should have.
  - *Message loss / reorder*: the channel is reliable-ordered per link, so loss means a link reset, which forces a Full
    snapshot; seq + hash catch any bug or chunk interleave.

## 6. Worker publishing

`RegionPublisher` buckets authoritative entities by region once per tick (rebucket on key change only). Each region has
a subscriber **mask** (bit per gateway link, ≤ 64; beyond that, extra gateways fall back to per-region sends).
Regions are grouped by mask and their entities packed into shared world-state batches per mask, written once and sent
to each gateway in the mask. With G gateways the work is O(entities + batches × subscribers), not O(entities ×
gateways). Netvars, SyncState and entity RPCs use the same mask lookup. Wide entities are matched against each
gateway's foci at `InterestEvalHz`; global entities go to every linked gateway; owned entities add their session
gateway's bit. Stats: per gateway filtered vs total entries and bytes.

Ghost band (worker ↔ worker) audit, fixed here: per-message `ToArray` allocations in the stream loop, the
`ulong→string→ulong` scratch round trip, `peers × ghostTargets` iteration (now per-target-worker lists), the
linear `DynamicList` scan in `NeighborsOf` for static containers (now via the spatial hash), the per-call list in
`ResumeInheritedGhosts`, and the one-tick ghost despawn/respawn window after `TransferAuthority` (inherited targets
are seeded with a fresh linger timestamp).

## 7. Policy API

```csharp
public interface IInterestPolicy {
    // Fill foci (points with optional radius scale, or boxes) and explicit always-relevant netIds for this client.
    void Collect(in InterestClient client, InterestQuery query);
    // Security filter, called before an entity may enter or stay. Team / fog of war.
    bool Authorize(in InterestClient client, in InterestEntity entity);
}
```

- `NebulaGateway.InterestPolicy` (default `DefaultInterestPolicy`: one focus at the pawn — resolved through carriers —
  plus the clamped client hint; authorize = true). Policies compose with `InterestPolicies.Combine(a, b)` (union of
  foci/extras, AND of authorize). `InterestClient` exposes ClientId, identity/name, pawn netId + position, instance,
  `Team` (a `byte` the game sets via `gateway.SetClientTag`), the validated hint. `InterestEntity` exposes netId,
  prefab id, owner, container, absolute position, `InterestGroup` byte from the prefab.
- Foci are capped at `InterestMaxFoci` (8). Multiple foci = RTS camera + owned units; spectators = policy supplies a
  focus for a pawn-less client.
- **Client hints are inputs, never authority.** `ClientFocusHint` (client→gateway, unreliable): position; dropped if
  non-finite, rate-limited to `InterestHintMaxHz` (5), and clamped to `InterestHintMaxDistance` (default 60 m) from the
  pawn unless the policy explicitly accepts free foci for that client.
- Per prefab, on `NetworkIdentity`: `RelevanceRadius` (0 = default), `AlwaysRelevant`, `InterestGroup`. They travel in
  `EntitySpawnMsg` (the standalone gateway has no prefabs) and are clamped by the gateway's config.

## 8. One notion of "near": cells, streaming, allocation, interest

`InterestSettings.NearCells(cellSize) = max(ClientLoadRadiusCells, ceil((InterestRadius + InterestExitMargin +
overshoot) / cell))`, where `overshoot = InterestMaxFocusSpeed × (InterestLingerSeconds + 1/InterestEvalHz)`: the
exit radius is where an entity *starts* to leave, so a client travelling at the maximum focus speed legitimately
still holds entities that much further out (after.md §6.6). With no world definition (`cell ≤ 0`) it
short-circuits to `max(0, ClientLoadRadiusCells)`.

- Client content streaming (baked: `WorldStreamer` anchor radius; runtime: which containers the client is told about)
  uses `NearCells`.
- `RuntimeGridAllocator.Ring` is *constructed* as `NearCells + 1` by `NebulaChunkedWorld`, so cells exist before
  interest needs them; there is no separate setting for validation to police.
- `ContainerOwnershipMsg` becomes a per-client **delta** (`Full` flag, upserts, removes): a client is told only about
  containers overlapping its `NearCells` window, its instance, and dynamic containers of entities in its set. Ownership
  upserts are queued on the same reliable batch *before* the spawns that need them, so an entity never arrives before
  its container; a container is removed only after the entities in it have been despawned.
- Validation (§9) enforces the relationships instead of documenting them.

## 9. Config (NebulaConfig + the Services mirror) and validation

New: `InterestRadius` 120, `InterestExitMargin` 16, `InterestLingerSeconds` 1, `InterestCellSize` 64,
`InterestPlanar` true, `InterestEvalHz` 4, `InterestSubscribeMargin` 32, `InterestRegionLingerSeconds` 3,
`InterestLinkLingerSeconds` 10, `InterestMaxRadius` 1024, `InterestMaxFoci` 8, `InterestHintMaxDistance` 60,
`InterestMaxExplicitPerClient` 16, `PartitionWarnEntities` 2000, `PartitionWarnFilterMs` 2. Kept:
`InterestNearRadius`, `InterestFarRadius`, divisors (LOD inside the set). Chunked world: `ChunkedWorld`,
`ChunkPlanar`, `ChunkRetireSeconds` (§10).

`NebulaConfig.Validate(List<ConfigIssue>)` (also `OnValidate`, the setup window, `nebula doctor`, and role start-up
logs): near ≤ far ≤ radius; exit margin > 0; cell size > 0 and, with a world definition, an integer divisor of the cell
(else snapped + warned); subscribe margin ≥ max speed / eval Hz; `GhostBandMargin` < interest cell; load radius cells ×
cell ≥ radius + exit (else auto-raised + warned); max radius ≥ radius, and a **non-positive max radius is set to
the mesh radius rather than read as "uncapped"** — it is the ceiling on what a prefab may ask a gateway to send,
so it is a boundary that is never off (**D43**). The allocator ring is *not* validated: `NebulaChunkedWorld`
constructs it as `NearCells + 1`, so there is no separate value to disagree with. Errors block
start-up only where behaviour would be wrong (non-positive sizes); everything else is clamped and warned.

## 10. Turnkey chunked world

`ChunkedWorld = true` + a `RuntimeWorld` definition (cell size) is the whole server/client setup. The bootstrap then:
builds the `RuntimeGrid` from the definition (`ChunkPlanar` → y fixed at 0, planar neighbourhoods, column bounds),
installs it as the runtime bounds hook and hash bucket size, runs the allocator on workers (ring from §8, retirement
after `ChunkRetireSeconds`), keeps the origin near the pawn on clients and near the owned centroid on workers, and
raises content callbacks on every role:

```csharp
NebulaChunks.Loaded   += (in ChunkContext c) => { /* c.Coord, c.Id, c.Seed, c.Container, c.Root, c.Role, c.IsHeadless */ };
NebulaChunks.Unloading += (in ChunkContext c) => { };
NebulaChunks.EnsureAt(position, container => worker.Spawn(...));   // spawn-point helper
```

`Loaded` back-fills containers registered before the handler was added; objects parented to `c.Root` are destroyed
with the chunk. The allocator also re-touches foreign leases and prunes its bookkeeping (both bugs in the sample's
hand-rolled copy). Baked worlds get the same interest/streaming behaviour through the manifest's cells.
`nebula-virtualworld` deletes `ChunkAllocator`, `ChunkLoader`, `Chunks` and keeps one content callback.
Its chunk ids change (`x<<32|z` → pinned `RuntimeGrid` packing): reset its persistence once.

## 11. Honest limits

| World shape | Client culling | Gateway scoping | Simulation scaling |
|---|---|---|---|
| Infinite grid / baked cells | yes | yes (regions ≈ cells) | yes |
| Several zone containers | yes | yes (container + region) | yes, as far as the zones allow |
| One big container | yes | partial (region filter, single worker source) | no — limited by the one worker |

A worker warns (log + telemetry → dashboard) when one container holds more than `PartitionWarnEntities` entities or
when its per-gateway filtering exceeds `PartitionWarnFilterMs` per tick: "partition this world".

## 12. Observability

`GatewayStats` += interest set avg/max, cached entities, subscribed regions, worker links (and why: region / foci /
global / explicit), spawns/s, despawns/s, interest eval ms (avg/max), bytes per client avg/max. Worker telemetry +=
per gateway: subscribed regions, entries sent vs total, bytes sent vs unfiltered. Shown in the debug overlay and the
dashboard; the load generator reports replicas and bytes/s per client.

## 13. Tests

Unit (EditMode + Services.Tests): grid keys incl. negatives/planar/offset/alignment snap, origin-shift invariance,
carrier subtree rebucket, wide/global lists, hysteresis + linger, policy composition + hint clamping, subscription
delta/BaseSeq/hash/resync/chunking, mask publisher, config validation, instance isolation & ObservePublic & unknown
container invariants, RuntimeGrid packing pins, and handover: an owned pawn surviving authority moving to a worker
the gateway has no link to, its previous worker's link going away right after, and a chain of handovers through
workers the gateway never linked (§15 D42).
Soak: `InterestSoakTests`, marked `[Category("Soak")]` and run with
`dotnet test Packages/com.1by3.nebula/Services~/Nebula.Services.slnx --filter TestCategory=Soak` — the three
shapes of §11 over the same fleet (real gateway, real `RegionPublisher` in the fake worker), at two world sizes
each, asserting bounded replicas/bytes for a stationary client independent of world size, gateway
cache/ingest/links bounded by its clients' interest, and a traversing client seeing no gaps, duplicates or leaked
replicas; the table lands in `docs/interest-baseline/soak-results.md`. There is no `nebula-loadgen --scenario`:
`nebula-loadgen` gained `--idle <0..1>` and per-client replica and byte columns, and is a load source, not a
shape runner.

## 14. What was built

`Runtime/Interest/` (pure C#, compiled into the Unity runtime and `Services~` through one `<Compile>` glob) holds
`InterestGrid`, `InterestIndex<T>`, `RegionSubscription` + `RegionSubscriptionSender`/`RegionSubscriptionReceiver`,
`InterestSettings`, the policy API (`IInterestPolicy`, `InterestClient`, `InterestEntity`, `InterestFocus`,
`InterestQuery`, `DefaultInterestPolicy`, `InterestPolicies.Combine`, `FocusHintFilter`), `ClientInterest<T,TSource>`
and `RegionPublisher`.

- **Gateway.** `Runtime/Gateway/GatewayInterest.cs` is the whole gateway engine (a partial of `NebulaGateway`):
  the entity index, per-client evaluation, the policy surface, worker links and subscriptions, scoped ownership
  deltas and the stats. `NebulaGateway.cs` fans **all** entity traffic over `EntityRecord.Observers`; the
  `ReconcileView` full scan, the per-client loop over every world-state entry and the join replay over every
  entity are gone. `NebulaClient` gained the `ViewSeq` guard, `FocusHint`/`ClearFocusHint` and replica counters.
- **Worker.** `Runtime/Worker/WorkerInterest.cs` (a second part of `NebulaWorker`) holds the worker's index, one
  `RegionSubscriptionReceiver` per gateway link, the mask arithmetic and the stats. `PublishToGateways` groups
  entities by their subscriber mask and writes one batch per group; netvars, sync state, entity RPCs and despawns
  use the same lookup. "Announce every authoritative entity on `Hello`" is gone.
- **Chunked world.** `ChunkedWorld = true` + a `RuntimeWorld` is the whole setup on every role.
  `NebulaBootstrap` adds `NebulaChunkedWorld` (`Runtime/World/NebulaChunkedWorld.cs`) wherever
  `NebulaWorld.IsActive` is false and `ChunkedWorld` is on — exactly the runtime-world case the baked
  `NebulaWorldStreaming` never covered. It builds the grid from the definition's cell size, installs it as
  `ContainerRegistry.RuntimeBoundsInFrame`, derives `RuntimeBucketSize` from it, runs a `RuntimeGridAllocator` on
  workers with `Ring = NearCells + 1` and `RetireAfterSeconds = ChunkRetireSeconds`, keeps the origin on the pawn
  (client) or on the centroid of the leased cells (worker), leaves gateway and orchestrator passive, and
  activates `NebulaChunks`.

Public API added: `NebulaGateway.InterestPolicy`, `SetClientTag`/`GetClientTag`, `MarkInterestDirty`,
`InterestSetSize`, `CachedEntityCount`, `SubscribedRegionCount`, `WorkerLinkCount`,
`InterestSettingsInUse`/`InterestGridInUse`; `InterestLinkReason`; `NebulaClient.FocusHint`, `ClearFocusHint`,
`ReplicaCount`, `SpawnsReceived`, `DespawnsReceived`; `ContainerRegistry.Overlapping`;
`NebulaConfig.ResolveWorldCellSize`; `ClientInterest.ConsiderOne`; the `GatewayStats` interest fields and
`WorkerStats.HasGlobalEntities`.

**Wire order.** `EntitySpawnBody` gained `f16 relevance_radius, u8 interest_flags, u8 interest_group, u16 view_seq`
after `owner_identity`; `EntityDespawnBody` gained `u16 view_seq`. `ContainerOwnership` gained a leading flags
byte (`Full`) and a trailing remove list.

Tests: EditMode (`InterestGridTests`, `InterestIndexTests`, `InterestPolicyTests`, `InterestSettingsTests`,
`InterestSubscriptionTests`, `InterestWireTests`, `InterestConfigTests`, `InterestPackingPinTests`,
`ClientInterestTests`, `RegionPublisherTests`, `WorkerInterestTests`, `NeighborsOfTests`, `ChunkedWorldTests`)
and `Services~/Nebula.Services.Tests` (`Fixtures/MeshFixtures.cs` with the subscription-aware `FakeWorker`,
`FakeClient` and `Fleet`; `InterestTests.cs`; `InterestSoakTests.cs`, `[Category("Soak")]`, the three shapes of
§11 at two sizes each, writing `docs/interest-baseline/soak-results.md`). `nebula-loadgen` reports replicas and
bytes per client and takes `--idle <0..1>`.

Ghost-band work done alongside §6: the per-message `_writer.ToArray()` save/restore in the stream loop (the
sequenced batch has its own writer), the `ulong -> string -> ulong` round trips in the expiry pass and in
`OnPeerLost`, the dead `_scratchEntities.Clear()`, the `peers x ghostTargets` iteration (an index of ghosts per
target worker is built in the expiry pass that already walks the table), the per-call `new List<ulong>` in
`ResumeInheritedGhosts`, and a fresh band timestamp for inherited targets after a handover.

## 15. Decisions

Decisions taken without asking, in one list. **D1-D5** are made in the sections above; **D6-D12** came out of the
shared interest core, **D13-D19** out of the gateway and client, **D20-D28** out of the worker, **D29-D37** out of
the turnkey chunked world, and **D38-D41** out of integrating the three and running them on a real mesh.

### The shared core

- **D6. Chunking commits explicitly.** §5 said only that "only the last chunk commits" without saying how a
  worker recognises it, so the flags byte gained `Commit`. The worker journals what it applied and rolls back on
  a failed or abandoned chain, so a partial update never becomes the live set.
- **D7. The subscription ends are named after their direction**, not "Desired"/"Applied":
  `RegionSubscriptionSender` (gateway) and `RegionSubscriptionReceiver` (worker), with the shared set hash in
  `static RegionSubscription`.
- **D8. The packing is duplicated, not shared.** `RuntimeGrid` lives in the Unity-only `Nebula.World` assembly,
  which the standalone gateway does not compile, so `InterestGrid.PackRegion` is a second copy of the pinned
  3x21-bit packing. `InterestPackingPinTests` asserts the two agree; `RuntimeGridTests` still pins the values.
- **D9. Wide and global placement is the caller's call.** The index takes `Add`, `AddWide` and `AddGlobal` rather
  than deciding from a radius itself, because the gateway (prefab radius from the spawn message) and the worker
  (the prefab in hand) learn the radius differently.
- **D10. `ClientInterest` is generic over a struct source** (`IInterestSource<T>`), so the gateway's
  `EntityRecord` and the worker's entities both plug in with no allocation and no copy into an interest-shaped
  record.
- **D11. Three more config fields** than §9 listed, because validation and the hint filter need them as knobs:
  `InterestResyncSeconds` (30, from §5), `InterestHintMaxHz` (5, from §7) and `InterestMaxFocusSpeed` (12, the
  speed §9 validates the subscribe margin against).
- **D12. The services learn the world's cell size from the manifest.** The exported config has no asset
  references, so `ServiceManifest.Load` fills `NebulaConfig.WorldCellSizeMeters`/`WorldCellsAreCentred` from the
  manifest's world, and the shared `ToInterestGrid()` snaps the grid with them.

### The gateway and the client

- **D13. Placing a first pawn is a link reason.** §5 lists region, foci, global, explicit and pinned reasons,
  none of which a client with no pawn has — so a first join into a mesh whose gateway holds no links could never
  complete, because `TryRequestSpawn` only picks containers whose worker it is connected to. A welcomed client
  with no pawn therefore adds a `Spawn` reason to the owners of leased containers; it goes as soon as the pawn
  exists, and the pawn's own worker then holds the link under `Owned`.
- **D14. Owned entities are a link reason and are never evicted.** The counterpart of §5's "an owned entity is
  always published to its session gateway": the gateway holds the link to that entity's worker and does not
  evict the record when its region falls out of the subscription, or it would drop the client's own pawn.
- **D15. The gateway's world space is absolute space.** The gateway never shifts its origin, so its existing
  `WorldPosition` container maths already produces absolute coordinates; the record caches that result as three
  doubles rather than duplicating the container chain in double. Precision is that of the container frame
  (`float`), the same precision containers are defined with, and the key is stable because nothing moves the
  frame under it.
- **D16. Ownership scoping uses the pawn's `NearCells` box, not a cell walk.** The gateway asks
  `ContainerRegistry.Overlapping` for a box of `NearCells × cellSize` around the pawn (falling back to the exit
  radius when the game has no world definition), plus the client's instance container and the containers of the
  entities entering or in its set.
- **D17. `ContainerRegistry.Overlapping` is new.** Resolving a region to its owning workers needs a box query;
  the registry had `Find`, `Along` and `NeighborsOf` but no box. The `Services~` mirror scans its manifest
  instead, which is bounded because the gateway caches region → workers and re-resolves only on control-plane
  changes.
- **D18. Evaluation is cursor-free.** Each client carries its own next-evaluation time, set one `EvalInterval`
  ahead when it is evaluated, which staggers clients by arrival without a schedule to maintain.
- **D19. The soak traverses a fixed distance, not the whole world.** Walking the length of a 32 km world inside a
  test budget would mean teleporting hundreds of metres per step, which measures how fast a set settles after a
  jump no player can make. The soak walks 320 m at 20 m/s — several regions, container boundaries and, in the
  sharded shapes, worker boundaries — and asserts gaps, duplicates and leaks at every stop. Size independence is
  measured on the stationary client and the gateway, where it belongs.

### The worker

- **D20. Gateways past the 64th are unfiltered, not dropped.** §6 allowed either a per-region fallback or a
  documented cap. A link with no free bit is kept in `_unmaskedGateways`, logged as an error naming the worker
  and the gateway, and receives every authoritative entity — correct but unbounded, which is visible in the
  interest stats. Silently filtering it would hide entities; refusing the link would lose players.
- **D21. Entities are grouped by *effective* mask, not by region group.** An entity's owner gateway, explicit
  subscribers and wide/global placement add bits a region group does not have, so the publish pass buckets
  entities into `mask -> entities` once per tick and packs one batch per distinct mask. Same complexity, and one
  entity is never written into two batches for the same gateway.
- **D22. Sticky bits are never sent a forget.** The gateway speaking for an entity's owner, and any gateway that
  named it explicitly, keep it whatever region it is in, so `RebucketBits` masks them out of both the spawn and
  the forget. Without that, a pawn crossing a region edge would be forgotten by its own client's gateway.
- **D23. Owner state and targeted client RPCs go to the session gateway only**, not to the mask: they are for one
  client, and the mask is about who may know the entity exists.
- **D24. `AuthorityTransferMsg` gained `InterestGateways`** (gateway keys), the counterpart of the
  `EntityRedirect` the old owner sends. The new owner announces to them plus the gateways its own regions cover.
- **D25. Wide hysteresis is two mask queries.** A wide entity's mask is `enter | (previous & stay)`, where
  `enter` uses its radius and `stay` its radius plus `InterestExitMargin`. Evaluated at `InterestEvalHz`, and
  immediately after a subscription commit (new foci).
- **D26. The partition warning is one string, computed at the evaluation rate.** Per-container counts are tallied
  `InterestEvalHz` times a second, not per tick, and the same string is the telemetry field, the dashboard banner
  and the log line (at most one a minute).
- **D27. Interest stats travel in the telemetry document, not the heartbeat.** `WorkerTelemetry` writes an
  `"interest"` object (totals as per-second rates, plus a row per gateway link); `MeshTelemetry.ParseInterest`
  lifts the flat fields out of it the way `ParseContainers` already does. Only `HasGlobalEntities` rides the
  heartbeat, because gateways need it to decide which workers to link.
- **D28. Carried containers got a broad phase of their own.** §6 asked for `NeighborsOf`'s linear `DynamicList`
  scan to go through "the grid/runtime hash", but carried boxes move every tick and cannot live in the static
  grid. They are hashed into the runtime bucket grid instead, rebuilt when the caches are refreshed (once per
  tick) or when one is registered, with every footprint and query padded by a whole bucket so a carrier that
  moved since the rebuild is still a candidate. Below 16 carried containers the exact scan is kept, being
  cheaper. `NeighborsOfTests` pins the result against the exhaustive scan on random layouts.

### The turnkey chunked world

- **D29. Planar is a `RuntimeGrid` flag, not a second grid type.** `new RuntimeGrid(cellSize, planar: true)`
  fixes y at 0 in `CoordOf`/`Normalize`, makes `Neighborhood` a square in one layer, and centres a cell's box on
  absolute y = 0 so the column spans ±`CellSize.y`/2 around the ground plane. `PackId`/`UnpackId` are untouched,
  so `RuntimeGridTests`' pins and `InterestPackingPinTests` still hold.
- **D30. The instance `Neighborhood(center, ring, List<Vector3Int>)` overload is the one the allocator uses.** The
  static `IEnumerable` version stays for callers that want it, but a policy tick that allocates an enumerator per
  anchor per 0.25 s is not what §8 asks for, and only the instance form knows about `Planar`.
- **D31. A chunk's content root is a child object, not the container itself.** `ChunkContext.Root` is a
  `GameObject` named `content` parented to the container, created on first dispatch. Nebula detaches *networked*
  objects when a chunk retires (`UnregisterRuntime`), so content parented directly to the container would sit
  next to entities it must not be confused with; a separate root also means a game never writes an `Unloading`
  handler just to clean up.
- **D32. The seed is splitmix64's finalizer over the packed id.** Grid ids differ in one low bit between
  neighbours, so the raw id is useless as a seed; the mix makes adjacent chunks unrelated while staying a pure
  function of the id, which is what lets every role generate identical content with nothing on the wire.
- **D33. The allocator always anchors the origin cell.** Without a standing anchor an empty mesh holds no
  container at all and the gateway has nowhere to place the first player. Chunk (0,0,0) is the guaranteed spawn
  area; a game that wants another one adds anchors to `NebulaChunkedWorld.Allocator`.
- **D34. `NebulaChunks.EnsureAt` has two behaviours by role, deliberately.** On a worker it goes through the
  allocator (request the lease, then wait); on a client or gateway it only waits, because a client does not get
  to decide which containers exist — the gateway's per-client window does. Both are the same call site in game
  code.
- **D35. Config issues are logged at role start-up from `NebulaBootstrap.StartServices`,** and surfaced in the
  Editor through `NebulaSetup.ReportConfigIssues` (called from `EnsureConfig`). `nebula doctor` does **not**
  exist in the CLI, so §9's mention of it remains aspirational.
- **D36. Baked worlds' client anchor radius is now `NearCells`,** computed once in
  `NebulaWorldStreaming.Initialize` and exposed as `NearCells`; `ClientLoadRadiusCells` stays the floor, so a
  game asking for more content than interest needs still gets it.
- **D37. The soak instrumentation lives in the package** (`Runtime/Debug/InterestProbe.cs`), not in the sample:
  the things worth failing a soak on are framework properties, so both reference games attach the same component
  and produce the same `[nebula-probe]` line.

### Integration

- **D38. A carrier is always a region entity.** `InterestIndex.SetCarrier`/`MoveSubtree` move a carrier's
  children only when the carrier itself is bucketed by region, so a dynamic container in the wide or global list
  would leave everything riding in it bucketed wherever it was boarded: the ship would travel and its passengers
  would not, breaking D3 exactly where D3 matters. Rather than make the wide and global lists carry subtrees (a
  second rebucket path for entities that by definition have no region), `NebulaWorker.InterestAdd` downgrades a
  carrier to `Region` placement and logs the prefab once. A ship that must be seen further away needs a larger
  `InterestRadius`, not a per-prefab override. This is why `nebula-shootergame`'s Starhopper keeps the mesh
  default radius.
- **D39. A carried entity inherits its root carrier's radius and always-relevance, not only its position.**
  `InterestSource.Describe` was taking the position from the root (per D3) but the relevance radius and the
  `AlwaysRelevant` flag from the entity itself, so a passenger prefab with a different radius from its ship
  would still enter and leave on its own boundary. All three now come from the root.
- **D40. The probe counts `carrierless`.** A replica riding in a dynamic container whose carrier the client does
  not hold is the observable failure of D3, and it is the only one a log line can see, so `InterestProbe`
  reports it next to `beyond`, `dup` and `orphans`. Both reference games' smoke tests fail on a non-zero count.
- **D41. The probe is attached by the bootstrap, not by either game.** `-nebula-bot` turns it on and
  `-nebula-probe=false` turns it off again; a player client takes `-nebula-probe`. Putting it in
  `NebulaBootstrap` is what makes the two games' `[nebula-probe]` lines comparable, and it is one line rather
  than one per game.
- **D42. A record follows its entity across a handover, and a client's pawn is recovered by name.** A worker
  cannot dial a gateway, so after `TransferAuthority` the new owner can only announce to gateways it already
  has a link to; everything else rests on the `EntityRedirect` the old owner sends. Two things were missing.
  The redirect only added a transient `Explicit` link reason and left `EntityRecord.OwnerWorkerIndex` naming
  the previous owner, so the `Owned` link reason, the owner-index checks on world state, `EntityForget` and
  `DropWorkerRecords` all pointed at the wrong worker; it now moves the record to the new owner at once and
  marks it `OwnerUnconfirmed` until a spawn from that worker confirms it. And a *chain* of handovers can
  outrun the gateway's dialling: the second redirect is sent over a link that does not exist, so the gateway
  never hears where the entity went. That cannot be fixed by ordering, so the gateway heals instead: a
  welcomed client whose pawn it cannot place — no record, an owner index no live worker answers to, or an
  unconfirmed one — subscribes that pawn **by name** on every live worker (design D5's mechanism, pointed at
  the pawn), and whoever owns it announces it. If nobody claims it within `PawnRecoverySeconds` (5 s) the
  pawn really is gone and the client starts its join again, rather than staying connected and bodiless.
  Belt and braces around it: an entity a connected client owns is never dropped by `DropWorkerRecords`, never
  forgotten on an `EntityForget` (it is sticky on the worker by D22, so such a message is a worker bug), and
  never the reason a link is dropped; and the worker remembers a follower it could not reach and announces to
  it the moment that gateway links (`AnnounceAwaited`).
- **D43. `InterestMaxRadius` is never uncapped.** It is the ceiling on what a prefab's `RelevanceRadius` may
  ask a gateway to send a client, which makes it a cost and security boundary rather than a hint, and the
  clamps that read it were written as "clamp if it is set". A boundary that a zero switches off is not a
  boundary, so `Validate` now reports a non-positive value and uses `InterestRadius`; the clamps in
  `NebulaGateway.OnEntitySpawn` and `ClientInterest.Consider` are unconditional.
- **D44. A focus hint is cleared explicitly.** Silence is not a clear — the gateway keeps the last accepted hint — so
  `ClientFocusHint` carries `Clear` (sent reliably, never rate-limited: narrowing interest is always allowed) and a `u8`
  generation bumped on every clear. Hints are sequenced and can cross the reliable clear; anything older than the newest
  generation is dropped, and a clear whose generation a newer hint already established is ignored. The gateway adopts the
  first message's generation, because a reconnecting client's counter does not restart at zero.
