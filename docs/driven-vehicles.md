# Driven vehicles — measurements and design (NEB-361)

Status: measured (§1), driver prediction landed (§3), protocol 25 (§4).
Harness: `Tests/EditMode/DrivenVehicleHarness.cs`, measurement `Tests/EditMode/DrivenVehicleLatencyTests.cs` (prints
the table in §1 to the Editor log). Conformance scenario 39: `ConformanceDrivenVehicleTests` (worker, tier B),
`ConformanceDrivenVehicleClientTests` (the client component), `Services~/Nebula.Services.Tests/ConformanceDrivenVehicleGatewayTests.cs`
(the gateway, tier A). API: `NetworkIdentity.SetDriver`, `ClearDriver`, `DriverClientId`, `IsLocallyDriven`,
`NetworkBehaviour.IsDriver`, `PredictedBehaviourBase.OnDriverChanged`, `DriverInputTimeoutTicks`,
`NebulaClient.DrivenEntities`; wire `MsgId.DriveInput` (25), `DriveInputMsg`, `EntitySpawnMsg.DriverClientId`. User
guide: "Drive a vehicle" in `website/content/docs/guides/prediction.mdx`.

## 0. Problem

A game wants vehicles players drive: a boat, a car, anything with a seat and physics of its own. Such a vehicle is a
server-owned entity (`NebulaWorker.SpawnServerDriven`): no client owns it, several players may ride in it, and it
outlives whoever sits at the wheel. Until now a server-owned entity was simulated by its worker only, and every
client, the driver included, saw it through the interpolated transform stream. Prediction
(`PredictedBehaviour<TInput>`) covered one entity per client, its own pawn.

The question the issue asks first: is that good enough? If it is, write it down and pin it with a test. If not, pick the
smallest general mechanism, either **driver prediction** (the driver's client predicts the vehicle with a deterministic,
game-supplied model and is reconciled to the worker's state) or **driver authority** (the driver's client simulates it
and the worker checks what it reports), keeping seats and passengers consistent, handing the vehicle over at worker
seams, and working inside a moving scope (a car in a ship's hold).

## 1. Measurements

**Method.** `DrivenVehicleHarness` runs one worker copy and one client copy of a vehicle in one process, in virtual
time, and every step between them is Nebula's own code: the worker consumes input with `ServerReceiveInput` and
`NetworkTick`, the interpolated view is a real `RemoteInterpolator`, the predicted view is `ClientPredictTick` reconciled
with `ClientReconcile`. The link is a one-way delay of half the round trip time (RTT) plus a uniform jitter of
2 ms + 15 % of the one-way delay, delivered sequenced (an older packet behind a newer one is dropped). The client's clocks
are `NebulaClient`'s: the server tick estimate anchored on the newest snapshot plus half the RTT, the render tick
`InterpolationDelayTicks` (3) behind the newest snapshot with its offset smoothed at 3/s, and inputs sent ahead by half
the RTT plus `InputLeadMarginTicks` (2) plus the adaptive adjustment towards `InputLeadTargetTicks` (3). Frames are 60
a second, each a FixedUpdate then an Update, at a phase that is not the worker's; a chase camera follows the shown pose
with an 80 ms time constant. The vehicle is a kinematic car (8 m/s² of throttle, drag 0.4/s, up to 60°/s of yaw,
steering scaled by speed up to 4 m/s).

"Worker simulates" is today's path for a vehicle: the driver's input reaches the worker as a pawn's does (the same
lead, the same repeat on a missed tick), the worker simulates, and the driver sees the result interpolated.

- **Input to visible**: full throttle, then at t = 3 s the wheel goes hard over; the time until the shown heading
  (the vehicle's, then the camera's) has moved half a degree.
- **Overshoot, settled**: the driver turns to a heading of 90°, steering by what the camera shows (a proportional
  driver: 4/s of gain, the wheel saturating 15° off); the largest heading past 90° the driver sees, and the time until
  the shown heading stays within 2°. 1.5 s is the fastest a 60°/s vehicle can turn 90°.
- **View jitter and worst hitch**: the frame-to-frame second difference of the shown pose less that of the true pose,
  RMS and worst single frame, over 5.5 s of driving.
- **Corrections**: after the first second (the input lead finding its level), and separately the one correction a
  0.5 m shove costs when only the worker knows about it (a collision the client's model does not have).

| Path | RTT | Input to visible (vehicle) | Input to visible (camera) | Overshoot of a 90° turn | Settled within 2° after | View jitter (RMS) | Worst hitch | Corrections |
|---|---|---|---|---|---|---|---|---|
| Worker simulates, driver interpolates | 30 ms | 140 ms | 156 ms | 5.4° | 2090 ms | 0.06 cm | 0.56 cm | n/a |
| Worker simulates, driver interpolates | 80 ms | 190 ms | 223 ms | 8.9° | 3290 ms | 0.08 cm | 0.56 cm | n/a |
| Worker simulates, driver interpolates | 150 ms | 273 ms | 290 ms | 12.8° | 3840 ms | 0.11 cm | 0.55 cm | n/a |
| Driver predicts | 30 ms | 23 ms | 40 ms | 0.1° | 1540 ms | 0.05 cm | 0.41 cm | 0; a 0.5 m shove: one of 0.50 m |
| Driver predicts | 80 ms | 23 ms | 40 ms | 0.1° | 1540 ms | 0.07 cm | 0.42 cm | 0; a 0.5 m shove: one of 0.50 m |
| Driver predicts | 150 ms | 23 ms | 40 ms | 0.1° | 1540 ms | 0.08 cm | 0.42 cm | 0; a 0.5 m shove: one of 0.50 m |

**Reading.** The interpolated view is smooth at every latency: jitter under 2 mm, no hitch worth the name. What is wrong
with it is the delay. The driver sees the vehicle answer the wheel a round trip plus 110 to 125 ms later: the input lead
(half the RTT plus 3 ticks, about 50 ms at 60 Hz), the worker's tick, the way back (half the RTT) and the render delay
(3 ticks behind the newest snapshot, 50 ms). That is 140 ms on a 30 ms link, already above the 100 ms at which players
start to notice a vehicle is late, and 273 ms at 150 ms. A driver steering by what they see oversteers: 5° to 13° past
the heading they wanted, and it takes 0.6 s to 2.3 s longer than the vehicle needs to settle on it. Camera smoothing adds
the same 16 to 33 ms on both paths and does not hide the delay.

With driver prediction the vehicle answers on the next rendered frame at every latency (23 ms is one frame plus the
frame's phase), the driver turns onto the heading without overshooting it, and a deterministic model agrees with the
worker on every tick, so there is nothing to correct. What prediction costs is visible in the last column: something
only the worker knows about (another player's car hitting yours, a ramp the client has not loaded) is corrected in one
step, by its size. See D9.

**Verdict:** not acceptable; driver prediction is the fix (§2).

## 2. Options

**Driver prediction.** The driver's client runs the vehicle's `PredictedBehaviour` with the driver's input, the worker
runs the same behaviour with the same input, and the client is reconciled to the worker's state. It is the path a pawn
already takes, so most of it existed: the input buffer, the repeat of a missed input, the lead adaptation, the
reconciliation and replay, the pending inputs and simulation state that travel with a handover, and container-local
state that makes prediction work in a moving container. What was missing is a client that is not the owner being
allowed to supply the input, and a client predicting a second entity. The worker stays the authority, so nothing the
driver sends can do more than the input struct allows.

**Driver authority.** The driver's client simulates the vehicle and sends its pose; the worker checks speed and
position limits and takes authority back when the driver leaves. It needs a new authority mode for an entity that
moves between workers, a validator the game has to tune per vehicle, and a rule for what the worker does with a pose
it refuses; a vehicle's passengers and anything it hits would be simulated by the worker against a pose the worker did
not produce. It would allow a non-deterministic model (a PhysX wheel collider car), which prediction does not (D10), but
it is the larger change and the weaker one against cheating.

**Chosen: driver prediction.** It is the smaller change, it reuses a path that is already tested across seams and in
moving containers, and the worker keeps authority.

## 3. Design decisions

**D1 A driver is the client in control of a server-owned entity's prediction.** `NetworkIdentity.SetDriver(clientId)`
on the worker that has authority hands the controls to a client; `ClearDriver()` (or `SetDriver(0)`) takes them back.
Only an entity with a `PredictedBehaviour<TInput>` that no client owns can be driven; the call is refused, with a
warning, otherwise. A pawn is its owner's alone. The input type and `Simulate` are the game's: a driven vehicle is an
ordinary predicted behaviour whose `GatherInput` reads the local player's controls and whose `GatherServerInput` says
what the vehicle does with nobody at the wheel.

**D2 The driver's input has a message of its own, and the worker reports to the driver as it does to an owner.** A
pawn's input (`ClientInput`) names no entity, and changing that would break every client. The driver sends
`DriveInput` (25): the vehicle's net id and the same three most recent input frames. The gateway forwards it only from
the client the entity's last spawn names as its driver, stamping the sender's client id, to the worker it believes owns
the entity; it drops it from anybody else and for any entity a client owns. The worker checks again (the driver it knows,
no owner) and runs the frames through `ServerReceiveInput`; a worker that has handed the entity on forwards the message
to the new owner, as `ForwardInput` does for a pawn. Every tick the worker sends `OwnerState` for the entity with the
driver's id, to the gateway of the driver's session when it knows it, and to every linked gateway when it does not (the
driver's pawn may live on another worker); a gateway passes it on only to that client, and only while the client holds
the entity. The worker does not claim the driver's session when input arrives: it may never hold that pawn.

**D3 Every copy knows who drives.** The driver's id is a trailing field of the entity's spawn body. A change marks the
entity, and the next publish sends every gateway that holds it a fresh spawn, which gateways and clients apply in place
(as a relevance priority change is). A gateway keeps a driven entity in its driver's set at full rate, as it does the
client's own entities. A client applies the driver from each spawn: the one it names starts predicting the entity, the
one it named before stops, and every copy's `OnDriverChanged(previous, current)` runs, so a game can show who is at the
wheel.

**D4 What the worker runs.** On each tick of a driven entity the worker runs the driver's input for that tick if it
has arrived. If not, it repeats the driver's last input, as it does for a pawn, for up to `DriverInputTimeoutTicks`
(30, half a second); after that, and before the driver's first input arrives, it runs `GatherServerInput`. A driver whose
link drops therefore does not leave the vehicle at full throttle, and gets the wheel back the moment its input returns;
the seat stays the driver's until the game clears it or the driver's session ends (D8). A driven entity is updated every
tick whatever its `UpdateInterval`, and it cannot sleep: `SetDriver` wakes it and `Sleep` is refused while it is driven.
On a change of driver the worker drops the input buffer, so nothing the previous driver sent is run for the next.

**D5 Handover.** The driver's id is in the spawn body, which `AuthorityTransfer` carries, so the worker that takes a
driven entity runs the same driver's input. The pending inputs and the `WriteState` state travel as they do for a pawn,
the timeout starts again on the new worker, the gateway re-routes `DriveInput` when the new owner's spawn arrives, and
the inputs sent to the old owner meanwhile are forwarded. The new owner's `OwnerState` carries its higher epoch, so the
driver drops a late report from the old owner. Scenario 39 drives a car across a seam at full throttle and it accelerates
on every tick, neither worker running its own input or repeating one.

**D6 The client predicts what it drives before its pawn.** `NebulaClient` predicts each driven entity, then the pawn, on
the same tick numbers, as a worker ticks a carrier before what rides in it: a pawn in the driving seat predicts against
the seat where the vehicle is now. Each driven entity is predicted in its own container's physics frame, and its box is
refreshed after it moves, so riders are placed through its new pose. `NebulaClient.DrivenEntities` lists them. A driven
entity's transform stream is ignored while it is predicted, exactly as the pawn's is.

**D7 Seats and passengers are the container tree's.** A vehicle with seats is a `Container` on its root (it carries a
box). Every entity in the box (the driver's pawn, passengers, cargo) is container-local, crosses frames with the
vehicle and is handed to another worker with it (`docs/container-tree.md`, scenario 37). On the driver's client the
vehicle is predicted, the driver's pawn is predicted in the box, and the other passengers' interpolated local poses are
placed through the predicted vehicle, so everyone stays in their seat on every client. Which seat a pawn takes, and when
it takes the wheel, is the game's: its pawn controller keeps the pawn at the seat, and its seat logic calls `SetDriver`
and `ClearDriver`. Scenario 39 flies a ship at up to 200 m/s with a 10°/s turn while a car with a passenger drives
circles in its hold, then drives out of it onto the ground: the passenger stays at its seat on every tick and the
driver's prediction is never corrected.

**D8 Leaving.** `ClearDriver` returns the entity to `GatherServerInput` from the next tick, announces it, and the former
driver's client stops predicting: its interpolation buffer starts again from the predicted pose, so the vehicle does not
jump back to where the stream last left it. When the driver's session ends on a worker that holds the vehicle (the
player leaves, is kicked or is not reclaimed in time, and the pawn rides in the vehicle, so both are on that worker), the
worker clears the driver itself. A disconnected driver who may
still come back keeps the seat, and D4's timeout takes care of the throttle; a game that wants the seat freed on a
disconnection calls `ClearDriver` from `NebulaGameMode.OnPlayerDisconnected`.

**D9 Adopting a report the client did not predict.** A report for a tick the client has no prediction for (it just took
the wheel, it just spawned, or the report is too old) used to be adopted outright, which threw away every tick the client
had predicted since: a driver taking a moving vehicle's wheel saw it pulled back by the input lead on every report for a
round trip. The client now adopts the worker's state and replays the inputs it predicted after that tick, as it does
after a correction. This applies to pawns too, and it is what keeps scenario 39's hold drive free of corrections. A
correction itself still moves the entity in one step; a game that wants it eased visually can use `OnCorrected`.

**D10 Not in this change.** A predicted vehicle needs a deterministic, kinematic model: the client re-simulates one entity
on its own during a replay, which PhysX cannot do for a `Rigidbody`, so a `NetworkRigidbody` vehicle is not predicted
(the same rule as for a pawn). Such a vehicle keeps working as before, simulated by its worker and seen interpolated by
everyone, with the delay in §1. Driver authority (§2) is not implemented. Other clients see a driven vehicle through the
interpolated stream, as before.

## 4. Protocol 25

Additive for clients, so the window is 24..25. New: `DriveInput` (25), client to gateway to worker and worker to worker.
`EntitySpawnBody` gains a trailing `driver_client_id` after `owner_claims`; a sender that writes it writes the earlier
trailing sections too (empty audience, empty maps, `owner_claims` count 0), and the body nested in `AuthorityTransfer`
always carries it, so the handover's later fields moved (workers must match exactly anyway). A protocol-24 client never
reads it and never sends `DriveInput`. `docs/protocol-versions.md` has the row and the replay fixture.

## 5. What a game writes

1. An input struct (`INetworkInput`) with the controls, and a `PredictedBehaviour<TInput>` on the vehicle's root with a
   deterministic `Simulate` (kinematic: no `NetworkRigidbody`), `WriteState`/`ReadState` for everything `Simulate` keeps
   (speed, gear), `GatherInput` reading the local player's controls, and `GatherServerInput` for an empty seat (brake).
2. For seats and passengers, a `Container` on the vehicle's root.
3. On the worker, when a player's pawn takes the driving seat: `vehicle.SetDriver(pawn.OwnerClientId)`; when it leaves:
   `vehicle.ClearDriver()`. Optionally `OnDriverChanged` for cameras and HUDs, `IsDriver` on the client.
