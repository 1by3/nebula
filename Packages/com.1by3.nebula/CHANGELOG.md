# Changelog

All notable changes to this package are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed

- **Two carriers with overlapping interiors could end up inside each other and kill the worker.** The worker placed each entity in the smallest box that held its origin, leaving out only the entity's own box. When two ships' interiors overlapped, the first ship was placed in the second ship's box, and then the second ship was placed in the first ship's box. The next `Container.InstanceId` or `ScopeKey` read then recursed until the process threw a `StackOverflowException`. In a live mesh this happened on every tick: the worker missed its heartbeats and was declared dead. Twenty ships spawned into one 64 m chunk were enough to cause it. Now the rule is that an entity is never placed in a container it carries at any depth: its own box, or the box of a carrier riding inside it.
  - `ContainerRegistry.Find` and `ContainerRegistry.Resolve` take the entity being placed (`subject`) and skip every container it carries. They then choose the next smallest box that holds the point. Overlapping ships nest one inside the other at most. `Resolve`'s last parameter changed from `Container exclude` to `NetworkIdentity subject`.
  - `NetworkIdentity` refuses a placement that would make an entity ride inside itself, at any chain length (previously only its own box was refused). It logs a warning once per entity. This covers a placement that arrives from another worker that decided differently.
  - New `Container.IsCarriedBy(NetworkIdentity)`: whether an entity carries a container, directly or through a chain of carriers.
  - Every walk up a carrier chain is now bounded: `InstanceId`, `ScopeKey`, the moved-frame check behind `WorldBounds`, and `NestingDepth`. A corrupt chain reads as the public scope instead of overflowing the stack or looping forever. On the next tick, the worker moves an entity out of a container that it carries.
  - Conformance scenario 15, `ConformanceCarrierCycleTests` (tier B), covers the fix. `ConformanceMesh.Worker.Tick` runs one whole worker tick.
- **The interest probe threw every sample in a world with scoped grids.** `InterestProbe` looked up each replica's chunk in the public grid. A scope's positions are expressed in that scope's frame, which can sit far outside the public grid's packed id range, so `RuntimeGrid.PackId` threw `ArgumentOutOfRangeException` from `Update`. The probe now judges each replica, and the pawn's `cell=`, in the replica's own scope's grid. `NebulaChunks.At` (and `IsLoadedAt`) return null for a position the public grid cannot name, as documented, instead of throwing.
- **`-key=value` on the command line was read as a switch named `key=value`.** `CommandLine` now accepts both `-key value` and `-key=value`. The second spelling is the only way to pass a value that starts with a dash (`-heading=-30`). New `CommandLine.ParseArgs` parses an argument vector the same way.
- **A worker's first telemetry beat could mark its containers at capacity and refuse joins.** The cost meter closed a window into a reading however few ticks it held. A worker that had just started could report one tick, the one that spawned everything it had been handed, as the container's cost per tick: 144 % of the budget in one sample. The orchestrator published the container as at capacity and the gateway refused joins with `AtCapacity` until the next beat. A window now needs `ContainerCostMeter.MinTicksPerSample` (10) ticks to become a reading; a shorter one carries into the next beat (docs/cost-telemetry.md D13).

### Crewed carriers crossing between scopes

A ship with players seated in its dynamic container can now fly from one scope into a chunk of another — from a planet's grid scope into a space grid scope — with its crew. No wire change; the protocol stays 18. Design record: `docs/scope-activation.md` §11 (D12–D20). User-facing page: [Move a crewed carrier into another scope](https://nebula.1by3.co/docs/guides/scopes#move-a-crewed-carrier-into-another-scope). Conformance: scenario 16 (`ConformanceCrossScopeCarrierTests`, `ConformanceCrossScopeCrewTests`).

- **A crossing into another scope becomes ready.** The gateway sends a pawn's client the destination container's row ahead of `InstancePrepare` and keeps it pinned for 30 s, so a client in one grid scope can prepare a chunk of another. Before, the client could not resolve the destination and the group never became ready.
- **Riders stay seated.** `TryCommitTransfers` keeps a member that rides in another member's dynamic container in its seat instead of placing it in the destination container.
- **A carrier crossing on its own takes its riders' clients with it.** The gateway follows every carrier its players ride in by network ID (an explicit interest subscription), so a ship that moves into another scope keeps reaching its crew's gateway; the riders' scope, window and subscriptions follow it with no reconnect, and a rider reconnecting after the crossing finds the ship. Explicitly subscribed records are no longer evicted when their region is unsubscribed. A client whose carrier chain is briefly unresolvable stays in the scope it was last in instead of falling back to the public world.
- **Onlookers are revoked first.** An entity that changes scope is re-authorized for every client holding it before anything about the destination is sent, so a client left behind is never told the destination's container.
- **No update names a container before its row.** Updates naming a runtime container whose lease has not reached the gateway yet are held until it arrives (10 s at most, then applied fail-closed as before); a state entry that changes an entity's container is relayed on the reliable stream behind the new row; rows that leave a client's window linger 1 s before they are withdrawn, so a replica interpolating out of a box is not despawned. This also fixes a fast carrier disappearing inside one scope.
- **Client.** `NebulaClient` switches the instance content view when the local pawn's scope changes through its carrier.

## [0.1.0-alpha.30] - 2026-09-22

### NEB-228: deployment and protocol compatibility policy

Nebula now states which versions may talk to each other, and enforces it with a refusal a client can act on instead of a silent disconnect. Design record: `docs/compatibility-policy.md`. Version table: `docs/protocol-versions.md`. User-facing page: [Upgrade a running mesh](https://nebula.1by3.co/docs/deploy/upgrades).

**The window.** A gateway accepts a client whose protocol is in `HelloMsg.MinProtocolVersion`..`HelloMsg.ProtocolVersion` — N-1 and N. Both are `18` in this release: 18 is the floor the policy starts from, so no older client is admitted, and the window widens at the next protocol change. Gateway-to-worker and worker-to-worker links still require an exact match, and now refuse with a logged reason on both sides instead of closing the link silently. Inside the window every protocol change must be additive; the gateway records the negotiated version on the session and encodes that client's traffic at it.

**Wire (protocol 18, appended after the rest of 18 was settled; the version is unchanged):**

- `HelloMsg` now writes the **sender's own** `Version` rather than always writing the `ProtocolVersion` constant, so a peer can announce a version that is not its build's. It also gained a trailing `u32 game_content_version`; a `Hello` that ends before it reads as `0`.
- `Welcome` gained a trailing `u16 negotiated_version`: the protocol the gateway settled on for the session. A `Welcome` that ends before it reads as `0`, meaning the version the client sent.
- `JoinRejected` gained trailing `u16 supported_min_version`, `u16 supported_max_version` and `u32 server_content_version`, sent on **every** refusal, and two `code` values: `3` `ProtocolUnsupported` and `4` `ContentVersionMismatch`. A client below the minimum must update; one above the maximum has reached a server that has not been upgraded yet.

**The game's own content version.** `NebulaConfig.GameContentVersion` (with `MinGameContentVersion`, and `-nebula-content-version` / `-nebula-min-content-version`) is a number your game chooses. The client announces it and the gateway refuses a mismatch with its own reason code — exact match by default, a range when a minimum is set, and no check at all while it is `0`. Nebula only compares the numbers.

**Client API.** `NebulaClient` gained `ServerProtocolWindow`, `ServerContentVersion` and `NegotiatedProtocolVersion`, and does not reconnect by itself after either build mismatch.

**Rolling upgrades** are a documented procedure, not a new command: drain a gateway through its control-plane row and replace it while its clients move to another one with their sessions intact; hand a worker's entities and leases to its neighbours before replacing it, so it leaves no orphaned container. Both are now tested in `RollingUpgradeTests` (scale scenarios S9a and S9b), the compatibility window at its four edges in scenario S9, and a **recorded** client stream replayed against a gateway of this build in `ConformanceProtocolCompatibilityTests`, from the checked-in fixture `Services~/Nebula.Services.Tests/Fixtures/protocol-18-handshake.json`.
### NEB-227: control-plane and entity-store availability

No wire change; the protocol stays 18. Design record: `docs/control-plane-availability.md`. User-facing page:
[Restart and restore a mesh](https://nebula.1by3.co/docs/deploy/availability).

**Fixed: a worker never noticed a control plane that came back empty.** `NebulaWorker` registered once at startup
and never again, so an orchestrator restarted without its document — a reset, a restore from an older backup, a
failover to a replica that never had it — left every worker simulating containers the mesh had no route to, until
each worker process was restarted by hand. A worker now registers again when it sees a document it is not in
(the rule `NebulaGateway` already had) and, on every control-plane change, re-claims the lease rows of the
containers it is still simulating, because nothing else in the mesh knows it is simulating them. Measured: a
replacement orchestrator on an empty database converges in 2.6 s with every container owned again by the worker
that holds its entities.

- **New public class `WorkerRegistration`** (`Runtime/Worker/WorkerRegistration.cs`): `Register`,
  `RegisterAgainIfForgotten`, `ReclaimContainers`, `Forget`. Pure C#, compiled into the standalone services as
  well, so the same decision code runs on a Unity worker and in the tests. Game code does not need to call it;
  `NebulaWorker` owns one.
- **New `IControlPlane.DocumentId`** (and a `document` field in the control-plane JSON document): the identity of the
  document, new when the orchestrator starts one from nothing and kept across a restart that restored it. The
  worker's reclaim is gated on it: a lease missing from a document the worker already reconciled with was removed on
  purpose (a retiring scope) and is left alone; a lease missing from a new document was lost and is put back.
- **New public property `ControlPlaneHost.StorageError`**: why the last save to the control-plane store failed,
  or null. Not a mesh failure by itself — the control plane keeps running in memory and the save is retried —
  but it is the window an operator watches across a database failover.

**New: a scripted backup-and-restore drill.** `Tools/restore-drill.ps1` seeds a database with known records,
snapshots it, backs it up, wipes it, proves the wipe emptied it, restores it and proves the restored database is
the one that was backed up, leaving `Logs/restore-drill/<timestamp>.json` and a summary. It needs no player
build, no Unity and no running mesh, and drills a throwaway SQLite database unless you name another. The
verification runs through `SqlPersistenceStore` and `SqlControlPlaneStorage` rather than hand-written SQL, so a
pass means the mesh can read what came back. Backup routes: SQLite `VACUUM INTO`, `pg_dump`/`pg_restore`, or an
engine-independent JSON export/import through the store. New project `Services~/Nebula.RestoreDrill`.

**New scale scenarios** in `Services~/Nebula.Services.Tests/ScaleAvailabilityTests.cs` (`docs/scale-suite.md`
S6b/S6c): an orchestrator **process** restart against a real `ControlPlaneHost`, `OrchestratorHttpServer` and
SQLite store with `RemoteControlPlane` mirrors attached (2.59 s, every lease identical, no mirror disconnected);
the same with an empty database; and a PostgreSQL failover under Docker, which skips with a reason when no
Docker daemon is there. New thresholds `ScaleThresholds.OrchestratorRestartSeconds` (15 s, derived from
`RemoteControlPlane.DisconnectAfterSeconds`) and `DatabaseFailoverSeconds` (60 s, provisional). The pinned gap
`docs/scale-suite.md` D7b is closed and its assertion turned around.


### Breaking: protocol 17 → 18, the scoped worlds and interaction contracts project

Every Nebula process must be rebuilt and restarted together. A gateway disconnects a client whose protocol version is not exactly `18`; there is no negotiation between 17 and 18. This release settles the first milestone of the scoped-worlds project: the cross-worker call contract, the entity location contract, the distributed-physics model, and the conformance suite that pins them.

#### Cross-worker call contract

See [RPCs and worker messages](https://nebula.1by3.co/docs/guides/rpcs#what-nebula-promises-for-a-cross-worker-call) and the [wire protocol specification](https://nebula.1by3.co/docs/specifications/wire-protocol#authority-calls); the design record is `docs/cross-worker-calls.md`.

**What changed and why.** An `AuthorityRpc` sent to a ghost's owner was a bare RPC on the worker link: applied if the receiver had authority, forwarded once if it had just handed the entity off, otherwise dropped in silence. Nothing identified a call, so nothing could tell a repeat from a first arrival; the epoch on the message was never read; a forward could loop; the sender never learned the outcome. There is now a stated and tested contract: every call carries a sender-minted id, the epoch the sender saw and a hop count; the receiving worker applies it once, forwards it after a handover (bounded), or rejects it with a reason; and a caller may ask for that outcome.

**Breaking wire format (protocol 18):**

- `AuthorityRpc` (46) now carries `AuthorityCallMsg` (`u64 call_id, u8 hops, u8 flags, u64 net_id, u32 entity_epoch, u8 behavior_index, u32 method_hash, bytes args`) instead of `EntityRpcBody`. `EntityRpc` (13) and `ServerRpc` (21) are unchanged.
- New message `AuthorityRpcReply` (49): `u64 call_id, u8 outcome, u32 entity_epoch, u8 hops`, sent to the worker that minted the call id when the call asked for a reply.
- A worker now ignores an `AuthorityRpc` from a peer that is not a worker.

**Behaviour that changed:**

- A cross-worker `AuthorityRpc` is applied **at most once** per call id: each worker keeps the ids it has applied for 4096 calls or 600 ticks (10 s), and rejects a repeat as `RejectedDuplicate`.
- A call whose epoch is ahead of the receiver's copy, or more than `AuthorityCallMaxHops` handovers behind it, is rejected as `RejectedStaleEpoch` instead of being applied (design D3: the window is the hop bound, because a call that legitimately chased its target through forwarding is never further behind than that).
- Forwarding after a handover is bounded to `AuthorityCallMaxHops` forwards (default 3) and ends in `RejectedHopLimit`; before, it was once, then silence. A worker holding a ghost now also forwards to the owner its ghost names, not only to a worker it handed the entity to itself, and never back to the peer the call came from.
- Every rejection is logged as a warning on the worker that decided it, with the reason, the call id, the epochs and the hop count.

**New `NebulaConfig` field:** `AuthorityCallMaxHops` (3), with the `-nebula-authority-call-hops` command-line override, mirrored into the services config.

**New public API:**

- `NetworkBehaviour.AuthorityRpcWithReply(method, args…, onDone, timeoutSeconds = 5)` (0–4 arguments) — as `AuthorityRpc`, and reports the outcome to `onDone` exactly once, on the worker's main thread: `Accepted`, `RejectedStaleEpoch`, `RejectedUnknownEntity`, `RejectedHopLimit`, `RejectedDuplicate`, `RejectedUnreachable` or `TimedOut`. Returns the call id (0 when applied locally or refused before sending). `NetworkBehaviour.DefaultAuthorityCallTimeoutSeconds`.
- `AuthorityCallOutcome`, `AuthorityCallResult` (`CallId`, `Outcome`, `TargetEpoch`, `Hops`, `Succeeded`).
- `AuthorityCallId`, `AuthorityCallLedger`, `AuthorityCallRouter`, `AuthorityCallTracker`, `AuthorityCallTarget`, `AuthorityCallDecision`, `AuthorityCallAction` (`Runtime/Worker/AuthorityCallContract.cs`) — the pure C# rules the worker runs, compiled into `Services~` too and covered by `ConformanceCallContractTests` (`[Category("Conformance")]`).
- `AuthorityCallMsg`, `AuthorityCallReplyMsg`, `AuthorityCallFlags`, `MsgId.AuthorityRpcReply`.
- `IRpcSink.SendAuthorityRpc(identity, behaviourIndex, methodHash, args, onDone, timeoutSeconds)` — the reply-requesting overload. A custom `IRpcSink` must implement it.
- `NebulaWorker.AuthorityCallsApplied`, `AuthorityCallsForwarded`, `AuthorityCallsRejected`, `AuthorityCallsPending`.

**Unchanged:** the fire-and-forget `AuthorityRpc` overloads keep their signatures and are governed by the same rules. Worker messages (`SendToWorker`) remain fire-and-forget: one delivery on the chosen channel, no call id, no forwarding, no reply; the guide now says so.

**Migration notes:**

- Rebuild and restart every worker, gateway, orchestrator, and client build together.
- A handler that relied on a cross-worker call arriving after several handovers should expect `RejectedHopLimit` past three; raise `AuthorityCallMaxHops` if your world hands entities over that often, or use `AuthorityRpcWithReply` and retry from the caller.
- A custom `IRpcSink` implementation must add the new `SendAuthorityRpc` overload.

#### Entity location contract

See the [entity location contract](https://nebula.1by3.co/docs/specifications/entity-location) and `docs/location-contract.md`.

**What changed and why.** There was no single, protocol-visible way to say where an entity durably is that did not depend on which worker held it: the wire named a container by a dense index or a carrier's net id, the instance an entity was in was known only as a 64-bit hash, and a persisted record carried no scope at all. `EntityLocation` is now that one answer: an opaque scope key, the container's string id and the container-local pose, the same value on the worker, gateway, orchestrator and client and in the store, unchanged by handover, worker restart, mesh restart and a store round trip. Nebula never parses the scope key.

**Breaking wire format (protocol 18):**

- `InstanceContainerInfo` (carried in `ContainerOwnership` entries and stored Base64-encoded on a runtime container's lease row) gained a trailing `string scope_key`: the instance key the game chose (`TemplateId/key`), beside the hash that was already there. A lease row stored by an earlier release has no key and reads as empty; the instance keeps working and reports an empty scope key until it is retired and prepared again.

**New public API:**

- `EntityLocation` (`Runtime/Containers/EntityLocation.cs`, pure C#, also in the standalone services) — the triple, with exact equality, `ToString`, `Write`/`Read`, `ContainerKind`, `IsPublic`, `HasContainer`, `Of(container, pose)` and `Resolve()`, which finds the container by id in this process's `ContainerRegistry` and refuses one in another scope. `LocationContainerKind` classifies a container id by its form (`None`, `Static`, `Runtime`, `Dynamic`).
- `NetworkIdentity.Location` and `NetworkIdentity.ScopeKey`; `Container.ScopeKey` (following the carrier for a dynamic container); `InstanceContainerInfo.ScopeKey`.
- `PersistedEntityRecord.ScopeKey` and `PersistedEntityRecord.Location` (get and set). `NebulaPersistence.BuildRecord` fills the scope key. The JSON body gained `"scopeKey"`, the local store file is now version 2 (version 1 files still load, as the public world), and the SQL store gained a `scope_key` column added on first open.
- `NebulaWorker.PrepareInstance` records the scope key on the lease it ensures and refuses a key that collides with a different non-empty key under the same hash.

**Conformance tests:** `Tests/EditMode/ConformanceLocationTests.cs` (`[Category("Conformance")]`) runs against the Unity registry in the package and against the service registry in `Services~/Nebula.Services.Tests`: the same triple resolves the same container in two independently populated registries, survives a handover and a store round trip, pins the wire bytes of a fixed triple, and reads older rows and records as the public world.

**Migration notes:**

- Rebuild and restart every worker, gateway, orchestrator and client build together.
- Persisted records and lease rows from earlier releases are read as the public world (`ScopeKey == ""`). Nothing has to be reset; retire and re-prepare an instance if its entities should report their scope key.
- A dynamic container's id (`label#netId`) is reported honestly as living for one mesh run; persistence continues to name a carried entity's place by `CarrierKey`, not by that id.

#### Distributed physics model and diagnostics

Concepts page and developer-time checks (`docs` design: the [Distributed physics](https://nebula.1by3.co/docs/concepts/distributed-physics) page). Added:

- [Distributed physics](https://nebula.1by3.co/docs/concepts/distributed-physics) concepts page: what a physics interaction is on one worker, across a seam through a kinematic ghost one tick behind, and why a joint or `ArticulationBody` between entities in different containers is unsupported.
- `PhysicsIslands`: the runtime rule for whether two networked bodies share one authority (`SameIsland`, `ContainerOf`, `FindCrossIslandJoints`) with an `IsCohesive` hook reserved for cohesion hints (NEB-223). Until those exist every cross-container joint is reported.
- **Nebula > Validate Project** warns about every `Joint` or child `ArticulationBody` in the open scenes and the network prefabs whose bodies belong to entities in different containers, naming both containers with the joint as the log context (`NebulaValidator.CheckPhysicsIslands`).
- A worker logs one warning per entity when it spawns or gains authority over an entity with such a joint (`PhysicsIslands.CheckOnAuthority`; set `PhysicsIslands.WarnOnAuthority` to false to turn it off). Entities without a joint cost one component lookup per spawn.
- `NebulaDiagnostics.RejectedAuthorityRpcSends` and `NebulaWorker.RejectedAuthorityRpcSends` count `AuthorityRpc` sends discarded because the caller held neither an authoritative nor a ghost copy; the worker's `profile` log line reports it as `rpcRejected`.

Changed:

- The warning for an `AuthorityRpc` sent from a copy that is neither authoritative nor a ghost now states the rule and the reason, and is checked before the RPC sink so it also fires in a process without one. Routing is unchanged.

#### Keyed simulation scopes activated on demand

See [Simulation scopes](https://nebula.1by3.co/docs/guides/scopes) and `docs/scope-activation.md`.

**What changed and why.** The only way to bring a private world into being was `NebulaWorker.PrepareInstance`, which needs an authored `InstanceBoundary` and a worker that is already running to ask. Matchmaking services, travel menus and login flows all decide where a player goes *before* the player is anywhere. `IControlPlane.ActivateScope(key, definition)` is that call: ask the mesh for a shared simulation scope by key, from any process that holds a control plane, and get the same containers for the same key however many requesters ask and whichever orchestrator run they ask in.

**Breaking wire format (protocol 18, appended after the rest of 18 was settled; the version is unchanged):**

- `HelloMsg` gained a trailing `string scope_key`. A `Hello` that ends before it reads as the public world, so a client built against an earlier snapshot of protocol 18 still joins. It is ignored on a gateway or worker `Hello`.

**Control-plane document and storage:**

- The control-plane document gained a `"scopes"` array, written only when at least one scope has been activated. A document without it parses to an empty list, so a stored document from an earlier release reads as before.
- New control-plane ops `ActivateScope` and `RemoveScope` on `POST /api/control-plane`, which is the whole HTTP surface for activation — there is no new endpoint.
- New table `nebula_scope (scope_key TEXT PRIMARY KEY, definition TEXT NOT NULL, created_at BIGINT NOT NULL)` in the orchestrator's SQLite/PostgreSQL database, created by `EnsureSchema`. A file-backed control plane keeps the same claims under `<control-plane>.scopes/`. This primary key is the uniqueness constraint that makes activation idempotent across concurrent requesters and across orchestrator runs; a memory-only control plane has no such constraint and is idempotent only while it lives.

**New public API:**

- `IControlPlane.Scopes`, `IControlPlane.ActivateScope(ScopeActivationRequest)`, `IControlPlane.RemoveScope(string)`; extension methods `FindScope(key)` and `IsScopeReady(key or ScopeInfo, workerTimeoutSeconds = 15)`.
- `ScopeInfo`, `ScopeActivationRequest`, `ScopeDefinition`, `ScopePart`, `ScopeKind`, `ScopeState`, `ScopeJson`, `IScopeStore` and `ScopeKeys` (`Runtime/ControlPlane/ScopeActivation.cs`, pure C#, compiled into `Services~` too). `ScopeKeys.Hash(key)` is the derivation `NebulaWorker.InstanceKey` has always used — `InstanceKey` now calls it — and `ScopeKeys.ContainerId(key, partId)` is the runtime container id that follows from it.
- `NebulaClient.ScopeKey` (seeded from `-nebula-scope`) and `HelloMsg.ScopeKey`.

**Behaviour that changed:**

- `NebulaWorker.PrepareInstance` now builds a `ScopeDefinition` and calls `ActivateScope` with itself as the preferred worker. Its signature, its return value, its collision exceptions and the guarantee that the asking worker owns the new containers from the first change are unchanged; an instance created by a boundary now also has a scope row.
- A gateway spawns a player only into a container whose `Container.ScopeKey` equals the client's `HelloMsg.ScopeKey`. A client that named a scope with no live owner is held in `JoinState.Starting` and placed with no reconnect once the scope is ready; it is never placed in another scope as a fallback, and a client that named nothing is never placed inside an instance.
- A second activation of a key with a *different* definition is refused and logged; the stored scope stands and nothing changes, so the caller keeps its current scope and may retry.

**What activation does not do:** it does not decide who may enter and does not load content (that stays with `InstanceScenes` and the `PrepareTransfer` handshake). `RemoveScope` removes a scope's row, claim and lease rows without draining it; idle retirement and restore are the scope lifecycle, below.

#### Scope lifecycle: idle retirement and re-activation

See [Simulation scopes](https://nebula.1by3.co/docs/guides/scopes#retiring-and-re-activating-a-scope); the design record is `docs/scope-lifecycle.md`.

**What changed and why.** A scope nobody was in kept its lease rows for ever: `RemoveScope` and the per-container `ReleaseRuntimeContainer` existed, but deciding a whole scope was finished with, saving what was in it, letting go of every part together and refusing admission while it came back was left to each game — and the instancing guide said so ("automatic empty-instance expiration and suspension are not currently provided"). The orchestrator now runs that sequence itself, with the policy as a hook.

**Breaking wire format (protocol 18):**

- `JoinStatus` (5) gained a trailing `u8 reason` (`JoinHoldReason`). A message that ends before it — from a gateway built before this release — reads as `WorldStarting`, which is the only reason such a gateway ever held a join.

**Control-plane document (additive):** a scope row gained `stateSince` and, while a step is in flight, an `acks` array of `{container, phase, count, worker}`. A row without them reads as an active scope with no step running, so a document written by an earlier orchestrator is understood unchanged.

**New behaviour:**

- A scope whose parts have held nothing for `ScopeIdleRetireSeconds` is retired: marked `Retiring` (which refuses admission at once), each part runs the before-retire window, force-checkpoints every persistent entity in it, waits for the store to confirm the writes, empties itself and acknowledges; only when every part has acknowledged are the lease rows deleted and the scope marked `Retired`. The row is kept, so the key keeps its identity.
- Idle age is the **smallest** lease-row age over the scope's parts, so one busy part keeps the whole scope hot. The owning worker re-stamps the lease of a part that holds a client-owned entity, a non-persistent entity, or a persistent entity with unsaved changes — the cases where retiring would lose something.
- Activating a retired key puts the scope in `Restoring` and brings its lease rows back with the same derived container ids. The ordinary lease-landing restore loads the records; the scope becomes `Active`, and admits clients, only when every part has reported its restore complete.
- A client whose `Hello` names a scope that is not `Active` is held in `JoinState.Starting` — welcomed, never dropped — and placed with no reconnect once the scope admits again. It is told why: `ScopeNotReady`, `ScopeRestoring` or `ScopeRetiring`.
- Each step has a 30 s deadline (`ScopeLifecycle.StepTimeoutSeconds`), logged as a warning, so a worker that dies mid-step cannot leave a scope nobody can ever enter or join.
- Nothing here deletes a persisted record. Transient (non-persistent) contents are lost by a retire, which is why the default policy refuses to retire a part that holds any.

**New `NebulaConfig` field:** `ScopeIdleRetireSeconds` (300 s; 0 turns retiring off), with `-nebula-scope-idle-retire`, mirrored into the services config. It never applies to the public world.

**New public API:**

- `ScopeLifecycle` (`Runtime/ControlPlane/ScopeLifecycle.cs`): `ShouldRetire` (the policy hook), `RetireWhenIdle` (the default), `IdleSeconds`, `Occupancy`, `Admits`, `NextState`, `StepTimeoutSeconds`; `ScopeRetirePolicy`, `ScopeRetireContext`.
- `ScopeState.Retiring`, `Retired`, `Restoring`, `ScopeState.AdmitsClients`; `ScopePhase`; `ScopeAck`; `ScopeInfo.StateSince`, `Acks`, `FindAck`, `AllAcked`, `AckedCount`.
- `IControlPlane.SetScopeState(scopeKey, state)` and `IControlPlane.AckScopePart(scopeKey, containerId, phase, count, workerId)` — implemented by `LocalControlPlane`, `ControlPlaneHost` and `RemoteControlPlane`, and reachable over `POST /api/control-plane` as the ops `SetScopeState` and `AckScopePart`. A custom `IControlPlane` must implement both.
- `NebulaPersistence.CheckpointContainer(containerId)`, `ContainerRestored` (`Action<string, int>`), `IsContainerRestored`, `RestoredCountFor`.
- `NebulaWorker.EmptyContainer(container)` (the contents half of `ReleaseRuntimeContainer`) and `NebulaWorker.ScopeLifecycleAgent`; `WorkerScopeLifecycle` with `IsBusy`, `RetiringParts`.
- `JoinHoldReason`, `JoinStatusMsg.Reason`, `NebulaClient.JoinHoldReason`.

**Dashboard:** `/api/state` gained a `scopes` array (`key`, `state`, `parts`, `ready`, `players`, `entities`, `idleSeconds`, `stateSeconds`, `ageSeconds`, `acked`, `containers`) and `scopeIdleRetireSeconds`; the dashboard shows a Scopes card, hidden until a mesh activates one.

#### Scoped procedural chunk grids

See [Simulation scopes](https://nebula.1by3.co/docs/guides/scopes#a-scope-can-be-a-whole-world) and [Build an infinite runtime world](https://nebula.1by3.co/docs/guides/infinite-runtime-world); the design record is `docs/scoped-chunk-grids.md`.

**What changed and why.** The turnkey chunked world was one grid per process: `NebulaChunks.Grid`, a static `RuntimeGrid`, with a chunk's id spending all 63 usable bits of the runtime id on three signed 21-bit axes. Chunk (x, y, z) was therefore one container, one lease row and one persistence record for the whole mesh, so a game with instanced open areas, several maps or one procedural region per party could not use it. There is now one grid **per scope**, each with its own definition, allocator, leases, persistence container ids and interest.

**Breaking wire format (protocol 18):**

- `InstanceContainerInfo` (carried in `ContainerOwnership` entries and stored Base64-encoded on a runtime container's lease row) gained a trailing `string part_id`: which part of its scope the container is, appended after `scope_key` and read with the same tolerance — a row stored by an earlier release has none and reads as empty. For a scoped chunk the part id is its coordinate (`c/x/y/z`), which is how a client or gateway places a chunk whose hashed id it never computed.

**Ids and migration:** the public world's chunk ids are unchanged (`RuntimeGrid.PackId`, still pinned by `RuntimeGridTests`), and a scoped grid's chunk id is `ScopeKeys.ContainerId(scopeKey, "c/x/y/z")` — the same derivation every other scope's containers already used. No persisted id was rewritten, no coordinate range narrowed, and rows from protocol 17 read as the public scope.

**New public API:**

- `ChunkGridDefinition` (`CellSize`, `Planar`, `Ring`, `RetireSeconds`, `Anchor`, `Validate`, `ToJson`/`FromJson`, `ToScopeDefinition`, `Of(scope)`, `Infer(coord, box)`) and `ChunkKeys` (`PartId`, `TryParsePartId`, `RuntimeId`, `ContainerId`) — pure C#, compiled into `Services~` as well.
- `ScopeKind.Grid`: a scope whose payload is a `ChunkGridDefinition` and whose only part is the anchor chunk, so activating an unbounded world does not enumerate it.
- `NebulaChunkedWorld.ActivateGrid(controlPlane, scopeKey, definition, requester, preferredWorkerId)` and `EnsureLocalGrid(scopeKey, definition)`; `NebulaChunkedWorld.ScopedAllocators`.
- `NebulaChunks.GridFor(key)`, `AllocatorFor(key)`, `Grids`, `ActiveScopeKeys`, `IsActiveFor(key)`, `GridOf(container)`, `GridOf(id)`, `ScopeOf(container)`, `BoundsOfId(id, fallback)`, and scope-qualified overloads of `At`, `CoordOf`, `EnsureAt` and `SeedOf`. `ChunkContext` gained `ScopeKey` and `Grid`.
- `RuntimeGrid(cellSize, planar, scopeKey)`, `RuntimeGrid.From(definition, scopeKey)`, `ScopeKey`, `InstanceId`, `IsPublic`, `IdOf(coord)`, `ContainerIdOf(coord)`, `TryCoordOf(id, out coord)`, `Adopt(id, partId, out coord)`, `Owns(container)`.
- `RuntimeGridAllocator.ScopeKey`, `AddPin(coord)`, `RemovePin(coord)`.
- `NebulaWorker.RequestRuntimeContainer(id, frameBounds, InstanceContainerInfo)` — ask for a runtime container that belongs to a scope.

**Behaviour that changed:**

- `NebulaChunks.Grid`, `.Allocator` and every unqualified lookup still mean the public world, and a game with one world sees no change.
- A gateway collects a client's container rows in the client's own scope, and additionally in the public world only when the client is public or its scope sets `ObservePublic`. Before, the window query ran in the public world only: a scoped client was never told about its own scope's empty terrain, and was told about the public world's whether or not it observed it.
- The worker-side chunk allocator rings only pawns of its own scope, retires only its own grid's chunks, and a worker's floating origin follows the centroid of the **public** cells it leases.
- `ContainerRegistry.RuntimeBoundsInFrame` is consulted for every runtime container on an origin shift (with the translated box as the fallback), not only for public ones, so a scoped chunk is recomputed from its coordinate instead of drifting. `NebulaChunks` installs a hook that routes by grid.
- `InstanceScenes.Prepare` no longer tries to create a scene outside play mode; it returns true after its resource checks. This only affects EditMode tests and tooling.

**Unchanged:** entities never ghost, interact or are announced across scopes — container adjacency was already qualified by isolation id, and the conformance scenario now pins it. Interest region ids stayed scope-free in this item (design D12); per-scope origin frames, below, salt them.

#### Per-scope origin frames

See [Each world keeps its own floating origin](https://nebula.1by3.co/docs/guides/infinite-runtime-world#each-world-keeps-its-own-floating-origin) and [Simulation scopes](https://nebula.1by3.co/docs/guides/scopes); the design record is `docs/scope-frames.md`.

**What changed and why.** A process had one floating origin: `WorldOrigin.Cell` was a static, the chunked world computed one centroid over every cell a worker leased, and `ContainerRegistry.ShiftRuntime` translated every runtime container by that one delta. That is correct for one world and wrong for two: an arena at (0, 0, 0) and a continent at (100000, 0, 100000) cannot both sit near Unity's origin, so one of them was simulated 6.4 million metres out, where a 32-bit float has about half a metre of resolution. Putting each scope on its own worker would have made worker count follow the number of worlds instead of the amount of play. Each scope now owns its origin frame instead, so one worker hosts as many worlds as its load allows and each of them stays precision-safe.

**No wire change.** Protocol stays 18. `InterestSubscribe` carries the same fields with the same encoding; region identifiers in `add_region`, `remove_region` and `focus_region` are now XORed with the scope's salt, and the public world's salt is zero, so a mesh with no scopes puts exactly the bytes it did before on the wire. Lease rows, telemetry and persistence still carry absolute boxes and are unchanged.

**New public API:**

- `ScopeFrame` and `ScopeFrames` (`Packages/com.1by3.nebula/World/ScopeFrames.cs`): `ScopeFrames.Public` (which *is* `WorldOrigin`), `Of(instanceId)`, `Of(scopeKey)`, `HasFrame`, `FrameIdOf`, `Ensure`, `Remove`, `All`, `ScopedCount`, `AnyShifted`; per frame `Cell`, `CellSize`, `ShiftCount`, `OriginOffset`, `ShiftDelta`, `Shifted`.
- `RuntimeGrid.Frame` and `RuntimeGrid.ShiftOrigin(coord)` — move this grid's origin and nothing else. The static `RuntimeGrid.ShiftOriginTo` is unchanged and still means the public world.
- `ContainerRegistry.ShiftRuntime(instanceId, delta)`, `ToFrame(box, instanceId)`, `ToAbsolute(box, instanceId)`. The existing no-argument forms remain and mean the public frame.
- `RegionKeys` (`SaltOf`, `Salt`, `Unsalt`) — pure C#, compiled into `Services~` as well.
- `RegionPublisher.WideMask(grid, instanceId, x, y, z, radius)` and `ClientInterest.ScopeSalt`.

**Behaviour that changed:**

- A worker follows a centroid **per scope** (`NebulaChunkedWorld.FollowOwnedCells`) and shifts each scope's origin on its own, moving only that scope's containers, the entities in them, their `StateHistory` world poses, their interpolation buffers and the content parented under `ChunkContext.Root`. The public world's shift no longer moves a scoped grid's containers, and vice versa.
- A scope gets a frame of its own only when it has a chunk grid in this process. Instance scopes (`ScopeKind.Parts` — a room, a dungeon) share the public frame and behave exactly as before.
- A client still keeps exactly one origin; it is now the frame of the scope its pawn stands in, which for an unscoped game is the public world's. Poses, prediction and interpolation are unchanged.
- Interest region identifiers are salted per scope end to end — the worker's index, the publisher's masks and wide-entity matching, the gateway's per-client windows, foci, region → clients map and worker resolution, and the subscribe messages. This closes the cost recorded as design D12 of the scoped chunk grids: a worker no longer streams a gateway the entities of a scope that gateway has no client in. The per-client instance check that decides what a client actually sees is unchanged.
- `NebulaWorker` writes a scoped chunk's lease box through that scope's frame, and `NebulaClient` reads one back through it. A container that reached a process before its grid did is re-placed when the grid activates.
- `ContainerRegistry.Overlapping` and `Find` are asked in the client's own scope when the gateway resolves which workers own a region.
- **Bug fix, all worlds:** `StateHistory.Shift` moved only the recorded poses of entries with *no* container, on the reasoning that a contained entry is rebuilt from its container. Nothing rebuilds it — `StateAt` returns the stored pose — so after an origin shift a contained entity's history answered with a pose from the previous frame. Every entry of the frame that moved is now shifted. This was wrong in the public world too and is corrected there as well.

**Unchanged:** the assignment policy, the assignment planner and the autoscaler never mention a scope; twelve quiet worlds consolidate onto one worker exactly as twelve quiet chunks of one world would, which conformance scenario 14 asserts both ways. `RuntimeGrid.PackId` and `InterestGrid.PackRegion` are untouched and still pinned.

**Known gap:** `WorkerTelemetry` reports entity positions through the public origin, so a scoped entity's reported absolute position on a worker whose scoped frames have moved is off by that scope's offset. It is a diagnostic surface only — nothing routes, leases or simulates on it.

#### Conformance suite

A deterministic test suite for Nebula's cross-worker guarantees, run with `Tools/conformance.ps1` (`-DotnetOnly` for the pure C# tier while the Editor is open). Every test is tagged `[Category("Conformance")]`; the script runs the category in `Nebula.Services.Tests` (`dotnet test`) and in `Nebula.Tests.EditMode` (Unity batchmode), and prints one PASS/FAIL summary with counts. Design and scenario ledger: `docs/conformance-suite.md`; user page: [Run the conformance suite](https://nebula.1by3.co/docs/guides/conformance-suite).

- `ConformanceMesh` (`Tests/EditMode/ConformanceMesh.cs`): two or more real `NebulaWorker` components in one Editor process, each with a recording transport and peer records for the others; `Pump()` delivers every recorded message into the receiving worker's own `Dispatch`. Handovers here run the production builder, wire format and applier end to end, with no gateway, leases or tick loop.
- Covered in this release: scenario 14 (per-scope origin frames, `ConformanceScopeFrameTests` in both tiers and `ConformanceScopeFrameWorkerTests` in the Unity tier), scenario 1 (the location contract, `ConformanceLocationTests`), scenario 2 (scope activation, `ConformanceScopeActivationTests` and `ConformanceScopeRoutingTests`), scenario 3 (the scope lifecycle, `ConformanceScopeLifecycleTests`, `ConformanceScopeCheckpointTests` and `ConformanceScopeAdmissionTests`), scenario 4 (scoped chunk grids, `ConformanceScopedGridTests` in both tiers), scenario 5 (the cross-worker call contract, `ConformanceCallContractTests`), scenario 6 (historical state, `ConformanceStateHistoryTests`), scenario 8 (server-driven handover state, below), scenario 9 (the cross-container joint diagnostic, `ConformancePhysicsDiagnosticTests`) and scenario 10 (the persistence durability window, `ConformancePersistenceDurabilityTests`). Scenario 7 is covered by the cohesion item below.
- Scenario 8, covered: a server-driven entity with `WriteHandoverState`/`ReadHandoverState` state, NetworkVariables and a `NetworkTransform` crosses workers and keeps every field, bumps its epoch by one, fires `OnLostAuthority`/`OnGainedAuthority` once each in order, and ignores a replayed transfer (`ConformanceHandoverStateTests`). The wire leg (`ConformanceHandoverWireTests`, every `AuthorityTransferMsg` field) also compiles into the service tests.
- Tagged into the suite: `ControlPlaneAndRpcTests.HandoverStateRoundTripsPerBehaviourAndIsolatesFaultyChunks` and `PersistenceTests.TheKeyTravelsWithTheHandoverSoTheNextWorkerUpdatesTheSameRecord`.

No runtime behaviour changed.

#### Cohesion hints

The game can now declare that a set of entities must be simulated by one worker and move between workers as a unit, and that a container should not be rebalanced for a bounded time. See [Cohesion hints](https://nebula.1by3.co/docs/guides/cohesion); the design record is `docs/cohesion-hints.md`.

**Wire (protocol 18, no further version bump):** `EntitySpawnMsg` gains a trailing `u32 cohesion_group` (0 for none), written after `view_seq` and before `cost_weight`. It therefore travels in `EntitySpawn`, `GhostSpawn` and `AuthorityTransfer`, so every process holding a copy knows which group the entity is in.

**New public API:**

- `NetworkIdentity.CohesionGroup` (`uint`, 0 = none), `JoinCohesionGroup(uint)`, `LeaveCohesionGroup()`, and a **Cohesion** section in the inspector for authoring one on a prefab.
- `CohesionGroups` (`Runtime/Core/CohesionGroups.cs`): the process-wide table of group membership - `Members`, `MemberCount`, `GroupCount`, `Groups`, `Same(a, b)`.
- `NebulaWorker.HoldContainer(container | containerId, seconds)`, `ReleaseHold(containerId)`, `HoldRemaining(containerId)`, `HeldContainers`, `MaxHoldSeconds` (120), `NebulaWorker.ContainerHold`, `NebulaWorker.CohesionSpan`.
- `NebulaDiagnostics.SplitCohesionGroups`: handovers that could not move a whole group because a member was not owned here. Each is also logged as a warning.
- `AssignmentInput.Holds`, `AssignmentInput.Cohesion`, `AssignmentInput.IsHeld(id)`, `AssignmentInput.DropHeldChanges(changes)`; `CohesionGroupInfo`, `UnsplittableGroup`; `CostBalancedAssignmentPolicy.MaxGroupUtilization` (1) and `.Unsplittable`.
- `MeshTelemetry.CopyHolds`, `CopyCohesion`, `HolderOf`, `ParseHolds`, `ParseCohesion`, `MeshTelemetry.CohesionSpan`.

**Behaviour that changed:**

- A handover of any member of a cohesion group now also hands over every other member the sending worker owns, to the same worker, each as a handover of its own (so a member that is a carrier still takes its passengers). A member the sender does not own cannot be included: that is logged and counted, never a silent split, and the transfer that was asked for still goes ahead.
- `PhysicsIslands.SameIsland` treats two entities in one cohesion group as one island, so `Nebula > Validate Project` and the worker's authority check no longer warn about a joint between them. `PhysicsIslands.IsCohesive` remains as the extension point for a guarantee that comes from elsewhere.
- The worker telemetry document gains `holds` (container id and seconds remaining) and `cohesion` (group, members, and the containers it spans under `in`) arrays. The orchestrator turns a reported hold into a deadline on its own clock, so the two processes need no common time base; a hold dies with the worker that asked for it.
- `CostBalancedAssignmentPolicy` deals the containers of a cohesion group as one item, together with any affinity groups they touch, and skips moves for held items. A group whose containers need more than `MaxGroupUtilization` of a tick budget is dealt whole anyway and reported in `Unsplittable`, in `Note` and on the dashboard.
- The orchestrator state document gains a `cohesion` object (`holds`, `groups`, `unsplittable`) and the dashboard a **Cohesion** card, shown only when there is something in it.
- Conformance scenario 7 is covered (`ConformanceCohesionTests`, both builds; `ConformanceCohesionHandoverTests`, real workers), and the cohesion-group test of scenario 9 is no longer `[Ignore]`d.

#### Capacity refusals on the join messages (protocol 18)

`JoinRejected` (6) gained two trailing fields, `u8 code` (`JoinRejectReason`: `0` none, `1` at capacity, `2` denied by the admission hook) and `f16 saturation` (how full the target was, `1.0` being the whole of the dominant cost component's budget). A message that ends before them — from a gateway built before this release — reads as `None` and `0`, and nothing is assumed. `JoinStatus.reason` gained the value `5`, `JoinHoldReason.AtCapacity`, for a client the admission hook asked to hold rather than refuse. The protocol version is **not** bumped again: 18 is already unreleased and already breaking. See `docs/capacity-admission.md`.

#### Per-entity cost weights (protocol 18)

`EntitySpawnMsg` gained a trailing `f16 cost_weight` (last in the body, after `cohesion_group`), the entity's cost multiplier
(`NetworkIdentity.EffectiveCostWeight`). It travels on `EntitySpawn`, `GhostSpawn` and
`AuthorityTransfer`, so the worker an entity hands over to reports the same cost for it. `0` on the
wire means "no opinion" and leaves the receiver's value alone. See `docs/cost-telemetry.md`.

### Added

#### Transport encryption for native clients (NEB-226)

A native client's UDP link to the gateway can now be encrypted and the gateway authenticated. See
[Encrypt client connections](https://nebula.1by3.co/docs/deploy/encryption); the design record is
`docs/transport-encryption.md`.

**What it does.** A client that asks for encryption exchanges keys with the gateway before it sends anything
else (X25519, one round trip, below `Hello`), and every packet after that is sealed with ChaCha20-Poly1305 —
21 bytes of overhead, whatever the payload. A packet altered in flight, or replayed, is dropped before the game
sees it. The gateway signs the exchange with an RSA certificate; the client checks it against a pinned
SHA-256 SubjectPublicKeyInfo fingerprint. With no certificate configured, the gateway generates a self-signed
one on first run, keeps it beside its executable and prints the fingerprint to pin, so a local mesh needs no
configuration.

**Scope.** Client-to-gateway only. Gateway-to-worker and worker-to-worker links are unchanged and unencrypted:
those processes must run on a private network reachable only by the mesh's gateways, which is now stated as a
requirement in the deployment guides. A web client's link was, and remains, encrypted by DTLS.

**New `NebulaConfig` fields,** mirrored into the services config, with command-line overrides:
`EncryptClients` (`-nebula-encrypt-clients`, default on: answer a key exchange), `RequireEncryption`
(`-nebula-require-encryption`, default off: refuse plaintext clients), `EncryptionCertPath` /
`EncryptionKeyPath` (`-nebula-encryption-cert`, `-nebula-encryption-key`), `EncryptionCertPem` /
`EncryptionKeyPem` (`NEBULA_ENCRYPTION_CERT`, `NEBULA_ENCRYPTION_KEY`), `EncryptionSelfSignedPath`
(`-nebula-encryption-store`), and on the client `ClientEncryption` (`-nebula-encrypt`) and
`GatewayFingerprint` (`-nebula-gateway-fingerprint`). The orchestrator passes the gateway settings to the
gateway it starts.

**Protocol 18, no version bump.** The handshake is a transport frame below `Hello`, not a protocol message.
`JoinRejectReason` gained the value `5`, `EncryptionRequired`, sent with `Retry = false` when
`RequireEncryption` refuses a plaintext client.

**New public API:** `EncryptedTransport`, `ClientEncryption`, `ISecureTransport`, `TransportSecurity`,
`TransportIdentity`, and the managed primitives `NebulaCrypto`, `X25519` and `ChaCha20Poly1305Managed`
(Unity's profile ships none of them). `NebulaGateway.CertificateFingerprint` reports what clients should pin.
On .NET the services use the platform's hardware-accelerated ChaCha20-Poly1305 and fall back to the managed
implementation elsewhere; both are checked against RFC 8439.

#### Scale and failure suite (NEB-237)

Measured, repeatable evidence of what a mesh does under load and when something breaks, in two clearly separated layers. Design record: `docs/scale-suite.md`; user page: [Run the scale and failure suite](https://nebula.1by3.co/docs/guides/scale-suite). No runtime behaviour changed; this is test and tooling only.

- **Synthetic layer**: twelve scenarios over the in-process fake mesh (real gateways, real control plane, real interest code, real client handshakes on loopback), tagged `[Category("Scale")]` so `Tools/conformance.ps1` never picks them up — `dotnet test --filter "TestCategory=Scale"`, about eighty seconds. Sustained load (120 clients, 4 workers, 2 gateways), a 150-client burst, four keyed scopes asserted for zero cross-scope leakage, a worker kill with its restore and no duplicate entities, a gateway lost without a drain, a control-plane restart with and without its storage, a whole-mesh restart curve, autoscale and rebalance under holds, and a rolling upgrade. The ones that run a mesh for seconds also carry `Soak`, so the default `TestCategory!=Soak` run is unaffected.
- **Real-worker layer**: `Tools/scale-suite.ps1`, which starts a real mesh through the `nebula` CLI, drives `Services~/Nebula.LoadGen`, kills real processes, and scrapes `/api/state`, `/api/cost` and each worker log's `[nebula] profile` line into CSV. Modes `-DryRun` (check preconditions, run nothing), `-Synthetic` (run the other layer) and the real run, plus `-Build`.
- **Artifacts**: one CSV per scenario under `Logs/scale/`, stamped `synthetic` or `unity` in the first column and in every line of runner output, so the two layers are never read as one series. The whole-mesh restore curve is compared against a checked-in baseline, `docs/baselines/mesh-restart.csv`.
- **Test fixtures** (`Services~/Nebula.Services.Tests/Fixtures/`): `Fleet.StartWorker`/`KillWorker`, `StartGateway`/`KillGateway(hard)`, `RestartControlPlane(snapshot)`, `FakeWorker.SpawnIntoRequestedContainer`, and the new `ScaleHarness`/`ScaleWorld` helpers.
- **Findings recorded rather than hidden**, each pinned by a test that fails when the behaviour changes: a gateway killed outright never releases its session claims and its sessions cannot be reclaimed (the workaround is a drain request on its control-plane row); a control plane restarted without its storage keeps its gateways but loses its workers, because a worker registers once and never again; and there was no protocol compatibility window at all — a gateway required an exact version match — so a rolling upgrade could replace processes at one protocol version but not span two. That last finding is closed by NEB-228 above, which is where the window and the rolling-upgrade procedure are now described.

#### Gateway fleet operations gap audit (NEB-229)

Closes the hard-gateway-kill session-reclaim gap the scale suite pinned (D7a). Design record:
`docs/gateway-fleet-audit.md`; user page: [Connecting clients](https://nebula.1by3.co/docs/guides/connecting-clients#reconnect-after-a-lost-link-or-a-draining-gateway).

- **A gateway that has stopped heartbeating is now treated as gone by session coordination.** `GatewaySessionDirectory` (`Runtime/ControlPlane/GatewaySessionCoordination.cs`) takes an optional gateway-liveness predicate; `LocalControlPlane` wires it to the control plane's existing `GatewayInfo.LastHeartbeat` (a new `GatewayStaleAfterSeconds`, default 5 s, the same cutoff a worker row uses). A claim against an owner already confirmed gone is granted immediately instead of parked for the 15 s pending window; a claim already pending re-checks the owner's liveness on every retry, so an owner that dies mid-wait is evicted as soon as it goes stale. An incarnation check keeps a merely slow-but-alive owner from ever being evicted.
- **Measured, not assumed:** a hard-killed gateway's sessions now reclaim in 4.7–5.0 s across single-pair and fan-out runs (was: never, refused after the 10 s coordination deadline). `ScaleThresholds.HardKillGatewayReclaimSeconds = 9.0 s` is set from the measurement with headroom. `ScaleFailureTests`' pinned negative test now asserts the positive; new `ScaleGatewayAuditTests.cs` adds a higher-fan-out measurement and asserts directly that a hard kill loses no authoritative worker state (same `NetId`, zero duplicate pawns).
- **Everything else in the audit — load balancing, drain, gateway replacement, client-side reconnect behaviour — was already correct** and is recorded closed with its evidence in `docs/gateway-fleet-audit.md`; nothing else changed. Nebula still does not run a load balancer in front of the gateway fleet; that boundary in `website/CONTENT_GUIDE.md` is restated, not changed.
- No wire format or protocol version change.

#### Lifecycle hooks for materialization and dematerialization (NEB-242)

Game callbacks at the moments the mesh brings a container or a scope to life, or puts it to sleep, with the ordering a game needs to seed from — or collapse into — its own state. Design record: `docs/lifecycle-hooks.md`; user page: [Lifecycle hooks](https://nebula.1by3.co/docs/guides/lifecycle-hooks). Conformance scenario 11.

- `NebulaLifecycle`, a static hook set raised on the main thread on the worker that owns the container: `OnScopeActivating(ScopeInfo scope, bool hasRecords)`, `OnContainerRestored(Container container, int restored)`, `OnBeforeRetire(Container container, CancellationToken cancel)` (async, awaited) and `OnRetired(Container container)`. `NebulaLifecycle.Reset()` clears them and runs at the start of a play session. A handler that throws is logged and ignored; the retire or the restore carries on.
- **Ordering guarantees**, each covered by a test: `OnScopeActivating` runs before any of that scope's containers restores on that worker (the restore is held for it); `OnContainerRestored` runs after the restore, once per lease, including for a container with nothing saved; `OnBeforeRetire` completes — or is cancelled on the existing 10 s window, with a warning — before the forced checkpoint is issued and before anything is despawned; `OnRetired` runs after the orchestrator has released the part's lease.
- New store method `IPersistenceStore.CountRecords(scopeKey, containerId, onCounted)`: how many records a scope holds, without reading them. A `SELECT COUNT(*)` on SQLite and PostgreSQL (with a new `nebula_entity_scope` index), a walk in `LocalPersistenceStore`, and the new additive endpoint `GET /api/store/count?scope=&container=` for `RemotePersistenceStore` — only the number travels. **A custom `IPersistenceStore` implementation must add it.** No wire protocol change and no persisted-row change.
- `IPersistenceStore.LoadWhere` is documented as the **offline read** for the records of a world nothing is simulating, with a new "Offline reads" section in the persistence guide. Nebula owns the moments, never the game's summary: persistence saves entities, not worlds.
- New internal seam `NebulaPersistence.RestoreGate` (a predicate the worker points at `WorkerScopeLifecycle.MayRestore`), and `WorkerScopeLifecycle.ActivatingTimeoutSeconds` (10 s), after which a store that never answered the record count lets the restore proceed with a warning. With no handler subscribed nothing is asked of the store and the restore path is unchanged.
- `Tests/EditMode/ConformanceLifecycleHooksTests.cs` (7 tests) and the store count in SQL and over HTTP in `Services~/Nebula.Services.Tests/StorageAndHostTests.cs`; ledger row 11 in `docs/conformance-suite.md` §4.

#### Cohesion-aware rebalancing along game-defined boundaries (NEB-235)

The planner already cut along the boundaries the game authored and already refused to split a cohesion group or move
a held container. It now explains what it did, and says why when it could not. Design record:
`docs/cohesion-rebalancing.md`; user pages:
[Cohesion → Rebalancing a hot area](https://nebula.1by3.co/docs/guides/cohesion#rebalancing-a-hot-area) and
[Orchestrator → Assignment plan](https://nebula.1by3.co/docs/guides/orchestrator-and-dashboard#assignment-plan).

- **New public API** (`Runtime/Orchestrator/AssignmentReport.cs`): `AssignmentMove` (container, from, to, reason),
  `SaturationReport` (container, scope key, the whole item, worker, utilization, cause, reason), `SaturationCause`
  (`NoBoundary`, `CohesionGroup`, `AffinityGroup`, `Held`, `Dedicated`) and `IExplainsAssignment`, the second
  interface a policy implements to offer both. `IAssignmentPolicy` is unchanged, so a policy a game wrote itself
  keeps compiling and simply explains nothing.
- `CostBalancedAssignmentPolicy` implements `IExplainsAssignment`: `Moves` carries one explained move per change,
  naming the boundary the cut fell on, the before/after peak and the group that kept containers together;
  `Saturated` names what the pass could not relieve. New knob `SaturationUtilization` (0.7).
- `ScaleDecision.BlockedCause` and `BlockedReason` append the constraint to the blocked reason, beside the unchanged
  `BlockedComponent` / `BlockedSaturation`. `AssignmentInput.HoldSeconds(containerId)`.
- `NebulaOrchestrator.LastMoves` and `Saturated`; `/api/state` gains an `assignment` block (`policy`, `rebalances`,
  `moves`, `saturated`) and `scale.blockedCause` / `scale.blockedReason`; the dashboard gains an **Assignment plan**
  card, and `assign c -> w` log lines carry the reason in brackets.
- **Fixed:** `AssignmentPlanner.Predict` did not carry `AssignmentInput.Cohesion` into its dry run, so a prediction
  could split a cohesion group the real deal may not and promise the scaler a relief that never arrives. It now
  carries `Cohesion`, `Cost` and `Holds`, and `AssignmentPlan.Moves` carries the dry run's explanations.
- Conformance scenario 13 (`Tests/EditMode/ConformanceRebalanceTests.cs`, 10 tests, both builds).
#### Explicit capacity limits and admission reporting (NEB-236)

See [When a target is full](https://nebula.1by3.co/docs/guides/scopes#when-a-target-is-full) and [Capacity and admission](https://nebula.1by3.co/docs/guides/orchestrator-and-dashboard#capacity-and-admission); the design record is `docs/capacity-admission.md`.

**What changed and why.** When an interaction domain exceeded what one worker could simulate, the mesh had no way to say so: a gateway could refuse a client for a bad token or because it was draining, and nothing else. A station that six hundred players jump to in five minutes is one authored domain that cannot be split past its parts, so the mesh now reports that it is at capacity and the game decides what to do — queue, deny or degrade. Nothing is ever silently split or copied to make room.

**New behaviour:**

- A per-container capacity reading is derived once per orchestrator pass from the cost rows (`docs/cost-telemetry.md`): the dominant component's share of its own budget, and whether that reached `CapacitySaturation`. A parts scope is as full as its **worst** part; a grid scope is judged one chunk at a time, like the public world, because its chunks are separate places. A container no worker has reported lately is *unknown*, not full, so a mesh without cost telemetry admits exactly what it did before.
- A container the planner reports it cannot relieve by moving anything (`SaturationReport`, NEB-235: a cohesion or affinity group spanning it, a hold, no authored boundary, a dedicated worker) is at capacity once its item's utilization reaches the threshold, whatever its own cost row says, and `CapacityInfo.Cause` carries which constraint it is all the way to the admission hook.
- The orchestrator publishes the reading on the container's lease row, so a gateway answers without an RPC. The write deliberately does **not** stamp the lease's `UpdatedAt` (that is the idle clock the scope lifecycle retires on), and only a reading that actually moved is written.
- A join into a target at capacity goes to `NebulaAdmission.Decide`, which refuses by default; the client is sent `JoinRejected` with `JoinRejectReason.AtCapacity`, the saturation, and `retry` clear, and `NebulaClient` does not reconnect by itself. `AdmissionDecision.Hold()` instead holds the client in `JoinState.Starting` with `JoinHoldReason.AtCapacity` and places it, with no reconnect, when room appears.
- In the public world the gateway prefers spawn candidates that are not at capacity and only refuses when every candidate is full.
- `NebulaWorker.PrepareTransfer` goes through the same hook. A refused crossing returns a finished `InstanceTransfer` with `Error`, `RejectReason` and `Saturation` set, and nothing is sent to the destination worker or the client's gateway.
- A policy that throws is counted in `NebulaAdmission.PolicyErrors`, logged, and read as a refusal.

**New `NebulaConfig` field:** `CapacitySaturation` (0.9; 0 turns the signal off and admits everything), with `-nebula-capacity-saturation`, mirrored into the services config.

**Control-plane document (additive):** a lease row gained `saturation`, `dominant`, `atCapacity` and (when the planner named one) `cause`, written only once the orchestrator has a reading. A row without them reads exactly as before.

**New public API:**

- `CapacityInfo` and `NebulaCapacity` (`Runtime/Orchestrator/CapacityInfo.cs`): `Derive`, `Of`, `OfScope`, `Target`, `Worse`; `IControlPlane` extensions `CapacityOf(containerId)` and `ScopeCapacityOf(scopeKey)`; `NebulaOrchestrator.CapacityOf`.
- `NebulaAdmission` (`Runtime/Gateway/NebulaAdmission.cs`): `Decide`, `AlwaysConsult`, `RejectWhenAtCapacity`, `Ask`, `DefaultReason`, `PolicyErrors`, `Reset`; `AdmissionPolicy`, `AdmissionRequest`, `AdmissionDecision`, `AdmissionAction`, `AdmissionKind`.
- `IControlPlane.SetContainerCapacity(containerId, saturation, dominant, atCapacity)` — implemented by `LocalControlPlane`, `ControlPlaneHost` and `RemoteControlPlane`, and reachable over `POST /api/control-plane` as the op `SetContainerCapacity`. A custom `IControlPlane` must implement it.
- `LeaseInfo.HasCapacity`, `Saturation`, `Dominant`, `AtCapacity`, `SaturationCause`; `ContainerCost.ComponentOf`; `ControlPlaneJson.CauseOf`; `MeshTelemetry.CapacitySaturation`; `NebulaCapacity.Apply` (the planner's saturation reports folded into the readings).
- `JoinRejectReason`, `JoinRejectedMsg.Code`/`Saturation`, `JoinHoldReason.AtCapacity`, `NebulaClient.JoinRejectReason`, `NebulaClient.JoinRejectSaturation`, `NebulaClient.JoinRefused`; `InstanceTransfer.RejectReason`, `InstanceTransfer.Saturation`.

**Dashboard and API:** `GET /api/cost` and the `cost` block of `/api/state` gained `capacitySaturation` and an `atCapacity` flag per row; each `scopes` row gained `capacityKnown`, `saturation`, `dominant`, `atCapacity` and `capacityCause`; `/api/state` gained `capacitySaturation`. The Container cost table has a **Full** column and the Scopes card a **Capacity** column.

**Conformance:** scenario 12 of `docs/conformance-suite.md` is covered by `Services~/Nebula.Services.Tests/ConformanceCapacityAdmissionTests.cs`, with the derivation unit tested in both builds by `Tests/EditMode/CapacityAdmissionTests.cs`.

#### Persistence durability window (NEB-224)

Docs and a conformance test state and check the bound on how much a worker crash can lose between checkpoints.
Design record: `docs/persistence-durability.md`; user page: [Persistence → Durability window](https://nebula.1by3.co/docs/guides/persistence#durability-window).

- The bound is `PersistenceCheckpointSeconds + MinSaveIntervalSeconds + ceil(N_dirty / MaxSavesPerFrame) frames + store write latency`, derived from `NebulaPersistence.IsDue`/`PumpCheckpoints`; with the defaults and a lightly loaded worker it is about 5.6 s.
- `ConformancePersistenceDurabilityTests` (`[Category("Conformance")]`, scenario 10 in `docs/conformance-suite.md` §4) kills a worker with 200 simultaneously dirty entities and asserts loss stays within the bound, and that the min-save throttle and per-frame budget terms are each real rather than vacuous.
- `NebulaPersistence.Now` is a new internal clock seam (`Func<float>`, defaults to `Time.unscaledTime`) so the checkpoint scheduler's timers can be driven deterministically in tests; no behaviour change at the default.
- `NebulaPersistence.OldestDirtyAgeSeconds`: how long the oldest currently-dirty tracked entity has been waiting for its next checkpoint, 0 when nothing is dirty.
- New additive heartbeat field `WorkerStats.OldestDirtySeconds` / `WorkerInfo.OldestDirtySeconds`, wired through `ControlPlaneJson`, `LocalControlPlane` and `RemoteControlPlane`; an older worker or orchestrator simply does not read it, so this is not a protocol version bump. The orchestrator's `/api/status` reports it as `workers[].oldestDirtySeconds`, and `NebulaDashboard.html` shows it per worker next to tick time.

#### Historical state on workers (`StateAt`)

See [Lag compensation](https://nebula.1by3.co/docs/guides/lag-compensation); the design record is `docs/state-history.md`. Conformance scenario 6.

A worker can now ask, for any entity it holds — one it simulates or a ghost of a neighbour's — what that entity looked like at a recent server tick. This is what lag compensation and time-sensitive validation need and what only the replication layer can supply, especially for ghosts.

- `NetworkIdentity.StateAt(tick)` / `TryGetStateAt(tick, out HistoricalState)`, plus `OldestAvailableTick`, `NewestAvailableTick` and `History`. `NebulaWorker.TryGetStateAt(netId, tick, out state)` answers the same question by net id for anything the worker holds.
- `HistoricalState`: `Available`, `Tick`, `Position`, `Rotation`, `Velocity`, `Container`, `Epoch`, `FromAuthority`, `TryGetValue(variable, out value)`.
- `StateHistory` — the bounded per-entity ring (`Capacity`, `HasEntries`, `OldestAvailableTick`, `NewestAvailableTick`, `DefaultWindowTicks` 32, `MaxWindowTicks` 1024, `GapToleranceTicks` 4). Allocated on an entity's first recorded tick and reused; recording a tick after that allocates nothing.
- `[SyncHistory]` (`SyncHistoryAttribute`) on a `NetworkVariable` field snapshots its value every recorded tick, readable through `HistoricalState.TryGetValue`. Pose, velocity, container and epoch are always recorded; a variable is not, because a snapshot costs a serialization per tick per copy. `NetworkVariableBase.SyncHistory` reports the flag.
- New `NebulaConfig` field `StateHistoryTicks` (32, about half a second at 60 Hz), with the `-nebula-state-history` command-line override, mirrored into the services config. `0` turns recording off entirely and allocates nothing.
- New sample `Samples~/LagCompensatedHitscan`: a reference hitscan validator that rewinds candidates to the tick a client claims, tests the shot there and applies the result through an `[AuthorityRpc]`. Rewinding colliders, performing the hit test and deciding which tick a client may claim stay game decisions; Nebula ships the history, not the policy.

**The bound.** A ghost entry is tagged with the **owner's** tick, so `StateAt(t)` means the same instant on every worker. A ghost holder's `NewestAvailableTick` is exactly one tick behind the authority's (the owner built that stream during its previous tick). Inside the window a tick nothing was recorded for is answered by the nearest recorded entry within 4 ticks, and the result says which tick it actually is; outside the window the result is unavailable, never an extrapolation and never the nearest edge.

#### Per-entity cost hints and per-container cost telemetry

A game can say what one entity costs, and an operator can see what one container costs, split into
simulation, replication and gateway relay. Design record: `docs/cost-telemetry.md`; user docs:
[Container cost](https://nebula.1by3.co/docs/guides/orchestrator-and-dashboard#container-cost) and
[Entity cost weights](https://nebula.1by3.co/docs/guides/orchestrator-and-dashboard#entity-cost-weights).

- `NetworkIdentity.CostWeight` (inspector, default `1`) is a **multiplier** on the entity's category
  weight in `NebulaConfig.CostWeights`, so a world that sets nothing behaves exactly as before.
  `NetworkIdentity.EffectiveCostWeight` is what it carries; `NetworkIdentity.SetCostWeight(w)` pins one.
- `NebulaCost.EntityWeight` (`Func<NetworkIdentity, float>`) decides a weight at spawn; a negative
  return declines and leaves the authored value. Precedence: `SetCostWeight` or a carried weight >
  the callback > `CostWeight`. Evaluated on spawn and on `SetCostWeight`, never per tick.
  `NebulaCost.MaxWeight` (1024), `NebulaCost.Clamp`, `NebulaCost.Reset`.
- `ContainerCost` and `CostComponent` (`Runtime/Orchestrator/ContainerCost.cs`): one typed row per
  container - `EntityCostSum`, measured `TickShareMs` / `TickShare`, `BytesOutPerSec`,
  `GatewayBytesPerSec`, `GhostCount`, `ScopeKey`, `WorkerId`, `Dominant`, `DominantSaturation`.
  The orchestrator keeps the latest row per container (which is per lease):
  `MeshTelemetry.CopyContainerCost`, `MeshTelemetry.BuildCostJson`, `NebulaOrchestrator.CostOf`,
  `AssignmentInput.Cost`.
- `ContainerCostMeter` (`Runtime/Worker/ContainerCostMeter.cs`) and `NebulaWorker.CostMeter`: the
  worker **measures** the `NetworkTick` time of each container's own entities (two stopwatch reads
  per entity per tick) and the bytes their replication and owner state cost. The rest of the tick
  (physics, ghosts, interest, publishing) belongs to no container and is not attributed, so a
  worker's rows add up to less than its utilization.
- The telemetry document's `containers` rows gained `scope`, `cost`, `tickMs`, `bytesOut` and
  `gatewayBytes`, additively. A document without them reads exactly as before.
- `CostWeights.Of` prefers the worker's reported entity cost sum
  (`ContainerLoad.EntityCostSum` / `HasEntityCost`) over counting heads, so weights reach the cost
  policy, `WorkerLoadTracker.Attribute` and the scaler's dry runs, not only the dashboard.
- `WorkerScaler` names the dominant component of a container it reports as unsplittable, in the
  reason string and as `ScaleDecision.BlockedComponent` / `BlockedSaturation`
  (`scale.blockedComponent` / `scale.blockedSaturation` in `/api/state`).
- `GET /api/cost` serves the rows, and `/api/state` carries them under `cost`. The dashboard has a
  **Container cost** table that highlights the blocked container.
- New `NebulaConfig` field: `CostLinkBudgetMbps` (100), the yardstick a container's bytes are
  weighed against when its dominant component is chosen. It limits nothing.

### Changed

- The worker's per-tick pose recording now records only entities it has authority over; a ghost records itself when the owner's stream is applied, tagged with the owner's tick instead of the receiving worker's. Before, every copy was recorded with the receiver's tick, so a ghost's history was silently off by a tick and an interpolating ghost recorded a smoothing artefact rather than the pose the owner reported.
- A worker sends an entity's `GhostVars` update ahead of that entity's state entry for the same tick, so a `[SyncHistory]` variable is snapshotted with the values the tick's stream carries. Nothing else depends on the order of those two messages.
- `NetworkIdentity.TryGetPoseAt(tick, out position, out rotation)` keeps its signature and behaviour (false plus the current pose) and is now a wrapper over `StateAt`. The constant `NetworkIdentity.PoseHistoryTicks` (64) is **removed**: the window is `NebulaConfig.StateHistoryTicks` (default 32) and the constant behind it is `StateHistory.DefaultWindowTicks`.

### Fixed

- **A client could lose its own pawn for good in a chunked world.** When an ownership update dropped the runtime container that the client's copy of its pawn was still filed under, `NebulaClient` despawned the pawn locally along with the container's other occupants. The gateway keeps a pawn in its owner's interest set wherever it is, so it never sent the pawn again: the client stayed connected with no pawn until a cross-worker handover respawned it. The local pawn is now kept and moves into the neighbouring container until its next state update places it.
- `NebulaPersistence.Apply` on a spawned entity whose record names a container or carrier that is not registered in this process (such as an unloaded chunk) no longer moves the entity out of every container to the record's container-relative position. The entity keeps its current pose, its persisted state is still applied, and the worker logs a warning. The restore path, which runs before the entity is spawned, is unchanged. This was what moved a returning player's pawn out of its spawn chunk and set up the pawn loss above.
- Workers no longer log `0 entity object(s) were destroyed without a despawn` on every tick. A scratch list shared between two steps of the tick was not cleared, so the check for destroyed entities ran (and logged) every tick with nothing to purge.

## [0.1.0-alpha.29] - 2026-09-21

### Breaking: protocol 16 → 17, interest management

Every Nebula process must be rebuilt and restarted together. A gateway disconnects a client whose protocol version is not exactly `17`; there is no negotiation between 16 and 17. See [Interest management](https://nebula.1by3.co/docs/guides/interest-management) and the [wire protocol specification](https://nebula.1by3.co/docs/specifications/wire-protocol).

**What changed and why.** Before this release, visibility was instance scope only: every client in a public instance was spawned every entity in it, and distance only slowed how often an already-visible entity's transform was resent. A gateway connected to every worker in the mesh and cached every entity it held. None of that scales past a small, fully loaded world. Interest management now bounds which entities a client is told about at all, and which workers a gateway needs to hear from, without changing who simulates what — a container is still one worker's simulation budget. See the guide's [scaling limits](https://nebula.1by3.co/docs/guides/interest-management#scaling-limits-and-when-to-partition) section for exactly what this does and does not solve.

**Breaking visibility semantics:**

- A client with no pawn (a spectator, or a client still joining) now receives only `AlwaysRelevant` entities, instead of every entity at the far update rate. Give a pawn-less client a focus through a custom `IInterestPolicy` if it should see more.
- An entity farther than a client's interest radius (`NebulaConfig.InterestRadius`, default 120 m, plus `InterestExitMargin` and a linger period) is despawned on that client, not just throttled. Set `NetworkIdentity.RelevanceRadius` above the default for anything that must be visible from farther away, and `AlwaysRelevant` for the few things every client must always see (a match timer, a world boss).
- Authorization failure (a private instance, an unauthorized team) now removes an entity from a client's set immediately, with no linger — this was already the intent for instance isolation, and is now also how a game's own `IInterestPolicy.Authorize` behaves.

**Breaking wire format (protocol 17):**

- `EntitySpawnBody` gained `f16 relevance_radius`, `u8 interest_flags`, `u8 interest_group`, `u16 view_seq`. `EntityDespawnBody` gained `u16 view_seq`. A receiver on an older protocol version cannot parse these messages.
- `ContainerOwnershipMsg` is now a per-client delta: a leading flags byte (`Full`) and a trailing list of removed container IDs, instead of always being the complete lease table. A gateway now tells a client about a container only when it is near that client's interest window, in its instance, or carries an entity already in its set.
- `AuthorityTransferMsg` gained `InterestGateways`, the gateways following an entity by explicit subscription (not by region) that must be told when it changes worker.
- New message types: `InterestSubscribe`, `InterestResync`, `EntityRedirect`, `EntityForget` (gateway ↔ worker subscription protocol), and `ClientFocusHint` (client → gateway; hints unreliable, the `Clear` form reliable, both carrying a generation so a late hint cannot undo a clear). `ClientFocusHint` carries the hinted point as **three `f64` absolute world coordinates** rather than a frame-relative `Vector3`: the client adds its floating origin before sending, so an origin shift does not move the place the gateway thinks it is watching, and a camera tens of kilometres out is still named to the metre.
- Workers no longer announce every authoritative entity when a gateway connects (`Hello`). A gateway now receives only the regions its clients' foci need, and links a worker only while it has a reason to (a subscribed region, a followed entity, or a first-spawn request for a joining client with no pawn yet).

**New `NebulaConfig` fields:** `InterestRadius` (120), `InterestExitMargin` (16), `InterestLingerSeconds` (1), `InterestCellSize` (64), `InterestPlanar` (true), `InterestEvalHz` (4), `InterestSubscribeMargin` (32), `InterestRegionLingerSeconds` (3), `InterestLinkLingerSeconds` (10), `InterestResyncSeconds` (30), `InterestMaxRadius` (1024), `InterestMaxFoci` (8), `InterestHintMaxDistance` (60), `InterestHintMaxHz` (5), `InterestMaxExplicitPerClient` (16), `InterestMaxFocusSpeed` (12), `PartitionWarnEntities` (2000), `PartitionWarnFilterMs` (2), `ChunkedWorld` (false), `ChunkPlanar` (true), `ChunkRetireSeconds` (30). New command-line overrides `-nebula-interest-radius` and `-nebula-interest-cell`.

**New public API:**

- `NetworkIdentity.RelevanceRadius`, `AlwaysRelevant`, `InterestGroup` — per-prefab interest overrides.
- `IInterestPolicy`, `DefaultInterestPolicy`, `InterestPolicies.Combine`, `InterestFocus`, `InterestQuery`, `InterestClient`, `InterestEntity` — the interest policy API (`Runtime/Interest/InterestPolicy.cs`).
- `NebulaGateway.InterestPolicy`, `SetClientTag`/`GetClientTag`, `SetClientTags`/`GetClientTags`, `SetClientFocusMode`/`GetClientFocusMode`, `MarkInterestDirty`, `MarkAllInterestDirty`, the `ClientJoined`/`ClientLeft` events and `GatewayClientInfo`, `InterestSetSize`, `CachedEntityCount`, `SubscribedRegionCount`, `WorkerLinkCount`, `InterestSettingsInUse`, `InterestGridInUse`, and the `InterestLinkReason` flags.
- `FocusMode`, `FocusHintDecision` and `IFocusHintPolicy` — the server's say over a client's focus hint. A hint is refused unless the server allowed this client one: `FocusMode.PawnClamped` (the default) clamps it to `InterestHintMaxDistance` of the pawn and refuses it outright from a client with no pawn, `FocusMode.Free` takes it as sent, `FocusMode.Disabled` ignores it. Instance isolation applies in every mode. `InterestClient.FreeHint` is now a read-only shorthand for `FocusMode == FocusMode.Free` and is actually populated; `InterestClient` also gained `Tags`, `PawnCarrierNetId` and `FocusMode`, and `InterestEntity.InstanceId` is now filled in (resolved through the carrier chain) instead of always reading zero.
- `InterestSchedule` (`Runtime/Interest/InterestSchedule.cs`) — the gateway's evaluation rotation: routine re-evaluations are spread over the ticks of one `InterestEvalHz` interval instead of every client being evaluated on the same tick, while a client marked dirty is still evaluated on the next tick. `GatewayStats.InterestEvalsPerSecond` reports the rate (also on the control-plane heartbeat and in the debug overlay).
- `FocusHintFilter.TryAccept` takes a `FocusMode` in place of its `freeHint` flag and gained `Throttled`, `Authorizes` and a `DroppedUnauthorized` counter. It also gained `Malformed`, `ThrottledAttempt`, `ResetBudget`, `MaxMagnitude` and the `DroppedOutOfRange`/`DroppedMalformed` counters: a focus hint is now checked for NaN, infinity and an absurd magnitude **before** anything reads it, and the rate limit is spent by every hint *attempt* rather than only by accepted ones, so a client whose hints are always refused can no longer invoke `IFocusHintPolicy` once per packet (design D80). `NebulaGateway` surfaces the counters as `FocusHintsAccepted`, `FocusHintsClamped`, `FocusHintsDroppedByRate`, `FocusHintsDroppedMalformed` and `FocusHintsDroppedUnauthorized`.
- `NebulaGateway.RevalidateInterest(clientId)` / `RevalidateAllInterest()` and `ClientInterest.Revalidate` — **immediate revocation** (design D81). `MarkInterestDirty`/`MarkAllInterestDirty` put a client at the front of the evaluation rotation, which is still bounded per tick, so on a gateway with more clients than the dirty cap a *tightening* change left clients holding replicas their policy had already refused for several ticks. The new calls re-authorize each affected client's current set synchronously — one `Authorize` per replica held, no grid query, nothing allocated — and are now what `NebulaGateway.InterestPolicy`'s setter, `SetClientTag` and `SetClientTags` use, so those paths fail closed with no code change in a game. `MarkInterestDirty`/`MarkAllInterestDirty` keep their behaviour and are documented as additive and staggered: use them for reveals, `Revalidate…` for anything that can take visibility away. `IGatewayExtensionContext` mirrors both pairs, and `Nebula.SampleExtension` now applies its fog file with `RevalidateAllInterest` because replacing that file can close fog as well as open it.
- `InterestIndex<T>` now decides where a **carried** entity sits, in one place, for the worker and the gateway alike (design D70). `Add`/`AddWide`/`AddGlobal` record an item's **own** placement; while it rides in something it sits exactly where its root carrier sits, and getting off (or losing the carrier to a despawn) restores the own placement. New and changed members: `SetCarrier` returns a `CarrierLink` (`Unchanged`, `Linked`, `Detached`, `UnknownItem`, `Cycle`) instead of `void` — a link that would close a carrier cycle is **refused**, changes nothing, and is reported by the caller once per entity; `RootOf(netId)` gives the outermost carrier an item rides in; `TryGetOwnPlacement(netId, out placement, out region)` reports what the item asked for itself, beside `TryGetPlacement`, which reports where it actually is. `CollectCarried` has no size cap any more (it was 4,096, which silently truncated a wide or deep subtree into a half-rebucketed ship); acyclic links are what makes the walk safe, and the index size is the only bound left.
- `CarriedTransition.Capture` and `Resolve` take an optional `Func<ulong, ulong> wideMaskOf` and read a slot's placement as well as its region (`Slot.From`/`Slot.To`), so a reclassification — global ↔ carried-region, wide ↔ carried-region — publishes the spawns and forgets it implies in the same carrier-before-contents order as a plain rebucket. They previously wrote every non-region entity off and published nothing for it.
- `HandoverScope` and the `HandoverScope` parameter on `CarriedTransition.Resolve` — the bookkeeping a whole-subtree authority handoff needs, shared by the worker and by anything else publishing the same index (design D85/D87). A transfer takes a `Frame` from the scope in a `using`; the outermost frame owns the follower set, `Collect(index, carrier)` fills it from the carrier links and `Follows(netId)` answers the one question the publication asks. Passing the scope to `Resolve` collapses a follower's transition to "nothing changed", which is where a handoff stops republishing its own passengers as orphans.
- `NebulaGateway.IsEntityCached(netId)` and `TryGetCachedPlacement(netId, out placement, out region)` — whether this gateway holds a record for one entity, and where it has it bucketed. The count alone cannot tell "never sent here" from "sent and filtered out per client", which is the distinction D70 is about.
- `NebulaClient.FocusHint`, `ClearFocusHint`, `ReplicaCount`, `SpawnsReceived`, `DespawnsReceived`.
- `NebulaClient.SetContentAnchor(Transform)`, `ContentAnchor` and `ActiveContentAnchor` — what the client keeps loaded and where its floating origin sits. The local pawn by default (unchanged behaviour); hand it a strategy camera's transform and both the baked cell-scene streaming (`NebulaWorldStreaming`) and the runtime chunk origin (`NebulaChunkedWorld`) follow the camera instead. `null` gives the pawn its job back. It is deliberately separate from `FocusHint`: the hint is a server-validated request about what to be *sent*, the anchor a local decision about what to keep in memory.
- `NebulaGateway.ContainerRowCap` and `ContainerBudgetHits`, `InterestSettings.MaxContainerRows(cellSize)`, `InterestQuery.BoxesClamped` and `MaxBoxHalf` — the bounds on the container window described below.
- `NebulaChunks` (`Loaded`/`Unloading` events, `EnsureAt`, `At`, `IsLoadedAt`, `CoordOf`, `SeedOf`) and `ChunkContent`, the turnkey chunked-world content API raised when `NebulaConfig.ChunkedWorld` is on.
- `Debug/InterestProbe`, a component that logs bounded-replica soak checks (`InterestProbe.Attach(client)`).
- `ContainerRegistry.Overlapping` (box query), used to scope a client's container ownership window.
- `IGatewayExtension` and `IGatewayExtensionContext` (`Services~/Nebula.Services/GatewayExtension.cs`) — **game code inside the standalone gateway**, and the only way to use the interest policy API when `nebula start` or a deployed mesh owns the gateway process. An extension is a class library that references the published gateway's `Nebula.Services.dll`, implements `IGatewayExtension`, sits next to `nebula-gateway` in the build folder (so it ships with the build and the deploy tarball), and is named in the new `NebulaConfig.GatewayExtension` field. The context exposes `SetInterestPolicy`, `ClientJoined`/`ClientLeft`, the client tag / focus-mode setters, `MarkInterestDirty`/`MarkAllInterestDirty`, `RevalidateInterest`/`RevalidateAllInterest`, `Post(Action)` (the thread-safe way to hand fog-of-war computed elsewhere to the gateway loop), `Option(key)`, `Config`, `GatewayId` and logging. `Initialize` runs before the gateway's first tick, so a policy installed there has been asked about every client. Nothing is ever scanned: a missing assembly, a missing type, more than one candidate type, or an assembly that does not reference the gateway's `Nebula.Services.dll` is a start-up failure with a message naming what was wrong. After start-up every call into the extension is isolated — logged, counted in the new `GatewayStats.ExtensionErrors`, and a throwing `IInterestPolicy.Authorize` **denies** rather than allows. `Services~/Nebula.SampleExtension` is a worked example (team tags on join, fog of war fed from a watcher thread).
- New `NebulaConfig` fields `GatewayExtension`, `GatewayExtensionType`, `GatewayExtensionOptions` (`key=value;key=value`), with `-nebula-gateway-extension`, `-nebula-gateway-extension-type`, `-nebula-gateway-extension-options` and per-extension `-nebula-ext-<key>` overrides; the orchestrator forwards all of them to the gateway it launches.
- `GatewayStats.ExtensionErrors` (also on the control-plane heartbeat and in `/api/state`): exceptions the gateway extension has thrown since the process started.

**Migration notes:**

- Rebuild and restart every worker, gateway, orchestrator, and client build together; a protocol mismatch disconnects clients outright.
- If your game assumed every client could see every public entity regardless of distance (a full-map minimap, a global radar), set `AlwaysRelevant` on those entities' prefabs or add a policy that gives the relevant clients a wider focus. Auditing this before upgrading avoids entities silently disappearing past 120 m.
- If a pawn-less client (a spectator, or a lobby before spawn) previously relied on seeing the world at the far update rate, install an `IInterestPolicy` that gives it a focus — the old default behavior for a pawn-less client is gone.
- If you read `ContainerOwnershipMsg` directly (a custom client implementation, not the stock `NebulaClient`), update it for the new per-client delta format: track `Full`/upserts/removes instead of assuming a complete table on every message.
- A game using the hand-rolled runtime-grid pattern from the previous `infinite-runtime-world` tutorial (a local `RuntimeGrid` field, a manual `RuntimeGridAllocator`, and a scene component polling `ContainerRegistry.Runtime`) can migrate to `NebulaConfig.ChunkedWorld` and `NebulaChunks` — see [Build an infinite runtime world](https://nebula.1by3.co/docs/guides/infinite-runtime-world). This is optional; the manual pattern still works.
- A world built with a hand-rolled chunk ID scheme such as `x << 32 | z` does not match `RuntimeGrid`'s pinned 3×21-bit packing used by `NebulaChunks`. Migrating to `NebulaChunks` changes a chunk's container ID, so persisted entities keyed by the old ID will not be found under the new one. Reset persistence (`nebula start --reset-persistence`, or delete the local database) once when migrating an existing world.

**Behaviour that changed after the first integration runs:**

- **A client can no longer lose its pawn when its authority moves between workers.** A gateway now follows an
  entity onto its new worker the moment the old one redirects it, and a welcomed client whose pawn the gateway
  cannot place — because the record is missing, its owner is gone, or a chain of handovers outran the gateway's
  dialling — has that pawn subscribed **by name** on every live worker until whichever worker holds it answers.
  If nobody claims it within five seconds the client is told its join is starting again and is given a new pawn,
  instead of staying connected with no body. An entity a connected client owns is also never dropped when a
  gateway lets go of a worker link, never forgotten on an `EntityForget`, and never the reason a link is dropped.
- **A client is sent the ground under every focus it has, not only under its pawn.** Container ownership rows
  were scoped to a window around the pawn, so a `FocusMode.Free` camera — or a policy focus, or a box focus —
  could be streamed the entities at the place it was looking without ever being told the chunks they stand in
  existed. Empty chunks were invisible to it entirely, because nothing but a lease row names a chunk with no
  entity in it, and a strategy camera looks mostly at empty ground. The window now follows every focus the
  evaluation authorized, deduplicated where they overlap, and is refreshed once per evaluation rather than only
  when an entity entered or left a set. A focus nobody authorized still contributes nothing, no focus can
  produce another instance's row or a container the gateway holds no lease for, and the ordering rules are
  unchanged: rows arrive before the spawns that name them and are taken back only after the despawns. The work
  is bounded by `NebulaGateway.ContainerRowCap` (derived from `InterestRadius`, the world cell size and
  `InterestMaxFoci`; reaching it is counted in `ContainerBudgetHits` and warned about once per client), and a
  box focus wider than `InterestMaxRadius` on any axis is shrunk to it about its centre rather than walked cell
  by cell. Chunk *allocation* is unchanged and stays server-side: a client camera can be shown any chunk the
  mesh already holds, and can never cause one to be created.
- **`InterestMaxRadius` is no longer switched off by a value of 0.** It is the ceiling on what a prefab's
  `RelevanceRadius` may ask a gateway to send, so a non-positive value is reported by `NebulaConfig.Validate`
  and replaced with `InterestRadius` rather than read as "uncapped". A game that set it to 0 to disable the cap
  must now set an explicit large value instead.
- A gateway now sends a container's ownership row before relaying an entity spawn that names a container the
  client does not hold yet (an authority handover into a new chunk), instead of leaving the client holding the
  entity at its last known pose.
- **A gateway caches a carried entity where its carrier is, not where its own prefab says** (design D82). The
  gateway's cached `Placement`/`Region` per entity record were still taken from that entity's own
  `AlwaysRelevant` and `RelevanceRadius` and never read back from the index after the carrier link was made, so
  they disagreed with the index for every passenger. Two things followed from that. The eviction sweep skipped
  an always-relevant or wide passenger entirely — the record was cached for the rest of the session once the
  region it rode in stopped being subscribed, because a worker deliberately sends nothing on unsubscribe — and
  an arriving passenger was offered to *every* connected client instead of only the ones whose subscribe discs
  cover its carrier's region. The gateway now reads the effective placement back from the index whenever
  anything can move a subtree (indexing, boarding, disembarking, a nested carrier change, a carrier arriving
  after its passengers, a rebucket, a carrier being removed), applies it to the whole subtree, and evicts a
  passenger with its root carrier. `CarrierLink.Cycle` is logged once per entity and changes nothing;
  `CarrierLink.UnknownItem` re-indexes the record rather than leaving a row no scan can find. A passenger's
  `InterestGroup`, owner and explicit subscriptions remain its own and are never inherited.
- **A carrier arriving after its passengers, or being removed from under them, is now published** (design
  D83). `InterestIndex` re-seats a subtree by itself in both cases, and the worker announced neither. A
  passenger whose container names a carrier that does not exist yet sits on its own placement until it does,
  so an `AlwaysRelevant` one is sent to every gateway in the mesh; when the carrier finally arrived the index
  quietly moved the whole pending subtree into the carrier's placement and the distant gateways were never
  told to forget it, caching it for the rest of the session. Removing a carrier is the mirror: every
  surviving passenger gets its own placement back — global again, or matched on its own reach again — and
  nothing published the spawns and forgets that implies. Both are now captured as a `CarriedTransition` and
  published for the whole subtree, in the usual order: the carrier is announced before the passengers that
  newly reach a gateway, and forgets go out contents-first. A gateway that owns a passenger or subscribed to
  it by id is still never told to forget it, and the removed carrier's own despawn is not duplicated.
- **Handing a ship and its passengers to another worker no longer publishes them as orphans** (design D85).
  `TransferAuthority` hands a carrier over first and then recursively hands over its contents, so between the
  two steps the carrier had already left the old worker's index while its passengers had not — and D83 read
  that as a destruction, restoring each passenger's own placement and publishing it. An `AlwaysRelevant` or
  wide passenger was therefore spawned to gateways nowhere near the ship, which then received neither a
  redirect nor a forget (the entity was no longer this worker's) and cached it for the rest of the session.
  A handoff is not a destruction: the passengers that are following the carrier are now excluded from that
  publication and are announced by the **new** owner, where the ship actually is. Contents that genuinely stay
  behind — a pinned interior, or anything this worker is not the authority for — are orphaned exactly as
  before. Carrier-first transfer and spawn order, contents-first forget order, sticky masks, redirects and the
  late-carrier-arrival reseat are unchanged.
- **A passenger that survives its carrier is put down somewhere that exists** (design D86). Restoring an
  orphan's placement and republishing it was not enough on its own: the spawn still named
  `ContainerRef.Dynamic(…)` for the carrier that had just been removed, so `NebulaGateway.ScopeContainer`
  could not resolve it and `CanObserve` failed closed. The newly eligible gateway ingested the spawn, cached
  the record, and could show it to no client. Removal now evacuates first — every direct passenger of a
  departing entity is moved into the container that entity itself sat in, keeping its **absolute world pose**
  — before its restored placement is published. Anything deeper keeps naming a carrier that is still there, so
  every spawn of a surviving subtree names an existing container, carriers before contents. Gateways that
  already hold the survivor learn the new frame through the ordinary container-change path (a reliable
  `Location` state entry on the next tick). Survival is automatic: a game does not have to notice that a ship
  exploded and re-place what was inside it. `ScopeContainer` and `CanObserve` are unchanged — an unresolvable
  dynamic container still fails closed.
- **A failed handoff can no longer corrupt the next one** (design D87). The follower set D85 introduced was
  held by a depth counter that was incremented before persistence, the network sends, the
  `AuthorityHandedOff` callback and the recursive transfers, and decremented only on success. A throwing
  persistence store, handover subscriber or nested transfer therefore left the worker permanently inside a
  handoff: the next top-level handoff skipped its own collection, suppressed the wrong entities and published
  its real passengers as temporary orphans, and the pooled contents snapshot the failed recursion borrowed
  was never returned. The bookkeeping now lives in a shared `HandoverScope` whose frame is taken in a `using`
  and released on every path out, borrowed content lists are returned in a `finally`, and the original
  exception is still propagated untouched. The suppression itself moved into `CarriedTransition.Resolve`, so
  the worker and the service test fixture share one implementation of it rather than each filtering its own
  publish loop; carrier-first ordering, pinned interiors and the unbounded carrier depth are unchanged.
- **The "no carrier depth limit" guarantee is true end to end** (design D84). `CollectCarried` lost its cap in
  this release, but nine other carrier-chain walks still stopped at 8 or 16 hops, each failing differently and
  silently: `NebulaWorker`'s subscription snapshot dropped every entity deeper than 8 from a newly subscribed
  region, `ClientInterest`'s spawn/despawn ordering compared every level past 16 as equal (so a spawn could
  arrive before the container it names), the gateway's `WorldPosition`/`WorldRotation`/`ContainerPosition`
  left a deep coordinate in an intermediate carrier's frame while using it as a world position, and
  `ScopeContainer` — the instance isolation boundary — gave up partway up the chain. Every one of them is now
  bounded by the number of entities or containers that can be in a chain rather than by a constant; carrier
  cycles are still refused where they are created, which is what makes that bound safe. The coordinate walks
  are iterative rather than recursive, and nothing new is allocated per tick. `NebulaWorker.MaxNestingDepth`
  is **removed**: it was the ceiling of those loops and there is no longer a constant to disagree with the
  guarantee. New public member: `InterestIndex<T>.DepthOf(netId)`, the carrier depth of one item.
  `InterestIndex<T>.HasCarried` and `CollectCarried` now also answer for a carrier that is not in the index
  yet, which is how a pending subtree is found.

## [0.1.0-alpha.28] and earlier

See the [GitHub releases](https://github.com/1by3/nebula/releases) for the history before this file was introduced.
