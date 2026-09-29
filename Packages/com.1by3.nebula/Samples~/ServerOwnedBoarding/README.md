# Server-owned boarding (sample)

A character that no player owns walks onto a moving platform, rides it, and walks off again, the same way a
player's pawn does. It is a sample, not part of Nebula. How a character decides where to go, avoids obstacles and
animates is up to your game.

Nebula needs nothing special for this. The worker that simulates an entity resolves its container every tick,
whoever owns it. When the entity's origin enters a container with its own physics frame, the worker moves it into
the frame. It converts the entity's position, rotation and `Motion.Velocity` at the frame's pose for that tick. When
the entity is farther outside the box than `HandoverHysteresis`, the worker converts it back. If the carrier is
handed to another worker on the same tick, the entity goes with it, or goes to the carrier's new worker and crosses
there. Clients interpolate the entity across the crossing like any other entity.

- `MovingPlatform` is the carrier: a `NetworkIdentity`, a `NetworkTransform` and a `Container` with
  `OwnPhysicsFrame` on the same root object, plus deck colliders under it. Its authority moves it each tick.
- `ScopeWalker` is the character: a `CharacterController` that walks to a target each tick in whatever space it is
  in. It turns the target from the scope's space into its own space with `Identity.FromScope`, and writes its
  movement into `Identity.Motion.Velocity`. In `OnContainerChanged` it reads its fall speed back from that
  converted velocity.

## Using it

On a worker, spawn both with `NebulaWorker.SpawnServerDriven`, then tell the walker where to go on the worker that
has authority over it:

```csharp
worker.SpawnServerDriven(platform, groundContainer);
worker.SpawnServerDriven(walker, groundContainer);
walker.GetComponent<ScopeWalker>().WalkTo(platform.transform.Find("spot")); // a marker on the deck
// later
walker.GetComponent<ScopeWalker>().WalkTo(pointOnTheGround);                // a point in the scope's own space
```

`WalkTo(Transform)` follows a transform, so a marker on the deck takes the walker aboard and keeps it on that spot
while the platform flies. Clients need no code.

Size the platform's box so that it:

- reaches from the deck to above a standing character's origin, so a character stepping aboard is inside it by more
  than `HandoverHysteresis` (0.35 m by default);
- stops about half a metre inside the deck's edges, so a character walking off is past the box by the hysteresis
  while it still stands on the deck. It then steps down onto the ground from the deck's original, rather than
  dropping off the edge of the deck's copy inside the frame.

`Tests/ServerOwnedBoardingTests.cs` runs the whole trip on one worker, a tick at a time. Nebula's conformance
scenario 37 (`ConformanceServerOwnedBoardingTests`) covers the same trip with a handover of the platform on the
boarding tick, and a client's view of it.

Guide: https://nebula.1by3.co/docs/guides/physics-frames#cross-the-frames-boundary
