# One container concept, and physics frames — design

Status: design of record for **NEB-264**. Decisions made without asking are marked **D#**. Protocol stays **18**:
every wire change is additive (a trailing optional field or section read with `Remaining > 0`, see
`docs/compatibility-policy.md` D3). User-facing pages: `website/content/docs/concepts/containers.mdx`,
`website/content/docs/guides/physics-frames.mdx`. Conformance: scenarios 19–22 of `docs/conformance-suite.md`.

## 0. Problem

Until this item Nebula had three kinds of container with three sets of rules:

| Kind | Where the box is | Who simulates what is inside | Where it comes from |
| --- | --- | --- | --- |
| baked | fixed in the world | its own lease | the scene or the world manifest |
| runtime | fixed, always a root of its scope, absolute `Vector3` bounds on the lease row | its own lease | `RequestRuntimeContainer` |
| carrier (`DynamicContainer`) | moves with its carrier entity | the carrier's worker (or a pinned worker) | a prefab |

They could not be combined. A carrier could hold another carrier but never a leased container; a leased
container was always a root with absolute float bounds (≈ 6 cm of error at 1,000 km, ≈ 1 m at 10,000 km); and
everything inside a carrier was simulated by one worker, in the outer physics scene, with colliders moving under
it. A planet modelled as a carrier was one worker for the whole planet, and an interior leased to a worker other
than the hull's simulated against a pose that arrived one replication delay late.

## 1. One container, three properties

**D1 `Container` is the only type, described by three independent properties.**

| Property | API | Values |
| --- | --- | --- |
| Frame: where the box is | `Container.FrameMode` | `Fixed` (placed relative to its parent) · `Entity` (driven by a carrier entity, today's `DynamicContainer`) |
| Authority: who simulates what is inside | `Container.Authority` (authored), `Container.IsLeased` (effective) | `Leased` (its own lease row and owner) · `Inherited` (whoever owns the parent) · `Auto` (the default: leased for a fixed frame, inherited for an entity frame, which is what the three kinds did) |
| Source: where it comes from | `Container.Source` | `Baked` · `Runtime` · `Prefab` |

A fourth, orthogonal switch is `Container.OwnPhysicsFrame` (§3). `IsDynamic` and `IsRuntime` remain as
shorthands for `FrameMode == Entity` and `Source == Runtime`; wire naming (`ContainerRef`) still follows the
source, because that is what makes a container nameable before it exists on a process.

**D2 Containers form a tree; the root is the scope.** Every container has a `Parent` (null for a root):

- a **baked** container's parent is the smallest baked container of its scope that encloses it, computed once
  when the registry loads (the boxes are identical on every process, so every process computes the same tree);
- a **runtime** container's parent is named on its lease row (`LeaseInfo.ParentId`, D5); a row whose parent is
  not resolvable here yet is held and registered when the parent arrives;
- a **carried** container's parent is the container its carrier stands in, so it changes as the carrier moves.

`Container.Depth` is the length of the parent chain; `Container.Children` lists the fixed children (carried
children are found through the entities standing in the container, as before). `ScopeRoot` is the root.

**D3 Resolution picks the deepest container that holds the point, then the smallest.** `ContainerRegistry.Find`
keeps its broad phases (the baked cell grid, the runtime hash, the carried hash) and ranks the containing
candidates by tree depth, smaller volume breaking ties. For properly nested boxes that is exactly the old
smallest-volume answer; where it differs (a ship parked across the corner of a smaller room) the tree is right and
volume was not. Hysteresis (`Resolve`) is unchanged.

**D4 "Never inside your own subtree."** The carrier-cycle rule generalises: a container is refused for an entity
when it lies anywhere in the subtree of the box that entity carries (`Container.IsCarriedBy` walks `Parent`, not
only carriers). That covers a fixed room registered inside a ship's frame as well as a shuttle in its hangar.

## 2. Authority and lease rows

**D5 Lease rows carry the parent id and local bounds; only roots carry a precise placement.** `LeaseInfo` gains
`ParentId` and keeps `HasBounds`/`BoundsSize`; the centre becomes `Center`, a `Double3`. For a child it is local
to the parent's frame (small, so the doubles are only a container), for a root it is absolute. Every process
converts a root's centre to its frame in double (`ContainerRegistry.ToFrame(Double3, ulong)`) before narrowing to
float, so a root 10,000 km out is placed to well under a millimetre (acceptance, scenario 21). `ScopeFrame`
gains `OriginOffsetPrecise` for the same reason. The old `BoundsCenter` `Vector3` stays as a derived property
for callers that only need the float.

**D6 Authority.**

- **Inherited**: `OwnerWorkerId`, `OwnerWorkerIndex` and `LeaseEpoch` are the parent's. For a carried container
  the "parent side" is the carrier's authority, as before. A runtime row may be inherited (`LeaseInfo.Inherited`):
  it only carries the box and the parent id; the orchestrator never deals it and no worker reads an owner from it.
- **Leased**: the container's own row, dealt by the planner, with cost telemetry and capacity like any other.
  A carried container authored `Leased` gets a row pinned to the worker that spawned its carrier
  (`LeaseInfo.Leased` on a `label#netId` row), and the orchestrator re-deals it, instead of unpinning it, when
  that worker leaves. This generalises the pinned carrier of `docs/dynamic-worlds.md`.
- **Handover** is unchanged: an entity whose resolved container's owner is another worker is handed to it.
  Re-dealing an octant therefore moves everything in its inheriting children with it, because their owner is
  derived, not stored.

**D7 The rule: a leased container sits only under a fixed parent or under a parent with its own physics frame.**
Checked where the parent is known: `RegisterRuntime` refuses a leased child of a moving unframed parent (it logs
and registers the container as inherited), and a leased carried container whose carrier stands in such a parent
is treated as inherited until it leaves (`Container.IsLeased` is false, `Container.AuthorityDemoted` says why).
Under a moving parent without its own frame the owner would simulate against a pose one replication delay old;
§3 is what removes that.

**D8 Planner and persistence stay per container.** `ComputeAssignment` deals baked leased containers as before
and skips inherited ones; runtime rows (leased) keep the stay-with-the-requester rule; leased carried rows are
re-dealt from an ineligible worker to the least-loaded eligible one. Persistence records name the container id
exactly as before; a nested runtime container restores once its row (and its parent's) is here.

**D9 `RuntimeGrid`/`RuntimeGridAllocator` stay.** A chunk grid is one way to lay out leased root containers.
Nothing in them changed except that their rows now carry `Center` in double.

## 3. Physics frames

**D10 A container can own a physics frame: a local physics scene in which it stands still.**
`Container.OwnPhysicsFrame` (authored, or `ContainerPlacement.OwnPhysicsFrame` for a runtime container). The
frame (`PhysicsFrame`, owned by `PhysicsFrames`) is a Unity scene created with `LocalPhysicsMode.Physics3D` and
one root object, the **frame root**. Everything the container holds — its entities, its fixed child containers,
carriers parked inside it and what they hold, down to the next framed container — is parented under the frame
root, so in *simulation space* (below) positions inside the frame are container-local. Container-local "down" is
Unity's "down" in that scene, so gravity, rigidbodies and character controllers work in the frame's own up
without a line of game code.

**D11 Simulation space and render space.** On a worker every frame root stays at the identity pose: a worker
never renders, and the interior of a frame never depends on where the frame is. On a client the frame roots
are posed at the frame's composed world pose (the carrier's interpolated transform, outermost frame first) in
`LateUpdate`, so the camera sees one world; around the client's prediction step the local pawn's frame root is
put back at identity (`PhysicsFrames.BeginSimulation`/`EndSimulation`), so a predicted pawn simulates exactly as
its worker does. The contract for game code: **`NetworkTick` runs in simulation space; `Update`, `LateUpdate`
and rendering see render space.** `PhysicsFrames.ToScope(position, frame)` and `FromScope` convert explicitly.

**D12 Interior colliders.** A frame needs the container's interior geometry in its own scene. `Container.FrameContent`
names a prefab (colliders and visuals, no `NetworkIdentity`) instantiated under the frame root; without one,
`PhysicsFrames` clones the carrier's static colliders (every enabled non-trigger collider under the carrier that
does not belong to a child entity) into the frame, so a ship's floor and walls are there with no authoring. The
carrier's own colliders stay in the outer scene for the outside world.

**D13 Scenes are pooled.** A released frame's scene is emptied and kept (`PhysicsFrames.PoolSize`, default 8), so
a carrier spawning and despawning does not create and unload a Unity scene each time. `InstanceScenes` (one scene
per private scope) is the scope-root case of the same idea and keeps its API; frames inside a private scope get
their own scene like any other.

**D14 Frame state is readable everywhere.** `Container.FrameState` (`PhysicsFrameState`: position, rotation,
velocity, angular velocity, acceleration in the parent frame) is updated once per tick on every process that
holds the frame: from the carrier's authoritative motion on its owner, from the replicated stream elsewhere
(finite differences of the interpolated pose, so a leased interior sees it one replication delay late, which is
what the ticket accepts). Nebula applies no fictitious forces; the game reads the state and decides.

**D15 Crossings go through the pose owner.** Moving between a frame and its parent frame needs the frame's pose at
that tick, which only the carrier's authority knows exactly (a fixed frame's pose is known everywhere). So:

- the **pose owner** crosses an entity directly: out of the frame when it is past the inner box by the hysteresis,
  into the frame when resolution in the parent frame puts it in the frame's box; position, rotation and velocity
  are converted with the pose of this tick (velocity adds the frame's linear velocity and ω × r);
- any **other** worker that would cross an entity hands it to the pose owner instead, unconverted and flagged
  (`AuthorityTransferMsg.Crossing`, trailing field). The pose owner crosses it on arrival, then hands it on
  if the destination is leased elsewhere. One extra handover, no error.

**D16 Crossing hooks.** `PhysicsFrames.CrossingPolicy` (`IFrameCrossingPolicy`) is asked before every crossing and
answers `Allow`, `Veto` (the entity stays; the game keeps it inside or outside) or `Defer` (ask again next tick),
so a game can confine crossings to portals. The default allows everything.

**D17 Hard edge, by design.** An entity belongs to exactly one frame and switches at the hysteresis threshold. A
ship half inside a hangar is either in the hangar's frame or not.

## 4. Per-frame-root origins and interest

**D18 A frame root is a region space.** Interest region keys of an entity inside a framed container are made from
its position in that frame, salted with the frame (`RegionKeys.SaltFrame`), not from its position in scope space,
when the frame asks for it (`Container.FrameInterest = OwnRegions`); the default (`WithCarrier`) keeps today's rule
that carried contents are published with their root carrier, which is right for ships and vehicles. A planet uses
`OwnRegions`, so nothing on a rotating planet churns regions in system space. A client subscribes around its
position in its own frame and, for each enclosing `OwnRegions` frame, around its position composed into that
frame.

**D19 Floating origin per frame root.** A frame's own coordinates are container-local and can be large (a planet).
`PhysicsFrame.Origin` is a `ScopeFrame`-like origin cell per frame root; `PhysicsFrames.ShiftOrigin(frame, cell)`
moves that frame's root and nothing else. The public frame and scoped chunk grids keep `ScopeFrames`.

## 5. Leased children under framed moving parents

**D20 With §3 in place, D7 permits a leased container under a moving parent that has its own frame.** The owner of
the leased child simulates it in the parent's frame, where nothing moves; the parent's pose only matters for
crossings (D15) and for clients (D11). A rotating planet is a carrier entity whose container has its own frame,
`OwnRegions` interest and leased octants (runtime containers whose `ParentId` is the planet's container); a
capital ship is a carrier whose frame holds a leased engine room.

## 6. Wire and storage

- `LeaseInfo`: `ParentId`, `Center` (`Double3`), `Inherited`, `Leased`, `OwnPhysicsFrame` — JSON fields `parent`,
  `centerD`, `inherited`, `leased`, `frame`; older rows read as root, float centre, leased, no frame.
- `ContainerOwnershipMsg`: a trailing section after the entries with, per entry that has one, the parent id,
  the double centre and the flags. A protocol-18 client ignores it.
- `AuthorityTransferMsg`: trailing `Crossing` byte (D15).
- `ControlPlaneJson` `EnsureRuntimeContainer` op: `parent`, `centerD`, `inherited`, `frame` arguments.

## 7. Conformance

| # | Scenario | Test |
| --- | --- | --- |
| 19 | Container tree: nested leased and inherited containers, parent-local rows, resolution by depth, subtree rule | `ConformanceContainerTreeTests` |
| 20 | Physics frame: interior simulated still, local gravity, crossings through the pose owner, hooks | `ConformancePhysicsFrameTests` |
| 21 | Placement 10,000 km from the origin to sub-centimetre accuracy | `ConformanceContainerTreeTests.PlacementFarFromTheOrigin*` |
| 22 | Planet with its own frame, 8 leased octants on 2 workers, a leased base; a ship lands in the other worker's octant; a ship at 1 km/s with a leased interior, crew crossing the seam, an entity leaving through the airlock | `ConformanceFramedWorldTests` |
