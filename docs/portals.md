# Portals — design

Status: design of record for **NEB-427**, protocol **v28** (new messages; the client window closes to 28..28, see §12).
Nothing here is implemented yet: §15 maps it onto NEB-428 to NEB-437. Decisions made without asking are marked **D#**.
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

**D3 The aperture is an oriented box in the face's axes, centred on the face's origin, the opening on its z = 0 plane.**
`PortalAperture` holds the half extents across the opening (x, y) and a `Kind`, `Box` today; any convex shape is
a later kind with the same contract (a cross-section test and a bounding box). Three regions derive from it, each in
face axes:

| Region | Shape | Used by |
|---|---|---|
| cross-section | the opening: \|x\| ≤ hx, \|y\| ≤ hy, z = 0 | crossing (§4), queries (§7) |
| band | cross-section grown by `BandMargin` sideways, \|z\| ≤ `BandDepth` (2 m) | proxies (§6), cross-worker ghosting (D30) |
| window, per side | cross-section grown by `WindowMargin` sideways, extruded `WindowDepth` into that side | observation (§5) |

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
is what lets the `InstanceBoundary` shim (D7) find every instance a game has already persisted.

**D6 A portal's id is derived, not allocated.** `PortalId = ScopeKeys.Hash(containerId + "#" + Id)`, where `Id` is
authored and unique within the near container (a second component with the same id is ignored and logged). Every
worker, network gateway and client computes the same id from what it already has, as `docs/scope-activation.md` D3 does
for container ids.

**D7 `InstanceBoundary` becomes a shim over a portal, with its old semantics.** It builds a `Portal` with a
template far side, party = `ResolveKey(entity)` (or the owner's identity, or `SharedKey`), and one extra aperture
containment kind, `Volume`, used by nothing else: the far side is the inside of the `Interior` box (turned with the
boundary, widened by `ExitMargin` on the way out), not a half-space beyond a plane. Its public members keep their
meaning: `Request` and `RequireRequest` drive the link open and closed, `CrossingReady`, `TryGetPreparation` and
`InstanceCrossingState` report the link's readiness in the old statuses, `ResolveCompanions` and `CompanionLeftBehind`
map onto D13, `CanEnter` onto D11. An entity that reaches the face of a link that is not open is held where it last
stood, as today, only under `Volume`. It exists so that a game compiles and behaves the same on the release that
introduces portals; new content uses plane apertures, and the shim's fate is open question 5.

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

## 4. Crossing

**D14 Anything whose root is the root of its carrier chain crosses.** Every authoritative, spawned entity that is not an
authored scene entity, not pinned (`docs/frame-bodies.md` D8) and not carried crosses on its own: player-owned and
server-owned alike (crates, projectiles, NPCs, vehicles). A carried entity crosses with its carrier and keeps its seat
(`docs/scope-activation.md` D19), and everything aboard is followed with it (D21 there). Companions (NEB-372) keep their
meaning, "crosses with its leader, not on its own", through `Portal.ResolveCompanions`: a held item is usually in
front of its holder and must not cross first. With the far side warm, a companion never waits for a preparation of its
own, so `CompanionWaitSeconds` has nothing to bound for a portal. Whether an NPC may follow a player through is
`CanEnter`'s answer, which is the game's; nothing in the portal stops it.

**D15 The test is a swept segment against the aperture, on the worker that simulates the entity.** After each tick's
simulation, for each portal registered on the entity's container chain (not every portal in the process: today's
`InstanceBoundary.Tick` asks every active boundary about every entity), Nebula takes the root's segment from last tick's
position to this tick's, in the face's axes, in double. The root crosses when the segment meets the cross-section and
ends at least `CrossDepth` (5 cm) beyond the plane; it crosses back when it ends `CrossDepth` short of it on the other
side. The depth is the hysteresis `ExitMargin` was for. A body faster than `CrossDepth` per step (a projectile is 5 to 20
m a tick) is tested on its predicted segment for the next step, `position + velocity × dt`, and committed before the step
that would carry it through, so it never simulates a step behind the face in the near scene, where a coincident far
scope is solid mass.

**D16 A commit goes through the link's standing preparation.** Because the link is Open (D10), Nebula commits without a
per-entity `PrepareTransfer`: an internal `CommitThrough(link, entity, pose)` does what `BeginCommit`/`EndCommit` do (the
same container selection, `FindDestination` into the deepest far container holding the mapped point; the same lease-epoch
check; `Epoch++`; `HasStateTick = false`) on the tick the root passes. Nothing is loaded and nothing is held. The one way
it can fail is that the far container's lease changed on this very tick; the entity is then refused for that tick (D17)
and tested again on the next.

**D17 Straddling resolves to the root's side; a refused entity meets the aperture.** An entity is in exactly one scope at
a time, the one its root is in; the part of it beyond the plane is a proxy in the other scope (§6). An entity that is
refused (not admitted, link not open, `AmbiguousParty`, a member not yet warm) does not cross. A physical body hits the
aperture collider, which is solid for it. A mover with no body of its own (a `CharacterController`, a script-moved
projectile) is clamped: its root is put back `CrossDepth` short of the plane and its velocity's component along the face
normal removed. Either way the near worker raises `Refused(entity, link, reason)` so the game can bounce, drop or destroy
it. Holding the entity where it last stood is the `Volume` shim's behaviour only (D7).

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

**D19 Interest at a commit keeps what a window still authorises.** `docs/scope-activation.md` D13 stays: an entity that
changes scope is re-authorised for every client that holds it (`RevalidateInterest`) before anything about the
destination is relayed. What changes is `CanObserve` (§5): a client that may look through the link the entity went
through, from inside the near window, still passes, so the thrower keeps the crate in sight. An onlooker who is not an
observer loses it at once and is never told where it went, as now. The order is safe by construction: a link is Open
only after its members' `PortalWarm` acknowledgements went through the network gateway, so that gateway holds the
link and its observers before any commit through it can be relayed.

## 5. Observation

**D20 An observer on one side receives the other side's window.** A client qualifies on a side of an Open (or Closing)
link when it is one of the link's observers and its pawn is in that side's scope and inside that side's window. It then
receives the other side's window: its container rows, the carriers of those rows out to the root of their chain (what
`HostCarriers` does for one door today, `docs/scope-frames.md` D12), and every entity standing in it. Both ways, for
every link: from the planet into a room, and from the room out to the planet.

**D21 The network gateway authorises it in `CanObserve`, from link records only.** One rule is added after the existing
ones: an entity in scope T is observable by a client in scope S when an Open or Closing link joins S and T, the client is
on its observer list (or the list is `Anyone`), the client's pawn is inside S's window, and the entity's record is
inside T's window. Both window tests are on the absolute records the network gateway already keeps
(`EntityRecord.AbsX/AbsY/AbsZ`), transformed through the link pose for an offset portal. The worker decides policy; the
network gateway only enforces the list (D11).

**D22 One portal deep.** Windows do not chain. An entity received through a link is never the source of another link's
window, and a client is never sent a scope two links away: on floor 1 you see floor 2's landing, not floor 3. This bounds
the cost per client at the windows of the links it stands in, at most `MaxPortalWindows` (4) at once, nearest first.

**D23 A client's subscriptions span scopes.** Region ids are salted with their scope (`docs/scope-frames.md` D7), and a
client's set is today salted with one scope (`ClientInterest.ScopeSalt`, `UpdateClientRegions`). The far window's
regions are subscribed with the far scope's salt beside the client's own; the subscription message already carries
opaque 64-bit ids, so nothing on the worker side changes. An entity's record survives its scope change: when a window
still authorises it (D19), the network gateway re-files the record under its new region key in place, never evicting it
and fetching it again (§18: with only `CanObserve` passing, a crossing crate blinked out for 0.3 s in 2 of 6 crossings).
Window rows count against `MaxContainerRows` after the client's
own windows and foci, as `docs/scope-activation.md` D23 rows do, so a full budget withholds window ground, never the rows
the client stands in.

**D24 Windows replace `ObservePublic`, `ObserveHost`, `HostCarriers` and NEB-370.** `ObservePublic` only ever looked
into scope 0 and only through an axis-aligned box fixed at preparation; `ObserveHost` kept the host's rows from a
per-connection history (`ClientConn.HostViews`), lost on a reconnect inside (`docs/scope-activation.md` §12.3), and no
host entities (D24 there). A link's windows give both directions, any scope, rows and entities, from link records that
survive a reconnect. The shim (D7) maps a template's `ObservePublic` and `PublicView` onto the host side's window of its
link, and `ObserveHost` onto the same window; NEB-436 removes the old paths once nothing reads them. NEB-370's sibling
observation (one instance seeing another's entities) is the general case of D20.

**D25 Observed entities are replicas in their own scope.** A client never simulates them; it predicts against them only
through proxies in the band (D26, D27), which it builds for its pawn's scope as the worker does. A client's dynamic
proxies are posed from the replica's latest authoritative state, the same tick the worker posed its proxy from, never
from the interpolated draw pose (§18: a pawn pushing against a crate across the face was corrected 52 times in 2.5 s with
no client proxy and 29 times with one posed from interpolated state). Replicas live in their own scope's containers on
the client, drawn through that scope's pose (§10). The client's own scope stays its pawn's (`docs/scope-activation.md` D16), as do routing, frames
(D6 of `docs/scope-frames.md`) and the view.

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

**D28 A closed portal is a solid plane.** Each scene has a thin box collider over the cross-section. It is solid while
the link is not Open. While it is Open, the collider is ignored, pair by pair (`Physics.IgnoreCollision`), for the
colliders of each admitted entity entering the band, and solid for everything else, which is how a refused entity meets
it (D17). With several parties' links open on one face, an entity passes only if its own party's link is open (D12).
An observation-only portal (§17) keeps it solid for everyone.

**D29 Across workers, the proxy's source is the ordinary ghost.** When the other side is simulated by another worker,
the entity reaches it through the ghost band (D30), as a kinematic copy driven by the received stream
(`NebulaWorker.SetGhostPhysics`); the receiving worker holds it as a proxy only, posed through the link in its own side's
scene, with no body in the entity's scope's scene there (that worker may not have that scene at all). Blocking is
one-sided, as for every ghost (`docs/architecture.md`, "Pre-warm, never transfer-at-crossing"), and the proxy is one
tick behind its authority.

**D30 NEB-232 is decided: scopes are adjacent at portals only, and only within a band.** `ContainerRegistry.Link` stays
scope-qualified (`docs/scoped-chunk-grids.md` D11): two containers of different scopes are never neighbours, however their
boxes overlap. A link that is not Closed adds a separate relation, portal adjacency, between the near face's container
and the far containers its band touches, and the ghost band consults it with the band box, not the containers' boxes. An
entity is ghosted across a portal when its extent or root is inside the band, never because two scopes' boxes overlap.
There is no free ghosting, interaction or sight between scopes anywhere else.

**D31 The contact model has a stated limit.** Each side resolves its contacts against the other side's proxies, which
are kinematic. A body resting across the face rests on its own side's statics and throat proxies and is stable. Two
dynamic bodies pushing each other across the face each see the other as immovable, so a shove through a doorway is
one-sided per side, and a pile that spans the face can jitter. That is the price of keeping one physics scene per scope;
shared physics scenes were rejected (Seamless Instances, "Proxies, not shared physics scenes").

## 7. Queries

**D32 `PortalQueries` continue through an open aperture, one portal deep.** `Raycast`, `Sweep` (sphere, capsule,
box) and `Overlap` take the scope to start in and the entity (or party) asking. They run in that scope's physics scene
ignoring `PortalProxyLayer`. When the cast meets the cross-section of a link that is Open for the asker's party
before it hits anything, it continues in the far scope's scene from the plane point, mapped through the link, with the
remaining distance, and stops at the next aperture. An overlap tests its own scope and the part of its shape beyond the
face, mapped into the far scope. A closed aperture is a hit on the plane, flagged `Aperture`, so a shot against a closed
door hits the door. Each hit reports its scope, collider, entity, and its point in both the asker's and the hit scope's
spaces. A query through a face with several parties' links picks the asker's party's far scope (D12).

**D33 Lag compensation reuses state history, and Nebula rewinds the far leg.** Nebula has no rewind query today, only
`NetworkIdentity.StateAt` (`docs/state-history.md`). A query with a `tick` poses the far window's entities at `StateAt(tick)`
for the far leg, through a small `HistoryRewind` helper NEB-433 adds; a caller that rewinds its own scope already does
nothing new for the near leg, and one that wants Nebula to do both passes `RewindNear`. Because the ring was rewritten at
the crossing (D18), an entity that crossed during the rewound interval is found in its scope of that tick. When the far
scope is on another worker, the near worker has only the band (ghost proxies and throat): the far leg is tested there
and a cast that leaves the band is returned `Truncated`. Open question 6.

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

## 9. Frames and precision

**D37 Every portal quantity is a `Double3` and a rotation, applied about the faces, never as a bare translation.** A
link stored as `T + R × p` with `T = o − R × o` loses metres at 10⁸ m: a float rotation is good to about 10⁻⁷, and that
error multiplies `o`. Applied as `N.Origin + R × (p − F.Origin)` (D4), the error multiplies the distance from the face,
which is metres. Face rotations are normalised and rounded to 10⁻⁶ as `InstanceBoundary.PoseRotation` is, so one door
always prepares one definition.

**D38 Geometry is face-relative, so the float parts are small.** An entity's root is read as an absolute `Double3`
(`ContainerRegistry.ToAbsolutePrecise`), the face origin subtracted in double, and only then narrowed to the face-relative
offset `OrientedBox` tests. Commits use the double overloads that exist (`TryCommitTransfer(transfer, frame, Double3,
rotation)`), so an entity lands to the millimetre wherever the scope's frame origin is on that worker. All of it sits on
NEB-426: a pinned frame per instance on workers (`docs/scope-frames.md` D11) and, on a client, an instance sharing the
frame of the scope its pawn entered from.

**D39 A link pose subsumes an instance pose.** For a template far side, the link's rotation is the instance's pose
against its host (`InstancePoses`, learned from lease rows, `docs/scope-frames.md` D12). `InstancePoses` stays as the
source for it; `Portals.Map(from, to, pose)` and `Portals.Turn(from, to)` replace direct uses for anything
that crosses a portal, and answer for offset portals too.

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

**D46 Protocol 28, and older clients are refused.** `HelloMsg.ProtocolVersion` becomes 28 and `MinProtocolVersion` 28: the
window closes, as `docs/compatibility-policy.md` D1 allows before 1.0, and a client of 27 is refused with
`ProtocolUnsupported`. The bytes alone would be additive (new message ids), but the meaning is not: a 27 client would be
sent replicas of scopes it believes hidden and never draw them posed, and would predict its pawn into an aperture it does
not know is solid. Decided by the project owner; no compatibility window. The usual steps of `docs/protocol-versions.md`
apply: a `protocol-28-handshake.json` recording, a frozen decoder for the next bump, the table row.

**D47 What 27 added stays.** The pose section of `ContainerOwnership` (`docs/scope-frames.md` D12) still carries
`InstancePoses` to clients, now as the source of a template link's rotation (D39). Removing the `ObserveHost` flag
byte from lease rows and the `ObservePublic` fields from scope definitions is NEB-436's, and changes nothing a client
reads.

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
| a hold at the face | none outside the `Volume` shim (D16, D17) | a crossing commits on the tick the root passes |
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
   replaces both for plane apertures; the shim keeps the old path (D7).
3. **Content is loaded synchronously, in the tick, on first prepare.** `InstanceScenes.Prepare` creates the physics scene
   lazily and does `Resources.Load` and `Instantiate` of a part's whole prefab inside the worker tick (and on a client
   when the row arrives). A large interior breaks R2. NEB-428 moves the scene's creation and the prefab load into
   Preparing, asynchronously (`Resources.LoadAsync`, `InstantiateAsync`), counted in D10's second condition.
4. **Revocation at a scope change.** `docs/scope-activation.md` D13 and `NebulaGateway.CanObserve`'s single-scope test
   (`(target?.InstanceId ?? 0) == scope`, then `ObservePublic` in scope 0 only) mean a crate thrown into a room is
   despawned for its thrower at the commit. Until NEB-431 lands, NEB-429's crossings show exactly that; D19 and D21 fix it.
5. **One salt per client.** `ClientInterest.ScopeSalt`, `UpdateClientRegions` and the foci keys salt with the client's
   one scope; D23 makes the salt per window.
6. **No adjacency across scopes.** `ContainerRegistry.Link` refuses it, by design, and stays so; portal adjacency is a
   second relation with its own band test (D30), so the ghost band changes only where it consults it.
7. **The world is turned to draw.** `InstanceViews.Begin` turns every scene root from inside a turned instance; D41 poses
   the view and its cameras instead.
8. **Host observation lives on the connection.** `ClientConn.HostViews` and `HostCarriers` are per-connection history;
   link records replace them (D24).
9. **History is left behind.** `StateHistory` entries hold a world pose and their container (`docs/state-history.md` D4)
   and are not touched at a crossing; D18 rewrites them.
10. **The crossing test is a volume.** `InstanceBoundary.InteriorContains` makes the whole `Interior` box the inside, so
    an entity is "in" whatever face it came through. That is the `Volume` containment of the shim (D7); a plane aperture
    is a different, stricter rule, and content moved onto portals must have its door on the face.
11. **Companions move by translation only.** `InstanceBoundary.Commit` commits the group with one displacement
    (`TryCommitTransfers(Group, therePrecise − entity.ScopePositionPrecise)`), which preserves each member's rotation,
    so through a turned door a held item arrives unturned. D18 maps every member's full pose.
12. **Ghosts are bodies in their own scope.** `SetGhostPhysics` makes a ghost kinematic where it is; a ghost across a
    portal must be a proxy in the other scope's scene instead (D29).
13. **Physics scenes do not exist in EditMode.** `InstanceScenes.Prepare` keeps containers in the active scene when not
    playing, so tier-B EditMode tests cannot test proxies or cross-scene contacts. NEB-432's tests need a play-mode
    fixture or a seam that creates local physics scenes, as `AdoptContent` is a seam for content.
14. **Link state has no home on a reassignment.** A container's lease moves without any state of its own; D44 adds the
    handoff.

## 15. Implementation plan

Each issue is one Nebula PR to the public repo. Conformance scenarios get the next free numbers of
`docs/conformance-suite.md` (50 onwards) in the PR that implements them.

**Milestone 1, portals and anything crosses.**

- **NEB-428 `Portal` replaces `InstanceBoundary`.** The component, faces, apertures (`Box`, and `Volume` for the
  shim), party links and their state machine with wait reasons (§3), the standing preparation (D10), asynchronous content
  preparation (§14, item 3), messages 27 to 29 and protocol 28 with the window closed (§12). `InstanceBoundary` becomes
  the shim (D7) with the same keys, events and statuses; `ObservePublic`, `ObserveHost` and `HostCarriers` are left as
  they are. Tests: every existing `InstanceBoundary*` test and `ConformanceFramedInstanceBoundaryTests` pass unchanged on
  the shim; a state-machine test of every transition and wait; tier A (fake mesh, real network gateway): a portal's
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

**Milestone 2, seeing and acting across.** The three issues are independent.

- **NEB-431 Observe both ways.** §5: windows, the `CanObserve` rule, per-window salts, the shim's mapping of
  `ObservePublic`/`ObserveHost`. Tests (tier A): a member in the near window receives the far window's rows, carriers and
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

**Milestone 3, across workers and on the client.**

- **NEB-434 Co-location and cross-worker commits.** D34 to D36. Tests (tier B, two workers): a link that is not Closed
  binds its containers into one item; with the far scope forced onto the other worker, a crossing commits through one
  handover with no hold and a correction ≤ 0.1 m; every wait reason is reported in its situation.
- **NEB-435 Several drawn scopes.** §10: `PoseOf`, posing non-anchor roots and cameras, rendering-layer slots. Tests:
  extend `ConformanceFarInstanceClientFrameTests` with two turned scopes drawn at once and from inside a turned scope;
  the anchor's roots are never moved; slot bits are stamped and nothing else of the mask changes.
- **NEB-436 Cleanup.** Remove `ObservePublic`, `ObserveHost`, `HostViews`, `HostCarriers` and `InstanceViews`'
  scene-root turn; decide the shim's future (open question 5); fold in NEB-405, NEB-308 and NEB-408; update the
  instancing, scopes and physics guides and `docs/conformance-suite.md`.

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
| `InstanceBoundary` | a shim over `Portal` (D7) |
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
| The volume containment's 1.2 m exit margin strands slow bodies in the far scope | a slow crate stopped outside still in the instance | D15 (plane, 5 cm), D7 (shim only) |
| First spawn and first replica instantiate dominate, not the crossing | 34 ms worker, 64 to 74 ms client, once | §13: prewarm what crosses with the far content |

The hitch budget could not be judged on an Editor-hosted worker, which peaks at 16 to 21 ms with nothing crossing; the
crossing step added at most 1.9 ms. R2 is measured on a built worker (milestone 8). A 1.6 km pawn correction was seen
once leaving the instance and was not isolated; NEB-431's window tests include a pawn leaving while observing the host.

## 19. Open questions

1. **Sizes.** `BandDepth` 2 m, `CrossDepth` 5 cm, `WindowMargin` and `WindowDepth` (proposed 8 m and 48 m),
   `MaxPortalWindows` 4, `CloseTimeoutSeconds` 5. The HVR-683 spike used a 2 m band and found it enough for a door-sized crate (§18); the window sizes were not measured.
2. **Default observers.** Members only (D11). Should a door shared by several parties let bystanders look into an open
   party's room by default, or only when the game says `Anyone`?
3. **Party of an unowned entity.** D12 refuses an unowned entity at a face with more than one open link unless `PartyOf`
   names a party. Is refusal the right default, rather than the most recently opened link?
4. **Crash.** A crashed near worker's links read Closed (D44), and anything straddling is pushed out when the far side
   comes back. Acceptable?
5. **The shim's future.** Keep `Volume` containment and `InstanceBoundary` as a supported compatibility type, or remove
   both in NEB-436 once content moves to plane apertures?
6. **Far-leg queries on a split link.** D33 truncates a cast that leaves the band on another worker. Acceptable, given D34
   keeps links co-located, or does it need an asynchronous `WorkerQuery` leg?
7. **Rendering layers.** Should Nebula stamp renderers (D42), or only assign slots and let the game stamp? Is eight slots
   enough?
8. **Lag compensation.** Nebula rewinds the far leg by default and the near leg only on request (D33). Should it own both?
9. **Client throat.** D27 has the client build the static throat for prediction. That is content work on the client at
   preparation; is it worth it, or should prediction at the face accept the worker's correction?
10. **Turning frames.** `docs/container-tree.md` already asks for doors on frames that do not turn. Does a face on a
    turning carrier need to work, or is that a documented limit?
11. **NEB-431 before NEB-429?** NEB-429 alone makes a thrower lose sight of a crate at the commit (§14, item 4). Ship
    them in one release, or accept the gap between milestones?
