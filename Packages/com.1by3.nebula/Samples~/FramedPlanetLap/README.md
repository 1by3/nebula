# Framed planet lap (sample)

A ship flies from the ground of a planet up into space and back down, in one scope, with no transfer. The planet is a
carrier whose box has its own physics frame and regions of its own; its ground is runtime containers leased under it,
dealt across workers. It is a sample, not part of Nebula: the path, the planet's size and how the ground is cut into
chunks are up to your game.

- `TurningPlanet` is the planet: a `NetworkIdentity`, a `NetworkTransform` and a `Container` with `OwnPhysicsFrame` on
  and `FrameInterest` set to `OwnRegions`, on the same root. Its authority turns it slowly. `RequestGround` asks for
  the ground chunks this worker should hold, as runtime containers whose parent is the planet's container
  (`ContainerPlacement.Child`, `ContainerAuthority.Leased`).
- `LapFlight` is on the ship (a carrier with a frame of its own): it flies waypoints given in the planet's own
  coordinates, putting the ship in whichever space it is in each tick and keeping `Motion.Velocity` right. The
  worker crosses the ship into and out of the planet's frame at the box's top (`docs/container-tree.md` D15).
- `FrameCrossingProbe` runs on a client: it records every rendered frame in which an entity's drawn step is off from
  its motion, every change of container with the step on that frame, and every long frame.

## Using it

On every worker, once the planet has spawned there, request that worker's ground (with two workers, slot 0 on the
first and 1 on the second), then spawn the ship on the worker that holds its chunk:

```csharp
planet.GetComponent<TurningPlanet>().RequestGround(worker, slot, workers);
var ship = worker.SpawnServerDriven(shipPrefab, groundChunk);
ship.GetComponent<LapFlight>().Planet = planet;
```

On a client, watch the pawn aboard the ship:

```csharp
probe.Target = myPawn;
// later
Debug.Log($"worst jump {probe.WorstJump * 1000f:0} mm, at a crossing {probe.WorstJumpAtCrossing * 1000f:0} mm");
```

## What the lap shows

Nebula's conformance test `Tests/EditMode/ConformanceFramedPlanetLapTests.cs` flies this lap on two in-process
workers and a gateway, a tick at a time, with the planet still and turning. It is the repro for what the lap finds:

- Position at every chunk seam, worker seam and box crossing: within float precision.
- Velocity at a box crossing with the planet turning: ω × r is lost below a few degrees a second, because the frame's
  angular velocity is a one-tick difference of float rotations (`AFrameTurningSlowlyReadsItsAngularVelocity`).
- A client outside the planet's box sees nothing that stands on the planet (`OwnRegions`, `docs/container-tree.md` §8).
- A carrier is bucketed by region on its worker, so a gateway receives a ship only while it subscribes the ship's
  region (about `InterestRadius` around a player), whatever the ship's `RelevanceRadius`.
- A client draws the ship across the box crossing within a few millimetres with the planet still, and with an error
  near ω × r times a fraction of a tick with it turning.

Guide: https://nebula.1by3.co/docs/guides/physics-frames
