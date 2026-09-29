# Many server-owned entities — measurements and design (NEB-359)

Status: measurements (§1–§3), relevance tiers (§5), dormancy (§6), and distance tiers per priority with distance-rated sync state (§7) landed; protocol 24 (§7 needed no bump).
Harness: `Tests/EditMode/CrowdMesh.cs` (two real workers), `Tests/EditMode/ServerOwnedCostMeasurementTests.cs`
(`[Explicit]`, writes `Logs/scale/editor-server-owned.csv`), `Services~/Nebula.Services.Tests/ServerOwnedBandwidthTests.cs`
(`[Category("Scale")]` + `Soak`, writes `Logs/scale/synthetic-server-owned-bandwidth.csv`). Conformance scenario 34
(`ConformanceCrowdSeamTests`). Related: `docs/scale-suite.md`, `docs/interest-management.md` §4 (rate tiers),
`docs/entity-extents.md` (the ghost band). User guide: `website/content/docs/guides/server-owned-entities.mdx`. Sample:
`Samples~/ServerOwnedCrowd`. Relevance tiers: `Runtime/Interest/RelevanceTiers.cs`, `NetworkIdentity.UpdateInterval`,
`NetworkIdentity.RelevancePriority`; tests `RelevanceTiersTests` (both places), `RelevanceTierWorkerTests`,
conformance scenario 35 (`ConformanceRelevanceTierTests`, service tests). Dormancy: `NetworkIdentity.Sleep`, `Wake`,
`IsDormant`, `SleepWhenUnobserved`; conformance scenario 36 (`ConformanceDormancyTests`). Tiers per priority and rated sync
state: `NebulaConfig.InterestHighTiers`/`InterestLowTiers`/`InterestBackgroundTiers` (`RelevanceTierBands`),
`NetworkBehaviour.SyncDistanceRating`; conformance scenario 38 (`ConformancePriorityTierTests`), `SyncDistanceRatingTests`.

## 0. Problem

A game with non-player characters wants hundreds of server-owned entities on each worker: townsfolk, traders,
wildlife, patrols. Nebula has always handled them as ordinary entities (`NebulaWorker.SpawnServerDriven`), and they
cross seams like any other. What nobody had measured is what one costs: on the worker per tick, between workers,
and on each client's link. The targets the issue sets, in general terms:

| Target | Value |
|---|---|
| Worker tick | 150 active server-owned entities **plus** 1,500 in a lighter tier, within **4 ms** of a server tick |
| Client link | at most **100 kbit/s** of entity traffic with about 200 active and 1,000 light entities in view, the rate falling off with distance |

## 1. Measurements

Two layers, as in `docs/scale-suite.md` D1, never mixed:

- **editor** — real `NebulaWorker`s on the in-process mesh (`CrowdMesh`, built on `ConformanceMesh`): two workers,
  each leasing one 256 m × 512 m container either side of a seam at x = 0, one gateway linked to both and
  subscribed to every region. A walker is a prefab with a `NetworkIdentity`, a root `NetworkTransform` (defaults)
  and a behaviour that moves it at 1.5 m/s in `NetworkTick`, bouncing inside a box, and carries its velocity across a
  handover. Each tick is each worker's own `Tick` (timed on its own), then delivery into the other worker's
  `Dispatch`. 120 ticks of warm-up, 600 measured. Times are Editor Mono on a Windows development machine: compare
  rows, not machines; a player build is faster.
- **synthetic** — the real gateway and a real client over loopback UDP (`Fleet`), with a worker that publishes
  walkers exactly as a root `NetworkTransform` does (`FakeWorker.PublishWalkers`: position, Euler rotation and
  half-float velocity, 46 bytes an entry; only when changed; the reliable recovery entry of
  `NetworkTransform.CaptureRoot`), paced at 60 ticks per second of wall clock. The client stands in the middle; the
  entities are spread evenly over a 110 m disc around it (inside the 120 m interest radius), so the share in each
  distance tier is the share of its area. Measured over 4 s once every spawn has arrived.

### 1.1 What a worker pays

One worker holding every entity (the second one idle), or a crowd split across the seam:

| Scenario | Entities | Worker tick avg / p95 / max (ms) | To the gateway | Entries/s (reliable) | Worker ↔ worker | Handovers/s |
|---|---:|---|---:|---:|---:|---:|
| idle | 100 | 0.40 / 0.47 / 0.61 | 0 | 0 | 0 | 0 |
| idle | 300 | 1.20 / 1.26 / 1.92 | 0 | 0 | 0 | 0 |
| idle | 1,000 | **4.18** / 4.54 / 6.18 | 0 | 0 | 0 | 0 |
| walking | 100 | 0.46 / 0.53 / 3.85 | 281 kB/s | 6,000 (200) | 0 | 0 |
| walking | 300 | 1.37 / 1.48 / 3.47 | 844 kB/s | 18,000 (600) | 0 | 0 |
| walking | 1,000 | **4.59** / 4.77 / 7.32 | **2.81 MB/s** | 60,000 (2,000) | 0 | 0 |
| across a seam | 100 + 100 | 0.50 and 0.48 | 564 kB/s | 12,000 (400) | 29 + 22 kB/s | 1.0 |
| across a seam | 300 + 300 | 1.48 and 1.50 | 1.69 MB/s | 36,000 (1,200) | 86 + 95 kB/s | 4.5 |
| across a seam | 1,000 + 1,000 | **5.01** and 4.97 (p95 5.8) | 5.63 MB/s | 120,000 (4,000) | 257 + 261 kB/s | 13.0 |

A second run of the same suite measured the 1,000-walker row at 6.2 / 9.9 / 17.2 ms and the 1,000 + 1,000 seam row at
6.7 / 16.1 ms: the Editor is noisy at the top end, the averages are the stable part.

**Every entity costs about 4.2 µs a tick whether it moves or not**, and walking adds little (4.6 µs). Where it goes,
from the worker's own profile sections and a micro-benchmark of the same 1,000 idle entities:

| Per entity per tick | µs |
|---|---:|
| capturing the root transform for replication (`PrepareReplication` → `NetworkTransform.CaptureRoot`), even when nothing changed | 1.6 |
| resolving its container, with hysteresis (`ContainerRegistry.Resolve`) | 0.6 |
| the ghost band's seam test (`NeighborsOf` + `SeamDistance` per neighbour) | 0.5 |
| interest index, publish masks, state history, the game's own `NetworkTick`, and the loops around them | ~1.5 |

Nothing in that list depends on whether any client can see the entity, and nothing lets a game say that one entity
matters less than another.

Crowds cross seams correctly (§3): 13 handovers a second at 1,000 walkers per worker, each inside the tick that
decided it, and every walker ends with exactly one authority.

**50 spawns in one frame** are 50 reliable `EntitySpawn` messages from the worker to each gateway (103 bytes each;
the transport packs several into a datagram), and reach a client as **5** transport messages: the gateway already
coalesces a client's reliable messages into `MsgId.Batch` packets of about 1,100 bytes.

### 1.2 What a client is sent

| Walking | Idle | Gateway tiers | Recovery entry | Bytes/s | **kbit/s** | Entries/s |
|---:|---:|---|---|---:|---:|---:|
| 100 | 0 | default | every 30 ticks while moving | 71,262 | 570 | 1,413 |
| 200 | 0 | default | every 30 ticks while moving | 137,341 | **1,099** | 2,798 |
| 300 | 0 | default | every 30 ticks while moving | 202,042 | 1,616 | 4,180 |
| 200 | 1,000 | default | every 30 ticks while moving | 135,131 | **1,081** | 2,756 |
| 200 | 0 | default | *what-if:* once settled | 123,802 | 990 | 2,574 |
| 200 | 0 | stretched | every 30 ticks while moving | 44,794 | 358 | 836 |
| 200 | 0 | stretched | *what-if:* once settled | 30,371 | 243 | 586 |

"Default" is `InterestNearRadius` 30 m (every tick), `InterestFarRadius` 80 m (every 4th tick between the two,
every 12th beyond). "Stretched" is as far as today's knobs go: a 20 m near band still sent every tick, every 30th
tick to 60 m, every 60th beyond. An entry costs about 49 bytes on the wire once batching and the transport are
counted.

## 2. What the numbers show

1. **The worker pays for every entity every tick.** 1,650 entities at 4.2–4.6 µs is 7–7.6 ms against a 4 ms
   budget, before the game's own behaviour code. An idle entity costs as much as a walking one, and an entity nobody
   observes costs as much as one in front of a player. The fix is to let the game say how often an entity needs
   Nebula's attention (a lighter tier), and to let an entity nobody needs cost nothing at all (dormancy).
2. **A client is sent every nearby entity at the rate of a player.** The near band is 60 updates a second and the
   middle band 15, for a torch-bearer in a crowd as much as for an enemy in a firefight. 200 walkers in view cost
   1.1 Mbit/s, eleven times the target. Stretching today's distance tiers gets to 358 kbit/s, and the rest is the near
   band, which cannot be slowed at all today: its rate is the tick rate. The fix is a per-entity rate on the worker
   (which also slows the gateway's ingest: 2.8 MB/s per gateway link for 1,000 walkers today) and a per-entity
   priority at the gateway.
3. **The recovery entry is sent twice a second per moving entity, reliably, to every client that holds it,**
   whatever its distance tier. `NetworkTransform.CaptureRoot` schedules it 30 ticks after a change, sends it, and
   schedules the next on the next change: a walker never stops sending them. It is about 10 % of a crowd's bytes at
   the default tiers and a third of them once the tiers are stretched (358 → 243 kbit/s). What it exists for is the
   pose an entity comes to rest at, which an unreliable entry may lose and nothing would resend; that needs one
   entry after the entity settles, not one every half second while it moves.
4. **Idle entities cost nothing on the wire** once spawned (200 walking among 1,000 idle cost what 200 walking
   alone do). A light tier that mostly stands still is cheap for the client already; it is the worker that pays for
   them (point 1).
5. **Seams are not the problem.** Handover of server-owned entities works for crowds (scenario 34), and its cost is
   inside the per-entity tick cost above.
6. **Spawns are already batched** where it matters, on the client's link (§1.1). The worker-to-gateway leg is a
   datacenter link and its per-spawn messages are merged into datagrams by the transport.

## 3. Crowds across a seam (conformance scenario 34)

`ConformanceCrowdSeamTests.ACrowdWalkingAcrossASeamKeepsOneAuthorityEachAndLosesNobody`: 40 walkers per worker at
3 m/s in a 48 m strip across the seam, 1,800 ticks. After every tick of both workers each walker has exactly one
authority and none is lost; at the end each walker still has its speed (its behaviour's handover state crossed
every time), and the gateway last heard it from the worker that owns it, at the epoch it has now, having been sent a
spawn by that worker when it took the walker. What a game must do for a walker: carry whatever its behaviour needs
to keep walking in `WriteHandoverState`/`ReadHandoverState` — a velocity, a target, a path index — because the
receiving worker's copy has only what the stream and the handover gave it.

## 4. What follows, and what does not

Built in separate changes, each with its own tests, sample and user guide (relevance tiers: §5; dormancy: §6):

- **Relevance tiers** — a per-entity update interval on the worker (the lighter tier: simulated, checked and
  published every N ticks, staggered), a per-entity relevance priority the gateway uses to pick each client's rate
  (including a background priority that stops updating distant clients without despawning the entity there), the
  recovery entry sent once an entity settles, and gateway rate tiers that compose with the worker's interval.
- **Dormancy** — an entity marked dormant is not ticked, checked or sent, keeps its authority, state and replicas,
  and wakes on a call or when a gateway starts to watch its region.

Not built, because the numbers do not ask for it:

- **Batched spawns.** The client already gets 50 spawns as 5 messages (§1.1).
- **Seam handover changes.** Crowds already hand over correctly (§3).
- **A compact entry encoding** for the client leg (per-client entity aliases, no epoch in steady state). It would
  save 10–12 of an entry's ~46 bytes; the tiers above save a factor of ten. Worth doing when the tiers are not enough.
- **Shaving the per-entity pass** (skipping `CaptureRoot` for an unchanged transform, caching a container's foreign
  neighbours per tick). General, and worth doing, but the lighter tier removes the same cost for the entities that
  are numerous, so it is left for its own change.

## 5. Relevance tiers (built)

### D1. The lighter tier is an update interval on the worker

`NetworkIdentity.UpdateInterval` (ticks, 1..255, default 1; `SetUpdateRate(hz)` rounds a rate to it) says how often
the worker that has authority updates the entity. On its update ticks it is everything it always was; on the
others the worker leaves it alone in every per-entity pass of the tick:

| Pass | On a tick that is not an update tick |
|---|---|
| `NetworkTick` of its behaviours | not called; the next call gets the time since the last one as `deltaTime` |
| container resolution, crossing a frame, the owner check and handover | not checked |
| the ghost band's seam test | not made; its ghosts' linger is lengthened by the interval, so they do not lapse between checks |
| the interest index (rebucketing) | not moved |
| the root transform entry | not captured, unless a teleport is waiting; a change of container or epoch is still published at once |

Which ticks are update ticks is decided once per tick (`NetworkIdentity.BeginTick`) and read by every pass, so the
passes agree. Entities with the same interval are spread over it by net id (`(tick + netId % n) % n == 0`), and a
gap as long as the interval is always an update, so an entity that has just arrived by spawn or handover, or whose
interval changed, updates on its next tick and then falls into its slot. State history is still recorded every
tick (it is cheap and keeps `StateAt` continuous). Variables, maps, RPCs, sync-channel behaviours and audiences are
still sent when they change: they are event-driven, and a game that changes them in `NetworkTick` changes them on
update ticks anyway. The interval travels with a handover (a trailing byte in `AuthorityTransfer`, protocol 24).

*Why the whole update and not only the send rate:* §1.1 says the send is not what costs a worker; capturing,
resolving and seam-testing every entity every tick is. An entity updated every 30 ticks costs a thirtieth of that.

### D2. The recovery entry is sent once an entity settles

`NetworkTransform.CaptureRoot` schedules its reliable recovery entry `SettleTicks` (30) after the **last** change,
and sends it only on a tick with no change: every change pushes it back. An entity that keeps moving sends none;
one that stops sends one, where it came to rest. That is the entry the mechanism exists for (an unreliable final
entry may be lost and nothing would resend it), and it is also what a client whose distance tier skipped the last
unreliable entries needs. Tests: `TransformReplicationTests.LostFinalUpdateGetsReliableRecoveryThenIdleCostsNothing`
(now counted from the last change), `RelevanceTierWorkerTests.AMovingRootSendsOneRecoveryEntryOnlyOnceItHasSettled`.

### D3. A relevance priority per entity, applied by the gateway per client

`NetworkIdentity.RelevancePriority` shifts the gateway's distance tiers for that entity, for every client:

| Priority | Near (≤ `InterestNearRadius`) | Middle (≤ `InterestFarRadius`) | Far |
|---|---|---|---|
| `High` | every update | every update | `InterestMidDivisor` |
| `Normal` (default) | every update | `InterestMidDivisor` | `InterestFarDivisor` |
| `Low` | `InterestMidDivisor` | `InterestFarDivisor` | `InterestFarDivisor` |
| `Background` | `InterestMidDivisor` | none | none |

"None" is the ability to stop sending to a client without despawning the entity there: the client keeps the
replica where it was last told, and is still sent every reliable entry (where it came to rest, D2; a teleport; a
change of container), so it is never left somewhere the entity was not. A client's own pawn is always sent every
update. The priority rides in bits 1–2 of the spawn's `interest_flags` (protocol 24), so the standalone gateway
needs no prefab; a change on the authority re-announces the entity to the gateways that have it (an in-place
spawn), so it is meant to change with an entity's role, not per tick. It travels with ghost spawns and handovers
in the same flags.

### D4. A client's rate is one entry per window, not "ticks divisible by the divisor"

The gateway used to send an unreliable entry to a client when `(tick + netId % d) % d == 0`. That only works when
the worker sends every tick: an entity sent every 6 ticks to a client with `d = 4` matched on no tick at all for
some net ids and was frozen on that client. The rule is now `RelevanceTiers.StartsWindow`: send the first entry of
the entity that falls in a new window of `d` ticks (windows staggered by net id), measured against the tick of the
entity's previous entry, which the gateway keeps per record. For an every-tick entity it sends exactly what the old
rule did; for an entity sent every `n` ticks it sends `min(1/n, 1/d)` of them. Tests: `RelevanceTiersTests`
(every net id, intervals 1, 6 and 30 against windows 4, 12 and 60), conformance scenario 35.

### D5. Protocol 24, additive for clients

The handover gains a trailing update section (one byte, written only when the interval is above 1; an extent
section written only to reach it says "none" with source byte `0xFF`), and the spawn's interest flags gain the
priority bits. Nothing a client reads changed: a protocol-23 client ignores the flag bits. The window is 23..24.
Workers and gateways must still match exactly.

### After: what the same crowds cost now

Worker (layer `editor`, one worker, everything walking at 1.5 m/s):

| Scenario | Tick avg / p95 / max (ms) | To the gateway | Entries/s |
|---|---|---:|---:|
| 150 every tick + 1,500 every 30 ticks | **1.46** / 1.59 / 4.44 | 563 kB/s | 12,000 |
| 150 every tick + 1,500 every 60 ticks | 1.38 / 1.59 / 6.65 | 493 kB/s | 10,500 |
| 150 every 6 ticks + 1,500 every 30 ticks | 0.95 / 1.12 / 3.37 | 211 kB/s | 4,500 |
| 150 every 6 ticks + 1,500 every 60 ticks | **0.79** / 1.06 / 4.85 | 141 kB/s | 3,000 |
| the sample: 1,000 wanderers across two workers (150 every 6 ticks, 850 every 60 at `Background`) | 0.32 and 0.29 | 57 + 53 kB/s | 2,348 |

against 7–7.6 ms for 1,650 every-tick entities before (§2 point 1). The sample's crowd handed 1.9 walkers a second
across the seam and held 74 ghosts.

Client (layer `synthetic`, 200 active walkers updated every 6 ticks around the client, recovery once settled):

| Also in view | Gateway tiers | Entry | kbit/s | Entries/s |
|---|---|---|---:|---:|
| — | default | default (46 B) | 608 | 1,558 |
| 1,000 `Background` standing | default | default | 609 | 1,559 |
| 1,000 `Background` walking, every 30 ticks | default | default | 664 | 1,706 |
| 1,000 `Background` walking, every 30 ticks | crowd: near 25 m, every 20th tick to 60 m, every 60th beyond | default | 210 | 499 |
| the same | crowd | yaw only, no velocity (32 B) | 154 | 503 |
| the same | crowd | yaw only, half floats (24 B) | 121 | 501 |
| 1,000 `Background` walking, every 60 ticks | sample: near 25 m, every 30th tick to 60 m, every 120th beyond | yaw only, half floats | **87** | 323 |

The last row is the issue's client target (at most 100 kbit/s with about 200 active and 1,000 light entities in
view, the rate falling off with distance) and is asserted by `ServerOwnedBandwidthTests.WithRelevanceTiers`. It
needs all three levers: the worker's interval, the gateway's tiers set for a crowd, and entries that carry only
what a walker needs. What is left is mostly the entry itself: 16 of its 24 bytes are the net id, epoch, container
and field mask (§4, compact encoding). Bytes are application payload; a datagram's UDP and IP headers come on top.

### What a game does

- Give every server-owned character an `UpdateInterval` that matches what it is doing: every tick or every few
  for the ones near players or in combat, 30–60 for the rest. Change it when the character's situation changes.
- Give the numerous ones `RelevancePriority.Background` (or `Low`), and the few that matter from afar `High`.
- Give the ones that only matter near players `SleepWhenUnobserved` (§6), or put them to sleep and wake them from
  game logic.
- Move by the `deltaTime` `NetworkTick` is given, and carry everything `NetworkTick` reads in
  `WriteHandoverState`/`ReadHandoverState` (§3).
- Sync only the transform fields a character needs on its root `NetworkTransform` (yaw only, no velocity, half
  floats where the container is small enough).
- Set the gateway's distance tiers for the game's own mix (`InterestNearRadius`, `InterestFarRadius`,
  `InterestMidDivisor`, `InterestFarDivisor`), and give the crowd's priorities tiers of their own, so players keep
  theirs (§7, D10).
- Rate the sync state of behaviours a far client needs only the gist of (§7, D11).

## 6. Dormancy (built)

An entity that nobody needs should cost nothing, not a thirtieth of an entity. `NetworkIdentity.Sleep(wakeOnInterest)`
puts a server-owned entity to sleep from the next tick; `Wake()` wakes it; `IsDormant` says whether it sleeps (on
the authority; a ghost or a client copy is never asleep). `SleepWhenUnobserved` (seconds, 0 = never, on the prefab
or the authority) lets the worker do it: once no gateway has watched the entity for that long, it falls asleep,
waking on interest. `NetworkBehaviour.OnSleep`/`OnWake` are called on the authority at each change.

### D6. What sleeping means

| | While asleep |
|---|---|
| `NetworkTick` | not called |
| container, frame crossing, ghost band, interest bucket | not checked (it does not move) |
| root transform | not sent; on the tick it falls asleep, one reliable entry with its pose and zero velocity, so every holder has it where it rests |
| variables, maps, sync state, audiences, priority | sent if the game changes them, to whoever watches it then (its mask is computed on demand) |
| its replicas on clients and the gateway's copy | kept: a gateway that subscribes its region is sent its spawn as usual |
| its ghosts on neighbouring workers | kept where it rests (their linger does not run); they go if it is handed away or the peer is lost |
| state history | still recorded every tick, so `StateAt` answers for a sleeping entity |
| authority, epoch, state | kept |
| the lease of its container | followed: if the container is dealt to another worker, the entity is handed over and stays asleep |

A client's entity cannot sleep (the call is ignored with a warning): a pawn is observed by definition.

### D7. Watching is a gateway's subscription

"Watched" is what the worker already computes every tick for every entity: its publish mask, the set of gateways
that subscribe its region, name it explicitly, reach it as a wide entity or, for an always-relevant one, are linked
at all. An awake entity with `SleepWhenUnobserved` whose mask has been empty for that many seconds of ticks falls
asleep. A sleeping entity that wakes on interest is asked for its mask once per interest evaluation
(`InterestEvalHz`, 4 a second): the worker sweeps a fifteenth of the sleepers on each tick, so the cost is spread and
an entity wakes within 250 ms of a gateway subscribing its region. Gateways subscribe regions a margin ahead of their
clients' interest radius (`InterestSubscribeMargin`, about three seconds at sprint), so a character wakes before a
client can see it. Counting in ticks, not wall time, keeps it deterministic.

### D8. Following a lease without looking every tick

An entity that is not resolved on a tick (asleep, or between its updates) cannot cross a seam, but its container's
lease can move (a rebalance, a worker draining). Asking every such entity whose container it is every tick cost
about 1 µs each in the Editor, which would have made 1,500 sleepers cost more than 1,500 light entities. So the
registry counts ownership changes (`ContainerRegistry.OwnershipVersion`, raised only when a container's owner
actually changes), and the worker asks these entities only on a tick after it changed, or again after a handover it
could not make (a peer not connected, a crossing held).

### D9. On the wire

The handover's update section (protocol 24) gains a dormancy byte (asleep; wakes on interest) and the entity's
`SleepWhenUnobserved` (f32), written when either is set, the interval then written as 1 if it was not above 1.
Nothing a gateway or client reads changed.

### What sleepers cost

| Scenario (layer `editor`, one worker) | Tick avg / p95 (ms) |
|---|---|
| 150 walkers every 6 ticks + 1,500 asleep | **0.43** / 0.46 |
| 150 walkers every 6 ticks + 5,000 asleep | 1.01 / 1.10 |
| for comparison: 150 every 6 ticks + 1,500 awake every 60 ticks | 0.78 / 0.83 |
| the sample (1,000 wanderers across two workers, light ones `SleepWhenUnobserved` 2 s) with one client's gateway watching a 120 m square in a corner: 749 asleep | 0.21 and 0.17 per worker |

About 0.12 µs per sleeping entity per tick, against 4.2 µs for an entity updated every tick. Tests: conformance
scenario 36 (`ConformanceDormancyTests`, 4 tests), `ConformanceHandoverWireTests.DormancyRidesInTheUpdateSectionAfterTheInterval`
(both places), `ServerOwnedCostMeasurementTests.DormantCrowd`.

### Limits

- Dormancy does not keep a chunk loaded, and never did for a server-owned entity: a runtime chunk is retired when no
  player needs it, and what is in it is saved (if persistent) and despawned, asleep or not.
- Dormancy, like the update interval and the priority, is not persisted: a restored entity starts awake, with its
  prefab's settings, unless the game sets them again.
- A sleeping entity that does not wake on interest stays asleep however close a player comes: `Wake` is the
  game's to call.

## 7. Distance tiers per priority, and distance-rated sync state (built)

A game measured the gap §5 left (200 NPCs in one client's view, each with `UpdateInterval` 2–6 and
`RelevancePriority.Low`): 535 kbit/s with the default tiers, 295 in a camp scene. Crowd tiers (near 25 m, every 30th tick
to 60 m, every 120th beyond) would have brought it to 116–144 kbit/s, but the tiers were one global setting, so they
would also have slowed every player and ship beyond 25 m to 2 Hz. And a swarm, one entity carrying its members' offsets
as sync state, was sent that state as often to a client 100 m away as to one beside it: sync state was not rated by
distance at all.

### D10. A priority can have tiers of its own

`NebulaConfig.InterestHighTiers`, `InterestLowTiers` and `InterestBackgroundTiers` are `RelevanceTierBands`: `Override`,
`NearRadius`, `FarRadius`, and a divisor per band, `NearDivisor`, `MidDivisor`, `FarDivisor` (ticks per update window, as
everywhere in the tiers; 0 is none). With `Override` off, the default, the priority keeps the shift of the global tiers
in D3, so a config that sets none of this sends exactly what it did. `Normal` has no override: the global tiers
(`InterestNearRadius`, `InterestFarRadius`, `InterestMidDivisor`, `InterestFarDivisor`) are its tiers, which is what lets
a game slow a crowd and not its players.

`InterestSettings.TiersFor(priority)` resolves a priority's bands (`RelevanceTiers.Resolve`: its own, or the shifted
global ones), and the gateway resolves all four once, when it reads its config. Per client and entry it picks the
bands by the record's priority and takes the divisor at the client's distance (`RelevanceTiers.DivisorAt`); the window
rule (D4) and the client's own pawn (always every update) are unchanged. `InterestSettings.Validate` repairs an
override's fields as it does the global ones (radii not negative, in order, inside `InterestRadius`; divisors not
negative), naming the field, e.g. `InterestLowTiers.FarRadius`. The fields are exported to the standalone services with
the rest of the config.

Nothing on the wire changed: the priority already rides the spawn's interest flags (D3), and only the gateway reads the
tiers.

### D11. Sync state can follow the entity's tier

`NetworkBehaviour.SyncDistanceRating` (configuration, read once at spawn like `SyncAudience`):

| Value | For a behaviour that | A client in the every-update band | A client further away |
|---|---|---|---|
| `Off` (default) | — | every chunk | every chunk |
| `Keyframes` | writes deltas | every chunk | keyframes only, one per window of its rate |
| `WholeState` | writes its whole state every time (the worker passes `full: true`; every chunk is flagged `Full`) | every chunk | one chunk per window of its rate |

"Every-update band" is the band whose divisor is 1 for the entity's priority at that client's distance; the client's own
pawn is always in it. A rated chunk carries `SyncStateCodec.ChunkFlags.DistanceRated` (bit 4), so the standalone gateway
needs no prefab.

**Why keyframes only.** A delta is relative to the state before it; a client that skipped one cannot apply the next.
So the gateway keeps, per client and entity, a bit per rated behaviour (the first 64 behaviours; later ones are sent as
if unrated) that says the client has missed a chunk. A client that is behind gets no delta until a keyframe has caught
it up; a client beyond its every-update band gets only keyframes, and only the one that opens a new window of its
rate (`RelevanceTiers.StartsWindow`, measured from the previous message that carried a rated keyframe, per stream).
The bit is cleared by any keyframe the client is sent, by a spawn the worker sends in place (a handover), and when the
entity leaves the client's set; a spawn from the gateway's cache marks the client behind on each rated behaviour whose
cached keyframe is older than the newest sync state relayed, since the deltas after it are not in the spawn.

**Nothing is lost, only coalesced.** A rated behaviour that has written a chunk while dirty owes one keyframe once it
settles: `NetworkIdentity.SyncSettleTicks` (30) after its last change, on a tick it is not dirty, the worker writes a
`Full | Settled | DistanceRated` chunk on the reliable stream, whatever the behaviour's own delivery (a sequenced
behaviour's settle is not also written on the sequenced stream). A change pushes it back, as the recovery entry of D2.
The gateway sends a `Settled` chunk to every client that holds the entity, near or far or quiet, and it clears the
client's missed bit. So a far client ends with the latest state once the state stops changing, and a client that comes
closer while it still changes gets the next keyframe (at most `SyncKeyframeInterval` ticks for a sequenced behaviour,
which writes one then whether dirty or not; for a reliable one, the next keyframe it writes while dirty, or its settle)
and every chunk after it. The debt is decided from state `ClearDirty` advances, so every destination of a tick is
written the same chunks; a sleeping entity stays in the publish pass until its settle has gone
(`NetworkIdentity.SyncSettlesPending`).

**What it is not for.** Events a client must see each of (an animator trigger) are lost to a far client by design:
keep them in an RPC or an unrated behaviour. `NetworkVariable`s are not rated: a variables message is the whole
variable block, sent reliably on each change, and rating it would need the gateway to hold and flush a pending copy
per client; a game with high-rate state puts it in sync state. The root `NetworkTransform` has its own stream, rated by
D3/D10 already, and ignores the setting.

**On the wire.** Two chunk-flag bits, meaningful to the gateway only; clients and ghost workers read `Full` and
`Cleared` as before and ignore them, and the gateway's cached keyframes keep only the audience bits. No protocol bump:
a protocol-24 gateway relays rated chunks to everyone as it always did, and a new gateway with an older worker sees no
rated chunk. Workers and gateways of a mesh must still run the same build.

### What it costs now

Client (layer `synthetic`, `ServerOwnedBandwidthTests.WithPriorityTiers`): 200 active walkers at `Low` updated every 6
ticks and 1,000 at `Background` every 60, walking, recovery once settled, plus another player: a `Normal` entity updated
every tick, 40 m from the client (the middle band of the default tiers).

| Tiers | Entry | kbit/s | Entries/s | The player's entries/s |
|---|---|---:|---:|---:|
| default | default (46 B) | 455 | 1,150 | 14.9 |
| global crowd tiers (near 25 m, every 30th tick to 60 m, every 120th beyond) | default | 87 | 168 | **2.0** |
| `InterestLowTiers` 25 m / 60 m, 1 / 30 / 120; `InterestBackgroundTiers` 25 m / 60 m, 30 / 0 / 0 | default | 150 | 340 | 15.0 |
| the same | yaw only, half floats (24 B) | **90** | 340 | **15.0** |
| the same + 20 swarms at `Low`, a 200-byte sync blob every 6 ticks, unrated | yaw only, half | 459 | 365 (+200 sync messages/s) | 14.9 |
| the same + 20 swarms, `WholeState` | yaw only, half | **139** | 364 (+24 sync messages/s) | 15.0 |

The fourth row is the issue's client target (at most 100 kbit/s with about 200 active and 1,000 light entities in view)
with a player unaffected, asserted by the test. The global crowd tiers are cheaper because they also slow `Low` inside
25 m (the shift makes its near band the middle divisor); a game that wants that sets `NearDivisor` above 1. The swarm
rows are the second gap: rated, a far client gets one 200-byte blob every 30 or 120 ticks instead of ten a second.
Tests: conformance scenario 38 (`ConformancePriorityTierTests`, 4 tests: rates per priority and band with `Normal`
unchanged, the shift without overrides, rated sync state per band with its settle keyframe, a client coming closer),
`SyncDistanceRatingTests` (the worker: flags, whole state, the settle keyframe, a sequenced behaviour settling reliably,
a sleeper staying in the publish pass, the root transform), `RelevanceTiersTests` and `InterestSettingsTests` (both
places).

## 8. Running the measurements

- Editor layer: `Unity -batchmode -projectPath <checkout> -runTests -testPlatform EditMode -testFilter
  Nebula.Tests.ServerOwnedCostMeasurementTests -testResults <file>`; the tests are `[Explicit]`, so an ordinary run
  never picks them up. Output: `Logs/scale/editor-server-owned.csv` (layer `editor`, one row per scenario, the
  profile sections in the last column).
- Synthetic layer: `dotnet test Services~/Nebula.Services.slnx --filter FullyQualifiedName~ServerOwnedBandwidthTests`.
  Output: `Logs/scale/synthetic-server-owned-bandwidth.csv`.
