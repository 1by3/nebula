# Interest-management "after" numbers

Recorded 2026-09-21 on `main` with the interest-management work (protocol v17) in place, plus the integration
fixes listed in §6 and the pawn-recovery fix of §7 (added later the same day; every smoke output below the
line in §7 is from builds that include it, everything above it is not). Nebula CLI `0.1.0-alpha.28`, Unity `6000.6.0f1`, Windows 11 Pro 26200.
Compare against [`baseline.md`](baseline.md). Raw logs and `/api/state` samples of the **pre-fix** runs are
under [`raw/shooter-after/`](raw/shooter-after/), [`raw/shooter-after-smoke/`](raw/shooter-after-smoke/),
[`raw/virtualworld-after/`](raw/virtualworld-after/) and
[`raw/virtualworld-after-smoke/`](raw/virtualworld-after-smoke/) — the last of those is the failing run §7
takes apart. The post-fix runs are quoted in §7.5; `smoke-test.ps1` clears `Builds/Win64/Logs` on start, so
each run overwrote the last and their log directories are not kept here.

---

## 0. Summary

| | shootergame | virtualworld |
|---|---|---|
| Typecheck vs live package | pass (13 known QFSW errors only) | pass (0 errors) |
| Package EditMode tests | **415 / 415** (baseline 265) | same suite |
| `Services~` tests | **231 / 231** (baseline 226) | same suite |
| Mesh run | 4w / 60 NPC / 8 bots, 210 s | 4w / 8 bots, 210 s, fresh persistence |
| **Mean replica entities per client** | **40.0** of 221 (was 104 of 173) | **18.5** of 117 (was 187 of 187) |
| **Mean inbound KB/s per client** | **20.0** (was 46.0) | **12.3** (was 24.6) |
| Errors / exceptions | 0 | 0 |
| Handover failures | 0 | 0 |

The headline: a client no longer holds most of the world. In virtualworld, where every client used to replicate
**100 %** of every entity in existence, a client now holds **16 %** — and in the observer soak (§4.3) a
stationary client's set and bandwidth stay completely flat while the world grows around it.

---

## 1. Exact commands

```powershell
$env:PATH = "C:\Users\jesse\.nebula-cli\bin;$env:PATH"

# per project, from the project directory
nebula build
nebula start --workers 4 --npcs 60 --bots 8 -v --yes          # shootergame
nebula start --workers 4 --bots 8 -v --yes --reset-persistence # virtualworld

# smoke tests (both exit non-zero on failure)
powershell -File C:\Dev\nebula-shootergame\Tools\smoke-test.ps1 -BotMode ship -Bots 8 -Npcs 60 -Seconds 180
powershell -File C:\Dev\nebula-virtualworld\Tools\smoke-test.ps1 -Mode observer -Bots 8 -Seconds 210 -ResetPersistence
powershell -File C:\Dev\nebula-virtualworld\Tools\smoke-test.ps1 -Mode line     -Bots 8 -Seconds 210 -ResetPersistence

# unit tests
dotnet test C:\Dev\nebula\Packages\com.1by3.nebula\Services~\Nebula.Services.slnx
Start-Process -Wait "$env:ProgramFiles\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" -ArgumentList @(
  '-batchmode','-nographics','-projectPath','C:\Dev\nebula',
  '-runTests','-testPlatform','EditMode','-testResults','editmode.xml')
```

`/api/state` was sampled every 15 s for 210 s (14 samples per run). Ports are unchanged: shootergame dashboard
7080 / gateway 7000, virtualworld dashboard 7180 / gateway 7200.

---

## 2. Tests

| Suite | Before | After |
|---|---|---|
| `C:\Dev\nebula` EditMode | 265 / 265 | **415 / 415** |
| `Services~` (`dotnet test`) | 226 / 226 | **231 / 231** |
| Soak (`--filter TestCategory=Soak`) | 200 / 2000 entities | **2 000 / 20 000 entities**, 151 s total |

Two pre-existing EditMode tests failed against the new code and were corrected (§6.1). The counts rose again
with the §7 fix: three fleet handover tests in `InterestTests` and one `InterestSettingsTests` case (the
EditMode test files are compiled into `Services~` too, which is why that suite gained four and not three). The soak was raised to
2 000 / 20 000 entities per shape at the same local density, as asked; each shape now takes ~25 s, well inside
the 2-minute budget. Regenerated table in [`soak-results.md`](soak-results.md):

| Shape | World entities | Replicas | Bytes/s to client | Gateway cache | Worker links |
|---|---:|---:|---:|---:|---:|
| zones (3 workers) | 2 000 | 9 | 10 259 | 13 | 3 |
| zones (3 workers) | 20 000 | 9 | 10 463 | 13 | 3 |
| grid (4 workers) | 2 000 | 9 | 12 979 | 13 | 4 |
| grid (4 workers) | 20 000 | 9 | 6 386 | 13 | 4 |
| one container | 2 000 | 9 | 10 382 | 13 | 1 |
| one container | 20 000 | 9 | 10 463 | 13 | 1 |

A tenfold world costs a client nothing: replicas, cache and links are identical at both sizes, with gaps,
duplicates and leaks all zero.

---

## 3. shootergame

### 3.1 Per-client (8 bots, roaming, t = 210 s)

| Bot | Replicas | Inbound KB/s | states/s | pkt/s | corrections | beyond | dup | carrierless |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| bot1 | 40 | 17.4 | 175 | 176 | 0 | 0 | 0 | 0 |
| bot2 | 44 | 22.1 | 260 | 215 | 0 | 0 | 0 | 0 |
| bot3 | 40 | 18.4 | 201 | 177 | 0 | 0 | 0 | 0 |
| bot4 | 45 | 19.9 | 206 | 202 | 0 | 0 | 0 | 0 |
| bot5 | 44 | 23.3 | 291 | 229 | 0 | 0 | 0 | 0 |
| bot6 | 45 | 20.5 | 223 | 198 | 1 | 0 | 0 | 0 |
| bot7 | 18 | 14.5 | 108 | 133 | 0 | 0 | 0 | 0 |
| bot8 | 44 | 23.9 | 313 | 202 | 3 | 0 | 0 | 0 |
| **mean** | **40.0** | **20.0** | **222** | **192** | | | | |
| **total** | | **160 KB/s** | | | | | | |

### 3.2 Before / after

| Metric | before | after | change |
|---|---:|---:|---|
| Mean replicas per client | 103.6 | **40.0** | **−61 %** |
| Worst client's replicas | 141 | 45 | −68 % |
| Client's share of the gateway's world | ~60 % | **26 %** | |
| Mean inbound KB/s per client | 46.0 | **20.0** | **−57 %** |
| Total client bandwidth (8 bots) | 368 KB/s | **160 KB/s** | −57 % |
| Gateway entries in / s | 2 751 | 2 155 | −22 % |
| Gateway entries sent / s (fan-out) | 3 627 | 2 086 | **−42 %** |
| Fan-out amplification (sent / in) | **1.32×** | **0.97×** | |
| Gateway cached entities | 173 | 153 | −12 % |
| Worker tick ms (avg) | 1.3 – 1.7 | 1.6 – 2.0 | comparable |
| Worker `publish` per tick | 0.04 ms | 0.04 ms | unchanged |
| Worker `interest` per tick | n/a | **0.03 – 0.04 ms** | new |
| Handover failures | 0 | **0** | |
| Errors / exceptions | 0 | **0** | |

New gateway fields (all zero before the fix in §6.3): cached entities **153**, subscribed regions **154**,
worker links **4** (`region=4,foci=4,global=0,explicit=0,spawn=0,owned=4`), interest set avg **40.6** / max
**47**, interest eval **0.091 ms avg / 1.089 ms max**, bytes per client avg **21.6 KB** / max **27.4 KB**.

Per-worker interest (t = 195 s): regions 23–77, one gateway each, filter **0.026 – 0.047 ms/tick**, entries sent
416/416, 553/553, 669/789, 492/508; no partition warnings.

### 3.3 Handovers, combat, prediction

| Metric | before | after |
|---|---:|---:|
| Handovers out / in | 878 / 890 | **2 001 / 2 032** |
| Handover failures | 0 | **0** |
| Cross-worker hits | (not counted) | 83 |
| Kills | (not counted) | 428 |
| Client `corrections` | 0 | 0 – 3 |
| Client `starved` | 0.0 % | 0.0 % |

Handovers went **up**, not down, and this is a consequence of a fix rather than of interest management: the
baseline's bots were not all in the world. Two of eight spawned into empty filler cells with no ground mesh and
fell through y = 0 for the whole run. Before v17 that was invisible — a falling bot still replicated the whole
world and its log looked like everyone else's — and it is exactly what the probe's empty interest set now
exposes. With §6.2's spawn and fall-out fixes all eight bots are in the populated compound, which has ~70 small
containers, so every bot crosses container boundaries constantly. The health signal (0 failures, out ≈ in) is
unchanged. Intermediate runs bear this out: 585 handovers with the old spawn behaviour, 770 with the spawn fix,
2 001 with all eight bots alive and roaming.

### 3.4 Smoke tests

`Tools/smoke-test.ps1` keeps its original output and adds the interest block. Full outputs in
[`raw/shooter-after-smoke/`](raw/shooter-after-smoke/).

| Run | Result |
|---|---|
| `-BotMode ship -Bots 8 -Npcs 60 -Seconds 180` | **all checks passed** (exit 0) |
| `-BotMode idle -Bots 8 -Npcs 60 -Seconds 150` | **all checks passed** (exit 0) |
| defaults (4 workers, 2 bots, 0 NPCs, 60 s) | all checks passed (exit 0) |

The ship run is the carrier test: `bot1` spawns a Starhopper, boards the pilot seat and holds full throttle
across cells and worker boundaries while the other seven roam and observe it.

```
  PASS  every bot has a pawn
  PASS  nothing lingers past the exit radius (longest run of beyond>0 samples: 1)
  PASS  no entity spawned twice without a despawn (dup=0)
  PASS  every carried replica arrives with its carrier (carrierless=0)
  PASS  the ship bot boarded a Starhopper
  PASS  a client holds far less than the gateway does (mean replicas 31.5 < 124 = 80% of 155 cached)
```

`carrierless = 0` for every bot over the whole 180 s: no observer ever saw the pilot without the ship or the
ship without the pilot. The pilot's own probe shows `limit=255.9` against the others' `160`, which is the
speed-aware slack of §6.4 doing its job.

Idle bots (`-sg-bot-idle`, the mode the baseline said did not exist): 29–45 replicas, mean 41.4, against a
gateway cache of 134.

---

## 4. virtualworld

Run at the same elapsed time and bot count as the baseline (t ≈ 210 s, 8 bots), with fresh persistence so the
world starts empty as the baseline's did.

### 4.1 Per-client (8 bots, wandering, t = 210 s)

| Bot | Replicas | Inbound KB/s | states/s | pkt/s | beyond | dup | orphans |
|---|---:|---:|---:|---:|---:|---:|---:|
| bot1 | 18 | 10.4 | 60 | 126 | 0 | 0 | 0 |
| bot2 | 23 | 15.3 | 132 | 135 | 0 | 0 | 0 |
| bot3 | 10 | 12.1 | 95 | 131 | 0 | 0 | 0 |
| bot4 | 25 | 13.4 | 124 | 134 | 0 | 0 | 0 |
| bot5 | 14 | 11.4 | 72 | 131 | 0 | 0 | 0 |
| bot6 | 8 | 11.0 | 72 | 130 | 0 | 0 | 0 |
| bot7 | 24 | 12.3 | 102 | 130 | 0 | 0 | 0 |
| bot8 | 26 | 12.3 | 98 | 131 | 0 | 0 | 0 |
| **mean** | **18.5** | **12.3** | **94** | **131** | | | |
| **total** | | **98 KB/s** | | | | | |

### 4.2 Before / after

| Metric | before | after | change |
|---|---:|---:|---|
| Props (`serverDriven`) at t ≈ 200 s | 135 | 107 | comparable |
| Total entities | 156 | 117 | comparable |
| **Mean replicas per client** | **187 (100 % of the world)** | **18.5 (16 %)** | **−90 %** |
| Mean inbound KB/s per client | 24.6 | **12.3** | **−50 %** |
| Total client bandwidth (8 bots) | 196 KB/s | **98 KB/s** | −50 % |
| Gateway entries in / s | 484 | 481 | unchanged |
| Gateway entries sent / s | 1 079 | **683** | **−37 %** |
| Fan-out amplification | **2.23×** | **1.42×** | |
| Gateway cached entities | 187 | 113 | −40 % |
| Worker tick ms | 0.39 – 0.69 | 0.44 – 1.10 | comparable |
| Worker `publish` per tick | 0.02 ms | 0.01 – 0.02 ms | unchanged |
| Worker `interest` per tick | n/a | **0.01 – 0.02 ms** | new |
| Handovers out / in | 471 / 471 | 139 / 139 † | |
| Handover failures | 0 | **0** | |
| Errors / exceptions | 0 | **0** | |

† Not comparable run to run: how many crossings are *cross-worker* handovers rather than *local* container
changes depends on how the chunk leases happen to be distributed, and that varies a lot between runs on a
freshly reset world. Out and in match exactly, and there are no failures.

Gateway interest fields: cached **113**, subscribed regions **49**, worker links **4**
(`region=4,foci=4,global=0,explicit=0,spawn=0,owned=3`), set avg **15.4** / max **25**, interest eval
**0.023 ms avg / 0.047 ms max**, bytes per client avg **13.2 KB**.

### 4.3 The observer soak — the exit test

`Tools/smoke-test.ps1 -Mode observer` stands `bot1` still at the origin and sends the other seven walking +X for
ever, each dropping a prop every three seconds. What a distant, idle client is sent must not follow the world:

```
  idle bot1 replicas: first half 0.0 -> second half 0.0; world props 9 -> peak 111 -> 107
  PASS  the world is far bigger than the idle client's set (peak 111 props vs 0.0 replicas)
  PASS  a distant idle client's replica count stays flat as the world grows (0.0 -> 0.0)
  idle bot1 inbound: first half 5.0 KB/s -> second half 4.2 KB/s
  PASS  and so does its inbound bandwidth (5.0 -> 4.2 KB/s)
  runtime containers: 330 distinct seen, 190 held at the end, so at least 140 retired
  PASS  idle chunks are retired behind the bots (140 at least)
  PASS  the floating origin shifted under a travelling client (5 distinct origin cells)
  PASS  no replica is stuck in a chunk this client never loads (longest run of orphans>0 samples: 0)
  PASS  every carried replica arrives with its carrier (carrierless=0)
  PASS  the gateway caches less than the whole world (45 < 127)
```

An earlier observer run before the §6.5 fix shows the same shape with a busier world: bot1 flat at **22.0
replicas both halves** and **11.2 → 10.8 KB/s** while props went 248 → peak 318. That is the claim of the whole
change, measured.

Origin shifts: walking bots pass through 4–5 distinct floating-origin cells per run with no discontinuity —
`beyond`, `dup`, `orphans` and `carrierless` all stay at zero across every shift, and there are no exceptions.

**That run still failed four checks.** It is kept here as the before picture; §7 has the root cause,
the fix and the runs that pass.

---

## 5. What could not be measured, and why

- **Visual pop-in, and any human-client judgement.** There is no human in this environment. The scripted proxy
  is the probe's `dmin` for newly-entered replicas together with `beyond`/`dup`: an entity appearing deep inside
  the radius rather than at its edge, or appearing twice, is what pop-in looks like in a log. Both are clean.
- **Two gateways in one local mesh.** The CLI starts exactly one gateway (`LocalMesh` has no gateway count), so
  the multi-gateway paths — mask bit allocation across links, `EntityRedirect`, the >64-gateway fallback — were
  exercised only by `Services~` fleet tests (`Fleet(gateways: 2)`), not on a real mesh. Building that into the
  CLI was out of scope.
- **Private-instance behaviour end to end in the shooter.** The shooter has private quarters
  (`InstanceTemplate` / `InstanceBoundary`), but no bot ever entered one in any run — `instance` appears zero
  times in every worker and gateway log. Instance isolation and `ObservePublic` under interest management are
  covered by `InterestTests.InstanceIsolationAndObservePublicSurviveInterestManagement` and `InstanceTests`,
  which run the real gateway, but not by a mesh run. Risk (d) is therefore **unverified end to end**.
- **The Arena scene.** `GameScene` is `Corporation` and the CLI passes no `-nebula-scene` to workers or the
  gateway, so the Arena cannot be brought up as a mesh without editing `NebulaConfig.asset` and rebuilding.
  It was **not run**. What *is* established is the zero-configuration claim it was meant to test: the shooter's
  config carries no `Interest*` fields at all beyond the pre-existing rate tiers, so every run above already
  used the C# defaults end to end.
- **Container allocate/retire counts** are still lower bounds derived from 15 s polling; no per-allocation log
  line exists (unchanged from the baseline).

---

## 6. Fixes made during integration

1. **Two pre-existing EditMode tests encoded pre-v17 assumptions** and failed against the new code.
   `WorkerBatchesOnlyOptedInEntitiesWithinSequencedPacketBudget` drove `PublishToGateways` with a gateway peer
   that had no mask bit and no subscription, for which "publish nothing" is now the correct answer; it now
   registers the link and subscribes the region the entities stand in.
   `GatewayMergesMaskedPoseVelocityAndRejectsStaleTicks` assumed a welcomed client sees everything; it now makes
   the client an observer explicitly. It also never set the record's `NetId`, which collided with the
   pawn-less client's `PawnNetId` of 0 — guarded in `NebulaGateway.WantsThisTick`.
2. **shootergame spawn placement and fall-out** (`ShooterGameMode`, `PlayerController`). The gateway picks a
   spawn container at random among its leases, and most of this world's twenty 2048 m cells are empty filler
   with no ground. `FindSpawn` now reports whether it found a floor, `FindGroundedSpawn` relocates to a
   container with an authored `SpawnPoint` when it did not, and a pawn below −50 m is put back on the ground.
   Two of eight bots were falling through the world for the whole baseline run; nobody could tell.
3. **`RemoteControlPlane.HeartbeatGateway` never sent the interest half of `GatewayStats`.** The orchestrator's
   `ReadGatewayStats` has always expected the keys, so every interest field on the dashboard and in `/api/state`
   read zero for any gateway reaching the control plane over HTTP — which is every gateway in a real mesh.
   Regression test: `ControlPlaneJsonTests.AGatewayHeartbeatCarriesItsInterestStatsOverTheRemoteControlPlane`.
4. **`InterestProbe`**: added `carrierless` (design D3's observable failure), `origin` (the floating-origin
   cell), `speed`, and `beyondIds`/`orphanIds` naming the offending entities. The distance limit is now
   speed-aware — the exit hysteresis legitimately leaves entities tens of metres behind a client travelling at
   60 m/s, so a fixed 24 m slack reported a leak every time anyone flew a ship. The probe is attached by
   `NebulaBootstrap` for `-nebula-bot` clients, so both games produce identical lines.
5. **Container ownership was never sent when an entity already in a set changed container.**
   `SendOwnershipUpserts` only ran for entities *entering* a set, which suffices in a baked world. In a chunked
   world a walking pawn changes container every few seconds without leaving anyone's set, so its observers were
   never told about the new chunk: they kept the replica, could not resolve its frame, and **froze it at its
   last known pose for ever**. It showed as a permanent `orphans=1` and a `beyond` distance growing at exactly
   the observer's own walking speed. Fixed by `SendOwnershipForContainerChange`, called from `OnWorldState` on
   any container change. Before: one permanently-stuck replica per walking bot. After: zero.
6. **`InterestSettings.NearCells` was one ring short.** It sized the content window to `ExitRadius`, but an
   entity only leaves after `LingerSeconds` beyond it, evaluated at `EvalHz`, so a client moving at
   `MaxFocusSpeed` legitimately still holds entities `MaxFocusSpeed × (linger + interval)` further out. The
   overshoot is now included. For virtualworld this takes the chunk window from 1 ring to 2 (the 5×5 = 25
   chunks its README already documented); for the shooter's 2048 m cells it changes nothing. Residual transient
   orphans dropped from 5.3 % of samples to 0.
7. **A carrier can no longer be a wide or global entity** (`NebulaWorker.InterestAdd`). `InterestIndex` moves a
   carrier's subtree only when the carrier is bucketed by region, so a dynamic container in the wide list would
   strand its passengers in whichever region they boarded in — breaking D3 exactly where D3 matters. Such a
   prefab is downgraded to region placement and logged once. This is why the Starhopper keeps the mesh default
   radius; see D38.
8. **A carried entity now inherits its root carrier's radius and always-relevance**, not only its position
   (`InterestSource.Describe`). It was taking the position from the root per D3 but the radius from itself, so a
   passenger with a different radius from its ship would still cross the boundary on its own tick. See D39.
9. **`GatewaySessionAdmission` removed a revoked link by key rather than by identity.** A revocation polled back
   after a replacement connection had taken the same session id, or after the transport recycled the peer id,
   would evict the connection just admitted. Now both dictionary removals check the value first. I could not
   construct a deterministic failing case against the in-process `LocalControlPlane` — its ordering happens to
   protect the sequence — so this is hardening for the asynchronous HTTP coordinator rather than a bug with a
   reproduction.
10. **`docs/interest-management.md` §14–§16** were three overlapping "what was built" sections with two `## 15`
    headings and three conflicting D13–D21 runs. They are now one §14 ("What was built") and one §15
    ("Decisions") with a single D1–D41 sequence, grouped by the phase each came from.
11. **Game bot modes.** `-sg-bot-idle` and `-sg-bot-ship` (shootergame, with `ShipBotDriver`);
    `-vw-bot-observer` and `BotMode.LineBuild` (virtualworld). `nebula start --bot-args` forwards them.

`EvictUnsubscribedRecords` (risk f) was **left O(cached)**. It is a scan of the gateway's own cache at
`InterestEvalHz`, which is 150 records four times a second and shows up in the measurement as a total interest
evaluation cost of 0.09 ms average / 1.09 ms worst per client evaluation. Making it O(change) means keying
eviction off regions leaving the subscription set, which loses the belt-and-braces catch for a record that
rebuckets *into* an unsubscribed region (today that is caught either by the worker's `EntityForget` or by this
scan). Given the measured cost, trading a correctness backstop for it was not worth it; noted rather than done.

---

## 7. The pawn-loss defect: root cause, fix, and the runs that pass

The blocker recorded here — *a client in the chunked world can permanently lose its pawn* — is fixed. This
section replaces the open-defect list; everything in it was measured on builds containing the fix.

### 7.1 What the failing run actually shows

`raw/virtualworld-after-smoke/` holds five bots losing their pawn, and they are **two different faults**:

- **Four bots at t = 20 s.** The orchestrator declared `w2` dead (`missed heartbeats for 5.3s`) and relaunched
  it; the gateway logged `worker w2 disconnected; dropping its entities`. `OnWorkerLost` did the right thing —
  it cleared `PawnNetId`, sent `JoinState.Starting` and scheduled a retry — but no retry ever produced a pawn.
  The session directory keeps a session's reserved worker while that worker is *alive*, and `w2` came back
  alive under the same id with none of its old leases, so every `reserve` was granted `w2`: a worker the
  gateway had no interest reason to dial and therefore no link to. `ReserveCoordinatedSpawn` fell through its
  `_workersById` lookup and did nothing, every three seconds, for the rest of the run.
- **bot3 at t = 33 s**, with no worker disconnect anywhere near it. Its pawn was alive the whole time (its
  handovers continue in `w2.log`/`w4.log` to the end of the run); the gateway simply stopped speaking for it.

The prior engineer's hypothesis — `DropLink` → `DropWorkerRecords` — is **wrong for this run**: the gateway
log was written with `-v` (it contains four `dialing worker` lines) and holds **no** `dropping worker link`
line at all. No link was ever dropped.

### 7.2 Root cause

A worker cannot dial a gateway; gateways dial workers. So after `TransferAuthority` the new owner can only
announce a pawn to gateways it already has a link to, and everything else rests on the `EntityRedirect` the
*old* owner sends. Two things were missing there.

1. **The redirect did not move the record.** `OnEntityRedirect` added a transient `Explicit` link reason (reset
   at the top of the next subscription pass) and left `EntityRecord.OwnerWorkerIndex` naming the previous
   owner. Every owner-index check is derived from that field — the `Owned` link reason that holds the pawn's
   worker, the world-state and `EntityForget` filters, `DropWorkerRecords` — so the gateway kept pointing at a
   worker that no longer had the entity, and the previous owner's stale messages were still accepted.
2. **A chain of handovers outruns the dialling.** A→B→C in quick succession: the gateway is linked to A, gets
   A's redirect naming B and starts dialling B — but B's own redirect naming C is sent over a link that does
   not exist yet, so it is never sent at all. By the time the gateway reaches B, B does not own the pawn and
   announces nothing. The gateway now has no way, ever, to learn where the pawn went. That is bot3: its pawn
   ping-pongs `w2 ↔ w4` across constantly-allocating chunks while the client sits pawn-less and connected.

Nothing in the ordering fixes (2) — the message that would carry the news has nowhere to go — so the gateway
has to notice and heal instead.

A third, smaller fault in the same path: an authority handover is also how an entity's **container** changes,
and `OnEntitySpawn` relayed that spawn to existing observers without first sending the new container's
ownership row. bot3's log shows it: `entity #844424930131969 is inside container rt_0, unknown here; holding`.

### 7.3 The fix

Gateway (`Runtime/Gateway/GatewayInterest.cs`, `NebulaGateway.cs`, `GatewaySessionAdmission.cs`):

- `OnEntityRedirect` takes the sending worker, moves the record to the new owner at once and marks it
  `OwnerUnconfirmed` until a spawn from that worker confirms it (a redirect is a promise, not an announcement).
- The `Owned` link reason resolves the owning worker through the **control plane** (`WorkerIdOfIndex`) rather
  than through `_workersByIndex`, so the gateway can dial an owner it has never talked to, and it holds the
  link while the owner is still unconfirmed — dialling it is how the confirmation can arrive.
- **Self-healing (design D5's mechanism, pointed at the pawn).** A welcomed client whose pawn the gateway
  cannot place — no record, an owner index no live worker answers to, or one still unconfirmed 0.75 s after
  the redirect — has that pawn subscribed **by name** on every live worker; whichever worker owns it announces
  it and the record is rebuilt wherever it went. If nobody claims it within `PawnRecoverySeconds` (5 s) the
  pawn really is gone: `PawnNetId` is cleared, the client is sent `JoinState.Starting` and placed again.
- A connected client's own pawn is never dropped by `DropWorkerRecords`, never forgotten on an `EntityForget`
  (it is sticky on the worker by D22, so such a message is a worker bug), and never the reason a link is
  dropped. These guards are deliberately scoped to the **pawn**, not to everything a client owns: a record
  nobody publishes any more is a frozen record, which is its own wrong answer.
- `OnEntitySpawn` sends the container's ownership row before relaying a spawn whose container changed.
- `ReserveCoordinatedSpawn` dials the worker the directory granted when the gateway has no link to it, which
  is what unblocks the four bots of §7.1.

Worker (`Runtime/Worker/WorkerInterest.cs`): `AnnounceToRelevantGateways` remembers a follower (and the owner's
session gateway) it has no link to, and announces to it the moment that gateway links (`AnnounceAwaited`).

Decision recorded as **D42** in `docs/interest-management.md`.

### 7.4 New tests

`Services~/Nebula.Services.Tests`: `FakeWorker` gained a faithful `HandOver` (redirect to the followers it can
reach, entity leaves the index with no despawn, the session travels with the pawn, the new owner announces only
to gateways it is linked to) and now honours `InterestSubscribe.Entities`. `InterestTests` gained three cases:

| Test | Against the code before the fix |
|---|---|
| `AnOwnedPawnSurvivesAHandoverToAWorkerTheGatewayHasNoLinkTo` | passed (the redirect does reach the gateway) |
| `AnOwnedPawnSurvivesItsPreviousWorkersLinkGoingAwayRightAfterTheHandover` | passed |
| `AnOwnedPawnSurvivesAChainOfHandoversThroughWorkersTheGatewayNeverLinked` | **failed** |

Only the chained case reproduced, which is exactly the shape §7.2 describes. All three pass now, and the two
that already passed are the regression guard for the redirect and link-drop paths. `InterestSettingsTests`
gained `MaxRadiusIsNeverUncapped` (§7.7).

### 7.5 Verification

| Check | Result |
|---|---|
| `dotnet test Packages/com.1by3.nebula/Services~/Nebula.Services.slnx` | **231 / 231** (was 227) |
| `powershell -File Tools/typecheck.ps1` (`C:\Dev\nebula`) | OK |
| `Tools/typecheck.ps1 -Repo C:\Dev\nebula-virtualworld` | OK, 0 errors |
| `Tools/typecheck.ps1 -Repo C:\Dev\nebula-shootergame` | 13 errors, all the known QFSW ones |
| Unity EditMode batchmode (`C:\Dev\nebula`) | **415 / 415** (was 414) |

`nebula-virtualworld`, rebuilt with `nebula build`:

```
observer, 8 bots, 210 s, fresh persistence — run 1 of 2
  PASS  every bot has a pawn
  PASS  nothing lingers past the exit radius (longest run of beyond>0 samples: 0)
  PASS  no entity spawned twice without a despawn (dup=0)
  PASS  no replica is stuck in a chunk this client never loads (longest run of orphans>0 samples: 0)
  PASS  every carried replica arrives with its carrier (carrierless=0)
  PASS  loaded chunks stay inside the near-cells window (max 18 <= 60)
  PASS  no exceptions or errors in any log (0)
  PASS  the walking bots crossed many chunks (fewest distinct chunk coords seen: 20)
  PASS  the floating origin shifted under a travelling client (5 distinct origin cells)
  idle bot1 replicas: first half 0.0 -> second half 0.0; world props 21 -> peak 264 -> 254
  PASS  the world is far bigger than the idle client's set (peak 264 props vs 0.0 replicas)
  PASS  a distant idle client's replica count stays flat as the world grows (0.0 -> 0.0)
  idle bot1 inbound: first half 7.0 KB/s -> second half 8.4 KB/s
  PASS  and so does its inbound bandwidth (7.0 -> 8.4 KB/s)
  runtime containers: 395 distinct seen, 247 held at the end, so at least 148 retired
  PASS  idle chunks are retired behind the bots (148 at least)
  PASS  the gateway caches less than the whole world (131 < 267)
  PASS  a client holds far less than the gateway does (mean replicas 6.0 < 104.8)
  PASS  a worker's gateway subscribes less than the worker owns (4 of 4 workers)
[smoke] all checks passed                                            (exit 0)

observer, 8 bots, 210 s, fresh persistence — run 2 of 2
  ... the same 22 checks, all PASS ...
  idle bot1 replicas: first half 4.3 -> second half 6.0; world props 29 -> peak 150 -> 141
  PASS  the gateway caches less than the whole world (76 < 149)
  PASS  a client holds far less than the gateway does (mean replicas 2.1 < 60.8)
[smoke] all checks passed                                            (exit 0)

line, 8 bots, 210 s, fresh persistence
  PASS  every bot has a pawn
  PASS  nothing lingers past the exit radius (longest run of beyond>0 samples: 0)
  PASS  no replica is stuck in a chunk this client never loads (0)
  PASS  the walking bots crossed many chunks (fewest distinct chunk coords seen: 20)
  SKIP  gateway cache check: the world (8 entities) is no bigger than 8 pawns plus one interest set
[smoke] all checks passed                                            (exit 0)
```

Zero pawn losses across all eight bots in every run, against five of eight before.

`nebula-shootergame`, rebuilt and re-run for regression:

| Run | Handovers out / in | Result |
|---|---|---|
| `-BotMode ship -Bots 8 -Npcs 60 -Seconds 180` | 1 379 / 1 380 | all checks passed (exit 0) |
| `-BotMode ship`, again | 3 496 / 3 529 | all checks passed (exit 0) |
| `-BotMode idle -Bots 8 -Npcs 60 -Seconds 150` | 1 728 / 1 780 | all checks passed (exit 0) |
| defaults (4 workers, 2 bots, 0 NPCs, 60 s) | 141 / 148 | all checks passed (exit 0) |

`carrierless = 0`, `dup = 0`, `orphans = 0` and zero errors or exceptions in every one; gateway cache
151 / 152 / 150 / 62 against mean client replicas 30.0 / 28.3 / 37.6 / 30.0, and the shooter's worker filter
ratio is a real measurement there (65 338 of 89 768 entries sent in one ship run, 953 of 1 006 in the default
run). Handover counts vary several-fold run to run in both games — how many crossings are cross-worker depends
on where the leases land — so they are a health signal (out ≈ in, zero failures), not a measurement.

### 7.6 Two smoke checks that could not pass as written

Both are in `nebula-virtualworld/Tools/smoke-test.ps1`, and neither was downstream of the pawn defect the way
the earlier note claimed:

- *"the workers filter what they publish over the run"* compared `entriesSent` with `entriesTotal`, which only
  counts entities that were **dirty** that tick. In this world the only things that move are the bots' own
  pawns, and an owned pawn is always published to its own client's gateway (D22) — with one gateway the ratio
  is 1.0 however well the worker filters. It is a real measurement in the shooter, where NPCs move. Replaced
  with the scoping this world *can* show — a worker's gateway subscribes far fewer regions than the worker
  owns containers (4 of 4 workers) — plus a `sent <= total` sanity check.
- *"the gateway caches less than the whole world"* cannot hold in `line` mode, where nobody drops props and the
  eight pawns **are** the world; a gateway always caches its own clients' pawns. Its gate now requires the
  world to exceed the bot count plus one interest set, and skips otherwise.

### 7.7 Also fixed here

- **`InterestMaxRadius` is no longer switched off by 0.** It is the ceiling on what a prefab's
  `RelevanceRadius` may ask a gateway to send, which makes it a cost and security boundary rather than a hint,
  so `Validate` reports a non-positive value and uses `InterestRadius`; the two clamps that read it
  (`NebulaGateway.OnEntitySpawn`, `ClientInterest.Consider`) are now unconditional. Decision **D43**;
  `InterestSettingsTests.MaxRadiusIsNeverUncapped` pins it.
- Three doc-versus-code contradictions in `docs/interest-management.md`: §8's `NearCells` formula now carries
  the linger/speed overshoot term and the `cell <= 0` short-circuit that the code has; §9 no longer claims
  `Validate` enforces "allocator ring ≥ NearCells" (`NebulaChunkedWorld` constructs the ring as `NearCells + 1`,
  so there is no separate value to police); §13 no longer mentions `nebula-loadgen --scenario`, which does not
  exist — the three shapes are `InterestSoakTests` run with `dotnet test --filter TestCategory=Soak`, and the
  load generator only gained `--idle` and per-client replica/byte columns. The same three are corrected in
  `website/content/docs/guides/{interest-management,configuration}.mdx`.

### 7.8 Still open

- **One `-BotMode ship` run out of four failed** `nothing lingers past the exit radius` (a run of 81 samples
  with `beyond > 0`). It was an earlier build of this fix, in which the "never drop it" guards covered
  *everything* a client owns rather than only its pawn. The ship bot had flown ~3 km outside the authored
  world and parked, and from `t = 100 s` it was sent 22 replicas back at the compound that never left its set
  — the signature of a stale carrier record holding its focus in the wrong place. Narrowing the guards to the
  pawn (§7.3) removes the way a stale owned record could survive, and the three ship runs since were clean
  (`beyond` runs of 1, 1, 1). **Causation was not proved**: the failure was not reproduced before or after the
  narrowing, and the logs of the failing run were overwritten by the next one. Worth watching.
- A bot that walks off the authored terrain still falls in the shooter until the −50 m guard catches it
  (unchanged).
- §3.1–3.3 and §4.1–4.2 are from the original `nebula start` measurement runs and were **not** repeated after
  this fix; only the smoke runs above were. The fix only changes what happens when a pawn's owner moves, not
  steady-state replica counts or bandwidth, but those numbers are from the earlier builds.


## 8. Remaining concerns

- The blocker recorded here is fixed (§7); what is left of it is the unproved ship-run failure in §7.8.
- The gateway's cache is only modestly smaller than the world in virtualworld (113 of 117) because eight
  clients spread over a small world collectively want nearly all of it. That is correct behaviour, but it means
  the *gateway-level* scoping claim is not demonstrated by that run; the soak (§2) demonstrates it properly, and
  the shooter run shows it at 153 of 221.
- `interest eval max` reached 1.089 ms once in the shooter run against a 0.091 ms average. Worth watching if
  client counts grow; `PartitionWarnFilterMs` did not fire.
- The virtualworld handover count varies several-fold between runs on a freshly reset world depending on how
  chunk leases distribute. Any future before/after on that number needs several runs, not one.
