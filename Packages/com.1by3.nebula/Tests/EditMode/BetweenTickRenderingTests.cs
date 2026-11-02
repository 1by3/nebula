using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="PredictedBehaviourBase.RenderBetweenTicks"/>: a predicted entity is drawn between its last two predicted ticks,
    /// so it moves smoothly at any frame rate, and the simulation never sees the blend. On <see cref="PredictedPairRig"/>.
    /// </summary>
    public sealed class BetweenTickRenderingTests
    {
        private static float Frame => NetworkTime.TickInterval;
        private const float Speed = 6f; // 0.1 m per tick at 60 Hz
        private PredictedPairRig _rig;

        [TearDown]
        public void TearDown()
        {
            _rig?.Dispose();
            _rig = null;
        }

        private PredictedPairRig Moving(int lag, bool betweenTicks)
        {
            var rig = new PredictedPairRig(lag);
            rig.Client.BetweenTicks = betweenTicks;
            rig.Client.Hands = rig.Worker.Hands = new Vector3(Speed, 0f, 0f);
            return rig;
        }

        [Test]
        public void AnEntityMovingSteadilyIsDrawnInEvenStepsBetweenTicks()
        {
            _rig = Moving(4, true);
            _rig.Advance(10);
            var client = _rig.Client;
            float tickStep = Speed * NetworkTime.TickInterval;
            float previous = float.NaN;
            for (int tick = 0; tick < 5; tick++)
            {
                _rig.Advance();
                float simulated = client.SimulatedPosition.x;
                Assert.That(simulated, Is.EqualTo(_rig.Step * tickStep).Within(1e-4f), "the simulation is where the ticks put it");
                // A 240 Hz display: four frames per fixed step.
                for (int f = 0; f < 4; f++)
                {
                    float fraction = f / 4f;
                    client.ApplyDrawnOffset(Frame / 4f, fraction);
                    float drawn = client.Identity.transform.position.x;
                    Assert.That(drawn, Is.EqualTo(simulated - tickStep * (1f - fraction)).Within(1e-4f), $"tick {tick} frame {f}");
                    Assert.That(client.DrawnPosition.x, Is.EqualTo(drawn).Within(1e-5f), "DrawnPosition says where it is drawn");
                    if (!float.IsNaN(previous)) Assert.That(drawn - previous, Is.EqualTo(tickStep / 4f).Within(1e-4f), "every frame moves the same distance");
                    previous = drawn;
                }
                Assert.That(client.SimulatedPosition.x, Is.EqualTo(simulated).Within(1e-6f), "drawing never moved the simulated pose");
                client.ApplyDrawnOffset(0f, 1f);
            }
        }

        [Test]
        public void ItIsOffByDefault()
        {
            _rig = Moving(4, false);
            _rig.Advance(10);
            float simulated = _rig.Client.SimulatedPosition.x;
            _rig.Client.ApplyDrawnOffset(Frame, 0.25f);
            Assert.That(_rig.Client.Identity.transform.position.x, Is.EqualTo(simulated).Within(1e-6f));
            Assert.AreEqual(0.25f, _rig.Client.RenderTickFraction);
        }

        [Test]
        public void TheSimulationIsTheSameWithAndWithoutTheBlend()
        {
            var blended = Run(true, out int blendedCorrections);
            var plain = Run(false, out int plainCorrections);
            Assert.AreEqual(plainCorrections, blendedCorrections, "the same corrections");
            Assert.AreEqual(plain.Count, blended.Count);
            for (int i = 0; i < plain.Count; i++) Assert.AreEqual(plain[i], blended[i], $"Simulate #{i} started from the same pose");

            List<Vector3> Run(bool betweenTicks, out int corrections)
            {
                using (var rig = Moving(8, betweenTicks))
                {
                    for (int i = 0; i < 80; i++)
                    {
                        if (i == 20) rig.Shove(new Vector3(-0.8f, 0f, 0.3f));
                        if (i == 45) rig.Worker.Drift = new Vector3(0f, 0f, -4f);
                        rig.Advance();
                        rig.Client.ApplyDrawnOffset(Frame * 0.5f, 0.5f);
                    }
                    corrections = rig.Client.Corrections;
                    return new List<Vector3>(rig.Client.Observed);
                }
            }
        }

        [Test]
        public void ACorrectionWhileBlendingKeepsTheEntityWhereItWasDrawn()
        {
            _rig = Moving(10, true);
            _rig.Advance(5);
            _rig.Shove(new Vector3(0.5f, 0f, 0f));
            var client = _rig.Client;
            float tickStep = Speed * NetworkTime.TickInterval;
            for (int i = 0; i < 40 && client.Corrections == 0; i++) _rig.Advance();
            Assert.AreEqual(1, client.Corrections, "one correction");
            // The correction moved the simulation half a meter; halfway through the step the entity is drawn halfway between
            // where it was predicted at the last two ticks, as if nothing had happened.
            client.ApplyDrawnOffset(0f, 0.5f);
            Assert.That(client.Identity.transform.position.x, Is.EqualTo((_rig.Step - 0.5f) * tickStep).Within(1e-4f));
            Assert.That(client.SimulatedPosition.x, Is.EqualTo(_rig.Step * tickStep + 0.5f).Within(1e-4f));
        }

        [Test]
        public void ATeleportBetweenTicksIsNotSmeared()
        {
            _rig = Moving(4, true);
            _rig.DeliverReports = false;
            _rig.Advance(6);
            var client = _rig.Client;
            float tickStep = Speed * NetworkTime.TickInterval;
            var at = client.SimulatedPosition + new Vector3(50f, 0f, 0f);
            client.Identity.SetLocalPose(client.Identity.Container, at, Quaternion.identity);
            client.ApplyDrawnOffset(Frame * 0.5f, 0.5f);
            Assert.That(client.Identity.transform.position.x, Is.EqualTo(at.x).Within(1e-4f), "drawn where it was put, not dragged from where it was");
            _rig.Advance();
            client.ApplyDrawnOffset(Frame * 0.5f, 0.5f);
            Assert.That(client.Identity.transform.position.x, Is.EqualTo(at.x + tickStep * 0.5f).Within(1e-4f), "and blended from there on");
        }

        [Test]
        public void ARotationIsSlerpedBetweenTicks()
        {
            _rig = new PredictedPairRig(4);
            var client = _rig.Client;
            client.BetweenTicks = true;
            client.TurnDegreesPerTick = _rig.Worker.TurnDegreesPerTick = 2f;
            _rig.Advance(10);
            var simulated = client.Identity.LocalRotation;
            client.ApplyDrawnOffset(Frame * 0.25f, 0.25f);
            // A quarter of the way from the previous tick's heading (2 degrees back) to the newest.
            float back = Quaternion.Angle(client.Identity.transform.rotation, simulated);
            Assert.That(back, Is.EqualTo(1.5f).Within(0.01f));
            Assert.That(Quaternion.Angle(client.DrawnRotation, client.Identity.transform.rotation), Is.LessThan(0.01f));
            client.RemoveDrawnOffset();
            Assert.That(Quaternion.Angle(client.Identity.LocalRotation, simulated), Is.LessThan(1e-4f), "and the simulated heading is untouched");
        }
    }
}
