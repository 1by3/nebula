# Gateway fleet operations audit (NEB-229)

Status: landed with NEB-229. User-facing page: `website/content/docs/guides/connecting-clients.mdx` §"Reconnect
after a lost link or a draining gateway" (the hard-kill reclaim bound and the load-balancer boundary). Fix:
`Packages/com.1by3.nebula/Runtime/ControlPlane/GatewaySessionCoordination.cs` (`GatewaySessionDirectory`,
`LocalControlPlane.GatewayKeyAlive`). Unit tests: `Services~/Nebula.Services.Tests/GatewaySessionDirectoryTests.cs`,
`GatewayFleetTests.cs`. Scale suite: `docs/scale-suite.md` scenario S5, gap D7a (closed here), and
`Services~/Nebula.Services.Tests/ScaleFailureTests.cs` / `ScaleGatewayAuditTests.cs` (new).

## 0. Purpose

NEB-229 asked for a gap audit, not a new feature: "much exists already (`GatewayFleetTests`, drain requests, the
session directory)". This document numbers every finding the audit produced — what already worked, what did not,
and what changed — so the issue closes against something checkable rather than a feeling that the fleet is safer
now. Every finding is closed or open with a reason; nothing is asserted without a test or a read of the code next
to it.

## D1. What "the gateway fleet" means here

A Nebula gateway is stateless about the world (workers hold authoritative entities; the control plane holds
leases): a gateway's only state is which clients are linked to it and which player sessions it currently owns
(`GatewaySessionDirectory`, one directory shared by every gateway through the control plane). That is what makes
gateway loss recoverable in principle — the question this audit answers is whether the *code* actually recovers
it, and how fast.

## Finding 1 — Load balancing across gateways: **closed, by design, with a documented boundary**

What exists: `GatewaySessionDirectory` (one player, one gateway owner at a time, arbitrated through the control
plane) and the session-token reclaim path (`GatewaySessionAdmission.BeginCoordinatedWelcome`): a client that
connects to *any* gateway in the mesh with its session token gets its old session back, wherever it was. A drain
(`IControlPlane.SetGatewayDraining`) tells connected clients to reconnect (`DrainWithin` seconds,
`NebulaConfig.GatewayDrainReconnectSeconds`) and refuses new joins with `Retry = true`
(`GatewayFleetTests.ADrainRequestTellsClientsToReconnectAndRefusesNewOnes`, already covered before this audit).

What does not exist, and is not in scope: Nebula does not pick which gateway a *new* client's first connection
goes to, and does not run a reverse proxy, DNS load balancer or connection router in front of the gateway fleet.
`website/CONTENT_GUIDE.md` already states this boundary ("Nebula does not provide the load balancer that spreads
clients over several gateways"); it stays true after this audit and is not something this issue changes. What
*is* new: the operator's load balancer or DNS now has a **bounded** amount of time to redirect an existing
client's reconnect to a live gateway before Nebula's own session coordination would otherwise refuse it (finding
2). An operator runs one gateway process per fleet member behind whatever balances load in their deployment (a
plain reverse proxy, a cloud load balancer, or DNS with health checks pointing away from a dead gateway); Nebula
gives it the primitives — the drain signal, the session directory, the reclaim bound below — needed to make that
safe, not the balancer itself.

**Closed.** No code change; documented so the boundary is unambiguous (`website/CONTENT_GUIDE.md`, unchanged
sentence, and `website/content/docs/guides/connecting-clients.mdx`, updated).

## Finding 2 — Drain: **closed, already covered**

`GatewayFleetTests.ADrainRequestTellsClientsToReconnectAndRefusesNewOnes` (pre-existing, not touched by this
issue) asserts the full path: `SetGatewayDraining(true)` → connected clients receive `DrainWithin` and reconnect
on their own with their session token → a late join to the draining gateway is refused with `Retry = true` and a
reason containing `"draining"` → the heartbeat reports `GatewayStats.Draining` so an operator's dashboard/load
balancer can see it → the replacement gateway's welcome carries the same pawn (no despawn, no duplicate spawn) →
the drain can be cancelled. A draining gateway therefore does stop accepting new joins and does hand its sessions
to a peer, exactly as the issue asked to verify. **Closed, no change.**

## Finding 3 — Replacement: a new gateway registering under connected clients — **closed, already covered**

`GatewayFleetTests.AGatewayRegistersAgainWhenTheControlPlaneForgetsIt` and the rolling-upgrade scenario (S9,
`docs/scale-suite.md` D8, `ScaleOperationsTests`) cover a gateway process being replaced while clients are
connected to the *rest* of the fleet: the incoming process re-registers under its id, an incarnation bump
invalidates the old process's drain flag and stats (`LocalControlPlane.RegisterGateway`), and connected clients on
other gateways are undisturbed. What a *hard-killed* gateway's own connected clients see, and how they get a
session back on a different gateway process, is finding 4. **Closed, no change.**

## Finding 4 — Authoritative state on gateway loss: **closed, D7a fixed, asserted directly**

This was the audit's central gap (`docs/scale-suite.md` D7a before this issue): `GatewaySessionDirectory` granted
a takeover only when the previous gateway *released* its claim, and a gateway that is killed outright (no
`Dispose`, no `release`) never does. Every reclaim attempt was pended for 15 s, re-pended on retry, and never
evicted — nothing treated a gateway that had stopped heartbeating as gone. The pinned negative test
(`ScaleFailureTests`, previously `AGatewayThatIsKilledOutrightStrandsItsSessionsUntilSomethingEvictsItsClaims`)
proved it: every reclaim was refused after the 10 s coordination deadline.

### The fix

`LocalControlPlane` already tracks every gateway's `LastHeartbeat` (`RegisterGateway` / `HeartbeatGateway`,
existing code, used by the orchestrator at `NebulaOrchestrator.cs:586` to evict a gateway row from the fleet after
`WorkerTimeoutSeconds * 3`). `GatewaySessionDirectory` did not have access to it. This issue wires it in:

- `GatewaySessionDirectory` takes an optional `gatewayAlive: Func<string, bool>` predicate, keyed on
  `PlayerSessions.GatewayKey` (gateway id + incarnation). `null` (the default for any caller that does not opt in)
  reproduces the pre-fix behaviour exactly — nobody is ever evicted — so nothing outside `LocalControlPlane`
  had to change.
- `LocalControlPlane.GatewayKeyAlive` parses the key, looks the gateway id up in the control plane's own
  `Gateways` list, and returns false when: the gateway is not registered at all (already unregistered or never
  existed), the registered row's `Incarnation` does not match the key's (a different process now owns that id —
  the process that made the original claim is gone even though something is answering under the same id), or the
  row's `LastHeartbeat` is older than `GatewayStaleAfterSeconds` (a new field on `LocalControlPlane`, defaulting to
  5 s — the same cutoff `IsWorkerAlive` uses for a worker row, and `NebulaConfig.WorkerTimeoutSeconds`'s own
  default).
- `GatewaySessionDirectory.HandleCore` uses this in two places: a **claim** against an owner that is already
  confirmed gone is granted immediately instead of being parked for the 15 s pending window (the fast path — this
  is what makes the common case, "the owner was already dead when the surviving gateway's client tried to
  reclaim," bounded by the staleness window rather than by 15 s); and a claim already **pending** re-checks the
  owner's liveness on every retry (every 0.25 s, `GatewaySessionAdmission.RetryCoordination`), so an owner that
  dies *while* a claim is waiting on it is evicted as soon as it goes stale rather than only at the 15 s pending
  expiry. The incarnation check means a merely slow-but-alive owner — heartbeating, just late — is never evicted:
  eviction requires the row to be gone, re-incarnated, or stale by wall clock, never a race with a live process.

### The measured bound

`ScaleFailureTests.AGatewayThatIsKilledOutrightHasItsSessionsReclaimedOnceItsHeartbeatGoesStale` (the turned-around
pinned test) hard-kills a gateway holding 4 of 8 sessions and reclaims them all onto a survivor. Three runs on
this machine: **4.73 s, 4.73 s, 4.76 s** (`Logs/scale/synthetic-gateway-kill.csv`). A fan-out variant
(`ScaleGatewayAuditTests.EveryClientOnAHardKilledGatewayReclaimsWithinTheDocumentedBoundRegardlessOfFleetSize`,
3 gateways, 2 workers, 18 clients, 6 doomed) measured **5.02 s** — the bound does not grow with how many
claimants are queued behind the same dead owner, because the fast path evicts on each claimant's own retry rather
than serially.

**`ScaleThresholds.HardKillGatewayReclaimSeconds = 9.0 s`** (`Fixtures/ScaleHarness.cs`), set from these numbers
with roughly 1.9× headroom for a loaded CI machine. Derivation: `NebulaConfig.WorkerHeartbeatSeconds` (1 s — how
stale the last-seen heartbeat can already be at the instant of the kill) + `GatewayStaleAfterSeconds` (5 s — how
long a heartbeat may then go missing before the row is treated as gone) + coordination (the 0.25 s retry cadence
and one claim round trip) ≈ 6.25 s derived, 4.7–5.0 s measured, 9.0 s threshold.

### No state lost

`ScaleGatewayAuditTests.AHardGatewayKillLosesNoAuthoritativeStateAndTheReclaimingClientSeesTheSamePawn` asserts
directly what the issue asked for: the worker (never touched by the kill) still holds the doomed client's pawn,
under the same `NetId`, throughout the outage; the reclaim keeps the same `ClientId`; the reclaiming client's
replica set contains that same `NetId` (not a respawn); and the worker's pawn count is unchanged before and after
(zero duplicates). Measured: `pawnsBefore=4, pawnsAfter=4, samePawn=True, duplicatePawns=0`
(`Logs/scale/synthetic-gateway-kill-state.csv`). This was already implied by the architecture — a gateway holds no
authoritative state — but was not asserted by a test before this issue.

**Closed.** `docs/scale-suite.md` D7a is rewritten as closed, S5 and D6's gateway-kill row are updated, and the
pinned negative test is now a positive regression test.

## Finding 5 — Client-side reclaim behaviour: **closed, documented, one boundary to call out**

`Runtime/Client/NebulaClient.cs`: on a transport disconnect the client sets `LastError`, clears its world view, and
retries the **same** `Config.GatewayAddress:Config.GatewayPort` once a second (`_nextConnectAttempt = ... + 1f`,
`HandleTransportEvent`, case `Disconnected`) — indefinitely, with its stored session token, so a successful
reconnect always attempts a reclaim (`BeginCoordinatedWelcome`). The client does **not** maintain a list of
gateway addresses and does not itself choose a different one: `Config.GatewayAddress` is a single string, set by
`Connect`/`ConnectTo`, and nothing in `NebulaClient` changes it in response to a failure. This is the client half
of finding 1's boundary: whether a reconnect ever reaches a *live* gateway within the reclaim bound depends
entirely on what `Config.GatewayAddress` resolves to — a fixed IP (single point of failure, matches finding 1's
"Nebula does not provide the load balancer"), or a DNS name / VIP an operator's load balancer keeps pointed at a
live gateway. Nebula gives the reconnect logic and the session-token reclaim; the address resolution is the
operator's.

**The bound from the client's perspective**, assuming `Config.GatewayAddress` already resolves to a live gateway
(operator responsibility, finding 1): transport disconnect detection (one missed heartbeat cycle on the
transport, not separately measured here — LiteNetLib's own timeout) + up to 1 s until the next connect attempt +
the gateway-side reclaim bound (finding 4: ≤ 9.0 s, typically ~5 s) ≈ **at most ~10 s, typically ~6 s**, end to
end from the old gateway going silent to the client holding its old pawn again on the new one. This composite
number is not independently asserted by a test (the client's own transport-timeout detection is LiteNetLib's, out
of this issue's scope); it is the arithmetic of the two measured/derived halves and is documented as such, not as
a new guarantee.

**Closed**, with the boundary written down rather than left implicit.

## D2. What was not touched (explicitly out of scope)

- `docs/scale-suite.md` D7b/D7c (control-plane availability, NEB-227) — a control plane that restarts without its
  storage still loses its workers; not this issue's gap.
- `docs/scale-suite.md` D8/S9 (protocol compatibility window, NEB-228) — `HelloMsg`, the version check and
  transport code are NEB-226/228 territory and were not edited here.
- The load balancer itself. See finding 1.

## D3. Non-goals

Not a rewrite of session coordination: the fix is additive (a new optional constructor parameter, a new field
with a safe default) and every existing `GatewaySessionDirectory` behaviour for an owner that is genuinely alive
or merely slow is unchanged (`GatewaySessionDirectoryTests.AClaimStillWaitsWhenTheOwningGatewayIsMerelySlowNotGone`,
`ANullGatewayAlivePredicateNeverEvictsAnOwner`). Not a database failover story (NEB-227). Not a new client-side
gateway-discovery mechanism — the audit found there is none, documented the boundary, and left it there.
