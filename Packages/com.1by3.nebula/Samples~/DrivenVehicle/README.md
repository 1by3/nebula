# Driven vehicle (sample)

A car that players drive, with seats for passengers. The driver's client predicts the car, so it responds to the
controls on the next frame at any latency, and the worker stays its authority. It is a sample, not part of Nebula: the
vehicle model and how a player gets into a seat are up to your game.

- `Vehicle` is the car: a `PredictedBehaviour<VehicleInput>` with a deterministic, kinematic model. `GatherInput`
  returns `Controls`, which your input code sets on the client every frame. `GatherServerInput` brakes when nobody
  drives. `WriteState` and `ReadState` carry the speed along with the pose.
- `DriverSeat` runs on the worker. `TrySit(pawn)` hands the controls to the pawn's player with
  `NetworkIdentity.SetDriver`, and `Leave()` takes them back with `ClearDriver`. It frees the seat by itself when the
  driver's pawn is gone from the car's box.

## Using it

Make a prefab with a `NetworkIdentity`, a `Container` sized to the cabin (the box holds the seats), `Vehicle`
(it adds the `NetworkTransform`) and `DriverSeat` on the same root object, and register it. On a worker:

```csharp
var car = worker.SpawnServerDriven(carPrefab, groundContainer);
// When a player's pawn gets into the driving seat (your interaction code, on the worker):
car.GetComponent<DriverSeat>().TrySit(pawn);
// When it gets out:
car.GetComponent<DriverSeat>().Leave();
```

Keep the pawns of the driver and the passengers inside the car's box, at their seats, in your pawn controller. They
then ride with the car on every client and change worker with it.

On the client, feed the controls while this client drives:

```csharp
foreach (var driven in client.DrivenEntities)
    if (driven.TryGetComponent<Vehicle>(out var vehicle))
        vehicle.Controls = new VehicleInput { Throttle = throttle, Steer = steer };
```

To free the seat when a player disconnects, rather than when the session ends, call `Leave()` from your game mode's
`OnPlayerDisconnected`.

Nebula's conformance scenario 39 (`ConformanceDrivenVehicleTests`) drives the same kinematic model across a worker
seam and in a flying ship's hold. The measurements behind driver prediction are in `docs/driven-vehicles.md`.

Guide: https://nebula.1by3.co/docs/guides/prediction#drive-a-vehicle
