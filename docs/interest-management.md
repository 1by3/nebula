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
- `InterestSchedule` — whose turn it is to be evaluated on this tick: the rotation of D48.
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
  A passenger's whole *placement* is the root carrier's, not only its region (**D70**): its own `AlwaysRelevant` and
  `RelevanceRadius` are remembered for when it gets off and decide nothing while it is aboard, so no gateway is sent a
  crate because of the crate's prefab. Both sides read that answer back out of the index rather than re-deriving it
  from the prefab — the worker in `BuildPublishMasks`/`PublishMaskOf`, the gateway onto `EntityRecord.Placement`/
  `Region` after every link, place, move and removal (**D82**) — so the eviction sweep and the region → clients
  fan-out see the same placement the buckets do. Carrier links are acyclic — a link that would close a cycle is
  refused and reported where it is made (**D71**) — and a subtree walk therefore has no size cap.
- **Wide and global lists.** An entity whose relevance radius exceeds `InterestRadius` (per-prefab override, clamped to
  `InterestMaxRadius`) is not found by widening every client's region scan; it lives in a small *wide list* checked
  directly (distance from focus ≤ its radius). `AlwaysRelevant` prefabs live in the *global list*. Both exist on the
  worker (to decide which gateways hear about them) and on the gateway (to decide which clients do).
- Rebucketing happens only when the key changes: the gateway recomputes a key when it relays a state entry with a
  position change (one multiply/floor per axis), the worker once per tick per authoritative entity.
- **Scope salt (D100, NEB-241).** With per-scope origin frames (`docs/scope-frames.md`) two scopes may legitimately
  occupy exactly the same absolute coordinates on one worker, so the *key* a region is known by is the packing XORed
  with a strong mix of the scope's isolation id: `RegionKeys.Salt(region, instanceId)`. The public world's salt is
  zero, so an unscoped mesh's keys — and therefore its subscription bytes — are byte for byte what they were, and no
  message, field or protocol version changed. The salt is invertible given the scope, and every holder of a region id
  always knows its scope (a subscription, a focus and an entity each sit in exactly one), so
  `InterestGrid.BoundsOf`/`SqrDistanceToRegion` still work on the plain packing: `WorkersForRegion` and
  `RegionPublisher.WideMask` unsalt before they measure. This closes the deliberate cost recorded as
  `docs/scoped-chunk-grids.md` D12 — a worker no longer streams a gateway the entities of a scope that gateway has no
  client in. The per-client instance check (`NebulaGateway.CanSee`) is unchanged and is still what decides what a
  client actually sees; the salt makes the bandwidth match it.

## 4. Per-client interest (gateway)

- `ClientConn.Visible` becomes the interest set; `EntityRecord.Observers` (dense list of clients) is its inverse.
  **All** entity traffic — world state, netvars, SyncState, broadcast and spatial RPCs, despawns — fans out over
  `Observers`, so per-tick cost is Σ(entries × observers) ≈ clients × nearby entities. Targeted RPCs and owner state go
  to their client only if the entity is in its set (the owner's always is).
- Evaluation is bounded-rate: each client is evaluated at `InterestEvalHz` (default 4), genuinely staggered across
  ticks by `InterestSchedule`'s rotation (D48), and on the next tick when a focus crosses a region edge, its
  pawn/instance/carrier/focus mode changes, or policy marks it dirty. An
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
- Foci are capped at `InterestMaxFoci` (8). A focus is a camera, a selection, or another small spatial anchor, not
  one per owned unit — an RTS policy gives a client its camera hint plus maybe a selection focus, and represents a
  large owned army it is not looking at with an aggregate entity instead of more foci (see the user guide's "RTS and
  strategy cameras" section). Spectators = policy supplies a focus for a pawn-less client.
- **Client hints are inputs, never authority.** `ClientFocusHint` (client→gateway, unreliable): an absolute world
  position in double (D45); dropped if non-finite, rate-limited to `InterestHintMaxHz` (5), and then worth exactly what
  the server's per-client `FocusMode` says (D46, D47) — clamped to `InterestHintMaxDistance` (default 60 m) from the
  pawn, refused outright when there is no pawn to clamp to, or taken as sent for a client the server gave
  `FocusMode.Free`. An `IFocusHintPolicy` may decide per hint. Instance isolation applies in every mode.
- Per prefab, on `NetworkIdentity`: `RelevanceRadius` (0 = default), `AlwaysRelevant`, `InterestGroup`. They travel in
  `EntitySpawnMsg` (the standalone gateway has no prefabs) and are clamped by the gateway's config.

### 7.1 Installing one in the standalone gateway (`IGatewayExtension`)

`NebulaGateway.InterestPolicy` and the `SetClient…` setters are only reachable from whoever constructed the gateway.
In a real deployment that is `ServiceHost` inside `nebula-gateway`, an executable built from Nebula's sources that
holds no game code at all — so a game had the policy API and no way to use it (**D51**). A **gateway extension** is
that way: one class library, named in configuration, loaded into the gateway process.

```csharp
public interface IGatewayExtension { void Initialize(IGatewayExtensionContext ctx); void Tick(double now); void Shutdown(); }
```

- `IGatewayExtensionContext` is the gateway's server-side surface and nothing else: `SetInterestPolicy`,
  `ClientJoined`/`ClientLeft`, `SetClientTag(s)`/`GetClientTag(s)`, `SetClientFocusMode`/`GetClientFocusMode`,
  `MarkInterestDirty`/`MarkAllInterestDirty` (additive, staggered) and `RevalidateInterest`/`RevalidateAllInterest`
  (immediate revocation — **D81**), `Post(Action)`, `Option(key)`, `Config`, `GatewayId`, logging.
- Config: `GatewayExtension` (the assembly, a file name resolved next to the gateway executable or an absolute
  path), `GatewayExtensionType` (only needed when the assembly holds more than one), `GatewayExtensionOptions`
  (`key=value;key=value`, read with `Option`). Overrides: `-nebula-gateway-extension`,
  `-nebula-gateway-extension-type`, `-nebula-gateway-extension-options`, `-nebula-ext-<key>`; the orchestrator
  forwards all of them to the gateway it launches.
- **Loading is deliberate.** Nothing is scanned: one configured file, and either it yields exactly one
  `IGatewayExtension` or the gateway refuses to start. The assembly must reference the gateway's own
  `Nebula.Services.dll` (a copy of its own would be a second set of types) and must not have been built against a
  newer one. `Initialize` runs after `gateway.Initialize` and before the first tick, so a policy installed there has
  been asked about every client; a throw from it stops the process.
- **Everything after start-up is isolated.** Policy calls, client events, posted work and `Tick` are wrapped: the
  exception is logged and counted in `GatewayStats.ExtensionErrors`, and a throwing `Authorize` **denies**
  (`AuthorizeFocusHint` rejects, `Collect` keeps the foci already added). Callbacks run on the gateway loop and must
  not block; work from another thread comes in through `Post`, which is drained at the top of the loop.
- **A fleet is consistent because the configuration is.** `NebulaConfig.GatewayExtension` is exported into
  `nebula-services.json`, the extension dll sits in the build folder beside `nebula-gateway`, and that folder is what
  `nebula build` produces, what the deploy tarball packs and what every VM unpacks — so every gateway of the fleet
  loads the same extension from the same relative path.
- `Services~/Nebula.SampleExtension` is a working one (teams from `ClientJoined`, a fog-of-war filter fed from a
  watcher thread through `Post`), and it is what the tests run.

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
  containers overlapping a `NearCells` window of **every focus its evaluation authorized** — the pawn, the accepted
  focus hint, and the policy's point and box foci — plus its instance and the dynamic containers of entities in its
  set (**D60**). Ownership upserts are queued on the same reliable batch *before* the spawns that need them, so an
  entity never arrives before its container; a container is removed only after the entities in it have been despawned.
  The window is re-collected once per evaluation and not only when an entity entered or left the set, because a camera
  over empty ground changes no entity at all. Bounded by `InterestSettings.MaxContainerRows` (**D61**).
- Client content anchor: what a client keeps loaded and where its floating origin sits follows
  `NebulaClient.ActiveContentAnchor` — the local pawn by default, or a transform a game hands to
  `NebulaClient.SetContentAnchor` (**D63**).
- Validation (§9) enforces the relationships instead of documenting them.

## 9. Config (NebulaConfig + the Services mirror) and validation

New: `InterestRadius` 120, `InterestExitMargin` 16, `InterestLingerSeconds` 1, `InterestCellSize` 64,
`InterestPlanar` true, `InterestEvalHz` 4, `InterestSubscribeMargin` 32, `InterestRegionLingerSeconds` 3,
`InterestLinkLingerSeconds` 10, `InterestMaxRadius` 1024, `InterestMaxFoci` 8, `InterestHintMaxDistance` 60,
`InterestMaxExplicitPerClient` 16, `PartitionWarnEntities` 2000, `PartitionWarnFilterMs` 2. Kept:
`InterestNearRadius`, `InterestFarRadius`, divisors (LOD inside the set). Chunked world: `ChunkedWorld`,
`ChunkPlanar`, `ChunkRetireSeconds` (§10).

Gateway extension (§7.1): `GatewayExtension` (""), `GatewayExtensionType` (""), `GatewayExtensionOptions` ("").
They are not numeric knobs and are not clamped: a configured extension that cannot be loaded stops the gateway.

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

Since NEB-239 there is one grid **per scope**, not one per process (`docs/scoped-chunk-grids.md`). `NebulaChunks.Grid`
and every unqualified call still mean the public world; `GridFor(key)` and the scope-qualified overloads reach the
others, and `ChunkContext.ScopeKey` says which world a chunk belongs to. Two consequences for this document: a
client's container rows are collected in its own scope and only additionally in the public world when its scope
observes it (§4's window query is scope-qualified, failing closed both ways), and region ids stay scope-free, so a
worker may hand a gateway entities of a scope that gateway has no client in — the per-client instance check drops
them before a client hears anything. That cost is D12 of `docs/scoped-chunk-grids.md` and is revisited by per-scope
origin frames (NEB-241).

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
global / explicit), spawns/s, despawns/s, interest eval ms (avg/max), bytes per client avg/max, and
`ExtensionErrors` (a running total, not a rate: anything but zero means the game's gateway extension threw and the
gateway carried on without its answer). Worker telemetry +=
per gateway: subscribed regions, entries sent vs total, bytes sent vs unfiltered. Shown in the debug overlay and the
dashboard; the load generator reports replicas and bytes/s per client.

## 13. Tests

Unit (EditMode + Services.Tests): grid keys incl. negatives/planar/offset/alignment snap, origin-shift invariance,
carrier subtree rebucket, a carried entity taking its root carrier's placement and getting its own back when it
leaves (D70: boarding/orphaning an always-relevant crate, nested inheritance from the outermost carrier, a passenger
indexed before its ship), whole-subtree walks well past the old 4,096 cap and refused carrier cycles (D71),
what a worker actually sends each gateway link for a carried always-relevant or wide passenger
(`CarriedSubtreeInterestTests`, over `FakeWorker.SpawnLog`/`ForgetLog`) and what a gateway caches for one
(the same fixture, over `NebulaGateway.IsEntityCached`/`TryGetCachedPlacement`: an always-relevant and a wide
passenger seated in their ship's region, both evicted with the ship once the client walks out of its regions,
own placement restored on disembark, and a carrier arriving after its passengers reseating them — D82),
a whole-subtree handoff between two workers publishing no intermediate orphan for a global, a wide or a nested
passenger (D85) and a survivor of a destroyed carrier being observable and not merely cached, one level and two
levels down (D86),
wide/global lists, hysteresis + linger, policy composition + hint clamping, subscription
delta/BaseSeq/hash/resync/chunking, mask publisher, config validation, instance isolation & ObservePublic & unknown
container invariants, RuntimeGrid packing pins, gateway extensions end to end (`GatewayExtensionTests`: the real
`ServiceHost` gateway on its own thread against a hosted control plane, loading the sample extension by file name —
its policy hides the other team's units, a fog reveal posted from another thread shows them, join/leave fire, a
throwing `Authorize` shows the client nothing and is counted in the heartbeat, and a missing assembly or type stops
start-up), and handover: an owned pawn surviving authority moving to a worker
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

Public API added: `NebulaGateway.InterestPolicy`, `SetClientTag`/`GetClientTag`, `SetClientTags`/`GetClientTags`,
`SetClientFocusMode`/`GetClientFocusMode`, `MarkInterestDirty`, `MarkAllInterestDirty`,
`RevalidateInterest`, `RevalidateAllInterest` and the `FocusHints*` counters (D80, D81), the
`ClientJoined`/`ClientLeft` events and `GatewayClientInfo` (D50), `InterestSetSize`, `CachedEntityCount`,
`SubscribedRegionCount`, `WorkerLinkCount`, `IsEntityCached`, `TryGetCachedPlacement` (D82),
`InterestSettingsInUse`/`InterestGridInUse`; `InterestLinkReason`;
`InterestIndex.RootOf`, `InterestIndex.TryGetOwnPlacement` and `CarrierLink` (the return of `SetCarrier`, D70/D71);
`FocusMode`, `FocusHintDecision`, `IFocusHintPolicy`, `InterestSchedule`; `NebulaClient.FocusHint`,
`ClearFocusHint`, `AbsoluteFocusHint`, `ReplicaCount`, `SpawnsReceived`, `DespawnsReceived`;
`ContainerRegistry.Overlapping`; `NebulaConfig.ResolveWorldCellSize`; `ClientInterest.ConsiderOne`; the
`GatewayStats` interest fields (including `InterestEvalsPerSecond`) and `WorkerStats.HasGlobalEntities`.

**Wire order.** `EntitySpawnBody` gained `f16 relevance_radius, u8 interest_flags, u8 interest_group, u16 view_seq`
after `owner_identity`; `EntityDespawnBody` gained `u16 view_seq`. `ContainerOwnership` gained a leading flags
byte (`Full`) and a trailing remove list. `ClientFocusHint` is `u8 generation, u8 clear, f64 x, f64 y, f64 z`
(absolute world coordinates, D45) — the position is omitted entirely when `clear` is set.

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
  evict the record when its region falls out of the subscription, or it would drop the client's own pawn. The
  same now holds for every entity the gateway names explicitly, which includes the carriers its clients' pawns
  ride in: the worker publishes a named entity wherever it is, so an unsubscribed region says nothing about it
  (`docs/scope-activation.md` §11, D12).
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
  *(Reversed by D48: it staggers nothing, because clients evaluated together get the same next deadline.)*
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

### The focus hint, the rotation, and the server surface

- **D45. A focus hint travels as absolute world coordinates in double.** `ClientFocusHintMsg.Position` was a
  `Vector3` documented as "the client's own frame", and the gateway compared it directly with the pawn's
  *absolute* position and then used it as an absolute focus. Those are the same numbers only while the client's
  floating origin is at zero: after one shift a hint 30 m ahead of the pawn reads as a point a whole cell away,
  the clamp fires against a place the player is not looking, and the camera's regions are subscribed in the
  wrong part of the world. Three candidate fixes: absolute doubles, a pawn-relative offset, or cell + local
  offset. Absolute doubles won because the client already knows its origin exactly — `WorldOrigin.Cell` ×
  `WorldDefinition.CellSize`, the same sum `WorkerInterest.ToAbsolute` makes on the worker, and the identity
  when there is no world definition — and because it is the one space everything else in interest management is
  already in (region keys, container boxes, `InterestFocus`, `InterestClient.PawnX`). A pawn-relative offset
  would have been undefined for the pawn-less client that a spectator *is*, and would have re-introduced a
  frame — the pawn's — that moves under the hint between two sends. `NebulaClient` does the sum on the way out
  (`AbsoluteFocusHint`) and keeps its `FocusHint` property in the game's own Unity space, so no game code
  changes. A camera riding a moving carrier is resampled at `InterestHintMaxHz` like any other camera; the
  point is a place in the world, and the region margins already cover a focus moving at `InterestMaxFocusSpeed`.
- **D46. "Free focus" is a server-owned per-client mode, not a policy flag.** `InterestClient.FreeHint` had no
  functional path: the gateway always called `FocusHintFilter.TryAccept` with `freeHint: false`, so the
  documented server-authorized free camera did not exist, and the field a policy read was always false. It is
  now `FocusMode` — `PawnClamped` (default), `Free`, `Disabled` — set with `NebulaGateway.SetClientFocusMode`
  and readable by a policy as `InterestClient.FocusMode` (`FreeHint` survives as a shorthand property). A
  policy that wants the decision per hint rather than per client implements the optional `IFocusHintPolicy` on
  the same object as its `IInterestPolicy`; it is handed the raw point and the server's mode, and may narrow
  it, widen it or move the point. Nothing the client sends reaches either: a client cannot grant itself a mode,
  and cannot tell which one it has except by what it is sent.
- **D47. A pawn-less client's hint is refused, not honoured.** `TryAccept` skipped the clamp when there was no
  pawn — the one case with no distance to clamp *to* — so "connect, send a hint, read the world" worked for a
  client that had not even been given a body, which is the cheapest map scrape there is. The default mode now
  refuses a hint outright when there is no pawn, and a spectator is given `FocusMode.Free` by the server, which
  is a decision somebody made rather than one the client made by omitting a pawn. A refusal also *revokes*: a
  policy that has just said no, or a free camera whose pawn has gone, drops the hint the gateway was holding
  instead of leaving the last allowed one standing (silence still is not a clear, per D44 — the revocation
  happens on the next hint the client sends, within 1/`InterestHintMaxHz` s). Instance isolation is untouched
  by all of this: it is evaluated in `CanObserve` before any policy or focus, in every mode.
- **D48. Evaluation is a rotation, not a deadline (reversing D18).** D18's per-client next-evaluation time
  staggers nothing: the clients evaluated on the same tick are given the same next deadline and stay in
  lockstep for the rest of the session, so a gateway with a hundred clients does all its interest work on one
  tick in four and none on the other three — the loop stall that shows up as a rubber-banding spike at exactly
  `InterestEvalHz`. `InterestSchedule` replaces it with a ring and a cursor: each tick takes
  `clients × dt / interval` of the rotation (fractions carried, arrears capped at one sweep so a stalled loop
  does not come back and evaluate everybody twice), which is rate-correct at any tick rate and spreads the work
  evenly. Dirty clients — pawn, instance, carrier, focus mode, hint clear, policy — are collected *before* the
  routine slice and do not consume it, so a security-relevant change lands on the next tick; that pass is
  bounded too (`max(8, 4 × routine)` per tick, with a cursor of its own) so a change that dirties every client
  at once is spread rather than becoming the stall this replaces. Steady state allocates nothing. The rate is
  visible as `GatewayStats.InterestEvalsPerSecond`.
- **D49. What a policy is told is complete or it is not there.** `InterestSource.Describe` left
  `InterestEntity.InstanceId` at zero, so every policy filtering on the simulation scope was in fact filtering
  on "the public world" for the whole mesh. It is now resolved through the carrier chain with the same
  `ScopeContainer` walk authorization uses, so a crate in a ship in an instance reports that instance.
  `InterestClient` gained `PawnCarrierNetId` (the outermost thing the pawn is riding) and `Tags`
  (`SetClientTags`, sixty-four server-owned bits beside the existing `Team` byte), and `FocusMode`.
  `InterestEntity.InterestGroup` stays the entity's own and not its root carrier's — unlike the radius and
  always-relevance of D39 — because a filter on "what kind of thing is this" is about the passenger, not the
  ship; that is now said in the XML comment rather than left to be inferred.
- **D50. The server surface is events plus setters, on the gateway loop.** A standalone-gateway extension needs
  to know when an authenticated client arrives and leaves and to set what a policy reads, so `NebulaGateway`
  raises `ClientJoined`/`ClientLeft` with a `GatewayClientInfo` (session id, authenticated subject, name,
  instance, bot, reclaimed). `ClientJoined` is raised after the welcome and *before* the first evaluation,
  which is what makes it the place to set tags and focus mode; `ClientLeft` is raised from
  `ForgetClientInterest`, the one choke point every removal path already goes through. Both are synchronous on
  the gateway loop and a throwing handler is caught and logged, never passed on to the client. `InterestPolicy`
  documents that it belongs before the first tick and marks every client dirty when it is replaced, so a policy
  installed late still takes away what it refuses on the following ticks.

### Content for a camera that is not the pawn

- **D60. The container window follows every authorized focus, not the pawn.** `CollectNeededContainers` built
  its `NearCells` window around the pawn alone, and every other row a client got came from an entity naming
  its container. That is exactly right for a shooter, where the camera *is* the pawn, and silently wrong for
  the strategy game D46's `FocusMode.Free` exists for: a commander camera 1.3 km from its pawn is looking at
  chunks that are mostly empty, an empty chunk is named by no spawn, and so the client was never given the
  lease row it needs to build the ground under its own camera. The window is now opened around each focus the
  evaluation authorized — reading `ClientInterest.Foci`, which is the capped, post-policy, post-validation
  list, so a refused hint and a pawn-less unauthorized client contribute nothing by construction rather than
  by a second check that could disagree with the first. The rows are deduplicated where windows overlap
  (a set beside the list; foci overlap constantly and a linear scan of a few hundred ids per focus is not
  free), and the pass now also runs once per evaluation rather than only when an entity entered or left,
  because panning over empty terrain changes no entity and would otherwise never refresh anything.
  Ordering is unchanged and still holds for the new rows: the entity-dependent rows are collected first, every
  upsert is written to the client's reliable batch before the spawns of that evaluation, and removes are sent
  after the despawns — a container an entity still in `Visible` lives in is always in the needed set, so
  hysteresis and linger keep their row for as long as they keep the replica. Instance isolation is untouched:
  the window query asks `ContainerRegistry.Overlapping` for the public world only, the client's own scope
  container is added explicitly, and an id with no lease row in `_ownershipById` is never sent, so no focus
  anywhere can produce another instance's row or a container the gateway does not itself hold.
- **D61. The windows are bounded by a derived cap, not by a new setting.** Foci are game input: a policy may
  add `InterestMaxFoci` of them and a box focus may be any size, so "a window per focus" is an unbounded loop
  and an unbounded message unless something says otherwise. Two bounds, both derived from settings that
  already exist rather than a knob to get wrong. A box focus is clamped to `InterestMaxRadius` per axis as it
  enters `InterestQuery` (`BoxesClamped` reports it), which bounds *every* consumer of a focus at one choke
  point — the region subscription's `CollectBox` walked the same unbounded range. And one evaluation may
  collect at most `InterestSettings.MaxContainerRows(cellSize) = clamp((2·NearCells+1)^(2 or 3) × MaxFoci, 64,
  4096)` window rows, reported as `NebulaGateway.ContainerRowCap`, counted as `ContainerBudgetHits` and warned
  about once per client. The cap is safe because of the collection order: rows entities in the set stand in
  are gathered before it applies and are never withheld, so exhausting it can only ever cost a camera some
  empty ground, never strand a replica in a container the client does not have. Point foci are collected
  before box foci for the same reason — a point window is one near-window wide and is what a camera or an
  owned unit is, a box can legitimately be a district.
- **D62. Chunk *allocation* stays a server decision; a client focus only ever asks for what exists.** A free
  camera can now be told about any chunk the mesh holds a lease for, and deliberately cannot cause one to be
  created. `RuntimeGridAllocator` keeps its ring around the pawns a worker simulates plus the anchors the game
  added (`AddAnchor`, `NebulaChunks.EnsureAt`); nothing on the gateway's client-facing path touches it. The
  alternative — allocating around a remote focus — would let a client spend the mesh's money by panning: chunk
  allocation costs a lease, a worker's memory, content generation and simulation, and the one input driving it
  would be a rate-limited hint a client chooses. A game that wants a commander to reveal unbuilt territory
  anchors it server-side, where somebody has decided it. So a camera over never-allocated ground is sent
  nothing for it, which is correct: there is nothing there yet.
- **D63. The client's content anchor is a transform, and the pawn is the default.** Content followed
  `NebulaClient.LocalPlayer` in both drivers (`NebulaWorldStreaming` for baked cell scenes, `NebulaChunkedWorld`
  for runtime chunks), for the anchor radius and for the floating origin. A camera the gateway is now happy to
  stream for would still have been rendering unloaded terrain, in single-precision metres measured from an
  origin nobody is near. `NebulaClient.SetContentAnchor(Transform)` overrides it and null gives the pawn its
  job back; both drivers read `ActiveContentAnchor` (the override when it is set and alive, the pawn
  otherwise), so one call moves streaming, chunk origin and baked-cell origin together and every existing
  project is unchanged. It is a transform rather than "follow the focus hint automatically" because the two
  are different questions: the hint is an optional, rate-limited, *server-validated* request about what to be
  sent, while the anchor is a local decision about what to hold in memory, sampled every frame so load/unload
  hysteresis and the origin-shift threshold have something continuous to measure. A minimap ping should not
  stream the ground under it. Correctness across a shift is unaffected: hints are absolute doubles (D45) and
  are converted on the way out, so moving the origin under a camera does not move the point the gateway thinks
  anyone is watching, and the predicted pawn reconciles in the same frame as everything else — `ShiftOriginTo`
  moves them all at once and suspends `CharacterController`s across the move whether or not the pawn is what
  caused the shift.
- **D51. The gateway's policy surface needs a door, and the door is one configured assembly.** Everything in
  §7 is reachable only from whoever constructs the `NebulaGateway`, which in every real deployment is
  `ServiceHost` inside the `nebula-gateway` executable — a process built from Nebula's own sources that holds
  no game code. A game running the standalone gateway therefore had an interest policy API it could not use:
  no way to install an `IInterestPolicy`, to give a joining client its faction, or to tell the gateway that fog
  of war had changed. `IGatewayExtension` + `IGatewayExtensionContext` (§7.1) is the supported way in, and each
  of its edges is a decision.
  *Configuration, not discovery*: a directory scan would make what a gateway enforces depend on what happened
  to be lying next to it, so `GatewayExtension` names exactly one file and a failure to load it is a failure to
  start. A gateway silently running Nebula's default policy because the game's assembly was missing is a
  gateway quietly showing players what the game meant to hide — the same reason `Initialize` throwing is fatal
  while everything after it is not.
  *Isolated, and closed on failure*: after start-up every call into game code is wrapped, counted in
  `GatewayStats.ExtensionErrors` and logged (the first few, then one in a hundred, because an extension that
  throws per entity per evaluation would otherwise cost more in logging than in throwing). A throw from
  `Authorize` denies. `Authorize` is a security filter, and the only safe reading of "the filter could not
  answer" is "no" — the opposite reading turns one bug in a game's policy into a wallhack for everyone on that
  gateway.
  *One thread, and a post box*: the policy, the client events and `Tick` all run on the gateway loop, which is
  also the loop relaying every packet, so the contract is "do not block" and `Post(Action)` is the only
  thread-safe member. Fog of war is computed wherever the game computes it and arrives as a queued action,
  drained at the top of the loop before the evaluations that read its result — no lock anywhere near the
  interest path, and no partially applied reveal.
  *Fleet consistency comes from the manifest, not from the operator*: `GatewayExtension` is an ordinary
  `NebulaConfig` field, so it is exported into `nebula-services.json`, and the assembly sits in the build
  folder that `nebula build` produces, the deploy tarball packs and every VM unpacks. Every gateway started
  from one package therefore loads the same extension with the same options, which is the only way a
  per-client visibility rule can mean anything across a fleet a player may be balanced onto at random.
- **D70. A carried entity's *placement* comes from its root carrier, not only its region.** D3 and D39 said a
  passenger is bucketed with its ship and judged per client at the ship's radius and always-relevance, but the
  worker decided `Region`/`Wide`/`Global` from the passenger's own `AlwaysRelevant` and `RelevanceRadius` before
  the carrier link existed, and `InterestIndex.SetCarrier` rebucketed only region entities. So an always-relevant
  crate stayed in the global list while riding in an ordinary ship: every gateway in the mesh was sent it, cached
  it and evaluated it for ever, and the subtree no longer entered and left publication scopes as one unit —
  exactly the guarantee D3 exists for. The decision now lives in one place, `InterestIndex` (so the worker and
  the `FakeWorker` fixture cannot disagree): `Add`/`AddWide`/`AddGlobal` record an item's **own** placement, and
  while it is carried it sits exactly where its root carrier sits. Getting off restores the own placement — a
  wide or global passenger returns to its list, a region one stays in the bucket it was put down in until its
  next `Move` — and so does losing a carrier to a despawn. Everything that can change a subtree's home (place,
  move, link, detach, orphan) funnels through one `Resettle`, so there is no path that moves half a tree, and a
  passenger indexed before its ship is placed the moment the ship is added. What stays the passenger's own is
  everything non-spatial: its `InterestGroup`, its owner's gateway and any explicit subscription (the sticky
  mask), which are per entity and never inherited. D38 is unchanged (a carrier is downgraded to a region entity),
  and this is what makes it a belt-and-braces rule rather than the only thing standing between a ship and its
  stranded passengers. `CarriedTransition` follows: it captures each slot's subscriber mask *before* the change
  and resolves it after, reading global as `LinkedMask` and wide through a caller-supplied `wideMaskOf`, so a
  reclassification (global ↔ carried-region, wide ↔ carried-region) publishes the spawns and forgets it implies
  with the same carrier-before-content order as a plain rebucket — the old code wrote every non-region entity off
  as "left to the wide evaluator" and published nothing at all for it.
- **D71. Carrier cycles are refused where they are created, and the subtree walk has no cap.** `CollectCarried`
  stopped at 4,096 ids, and a root with that many direct children never reached its grandchildren: a partly
  rebucketed ship, some of it published to the destination's gateways and the rest still addressed to the
  origin's, with nothing downstream able to tell. The cap existed to bound a cycle written by mistake upstream.
  Bounding the *damage* is the wrong end of that: `SetCarrier` now walks the ancestor chain of the proposed
  carrier and returns `CarrierLink.Cycle` (with `Unchanged`, `Linked`, `Detached` and `UnknownItem` the other
  outcomes), changing nothing, and `NebulaWorker` logs a refused link once per entity. With links acyclic by
  construction the walk needs no cap: the only bound left is the size of the index, which a valid tree cannot
  exceed, so a 50,000-crate freighter moves whole. There is deliberately no subtree-size or depth limit —
  "one worker's container outgrew one worker's budget" is the partition warning of §11, not something to express
  as a silently truncated spawn list.
  *The guarantee is now true end to end (D84).* When D71 was written only `CollectCarried` had lost its cap;
  every consumer downstream of it still stopped at 8 or 16, which made the sentence above true of the index and
  false of everything that reads it. See **D84** for the audit and what each guard became.
- **D82. The gateway's cached placement is read back from the index, never re-derived from the prefab.**
  D70 moved the decision into `InterestIndex`, and the worker's side reads it back (`TryGetPlacement` in
  `BuildPublishMasks`, `PublishMaskOf`, the `FakeWorker` fixture's `SyncPlacement`). The gateway did not:
  `IndexEntity` set `EntityRecord.Placement`/`Region` from the record's *own* `AlwaysRelevant` and
  `RelevanceRadius` and then called `SetCarrier`, whose reseating it never read. Every passenger's cached row
  therefore disagreed with the index it was in, and two things that read the cache went wrong with it.
  `EvictUnsubscribedRecords` skips anything that is not a region record, so an always-relevant or wide
  passenger was never a candidate: once the region its ship rode in stopped being subscribed, the record stayed
  cached for the rest of the session — a worker deliberately sends nothing on unsubscribe (§5), so the eviction
  sweep is the *only* thing that can drop it, which is exactly the per-gateway memory D70 set out to remove.
  And `OnEntityArrived` treats a non-region record as everybody's candidate, so a crate in somebody else's ship
  cost one evaluation per connected client on arrival.
  *The fix is one direction of travel.* `IndexEntity`, `RelinkCarrier` and `RebucketIfMoved` place or link, then
  read the answer back with `TryGetPlacement` — for the record **and** its whole subtree, because a link moves
  everything riding in the item. `UnindexEntity` does the same for the passengers a removed carrier orphans, so
  a pawn left behind on an evicted ship is not still addressed to the region the ship took with it. There is
  one `ReadPlacement`, and nothing else writes those two fields.
  *Every path that can move a subtree ends there.* Boarding, disembarking and a nested carrier change arrive as
  `changedCarrier` in `OnWorldState` → `RelinkCarrier`; a carrier arriving after its passengers is
  `OnEntitySpawn` → `IndexEntity`; a plain rebucket is `RebucketIfMoved`; removal is `UnindexEntity`.
  `ConsiderCarried` pairs with them so the passengers, not just the carrier, are offered to the clients of
  wherever the subtree now is.
  *Refused links are handled, not assumed away.* `CarrierLink.Cycle` is logged once per entity (the same
  once-per-entity rule as the worker's) and changes nothing; `CarrierLink.UnknownItem` re-indexes the record
  rather than leaving a cached row no scan can reach.
  *What stays the passenger's own* is what D70 already said: `InterestGroup`, its owner, and any explicit
  per-id subscription. Only the spatial placement is inherited.
  *Tested at the gateway.* `CarriedSubtreeInterestTests` asserts on `NebulaGateway.IsEntityCached` /
  `TryGetCachedPlacement` rather than on a client's replica set, because a client-side set cannot tell a record
  that was never cached from one that is cached and filtered out per client. The eviction cases walk a client's
  pawn away instead of moving the ship: that is the one path on which the worker sends nothing at all and the
  gateway's own sweep is what has to act.

### Hardening the two server-side security edges

- **D80. A focus hint is validated, then rate limited, then shown to the game — in that order.** The pipeline
  read `Throttled` → `IFocusHintPolicy.AuthorizeFocusHint` → `TryAccept`, and `FocusHintFilter` only ever
  recorded *accepted* hints. Every hint that was going to be refused anyway — a client with no pawn, a client
  the server had set to `Disabled`, a point the game's own hint policy rejects — therefore spent nothing, so a
  client could invoke game code once per packet, and the finite-number check lived inside `TryAccept`, *after*
  that call, so the value it invoked it with could be a NaN or an infinity. Two separate faults with one shape:
  the cheap checks were behind the expensive ones.
  The order is now: generation fence → clear (still bypasses everything: narrowing interest is never an attack)
  → `Malformed` → `ThrottledAttempt` → snapshot → policy → `Authorizes` → `TryAccept`.
  *Malformed is first and has teeth.* NaN and infinity are rejected before `SnapshotClient` and before any game
  code. So is a magnitude beyond `FocusHintFilter.MaxMagnitude` (1e9 m): the grid packs
  `InterestGrid.MaxCoordinate` cells per axis — about 6.7e7 m at the default 64 m edge — so every point out
  there is the same clamped region and no camera produced it. It is **rejected, not clamped**: clamping would
  invent a place the client never asked about and then stream it, which is a worse answer than none.
  *The rate limit is on the attempt, not on the acceptance.* `ThrottledAttempt` keeps a second per-client
  clock, spent by every hint that gets as far as it whatever becomes of the hint afterwards, which is what
  bounds calls into `IFocusHintPolicy` at `InterestHintMaxHz`. The accepted clock stays as it was and still
  paces the hint itself — `TryAccept` remains self-contained for anyone using the filter directly.
  *A dropped hint still advances the generation.* The fence is about ordering, not about acceptance: leaving
  it behind when a hint is dropped would let an older hint arriving afterwards win, which is the one thing the
  sequence exists to prevent. A clear therefore still beats everything sent before it.
  *A server-side authorization resets the budget.* `SetClientFocusMode`, `SetClientTag` and `SetClientTags`
  call `FocusHintFilter.ResetBudget`. A client cannot know it has just been granted a free camera, and making
  a freshly authorized commander wait out a budget it spent while being refused would be a bug that looks like
  a dead camera. Only the server can reset it, so it opens nothing.
  *The counters say which.* `DroppedRate`, `DroppedNonFinite` + `DroppedOutOfRange` (together
  `DroppedMalformed`), `DroppedUnauthorized`, `Clamped`, `Accepted`, surfaced on the gateway as
  `FocusHintsDroppedByRate`, `FocusHintsDroppedMalformed`, `FocusHintsDroppedUnauthorized`,
  `FocusHintsClamped` and `FocusHintsAccepted`. They are gateway properties and not `GatewayStats` fields: the
  question they answer ("is one client being refused, and why") is a per-gateway debugging question, and a new
  heartbeat field costs a protocol change in `GatewayStats`, `ControlPlaneJson` and `RemoteControlPlane` for a
  number nothing aggregates yet.

- **D81. Revocation is immediate; only reveals are staggered.** D48's rotation bounds routine work, and
  `MarkAllInterestDirty` fed the same bounded dirty pass (`max(MinDirtyPerTick, routine × DirtyBurst)`, in
  practice 8 clients a tick). Everything used it, including the paths that *tighten* what may be seen — a
  replaced `InterestPolicy`, a changed team, a fog sweep — so on a gateway with more clients than the cap,
  clients past it kept replicas their policy had already refused for several ticks, and kept receiving state,
  netvars, sync state and RPCs for them. That directly contradicts `IInterestPolicy.Authorize`'s documented
  "returning false removes the entity at once". Calling that eventual consistency would have been redefining a
  security guarantee to match an implementation.
  *The split is by direction, not by caller.* `MarkInterestDirty`/`MarkAllInterestDirty` keep their meaning and
  are now documented for what they are: additive, staggered, for reveals. `RevalidateInterest(clientId)` and
  `RevalidateAllInterest()` are the revocation path and run synchronously inside the call. They also mark the
  client dirty, so a change that both tightens and loosens gets the tightening now and the loosening at the
  next evaluation — which is the right way round, because revealing late is a latency bug and revoking late is
  a security one.
  *A revocation pass is not an evaluation.* `ClientInterest.Revalidate` walks the set as it stands and re-runs
  instance scope plus `Authorize` over it: one authorization per replica the client already holds, no grid
  query, no candidate scan, no additive work, nothing allocated. It is strictly cheaper than the evaluation it
  precedes, which is why the safe behaviour could be made the default for every built-in path that tightens —
  the `InterestPolicy` setter, `SetClientTag`, `SetClientTags` — instead of being something a game has to
  remember. Losses leave through the normal `ApplyInterestChanges`, so §8's ordering holds: despawns first,
  then the container rows nothing needs any more.
  *Nothing on the per-tick path changed.* Ordinary movement and camera updates still go through the rotation;
  `RevalidateAllInterest` is for the events that tighten a rule, and its cost is about one round of
  evaluation concentrated into the call rather than spread over an eval interval.
  *`SetClientFocusMode` is deliberately not a revocation.* Dropping the hint narrows the foci, and a focus is a
  distance, not a boundary: the entities it was holding leave through the usual hysteresis.
  The sample extension shows the distinction where it matters — a fog file is replaced wholesale, so it can
  close fog as well as open it, and it now calls `RevalidateAllInterest`.

### Implicit index changes are publications too

- **D83. An implicit reseat is captured as a `CarriedTransition`, like every explicit one.** `InterestIndex`
  re-seats a subtree on its own in two places, and neither had a publication behind it. A passenger can be
  indexed *before* the carrier its container names — the crate arrives, the ship has not — so until the ship
  exists the crate sits on its own placement, and an always-relevant one is therefore sent to every gateway in
  the mesh. Adding the ship re-seats the whole pending subtree into the ship's placement (D70), and the worker
  announced only the ship: the gateways on the other side of the world were never told to forget the crate and
  cached it for the rest of the session — exactly the per-gateway memory D70 set out to remove. The mirror case
  is removal: taking a carrier out of the index orphans its passengers and gives each of them its own placement
  back, so an always-relevant crate whose ship is destroyed becomes global again and a wide one is matched on
  its own reach again, and again nothing published the spawns and forgets that implies.
  *Both are the same mechanism as a rebucket.* `CarriedTransition.Capture` now works from a root that is not in
  the index: children are remembered from the moment they name a carrier, so `InterestIndex.HasCarried` and
  `CollectCarried` answer for a missing carrier too, and the capture records the pending subtree with no slot
  for the root (which has nothing to say yet). Removal needs nothing new at all — the removed carrier's slot
  resolves to "gone", which `Resolve` already reads as equal masks, so it publishes nothing and only the
  orphans do. (Two things it did need turned out later: the removal has to be a real one — see D85 — and the
  orphan has to be standing somewhere before it is published — see D86.)
  *Order is what the extra step buys.* `NebulaWorker.InterestAddAndAnnounce` captures, indexes, announces the
  carrier, then publishes the subtree, so a gateway hearing about a passenger for the first time has already
  been given the container the passenger names. `InterestRemove` captures, removes, then publishes: forgets go
  out contents-first, and the carrier's own despawn is sent by the despawn path and is not duplicated here.
  Sticky masks are untouched in both — a gateway that owns a passenger or named it by id is never told to
  forget it. The `FakeWorker` fixture mirrors all of this, because a fixture that published a ship differently
  from a worker would be testing the fixture.
- **D84. Every carrier-chain guard is bounded by membership, not by a constant.** D71 removed the cap from
  `CollectCarried` and left eight other walks capped, so the "no depth limit" guarantee stopped at the index.
  What the constants were protecting turned out to be nothing but runaway on a cyclic chain, and a cycle is
  already refused where it is created (`CarrierLink.Cycle`, D71) — so every one of them is now bounded by the
  number of things that can be in a chain instead, which no valid tree can exceed and a corrupt one trips at
  once. `NebulaWorker.MaxNestingDepth` is gone rather than redefined; there is no constant left to disagree
  with the guarantee.
  | Guard | Was | Is |
  | --- | --- | --- |
  | `InterestIndex.DepthOf` (new) | — | Carrier hops, bounded by the index size |
  | `WorkerInterest.SendSpawnsInCarrierOrder` | depths 0..8, then the rest of the list silently dropped | Depths 0..deepest in the list, from `DepthOf` |
  | `NebulaWorker` simulation order | depths 0..8 | Depths 0..`_authoritative.Count`, leaving as soon as nothing is deeper |
  | `NebulaWorker` ghost band | depths 0..8 | The same |
  | `Container.NestingDepth` | `depth < 16` | Bounded by `ContainerRegistry.Dynamic.Count` |
  | `ClientInterest.CarrierDepth` | `depth < 16` | `InterestIndex.DepthOf` |
  | `GatewayInterest.RootOf` | `depth < 8` | Bounded by the records held |
  | `GatewayInterest.AddOf` (container rows) | `depth < 8` | The same |
  | `NebulaGateway.ScopeContainer` | `depth < 16` | The same; a chain that exceeds it resolves to null and `CanObserve` fails closed |
  | `NebulaGateway.WorldPosition`/`WorldRotation`/`ContainerPosition` | recursive, `depth > 8` | Iterative, bounded by the records held |
  *Why the caps were wrong in different ways.* A truncated spawn list is invisible: `SendSpawnsInCarrierOrder`
  cleared the whole list after eight passes, so a subscription snapshot of a deeper ship silently omitted its
  bottom and no client ever saw it. A clamped *depth* is worse than a truncated list, because
  `ClientInterest`'s sort compares depths: every level past 16 compared equal, and the ordering rule that puts
  a container ahead of its contents became whatever order the scan produced. A truncated *coordinate* resolve
  leaves a position in some intermediate ship's frame while it is used as a world position — a wrong region
  key and a wrong observation-window test, with nothing to notice it. And `ScopeContainer` is the instance
  isolation boundary: it now resolves the whole chain, and still fails closed if it cannot.
  *No new steady-state allocation.* `DepthOf` walks the index's own dictionary; the spawn ordering keeps a
  reused `List<int>` beside its reused `_spawnOrder`; `ContainerPosition` replaced its recursion with one
  reused carrier list rather than a stack frame per level.

### Leaving the index is not always leaving the world

- **D85. A whole-subtree handoff publishes no intermediate orphan.** `NebulaWorker.TransferAuthority` hands a
  carrier over first and then recursively hands over its contents, so that the receiver applies the ship before
  its passengers. Between those two steps the carrier is out of the old worker's index while its passengers are
  still in it, and D83 read that exactly as a destruction: every surviving passenger got its own placement back
  and was published on it. For an ordinary ship sailing onto the next worker that meant an always-relevant or
  wide passenger was spawned to gateways nowhere near it, which then received neither a redirect (they do not
  follow it by name or by session) nor a later forget (it is not this worker's to forget any more) and cached
  it for the rest of the session — the per-gateway memory D70 exists to remove, reintroduced by the one event
  that is supposed to be invisible.
  *A handoff is not a destruction, so it says so.* The outermost `TransferAuthority` collects the authoritative
  subtree that is about to follow — the same walk the transfer itself recurses over, stopping at a pinned
  interior — into a `HandoverScope`, and the publication skips any slot in it. Passengers that follow are
  published by the *new* owner, where the ship actually is; passengers that genuinely stay behind (a pinned
  interior, anything this worker is not the authority for) are not in the set and are orphaned exactly as a
  despawned carrier's survivors are. The set is empty outside a handover, so no steady-state path pays for it,
  and carrier-first transfer and spawn order, contents-first forget order, sticky masks, redirects and the
  late-carrier-arrival reseat (D83) are all untouched.
  *One shared scratch list could not hold this.* The recursive transfer walked `_contentsScratch` while the
  recursion refilled it. It now borrows a pooled list per level, which is also what the evacuation in D86 uses.
- **D86. A survivor is put down before its restored placement is published.** D83 gave an orphan its own
  placement back and republished it, but nothing gave it a *container*. Its wire state still named
  `ContainerRef.Dynamic(<the carrier that was just removed>)`, so the newly eligible gateway could not resolve
  it: `NebulaGateway.ScopeContainer` walks the carrier chain and returns null for a link that is not there, and
  `CanObserve` fails closed on an unresolvable container (which is the right answer — an unknown runtime
  container must never fall back to public visibility). The gateway therefore ingested the spawn, cached the
  record, evaluated it for every client and could show it to none. The republication was real and useless.
  *Removal evacuates first.* `NebulaWorker.RemoveLocal` now puts every direct passenger of a departing entity
  down in the container the entity itself sat in, before `InterestRemove` publishes anything —
  `NetworkIdentity.SetContainer` reparents with the world position kept, so the absolute pose is what survives,
  not the offset from a ship that is gone. Only the *direct* contents move: anything deeper still names a
  carrier that is there, so every spawn of a surviving subtree names an existing container and the carrier-first
  ordering keeps resolving top to bottom. Gateways that already hold the survivor learn the new frame through
  the path every container change uses — `NetworkIdentity.PrepareReplication` sees `_publishedContainer` change
  and forces a reliable `Location` entry on the next tick, which `NebulaGateway.OnWorldState` turns into a
  rebucket, a container-ownership row for the observers and a re-evaluation. Nothing about `ScopeContainer` or
  `CanObserve` was weakened: an unknown dynamic container still fails closed, there is simply no longer a
  supported way to produce one.
  *Automatic survival is the contract.* A game does not have to notice that a ship exploded and re-place what
  was inside it; a passenger that outlives its carrier is placed where the carrier was. The `FakeWorker`
  fixture mirrors both halves (no orphan publication on `HandOver`, an evacuation on `Despawn`) for the reason
  it mirrors everything else: a fixture that published a departing ship differently from a worker would be
  testing the fixture.

- **D87. The handoff bookkeeping survives an exception, and one shared component holds it.** D85's follower
  set was owned by a depth counter incremented before persistence, serialization, the network sends, the
  `AuthorityHandedOff` callback and the recursive transfers, and decremented only on the happy path. Any of
  those can throw — a persistence store that cannot reach its database, a game's handover subscriber, a nested
  transfer — and the worker was then left permanently *inside* a handoff: a nonzero depth, so the next
  top-level handoff skipped its own collection, and a stale follower set, so it suppressed the wrong entities
  and republished its real passengers as temporary orphans. The very failure that made the first ship's
  handoff go wrong silently corrupted every ship after it. The pooled contents snapshot leaked the same way:
  a nested transfer that threw never gave its list back.
  *A scope with a frame, not a counter.* `HandoverScope` (in `Runtime/Interest`, pure C#) holds the depth and
  the follower set, and a transfer takes a `Frame` from it in a `using`; disposing the outermost frame clears
  the set, on the exceptional path as much as on the ordinary one. `TransferAuthority` is now the scope
  wrapper and `TransferAuthorityInScope` the work, so the body reads as before; the borrowed contents
  snapshot is returned in a `finally`, as is the evacuation's. Nothing catches: the original exception
  propagates untouched, because a handoff that failed half way is the caller's problem and hiding it would
  only move the bug.
  *The suppression lives in one place, where a test can reach it.* `CarriedTransition.Resolve` takes the scope
  and collapses a follower's transition to "nothing changed" — it still resolves the placement, so the stale
  wide-mask sweep is unaffected — instead of each host filtering its own publish loop. `NebulaWorker` and the
  `FakeWorker` fixture now both get their suppression from that one method, which is what makes it testable:
  `HandoverScopeTests` drives the production types directly, and `WorkerHandoverTests` drives a real
  `NebulaWorker` (its own `TransferAuthority`, `InterestRemove` and `RemoveLocal`) over a recording transport
  with one gateway link, so deleting the suppression, the evacuation, the `using` or the `finally` each fails
  a test rather than only the fixture's copy of the idea.
