# Changelog

All notable changes to this package are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
- New message types: `InterestSubscribe`, `InterestResync`, `EntityRedirect`, `EntityForget` (gateway ↔ worker subscription protocol), and `ClientFocusHint` (client → gateway; hints unreliable, the `Clear` form reliable, both carrying a generation so a late hint cannot undo a clear).
- Workers no longer announce every authoritative entity when a gateway connects (`Hello`). A gateway now receives only the regions its clients' foci need, and links a worker only while it has a reason to (a subscribed region, a followed entity, or a first-spawn request for a joining client with no pawn yet).

**New `NebulaConfig` fields:** `InterestRadius` (120), `InterestExitMargin` (16), `InterestLingerSeconds` (1), `InterestCellSize` (64), `InterestPlanar` (true), `InterestEvalHz` (4), `InterestSubscribeMargin` (32), `InterestRegionLingerSeconds` (3), `InterestLinkLingerSeconds` (10), `InterestResyncSeconds` (30), `InterestMaxRadius` (1024), `InterestMaxFoci` (8), `InterestHintMaxDistance` (60), `InterestHintMaxHz` (5), `InterestMaxExplicitPerClient` (16), `InterestMaxFocusSpeed` (12), `PartitionWarnEntities` (2000), `PartitionWarnFilterMs` (2), `ChunkedWorld` (false), `ChunkPlanar` (true), `ChunkRetireSeconds` (30). New command-line overrides `-nebula-interest-radius` and `-nebula-interest-cell`.

**New public API:**

- `NetworkIdentity.RelevanceRadius`, `AlwaysRelevant`, `InterestGroup` — per-prefab interest overrides.
- `IInterestPolicy`, `DefaultInterestPolicy`, `InterestPolicies.Combine`, `InterestFocus`, `InterestQuery`, `InterestClient`, `InterestEntity` — the interest policy API (`Runtime/Interest/InterestPolicy.cs`).
- `NebulaGateway.InterestPolicy`, `SetClientTag`/`GetClientTag`, `MarkInterestDirty`, `InterestSetSize`, `CachedEntityCount`, `SubscribedRegionCount`, `WorkerLinkCount`, `InterestSettingsInUse`, `InterestGridInUse`, and the `InterestLinkReason` flags.
- `NebulaClient.FocusHint`, `ClearFocusHint`, `ReplicaCount`, `SpawnsReceived`, `DespawnsReceived`.
- `NebulaChunks` (`Loaded`/`Unloading` events, `EnsureAt`, `At`, `IsLoadedAt`, `CoordOf`, `SeedOf`) and `ChunkContent`, the turnkey chunked-world content API raised when `NebulaConfig.ChunkedWorld` is on.
- `Debug/InterestProbe`, a component that logs bounded-replica soak checks (`InterestProbe.Attach(client)`).
- `ContainerRegistry.Overlapping` (box query), used to scope a client's container ownership window.

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
- **`InterestMaxRadius` is no longer switched off by a value of 0.** It is the ceiling on what a prefab's
  `RelevanceRadius` may ask a gateway to send, so a non-positive value is reported by `NebulaConfig.Validate`
  and replaced with `InterestRadius` rather than read as "uncapped". A game that set it to 0 to disable the cap
  must now set an explicit large value instead.
- A gateway now sends a container's ownership row before relaying an entity spawn that names a container the
  client does not hold yet (an authority handover into a new chunk), instead of leaving the client holding the
  entity at its last known pose.

## [0.1.0-alpha.28] and earlier

See the [GitHub releases](https://github.com/1by3/nebula/releases) for the history before this file was introduced.
