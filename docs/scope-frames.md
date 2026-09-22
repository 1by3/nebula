# Per-scope origin frames — design

Status: design of record for **NEB-241**, protocol **v18** (no message, field or version changed; see §6). Decisions
made without asking are marked **D#**. Builds directly on `docs/scoped-chunk-grids.md` (NEB-239) and closes its D12.
User-facing pages: `website/content/docs/guides/infinite-runtime-world.mdx` (§"Two worlds on one worker") and
`website/content/docs/guides/scopes.mdx`. Conformance: scenario 14 of `docs/conformance-suite.md`
(`Tests/EditMode/ConformanceScopeFrameTests.cs`, tier A, also compiled into the service tests; and
`Tests/EditMode/ConformanceScopeFrameWorkerTests.cs`, tier B).

## 0. Problem

A floating origin is not optional at planet scale, and it is Nebula's to provide: a game cannot bolt one on from
outside, because Nebula owns the container transforms, the entity poses, the state history and the interpolation
buffers that all have to move together.

Until this item a process had exactly **one** origin. `WorldOrigin.Cell` was a static, `NebulaChunkedWorld`
computed one centroid over every cell the worker leased, and `ContainerRegistry.ShiftRuntime` translated every
runtime container by that one delta. That is right for one world. It is wrong the moment one worker holds
containers of two scopes, because two scopes' coordinates have nothing to do with each other: an arena at
(0, 0, 0) and a continent at (100000, 0, 100000) cannot both be near Unity's origin, so one of them is simulated
at 6.4 million metres, where a 32-bit float has about half a metre of resolution.

The alternative — put every scope on its own worker — is the one thing the cost model cannot afford. A dozen
sparsely populated scopes would cost a dozen machines, and worker count would follow the *number of worlds*
instead of the *amount of play*. Scope count must never force worker count; that is a **non-goal stated in the
issue and enforced by a test** (§5).

## 1. What a scope frame is

```csharp
var frame = NebulaChunks.GridFor("world/arena#run-4812").Frame;   // a ScopeFrame
frame.Cell;          // which cell of THAT scope's grid sits at Unity's (0,0,0)
frame.CellSize;      // that grid's cell size
frame.ShiftCount;    // how often this scope's origin has moved
frame.Shifted += delta => { /* move anything of this scope Nebula does not know about */ };
```

**D1 `WorldOrigin` stays exactly what it was, and *is* the public world's frame.** `ScopeFrames.Public` is a
`ScopeFrame` whose `Cell`, `CellSize`, `ShiftCount` and `Shifted` are `WorldOrigin`'s, so every existing call,
every existing test and every game that never activates a scope sees no change at all — not one line of behaviour,
not one byte on the wire. The new surface (`ScopeFrame`, `ScopeFrames`, `RuntimeGrid.Frame`,
`RuntimeGrid.ShiftOrigin`) sits beside it.

**D2 A scope gets a frame of its own only when it has a grid in this process; everything else stays in the public
frame.** `ScopeFrames.FrameIdOf(instanceId)` answers "which frame do this scope's containers move with" and
returns 0 — the public frame — for every scope that has not asked for one. That is deliberate and it is what keeps
NEB-233's *instance* scopes (a room, a dungeon, an interior) behaving precisely as they did: they are placed
relative to the public origin, they move with it, and nothing about them changed. Only a **scoped chunk grid** —
a whole world, which is the thing that can be millions of metres from the public world — takes a frame.

The frame is registered by `NebulaChunks.Activate`, which is already the one place a grid comes into being in a
process, on the rule NEB-239 D7 set: the first time one of the scope's containers turns up here.

## 2. Shifting one scope

**D3 `RuntimeGrid.ShiftOrigin(coord)` moves one frame and nothing else.** The public grid's case is the old
`ShiftOriginTo`, unchanged, because the public frame also owns the authored cell scenes and the streamer has to
move those. A scoped grid has no authored scenes, so its shift is the five steps that matter, in this order:

1. suspend the enabled `CharacterController`s of **this frame's** entities (PhysX must reinsert a controller at
   the new pose, not sweep it over the shift);
2. `frame.Apply(target, delta)` — the frame moves *first*, so that
3. `ContainerRegistry.ShiftRuntime(instanceId, delta)` can ask the bounds hook
   (`NebulaChunks.BoundsOfId`, NEB-239 D14) to recompute every chunk of this grid **from its coordinate in the new
   frame** rather than translating a box and letting it drift; containers of other scopes are skipped outright;
4. `NetworkIdentity.ShiftFrameAll(instanceId, delta)` — the entities of this frame only: their transforms (for an
   entity outside every container), their `StateHistory` (§4), their `RemoteInterpolator` buffers and their
   behaviours' `OnOriginShifted`;
5. resume the controllers and `Physics.SyncTransforms()`.

Content the game parented under `ChunkContext.Root` is a child of the container and moves with it, so a game that
follows the documented pattern needs no origin-shift code of its own in a scoped world either.

**D4 Every absolute↔frame conversion takes the scope.** `ContainerRegistry.ToFrame(box, instanceId)` /
`ToAbsolute(box, instanceId)`, `NebulaWorker.Request` (the box written on the lease row),
`NebulaClient`'s container rows and the worker's `ToAbsolute(entity, …)` all convert through the entity's or
container's own frame. The no-argument overloads remain and mean the public frame, which is what every existing
caller wanted. Lease rows, telemetry and persistence therefore keep carrying **absolute** boxes, unchanged, and
two processes with different per-scope origins still agree on every one of them.

**D5 A container that arrived before its grid did is re-placed, not left behind.** A lease row can reach a
process before that scope's grid is activated here (the grid is activated *because* of it). Such a container is
registered in the public frame, because nothing here could say otherwise yet. `NebulaChunks.Activate` therefore
ends a scoped activation with `ContainerRegistry.ShiftRuntime(instanceId, Vector3.zero)`: a zero-delta shift
re-asks the bounds hook for every container of that scope, so each one is recomputed from its coordinate in its
own new frame. One pass, no special case anywhere else.

**D6 A worker follows a centroid per scope; a client keeps exactly one frame, and it is its own scope's.**
`NebulaChunkedWorld.FollowOwnedCells` now buckets the cells this worker leases by grid and calls
`KeepOriginNear` once per grid, so a worker holding two scopes keeps both precision-safe at once. A client is in
one scope at a time, so it picks the grid of the scope its pawn stands in and follows that one — which for an
unscoped game *is* the public grid, so the client's behaviour is unchanged, and for a client in scope B is B's
frame and only B's. The conformance test asserts exactly that.

## 3. Interest: scope-salted region ids

NEB-239 D12 left region ids scope-free and named the cost: a worker may send a gateway entities of a scope that
gateway has no client in, and the gateway drops them on the per-client instance check. Nothing leaked; the waste
was proportional to scopes that overlap in absolute coordinates. With per-scope frames two scopes overlapping in
absolute coordinates is no longer an accident — it is the normal case — so the cost is now paid every tick.

**D7 A region id is XORed with a strong mix of its scope's isolation id, and the public world's salt is zero.**
`RegionKeys.Salt(region, instanceId)` / `Unsalt(key, instanceId)`:

| Where | What is salted |
|---|---|
| `NebulaWorker.RegionOf(entity)` | with the entity's `InstanceId`: the worker's `InterestIndex` buckets per scope |
| `RegionPublisher` masks and groups | the keys it is handed, so a mask is per scope |
| `RegionPublisher.WideMask(grid, instanceId, …)` | unsalts each focus with the entity's scope before the distance test |
| `GatewayInterest.RegionOf(record)` | with the scope the record's carrier chain resolves to |
| `UpdateClientRegions`, `client.Regions`, `_regionClients`, the foci keys | with the client's scope |
| `ClientInterest.ScopeSalt` | the scan looks in the buckets of the client's own world |
| `WorkersForRegion(region, instanceId)` | unsalts to get the box, and asks `ContainerRegistry.Overlapping` in that scope |
| `InterestSubscribeMsg.Add/Remove/FociRegions` | carry the salted ids, which is the whole wire change |

Two properties make this work and are why XOR was chosen over a wider key:

- **It is invertible given the scope**, and every holder of a region id always knows its scope — a subscription, a
  focus and an entity each sit in exactly one. So `InterestGrid.BoundsOf` and `SqrDistanceToRegion` keep working on
  the plain packing, and none of the region arithmetic had to change.
- **The public world is the identity.** `SaltOf(0) == 0`, so an unscoped mesh produces byte for byte the
  subscription messages it produced yesterday, and `InterestPackingPinTests` still holds.

What it costs: two *different* scopes' regions could in principle collide on one 64-bit key. That is the same
class of risk NEB-239 D3 already accepted for container ids (`ScopeKeys.Hash`), at the same 2⁻⁶⁴ odds, and it fails
the way a container-id collision would — not by leaking across a boundary, because the gateway's per-client
instance check (`NebulaGateway.CanSee`) is still there underneath and is still the thing that decides what a
client sees.

## 4. State history across a shift

**D8 Recorded poses are shifted, not tagged, and entries recorded under a container are not touched at all.**
`StateHistory` (NEB-222) records a world pose plus the container the entity was in. `StateHistory.Shift(delta)`
moves only the entries whose `Container` is null; every other entry is rebuilt from its container, and the
container moved with the frame, so its pose already means the same place. Because `ShiftFrameAll` is now
scope-addressed, an entity only ever gets its **own** frame's delta. `StateAt(tick)` therefore keeps answering
with a pose that means the same place in the world before and after a shift, which is what it promises —
asserted in the tier-B conformance test.

The alternative (tag every entry with the frame's shift count and convert on read) was rejected: it would put a
branch and a multiply on the lag-compensation read path, which is the hot one, to save a walk over a 32-entry ring
that happens once per origin shift — an event that happens a few times a minute at most.

## 5. Physics, and the scaler

**D9 Physics isolation was already there; this item only had to stop breaking it.** `InstanceScenes.Prepare`
gives every isolation id its own `Scene` with `LocalPhysicsMode.Physics3D` in play mode, and
`InstanceScenes.Simulate` steps each of them; `InstanceScenes.PhysicsFor(instanceId)` is how a game raycasts in
the right world. Two scopes standing on the same ground are therefore two physics scenes and never collide, which
is exactly the property per-scope frames need. The registry queries that back gameplay
(`ContainerRegistry.Overlapping/Find/Along`) already took an isolation id (NEB-239), and `PhysicsIslands.SameIsland`
is container identity, so two scopes are never one island. Nothing was missing; the conformance test pins it.

**D10 The scaler was not changed, and that is the result.** Neither `AssignmentPolicy` nor `AssignmentPlanner` nor
`WorkerScaler` mentions a scope, an instance id or a scope key anywhere — containers are containers, and the only
thing they are weighed by is measured utilization. The dry run therefore deals two low-load scopes onto one
synthetic worker and the scaler shrinks a two-worker mesh to one, which the tier-A conformance test asserts both
ways: twelve worlds of one chunk plan exactly like one world of twelve chunks.

## 6. What did not change

- **No wire message, field or protocol version.** Protocol stays 18. `InterestSubscribeMsg` carries the same
  fields with the same encoding; a scoped mesh simply puts different 64-bit values in them, and an unscoped one
  puts the same ones it always did.
- **No persistence format.** Boxes on lease rows are absolute and stay absolute (D4).
- **No client API.** A client still has one origin and one frame (D6).
- `RuntimeGrid.PackId`, `InterestGrid.PackRegion` and their inverses are untouched and still pinned.

## 7. Not in scope

From the issue, unchanged: continuous coordinates across scopes, seamless scope-to-scope travel, curved or
spherical coordinate systems, and within-scope precision for one very large scope (players far apart on one
planet held by one worker) — that remains the existing single-frame trade-off, and the answer to it is more
workers, not more frames.

## 8. Known gaps, for later

- **Telemetry poses of scoped entities.** `WorkerTelemetry` reports pawn and carrier positions through
  `MeshTelemetry.ToAbsolute`, which uses the public origin. On a worker whose scoped frames have moved, a scoped
  entity's reported absolute position is off by that scope's offset. It is a diagnostic surface only — nothing
  routes, leases or simulates on it — and making it scope-aware means threading the entity's scope through the
  telemetry document, which belongs with whatever next touches that document.
- **A per-scope origin ring.** Every frame uses the process's `OriginShiftThresholdCells`. A scope whose cells are
  much larger or smaller than the public world's might want its own; `ChunkGridDefinition` is the place to put it.
- **The frame is dropped when the grid is deactivated** (`ScopeFrames.Remove`), which resets that scope's origin to
  cell zero if it is ever activated again in the same process. That is correct — the containers are re-registered
  from their lease rows and re-placed by D5 — but it does mean a scope that flaps takes one extra re-place.
