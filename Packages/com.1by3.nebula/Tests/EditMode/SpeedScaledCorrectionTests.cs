using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Speed-aware reconciliation: the correction threshold, the snap distance and the smoothing time grow with the speed in the
    /// worker's report (<see cref="PredictedBehaviourBase.CorrectionThresholdTickFraction"/> and its siblings). On <see cref="PredictedPairRig"/>.
    /// </summary>
    public sealed class SpeedScaledCorrectionTests
    {
        private PredictedPairRig _rig;

        [TearDown]
        public void TearDown()
        {
            _rig?.Dispose();
            _rig = null;
        }

        private PredictedPairRig Moving(float speed)
        {
            var rig = new PredictedPairRig(10);
            rig.Client.ReportVelocity = rig.Worker.ReportVelocity = true;
            rig.Client.Hands = rig.Worker.Hands = new Vector3(speed, 0f, 0f);
            return rig;
        }

        private int CorrectionsAfterShove(float meters, int ticks = 30)
        {
            _rig.Shove(new Vector3(meters, 0f, 0f));
            int before = _rig.Client.Corrections;
            _rig.Advance(ticks);
            return _rig.Client.Corrections - before;
        }

        private void AdvanceToFirstCorrection()
        {
            for (int i = 0; i < 40 && _rig.Client.Corrections == 0; i++) _rig.Advance();
            Assert.AreEqual(1, _rig.Client.Corrections, "one correction");
        }

        [Test]
        public void AtWalkingSpeedTheConfiguredThresholdHolds()
        {
            _rig = Moving(5f);
            _rig.Advance(3);
            Assert.AreEqual(0.05f, _rig.Client.CorrectionThresholdAt(5f), 1e-6f, "a quarter tick at 5 m/s is 2 cm: the 5 cm threshold stands");
            Assert.AreEqual(1, CorrectionsAfterShove(0.1f), "a 10 cm error is corrected");
        }

        [Test]
        public void AtFlightSpeedTheThresholdIsAQuarterOfATicksTravel()
        {
            _rig = Moving(400f);
            _rig.Advance(3);
            float threshold = 400f * NetworkTime.TickInterval * 0.25f;
            Assert.AreEqual(threshold, _rig.Client.CorrectionThresholdAt(400f), 1e-5f);
            Assert.AreEqual(0, CorrectionsAfterShove(1f), "a 1 m error at 400 m/s is not replayed");
            Assert.AreEqual(400f, _rig.Client.LastCorrectionSpeed, 1e-3f, "the speed came from the worker's report");
            Assert.AreEqual(1, CorrectionsAfterShove(1f), "but one of 2 m is (over 1.67 m)");
        }

        [Test]
        public void AtFlightSpeedACorrectionOverThreeMetersIsEasedNotSnapped()
        {
            _rig = Moving(400f);
            _rig.Advance(3);
            _rig.Shove(new Vector3(10f, 0f, 0f));
            AdvanceToFirstCorrection();
            Assert.That(_rig.Client.CorrectionOffsetMeters, Is.EqualTo(10f).Within(1e-2f), "10 m is 25 ms of flight: eased (snap distance 100 m at 400 m/s)");
            _rig.Dispose();

            _rig = Moving(0f);
            _rig.Advance(3);
            _rig.Shove(new Vector3(10f, 0f, 0f));
            AdvanceToFirstCorrection();
            Assert.AreEqual(0f, _rig.Client.CorrectionOffsetMeters, "standing still, 10 m is a teleport, as before");
        }

        [Test]
        public void AtOrbitalSpeedACorrectionIsEasedOverLonger()
        {
            _rig = Moving(3000f);
            _rig.Advance(3);
            _rig.Shove(new Vector3(20f, 0f, 0f));
            AdvanceToFirstCorrection();
            float start = _rig.Client.CorrectionOffsetMeters;
            Assert.That(start, Is.EqualTo(20f).Within(0.05f));
            _rig.Advance(6); // 100 ms
            // 300 ms of smoothing at 3 km/s: exp(-4.6 x 0.1 / 0.3) = 22% left, where the 100 ms default leaves 1%.
            Assert.That(_rig.Client.CorrectionOffsetMeters / start, Is.InRange(0.18f, 0.26f));
        }

        [Test]
        public void TheScalingTurnsOffWithItsFactors()
        {
            _rig = Moving(400f);
            _rig.Client.ThresholdTickFraction = 0f;
            _rig.Client.SnapSeconds = 0f;
            _rig.Client.SmoothingDoublingSpeed = 0f;
            _rig.Advance(3);
            Assert.AreEqual(0.05f, _rig.Client.CorrectionThresholdAt(400f), 1e-6f);
            _rig.Shove(new Vector3(5f, 0f, 0f));
            AdvanceToFirstCorrection();
            Assert.AreEqual(0f, _rig.Client.CorrectionOffsetMeters, "5 m snaps at the fixed 3 m snap distance");
            Assert.AreEqual(1, CorrectionsAfterShove(0.1f), "and 10 cm is corrected at the fixed threshold");
        }
    }
}
