using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NebulaSamples.Tests
{
    public sealed class ServerOwnedCrowdTests
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private ulong _nextId = 1000;

        [SetUp]
        public void SetUp()
        {
            typeof(NebulaRuntime).GetMethod("Reset", Members).Invoke(null, null);
            typeof(NebulaRuntime).GetProperty("IsServer", Members).SetValue(null, true);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            typeof(NebulaRuntime).GetMethod("Reset", Members).Invoke(null, null);
        }

        private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Members).SetValue(target, value);

        /// <summary>A walker as the worker that has authority over it holds it.</summary>
        private CrowdWalker Walker(Vector3 position)
        {
            var go = new GameObject("walker");
            _objects.Add(go);
            go.transform.position = position;
            var identity = go.AddComponent<NetworkIdentity>();
            var walker = go.AddComponent<CrowdWalker>();
            walker.AreaCentre = Vector3.zero;
            walker.AreaSize = new Vector2(200f, 200f);
            typeof(NetworkIdentity).GetMethod("Initialize", Members).Invoke(identity, null);
            Set(identity, "NetId", ++_nextId);
            Set(identity, "Epoch", 1u);
            Set(identity, "IsSpawned", true);
            Set(identity, "HasAuthority", true);
            return walker;
        }

        [Test]
        public void AWalkerCoversTheSameGroundWhateverItsUpdateInterval()
        {
            var often = Walker(Vector3.zero);
            var seldom = Walker(Vector3.zero);
            Set(seldom.Identity, "NetId", often.Identity.NetId); // same seed, same targets
            often.OnGainedAuthority();
            seldom.OnGainedAuthority();
            Assume.That(seldom.Target, Is.EqualTo(often.Target));
            // One second: 60 updates of a tick each, or one update of 60 ticks (UpdateInterval 60).
            for (int i = 0; i < 60; i++) often.NetworkTick((uint)i, 1f / 60f);
            seldom.NetworkTick(60, 1f);
            Assert.AreEqual(0f, Vector3.Distance(often.transform.position, seldom.transform.position), 0.01f);
            Assert.AreEqual(often.Speed, often.transform.position.magnitude, 0.01f, "it walked Speed metres in a second");
        }

        [Test]
        public void EverythingAWalkerNeedsCrossesAHandover()
        {
            var before = Walker(new Vector3(10f, 0f, -20f));
            before.OnGainedAuthority();
            for (int i = 0; i < 90; i++) before.NetworkTick((uint)i, 1f / 60f);
            var writer = new NetworkWriter();
            before.WriteHandoverState(writer);

            // The next worker's copy knows nothing but what the stream and the handover give it.
            var after = Walker(before.transform.position);
            after.AreaCentre = new Vector3(999f, 0f, 999f);
            after.ReadHandoverState(new NetworkReader(writer.ToSegment()));
            after.OnGainedAuthority();
            Assert.AreEqual(before.Target, after.Target);
            Assert.AreEqual(before.Wait, after.Wait);
            Assert.AreEqual(before.AreaCentre, after.AreaCentre);
            // And it goes on exactly as the old one would have, next target included.
            for (int i = 0; i < 1200; i++)
            {
                before.NetworkTick((uint)i, 1f / 60f);
                after.NetworkTick((uint)i, 1f / 60f);
            }
            Assert.AreEqual(0f, Vector3.Distance(before.transform.position, after.transform.position), 1e-3f);
            Assert.AreEqual(before.Target, after.Target);
        }

        [Test]
        public void TheSpawnerGivesTheFirstWalkersTheActiveTierAndTheRestTheLightOne()
        {
            var spawner = new GameObject("spawner").AddComponent<CrowdSpawner>();
            _objects.Add(spawner.gameObject);
            spawner.ActiveCount = 2;
            spawner.ActiveUpdateInterval = 6;
            spawner.LightUpdateInterval = 60;
            spawner.AreaCentre = new Vector3(5f, 0f, 5f);
            var tiers = new List<NetworkIdentity>();
            for (int i = 0; i < 4; i++)
            {
                var walker = Walker(Vector3.zero);
                Set(walker.Identity, "IsSpawned", false);
                spawner.Configure(walker.Identity, i);
                tiers.Add(walker.Identity);
            }
            Assert.AreEqual(6, tiers[0].UpdateInterval);
            Assert.AreEqual(RelevancePriority.Normal, tiers[1].RelevancePriority);
            Assert.AreEqual(60, tiers[2].UpdateInterval);
            Assert.AreEqual(RelevancePriority.Background, tiers[3].RelevancePriority);
            Assert.AreEqual(0f, tiers[0].SleepWhenUnobserved, "an active walker never falls asleep by itself");
            Assert.AreEqual(spawner.LightSleepWhenUnobserved, tiers[2].SleepWhenUnobserved, "a light one does, with no client near");
            Assert.AreEqual(spawner.AreaCentre, tiers[3].GetComponent<CrowdWalker>().AreaCentre);
        }
    }
}
