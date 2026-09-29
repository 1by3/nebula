using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Relevance tiers on the worker (<c>docs/server-owned-entities.md</c> D1–D3), on two real workers
    /// (<see cref="CrowdMesh"/>): an entity with an <see cref="NetworkIdentity.UpdateInterval"/> is ticked, checked
    /// and published only on its update ticks, with the time between them, spread over the interval by net id; a
    /// teleport still goes at once; a moving root sends no recovery entry until it settles; a light crowd crosses a
    /// seam keeping one authority each and its interval, with no ghost flapping; and the priority is announced with
    /// the spawn, again when it changes, and travels with a handover.
    /// </summary>
    public sealed class RelevanceTierWorkerTests
    {
        private CrowdMesh _crowd;

        [TearDown]
        public void TearDown() => _crowd?.Dispose();

        /// <summary>What the gateway was sent in the last delivery: entries per net id, reliable ones, spawns.</summary>
        private sealed class Heard
        {
            public readonly Dictionary<ulong, int> Entries = new Dictionary<ulong, int>();
            public int Reliable, Total, Spawns;
            public readonly List<EntitySpawnMsg> SpawnMessages = new List<EntitySpawnMsg>();
            public readonly List<EntityStateEntry> States = new List<EntityStateEntry>();
        }

        private Heard Listen()
        {
            var heard = new Heard();
            foreach (var m in _crowd.Mesh.Delivered)
            {
                if (m.To != _crowd.Gateway.Id) continue;
                if (m.Id == MsgId.EntitySpawn) { heard.Spawns++; heard.SpawnMessages.Add(m.Read(r => EntitySpawnMsg.Read(r))); continue; }
                if (m.Id != MsgId.WorldState) continue;
                var reader = new NetworkReader(m.Bytes);
                reader.ReadByte();
                WorldStateMsg.ReadHeader(reader, out _, out _, out ushort count);
                for (int i = 0; i < count; i++)
                {
                    var entry = EntityStateEntry.Read(reader);
                    heard.Entries.TryGetValue(entry.NetId, out int n);
                    heard.Entries[entry.NetId] = n + 1;
                    heard.Total++;
                    heard.States.Add(entry);
                    if (entry.Reliable) heard.Reliable++;
                }
            }
            _crowd.Mesh.Delivered.Clear();
            return heard;
        }

        private static void WalkNorth(NetworkIdentity e, float speed)
        {
            var walker = e.GetComponent<CrowdMesh.Walker>();
            walker.Velocity = new Vector3(0f, 0f, speed);
            e.Motion.Velocity = walker.Velocity;
        }

        [Test]
        public void AnEntityWithAnUpdateIntervalIsUpdatedAndPublishedEveryNthTickWithTheTimeBetween()
        {
            _crowd = new CrowdMesh();
            var light = _crowd.SpawnWalkers(_crowd.W1, 30, -200f, -100f, 0f);
            var full = _crowd.SpawnWalkers(_crowd.W1, 30, -200f, -100f, 0f);
            foreach (var e in light) { e.UpdateInterval = 6; e.transform.position = new Vector3(e.transform.position.x, 0, -100f); WalkNorth(e, 1.5f); }
            foreach (var e in full) { e.transform.position = new Vector3(e.transform.position.x, 0, -100f); WalkNorth(e, 1.5f); }
            _crowd.Run(12);
            _crowd.Mesh.Delivered.Clear();
            var ticksBefore = new Dictionary<NetworkIdentity, int>();
            var zBefore = new Dictionary<NetworkIdentity, float>();
            foreach (var e in _crowd.Spawned) { ticksBefore[e] = e.GetComponent<CrowdMesh.Walker>().Ticks; zBefore[e] = e.transform.position.z; }

            var perTick = new List<int>();
            var lightIds = new HashSet<ulong>();
            foreach (var e in light) lightIds.Add(e.NetId);
            int reliable = 0;
            var entries = new Dictionary<ulong, int>();
            for (int t = 0; t < 600; t++)
            {
                _crowd.Run(1, keepDelivered: true);
                var heard = Listen();
                reliable += heard.Reliable;
                int lightThisTick = 0;
                foreach (var kv in heard.Entries)
                {
                    entries.TryGetValue(kv.Key, out int n);
                    entries[kv.Key] = n + kv.Value;
                    if (lightIds.Contains(kv.Key)) lightThisTick += kv.Value;
                }
                perTick.Add(lightThisTick);
            }

            foreach (var e in light)
            {
                Assert.AreEqual(100, e.GetComponent<CrowdMesh.Walker>().Ticks - ticksBefore[e], $"{e.NetId}: NetworkTick every 6th tick");
                Assert.AreEqual(100, entries[e.NetId], $"{e.NetId}: one entry per update");
                Assert.AreEqual(15f, e.transform.position.z - zBefore[e], 0.2f, $"{e.NetId}: as far in 10 s as an every-tick walker (deltaTime is the time since the last update)");
            }
            foreach (var e in full)
            {
                Assert.AreEqual(600, e.GetComponent<CrowdMesh.Walker>().Ticks - ticksBefore[e]);
                Assert.AreEqual(600, entries[e.NetId]);
                Assert.AreEqual(15f, e.transform.position.z - zBefore[e], 0.05f);
            }
            int max = 0;
            foreach (int n in perTick) if (n > max) max = n;
            Assert.LessOrEqual(max, 12, "30 light walkers are spread over the 6 ticks of their interval, not sent on one");
            Assert.AreEqual(0, reliable, "a root that keeps moving sends no recovery entry");
        }

        [Test]
        public void AMovingRootSendsOneRecoveryEntryOnlyOnceItHasSettled()
        {
            _crowd = new CrowdMesh();
            var e = _crowd.SpawnWalkers(_crowd.W1, 1, -120f, -110f, 0f)[0];
            WalkNorth(e, 2f);
            _crowd.Run(5);
            int reliable = 0;
            for (int t = 0; t < 300; t++) { _crowd.Run(1, keepDelivered: true); reliable += Listen().Reliable; }
            Assert.AreEqual(0, reliable, "five seconds of walking: no reliable entry");

            WalkNorth(e, 0f);
            var reliableAt = new List<int>();
            for (int t = 0; t < 200; t++)
            {
                _crowd.Run(1, keepDelivered: true);
                var heard = Listen();
                if (heard.Reliable > 0) reliableAt.Add(t);
            }
            Assert.AreEqual(1, reliableAt.Count, "one reliable entry once it came to rest");
            Assert.That(reliableAt[0], Is.InRange((int)NetworkTransform.SettleTicks - 1, (int)NetworkTransform.SettleTicks + 2), "SettleTicks after its last change");
        }

        [Test]
        public void ATeleportOfALightEntityGoesOutOnTheNextTick()
        {
            _crowd = new CrowdMesh();
            var e = _crowd.SpawnWalkers(_crowd.W1, 1, -120f, -110f, 0f)[0];
            e.UpdateInterval = 60;
            _crowd.Run(90);
            _crowd.Mesh.Delivered.Clear();
            // Wait for a tick that is not one of its update ticks.
            while (e.IsDueAt(_crowd.CurrentTick + 1)) _crowd.Run(1);
            _crowd.Mesh.Delivered.Clear();
            e.RootTransform.Teleport(new Vector3(-150f, 0f, 40f), Quaternion.identity, Vector3.one);
            _crowd.Run(1, keepDelivered: true);
            var heard = Listen();
            Assert.AreEqual(1, heard.States.Count, "the teleport went out on a tick that was not an update tick");
            Assert.IsTrue((heard.States[0].Fields & TransformFields.Teleport) != 0);
            Assert.IsTrue(heard.States[0].Reliable);
        }

        [Test]
        public void ALightCrowdCrossesASeamWithOneAuthorityEachKeepingItsIntervalAndItsGhosts()
        {
            _crowd = new CrowdMesh();
            var min = new Vector2(-24f, -200f);
            var max = new Vector2(24f, 200f);
            _crowd.SpawnWalkers(_crowd.W1, 30, -20f, -1f, 3f, min, max);
            _crowd.SpawnWalkers(_crowd.W2, 30, 1f, 20f, 3f, min, max);
            foreach (var e in _crowd.Spawned) e.UpdateInterval = 12;
            int total = _crowd.Spawned.Count;
            int transfers = 0, flaps = 0;
            var despawnedAt = new Dictionary<(ulong, string), int>();
            for (int t = 0; t < 1800; t++)
            {
                _crowd.Run(1, keepDelivered: true);
                foreach (var m in _crowd.Mesh.Delivered)
                {
                    if (m.Id == MsgId.AuthorityTransfer) transfers++;
                    else if (m.Id == MsgId.GhostDespawn) despawnedAt[(m.Read(r => EntityDespawnMsg.Read(r)).NetId, m.To)] = t;
                    else if (m.Id == MsgId.GhostSpawn)
                    {
                        ulong id = m.Read(r => EntitySpawnMsg.Read(r)).NetId;
                        if (despawnedAt.TryGetValue((id, m.To), out int at) && t - at < 30) flaps++;
                    }
                }
                _crowd.Mesh.Delivered.Clear();
                Assert.AreEqual(total, _crowd.AuthoritativeOn(_crowd.W1) + _crowd.AuthoritativeOn(_crowd.W2), $"tick {t}: one authority each");
            }
            Assert.Greater(transfers, total / 2, "the light crowd crossed the seam");
            Assert.AreEqual(0, flaps, "no ghost was dropped and sent again within half a second: its linger covers its interval");
            foreach (var w in _crowd.Mesh.Workers)
                foreach (var e in w.Instance.Entities)
                    if (e != null && e.HasAuthority) Assert.AreEqual(12, e.UpdateInterval, $"{e.NetId} kept its interval across its handovers");
        }

        [Test]
        public void AGhostOfALightEntityLingersForItsIntervalOnTopOfTheBandsLinger()
        {
            // The band refreshes a ghost's timestamp only on the entity's update ticks, so the linger it is judged by
            // is lengthened by the interval (the Editor's clock does not advance inside one test, so this is the
            // arithmetic rather than the crowd above).
            var go = new GameObject("light");
            try
            {
                var e = go.AddComponent<NetworkIdentity>();
                Assert.AreEqual(0f, NebulaWorker.UpdateSpanSeconds(e), "an every-tick entity adds nothing");
                e.UpdateInterval = 241;
                Assert.AreEqual(4f, NebulaWorker.UpdateSpanSeconds(e), 1e-4f);
                const float linger = 2f;
                Assert.IsFalse(NebulaWorker.GhostTargetExpired(true, true, 103.9f, 100f, linger + NebulaWorker.UpdateSpanSeconds(e)),
                    "3.9 s since its last check: inside 2 s of linger plus its 4 s interval");
                Assert.IsTrue(NebulaWorker.GhostTargetExpired(true, true, 106.1f, 100f, linger + NebulaWorker.UpdateSpanSeconds(e)));
                Assert.AreEqual(0f, NebulaWorker.UpdateSpanSeconds(null));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TheIntervalIsClampedAndARateIsRoundedToWholeTicks()
        {
            var go = new GameObject("rate");
            try
            {
                var e = go.AddComponent<NetworkIdentity>();
                Assert.AreEqual(1, e.UpdateInterval, "every tick by default");
                e.UpdateInterval = 0; Assert.AreEqual(1, e.UpdateInterval);
                e.UpdateInterval = 1000; Assert.AreEqual(NetworkIdentity.MaxUpdateInterval, e.UpdateInterval);
                e.SetUpdateRate(10f); Assert.AreEqual(6, e.UpdateInterval);
                e.SetUpdateRate(0.5f); Assert.AreEqual(120, e.UpdateInterval);
                e.SetUpdateRate(60f); Assert.AreEqual(1, e.UpdateInterval);
                e.SetUpdateRate(1000f); Assert.AreEqual(1, e.UpdateInterval);
                e.SetUpdateRate(0f); Assert.AreEqual(NetworkIdentity.MaxUpdateInterval, e.UpdateInterval, "zero is as slow as it goes");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ThePriorityIsAnnouncedWithTheSpawnAgainWhenItChangesAndTravelsWithAHandover()
        {
            _crowd = new CrowdMesh(configurePrefab: prefab => prefab.RelevancePriority = RelevancePriority.Background);
            var e = _crowd.SpawnWalkers(_crowd.W1, 1, -6f, -5f, 0f)[0];
            Assert.AreEqual(RelevancePriority.Background, e.RelevancePriority, "from the prefab");
            _crowd.Run(1, keepDelivered: true);
            var spawn = Listen().SpawnMessages.Find(s => s.NetId == e.NetId);
            Assert.AreEqual(RelevancePriority.Background, RelevanceTiers.PriorityOf(spawn.InterestFlags), "announced with the spawn");

            e.RelevancePriority = RelevancePriority.High;
            _crowd.Run(1, keepDelivered: true);
            var again = Listen().SpawnMessages.FindAll(s => s.NetId == e.NetId);
            Assert.AreEqual(1, again.Count, "a change re-announces the entity once");
            Assert.AreEqual(RelevancePriority.High, RelevanceTiers.PriorityOf(again[0].InterestFlags));
            _crowd.Run(1, keepDelivered: true);
            Assert.AreEqual(0, Listen().SpawnMessages.FindAll(s => s.NetId == e.NetId).Count, "and only once");

            // Walk it over the seam: the next authority has the priority and announces the entity with it.
            e.GetComponent<CrowdMesh.Walker>().Velocity = new Vector3(3f, 0f, 0f);
            NetworkIdentity next = null;
            bool announcedHigh = false;
            for (int t = 0; t < 240 && next == null; t++)
            {
                _crowd.Run(1, keepDelivered: true);
                foreach (var m in _crowd.Mesh.Delivered)
                    if (m.From == _crowd.W2.Id && m.To == _crowd.Gateway.Id && m.Id == MsgId.EntitySpawn)
                    {
                        var s = m.Read(r => EntitySpawnMsg.Read(r));
                        if (s.NetId == e.NetId && RelevanceTiers.PriorityOf(s.InterestFlags) == RelevancePriority.High) announcedHigh = true;
                    }
                _crowd.Mesh.Delivered.Clear();
                var copy = _crowd.W2.Find(e.NetId);
                if (copy != null && copy.HasAuthority) next = copy;
            }
            Assert.IsNotNull(next, "handed over to the east worker");
            Assert.AreEqual(RelevancePriority.High, next.RelevancePriority, "the priority travelled with the handover");
            Assert.IsTrue(announcedHigh, "and the new authority announced the entity to the gateway with it");
        }
    }
}
