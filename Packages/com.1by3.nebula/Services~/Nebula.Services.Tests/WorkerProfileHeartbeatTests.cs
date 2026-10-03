using System.Collections.Generic;
using System.Text;
using Nebula;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// A worker's whole-frame figures and profiler sections (NEB-377) travel on its heartbeat as additive fields: the
/// control plane keeps them on the worker row, the snapshot carries them, /api/state publishes the sections as a map,
/// and a row from an older worker that never sent them reads zero and empty.
/// </summary>
[TestFixture]
public class WorkerProfileHeartbeatTests
{
    [Test]
    public void FrameFiguresAndSectionsLandOnTheWorkerRowAndSurviveTheSnapshot()
    {
        var plane = new LocalControlPlane();
        plane.Connect();
        plane.RegisterWorker("w1", 1, "127.0.0.1", 7100);
        plane.HeartbeatWorker("w1", WorkerStatus.Ready, new WorkerStats
        {
            FrameAvgMs = 16.7f, FrameP90Ms = 17.4f, FrameMaxMs = 41.25f,
            FrameSections = "physics=1.20 fixed=0.10 tick=6.40 update=0.50 late=0.10 idle=8.40",
            ProfileSections = "poll=0.03 simulate=5.40 presence=0.12",
        });
        var row = plane.FindWorker("w1");
        Assert.That(row.FrameP90Ms, Is.EqualTo(17.4f));

        var json = ControlPlaneJson.Write(1, plane.Now, new[] { row }, new LeaseInfo[0], new GatewayInfo[0], new Dictionary<string, string>());
        var back = ControlPlaneJson.Parse(json).Workers[0];
        Assert.That(back.FrameAvgMs, Is.EqualTo(16.7f));
        Assert.That(back.FrameP90Ms, Is.EqualTo(17.4f));
        Assert.That(back.FrameMaxMs, Is.EqualTo(41.25f));
        Assert.That(back.FrameSections, Is.EqualTo(row.FrameSections));
        Assert.That(back.ProfileSections, Is.EqualTo("poll=0.03 simulate=5.40 presence=0.12"));
    }

    [Test]
    public void ARemoteHeartbeatCarriesTheFieldsThroughTheOpWriter()
    {
        var op = new ControlPlaneJson.OpWriter().Op(ControlPlaneJson.HeartbeatWorker).Arg("workerId", "w1").Arg("status", WorkerStatus.Ready)
            .Arg("frameAvgMs", 16.7f).Arg("frameP90Ms", 17.4f).Arg("frameMaxMs", 41.25f)
            .Arg("frameSections", "physics=1.20 idle=8.40").Arg("profileSections", "simulate=5.40").End();
        var plane = new LocalControlPlane();
        plane.Connect();
        plane.RegisterWorker("w1", 1, "127.0.0.1", 7100);
        Assert.That(ControlPlaneJson.ApplyBatch(ControlPlaneJson.WriteBatch(new[] { op }), plane), Is.Null);
        var row = plane.FindWorker("w1");
        Assert.That(row.FrameMaxMs, Is.EqualTo(41.25f));
        Assert.That(row.FrameSections, Is.EqualTo("physics=1.20 idle=8.40"));
        Assert.That(row.ProfileSections, Is.EqualTo("simulate=5.40"));
    }

    [Test]
    public void AnOlderWorkersRowWithoutProfileFieldsReadsZeroAndEmpty()
    {
        var json = "{\"version\":1,\"now\":0,\"workers\":[{\"workerId\":\"w1\",\"workerIndex\":1,\"address\":\"a\",\"port\":1,\"status\":\"ready\",\"lastHeartbeat\":0,\"tickCount\":5}]}";
        var w = ControlPlaneJson.Parse(json).Workers[0];
        Assert.That(w.FrameAvgMs, Is.EqualTo(0f));
        Assert.That(w.FrameSections, Is.EqualTo(""));
        Assert.That(w.ProfileSections, Is.EqualTo(""));
    }

    [Test]
    public void SectionsBecomeAJsonObjectOfNumbers()
    {
        var sb = new StringBuilder();
        var w = new JsonWriter(sb);
        w.BeginObject();
        ControlPlaneJson.SectionsObject(w, "profileSections", "poll=0.03 npc.move=1.50 bad junk=x");
        ControlPlaneJson.SectionsObject(w, "frameSections", "");
        w.EndObject();
        Assert.That(sb.ToString(), Is.EqualTo("{\"profileSections\":{\"poll\":0.03,\"npc.move\":1.5},\"frameSections\":{}}"));
    }
}
