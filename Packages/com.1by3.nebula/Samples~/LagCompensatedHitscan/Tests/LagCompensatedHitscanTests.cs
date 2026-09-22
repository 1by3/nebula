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

        private Container Container(string name, ulong scope)
        {
            var container = NewObject(name).AddComponent<Container>();
            Set(container, "Instance", new InstanceContainerInfo { InstanceId = scope });
            return container;
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

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void AShotCanCrossContainersButNotSimulationScopes(bool ghost, bool otherScope)
        {
            var worker = NewObject("worker").AddComponent<NebulaWorker>();
            var shooter = Behaviour<LagCompensatedHitscan>("shooter", true);
            shooter.Worker = worker;
            Set(shooter.Identity, "Container", Container("west", 0));
            var victim = Behaviour<SampleHealth>("victim", !ghost);
            Set(victim.Identity, "Container", Container("east", otherScope ? 7UL : 0UL));
            victim.transform.position = new Vector3(5, 0, 0);
            typeof(NetworkIdentity).GetMethod("RecordAuthoritativeState", Members).Invoke(victim.Identity, new object[] { 10u });
            Set(worker, "CurrentTick", 10u);
            var entities = (Dictionary<ulong, NetworkIdentity>)typeof(NebulaWorker).GetField("_entities", Members).GetValue(worker);
            entities[victim.NetId] = victim.Identity;

            typeof(LagCompensatedHitscan).GetMethod("RpcFire", Members).Invoke(shooter,
                new object[] { new Vector3(0, 0.9f, 0), Vector3.right, 10u });

            Assert.AreEqual(!otherScope && ghost ? 1 : 0, _sink.AuthorityCalls);
            Assert.AreEqual(!otherScope && !ghost ? 90 : 100, victim.Health.Value);
            if (!otherScope && ghost) Assert.AreSame(victim.Identity, _sink.Target);
        }

        [TestCase(-10f, 1f, 1f, 0f, true, 9.5f)]
        [TestCase(-10f, 100f, 1f, 0f, false, 0f)]
        [TestCase(0f, 100f, 0f, 1f, false, 0f)]
        [TestCase(0f, 100f, 0f, -1f, true, 97.5f)]
        [TestCase(-10f, 2.3f, 1f, 0f, true, 9.6f)]
        [TestCase(-10f, -0.3f, 1f, 0f, true, 9.6f)]
        [TestCase(0f, 1f, 1f, 0f, true, 0f)]
        [TestCase(10f, 1f, 1f, 0f, false, 0f)]
        [TestCase(-10f, 2.5f, 1f, 0f, true, 10f)]
        public void FiniteCapsuleReturnsTheNearestForwardIntersection(float x, float y, float dx, float dy, bool expected, float expectedDistance)
        {
            var ray = new Ray(new Vector3(x, y, 0), new Vector3(dx, dy, 0));
            bool hit = Intersects(ray, Vector3.zero, Vector3.up * 2f, 0.5f, out float distance);
            Assert.AreEqual(expected, hit);
            if (hit) Assert.AreEqual(expectedDistance, distance, 0.002f);
        }

        [Test]
        public void AZeroLengthCapsuleIsASphere()
        {
            Assert.IsTrue(Intersects(new Ray(Vector3.left * 3f, Vector3.right), Vector3.zero, Vector3.zero, 0.5f, out float distance));
            Assert.AreEqual(2.5f, distance, 0.001f);
        }

        [Test]
        public void AZeroDirectionCannotHit()
        {
            Assert.IsFalse(Intersects(new Ray(Vector3.zero, Vector3.zero), Vector3.zero, Vector3.up * 2f, 0.5f, out _));
        }

        private static bool Intersects(Ray ray, Vector3 a, Vector3 b, float radius, out float distance)
        {
            var args = new object[] { ray, a, b, radius, 0f };
            bool hit = (bool)typeof(LagCompensatedHitscan).GetMethod("IntersectsCapsule", Members).Invoke(null, args);
            distance = (float)args[4];
            return hit;
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
