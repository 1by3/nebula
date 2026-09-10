# Nebula prototype: dynamic server meshing, end to end

This is what is actually built in this repository as of 2026-09-08. It is a deliberately small implementation of
`architecture.md`: four containers, N workers, one gateway, one orchestrator, one control plane, and a shooter that
exists to make crossing a container boundary mid-firefight visible.

```
                 ┌──────────────────────────────────────────────────────────┐
                 │  SpacetimeDB  (control plane: workers, leases, gateways)  │
                 └──────▲──────────────▲──────────────▲──────────────▲──────┘
                        │ reducers     │ subscribe    │              │
                 ┌──────┴──────┐  ┌────┴────┐   ┌─────┴─────┐  ┌─────┴─────┐
                 │ Orchestrator│  │ Gateway │   │ Worker w1 │◄─►│ Worker w2 │ ... lateral link (UDP)
                 │ spawns procs│  │ udp/7000│◄─►│ udp/7101  │   │ udp/7102  │     ghosts + authority transfers
                 └─────────────┘  └────▲────┘   └───────────┘  └───────────┘
                                       │ one connection per client
                                  ┌────┴────┐
                                  │ Client  │  inputs up, snapshots down, predicted local player
                                  └─────────┘
```

Every box is the same Unity player build (`Builds/Win64/Nebula.exe`) started with a different `-nebula-role`.
The Editor plays the client role by default.

## The four components

### ShooterGame (`Assets/ShooterGame`)
A 40x40 m room split into four container quadrants (`quadrant-NE/NW/SE/SW`). Players are capsules with a
predicted first-person controller, hitscan rifle (raycast), health, death and respawn. The floor of each quadrant
is tinted with the colour of the worker that owns it, and every player capsule is tinted with the colour of the
worker that currently simulates it - so a handover is a colour change you can watch.

The client presentation is player-facing: shots draw as flying laser bolts with an impact spark (`LaserBolts`; the
hit itself is still resolved instantly by the worker), other pawns carry a floating name and health bar that ticks
down as hits land (`HealthBars`), and the HUD (`ShooterHud`) shows a crosshair with hit ticks (white for a hit,
red for a kill), your health, kills/deaths, the kill feed, a damage flash and the death/respawn screen. The
technical Nebula overlay (ticks, workers, containers, corrections) is hidden by default; F3 toggles it. Sounds are
synthesised at startup (`ShooterAudio`), so there are no audio assets.

**Console.** The backquote key (`` ` ``) opens an in-game command console (Quantum Console, `Assets/Plugins/QFSW`;
`ShooterConsole` instantiates it on clients only, `ShooterCommands` holds the commands). While it is open the cursor
is free and the pawn ignores the keyboard. The first command is `npcs`:

```
npcs           how many NPCs this client sees, and the mesh-wide total
npcs 200       set the mesh-wide NPC total; workers spawn or despawn to match, no restart
```

Any player may run it (this is a load control for the prototype, not a moderated admin command). It goes through
the pawn as an ordinary `ServerRpc`, so it reaches whichever worker owns the pawn; that worker writes the `npcs`
setting on the control plane and every worker's `NpcDirector` reconciles against it (see *Bots vs NPCs* below).

Everything here is written against the Nebula API exactly the way you would write it against Mirror or NGO:

```csharp
public sealed class PlayerController : PredictedBehaviour<ShooterInput>
{
    public NetworkVariable<float> Health = new NetworkVariable<float>(100f);

    protected override ShooterInput GatherInput() { /* owner client: read the Input System */ }
    protected override void Simulate(uint tick, in ShooterInput input, float dt) { /* server + predicting client */ }

    [AuthorityRpc] void TakeDamage(float amount, ulong attacker, string attackerName) { Health.Value -= amount; ... }
    [ClientRpc]    void RpcShotFired(Vector3 from, Vector3 to, bool hit) { /* laser bolt; hit tick for the owner */ }
}
```

`ShooterGameMode : NebulaGameMode` is the server hook that spawns a player when the gateway asks for one.

Press F for the box gun: it spawns a `Box` prefab (a dynamic Rigidbody) that is never despawned, so physical
objects cross container boundaries all game long. `NetworkRigidbody` (`Packages/com.1by3.nebula/Runtime/Core`) is the
middleware piece that makes any Rigidbody a meshed entity: dynamic on the authoritative worker, kinematic on ghosts
and clients, linear velocity mirrored into the identity every tick, angular velocity carried by the handover only.
The latter uses the general hook every `NetworkBehaviour` gets, `WriteHandoverState`/`ReadHandoverState`: state
that only the next simulating worker needs (spin, timers, RNG) rides the `AuthorityTransfer` message and never the
per-tick stream. `PhysicsBox` on the prefab is only the per-worker tint.

Next to it sit `NetworkTransform` and `NetworkAnimator`, the two other components an NGO user reaches for first.
Both ride a second replication channel, the *sync channel*: any `NetworkBehaviour` can opt in with `HasSyncState`
and a `WriteSyncState`/`ReadSyncState` pair, mark itself dirty, and the worker packs one bounded chunk per dirty
behaviour into an `EntityState` message every tick (`GhostSyncState` on the lateral link). A behaviour picks its
delivery: reliable-ordered, or unreliable-sequenced deltas healed by a keyframe every 30 ticks. Keyframes are also
what the gateway caches per behaviour and hands to late joiners inside the spawn message, and what a fresh ghost or
the receiving side of a handover starts from. Non-authoritative copies get `RemoteTick(renderTick)` (per frame on a
client, per tick on a worker before physics syncs) to present their buffers at `NetworkTime.RenderTick`.

- `NetworkTransform` carries NGO's option set: per-axis position/rotation/scale selection, thresholds, local or
  world space (world values travel container-local like the identity stream), `Authority = Server | Owner`
  (owner authority is NGO's `ClientNetworkTransform`: the owning client sends its pose to the worker over a
  ServerRpc and the worker re-broadcasts), buffered tick interpolation or smooth damping, slerped positions,
  half-float precision, quaternion synchronisation with smallest-three compression, unreliable deltas, `Teleport`
  and `SetState`, and the `OnAuthorityPushTransformState`/`OnNetworkTransformStateUpdated` hooks. One Nebula
  twist: the entity root's position and rotation are already in the identity stream, so a `NetworkTransform` on the
  root never sends them again; there it adds scale, teleport (which also resets the identity interpolator) and the
  owner-authority path. On child transforms it replicates everything selected. `TickSyncChildren` is implicit (all
  chunks of an entity share one message per tick) and `SwitchTransformSpaceWhenParented` has no equivalent (an
  entity's parent only changes with its container, which the wire format already accounts for).
- `NetworkAnimator` replicates parameters (int/float/bool with a float threshold; curve-driven parameters are
  skipped like NGO), triggers through `NetworkAnimator.SetTrigger` only (an owning client under server authority
  asks the worker and sees the result a round trip later, exactly NGO's behaviour), and per-layer state (state hash,
  normalized time, weight) plus in-progress transitions; keyframes carry all of it so late joiners start in the right
  pose. Always reliable. `Authority = Owner` is NGO's `OwnerNetworkAnimator`. Non-authoritative copies have root
  motion switched off because their pose comes from the network.

### Nebula Worker (`Packages/com.1by3.nebula/Runtime/Worker`)
A headless Unity dedicated-server process (`-batchmode -nographics -nebula-role worker`). It loads the same Arena
scene as everyone else, so pathfinding, colliders and physics are all available to gameplay code. Each tick (60 Hz,
derived from the wall clock so all workers agree on tick numbers with no tick master) it:

1. moves the ghosts it holds to the pose its neighbours streamed (kinematic, no solve of their own),
2. runs `NetworkTick`/`Simulate` on every entity it has authority over,
3. re-evaluates container membership with hysteresis and transfers authority for entities that crossed into a
   container owned by another worker,
4. maintains the ghost band: any authoritative entity within `GhostBandMargin` (4 m) of a neighbouring container
   owned by another worker is pre-warmed there (`GhostSpawn`, then `GhostState` at 60 Hz, `GhostVars` on change),
5. streams `WorldState`/`OwnerState`/`EntityVars` for its authoritative entities to the gateway.

Workers discover each other through the control plane and peer directly over UDP (the lower index dials). The
transfer itself is one reliable `AuthorityTransfer` message carrying the sender's exact final state, the new epoch
and the not-yet-simulated inputs, so the receiver snaps to a handed state and the input stream never breaks. The
old owner keeps the object as a ghost and forwards any inputs that still arrive for it. If two containers are on
the same worker, the handover is local and costs nothing.

### Nebula Orchestrator (`Packages/com.1by3.nebula/Runtime/Orchestrator`)
Launches the gateway and keeps `DesiredWorkers` worker processes running (initially `WorkerCount`), writes one lease
row per container, and every 500 ms deals containers across live workers as evenly as possible (sticky to current
owners, so a rebalance moves as few containers as possible): 4 workers = one each, 3 workers = one of them has two,
1 worker = all four. A worker whose heartbeat stops for `WorkerTimeoutSeconds` is declared dead, its leases are
reassigned immediately, and a replacement process is launched after `DeadWorkerReplaceDelaySeconds` (then containers
flow back to it).

The worker count is a live setting. Raising it launches processes on the next free index (indices are reused, so
ports and entity-id prefixes stay dense); lowering it *retires* the highest-index worker: it is dropped from the
assignment set, the next pass moves its containers to the survivors, the worker hands its entities over through the
normal per-entity handover path, and once it holds no lease and reports no authoritative entities (or
`WorkerDrainTimeoutSeconds` passes) the process is killed and unregistered. Players never notice more than a
handover.

**Worker hosts.** The orchestrator never starts a worker itself; it asks an `IWorkerHost`
(`Runtime/Orchestrator/Hosting`) to. A host decides *where* a worker runs and how to start and stop it; the
orchestrator keeps deciding *how many* and which containers each owns. Liveness stays heartbeat-based whatever the
host, the host's handle only lets the orchestrator react sooner when an instance is known to be gone.
`ProcessWorkerHost` (`-nebula-host process`, the default and the local mesh) launches child processes of the same
executable; `HetznerWorkerHost` (`-nebula-host hetzner`) creates one Hetzner Cloud VM per worker and deletes it on
kill or retire (see *Running it on Hetzner* below). Adding a provider means implementing the interface and one line
in `NebulaOrchestrator.CreateHost`; the `-nebula-cloud-*` switches (location, machine type, image, network, ssh
key, firewall, mesh label) are provider-neutral. The gateway is always started next to the orchestrator through the
process host.

**Dashboard.** The orchestrator serves a small web page at `http://localhost:7080/` (`DashboardPort`,
`-nebula-dashboard-port`, `-nebula-dashboard-bind +` to expose it on the LAN). It shows every worker with the
containers it owns, how many human players, bots and server-driven entities it is simulating, its authoritative and ghost entity
counts, tick time and heartbeat age; the container -> worker table with lease epochs; the gateway; and the orchestrator's event
log. Buttons add a worker, remove one (graceful drain), kill one (simulated crash, it is relaunched) and force a
rebalance pass. The same operations are an HTTP API, used by `smoke-test.ps1 -ScaleTo`:

```
GET  /api/state                       full JSON snapshot (workers, containers, gateways, totals, events)
POST /api/desired        {"desired":n}
POST /api/settings       {"key":"npcs","value":"128"}   a mesh-wide setting (also editable on the page)
POST /api/workers/add
POST /api/workers/remove {"workerId":"w3"}   (omit workerId to drop the highest index)
POST /api/workers/kill   {"workerId":"w3"}
POST /api/rebalance
```

**Bots vs NPCs.** Two different things drive a pawn without a human:

- A *bot* is a full client process (`Nebula.exe -nebula-role client -nebula-bot`): it connects through the gateway,
  predicts, reconciles and sends inputs like a real player, with the `BotBrain` in place of the keyboard. Bots
  exercise the client path. Each one is a whole headless Unity player, so use a handful, not a hundred: 128 of them
  on one machine starve the workers.
- An *NPC* is what ShooterGame calls a *server-driven* entity: an ordinary entity with no owning client
  (`NetworkIdentity.IsServerDriven`, spawned with `NebulaWorker.SpawnServerDriven`). Whichever worker holds
  authority is its brain: for a `PredictedBehaviour` the worker calls `GatherServerInput` each tick instead of
  waiting for a client's input, and the entity ghosts, hands over, shoots and gets shot exactly like a player's
  pawn. It costs one entity and nothing else, so NPCs are the tool for load. That is all Nebula knows about them.

  How many there are is game policy, in `ShooterGame/Scripts/NpcDirector.cs`, built on one Nebula primitive:
  *mesh settings*, string key/values on the control plane (`game_setting` table) that Nebula attaches no meaning
  to. The orchestrator seeds them from `-nebula-settings npcs=128` (`nebula start --npcs`), the
  dashboard edits them, and a worker may write them (the in-game `npcs` command does, through a `ServerRpc`).
  The director on every worker reconciles against `npcs`: the live workers split the deficit between the mesh-wide census (every heartbeat carries a `ServerDrivenCount`) and `npcs`
  into container `i % containers` (the mesh hands it to the container's owner on the next tick), each worker
  remembers the indices it spawned so a bigger total only adds the missing ones, and the index rides in a synced
  variable, so when the total shrinks whichever worker currently simulates an NPC above it despawns it, wherever
  it has wandered. A relaunched worker spawns its indices again. This "everyone reconciles against a shared
  value" pattern is the extent of Nebula's cross-worker game logic today; anything needing a single
  decision-maker (one boss, load-based placement) would want a mesh singleton, which does not exist yet.

Workers learn whether a client is a bot from the gateway (the client's `Hello` carries a bot flag when started with
`-nebula-bot`); that flag and the server-driven flag travel with the entity through ghosting and handover so the
per-worker player/bot/server-driven split stays right after a container moves.

### Nebula Gateway (`Packages/com.1by3.nebula/Runtime/Gateway`)
The one address clients connect to. It keeps a link to every worker, routes each client's inputs to the worker
that currently owns that client's entity, re-emits the workers' replication streams to clients, and drops anything
carrying a stale authority epoch or coming from a worker that is no longer the owner. It holds nothing
authoritative; a dead worker's entities are dropped and its players are respawned elsewhere.

### Control plane (`Packages/com.1by3.nebula/SpacetimeDB`)
A tiny SpacetimeDB module (`Module~/Lib.cs`): `worker`, `container_lease`, `gateway`, `orchestrator` tables and the
reducers that register/heartbeat nodes and assign/release leases. Generated C# bindings live in `Generated/`.
All sim code talks to `IControlPlane`; `SpacetimeControlPlane` is the real one, `LocalControlPlane` an in-process
double with identical semantics. The control plane is never on the per-tick path: if it goes away the mesh keeps
simulating with its last known topology.

## Seams
There are no authored seam volumes in this prototype. Each container boundary carries an automatic ghost band
(metres from the boundary) and an entry hysteresis (an entity must be 0.35 m inside the new container before
authority flips). That gives the pre-warm-then-flip behaviour the architecture wants with nothing extra to author.
Cross-container hits use the `[AuthorityRpc]` primitive: the shooter's worker resolves the raycast against its
local world (which includes kinematic ghosts) and the damage claim is executed on whichever worker owns the victim.

## Wire protocol (all links, LiteNetLib over UDP)
Two delivery classes: sequenced-unreliable for transforms (`WorldState`, `GhostState`, `ClientInput`, `OwnerState`)
and reliable-ordered for everything else (`EntitySpawn/Despawn/Vars/Rpc`, `GhostSpawn/Vars/Despawn`,
`AuthorityTransfer`, `SpawnPlayer`, `ContainerOwnership`). `EntityState`/`GhostSyncState` (the sync channel) carry
a flag saying which class they were sent on so the gateway re-emits them on the same one. Every entity message carries `(netId, epoch)`; entity ids
are `[workerIndex:16][sequence:48]` minted locally. Positions are sent in container-local space.

## Running it

Everything below goes through the `nebula` CLI (`docs/cli.md`; install it from this checkout with
`powershell -File cli\install\install.ps1 -Source .`). This repository is itself a Nebula project (`nebula.json`).

```powershell
# 1. build the player once (or Editor: Nebula > Build > Windows Player)
nebula build                                    # mirrors the project if the Editor has it open

# 2. start SpacetimeDB, publish the module, launch orchestrator (+ gateway + 4 workers) and 2 bots
nebula start --workers 4 --bots 2 --open-ui     # dashboard: http://localhost:7080/
nebula start --workers 4 --npcs 128             # load: 128 worker-simulated NPCs, no extra processes
#    (change the total while it runs: `npcs 300` in the in-game console, or the NPCs field on the dashboard)

# 3. join: press Play in the Editor (NebulaBootstrap.EditorRole = Client) or
Builds\Win64\Nebula.exe -nebula-role client -nebula-name jesse
#    Either way a title screen asks for the gateway (Local = 127.0.0.1:7000, Hetzner = the last remote address you
#    used, or anything typed in) and a name; Escape in game brings it back to disconnect or switch. Bots and clients
#    started with -nebula-gateway <addr:port> (or -nebula-connect) skip it and connect at once.

nebula status                                   # workers, containers, players/bots/NPCs, events
nebula logs w1 --follow                         # orchestrator | gateway | w1..wN | bot1..
nebula stop
pwsh Tools/smoke-test.ps1 -KillWorker w2 -KillAfter 30    # unattended run that reports handovers and a rebalance
pwsh Tools/smoke-test.ps1 -ScaleTo 2 -ScaleAfter 25       # shrink 4 -> 2 workers mid-run through the dashboard API
```

The same things are on the Editor menu under **Nebula** (Control Plane / Mesh / Build) and **ShooterGame**
(regenerate the Arena scene, prefab and config from code).

Fast compile check without the Editor: `powershell -File Tools/typecheck.ps1`.

## Running it on Hetzner

The same build, on real machines: the control plane on SpacetimeDB maincloud (`nebula-shootergame`), one Hetzner
Cloud VM for the orchestrator (dashboard + gateway), and one VM per worker that the orchestrator creates and deletes
itself as the desired count changes. Workers peer over a Hetzner private network (10.0.0.0/16); only the gateway
(udp/7000), the dashboard (tcp/7080) and ssh face the internet.

```
            you (client / bot)                     Hetzner project nebula-shootergame, location ash
                    │ udp/7000                     ┌─────────────────────────────────────────────────────┐
                    ▼                              │  nebula-orchestrator VM (public IP)                  │
  ┌───────────────────────────────┐   HCLOUD API   │   Nebula.x86_64 -nebula-role orchestrator            │
  │ api.hetzner.cloud             │◄───────────────│     -nebula-host hetzner  (creates/deletes worker VMs)│
  └───────────────────────────────┘                │     dashboard :7080, serves /build/nebula-linux.tar.gz│
                                                   │   gateway process  udp/7000                          │
  ┌───────────────────────────────┐                └───────────────▲───────────────▲─────────────────────┘
  │ maincloud.spacetimedb.com     │  reducers/subscriptions        │ private net   │ 10.0.1.0/24
  │ nebula-shootergame            │◄──────────────────────┬────────┴───────┬───────┴─────────┐
  └───────────────────────────────┘                       │ orch1-w1-1 VM  │ orch1-w2-2 VM   │ ...
                                                          │ worker udp/7101│ worker udp/7102 │
                                                          └────────────────┴─────────────────┘
```

Each worker VM boots Ubuntu 24.04 with a cloud-init script (`HetznerWorkerHost.BuildCloudInit`) that reads its
private IP from the Hetzner metadata service, downloads the build from the orchestrator over the private network,
and starts the worker as a systemd unit advertising that private IP. VM creation to a registered, container-owning
worker takes about 30 s. Killing a worker (dashboard, or the API) deletes its VM; retiring one drains it first,
then deletes it; an orchestrator restart deletes every VM labelled `nebula-mesh=nebula,nebula-role=worker`
before launching fresh ones, so a crashed orchestrator cannot leave machines billing.

```powershell
# once
nebula config hetzner                           # Read & Write API token (or HCLOUD_TOKEN in the environment), region, VM types
nebula config spacetime                         # `spacetime login` for maincloud + the database name (nebula-shootergame)

# every code change
nebula deploy --workers 4 --npcs 128 --open-ui  # Linux server build + tarball, publish the module, provision what is
                                                # missing (ssh key, network, firewalls, orchestrator VM), upload, restart
nebula status --cloud                           # Hetzner servers + the orchestrator's view
nebula logs --cloud w1                          # orchestrator | gateway | w1..wN, read over ssh
Builds\Win64\Nebula.exe -nebula-role client -nebula-gateway <orchestrator public ip>:7000 -nebula-name jesse

nebula destroy                                  # delete every mesh server (--all: network, firewalls, key too)
```

`nebula deploy` writes the token to `/etc/nebula/env` (root-only) and the unit `nebula-orchestrator.service`; the
orchestrator passes `-nebula-cloud-location/-type/-image/-network/-sshkey/-firewall/-mesh` to the host and
`-nebula-advertise <private ip>` / `-nebula-gateway <public ip>:7000` to the roles it launches. Worker VMs default
to `cpx21` (3 shared vCPU, 4 GB, about EUR 0.018/h each); set `workerType: "ccx13"` in nebula.json (deploy section) for dedicated vCPUs when
measuring tick time. Logs: `/var/log/nebula/orchestrator.log` and `/opt/nebula/bin/Logs/gateway.log` on the
orchestrator VM, `/var/log/nebula-worker.log` and `/var/log/nebula-bootstrap.log` on each worker VM.

What is not there yet: the control-plane reducers accept any caller, so anyone who knows the maincloud database
name could register a fake worker (a shared secret checked in the reducers is the next step); there is no warm pool,
so a replacement worker is ~30 s away rather than instant; the dashboard is unauthenticated on a public port.

## What has been verified (2026-09-08, all on one machine)
`Tools/smoke-test.ps1` runs the whole mesh headless with bot clients that roam between quadrants and shoot at
each other, then reads the logs:

| Scenario | Result |
| --- | --- |
| 4 workers, 3 bots, 75 s | 47 authority handovers (out = in), 39 cross-worker hits, 15 kills, 0 errors/warnings |
| 4 workers, 2 bots, kill `w3` at 25 s | SE reassigned to `w1` within a second, `w3` relaunched after 8 s and got SE back; sim never stopped |
| 2 workers, 4 bots, kill `w1` at 20 s | `w2` took all four containers; the two players whose pawns died were respawned on `w2` |
| 4 workers, 3 bots, dashboard scale 4 -> 2 at 25 s | `w3`/`w4` drained in one pass (each handed its container and bots over), shut down with 0 drain timeouts; 34 handovers out = 34 in, 0 errors |
| same run, dashboard buttons | Add worker relaunched `w3` (index reused) and moved one container to it; Remove `w1` drained exactly its two containers; Kill `w3` moved its containers to `w2` within a second and they flowed back after the 8 s relaunch |
| 4 workers, 128 NPCs, 7 min | all 128 alive on the dashboard throughout; ~30 handovers/s sustained (out = in), 0 errors, 0 warnings, 0 dropped packets; worker tick 0.25-0.35 ms, each worker ~15% of one core, 6 processes total. The same 128 as bot clients had ground the machine to a halt (128 uncapped headless Unity players) |

26 EditMode tests cover serialization, the assignment policy and scaling helpers, container resolution/hysteresis,
the local control plane, RPC binding, NetworkVariable discovery and the interpolator (`Packages/com.1by3.nebula/Tests/EditMode`).

## What is deliberately not here yet
- No lag compensation / rewind on hit validation (the victim's worker applies the claim as-is).
- No delta compression or quantisation; every var change ships the entity's full var blob.
- Ghost interpolation on workers is one tick behind; the client interpolates 3 ticks behind.
- Container reassignment reuses the per-entity handover path (drain = transfer every entity), which is correct but
  not staged; there is no persistence, so a dead worker loses its transient entities (players respawn).
- RPCs and NetworkVariables are reflection-bound; a source generator would replace `RpcRegistry` without touching
  gameplay code.
