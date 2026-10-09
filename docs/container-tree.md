# One container concept, and physics frames — design

Status: design of record for **NEB-264**, implemented; D22 (hosted chunk grids) for **NEB-389**. Decisions made without asking are marked **D#**. Protocol
stays **18**: every wire change is additive (a trailing optional field or section read with `Remaining > 0`, see
`docs/compatibility-policy.md` D3). User-facing pages: `website/content/docs/concepts/containers-and-handover.mdx`
(§ The container tree), `website/content/docs/guides/physics-frames.mdx`, `guides/runtime-containers.mdx`
(§ Place a container inside another), `guides/dynamic-containers.mdx` (§ Lease a dynamic container). Conformance:
scenarios 20–23, 40 and 41 of `docs/conformance-suite.md`.

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
| Frame: where the box is | `Container.FrameMode` | `Fixed` (placed relative to its parent) · `Entity` (the container sits on an entity's root, next to its `NetworkIdentity`, which carries it; D21) |
| Authority: who simulates what is inside | `Container.Authority` (authored), `ResolvedAuthority`, `IsLeased` (effective) | `Leased` (its own lease row and owner) · `Inherited` (whoever owns the parent) · `Auto` (the default: leased for a fixed frame, inherited for an entity frame, which is what the three kinds did) |
| Source: where it comes from | `Container.Source` | `Baked` · `Runtime` · `Prefab` |

A fourth, orthogonal switch is `Container.OwnPhysicsFrame` (§3). The frame mode is read from the object, never
authored (D21): an entity's own box cannot be fixed. `IsDynamic` now means "registered as carried on this process"
(the entity has spawned here), and `IsRuntime` remains the shorthand for `Source == Runtime`, because hundreds of call
sites read them. Wire naming
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

**D9 `RuntimeGrid`/`RuntimeGridAllocator` stay.** A chunk grid is one way to lay out leased root containers, and
since D22 leased children of a host container.
Nothing in them changed except that their rows carry `Center` in double.
An entity's cell (`RuntimeGrid.CoordOf(NetworkIdentity)`) comes from its container only when that is a root chunk of
the grid's scope; anywhere else, a rider aboard a carrier at any depth included, it comes from the entity's position
converted to the scope's own space (`ToScope`), because inside a frame a worker's transform is frame-local (D11). A
client follows the grid of the chunk its pawn's carrier chain ends in (`NebulaChunks.GridHolding`) (NEB-306).

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
`NebulaClient.Update`), so everything after it, cameras included, sees one world. A scope's origin shift on a client
(`RuntimeGrid.ShiftOrigin`, or any shift of the public world's origin) poses the frames again before it returns: the
carriers moved with their chunks, and an origin rule run later in the same frame must read what stands in a frame
where the shift put it, not a whole shift away. A large frame is posed about a render origin (D25). Around the prediction step and a
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
late. Linear rates are finite differences over one tick. The angular velocity is taken in double from the rotation's
vector part, over as many of the last 256 samples as it takes the frame to turn a milliradian, while that baseline
agrees with the newest tick's own reading (NEB-390): a one-tick difference of float quaternions read zero below about
2 degrees a second, and a planet turning once in a few hours turns by about 2e-6 rad a tick. A planet at 1e-4 rad/s
reads within 0.1% after about four seconds of samples; a frame that stops turning reads still at once. Nebula applies
no fictitious forces by itself; a body opts in with `FrameInertia` (`docs/frame-bodies.md` D4).

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

Crossings are decided for every entity a worker has authority over, whoever owns it: a player's pawn, a
server-driven character, a crate. Nothing on the path (`ContainerRegistry.Resolve`, `SetContainer`'s conversion, the
hand-off to the pose owner, the rider handover, the client's interpolation across a change of container) reads the
owner. Scenario 37 (NEB-360) pins that for a server-driven walker, including a handover of the carrier on the
boarding tick in either order.

**D16 Crossing hooks.** `PhysicsFrames.CrossingPolicy` (`IFrameCrossingPolicy`) is asked on the pose owner before
every crossing and answers `Allow`, `Veto` (the entity stays in its container; the game keeps it on its side) or
`Defer` (ask again next tick). A throwing policy allows. The default allows everything.

**D17 Hard edge, by design.** An entity belongs to exactly one frame and switches at the hysteresis threshold. A ship
half inside a hangar is either in the hangar's frame or not. Raycasts do not pass between frames.

**D25 A client draws a large frame about a render origin near its camera.** A frame's root hangs under a
pivot, the frame's top level. On a worker, and on a client while the frame simulates, the pivot stays at the identity.
On a client drawing the frame, `PoseForRender` puts the pivot at (T + R(S O), R, S), worked out in double, and the
root at (-O, identity) under it, where (T, R, S) is the carrier's pose. O, `PhysicsFrame.RenderOrigin`, is zero
while the render anchor is within `RenderOriginThreshold` (256 m) of the frame's own origin, and otherwise the
anchor's frame-local position snapped to `RenderOriginStep` (128 m), moved again once the anchor is the threshold
away from it. The anchor is `PhysicsFrames.RenderAnchor` when a game sets one (its camera), else the client's content
anchor (`NebulaClient.ActiveContentAnchor`).

Why: Unity composes a child's world position from the leaf up. Under a root posed at (T, R), ground 205 km from a
planet's centre was R × (205 km) + T in float, where a float is 1.6 cm apart; with the planet turning, R changes every
frame, so that rounding changed every frame and differed per object, and the ground near the camera moved 3 cm and
more against it. Under the pivot, a child's offset meets -O first, where the two cancel exactly, and only the
difference, a few hundred metres, is turned: the ground holds to a fraction of a millimetre (scenario 49).

With -O on the root, something moving inside a chunk is still composed as (its offset h in the chunk + the chunk's
offset C) in float before -O is met: (h + C) rounds at 200 km, by up to a centimetre and differently every frame as it
moves, so a ship's hull and cockpit shook against the pilot's camera by 6 mm a frame. So while cameras draw, -O moves
from the root down to the root's direct children: the root goes to zero under the pivot and each child to C - O (two
floats this close subtract exactly), and every leaf's sum stays a few hundred metres long. The shift starts as cameras
start drawing (`RenderPipelineManager.beginContextRendering`, or `Camera.onPreCull` in the built-in pipeline) and every
root and child is put back exactly when they finish (`endContextRendering`, `Camera.onPostRender`). No game code, and
nothing Nebula reads (interpolation, prediction, `NetworkIdentity.LocalPosition`, the wire), sees the shifted poses.
`PhysicsFrames.ShiftChildrenWhileDrawing = false` turns it off. A carrier standing in a frame that is drawn (a ship on
a planet) has its own frame posed at its pose composed in double from its frame-local pose, not at Unity's
`position`, which would round (h + C) the same way.

**The scene render origin.** Unity holds every object's world matrix in float, in scene coordinates, and the camera
and each object round to that grid on their own: 2 mm apart 16 to 32 km from Unity's origin, where a scope with large
origin cells can leave the camera by design. A tool held in front of the camera shook against it by that much. So
while cameras draw, `SceneRenderOrigin` also moves the scene by -Δ, Δ the leading camera's position (the main camera
when it draws) snapped to `SceneRenderOrigin.Step` (64 m), once the camera is more than `Threshold` (1,024 m) from
Unity's origin:

- what moves: the root objects of every loaded scene and of the don't-destroy-on-load scene (not screen-space overlay
  canvases), each frame's pivot, placed at its drawn pose in double less Δ rather than moved from a position already
  rounded kilometres out, every drawing camera outside those roots, and roots a game adds (`AddRoot`);
- `SceneRenderOrigin.Shifted(Δ)` is raised once it has moved, before the frames' children take their render origins,
  so a game that composes its camera in double places it at (its position - Δ) there, and offsets what a transform
  does not carry; `Restoring(Δ)` is raised before Nebula puts everything back;
- `SceneRenderOrigin.Drawing(Δ)` is raised every time cameras start drawing, after both shifts (the scene's and the
  frames' children's), with Δ zero when the scene is not moved: a game queues the draws it issues with its own world
  matrices there, reading every transform where it is drawn;
- everything is put back exactly as drawing ends, children first, so game code, physics, prediction and Nebula's own
  state never see it. Inside rendering callbacks positions are shifted, `Camera.main.transform.position` included;
  `SceneRenderOrigin.Offset` says by how much. Directional lights are unaffected by translation; point and spot
  lights, reflection probes and shadows move with the scene;
- not moved, since a transform does not carry them: world-space particles, trails and lines, statically batched
  meshes, light probes, and meshes a game draws with its own world matrices. A game with those offsets them in
  `Drawing` or `Shifted`, or sets `SceneRenderOrigin.Enabled = false`. Particles in a custom simulation space stay
  where they were simulated too: Unity places them from that space's pose at simulation time, so moving the space's
  transform for drawing does not carry them. A system that should follow simulates in local space instead;
- the draw hooks are installed whenever a client poses its frames, frames or none, so a client with no physics frames
  still gets the scene render origin. A frame whose owner was destroyed without being released is skipped. When Play
  ends in an Editor that keeps its domain, drawing ends and the render and content anchors and the scene render
  origin's handlers and extra roots are forgotten, as they are when the next session starts.

Scenario 49 holds a tool 0.4 m in front of a camera 20 km from Unity's origin, the pawn walking in a chunk 205 km out
on a turning planet, to 0.013 mm a frame (4 mm without).

The rules that keep it invisible to everything else:

- The root's world pose is the carrier's to float precision. `PhysicsFrame.RenderPosition`, `RenderRotation` and
  `RenderScale` hold it exactly, and `LocalToRender`, `LocalToRenderPrecise`, `RenderToLocal` and
  `RenderToLocalPrecise` compose frame-local points into the drawn scene in double. Game code reads these, not
  `Root.position`, which under a render origin is composed through the turned origin in float.
- `BeginSimulation` ends any drawing shift and puts the pivot at the identity before it sets the root's simulation
  pose, so a prediction step runs bit for bit as it did, never through a turned pivot; `EndSimulation` poses the frame
  again. A frame that is simulating is never shifted for drawing.
- A frame whose content stays near its own origin (a ship's) never gets one while the anchor is aboard; a frame inside
  another (a ship on a planet) is posed from its carrier as drawn through the outer frame's render origin, outermost
  first.
- `PhysicsFrame.UseRenderOrigin = false` turns it off for one frame, `RenderOriginThreshold = float.PositiveInfinity`
  for all. A root a game reparented is posed directly, as before.
- Nothing changes on the wire, in persistence or on a worker.

## 4. Per-frame-root origins and interest

**D18 A frame root can be a region space.** With `Container.FrameInterest = OwnRegions` (a planet), what stands in the
frame is bucketed by its position in the frame, salted with the frame (`RegionKeys.FrameKeyOf`,
`SaltOf(instance, frameKey)`), and the frame's carrier carries nothing for interest (`WorkerInterest.CarrierOf`,
`NebulaGateway.TryCarrierOf` stop there). The default `WithCarrier` keeps the rule that carried contents are
published with their root carrier, which is right for ships and vehicles; frames in between that publish with their
carrier are converted out of. On the gateway an `EntityRecord` keeps its position in its region space
(`AbsX/Y/Z` plus `FrameKey`), `InterestEntity.Space` and `InterestFocus.Space` name the space, `ClientInterest` scans
and measures each focus in its own space only, and the gateway adds a focus at the player's position in every
space around the player's frame (`AddEnclosingSpaceFoci`), so ships overhead stay in view (and, the other way, D23). The workers that hold a
frame's region are the owners of every container fixed in the frame and of the frame itself (`FrameOwners`).
Carried rows tell gateways which carriers are such frames: `EnsureContainer` carries the frame flags. A custom
interest policy must give its player focus `client.PawnSpace`. Carrying nothing for interest does not take the frame
out of its carrier's scope: the gateway resolves the scope, world position and axes of what stands directly in the
frame (between its hosted ground and the face of its box) through the carrier's own record (`TryFrameCarrier`), never
through a registry entry for the box, which a gateway outside every worker's process does not hold. A frame whose
carrier the gateway has no record of fails closed, as an unknown container does (NEB-401, scenario 47).

**D19 Floating origin per frame root.** A frame's coordinates can be large. `PhysicsFrame.Origin` is the frame-local
point that sits at Unity's origin in simulation space; `PhysicsFrames.ShiftOrigin` moves the frame root (and with it
everything in the frame), moves the cached history and interpolation of the frame's entities
(`NetworkIdentity.ShiftFrameIn`) and raises `PhysicsFrame.Shifted`. Each worker calls `AutoShift` every tick: when
the mean position of what it simulates in a frame is farther than `OriginShiftThreshold` (2,048 m) from the origin,
the origin moves there, snapped to `OriginShiftStep` (1,024 m). Container-local coordinates, the wire and region
keys do not change (`SimulationToLocal`, `LocalToSimulation`, and every conversion take the root offset off). The
public world and scoped chunk grids keep `ScopeFrames`; clients never shift frames, since they render them posed.
A scope's own shift leaves a frame's simulation space alone: an entity inside a frame keeps its pose, its behaviours
are not told (`OnOriginShifted`), and of its state history only the entries recorded in the scope's own space move.
A frame's shift likewise moves only the history entries recorded in that frame.

## 5. Leased children under framed moving parents

**D20 With §3 in place, D7 permits a leased container under a moving parent that has its own frame.** The owner of the
leased child simulates it in the parent's frame, where nothing moves; the parent's pose only matters for crossings
(D15) and for clients (D11). A rotating planet is a carrier entity whose container has its own frame, `OwnRegions`
interest and leased octants (runtime containers whose `ParentId` is the planet's container); a capital ship is a
carrier whose frame holds a leased engine room. The gateway positions and buckets a runtime container fixed inside a
carrier through that carrier (`TryCarrierOf` reads the placements on the lease rows it mirrors). A client interpolating
an entity across a change of container converts the older sample through both containers' poses at that sample's own
tick, read from each moving carrier's interpolation buffer, not through where they are now: a planet turning while a
ship crosses its box moved on by a render delay since, which drew the ship up to 10 cm off (NEB-392).

## 5b. Chunk grids hosted by a container

**D22 A scope can hold chunk grids hosted by containers, besides its root grid.** D9 kept grids to leased *root*
containers, so a planet that is a carrier (D20) in a system's scope had its ground leased statically, and a scope held
one grid (`NebulaChunkedWorld.ActivateGrid`). A hosted grid is a `ChunkGridDefinition` laid out in a host container's
own coordinates, the host being a container with its own physics frame (a planet's, a station's, a capital ship's).
`NebulaChunkedWorld.ActivateHostedGrid(controlPlane, scopeKey, gridKey, hostContainerId, definition)`:

- **Keys.** The grid is registered under a **grid key** of its own, distinct from the scope's key and from every
  other scope's, so `NebulaChunks.GridFor`, `EnsureAt` and `AllocatorFor` take it exactly as they take a scope key.
  A chunk's container id is `ChunkKeys.RuntimeId(gridKey, coord)`, the same derivation as a root chunk with the grid
  key in place of the scope key, so it does not depend on the host's id. The chunk belongs to the host's scope: its
  lease row's `Instance` carries the scope's id and key, what stands in it is in that scope, and the scope's lifecycle
  (`docs/scope-lifecycle.md`) checkpoints and retires it with the rest of the scope.
- **Part ids.** A hosted chunk's part id is `<gridKey>/c/x/y/z` (`ChunkKeys.HostedPartId`), whose hash is the
  container id. It travels on the lease row and in persisted records as every part id does, so a role handed only the
  row (a client, a gateway) learns the grid and the coordinate from it, and a record saved in a hosted chunk names it
  (`ChunkKeys.TryHostedCoordOf`). `ChunkKeys.TryParsePartId` and a root grid reject it, so a hosted chunk is never
  read as a root chunk of the scope.
- **Placement.** Each chunk is a leased runtime child of the host (`RuntimeGrid.PlacementOf`: `ParentId` the host,
  centre in the host's coordinates), so D7 and D20 apply unchanged: its owner simulates it in the host's frame. A
  planar grid is one layer of columns centred on the host's y = 0, and makes the host's frame keep its origin level
  (`PhysicsFrame.KeepOriginLevel`: `AutoShift` moves it across, never up or down), the rule a planar root grid keeps
  for its own origin. The grid has no origin of its own: its `Frame` follows the host frame's
  (`ScopeFrame.IsFollowing`; `OriginOffset` is where the host's (0,0,0) sits in simulation space, and the host frame's
  shifts are raised as the grid frame's), so `grid.Frame.OriginOffset` converts between the host's coordinates and
  simulation space as it converts a root grid's absolute coordinates. `ShiftOrigin` and `KeepOriginNear` do nothing
  on it, a worker's centroid rule skips it, and a client standing in a hosted chunk follows its scope's root grid,
  where the host is.
- **Geometry.** A hosted grid's shape is an `IChunkGridGeometry`: point to cell (`CellOf`, decided by the cell, not by
  which box holds the point), cell to box (`BoundsOf`, the leased container's box in the host's coordinates), the
  canonical cell (`Normalize`), the ring in steps of adjacency (`Neighborhood`, which the allocator rings and leads in),
  the container id (`IdOf`, `ChunkKeys.RuntimeId` by default; a geometry may pack its own) and the lead sampling step.
  `ChunkLattice` (planar or volumetric boxes) is built in and the default; a game supplies another, such as a
  cube-sphere's six faces, through `NebulaChunkedWorld.HostedGeometry` on every process. Cells stay named by a
  `Vector3Int` the geometry gives meaning to (a face and level can be folded into it), which is what part ids carry.
  Where neighbouring cells' boxes overlap, as on a sphere, D3's "deepest, then smallest box holding the point" would
  pick either; `ContainerRegistry.RuntimeClaims`, installed by `NebulaChunks`, lets the geometry veto a hosted chunk
  whose box holds the point but whose cell does not (`NebulaChunks.Claims`). A lattice's boxes tile, so its chunks
  skip it and resolve exactly as before.
- **Activation and propagation.** Activation is one mesh setting row, `nebula.hostedGrid/<gridKey>` =
  `{"scope","host","grid"}` (`IControlPlane.SetSetting`). No new control-plane operation and no wire change: every
  worker applies the rows twice a second (`NebulaChunkedWorld.EnsureHostedGrid`), building the grid and an allocator,
  and replaces a grid whose host or definition changed. A gateway sends a client the rows of the hosted chunks round its pawn and its foci in the host's frame, read from the lease rows in the host's coordinates (NEB-396). A client or a gateway infers the grid from a chunk's row: the
  part id gives the key and the coordinate, the parent the host, the box the cell, and a box centred on its host's
  y = 0 a column. A worker that sees a chunk before the setting row infers it the same way. A carrier that comes back
  under another container id (carried ids are `label#netId`, and a restore gives a new net id) is hosted again by
  activating with the new id. A chunk row still naming a parent that is not here is removed and requested again under
  the current host by the next allocator that wants it. A record in a hosted chunk restores into it once the chunk
  registers, which needs the host, so after the carrier's own record has restored.
- **Allocation.** `RuntimeGridAllocator` on a hosted grid reads each pawn's position in the host's coordinates
  (`RuntimeGrid.TryHostPosition`, through every frame in between with `PhysicsFrames.Convert`), counts the pawn only
  inside the host's box grown by `ChunkGridDefinition.Reach` and inside the box of the cell it is over grown by as much
  (a lattice's columns fill the host's box, so for them the second test changes nothing; a sphere's cells inside its
  bounding cube leave most of the cube empty, and a pawn high over them leases nothing), and requests nothing while the
  host is not in its process. Dealing is unchanged: a chunk row is created assigned to the worker that asked, and the orchestrator
  re-deals it like any leased runtime container. The anchor is not pinned.
- **A copy of the host wherever its ground is leased.** A worker can only register a chunk once the host's box is in its
  process, and a chunk may be dealt to a worker that owns nothing near the host. So the host's worker gives every
  worker that leases a container in the host's box, at any depth, a ghost of the host for as long as the lease is held
  (`NebulaWorker.GhostHostsToHostedLeases`, read from the rows' placements, with the ghost band on). The chunk registers
  under that copy and its owner simulates it in the copy's frame. What stands in a chunk does not follow the host's
  ghosts (it follows the chunk's own lease, and the band covers the chunk's seams), so a planet's ground is not sent
  whole to every worker that holds a copy of the planet. A handover into a container that has not registered, and a
  leased container held for a missing parent, are reported once after five seconds
  (NEB-400, scenario 48).
- **Lead.** `ChunkGridDefinition.LeadSeconds` (any grid; 0 by default, so root grids keep today's ring) adds a ring
  around every cell on the line from the pawn to where its velocity takes it in that time: a capsule along the path,
  not a wider ring. The velocity is measured from the pawn's position in the grid's absolute coordinates between two
  policy ticks, so a pilot aboard a ship leads with the ship, and a frame crossing does not disturb it. A speed above
  `RuntimeGridAllocator.MaxLeadSpeed` (2 km/s) is read as a jump and leads nothing. Scenario 41 measures the lead a
  ship gets at 300 m/s.
- **Compatibility.** No protocol bump and no persistence format change: the part id is an opaque string everywhere it
  travels. `lead` and `reach` are written into a definition only when set, so a scope row written before them reads
  back unchanged and its allocator does not take it for a changed definition.

## 5a. One component: `DynamicContainer` folded into `Container`

**D21 A container on an entity's root is carried by that entity; nothing else makes a container carried.** A
`Container` whose GameObject also has a `NetworkIdentity` is `FrameMode.Entity`; every other container is fixed. The
rule is read from the object (`Container.IsEntityObject`), so there is no switch to author and no way to fix an
entity's own box. What `DynamicContainer` did moved to where its entity lives:

- `NetworkIdentity.Initialize` caches the root's `Container` (`Carried`). `InvokeSpawn` registers it
  (`ContainerRegistry.RegisterDynamic`) after the entity was placed in the container around it and before any
  behaviour's `OnNetworkSpawn`; `InvokeDespawn` unregisters it first, evacuating the riders while the entity is still
  there; `OnDestroy` cleans up after an entity destroyed without a despawn. The same order as before for a carrier
  whose `DynamicContainer` sat before its other behaviours, which is where the component menu and every setup
  script put it.
- The Rigidbody-to-carrier map is `Container.OfRigidbody` / `Container.IsCarrierGeometry`, reset by `NebulaStatics`.
- `ContainerRegistry.Rebuild`, `ServiceExport` and `WorldBaker` skip containers on an entity's root. (`WorldBaker`
  used to bake every container in a cell scene, carriers included.)
- Placement is validated rather than required: `Container.PlacementProblem`, reported by `OnValidate` (after the
  edit, on authored objects only), the `Container` inspector and `NebulaValidator`. A container on a child object of
  an entity is a warning (it is baked as a fixed box and never moves with the entity); one on an entity's root without
  a `NetworkTransform` is an error.
- `NetworkIdentity.Carried` stays the way to reach an entity's box; `Container.Entities` replaces
  `DynamicContainer.Contents`.

`DynamicContainer` stays for one release as an `[Obsolete]` `MonoBehaviour` that does nothing. It is no longer a
`NetworkBehaviour`, so it takes no behaviour slot. **Nebula > Migrate > Remove DynamicContainer**
(`NebulaMigrate`) strips it from every prefab and open scene and names any object a script's
`[RequireComponent(typeof(DynamicContainer))]` keeps it on.

**Protocol 19, not additive.** Removing a `NetworkBehaviour` from a prefab renumbers the prefab's `Behaviours` array,
and RPCs, network variables and sync state are addressed by `BehaviourIndex`. A protocol-18 client would address the
wrong behaviour on every carrier without noticing, and there is no trailing field that could tell it. So
`HelloMsg.ProtocolVersion` and `HelloMsg.MinProtocolVersion` both moved to 19 (`docs/protocol-versions.md`): an older
client is refused with its range instead of being admitted into a world it would misread.

**D23 A client outside a framed container's box looks into it while it is near (NEB-386).** D18's enclosing foci look
outward from a frame; this is the mirror, inward. A pawn in the space around a frame with regions of its own (a
planet, a station) that is within `InterestFrameApproachMargin` (default 4,000 m, 0 turns it off) of the frame's box
gets one more focus, in that frame's region space (`InterestFocus.Space`), so `ClientInterest` scans the frame's
buckets with it: a pilot over a planet sees the outposts, ships and players standing on it, and the same rule serves a
ship looking at a station or a fighter at a capital ship's deck. The gateway adds it beside the enclosing foci
(`AddFrameApproachFoci`), for every such frame in the pawn's scope and in any of the spaces around the pawn, and skips
the frames the pawn is in or rides in, which the policy's focus and the enclosing foci already cover.

- *Where the focus is.* At the point of the frame's box nearest the pawn, in the frame's coordinates, not at the
  pawn's own place in the frame. A focus at the pawn's own place would put what stands on the planet at the pawn's true
  distance, 3 km for a pilot 3 km up, further than any entity's radius reaches (`InterestMaxRadius`, 1,024 m), so the
  planet would be invisible from exactly where a pilot wants to see it. From the box's face the pilot sees what stands
  at or near the face at the radius it asks for, as a player on the ground would from the edge of the box, and the
  margin alone bounds how far off the planet the pilot looks in. A projection onto "the ground" was not taken, because
  a frame has no surface to project onto; a game that wants the pilot to see the ground sizes the box so its top is near it.
- *Hysteresis.* A frame looked into stays looked into to the margin plus a tenth of it (at least `InterestExitMargin`),
  so a pawn hovering at the margin does not make the planet's regions come and go; the entities then linger one second
  as they always do.
- *The box.* A container the gateway's registry holds gives its own box (a baked frame; any, when a worker shares the
  process). A carried frame is not on a gateway of its own and its lease row carries no box, so the boxes of the
  runtime containers fixed in it stand for it (a planet's chunks: where there is ground, which is where anything can
  stand). With neither, the box is the frame's origin point. Giving a carried frame's lease row its box is the one change
  that would make this exact, and it is a wire change: not made here.
- *Workers.* The decision is the gateway's, and so is the one the workers act on: a gateway dials the owners of a
  frame (`FrameOwners`) because one of its clients has a focus in the frame (`LinkNearbyOwners`), subscribes the regions
  around it (`UpdateClientRegions`) and tells the workers the foci (`_fociRegions`) they match their wide entities
  with. The approach focus is an ordinary focus in all three, so no worker code changed and the wire is as it was.
- Only frames fixed in a scope or carried (a planet, a station, a ship) are looked into. A frame fixed inside another
  frame (an octant with a frame of its own) is not.

## 6. Wire and storage

- `LeaseInfo`: `ParentId`, `Center` (`Double3`), `Authority`, `OwnPhysicsFrame`, `FrameInterest`; new lease state
  `inherited`. JSON fields `parent`, `center` (written at double precision), `authority` (`leased`/`inherited`),
  `frame`, `interest`; older rows read as a root, leased, with no frame.
- `EnsureRuntimeContainer(id, ContainerPlacement, workerId, instance)` and its op: `parent`, `authority`, `frame`,
  `interest`. `EnsureContainer(id, authority, ownPhysicsFrame, frameInterest)` and its op: the same three flags.
- `ContainerOwnershipMsg`: a trailing placement section after the removes (entry index, parent id, centre in double,
  authority, frame flags), written only for entries that need it. A protocol-18 reader stops before it.
- `AuthorityTransferMsg`: trailing `Crossing` byte (D15), written only when set.

**D24 Positions on the wire stay container-local float32; a large frame needs no wider format (NEB-387).** A planet's
box can be 500 km across (a 205 km radius with headroom) and its scope can sit thousands of kilometres from the world's
origin, and a position is a `Vector3` in the container the entity stands in (`EntitySpawnMsg.LocalPosition`). That is
enough, and no wire change was made:

- *Entities on the ground* stand in the chunks the planet hosts (D22), whose boxes are a few hundred metres across, so
  their wire coordinates are small however large the planet is: exact to a float's own resolution at a few hundred
  metres (a fraction of a millimetre).
- *Entities directly in the frame* (a ship above the chunks, a loose body) carry frame-local floats up to the box's
  half-extent. A float between 2^17 and 2^18 m (131 km and 262 km) has a spacing of 2^-6 m, 1.6 cm, so a value is
  rounded by at most 0.8 cm there (half that between 65 km and 131 km). That is the worst case at the edge of a
  500 km box, and is acceptable for hulls and loose bodies; an entity near the frame's centre is finer.
- *Where the scope is* does not enter into it. The wire position is relative to the container, never to the scope's or the
  world's origin, so a planet in a system 10,000 km from the origin sends the same numbers as one at the origin.
  Scopes and frames hold their own offsets in double (`ScopeFrame.OriginOffsetPrecise`, `ContainerRegistry.ToAbsolutePrecise`).
- *The simulation* has the same property: each framed carrier is its own physics scene with a floating origin (D19), and
  a scope has its own, so physics precision does not depend on where a planet sits in its system.
- *Far entities* (NEB-388) travel as absolute `Double3` positions in the scope, so being seen from a long way off does
  not depend on float precision.
- *The gateway's record* (`EntityRecord.AbsX/AbsY/AbsZ`, with `FrameKey`) is stored in double, and an entity in a frame with regions of
  its own is filed in that frame's space, so its numbers are the frame-local wire numbers and no 10,000 km offset
  enters. It is filled from `RegionSpaceOf`, which composes in `Vector3`: for an entity standing in the scope itself
  (key 0) a long way from the origin the value therefore carries a float's resolution there (about a metre at 10,000 km).
  Interest is bucketed in tens to hundreds of metres, so nothing decides on it; it is a limit to know, not changed here.

A game that needs better than 1.6 cm for something large in a very big frame keeps it in a chunk, or in a framed
carrier of its own (a ship's frame is local to the ship). Scenario 45 pins the numbers.

## 7. Conformance

| # | Scenario | Test |
| --- | --- | --- |
| 20 | Container tree: baked parents from nesting, runtime children placed in their parent and held until it arrives, rows in any order, resolution by depth, inherited ownership following the parent, the D7 demotion, the subtree rule, contents of inherited rooms leaving with their box; lease rows and ownership messages round-trip placements | `ConformanceContainerTreeTests` |
| 21 | Physics frame: a still scene with synced collider copies, a rigidbody resting on the container's own floor with the container on its side, frame state, crossings in and out by the pose owner at 1 km/s, a non-pose-owner handing over unconverted, a walk between two owners inside a frame, veto and defer | `ConformancePhysicsFrameTests` |
| 22 | A root 10,000 km from the origin placed within a centimetre | `ConformanceContainerTreeTests.PlacementFarFromTheOriginIsExact` |
| 23 | A planet with its own frame and eight octants leased to two workers with a base leased on its own; a ship flies in and lands in the other worker's octant without error; rotation changes neither local positions nor region keys; the ship leaves through the planet's pose owner; a frame's origin follows its worker; a ship at 1 km/s with a leased engine room, crew walking between the two workers and leaving through an airlock policy | `ConformanceFramedWorldTests`, plus `FrameRegionSpaceTests` for the region keys and per-space foci |
| 45 | Wire precision in a large frame: an entity in a hosted chunk of a 500 km planet is near-exact, an entity directly in the frame at 100 km and 250 km from its origin is within half the float spacing (1.6 cm at 250 km) of where it stands, none of it depends on the scope being 10,000 km out, and the gateway's absolute records are double fields filed in the frame's own space | `ConformanceFramePrecisionTests` |
| 41 | A planet's ground as a grid hosted by the planet (D22), nothing leased statically: allocated around a ground pawn on one worker, a second pawn on the other and a ship flying scenario 40's lap at 300 m/s; the ship never stands in the box outside a leased chunk, each chunk it enters was leased ahead of it, the ground behind it retires, and a restart brings back a crate standing in a hosted chunk under the planet's own restored container | `ConformanceCarrierHostedGridTests`, plus `HostedChunkGridTests` |
| 43 | An `InstanceBoundary` in a chunk hosted by a framed carrier (NEB-394): its position is read out of the carrier's frame, as an entity's is, so the instance is prepared at the door; a player on the planet enters it and leaves back into the planet's chunk, in its frame | `ConformanceFramedInstanceBoundaryTests` |
| 49 | A large frame on a client (D11, D19, D25): a scope's origin shift poses the frames at once, so `KeepOriginNear` on a pawn on a framed planet run after a game's own rule never shifts back over 1,000 frames; a scope's shift leaves the history of what stands in a frame alone; ground 205 km out on a planet turning at 8.5e-4 rad/s holds within a millimetre against the camera over 1,000 frames under a render origin (about 3 cm without); the frame's world pose is unchanged; prediction runs bit for bit at the simulation pose; a ship on the planet is drawn through the planet's render origin and needs none of its own; a body moving at 100 m/s in a chunk 205 km out, with a camera riding it and the scene's origin kept within 1 km of the camera, holds within half a millimetre a frame while cameras draw (about 14 mm a frame with -O left on the root); drawing puts every root and child back exactly and never overlaps a prediction step; a tool 0.4 m in front of a camera 20 km from Unity's origin, through the turning frame, holds within 0.05 mm a frame under the scene render origin (about 4 mm without) | `ConformanceFrameRenderOriginTests` |

Tier limit (B, `ConformanceMesh`): the container registry is process-wide, so of two workers' copies of one carrier
only the last registered owns the box and its frame. Scenario 21's two-worker case keeps the copies' poses in step.

## 8. Known limits

- Every process that holds a framed container holds its frame scene.
- Raycasts do not pass from one frame into another; the game casts again in the space around a frame.
- An `InstanceBoundary` reads its own position and an entity's in the scope's own space, out of any physics frame (NEB-394), and leaves an occupant into the deepest container holding the point, descending into frames (`InstanceBoundary.FindDestination`). Its `Interior` is an oriented box: the boundary's rotation, read in the scope's own space like its position, turns the box, so a door on sloping or curved ground has a crossing volume that follows it. A boundary on a frame that is turning still sweeps its box with the frame: keep doors on frames that turn off. A client in the instance is not sent the carrier (`docs/scope-activation.md` D24: rows only, no host entities), so the chunks under it are held for their parent there: a crossing into one is acknowledged by the client all the same (`ContainerRegistry.IsHeldForCarrier`), and the carrier and its ground arrive with the scope; but an `ObserveHost` window over a carrier's ground keeps rows the client cannot register, so the ground behind an open door on a framed planet is not drawn.
- A client predicts in the frame of its own player only.
- Runtime containers inside a frame are scanned linearly, not hashed per frame.
- A hosted grid's chunks are runtime containers inside its host's frame, so the line above bounds them too: keep a
  hosted grid's ring and lead to a few dozen chunks per pawn and its leased total in the hundreds.
- A hosted grid lives in a scope; the public world hosts none. Its key must not be a scope key in use.
- A geometry of a game's own (D22) is consulted for resolution inside the host's frame only. The gateway buckets and
  places a hosted chunk by its box like any runtime container, and `ContainerRegistry`'s fast path and hysteresis
  still measure boxes: with overlapping cells an entity near a cell edge enters the right cell, and leaves it by the
  box's hysteresis.
- `grid.Frame.OriginOffset` of a hosted grid converts in simulation space only. A client draws the host posed (D11):
  convert a host-local point for rendering with `PhysicsFrame.ToParent`.
- A client outside a planet's box sees what stands on it only within `InterestFrameApproachMargin` of the box, and from the box's face, not from where it is (D23). A gateway of its own knows a carried planet's box only from its chunks' leases.
- The planet → space → planet lap runs in one process as scenario 40 (`ConformanceFramedPlanetLapTests`, two workers
  and a gateway, no client process; the sample `Samples~/FramedPlanetLap`) over ground leased statically, and as
  scenario 41 over a hosted grid streamed around players (D22). Not yet on a live mesh. What it found
  besides the box limit, which D23 closed: a frame's angular velocity reads zero below a few degrees a second, so crossings of a slowly
  turning frame miss ω × r. A carrier being a region entity, so that a gateway received a ship only within about
  `InterestRadius` of a player whatever its `RelevanceRadius`, was fixed by NEB-391: a carrier's own radius is a reach
  on top of its region (`docs/interest-management.md` §17).
- Far entities (`NetworkIdentity.FarRelevanceRadius`, `docs/interest-management.md` §16) cross frames by being
  measured in the scope's own space: the worker converts the entity out of every frame it stands in, and each client
  is measured from its focus in the scope's space (the enclosing focus of D18 for a pawn on a planet). A ship on a
  planet and a ship in orbit see each other as markers out to their far radius.
