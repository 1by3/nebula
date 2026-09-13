# Nebula's SpacetimeDB modules

Nebula runs two SpacetimeDB databases side by side. Both live here, each with its own module sources and its
own generated client bindings. The trailing `~` on the module folders hides them from Unity's asset pipeline,
so the server-side C# is never compiled into the game.

| | module | database | bindings | client |
|---|---|---|---|---|
| control plane | `Module~/` | `nebula` | `Generated/` (`Nebula.Spacetime`) | `Runtime/ControlPlane/SpacetimeControlPlane.cs` |
| persistence | `PersistenceModule~/` | `nebula-persist` | `GeneratedPersistence/` (`Nebula.Spacetime.Persistence`) | `Runtime/Persistence/SpacetimePersistenceStore.cs` |

The `Nebula.Spacetime` assembly definition covers this folder recursively, so both binding folders compile into
it. They use different namespaces and share no global types.

## Control plane (`Module~`)

Process registrations, container leases, authority epochs and game-defined settings. No per-tick game state:
workers exchange that directly and the gateway routes it. It is published with `--delete-data` because
everything in it is ephemeral registry state.

```powershell
cd Packages/com.1by3.nebula/SpacetimeDB/Module~
spacetime build
spacetime generate --lang csharp --module-path . --out-dir ../Generated --namespace Nebula.Spacetime -y
spacetime publish -s local nebula -y --delete-data   # after `spacetime start` is running
```

## Persistence (`PersistenceModule~`)

The long-term store for entities that opted in (`PersistentEntity`): one `persisted_entity` row per entity, with
the reducers `SaveEntity`, `DeleteEntity` and `ClearPersistence`. `SaveEntity` upserts and holds the only write
rule — a save whose authority epoch is older than the stored row's is dropped, and every accepted save bumps
`Version` and stamps `SavedAt`/`SavedBy`. **Never publish this module with `--delete-data`** unless you mean to
wipe saved worlds (`nebula start --reset-persistence` and `nebula deploy --reset-persistence` do exactly that).

```powershell
cd Packages/com.1by3.nebula/SpacetimeDB/PersistenceModule~
spacetime build
spacetime generate --lang csharp --module-path . --out-dir ../GeneratedPersistence --namespace Nebula.Spacetime.Persistence -y
spacetime publish -s local nebula-persist -y
```

Two notes on this module's project file:

- `AssemblyName` is `NebulaPersistenceModule`, but the spacetime CLI (2.10) looks for a build output literally
  named `StdbModule.wasm`. The csproj therefore carries a small `NebulaAliasWasmForCli` target that copies the
  produced wasm to that name after the build. Nothing else in the project differs from `Module~`'s.
- The generated bindings needed no hand edits: every generated type sits inside `Nebula.Spacetime.Persistence`,
  so nothing collides with the control-plane bindings next to them.

## Regenerating from the Editor or the CLI

`Nebula > Control Plane > Publish Module` and `Nebula > Control Plane > Regenerate C# Bindings` run the commands
above for **both** modules, and so does `nebula start` (control plane with fresh data, persistence without).
After regenerating outside the Editor, add the `.meta` files with `bash Tools/gen-meta.sh Packages/com.1by3.nebula`
from the repository root — never inside the `~` folders, which Unity ignores.
