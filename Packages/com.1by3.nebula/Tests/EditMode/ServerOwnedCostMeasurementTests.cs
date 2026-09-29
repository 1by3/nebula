using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// What a plain server-owned entity costs a worker today, at 100, 300 and 1,000 per worker
    /// (<c>docs/server-owned-entities.md</c> §1). Real <see cref="NebulaWorker"/>s on the in-process mesh
    /// (<see cref="CrowdMesh"/>): the tick's own time and sections, what goes to the gateway and to the neighbouring
    /// worker, and the handovers of a crowd walking across a seam.
    /// <para>
    /// A measurement, not a test of a rule: <c>[Explicit]</c>, so an ordinary run never picks it up. Run it with
    /// <c>-testFilter Nebula.Tests.ServerOwnedCostMeasurementTests</c>; it writes <c>Logs/scale/editor-server-owned.csv</c>.
    /// Times are Editor (Mono, no player optimisations) and machine-dependent: compare rows, not machines.
    /// </para>
    /// </summary>
    [Explicit("measurement: run on purpose, writes Logs/scale/editor-server-owned.csv")]
    [Category("Scale")]
    public sealed class ServerOwnedCostMeasurementTests
    {
        private static readonly List<string> Rows = new List<string>();
        private const int Warmup = 120;
        private const int Measured = 600;

        [OneTimeTearDown]
        public void Write()
        {
            if (Rows.Count == 0) return;
            var lines = new List<string> { CrowdMesh.CsvHeader };
            lines.AddRange(Rows);
            string path = CrowdMesh.ArtifactPath("editor-server-owned.csv");
            File.WriteAllLines(path, lines);
            Debug.Log("[scale:editor] wrote " + path + "\n" + string.Join("\n", lines));
        }

        /// <summary>N entities on one worker, standing still: what Nebula costs per entity when the game does nothing.</summary>
        [Test]
        public void Idle([Values(100, 300, 1000)] int count)
        {
            using var crowd = new CrowdMesh();
            crowd.SpawnWalkers(crowd.W1, count, -240f, -40f, speed: 0f);
            crowd.Run(Warmup);
            var w = crowd.Measure(Measured);
            Rows.Add(CrowdMesh.CsvRow("idle", count, w));
            Debug.Log(CrowdMesh.CsvRow("idle", count, w));
        }

        /// <summary>N entities on one worker, walking at 1.5 m/s away from any seam: the steady cost of moving NPCs.</summary>
        [Test]
        public void Walking([Values(100, 300, 1000)] int count)
        {
            using var crowd = new CrowdMesh();
            crowd.SpawnWalkers(crowd.W1, count, -240f, -40f, speed: 1.5f, boxMin: new Vector2(-250f, -250f), boxMax: new Vector2(-30f, 250f));
            crowd.Run(Warmup);
            var w = crowd.Measure(Measured);
            Rows.Add(CrowdMesh.CsvRow("walking", count, w));
            Debug.Log(CrowdMesh.CsvRow("walking", count, w));
        }

        /// <summary>
        /// 50 entities spawned in one frame: how many messages and bytes the worker hands the gateway for them
        /// (one reliable <c>EntitySpawn</c> each; the transport may still pack several into one datagram).
        /// </summary>
        [Test]
        public void BurstOfFiftySpawns()
        {
            using var crowd = new CrowdMesh();
            crowd.Run(2);
            crowd.SpawnWalkers(crowd.W1, 50, -200f, -40f, speed: 0f);
            crowd.Mesh.Pump();
            int messages = 0; long bytes = 0;
            foreach (var m in crowd.Mesh.Delivered)
                if (m.To == crowd.Gateway.Id && m.Id == MsgId.EntitySpawn) { messages++; bytes += m.Bytes.Length; }
            crowd.Mesh.Delivered.Clear();
            string row = $"editor,spawn-burst,50,1,,,,,,,,,,,,,{messages},,,,{bytes},\"EntitySpawn messages to the gateway: {messages}, {bytes} bytes ({bytes / System.Math.Max(1, messages)} each)\"";
            Rows.Add(row);
            Debug.Log(row);
            Assert.AreEqual(50, messages, "one EntitySpawn per entity, worker to gateway");
        }

        /// <summary>
        /// N entities per worker walking back and forth across the seam at x = 0 (a 60 m strip either side): handover,
        /// the ghost band and both workers' ticks under a crowd that keeps crossing.
        /// </summary>
        [Test]
        public void CrowdAcrossASeam([Values(100, 300, 1000)] int countPerWorker)
        {
            using var crowd = new CrowdMesh();
            var min = new Vector2(-60f, -250f);
            var max = new Vector2(60f, 250f);
            crowd.SpawnWalkers(crowd.W1, countPerWorker, -60f, -1f, speed: 1.5f, boxMin: min, boxMax: max);
            crowd.SpawnWalkers(crowd.W2, countPerWorker, 1f, 60f, speed: 1.5f, boxMin: min, boxMax: max);
            crowd.Run(Warmup);
            var w = crowd.Measure(Measured);
            Rows.Add(CrowdMesh.CsvRow("seam", countPerWorker * 2, w));
            Debug.Log(CrowdMesh.CsvRow("seam", countPerWorker * 2, w) + $" auth w1={crowd.AuthoritativeOn(crowd.W1)} w2={crowd.AuthoritativeOn(crowd.W2)}");
            Assert.AreEqual(countPerWorker * 2, crowd.AuthoritativeOn(crowd.W1) + crowd.AuthoritativeOn(crowd.W2), "every walker has exactly one authority");
        }
    }
}
