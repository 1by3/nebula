# Portals — design

Status: design of record for **NEB-427**, protocol **v28** (new messages; the client window closes to 28..28, see §12).
The design was approved by Jesse, the project owner, on 2026-10-12, and implementation is on the branch `jesse/portals`:
§15 maps it onto NEB-428 to NEB-437. Decisions made without asking are marked **D#**.
Builds on `docs/scope-frames.md` (D11, D12: pinned instance frames, turned doors, NEB-426), `docs/scope-activation.md`
(D2, D13, D14, D19, D21, §12, §13), `docs/interest-management.md`, `docs/cohesion-hints.md`, `docs/entity-extents.md`
and `docs/state-history.md`. It supersedes `docs/scope-frames.md` §7's "seamless scope-to-scope travel" non-goal,
`docs/scope-activation.md` D24 ("rows only; no host entities") and §12 (`ObserveHost`), and decides NEB-232 (§6, D30).
§16 lists everything it replaces.

## 0. Problem

An instance is a scope (`docs/scope-activation.md` D2): its own containers, physics scene, frame and lifecycle. Its
only link to the world is `InstanceBoundary` (`Runtime/Containers/InstanceBoundary.cs`), and that link is thin:

- **Only player pawns cross by themselves.** `InstanceBoundary.Tick` returns for `OwnerClientId == 0`. A held item
  crosses as a companion (NEB-372), moved by one translation for the whole group, so a turned door does not turn it.
  Everything else needs a hand-written `PrepareTransfer`.
- **A crossing holds at the face.** Each crossing is a per-entity preparation (`InstanceTransfer`): the destination
  worker loads content, the owner's client acknowledges the row, and `Cross` puts the entity back where it last stood
  until both have answered. The preparation is fenced on the entity's epoch *and its source container*
  (`NebulaWorker.ValidTransfer`), so walking from one chunk to the next near the door starts it again.
- **Nobody sees through.** `NebulaGateway.CanObserve` passes an entity in the client's own scope, the carriers kept by
  `HostCarriers`, and public entities in an `ObservePublic` box, which only ever looks at scope 0. Outside, nobody sees
  into an instance; inside, nobody sees the host's entities (D24).
- **Nothing touches across.** One `PhysicsScene` per instance (`InstanceScenes.Prepare`), nothing mirrored, no query
  that continues into another scope. `ContainerRegistry.Link` refuses neighbours in different scopes, so the ghost band
  never offers anything across (`docs/scoped-chunk-grids.md` D11).
- **One turned view.** `InstanceViews.Begin` turns either each turned instance about its door, or, from inside one,
  every other scene root back about that door. Only one view scope is handled, and only by moving the world.

The docs state the goal as a non-goal: "continuous coordinates across scopes, seamless scope-to-scope travel"
(`docs/scope-frames.md` §7), "entities do not ghost, interact or see across scopes" (`docs/scoped-chunk-grids.md` §7).

A game needs the opposite: a space behind a door that is part of the world. You see into it and out of it while the
door is open; anything you carry, throw, drive or shoot goes through; things rest against the doorway from both
sides; and nothing hitches. The scopes stay what they are (isolated simulation, their own frame, their own lifecycle and
persistence). What changes is the link between them.

## 1. Names

**D1 The public type family is `Portal`, decided by the project owner.** Prose says "portal", "coincident portal" or
"offset portal" (D4), and "the network gateway" for `NebulaGateway`. The link between two scopes is not called a gateway
because Nebula already has one: the network edge process (`Runtime/Gateway/`, `NebulaGateway`,
`docs/gateway-fleet-audit.md`, the CLI and the dashboard), and its partial classes are already named `GatewayInterest`,
`GatewayTransfers`, `GatewayFarInterest`. A public `Gateway` component or `GatewayQueries` class would sit in the same
namespace as those and be read as part of the network edge in every log line, stack trace and search. The
earlier proposal prefixed the family with `Scope` and kept "gateway"; `Portal` avoids the clash and is shorter. So:

| Type | What |
|---|---|
| `Portal` | the component on the near side's content: faces, aperture, far side, hooks |
| `PortalFace`, `PortalAperture` | value types: a face's pose and opening |
| `PortalLink` | one party's link through a portal: far scope, state, wait reason |
| `PortalState`, `PortalWait` | the link's state and why it is not open |
| `Portals` | static: lookup, link poses (`Map`, `Turn`, `PoseOf`), rendering-layer slots |
| `PortalQueries` | `Raycast`, `Sweep`, `Overlap` across an open link |

One private test class named `Portal` already exists, in `Tests/EditMode/ConformancePhysicsFrameTests.cs` (an
`IFrameCrossingPolicy`); NEB-428 renames it to avoid confusion.

Rejected: renaming the network gateway (public API, CLI verbs, cloud and docs; a break for every user to name a feature
most of them will not use); `Doorway`, `Passage` (a hangar roof or a window is neither). The planning documents and
issue titles that say `Gateway` and `GatewayQueries` predate the name. A game's own types (a door component built on
this) can be named as the game likes.

## 2. The portal: faces, aperture, link pose

```csharp
var portal = door.GetComponent<Portal>();
portal.Id = "north-door";                       // unique within its container
portal.Aperture = PortalAperture.Box(halfWidth: 1.5f, halfHeight: 2.2f);
portal.FarTemplate = interiorTemplate;          // far scope created on demand per party (D5)
portal.Open(party: "crew-91", members);         // worker: prepare, then open when ready (§3)
```

**D2 A portal links two faces, and each face is fixed in a container.** A face is: the scope, the container it is
fixed in (a chunk, a carrier, an instance part), and its pose in that container's own space, a `Double3` position and a
rotation. The near face is where the `Portal` component stands, read as `InstanceBoundary.AbsolutePosition` reads
itself today (out of any physics frame, through the container's scope frame, in double). Both faces use one convention:
the face's +z axis is the way from the near scope into the far one. In either face's axes, z < 0 is the near side's
region and z ≥ 0 is the far side's.

> **As built (NEB-428).** `PortalFace` holds the scope, the id of the container the face is fixed in, the origin **absolute in the scope's own
> space** (read out of any physics frame, in double, rounded to 1 mm) and the rotation (normalised, in the hemisphere w ≥ 0, rounded to 10⁻⁶, and the
> identity within `NebulaWorker.PoseAngleEpsilon`). It is not stored in the container's own space, because every crossing compares in the scope's
> absolute space and a face in a framed container would have to be converted into it on every test; the container is kept for ownership, for the
> derived id (D6) and for the network gateway's rows. A face fixed in a *moving* container (§17) is still not built. `Portal.NearContainer` is the
> nearest `Container` among the component's parents (a chunk, an instance part, a carrier) or, for content in the shared scene, the container that holds
> its position.

**D3 The aperture is an oriented box in the face's axes, centred on the face's origin, the opening on its z = 0 plane.**
`PortalAperture` holds the half extents across the opening (x, y) and a `Kind`, `Box` today; any convex shape is
a later kind with the same contract (a cross-section test and a bounding box). Three regions derive from it, each in
face axes:

| Region | Shape | Used by |
|---|---|---|
| cross-section | the opening: \|x\| ≤ hx, \|y\| ≤ hy, z = 0 | crossing (§4), queries (§7) |
| band | cross-section grown by `BandMargin` sideways, \|z\| ≤ `BandDepth` (2 m) | proxies (§6), cross-worker ghosting (D30) |
| window, per side | cross-section grown by `WindowMargin` sideways, extruded `WindowDepth` into that side | observation (§5) |

> **As built (NEB-428).** The aperture's three regions are carried on the wire (`PortalRecord`: half extents, `BandMargin`/`BandDepth`,
> `WindowMargin`/`WindowDepth`) and `Portal` has the sizes as `BandMargin`, `WindowMargin`, `WindowDepth` (fields) and `BandDepth`, `CrossDepth`,
> `CloseTimeoutSeconds`, `ApproachDistance` (constants). Today the cross-section decides crossing and the band decides when a closing link may close; the
> windows are carried and unused until NEB-431.
>
> **As built (NEB-431).** The window is a public value type, `PortalWindow` (`Runtime/Protocol/PortalWindow.cs`), built from a `PortalRecord` alone: `PortalRecord.NearWindow` and
> `PortalRecord.FarWindow(farScope)`, so the network gateway, which runs no game code, and a client compute the same box. It is closed (a point on its boundary is inside), measured in
> its own face's axes in double, and empty when `WindowDepth` is 0. A template's record now carries its far face (the near face's origin and the identity rotation, or the near face's
> own with `TurnWithFace` off), because a window is measured in the far face's axes; it is the same for every party's far scope, so it is one record and not one per link. The sizes
> are `Portal.WindowMargin` and `Portal.WindowDepth` (8 m and 48 m), carried in the record since NEB-428.

**D4 The link pose is derived from the two faces, never stored on its own.** With N the near face's pose in the near
scope's space and F the far face's in the far scope's, a point of the far scope maps into the near scope as
`p_near = N.Origin + (N.Rotation × F.Rotation⁻¹) × (p_far − F.Origin)`. Two kinds follow, and neither is a flag:

- **Coincident portal**: both faces' origins are the same absolute point (to `InstanceBoundary.OriginResolution`, 1 mm).
  The far scope is built in the same place as the door, inside the near side's solid mass, which is how every instance
  is placed today. A rotation difference is D12 of `docs/scope-frames.md` (a door turned on a sphere, an interior built
  upright): the link is a pure turn about the door.
- **Offset portal**: the origins differ, so the link carries a translation too. The data model above carries it from the
  first version; NEB-437 (milestone 7) makes everything that reads a link pose honour a translation.

**D5 The far side is a fixed scope or a template.** A fixed far side names a scope key and the far face's pose in that
scope's space (an offset portal into a place that exists anyway, or the next floor of a fixed building). A template far
side is `InstanceBoundary`'s rule unchanged: the far scope's key is `TemplateId + "/" + party`, prepared with
`PrepareInstance(template, key, nearFace.AbsoluteOrigin, nearFace.PoseRotation)`, so it is coincident by construction and
its far face is the near face's absolute pose read in the instance's upright space. Keeping the derivation byte for byte
is what lets a game find every instance it has already persisted; that rule stands on its own (D7).

> **As built (NEB-428).** The far scope is the template's `TemplateId + "/" + party`, prepared with `PrepareInstance(template, key, nearFace.Origin,
> PoseRotation)`; `Portal.PoseRotation` is the near face's rotation, or the identity when `TurnWithFace` is off (the old `TurnWithDoor`). The far face
> of a template is the near face's origin with the identity rotation (or, with `TurnWithFace` off, the near face's own rotation, so `Turn` is
> the identity). A crossing enters the template's **first part** (the entry part `PrepareInstance` returns first), as
> `InstanceBoundary` did; for a fixed far side it is the container of the far scope that holds the point 25 cm inside the far face. The deepest container
> holding the mapped point (D16) waits for NEB-431's window, because a member client is warm only for the entry container.

**D6 A portal's id is derived, not allocated.** `PortalId = ScopeKeys.Hash(containerId + "#" + Id)`, where `Id` is
authored and unique within the near container (a second component with the same id is ignored and logged). Every
worker, network gateway and client computes the same id from what it already has, as `docs/scope-activation.md` D3 does
for container ids.

> **As built (NEB-428).** A second component with the same derived id is ignored (`Portal.IsIgnored`) and logged once; the first registered keeps the id.
> `PortalIds.Of`, `PartyHash` and `Link` are in `Runtime/Protocol/PortalMessages.cs` so the network gateway and the standalone services compute them
> without Unity.

**D7 `InstanceBoundary` is removed, not shimmed; decided by the project owner on 2026-10-12.** There is no
backwards compatibility and no incremental shipping: only Holoverse uses this code and it has no active users. The type
goes in NEB-428 and the game ports to `Portal` in the same pass. There is no `Volume` containment kind; every portal uses
a plane aperture (D3). What a game does instead: put a `Portal` with a template far side (D5) on its door, with the party
the game chooses (D8), and move the door onto the face, because a plane aperture is a stricter rule than the old
`Interior` box (§14, item 10). `Request` and `RequireRequest` become opening and closing the portal for a party,
`CrossingReady`, `TryGetPreparation` and `InstanceCrossingState` become the link's state and wait (§3),
`ResolveCompanions` and `CompanionLeftBehind` map onto D13, and `CanEnter` onto D11. D5's key derivation stays byte for
byte, so persisted instances are still found.

> **As built (NEB-428).** `InstanceBoundary`, `InstanceCrossingState` and `InstanceCrossingStatus` are deleted with no shim. `OrientedBox` (an internal
> pure-C# helper with its own tests, which the service tests compile) stays: D38's face-relative tests are the next user of an oriented box. The
> mapping a game ports by is in the changelog's *Removed* entry.

## 3. Parties, state and readiness

**D8 A portal has one link per party, and a party is an opaque string the game chooses.** The far scope, the open state
and the observers are per link. A fixed far side usually has one party, `""`. A template far side has one far scope per
party behind the same near face, which is what one door leading to a different room for each owner needs: nothing more
is required of the model for it. Nebula never parses a party, as it never parses a scope key.

**D9 A link moves Closed → Preparing → Open → Closing → Closed, on the near face's worker.** The game calls
`Open(party, members)` and `Close(party)` there; Nebula makes the other transitions:

| State | Entered when | Aperture (§6) | Observation (§5) | Crossing (§4) |
|---|---|---|---|---|
| Closed | start; `Close` once the band is clear | solid | none | none: refused |
| Preparing | `Open` | solid | none | none: refused |
| Open | every readiness condition (D10) holds | open for admitted entities | both ways | commits on the tick the root passes |
| Closing | `Close` | solid for entities entering the band | both ways | only bodies already straddling finish |

Closing waits for the band to be empty of bodies, at most `CloseTimeoutSeconds` (5 s): turning the aperture solid under a
straddling body would make PhysX push it out. Past the timeout the link closes anyway, each straddler resolves to its
root's side and is pushed out of the aperture box, and the overrun is logged. Every state but Open carries a
`PortalWait` reason (`FarScopeActivating`, `FarWorkerContent`, `FarEntitiesRestoring`, `ClientWarming` with the
client, `FarWorkerHandshake`, `BandOccupied`, `FarScopeRetired`, `NearContainerMoving`), so a door that will not open
always says why, and `PortalLink.Progress` (0..1) gives a door's UI something to show.

> **As built (NEB-428).** The link table keeps a link after it closes (its `Generation` counts openings), so a closed link can still say why and an
> occupant meets the aperture from inside (D17); a link is created by `Open` and lives as long as the component is enabled. `Open` on a closing link
> prepares it again at once and, as every condition still holds, opens it on the next tick without asking the clients again. A member added to an
> open link leaves the state `Open` and sets the *wait* to `ClientWarming` with `WaitClient`; every state but Open carries a wait, and Open carries
> one only for that case. An open link costs a lease-epoch check, a member check and, once a second, a lookup of its far scope; it does not poll content.
> `BandOccupied` was decided from the roots of the entities the worker simulates in either face's band until NEB-432, which tests bodies by extent (below). A
> link to a template with an empty party cannot open (`Error` says so) and a link whose far content failed stays in `Preparing` with
> `FarWorkerContent` and an `Error`: there is no wait reason for a failure.

> **As built (NEB-432).** `BandOccupied` is true while any body touches the band of either face (`PortalBodies.Touches`): by its **extent** when it has one (`docs/entity-extents.md`), by the
> **shapes of its colliders** when it has not, by its root when it has neither, for the bodies the worker simulates and for the ghosts it holds across a portal (D29). A ship whose hull is in
> the doorway holds a closing link open although its root is 10 m back (`PortalBandOccupiedTests`). A cached description of a body is trusted for `PortalBodies.RefreshTicks` (30) ticks and
> made again at once when its extent is set, changed or cleared.

**D10 Opening is readiness, and readiness is standing, not per entity.** A link turns Open when all of these hold:

1. the far scope is active and every container of it the far window touches is leased in an owning state
   (`IsScopeReady`, `docs/scope-activation.md` D7);
2. the far content of those containers is loaded on the far worker (`InstanceScenes.ContentState` is `Ready`, i.e. every
   `IInstanceContentLoader` has finished, NEB-367/368), and the near content of the near window on the near worker;
3. the far scope's persisted entities are restored (`docs/scope-lifecycle.md`);
4. both workers have built the band's throat proxies and aperture colliders (§6);
5. every member's client is warm: it holds the far window's rows, its own content loaders for them report `Ready`, and it
   has been sent the far window's entities. The client says so with `PortalWarm` (D45) after it has applied the
   network gateway's first full delivery for the window, which the network gateway marks.

This generalises the per-entity handshake (`InstanceTransfer.WorkerReady`, `ClientReady`, `WorkerContentPending`) into a
property of the link, held for as long as the link is open. It is what lets a commit load nothing (D16).

> **As built (NEB-428).** The conditions of D10 are all there, with these limits: the far window is the entry container (the one row the network
> gateway keeps pinned for a member), item 4 (proxies and aperture colliders) is true until NEB-432 builds them, and the "first full delivery for the
> window, which the network gateway marks" is the arrival of the entry container's row ahead of the link, since entities are not delivered through a
> link until NEB-431. A member client prepares the container (`InstanceScenes.Prepare`, then `ContentState`), and answers once with `PortalWarm`
> carrying the link's id and `Generation`; a warm for an earlier opening, from a non-member or for an unknown link is ignored by the network gateway
> and by the worker. When the far container belongs to another worker, readiness includes that worker's answer to an `InstancePrepare` carrying the
> link id (`FarWorkerHandshake`); the commit then relies on the ordinary handover, as D35 describes. The scope lifecycle treats a live link as interest
> in its far entry container (D43).

> **As built (NEB-432), condition 4.** It is real now. When conditions 1 to 3 hold, `Portal.Evaluate` asks `PortalPhysics.PrepareLink` (once per opening of the link), which builds, on
> this worker, the aperture collider of each side whose scene it holds (D28), the throat when it holds both (D27: the static colliders of each side's half of the band, copied into the other's scene,
> switched off until the link opens), a pool of proxies for the bodies near the face (D26), and runs `Portal.Validate`. Building takes a few milliseconds in one tick, so a failure is not a wait: the
> condition only fails while no scene can be built in, and then the link reports `FarWorkerContent`. When another worker holds the far container, its half is part of its answer to the
> `InstancePrepare` that carries the link id: it replies `Success` only after it has been told of the link (`MsgId.PortalAdjacency`, sent ahead of the preparation on the same ordered stream, D30) and its
> far aperture collider stands (`NebulaWorker.BuildFarSideOf`), otherwise the near worker asks again. Neither worker builds a static throat for a split link (see D27). Without play mode (EditMode
> tier B tests) there are no scenes and the condition holds trivially, as before.

> **As built (NEB-434), conditions 1, 2 and 5.** The "far window" of the conditions is real now. A link keeps **rows** (`PortalLink.Rows`): the entry, and then the
> nearest containers of the far scope that the far window overlaps (`ContainerRegistry.Overlapping` over the window's box in the far face's axes) that have a lease of
> their own (`Container.IsLeased`; a box inherited from its carrier is not a row), at most 16. They are worked out while the link prepares and **frozen while it is
> open**: a container that appears in the far scope later is not warm on any client, so it is not entered until the link prepares again, and when preparing again adds a
> row the opening counts anew (`Generation` goes up and the warm set is cleared, so the members hold it before the link opens). Condition 1 is every row leased in an
> owning state; condition 2 is every row loaded on the worker that owns it, which for a row another worker owns is that worker's own answer to an `InstancePrepare`
> carrying the link id (one for each such row; `FarWorkerHandshake` until all have answered); condition 5 is every member's client holding and preparing every row
> before it says `PortalWarm` (`PortalLinkRecord.FarRows`/`FarRowIds`, which the network gateway pins for a member). The lease epoch of every row is kept from the tick the
> link became ready: a lease that moved under any row makes the link prepare again. A worker that owns only window ground (not the band) is told of the link in the new
> trailing list of `PortalAdjacencyMsg` (`FarRows`) so it can answer, and ghosts nothing for it. A scope that is one container, which is every template of one part, has
> one row, the entry, and behaves exactly as it did.

**D11 Three hooks, on the near worker, keyed by party.** `PartyOf(entity)` says which party an entity belongs to (default:
the owner's identity when it has an owner, as `InstanceBoundary.Personal`; null for a server-owned entity).
`CanEnter(entity, link)` decides admission (default: admit while the link is Open when the entity is server-owned or its
owner is a member). `CanObserve(clientId, link)` decides who may look through (default: the members). Observation
is decided when a member is added, not per message: the near worker publishes each link's observer list (client ids,
or `Anyone` for a door any passer-by may look through), and the network gateway enforces the list without running game
code, which it cannot do as a standalone service (`Services~`).

**D12 Admission is by open state, and a party is resolved in a fixed order.** An entity whose root passes an open
link's aperture belongs to that party's far scope. The link is chosen by: `PartyOf(entity)` when it names a party with a
link on this face; otherwise the face's one Open link, when there is exactly one; otherwise the entity is refused with
`AmbiguousParty`. A player's crate thrown through their own open door goes into their scope; a crate with no owner at a
door with two parties' links open goes nowhere unless the game says whose it is. A stranger's crate is refused by the
default `CanEnter` and meets the aperture (D17).

**D13 Members are the clients that must be warm; observers are the clients that may look.** `Open(party, members)` names
the members; `AddMember` and `RemoveMember` change them while the link lives. A member added to an open link warms on
its own (its wait is `ClientWarming`) without closing the link for the others, and its own entities are refused at the
aperture until it is warm, so it never loads at the face either.

> **As built (NEB-428).** The default `CanEnter` admits a server-owned entity, and an owned one only when its client is a member of the link **and
> warm** (D13). `CanObserve(clientId, link)` is asked when a client becomes a member, and `Portal.ObserveAnyone` marks a door anyone may look through;
> both are published in the link record, `NebulaGateway.PortalObserves(clientId, linkId)` answers from them, and nothing consults them to decide
> visibility until NEB-431.

## 4. Crossing

**D14 Anything whose root is the root of its carrier chain crosses.** Every authoritative, spawned entity that is not an
authored scene entity, not pinned (`docs/frame-bodies.md` D8) and not carried crosses on its own: player-owned and
server-owned alike (crates, projectiles, NPCs, vehicles). A carried entity crosses with its carrier and keeps its seat
(`docs/scope-activation.md` D19), and everything aboard is followed with it (D21 there). Companions (NEB-372) keep their
meaning, "crosses with its leader, not on its own", through `Portal.ResolveCompanions`: a held item is usually in
front of its holder and must not cross first. With the far side warm, a companion never waits for a preparation of its
own, so `CompanionWaitSeconds` has nothing to bound for a portal. Whether an NPC may follow a player through is
`CanEnter`'s answer, which is the game's; nothing in the portal stops it.

> **As built (NEB-428, NEB-429).** Every authoritative, spawned entity that is not an authored scene entity and not pinned crosses on its own, player-owned
> or server-owned; `Portals.Tick` asks the portals on the entity's container chain and those in the neighbours of the container it is in (a door beyond a
> chunk seam), and, for an entity in a far scope, the portals one of whose links leads into it. The index (portals by container, by far scope) is rebuilt once a
> worker tick, so an entity is never asked about a portal in a container it has nothing to do with (`Portals.Asks` counts it; the test pins 101 asks for 101
> entities and 50 doors in another yard). A passenger is not a candidate: walking up from the entity's container, a portal is asked only while no carried
> container lies below it on the chain, so a ship's passengers go with the ship, and a hatch fixed in the ship's own box is still asked about them. A
> carrier crosses with everything aboard it that this worker simulates (`Container.IsCarriedBy`, at any depth); each seated rider keeps its container and
> pose, and takes a new epoch (`docs/scope-activation.md` D19, D21). Admission applies to the whole group: if anyone aboard is not admitted (by default an
> owned rider is admitted when its client is a member and warm), the carrier is refused as a whole with `NotAdmitted`, so nobody is carried into a scope their
> client has not been readied for. Companions are asked for **every tick the leader is within `ApproachDistance` of an open face** (the NEB-428 way), not once
> at the commit, and this is the sounder reading now that everything crosses on its own: a held item in front of its holder reaches the plane first, and unless it
> is already known to be the holder's it would cross alone and leave the holder behind. The cost is one `ResolveCompanions` call per tick per entity near an
> open face, only when the hook is set. `ResolveCompanions` keeps its meaning, a leader and its companions commit as one group and each member arrives at **its
> own** pose mapped through the link. `CompanionLeftBehind` carries a `PortalRefusal`. A pinned or scene entity, one in another scope than its leader and one this worker
> does not simulate is left behind.

**D15 The test is a swept segment against the aperture, on the worker that simulates the entity.** After each tick's
simulation, for each portal registered on the entity's container chain (not every portal in the process: today's
`InstanceBoundary.Tick` asks every active boundary about every entity), Nebula takes the root's segment from last tick's
position to this tick's, in the face's axes, in double. The root crosses when the segment meets the cross-section and
ends at least `CrossDepth` (5 cm) beyond the plane; it crosses back when it ends `CrossDepth` short of it on the other
side. The depth is the hysteresis `ExitMargin` was for. A body faster than `CrossDepth` per step (a projectile is 5 to 20
m a tick) is tested on its predicted segment for the next step, `position + velocity × dt`, and committed before the step
that would carry it through, so it never simulates a step behind the face in the near scene, where a coincident far
scope is solid mass.

> **As built (NEB-428, NEB-429).** The test is the segment from the entity's position at the last look to this one, in the face's axes and in double, against the
> plane z = +`CrossDepth` (entering) or z = −`CrossDepth` (leaving): it crosses when the segment passes that plane inside the cross-section. That is the
> hysteresis of D15, and it works for a root that creeps across the plane. **The predictive test:** a body that moves further than `CrossDepth` in a step (its velocity
> in its scope's axes times `TickInterval × UpdateInterval`) is tested on the segment `position → position + velocity × dt` the same way, and committed on this tick
> if that segment would pass the plane, before the step that would carry it through. It is committed at the pose it stands in now, mapped through the link (not at the
> predicted one), so the step runs in the far scene from there; it can land up to one step short of the far face, in the throat D27 keeps clear. A refused predictive crossing
> stops the body where its predicted path meets the plane (D17). A body is tracked while it is within `ApproachDistance` plus a step of a face (the reach grows
> with its speed, so a 300 m/s projectile is seen a step ahead); the last position is kept per portal for `UpdateInterval + 1` ticks and is not trusted after a longer gap, so a teleport
> or a respawn through the plane is not a crossing, and an entry nobody has looked at for 120 ticks is dropped. Every portal is tested, linked or not, so a portal nobody
> opened is solid to anything. The near face is read once per worker tick per portal for the test.

**D16 A commit goes through the link's standing preparation.** Because the link is Open (D10), Nebula commits without a
per-entity `PrepareTransfer`: an internal `CommitThrough(link, entity, pose)` does what `BeginCommit`/`EndCommit` do (the
same container selection, `FindDestination` into the deepest far container holding the mapped point; the same lease-epoch
check; `Epoch++`; `HasStateTick = false`) on the tick the root passes. Nothing is loaded and nothing is held. The one way
it can fail is that the far container's lease changed on this very tick; the entity is then refused for that tick (D17)
and tested again on the next.

> **As built (NEB-428, NEB-429).** `Portals.Tick` runs where `InstanceBoundary.Tick` ran, after each entity's simulation. The commit is
> `NebulaWorker.CommitThrough(members, destination, map)` and is kept as it was, with a standing preparation: it checks that every member is simulated here and
> not a scene entity, that the destination's lease is owning and its scope admits, completes the destination's content (a no-op once the link is open,
> `InstanceScenes.Complete` otherwise), reads every member's pose and velocities in the scope being left **before anything moves** (a rider's frame, and so its velocity, changes
> when its carrier does), puts each non-seated member into the destination at its pose carried through `PortalMap` in double (`SetFramePose`), keeps seated riders in place,
> and finishes each (velocities, history, epoch; D18). The lease-epoch check of D16 is the epoch the link was ready with (`PortalLink.ReadyEpoch`): if the far
> container's lease moved, the commit is refused for that tick and the link prepares again. **Kept:** a crossing enters the template's entry part, or the container that holds
> the point 25 cm inside a fixed far face, because a member's client is warm only for that container; the deepest container holding the mapped point waits for NEB-431's window.
>
> **As built (NEB-431): the entry-container rule stays (superseded by NEB-434, above).** A window gives a member client the far window's *rows*, but it does not make the client warm for them: `PortalWarm` is sent
> for the entry container alone, and a deeper container's content would be built on the client at the commit, which is the hitch D10 exists to remove. Making the deepest container
> the destination needs the warm handshake to cover every container the far window touches, and the lease check of D16 to cover each of them; both belong with NEB-434's cross-worker
> commits. Nothing in the window code depends on it.
> **As built (NEB-434): the deepest far container is the destination.** Superseded: the note of NEB-431 below said the entry rule stayed because a member was warm
> for the entry alone. A member is now warm for every row (D10, as built), so `Portal.FarDestination` finds the deepest container of the far scope that holds the point
> the entity is mapped to (`FindDestination`, descending into physics frames), climbs `Container.Parent` to the first that is a row, and falls back to the entry. A
> container the link did not ready is never entered; the commit is also refused (`NoDestination`) when the lease epoch of that row moved since the link was ready. A
> template of two parts whose first part is behind the doorway and whose second holds it commits into the second (`PortalDeepestFarContainerTests`); before, the
> container pass of the same tick moved the entity there after a commit into the first. What "deepest" cannot be: a container that is not leased on its own (a ship's
> box, an inherited chunk), which the climb passes through to its leased ancestor.
>
> Leaving looks for the container of the near scope that holds the mapped point, descending into physics frames. A server-owned entity needs no client warmth:
> `Admits` is true for it (`ClientReady` was always true for unowned entities).

**D17 Straddling resolves to the root's side; a refused entity meets the aperture.** An entity is in exactly one scope at
a time, the one its root is in; the part of it beyond the plane is a proxy in the other scope (§6). An entity that is
refused (not admitted, link not open, `AmbiguousParty`, a member not yet warm) does not cross. A physical body hits the
aperture collider, which is solid for it. A mover with no body of its own (a `CharacterController`, a script-moved
projectile) is clamped: its root is put back `CrossDepth` short of the plane and its velocity's component along the face
normal removed. Either way the near worker raises `Refused(entity, link, reason)` so the game can bounce, drop or destroy
it. Nothing holds an entity where it last stood.

> **As built (NEB-429).** An entity the link does not let through is **stopped, not held**: its root is put at the point where its path met the plane, `CrossDepth` short of
> it on the side it came from, in double (`SetFramePose`), and the component of its velocity that pointed through is removed, from `Motion.Velocity` and from a dynamic body's
> `linearVelocity`. A physical body is left to meet the aperture collider; that collider is NEB-432's job, so until then a refused `Rigidbody` is stopped exactly like a
> mover with no body (a test pins it). `Portal.Refused(entity, link, reason)` is raised on the worker in every case, once for each tick the entity reaches the aperture: `link` is
> null when the entity has no link here (`LinkNotOpen`, or `AmbiguousParty` when its party named none and more than one link is open), the link itself for `LinkNotOpen` on a closed
> or preparing link, `NotAdmitted`, and `NoDestination`. A body that keeps pushing is refused on every tick it reaches the plane, and `NebulaDiagnostics.PortalRefusals` counts each.
> An occupant that a closed link keeps in is stopped `CrossDepth` short of the plane on the inside. `PortalRefusal` gained no member: `AmbiguousParty` was there from NEB-428.

> **As built (NEB-432).** A refused body that has colliders now meets the aperture collider (D28) before it reaches the plane: a rigidbody bounces off the 10 cm box, a `CharacterController` is
> stopped by it, and neither is clamped, because neither gets to the plane. `Refused(entity, link, reason)` is raised from `Portal.RefuseOnContact` instead, on every tick a collider of the body touches
> the solid aperture collider (grown 12 cm each way, a character controller stops a skin width short) and the body is not admitted (`link` is null when it has no link here: `LinkNotOpen` or `AmbiguousParty`; `NotAdmitted` for an open link that refuses it, and for
> every body at an observation-only portal). **The clamp stays** for what has no collider (a script-moved projectile), for a body that reaches the plane anyway (it tunnelled, or it was admitted by the
> aperture collider's pair and then refused by the commit: `NoDestination`) and for the predictive test, exactly as NEB-429 built them.

**D18 Everything an entity carries in time goes through the link pose with it.** At the commit: the position in double
(D37) and rotation; `Motion.Velocity`; a `Rigidbody`'s linear and angular velocity; and the `StateHistory` ring, whose
entries are rewritten into the destination's space and container, as `docs/scope-frames.md` D8 rewrites them on an
origin shift, so a lag-compensated read across the crossing tick finds poses in one space and the read path keeps no
branch. A ghost holder rewrites its copy's ring when the crossing reaches it. On a client the replica's
`RemoteInterpolator` buffer and, for the local pawn, the prediction history replayed on reconciliation are rewritten the
same way when the container change arrives, so a replica glides on and the pawn is not corrected. The container change
and the pose composed through the link pose arrive in one message and are applied in the same step: a replica must
never be read for a frame in its new scope with its old pose (§18: at a door turned 54.7° that frame drew the crate 0.34
to 0.9 m off). A game's own heading
bookkeeping turns with `Portals.Turn(from, to)` (today's `InstancePoses.Turn`) in a `Crossed(entity, portal,
from, to)` handler raised on the worker and on clients.

> **As built (NEB-429).** At the commit, for every member (a seated rider included), in this order: the pose is read in the scope's own axes and in double
> (`NetworkIdentity.CapturePortalState`); the entity is moved with `SetContainer` and `SetFramePose` to `PortalMap.Point(absolute)` and `Turn × rotation`
> (`MoveThroughPortal`); then its velocities and history are finished (`FinishThroughPortal`): `Motion.Velocity`, and for a dynamic `Rigidbody` `linearVelocity` and `angularVelocity`,
> are turned by the link and converted into the destination's physics frame, if it has one, so a crate thrown through a door turned 20° and 90° leaves with its momentum in the
> room's axes; the `StateHistory` ring has **every entry** rewritten (all of them were recorded before the commit) into the destination's space and container, through the same
> maths, so `StateAt(crossingTick)` and every earlier tick answer in the room's space and `Container` (a rider's entries recorded inside a physics frame that its carrier has since left are
> converted as the world now stands). On a copy this process does not simulate (a client; a ghost holder on a worker), `NetworkIdentity.SetContainer` finds the link between the two scopes
> when a container change crosses scopes (`Portals.TryCarryReplica`: a link a worker holds, a portal record a client was sent, or the pose of the instance, in that order, and only when
> the entity was within `Portals.CarryRange`, 48 m, of its face) and **in the same call** puts the entity at the link-composed pose, turns its velocity, rewrites its
> `RemoteInterpolator` buffer (`NetworkTransform.ReceiveRoot` does so as soon as the first sample in the new container arrives, so the interpolator never bridges two scopes through their containers'
> world poses; that bridge was the 0.34 to 0.9 m pop), its prediction history (`PredictedBehaviour`) and a ghost's `StateHistory` entries recorded in the scope it left, into the destination.
> `Portal.Crossed(entity, portal, from, to)` (scope ids) is raised on the worker for every member and on a client where the `Portal` component exists; `Portals.Crossed(entity, portalId, from, to)` is raised
> wherever the crossing is seen and `portalId` is 0 when only the instance's pose related the scopes. A game's heading bookkeeping turns with `Portals.Turn(from, to)`. Not rewritten: the
> history of a seated rider on a ghost holder (NEB-434).

**D19 Interest at a commit keeps what a window still authorises.** `docs/scope-activation.md` D13 stays: an entity that
changes scope is re-authorised for every client that holds it (`RevalidateInterest`) before anything about the
destination is relayed. What changes is `CanObserve` (§5): a client that may look through the link the entity went
through, from inside the near window, still passes, so the thrower keeps the crate in sight. An onlooker who is not an
observer loses it at once and is never told where it went, as now. The order is safe by construction: a link is Open
only after its members' `PortalWarm` acknowledgements went through the network gateway, so that gateway holds the
link and its observers before any commit through it can be relayed.

> **As built (NEB-431).** Built as written. A commit changes the entity's scope and bumps its epoch as before; the network gateway refreshes the record's place in the new scope
> and then re-authorizes every client that holds it (`RevokeAcrossScope`), before the destination's row is sent to anyone. `CanObserve` has the window rule (D21), so a client on the link's
> observer list whose pawn is inside the near window keeps the crate with no despawn and no new spawn, and an onlooker who is not an observer loses it and is never told the room exists
> (`ConformancePortalCrossingGatewayTests`: six crossings in and out, no despawn and no respawn for the thrower; the onlooker's wire holds no row of the room). The eviction sweep also leaves a
> record alone while one of its observers holds it through a window (`HeldThroughWindow`), so a crossing that outruns the client's next subscription update cannot blink it out. Which clients may
> look is the worker's: members are asked (`Portal.CanObserve`), `Portal.ObserveAnyone` opens a door to everyone, and the new `Portal.AddObserver(party, clientId)` lets a bystander look without being a member.

## 5. Observation

**D20 An observer on one side receives the other side's window.** A client qualifies on a side of an Open (or Closing)
link when it is one of the link's observers and its pawn is in that side's scope and inside that side's window. It then
receives the other side's window: its container rows, the carriers of those rows out to the root of their chain (what
`HostCarriers` does for one door today, `docs/scope-frames.md` D12), and every entity standing in it. Both ways, for
every link: from the planet into a room, and from the room out to the planet. An entity is relayed through a window only
after its container's row and that row's carrier chain (`docs/scope-activation.md` D14, D15). A client that is sent an
entity whose container it cannot resolve holds the update, whatever the container's kind, instead of placing the entity
in the public world (§18: today `NebulaClient.OnEntitySpawn` holds only for carrier and runtime containers and otherwise
calls `SetContainer(null)`, so a crate crossing out of an instance landed in scope 0 with its container-local coordinates
read from the world root, about 1.2 km off).

> **As built (NEB-431).** The window is a focus: for each window a client looks through, `AddPortalWindows` adds a box focus in the *other* side's scope (`InterestFocus.InScope`), small in reach
> (`WindowFocusScale`) because authorization, not distance, decides what is inside. The container rows inside the window are collected after the client's own windows and foci
> (`CollectNeededContainers`, `AddWindowRows`, queried in the other side's scope), and the carriers of those rows, out to the root of their chain, are kept in the client's set
> (`ClientConn.WindowCarriers`: what `HostCarriers` was, computed from the link records at each evaluation and not from a connection's history). A carrier is sent before the entities that stand in its rows.
> **Holding.** `NebulaClient.OnEntitySpawn`, the reconcile path (`OnOwnerState`) and `NetworkIdentity.ReceiveState` hold what names a container the client cannot resolve whatever its kind
> (`ContainerRef.NamesAContainer`: only `None` means the world root); a held spawn for a baked container binds when `ContainerRegistry.Rebuilt` finds it. Tests: `ClientHoldsUnresolvedContainerTests` (the spawn, a known entity,
> a state entry, the reconcile path). **Limit:** a window whose ground is a frame with regions of its own (a planet that is a carrier) is authorized in the scope's space (a position composed through the frame) but
> subscribed in the scope's own region space only, so the entities standing on such ground are not found by the window's focus; the host-side focus in the frame's region space is not built (see the seams under §15).

**D21 The network gateway authorises it in `CanObserve`, from link records only.** One rule is added after the existing
ones: an entity in scope T is observable by a client in scope S when an Open or Closing link joins S and T, the client is
on its observer list (or the list is `Anyone`), the client's pawn is inside S's window, and the entity's record is
inside T's window. Both window tests are on the absolute records the network gateway already keeps
(`EntityRecord.AbsX/AbsY/AbsZ`), transformed through the link pose for an offset portal. The worker decides policy; the
network gateway only enforces the list (D11).

> **As built (NEB-431).** The rule is `ObservableThroughWindow`, after the existing ones, on the entity's *root* (a passenger is judged where its carrier is): the client has a window whose other side
> is the entity's scope, and the root's place, from `EntityRecord.AbsX/Y/Z` (composed through a frame for a record in a frame's region space), is inside it. Each side's window is measured in its own face's
> axes in its own scope, so no mapping through the link is needed for the test and an offset portal observes exactly as a coincident one (`PortalMap` is for what crosses, not for what is seen).
> The windows are `ClientConn.Windows`, recomputed from the portal and link records (`RefreshWindows`) at each of the client's evaluations, whenever a record changes (`ReviseWindows`: a client that lost a window is
> re-authorized at once, so a closing link takes the far side away now) and before a revalidation. A client qualifies when the link is Open or Closing, the client is on the observer list (or the list is `Anyone`), its
> scope is a side's scope and its pawn is inside that side's window. A pawn that crosses is re-evaluated the same way, because the record's place is refreshed before anybody is asked.

**D22 One portal deep.** Windows do not chain. An entity received through a link is never the source of another link's
window, and a client is never sent a scope two links away: on floor 1 you see floor 2's landing, not floor 3. This bounds
the cost per client at the windows of the links it stands in, at most `MaxPortalWindows` (4) at once, nearest first.

> **As built (NEB-431).** Windows are computed from the client's own scope only, so nothing is two links away by construction (`ConformancePortalWindowTests.WindowsDoNotChain...`): a client on the street that is an
> observer of both the door and a second door inside the room sees the room and not the second room. `NebulaGateway.MaxPortalWindows` (4) bounds the windows one client looks through, nearest first by the distance
> from its pawn to the face of its own side; the rest are simply not looked through. `InterestClient.PortalWindows` tells a policy how many, and `NebulaGateway.PortalWindowCount` and `PortalWindowsOf(clientId)` the operator.

**D23 A client's subscriptions span scopes.** Region ids are salted with their scope (`docs/scope-frames.md` D7), and a
client's set is today salted with one scope (`ClientInterest.ScopeSalt`, `UpdateClientRegions`). The far window's
regions are subscribed with the far scope's salt beside the client's own; the subscription message already carries
opaque 64-bit ids, so nothing on the worker side changes. An entity's record survives its scope change: when a window
still authorises it (D19), the network gateway re-files the record under its new region key in place, never evicting it
and fetching it again (§18: with only `CanObserve` passing, a crossing crate blinked out for 0.3 s in 2 of 6 crossings).
Window rows count against `MaxContainerRows` after the client's
own windows and foci, as `docs/scope-activation.md` D23 rows do, so a full budget withholds window ground, never the rows
the client stands in.

> **As built (NEB-431).** `InterestFocus` has a scope (`Scope`, `HasScope`, `InScope`), and `ClientInterest` salts a scoped focus's regions with it and lets it see only that scope's entities; the gateway's `UpdateClientRegions`
> keeps a scope per region, so a client's `Regions` hold its own scope's keys and each window's far scope's, and `UpdateSubscriptions` salts the focus regions it passes to the workers the same way. Nothing changes on
> the worker. The container rows of a window count against `MaxContainerRows` after the client's own, because the foci loop reaches them last (points, then boxes). The record's re-filing is the existing rebucket (`RebucketIfMoved`),
> which keeps the record and moves its region key; what NEB-431 adds is that the new key is one the client already subscribes (the window's), and the eviction guard of D19.

**D24 Windows replace `ObservePublic`, `ObserveHost`, `HostCarriers` and NEB-370.** `ObservePublic` only ever looked
into scope 0 and only through an axis-aligned box fixed at preparation; `ObserveHost` kept the host's rows from a
per-connection history (`ClientConn.HostViews`), lost on a reconnect inside (`docs/scope-activation.md` §12.3), and no
host entities (D24 there). A link's windows give both directions, any scope, rows and entities, from link records that
survive a reconnect. `ObservePublic`, `PublicView`, `ObserveHost`, `ClientConn.HostViews` and `HostCarriers` are removed in the change that
brings windows (NEB-431), with no mapping onto them: a game that looked out of a room declares the host side's window of
its portal instead. NEB-370's sibling
observation (one instance seeing another's entities) is the general case of D20.

> **As built (NEB-431).** Removed, with no mapping: `InstanceTemplate.ObservePublic`, `PublicView` and `ObserveHost`; `ScopeDefinition.ObservePublic`, `ObservationCenter`, `ObservationSize` and `ObserveHost` (and their canonical JSON);
> `InstanceContainerInfo.ObservePublic`, `ObservationCenter` and `ObservationSize` (on the client wire, which is shorter by 25 bytes per instance row) and `ObserveHost` (the flags byte of a stored lease row, which now holds only the pose
> bit, value 1); `ClientConn.HostViews`, `HostCarriers`, `Placed`, `NebulaGateway.MaxHostViews`, `ClientInterest.ScanPublicToo`. Still protocol 28: nothing has shipped, and the frozen decoder skips instance rows. A game that
> looked out of a room declares its portal's observers instead: `Portal.ObserveAnyone` for a door anyone may look through, `Portal.CanObserve` for the members it allows, `Portal.AddObserver` for a bystander. Ported tests:
> `InstanceObserveHostTests` became `ScopedChunkViewTests` (a client hides a scoped grid's chunks while its pawn is elsewhere; the rest of it was `ObserveHost`), and its carrier test is `PortalWindowFrameTests`
> (a window onto a planet's ground); `InstanceTests`, `InterestTests` and the scope tests lost their `ObservePublic` lines.

**D25 Observed entities are replicas in their own scope.** A client never simulates them; it predicts against them only
through proxies in the band (D26, D27), which it builds for its pawn's scope as the worker does. A client's dynamic
proxies are posed from the replica's latest authoritative state, the same tick the worker posed its proxy from, never
from the interpolated draw pose (§18: a pawn pushing against a crate across the face was corrected 52 times in 2.5 s with
no client proxy and 29 times with one posed from interpolated state). Replicas live in their own scope's containers on
the client, drawn through that scope's pose (§10). The client's own scope stays its pawn's (`docs/scope-activation.md` D16), as do routing, frames
(D6 of `docs/scope-frames.md`) and the view.

> **As built (NEB-432).** A client builds the same things a worker does for its pawn's scope only (`PortalPhysics.StepClient`, called by `NebulaClient.FixedUpdate` before the pawn predicts a tick): the
> aperture collider of every face the pawn is on the near side of (solid with or without a link, as on a worker), the far aperture of a link the pawn stands in the far scope of, the static throat of an open
> link, and a kinematic proxy in the pawn's scene for each collider of each replica of the other scope that touches the band. The proxy is posed, in the scene's space and through the link, from the
> replica's **newest sample** (`RemoteInterpolator.LatestPosition` and `LatestRotation`, the collider carried with the root from where it is drawn to where that sample puts it), never from the interpolated
> pose it is drawn at (`PortalPhysicsClientPlayTests`). A client does not step instance scenes, so its proxies are placed through their transforms and `Physics.SyncTransforms` runs once per step for the
> `CharacterController` to see them. The pawn ignores the aperture collider of its side when the link is open and it is a member (`PortalLinkFlags.Member`); a pawn standing in the far scope always
> passes outward. A client does not run the pair rule (nothing on it is simulated but the pawn), and builds no pool ahead of time. A pawn inside a physics frame is built for as a worker builds for its side (see the
> physics-frame note under D26): `StepClient` runs inside `PhysicsFrames.BeginSimulation(pawn.Space, pawn.transform)` and `EndSimulation`, so the aperture collider, the throat and the proxies stand in the frame's scene in
> its simulation space, about the origin the pawn's own prediction step uses (`FramedPortalClientPlayTests`).

## 6. Physics across the face

**D26 A body in the band is mirrored into the other scope's physics scene as a kinematic proxy.** Membership of the band
is tested on the entity's extent when it has one (`docs/entity-extents.md`), on its colliders' bounds otherwise, so a
ship whose root is far from the face but whose hull is in the doorway is mirrored. On the worker that simulates both
scopes, the proxy is built from the authority's colliders (box, sphere, capsule, mesh; a `CharacterController` as a
capsule; terrain never), only the ones that overlap the band, put in the other scope's scene (`InstanceScenes`, or the
default scene for the public world) on `PortalProxyLayer`, and posed through the link before that scene steps with
`MovePosition`/`MoveRotation`, so contacts get its velocity. Proxies are pooled by shape signature and built while a
link is Preparing, so entering the band allocates nothing. A `PortalProxy` component names the authority, for hit
attribution, and lets queries and the game's triggers skip proxies. A proxy is re-posed only when its source moved since
the last step, so a body resting on a resting proxy can sleep (§18: a proxy re-posed every tick kept the crate on it
jittering at 0.8 to 1 m/s). When two bodies on opposite sides both have proxies, the pair collides in one scene only:
each body ignores the proxy of a body whose own proxy it is already in contact with, so a dropped body does not push
through as an infinite mass (§18: mirrored both ways, a dropped crate squeezed the one under it 0.9 m out).

> **As built (NEB-432).** The engine is `PortalPhysics` (`Runtime/Containers`, one instance per `NebulaWorker` and one per `NebulaClient`; never process-wide state, because the in-process mesh holds several
> workers), with `PortalBodies` (what a body touches), `PortalBand` and `PortalFaceFrame` (the geometry), `PortalShapes` and `PortalShapePool` (shapes and the pool) and `PortalPairRule`.
> `NebulaWorker.Tick` runs `PortalPhysics.Step` after the entities' own simulation and immediately before `InstanceScenes.Simulate`; the street's default scene is stepped by Unity after `FixedUpdate`, so it sees the same poses.
> - **Membership** is `PortalBodies.Touches` (see D10). A body is tracked from the tick it touches the band of its own side until it is `PortalPhysics.BandHysteresis` (25 cm) plus the distance it travels in
>   a step outside it. New bodies are tracked only while the link is **Open**; a Closing link keeps the bodies it has (D9).
> - **Shapes.** Box, sphere, capsule and mesh colliders, and a `CharacterController` as an upright capsule; scale is folded into the dimensions (a mesh keeps its scale on the proxy's transform). Triggers, disabled
>   colliders, terrain and colliders that belong to another identity are never copied. One kinematic `Rigidbody` and collider for each of the body's colliders that touches the band, posed at the centre of the
>   collider's shape. They stand on `NebulaConfig.PortalProxyLayer` (31; `Portals.ProxyLayer`) with a `PortalProxy` naming the authority.
> - **Posing** is the pivot form in float about the two faces (`to.Origin + turn * (p - from.Origin)`, D37; a scene is anchored near what it holds, so the offsets are small). The first pose is a teleport; after
>   that a worker uses `MovePosition` and `MoveRotation`, a client the transform. A proxy is re-posed only when its source moved by more than 0.1 mm or 2×10⁻⁵ since it was last posed, and a **sleeping** body is not
>   looked at again once its proxies are posed: a body resting on a proxy sleeps (`ABodyRestingOnAnotherBodysProxySleeps`).
> - **Pooling.** `PortalShapePool` keeps free proxies by (scene, shape). `PrepareLink` builds, while the link prepares, one for each collider of each body within 16 m of the face, so a body entering the band takes
>   a pooled proxy and builds nothing (`PortalPhysics.ProxiesBuilt` does not move). A shape nobody foresaw is built on demand.
> - **Bound.** `Portal.MaxProxiesPerPortal` (64, a field) bounds the body proxies a portal has standing over all its links. A body past the bound gets none (the bodies that came first keep theirs), a warning is
>   logged once and `NebulaDiagnostics.PortalProxiesDropped` counts it. `NebulaDiagnostics.PortalProxies` is the number of proxies and throat copies standing on this process.
> - **The pair rule, as built.** The spike's rule ("each body ignores the proxy of a body whose own proxy it already touches") cannot be applied to two bodies at once, since each would ignore the other's. What is built
>   is its sound form (`PortalPairRule`): when a body native to each side, both with proxies, touch, the pair **collides in one scene, the faster body's** (equal speeds: the lower net id's), and the other scene's
>   colliders of the pair ignore each other with `Physics.IgnoreCollision`. The choice is kept for as long as the two touch, so a body that lands on another and comes to rest does not hand the pair over. The falling
>   crate is stopped by the copy of the crate below it; the crate below is not moved by a copy of the one above (it was driven 0.78 m out without the rule; the test pins 2 cm). It applies when one worker holds
>   both scenes; across workers each side is one-sided, as D31 says.
> - **A crossing swaps the sides on its own tick.** `CommitThrough` drops the members' proxies in the scene they are about to enter before they arrive (`BeforeCommit`: a body arriving on top of its own proxy would
>   be pushed out of it) and looks at them again at once when they have (`AfterCommit`), so the scene they left holds their proxies on the tick they crossed and no tick has a body without a collider on either side.
> - **A side that simulates in a physics frame (a planet that is a carrier; `Container.InnerSpace`).** It is built where its bodies simulate: the aperture collider, the throat and the proxies of that side stand in the
>   **frame's own physics scene**, in the frame's **simulation space** (the frame-local position less the frame's floating origin, `PhysicsFrame.LocalToSimulation`), and a body is tested and posed in that same space.
>   `PortalSpaces` is the seam (`Runtime/Containers`): `SceneOf` (the container's scene, or its frame's), `FaceFrame`, and `Point`/`Rotation` for a pose between two spaces (a body standing in a frame nested inside
>   the side's, a ship on the planet, is converted into the side's space with `PhysicsFrames.Convert`; one standing in the side's own frame is read as it is). A side that is not framed is its scope's scene, as before.
>   - **The face is composed in double and narrowed once.** The link's absolute face (a `Double3` and a rotation) goes into the scope's own coordinates with `ContainerRegistry.ToFramePrecise`, through the carrier's
>     pose into the frame's local coordinates with `PhysicsFrames.ConvertLocal` (which never passes through a float in the scope: the carrier stands 10⁷ m out, where a float is a metre apart), and on to the
>     simulation space by adding the frame's root offset, still in double; only that last sum, a small number near the frame's origin, becomes a `float`. After it every offset is face-relative and small.
>     The turn between the sides is taken between the faces as the two scenes hold them (`TurnNearToFar = FarFrame.Rotation × NearFrame.Rotation⁻¹`), so a planet turned in its scope and a door turned on the
>     planet do not appear in a proxy's pose twice, and the pose is the pivot form of D37 as before. The carrier's place in its scope drops out: the door is fixed in the planet, so it stands still in the
>     frame's scene whatever the planet does in the scope (`TheFacesFrameInThePlanetsSceneDoesNotDependOnWhereTheCarrierStandsInItsScope`).
>   - **Origin shifts and moving frames.** The face is worked out again every step, on a worker after the frame's own origin moved at the end of the last tick. A frame whose floating origin shifts (or whose
>     door moves in it) moves the scene-level objects with no help: the aperture collider is *moved* when its face moved more than 2 mm or 2 × 10⁻⁵ (not made again; a face is rounded to a millimeter where it
>     is named, so working it out again after the carrier moved is not exact, and that noise is not a move: `PortalFaceFrame.SameAs`), a side's throat is made again when that side's face moved, and a proxy is
>     posed at once (a teleport, not a move) when the face its scene is posed about moved, **even if its source sleeps** (a sleeping source is otherwise not looked at again).
>   - **Both directions** are built from the same code: a body on the framed side is mirrored into the instance's scene, a body in the instance into the planet frame's scene (`FramedPortalPhysicsPlayTests`). The
>     throat copies the planet's static colliders in its half of the band into the instance and the instance's into the frame's scene; `Portal.Validate` and `BandOccupied` read the side the same way.
>   - **Across workers** the far worker's rig names the near side's space from the portal record's near container, and poses a ghost of a body that stands in the planet's frame through the same face
>     (`FramedPortalAcrossWorkersPlayTests`, both directions). A frame that has no scene on the worker (a frame it does not hold) builds nothing for that side.
>   - **Limits.** One level of frame per side: the side's own frame. A body in a frame nested inside it is mirrored by its converted pose, but that frame's interior statics are not part of the throat (only
>     the colliders in the side's own frame's scene are, the nested frame's hull copies among them). A client whose pawn is in the **instance** poses a replica standing in a planet's frame from where that replica
>     is drawn (render space, the precision of a float at its distance from the scene's render origin); a pawn standing in the frame is exact (above). The frame's floating origin on a client follows the pawn by
>     the prediction step's rule (`BeginSimulation`); if it moves between `StepClient` and the prediction ticks of the same frame, the pieces catch up on the next step.

**D27 The throat: each side's static geometry in the band is mirrored too, and each side must be open there.** A
coincident far scope is built inside the near side's solid mass. A body straddling the face would have its far half
inside the near side's hill, and PhysX would push it out. Two rules make the doorway a doorway in both scenes:

- While a link is Preparing, each worker copies the other side's *static* content colliders that overlap the band into
  its own side's scene as static proxies (the far room's walls and floor in front of the planet's door, and the
  planet's doorstep in the room). They are enabled at Open and dropped at Closed. A client builds the same static throat
  for its pawn's scope, so prediction at the face sees the same walls the worker does.
- The game builds each side so that, within the band beyond the face, its own static colliders leave the cross-section
  clear: a tunnel mouth at least `BandDepth` deep. `Portal.Validate` overlap-tests this in the Editor and when a
  link starts preparing, and logs what is in the way.

> **As built (NEB-432).** The throat is `PortalPhysics.BuildThroat`. It copies, into each side's scene, the colliders of the other scene (found with `OverlapBox`) that overlap the half of the band that is the other
> side's own side of the plane: the far room's colliders in `0 ≤ z ≤ BandDepth` into the street's scene, the street's in `-BandDepth ≤ z ≤ 0` into the room's (the other halves are the solid mass behind each
> face, which is not what a body straddling the face needs). A collider counts when it is not a trigger, not terrain, not one of ours, has no `Rigidbody` and no `NetworkIdentity` above it, and is a shape D26
> can copy; a kinematic door in the content is a `Rigidbody` and is left out. Copies are inactive until the link opens (`SweepRig` enables them) and destroyed at Closed; `PortalPhysics.MaxThroatColliders`
> (128 a side) bounds them and says so once. A client builds the one its pawn's scene needs. **A split link has no static throat:** each worker holds the scene of its own side only, and nothing carries the
> other side's geometry between workers, so a body straddling the face on a split link rests on its own side's statics alone (D31). Co-location (D34) keeps links off that path.
> `Portal.Validate(blockers)` is public: in play it checks the near side and, for each link whose far container this process holds, the far side; in the Editor, the near side in the open scene. It tests
> the volume of the cross-section, drawn in 5 cm from its edges so a floor that meets them does not count, from the face to `BandDepth` beyond it, for static colliders (not triggers, not bodies, not
> ours), logs what it finds (up to six names) and returns whether it is clear. It runs when a link starts preparing and, in the Editor, shortly after a portal is edited (`OnValidate`).

**D28 A closed portal is a solid plane.** Each scene has a thin box collider over the cross-section. It is solid while
the link is not Open. While it is Open, the collider is ignored, pair by pair (`Physics.IgnoreCollision`), for the
colliders of each admitted entity entering the band, and solid for everything else, which is how a refused entity meets
it (D17). With several parties' links open on one face, an entity passes only if its own party's link is open (D12).
An observation-only portal (§17) keeps it solid for everyone.

> **As built (NEB-432).** The aperture collider is a `BoxCollider` of the cross-section's size and `PortalPhysics.ApertureThickness` (10 cm, twice `CrossDepth`) deep, centred on the face's plane, on the proxy layer
> with a `PortalProxy` (`IsAperture`). The **near** one is one for each portal, in its near scene, and stands whether or not the portal has a link (a portal nobody opened is solid); the **far** one is one for each link,
> in its far scene, and stays after the link closes (an occupant it closed under is stopped). A body that touches a band is looked at when it enters and every `PortalPhysics.AdmitCheckTicks` (6) ticks after:
> on the near side it is let through when `Portal.AdmitsThrough` says so (its party's link, D12, is open and `CanEnter` or the default admits it); on the far side anything leaving is let through while the link is
> open. For a body that is let through, each of its colliders that touches the band ignores the aperture collider (`Physics.IgnoreCollision`); for every other body the collider stays solid, so a refused body, a
> body of another party, a body at a link that is not open and every body at an `ObservationOnly` portal meet it. A Closing link keeps the pairs of the bodies it had and makes no new ones; the pairs go when
> the link closes and what straddles is pushed out by PhysX. `Portal.ObservationOnly` is the §17 flag: a portal that admits nothing (`TryCross` refuses with `NotAdmitted`), carried to the gateways and
> clients in `PortalRecord.Flags`.

**D29 Across workers, the proxy's source is the ordinary ghost.** When the other side is simulated by another worker,
the entity reaches it through the ghost band (D30), as a kinematic copy driven by the received stream
(`NebulaWorker.SetGhostPhysics`); the receiving worker holds it as a proxy only, posed through the link in its own side's
scene, with no body in the entity's scope's scene there (that worker may not have that scene at all). Blocking is
one-sided, as for every ghost (`docs/architecture.md`, "Pre-warm, never transfer-at-crossing"), and the proxy is one
tick behind its authority.

> **As built (NEB-432).** A body that `PortalPhysics.Step` finds in the band of a face (it is tracked, D26) is ghosted to the workers that own the other side: for a body on the near side, the owners of the far
> containers the far band touches; for one on the far side, the owner of the near container (`PortalPhysics.GhostTargets`, called from `UpdateGhostBand`). The ghost spawn carries the new
> `EntityFlags.PortalGhost`; the receiver (`NebulaWorker.HoldAsPortalProxy`) switches the ghost's colliders off, so it is **no body** in its scope's scene on that worker, lists it in `PortalGhostEntities` and poses
> proxies of it, from its transform (one tick behind its authority, as stated), into the scene it holds. A ghost that becomes this worker's own by a handover gets its colliders back. `PortalBodies` reads a
> portal ghost's disabled colliders. **Limits:** a ghost that is wanted for an ordinary seam as well as for a portal is a portal ghost if the portal asked first (the flag is set at the spawn); a portal ghost of a
> body in an instance scope makes the receiving worker prepare that container's content, as any ghost into an instance container does; a split link has no static throat (D27) and no pair rule (D26), so the
> contact is one-sided on both sides.

**D30 NEB-232 is decided: scopes are adjacent at portals only, and only within a band.** `ContainerRegistry.Link` stays
scope-qualified (`docs/scoped-chunk-grids.md` D11): two containers of different scopes are never neighbours, however their
boxes overlap. A link that is not Closed adds a separate relation, portal adjacency, between the near face's container
and the far containers its band touches, and the ghost band consults it with the band box, not the containers' boxes. An
entity is ghosted across a portal when its extent or root is inside the band, never because two scopes' boxes overlap.
There is no free ghosting, interaction or sight between scopes anywhere else.

> **As built (NEB-432).** The relation is `PortalAdjacency` (`Runtime/Worker`): an edge for each link that is not closed from the container the near face is fixed in to the containers of the far scope the far band
> touches (`ContainerRegistry.Overlapping` over the band's box, the far entry first). The driving worker keeps it and tells the workers that own those containers with `MsgId.PortalAdjacency` (51,
> `PortalAdjacencyMsg`: the portal record with the link's far face, the link's state and generation, the near container and the far ones), whenever the link's state, its opening or a lease changes and at least every
> half second; a link that goes away, or a worker that no longer holds any of the far containers, is sent a removal. `ContainerRegistry.Link` is untouched: scenario 59 pins that the containers of the two scopes are not
> neighbours while their adjacency edge exists, and `PortalPhysicsLogicTests` that two runtime containers with exactly the same box in two scopes are not either. Worker-to-worker messages require protocol 28
> exactly (`HelloMsg`), and this one is new in it.

**D31 The contact model has a stated limit.** Each side resolves its contacts against the other side's proxies, which
are kinematic. A body resting across the face rests on its own side's statics and throat proxies and is stable. Two
dynamic bodies pushing each other across the face each see the other as immovable, so a shove through a doorway is
one-sided per side, and a pile that spans the face can jitter. That is the price of keeping one physics scene per scope;
shared physics scenes were rejected (Seamless Instances, "Proxies, not shared physics scenes").

> **As built (NEB-432).** Pinned by the tests: a crate resting across the face rests on its own side and sleeps; a body on another body's proxy sleeps; and the pair rule of D26 keeps a shove from one side of
> the face from becoming an infinite mass on the other. A pile that spans the face on a split link is one-sided on both sides.

## 7. Queries

**D32 `PortalQueries` continue through an open aperture, one portal deep.** `Raycast`, `Sweep` (sphere, capsule,
box) and `Overlap` take the scope to start in and the entity (or party) asking. They run in that scope's physics scene
ignoring `PortalProxyLayer`. When the cast meets the cross-section of a link that is Open for the asker's party
before it hits anything, it continues in the far scope's scene from the plane point, mapped through the link, with the
remaining distance, and stops at the next aperture. An overlap tests its own scope and the part of its shape beyond the
face, mapped into the far scope. A closed aperture is a hit on the plane, flagged `Aperture`, so a shot against a closed
door hits the door. Each hit reports its scope, collider, entity, and its point in both the asker's and the hit scope's
spaces. A query through a face with several parties' links picks the asker's party's far scope (D12).

> **As built (NEB-433).** `PortalQueries` (`Runtime/Containers`) is a class with an instance for each process, `NebulaWorker.PortalQueries` and `NebulaClient.PortalQueries`, not a static one: what a query can answer
> is the scenes and the link rigs of the process asking (the in-process mesh the tests run holds several workers). Its calls: `Raycast`, `RaycastAll`, `SweepSphere`, `SweepCapsule`, `SweepBox` and
> `OverlapSphere`, `OverlapCapsule`, `OverlapBox`. A query takes the scope to start in, a `PortalAsker` (`Of(entity)`, `OfParty(party, entity)` or `None`), the shape, a caller-owned `PortalHit[]` for the
> `...All` and `Overlap` forms, and an optional `PortalQueryOptions` (layer mask, trigger interaction, `Tick`, `RewindNear`, and `Space`, the physics frame the cast starts in). Nothing is allocated per call: a
> cast is a loop of closest-hit casts that advance past a hit that is not one (the asker's own collider), so no scratch buffer can overflow and miss the nearest.
> - **Where it runs.** In the physics scene of the scope (`InstanceScenes.PhysicsFor`) or, with `Space`, of the frame (`PortalSpaces.SceneOf`), with every layer but `NebulaConfig.PortalProxyLayer` (never in the mask,
>   whatever the caller asks for; a collider with a `PortalProxy` is skipped as well). Origins and points are in that scene's coordinates, as `PortalPhysics` keeps them: floats about a face, small by construction,
>   carried between the sides by the pivot form (D37) in each face's axes, and not `Double3`. (A caller that wants the scope's absolute point composes it with `ContainerRegistry.ToAbsolutePrecise`.) A client
>   queries the scenes it holds in the frame each scope shares (`ScopeFrames.Resolve` is already in the face frames it builds), against its replicas as they stand.
> - **Faces.** The faces of a scope are the aperture colliders of the process (`PortalPhysics.GatherFaces`): every near face whose portal stands in the scope, linked or not, and the far face of every link
>   into it. The cast meets a face where the **path of its reference point** (the ray's origin, a sphere's or a box's centre, a capsule's midpoint) passes through the opening on the way through
>   (+z of a near face, -z of a far one); a sweep that is wider than the doorway is stopped by the jambs in the near scene before it gets there. The cast is made in the scene only as far as the plane, so
>   the solid mass a coincident far scope lives in is never hit.
> - **Party.** For a near face the link is the named party's; otherwise the asking entity's `Portal.PartyOf` (by default its owner's identity); on a client the link it is a member of; otherwise the face's one
>   open link; none is the closed case (D12). Leaving through a far face needs only that its own link is open. Open means `PortalState.Open` and not `ObservationOnly`: a closing link is solid.
> - **A closed aperture** is a hit on the plane: `PortalHitFlags.Aperture`, the aperture collider as the collider (the one collider a query reports that is a proxy), no entity, the normal facing the ray.
>   **One portal deep** is a cast that meets a second aperture in the far scope: it stops there, a hit on the plane flagged `Aperture | DepthLimit | ThroughPortal` (closed: without `DepthLimit`). There is no depth
>   setting; D22 (windows do not chain) is the rule here too.
> - **A hit** (`PortalHit`) carries the scope, collider, entity (the nearest `NetworkIdentity` above the collider), the distance summed over the legs, the point and the normal in the asker's space and in the hit
>   scope's, the flags (`Aperture`, `Truncated`, `ThroughPortal`, `Rewound`, `DepthLimit`) and the link's id. Points in the asker's space are the straight continuation of the cast, as if the link were not there.
> - **Overlaps** test their own scope, then for every open face the shape's bounds reach through (inside the opening, beyond the plane) the shape mapped into the far scope, keeping the colliders whose bounds
>   reach the prism over the cross-section on the far side. That is an approximation by bounding boxes. A closed face the shape straddles is a hit on the plane. The point of an overlap hit is the closest
>   point of the collider to the shape's reference point (its bounds' for a concave mesh).
> - **Limits.** A body straddling the face is in one scope; the part of it beyond the plane is a proxy and proxies are never hit, so a cast that passes through the doorway while a crate is half in it misses the
>   half in the far room (it is hit by the near leg if it is hit before the plane). A sweep is not clipped to the doorway beyond what the near scene's geometry does.

**D33 Lag compensation reuses state history, and Nebula rewinds the far leg.** Nebula has no rewind query today, only
`NetworkIdentity.StateAt` (`docs/state-history.md`). A query with a `tick` poses the far window's entities at `StateAt(tick)`
for the far leg, through a small `HistoryRewind` helper NEB-433 adds; a caller that rewinds its own scope already does
nothing new for the near leg, and one that wants Nebula to do both passes `RewindNear`. Because the ring was rewritten at
the crossing (D18), an entity that crossed during the rewound interval is found in its scope of that tick. When the far
scope is on another worker, the near worker has only the band (ghost proxies and throat): the far leg is tested there
and a cast that leaves the band is returned `Truncated`. Open question 6.

> **As built (NEB-433).** `HistoryRewind` (`Runtime/Containers/HistoryRewind.cs`, public) is a disposable that puts one scope's scene back as it stood at a tick for any physics query, `Physics.*` or
> `PortalQueries`, and puts it back exactly: `using (HistoryRewind.Begin(worker, scope, tick, area)) { ... }`. A query with a `Tick` does it for the far leg (always) and for the near leg with
> `RewindNear`, around the one leg's casts, with an `area` box around the leg's path so that only what can matter is moved. Entities with no entry in the ring for the tick are left where they are, and a
> client, which records nothing, ignores the tick. For each entity of the scope that has an entry, its transform is set to the recorded pose and put back after; the colliders keep their own shapes, so a
> compound entity needs nothing more. **Crossings during the interval.** The ring was rewritten into the scope the entity is in now (D18), so a pose recorded before a crossing is the continuation of its path
> through the link. The helper reads that as the rule of D15 turned round: an entity of this scope whose pose at the tick lay on the other side of a face, within `Portal.ApproachDistance` of it and its
> opening, was in the other scope then, and its colliders are switched off for the length of the rewind; an entity of the other scope whose pose at the tick, carried through the link, lies on this side of the
> face (to the same reach), was here, and a shape of each of its colliders is stood in this scope's scene at that pose (a pooled `RewindShadow`, reported as the entity and the collider it stands for, never as
> itself). `ConformanceMesh` pins both directions (`PortalQueriesRewindPlayTests`). **Cost:** one ring read for each entity of the scope and of the scopes linked to it (every entity when no `area` is given), a
> transform set for each one that moved more than a millimeter, one `Physics.SyncTransforms` each way; nothing is allocated once the shapes are pooled (a test pins 200 mixed queries under 2 KB). A pose set on a
> sleeping body may wake it. A framed side is posed in the frame's simulation space as recorded and assumes the entity still stands in the space it was recorded in (a pose recorded in another space is skipped).
>
> **A split link (Q6, built as proposed).** When the other side is on another worker the query does not map into a scene it does not have. The cast arrives at the plane and goes on in its own scene, against
> the ghost proxies of the far bodies that `PortalPhysics` poses there (D29) and only inside the band (the proxy layer, `Authority` set, this link): a hit is reported as the ghost, in the far scope, with the point
> mapped through the link. A cast that goes past the band's box without a hit is returned with `Truncated` at the band's edge: a pseudo-hit, with no collider and no entity, so that a caller cannot take it for a
> miss. A leg in the band is not rewound: the proxies stand one tick behind their authority (D29) and a ghost's history is the far worker's to answer.

## 8. Workers

**D34 Linked scopes co-locate through cohesion.** `PrepareInstance` already creates a template far scope's rows active on
the near worker (`PreferredWorkerId`). To keep them together, the near worker reports each link that is not Closed as a
binding of the near face's container and the far containers its window touches, in the cohesion block of its telemetry
(`docs/cohesion-hints.md` D7); the planner's union-find deals them as one item, holds and D9 reporting included. Bindings
go when links close, so a town with forty hangar doors binds only the doors in use.

**D35 When they are split anyway, a commit goes through handover (NEB-371).** Capacity (`docs/cohesion-hints.md` D9),
failover or an operator can put the far scope on another worker. Readiness (D10) then includes the far worker's answer
over the worker link, the existing `InstancePrepare`/`InstanceReady` exchange carrying the link id, and portal adjacency
(D30) gives the far worker a warm ghost of anything in the band. The near worker commits as in D16 into the far
container, which it does not own, and the ordinary handover sends authority to the far worker on the same tick
(`AuthorityTransfer`, with the cohesion group and the carried subtree). The far worker snaps to the handed state, as at
any seam. Nothing holds at the face; the handover's latency is the only cost.

**D36 Every wait has a reason.** Link waits are D9's `PortalWait`; an entity's refusal names its reason (D17);
`NebulaDiagnostics` counts open links, links split across workers (`SplitPortalLinks`), refusals and close overruns,
and the dashboard lists each open link with its state and wait.

> **As built (NEB-434), D34.** `NebulaWorker.CopyPortalBindings` (`Runtime/Worker/WorkerCohesion.cs`) lists, for each portal whose near container this worker owns,
> each link that is not Closed with the near container's id, the ids of its rows, its state and wait, and whether any row is another worker's. `WorkerTelemetry` writes them
> as `"portalLinks"` beside `"cohesion"` (the containers under `"in"` again, never `"containers"`), `MeshTelemetry` reads them (`PortalLinkSpan`, `ParsePortalLinks`) and
> `CopyCohesion` hands each to the planner as a `CohesionGroupInfo` whose `Label` is `portal <authored id> <link id in hex>` (never the party: a party can be a player's
> identity), `Detail` its state and wait and `IsSplitLink` its split flag. `CostBalancedAssignmentPolicy.Bindings` takes them unchanged, so the union-find, holds and the
> D9 oversize report are the planner's own (`ConformancePortalCohesionTests`: a chain of three, a hold on one member, an oversize link reported by its label). A link binds from the
> moment it prepares (its rows are known once the far scope's first row is) and until it closes. Expiry is the reader's: rows from a worker that stopped reporting are dropped
> after `MeshTelemetry.ExpireSeconds`. **A game declares nothing:** `Open` is the declaration. The dashboard's Cohesion card lists the rows with their detail; there is no separate
> card.
>
> **As built (NEB-434), D35.** The exchange was already there (NEB-432): the near worker asks for each row another worker owns, the far worker answers once its container is ready
> and, for a row in the band, its side's aperture collider stands, and portal adjacency gives it a ghost of anything in the band. What this issue adds: the ask is per row; the
> commit goes into the deepest row (D16, above), and `Portals.Tick` runs before the container pass of the same tick, so the ordinary handover (`TransferAuthority`, with the
> cohesion group and the carried subtree) leaves in the tick of the commit; and **a carrier with riders this worker does not simulate is refused whole**:
> `Portal.AboardIsWhole` looks at everything in the carrier's box at any depth (`Container.CollectContents`) and at whether the box is pinned to another worker, and
> `PortalRefusal.SplitAboard` is raised with `NebulaDiagnostics.PortalSplitRefusals` (and `PortalRefusals`); a companion with such a crew is left behind with the same reason.
> A rider that crossed with its carrier on a ghost holder, whose recorded history is not rewritten (D18), is still not. **Not built, and why:** the static throat of a split link (each
> worker holds the scene of its own side only, nothing carries the other side's geometry; D27 stands: co-location keeps links off that path) and ghosting of a body's whole
> cohesion group across a portal (the group is carried by the handover itself, D4 of `docs/cohesion-hints.md`; a group member in the band is ghosted as any body in the band is).
> **Measured:** a pawn and a thrown crate through a split link are one handover each on the tick of the commit and are never put back or corrected by more than 0.1 m
> (`PortalSplitCommitPlayTests`). A rigidbody keeps its momentum across the handover only if the prop has a `NetworkRigidbody`, as for any handover.
>
> **As built (NEB-434), D36.** Waits: each `PortalWait` has a situation that reaches it (`PortalWaitReasonTests`), the link and its telemetry row report the same one, and a
> crossing refused for a link that is not open raises `Refused(entity, link, LinkNotOpen)` whose link's `Wait` is the reason. `NebulaDiagnostics`: `OpenPortalLinks`,
> `SplitPortalLinks` (an open link with any row another worker's), `BoundPortalLinks`, `PortalCrossings`, `CrossWorkerPortalCrossings`, `PortalRefusals`, `PortalSplitRefusals`,
> `PortalCloseOverruns`, `PortalHandoffs`. The dashboard lists each link that is not closed in its Cohesion card with its state and wait (D34).

## 9. Frames and precision

**D37 Every portal quantity is a `Double3` and a rotation, applied about the faces, never as a bare translation.** A
link stored as `T + R × p` with `T = o − R × o` loses metres at 10⁸ m: a float rotation is good to about 10⁻⁷, and that
error multiplies `o`. Applied as `N.Origin + R × (p − F.Origin)` (D4), the error multiplies the distance from the face,
which is metres. Face rotations are normalised and rounded to 10⁻⁶ as `InstanceBoundary.PoseRotation` is, so one door
always prepares one definition.

> **As built (NEB-428, NEB-430).** One type holds the maths of a link in one direction: `PortalMap` (`PortalTypes.cs`), `ToOrigin + Turn × (p − FromOrigin)` with both origins `Double3` and the turn the
> near face's rotation times the far face's inverse, applied in double (`PhysicsFrames.Rotate` normalises the float quaternion in double first). `PortalLink.ToNear`, `ToFar` and `Turn` are that map; a crossing, the history,
> the interpolation buffer and the prediction history all go through it, so nothing else holds a link's pose. The pin that the other form is wrong is a test, not a comment
> (`ConformanceFarInstanceFrameTests.TheTranslationFormOfALinkWouldBeOffByMetresFiveTimesTenToTheSeventhOut`): for the door turned 55° about x and 180° about y on a planet 5×10⁷ m out, `T + R p` with `T` baked from the exact
> rotation and applied with the float one is 31 m off, and the pivot form 0.7 µm.

**D38 Geometry is face-relative, so the float parts are small.** An entity's root is read as an absolute `Double3`
(`ContainerRegistry.ToAbsolutePrecise`), the face origin subtracted in double, and only then narrowed to the face-relative
offset `OrientedBox` tests. Commits use the double overloads that exist (`TryCommitTransfer(transfer, frame, Double3,
rotation)`), so an entity lands to the millimetre wherever the scope's frame origin is on that worker. All of it sits on
NEB-426: a pinned frame per instance on workers (`docs/scope-frames.md` D11) and, on a client, an instance sharing the
frame of the scope its pawn entered from.

> **As built (NEB-430).** The aperture test never leaves double: the root is `Double3` absolute, `PortalFace.ToLocal` subtracts the face origin and turns back by its rotation in double, and `PortalAperture.Contains` takes the
> two face-relative doubles, so there is no `OrientedBox` and no float between the entity and the decision (`OrientedBox` stays as the internal helper its own tests cover). An audit of the portal, transfer and preparation paths found no absolute position left in float except
> four, each deliberate: `ScopeDefinition.ObservationCenter` and `InstanceContainerInfo.ObservationCenter` (`(absoluteOrigin + PublicView.center).ToVector3()` in `PrepareInstance`), a wire field that goes with `ObservePublic` in NEB-431 (D24, D47);
> the `PrepareInstance(template, key, Vector3 origin)` overload, whose float is the caller's and is widened exactly; `TryCommitTransfer(transfer, Vector3 position, Quaternion rotation)` and `TryCommitTransfers(..., Vector3 translation)`, the explicit-transfer API
> that has `Double3` overloads; and the float frame positions `ContainerRegistry.ToFrame` hands to `FindDestination` and `ResolveEntry` only to find which container holds a point, small by construction because a scope's frame is kept near what it holds. `PrepareInstance`'s part
> bounds are relative to the origin (small) and are added to it in double, as NEB-426 left them. A crossing lands to the millimetre both ways at 5×10⁷ m with the door turned 55° about x and 180° about y: in, 24 µm from the door's own offsets (the face's
> origin is rounded to a millimetre, so the door is up to 0.87 mm from it); out, 2 µm from where it walked.

**D39 A link pose subsumes an instance pose.** For a template far side, the link's rotation is the instance's pose
against its host (`InstancePoses`, learned from lease rows, `docs/scope-frames.md` D12). `InstancePoses` stays as the
source for it; `Portals.Map(from, to, pose)` and `Portals.Turn(from, to)` replace direct uses for anything
that crosses a portal, and answer for offset portals too.

> **As built (NEB-428, NEB-430).** `Portals.Map(from, to, pose)` (a `PortalPose`, or a `Double3` alone) and `Portals.Turn(from, to)` are in `Portals.cs` and answer from `Portals.TryMap`: first a link a worker holds between the two scopes (the one whose
> face is nearest the pose when several join them), then a portal record a client was sent for a portal with a fixed far side (a template's far face is the near face's origin, so its turn is the instance's pose and needs no record), then the pose of the
> instance one of the scopes is (`InstancePoses`, learned from lease rows, so a process that holds no link answers the same for a template). Scopes nothing relates, and a scope with itself, return the pose unchanged and the identity.
> `Portals.Turn(from, to)` is `InstancePoses.Turn(from, to)` for a template, and is a link's turn for an offset one. A fixed far side maps through the translation too. They landed with NEB-429, whose client path needs the same lookup;
> NEB-430 pins them (`PortalMapTests`) and removes the second copy of the link maths.

## 10. Rendering on the client

**D40 A client draws several scopes, each with its own pose.** The drawn set is the pawn's scope (the view), every scope
one Open link away of which the client is an observer, the public world, and `KeepDrawn` requests
(`docs/scope-activation.md` D29 to D30). `Portals.PoseOf(scope)` is each drawn scope's pose relative to the
*anchor*: the scope whose frame the view's frame resolves to (`ScopeFrames.Resolve`), which is the world the player
first walked in from. It is the composition, in double, of the link poses along the portals from the anchor to the
scope. This replaces the one-view limit of `InstanceViews` and its two cases (outside a turned instance; inside one).

**D41 Drawing poses the drawn scopes that are not the anchor, and the cameras, never the anchor.** While cameras draw
(`SceneRenderOrigin.Drawing`, as `InstanceViews.Begin` is called now), Nebula poses each drawn non-anchor scope's root
containers by its pose and, when the view is not the anchor, the cameras by the view's pose; at the end it puts them back
exactly. Today, from inside a turned instance, `InstanceViews.Begin` turns every scene root except the instance and the
cameras: that is every root of the world, twice a frame, and every static collider under them is dirtied and resynced
by PhysX at the next step though it ends where it started; and the sky stays upright to the instance instead of the
world. Moving the view's scope and its cameras gives the same image for everything posed, moves a handful of
instance roots instead of the world, and leaves the sky, height fog and the sun as they are outside. Root sets are cached
per scope and invalidated when the registry changes. Still drawing only: simulation, prediction and every pose a game
reads stay in each scope's upright space.

**D42 Each drawn scope gets a rendering-layer slot; the game does the rest.** `NebulaConfig.ScopeRenderingLayers` reserves
a mask of rendering-layer bits (0, the default, turns this off; eight bits allows eight drawn scopes). Nebula gives each
drawn scope a slot (the view's is stable; others take one when first drawn and free it when no longer drawn) and stamps
that bit, and only that bit, on the renderers it already manages: an instance's static content, a scoped grid chunk's
content under `ChunkContext.Root`, and entities' renderers when they spawn or change scope (cached per identity).
`Portals.RenderingLayerOf(scope)` and `SlotChanged` tell the game. The game owns lights and their masks, how the
look blends between scopes (it has `PoseOf` and the faces to measure the camera against), and any offset-portal mask:
an offset portal (D4) needs the far scope clipped to the aperture, which is a renderer feature of the game's, not Nebula's.

## 11. Lifetime and persistence

> **As built (NEB-428), D43 and D44 in part.** `WorkerScopeLifecycle.IsBusy` is true for a container a live link (preparing, open or closing) enters
> (`Portals.HoldsFarContainer`), so a door left open onto an empty room does not lose the room. An open link whose far scope begins to retire closes with
> `FarScopeRetired` within a second; a preparing link waits for the scope it just asked for. When the near container's owner changes, the links read
> Closed with `NearContainerMoving` and the portal is withdrawn from the network gateways; the `PortalHandoff` message that carries them to the new
> owner is NEB-434's.

> **As built (NEB-434), D44.** `MsgId.PortalHandoff` (52, `PortalHandoffMsg`: the portal's id and near container, and for each party its link's generation, state, members, the members
> that were warm and the observers) is built. In the tick `UpdatePortals` finds a portal's near container owned by another worker, the releasing worker sends it to that worker (when its
> worker link is connected), describes every link of the portal in it (a closed one too, so the generation goes on), lets its own copies go (they read `Closed` waiting on
> `NearContainerMoving` for whoever holds one) and keeps no record of the portal. The network gateways are **not** told it is gone for a second (`DeferredRemovalSeconds`): the
> new owner's upsert replaces the record (a record belongs to the worker that last published it) and a late removal from the old one is ignored, so a client never sees the link
> vanish. The new owner (`OnPortalHandoff`) installs the links as soon as it holds the portal's content and owns the container's lease, waiting up to 15 s for either
> (`PendingHandoffSeconds`): an open, preparing or closing link comes back **Preparing waiting on `NearContainerMoving`**, with its members, warm set, observers and generation, and
> opens on its first look if the far scope, the leases, its own side of the aperture and the content are as the link needs them; the warm members are not asked again. A closing link
> keeps closing; a closed one keeps only its generation. With no owner, a new owner whose worker link is not connected, or a crash, nothing is sent and the links read `Closed` waiting on
> `NearContainerMoving`. Two workers in the in-process test mesh share the portal component (the static registry), so the tests see the new owner's links on the same object: what
> they pin is what each worker does with the links it is given. A game's handlers on the old worker's link object see it close; read the link again with `Portal.TryGetLink`.

**D43 Scope lifecycle is unchanged, and an open link counts as interest.** A link that is Preparing, Open or Closing keeps
its far scope from being judged idle, as an occupant does (`docs/scope-lifecycle.md`). A far scope that is retiring or
retired makes its links read Closed with `FarScopeRetired`, which makes the aperture solid. A portal goes with its near
container's content; its links close and their far scopes follow the ordinary lifecycle.

**D44 Nothing new is persisted.** Portals are content: the game's content recreates them. Links are runtime state and read
Closed after a restore or a restart. On a planned reassignment of the near container, the releasing worker hands its
links to the new owner (`PortalHandoff`, worker to worker, with the release), whose wait is `NearContainerMoving`
meanwhile; on a crash they are lost and read Closed, which is the safe state.

## 12. Wire and compatibility

**D45 Three client messages, two internal ones, and two trailing fields.**

| Id | Message | Direction | Carries |
|---|---|---|---|
| 27 | `Portals` | worker → network gateway → client | upserts and removals: portal id, near face (container ref, pose: `Double3` + rotation), far side (fixed scope id and far face, or template), aperture, band and window sizes, flags |
| 28 | `PortalLinks` | worker → network gateway → client | per link: portal id, party hash, far scope id and entry container ref, state, wait; to the network gateway the observer list, to a client only whether it is an observer |
| 29 | `PortalWarm` | client → network gateway → worker | link id: this client holds the far window and its content is ready |
| new | `PortalAdjacency` | worker ↔ worker | the band a link adds between two workers' containers (D30) |
| new | `PortalHandoff` | worker → worker | a near container's links on a planned reassignment (D44) |
| — | `InstancePrepare`, `InstanceReady` | worker ↔ worker | a trailing link id (D35) |

The network gateway sends a client a portal's record and its links on the reliable stream behind the row of either face
(as `docs/scope-activation.md` D14 and D15 order rows before what names them), and withdraws them with the row. Spawns,
state entries and container rows are unchanged.

> **As built (NEB-428).** The records: `PortalRecord` carries the derived id, the authored id, the near scope and container (by id *and* reference),
> the near face, the far side (fixed: scope id and far face; template: the template id), the aperture, the band and window sizes and a reserved flags
> byte. `PortalLinkRecord` carries the link, portal and party hash, the far scope, the entry container (by id and reference), the state, wait, the member
> the link waits for, progress as a byte, the generation of the opening, a flags byte (`Member`, `Observer`, `ObserverAnyone`), and, toward the network
> gateway only, the members and the observers. `PortalWarm` carries the link id, the generation and, forwarded by the gateway, the client id it stamps.
> `PortalAdjacency` (NEB-432) and `PortalHandoff` (NEB-434) are built; `PortalAdjacencyMsg` gained a trailing list of the other far-window rows (NEB-434) and `PortalLinkRecord` the rows of the link (`FarRows`, `FarRowIds`). The network gateway keeps the records and sends them per client as D45 describes, a
> link to a member only once it holds the far row; a worker publishes to every gateway on connect (`AddGatewayLink`) and on change.

**D46 Protocol 28, and older clients are refused.** `HelloMsg.ProtocolVersion` becomes 28 and `MinProtocolVersion` 28: the
window closes, as `docs/compatibility-policy.md` D1 allows before 1.0, and a client of 27 is refused with
`ProtocolUnsupported`. The bytes alone would be additive (new message ids), but the meaning is not: a 27 client would be
sent replicas of scopes it believes hidden and never draw them posed, and would predict its pawn into an aperture it does
not know is solid. Decided by the project owner; no compatibility window. The usual steps of `docs/protocol-versions.md`
apply: a `protocol-28-handshake.json` recording, a frozen decoder for the next bump, the table row.

> **As built (NEB-428).** The handshake recording is `protocol-28-handshake.json`, the frozen decoder is `Fixtures/Protocol28GatewayDecoder.cs`, and the
> protocol-27 recording is kept to prove a client of 27 is refused with the gateway's range (28..28). The protocol-25 and -26 recordings and decoders
> were removed with the window they served. The table row and the paragraph are in `docs/protocol-versions.md`.

**D47 What 27 added stays.** The pose section of `ContainerOwnership` (`docs/scope-frames.md` D12) still carries
`InstancePoses` to clients, now as the source of a template link's rotation (D39). The `ObserveHost` flag byte
leaves lease rows and the `ObservePublic` fields leave scope definitions under protocol 28 (NEB-431); nothing a client
reads changes.

> **As built (NEB-431).** `InstanceContainerInfo.Write`, which is what a client is sent, loses the `ObservePublic` flag and the two observation vectors (25 bytes), so an instance row is shorter under 28; a stored lease row's
> trailing flags byte keeps only the pose bit (value 1). The frozen protocol-28 decoder skips instance rows and the recorded handshake holds none, so the fixture is unchanged.

> **As built (NEB-428), §14 item 3.** `InstanceScenes.Prepare` creates the scope's physics scene and moves the container into it when it is first
> called (`SceneManager.CreateScene` has no asynchronous form, so this stays synchronous, but it happens while a link prepares and not in a commit), and
> loads the content with `Resources.LoadAsync` and instantiates it with `InstantiateAsync`, one step each time `ContentState` is asked; a missing
> resource is found when the load finishes and reads `Failed`. The new `InstanceScenes.Complete` is the synchronous fallback, used by
> `NetworkIdentity.SetContainer` and by every commit, so an entity placed in a container nobody warmed still never stands on geometry that does not exist.
> A client's `PortalWarm` polls `ContentState` once a frame. The play-mode tests are `Tests/PlayMode/InstanceAsyncContentPlayTests.cs`.

## 13. Hitch budget

Requirements, measured at every crossing and every door open, on one worker and on two:

- **R1** no client frame over 33 ms;
- **R2** no worker tick over 16 ms;
- **R3** no prediction correction over 0.1 m.

How the design keeps them, and what a test pins:

| Where a hitch could come from | What keeps it out | Pinned by |
|---|---|---|
| content load at a crossing | the far side is warm before Open (D10); a commit loads nothing (D16) | no `Instantiate` or `Resources.Load` during a commit tick |
| content load at preparation | sliced: `IInstanceContentLoader`, and `InstanceScenes.Prepare` made asynchronous (§14, item 3) | worker tick ≤ 16 ms while a large far scope prepares |
| proxies | pooled, built while Preparing (D26, D27); Open is a state flip | no allocation when a body enters the band |
| a hold at the face | none (D16, D17) | a crossing commits on the tick the root passes |
| a snap at the commit | pose, velocity, history, interpolation and prediction history go through the link (D18) | correction at a crossing ≤ 0.1 m, replica speed continuous |
| drawing | posing a few roots, not the world (D41) | client frame ≤ 33 ms with two drawn scopes |
| cross-worker | co-location (D34); a split commit is one handover (D35) | correction ≤ 0.1 m on a split link |
| the crossing test | portals indexed by container (D15), not every portal for every entity | worker tick with 1,000 entities and 50 portals |

## 14. Where the current code fights the design

1. **Only pawns, every boundary, every entity.** `InstanceBoundary.Tick` returns for `OwnerClientId == 0` and asks every
   `Active` boundary about every entity. NEB-429 drops the owner test and registers portals on their container chain
   (D14, D15).
2. **Per-entity preparations hold at the face.** `NebulaWorker.ValidTransfer` requires `entity.Container ==
   transfer.Source` and the same epoch, and a preparation expires after 15 s; `InstanceBoundary.Cross` writes
   `entity.transform.position` back to `crossing.LastPosition` while it waits. The link's standing preparation (D10, D16)
   replaces both, and `InstanceBoundary` goes with them (D7).
3. **Content is loaded synchronously, in the tick, on first prepare.** `InstanceScenes.Prepare` creates the physics scene
   lazily and does `Resources.Load` and `Instantiate` of a part's whole prefab inside the worker tick (and on a client
   when the row arrives). A large interior breaks R2. NEB-428 moves the scene's creation and the prefab load into
   Preparing, asynchronously (`Resources.LoadAsync`, `InstantiateAsync`), counted in D10's second condition.
4. **Revocation at a scope change.** `docs/scope-activation.md` D13 and `NebulaGateway.CanObserve`'s single-scope test
   (`(target?.InstanceId ?? 0) == scope`, then `ObservePublic` in scope 0 only) mean a crate thrown into a room is
   despawned for its thrower at the commit. Until NEB-431 lands, NEB-429's crossings show exactly that; D19 and D21 fix it.
   *Fixed by NEB-431.*
5. **One salt per client.** `ClientInterest.ScopeSalt`, `UpdateClientRegions` and the foci keys salt with the client's
   one scope; D23 makes the salt per window. *Done in NEB-431: an `InterestFocus` can carry its own scope.*
6. **No adjacency across scopes.** `ContainerRegistry.Link` refuses it, by design, and stays so; portal adjacency is a
   second relation with its own band test (D30), so the ghost band changes only where it consults it.
7. **The world is turned to draw.** `InstanceViews.Begin` turns every scene root from inside a turned instance; D41 poses
   the view and its cameras instead.
8. **Host observation lives on the connection.** `ClientConn.HostViews` and `HostCarriers` are per-connection history;
   link records replace them (D24). *Done in NEB-431.*
9. **History is left behind.** `StateHistory` entries hold a world pose and their container (`docs/state-history.md` D4)
   and are not touched at a crossing; D18 rewrites them.
10. **The crossing test is a volume.** `InstanceBoundary.InteriorContains` makes the whole `Interior` box the inside, so
    an entity is "in" whatever face it came through. A plane aperture
    is a different, stricter rule, and content ported to portals must have its door on the face (D7).
11. **Companions move by translation only.** `InstanceBoundary.Commit` commits the group with one displacement
    (`TryCommitTransfers(Group, therePrecise − entity.ScopePositionPrecise)`), which preserves each member's rotation,
    so through a turned door a held item arrives unturned. D18 maps every member's full pose.
12. **Ghosts are bodies in their own scope.** `SetGhostPhysics` makes a ghost kinematic where it is; a ghost across a
    portal must be a proxy in the other scope's scene instead (D29).
13. **Physics scenes do not exist in EditMode.** `InstanceScenes.Prepare` keeps containers in the active scene when not
    playing, so tier-B EditMode tests cannot test proxies or cross-scene contacts. NEB-432's tests need a play-mode
    fixture or a seam that creates local physics scenes, as `AdoptContent` is a seam for content.

    *Done in NEB-432 with a play-mode fixture:* `Tests/Shared` is a new test assembly (`Nebula.Tests.Shared`) holding `ConformanceMesh` and `PortalHarness`, which both the EditMode and the PlayMode
    assemblies reference, so a PlayMode test runs real `NebulaWorker`s on the in-process mesh with real scenes and real PhysX (`Tests/PlayMode/PortalPhysicsRig.cs`: a street with a hill and a tunnel, a hall,
    physics stepped by the test once to each worker tick). The rules that need no scene are EditMode.
14. **Link state has no home on a reassignment.** A container's lease moves without any state of its own; D44 adds the
    handoff.

## 15. Implementation plan

Milestones 1 to 3 land as one Nebula branch, `jesse/portals`, with one commit per issue, merged once, and the game is
ported in the same pass (D7). Nothing between the commits needs to be shippable, but each commit must compile and keep
the Nebula test suites green, minus tests deleted with removed features. Conformance scenarios get the next free numbers
of `docs/conformance-suite.md` (50 onwards) in the commit that implements them.

**Milestone 1, portals and anything crosses.**

- **NEB-428 `Portal` replaces `InstanceBoundary`.** The component, faces, plane apertures (`Box`), party links and their state machine with wait reasons (§3), the standing preparation (D10), asynchronous content
  preparation (§14, item 3), messages 27 to 29 and protocol 28 with the window closed (§12). `InstanceBoundary` is removed
  (D7) and the game's doors move onto `Portal`. Tests: the existing `InstanceBoundary*` tests' scenarios are ported to
  `Portal`, and `ConformanceFramedInstanceBoundaryTests` becomes a portal conformance test; a state-machine test of every transition and wait; tier A (fake mesh, real network gateway): a portal's
  record and link state reach clients holding either face's row and only those, a non-member gets no observer bit, a
  27 client is refused; `ConformanceProtocolCompatibilityTests` on 28..28.
- **NEB-429 Any entity crosses on its own.** D14 to D19 except the observation half of D19: the swept and predictive test,
  server-owned entities, carriers with seated riders and followed contents, companions through `ResolveCompanions`,
  refusal by collider and clamp, velocity, history, interpolation and prediction history through the link. Tests (tier
  B): a server-owned crate pushed through an open plane aperture commits on the tick its root passes, with no
  `PrepareTransfer`, no hold and nothing instantiated; a projectile at 300 m/s crosses without a step behind the face; a
  ship with two riders crosses with them seated; a refused crate is stopped and `Refused` raised; a history read at the
  crossing tick answers in the destination's space; the pawn's correction at a crossing ≤ 0.1 m.
- **NEB-430 Geometry in double and oriented.** D4, D37 to D39: faces in double, the pivot form, face-relative
  `OrientedBox` tests, `Portals.Map` and `Turn`. Tests: a portal 5×10⁷ m out on a door turned 55° about x and 180°
  about y, crossings both ways land to the millimetre with rotation and velocity turned (extending
  `ConformanceFarInstanceFrameTests`); a pin that the translation form would have been off by metres there.

**NEB-428 as built, and the seams it leaves.** `Portal`, `PortalFace`, `PortalAperture`, `PortalLink`, `PortalState`, `PortalWait`, `PortalRefusal` and
`Portals` (`Runtime/Containers`), the worker's side (`Runtime/Worker/PortalLinks.cs`, `PortalCrossing.cs`, `NebulaWorker.Portals.cs`), the messages
(`Runtime/Protocol/PortalMessages.cs`), the network gateway (`Runtime/Gateway/GatewayPortals.cs`) and the client (`Runtime/Client/NebulaClient.Portals.cs`).
For the issues that follow: NEB-430 audits the face and link maths (`PortalFace`, `PortalLink.ToNear/ToFar/Turn`, already in double and in the pivot form of D37) and adds the far-out tests;
NEB-431 widens the far window from the entry container to the rows a window touches (`GatewayPortals.cs`:
`HoldsLink`, `AddMemberRows` in `CollectNeededContainers`; `NebulaClient.Portals.cs`: the container prepared), uses the observer lists the records
already carry in `NebulaGateway.CanObserve`, and removes `ObservePublic`, `ObserveHost` and `HostCarriers`; NEB-432 sets the throat and aperture-collider
condition (`Portal.Evaluate`, condition 4, true today), replaces the roots-only band test (`Portal.BandOccupied`) with proxies and replaces `Portal.Stop` for a physical body
with the aperture collider.

**NEB-429 as built, and the seams it leaves.** The crossing test, the index and the stop are in `Portals.cs` (`Tick`, `EnsureIndex`) and `PortalCrossing.cs` (`Cross`, `Passes`, `TryCross`, `Stop`); the
commit is `NebulaWorker.CommitThrough(members, destination, PortalMap)`; what travels with an entity is `NetworkIdentity.CapturePortalState / MoveThroughPortal / FinishThroughPortal`
(the authority) and `CarryReplicaThroughPortal` (a copy, from `SetContainer`), with `CarryThroughPortal` on `StateHistory`, `RemoteInterpolator` and `PredictedBehaviour`, and `Portals.CarryLocal`
for the poses they hold. `Portals.TryMap` finds the link between two scopes (a worker's links, then a client's portal records, then `InstancePoses`) and is what `Portals.Map` and `Portals.Turn` answer from.
For NEB-431: a commit changes an entity's scope and bumps its epoch exactly as before, so `RevalidateInterest` (D13) still runs before anything about the destination is relayed; the thrower's window
keeps the entity by consulting the link records, and a copy in a window that then crosses is carried by the same `SetContainer` hook, because both scopes are ones the client holds and `Portals.TryMap` relates them. The hook
only fires for an entity within `Portals.CarryRange` (48 m) of the face it used, which is the window's depth, so a replica a window shows is carried when it crosses at the face, not when it stands deep in the window. A crossing is
announced to the game by `Portal.Crossed` and `Portals.Crossed`; neither tells a client that a crossing was refused (`Refused` is worker-side). A seated rider's `StateHistory` on a ghost holder is not rewritten
(NEB-434).

**NEB-431 as built, and the seams it leaves.** The windows are `Runtime/Protocol/PortalWindow.cs` (`PortalWindow`, `PortalGeometry`, `PortalRecord.NearWindow/FarWindow`), the network gateway's side is
`Runtime/Gateway/GatewayWindows.cs` (`RefreshWindows`, `ObservableThroughWindow`, `AddPortalWindows`, `CollectWindowCarriers`, `ReviseWindows`, frame windows) with the hooks in `NebulaGateway.CanObserve`,
`GatewayInterest.cs` (`EvaluateClient`, `UpdateClientRegions`, `UpdateSubscriptions`, `CollectNeededContainers`, `ApplyInterestChanges`, the eviction sweep) and `GatewayPortals.cs`, and the client's
holding is in `NebulaClient.cs` and `NetworkIdentity.ReceiveState`. Tests: tier A, `ConformancePortalWindowTests` (8: both ways, non-observers and the pawn outside the window, walking in and out, one portal deep, closing, a reconnect inside,
`MaxPortalWindows`), `ConformancePortalCrossingGatewayTests` (3: six crossings with no despawn or respawn for the thrower, the onlooker's wire, closing after a crossing), `PortalWindowFrameTests` (a window onto a
planet's ground: the frame box, the rows of that ground and the planet only, the authorization through the frame); tier B, `PortalWindowTests` (geometry to the millimetre 5×10⁷ m out, what the worker publishes,
`Portal.AddObserver`) and `ClientHoldsUnresolvedContainerTests` (conformance scenarios 55 to 57). For the issues that follow: NEB-432's proxies are in the band, which is inside both windows, and read the window records the same way;
NEB-435 draws the windows' scopes and finds them by `Portals.Known` and the links a client holds (`Observer` is on its link record); NEB-437 needs nothing in observation, because each window is measured in its own face's axes. **Not built:** the
deepest far container as a crossing's destination (D16, above); a window whose frame is nested in another frame; `Closing` links are windows both ways like `Open` ones, as D9 says, but a client is not told the difference.

**NEB-432 as built, and the seams it leaves.** The engine is `Runtime/Containers/PortalPhysics*.cs` (the worker's `Step`, the throat and the aperture colliders, the pair rule, the adjacency rigs, the client's
`StepClient`) with `PortalBodies`, `PortalBand`, `PortalShape`, `PortalPairRule`, `PortalProxy`, `Portal.Physics.cs` (`Validate`, `RefuseOnContact`) and, on the worker, `Worker/PortalAdjacency.cs` and
`NebulaWorker.PortalAdjacency.cs`. Tests: `Tests/PlayMode/PortalPhysicsPlayTests` (14: resting across the face and sleeping, a proxy re-posed only when its source moved, pushed through with no jolt and the proxy
swapping sides on the commit tick, a closed portal, a refused rigidbody bouncing, a refused `CharacterController` stopped while a member walks through, a body resting on another's proxy, a dropped body not squeezing
the one below, the throat and apertures built before Open and dropped at Closed, `Validate`, pooling, the bound, an observation-only portal, leaving back out), `PortalPhysicsAcrossWorkersPlayTests` (4: a body in the
band is a proxy on the far worker and nothing outside it is ghosted across, the same the other way, adjacency is a separate relation, a crate pushed through a split link arrives as a body again), `PortalPhysicsClientPlayTests` (3: the client's proxy, throat and aperture, posed
from the newest sample), `PortalPhysicsLogicTests` (15: band, pair rule, pool, shapes, bodies by extent, adjacency, `Validate` in the Editor, the adjacency message) and `PortalBandOccupiedTests` (2), conformance
scenarios 58 to 61. The play-mode tests run on `Tests/Shared` (`ConformanceMesh` and `PortalHarness`, moved out of the EditMode assembly so both assemblies reference them). **Deviations from the design**, each
recorded at its decision: the pair rule is the faster body's scene, not the literal "ignore the proxy of a body whose own proxy it touches" (D26); the throat copies only the other side's own half of the band, and a
split link has none (D27); a refused body that has colliders is stopped by the aperture collider and `Refused` is raised from contact, the clamp staying for the rest (D17). A side that simulates in a physics frame (a planet that is a carrier) was a fourth deviation of the first version; it is built now (D26, as built; `FramedPortalPhysicsPlayTests` (9), `FramedPortalAcrossWorkersPlayTests` (2), `FramedPortalClientPlayTests` (2), conformance scenarios 62 and 63). **Seams for the issues that follow:** NEB-433's queries skip `PortalProxy` colliders (`PortalProxy.Is`) and hit an aperture collider as the closed plane (`PortalProxy.IsAperture`,
`PortalPhysics.NearApertureOf`/`FarApertureOf`); NEB-434 should extend the ghosting here to a body's whole group and give a split link its throat, and is where `PortalHandoff` is built; NEB-435's drawn
scopes need nothing of the physics, which stays in each scope's upright space; NEB-437's offset portals are already carried by the pivot form (`PortalFaceFrame` and the two turns), the proxies and the aperture
colliders need no change for a translation.

**Milestone 2, seeing and acting across.** The three issues are independent.

- **NEB-431 Observe both ways.** §5: windows, the `CanObserve` rule, per-window salts. Removes `ObservePublic`, `PublicView`,
  `ObserveHost`, `ClientConn.HostViews` and `HostCarriers`, with the `ObserveHost` flag byte of lease rows and the
  `ObservePublic` fields of scope definitions (D24, D47). Tests (tier A): a member in the near window receives the far window's rows, carriers and
  entities and not what is outside it, and the reverse from inside; a non-member receives nothing through; one portal
  deep; closing revokes; a crate thrown in stays for its thrower while a non-member onlooker loses it and never hears of
  its destination (`docs/scope-activation.md` D13 kept); a client reconnecting inside sees out again.
- **NEB-432 Proxies in the band.** §6 and the NEB-232 decision: proxies, the throat and its validation, the aperture
  collider, portal adjacency and proxy ghosts. Tests (play mode): a crate rests straddling an open face; a body pushed
  through crosses without a jolt; a closed portal stops a body; a refused body bounces; on two workers a body in the band
  is a proxy on the far worker and nothing outside the band is ghosted across.
- **NEB-433 Queries across.** §7. Tests: a ray through an open aperture hits a far entity, at its rewound pose with a
  tick; a closed aperture is hit at the plane; proxies are never hit; a face with two parties' links casts into the
  asker's party's scope; a far leg beyond the band on another worker is `Truncated`.
  *As built:* `Tests/PlayMode/PortalQueriesPlayTests` (12), `PortalQueriesRewindPlayTests` (6), `PortalQueriesWorkersPlayTests` (3, and `FramedPortalQueriesPlayTests` 2), `PortalQueriesClientPlayTests` (2),
  `Tests/EditMode/PortalQueriesLogicTests` (8), conformance scenarios 64 to 67. The `LagCompensatedHitscan` sample is a `PortalQueries.Raycast` with a tick and keeps no scope filter of its own. Deviations from
  the design as written, each recorded at its decision: `PortalQueries` is an instance for each process, not a static class (D32); points are scene-space floats about the faces, not `Double3` (D32); a
  leg in the band of a split link is not rewound (D33). What a game does to adopt it is in the instancing guide, "Cast through a portal".

**Milestone 3, across workers and on the client.**

- **NEB-434 Co-location and cross-worker commits.** D34 to D36. Tests (tier B, two workers): a link that is not Closed
  binds its containers into one item; with the far scope forced onto the other worker, a crossing commits through one
  handover with no hold and a correction ≤ 0.1 m; every wait reason is reported in its situation.
  *As built:* the cohesion bindings, the planner and the dashboard (D34, D36: `Runtime/Worker/WorkerCohesion.cs`, `WorkerTelemetry.cs`, `MeshTelemetry.cs`, `NebulaOrchestrator.cs`,
  `NebulaDashboard.html`), the per-row readiness and the deepest-container commit (D10, D16: `Runtime/Worker/PortalLinks.cs`, `PortalCrossing.cs`, `NebulaWorker.Portals.cs`,
  `NebulaClient.Portals.cs`, `GatewayInterest.cs`), the refusal of a split crew (D35: `Portal.AboardIsWhole`, `PortalRefusal.SplitAboard`) and `PortalHandoff` (D44: `Runtime/Worker/NebulaWorker.PortalHandoff.cs`).
  Tests: EditMode `PortalCoLocationTests` (5), `PortalSplitCommitTests` (5), `PortalDeepestFarContainerTests` (6), `PortalHandoffTests` (5), `PortalWaitReasonTests` (2),
  `ConformancePortalCohesionTests` (8, also in `Nebula.Services.Tests`), `PortalWireTests` (3 more), and the member's pin of every row in `ConformancePortalGatewayTests`; PlayMode `PortalSplitCommitPlayTests` (2);
  conformance scenarios 68 to 73. **Deviations from the design as written**, each recorded at its decision: the far window's rows are the leased containers its box overlaps, at most 16 and frozen while the link
  is open (D10); the binding is reported from the moment a link prepares, in the Cohesion card, not a card of its own (D34, D36); `SplitPortalLinks` counts a link split by any row, not the entry only (D36); the
  network gateways keep the old worker's records for a second after a handoff (D44). **Seams left:** a split link still has no static throat and contact across it is one-sided (D27, D31); a rider's
  history on a ghost holder is not rewritten (D18); a row that appears after a link opened is entered only after the link prepares again; offset portals (NEB-437) change none of it, because every row is
  found in the far face's own axes.
- **NEB-435 Several drawn scopes.** §10: `PoseOf`, posing non-anchor roots and cameras, rendering-layer slots. Removes
  `InstanceViews`' scene-root turn (§14, item 7). Tests: extend `ConformanceFarInstanceClientFrameTests` with two turned
  scopes drawn at once and from inside a turned scope; the anchor's roots are never moved; slot bits are stamped and nothing else of the mask changes.
- **NEB-436 Cleanup.** Fold in NEB-405, NEB-308, NEB-408 and the HVR-674 leftovers; update the instancing, scopes and
  physics guides and `docs/conformance-suite.md`. The old paths are not here: each is removed by the issue that
  replaces it (NEB-428, NEB-431, NEB-435).

**Milestone 7, offset portals.**

- **NEB-437 Non-coincident link pose.** Every reader of a link pose honours its translation: crossing, windows (D21 for
  an offset portal), proxies, queries, `PoseOf`. Tests: an offset portal between two places in one scope and between two
  scopes; a crate goes through both ways; a ray and an observer see through; a proxy straddles; precision at 10⁸ m on both faces.

## 16. What this supersedes

| Was | Now |
|---|---|
| `docs/scope-frames.md` §7: seamless scope-to-scope travel is not in scope | it is, through a portal; curved coordinate systems still are not |
| `docs/scope-activation.md` D24: rows only, no host entities | windows, rows and entities both ways (D20 to D24) |
| `docs/scope-activation.md` §12, `ObserveHost`, `HostViews` | the near side's window of the link (D24) |
| `docs/scope-frames.md` D12, `HostCarriers` | carriers of a window's rows (D20) |
| `ObservePublic` and `PublicView`, scope 0 only | the host side's window, any scope (D24) |
| `docs/scope-frames.md` D12, drawing by turning the world | `PoseOf` and posed roots and cameras (D40, D41) |
| `docs/scoped-chunk-grids.md` §7: no ghosting or interaction across scopes | adjacency at portals only, within a band (D30) |
| `InstanceBoundary` | removed; `Portal` (D7) |
| NEB-370 sibling observation, NEB-371 cross-worker crossings, NEB-232 adjacency research | D20, D35, D30 |

## 17. Seams kept open

None of these is built here; each must stay possible without redoing what is.

- **Something that carries a crew through.** A carrier crosses with every rider and everything aboard (D14), so anyone
  aboard a vehicle passing through a large aperture enters with it. A large body is mirrored by its extent (D26) and
  crosses by its root.
- **One door, one far scope per party.** D8: one near face, one link and far scope per party; observation and admission
  per link (D11, D12).
- **Observation-only portals.** A flag that keeps the aperture solid for everyone (D28) and admits nothing, with windows
  as usual: a window in a wall.
- **Something following a player through.** Admission is `CanEnter`'s (D11), which the game writes; a server-owned
  entity crosses like any other (D14).
- **A door on a moving carrier.** A face is fixed in a container (D2), so a door in a vehicle's hull moves with it; a
  face on a frame that turns is open question 10.

## 18. What the HVR-683 spike measured

A throwaway prototype (not on a branch meant to merge) threw a server-owned crate through a coincident portal into an
instance and back, on one worker, at a door turned 54.7° against its host's frame, with kinematic proxies in a 2 m
band. What it changed in this design:

| Finding | Number | Where it lands |
|---|---|---|
| An unowned entity crosses by itself, keeping its id, persistence key and game data | both directions | D14, D16 |
| A standing preparation makes a commit load nothing; a late one holds at the face | ready in 1 to 2 ticks; a late request held 0.33 s | D10, D16 |
| Moving a body between physics scenes is cheap | `SetContainer` 0.15 to 0.35 ms, scene move 0.04 to 0.09 ms (worker); 0.2 to 0.9 ms (client) | no change |
| A rigidbody's velocity was not turned through a turned door | the crate dove into the floor | D18 (already required) |
| Container and pose must change in the same step on the client | one frame 0.34 to 0.9 m off | D18 |
| Observing across needs subscription, and the record must survive the scope change | despawned without; blinked 0.3 s in 2 of 6 with a policy hack | D19, D23 |
| A crossing into a host scope held as rows only lands in the wrong frame | replica about 1.2 km off | D20 (carriers come with the window) |
| Proxies are cheap and accurate | create about 0.4 ms (pooled), keep 0.02 ms a tick, 1 cm pose error | D26 |
| The client must predict against proxies posed from the authoritative tick | 52 corrections in 2.5 s without, 29 with interpolated poses, 0 control | D25 |
| A body on a proxy re-posed every tick never sleeps; mirrored pairs push through | 0.8 to 1 m/s jitter; 0.9 m squeeze | D26 |
| The volume containment's 1.2 m exit margin strands slow bodies in the far scope | a slow crate stopped outside still in the instance | D15 (plane, 5 cm); the volume containment is gone (D7) |
| First spawn and first replica instantiate dominate, not the crossing | 34 ms worker, 64 to 74 ms client, once | §13: prewarm what crosses with the far content |

The hitch budget could not be judged on an Editor-hosted worker, which peaks at 16 to 21 ms with nothing crossing; the
crossing step added at most 1.9 ms. R2 is measured on a built worker (milestone 8). A 1.6 km pawn correction was seen
once leaving the instance and was not isolated; NEB-431's window tests include a pawn leaving while observing the host.

## 19. Open questions

Jesse approved the design as written on 2026-10-12, so every question below takes the default this document proposes,
except Q5 and Q11, which he answered.

1. **Sizes.** `BandDepth` 2 m, `CrossDepth` 5 cm, `WindowMargin` and `WindowDepth` (proposed 8 m and 48 m),
   `MaxPortalWindows` 4, `CloseTimeoutSeconds` 5. The HVR-683 spike used a 2 m band and found it enough for a door-sized crate (§18); the window sizes were not measured.
2. **Default observers.** Members only (D11). Should a door shared by several parties let bystanders look into an open
   party's room by default, or only when the game says `Anyone`?
3. **Party of an unowned entity.** D12 refuses an unowned entity at a face with more than one open link unless `PartyOf`
   names a party. Is refusal the right default, rather than the most recently opened link?
4. **Crash.** A crashed near worker's links read Closed (D44), and anything straddling is pushed out when the far side
   comes back. Acceptable?
5. **The shim's future.** Answered by Jesse on 2026-10-12: there is no shim. `InstanceBoundary` is removed and the game
   ports to `Portal` in the same pass; there is no `Volume` containment (D7).
6. **Far-leg queries on a split link.** D33 truncates a cast that leaves the band on another worker. Acceptable, given D34
   keeps links co-located, or does it need an asynchronous `WorkerQuery` leg?
7. **Rendering layers.** Should Nebula stamp renderers (D42), or only assign slots and let the game stamp? Is eight slots
   enough?
8. **Lag compensation.** Nebula rewinds the far leg by default and the near leg only on request (D33). Should it own both?
9. **Client throat.** D27 has the client build the static throat for prediction. That is content work on the client at
   preparation; is it worth it, or should prediction at the face accept the worker's correction?
10. **Turning frames.** `docs/container-tree.md` already asks for doors on frames that do not turn. Does a face on a
    turning carrier need to work, or is that a documented limit?
11. **NEB-431 before NEB-429?** Answered by Jesse on 2026-10-12: they ship together. NEB-429 and NEB-431, with the rest
    of milestones 1 to 3, land as one branch, one commit per issue, merged once (§15), so the gap of §14, item 4 never
    ships.
