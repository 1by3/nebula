using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 34 (see <c>docs/conformance-suite.md</c>, <c>docs/server-owned-entities.md</c> §3): a crowd
    /// of server-owned entities walking back and forth across a worker seam. Two real workers each lease one side;
    /// every walker is spawned server-driven and carries its own velocity across each handover
    /// (<see cref="NetworkBehaviour.WriteHandoverState"/>). After every tick of both workers, every walker has exactly
    /// one authority, nobody is lost, and the gateway hears each walker from the worker that owns it now, at the
    /// epoch it has now.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceCrowdSeamTests
    {
        private const int PerWorker = 40;
        private const float Speed = 3f;

        [Test]
        public void ACrowdWalkingAcrossASeamKeepsOneAuthorityEachAndLosesNobody()
        {
            using var crowd = new CrowdMesh();
            var min = new Vector2(-24f, -200f);
            var max = new Vector2(24f, 200f);
            crowd.SpawnWalkers(crowd.W1, PerWorker, -20f, -1f, Speed, min, max);
            crowd.SpawnWalkers(crowd.W2, PerWorker, 1f, 20f, Speed, min, max);
            int total = PerWorker * 2;

            // What the gateway was last told about each walker: which worker, at which epoch, and every spawn it got.
            var lastEntry = new Dictionary<ulong, (string From, uint Epoch)>();
            var spawns = new HashSet<(ulong NetId, uint Epoch, string From)>();
            int transfers = 0;

            for (int t = 0; t < 1800; t++)
            {
                crowd.Run(1, keepDelivered: true);
                foreach (var m in crowd.Mesh.Delivered)
                {
                    if (m.Id == MsgId.AuthorityTransfer) transfers++;
                    if (m.To != crowd.Gateway.Id) continue;
                    if (m.Id == MsgId.EntitySpawn)
                    {
                        var spawn = m.Read(r => EntitySpawnMsg.Read(r));
                        spawns.Add((spawn.NetId, spawn.Epoch, m.From));
                    }
                    else if (m.Id == MsgId.WorldState)
                    {
                        var r = new NetworkReader(m.Bytes);
                        r.ReadByte();
                        WorldStateMsg.ReadHeader(r, out _, out _, out ushort count);
                        for (int i = 0; i < count; i++)
                        {
                            var entry = EntityStateEntry.Read(r);
                            lastEntry[entry.NetId] = (m.From, entry.Epoch);
                        }
                    }
                }
                crowd.Mesh.Delivered.Clear();

                int authorities = crowd.AuthoritativeOn(crowd.W1) + crowd.AuthoritativeOn(crowd.W2);
                Assert.AreEqual(total, authorities, $"tick {t}: every walker is authoritative on exactly one worker");
                foreach (var e in crowd.Spawned)
                {
                    int copies = 0;
                    foreach (var w in crowd.Mesh.Workers)
                    {
                        var copy = w.Find(e.NetId);
                        if (copy != null && copy.HasAuthority) copies++;
                    }
                    Assert.AreEqual(1, copies, $"tick {t}: walker {e.NetId} has exactly one authority");
                }
            }

            Assert.Greater(transfers, total, "the crowd crossed the seam many times over");
            foreach (var e in crowd.Spawned)
            {
                ConformanceMesh.Worker owner = null;
                NetworkIdentity authority = null;
                foreach (var w in crowd.Mesh.Workers)
                {
                    var copy = w.Find(e.NetId);
                    if (copy != null && copy.HasAuthority) { owner = w; authority = copy; }
                }
                Assert.NotNull(owner);
                var walker = authority.GetComponent<CrowdMesh.Walker>();
                Assert.AreEqual(Speed, walker.Velocity.magnitude, 1e-3f, $"walker {e.NetId} kept its velocity across every handover");
                Assert.IsTrue(lastEntry.TryGetValue(e.NetId, out var last), $"the gateway heard walker {e.NetId}");
                Assert.AreEqual(owner.Id, last.From, $"the gateway last heard walker {e.NetId} from the worker that owns it");
                Assert.AreEqual(authority.Epoch, last.Epoch, $"at the epoch it has now");
                Assert.IsTrue(spawns.Contains((e.NetId, authority.Epoch, owner.Id)), $"the owner announced walker {e.NetId} to the gateway when it took it");
            }
        }
    }
}
