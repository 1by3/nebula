# Changelog

All notable changes to this package are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Breaking: protocol 17 → 18, cross-worker call contract

Every Nebula process must be rebuilt and restarted together. A gateway disconnects a client whose protocol version is not exactly `18`. See [RPCs and worker messages](https://nebula.1by3.co/docs/guides/rpcs#what-nebula-promises-for-a-cross-worker-call) and the [wire protocol specification](https://nebula.1by3.co/docs/specifications/wire-protocol#authority-calls); the design record is `docs/cross-worker-calls.md`.

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
