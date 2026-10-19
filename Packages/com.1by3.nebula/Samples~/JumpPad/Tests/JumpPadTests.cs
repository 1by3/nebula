using System.Collections.Generic;
using System.Reflection;
using Nebula;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NebulaSamples.Tests
{
    /// <summary>
    /// The jump pad end to end without a network: a pad on the worker launches a pawn with a server event, the pawn's
    /// owner hears of it in its reports, and its replay ends where the worker did, with nothing to correct.
    /// </summary>
    public sealed class JumpPadTests
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const int Lag = 6;
        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            typeof(NebulaRuntime).GetMethod("Reset", Members).Invoke(null, null);
            typeof(NebulaRuntime).GetProperty("IsServer", Members).SetValue(null, true);
            var go = new GameObject("outdoor");
            _objects.Add(go);
            var container = go.AddComponent<Container>();
            container.ContainerId = "outdoor";
            container.Size = new Vector3(200, 60, 200);
            container.Center = new Vector3(0, 30, 0);
            ContainerRegistry.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
            typeof(NebulaRuntime).GetMethod("Reset", Members).Invoke(null, null);
        }

        private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Members).SetValue(target, value);
        private static void SetTick(uint tick) => typeof(NetworkTime).GetProperty("Tick", Members).SetValue(null, tick);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Members).Invoke(target, args);
        private static object Call(object target, string name, params object[] args) => typeof(PredictedBehaviourBase).GetMethod(name, Members).Invoke(target, args);

        private T Make<T>(string name, Vector3 position) where T : NetworkBehaviour
        {
            var go = new GameObject(name);
            _objects.Add(go);
            go.transform.position = position;
            var behaviour = go.AddComponent<T>();
            var identity = go.GetComponent<NetworkIdentity>();
            Invoke(identity, "Initialize");
            Invoke(identity, "SetContainer", ContainerRegistry.Find(position), true);
            return behaviour;
        }

        [Test]
        public void APadLaunchesAPawnOnTheWorkerAndTheOwnersReplayEndsInTheSameState()
        {
            var pad = Make<JumpPad>("pad", new Vector3(0f, 0f, 3f));
            Set(pad.Identity, "HasAuthority", true);
            Set(pad.Identity, "NetId", 10UL);

            var worker = Make<JumpPadWalker>("worker-pawn", Vector3.zero);
            Set(worker.Identity, "HasAuthority", true);
            Set(worker.Identity, "OwnerClientId", 1UL);
            Set(worker.Identity, "NetId", 11UL);
            var client = Make<JumpPadWalker>("client-pawn", Vector3.zero);
            client.Controls = new Vector2(0f, 1f); // walks towards the pad, which is 3 m ahead

            var inputs = new Dictionary<uint, byte[]>();
            var writer = new NetworkWriter(64);
            float peak = 0f;
            for (uint step = 1; step <= 120; step++)
            {
                SetTick(step);
                writer.Reset();
                Call(client, "ClientPredictTick", step, writer);
                inputs[step] = writer.ToArray();
                if (step <= Lag) continue;
                uint s = step - Lag;
                Call(worker, "ServerReceiveInput", s, new NetworkReader(inputs[s]));
                SetTick(s);
                // The pawn ticks, then the pad: the event is stamped for the next tick, and the report of this one carries it.
                worker.NetworkTick(s, NetworkTime.TickInterval);
                pad.NetworkTick(s, NetworkTime.TickInterval);
                peak = Mathf.Max(peak, worker.transform.position.y);
                writer.Reset();
                Call(worker, "WriteOwnerState", writer);
                var state = writer.ToArray();
                Call(client, "ReceiveServerEvents", Call(worker, "TakeServerEventsForReport"));
                Call(client, "ClientReconcile", s, new NetworkReader(state));
            }
            Assert.That(peak, Is.GreaterThan(3f), "the worker's pawn was thrown up by the pad");
            Assert.AreEqual(1, worker.ServerEventsPosted, "once: the cooldown holds it while it stands near the pad");
            Assert.AreEqual(0, client.Corrections, "the owner's replay applied the launch on the worker's tick: nothing to correct");
            Assert.AreEqual(1, client.ServerEventReplays, "by replaying the report that carried the event, before the launch tick arrived");
        }
    }
}
