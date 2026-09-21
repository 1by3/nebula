# Interest-management baseline ("before")

Recorded 2026-09-20/21 on `main` @ `487741a` ("Up to date"), Nebula CLI `0.1.0-alpha.28`, Unity `6000.6.0f1`,
Windows 11 Pro 26200. No source under `Packages/` or either game project was modified for this run.

Raw logs, test XML and the dashboard JSON are in [`raw/`](raw/).

---

## 0. Summary

| | shootergame | virtualworld |
|---|---|---|
| Typecheck vs live package | pass (13 known QFSW errors only) | pass (0 errors) after the §4 fix |
| Package EditMode tests | 265/265 pass | not re-run (same package suite, see §4b.9) |
| `nebula build` | success (466 s) | success (~60 s) |
| Mesh run | 4w / 60 NPC / 8 bots, 3 min | 4w / 8 bots, 3.5 min (§4b) |
| `Tools/smoke-test.ps1` | done (defaults) | n/a (no such script) |
| **Mean replica entities per client** | 104 of 173–183 (~60 %) | **187 of 187 (100 %)** |
| **Mean inbound KB/s per client** | 46.0 | 24.6 |

§4 records the original virtualworld blocker; it was fixed with the one-word `uint` → `ulong` change and
the project was then measured in **§4b**. No other source change was needed and `Packages/` was untouched.

---

## 1. Environment and exact commands

The CLI is **not on the Bash PATH**. Full path: `C:\Users\jesse\.nebula-cli\bin\nebula.exe`.
Verified fresh: binary mtime `2026-09-20 21:27`, newer than every file under `C:\Dev\nebula\cli`, and
`nebula --version` → `nebula 0.1.0-alpha.28 (win-x64)`. **No reinstall was needed.**

```bash
# typecheck (from anywhere)
powershell -File C:\Dev\nebula\Tools\typecheck.ps1 -Repo C:\Dev\nebula-shootergame  -ExtraSourceRoots C:\Dev\nebula\Packages
powershell -File C:\Dev\nebula\Tools\typecheck.ps1 -Repo C:\Dev\nebula-virtualworld -ExtraSourceRoots C:\Dev\nebula\Packages
```

```powershell
# EditMode / PlayMode tests (no Unity Editor may have the project open; none were open for this run)
$unity = Join-Path $env:ProgramFiles 'Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe'
Start-Process -FilePath $unity -Wait -PassThru -ArgumentList @(
  '-batchmode','-nographics','-projectPath','C:\Dev\nebula',
  '-runTests','-testPlatform','EditMode','-testResults','<out>.xml','-logFile','<out>.log')
```

```bash
# build + run (must be cwd = the game project)
cd C:/Dev/nebula-shootergame
/c/Users/jesse/.nebula-cli/bin/nebula.exe build
/c/Users/jesse/.nebula-cli/bin/nebula.exe start --workers 4 --npcs 60 --bots 8 -v --yes
/c/Users/jesse/.nebula-cli/bin/nebula.exe status
/c/Users/jesse/.nebula-cli/bin/nebula.exe logs gateway -n 200
/c/Users/jesse/.nebula-cli/bin/nebula.exe stop
```

```powershell
# smoke test (needs the CLI on PATH — the script calls bare `nebula`)
$env:PATH = "C:\Users\jesse\.nebula-cli\bin;$env:PATH"
Set-Location C:\Dev\nebula-shootergame
powershell -File Tools\smoke-test.ps1
```

Ports: shootergame dashboard `7080`, gateway `7000`. virtualworld dashboard `7180`, gateway `7200`
(from each project's `nebula.json`).

---

## 2. Tests and typechecks

### Typecheck

| Project | Result | Errors |
|---|---|---|
| nebula-shootergame | FAILED (13) — **all pre-existing QFSW noise**, as documented | 13 |
| nebula-virtualworld | FAILED (1) — **real** | 1 |

Shooter's 13 errors are entirely in `Assets/Plugins/QFSW/Quantum Console/...` (`IQcScanRule`,
`ScanRuleResult`, `UnityEngine.UI`, `UnityEngine.EventSystems`, `UnityEditor.SceneManagement` not
resolving). Zero errors in `Nebula.*`, `ShooterGame*`, `Nebula.World` or `VattalusAssets`. Full output:
[`raw/typecheck-shooter.txt`](raw/typecheck-shooter.txt).

Virtualworld's single error is in §4.

### Unit tests

All tests live in the package (`Packages/com.1by3.nebula/Tests/{EditMode,PlayMode}`). **Neither game
project has tests of its own**; both list `com.1by3.nebula` under `testables`, so running a game
project's tests just re-runs the package suite through that project's assembly set.

| Suite | Total | Passed | Failed | Skipped | Duration |
|---|---|---|---|---|---|
| `C:\Dev\nebula` EditMode | 265 | 265 | 0 | 0 | 1.12 s |
| `C:\Dev\nebula` PlayMode | 1 | 1 | 0 | 0 | 0.04 s |
| `C:\Dev\nebula-shootergame` EditMode | 265 | 265 | 0 | 0 | — |
| `C:\Dev\nebula-virtualworld` EditMode | — | — | — | — | **did not run** (compile error) |

**There are zero pre-existing test failures.** Any failure after the change is a regression.

Result XML: `raw/tests-*.xml`. Note the PlayMode suite is a single test — it is not meaningful coverage.

---

## 3. shootergame — mesh baseline

### 3.1 Build

`nebula build` succeeded: Unity player in **453 s** (0 errors), 466 s including the orchestrator/gateway
publish. Exported service manifest: **77 containers, 110 persistence schemas**. 22 scenes (Arena,
Corporation, and 20 baked `Corporation_x_0_z` world cells).

### 3.2 Run configuration

`nebula start --workers 4 --npcs 60 --bots 8 -v --yes`, sampled `/api/state` every 15 s for **195 s**.
Workers fixed at 4 (min = max = 4), autoscaling off, SQLite control plane.

### 3.3 World totals (from `/api/state` at t=195 s)

| Metric | Value |
|---|---|
| Live workers | 4 |
| Players (human) | 0 |
| Bots (clients) | 8 |
| NPCs (`serverDriven`) | 62 |
| **Total entities (incl. ghosts)** | **227** |
| **Authoritative entities** | **174** |
| **Containers** | **78** |
| Persisted entities (SQLite) | 75 |

### 3.4 Per-worker (from `/api/state` at t=195 s and `nebula status`)

| Worker | Containers | Tick ms | Utilization | Entities | Authoritative | Ghosts | Bots | NPCs |
|---|---|---|---|---|---|---|---|---|
| w1 | 22 | 1.603 | 0.146 | 56 | 51 | 5 | 3 | 10 |
| w2 | 19 | 1.757 | — | — | 47 | 16 | 2 | 13 |
| w3 | 19 | 1.202 | — | — | 33 | 20 | 1 | 21 |
| w4 | 26 | 1.575 | — | — | 42 | 11 | 2 | 17 |

Tick ms across the whole 195 s window stayed in **1.04 – 2.64 ms** (tick period 16.67 ms, so ~6–16 %
utilization). Container counts drift run-to-run as containers rebalance; the totals hold at 77–78.

### 3.5 Worker tick profile (`[nebula] profile`, 5 s windows, steady state)

```
w1: profile 300 ticks/5s 300 frames avg 1.7ms max 4.9ms dup 0 skip 0 gc 7 auth 52 ghosts 4 |
    npc.brain=0.04(2505) npc.move=0.43(3226) npc.fire=0.00(8) npc.path=0.00(85) npc.presence=0.01(80)
    poll=0.06 ghosts=0.02 physics.sync=0.11 simulate=0.90 poses=0.05 containers=0.31 band=0.11
    publish=0.04 gap=15.00
w3: profile 300 ticks/5s 300 frames avg 1.3ms max 4.2ms dup 0 skip 0 gc 6 auth 34 ghosts 19 |
    npc.brain=0.08(5262) npc.move=0.27(5091) npc.fire=0.01(19) npc.path=0.02(83) npc.presence=0.01(80)
    poll=0.03 ghosts=0.03 physics.sync=0.12 simulate=0.68 poses=0.05 containers=0.22 band=0.08
    publish=0.04 gap=15.34
```

`dup 0 skip 0` throughout — no dropped or duplicated ticks. `publish` (the per-tick replication send)
costs **0.04–0.05 ms**, i.e. ~3 % of a 1.5 ms tick, and `band` 0.08–0.12 ms.

### 3.6 Gateway (`worldstate:` counters, `-v`; 271 steady-state 1 s samples, 8 clients)

| Metric | avg | min | max |
|---|---|---|---|
| Packets in / s | 372 | 29 | 509 |
| **Entries in / s (ingest)** | **2 751** | 200 | 3 684 |
| **Entries sent / s (fan-out, all clients)** | **3 627** | 528 | 4 346 |
| **Entities known to gateway (cached)** | **173** | 60 | 183 |
| Connected clients | 8 | | |
| Worker connections to the gateway | **4** (w1–w4, one each) | | |

Fan-out amplification (`sent / in`) = **1.32×**. Dropped counters were `unknown=0 stale=0 wrongOwner=0`
in **all 280** samples.

### 3.7 Per-client (bot) replica count and bandwidth

Last stats line per bot, steady state. Bots are moving (see §3.9).

| Bot | **Replica entities** | **Inbound KB/s** | states/s | pkt/s |
|---|---|---|---|---|
| bot1 | 81 | 46.3 | 486 | 304 |
| bot2 | 103 | 49.0 | 547 | 325 |
| bot3 | 99 | 43.4 | 405 | 314 |
| bot4 | 101 | 50.3 | 582 | 341 |
| bot5 | 103 | 44.1 | 423 | 328 |
| bot6 | 99 | 46.1 | 480 | 332 |
| bot7 | 103 | 46.7 | 493 | 328 |
| bot8 | 141 | 41.8 | 371 | 306 |
| **mean** | **103.6** | **46.0** | **473** | **322** |
| **total (8 bots)** | | **368 KB/s ≈ 2.94 Mbit/s** | | |

**This is the number the change is aimed at.** The gateway knows 173–183 entities; the average client
holds **~104 of them (~60 % of the world)** and the worst holds **141 (~81 %)**, at a flat **~46 KB/s**
each regardless of where the bot is standing. Replica count tracks world size, not proximity.

Client health was clean: `rtt 0ms`, `corrections 0`, `starved 0.0 %`, snapshot age avg ~13.2 ms
(min 6.2 / max 43.7), interp depth 2.3–3.0 ticks, ~840–1100 fps headless.

### 3.8 Handovers (full 195 s run)

| Worker | handover OUT | handover IN | Errors/Exceptions |
|---|---|---|---|
| w1 | 179 | 181 | 0 |
| w2 | 230 | 236 | 0 |
| w3 | 300 | 289 | 0 |
| w4 | 169 | 184 | 0 |
| **Total** | **878** | **890** | **0** |

**Zero handover failures** — no log line matching `handover.*(fail|timeout|reject)` in any worker log.
(OUT and IN totals differ by 12 because the run was cut mid-flight at stop time.)

### 3.9 Stationary-bot variant — **not available**

There is no stationary-bot option. `-nebula-bot` unconditionally sets `Autopilot = true`
(`Assets/ShooterGame/Scripts/PlayerController.cs:266`) and the pawn is then driven by `BotBrain`. There
is no CLI flag or game setting to make a bot stand still, so **the stationary-bot numbers requested
could not be measured.** Adding e.g. `-nebula-bot-idle` would make the before/after comparison much
sharper, since a stationary client is exactly the case interest management should shrink most.

### 3.10 `Tools/smoke-test.ps1` (run as-is, defaults: 4 workers, 2 bots, 0 NPCs, 60 s)

```
orchestrator: assignments=77 dead-declared=0 relaunched=0 scaled-up=4 retired=0 drain-timeouts=0
gateway: workers-connected=7 clients-connected=4 spawn-requests=0
w1: registered=1 peers=3 spawned=1 handover-out=0 handover-in=0 cross-worker-hits=0 kills=0 warnings=0 errors=0
w2: registered=1 peers=3 spawned=0 handover-out=0 handover-in=0 cross-worker-hits=0 kills=0 warnings=0 errors=0
w3: registered=1 peers=3 spawned=0 handover-out=0 handover-in=0 cross-worker-hits=0 kills=0 warnings=0 errors=0
w4: registered=1 peers=3 spawned=1 handover-out=0 handover-in=0 cross-worker-hits=0 kills=0 warnings=0 errors=0
bot1: welcome=1 local-player=1 errors=0
bot2: welcome=1 local-player=1 errors=0
TOTAL handovers: out=0 in=0
```

**The smoke test reports 0 handovers**, which it prints in red as its own failure signal. This is a
property of its defaults, not a regression: with `-Npcs 0` and only 2 bots over 60 s neither bot
happened to cross a container boundary. The same build with 8 bots + 60 NPCs over 195 s produced 878
handovers (§3.8). Worth knowing before reading a post-change smoke run: **treat `out=0` at defaults as
inconclusive, and compare against the §3.8 loaded run instead** (or run
`Tools\smoke-test.ps1 -Bots 8 -Npcs 60 -Seconds 180`).

Two other oddities in the smoke output, both pre-existing and both cosmetic counting bugs in the
script's regexes:
- `npcs=11` despite `-Npcs 0` — the game spawns a small baseline population regardless.
- `gateway: workers-connected=7 clients-connected=4` — the script counts bare `connected` / `client \d+ '`
  matches, which over-count. Ground truth from the loaded run is 4 worker connections and 8 clients.

Full output: [`raw/shooter-smoke-test.txt`](raw/shooter-smoke-test.txt).

---

## 4. virtualworld — the original blocker (resolved; numbers are in §4b)

The project does not compile against the current package, so its tests, its build and therefore its
mesh run could not be done. Nothing here is estimated or carried over from the shooter.

### The error

```
C:\Dev\nebula-virtualworld\Assets\VirtualWorld\Scripts\VirtualWorldGameMode.cs(48,41):
error CS0115: 'VirtualWorldGameMode.OnSpawnPlayer(NebulaWorker, uint, string, Container)':
no suitable method found to override
```

The package base signature is `ulong clientId`:

```csharp
// Packages/com.1by3.nebula/Runtime/Core/NebulaGameMode.cs:15
public virtual NetworkIdentity OnSpawnPlayer(NebulaWorker worker, ulong clientId, string playerName, Container container)
```

`VirtualWorldGameMode.cs:48` still declares `uint clientId`. The widening to `ulong` landed in the
package at `ba23e10` *"Coordinate player admission across gateways and release alpha.26"*;
nebula-virtualworld was last touched at `71ff49d` and still pins `"nebula": "0.1.0-alpha.15"` in its
`nebula.json`. **The project has been broken against the package since alpha.26** — this is not caused
by anything in this baseline run.

The fix is a one-word change (`uint` → `ulong` on that parameter, plus whatever the body needs). It was
**not applied**, because this task explicitly forbids modifying the game projects' source.

### What that blocked

| Step | Status |
|---|---|
| Typecheck | ran — FAILED (1 error above) |
| EditMode tests | **did not run** — Unity exits with "Scripts have compiler errors", no results XML produced |
| `nebula build` | **failed** — same CS0115, build aborts before the player or the .NET services are published |
| Mesh run (`--workers 4 --bots 8`) | **not attempted** |
| `/api/state`, logs, per-client metrics | **unavailable** |

A stale player build does exist at `C:\Dev\nebula-virtualworld\Builds\Win64\Nebula.exe`, dated
**2026-09-14 11:15**. It was deliberately **not** used: it predates the alpha.26 cross-gateway admission
change and the drop-SpacetimeDB control-plane rework, so its orchestrator, gateway and wire protocol do
not match today's package. Numbers from it would measure a six-day-old different codebase and would be
worse than no baseline. Note also that virtualworld's game code has **no `npcs` setting** — only
`--bots N` would apply.

**To get a virtualworld baseline, fix `VirtualWorldGameMode.cs:48` first, then re-run §1's build/start
commands from `C:\Dev\nebula-virtualworld` (dashboard 7180, gateway 7200).**

---

## 4b. virtualworld — mesh baseline (run 1, 2026-09-20 22:41–22:47)

Recorded after the §4 blocker was fixed. The **only** change to the project was the one-word widening
named in §4:

```diff
- public override NetworkIdentity OnSpawnPlayer(NebulaWorker worker, uint  clientId, ...)
+ public override NetworkIdentity OnSpawnPlayer(NebulaWorker worker, ulong clientId, ...)
```
(`Assets/VirtualWorld/Scripts/VirtualWorldGameMode.cs:48`). **No further compile breaks appeared** —
typecheck and build both passed with no other edit. Nothing under `Packages/` was touched.
`nebula.json` still pins `"nebula": "0.1.0-alpha.15"`; that string is not enforced, the build used the
live package.

Raw logs, the 15 `/api/state` samples and `nebula status` are in
[`raw/virtualworld-run1/`](raw/virtualworld-run1/).

### 4b.1 Commands (exactly as run)

```bash
powershell -File C:\Dev\nebula\Tools\typecheck.ps1 -Repo C:\Dev\nebula-virtualworld -ExtraSourceRoots C:\Dev\nebula\Packages
cd C:/Dev/nebula-virtualworld
/c/Users/jesse/.nebula-cli/bin/nebula.exe build
/c/Users/jesse/.nebula-cli/bin/nebula.exe start --workers 4 --bots 8 -v --yes
# sampled http://localhost:7180/api/state every 15 s, 15 samples (t = 0 … 210 s after warm-up)
/c/Users/jesse/.nebula-cli/bin/nebula.exe status
/c/Users/jesse/.nebula-cli/bin/nebula.exe stop
```

Typecheck: **OK**, 0 errors, 6 assemblies (LiteNetLib, Nebula.Runtime, Nebula.Editor, Nebula.World,
VirtualWorld, VirtualWorld.Editor). No QFSW noise — the project has no QFSW.

### 4b.2 Build — completed **before** any measurement

`nebula build` exited 0 at **22:41:29**, `nebula start` was issued at **22:41:45**. Player build 23 s
(0 errors), 33 s including staging, then the orchestrator and gateway publishes; ~1 min end to end (vs
the shooter's 466 s). One scene (`Assets/VirtualWorld/Scenes/World.unity`). Exported service manifest:
**0 containers, 2 persistence schemas** — expected, virtualworld is a runtime-only world and allocates
all its containers at runtime (`ChunkAllocator`). Artifact mtimes: `Nebula.exe` 22:41:16,
`nebula-gateway.exe` 22:41:29. No "missing script" symptoms, so no `touch` of `Scripts/*.cs` was needed.

### 4b.3 Run configuration and the one big caveat

`--workers 4 --bots 8`, workers fixed (min = max = 4), autoscaling off, SQLite control plane,
`npcs=0` (virtualworld has no NPC setting; the "NPC"/`serverDriven` count is **props**).

> **The world does not reach a steady state.** Bots continuously spawn props, so entity counts grow
> monotonically for the whole run: `serverDriven` 15 → 135 over 210 s (**≈ 0.57 props/s**), and the
> persisted-entity count tracks it. **Every total below is time-indexed, not an equilibrium**, and a
> post-change run must be compared at the same elapsed time and same bot count, or normalized per
> entity. This is the single biggest gotcha for reusing this baseline.

### 4b.4 World totals (`/api/state`, t = 210 s)

| Metric | t = 0 s | t = 210 s |
|---|---|---|
| Live workers | 4 | 4 |
| Players (human) | 0 | 0 |
| Bots (clients) | 8 | 8 |
| Props (`serverDriven`) | 15 | **135** |
| **Total entities (incl. ghosts)** | 27 | **156** |
| Authoritative entities | 23 | 143 |
| **Runtime containers** | 40 | **48** (peak 57) |
| Persisted entities (SQLite) | 23 | 145 |
| Rebalances (cumulative) | 4 | **17** |

`nebula status` taken ~20 s later already read 148 props / 158 persisted — consistent with the growth
above.

### 4b.5 Per-worker (t = 210 s)

| Worker | Containers | Tick ms | Utilization | Entities | Authoritative | Ghosts | Bots | Props |
|---|---|---|---|---|---|---|---|---|
| w1 | 20 | 0.556 | 0.034 | 43 | 40 | 3 | 3 | 37 |
| w2 | 13 | 0.690 | 0.043 | 51 | 47 | 4 | 5 | 42 |
| w3 | 10 | 0.473 | 0.032 | 36 | 33 | 3 | 0 | 33 |
| w4 | 5 | 0.391 | 0.027 | 26 | 23 | 3 | 0 | 23 |

Over the whole run per-worker `tickMs` rose from ~0.12 ms to ~0.7 ms as props accumulated; utilization
stayed under **4.3 %** of the 16.67 ms budget. `[nebula] profile` steady-state (samples 13-63):
w1 0.43 ms, w2 0.52 ms, w3 0.42 ms, w4 0.47 ms average; worst single 5 s window 0.9 ms.

`containers` dominates the tick profile here, unlike the shooter:
```
w1: profile 300 ticks/5s 300 frames avg 0.6ms max 0.9ms dup 0 skip 0 gc 11 auth 37 ghosts 4 |
    poll=0.02 ghosts=0.01 physics.sync=0.00 simulate=0.02 poses=0.02 containers=0.40 band=0.04
    publish=0.02 gap=16.09
```
`publish` is **0.02 ms** and `band` **0.04 ms** per tick.

**Duplicate ticks are not zero** (the shooter run had `dup 0` throughout): totals over the run
w1 = 44, w2 = 1, w3 = 91, w4 = 106, and one `skip` on w3. They cluster in 2-3 consecutive 5 s windows
per worker (w4 at ~t 65-75 s, w3 at ~t 190-200 s) rather than being spread out — i.e. brief hitches,
most likely container churn/rebalance, not a steady defect. Note this so a post-change run is not read
as a new regression.

### 4b.6 Gateway (`worldstate:`, 316 one-second samples; last 60 = steady-ish tail)

| Metric | whole run avg | last-60 s avg | min | max |
|---|---|---|---|---|
| Packets in / s | 217 | — | 0 | 259 |
| **Entries in / s (ingest)** | 469 | **484** | 0 | 525 |
| **Entries sent / s (fan-out, all clients)** | 1 018 | **1 079** | 0 | 1 474 |
| **Entities known to gateway (cached)** | 94 (ramping) | **168** | 0 | **187** |
| Connected clients | 8 | 8 | | |
| **Worker connections** | **4** | 4 | | |

Fan-out amplification over the last 60 s = **2.23×** (shooter: 1.32×). Dropped counters
`unknown=0 stale=0 wrongOwner=0` in **all 316** samples.

Gateway byte and loop counters (available here, from `/api/state` `gateways[0]` and the gateway's own
`loop` line — see §5, they were not used in §3):

| Metric | value at t = 210 s |
|---|---|
| bytesIn / bytesOut (clients) | 28.0 KB/s / 175.5 KB/s |
| workerBytesIn / workerBytesOut | 62.8 KB/s / 27.8 KB/s |
| packetsIn / packetsOut | 496 / 1 240 per s |
| Gateway CPU / RSS | 1.6 % / 90 MB |
| `loopLagMs` | 6.0 – 7.8 |
| Gateway loop (63 samples) | 240 Hz, tick avg **0.070 ms**, worst tick 30.1 ms, period max avg 12.1 ms (worst 36.6) |

### 4b.7 Per-client (bot) replica count and bandwidth — **the headline number**

Last stats line per bot (~22:46:49; by then the gateway knew 187 entities):

| Bot | **Replica entities** | **Inbound KB/s** | states/s | pkt/s |
|---|---|---|---|---|
| bot1 | 187 | 24.4 | 143 | 162 |
| bot2 | 187 | 24.2 | 132 | 182 |
| bot3 | 187 | 24.2 | 139 | 156 |
| bot4 | 187 | 23.8 | 130 | 153 |
| bot5 | 187 | 25.7 | 170 | 162 |
| bot6 | 187 | 23.8 | 130 | 152 |
| bot7 | 187 | 25.5 | 168 | 159 |
| bot8 | 187 | 24.8 | 151 | 161 |
| **mean** | **187** | **24.6** | **145** | **161** |
| **total (8 bots)** | | **196 KB/s ≈ 1.57 Mbit/s** | | |
| mean of last 5 lines each | 179.6 | 23.1 | 129 | |

**Every bot replicates every entity in the world — 187 / 187, i.e. 100 %**, even though each bot only
has ~20 chunks streamed in. Virtualworld is therefore a *cleaner* demonstration of the problem than the
shooter (which sat at ~60 % / worst 81 %): here replica count is exactly world size, and since the world
grows without bound during a run, **per-client bandwidth grows without bound too**. That is the number
the change must move.

Client health was clean: `rtt 0ms`, `corrections 0-1`, `starved 0.0 %`, interp depth 1.4–1.8 ticks,
snapshot age avg 8–14 ms (max 26), ~20 000 fps headless, `gaps>3t 0`.

### 4b.8 Runtime containers, handovers, errors (full run)

| Metric | Value |
|---|---|
| Runtime containers at t=0 / peak / t=210 s | 40 / **57** / 48 |
| Distinct runtime containers seen across the 15 samples | **63** |
| Container **retirements** (disappearances between consecutive samples, lower bound) | **22** |
| Container **allocations** (appearances, lower bound) | 23 (63 seen − 40 initial) |
| Orchestrator `assign rt_*` events | **155** |
| Rebalances | **17** |
| Workers launched / dead-declared / relaunched | 4 / 0 / 0 |

Allocation and retirement are **lower bounds**: the orchestrator logs no explicit
`container allocated/retired` line (only `assign`), so these are derived from the 15 s `/api/state`
samples and any container that lived and died inside one 15 s gap is invisible. Recording this properly
would need a per-allocation log line in the package.

| Worker | handover OUT | handover IN | handover failures |
|---|---|---|---|
| w1 | 81 | 67 | 0 |
| w2 | 167 | 160 | 0 |
| w3 | 156 | 150 | 0 |
| w4 | 67 | 94 | 0 |
| **Total** | **471** | **471** | **0** |

By kind: **Prop 321 out / 321 in**, Player (incl. bots) 150 out / 150 in. No line matching
`handover.*(fail|timeout|reject|abort)` in any worker log.

**Errors / warnings / exceptions:** zero in all 8 bot logs, zero in the orchestrator log, and exactly
one line each in w2, w3, w4 and the gateway — all the same shutdown artifact emitted at `nebula stop`
when the orchestrator's dashboard goes away first:
`control plane: reading http://127.0.0.1:7180 failed ...; mesh keeps running on its last known topology`.
**Treat that as expected noise, not a defect.**

### 4b.9 Not measured for virtualworld, and why

- **EditMode/PlayMode tests were not re-run for this project.** §2 already establishes the suite is the
  package's own 265 tests and that running it through a game project just re-runs them; nothing in this
  task's scope changed that.
- **Stationary bots** — same as §3.9, no such mode.
- **Per-container / per-chunk bandwidth** — no counter exists at that granularity.
- **Steady-state numbers** — impossible today, see the caveat in §4b.3.
- **NPCs** — virtualworld has no NPC concept; `npcs=0` and the `serverDriven` figure is props.

---

## 5. Gaps — metrics that do not exist today

Stated explicitly rather than guessed:

- **Gateway tick time — corrected in §4b.** The gateway *does* emit a periodic
  `loop 240 Hz, period max … ms, tick avg … ms max … ms` line, and `/api/state` carries `loopLagMs` and
  `cpu` per gateway. §3 simply did not use them; §4b.6 does. What is still missing is any *per-client*
  or *per-entity* send cost.
- **Gateway ingest/egress in bytes — partly available, corrected in §4b.** The `worldstate:` log line
  counts packets and state *entries* only, but `/api/state` `gateways[0]` exposes
  `bytesIn/bytesOut/workerBytesIn/workerBytesOut` per second (used in §4b.6). The §3.7 byte figures are
  still the **client-side** ones (`NebulaClient.cs:448`), including framing, inbound only.
- **No per-container or per-chunk bandwidth counter** exists at any layer.
- **No container allocate/retire log line.** The orchestrator logs only `assign rt_*`, so runtime
  container allocation and retirement counts (§4b.8) are lower bounds derived from polling `/api/state`.
- **Per-client replica count is only visible in the client's own log line**, not in `/api/state` or any
  gateway output. Extracting it requires reading each `bot*.log`.
- **Stationary-bot measurements** — see §3.9, no such mode exists.
- **`nebula status`** shows per-worker `auth` and `ghosts` but no bandwidth of any kind.

## 6. Gotchas for repeating this

1. The CLI is not on Bash's PATH; `Tools\smoke-test.ps1` calls bare `nebula`, so prepend
   `C:\Users\jesse\.nebula-cli\bin` to `$env:PATH` before running it.
2. `nebula` commands must run with cwd = the game project directory.
3. Only one local mesh at a time. On this machine `nebula start` found and stopped a *third* project's
   mesh, `C:\dev\holoverse-nebula` (6 processes). `--yes` stops it without prompting.
4. `Tools\smoke-test.ps1` **deletes `Builds\Win64\Logs\*.log`** on start. Copy the logs you care about
   out first — that is why `raw/shooter-run1/` was archived before the smoke test ran.
5. `-v` on `nebula start` is required for the gateway `worldstate:` lines.
6. In `/api/state` the per-worker fields are `entities` / `authoritative`, **not** `entityCount` /
   `authoritativeCount` (those names appear in the C# DTO and in the remote control-plane wire form, but
   not in the dashboard JSON).
7. Unity batchmode tests need the Editor closed for that project. None were open here
   (`Get-Process Unity` returned nothing), so the robocopy-to-scratch workaround was not needed.
8. Shooter `nebula build` takes ~7.5 min end to end, not ~4. Virtualworld takes ~1 min.
9. **virtualworld's world grows for the whole run** (bots spawn props, ≈0.57/s). Compare any post-change
   run at the same elapsed time, same bot count — or normalize per entity. See §4b.3.
10. `Builds\Win64\Logs` in virtualworld still holds `w5.log`–`w12.log` from a 2026-09-14 run. Check log
   mtimes before aggregating, or you will mix runs.
11. virtualworld has no NPCs. `--npcs` does nothing; `serverDriven` in `/api/state` (and the "NPC" column
   in `nebula status`) means **props**.
12. At `nebula stop` each worker and the gateway log one `control plane: reading http://127.0.0.1:7180
   failed` line. Expected shutdown ordering noise; exclude it from error counts.
