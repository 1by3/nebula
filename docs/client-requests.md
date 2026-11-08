# Client requests — design

Status: implemented (protocol 33). User-facing page: `website/content/docs/guides/client-requests.mdx`. Tests:
`ClientRequestRuleTests` and `ConformanceClientRequestGatewayTests` (`Services~/Nebula.Services.Tests/ConformanceClientRequestTests.cs`),
`ConformanceClientRequestWorkerTests` (`Tests/EditMode`). Sibling of `docs/cross-worker-calls.md`, whose reply machinery this reuses in shape.

## 0. Purpose

A game often needs a player to act on an object it can see and does not own: touch a door, sit on a chair, take an item, edit a prop. Before this
change Nebula gave a client two ways to call a worker, and neither fits:

- `ServerRpc` is accepted only from the **owner** of the entity (the gateway checks `OwnerClientId`, the worker checks it again). The owner of a
  door is nobody.
- `AuthorityRpc` is sent **between workers**, and only from a worker that holds a copy of the target. A client that can see an entity 30 m away across a
  worker seam is on a worker that may hold no copy of it (a ghost band is a few metres), so there is nobody to send it.

The workaround, a `ServerRpc` on the player's own pawn that then makes an `AuthorityRpc` to the target, fails for the same reason when the pawn's worker
does not hold the target, and every game would have to rebuild the rate limiting, the sender plumbing and the reply on top.

`[ClientRequestRpc]` is that path, built once: a client sends it to any entity in its interest set; the gateway checks and stamps it; it is routed to the
worker with authority wherever that is; the handler receives who sent it; the client may ask what became of it.

## D1. The name, and the shape of the API

`[ClientRequestRpc]` on the receiving method, `ClientRequest(...)` and `ClientRequestWithReply(...)` on the sending side. The existing attribute names say
where the method **runs** (`ClientRpc` on clients, `ServerRpc` on the server, `AuthorityRpc` on the authority), so `ClientRequestRpc` is not in the same
family on a strict reading: it runs on the server, and a client sends it. The name was chosen over `RequestRpc` (reads as generic request/response, which this is not: the reply is a verdict, not data) and
`InterestRpc` (describes the admission rule, not what it is for). It reads in code as a *client's request*, which is how a handler should think of its arguments. The doc comment on
the attribute states where it runs.

The method's **first parameter is a `ClientRequestContext`**. Nebula fills it in; it is not serialised. The registry refuses a `[ClientRequestRpc]` whose
first parameter is anything else, so a handler cannot be written that does not receive the sender.

## D2. What the client may send, and where the checks live

Checks run in the **gateway** first and the **worker** again. The gateway is the right place for the cheap ones: it knows the sender's interest set and holds the connection, it
sees a flood before the flood reaches a simulation, and a drop there costs a worker nothing.

| Check | Where | Outcome when it fails |
|---|---|---|
| Arguments larger than `ClientRequestMaxBytes` (1024) | gateway, worker | `RejectedTooLarge` |
| Entity unknown to the gateway | gateway | `RejectedUnknownEntity` |
| Entity not in the sender's `Visible` set | gateway | `RejectedNotInInterest` |
| Sender over `ClientRequestsPerSecond` (burst `ClientRequestBurst`) | gateway | `RejectedClientRateLimit` |
| Entity over `EntityRequestsPerSecond` (burst `EntityRequestBurst`), across all senders | gateway | `RejectedEntityRateLimit` |
| Owner worker not connected | gateway | `RejectedUnreachable` |
| Worker holds no copy of the entity | worker | `RejectedUnknownEntity` |
| Entity moved and the request was forwarded `AuthorityCallMaxHops` times | worker | `RejectedHopLimit` |
| Behaviour index out of range, or the method is not a `[ClientRequestRpc]` | worker | `RejectedNoHandler` |
| Arguments do not decode | worker | `RejectedBadArguments` |
| Handler throws | worker | `HandlerThrew` |

"In interest" means the entity is in the sender's **visible set** (`ClientConn.Visible`): the client holds a replica. Entities held only as far-tier markers are not in it. Entities
lingering after leaving the radius (the exit margin) still are; that is the hysteresis the client itself sees.

The rate limits are token buckets (`RequestBucket`), one in each client's record and one per entity (swept when idle). The order of the checks is deliberate: size, existence and interest first, so a
request that could never be delivered spends no token and creates no bucket for an entity the sender cannot see; then the sender's bucket (spent whatever happens next, so hammering one busy entity is not free); then the entity's.
A rate of 0 turns that limit off.

**The per-entity limit is shared by every sender.** It protects an entity's handler from a crowd; a sender cannot exceed its own limit by spreading over entities, and a crowd cannot exceed an entity's. It also means one client
can use up an entity's burst and starve another; that is the cost of a limit that cannot be bypassed with more accounts. A game that needs per-pair fairness
enforces it in the handler, which has the identity.

Every drop is logged at the gateway with its reason, at most one line a second per client so a flood does not flood the log, and counted (`NebulaGateway.RequestGate.Refused`, `ClientRequestsRouted`).

## D3. Routing and the stamp

The gateway sends a `ClientRequestRouted` to the worker its entity record names as owner. It carries what the gateway knows and the client cannot say:

- `client_id` — the session id, from the connection.
- `identity` — the verified identity (`PlayerIdentity`), as in the session.
- `claims` — the claims `ForwardedClaims` selects from the verified token, as in `SpawnPlayer`. They are stamped on **every** request rather than looked up at the worker, because the worker handling a request often is not the one the player's pawn is on and so does not have them.
- `pawn_net_id`, `pawn_position` — the sender's pawn and where the gateway last saw it, in scope-absolute space. The handler can range-check against the entity's `ScopePositionPrecise` without holding the pawn. The position is the gateway's copy: at most one interest update old.

The client's own message (`ClientRequest`, 54) has no sender field at all, so there is nothing a client could forge and no field the gateway must remember to overwrite. The routed message (55) is accepted by a worker only from a
gateway or worker peer, which authenticate with the mesh token.

A worker that receives a request for an entity it has only a ghost of passes it to the worker it believes has authority (the same `ForwardPeerFor` an `AuthorityRpc` uses: where it handed the entity, else the owner its ghost names),
counting hops and stopping at `AuthorityCallMaxHops`. There is no epoch fence and no duplicate ledger as for `AuthorityRpc`: the request names an entity by net id, which is never reused, the gateway-to-worker link
is reliable, and a client request is a human's action, so a lost one is retried by a person and a duplicate cannot come from a retry because there is none. A request is applied **at most once**.

Ordering: requests from one client to one worker are in order (one reliable link). Two requests from one client that reach different workers (the entity moved between them) can be applied out of order. Requests from different clients are not ordered against each other.

## D4. The handler

```csharp
[ClientRequestRpc]
private void Poke(ClientRequestContext sender, int amount) { ... }
```

`ClientRequestContext` gives `ClientId`, `Identity`, `Claims`, `Entity`, `SenderPawnNetId`, `SenderPosition`, `CallId`, `WantsReply`, `Hops`, `TryGetSenderPawn`, and `ReplyCode`.

**The arguments are untrusted. The sender is not.** Nebula's guarantees: who sent it; the entity was in their interest when the gateway admitted it; the payload is under the cap and within the rate. Everything else is the
handler's: permission, range, cooldown, that a number is in range, that an id refers to something the sender may use. This is stated on the attribute and in the guide. Nebula does not offer a "permission" hook:
what permission means is the game's.

Only `[ClientRequestRpc]` methods can be reached by a request. `RpcRegistry.InvokeClientRequest` refuses any other kind by hash. The old `Invoke` refuses a `[ClientRequestRpc]` (it cannot supply the context), so a
client cannot call it by guessing its hash through `ServerRpc` either.

## D5. Replies

A request sent with `ClientRequestWithReply` carries a client-minted id (`uint`, never 0) and `WantsReply`. The client's `ClientRequestTracker` (the same shape as `AuthorityCallTracker`: mint, track, complete once, expire) settles it exactly once: with the
reply, with `TimedOut` (default 5 s), or with `Disconnected` if the link went down.

The reply (`ClientRequestReply`, 56) is `client_id, call_id, outcome, code`. `Accepted` means the handler ran and returned. It does not say the handler agreed: the handler may set `ClientRequestContext.ReplyCode` (a `ushort` of its own: allowed, too far,
not yours) and that rides back as `ClientRequestResult.Code`. A reply carries no data beyond that on purpose: a game that has something to show sends a `ClientRpc`/`OwnerRpc`, which already handles who sees it.

A worker answers on the gateway link the request came through. A request passed on by another worker arrives from a worker, so the owner answers through the gateway it knows holds the session if it knows one, else through every gateway it is linked to; a gateway that does not hold the client drops it.
The gateway answers a request it dropped itself, directly. A request without `WantsReply` is never answered, whatever happens.

## D6. Protocol 33 and the compatibility policy

`docs/compatibility-policy.md` D1: the window is N and N-1, and every change inside it must be additive (D3). This change adds three message ids (`ClientRequest` 54, `ClientRequestRouted` 55,
`ClientRequestReply` 56) and changes no existing message. A client of 32 never sends 54 and is never sent 56, so it is sent exactly the bytes it was; nothing needs a negotiated-version gate. `ProtocolVersion` is 33 and
`MinProtocolVersion` is 32, so clients of 31 and older are refused with `ProtocolUnsupported`. Gateway-to-worker and worker-to-worker links need protocol 33 exactly, as always.

Per `docs/protocol-versions.md` step 3 a fresh handshake fixture is recorded for the bump: `protocol-33-handshake.json`.

## D7. Configuration

`NebulaConfig`: `ClientRequestMaxBytes` (1024), `ClientRequestsPerSecond` (10), `ClientRequestBurst` (20), `EntityRequestsPerSecond` (40), `EntityRequestBurst` (80). Overrides `-nebula-client-request-bytes`, `-nebula-client-request-rate`, `-nebula-client-request-burst`,
`-nebula-entity-request-rate`, `-nebula-entity-request-burst`; the orchestrator passes its values to a gateway it launches. They are read by the gateway; a worker reads only the size cap and the hop limit.

## D8. What this deliberately does not do

- No ownership or permission model. `OwnerClientId` is available to a handler; the rest is the game's.
- No guaranteed delivery. A request lost with a worker or link is lost, and a `TimedOut` caller does not know if it ran.
- No batching or coalescing. Reliable ordered, one message per call.
- No client-side prediction of the effect. The client sees the result through the entity's ordinary replication.
- `ServerRpc` is unchanged, including that its hash lookup does not check the kind of the method it finds. That was true before this change and is not widened by it; see D4 for why a `[ClientRequestRpc]` is protected from it.

## D9. Tests

| Test | Tier | Proves |
|---|---|---|
| `ClientRequestRuleTests` | A, pure | bucket; admission order and the cost of a refused request; per-client vs shared per-entity limits; sweep; routing decision; tracker settles once; wire forms; window 32..33 |
| `ConformanceClientRequestGatewayTests` | A, real gateway, two fake workers, real clients over UDP | a request for an entity on the *other* worker than the sender's pawn reaches it alone, stamped (client, identity, pawn, position), and the reply reaches the client; unknown, oversize, out-of-interest and rate-limited requests are dropped with their reasons and the client is told; a request follows an entity across a handover; protocol 31 joins, 30 is refused |
| `ConformanceClientRequestWorkerTests` | B, two real `NebulaWorker`s on `ConformanceMesh` | the handler gets the stamp; a request at a ghost is forwarded and applied once at the owner with the reply returning through the gateway; a ServerRpc hash, a bad argument list, oversize, a throwing handler, an unknown entity and the hop limit are each rejected without running a method; the client send path and a legacy sink |

Tiers: the two halves are separate because the repo has no harness that runs a real gateway and real workers together in one process; each half drives the production code on its side with a fake of the other. A true end to end run needs `Tools/smoke-test.ps1` with a player build.
