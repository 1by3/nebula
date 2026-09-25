# Ghosting large entities by their extent — design

Status: design of record for **NEB-334**, implemented. Decisions made without asking are marked **D#**. Protocol
stays **20**: one trailing optional section is appended to `AuthorityTransfer`, which only workers read, as NEB-309
did in alpha.32; every worker of a mesh must still run the same build. User-facing pages:
`website/content/docs/concepts/containers-and-handover.mdx` (§ Ghost a large entity by its extent) and
`website/content/docs/concepts/distributed-physics.mdx`. Conformance: scenario 28 of `docs/conformance-suite.md`
(`ConformanceEntityExtentTests`).

## 0. Problem

A worker ghosts an authoritative entity to a neighbouring worker when the entity's root, `transform.position`, is
within `GhostBandMargin` (4 m) of the seam between its container and a container the neighbour owns
(`NebulaWorker.UpdateGhostBand`, `SeamDistance`). Colliders and bounds were never considered. An entity larger than
a few metres whose root is away from the seam but whose body reaches it (a building, a player-built structure, a
large ship or vehicle, a big prop) had no copy on the neighbouring worker, so a player simulated there walked
through it.

Holoverse hits it first: an outpost's structure sections are hidden persistent entities attached with
`FrameAttachment` to a chunk of a scoped chunk grid, in the planet's local coordinates, and span up to 256 m, so one
section can cover several chunks held by several workers.

## 1. What an entity declares

**D1 An extent is a box in the entity's own space, declared on `NetworkIdentity`.** Three sources,
`EntityExtentSource`:

| Source | Box | Cost when unused |
| --- | --- | --- |
| `None` (the default) | none: the ghost band tests the root, exactly as before | one enum compare per entity per tick |
| `Explicit` | `NetworkIdentity.Extent`, authored in the inspector or set with `SetExtent` | — |
| `Colliders` | computed from the entity's own colliders (D2) | — |

The box is a `Bounds` in the root's local space (centre and size, before the root's scale; the root's scale and
rotation apply to it, like a `BoxCollider` on the root). A box can leave the root out: a structure anchored at one
corner is described as it is.

**D2 `Colliders` computes the box from the shapes, not from `Collider.bounds`.** Every enabled, non-trigger collider
on an active object under the entity whose nearest `NetworkIdentity` is this one (so a rider's colliders never count,
the same rule `PhysicsFrames` uses to copy a hull), from the collider's own shape: a box's corners, a sphere's and a
capsule's box (with Unity's rule that a non-uniform scale grows the radius by the largest axis), a
`CharacterController`'s capsule, a mesh collider's mesh bounds, and `Collider.bounds` for anything else (a terrain,
a wheel). Shapes are read rather than `Collider.bounds` because the latter is only right after the physics scene
synced its transforms, is world-axis-aligned (a rotated wall would grow into a much bigger box), and is empty for a
disabled collider on some platforms. A trigger does not block anything, so it does not widen the band; a game that
wants its trigger ghosted declares an explicit box. An entity with no such collider has no extent and is tested by
its root.

The box is computed on the authority, lazily: the first time the ghost band needs it, then again when the game
calls `RefreshExtent()`, when a direct child is added or removed (`OnTransformChildrenChanged`), and otherwise at
most every `NetworkIdentity.ColliderExtentRefreshTicks` (60) ticks. Nothing Unity raises says that a collider deeper
in the hierarchy was resized or switched on, so the periodic refresh is what makes "refreshed when they change"
true without a per-tick walk; `RefreshExtent()` makes it immediate.

**D3 The extent can change at runtime: `SetExtent`, `UseColliderExtent`, `ClearExtent`, `RefreshExtent`.** A
structure edit calls `SetExtent(newLocalBox)` on the authority. The value only matters where the entity is
authoritative (D5), so the API does not refuse a call on a ghost or before a spawn: a prefab instance can be given its
box before `SpawnServerDriven`. A call made at runtime marks the extent as the entity's own
(`ExtentChangedAtRuntime`), which is what makes it travel (D6) and persist (D7).

## 2. The ghost band

**D4 The band tests the distance from the extent to each seam, and asks the registry for every container the extent
comes near.** For an entity with an extent, `UpdateGhostBand`:

1. takes the eight corners of the box through the root's `localToWorldMatrix`. On a worker that is simulation
   space: the coordinates in which the boxes of the entity's container's space live too (D9);
2. collects candidates: `ContainerRegistry.NeighborsOf(container)`, as for every entity, plus
   `ContainerRegistry.Overlapping(box grown by the margin, instance)`. The second query is what finds a chunk that
   does not touch the entity's own chunk: a 256 m section anchored in a 64 m chunk covers chunks two and three cells
   away. Candidates in another space (container-tree D17, a hard edge), in another scope (the query is by instance
   id), carried by the entity itself (its own box, a shuttle in its hangar), or owned by this worker are skipped;
3. measures each candidate as the root test does, with the extent in place of the point
   (`NebulaWorker.ExtentSeamDistance`):
   - a sibling or an inner box: the distance between the extent and the candidate's box, computed in the candidate's
     local frame. The extent's corners are transformed there and bounded by an axis-aligned box, and the gap between
     the two boxes is measured per axis (`Container.DistanceToCorners`). Exact when the extent and the candidate are
     aligned (every chunk grid, and a structure built on it); for a rotated extent it measures the box around the
     rotated one, so it errs towards ghosting, never away from it;
   - a candidate that encloses the entity's container, or the frame around it: the seam is the container's own
     surface, so the distance is how far the extent is from leaving it, the largest signed distance of its corners
     (`Container.MaxSignedDistance`), negated as the root test negates it. An extent that reaches out of its own
     container is therefore also ghosted to the owner of the enclosing container (a planet's frame, an outdoor area
     around buildings), even where the part outside is covered by sibling containers. That errs towards ghosting, as
     the root test already does for a root near its container's surface, and costs at most one ghost per enclosing
     owner;
4. ghosts the entity to the candidate's owner when that distance is within `GhostBandMargin`, **or** when the root
   test would have. The union means an extent can only add targets: an entity with a box that leaves its root out
   still has the band it always had at its root.

The margin is the same `GhostBandMargin`: it is sized for how far something travels while a ghost spawn is in
flight, and a structure's walls are reached by a walking player in the same time as any other surface.

**D5 An extent never moves authority.** The root still decides the entity's container (`ContainerRegistry.Resolve`),
and therefore which worker simulates it and when it is handed over. A structure anchored in a chunk stays with that
chunk's worker whatever its extent covers; `FrameAttachment` pins it there anyway (frame-bodies D8). An extent
spanning a seam only means that every worker whose region it reaches holds a ghost.

**Unchanged, by construction.** Lingering: an extent adds targets to the same `_ghostTargets` table with the same
timestamps, so a target the extent no longer reaches lingers for `GhostLingerSeconds` and is then despawned, as for a
root that left the band. Carrier contents: they still follow their carrier's targets, so a large ship's extent takes
its passengers' ghosts wherever the hull's go. Cohesion groups: they are a handover rule, and an extent never causes a
handover. Scopes: the query is by instance id, so an extent never ghosts into another scope standing on the same
ground (scenario 4).

## 3. How the extent reaches the decision

The decision is made only on the authority, from the copy of `NetworkIdentity` it holds. So only the authority needs
the box, and the question is how the next authority gets it.

- **Authored on the prefab or the scene object**: every process instantiates the same box. Nothing travels.
- **Set at runtime (D6)**: `AuthorityTransferMsg` gains a trailing optional extent section (source, centre, size),
  written only when `ExtentChangedAtRuntime` is set. It follows the session section; when there is no crossing byte
  or session section to write, the writer puts their "none" values first, so an older reader still stops where it
  always did. The receiver applies it before `ReadHandoverState`, so a behaviour's `OnGainedAuthority` already sees
  the new box. `Colliders` travels as the source only: the receiver computes it from its own copy's colliders.
- **Across a restore (D7)**: `PersistentEntity` writes the runtime extent in its own `WritePersistentState` chunk
  (`PersistentEntity#state`, versioned), only when `ExtentChangedAtRuntime` is set, and reads it back before the
  restored entity spawns. A persistent entity with an authored extent, or none, writes nothing, so its records are
  byte for byte what they were, and a record from an older build has no chunk and keeps the prefab's box. A runtime
  change marks the entity dirty, so the next checkpoint saves it.
- **Not to gateways, clients or ghosts.** A client's interest is not decided by the ghost band; a large entity that
  should be seen from far away raises `RelevanceRadius`, as before. A ghost does not decide anything, and a ghost that
  becomes the authority does so through a handover, which carries the box.

## 4. What the neighbour holds

A ghost is instantiated from the prefab and posed by the authority's stream. It collides with local bodies through
the prefab's colliders and whatever the game builds on it from replicated state; a structure whose colliders are
built at runtime from its pieces must build them on a ghost too, from the `NetworkVariable`s and sync state the ghost
spawn carries. That is the game's job and was already true for any runtime-built entity; the extent only makes sure
the ghost exists wherever its body reaches.

## 5. Cost

Entities without an extent: one compare per tick, nothing else.

Per entity with an extent, per tick on its authority:

- eight point transforms for the corners;
- one `Overlapping` query: the baked cell grid (the cells the grown box covers), the runtime spatial hash (its
  buckets), the carried list (small by construction) and the moving runtime list (every runtime container inside a
  physics frame or a carrier, scanned linearly: `ContainerRegistry` keeps those out of its hash because they move);
- per candidate in the same space: eight point transforms and a box test.

A 256 m section in a 64 m planar grid has about 25 candidate chunks, so about 200 point transforms and 25 box tests
a tick; a few hundred such sections on one worker cost tens of thousands of point transforms a tick, well under a
millisecond. These are estimates from the operation counts, not measurements. The term that grows is the moving
runtime list: in a planet frame with N leased chunks, each extent entity scans N containers a tick. `Colliders`
adds one `GetComponentsInChildren` walk per entity every 60 ticks.

Not done, and the obvious next step if a profile asks for it: remember the candidate set of an extent entity that did
not move and whose neighbourhood did not change, as `NeighborsOf` already does for static adjacency.

## 6. Physics frames, floating origins and planet-local coordinates

**D8 The extent is measured in the space of the entity's container, which is where its neighbours' boxes are.** An
entity's transform on a worker is in simulation space (container-tree D11): inside a physics frame, frame-local with
the frame's floating origin taken off. The boxes of containers fixed in that frame (a planet's chunks) live under the
same frame root, so their `WorldBounds` and the corners are in one coordinate system with no conversion. When the
frame's origin shifts (container-tree D19) the root moves the entity and the boxes together, and a scoped grid's
origin shift (scope frames) moves both too; neither changes a distance. The one conversion is the root test's: an
entity directly in a container that owns a frame is inside that frame, and its corners are converted out to the
space the container's box lives in (`PhysicsFrames.Convert`) before they are measured against its neighbours.

**D9 An extent does not reach across a frame's edge.** A candidate in another space is skipped (container-tree D17):
a structure on a planet is never ghosted to the worker that holds the space around the planet by its extent, as its
root never was. A ship in the planet's frame is in the same space as the chunks, so a ship's extent works on a planet
as it does in the public world.

## 7. Conformance

| # | Scenario | Test |
| --- | --- | --- |
| 28 | A static entity with a 100 m extent rooted 50 m from a worker seam is ghosted to the neighbouring worker, and a player simulated there collides with it; the same entity without an extent is not, and the player walks through; an extent that stops 10 m short of the seam is not ghosted. An extent attached with `FrameAttachment` to a chunk of a scoped grid is ghosted to every worker whose chunk it comes within the margin of, including a chunk that does not touch its own, and to none further; `SetExtent` at runtime shrinks it and the ghost it no longer reaches lingers, then goes; the runtime extent survives a handover; the same holds in a planet's physics frame, in planet-local coordinates. | `ConformanceEntityExtentTests` |

Unit tests: `EntityExtentTests` (the box-to-box and exit distances, rotated extents, a candidate that does not touch
the entity's container, the collider computation per shape and its exclusions, the API, the persistence chunk) and
`ConformanceHandoverWireTests` (the extent section round-trips, and a message without one reads as it did).

## 8. Known limits

- The extent is a box. A long diagonal wall is bounded by the box around it and can be ghosted to a worker it does
  not reach within the margin; it is never ghosted less than it should be.
- `Colliders` sees a collider deeper than a direct child change within `ColliderExtentRefreshTicks` ticks, not at
  once. Call `RefreshExtent()` after a change that matters sooner.
- Client interest is unchanged: `RelevanceRadius` still measures from the root.
- The moving-runtime scan (§5) is linear in the chunks of a frame.
- An extent that leaves its own container is also ghosted to the owner of any container enclosing it (D4), even when
  the sibling containers there cover all of it.
