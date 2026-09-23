# Scope activation — design

Status: design of record for **NEB-233**, protocol **v18** (one appended `Hello` field, no version bump), and for
transfers across scopes (§11, no wire change). Decisions made without asking are marked **D#**. User-facing page:
`website/content/docs/guides/scopes.mdx`. Conformance tests: `Tests/EditMode/ConformanceScopeActivationTests.cs` and
`Services~/Nebula.Services.Tests/ConformanceScopeRoutingTests.cs` (both `[Category("Conformance")]`, scenario 2 of
`docs/conformance-suite.md`).

## 0. Problem

Before this item there was exactly one way to bring a private world into being: a `NebulaWorker` had to call
`PrepareInstance(template, key, origin)` while a player was already standing next to an `InstanceBoundary` in the
public world. That shape rules out everything that decides where a player should go *before* the player is anywhere:

- a matchmaking service that wants the raid instance to exist before it tells four clients to connect;
- a travel menu that sends a player straight to a housing key from the main menu;
- a login flow that must put a returning player back in the scope their last save names;
- a second gateway that must send its player to the *same* world the first gateway's player went to.

`InstanceBoundary` also hard-codes the policy (a trigger volume, a key from the owner's identity) into the mechanism
(make the containers exist). The mechanism is what other things need.

The location contract (`docs/location-contract.md`) already gave a scope a name that every role agrees on: the
opaque `ScopeKey`, carried on `InstanceContainerInfo.ScopeKey`, `Container.ScopeKey`, `NetworkIdentity.ScopeKey`
and `EntityLocation.ScopeKey`. D10 of that document says a new scope must set the key. This item is the call that
makes a scope exist under a key, from anywhere.

## 1. The call

```csharp
controlPlane.ActivateScope(new ScopeActivationRequest {
    ScopeKey   = "raid/molten-core#run-4812",   // opaque; the game chose it
    Definition = definition,                    // what to bring into being
    Requester  = "matchmaker",                  // free-form, for logs
});
```

`IControlPlane.ActivateScope` is on the interface every role already holds: `LocalControlPlane` (single-process runs
and tests), `ControlPlaneHost` (the orchestrator), `RemoteControlPlane` (workers, gateways, and anything else that
speaks the control-plane HTTP API). No worker is involved and nothing needs to be running in the scope.

**D1 Activation is an ordinary control-plane write, and the answer is the mirrored row, not a reply.** Every other
control-plane write (`EnsureRuntimeContainer`, `AssignContainer`, `SetSetting`) is fire and forget: queued, batched,
retried, never dropped, applied in order on the orchestrator's main thread, and observed through the document every
subscriber mirrors. Activation is the same. The caller watches `IControlPlane.Scopes`:

```csharp
var scope = controlPlane.FindScope(key);              // null until the orchestrator applied it
bool ready = controlPlane.IsScopeReady(key);          // §4
```

A request/reply RPC was the alternative (`IGatewaySessionControlPlane` is one). It was rejected because an
activation's result is *durable shared state*, not a per-caller answer: two requesters must see the same row, a
requester that restarts must see it again, and a requester that never asked (a gateway routing a client) must see
it too. A reply would have been a second, weaker copy of something the document already carries.

**D2 An instance is a scope; `PrepareInstance` is `ActivateScope` with a template.** `NebulaWorker.PrepareInstance`
now builds a `ScopeDefinition` from the `InstanceTemplate` and the origin and calls `ActivateScope`, with
`PreferredWorkerId` set to itself. Its public behaviour is unchanged — the same `ContainerRef[]`, the same
`InvalidOperationException` when a key already names different content or bounds, the same "the asking worker owns
the box from the first change anyone sees" — but there is now one code path and one scope row for both entry
points, so an instance a boundary created is visible to `nebula`'s dashboard, to NEB-240's idle sweep and to a
gateway routing by key, exactly like one a matchmaker created.

**D3 A scope's container ids are derived from its key, not allocated.** `ScopeKeys.ContainerId(scopeKey, partId)` is
`"rt_" + ScopeKeys.Hash(scopeKey + "/" + partId)`, which is byte for byte what `PrepareInstance` has always
computed. `ScopeKeys.Hash` is the SHA-256 prefix `NebulaWorker.InstanceKey` used; `InstanceKey` now calls it. Two
processes, two orchestrator runs and two requesters therefore agree on the ids without ever having to talk, which is
what makes the whole thing idempotent rather than coordinated. `ScopeKeys` is pure C# in
`Runtime/ControlPlane/ScopeActivation.cs`, so the gateway, the orchestrator and a test compute the same ids without
Unity.

## 2. What a definition is

```csharp
new ScopeDefinition {
    Kind = ScopeKind.Parts,
    ObservePublic = true,
    ObservationCenter = new Vector3(500, 0, 0), ObservationSize = new Vector3(60, 30, 60),
    Parts = {
        new ScopePart { PartId = "interior", Center = new Vector3(500, 0, 0), Size = new Vector3(20, 10, 20),
                        ContentResource = "Instances/Room" },
    },
}
```

Each part becomes one runtime container: a lease row with the part's box and an `InstanceContainerInfo` carrying the
scope key, the isolation hash, the content resource and the outward view.

**D4 Definition geometry is absolute world coordinates**, the same frame as `LeaseInfo.Bounds` and
`ContainerRegistry.ToAbsolute`. A definition is written by a process that may have no world origin at all (a
matchmaking service, the orchestrator), so a frame-relative box would have no meaning there. `PrepareInstance` does
the `origin + part.Bounds` and `ToAbsolute` arithmetic on the worker, where the frame is known, and hands absolute
boxes down.

**D10 `Kind` and `Payload` are the extension seam.** `Kind` is `"parts"` today and an unknown kind is refused
outright rather than half-activated. `Payload` is an opaque string Nebula stores, compares and carries verbatim and
never parses. Scoped chunk grids (NEB-239) add `Kind = "grid"` and put the grid definition in `Payload`; nothing
else in this file has to change for them.

## 3. Idempotency

Two things make "same key → same scope" true.

**D5 The durable half is a uniqueness constraint on the scope key in storage, not a lock.** `IScopeStore.ClaimScope`
stores the definition under the key *if the key is free* and returns whichever definition is stored under it now —
the caller's, or the one that was already there. Implementations:

| Storage | Constraint |
|---|---|
| `SqlControlPlaneStorage` (SQLite, PostgreSQL) | `nebula_scope (scope_key TEXT PRIMARY KEY, definition TEXT NOT NULL, created_at BIGINT NOT NULL)`, written with `INSERT ... ON CONFLICT (scope_key) DO NOTHING` and read back in the same call. |
| `FileControlPlaneStorage` | one file per key under `<control-plane>.scopes/`, created with `FileMode.CreateNew`: exactly one caller wins the create, everybody else reads what that one wrote. |
| `MemoryControlPlaneStorage` | none — it does not implement `IScopeStore`, and activation is then idempotent only for as long as this orchestrator lives. That is the correct behaviour for a store whose whole contract is "keep nothing". |

No lock is held between the write and the read in either implementation, so the guarantee survives concurrency the
process cannot see (`RacingClaimsOnOneKeyAllSeeTheSameDefinition` runs sixteen threads at one key and asserts they
are all told the same definition). It also survives an orchestrator restart with the document lost:
`ASecondOrchestratorRunAdoptsTheStoredDefinition` starts a fresh `ControlPlaneHost` over the same storage and
watches it adopt the stored scope instead of replacing it.

**The in-process half** is the control plane's single-writer discipline: every activation, wherever it came from —
a direct call, an op in a `POST /api/control-plane` batch, the orchestrator itself — is applied on the
orchestrator's main thread, so the "is there a row for this key" check and the create are one step. Creating the
lease rows is idempotent on its own too: `EnsureRuntimeContainer` is a documented no-op when the row exists, so an
activation that runs twice never bumps an epoch or disturbs an owner.

**D6 A different definition under the same key is refused, and the stored one stands.** Joining two different
worlds under one key would be worse than either failure mode, and `PrepareInstance` has always refused the
equivalent collision. The comparison is on the definition's canonical JSON (`ScopeJson.WriteDefinition`, floats in
round-trip format), so it is exact and order-independent of how the caller built the object. The refusal is logged
on the orchestrator and changes nothing at all — not the row, not the leases, not the control-plane version — so
**the caller's current scope stays authoritative and the caller may retry**, which is the issue's failure
requirement. A caller that cares can compare the definition it gets back with the one it asked for.

**D8 A private copy is the same call with a key the game made unique.** There is no `ActivatePrivateScope`. A
matchmaker that wants a shared raid uses `raid/molten-core`; one that wants one copy per party uses
`raid/molten-core#party-91`. Nebula never parses the key (location contract D2), so the uniqueness is entirely the
game's to arrange, and two different keys are two scopes with disjoint container ids and different isolation hashes.

## 4. What "ready" means, and what it does not

```csharp
bool ready = controlPlane.IsScopeReady(scopeKey);
```

**D7 Ready means: every container of the scope has a lease row, in an owning state, held by a worker that is still
heartbeating.** That is the point at which a client can be spawned into the scope and an entity can be transferred
into it.

Ready does **not** mean the worker has loaded the scope's content. Content loading is the game's path and stays
there (a non-goal of this issue): a worker instantiates `ScopePart.ContentResource` through `InstanceScenes.Prepare`
when a crossing is prepared, and the existing `PrepareTransfer` / `InstancePrepare` handshake is what reports back
whether the destination's content is actually available. So:

| Question | Answered by |
|---|---|
| Does this scope exist? | `FindScope(key) != null` |
| Is there a worker simulating it? | `IsScopeReady(key)` |
| Is the destination container's content loaded on that worker? | `InstanceTransfer.Ready` after `PrepareTransfer` |
| Are this scope's persisted entities restored? | not this item — NEB-240 |

Getting from "rows exist" to "ready" needs no further call: the rows are runtime containers with bounds, so the
orchestrator's ordinary `ComputeRuntimeAssignment` deals the orphaned ones out to workers on its next rebalance. A
requester that is itself a worker skips the wait by passing `PreferredWorkerId`, which creates the rows already
active on it (epoch 1) — this is what `PrepareInstance` does, and why it never had a window where the asking worker
did not own the box.

**Activation failure is the absence of a row.** Nothing is half-created: a malformed definition, an unknown kind, an
empty key or a storage claim that threw all leave `Scopes` and `Leases` exactly as they were, and the caller keeps
whatever scope it is in.

## 5. Routing a client into a scope by key

`HelloMsg` gains one appended field, `string scope_key`, written after `incarnation`:

```text
Hello := u16 protocol, u8 role, string id, u32 index, u8 flags, string token, string session, u32 incarnation,
         string scope_key
```

**D9 The wire change is one appended string and the protocol version is not bumped again** (18 is already unreleased
and already breaking). `HelloMsg.Read` tolerates a message that ends before the field and reads it as `""`, which is
the public world — the same thing every client meant before the field existed.

On the client it is `NebulaClient.ScopeKey`, seeded from `-nebula-scope`, set before connecting. On the gateway it
is `ClientConn.ScopeKey`, and it changes exactly one thing: `CollectSpawnCandidates` now only offers containers
whose `Container.ScopeKey` is ordinally equal to the client's. **The check fails closed in both directions** — a
client that named a scope is never placed in the public world as a fallback, and a public client is never placed
inside somebody's instance. A client whose scope has no container with a live owner is held in
`JoinState.Starting`, exactly as a client that arrives before the first worker has booted is held today, and is
placed with no reconnect as soon as the scope becomes ready.

**The gateway does not activate the scope.** Whoever sent the player to the key is the one that called
`ActivateScope`; a gateway that activated scopes on demand would be admission policy, which is a non-goal. The
second way in is unchanged: an entity already in the mesh crosses with the epoch-fenced `PrepareTransfer` /
`TryCommitTransfer` pair.

## 6. The scope row

| Field | Meaning |
|---|---|
| `ScopeKey` | the opaque key; never empty (the public world has no row) |
| `InstanceId` | `ScopeKeys.Hash(ScopeKey)`; the same value entities carry in `NetworkIdentity.InstanceId` |
| `State` | `ScopeState.Active`. NEB-240 adds retirement states here |
| `ContainerIds` | the scope's container ids, in definition order |
| `Definition` | the definition the first activation won with |
| `CreatedAt`, `UpdatedAt` | when the row was created and last activated |
| `Requester` | free-form caller name, for logs and the dashboard |

The rows travel in the control-plane document under `"scopes"`, and therefore to every worker, gateway and
orchestrator through the ordinary long poll. The key is written only when there is at least one scope, so a mesh
that activates nothing produces the document it always did, and a document from before this item parses to an empty
list. `InstanceId` is written as a **decimal string**: the document's parser turns every JSON number into a
`double`, which would round a 64-bit hash off by a few hundred. A row that lost it re-derives it from the key.

The lease rows themselves are unchanged from what `PrepareInstance` has always written, including the
`InstanceContainerInfo` blob, so nothing downstream — isolation checks, the gateway's instance filtering, the
location contract — sees anything new.

## 7. Reaching it from outside the mesh

The op is an ordinary control-plane write, so the orchestrator's existing `POST /api/control-plane` endpoint is the
whole HTTP surface. No new endpoint, no new authentication path:

```bash
curl -X POST http://orchestrator:7080/api/control-plane \
  -H 'X-Nebula-Token: <mesh token>' -H 'Content-Type: application/json' \
  -d '{"ops":[{"op":"ActivateScope","scopeKey":"raid/molten-core#run-4812","workerId":"","requester":"matchmaker",
       "definition":"{\"kind\":\"parts\",\"parts\":[{\"partId\":\"interior\",\"center\":[500,0,0],\"size\":[20,10,20],\"content\":\"Instances/Room\"}],\"observePublic\":false,\"observationCenter\":[0,0,0],\"observationSize\":[0,0,0],\"payload\":\"\"}"}]}'
```

The definition travels as a JSON document inside a JSON string, for the same reason `InstanceContainerInfo` travels
as base64 on the lease row: `ControlPlaneJson.OpWriter` writes scalar arguments, and a string keeps one encoding
between the op, the document and the storage claim.

**No `nebula` CLI command was added.** The CLI wraps the orchestrator's read-only dashboard (`GET /api/state`) and
the Nebula Cloud API; it has no control-plane write path to extend, and the issue's guidance was to add a command
only if one already existed. `nebula status` will show scopes when the dashboard reports them; that, and a
`nebula scope` command, are a follow-up worth doing once something in the CLI needs to write to the control plane.

## 8. Retiring a scope

`IControlPlane.RemoveScope(scopeKey)` drops the scope row, releases the durable claim and removes the lease rows of
its containers.

**D11 This is the mechanism only.** It does not decide when a scope should go, does not wait for the scope to be
empty, does not move entities out and does not save anything. Deciding that a scope is idle, draining it, restoring
it and refusing admission while the restore runs is scope lifecycle (NEB-240), which builds on these rows: every row
carries `UpdatedAt`, and `ContainerIds` is what an idle sweep aggregates lease activity over.

## 9. Seams left for the issues that follow

- **Scoped chunk grids (NEB-239)** extend `ScopeDefinition` through `Kind` + `Payload` (D10) and derive their
  containers with `ScopeKeys.ContainerId(scopeKey, partId)` (D3), where `partId` is the chunk coordinate. Because
  the ids are a hash of *key and part*, two scopes with overlapping chunk coordinates already get disjoint
  container ids and disjoint lease rows — that scenario's guarantee falls out of D3 rather than needing new
  machinery. What NEB-239 must add is a definition that does not enumerate every chunk up front.
- **Scope lifecycle (NEB-240)** aggregates over `IControlPlane.Scopes`: `UpdatedAt` for idle age, `ContainerIds`
  for the leases and their occupants, `State` for the states it adds beside `ScopeState.Active`, and `RemoveScope`
  for the last step. Admission refusal while a restore runs belongs on the gateway's existing hold
  (`JoinState.Starting`, §5), which already exists and already holds rather than drops.

## 10. Non-goals (from the issue, kept)

Deciding which key a player should go to, admission policy, matchmaking and loading-screen UI are the game's.
Content preparation stays the game's `ChunkContent` / instance content path (§4). Nebula still never parses a scope
key (location contract D2).

## 11. Transfers across scopes

Status: added after alpha.30 for a crewed ship flying out of one grid scope (a planet, `world/planet`, planar) into
another (space, `world/space`, volumetric). No wire message, field or protocol version changed (**D20**). Conformance:
scenario 16 of `docs/conformance-suite.md` — `Services~/Nebula.Services.Tests/ConformanceCrossScopeCarrierTests.cs`
(tier A: the gateway and the client) and `Tests/EditMode/ConformanceCrossScopeCrewTests.cs` (tier B: the worker).

### 11.1 What went wrong

§5 says the second way into a scope is "an entity already in the mesh crosses with `PrepareTransfer` /
`TryCommitTransfer`". Between two *grid* scopes that path had four holes:

1. **A group of a ship and its crew never became ready.** A client-owned pawn's preparation is answered by its
   client, which says ready only when it can resolve the destination. A client can resolve only the container rows
   its gateway sent it, and a client in scope A is never sent a chunk of scope B (`docs/scoped-chunk-grids.md`
   D10). So the answer was always "unavailable".
2. **A ship that crossed on its own stranded its crew's clients.** A client's scope is its pawn's, and a carried
   pawn's scope is its outermost carrier's (`NebulaGateway.ScopeContainer`). The gateway learns that a carrier moved
   only from the carrier's own updates — which, once it is in B, are published under B-salted region keys
   (`docs/scope-frames.md` D7) that nobody subscribed for the crew, because their salt still came from the ship's
   old record. The worker told the gateway to forget the ship, the pawns' scope resolved to nothing, and nothing
   ever put it right: not a reconnect, not a fresh session.
3. **A fast ship was lost inside one scope.** A worker leases the next chunk and flies into it before that lease
   reaches the gateway's control-plane mirror. The gateway could not resolve the container the update named, failed
   closed (an unknown runtime container is never observable), and took the ship — and, through it, everyone aboard —
   away from every client that held it.
4. **A group commit pulled the crew out of the ship.** `TryCommitTransfers` put every member into the destination
   container, so riders prepared with their ship arrived standing in the chunk, not in their seats.

### 11.2 Decisions

**D12 A client follows its pawn's carriers by name.** On every subscription pass the gateway names each carrier in
its clients' pawns' carrier chains in `InterestSubscribe.Entities`, the explicit subscription the protocol already
has. An explicitly named entity is sticky on its worker for that gateway (it is in `StickyMask`), so it is never
forgotten on a rebucket and is published to that gateway wherever it goes. The crossing therefore arrives, the
pawn's scope resolves to B, and the client's salt, window and subscriptions follow. Two supporting rules: the
eviction sweep never evicts an explicitly named record (its region being unsubscribed says nothing about whether it
is wanted), and the carrier's worker is linked with the `Owned` reason. A carrier the gateway has no record of at all
— a reconnect after the crossing — is named too, and an explicit id nobody has answered for links every live worker
until its owner announces it, which is how a reconnecting rider finds its ship in space.

The alternative was to make crossing move the riders by construction: refuse a carrier transfer whose riders are
not in the group, or have the worker drag them in. It was rejected because it fixes only the path through
`TryCommitTransfers`. A carrier changes scope in other ways too — a handover into another worker's chunk, a game
that moves a ship itself — and a client that lost its ship's record for any reason (a reconnect, an eviction) would
still be stuck. Following by name repairs the client whatever moved the ship, with a mechanism the worker already
implements, and costs one id per carrier in a subscription message.

**D13 An entity that changes scope is revoked before anything about the destination is sent.** When a state entry
or a spawn moves an entity into another scope, every client that holds it is re-authorized on the spot
(`RevalidateInterest`). An onlooker on the planet loses the ship and everyone aboard before the ship's next message
is relayed and before the destination container's row goes to anyone — so it is never told which chunk of space
the ship went to. The crew, whose own scope is resolved through the ship, stay authorized and lose nothing.

**D14 A preparation brings its destination row with it.** When the gateway relays `InstancePrepare` to a pawn's
client, it first sends that client the destination container's row on the same reliable stream, and pins it in the
client's needed set for `NebulaGateway.PreparedRowSeconds` (30 s) so the window cannot withdraw it before the
commit names it. Only that client is told, and only because the worker simulating its own pawn asked; a
preparation still grants no visibility of the destination's entities, because those are authorized by the pawn's
scope, which has not changed yet. If the gateway has no row for the destination yet (or one older than the lease
epoch the worker named), the preparation waits for it, for up to 15 s.

The alternative — putting the destination's row inside `InstancePreparationMsg` — was a wire change for something
the ordered reliable stream already guarantees, and would have given the client a row outside the delta the gateway
tracks per client (`KnownContainers`), so the next window pass would not have known to withdraw it.

**D15 An update naming a container the gateway cannot describe waits for the row.** A spawn or a state entry that
names a runtime container missing from the gateway's registry is held, per entity and in arrival order, until the
control plane delivers the lease; everything after it for that entity waits behind it. Held updates are applied
through the normal path when the row arrives, and relayed on the reliable stream behind the row. The hold is bounded
(`NebulaGateway.HoldSeconds`, 10 s, and 1024 updates); past the bound the updates are applied anyway and fail closed
exactly as before. Separately, any state entry that moved an entity into another container is now relayed on the
reliable stream, behind the row `SendOwnershipForContainerChange` has just queued there, instead of on the sequenced
channel where it could overtake it. Together: a client is never sent an entity update naming a container before
that container's row.

What arrives behind a held update waits with it (NEB-257). Variables, behavior state, RPCs and a despawn for an entity
with held updates are held too, in arrival order, and applied through the normal path right after what they followed.
Before, a new owner's messages that arrived while its spawn was held were dropped by the ordinary owner check (the
record still named the previous owner), or, after a redirect had already named the new owner, relayed *ahead* of the
spawn, whose older variables then overwrote them on every client. Either way a carrier arrived with stale variables
(a speed cap, a gear state) until the next change. Two refinements: a message of an epoch older than everything held
(the previous owner's last words) is not held, and goes through or is dropped exactly as before; and a released
behavior-state message is relayed on the reliable stream, so a sequenced copy cannot overtake the spawn it waited
behind. A state entry for an entity the gateway knows only as a held spawn waits behind it too, instead of being
dropped as unknown. The bound is shared: every held message counts toward the 1024 updates per entity, and the 10 s
applies to them all. Owner state (`OwnerStateMsg`) is not held: it is sequenced, and the next tick supersedes it.

**D16 A client whose carrier chain cannot be resolved stays in the scope it was last in.** `ClientConn.LastScope`
records the client's scope whenever its pawn's chain resolves, starting from its `Hello`'s scope key. While the chain
is broken (a carrier record on its way), authorization, region salting, the container window and the policy
snapshot all use it, instead of treating the client as standing in the public world — which would have widened what
it may see and subscribed it to the wrong world.

**D17 A container row that leaves a client's window lingers for a second.** A row whose lease still exists is
withdrawn only after it has been out of the client's window for `NebulaGateway.ContainerRowLingerSeconds` (1 s). A
client despawns whatever stands in a row it is told to drop, and a replica that has just moved out of a box — a
fast ship, or a whole crew whose scope just changed — is still gliding out of it through its interpolation buffer
in that box's frame. A row whose lease is gone is withdrawn at once, as before.

**D18 The client's instance view follows its pawn's carriers.** `InstanceScenes.SetView` was called only when the
local pawn changed container. A seated rider changes scope without changing container — its ship moved — so
`NebulaClient` now compares the pawn's `InstanceId` (which follows the carrier chain) every frame and switches the
view when it changes.

**D19 A group commit keeps riders in their seats.** A member of a `TryCommitTransfers` group that is carried, at any
depth, by another member of the same group now stays in its carrier: carriers are committed first, and a seated
member is committed in place — new epoch, preparation finished, container and pose unchanged. Its preparation is
still what readied its owner's client for the destination. A rider committed without its ship is put down in the
destination as before.

**D20 No wire change.** Every rule above is a gateway, client or worker decision over messages that already existed:
`InterestSubscribe.Entities`, `ContainerOwnership` deltas, `WorldState` on the reliable stream. Protocol stays 18.

### 11.3 What a game does

Nothing new is required. A game may prepare every rider along with the ship — that readies each rider's client for
the destination before the commit — and commit them as one group; or it may move the ship alone and let the riders'
clients follow it. Both are tested.
