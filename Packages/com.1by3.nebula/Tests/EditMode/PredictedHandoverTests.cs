using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// A predicted entity handed to another worker carries its simulation state (<c>WriteState</c>) with its pending
    /// inputs, so the next worker continues exactly where the last stopped: a pawn in the middle of a jump keeps its
    /// vertical speed and air state across a seam.
    /// </summary>
    public sealed class PredictedHandoverTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        private struct Input : INetworkInput
        {
            public void Serialize(NetworkWriter w) { }
            public void Deserialize(NetworkReader r) { }
        }

        private sealed class Jumper : PredictedBehaviour<Input>
        {
            public float VerticalSpeed;
            public bool Airborne;

            protected override Input GatherInput() => default;
            protected override void Simulate(uint tick, in Input input, float dt) { }

            protected override void WriteState(NetworkWriter writer)
            {
                base.WriteState(writer);
                writer.WriteFloat(VerticalSpeed);
                writer.WriteBool(Airborne);
            }

            protected override void ReadState(NetworkReader reader)
            {
                base.ReadState(reader);
                VerticalSpeed = reader.ReadFloat();
                Airborne = reader.ReadBool();
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
        }

        private Jumper Make(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var jumper = go.AddComponent<Jumper>();
            go.GetComponent<NetworkIdentity>().Initialize();
            return jumper;
        }

        [Test]
        public void TheNextWorkerContinuesFromTheSimulationState()
        {
            var sender = Make("sender");
            sender.transform.position = new Vector3(3f, 12f, -4f);
            sender.VerticalSpeed = 6.5f;
            sender.Airborne = true;
            var writer = new NetworkWriter(256);
            sender.WritePendingInputs(writer);

            var receiver = Make("receiver"); // a ghost: whatever it last held, on the ground
            receiver.transform.position = new Vector3(3f, 11.8f, -4f);
            var reader = new NetworkReader();
            reader.Set(writer.ToSegment());
            receiver.ReadPendingInputs(reader);

            Assert.AreEqual(0, reader.Remaining);
            Assert.AreEqual(6.5f, receiver.VerticalSpeed);
            Assert.IsTrue(receiver.Airborne);
            Assert.That(Vector3.Distance(new Vector3(3f, 12f, -4f), receiver.transform.position), Is.LessThan(1e-5f));
        }

        [Test]
        public void ABlobWithoutTheStateStillReads()
        {
            // An older sender: inputs only.
            var writer = new NetworkWriter(64);
            writer.WriteUInt(40);   // last processed tick
            writer.WriteUShort(0);  // no pending inputs
            writer.WriteBool(false); // no last input
            var receiver = Make("receiver");
            receiver.VerticalSpeed = -1f;
            var reader = new NetworkReader();
            reader.Set(writer.ToSegment());
            receiver.ReadPendingInputs(reader);
            Assert.AreEqual(40u, receiver.LastProcessedInputTick);
            Assert.AreEqual(-1f, receiver.VerticalSpeed, "nothing to apply, nothing touched");
        }
    }
}
