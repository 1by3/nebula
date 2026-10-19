using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NebulaSamples.Tests
{
    public sealed class LagCompensatedHitscanTests
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private RecordingSink _sink;
        private ulong _nextId;

        [SetUp]
        public void SetUp()
        {
            ResetRuntime();
            SetStatic(typeof(NebulaRuntime), "IsServer", true);
            SetStatic(typeof(StateHistory), "WindowTicks", 8);
            _sink = new RecordingSink();
            SetStatic(typeof(NebulaRuntime), "RpcSink", _sink);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ResetRuntime();
        }

        private static void ResetRuntime() => typeof(NebulaRuntime).GetMethod("Reset", Members).Invoke(null, null);
        private static void SetStatic(Type type, string name, object value) => type.GetProperty(name, Members).SetValue(null, value);
        private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Members).SetValue(target, value);

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        private T Behaviour<T>(string name, bool authority) where T : NetworkBehaviour
        {
            var go = NewObject(name);
            var identity = go.AddComponent<NetworkIdentity>();
            var behaviour = go.AddComponent<T>();
            typeof(NetworkIdentity).GetMethod("Initialize", Members).Invoke(identity, null);
            Set(identity, "NetId", ++_nextId);
            Set(identity, "Epoch", 1u);
            Set(identity, "IsSpawned", true);
            Set(identity, "HasAuthority", authority);
            return behaviour;
        }


        [Test]
        public void FireFromTheOwningClientSerializesAServerRpc()
        {
            SetStatic(typeof(NebulaRuntime), "IsServer", false);
            SetStatic(typeof(NebulaRuntime), "IsClient", true);
            var shooter = Behaviour<LagCompensatedHitscan>("shooter", false);
            Set(shooter.Identity, "IsLocalPlayer", true);

            shooter.Fire(new Vector3(1, 2, 3), Vector3.right, 17);

            Assert.AreEqual(1, _sink.ServerCalls);
            Assert.AreSame(shooter.Identity, _sink.Target);
            var reader = new NetworkReader(_sink.Payload);
            Assert.AreEqual(new Vector3(1, 2, 3), reader.ReadVector3());
            Assert.AreEqual(Vector3.right, reader.ReadVector3());
            Assert.AreEqual(17u, reader.ReadUInt());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DamageUsesTheAuthorityRpcPathForLocalAndGhostVictims(bool ghost)
        {
            var victim = Behaviour<SampleHealth>("victim", !ghost);

            victim.ApplyDamage(10);

            Assert.AreEqual(ghost ? 1 : 0, _sink.AuthorityCalls);
            Assert.AreEqual(ghost ? 100 : 90, victim.Health.Value);
            if (!ghost) return;
            Set(victim.Identity, "HasAuthority", true);
            _sink.Deliver(victim);
            Assert.AreEqual(90, victim.Health.Value, "the serialized call resolves the attributed damage handler");
        }

        // A shooter at the origin and a victim 5 m along +x, a box 1 m wide and 2 m high. The victim stands at z = 0 at tick 10 and at z = 3 at tick 11, so a shot along the
        // x axis hits it only if the shot is rewound to tick 10: the sample's cast is a PortalQueries.Raycast with a tick.
        private (LagCompensatedHitscan shooter, SampleHealth victim) ShotScene(bool ghost)
        {
            var worker = NewObject("worker").AddComponent<NebulaWorker>();
            var shooter = Behaviour<LagCompensatedHitscan>("shooter", true);
            shooter.Worker = worker;
            var victim = Behaviour<SampleHealth>("victim", !ghost);
            var box = victim.gameObject.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 2f, 1f);
            box.center = new Vector3(0f, 1f, 0f);
            victim.transform.position = new Vector3(5, 0, 0);
            var record = typeof(NetworkIdentity).GetMethod("RecordAuthoritativeState", Members);
            record.Invoke(victim.Identity, new object[] { 10u });
            victim.transform.position = new Vector3(5, 0, 3);
            record.Invoke(victim.Identity, new object[] { 11u });
            Physics.SyncTransforms();
            Set(worker, "CurrentTick", 11u);
            var entities = (Dictionary<ulong, NetworkIdentity>)typeof(NebulaWorker).GetField("_entities", Members).GetValue(worker);
            entities[victim.NetId] = victim.Identity;
            return (shooter, victim);
        }

        private static void Shoot(LagCompensatedHitscan shooter, uint tick) =>
            typeof(LagCompensatedHitscan).GetMethod("RpcFire", Members).Invoke(shooter, new object[] { new Vector3(0, 1f, 0), Vector3.right, tick });

        [TestCase(false)]
        [TestCase(true)]
        public void AShotHitsWhatStoodThereAtTheClaimedTick(bool ghost)
        {
            var (shooter, victim) = ShotScene(ghost);
            Shoot(shooter, 10u);
            Assert.AreEqual(ghost ? 1 : 0, _sink.AuthorityCalls);
            Assert.AreEqual(ghost ? 100 : 90, victim.Health.Value);
            if (ghost) Assert.AreSame(victim.Identity, _sink.Target);
            Assert.AreEqual(new Vector3(5, 0, 3), victim.transform.position, "and the victim is where it is now again");
        }

        [Test]
        public void AShotAtTheCurrentTickMissesWhatMovedAway()
        {
            var (shooter, victim) = ShotScene(false);
            Shoot(shooter, 11u);
            Assert.AreEqual(100, victim.Health.Value);
        }

        // What a client at a given round trip claims (NEB-472). The client renders remote entities half a round trip plus
        // InterpolationDelayTicks behind the newest tick, and its shot takes another half round trip to arrive, so the claim is
        // a whole round trip plus the interpolation delay old when the worker sees it. -nebula-sim-latency <ms> puts that
        // round trip on a local session; this checks the sample against the same arithmetic.
        private static uint ClaimAgeTicks(int roundTripMs)
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try { return (uint)(Mathf.CeilToInt(roundTripMs / 1000f * NetworkTime.TickRate) + config.InterpolationDelayTicks); }
            finally { Object.DestroyImmediate(config); }
        }

        [TestCase(0, true)]
        [TestCase(100, true)]
        [TestCase(200, true)]
        [TestCase(400, false)]
        public void AShotFromAClientAtSimulatedLatencyHitsWhatStoodThereUnlessTheClaimIsTooOld(int roundTripMs, bool hits)
        {
            SetStatic(typeof(StateHistory), "WindowTicks", 32); // StateHistory.DefaultWindowTicks, as NebulaConfig.StateHistoryTicks sets it
            var (shooter, victim) = ShotScene(false);
            uint age = ClaimAgeTicks(roundTripMs);
            uint claimed = 10u;
            uint now = claimed + age;
            var record = typeof(NetworkIdentity).GetMethod("RecordAuthoritativeState", Members);
            // The victim stood at z = 0 at the claimed tick and moved to z = 3 on the next, and stays there until the worker's tick.
            for (uint tick = 12; tick <= now; tick++) record.Invoke(victim.Identity, new object[] { tick });
            Set(shooter.Worker, "CurrentTick", now);
            Shoot(shooter, claimed);
            Assert.AreEqual(hits ? 90 : 100, victim.Health.Value, $"a claim {age} ticks old ({roundTripMs} ms round trip) against MaxClaimTicks {shooter.MaxClaimTicks}");
        }

        [Test]
        public void AClaimOutsideWhatTheGameAllowsIsRefused()
        {
            var (shooter, victim) = ShotScene(false);
            Shoot(shooter, 12u); // in the future
            Assert.AreEqual(100, victim.Health.Value);
            shooter.MaxClaimTicks = 0;
            Shoot(shooter, 10u); // further back than allowed
            Assert.AreEqual(100, victim.Health.Value);
        }

        [Test]
        public void AShooterDoesNotHitItself()
        {
            var (shooter, victim) = ShotScene(false);
            var own = shooter.gameObject.AddComponent<BoxCollider>();
            own.size = new Vector3(1f, 2f, 1f);
            own.center = new Vector3(0f, 1f, 0f);
            Physics.SyncTransforms();
            Shoot(shooter, 10u);
            Assert.AreEqual(90, victim.Health.Value, "the shot starts inside its own shooter's collider and goes on to the victim");
        }

        private sealed class RecordingSink : IRpcSink
        {
            public int ServerCalls, AuthorityCalls;
            public NetworkIdentity Target;
            public byte[] Payload;
            private uint _method;

            private void Capture(NetworkIdentity identity, uint method, ArraySegment<byte> args)
            {
                Target = identity;
                _method = method;
                Payload = new byte[args.Count];
                Array.Copy(args.Array, args.Offset, Payload, 0, args.Count);
            }

            public void SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args)
            { ServerCalls++; Capture(identity, methodHash, args); }

            public void SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args)
            { AuthorityCalls++; Capture(identity, methodHash, args); }

            public ulong SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args,
                Action<AuthorityCallResult> onDone, float timeoutSeconds) => throw new NotSupportedException();

            public void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args,
                ulong targetClientId, float radius) => throw new NotSupportedException();

            public void Deliver(NetworkBehaviour receiver) => typeof(NetworkBehaviour).Assembly.GetType("Nebula.RpcRegistry")
                .GetMethod("Invoke", Members).Invoke(null, new object[] { receiver, _method, new NetworkReader(Payload) });
        }
    }
}
