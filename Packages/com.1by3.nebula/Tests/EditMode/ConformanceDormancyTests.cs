using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 36 (see <c>docs/conformance-suite.md</c>, <c>docs/server-owned-entities.md</c> §6): dormancy,
    /// on two real workers (<see cref="CrowdMesh"/>). A server-owned entity put to sleep is sent once where it rests,
    /// then not ticked and not sent while it keeps its authority, its state and the gateway's copy; it wakes by a call,
    /// or by itself when a gateway watches it if it was put to sleep that way; one that nobody watches falls asleep by
    /// itself with <see cref="NetworkIdentity.SleepWhenUnobserved"/>; a sleeping entity follows its container's lease
    /// to another worker and stays asleep there; a client's entity cannot sleep.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceDormancyTests
    {
        private CrowdMesh _crowd;

        [TearDown]
        public void TearDown() => _crowd?.Dispose();

        /// <summary>The world-state entries the gateway was sent for <paramref name="netId"/> in what was delivered, and clear it.</summary>
        private List<EntityStateEntry> EntriesFor(ulong netId, List<EntitySpawnMsg> spawns = null)
        {
            var entries = new List<EntityStateEntry>();
            foreach (var m in _crowd.Mesh.Delivered)
            {
                if (m.To != _crowd.Gateway.Id) continue;
                if (m.Id == MsgId.EntitySpawn && spawns != null)
                {
                    var spawn = m.Read(r => EntitySpawnMsg.Read(r));
                    if (spawn.NetId == netId) spawns.Add(spawn);
                    continue;
                }
                if (m.Id != MsgId.WorldState) continue;
                var reader = new NetworkReader(m.Bytes);
                reader.ReadByte();
                WorldStateMsg.ReadHeader(reader, out _, out _, out ushort count);
                for (int i = 0; i < count; i++)
                {
                    var entry = EntityStateEntry.Read(reader);
                    if (entry.NetId == netId) entries.Add(entry);
                }
            }
            _crowd.Mesh.Delivered.Clear();
            return entries;
        }

        private List<EntityStateEntry> RunAndListen(ulong netId, int ticks, List<EntitySpawnMsg> spawns = null)
        {
            var all = new List<EntityStateEntry>();
            for (int t = 0; t < ticks; t++)
            {
                _crowd.Run(1, keepDelivered: true);
                all.AddRange(EntriesFor(netId, spawns));
            }
            return all;
        }

        private static void Walk(NetworkIdentity e, Vector3 velocity)
        {
            e.GetComponent<CrowdMesh.Walker>().Velocity = velocity;
            e.Motion.Velocity = velocity;
        }

        [Test]
        public void ASleepingEntityIsSentOnceWhereItRestsThenNeitherTickedNorSentAndWakesWhereItSlept()
        {
            _crowd = new CrowdMesh();
            var e = _crowd.SpawnWalkers(_crowd.W1, 1, -120f, -110f, 0f)[0];
            var walker = e.GetComponent<CrowdMesh.Walker>();
            Walk(e, new Vector3(0f, 0f, 2f));
            _crowd.Run(20);
            _crowd.Mesh.Delivered.Clear();

            e.Sleep(wakeOnInterest: false);
            var settling = RunAndListen(e.NetId, 1);
            Assert.IsTrue(e.IsDormant);
            Assert.AreEqual(1, walker.Slept, "OnSleep was called once");
            Assert.AreEqual(1, settling.Count, "one entry on the tick it fell asleep");
            Assert.IsTrue(settling[0].Reliable, "reliable: every client holds it where it rests");
            Assert.AreEqual(Vector3.zero, settling[0].Velocity, "with no velocity");
            Assert.AreEqual(1, _crowd.W1.Instance.DormantCount);

            int ticks = walker.Ticks;
            var position = e.transform.position;
            var quiet = RunAndListen(e.NetId, 300);
            Assert.AreEqual(ticks, walker.Ticks, "not ticked while asleep");
            Assert.AreEqual(0, quiet.Count, "not sent while asleep, although the gateway watches its region");
            Assert.AreEqual(position, e.transform.position);
            Assert.IsTrue(e.HasAuthority, "it keeps its authority");
            Assert.AreEqual(2f, walker.Velocity.magnitude, 1e-4f, "and its state");
            Assert.IsTrue(e.IsDormant, "put to sleep without waking on interest: a watching gateway does not wake it");

            e.Wake();
            var awake = RunAndListen(e.NetId, 60);
            Assert.IsFalse(e.IsDormant);
            Assert.AreEqual(1, walker.Woke);
            Assert.Greater(walker.Ticks, ticks, "ticked again");
            Assert.That(awake.Count, Is.InRange(58, 60), "and sent again");
            Assert.AreEqual(position.z + 2f, e.transform.position.z, 0.1f, "from where it slept");
            Assert.AreEqual(0, _crowd.W1.Instance.DormantCount);
        }

        [Test]
        public void AnEntityNobodyWatchesFallsAsleepAndWakesWhenAGatewayWatchesItsRegion()
        {
            _crowd = new CrowdMesh(subscribeEverything: false);
            var e = _crowd.SpawnWalkers(_crowd.W1, 1, -120f, -110f, 0f)[0];
            e.SleepWhenUnobserved = 1f;
            var walker = e.GetComponent<CrowdMesh.Walker>();
            Walk(e, new Vector3(0f, 0f, 1f));

            _crowd.Run(50);
            Assert.IsFalse(e.IsDormant, "unwatched for less than a second: awake");
            _crowd.Run(30);
            Assert.IsTrue(e.IsDormant, "unwatched for a second: asleep");
            Assert.IsTrue(e.WakesOnInterest);
            Assert.AreEqual(1, walker.Slept);
            int ticks = walker.Ticks;
            _crowd.Run(120);
            Assert.AreEqual(ticks, walker.Ticks, "asleep");

            // A client comes near: its gateway subscribes the regions around the entity.
            _crowd.Mesh.Delivered.Clear();
            _crowd.SubscribeAround(e.transform.position, 40f);
            var spawns = new List<EntitySpawnMsg>();
            int woke = -1;
            for (int t = 0; t < 30 && woke < 0; t++)
            {
                _crowd.Run(1, keepDelivered: true);
                EntriesFor(e.NetId, spawns);
                if (!e.IsDormant) woke = t;
            }
            Assert.That(spawns.Count, Is.GreaterThanOrEqualTo(1), "the gateway was sent it as it subscribed, asleep or not");
            Assert.That(woke, Is.InRange(0, 16), "and it woke within one interest evaluation");
            Assert.AreEqual(1, walker.Woke);
            var entries = RunAndListen(e.NetId, 60);
            Assert.That(entries.Count, Is.InRange(58, 60), "sent every tick again");

            // Watched, it stays awake however long.
            _crowd.Run(180);
            Assert.IsFalse(e.IsDormant);
            // Nobody watches again: asleep a second later.
            _crowd.SubscribeAround(Vector3.zero, 0f);
            _crowd.Run(75);
            Assert.IsTrue(e.IsDormant);
            Assert.AreEqual(2, walker.Slept);
        }

        [Test]
        public void ASleepingEntityFollowsItsContainersLeaseToAnotherWorkerAndStaysAsleep()
        {
            _crowd = new CrowdMesh();
            var e = _crowd.SpawnWalkers(_crowd.W1, 1, -120f, -110f, 0f)[0];
            e.SleepWhenUnobserved = 30f;
            Walk(e, new Vector3(0f, 0f, 1f));
            _crowd.Run(10);
            e.Sleep(wakeOnInterest: false);
            _crowd.Run(2);
            Assume.That(e.IsDormant);

            // The west container is dealt to the other worker (a rebalance, or this worker draining).
            _crowd.Mesh.SetOwner(_crowd.West, _crowd.W2);
            _crowd.Run(2);
            var there = _crowd.W2.Find(e.NetId);
            Assert.IsNotNull(there);
            Assert.IsTrue(there.HasAuthority, "handed over although it sleeps");
            Assert.IsTrue(there.IsDormant, "and asleep on the new worker");
            Assert.IsFalse(there.WakesOnInterest, "put to sleep without waking on interest, still");
            Assert.AreEqual(30f, there.SleepWhenUnobserved, "its setting came with it");
            Assert.IsFalse(e.HasAuthority);
            Assert.IsFalse(e.IsDormant, "the copy left behind is a ghost, and a ghost is not asleep");

            var walker = there.GetComponent<CrowdMesh.Walker>();
            int ticks = walker.Ticks;
            Assert.AreEqual(0, walker.Slept, "no OnSleep on arrival: it was already asleep");
            _crowd.Run(120);
            Assert.AreEqual(ticks, walker.Ticks, "not ticked on the new worker either");
            Assert.AreEqual(1f, walker.Velocity.magnitude, 1e-4f, "its state came with it");
            there.Wake();
            _crowd.Run(10);
            Assert.Greater(walker.Ticks, ticks);
        }

        [Test]
        public void AClientsEntityCannotSleep()
        {
            var go = new GameObject("pawn");
            try
            {
                var e = go.AddComponent<NetworkIdentity>();
                e.OwnerClientId = 42;
                e.Sleep();
                e.BeginTick(100, NetworkTime.TickInterval);
                Assert.IsFalse(e.IsDormant);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
