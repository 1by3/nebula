using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The gateway half of conformance scenario 39 (NEB-361, docs/driven-vehicles.md D2 and D3): the real gateway forwards
/// <see cref="MsgId.DriveInput"/> for an entity only from the client its owner's spawn names as the driver, stamped with
/// that client's id, to the worker that owns the entity, and drops it from anybody else, for a pawn too; it passes the
/// worker's owner state for the entity to the driver alone; every client that holds the entity is told who drives it,
/// and a spawn naming another driver moves the seat at once. Tier A: a real <see cref="NebulaGateway"/> on a real socket,
/// with a <see cref="FakeWorker"/> and two <see cref="FakeClient"/>s.
/// </summary>
[TestFixture]
[Category("Conformance")]
public class ConformanceDrivenVehicleGatewayTests
{
    private const ulong Car = 5001;
    private string _directory = "";

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-driven-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        var container = new Container
        {
            ContainerId = "c0", Index = 0, Size = new Vector3(512, 512, 512),
            transform = new ContainerFrame { position = new Vector3(256, 0, 256) },
        };
        string path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest { Containers = new List<Container> { container } }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Test]
    public void OnlyTheDriverSteersAndOnlyTheDriverIsSentTheVehiclesState()
    {
        using var fleet = new Fleet(1);
        var worker = fleet.Worker;
        var driver = fleet.Connect(0, "driver");
        var rider = fleet.Connect(0, "rider");
        Assert.That(fleet.Run(() => driver.Join == JoinState.Joined && rider.Join == JoinState.Joined, seconds: 10), Is.True, "both join");
        ulong driverId = driver.Welcome!.Value.ClientId, riderId = rider.Welcome!.Value.ClientId;
        Assert.That(fleet.Run(() => worker.Pawns.ContainsKey(driverId) && worker.Pawns.ContainsKey(riderId), seconds: 10), Is.True);

        // A car next to the driver's pawn, then the worker hands the driver the wheel.
        var car = worker.Spawn(Car, worker.Entities[worker.Pawns[driverId]].Local + new Vector3(3, 0, 0));
        Assert.That(fleet.Run(() => driver.Replicas.Contains(Car) && rider.Replicas.Contains(Car), seconds: 10), Is.True, "both hold the car");
        car.DriverClientId = driverId;
        worker.Reannounce(Car, newEpoch: false);
        Assert.That(fleet.Run(() => driver.DriverOf.TryGetValue(Car, out var d) && d == driverId && rider.DriverOf.TryGetValue(Car, out var r) && r == driverId, seconds: 10),
            Is.True, "every client that holds it is told who drives it");

        // The driver's input is forwarded, stamped with the driver's id; the passenger's is not.
        driver.SendDriveInput(Car, 10);
        rider.SendDriveInput(Car, 11);
        // Nor is a DriveInput for somebody's pawn: a pawn is steered by its owner's own input only.
        driver.SendDriveInput(worker.Pawns[riderId], 12);
        Assert.That(fleet.Run(() => worker.DriveInputs.Count > 0, seconds: 5), Is.True, "the driver's input reached the worker");
        fleet.RunFor(0.3);
        Assert.AreEqual(1, worker.DriveInputs.Count, "only the driver's input for the car was forwarded");
        Assert.AreEqual(driverId, worker.DriveInputs[0].ClientId, "stamped by the gateway with the driver's id");
        Assert.AreEqual(Car, worker.DriveInputs[0].NetId);
        Assert.AreEqual(10u, worker.DriveInputs[0].Frames[0].Tick);

        // The worker's owner state for the car goes to the driver only.
        worker.SendOwnerState(Car, driverId, 20);
        Assert.That(fleet.Run(() => driver.OwnerStates.Count > 0, seconds: 5), Is.True, "the driver is sent the car's state");
        fleet.RunFor(0.2);
        Assert.AreEqual(Car, driver.OwnerStates[0].NetId);
        Assert.AreEqual(20u, driver.OwnerStates[0].Tick);
        Assert.AreEqual(0, rider.OwnerStates.Count, "nobody else is");

        // The seat changes hands: from the next spawn, only the new driver steers.
        car.DriverClientId = riderId;
        worker.Reannounce(Car, newEpoch: false);
        Assert.That(fleet.Run(() => driver.DriverOf[Car] == riderId && rider.DriverOf[Car] == riderId, seconds: 10), Is.True);
        worker.DriveInputs.Clear();
        driver.SendDriveInput(Car, 30);
        rider.SendDriveInput(Car, 31);
        Assert.That(fleet.Run(() => worker.DriveInputs.Count > 0, seconds: 5), Is.True);
        fleet.RunFor(0.3);
        Assert.AreEqual(1, worker.DriveInputs.Count, "only the new driver's input");
        Assert.AreEqual(riderId, worker.DriveInputs[0].ClientId);
        Assert.AreEqual(31u, worker.DriveInputs[0].Frames[0].Tick);

        // Nobody drives it: nothing is forwarded.
        car.DriverClientId = 0;
        worker.Reannounce(Car, newEpoch: false);
        Assert.That(fleet.Run(() => rider.DriverOf[Car] == 0, seconds: 10), Is.True);
        worker.DriveInputs.Clear();
        rider.SendDriveInput(Car, 40);
        fleet.RunFor(0.3);
        Assert.AreEqual(0, worker.DriveInputs.Count, "the former driver's input is dropped");
    }
}
