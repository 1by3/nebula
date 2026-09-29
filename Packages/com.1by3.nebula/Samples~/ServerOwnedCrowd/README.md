# Server-owned crowd (sample)

A thousand server-owned walkers in two relevance tiers. It is a sample, not part of Nebula: what a crowd member
does, and which ones are active, are game decisions (`docs/server-owned-entities.md`).

- `CrowdWalker` walks to a random point inside its area, waits a few seconds and picks the next one. It moves by the
  `deltaTime` it is given, so it covers the same ground whatever its update interval, and it carries its target,
  its wait and its random state in `WriteHandoverState`, so the worker it is handed to at a seam carries on exactly
  where the last one stopped.
- `CrowdSpawner` spawns `Count` walkers server-driven on the worker that owns the container at `AreaCentre`, once the
  mesh is ready. The first `ActiveCount` are updated every `ActiveUpdateInterval` ticks at `RelevancePriority.Normal`;
  the rest every `LightUpdateInterval` ticks at `LightPriority` (`Background` by default), and they fall asleep after
  `LightSleepWhenUnobserved` seconds (10) with no client near: not ticked and not sent until a client comes near
  again. Walkers spawned into another worker's container are handed to it on the next tick.

## Using it

1. Make a walker prefab: a `NetworkIdentity`, a root `NetworkTransform` and a `CrowdWalker`. Register it in
   `NebulaConfig.NetworkPrefabs`. A walker only turns about its vertical axis, so turn off `SyncRotAngleX` and
   `SyncRotAngleZ`; turn off `SyncVelocity` if your clients do not extrapolate; and turn on `UseHalfFloatPrecision`
   if your containers are a few hundred metres across or smaller. The default fields cost 46 bytes an update; yaw
   only in half floats, 24.
2. Add a `CrowdSpawner` to the scene your workers load, set `WalkerPrefab`, and set `AreaCentre` and `AreaSize` to
   ground your containers cover.
3. For a crowd rather than a firefight, slow the gateway's distance tiers in `NebulaConfig`:

   | Setting | Default | Crowd |
   |---|---|---|
   | `InterestNearRadius` | 30 | 25 |
   | `InterestFarRadius` | 80 | 60 |
   | `InterestMidDivisor` | 4 | 30 |
   | `InterestFarDivisor` | 12 | 120 |

   These apply to every entity, players included. In a game with players, leave them at their defaults and give the
   crowd's priorities tiers of their own instead: `InterestBackgroundTiers` for the light walkers and, if you give the
   active ones `RelevancePriority.Low`, `InterestLowTiers` (`docs/server-owned-entities.md` §7, and "Set the distance
   tiers for a crowd" in the guide).

With those settings and the defaults above, a thousand walkers across two workers cost each worker about 0.3 ms a
tick in the Editor, and a client standing among 200 active and 1,000 light walkers is sent about 87 kbit/s
(`docs/server-owned-entities.md` §5).

A real game promotes the characters near players to the active tier and demotes the rest by setting
`NetworkIdentity.UpdateInterval` and `NetworkIdentity.RelevancePriority` on the worker that has authority.

Guide: https://nebula.1by3.co/docs/guides/server-owned-entities
