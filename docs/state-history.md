# Historical state on workers — the recorded window

Status: design of record. Decisions made without asking are marked **D#**. Linear: NEB-222.
User-facing guide: `website/content/docs/guides/lag-compensation.mdx`. Conformance tests:
`Tests/EditMode/ConformanceStateHistoryTests.cs` (scenario 6 of `docs/conformance-suite.md`).
Reference sample: `Packages/com.1by3.nebula/Samples~/LagCompensatedHitscan/`.

## 0. Problem

A worker validates a claim about the past. A shot fired on a client left a gun aimed at what that client's screen
showed, which is interpolation delay plus transit plus input lead behind the worker's current tick; a door opened at
tick *t* has to be checked against where the player stood at *t*, not at *t + 8*. The worker holds only the present.

Clients already keep a bounded, tick-indexed pose buffer (`RemoteInterpolator`, 64 ticks) because they consume a
stream. Workers had a narrower thing: `NetworkIdentity.RecordPose`, a 64-tick world-pose ring the worker filled for
**every** entity it held with **its own** tick number. It had three problems the moment a ghost was involved:

1. A ghost's entry was tagged with the receiving worker's tick, not the tick the owner simulated. The pose in it came
   from the owner's tick *t*; the tag said *t + 1* (or whatever the receiver's loop was on). Comparing a ghost's
   history to the authority's was therefore off by one tick, silently.
2. An interpolating ghost has not moved to the delivered pose yet when the packet arrives; reading `transform.position`
   a frame later records a smoothing artefact, not what the owner reported.
3. There was no window bound anyone could ask about, so game code had to guess whether an answer existed and got a
   silent fall-back to "where the entity is now" when it did not.

Nothing carried anything but the pose, so a validator could not ask what stance, team or hitbox state the entity was
in at the claimed tick.

This document states what a worker records, what it promises about the answer, and what it explicitly does not do.

## 1. Shape

`Runtime/Core/StateHistory.cs` (Unity-side only; `NetworkIdentity`, `Container` and `NetworkVariable` are not in
`Services~`):

- `SyncHistoryAttribute` (`[SyncHistory]`) — marks a `NetworkVariable` field for snapshotting.
- `HistoricalState` — one recorded tick, returned by value: `Available`, `Tick`, `Position`, `Rotation`,
  `Velocity`, `Container`, `Epoch`, `FromAuthority`, and `TryGetValue(variable, out value)` for the marked fields.
- `StateHistory` — the per-entity ring: `Capacity`, `HasEntries`, `OldestAvailableTick`, `NewestAvailableTick`,
  `Record`, `TryGetStateAt`, `Shift`, `Clear`, and the constants `DefaultWindowTicks` (32), `MaxWindowTicks` (1024),
  `GapToleranceTicks` (4).

Entry points:

| Call | Answers for |
|---|---|
| `NetworkIdentity.StateAt(tick)` / `TryGetStateAt(tick, out state)` | that entity, on whichever worker holds this copy |
| `NetworkIdentity.OldestAvailableTick` / `NewestAvailableTick` | the window this copy can answer inside |
| `NetworkIdentity.History` | the ring itself (null when recording is off) |
| `NebulaWorker.TryGetStateAt(netId, tick, out state)` | anything the worker holds, authoritative or ghosted |
| `NetworkIdentity.TryGetPoseAt(tick, out p, out r)` | unchanged signature, now a wrapper over `StateAt` |

`NebulaWorker` is the only place that turns recording on (`Initialize` sets `StateHistory.WindowTicks` from the
config). A client leaves the window at zero: it has `RemoteInterpolator`, and nothing on a client validates a claim.

## 2. What is recorded, where, and when

**D1. The authority records after the tick's simulation, from the transform; a ghost holder records when a
replicated entry is applied, from the stream.** `NebulaWorker.Tick` records every *authoritative* entity right after
step 2 (simulation) and before container resolution and publishing, so the entry is the pose the tick produced and
the one this tick's stream is built from. Ghosts are no longer recorded there; `NetworkIdentity.ReceiveState` records
them, because only there is the owner's tick known.

**D2. Every entry is tagged with the server tick it belongs to, never with the tick of the process recording it.**
On the authority those are the same number. On a ghost holder the tag is the `WorldStateMsg` tick — the owner's — so
`StateAt(t)` on any worker means the same instant. This is the correctness claim of the whole item and what
scenario 6 pins.

**D3. A ghost's pose comes from the delivered sample, not from the ghost's transform.** `RecordGhostState` reads the
`RemoteInterpolator` sample the just-applied entry pushed (the merged full local pose, in its container's frame), and
falls back to the identity's local pose when there is none. An interpolating ghost is therefore recorded at the pose
the owner reported for that tick even though its transform will not be there until it has smoothed into it.

**D4. Entries hold a world pose plus the container they were in.** The ring stores world position and rotation, so a
hit test needs no frame conversion, and the container so a validator can tell scopes apart and reject a claim across
a seam. The world pose of an entity inside a *moving* container is resolved through the container's frame at the
moment of recording; a claim against a fast carrier is therefore accurate to that tick's frame, which is the same
approximation the ghost stream itself makes. A floating-origin shift moves the recorded world positions of entries
with no container (`StateHistory.Shift`); entries inside a container are rebuilt from the container, which moved.

**D5. History is not cleared by a handover.** A worker that ghosted an entity and then gained authority over it can
still answer for the ticks it recorded as a ghost; the entries keep saying `FromAuthority == false`. A call that
chases an entity across a handover (see `docs/cross-worker-calls.md` §7) therefore lands on a worker that can still
see the tick the caller is talking about. Unbinding an entity (despawn, scene-entity release) drops the ring.

**D6. An older tick than the newest recorded is ignored.** `Record` is monotonic, so a duplicated or replayed packet
cannot rewrite an entry, and a late one cannot appear behind the newest. Ordering on the wire already guarantees
this for the reliable path; the rule makes it true whatever arrives.

## 3. The window

**D7. One knob, `NebulaConfig.StateHistoryTicks`, default 32 ticks, `-nebula-state-history` to override, 0 to turn
recording off.** 32 ticks is about half a second at 60 Hz, which covers interpolation delay (3 ticks) plus half a
round trip plus input lead for a player at a few hundred milliseconds of latency, with room left over. It is
mirrored into `Services~/Nebula.Services/NebulaConfig.cs` and travels in the exported `nebula-services.json` with
every other field. The value is clamped to `MaxWindowTicks` (1024). It replaces the old `NetworkIdentity.PoseHistoryTicks`
constant of 64, which no longer exists.

**Memory.** One entry is 4 uints/bools plus a `Vector3`, a `Quaternion`, a `Vector3` and a `Container` reference:
about 64 bytes, plus the serialized `[SyncHistory]` values and one `int` offset per marked variable. With the
default window and no marked variables that is ~2 KB per entity per worker that holds a copy — an entity ghosted on
three neighbours costs the ring four times. A worker holding 2 000 copies pays about 4 MB. One marked `float`
variable adds 4 bytes plus 8 bytes of offsets per entry, so ~400 bytes per entity at the default window. Marking a
`string` variable is the expensive case and the reason marking is opt-in (**D8**).

**Allocation.** The ring and the per-slot field buffers are allocated on the entity's first recorded tick and reused;
recording after that copies into them and allocates nothing (pinned by `RecordingDoesNotAllocateOnceTheRingIsWarm`).
Turning the window off allocates nothing at all: `NetworkIdentity.History` stays null.

## 4. What `StateAt` promises

**D9. Inside the window, the entry for that tick, or the nearest recorded one within `GapToleranceTicks` (4).**
Not every tick is recorded on a ghost holder: the owner sends an entity's state only when it changed, so a pawn
standing still leaves gaps. Within four ticks either side (later preferred) the nearest recorded entry is returned
and `state.Tick` says which tick it actually is. Nothing is interpolated between entries and nothing is
extrapolated past one.

**D10. Outside the window the answer is "unavailable", never the nearest edge.** `tick < OldestAvailableTick` or
`tick > NewestAvailableTick` gives `Available == false`. Returning the oldest entry for a tick a second older would
let a hit test claim a hit against a pose nobody was ever at, and would do it silently. `TryGetPoseAt` keeps its old
shape — `false` plus the entity's *current* pose — for callers that only want a best effort.

**D11. The staleness bound for a ghost is one tick.** A worker's own tick *n* sees, for every ghost it holds, the
owner's tick *n − 1* at the newest: the owner built that stream during its tick *n − 1*, and the packet is applied
before or during this worker's tick *n*. `NewestAvailableTick` on a ghost is therefore one below the authority's,
and asking for the authority's newest tick on a ghost holder returns unavailable rather than a guess
(`AGhostIsExactlyOneTickBehindTheOwner`). Poses for ticks *older* than that are not approximations: they are the
owner's own recorded values for those ticks, to within the wire precision of `EntityStateEntry` (exact floats for
position by default; the rotation goes through an Euler round trip, so the conformance test allows 0.1°).

Under packet loss on the unreliable path the bound degrades to "the newest tick that arrived"; that is what
`NewestAvailableTick` reports, and it is why the API makes a caller look at it instead of assuming.

## 5. Opted-in sync fields

**D8. A `NetworkVariable` is snapshotted only when its field carries `[SyncHistory]`; pose, velocity, container and
epoch always are.** A snapshot costs a serialization of the value every recorded tick on every worker that holds a
copy, and most variables (a name, an inventory, a score) are never part of a claim about the past. Marking is per
field and discovered exactly like `[Persist]`, in `NetworkIdentity.DiscoverVars`; the marked subset is `HistoryVars`,
in declaration order.

The values are written into a per-slot byte buffer with an offset per variable, so reading one back
(`HistoricalState.TryGetValue(variable, out value)`) seeks straight to it. A variable belonging to another entity, or
one with no `[SyncHistory]`, returns false rather than a wrong value.

**D12. A ghost's marked variables are the values the owner's stream had delivered by that tick.** `EntityVarsMsg`
carries no tick of its own, so the worker sends an entity's variable update **ahead of** its state entry for the same
tick; on the reliable link that puts the fresh values in place before the entry is applied and recorded. The
unreliable state batch travels on another channel and can still cross a variable update, so the promise is the same
one tick as the pose, not tighter.

## 6. Non-goals

Stated so a game does not wait for them.

- **No collider rewind.** Nebula does not move colliders back and does not perform the hit test. Poses of hitboxes
  that are not the entity's root are the game's business; a rewind that moves them is game code, and the sample
  shows one shape of it.
- **No decision about which tick a client may claim.** Nebula does not validate that a claimed tick is one the
  client could plausibly have been rendering. `NewestAvailableTick`, `OldestAvailableTick` and the client's own
  `NetworkTime` are the inputs; the policy is the game's.
- **No history on clients.** `StateHistory.WindowTicks` stays 0 there. A client that wants its own record has
  `RemoteInterpolator`.
- **No interpolation or extrapolation** (D9, D10).
- **No history for anything the worker does not hold.** A worker answers only for its own copies. Asking another
  worker is a cross-worker call or a `WorkerQuery`, and what it should carry is the game's design.
- **No persistence.** The ring is process memory; a restarted worker starts empty.

## 7. Tests

`ConformanceStateHistoryTests` (tier B, two real workers on `ConformanceMesh`):

- `StateAtMatchesTheRecordedPosesOnTheAuthorityAndOnAGhost` — the exit criterion. Eight ticks of a pawn walking
  along a seam; every tick's pose, rotation, epoch and container agree between the authority's ring and the ghost
  holder's, and every ghost entry is tagged with the owner's tick.
- `AGhostIsExactlyOneTickBehindTheOwner` — D11, by publishing a tick and asserting before and after delivery.
- `ATickOutsideTheWindowIsUnavailableRatherThanExtrapolated` — D10, on both sides.
- `AMarkedVariableIsSnapshottedPerTickAndAnUnmarkedOneIsNot` — D8 and D12.
- `AWorkerAnswersForAnythingItHolds` — the `NebulaWorker` helper, for an owned entity, a ghosted one and an unknown id.
- `HistoryRecordedAsAGhostSurvivesGainingAuthority` — D5, across a real handover.
- `AZeroWindowRecordsNothing` — D7.

`StateHistoryRingTests` (tier C): the ring's own contract — capacity, oldest/newest, gaps, monotonicity, the
clamp, the pinned constants, origin shift, and that a warm ring records without allocating. A mesh would add only
ceremony here.

The `ConformanceMesh` harness grew two things for this: every worker now gets a real `NebulaConfig`, and
`Worker.PublishTick(tick)` runs the authority half of a tick (record, prepare replication, ghost band) through the
worker's own code. `Mesh.SetOwner(container, worker)` applies a lease, which is what makes the ghost band fire.

## 8. Decisions

- **D1** Authority records after simulation from the transform; a ghost holder records on apply, from the stream (§2).
- **D2** Every entry carries the server tick it belongs to, the owner's on a ghost (§2).
- **D3** A ghost's pose is the delivered sample, not the (still smoothing) transform (§2).
- **D4** Entries hold a world pose plus the container; origin shifts move uncontained entries (§2).
- **D5** A handover does not clear history; entries keep saying where they came from (§2).
- **D6** `Record` is monotonic: a replay or a late packet cannot rewrite an entry (§2).
- **D7** One knob, `StateHistoryTicks`, default 32, `-nebula-state-history`, 0 = off, clamped to 1024 (§3).
- **D8** Sync fields are snapshotted only with `[SyncHistory]`; pose and friends always (§5).
- **D9** Inside the window: the tick, else the nearest recorded within 4 ticks, which says what it is (§4).
- **D10** Outside the window: unavailable, never the nearest edge, never an extrapolation (§4).
- **D11** A ghost's documented staleness bound is one tick (§4).
- **D12** Variable updates are sent ahead of the state entry of the same tick so a snapshot matches it (§5).
