# Runtime containers

Goal: make Nebula flexible enough to host a world whose shape is decided at runtime by the game
(Holospace's continuous chunked landscape is the first such game) without losing anything the baked
world and dynamic container designs give the shooter demo.

The world architecture (chunks, terrain, storage, what content a chunk holds) is the game's
business. Content-addressed prefabs are also the game's business. Nebula's job is a small set of
runtime primitives that any such world can be built on. This document is the plan and its outcome;
the user-facing write-up is `website/content/docs/guides/runtime-containers.mdx`.

## Delineation

| Nebula provides | The game (Holospace) provides |
| --- | --- |
| Static containers that can be registered and removed while the mesh runs, named by an app-assigned 64-bit id plus bounds | The chunk model: size, coordinates, hashing coordinates to ids, when to allocate and retire |
| A wire form for runtime containers | Terrain and chunk-content storage and streaming |
| Neighbour detection for runtime containers by spatial hash, so ghosting works | A `Prop` network prefab with a persisted network variable naming a bundle asset |
| Occupancy in worker telemetry, a lease lifecycle API, and a pluggable assignment policy with a cost-aware default | The CDN bundle loader that runs on clients and workers, and the allow-list for what a bundle may contain |
| Worker cap as configuration rather than a type limit, and autoscale through `IWorkerHost` | Placement rules and permissions |
| `Nebula.World` stays optional: floating origin, grid helpers, baked cells | Whether to use `Nebula.World` at all |

Nothing changes for content-addressed prefabs in Nebula. A prop is a normal registered prefab
whose synced state includes which asset to load; loading is ordinary Unity code in the game.

## What shipped (2026-09-13)

| Seam | Before | Now |
| --- | --- | --- |
| `ContainerRef` | 2-byte index, or `DynamicIndex` + 8-byte net id | Third form `RuntimeIndex = ushort.MaxValue - 2` + 8-byte id. `IsRuntime`, `IsStatic`, `MayArriveLater` (dynamic or runtime: messages naming an unknown one wait). Same max wire size |
| `Container` | `IsDynamic`, `Carrier` | `IsRuntime`, `RuntimeId`. Leased and owned like a baked container |
| `ContainerRegistry` | Dense list built once by `Load`; `_grid` when gridded; flat dynamic list | `RegisterRuntime(id, frameBounds)` / `UnregisterRuntime(id)` / `GetRuntime` / `Runtime`, legal after boot. Spatial hash (`RuntimeBucketSize`, default 256 m). `Find`, `Resolve`, `Along`, `NeighborsOf` visit it. Adjacency between runtime and baked boxes both ways. `SyncRuntime(leases)` mirrors the control plane; `PruneRuntime`; `ToFrame`/`ToAbsolute`; `ShiftRuntime` on origin shift. Events `RuntimeRegistered` / `RuntimeUnregistering`. `rt_<id>` string ids (`IsRuntimeId`, `TryParseRuntimeId`) |
| `IControlPlane` | `EnsureContainer(id)` | `EnsureRuntimeContainer(id, bounds, workerId)`: row carries the box, created already assigned to the requester (first wins). `LeaseInfo.HasBounds/BoundsCenter/BoundsSize`. Module `container_lease` gained the box columns and the `EnsureRuntimeContainer` reducer; bindings regenerated |
| Wire messages | `ContainerOwnershipEntry` without a box; `SpawnPlayerMsg.ContainerIndex` | Entries carry a flags byte and the box for runtime containers, so clients register them; `SpawnPlayerMsg.Container` is a `ContainerRef` so the gateway can spawn into a runtime container |
| Roles | Waited on `IsDynamic` only | Worker, gateway, orchestrator call `SyncRuntime` before applying leases; clients from the ownership message. Pending ghosts/handovers/spawns keyed by `ContainerRef` and flushed by either registration event. Persistence dates leases on runtime containers too. Overlay, telemetry geometry, dashboard state and map include them |
| Worker API | none | `RequestRuntimeContainer(id, frameBounds)` (idempotent) and `ReleaseRuntimeContainer(id)` (checkpoints persistent contents, despawns, deletes the row) |
| Orchestrator | `ComputeAssignment` even split; `MaxWorkers` const 32 | `IAssignmentPolicy` (`NebulaOrchestrator.Policy`), `BakedAssignmentPolicy` (the old dealer + sticky runtime containers, `ComputeRuntimeAssignment`), `CostBalancedAssignmentPolicy` (Morton order, equal-cost contiguous runs, threshold hysteresis, orphans placed next to their neighbours' owner). `NebulaConfig.AssignmentPolicy` auto/baked/cost, `-nebula-assignment`. `MaxWorkers` from config (limit 65535: 16-bit index in every net id). `AutoScale` with `MinWorkers`/`MaxWorkers`, utilization thresholds and a planner dry run (see `docs/autoscale.md`). `POST /api/containers/ensure|remove`. State JSON: `policy`, `totalCost`, `runtimeContainers` |
| Telemetry | Raw worker documents only | `MeshTelemetry.ParseContainers` pulls per-container counts without parsing the entity list; `CopyOccupancy` feeds the policy; entries expire with the documents |
| Tests | 99 | 128: `WireFormatTests` (Phase 0 guardrail: 2-byte static ref, message layouts), `RuntimeContainerTests`, `AssignmentPolicyTests` |
| Tooling | `typecheck.ps1` checks the repository it lives in | `-Repo` and source-over-DLL for `-ExtraSourceRoots`, so `typecheck.ps1 -Repo C:\Dev\nebula-shootergame -ExtraSourceRoots C:\Dev\nebula\Packages` checks a `file:` consumer against the live library |

Untouched, as planned: baked manifests, cell scenes, `ContainerRegistry.Load` and the 2-byte static
reference; dynamic containers and their carrier-follows semantics; the lease state machine;
`NetworkPrefabs` and the spawn message; `Nebula.World`.

## Constraints on the Prop approach (no Nebula changes, but design around them)

- Synced state must live on the Prop. Content loaded from a bundle and parented afterwards is not
  bound by Nebula, so it cannot carry `NetworkBehaviour`s or a `NetworkIdentity`.
- The content id arrives in the spawn snapshot, so a Prop exists before its visuals and colliders
  do. Keep a placeholder collider on the Prop or accept the window.
- Workers load bundles too, headless. Bundles need a Linux server build with colliders intact, and
  workers need CDN access.
- Ghosts on neighbouring workers also resolve their content. Correct, but a cache consideration.
- Persistence works as-is: the content id is a persisted network variable on a `PersistentEntity`.

## The sample: nebula-virtualworld

`C:\Dev\nebula-virtualworld` (Unity 6000.6, URP template, Nebula by `file:` reference like the
shooter) is the exit test for the whole plan and the template for Holospace's Nebula integration:

- `Chunks`: 64 m chunks on the ground plane; id = packed grid coordinate; box = a 512 m column.
- `ChunkAllocator` (worker): every 0.25 s, a ring of one chunk around every pawn this worker owns is
  requested; owned chunks nobody wanted for 60 s and with no pawn inside are released.
- `ChunkLoader` (every role): on `RuntimeRegistered` a tinted ground slab with a collider is put
  under the container; a real world streams terrain here.
- `Prop`: the one prefab every placed object is; `[Persist]` `Shape`, `Tint`, `PlacedBy` name the
  content, `Load()` resolves it on every process (a primitive here, a CDN bundle in Holospace).
- `Avatar`: predicted walker; E places, X removes, 1-4 pick the shape; `-nebula-bot` clients wander.
- `VirtualWorldGameMode`: asks for the 3x3 around the origin on `OnWorkerStarted` (the worker queues
  requests made before it is registered), spawns pawns by name with persistence, places and removes
  props on the authority. The allocator keeps the origin ring wanted so it never retires.
- Verified 2026-09-13 with `nebula start --workers 2 --bots 3`: policy switched to `cost`, 30+ chunks
  allocated as the bots roamed, pawns and prop ghosts handed over across seams in both directions,
  props persisted, no warnings in any log.
- `VirtualWorld > Build Sample` (or `-executeMethod VirtualWorld.Editor.VirtualWorldSetup.BuildBatch`)
  builds the prefabs, the World scene, the config and the build settings from the scripts.

## Fixed after the first run (2026-09-13, evening)

- Entities parented under a runtime container were destroyed with its object when the chunk retired
  (ghosts on workers, every remote pawn and prop on clients). On the worker the dead identity then threw
  in `RecordPose` every tick, which aborted the tick before `PublishToGateways`: clients received no
  world state at all ("0 states/s") and saw no bots. `NetworkIdentity.SetContainer` now detaches on
  leaving a runtime box, `UnregisterRuntime` detaches anything networked still under it, and the worker
  purges a destroyed identity instead of throwing.
- Chunks flickered: an owner retired a chunk on its own idle clock while the neighbour worker's pawn
  stood next to it. `TouchContainer` (module reducer, `IControlPlane`) stamps the row from
  `RequestRuntimeContainer` every 15 s while another worker wants the box; the owner reads
  `RuntimeContainerIdleSeconds` before retiring.
- The gateway logs world-state relay counters once a second under `-nebula-verbose`.
- Bots turn back once 150 m from the origin so a player near the spawn can find them.

## Opt-in helpers for a procedural, unbounded grid (2026-09-18)

Holospace's continuous chunked landscape and the nebula-virtualworld sample both hand-wrote the same
thing on top of runtime containers: pack a grid coordinate into a stable id, get a cell's bounds in
the current floating-origin frame, keep a ring of cells requested around every player, and wait for
a container to exist before acting on it. `Nebula.World.RuntimeGrid` and `RuntimeGridAllocator`
(`Runtime/World/RuntimeGrid.cs`, `RuntimeGridAllocator.cs`) package that, opt-in: a game that does not
construct one sees no behaviour change, and every seam they touch (`ContainerRegistry.RuntimeBoundsInFrame`,
`NebulaWorker.RequestRuntimeContainer`/`ReleaseRuntimeContainer`, `ContainerRegistry.RuntimeRegistered`)
already existed. No wire or protocol change.

- `RuntimeGrid`: cell-size configuration (uniform or per-axis) plus pure helpers - `PackId`/`UnpackId`
  (three signed 21-bit fields, x high; bit-for-bit identical to Holospace's `WorldChunks.IdOf`/`CoordOf`
  because ids are already persisted), `IsValid`, `CoordOf` (a frame position or a `NetworkIdentity`),
  `CenterOf`/`BoundsOf`, `IsNear` (Chebyshev ring distance) and `Neighborhood` (a ring of coordinates).
  `UseAsRuntimeBounds()` points `ContainerRegistry.RuntimeBoundsInFrame` at the grid, so a game that
  adopts it no longer supplies that hook itself; games with their own container shape keep using the
  hook directly, untouched.
- Floating-origin policy, also on `RuntimeGrid`: `static ShiftOriginTo(Vector3Int)` performs the shift
  the way a PhysX game needs it - enabled `CharacterController`s on runtime-container entities are
  suspended across `NebulaWorld.Streamer.ShiftOrigin` (a controller must be reinserted at the new pose,
  not sweep over the shift) and `Physics.SyncTransforms()` runs after. `KeepOriginNear(entity, ring)`
  is the usual policy on top: shift when the followed entity leaves `ring` cells of `WorldOrigin.Cell`.
  Both are no-ops without a loaded runtime world; a game that keeps its own policy calls
  `Streamer.ShiftOrigin` as before.
- `RuntimeGridAllocator`: a plain class (not a `MonoBehaviour`) wrapping a `NebulaWorker` and a
  `RuntimeGrid`. `Tick(unscaledTime)` - called from whatever the game already updates every frame -
  requests a ring around every player-owned authoritative entity and any fixed anchor coordinates,
  re-touches them so a neighbour's owner does not retire them, and releases owned/unwanted/unoccupied
  cells after `RetireAfterSeconds`; the same policy Holospace's and nebula-virtualworld's hand-written
  `ChunkAllocator`s already ran. `IsWanted`/`WantedIds` expose the current interest set so a game stops
  recomputing it for its own loader. `EnsureContainer(coord, onReady)` requests a container and calls
  back once `ContainerRegistry.RuntimeRegistered` fires for it (immediately if it already exists) -
  event-driven, replacing the request-then-poll-every-0.1s loops a game otherwise writes at each call
  site that needs a container before acting on it.

Inspecting a persisted record without the entity: `PersistentStateCodec.TryReadEntry(state, name, out
ArraySegment<byte>)` returns one named entry of a state blob - a behaviour's chunk
(`TryReadBehaviourState(state, "WorldObject", out ...)`, i.e. the `WorldObject#state` entry) or one
persisted variable (`"Chest.Coins"`) - so a game that loads a `PersistedEntityRecord` for an entity no
worker has spawned reads it through the codec instead of re-implementing the blob layout. False for a
null/empty blob, a blob from a newer build, or a name the blob does not hold. Read-only, no format change.

Games with a different chunk shape, packing, or allocation policy are unaffected: nothing here is
wired in automatically, and the underlying primitives (`ContainerRegistry.RegisterRuntime`/`GetRuntime`,
`NebulaWorker.RequestRuntimeContainer`/`ReleaseRuntimeContainer`) are unchanged.

## Follow-ups

- Holospace: the bundle loader and allow-list, Linux server bundle variants, placement permissions.
- A gridded baked world plus runtime containers has not been exercised together beyond unit tests.
- The cost policy's Morton quantum is 8 m; containers smaller than that in one axis still order
  correctly but may interleave with neighbours.
- The persistence store is asked for each lease's records once (`LoadContainers`, batched with every other
  container that is due); a chunk that is retired and re-requested within `PersistenceRestoreGraceSeconds` waits
  the grace period before its props return.

## Configure runtime chunks

Assign `NebulaConfig.RuntimeWorld` and enable `ChunkedWorld` to install `NebulaChunkedWorld` on every role.
It manages the chunk grid, allocator, and floating origin. Supply terrain through `NebulaChunks.Loaded` and
`Unloading`, or a `ChunkContent` subclass. Your game supplies gameplay and networked entity spawning.
Follow the [runtime-world tutorial](../website/content/docs/guides/infinite-runtime-world.mdx) for a complete example.

For a custom allocation workflow, leave `ChunkedWorld` disabled and use `RuntimeGrid`, `RuntimeGridAllocator`,
and `ContainerRegistry.RegisterRuntime` directly.

## More than one grid per mesh (2026-09-22)

The turnkey chunked world was one grid per process: `NebulaChunks.Grid`, a static `RuntimeGrid`, with a chunk's id
spending all 63 usable bits of the runtime id on three signed 21-bit axes. A game with instanced open areas,
several maps or one procedural region per party could not use it, because chunk (x, y, z) was one container
whoever asked for it.

`NebulaChunks` is now a registry keyed by **scope key**: one grid, one allocator, one set of lease rows and one set
of persistence records per scope, with `Grid`, `Allocator` and every unqualified lookup still meaning the public
world. A scoped grid is activated as a `ScopeKind.Grid` scope (`docs/scope-activation.md`) whose payload is a
`ChunkGridDefinition` and whose only part is the anchor chunk, so activating a world does not enumerate it. Ids
stay as they were in the public scope and are derived from the key and the coordinate in every other, so nothing
persisted was rewritten and nobody's coordinate range narrowed.

Decisions, the id-layout choice and the seams left for per-scope origin frames: `docs/scoped-chunk-grids.md`.
