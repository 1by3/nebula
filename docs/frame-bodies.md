# Loose bodies in a moving frame, and attaching entities to a frame — design

Status: design of record for **NEB-267**, implemented. Decisions made without asking are marked **D#**. Protocol
stays **19**: nothing on the wire changes shape. The new state rides behaviours a game opts into (a network variable,
handover bytes, a persistence chunk), which a prefab without them never sends. Builds on physics frames
(`docs/container-tree.md` §3, D10–D17). User-facing page: `website/content/docs/guides/physics-frames.mdx`
(§ Carry loose cargo, § Attach an entity to a container). Conformance: scenarios 24 and 25 of `docs/conformance-suite.md`.

## 0. Problem

A ship with its own physics frame (`Container.OwnPhysicsFrame`) simulates its inside in a scene where it stands
still. A crate with a `Rigidbody` in the hold already falls to the ship's own floor
(`ConformancePhysicsFrameTests.ARigidbodyInsideFallsTowardsTheContainersOwnFloor`). What was missing for cargo:

1. Nothing showed that stacked crates survive a whole flight: takeoff, surges, rolls, cruise at speed, a landing,
   a crossing out through the ramp, a handover of the ship to another worker.
2. A frame feels nothing of its carrier's motion (container-tree D14). A game that wants crates to slide a little
   when the ship brakes had to write the fictitious forces itself.
3. A ramp or door on the hull is copied into the frame as a static collider and teleported each tick. A crate on a
   moving ramp is shoved out of it rather than carried.
4. `NebulaWorker.Despawn(ship, keepPersisted: true)` sets the ship's riders down where it stood. Only
   `EmptyContainer` (a chunk retiring) saved the cargo aboard. A game that stows a ship (into a hangar list, an
   inventory) lost its cargo into the world.
5. `NetworkMotionState.Velocity` said "world-space", but inside a frame it is frame-local.
6. Nothing let a game fix an entity to a container: a crate strapped to a cargo grid, a turret bolted to a deck, a
   crate set on a shelf in a building. Such an entity must not slide, must not be re-resolved into a neighbouring
   container, must survive handover, persistence and late joiners, and must be released gently.

## 1. Loose bodies

**D1 A loose body in a frame needs no component of its own.** A crate prefab is a `NetworkIdentity`, a
`NetworkTransform`, a `Rigidbody`, a `NetworkRigidbody` and colliders. It simulates in the frame's scene, in
container-local coordinates, with the container's own down. The carrier's motion never reaches it, so a stack
survives any flight profile. Recommended body settings, which scenario 24 uses: `solverIterations` 8–10 (stacks
of three or more), `collisionDetectionMode = ContinuousSpeculative`, default sleep threshold, and a
`PhysicsMaterial` with friction around 0.6. Nothing in Nebula sets them; they are the game's.

**D2 `NetworkMotionState.Velocity` is in the entity's own space.** The documentation now says so: inside a physics
frame it is frame-local (the velocity relative to the ship), elsewhere it is in the scope's own space. That is what
the code always did (`NetworkRigidbody` copies `Rigidbody.linearVelocity`, and a frame's scene is still), what the
wire carries (poses and velocities are container-local), and what `SetContainer` converts on a crossing
(container-tree D15).

**D3 A hull part that moves becomes a kinematic body in the frame.** `PhysicsFrames.SyncContent` still keeps every
copy at its source's pose relative to the owner. The first time it sees a copy's source move, on a process that
steps the frame's scene (a worker), it adds a kinematic `Rigidbody` to the copy and from then on moves it with
`MovePosition` and `MoveRotation`. PhysX then gives the part a velocity for the step, so a crate on a lowering
ramp or a rising lift rides it instead of being pushed out of an overlap. A part that never moves stays a static
collider. A client does not step frame scenes (it renders them posed, container-tree D11), so it keeps teleporting
its copies. A scale change is still applied through the transform. A copy's pose relative to the owner is now
composed from the local poses between the two, not through world space: for a carrier kilometres from the origin
the world-space route subtracted two large positions, and its rounding would have moved a part at rest every tick,
made it kinematic and kept the cargo on it awake. A kinematic copy is compared with the target it was last sent,
exactly, since its transform reads back from the body with rounding.

**D4 Fictitious forces are opt-in: `FrameInertia`.** A `NetworkBehaviour` for a body that should feel its frame's
motion. On the authority, each `NetworkTick` (before the frame scene steps) it applies, as
`ForceMode.Acceleration`,

    a = -Scale × (A + α × r + ω × (ω × r) + 2 ω × v)

with `A` the frame's acceleration and `ω` its angular velocity in the frame's own axes
(`PhysicsFrameState.LocalAcceleration`, `LocalAngularVelocity`), `α` the rate of change of `ω`, `r` the body's
frame-local position and `v` its frame-local velocity. The frame state is sampled once per tick after the carriers
moved (container-tree D14), so the force lags the carrier by a tick. Each tick's value is multiplied by `Scale`,
clamped to `MaxAcceleration` (20 m/s²), then smoothed with an exponential moving average over `SmoothingTicks` (6).
Clamping before smoothing is what turns a snap of the carrier (a teleport, a landing, a jump to cruise speed:
thousands of m/s² for one tick) into a nudge. Nothing is applied below `MinAcceleration` (0.05 m/s²), so a body at
rest in a cruising ship can sleep. `Scale` defaults to 0: a game chooses how much of the ship's motion its cargo
feels (inertial dampers), usually per ship. Nothing in Nebula applies fictitious forces without the component.

**D5 Stowing a carrier can take its cargo: `CargoPolicy.Stow`.** `NebulaWorker.Despawn(identity, keepPersisted,
CargoPolicy cargo)`. With `SetDown` (the default, and what the existing overloads do) the riders are put down where
the carrier stood, as before. With `Stow` the worker walks the carrier's box, deepest first, and despawns with
`keepPersisted` every rider it is authoritative for that has a `PersistentEntity`, no owning client, and that sits
directly in the box of the carrier or of another stowed rider (not in a room fixed inside it, whose record names
the room rather than the carrier). Each is checkpointed while still aboard, so its record names its carrier
(`CarrierKey`) and `IPersistenceStore.LoadCarried` brings it back when the carrier is restored. Anything else
aboard (a player's pawn, a transient entity, a rider of a transient vehicle) is put down as with `SetDown`. `Stow`
needs a persistent carrier that is kept: with `keepPersisted` false or no `PersistentEntity` on the carrier it
logs a warning and sets the cargo down, since a record naming a deleted carrier would never come back.
`EmptyContainer` and `Stow` share the walk.

**D6 The hold's box decides where a crate changes frame.** Container-tree D17 (a hard edge) still holds: a crate
belongs to the ship's frame while its origin is inside the container's box, plus the hysteresis. Size the box so it
ends on the ramp, not at the door: a crate pushed down the ramp then crosses to the ground's space while it still
rests on the ramp's copy, and lands on the terrain in the outer scene, where the ramp's original is.

## 2. Attaching an entity to a container

**D7 `FrameAttachment` is a component, with or without a body.** `[DisallowMultipleComponent] FrameAttachment :
NetworkBehaviour`. On the authority: `Attach(container, localPosition, localRotation, teleport)`, `Attach()` (where
it is now), `Detach()`, and, from any worker that holds a copy, `RequestAttach(...)` and `RequestDetach()`, which
reach the authority by `AuthorityRpc`. It works for a ship's frame, a fixed container (a building, a chunk) and a
carrier without a frame. Invariant: **an attached entity sits in the container it is attached to**, so
`AttachedTo` is `Identity.Container` while `Attached` is true, and no container id is stored. `Attached` is a
read-only property over a private network variable, so game code cannot set the flag without going through
`Attach` and `Detach`.

**D8 An attached entity's container is pinned.** `NetworkIdentity.ContainerPinned` (internal) makes the worker's
tick skip re-resolving the entity's container: it is not moved into a neighbouring chunk, out of a frame, or
across an `InstanceBoundary`. The owner check still runs, so an entity attached to a container another worker owns
is handed to that worker on the next tick, and a re-deal of the container takes the entity with it.

**D9 A held body is kinematic for a second reason.** `NetworkRigidbody.Held` (internal) sits next to `Simulate`:
the authoritative body is kinematic while `!Simulate || Held`, and the body's own kinematic flag is kept underneath
for when both reasons clear. The handover still carries the underlying flag. `FrameAttachment` sets `Held` while
reading handover or persistent state, before the entity gains authority or spawns, so the body is never dynamic
for a tick. A bare `Rigidbody` without `NetworkRigidbody` is made kinematic and dynamic directly. No variable was
added to `NetworkRigidbody`, whose variable layout existing props depend on.

**D10 The authority re-applies the attached pose every tick.** In `NetworkTick`, `SetLocalPose(container, pose)`.
Nothing then drifts, whatever touched the transform, and the attached pose is exact in a frame, in a fixed
container, and in a frameless carrier (whose box moves the entity with it through the parenting).

**D11 A detach is gentle.** The pin clears and the entity stays in its container. The body's pose is pushed to
PhysX before it turns dynamic. Its velocity is what the attached pose did over the last tick, in simulation space:
zero in a frame or a fixed container, the carrier's velocity in a frameless carrier. For `DetachSettleTicks` (10)
the body's `maxDepenetrationVelocity` is capped at `DetachDepenetrationVelocity` (1 m/s), so a crate that was
attached slightly inside another is eased apart rather than launched.

**D12 Handover carries the attachment.** `WriteHandoverState` writes the flag and the local pose, the receiver pins
and holds before it gains authority, and the next tick re-applies the pose. The network variable `Attached` rides
the spawn message's variables, so a late joiner or a new ghost knows it too.

**D13 Losing the container detaches.** When the container of an attached entity changes on its authority for any
reason other than `Attach` (its carrier despawned and set it down, `PlaceInScope`, an instance transfer), the entity
detaches and `AttachedChanged(false)` is raised. A pinned entity is never re-resolved, so this only happens when
something outside the tick moves it.

**D14 Persistence saves one chunk, not a variable.** `WritePersistentState` always writes a version, the flag, and
the local pose when attached, so a detached save says so rather than leaving a stale value in place.
`ReadPersistentState` sets `Attached` and the hold from it before the restored entity spawns. `Attached` is
deliberately not `[Persist]`: one source of truth, with no dependence on the order in which the codec reads
variables and chunks, and it survives `PersistentEntity.PersistPose = false`. A despawn on the authority clears the
attachment and gives the body its own kinematic flag back: a scene entity keeps its values when it leaves the
network, and one spawned again with no record is not attached. The container comes from the record (`ContainerId`
or `CarrierKey`), so attached cargo stowed with `CargoPolicy.Stow` comes back attached with its carrier.

**D15 `AttachedChanged` fires on every process.** It is raised from `Attached.OnValueChanged`, which fires on the
authority when the value is set and on ghosts and clients when it is received, including a late joiner's spawn.

## 3. Conformance

| # | Scenario | Test |
| --- | --- | --- |
| 24 | Loose bodies in a moving frame: three crate sizes stacked in a closed hold survive a 900-tick flight (takeoff, ±20 m/s² surges, 80°/s roll and yaw, 1 km/s cruise, a landing snap) in the box, in the hull, in order, never jumping, asleep within 2 s of landing; with `FrameInertia` they slide and stay; a rigidbody crosses out and in with its velocity converted; a handover of the ship keeps the crates' poses and sleep; a crate rides a rising lift; a stowed ship takes its cargo into the store and brings it back | `ConformanceFrameBodiesTests` |
| 25 | Attachment: the attached pose stays constant through a flight; a loose crate rests on an attached one; a detach moves the crate less than 1 mm and leaves it slower than 0.05 m/s over 10 ticks; an entity attached to a chunk is not handed to the neighbouring chunk's worker but is handed over with a re-deal; a handover keeps it attached; an evacuation detaches it; persistence round trips in a chunk and in a ship; a late joiner sees it attached | `ConformanceFrameAttachmentTests` |

## 4. Known limits

- `FrameInertia` reads the frame state one tick late, and a leased interior reads it one replication delay late
  (container-tree D14).
- A client teleports its copies of moving hull parts (D3): a client predicting a pawn on a moving ramp predicts
  against a teleported collider.
- `CargoPolicy.Stow` takes the riders this worker is authoritative for. Riders owned by another worker (an interior
  pinned to a worker of its own) are set down there as with `SetDown`.
- An attached entity in a frameless carrier moves with the carrier through its transform: bodies resting on it in
  the outer scene are teleported with it, as with the carrier's own colliders.
- `Attach()` on a worker that is not the pose owner of the target frame converts at its ghost's pose of the carrier,
  one replication delay old; give the local pose explicitly when that matters.
