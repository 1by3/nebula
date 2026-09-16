# The `nebula` CLI

The command-line tool installs Nebula into a Unity project, runs the mesh locally, and deploys it to Nebula
Cloud or to your own Hetzner Cloud project. It is one self-contained executable (no .NET runtime
needed) built from `cli/Nebula.Cli`, and it works the same on Windows, macOS and Linux.

## Installing

Hosted installers (they proxy to `cli/install/` on the `main` branch, so a push updates them):

```powershell
iwr https://windows.nebula.1by3.co -useb | iex
```

```bash
curl -sSf https://install.nebula.1by3.co | sh
```

From a checkout (needs the .NET 10 SDK): the same scripts build the CLI with `dotnet publish` and remember
the checkout so `nebula init --embed` copies the package from it instead of cloning.

```powershell
powershell -File cli\install\install.ps1 -Source .
```

```bash
sh cli/install/install.sh --source .
```

Both put `nebula` in `~/.nebula-cli/bin` and add it to `PATH` (open a new terminal afterwards). Other modes:
`-Archive`/`--archive <file>` installs a local release archive; without either, the script downloads
`nebula-<version>-<rid>.zip|tar.gz` from the repository's releases (`NEBULA_VERSION` picks one,
`NEBULA_RELEASE_BASE` another host). `cli/scripts/package.{ps1,sh}` builds those archives for every platform
into `cli/dist`.

## Commands

```
nebula setup                 install the .NET 10 SDK, check git/ssh and the Unity editors
nebula init                  install Nebula into the Unity project you are in (Packages/com.1by3.nebula, manifest, nebula.json)
nebula build [--linux]       build the executable every role runs (host player, or the Linux server + tarball)
nebula start [--build] [--workers N] [--min N] [--max N] [--npcs N] [--bots N] [--open-ui] [--reset-persistence]
                             --workers N fixes the count (min = max = N); --min/--max open the autoscaling band and
                             the mesh starts at --min, growing and shrinking between the two (--min 0 scales to zero)
nebula stop
nebula status [--cloud]      dashboard + gateway addresses, workers, containers, players/bots/NPCs, persistence, recent events
nebula logs [role] [-n N] [--follow] [--cloud] [--instance x] [--since 10m]
nebula scale --min N --max N [--gateways-min N --gateways-max N] [--target local|hetzner|cloud]
nebula dashboard [--target local|hetzner|cloud]
nebula config hetzner|database|unity|source|show
nebula deploy [--target hetzner|cloud] [--workers N] [--min N] [--max N] [--npcs N] [--open-ui] [--reset-persistence]
                             same --workers/--min/--max semantics as `nebula start`; defaults from nebula.json deploy.*
                             cloud: [--release rel_id] [--label v12] [--allow-protocol-change] [--region id] [--worker-size s]
nebula destroy [--target hetzner|cloud] [--all]
nebula cloud login|logout|account
nebula deployments [--all] [--json]
nebula rollback [--release rel_id]
```

`nebula --help` and `nebula <command> --help` describe every option. Global options: `--project <path>`
(run against another project), `--verbose`, `--yes`.

## What `init` does

Run inside a Unity project. Nebula is a Unity package (`com.1by3.nebula`: runtime, editor tooling, the standalone
service sources, vendored LiteNetLib, EditMode tests) that Unity fetches from the repository on GitHub. `init` adds
it to `Packages/manifest.json` pinned to the tag matching the CLI version (`--ref <tag|branch>` picks another),
removes the SpacetimeDB SDK entry older releases added, lists the package in
`testables` so its tests show in the Test Runner, writes a default `Assets/Resources/NebulaConfig.asset` (empty
prefab list; set `GameScene` and `NetworkPrefabs` in the Inspector) and creates `nebula.json` at the project root.
`--force` moves an existing entry to this CLI's release; the config asset and `nebula.json` are always kept.

`--embed` copies the package's sources into `Packages/com.1by3.nebula` instead (an *embedded* package: Unity
compiles it like project code, so you can read and modify the middleware in place). The sources come from
`--source <path>`, `$NEBULA_SOURCE`, the `sdkSource` in the CLI config (set by the from-source installer, or
`nebula config source --path`), or a shallow clone of the repository into `~/.nebula-cli/sdk` at the tag matching
the CLI version (`main` when the tag does not exist).

## `nebula.json`

Per-project settings that travel with the project:

```json
{
  "nebula": "0.1.0",
  "executable": "Nebula",
  "mesh":   { "workers": 4, "npcs": 0, "dashboardPort": 7080, "gatewayPort": 7000,
              "database": "sqlite:Library/Nebula/nebula.db" },
  "deploy": { "target": "hetzner", "meshName": "nebula-mygame", "database": "postgres://user:pw@host/db",
              "persistenceDatabase": "nebula-mygame-persist", "workers": 4, "minWorkers": 1, "maxWorkers": 8,
              "idlePoolSeconds": 0, "npcs": 0 },
  "cloud":  { "organization": "my-studio", "project": "my-game", "deployment": "production" }
}
```

`mesh` holds the local defaults `nebula start` uses; `deploy` the cloud ones (`workerType`,
`orchestratorType` and `location` may be added to override the values in the CLI config). `deploy.target` is
`hetzner` or `cloud`. `cloud` names the Nebula Cloud organization, project and deployment; the first
`nebula deploy --target cloud` writes it. Keys the CLI does not know are kept as they are. `executable` is the
base name NebulaBuild gives the player (`Nebula.exe`, `Nebula.x86_64`, `Nebula.app`).

`database` names the control-plane database, `persistenceDatabase` the separate database that holds saved
entities. Leave `deploy.persistenceDatabase` out and the CLI uses the control-plane database name with a
`-persist` suffix.

## Building and running locally

`nebula build` runs `Nebula.Editor.NebulaBuild` in batchmode with the Unity version from
`ProjectSettings/ProjectVersion.txt`, found through Unity Hub's install folders, `NEBULA_UNITY`, or
`nebula config unity`. If the Editor has the project open (it holds an exclusive lock), the build automatically
runs from a mirrored copy under `~/.nebula-cli/scratch/<project>` and the result is copied back into `Builds/`.
Lines tagged `[nebula]` and compiler errors from the Unity log are echoed while it runs; the full log is
`Builds/unity-build.log`.

`nebula start` runs the whole local mesh: it launches the orchestrator from the build with its database at
`Library/Nebula/nebula.db` (the orchestrator hosts the control plane and launches the gateway and the workers),
waits for the dashboard and prints how to join. Saved entities stay across restarts (`--reset-persistence` wipes
them). Logs live next to the build in `Logs/` (`orchestrator`, `gateway`, `w1..wN`, `bot1..`).
`nebula stop` kills every process of the build.

The control plane only records which processes are running, so `nebula start` republishes it with fresh data
every time. Saved entities live in their own database and survive restarts. To throw them away, run
`nebula start --reset-persistence`, which republishes the persistence module with `--delete-data`.
`nebula status` prints a persistence line (mode, backend, connection, number of saved entities) when the mesh
reports one.

## Deploying to Nebula Cloud

```
nebula cloud login           device code: the CLI prints it, opens the Cloud Dashboard, and polls until you approve
nebula deploy --target cloud --open-ui
```

The first cloud deploy in a project picks (or creates) the organization, project and deployment, interactively or
from `--org`, `--cloud-project`, `--deployment`, `--region`, `--worker-size` (`--yes` takes the defaults), and
writes them to `nebula.json`. Every deploy then: builds the Linux server and packs the tarball; computes its
SHA-256 and registers an artifact (`POST /v1/projects/{p}/artifacts`), uploads it to the presigned URL with a
progress line and completes it (content-addressed: the same bytes are never uploaded twice); creates a release
with the service manifest, the CLI version, `HelloMsg.ProtocolVersion` read from the package source and the git
commit/branch/dirty flag; patches the worker band when `--min/--max` were given; starts a rollout and follows the
operation's events (`GET /v1/operations/{id}/events?after=&wait=30`) until it finishes. Ctrl-C leaves the operation
running and the next run reattaches to it; a 409 from the rollout does the same with the operation it names.

`nebula status --cloud` renders `GET /v1/deployments/{d}/status` (health, release, orchestrator, gateways with
clients/traffic/cpu/lag, workers, mesh totals); `nebula logs --cloud <role|w1|gw1> [--since 10m] [--follow]` reads
the log page and then the server-sent event stream; `nebula scale`, `nebula rollback`, `nebula destroy` (type the
deployment name, or `--yes`) and `nebula dashboard` map to the corresponding endpoints. `nebula deployments`
lists deployments across the project's organization (`--all` for every organization).

`Cloud/CloudApi.cs` is the client: bearer tokens from `~/.nebula-cli/config.json` (`cloud` section), a refresh
on 401 that rotates and saves the refresh token, an `Idempotency-Key` (one random id per CLI invocation plus the
operation name) on every mutating call so a retried request replays instead of repeating, a
`User-Agent: nebula-cli/<version>`, and the error envelope mapped to messages with hints (402 spend limit, 426
upgrade required, 401 log in). `NEBULA_CLOUD_API` overrides the API host; `--api` at login stores one.

`cli/Nebula.Cli.Tests` (NUnit) runs the commands in-process against a fake Cloud API on a local port:
`dotnet test cli/Nebula.Cli.Tests`.

## Deploying to Hetzner

```
nebula config hetzner        API token (checked against the API), project label, region, VM types
nebula config database       optional: a PostgreSQL URL for deployed meshes (default: SQLite on the orchestrator VM)
nebula deploy --open-ui
```

`deploy` refuses to run until both are configured, and then: builds the Linux dedicated server and packs
`Builds/nebula-linux.tar.gz`; publishes the control-plane module to the configured server (`--reset-control-plane`
adds `--delete-data`) and the persistence module next to it, keeping its saved entities unless you pass
`--reset-persistence`; creates the ssh key (`~/.ssh/nebula_hetzner`), private network, firewalls and orchestrator
VM that are missing; uploads the tarball over scp, writes `/etc/nebula/env` (root-only, the token) and the
`nebula-orchestrator` systemd unit over ssh, restarts it and waits for the dashboard. The orchestrator creates
one VM per worker. `nebula status --cloud` and `nebula logs --cloud orchestrator|gateway|w1` read the deployed
mesh; `nebula destroy` deletes the servers (`--all` also the network, firewalls and key). A SQLite database goes
with the orchestrator VM; a PostgreSQL database is left in place.

The Hetzner token is stored in `~/.nebula-cli/config.json` (0600 on Unix); `HCLOUD_TOKEN` in the environment
always takes precedence, so CI can run without the file. The CLI replaced the earlier `Tools/run-mesh.ps1`,
`Tools/build.ps1` and `Tools/hetzner/*.ps1` scripts; `Tools/smoke-test.ps1` drives the mesh through it.

## Layout

```
cli/
  Nebula.Cli/          the .NET 10 console app (Program.cs dispatches to Commands/*, Core/* and Cloud/* do the work)
  Nebula.Cli.Tests/    NUnit tests with an in-process fake of the Nebula Cloud API
  install/install.ps1  Windows installer (hosted at windows.nebula.1by3.co)
  install/install.sh   Linux/macOS installer (hosted at install.nebula.1by3.co)
  scripts/package.*    builds the per-platform release archives into cli/dist
```

`dotnet run --project cli/Nebula.Cli -- <args>` runs it from source without installing.
