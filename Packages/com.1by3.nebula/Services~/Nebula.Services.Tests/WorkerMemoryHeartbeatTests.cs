using Nebula;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// A worker's memory figures (NEB-364) travel on its heartbeat as additive fields: the control plane keeps them on
/// the worker row, the snapshot carries them, and a row from an older worker that never sent them reads zero.
/// </summary>
[TestFixture]
public class WorkerMemoryHeartbeatTests
{
    [Test]
    public void MemoryFiguresLandOnTheWorkerRowAndSurviveTheSnapshot()
    {
        var plane = new LocalControlPlane();
        plane.Connect();
        plane.RegisterWorker("w1", 1, "127.0.0.1", 7100);
        plane.HeartbeatWorker("w1", WorkerStatus.Ready, new WorkerStats
        {
            ResidentBytes = 3_000_000_000UL, NativeAllocatedBytes = 400_000_000UL, NativeReservedBytes = 500_000_000UL,
            ManagedBytes = 200_000_000UL, GcCount = 42,
        });
        var row = plane.FindWorker("w1");
        Assert.That(row.ResidentBytes, Is.EqualTo(3_000_000_000UL));
        Assert.That(row.GcCount, Is.EqualTo(42u));

        var json = ControlPlaneJson.Write(1, plane.Now, new[] { row }, new LeaseInfo[0], new GatewayInfo[0], new System.Collections.Generic.Dictionary<string, string>());
        var back = ControlPlaneJson.Parse(json).Workers[0];
        Assert.That(back.ResidentBytes, Is.EqualTo(3_000_000_000UL));
        Assert.That(back.NativeAllocatedBytes, Is.EqualTo(400_000_000UL));
        Assert.That(back.NativeReservedBytes, Is.EqualTo(500_000_000UL));
        Assert.That(back.ManagedBytes, Is.EqualTo(200_000_000UL));
        Assert.That(back.GcCount, Is.EqualTo(42u));
    }

    [Test]
    public void AnOlderWorkersRowWithoutMemoryFieldsReadsZero()
    {
        var json = "{\"version\":1,\"now\":0,\"workers\":[{\"workerId\":\"w1\",\"workerIndex\":1,\"address\":\"a\",\"port\":1,\"status\":\"ready\",\"lastHeartbeat\":0,\"tickCount\":5}]}";
        var w = ControlPlaneJson.Parse(json).Workers[0];
        Assert.That(w.ResidentBytes, Is.EqualTo(0UL));
        Assert.That(w.ManagedBytes, Is.EqualTo(0UL));
    }
}
