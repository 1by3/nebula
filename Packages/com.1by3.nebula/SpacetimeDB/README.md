# Nebula control plane (SpacetimeDB)

- `Module~/` is the SpacetimeDB server module (C#, .NET 10, compiled to WASM). The trailing `~` hides it
  from Unity's asset pipeline so its sources are not compiled into the game.
- `Generated/` holds the C# client bindings produced by `spacetime generate` in the `Nebula.Spacetime`
  namespace. Regenerate them whenever `Module~/Lib.cs` changes.

```powershell
cd Assets/Nebula/SpacetimeDB/Module~
spacetime build
spacetime generate --lang csharp --module-path . --out-dir ../Generated --namespace Nebula.Spacetime -y
spacetime publish -s local nebula          # after `spacetime start` is running
```

`Nebula > Control Plane > Publish Module` in the Editor runs the same commands.
