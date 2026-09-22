# Cross-worker calls — delivery, ordering, fencing and outcome

Status: design of record for protocol **v18** (breaking). Decisions made without asking are marked **D#**.
Linear: NEB-221. User-facing guide: `website/content/docs/guides/rpcs.mdx`. Conformance tests:
`Tests/EditMode/ConformanceCallContractTests.cs` (also compiled into `Services~/Nebula.Services.Tests`).

## 0. Problem

Before v18 an `AuthorityRpc` sent to a ghost's owner was a bare `EntityRpcMsg` on the worker link. The receiving
worker invoked it if it had authority, forwarded it once if it had just handed the entity off (`_handedOff`), and
otherwise dropped it silently. Nothing identified a call, so nothing could tell a second arrival from a first; the
epoch on the message was written but never read; a forward could loop between two workers that each believed the
other had authority; and the sender never learned whether anything happened. Games built damage, pickups and door
interactions on this without a stated contract to build against.

Worker messages (`SendToWorker`) had the same lack of a statement, though a simpler one: they were, and remain,
fire-and-forget.

This document states the contract, and the code and tests enforce it. It does **not** redesign handover, add
transactions across several targets, add distributed locks, or judge whether a call's gameplay claim is valid
(§8).

## 1. Terms

- **Call**: one `AuthorityRpc` invocation that leaves the calling worker because the target is a ghost there.
  A call on an entity the caller has authority over runs in-process and never enters this contract.
- **Sender**: the worker that minted the call. **Authority**: the worker that has authority over the target when
  the call is decided. **Path**: the workers the call visits between them.
- **Epoch**: the entity's authority epoch (`NetworkIdentity.Epoch`), bumped by one on every handover.
- **Decision**: what a receiving worker does with a call: **apply**, **forward**, or **reject** with a reason.

## 2. Code layout

All logic that decides a call is pure C# with no engine types, in `Runtime/Worker/AuthorityCallContract.cs`, and is
compiled into both the Unity runtime and `Services~`:

- `AuthorityCallOutcome` — the outcome enum, also the reply's wire value (§7).
- `AuthorityCallId` — how a call id is composed (§4).
- `AuthorityCallLedger` — the bounded memory of applied call ids (§6).
- `AuthorityCallRouter` — the rules: `Decide(callId, callEpoch, hops, target, tick)` → apply / forward / reject (§5).
- `AuthorityCallTracker` — the sender's pending replies and their deadlines (§7).
- `AuthorityCallTarget`, `AuthorityCallDecision`, `AuthorityCallAction`, `AuthorityCallResult` — the values in and out.

`NebulaWorker` owns one router and one tracker (`OnAuthorityRpc`, `SendAuthorityCall`, `ReplyToCall`,
`OnAuthorityRpcReply`) and does only what the decision says. The wire shapes are `AuthorityCallMsg` and
`AuthorityCallReplyMsg` in `Runtime/Protocol/Messages.cs`. The public entry points are the `AuthorityRpc` and
`AuthorityRpcWithReply` overloads on `NetworkBehaviour`.

## 3. Delivery and ordering

- A call travels on the worker-to-worker link as `MsgId.AuthorityRpc` (46) with **reliable ordered** delivery, the
  same channel handover (`AuthorityTransfer`) and ghost spawns use. Between one pair of workers, a call therefore
  arrives after every message sent before it on that link, including the handover of its target. This is what
  makes forwarding correct: when B has handed the entity to C before A's call arrives at B, B's own transfer message
  to C is already ahead of the forwarded call on the B→C link.
- **Nothing is ordered across links.** Two calls for the same entity from two senders, or one call forwarded through
  B and a later one sent straight to C, may apply in either order. A game that needs an order between calls from
  different workers must carry it in the arguments (a tick, a sequence of its own) and enforce it in the handler.
- A call is never retried by Nebula. Reliable delivery covers loss on a live link; a link that dies loses the call,
  and the sender learns that only through a reply timeout (§7) or not at all.

## 4. Identity of a call

**D1. The sender mints the id: its worker index in the top 16 bits, a sequence in the low 48**
(`AuthorityCallId.Make`), the same shape as a `NetId`. Forwarding keeps the id, so the sender, every worker on the
path and the reply all name the same call, and a reply can be routed from the id alone (`WorkerIndexOf`).

**D2. The sequence starts at `(incarnation & 0xFFFF) << 32`** (`AuthorityCallId.InitialSequence`). A worker that
restarts under the same index mints ids its previous run never used, so a receiver whose ledger still holds the old
run's ids cannot mistake the new run's first calls for duplicates. 2³² calls per incarnation before the range could
touch another incarnation's, and the ledger forgets an id after ten seconds anyway (§6).

## 5. Fencing by epoch

Every call carries the epoch of the sender's copy of the entity at the time of the call (`AuthorityCallMsg.Epoch`).
The receiving worker compares it with its own copy's epoch.

**D3. The window is tolerant, and its width is the hop bound.** A call is **stale** when its epoch is *ahead* of the
receiver's copy, or more than `AuthorityCallMaxHops` (default 3) *behind* it; anything within the window is
current (`AuthorityCallRouter.IsStale`).

Why not strict equality: the case forwarding exists for is exactly the one a strict rule would reject. A hands over
to C while A's call to B is in flight; B and C are now at epoch e+1, the call says e. Every handover bumps the epoch
by one and costs the call one forward, so a call that reaches its authority within the hop bound is never more than
`MaxHops` epochs behind. A call further behind than that did not arrive by any legitimate forwarding path: its
sender was acting on a copy that had missed several authority changes (its ghost stream would have been rejected
for the same reason), and the entity it was looking at is not, in the sense the epoch measures, the one the
authority holds. A call from the *future* (its epoch ahead of the receiver's) means the receiver's copy is behind
the caller's; a ghost that far behind is not the authority the caller meant, and an authoritative copy is never
behind a ghost, so this only happens on a ghost and is rejected rather than forwarded.

With `AuthorityCallMaxHops = 0` the rule degenerates to strict equality and no forwarding: an operator who wants
fail-fast semantics can have them.

Staleness is checked **before** authority, so a ghost does not forward a call its own copy already knows is stale.

The epoch is the only past the call carries. A call that needs a *tick* - "I shot at what tick 412 looked like" -
puts it in the arguments, and the worker that applies the call answers it from its own recorded state history
(`NetworkIdentity.StateAt`, `docs/state-history.md`). That works on the authority and on any worker holding a ghost,
and it survives the forward: a worker that gained authority mid-flight still has the ticks it recorded while it was
ghosting the entity (state history D5). Whether the claimed tick is one the caller could plausibly have been looking
at is the game's decision, not the contract's (§11).

## 6. At-most-once

**D4. Application is at most once per call id, per worker, within the ledger's bounds.** Every worker keeps one
`AuthorityCallLedger` of the call ids it has *applied* (not forwarded: the same call may legitimately return to a
worker after another handover, and only an application is an application). A call whose id is in the ledger is
rejected with `RejectedDuplicate` and nothing runs. Since authority is held by one worker at a time and the ledger
key is the call id alone, this is also at most once per call across the mesh, for as long as the ledger remembers
the id.

The ledger is bounded two ways, and the bounds are the contract:

- **Capacity: 4096 ids** (`AuthorityCallLedger.DefaultCapacity`). Recording past it forgets the oldest id first.
- **Time: 600 ticks** (`DefaultTtlTicks`, ten seconds at 60 Hz). `Expire(tick)` runs once a frame from
  `NebulaWorker.Update`.

Beyond either bound a duplicate of a forgotten call would be applied again. In practice a duplicate can only be
produced by a forwarding loop (bounded by §5's hop rule, so it cannot outlast the ledger) or by a future retry
feature; reliable ordered delivery does not duplicate messages on its own. The bounds are constants, not config:
nothing in a game's behaviour should depend on them, and making them tunable would invite exactly that.

The issue asked for "per call id per target epoch". Keying on the id alone is strictly stronger and simpler, and it is
what the ledger does.

## 7. Forwarding after handover, bounded

**D5. A call may be forwarded at most `AuthorityCallMaxHops` times, default 3.** `NebulaConfig.AuthorityCallMaxHops`,
overridden by `-nebula-authority-call-hops`, mirrored into the services config. A worker that receives a call for a
ghost forwards it, with `Hops + 1`, to the worker it believes has authority: the worker it handed the entity to if it
was the last authority (`_handedOff`), else the owner its ghost names (`OwnerWorkerIndex`). Never back to the peer it
came from, and only over a link that has completed `Hello`. A call arriving with `Hops >= MaxHops` at a worker without
authority is rejected with `RejectedHopLimit`; a call arriving at its authority is applied whatever its hop count. A
ghost with nobody to forward to rejects with `RejectedUnreachable`; a worker that does not hold the entity at all
rejects with `RejectedUnknownEntity`.

Three is enough for a handover chain a call can realistically race (A→B→C→D within one round trip is already
unusual); more hops only delay the answer to a call that is chasing an entity it will not catch.

## 8. Outcome

**D6. A reply is opt-in.** The fire-and-forget `AuthorityRpc` overloads are unchanged in signature and now carry a call
id and are subject to §5–§7; a rejection is logged as a warning on the worker that decided it, and that is all the
sender hears. `AuthorityRpcWithReply(method, args…, onDone, timeoutSeconds = 5)` sets `WantsReply` on the call; the
worker that decides it sends `MsgId.AuthorityRpcReply` (49) to the sender (in-process when the sender is itself), and
the sender's `AuthorityCallTracker` runs `onDone` exactly once with an `AuthorityCallResult`:

| Outcome | Meaning |
|---|---|
| `Accepted` | The authority ran the method once. The handler may still have thrown (logged there) or refused the claim; Nebula does not know. |
| `RejectedStaleEpoch` | §5. `TargetEpoch` says how far ahead the entity is. |
| `RejectedUnknownEntity` | No worker on the path holds the entity (despawned, or never there). |
| `RejectedHopLimit` | §7. |
| `RejectedDuplicate` | §6: this id was already applied. |
| `RejectedUnreachable` | No link to the entity's owner, on the sender or on a forwarder. |
| `TimedOut` | No reply within the deadline. The call may or may not have been applied. Sender-side only; never on the wire. |

`AuthorityCallResult` also carries the call id, the deciding worker's epoch for the entity (0 when it did not hold
it) and the hop count at decision. A call applied locally (the caller had authority) reports `Accepted` with call id
0 and no wire trip. A call refused before it leaves (not spawned, not a worker, no link) reports `RejectedUnreachable`
synchronously, so a caller always hears exactly once.

Late replies (after a timeout) are counted (`AuthorityCallTracker.LateReplies`) and dropped. `NebulaWorker` exposes
`AuthorityCallsApplied`, `AuthorityCallsForwarded`, `AuthorityCallsRejected` and `AuthorityCallsPending`.

## 9. Worker messages

**D7. Worker messages stay fire-and-forget, and the guide says so.** `SendToWorker` sends `[kind][payload]` once, on
the delivery class the caller chose (reliable ordered by default), to one worker, with no call id, no ledger, no
forwarding and no reply. A message is delivered at most once by the transport and exactly once on a live reliable
link; if the link drops, it is lost and nobody is told. A game that needs an answer uses `WorkerQuery` or
`EntityRequests`, which carry their own request ids and deadlines; a game that needs a message to follow an entity
uses `AuthorityRpc`. Giving worker messages the AuthorityRpc contract would mean inventing a target for the epoch and
the forward, and they have neither: they address a worker, not an entity.

## 10. Wire changes (v17 → v18)

- `MsgId.AuthorityRpc` (46) now carries `AuthorityCallMsg`:
  `u64 call_id, u8 hops, u8 flags, u64 net_id, u32 entity_epoch, u8 behavior_index, u32 method_hash, bytes args`.
  Flag bit 0 = `WantsReply`. `EntityRpc` (13) and `ServerRpc` (21) keep `EntityRpcBody`.
- New `MsgId.AuthorityRpcReply` (49): `u64 call_id, u8 outcome, u32 entity_epoch, u8 hops`.
- `HelloMsg.ProtocolVersion` 17 → 18.

## 11. What is explicitly not promised

- **Ordering across links** (§3). Two senders' calls on one entity, or a forwarded call and a direct one, have no
  relative order.
- **Exactly-once.** At most once within the ledger's bounds (§6); the sender learns of an application only when it
  asked, and a timeout tells it nothing.
- **Delivery through a link failure.** A worker that dies with a call in its queue loses it. Nebula does not retry.
- **That the claim meant anything.** `Accepted` means the method ran. Validating damage, range, cooldowns or
  permissions is the handler's job, on the authority, as before.
- **Atomicity across targets.** Two `AuthorityRpc`s to two entities on two workers are two calls.
- **Locks.** Nothing here holds authority still while a call is in flight; a target may hand over again right after
  the call is applied.
- **Behaviour-index or method-hash mismatches.** A call whose behaviour index or method hash names nothing on the
  receiving entity is a game-schema mismatch (the protocol requires matching schemas on every peer). It is logged on
  the receiver and counts as `Accepted`: the contract is about delivery, and the call was delivered.
- **Ledger memory across a worker restart.** A restarted worker's ledger is empty; D2 keeps the *new* run's ids
  apart from the old, but a call sent to the old run that arrives at the new one is simply unknown or stale.

## 12. Tests

`ConformanceCallContractTests` (`[Category("Conformance")]`, pure C#, in both test projects):

- (a) A target that hands over between send and apply: B forwards with hop 1 and the same id, C applies once, a replay
  of the same id at C is `RejectedDuplicate`, the sender observes `Accepted` at epoch 2 with 1 hop, the duplicate's
  reply is late. Plus the forward-back-to-sender case, settled in-process.
- (b) A stale epoch is `RejectedStaleEpoch`, not applied, not ledgered, and the sender observes it with the target's
  epoch. The window is exactly the hop bound; `MaxHops = 0` is strict equality; staleness beats forwarding.
- (c) Forwarding stops after exactly `MaxHops` forwards with `RejectedHopLimit`; a call at the bound is still applied
  by its authority; unreachable and unknown are reported as such; a fire-and-forget call is decided by the same rules
  with no reply.
- (d) The ledger forgets the oldest past capacity and still dedupes the newest; expires by tick; records only what it
  applies; the defaults are 4096 / 600.
- (e) Call ids carry their sender and restart into fresh ranges; the tracker settles exactly once by reply or by
  deadline; `AuthorityCallMsg` and `AuthorityCallReplyMsg` round-trip and their bytes are pinned; the protocol
  version and the new ids are pinned.

## 13. Decisions

- **D1** Sender-minted call id: worker index ‹‹ 48 | sequence (§4).
- **D2** Sequence seeded from the incarnation so a restart cannot collide (§4).
- **D3** Tolerant epoch window, width = hop bound; ahead is always stale; checked before authority (§5).
- **D4** At most once per call id per worker, ledger bounded at 4096 ids / 600 ticks, constants not config (§6).
- **D5** Forwarding bounded at `AuthorityCallMaxHops` (default 3, `-nebula-authority-call-hops`), to the handed-off
  worker or the ghost's owner, never back to the sender of the hop (§7).
- **D6** Reply opt-in via `AuthorityRpcWithReply`; fire-and-forget overloads unchanged but governed by the same rules;
  every caller that asked hears exactly once (§8).
- **D7** Worker messages remain fire-and-forget and are documented as such (§9).
- **D8** `MsgId.AuthorityRpc` keeps its number and changes its body; the reply is a new id (49); protocol 18 (§10).
- **D9** Schema mismatches on the receiver count as delivered (`Accepted`), not as a rejection (§11).
