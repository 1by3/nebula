# Scoped procedural chunk grids — design

Status: design of record for **NEB-239**, protocol **v18** (one appended field on the lease row's instance blob, no
version bump). Decisions made without asking are marked **D#**. User-facing pages:
`website/content/docs/guides/scopes.mdx` (§"A scope can be a whole world") and
`website/content/docs/guides/infinite-runtime-world.mdx`. Conformance tests:
`Tests/EditMode/ConformanceScopedGridTests.cs` and `Services~/Nebula.Services.Tests/ConformanceScopedGridTests.cs`
(both `[Category("Conformance")]`, scenario 4 of `docs/conformance-suite.md`).

## 0. Problem

`NebulaChunks.Grid` was one static `RuntimeGrid` per process, and a chunk's id was `RuntimeGrid.PackId(coord)` —
three signed 21-bit axes in a 64-bit word, with no bit left over. One process, one grid, one world. That is the
whole of the turnkey chunked world, and it is why a game that wants two of anything — an instanced open area, a
second map, one procedural region per party — cannot use it: chunk (3, 0, −7) is one container, one lease row and
one persistence record, whoever asks for it.

The turnkey chunked world is the reason people reach for Nebula for open worlds. A singleton grid caps that at one
world per mesh.

## 1. What a scoped grid is

A chunk grid now belongs to a **scope key** (`docs/location-contract.md` D2: opaque, never parsed by Nebula;
`""` is the public world). One grid per scope, several per process:

```csharp
// anywhere with an IControlPlane: a matchmaker, a worker, the orchestrator, a test
NebulaChunkedWorld.ActivateGrid(controlPlane, "world/arena#run-4812", new ChunkGridDefinition
{
    CellSize = new Vector3(64, 512, 64),
    Planar   = true,
    Ring     = 2,          // 0 = the role's own interest-derived ring
    Anchor   = Vector3Int.zero,
});

var grid = NebulaChunks.GridFor("world/arena#run-4812");   // null until this process has joined it
NebulaChunks.At(position, "world/arena#run-4812");         // the chunk there, in that world
```

**D1 The singleton becomes a keyed registry, and the public world keeps every name it had.**
`NebulaChunks.Grid`, `.Allocator`, `.At(pos)`, `.CoordOf(pos)`, `.EnsureAt(pos, cb)` and `.SeedOf(coord)` all still
mean the public world, so a single-world game — which is most of them — sees no change at all, and the existing
tests that pin those behaviours were not touched. Beside them: `GridFor(key)`, `AllocatorFor(key)`, `Grids`,
`ActiveScopeKeys`, `IsActiveFor(key)`, and a scope-qualified overload of each lookup. `NebulaChunks.Activate` is still
internal; a game activates a grid through the control plane, not by constructing one.

**D2 A grid scope is a `ScopeKind.Grid` activation whose payload is the grid definition, and its only part is the
anchor chunk.** NEB-233 left `Kind` + `Payload` as the extension seam and named the missing piece: "a definition
that does not enumerate every chunk up front". A grid is unbounded, so enumerating it is not a size problem but an
impossibility. What activation creates is exactly one container — the **anchor** — which is the scope's guaranteed
spawn area and the container the gateway's routing-by-key finds (`docs/scope-activation.md` §5). Every other chunk
is leased on demand by the allocator of whichever worker a pawn of that scope is simulated on, exactly as the
public world's chunks always were.

`ScopeDefinition.Validate` is the one place that knows about the kind: a grid scope whose payload does not parse,
or whose cell size or anchor is nonsense, is refused outright rather than half-activated. That is the only reading
of `Payload` anywhere in the control plane; it stays opaque everywhere else.

## 2. The id layout — the decision the issue left open

The issue named the choice: a scope component in the runtime id (narrower coordinates) versus a scope component on
the lease row. Neither is needed.

**D3 The public scope keeps the pinned packing; a scoped grid derives its ids the way every other scope derives
its container ids.**

| Scope | Runtime id of chunk (x, y, z) | Container id |
|---|---|---|
| `""` (public) | `RuntimeGrid.PackId(coord)` — three signed 21-bit axes, unchanged | `rt_<packed>` |
| any other key | `ScopeKeys.Hash(key + "/" + "c/x/y/z")` | `ScopeKeys.ContainerId(key, "c/x/y/z")` |

`ScopeKeys.ContainerId` is NEB-233's D3, already the derivation every instance's containers use, and it is *already*
of the form `rt_<64-bit>` — a runtime container id, parsed by `ContainerRegistry.TryParseRuntimeId` with no change
at all. So a scoped chunk is an ordinary runtime container whose id happens to be a hash rather than a packing, and
two scopes at the same coordinate get disjoint ids, disjoint lease rows, disjoint persistence records and disjoint
adjacency **without spending a bit of the runtime id on a scope field and without narrowing anyone's world**.

The 21-bit axis range (±1,048,575 chunks) is what it always was, in every scope.

What this costs: a scoped id cannot be unpacked. `RuntimeGrid.IdOf(coord)` memoises both directions as coordinates
are named, and `RuntimeGrid.TryCoordOf(id)` answers from that memo — and for the public grid it is still pure
arithmetic with no memo at all. A process that was handed an id it never computed adopts it from the lease row
(§3). `RuntimeGrid.PackId` stays exactly what it was and stays public-world only; `RuntimeGridTests` pins both
layouts against literal values, because both are persisted.

**D4 Migration: there is none, and that is the point.** A row written before this item has no scope key and no part
id, so it reads as the public world (`InstanceContainerInfo.ScopeKey == ""`, the location contract's D10 default)
and its id still unpacks to the cell it always named. Nothing is rewritten, nothing is read twice, and a mesh that
never activates a grid scope produces byte for byte the lease rows it produced yesterday. The protocol-18 window
was available and was not needed for the id layout; it was needed for one appended string (§3).

## 3. How a role that never computed an id learns what it is

**D5 The chunk's coordinate is its scope part id, and the part id travels on the lease row.**
`InstanceContainerInfo` gains a trailing `string part_id`, appended after `scope_key` and read with the same
`Remaining > 0` tolerance that field uses, so an old stored row simply reads it as `""`. `ChunkKeys.PartId(coord)`
is `c/x/y/z`; `ChunkKeys.TryParsePartId` is its inverse and refuses anything else, which is how an instance's
`interior` part is told apart from a chunk by the same code.

A role that mirrors a lease row therefore has everything: the scope key says which grid, the part id says which
cell, the box says where. `RuntimeGrid.Adopt(id, partId)` fills the memo — and refuses a part id that does not
derive the id it came with, so a malformed or mismatched row cannot place a chunk somewhere it is not.

**D6 The grid definition travels in the scope row; a role with no control plane infers it from the chunk.** A
worker, a gateway and the orchestrator read `ChunkGridDefinition.Of(scope)` from the mirrored scope row. A client
has no control plane at all — only the container rows its gateway sent it — so
`ChunkGridDefinition.Infer(coord, absoluteBox)` reconstructs the geometry from a single chunk: the box's size *is*
the cell size, and a planar (column) grid is the one whose boxes are centred on y = 0 whatever the coordinate,
which no volumetric cell ever is. No new client-facing message, and no grid definition on the wire.

**D7 A process activates a scope's grid the first time one of that scope's containers turns up in it.** That is the
one rule that works on every role: a worker has the anchor dealt to it or an entity transferred into it, a client
is told about a chunk of the scope it joined, a gateway mirrors the lease. A process that never touches a scope
pays nothing for it — no grid, no allocator, no pin — which is what makes "a hundred instanced arenas" a sane thing
to ask for.

## 4. Leasing, per scope

**D8 One allocator per grid, and an allocator only ever sees its own world.** `RuntimeGridAllocator` gained three
scope rules, all of them things that would otherwise be silent cross-world damage:

- it rings only the pawns whose `InstanceId` is its grid's — a player standing at (3, 0, 7) of the arena must not
  drag the public world's chunks into being at the coordinate he happens to occupy over there;
- it retires only containers its own grid `Owns` — retiring a box it does not own would empty somebody else's
  world;
- the chunks it requests carry the scope blob (`RequestRuntimeContainer(id, bounds, instance)`, a new overload),
  so the lease row is born in the scope. The blob is cached per chunk; **the public world's chunks carry nothing,
  exactly as before**, so its rows are unchanged.

**D9 The anchor is pinned, not anchored.** `AddPin(coord)` keeps one chunk wanted without a ring around it. The
anchor must never be retired — it is where a client routed by key arrives, and `IsScopeReady` is false without it —
but a *ring* around it on every worker that has ever seen the scope would lease the same chunks everywhere. A pin
is exactly the one box that must not go.

`ChunkGridDefinition.Ring` and `.RetireSeconds` let a scope declare its own allocator policy; 0 takes the process's
own configured value, so a scope that does not care says nothing.

## 5. Interest and ghosting

**D10 A client's container rows are collected in its own scope, and both directions fail closed.** The gateway's
window query (`CollectNeededContainers` → `ContainerRegistry.Overlapping`) ran in the public world only, which was
invisible while a scope was a room you walked into: a scoped client got its own rows through the entities standing
in them. A scoped *grid* is a whole world of empty terrain, and empty terrain is only ever reached by a window. So
the window is now queried with the client's own isolation id, and additionally in the public world **only when the
client is public or its scope's `ObservePublic` is set**. A public client is never told a scope's chunks exist; a
scoped client is never told the public world's unless its scope looks out at it. The conformance test fails on
either half. One exception is made on an authority's word: the destination of a crossing a worker has prepared for the
client's own pawn is sent to that client, and only to it (`docs/scope-activation.md` §11, D14).

**D11 Ghosting and adjacency needed nothing: they were already scope-qualified.** `ContainerRegistry.Link` refuses
to make neighbours of containers with different `InstanceId`s, and every neighbour query filters the same way. Two
chunks of two scopes occupying *exactly* the same box are therefore not adjacent, so the ghost band never offers
anything across the boundary — asserted end to end on two real workers rather than assumed.

**D12 Interest region ids stay scope-free, and that is a deliberate cost.** A region id is a packing of absolute
coordinates that both ends unpack (`InterestGrid.BoundsOf`, `SqrDistanceToRegion`); salting it with the scope would
make every distance computation in the gateway and the publisher wrong unless the salt travelled separately through
`InterestIndex`, `RegionPublisher` and the subscribe message. What that would buy is bandwidth on the
**worker→gateway** link — a worker may send a gateway entities of a scope that gateway has no client in, and the
gateway drops them on the per-client instance check (`NebulaGateway.CanSee`, `InterestPolicy.InstanceId`) before
anything reaches a client. Nothing leaks; the waste is proportional to scopes that overlap in absolute
coordinates. NEB-241 gives each scope its own origin frame, which is where scope-local region arithmetic belongs;
until then this is one measured inefficiency instead of a rewrite of the interest wire.

## 6. Origin and physics

**D13 The floating origin stays the public world's, and so does the physics scene.** A worker's origin follows the
centroid of the **public** cells it leases (`NebulaChunkedWorld.FollowOwnedCells` now filters to `Grid.Owns`), and
`ContainerRegistry.ShiftRuntime` asks the bounds hook for every runtime container, so a chunk of any grid is
recomputed from its coordinate rather than translated by a delta and left to drift. Scoped content that is far from
the public origin in absolute coordinates loses float precision exactly as a scoped *instance* does today. That is
the hole NEB-241 fills, and the seams it needs are named in §8.

**D14 One bounds hook, routed by grid.** `ContainerRegistry.RuntimeBoundsInFrame` is a single static delegate, so
with several grids it can no longer be one grid's method. `NebulaChunks.BoundsOfId` routes: scoped grids are asked
first (they answer only for ids they actually named), the public grid last (every 64-bit value unpacks to one of
its coordinates), and an id no grid here knows keeps the box the caller had. `RuntimeGrid.UseAsRuntimeBounds` is
still there for a game with one grid and no scopes.

## 7. What is not in scope

From the issue, unchanged: non-Cartesian grids, terrain generation, seams between grids, and cross-scope adjacency.
Entities do not ghost, interact or see across scopes, by construction (D11) rather than by policy. Deciding which
scope a player belongs in, and when a scope should go away, remain the game's and NEB-240's.

**Interaction with the scope lifecycle (NEB-240).** A grid scope's `ScopeInfo.ContainerIds` is its anchor and
nothing else, because that is all activation created. The idle sweep therefore measures a grid scope's idleness and
occupancy on its anchor chunk alone, and retiring one drops the anchor's lease row; the chunks leased on demand
around it are retired by the allocator's own idle rule (`ChunkGridDefinition.RetireSeconds`), which is what
retires them today in the public world. A grid scope that is idle at the anchor but busy three chunks away is
therefore judged idle. Making the lifecycle aggregate over a scope's *live* containers — every lease row whose
`InstanceContainerInfo.ScopeKey` is the scope's, not only the ones on the row — is a small follow-up on top of
both items, and is the right place to fix it.

One more, stated because the code allows it: nothing stops two scopes' chunks from occupying the same absolute
coordinates, and the conformance scenario relies on it. They are different worlds that happen to be drawn on the
same graph paper.

## 8. Seams left for NEB-241 (per-scope origin frames)

The issue asked for "which scope does this container/entity belong to" to be cheap and central. It is:

| Question | Answer | Cost |
|---|---|---|
| Which scope is this container in? | `NebulaChunks.ScopeOf(container)` / `GridOf(container)` | one dictionary lookup on `Container.InstanceId` |
| Which grid named this id? | `NebulaChunks.GridOf(id)` | one lookup per active scoped grid, then the public grid |
| Which chunk of its own grid is this container? | `RuntimeGrid.TryCoordOf(container.RuntimeId)` | memo lookup; arithmetic for the public grid |
| Is this container one of mine? | `RuntimeGrid.Owns(container)` | the two above |
| Which scope is this entity in? | `NetworkIdentity.InstanceId` (its container's) | already there, unchanged |

The places NEB-241 will have to take over, named now:

- `NebulaChunkedWorld.FollowOwnedCells` and the client's `KeepOriginNear` call decide *one* origin from the public
  grid. A per-scope frame makes this per-grid, and `WorldOrigin.Cell` becomes per-scope state.
- `RuntimeGrid.CoordOf`, `CenterOf` and `BoundsOf` read `WorldOrigin.Cell` directly; they are the arithmetic that
  has to become frame-relative-to-*this grid's* origin.
- `ContainerRegistry.ShiftRuntime` shifts every runtime container by one delta (D13). Per-scope frames make it one
  delta per scope, and the bounds hook (D14) is already the per-grid seam it needs.
- `InstanceScenes.Prepare` already puts every container of an isolation id into its own physics scene, so a scoped
  grid's chunks are already in a scene of their own on a worker in play mode. Per-scope physics is therefore mostly
  a matter of simulating those scenes, not of separating them.
- D12's region arithmetic becomes tractable once a scope has its own frame.
