# Lag-compensated hitscan (sample)

A reference validator built on `PortalQueries.Raycast` with a tick, which rewinds on `NetworkIdentity.StateAt(tick)`. It is a sample, not part of
Nebula: deciding which tick a client may claim and applying damage are game decisions (`docs/state-history.md` §6).

`LagCompensatedHitscan.Fire(origin, direction, aimTick)` sends a server RPC with the tick the shooter's client
was rendering (`NetworkTime.RenderTick`). On the worker it:

1. refuses a tick in the future or further back than the game allows;
2. casts the ray as the world stood at that tick, from the shooter's scope: `worker.PortalQueries.Raycast(PortalAsker.Of(Identity), Identity.InstanceId, ...)`
   with `PortalQueryOptions.Tick` set and `RewindNear` on. Nebula poses every entity the worker holds - the ones it simulates and the ghosts of its
   neighbours' - at its recorded pose for the length of the cast and puts them back, and carries the cast on through any portal that is open for the
   shooter into the room behind it. The sample has no scope filter of its own and no hitbox maths: which scope a victim stood in, and whether it
   crossed a portal since the claimed tick, is the query's to know; an entity whose history cannot answer for the tick is left where it is;
3. ignores a hit that is not a victim (a closed door is a hit on its plane, `hit.IsAperture`; a cast that left what this worker holds is
   `hit.IsTruncated`) and otherwise calls `SampleHealth.ApplyDamage`, which uses `AuthorityRpc(...)` to apply damage on the victim's worker
   (`docs/cross-worker-calls.md`). The sample uses explicit send helpers; attributes alone do not send calls.

`SampleHealth` is the victim side, and shows `[SyncHistory]` on a `Stance` variable: `TryGetStanceAt(tick)` reads the stance *at the claimed tick* rather
than the current one, for a validator that wants a smaller target for a crouching player.

## Using it

Add `LagCompensatedHitscan` to the shooter prefab and `SampleHealth` to anything shootable, with real colliders on the victims. Set
`NebulaConfig.StateHistoryTicks` (default 32) wide enough to cover your players' interpolation delay plus half a
round trip plus input lead. A game with hitboxes that are not the entity's root (bones, a head sphere) poses them from `state.Position`,
`state.Rotation` and the marked variables itself, around the same call.

Guides: https://nebula.1by3.co/docs/guides/lag-compensation and https://nebula.1by3.co/docs/guides/instancing#cast-through-a-portal
