using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// <c>OnServerInputReceived</c>: the authority sees an input once, when it first arrives and before its tick runs.
    /// </summary>
    public sealed class PredictedServerInputReceivedTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        private struct Input : INetworkInput
        {
            public int Value;
            public void Serialize(NetworkWriter w) => w.WriteInt(Value);
            public void Deserialize(NetworkReader r) => Value = r.ReadInt();
        }

        private sealed class Probe : PredictedBehaviour<Input>
        {
            public readonly List<string> Log = new List<string>();
            public bool Throw;

            protected override Input GatherInput() => default;
            protected override void Simulate(uint tick, in Input input, float dt) => Log.Add($"sim {tick}:{input.Value}");
            protected override void OnServerInputReceived(uint tick, in Input input)
            {
                Log.Add($"recv {tick}:{input.Value}");
                if (Throw) throw new InvalidOperationException("override failed");
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _objects.Clear();
        }

        private Probe Make(bool authority = true)
        {
            var go = new GameObject("probe");
            _objects.Add(go);
            var p = go.AddComponent<Probe>();
            go.GetComponent<NetworkIdentity>().Initialize();
            p.Identity.OwnerClientId = 1;
            p.Identity.HasAuthority = authority;
            return p;
        }

        private static void Receive(Probe p, uint tick, int value)
        {
            var w = new NetworkWriter(16);
            new Input { Value = value }.Serialize(w);
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            p.ServerReceiveInput(tick, r);
        }

        [Test]
        public void FiresOncePerInputWithItsTickBeforeThatTickIsSimulated()
        {
            var p = Make();
            Receive(p, 12, 7);
            Receive(p, 13, 8);
            CollectionAssert.AreEqual(new[] { "recv 12:7", "recv 13:8" }, p.Log);
            p.NetworkTick(12, 1f / 60f);
            p.NetworkTick(13, 1f / 60f);
            CollectionAssert.AreEqual(new[] { "recv 12:7", "recv 13:8", "sim 12:7", "sim 13:8" }, p.Log);
        }

        [Test]
        public void DoesNotFireForARedundantResend()
        {
            var p = Make();
            Receive(p, 12, 7);
            Receive(p, 12, 7);
            Receive(p, 13, 8);
            Receive(p, 12, 7);
            CollectionAssert.AreEqual(new[] { "recv 12:7", "recv 13:8" }, p.Log);
        }

        [Test]
        public void DoesNotFireForAnInputThatArrivesAfterItsTick()
        {
            var p = Make();
            Receive(p, 10, 1);
            p.NetworkTick(10, 1f / 60f);
            p.NetworkTick(11, 1f / 60f); // repeats the last input: tick 11 arrives late
            p.Log.Clear();
            Receive(p, 10, 1); // already simulated
            Receive(p, 11, 2); // its tick ran with a repeated input
            Assert.IsEmpty(p.Log);
            Receive(p, 12, 3);
            CollectionAssert.AreEqual(new[] { "recv 12:3" }, p.Log);
        }

        [Test]
        public void DoesNotFireOnACopyWithoutAuthority()
        {
            var p = Make(authority: false);
            Receive(p, 12, 7);
            Assert.IsEmpty(p.Log);
        }

        [Test]
        public void DoesNotFireForInputsCarriedInAHandover()
        {
            var sender = Make();
            Receive(sender, 12, 7);
            var w = new NetworkWriter(256);
            sender.WritePendingInputs(w);
            var receiver = Make();
            var r = new NetworkReader();
            r.Set(w.ToSegment());
            receiver.ReadPendingInputs(r);
            Assert.IsEmpty(receiver.Log);
            receiver.NetworkTick(12, 1f / 60f);
            CollectionAssert.AreEqual(new[] { "sim 12:7" }, receiver.Log);
        }

        [Test]
        public void AnOverrideThatThrowsDoesNotBreakInput()
        {
            var p = Make();
            p.Throw = true;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("OnServerInputReceived of Probe .*threw"));
            Receive(p, 12, 7);
            p.NetworkTick(12, 1f / 60f);
            CollectionAssert.AreEqual(new[] { "recv 12:7", "sim 12:7" }, p.Log);
            Assert.AreEqual(12u, p.LastProcessedInputTick);
        }
    }
}
