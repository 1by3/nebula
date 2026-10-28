# A configurable tick rate — design

Status: implemented on branch `perf/tick-rate` (NEB-381). Protocol stays **30**: honouring the welcome's rate is part of
protocol 30, which has not been released. Tests: `TickRateTests` (EditMode),
`ConformanceProtocolCompatibilityTests.AMeshAt30HzRefusesAProtocol29ClientAndWelcomesACurrentOneWithItsRate` (services).

## 0. Purpose

The simulation ran at a constant 60 Hz (`NetworkTime.TickRate` was a `const`). Every per-tick cost on a worker is paid
60 times a second, whether or not anything changed: the entity loops, the containers pass, replication, the physics
frames' steps and each predicted pawn's step. A big-world game is normally simulated at 20 to 30 Hz with prediction and
lag compensation, which Nebula has. At 30 Hz a worker has a 33 ms budget per tick instead of 17, and pays every
per-tick term half as often. The cost is up to one extra tick period (17 ms) of latency on each hop, and coarser steps.

## 1. One rate per mesh (D1)

`NebulaConfig.TickRate` (default 60, 10 to 240) sets it. Every process of a mesh runs at the same rate, because a tick
number is derived from the wall clock (`NetworkTime.DerivedTick = seconds since the origin × rate`) and every message
that carries a tick means that number:

- A Unity worker, a Unity-hosted orchestrator or gateway calls `NetworkTime.Configure(config.TickRate)` in
  `NebulaBootstrap.Awake`, after the command-line overrides and before anything reads a tick.
- The standalone orchestrator and gateway call it in `ServiceHost.Run` from the exported `nebula-services.json`.
- The rate a process uses is, first, `-nebula-tick-rate <hz>` on its command line, then the `NEBULA_TICK_RATE`
  environment variable, then the config (`NetworkTime.ResolveRate`). The environment variable lets a local mesh started
  from one shell run every role at another rate without a rebuild (a load test's A/B).
- The orchestrator passes its own rate to everything it launches (`-nebula-tick-rate` in `CommonArgs`), so a gateway
  or a worker never disagrees with it.
- A client takes the rate from the gateway: `WelcomeMsg.TickRate`, a byte that was always on the wire and always 60
  (D3).

`NetworkTime.TickRate` and `TickInterval` are now static properties. Code that used them in a `const` must use
`static readonly` or a property; this is the one source-breaking change.

## 2. Durations are seconds; counts are ticks (D2)

A value that means a length of time is held in seconds and converted at the running rate when it is read
(`NetworkTime.SecondsToTicks`, rounded and at least 1, or `SecondsToTicksCeil` for a margin that must cover the time).
A value that counts steps or samples stays a tick count. Each of Nebula's own:

| What | Was (ticks at 60 Hz) | Now | At 30 Hz |
|---|---|---|---|
| Render delay `InterpolationDelaySeconds` | 3 | 0.05 s, rounded up | 2 ticks (two snapshots) |
| Stream-lag cap `MaxStreamLagSeconds` | 12 | 0.2 s | 6 |
| Slow-update delay cap `SlowUpdateMaxDelaySeconds` | 30 | 0.5 s | 15 |
| Input lead margin `InputLeadMarginSeconds` | 2 | 2/60 s, rounded up | 1 |
| Input lead target `InputLeadTargetSeconds` | 3 | 0.05 s, rounded up | 2 |
| Input lead adjustment cap `InputLeadMaxAdjustSeconds` | 30 | 0.5 s | 15 |
| Lag-compensation history `StateHistorySeconds` | 32 | 32/60 s, rounded up | 16 |
| Worker skips ticks beyond `MaxLagTicks` | 15 | `MaxLagSeconds` 0.25 s | 8 |
| Sustained skipping window `SustainedSkipTicks` | 60 | 1 s | 30 |
| Server event repeat `ServerEventRepeatTicks` | 30 | 0.5 s | 15 |
| Authority call id memory `AuthorityCallLedger.DefaultTtlTicks` | 600 | 10 s | 300 |
| Crossing hold, collider extent refresh, portal admit check, portal body refresh and prune, adjacency refresh, stale crossing | 30, 60, 6, 30, 120/600, 30, 120 | 0.5, 1, 0.1, 0.5, 2/10, 0.5, 2 s | half |
| Client clock snap, render-offset snap, prediction jump and stall, lead relax margin and step | 3, 10, 8, 4, 3, 8 | the same seconds | half, at least 2 for jump and stall |
| Update-gap delay: slew, longest gap | 30/s, 60 | half the rate per second, 1 s (at most 63) | 15/s, 30 |
| `WorkerLoadTracker.TickPeriodMs` | 16.7 | 1000 / rate | 33.3 |
| Gateway relay loop | a quarter tick | a quarter tick, never longer than a quarter of a 60 Hz tick | 4.2 ms |
| Catch-up budget `WorkerCatchUpBudgetMs` | 40 ms | unchanged: it is wall time a frame may spend | 40 ms |

Kept as counts: `MaxCatchUpTicks` (ticks per FixedUpdate), the input redundancy (the last 3 inputs ride in each
packet), `StateHistory.GapToleranceTicks` (missing records), `ContainerCostMeter.MinTicksPerSample`, the client's
own-pawn gap telemetry, and buffer capacities (the predicted input ring of 128 ticks is 2.1 s at 60 Hz and 4.3 s at 30;
at 240 Hz it is 0.53 s). `NetworkIdentity.UpdateInterval` stays ticks between updates; a game that means a rate calls
`SetUpdateRate(updatesPerSecond)`, which converts at the running rate.

**Config upgrade.** The client and history fields were ints in ticks. A config saved by an older Nebula still holds
them under the old names; `NebulaConfig` reads them through `[FormerlySerializedAs]` into hidden legacy fields and, in
`OnAfterDeserialize`, converts them to seconds at 60 Hz (the only rate there was) once. The old names remain as
properties that read the value in ticks at the running rate, so `config.InterpolationDelayTicks` still compiles and
means what the runtime uses; setting one converts back at the current rate. `-nebula-state-history <ticks>` is ticks
at the running rate.

**Counted at 60 Hz, rescaled.** Three kinds of values are tick counts typed into inspectors across many assets, where a
unit change would mean migrating every prefab: the interest divisors (`InterestMidDivisor`, `InterestFarDivisor`, a
priority's `RelevanceTierBands`), `FrameAttachment.DetachSettleTicks` and `FrameInertia.SmoothingTicks`. They are
documented as counted at 60 Hz, and rescaled at another rate so they last as long or keep their updates per second
(`NetworkTime.FromDefaultRateTicks`, `RelevanceTiers.AtTickRate`): a middle band of 4 (15 Hz) is every 2nd tick at
30 Hz, a far band of 12 (5 Hz) every 6th, a background band of 30 every 15th; 1 stays every update and 0 stays none.
The far tier (`FarRelevance`) and the interest evaluation (`InterestEvalHz`) were already in Hz.

## 3. The client follows its gateway (D3)

A client boots at its own config's rate, then `NebulaClient.AdoptTickRate(WelcomeMsg.TickRate)` runs before the
welcome's tick anchors the server clock. On a change it calls `NetworkTime.Configure`, sets `Time.fixedDeltaTime` to the
new interval, and drops everything counted at the old rate: the server clock anchor and estimate, the latest server
tick, the prediction tick, the render offset, the adaptive delay and the input-lead adjustment. A welcome with 0 (a
gateway that never set the byte) is 60. The loadgen does the same, and sends one input per tick unless `--input-hz`
says otherwise.

**Compatibility.** A client of protocol 29 ignores the byte and counts at 60 Hz. Honouring it is part of protocol 30
(`HelloMsg.TickRateVersion`), which has not shipped, so no bump: a gateway at the default rate admits 29 and 30 as
before; a gateway at any other rate admits 30 only (`ProtocolCompatibility.MinClientProtocol(rate)`) and refuses 29
with `ProtocolUnsupported` and the narrowed window in `SupportedMinVersion`. Gateways and workers of a mesh must run
the same build, as before.

## 4. Physics (D4)

Unity's fixed step stays one tick on every role (`Time.fixedDeltaTime = NetworkTime.TickInterval`): the worker
simulates a tick in `FixedUpdate` and PhysX steps the default scene right after it; each physics frame and instance
scene is stepped by Nebula once per tick with the tick interval; a client predicts one tick per `FixedUpdate`. Keeping
them equal is what lets a predicted entity's casts see the same world on the owner and the worker.

At 30 Hz each step is 33 ms. What that means for fast bodies:

- **Kinematic bodies that sweep** (a ship moved by a cast along its step's displacement, a character controller,
  hitscan and projectiles that cast the segment they travel) are step-length independent: a 400 m/s ship sweeps 13 m
  instead of 6.7 m and still stops at the first hit. Holoverse's ships, vehicles and pawns are all of this kind.
- **Two fast kinematic bodies closing on each other** each sweep against the other's pose at the start of the step, so
  a head-on pass can be missed when their closing distance per step exceeds their combined length. That was already so
  at 60 Hz (800 m/s closing is 13 m a step); at 30 Hz it is 27 m.
- **Dynamic rigidbodies** that move fast need `CollisionDetectionMode.ContinuousSpeculative` or `Continuous`; discrete
  detection tunnels through anything thinner than speed × 33 ms.
- **Prediction** compares the owner's and the worker's state tick for tick, so the step length does not change whether
  they agree; the correction threshold already scales with a tick's travel (`CorrectionThresholdTickFraction`).

No substepping is built: nothing in Nebula's own physics needs it, and every fast mover in the test game sweeps. A game
with fast dynamic bodies that must collide with each other can lower its rate less, or step its own bodies in smaller
steps inside `Simulate`.

## 5. What a game must do

- Hold its own durations in seconds and convert with `NetworkTime.SecondsToTicks`, or use the `deltaTime` an update is
  handed. A bare tick count (`if (tick % 30 == 0)`) silently halves its rate at 30 Hz.
- Use `SetUpdateRate` rather than `UpdateInterval` for an entity that should update a number of times a second.
- Replace `const` uses of `NetworkTime.TickRate` and `TickInterval`.
