# The `nebula` CLI

The command-line tool is the developer's front door to Nebula: it installs Nebula into a Unity project, runs
the mesh locally, and deploys it to a cloud provider. It is one self-contained executable (no .NET runtime
needed) built from `cli/Nebula.Cli`, and it works the same on Windows, macOS and Linux.

## Installing

Hosted installers (the URLs are not live yet; the scripts are in `cli/install/`):

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
nebula setup                 install SpacetimeDB and the .NET 10 SDK, check git/ssh and the Unity editors
nebula init                  install Nebula into the Unity project you are in (Packages/com.1by3.nebula, manifest, nebula.json)
nebula build [--linux]       build the executable every role runs (host player, or the Linux server + tarball)
nebula start [--build] [--workers N] [--npcs N] [--bots N] [--open-ui]
nebula stop
nebula status [--cloud]      workers, containers, players/bots/NPCs, recent events
nebula logs [role] [-n N] [--follow] [--cloud]
nebula config hetzner|spacetime|unity|source|show
nebula deploy [--target hetzner] [--workers N] [--npcs N] [--open-ui]
nebula destroy [--all]
```

`nebula --help` and `nebula <command> --help` describe every option. Global options: `--project <path>`
(run against another project), `--verbose`, `--yes`.

## What `init` does

Run inside a Unity project. Nebula is a Unity package (`com.1by3.nebula`: runtime, editor tooling, the SpacetimeDB
control-plane module and generated bindings, vendored LiteNetLib, EditMode tests) that Unity fetches from the
repository on GitHub. `init` adds it to `Packages/manifest.json` pinned to the tag matching the CLI version
(`--ref <tag|branch>` picks another), adds the SpacetimeDB SDK package next to it, lists the package in
`testables` so its tests show in the Test Runner, writes a default `Assets/Resources/NebulaConfig.asset` (empty
prefab list; set `GameScene` and `NetworkPrefabs` in the Inspector) and creates `nebula.json` at the project root.
`--force` moves an existing entry to this CLI's release; the config asset and `nebula.json` are always kept.
`--sample` is reserved for a bundled sample, which does not ship yet.

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
              "spacetimeUri": "http://127.0.0.1:3000", "database": "nebula" },
  "deploy": { "target": "hetzner", "meshName": "nebula", "database": "nebula-mygame", "workers": 4, "npcs": 0 }
}
```

`mesh` holds the local defaults `nebula start` uses; `deploy` the cloud ones (`workerType`,
`orchestratorType` and `location` may be added to override the values in the CLI config). `executable` is the
base name NebulaBuild gives the player (`Nebula.exe`, `Nebula.x86_64`, `Nebula.app`).

## Building and running locally

`nebula build` runs `Nebula.Editor.NebulaBuild` in batchmode with the Unity version from
`ProjectSettings/ProjectVersion.txt`, found through Unity Hub's install folders, `NEBULA_UNITY`, or
`nebula config unity`. If the Editor has the project open (it holds an exclusive lock), the build automatically
runs from a mirrored copy under `~/.nebula-cli/scratch/<project>` and the result is copied back into `Builds/`.
Lines tagged `[nebula]` and compiler errors from the Unity log are echoed while it runs; the full log is
`Builds/unity-build.log`.

`nebula start` runs the whole local mesh: it starts a local SpacetimeDB if none answers on the
configured address (logs to `Builds/<platform>/Logs/spacetimedb.log`), publishes the control-plane module with
fresh data, launches the orchestrator (which launches the gateway and the workers), waits for the dashboard and
prints how to join. Logs live next to the build in `Logs/` (`orchestrator`, `gateway`, `w1..wN`, `bot1..`).
`nebula stop` kills every process of the build and the SpacetimeDB the CLI started (`--spacetime` to stop one
that was already running).

## Deploying

```
nebula config hetzner        API token (checked against the API), project label, region, VM types
nebula config spacetime      `spacetime login` for Maincloud (or another server) and the database name
nebula deploy --open-ui
```

`deploy` refuses to run until both are configured, and then: builds the Linux dedicated server and packs
`Builds/nebula-linux.tar.gz`; publishes the control-plane module to the configured server (`--reset-control-plane`
adds `--delete-data`); creates the ssh key (`~/.ssh/nebula_hetzner`), private network, firewalls and orchestrator
VM that are missing; uploads the tarball over scp, writes `/etc/nebula/env` (root-only, the token) and the
`nebula-orchestrator` systemd unit over ssh, restarts it and waits for the dashboard. The orchestrator creates
one VM per worker. `nebula status --cloud` and `nebula logs --cloud orchestrator|gateway|w1` read the deployed
mesh; `nebula destroy` deletes the servers (`--all` also the network, firewalls and key).

The Hetzner token is stored in `~/.nebula-cli/config.json` (0600 on Unix); `HCLOUD_TOKEN` in the environment
always takes precedence, so CI can run without the file. The CLI replaced the earlier `Tools/run-mesh.ps1`,
`Tools/build.ps1` and `Tools/hetzner/*.ps1` scripts; `Tools/smoke-test.ps1` drives the mesh through it.

## Layout

```
cli/
  Nebula.Cli/          the .NET 10 console app (Program.cs dispatches to Commands/*, Core/* and Cloud/* do the work)
  install/install.ps1  Windows installer (hosted at windows.nebula.1by3.co)
  install/install.sh   Linux/macOS installer (hosted at install.nebula.1by3.co)
  scripts/package.*    builds the per-platform release archives into cli/dist
```

`dotnet run --project cli/Nebula.Cli -- <args>` runs it from source without installing.
