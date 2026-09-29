# Many server-owned entities — measurements and design (NEB-359)

Status: measurements landed (this document, §1–§2). Relevance tiers and dormancy follow in their own changes (§4).
Harness: `Tests/EditMode/CrowdMesh.cs` (two real workers), `Tests/EditMode/ServerOwnedCostMeasurementTests.cs`
(`[Explicit]`, writes `Logs/scale/editor-server-owned.csv`), `Services~/Nebula.Services.Tests/ServerOwnedBandwidthTests.cs`
(`[Category("Scale")]` + `Soak`, writes `Logs/scale/synthetic-server-owned-bandwidth.csv`). Conformance scenario 34
(`ConformanceCrowdSeamTests`). Related: `docs/scale-suite.md`, `docs/interest-management.md` §4 (rate tiers),
`docs/entity-extents.md` (the ghost band).

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

Built in separate changes, each with its own tests, sample and user guide:

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

## 5. Running the measurements

- Editor layer: `Unity -batchmode -projectPath <checkout> -runTests -testPlatform EditMode -testFilter
  Nebula.Tests.ServerOwnedCostMeasurementTests -testResults <file>`; the tests are `[Explicit]`, so an ordinary run
  never picks them up. Output: `Logs/scale/editor-server-owned.csv` (layer `editor`, one row per scenario, the
  profile sections in the last column).
- Synthetic layer: `dotnet test Services~/Nebula.Services.slnx --filter FullyQualifiedName~ServerOwnedBandwidthTests`.
  Output: `Logs/scale/synthetic-server-owned-bandwidth.csv`.
