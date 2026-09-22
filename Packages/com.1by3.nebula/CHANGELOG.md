# Changelog

All notable changes to this package are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Breaking: protocol 17 → 18, the contracts milestone

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

**What activation does not do:** it does not decide who may enter, does not load content (that stays with `InstanceScenes` and the `PrepareTransfer` handshake), and does not retire anything. `RemoveScope` removes a scope's row, claim and lease rows without draining it; idle retirement and restore are a later item.

#### Conformance suite

A deterministic test suite for Nebula's cross-worker guarantees, run with `Tools/conformance.ps1` (`-DotnetOnly` for the pure C# tier while the Editor is open). Every test is tagged `[Category("Conformance")]`; the script runs the category in `Nebula.Services.Tests` (`dotnet test`) and in `Nebula.Tests.EditMode` (Unity batchmode), and prints one PASS/FAIL summary with counts. Design and scenario ledger: `docs/conformance-suite.md`; user page: [Run the conformance suite](https://nebula.1by3.co/docs/guides/conformance-suite).

- `ConformanceMesh` (`Tests/EditMode/ConformanceMesh.cs`): two or more real `NebulaWorker` components in one Editor process, each with a recording transport and peer records for the others; `Pump()` delivers every recorded message into the receiving worker's own `Dispatch`. Handovers here run the production builder, wire format and applier end to end, with no gateway, leases or tick loop.
- Covered in this release: scenario 1 (the location contract, `ConformanceLocationTests`), scenario 2 (scope activation, `ConformanceScopeActivationTests` and `ConformanceScopeRoutingTests`), scenario 5 (the cross-worker call contract, `ConformanceCallContractTests`), scenario 6 (historical state, `ConformanceStateHistoryTests`), scenario 8 (server-driven handover state, below), scenario 9 (the cross-container joint diagnostic, `ConformancePhysicsDiagnosticTests`) and scenario 10 (the persistence durability window, `ConformancePersistenceDurabilityTests`). Scenarios 3, 4 and 7 wait for their items.
- Scenario 8, covered: a server-driven entity with `WriteHandoverState`/`ReadHandoverState` state, NetworkVariables and a `NetworkTransform` crosses workers and keeps every field, bumps its epoch by one, fires `OnLostAuthority`/`OnGainedAuthority` once each in order, and ignores a replayed transfer (`ConformanceHandoverStateTests`). The wire leg (`ConformanceHandoverWireTests`, every `AuthorityTransferMsg` field) also compiles into the service tests.
- Tagged into the suite: `ControlPlaneAndRpcTests.HandoverStateRoundTripsPerBehaviourAndIsolatesFaultyChunks` and `PersistenceTests.TheKeyTravelsWithTheHandoverSoTheNextWorkerUpdatesTheSameRecord`.

No runtime behaviour changed.

#### Cohesion hints

The game can now declare that a set of entities must be simulated by one worker and move between workers as a unit, and that a container should not be rebalanced for a bounded time. See [Cohesion hints](https://nebula.1by3.co/docs/guides/cohesion); the design record is `docs/cohesion-hints.md`.

**Wire (protocol 18, no further version bump):** `EntitySpawnMsg` gains a trailing `u32 cohesion_group` (0 for none), written last in the body. It therefore travels in `EntitySpawn`, `GhostSpawn` and `AuthorityTransfer`, so every process holding a copy knows which group the entity is in.

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

### Added

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

### Changed

- The worker's per-tick pose recording now records only entities it has authority over; a ghost records itself when the owner's stream is applied, tagged with the owner's tick instead of the receiving worker's. Before, every copy was recorded with the receiver's tick, so a ghost's history was silently off by a tick and an interpolating ghost recorded a smoothing artefact rather than the pose the owner reported.
- A worker sends an entity's `GhostVars` update ahead of that entity's state entry for the same tick, so a `[SyncHistory]` variable is snapshotted with the values the tick's stream carries. Nothing else depends on the order of those two messages.
- `NetworkIdentity.TryGetPoseAt(tick, out position, out rotation)` keeps its signature and behaviour (false plus the current pose) and is now a wrapper over `StateAt`. The constant `NetworkIdentity.PoseHistoryTicks` (64) is **removed**: the window is `NebulaConfig.StateHistoryTicks` (default 32) and the constant behind it is `StateHistory.DefaultWindowTicks`.

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
