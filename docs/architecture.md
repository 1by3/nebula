This document summarizes the Nebula architecture as designed in the Aug 8, 2026 design conversation. It is written for developers who have not read that conversation. A companion Decision Log records each decision with rationale, alternatives, and status.

Goal and product posture

Nebula is sellable middleware for dynamically meshed multiplayer game servers, Unity-first: an MMO-scale world is served by many dedicated game servers, each dynamically responsible for a portion of the world (a city down to an individual room), with entities crossing between servers' areas of influence seamlessly. The reference point is Star Citizen: CIG shipped static meshing in 4.0 (Dec 2024); dynamic meshing remains their in-progress next step. The static-to-dynamic gap is where the hard problems live.

Product posture is narrow and honest: market to specific types of games, publish the system's limitations explicitly, and treat that honesty as a trust advantage. Even though we will market broadly, the prototype targets FPS combat at 60Hz hitscan, because it is the strictest case — if seam crossings are invisible there, they are invisible in a survival game or social world. The reverse does not hold.

The most product-relevant lesson comes from SpatialOS. Improbable built almost exactly this system — worker-based authority with handover as a first-class concept — and the technology largely worked. What killed it was developer experience: authority semantics leaked into every component, the workflow fought the engine, and studios spent their time fighting the platform. The failure mode for this product category is not "the tech doesn't work"; it's "developers hate using it." Everything that sounds like polish — the debug overlay, validator error messages, gameplay API ergonomics — is the core product surface. The distributed-systems part is the part we can definitely do.

The container model

The unit of authority is not a spatial grid (no octrees, BSP, or uniform chunks). The world is a static, designer-authored hierarchy of nested object containers — a room inside a ship inside a system — each with its own local coordinate space. Authority is assigned per-container. The container tree is static; the assignment of containers to servers is dynamic. Splitting a city across 4 servers means "reassign these 40 room containers to server B," never "compute a cut plane through live geometry."

This is a hard requirement (per Jesse, explicitly): never flat-spatial. The hierarchy pays off three separate ways:

Moving reference frames for free. A ship flying across a system never straddles boundaries because everything inside it lives in the ship's local space. Grid-based schemes die here.

Floating-point precision at planetary scale — nothing is ever far from its local origin.

Semantic boundaries — a doorway is a natural authority seam in a way that x=1024 never is.

A fourth payoff follows from the first: passengers never cross seams. When a ship crosses a seam, the ship is one entity changing containers; everyone aboard is in the ship's local space and their transforms never change. Forty players in a capital ship experience nothing. (The real cost of a large ship crossing is handover payload — a container subtree of hull, cargo, and passengers — mitigated by prioritized incremental hydration, keeping durable data like inventories in the persistence layer read lazily, and provisional flip with not-yet-resident state frozen rather than wrong.)

In Unity, containers are the transform hierarchy: a Container component declares bounds, stable ID, and a streamed-content reference; a Seam component adds neighbor references and an approach-time budget. A bake-time validator enforces the seam contract and exports a container-graph manifest — the level file is the deployment topology.

Tier stack

Top to bottom:

Control plane + persistence — SpacetimeDB, eventually. Two jobs: (1) the control plane — container ownership leases, authority epochs, node registry, entity directory; low write volume, transactionally consistent, subscription push. A lease row is roughly container_id, owner_node, epoch, state, heartbeat_at, expires_at with state ∈ {assigning, active, draining, orphaned}. (2) Durable world state — inventories, placed structures, player persistence — written on checkpoint interval and container drain, read on hydration. What it must never be: the transport for per-tick transforms or the arbiter of hit registration. SpacetimeDB is not a known quantity, so all sim code talks to an IControlPlane abstraction; the prototype implements it with a static manifest plus in-process leases and defers real integration. Its scaling story (single module ≈ single-node DB) is the honest risk; region-sharding with a coordinating layer above is presumed viable — Clockwork Labs' own BitCraft scales this way today. Control plane and persistence probably shouldn't even be the same module, since their consistency and durability needs diverge.

Stateless sim servers. Headless Unity processes simulating at 60Hz. A sim is not "the server for a region" — it is a disposable compute worker leased authority over a set of containers. Authoritative durable state lives above it; a sim dying does not take state with it.

The lateral link. Sims owning adjacent containers peer directly over UDP/QUIC on a private datacenter network to carry ghost-band state — never through the database. Each sim peers only with sims owning adjacent containers, so it is bounded, not N².

Gateway (production only). One client connection; the gateway multiplexes to whichever sims own containers in the client's interest set, does interest filtering, packet coalescing, and ghost/authority dedupe, making handover invisible to the client. ~1ms extra hop when colocated. Client interest is a separate subsystem from the ghost band — it is driven by visibility, not authority (you can see much farther than you can collide), and is much larger than either sim-side overlap set. The prototype skips the gateway; clients connect to both sims directly.

Clients. Predicted, reconciled, never a party to the handover protocol (see below).

An orchestrator (out of prototype scope) watches sim telemetry — p99 tick time, not entity count — decides split/merge, drives reassignment via lease intents, and owns a warm pool of pre-booted sims, since cold-starting an engine process with a loaded level takes tens of seconds.

Seams

A boundary is a volume, not a plane — a 12m airlock corridor, not an infinitely thin plane at x=0. Better still: the seam volume is its own container with exactly one owner (usually a neighbor, or its own sim if hot). Consequences:

All interaction inside the seam is single-authority: no cross-boundary hit registration, no ghost divergence, no claim/validate for anything happening in the corridor.

Handover becomes two sequential single-authority transfers (A → seam, seam → B), each at a moment we control, instead of one fuzzy simultaneous one.

Cross-seam combat reduces to "entities at opposite ends of a corridor," which occlusion mostly eliminates.

This converts our hardest correctness problem into a scheduling problem, at the cost of one extra handover per traversal.

Ghost band ≠ seam volume. They were initially conflated and must not be. The seam volume exists for interaction containment; its size is set by gameplay geometry. The ghost band exists for pre-warm timing; it is sized in seconds of approach time, not meters, and need not be geometric at all — it is trajectory-predicted: ghost an entity when its projected path crosses a seam within N milliseconds. A ship at 1000 m/s with a 500ms budget starts ghosting 500m out (the horizon scales linearly with speed and budget) while the seam stays a thin shell. Trajectory prediction works best exactly where geometry fails (fast, high-momentum entities) and geometry covers where prediction fails (a player on foot who can reverse in 200ms).

The seam contract (enforced by the bake validator): no constraint island spans a seam (hard error — no graceful degradation exists); seams should occlude or be closable; interaction volumes, triggers, and NavMesh links terminate at seams (with declared seam-links); bounded crossing throughput. Seam-length-vs-speed constraints are soft (per Jesse — fast spaceships can't always have long-enough seams): quantified warnings ("clean crossings up to 45 m/s; above that expect up to 30cm correction") plus a graceful degradation ladder — full pre-warm (invisible) → late snapshot (brief visible correction) → no pre-warm (provisionally spawn from the transform stream, frozen until state backfills) → never an entity that vanishes or exists on zero servers.

Two operations both called "handover"

They have completely different budgets and conflating them is a classic failure:

Entity handover — a player walks through a seam. Frequency: constant. Budget: sub-frame. Mechanism: ghost-band pre-warm plus an epoch bump agreed sim-to-sim over the lateral link; the DB write is the durable record that settles ties, and gameplay never waits on it.

Container reassignment — the load balancer moves container C from sim A to sim B. Frequency: rare, seconds apart. Budget: hundreds of milliseconds, staged: B prepares (loads static data, hydrates state), A drains (stops accepting entities, flushes writes), lease flips, A releases. Both simulate during drain, B non-authoritatively.

Handover mechanism

Pre-warm, never transfer-at-crossing. Each sim ghosts the containers adjacent to (or trajectory-predicted toward) its own, so the neighbor already holds full warm state before an entity can cross. Crossing costs an authority epoch increment — every grant carries a monotonic epoch; clients and the control plane reject writes from stale epochs, preventing split-brain. On the non-owning sim a ghost is a kinematic collider driven by the received transform stream — no solve, no constraint participation; local dynamic entities collide against it (blocking is one-sided). At the flip, the authority event carries A's exact final state and B snaps to it; B never computes a starting state, it is handed one. Divergence at flip therefore comes from interpolation lag alone (~16–30ms staleness). Anti-thrash: asymmetric enter/exit thresholds, minimum dwell time, deferring handover during high-acceleration frames, and keeping ghosts resident a few seconds after band exit.

The ghost-band wire protocol is four tiers in three reliability classes — Transform (60Hz, unreliable sequenced, ~20 bytes/entity quantized), Sim state (on change, reliable ordered — capsule dims, movement mode, plus AI perception fields), Snapshot (once on band entry, reliable ordered — full dynamics, input cursor, ability state), Authority event (on crossing, reliable ordered). QUIC provides exactly this shape natively. Bandwidth is a non-issue (~1 Mbps per neighbor pair at 100 entities).

Cross-boundary interaction

The genuinely nasty problem is not handover; it is a player on A shooting a player on B at 60Hz with lag compensation. Strategy, in order:

Design seams so it can't happen. Boundaries at airlocks, elevators, tunnel chokepoints — occluded, no line of sight. Level designers author the seams; the balancer only chooses which authored seams to activate.

Claim/validate as fallback. The shooter's server sends a damage claim (ray, tick, shooter state); the victim's server rewinds against its own history and accepts/rejects. Adds a round trip to hit confirmation. Grenades and other consequential projectiles use the same path: an authoritative tick-stamped event, applied locally by each owning sim.

Seam-as-container contains everything happening inside the seam under a single authority, so the contested zone itself never needs cross-server resolution.

Failure behavior

Sim dies. Its lease TTL expires, containers go orphaned, the orchestrator assigns a warm node which hydrates from the last checkpoint. Transient state since the checkpoint is lost (velocities, in-flight projectiles, a few seconds of position). Players see a rubber-band, not a disconnect — the main payoff of stateless sims.

Gateway dies. Client reconnects to another gateway and re-subscribes. Visible blip, no state loss; the gateway holds nothing authoritative.

Control plane unavailable. Existing leases remain valid until TTL; the mesh keeps simulating with a frozen topology — no reassignment, no new containers, degraded but alive. The control plane must never be a hot-path dependency; if its outage stops the game, the design has failed its purpose. (The decentralized tick derivation supports this: no sim depends on the control plane, or any tick master, to know what time it is.)

The two networks, and build vs. buy

Sim↔sim is trusted, datacenter-local, symmetric, low peer count, no NAT, no cheating, no DDoS surface — most of what makes network programming hard doesn't apply, and it is the link the whole meshing concept depends on. Client↔sim is untrusted WAN: asymmetric, thousands of connections, NAT traversal, mandatory encryption, adversarial. Serving both with one stack is how scope explodes; build the trusted link well and solve the client link conventionally.

Layer

Build or buy

Sockets, reliability channels, congestion control, crypto

Buy — subtly wrong is catastrophic and undebuggable

Serialization / bitpacking / quantization

Buy or thin-wrap

Ghost replication, baselines, delta compression, acks

Build

Authority model — leases, epochs, handover

Build — exists nowhere

Prediction and reconciliation across authority change

Build, referencing prior art heavily

Gameplay API — MeshedBehaviour, EntityRef, commands

Build — this is the product

Transport sits behind an ITransport seam: LiteNetLib (or Unity Transport) for the prototype, System.Net.Quic for sim↔sim when Unity 6.8/CoreCLR lands (targeted end of 2026; slippage plausible). Existing frameworks (NGO, Mirror, FishNet) encode single-authoritative-server structurally and cannot be massaged into multi-authority; Netcode for Entities has the right ghost/snapshot shape but assumes one server and drags in DOTS. Study SpatialOS and Netcode for Entities; build on neither.

What the prototype proves

The riskiest assumption: a player crossing a seam mid-firefight cannot tell. Success criterion: the handover correction distribution is statistically indistinguishable from the baseline correction distribution of normal play — validated by telemetry (divergence at flip, correction magnitude, authority-gap and input-continuity counters that must read zero, handover latency, ghost staleness) and a blind A/B test where testers can't locate the seam by feel at 40ms sim-to-sim RTT. Scope is deliberately minimal (see Decision Log #17). A clean result proves seamlessness at N=2; scale is a separate hypothesis requiring a separate experiment.
