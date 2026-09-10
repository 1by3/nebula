# Nebula

Middleware for dynamically meshed multiplayer game servers, Unity-first: Unity dedicated-server workers that hand
entities to each other, an orchestrator that assigns world containers to workers through a SpacetimeDB control
plane, and a gateway clients connect to. One player build runs every role.

Nebula ships as a Unity package, `com.1by3.nebula`, and a command-line tool, `nebula`, that installs the package
into a Unity project, runs the mesh locally and deploys it to a cloud provider.

```powershell
iwr https://windows.nebula.1by3.co -useb | iex      # Windows
```

```bash
curl -sSf https://install.nebula.1by3.co | sh       # Linux / macOS
```

Then, inside your Unity project: `nebula setup`, `nebula init`, `nebula build`, `nebula start --open-ui`.
The docs at [nebula.1by3.co](https://nebula.1by3.co) have the tutorial and the guides.

## Layout

This repository is itself a Unity project (6000.x) with the package embedded, so the middleware can be opened,
compiled, tested and built on its own.

- `Packages/com.1by3.nebula` - the package: runtime, editor tooling (menus, `NebulaBuild`, world authoring),
  `World/` cell streaming, `SpacetimeDB/` control-plane module (`Module~`) and generated bindings, vendored
  LiteNetLib, EditMode tests. Consumers reference it from `Packages/manifest.json`
  (`nebula init` does this) or embed a copy (`nebula init --embed`).
- `Assets/Resources/NebulaConfig.asset` - this project's config; every consuming project has its own.
- `cli/` - the CLI's sources (.NET 10, one self-contained executable per platform), its install scripts and the
  packaging script that builds the release archives.
- `website/` - the docs site (Fumadocs). `cd website && npm install && npm run dev`; `npm run gen` regenerates
  the CLI and API references from the sources.
- `docs/architecture.md` - the design. `docs/cli.md` - the CLI. `docs/prototype.md` - how the pieces fit.
- `Tools/typecheck.ps1` - Editor-free compile check of every assembly; `Tools/smoke-test.ps1` - unattended mesh run
  that reports handovers; `Tools/gen-meta.sh` - deterministic `.meta` files.

The ShooterGame demo that exercises all of this is a separate project (it uses licensed art that cannot be
redistributed); it consumes this repository through the package like any other game.

## Developing

Open this folder in Unity to work on the package; `Tools/typecheck.ps1` compiles everything without the Editor.
`dotnet run --project cli/Nebula.Cli -- <args>` runs the CLI from source. A game project next to this checkout can
use the working copy directly with `"com.1by3.nebula": "file:../../nebula/Packages/com.1by3.nebula"` in its
manifest.

Releases are tags `vX.Y.Z`; the CLI's version (`cli/Nebula.Cli/Nebula.Cli.csproj`) and the package's
(`Packages/com.1by3.nebula/package.json`) match the tag, and `nebula init` pins the package to the tag of the CLI
that ran it.
