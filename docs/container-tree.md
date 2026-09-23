# One container concept, and physics frames — design

Status: design of record for **NEB-264**, implemented. Decisions made without asking are marked **D#**. Protocol
stays **18**: every wire change is additive (a trailing optional field or section read with `Remaining > 0`, see
`docs/compatibility-policy.md` D3). User-facing pages: `website/content/docs/concepts/containers-and-handover.mdx`
(§ The container tree), `website/content/docs/guides/physics-frames.mdx`, `guides/runtime-containers.mdx`
(§ Place a container inside another), `guides/dynamic-containers.mdx` (§ Lease a dynamic container). Conformance:
scenarios 20–23 of `docs/conformance-suite.md`.

## 0. Problem

Until this item Nebula had three kinds of container with three sets of rules:

| Kind | Where the box is | Who simulates what is inside | Where it comes from |
| --- | --- | --- | --- |
| baked | fixed in the world | its own lease | the scene or the world manifest |
| runtime | fixed, always a root of its scope, absolute `Vector3` bounds on the lease row | its own lease | `RequestRuntimeContainer` |
| carrier (`DynamicContainer`) | moves with its carrier entity | the carrier's worker (or a pinned worker) | a prefab |

They could not be combined. A carrier could hold another carrier but never a leased container; a leased container
was always a root with absolute float bounds (about 6 cm of error at 1,000 km, about 1 m at 10,000 km); and
everything inside a carrier was simulated by one worker, in the outer physics scene, with colliders moving under
it. A planet modelled as a carrier was one worker for the whole planet, and an interior leased to a worker other
than the hull's simulated against a pose that arrived one replication delay late.

## 1. One container, three properties

**D1 `Container` is the only type, described by three independent properties.**

| Property | API | Values |
| --- | --- | --- |
| Frame: where the box is | `Container.FrameMode` | `Fixed` (placed relative to its parent) · `Entity` (driven by a carrier entity, what `DynamicContainer` makes) |
| Authority: who simulates what is inside | `Container.Authority` (authored), `ResolvedAuthority`, `IsLeased` (effective) | `Leased` (its own lease row and owner) · `Inherited` (whoever owns the parent) · `Auto` (the default: leased for a fixed frame, inherited for an entity frame, which is what the three kinds did) |
| Source: where it comes from | `Container.Source` | `Baked` · `Runtime` · `Prefab` |

A fourth, orthogonal switch is `Container.OwnPhysicsFrame` (§3). `IsDynamic` and `IsRuntime` remain as shorthands
for `FrameMode == Entity` and `Source == Runtime`, because hundreds of call sites read them. Wire naming
(`ContainerRef`) still follows the source: it is what makes a container nameable on a process before it exists
there. `Auto` exists because the field is serialized: existing prefabs and scenes have no value for it and must
keep today's behaviour.

**D2 Containers form a tree; the root is the scope.** Every container has a `Parent` (null for a root):

- a **baked** container's parent is the smallest baked container of its scope that encloses it, computed once when
  the registry loads (`AssignBakedParents`); the boxes are identical on every process, so every process builds the
  same tree. The services mirror (`ServiceManifest.ContainerRegistry.Load`) builds it the same way;
- a **runtime** container's parent is named on its lease row (`LeaseInfo.ParentId`, D5). A row whose parent is not
  resolvable here yet is held (`ContainerRegistry.PendingRuntimeCount`) and registered when the parent arrives,
  from `RegisterRuntime` or `RegisterDynamic`. A parent that leaves puts its runtime children back on hold;
- a **carried** container's parent is the container its carrier stands in, so it changes as the carrier moves.

`Container.Depth` is the length of the parent chain, `Children` the fixed children (carried children are found
through the entities standing in the container). `ScopeRoot` walks `Parent`. Every walk is bounded by
`ContainerRegistry.ChainBound`, the number of registered containers.

**D3 Resolution picks the deepest container that holds the point, then the smallest.**
`ContainerRegistry.Find` keeps its broad phases (the baked cell grid, the runtime hash, the carried hash) and
ranks the containing candidates by tree depth, smaller volume breaking ties. For properly nested boxes that is
exactly the old smallest-volume answer; where it differs (a box registered as a child that is larger than its
parent where they overlap) the tree is right and volume was not. Hysteresis (`Resolve`) is unchanged. Runtime
containers that can move without re-registering (fixed children of a carrier, anything inside a frame) are kept
out of the spatial hash in a list scanned linearly, like carried containers.

**D4 "Never inside your own subtree."** `Container.IsCarriedBy` walks `Parent`, not only carriers, and compares the
carrier by net id as well as by reference. So a fixed room registered in a ship's frame is as much the ship's as a
shuttle in its hangar, and a copy of the same entity on another worker of an in-process test mesh counts as the
same entity.

## 2. Authority and lease rows

**D5 Lease rows carry the parent id and local bounds; only roots carry a precise placement.** `LeaseInfo` gains
`ParentId`, `Authority`, `OwnPhysicsFrame` and `FrameInterest`, and its centre becomes `Center`, a `Double3`. For a
child it is local to the parent (small, so the doubles only transport it), for a root it is absolute. The process
turns a root into its frame in double (`ContainerRegistry.ToFrame(Double3, ulong)`, `ToAbsolutePrecise`) and
narrows only the remainder to float, so a root 10,000 km out lands within a centimetre (scenario 22).
`ScopeFrame.OriginOffsetPrecise` gives the scope's origin in double for the same reason (it returns three doubles:
the `Nebula.World` assembly cannot see `Double3`). `ContainerPlacement` is the value type games pass and rows
carry; a `Bounds` converts to a root placement, so every call that took an absolute box still compiles.
`BoundsCenter` remains as a float view of `Center`.

**D6 Authority.**

- **Inherited:** `OwnerWorkerId`, `OwnerWorkerIndex` and `LeaseEpoch` are the parent's. For a carried container the
  parent side is the carrier's authority, as before. A runtime row may be inherited: it only carries the box and the
  parent, sits in the new lease state `inherited`, and the orchestrator never deals it.
- **Leased:** the container's own row, dealt by the planner, with cost telemetry and capacity like any other. A
  carried container authored `Leased` gets a row (`EnsureContainer(id, Leased, …)`) pinned to the worker holding its
  carrier when it appears, and the orchestrator re-deals it to the least loaded eligible worker, instead of
  unpinning it, when that worker leaves.
- **Handover** is unchanged: an entity whose resolved container's owner is another worker is handed to it. Re-dealing
  an octant moves everything in its inherited children with it, because their owner is derived, not stored.
  `Container.CollectContents` opens inherited fixed children too, so emptying a box takes what stands in its
  inherited rooms; a leased child is its owner's.

**D7 The rule: a leased container sits only under a fixed parent or under a parent with its own physics frame.**
`Container.InMovingSpace` walks up from the parent: a framed container ends the walk (safe), a carried one before a
frame means the box moves. A leased container in moving space is simulated as inherited (`IsLeased` false,
`AuthorityDemoted` true) until it is somewhere allowed; `RegisterRuntime` logs when it registers one. Checked on
read rather than refused at registration, because a carried container's parent changes as its carrier moves.

**D8 Planner and persistence stay per container.** The orchestrator hands the policies only leased baked and
runtime containers; `ComputeRuntimeAssignment` skips inherited rows. Persistence records name the container id as
before; a nested runtime container restores once its row and its parent's are here. A retiring runtime container
takes its inherited children's rows with it (`ReleaseRuntimeContainer`); a leased child keeps its row and waits.

**D9 `RuntimeGrid`/`RuntimeGridAllocator` stay.** A chunk grid is one way to lay out leased root containers.
Nothing in them changed except that their rows carry `Center` in double.

## 3. Physics frames

**D10 A container can own a physics frame: a local physics scene in which it stands still.**
`Container.OwnPhysicsFrame` (authored, or `ContainerPlacement.WithPhysicsFrame` for a runtime container). The frame
(`PhysicsFrame`, managed by `PhysicsFrames`) is a Unity scene created with `LocalPhysicsMode.Physics3D` and one root
object, the **frame root**. `Container.ContentRoot` is the frame root, so entities and fixed child containers of the
framed container hang under it and their local pose is container-local either way. `Container.Space` is the nearest
framed ancestor (the space a box lives in), `InnerSpace` the space what it holds lives in. Container-local "down" is
Unity's "down" in the frame, so gravity, rigidbodies and character controllers work in the frame's own up with no
game code. In the Editor outside play mode a test installs `PhysicsFrames.SceneFactory` (preview scenes have their
own physics); with none, the root lives in its owner's scene and only physics isolation is lost.

**D11 Simulation space and render space.** On a worker every frame root stays at its floating origin (D19) with no
rotation: a worker never renders, and a frame's inside never depends on where the frame is. On a client the frame
roots are posed at the carrier's interpolated pose, outermost first (`PhysicsFrames.PoseForRender`, at the end of
`NebulaClient.Update`), so everything after it, cameras included, sees one world. Around the prediction step and a
reconcile replay, the local pawn's frame root is put back at the identity pose (`BeginSimulation`/`EndSimulation`),
so a predicted pawn simulates exactly as its worker does. The contract for game code: **`NetworkTick` runs in
simulation space; `Update`, `LateUpdate` and rendering see render space.** `NetworkIdentity.Space`, `ToScope`,
`FromScope`, `PlaceInScope`, `PhysicsFrames.Convert`, `ConvertVelocity` and `InSimulationPose` convert explicitly.
`NetworkIdentity.SetContainer` converts pose and velocity itself when an entity changes space on a worker (never on
a first placement, whose pose is taken in the container's space, and never on a client, whose frames are posed).

**D12 Interior colliders.** `Container.FrameContent` names a prefab (colliders and visuals, no `NetworkIdentity`)
instantiated under the frame root. Without one, `PhysicsFrames` copies the owner's colliders into the frame: every
non-trigger collider under the owner whose nearest `NetworkIdentity` is the owner's (box, sphere, capsule and mesh).
Each copy is kept at its source's pose relative to the owner and enabled with it, once per worker tick
(`SyncAllContent`), so a lowering ramp or an opening door works inside too. The carrier's own colliders stay in the
outer scene. `PhysicsFrames.SourceOf(collider)` maps a copy back to its source, for game code that looks for
components on what a raycast hit.

**D13 Scenes are pooled.** A released frame's scene is kept while it is empty and the pool is under
`PhysicsFrames.PoolSize` (8). `InstanceScenes` (one scene per private scope) keeps its API; a frame inside a private
scope gets its own scene like any other. A worker steps every frame scene after its tick (`PhysicsFrames.Simulate`).

**D14 Frame state is readable everywhere.** `PhysicsFrame.State` (`PhysicsFrameState`: position, rotation, velocity,
angular velocity, acceleration in the space around the frame; `LocalAcceleration`, `LocalAngularVelocity`,
`PointVelocity`) is sampled once per tick on every process that holds the frame, from the carrier's transform: the
authoritative pose on its owner, the interpolated one elsewhere, so a leased interior reads it one replication delay
late. Rates are finite differences over one tick. Nebula applies no fictitious forces.

**D15 Crossings go through the pose owner.** Moving between a frame and the space around it needs the frame's pose at
that tick, which only the carrier's authority knows exactly (a fixed frame's pose is known everywhere).
`ContainerRegistry.Resolve` answers across the boundary: past the frame's inner box by the hysteresis it resolves at
the converted point in the space around it; into a framed box it descends to the deepest container of that frame.
The worker then:

- crosses the entity itself when it simulates the frame's carrier (`IsPoseOwner`, by net id): `SetContainer`
  converts position, rotation and velocity (leaving adds the frame's velocity and ω × r, entering takes them off);
- otherwise hands it to the carrier's worker unconverted, flagged (`AuthorityTransferMsg.Crossing`, trailing byte).
  The receiver holds it for `CrossingHoldTicks` (30) against being handed straight back, crosses it on its next
  tick, then hands it on if the destination belongs to another worker. One extra handover, no error.

`NebulaWorker.FrameCrossings` and `CrossingHandoffs` count both.

**D16 Crossing hooks.** `PhysicsFrames.CrossingPolicy` (`IFrameCrossingPolicy`) is asked on the pose owner before
every crossing and answers `Allow`, `Veto` (the entity stays in its container; the game keeps it on its side) or
`Defer` (ask again next tick). A throwing policy allows. The default allows everything.

**D17 Hard edge, by design.** An entity belongs to exactly one frame and switches at the hysteresis threshold. A ship
half inside a hangar is either in the hangar's frame or not. Raycasts do not pass between frames.

## 4. Per-frame-root origins and interest

**D18 A frame root can be a region space.** With `Container.FrameInterest = OwnRegions` (a planet), what stands in the
frame is bucketed by its position in the frame, salted with the frame (`RegionKeys.FrameKeyOf`,
`SaltOf(instance, frameKey)`), and the frame's carrier carries nothing for interest (`WorkerInterest.CarrierOf`,
`NebulaGateway.TryCarrierOf` stop there). The default `WithCarrier` keeps the rule that carried contents are
published with their root carrier, which is right for ships and vehicles; frames in between that publish with their
carrier are converted out of. On the gateway an `EntityRecord` keeps its position in its region space
(`AbsX/Y/Z` plus `FrameKey`), `InterestEntity.Space` and `InterestFocus.Space` name the space, `ClientInterest` scans
and measures each focus in its own space only, and the gateway adds a focus at the player's position in every
space around the player's frame (`AddEnclosingSpaceFoci`), so ships overhead stay in view. The workers that hold a
frame's region are the owners of every container fixed in the frame and of the frame itself (`FrameOwners`).
Carried rows tell gateways which carriers are such frames: `EnsureContainer` carries the frame flags. A custom
interest policy must give its player focus `client.PawnSpace`.

**D19 Floating origin per frame root.** A frame's coordinates can be large. `PhysicsFrame.Origin` is the frame-local
point that sits at Unity's origin in simulation space; `PhysicsFrames.ShiftOrigin` moves the frame root (and with it
everything in the frame), moves the cached history and interpolation of the frame's entities
(`NetworkIdentity.ShiftFrameIn`) and raises `PhysicsFrame.Shifted`. Each worker calls `AutoShift` every tick: when
the mean position of what it simulates in a frame is farther than `OriginShiftThreshold` (2,048 m) from the origin,
the origin moves there, snapped to `OriginShiftStep` (1,024 m). Container-local coordinates, the wire and region
keys do not change (`SimulationToLocal`, `LocalToSimulation`, and every conversion take the root offset off). The
public world and scoped chunk grids keep `ScopeFrames`; clients never shift frames, since they render them posed.

## 5. Leased children under framed moving parents

**D20 With §3 in place, D7 permits a leased container under a moving parent that has its own frame.** The owner of the
leased child simulates it in the parent's frame, where nothing moves; the parent's pose only matters for crossings
(D15) and for clients (D11). A rotating planet is a carrier entity whose container has its own frame, `OwnRegions`
interest and leased octants (runtime containers whose `ParentId` is the planet's container); a capital ship is a
carrier whose frame holds a leased engine room. The gateway positions and buckets a runtime container fixed inside a
carrier through that carrier (`TryCarrierOf` reads the placements on the lease rows it mirrors).

## 6. Wire and storage

- `LeaseInfo`: `ParentId`, `Center` (`Double3`), `Authority`, `OwnPhysicsFrame`, `FrameInterest`; new lease state
  `inherited`. JSON fields `parent`, `center` (written at double precision), `authority` (`leased`/`inherited`),
  `frame`, `interest`; older rows read as a root, leased, with no frame.
- `EnsureRuntimeContainer(id, ContainerPlacement, workerId, instance)` and its op: `parent`, `authority`, `frame`,
  `interest`. `EnsureContainer(id, authority, ownPhysicsFrame, frameInterest)` and its op: the same three flags.
- `ContainerOwnershipMsg`: a trailing placement section after the removes (entry index, parent id, centre in double,
  authority, frame flags), written only for entries that need it. A protocol-18 reader stops before it.
- `AuthorityTransferMsg`: trailing `Crossing` byte (D15), written only when set.

## 7. Conformance

| # | Scenario | Test |
| --- | --- | --- |
| 20 | Container tree: baked parents from nesting, runtime children placed in their parent and held until it arrives, rows in any order, resolution by depth, inherited ownership following the parent, the D7 demotion, the subtree rule, contents of inherited rooms leaving with their box; lease rows and ownership messages round-trip placements | `ConformanceContainerTreeTests` |
| 21 | Physics frame: a still scene with synced collider copies, a rigidbody resting on the container's own floor with the container on its side, frame state, crossings in and out by the pose owner at 1 km/s, a non-pose-owner handing over unconverted, a walk between two owners inside a frame, veto and defer | `ConformancePhysicsFrameTests` |
| 22 | A root 10,000 km from the origin placed within a centimetre | `ConformanceContainerTreeTests.PlacementFarFromTheOriginIsExact` |
| 23 | A planet with its own frame and eight octants leased to two workers with a base leased on its own; a ship flies in and lands in the other worker's octant without error; rotation changes neither local positions nor region keys; the ship leaves through the planet's pose owner; a frame's origin follows its worker; a ship at 1 km/s with a leased engine room, crew walking between the two workers and leaving through an airlock policy | `ConformanceFramedWorldTests`, plus `FrameRegionSpaceTests` for the region keys and per-space foci |

Tier limit (B, `ConformanceMesh`): the container registry is process-wide, so of two workers' copies of one carrier
only the last registered owns the box and its frame. Scenario 21's two-worker case keeps the copies' poses in step.

## 8. Known limits

- Every process that holds a framed container holds its frame scene.
- Raycasts do not pass from one frame into another; the game casts again in the space around a frame.
- A client predicts in the frame of its own player only.
- Runtime containers inside a frame are scanned linearly, not hashed per frame.
- A client outside a planet's box does not see what stands on the planet (`OwnRegions`).
- The star-system demo's planet → space → planet lap has not been re-run on this implementation; its planets are
  still separate grid scopes joined by transfers.
