# Deployment and protocol compatibility policy — design

Status: implemented on this branch (NEB-228). Protocol stays **18**; 18 is the floor the policy starts from.
Version table: `docs/protocol-versions.md`. User-facing page:
`website/content/docs/deploy/upgrades.mdx`. Tests: `ConformanceProtocolCompatibilityTests`,
`RollingUpgradeTests`, and scale scenario S9 in `ScaleOperationsTests`.

## 0. Purpose

A mesh is several processes and a lot of installed clients, and they are not replaced at the same instant. Two
questions had no answer in the code: **which versions may talk to each other**, and **how a running fleet is
upgraded without dropping the players on it**.

What was there before: `NebulaGateway.DispatchClient` compared the peer's `HelloMsg.Version` to the
`HelloMsg.ProtocolVersion` constant for exact equality and closed the link — no message, no reason, no minimum.
`HelloMsg.Write` did not even serialise the instance's `Version` field; it always wrote the constant, so a peer
built from this source could not announce anything else (recorded as gap D8 in `docs/scale-suite.md`, now
rewritten). A client one release behind saw its connection drop with no explanation, and a rolling upgrade could
replace processes at one protocol version but never span two.

Non-goals: the game's own content versioning *logic* (Nebula carries a number and compares it, and that is all);
a CLI command for upgrades; asset or save-data migration; downgrade.

## D1. The window: N and N-1 for clients, exact for infrastructure

`HelloMsg.MinProtocolVersion` is the oldest protocol a gateway admits from a **client**, and
`HelloMsg.ProtocolVersion` is the newest. A gateway accepts the closed range between them. Today both are 19: the
step from 18 renumbered carrier behaviours and could not be additive, so 18 is outside the window, as 17 was before
it (see `docs/protocol-versions.md`).

Gateway-to-worker and worker-to-worker links require `HelloMsg.ProtocolVersion` **exactly**, in both
`NebulaGateway.DispatchClient` (for a peer that dials in) and `NebulaWorker.Dispatch`. Two reasons. These
processes are deployed together by one operator, so a mismatch is a deployment mistake that should be loud rather
than absorbed; and the internal messages are where the hard invariants live (authority epochs, lease epochs,
interest regions), so a half-understood message between two servers can corrupt a world rather than merely
disappoint a player. Both sides now log which version they saw and which they wanted, which is the thing that was
missing when the link simply closed.

Why one version and not three: every additive-only release has to be audited against every older version still
inside the window, and the suite can only carry a recording for so many of them. One step is enough to replace a
fleet under live players, which is what the window is for.

## D2. Refusals say why, in a form the client can act on

A client outside the window is now **refused with a message** rather than disconnected: `JoinRejectedMsg` with
`JoinRejectReason.ProtocolUnsupported`, `Retry = false`, and the gateway's `SupportedMinVersion` and
`SupportedMaxVersion`. That pair is what lets a game tell the two cases apart without guessing:

- the client's protocol is **below** `SupportedMinVersion` → the player must update the game;
- it is **above** `SupportedMaxVersion` → this server has not been upgraded yet (a common sight mid-rollout, and
  the reason to say something other than "update required").

The range and the server's content version ride on **every** refusal, not only a version one, so a client refused
for an unrelated reason still learns an update is waiting. `NebulaClient` exposes them as
`ServerProtocolWindow` and `ServerContentVersion` beside the existing `JoinRejectReason`, and does not retry
either mismatch: every gateway of the mesh would answer the same way.

## D3. The additive rule, and where the negotiated version lives

Inside the window every protocol change must be additive: a new message id, or a trailing optional field read
with `Remaining > 0`, exactly as `HelloMsg.ScopeKey` and `JoinRejectedMsg.Code` were added inside 18. A field
that moves, shrinks or disappears is not additive.

The gateway **records the negotiated version on the session** (`ClientConn.ProtocolVersion`, set from the Hello
before anything else is decided) and encodes for that client at that version: a field introduced in N is written
to an N-1 client only when the session's number allows it. It is echoed back in `WelcomeMsg.NegotiatedVersion`,
so a client knows which optional fields to expect, and it survives a reconnection because a reconnecting client
sends its Hello again. Today the window is one version wide, so nothing is gated yet; the mechanism and the test
that proves it exist now, which is the part that cannot be retrofitted in a hurry during a release.

`HelloMsg.Write` now writes the instance's `Version` when it has one and the constant otherwise. That one-line
fix is what makes any of this testable: a peer can announce a version that is not this build's.

## D4. The game's content version is a number Nebula carries

A game's own content — the scenes, the items, the rules — versions on a different clock from the wire protocol. A
client with last week's item table can speak protocol 18 perfectly and still be unplayable.

`HelloMsg` therefore carries a trailing optional `u32 GameContentVersion`, declared by the game
(`NebulaConfig.GameContentVersion`, `-nebula-content-version`, mirrored in the `Services~` config). The gateway
compares it with its own: exact match by default, or a range when `MinGameContentVersion` is set, and
`GameContentVersion = 0` turns the check off entirely so a game that does not version its content is unaffected.
A mismatch is `JoinRejectReason.ContentVersionMismatch` with the server's number attached.

Nebula does not interpret the number, does not order releases by it and does not migrate anything. The protocol
version is **not** bumped for the field: a Hello that ends before it reads as 0, which a gateway that declares no
content version accepts.

## D5. Drain and replace is a procedure, not a command

No new CLI command. A rolling upgrade is made of two things Nebula already does, and what this issue adds is the
statement of the procedure and the tests that it survives traffic.

**Gateways.** Set the drain flag on the gateway's control-plane row (`IControlPlane.SetGatewayDraining`). The
gateway tells its clients how long they have to reconnect (`GatewayDrainingMsg`,
`GatewayDrainReconnectSeconds`) and refuses new joins with `Retry = true`, so a load balancer's next attempt
lands elsewhere. Each client reconnects to another gateway with its session token and keeps its session id, its
identity and its pawn. Then stop the process and start the new build. Repeat one gateway at a time.

**Workers.** Hand the worker's entities to the workers taking over (authority transfer) and move its leases,
then let it exit and start the replacement. A drained worker leaves **no orphaned container**, which is the whole
difference from the worker-kill scenario (S4): nothing has to be restored, because nothing was lost.

Order matters within a mesh: because D1 makes the infrastructure tier an exact match, gateways and workers are
upgraded in one pass, together, while the client window covers the clients that have not updated yet.

## D6. The tests

| Test | What it pins |
|---|---|
| `ConformanceProtocolCompatibilityTests.TheWindowIsOneVersionWideAtMost` | `Min == Current` or `Min == Current - 1`, with the bump procedure in its own doc comment |
| `…AHelloAnnouncesTheSendersOwnVersionAndItsContentVersion` | the D3 fix: a peer can announce a version that is not this build's |
| `…AHelloWithoutTheContentVersionFieldIsReadAsZero` | the trailing field is optional both ways |
| `…AClientOutsideTheWindowIsRefusedWithTheGatewaysRange` | the refusal is a message with a code and a range, not a silent close |
| `…AContentVersionOutsideTheGatewaysWindowIsRefusedWithItsOwnCode` | D4, against a real gateway |
| `…ARecordedClientStreamIsAdmittedAndAnsweredByAGatewayOfThisBuild` | the replay: recorded bytes, real gateway |
| `RollingUpgradeTests` (S9a, S9b) | drain and replace a gateway under a connected client; drain and replace a worker with no entity lost |
| `ScaleOperationsTests` S9 | all four edges of the window, with reason codes |

`RollingUpgradeTests` carries `Scale` but **not** `Soak`, unlike S9. The scale suite's rule (D10) is that a
scenario running a mesh for seconds carries `Soak` too; these two take about 10 s together, and the drain paths
they cover are exactly the kind that rot quietly, so they are worth running in the default `dotnet test` pass.
The deviation is deliberate and recorded here rather than left to be noticed.

**The recorded fixture.** `Services~/Nebula.Services.Tests/Fixtures/protocol-19-handshake.json` holds the exact
frames a client sent and a gateway answered, as hex, recorded from the mesh fixtures by the explicit test
`RecordTheHandshakeFixture`. The replay pushes the client frames byte for byte at a gateway built from today's
source and asserts it is welcomed, negotiated at the recorded version and sent a world it can parse; it then
decodes both recorded replies and newly generated replies with an independent, frozen protocol-19 reader
(`Protocol19GatewayDecoder`; protocol 19 changed no frame layout, so it is the protocol-18 reader with its version
literals raised).
The reader checks the public-container handshake, pawn ownership, spawn framing, and transform values. It
accepts optional trailing fields and unknown message IDs as the protocol-19 client does. Truncated fields and
malformed nested snapshots have negative tests. Game-defined payloads and private-instance messages are outside
this recording's coverage.
Because the window is one version wide today, this proves N compatibility only. The protocol-18 recording is
kept and replayed to prove that such a client is refused with the gateway's range. At an additive protocol 20 the
protocol-19 recording and reader can exercise N-1. Later versions need their own recorded bytes and frozen reader; updating
both production readers and writers must not silently update the compatibility oracle.

Since NEB-321 that is the case: protocol 20 (sync audiences, `docs/sync-audience.md` D10) is additive, the window is
19..20, the protocol-19 replay exercises N-1, and `protocol-20-handshake.json` is recorded for the next bump. The
first field written differently by negotiated version is the `Cleared` sync chunk, sent only to protocol-20 clients.

## D7. Measured on this machine

`dotnet test --filter "TestCategory=Conformance"`: **105 passed, 0 failed, 50 s** (6 of them this issue's; the
seventh, the recorder, is explicit and runs only when named).
`dotnet test --filter "TestCategory=Scale"`: **14 passed, 0 failed, 1 m 27 s** (S9 rewritten, S9a and S9b new).
`dotnet test --filter "TestCategory!=Soak&FullyQualifiedName!~AcmeTests"`: **475 passed, 0 failed, 10 m 4 s**. The
first attempt at that run aborted with "test host process crashed" after 42 tests and no failure; the re-run,
which the suite's own notes say to do when two test processes collide on a loopback port, was clean.
Unity: `Tools/typecheck.ps1` reports the same 43 pre-existing errors with and without this change, all of them in
`Samples~/LagCompensatedHitscan/Tests` (a missing NUnit reference in that sample's assembly definition); every
`Nebula.Runtime` assembly compiles.

## D8. What was verified, and what was not

Verified: the window's four edges against a real gateway on a real socket; the refusal codes and the range on the
wire; the content-version window, including that a gateway which declares none refuses nobody; a recorded byte
stream admitted and answered; a gateway drained and replaced with the session, identity and pawn intact; a worker
drained and replaced with no entity lost, no orphaned container and no client disconnected.

Not verified:

- **A genuine N-1 client.** There is no build that speaks 17 and can be run against this one, and 17 is outside
  the window by design. The recorded stream and frozen reader exercise protocol 19 against 19 today. They
  prepare future N-1 coverage but do not establish current support for protocol 17 or every game payload.
- **Processes.** The drain-and-replace tests replace objects in one process, not operating-system processes on
  separate machines behind a load balancer. The gateway half was measured this way; a fleet with a real load
  balancer in front of it has never been run here. See `docs/scale-suite.md` D9a.
- **The per-version encoder.** Nothing is gated on the negotiated version yet, because the window is one version
  wide. The session field, the welcome echo and the replay exist; the first field actually written differently to
  an N-1 client will be the first real exercise of it.
- **Unity EditMode tests.** Not run: the worktree has no `Library/` and the Editor is open elsewhere. The Runtime
  changes were checked with `Tools/typecheck.ps1`, and every behavioural test here runs under `dotnet test`.
