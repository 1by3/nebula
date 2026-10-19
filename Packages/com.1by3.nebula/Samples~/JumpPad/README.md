# Jump pad (sample)

A pad that throws a player's pawn into the air, with the launch predicted: the owner's client shows it without
snapping back, even though only the worker decides it. It is a sample, not part of Nebula: the movement model is
deliberately small.

- `LaunchEvent` is the server event: the velocity the pawn takes. It implements `INetworkSerializable`.
- `JumpPadWalker` is a `PredictedBehaviour<WalkerInput>`. Inside `Simulate`, `TryGetServerEvent<LaunchEvent>`
  gives it the launch for the tick being simulated, and it applies the launch to its own state (vertical speed
  and push). `WriteState` and `ReadState` carry that state, so the owner's replay and the next worker start from
  it.
- `JumpPad` runs on the worker that has authority over the pad. Each tick it posts a `LaunchEvent` with
  `PostServerEvent` to every predicted entity in its container that stands on it.

## Using it

Make a pawn prefab with a `NetworkIdentity` and `JumpPadWalker`, and a pad prefab with a `NetworkIdentity` and
`JumpPad`, in the same container. Register both. On the client, set `JumpPadWalker.Controls` from your input
every frame.

## What happens

1. The pad posts the event on the worker. It is stamped with the next tick the pawn simulates, T.
2. The worker's `Simulate` for tick T sees it and launches the pawn.
3. The state report for tick T, or for the tick before it, carries the event to the owner.
4. The owner's client keeps the event and replays its ticks from the reported state with the event on tick T.
   Its simulated state ends where the worker's did, so there is nothing left to correct.
5. The owner hears of the launch about half a round trip after it happened, so the pawn starts rising that much
   later than on the worker. The replay moves the simulated pawn to where it would be, and correction smoothing
   draws the pawn easing there over about 100 ms instead of jumping.

Guide: https://nebula.1by3.co/docs/guides/prediction#apply-server-events
