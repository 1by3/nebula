using System;
using NUnit.Framework;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula.Tests
{
    /// <summary>The wire and storage format shared by the hosted control plane, its mirrors and the persistence host.</summary>
    public class ControlPlaneJsonTests
    {
        [Test]
        public void SnapshotSurvivesARoundTrip()
        {
            var cp = new LocalControlPlane();
            cp.Connect();
            cp.RegisterWorker("w1", 1, "10.0.0.5", 7101);
            cp.HeartbeatWorker("w1", WorkerStatus.Ready, new WorkerStats { TickCount = 123456789012UL, TickMs = 2.75f, EntityCount = 7, AuthoritativeCount = 5, GhostCount = 2, PlayerCount = 1, BotCount = 1, ServerDrivenCount = 3 });
            cp.RegisterGateway("gw1", "203.0.113.9", 7000);
            cp.EnsureContainer("cell-1");
            cp.AssignContainer("cell-1", "w1");
            cp.EnsureRuntimeContainer("rt_5", new Bounds(new Vector3(100.5f, -3.25f, 0.125f), new Vector3(8, 8, 8)), "w1");
            cp.PinContainer("rt_5", "w1");
            cp.SetSetting("npcs", "12");
            cp.SetSetting("motd", "quote \"this\" \\ and \n newline");
            long version = cp.Version;

            string json = cp.ToJson();
            var s = ControlPlaneJson.Parse(json);
            Assert.AreEqual(version, s.Version);
            Assert.AreEqual(1, s.Workers.Count);
            var w = s.Workers[0];
            Assert.AreEqual("w1", w.WorkerId);
            Assert.AreEqual(1u, w.WorkerIndex);
            Assert.AreEqual("10.0.0.5", w.Address);
            Assert.AreEqual(7101, w.Port);
            Assert.AreEqual(WorkerStatus.Ready, w.Status);
            Assert.AreEqual(123456789012UL, w.TickCount);
            Assert.AreEqual(2.75f, w.TickMs);
            Assert.AreEqual(7u, w.EntityCount);
            Assert.AreEqual(3u, w.ServerDrivenCount);
            Assert.Less(Math.Abs((w.LastHeartbeat - cp.FindWorker("w1").LastHeartbeat).TotalMilliseconds), 1.0);
            Assert.AreEqual(1, s.Gateways.Count);
            Assert.AreEqual(7000, s.Gateways[0].Port);
            Assert.AreEqual(2, s.Leases.Count);
            var cell = s.Leases.Find(l => l.ContainerId == "cell-1");
            Assert.AreEqual("w1", cell.WorkerId);
            Assert.AreEqual(1UL, cell.Epoch);
            Assert.AreEqual(LeaseState.Active, cell.State);
            Assert.IsFalse(cell.HasBounds);
            var rt = s.Leases.Find(l => l.ContainerId == "rt_5");
            Assert.IsTrue(rt.HasBounds);
            Assert.AreEqual(100.5f, rt.BoundsCenter.x);
            Assert.AreEqual(-3.25f, rt.BoundsCenter.y);
            Assert.AreEqual(0.125f, rt.BoundsCenter.z);
            Assert.AreEqual(8f, rt.BoundsSize.z);
            Assert.AreEqual(LeaseState.Pinned, rt.State);
            Assert.AreEqual(2UL, rt.Epoch);
            Assert.AreEqual("12", s.Settings["npcs"]);
            Assert.AreEqual("quote \"this\" \\ and \n newline", s.Settings["motd"]);

            // A second plane imports the document and produces the same one.
            var again = new LocalControlPlane();
            again.Import(s);
            var s2 = ControlPlaneJson.Parse(again.ToJson());
            Assert.AreEqual(s.Leases.Count, s2.Leases.Count);
            Assert.AreEqual(s.Workers[0].TickCount, s2.Workers[0].TickCount);
            Assert.GreaterOrEqual(again.Version, version);
        }

        [Test]
        public void WritesApplyInOrderAndRejectUnknownOps()
        {
            var cp = new LocalControlPlane();
            cp.Connect();
            var op = new ControlPlaneJson.OpWriter();
            var ops = new[]
            {
                op.Op(ControlPlaneJson.RegisterWorker).Arg("workerId", "w1").Arg("workerIndex", 1u).Arg("address", "127.0.0.1").Arg("port", 7101L).End(),
                op.Op(ControlPlaneJson.EnsureContainer).Arg("containerId", "c1").End(),
                op.Op(ControlPlaneJson.AssignContainer).Arg("containerId", "c1").Arg("workerId", "w1").End(),
                op.Op(ControlPlaneJson.EnsureRuntimeContainer).Arg("containerId", "rt_1").Arg("workerId", "").Arg("center", new Vector3(1, 2, 3)).Arg("size", new Vector3(4, 5, 6)).End(),
                op.Op(ControlPlaneJson.SetSetting).Arg("key", "k").Arg("value", "v").End(),
            };
            Assert.IsNull(ControlPlaneJson.ApplyBatch(ControlPlaneJson.WriteBatch(ops), cp));
            Assert.AreEqual("w1", cp.FindLease("c1").WorkerId);
            Assert.AreEqual(1UL, cp.FindLease("c1").Epoch);
            Assert.AreEqual(LeaseState.Orphaned, cp.FindLease("rt_1").State);
            Assert.AreEqual(5f, cp.FindLease("rt_1").BoundsSize.y);
            Assert.AreEqual("v", cp.Settings["k"]);

            long before = cp.Version;
            Assert.IsNotNull(ControlPlaneJson.ApplyBatch("{\"ops\":[{\"op\":\"Explode\"}]}", cp));
            Assert.IsNotNull(ControlPlaneJson.ApplyBatch("{\"ops\":[{\"op\":\"SetSetting\",\"key\":\"a\",\"value\":\"b\"},{\"nope\":1}]}", cp));
            Assert.AreEqual(before, cp.Version, "a rejected batch applies nothing");
            Assert.IsNotNull(ControlPlaneJson.ApplyBatch("not json", cp));
        }

        [Test]
        public void PersistedRecordSurvivesARoundTrip()
        {
            var r = new PersistedEntityRecord
            {
                Key = "crate#7", PrefabId = 3, PrefabName = "Crate", SceneId = 0, ContainerId = "cell-1", CarrierKey = "",
                LocalPosition = new Vector3(1.5f, -2.25f, 3.125f), LocalRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), Velocity = new Vector3(0, -9.81f, 0),
                Epoch = 4, ServerDriven = true, Owned = false, Name = "crate", State = new byte[] { 0, 1, 2, 255, 128 }, Version = 9, SavedAt = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc), SavedBy = "w2",
            };
            string json = PersistedRecordJson.WriteList(new[] { r });
            var list = PersistedRecordJson.ParseList(json);
            Assert.AreEqual(1, list.Count);
            var b = list[0];
            Assert.AreEqual(r.Key, b.Key);
            Assert.AreEqual(r.PrefabId, b.PrefabId);
            Assert.AreEqual(r.LocalPosition.z, b.LocalPosition.z);
            Assert.AreEqual(r.LocalRotation.w, b.LocalRotation.w);
            Assert.AreEqual(r.Velocity.y, b.Velocity.y);
            Assert.AreEqual(r.Epoch, b.Epoch);
            Assert.AreEqual(r.ServerDriven, b.ServerDriven);
            Assert.AreEqual(r.State, b.State);
            Assert.AreEqual(r.Version, b.Version);
            Assert.AreEqual(r.SavedAt, b.SavedAt);
            Assert.AreEqual(r.SavedBy, b.SavedBy);

            Assert.IsNull(PersistedRecordJson.ParseOne(PersistedRecordJson.WriteOne(null)));
            var empty = new PersistedEntityRecord { Key = "e" };
            Assert.AreEqual(0, PersistedRecordJson.ParseOne(PersistedRecordJson.WriteOne(empty)).State.Length);
            Assert.Throws<FormatException>(() => PersistedRecordJson.ParseList("nope"));
        }

        [Test]
        public void DatabaseUrlNamesTheEngine()
        {
            Assert.AreEqual("file", DatabaseUrl.Parse("", "file:C:/data").Scheme);
            Assert.AreEqual("C:/data", DatabaseUrl.Parse("", "file:C:/data").Target);
            Assert.AreEqual("memory", DatabaseUrl.Parse("memory", "file:x").Scheme);
            Assert.AreEqual("sqlite", DatabaseUrl.Parse("sqlite:world.db", "").Scheme);
            Assert.AreEqual("postgres://u:***@h/d", DatabaseUrl.Parse("postgres://u:pw@h/d", "").Display);
            Assert.Throws<ArgumentException>(() => DatabaseUrl.Parse("redis://x", ""));
        }
    }
}
