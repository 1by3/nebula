# Lag-compensated hitscan (sample)

A reference validator built on `NetworkIdentity.StateAt(tick)`. It is a sample, not part of Nebula: rewinding
colliders, deciding which tick a client may claim and applying damage are game decisions
(`docs/state-history.md` §6).

`LagCompensatedHitscan.Fire(origin, direction, aimTick)` is a `[ServerRpc]` the shooter's client sends with the
tick it was rendering. On the worker it:

1. refuses a tick in the future or further back than the game allows;
2. asks every entity the worker holds — the ones it simulates and the ghosts of its neighbours' — what it looked
   like at that tick, and tests the ray against a capsule at that recorded pose. No collider is moved, and an entity
   whose history cannot answer for the tick is simply not a candidate;
3. applies the damage through an `[AuthorityRpc]`, so a victim that has handed over mid-flight still takes it
   exactly once (`docs/cross-worker-calls.md`).

`SampleHealth` is the victim side, and shows `[SyncHistory]` on a `Stance` variable: a validator that wants a
shorter capsule for a crouching player reads the stance *at the claimed tick* rather than the current one.

## Using it

Add `LagCompensatedHitscan` to the shooter prefab and `SampleHealth` to anything shootable. Set
`NebulaConfig.StateHistoryTicks` (default 32) wide enough to cover your players' interpolation delay plus half a
round trip plus input lead. Then replace the capsule with your own hitboxes: a real game records its bone poses
itself, or derives them from `state.Position`, `state.Rotation` and the marked variables.

Guide: https://nebula.1by3.co/docs/guides/lag-compensation
