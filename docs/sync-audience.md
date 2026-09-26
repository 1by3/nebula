# Sync audiences — design

Status: design of record for **NEB-321**. Wire protocol **20**, additive for clients (the window is 19..20); one new
worker-to-gateway message (`SyncAudience`, 37), trailing fields in `EntitySpawnMsg` and `EntitySyncMsg`, and new bits
in a sync chunk's flags. Decisions made without asking are marked **D#**. Builds on the sync channel
(`NetworkBehaviour.WriteSyncState`/`ReadSyncState`, `SyncStateCodec`), `docs/interest-management.md` (the gateway's
per-client sets and its entity cache) and the session model in `website/content/docs/guides/authentication.mdx`.
User-facing page: `website/content/docs/guides/sync-audiences.mdx`. Tests: `Tests/EditMode/SyncAudienceTests.cs`
(worker and client, conformance scenario 27) and `Services~/Nebula.Services.Tests/ConformanceSyncAudienceTests.cs`
(the gateway).

## 0. Problem

A behaviour's sync state went to every client that had the entity in its interest set. A game could not keep state
private: a player's inventory, the contents of a container only its user has open, an NPC's hidden intent. Interest
management decides *which entities* a client holds; it has no say over *which parts* of an entity. RPCs have
`OwnerRpc`, but state that must be current for whoever joins or reconnects belongs in replicated state, not in a
stream of calls.

## 1. The surface

```csharp
public enum SyncAudience : byte { Everyone, Owner, WorkersOnly, Custom }

// NetworkBehaviour
public virtual SyncAudience SyncAudience => SyncAudience.Everyone;            // read once per entity
protected virtual bool IsInSyncAudience(ulong clientId, NetworkIdentity pawn); // Custom: asked on the authority
public virtual uint SyncAudienceRefreshTicks => 30;                           // Custom: 0 = only when marked
protected void MarkSyncAudienceDirty();                                        // Custom: ask again next tick
public virtual void OnSyncStateCleared();                                      // client: you left the audience

// NetworkIdentity
public uint SyncAudienceGeneration { get; }
public bool HasCustomAudience { get; }
public bool HasRestrictedAudience { get; }
```

## 2. Decisions

**D1 An audience restricts clients, never workers.** Every worker that holds a ghost receives every behaviour's state
whatever its audience, in `GhostSpawn`, `GhostSyncState` and `AuthorityTransfer`. Cross-worker game logic reads those
copies (a hit test against a ghost, an interaction, the next authority after a handover, which starts from the
spawn data), and a worker is trusted anyway. `Owner` and `Custom` therefore mean "the workers, plus these clients".
A game that needs state hidden from other workers too has no use case here and would have to keep it out of the
sync channel.

**D2 `WorkersOnly` never reaches a gateway.** The worker leaves `WorkersOnly` behaviours out of everything it writes
for a gateway (`WriteSyncState(..., forGateway: true)`, `WriteSyncSnapshot(..., forGateway: true)`,
`EntitySpawnMsg.From(..., forGateway: true)`), which also saves the bandwidth. The gateway drops a `WorkersOnly` chunk
anyway if one arrives, so the guarantee does not rest on one side alone.

**D3 The audience is configuration.** `SyncAudience` is an overridable property read once, in
`NetworkIdentity.Initialize`, into `NetworkBehaviour.Audience`. It is not state: it does not travel in a handover and
cannot change at run time, because every process must agree on it for the entity's whole life and a change would
need its own join and leave protocol. An entity's root `NetworkTransform` is forced to `Everyone` with a warning: the
root pose is the entity's place in the world, and interest, carriers and every client's view of the entity are built
on it (it rides `WorldState`, not the sync envelope). A child `NetworkTransform` or a `NetworkAnimator` can be
restricted like any behaviour.

**D4 Filtering happens at the gateway, on every path a chunk takes to a client.** Each chunk carries its audience in
flag bits 1-2 (`SyncStateCodec.ChunkFlags.AudienceMask`); zero is `Everyone`, so an unrestricted chunk is byte for byte
what it was before. The gateway is the only process that sees both the chunk and the individual clients, so:

- **Deltas and keyframes** on both channels (`OnEntityState`): if no chunk in the message is restricted it is relayed
  exactly as before. Otherwise each observer is sent the chunks it may have; observers that may have only the
  unrestricted ones share one message built once (`BroadcastSyncFiltered`).
- **The spawn a worker sends for an entity clients already hold** (a handover, a container change) is filtered per
  observer (`BroadcastSpawnFiltered`).
- **The cached keyframes a late joiner, a new subscriber or a reclaimed session starts from**: the cache
  (`EntityRecord.SyncKeyframes`) keeps each keyframe with its audience bits and generation, and the spawn a client
  gets is assembled from it per client (`CachedStateFor`). A non-member's spawn simply lacks the chunk.

A member is: anyone for `Everyone`; the client whose session id equals the entity's `OwnerClientId` for `Owner`; a
client in the behaviour's member set for `Custom`; nobody for `WorkersOnly`. A `Custom` behaviour with no member set
yet admits nobody (fail closed).

**D5 `Custom` is evaluated on the authority, over the pawns it holds, when due.** The predicate is game code and only
the authority runs game code for an entity, so the authority computes a member set and ships it to the gateways. Its
candidates are the clients whose pawn this worker holds, simulated or ghosted (`NebulaWorker._players`): everyone
near enough to see or touch anything the worker simulates, and it hands the predicate the pawn so a distance rule
costs nothing extra. A client whose pawn the worker does not hold is not asked and is not a member.

A behaviour is evaluated when the entity gains authority, when `MarkSyncAudienceDirty` was called (at the next tick,
at most once per tick), and every `SyncAudienceRefreshTicks` ticks (30, half a second at 60 Hz; 0 turns the refresh
off for rules that change only on events). The cost is one predicate call per candidate per due behaviour, and
nothing at all for entities without a `Custom` behaviour. Evaluation runs in the tick after simulation and before
anything is written (`NebulaWorker.EvaluateSyncAudiences`), so a set and the chunks it decides are always from the
same tick.

A set holds at most `SyncAudienceCodec.MaxMembers` (256) session ids, kept sorted; past that the lowest ids are kept
and a warning is logged once per behaviour. Sets are sent whole on change, 8 bytes a member (about 2 KB at the cap),
so the cost scales with how often an answer changes, not with ticks. At the gateway membership is a binary search per
chunk per observer, only for entities that have restricted behaviours.

**D6 A generation keeps a new chunk away from an old set.** A set change goes out reliably, ahead of that tick's
chunks, as a `SyncAudienceMsg`. A reliable chunk cannot overtake it, but a sequenced one can: a keyframe written after
Bob left could reach the gateway while it still lists Bob. So every entity has a `SyncAudienceGeneration`, raised on
every change, and every sync message of an entity with a restricted behaviour is stamped with it (a trailing `u32`;
entities without one pay nothing). The gateway sends nobody a `Custom` chunk stamped with a generation newer than the
sets it holds; it still caches it, and a client that joins when the sets arrive gets it. The generation travels with
the entity (D7), so it only ever goes up and the comparison holds across handovers. `Owner` chunks need no guard:
ownership only changes with a new epoch, which the epoch check already fences.

**D7 Handover carries the generation and the sets; the new authority asks again.** Both ride `EntitySpawnMsg`
(`AudienceGeneration`, `Audience`), so `GhostSpawn` and `AuthorityTransfer` carry them. The receiving worker adopts them
(`ApplyCarriedAudience`) and marks every `Custom` behaviour dirty when it gains authority: if it reaches the same
answer nothing changes for any client; if its candidates differ (it holds other pawns), the change goes out as usual
with the next generation. The entity body nested in `AuthorityTransfer` always carries the section, because fields
follow it there (`EntitySpawnMsg.WriteBody(w, embedded: true)`).

**D8 Joining: a keyframe at once, and a fresh one right after.** When a client joins an audience the gateway sends it,
reliably, the newest keyframe it caches for the behaviour, so it has state within one round trip. The worker also
forces a keyframe of every behaviour whose set changed (`NetworkBehaviour.AudienceKeyframe`), because a cached
keyframe of a reliable behaviour can be older than the deltas that followed it. For `Owner`, joining is a spawn: a
reclaimed session is a new connection with the same session id, and its interest set is rebuilt from spawns filtered
by D4, so it gets the owner chunks from the cache. The worker additionally forces keyframes of everything a
reclaiming session is in the audience of (`NebulaWorker.OnSpawnPlayer` → `ForceAudienceKeyframes`). An owner change
announced by a spawn (a new epoch naming another owner) makes the new owner's filtered spawn carry the chunks.

**D9 Leaving: an explicit notice and a callback.** A client that leaves an audience it was in is sent a reliable
`EntityState` with one empty chunk per behaviour it left, flagged `Cleared`. The client calls
`NetworkBehaviour.OnSyncStateCleared` instead of `ReadSyncState`, so the game can drop the state and clear UI. A
sequenced chunk the gateway relayed before the leave can arrive after the notice (the two channels are not ordered
against each other); the notice is stamped with the newest sync tick the gateway relayed for the entity, and the
client drops chunks of that behaviour from sequenced packets no newer than it until a keyframe arrives on the reliable
stream (a rejoin). Leaving the entity itself (a despawn) is not an audience leave: `OnNetworkDespawn` covers it.

**D10 Protocol 20, additive for clients.** Client-visible: the audience bits (a protocol-19 client reads only `Full`)
and the `Cleared` chunk, which the gateway sends only to clients that negotiated 20 (the first field written by
negotiated version). A protocol-19 client that leaves an audience is not told, but it is sent nothing more of the
state. Between workers and gateways, which must match exactly: `MsgId.SyncAudience` (37), the trailing
`audience_generation` and `audience_sets` of the spawn body, and the trailing `audience_generation` of the sync body.
The gateway never forwards any of the three to a client (`EntitySpawnMsg.ForClient`). `MinProtocolVersion` stays 19.

**D11 Owner means the session, and ownership does not move today.** Membership of `Owner` is
`NetworkIdentity.OwnerClientId`, the session id, which stays the same across a reconnection and a move to another
gateway, so the audience survives both without any bookkeeping. Nebula has no API that gives an existing entity to
another client: an owned entity is a player's pawn. The gateway nevertheless handles an owner change on any spawn it
receives (new owner gets the chunks with the spawn, old owner gets `Cleared`), so the rule is general, and
`AnOwnerChangeMovesTheAudience` pins it. A game that models "the player holding this" itself should use `Custom`.

**D12 What stays public.** `NetworkVariable`s are resent whole with the entity's other variables to every client that
holds it, and are not filtered: private state belongs in sync state with a restricted audience. `ClientRpc` is not
filtered by audience either (`OwnerRpc` exists for owner-only calls). Encryption and anti-cheat are out of scope: an
audience decides what the gateway sends, not what a compromised process can read.

## 3. What this does not do

- A `Custom` candidate must have a pawn on the entity's authority. A rule over arbitrary session ids ("everyone in my
  guild, wherever they are") only sees the ones nearby.
- The audience cannot change at run time (D3), and the root `NetworkTransform` cannot be restricted.
- A late joiner starts from the newest cached keyframe; for a reliable behaviour that keyframe can predate the deltas
  after it until the behaviour's next keyframe. That is how every late joiner has always started; D8 closes it for
  audience joins and reclaims, not for ordinary late joins.
- A gateway pays per-observer message building for entities with restricted behaviours on ticks they send them.
  Owner-only state on every pawn is therefore cheap (one member each); a `Custom` behaviour on hundreds of busy
  entities with large audiences is not free.

## 4. Tests

- `Tests/EditMode/SyncAudienceTests.cs`: chunk flags per audience, `WorkersOnly` never written for a gateway, the
  unchanged `Everyone` wire form, the wire round trip (standalone spawn, handover-embedded spawn, sync message), the
  `Custom` evaluation schedule and generation, the member cap, reclaim keyframes, the client's `Cleared` handling and
  its drop of stale sequenced chunks, the root transform rule. `ConformanceSyncAudienceTests` in the same file
  (scenario 27, real workers on the `ConformanceMesh`): every audience reaches a ghost; a gateway gets the sets before
  the chunks they decide and never a `WorkersOnly` chunk; generation and sets travel with a handover; a returning
  session gets a fresh owner keyframe.
- `Services~/Nebula.Services.Tests/ConformanceSyncAudienceTests.cs` (a real gateway over UDP): a non-owner never gets
  an `Owner` chunk by spawn, delta, keyframe or late-join cache; `WorkersOnly` reaches no client; an owner change
  moves the audience; the owner keeps its state through a handover and a reclaimed session; a `Custom` audience adds
  and removes clients (keyframe on join, `Cleared` on leave, a newer-generation chunk held back); (a protocol-19 client got no notice but no state, until protocol 21 closed the window to 20..21); an unrestricted entity is relayed as before.
