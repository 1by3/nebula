using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-473: after a correction the simulated state is exact, and only the drawn pose carries an offset that decays. On
    /// <see cref="PredictedPairRig"/>: a worker's walker and an owner's predicted walker, Nebula's own reconcile between them.
    /// </summary>
    public sealed class CorrectionSmoothingTests
    {
        private const float Frame = NetworkTime.TickInterval;
        private PredictedPairRig _rig;
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            _rig?.Dispose();
            _rig = null;
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
        }

        /// <summary>Runs until the owner has corrected once and returns the step it did.</summary>
        private void AdvanceToFirstCorrection()
        {
            for (int i = 0; i < 40 && _rig.Client.Corrections == 0; i++) _rig.Advance();
            Assert.AreEqual(1, _rig.Client.Corrections, "one correction");
        }

        [Test]
        public void AHalfMeterCorrectionIsDrawnOverAboutAHundredMillisecondsWithNoFrameMovingMoreThanTheDecayAllows()
        {
            _rig = new PredictedPairRig(10);
            _rig.Advance(3);
            _rig.Shove(new Vector3(0.5f, 0f, 0f));
            var client = _rig.Client;
            AdvanceToFirstCorrection();

            // The frame of the correction draws the entity where it was: no jump.
            Assert.That(client.Identity.transform.position.x, Is.EqualTo(0f).Within(1e-4f), "drawn where it was before the correction");
            Assert.That(client.CorrectionOffsetMeters, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(client.DrawnPosition.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(client.SimulatedPosition.x, Is.EqualTo(0.5f).Within(1e-4f), "while the simulation is already at the corrected pose");
            client.ApplyDrawnOffset(0f); // back to the drawn pose (reading the simulated pose took it off)

            float k = Mathf.Exp(-4.60517f * Frame / 0.1f);
            float previousDrawn = client.Identity.transform.position.x;
            float previousOffset = client.CorrectionOffsetMeters;
            int frames = 0;
            while (client.CorrectionOffsetMeters > 0f && frames < 30)
            {
                _rig.Advance();
                frames++;
                float drawn = client.Identity.transform.position.x;
                float step = drawn - previousDrawn;
                Assert.That(step, Is.GreaterThanOrEqualTo(-1e-5f), "never backwards");
                // The step is what the offset gives up in one frame, plus nothing: the simulated pose does not move (the walker stands still).
                Assert.That(step, Is.LessThanOrEqualTo(previousOffset * (1f - k) + 0.0011f), $"frame {frames} moved {step:F4} m");
                previousDrawn = drawn;
                previousOffset = client.CorrectionOffsetMeters;
                if (frames == 6) Assert.That(client.CorrectionOffsetMeters, Is.LessThan(0.01f), "about 1% left after 100 ms");
            }
            Assert.That(frames, Is.InRange(6, 10), "gone within a handful of frames");
            Assert.That(client.Identity.transform.position.x, Is.EqualTo(0.5f).Within(1e-5f), "and drawn at the simulated pose at the end");
            Assert.AreEqual(Vector3.zero, client.CorrectionOffset);
            Assert.AreEqual(Quaternion.identity, client.CorrectionRotationOffset);
        }

        [Test]
        public void ACorrectionOverTheSnapDistanceTeleportsAndOneUnderItIsEased()
        {
            _rig = new PredictedPairRig(10);
            _rig.Advance(3);
            _rig.Shove(new Vector3(0f, 0f, 2.9f));
            AdvanceToFirstCorrection();
            Assert.That(_rig.Client.CorrectionOffsetMeters, Is.EqualTo(2.9f).Within(1e-3f), "2.9 m is eased");
            _rig.Dispose();

            _rig = new PredictedPairRig(10);
            _rig.Advance(3);
            _rig.Shove(new Vector3(0f, 0f, 3.1f));
            AdvanceToFirstCorrection();
            Assert.AreEqual(0f, _rig.Client.CorrectionOffsetMeters, "3.1 m is not");
            Assert.That(_rig.Client.Identity.transform.position.z, Is.EqualTo(3.1f).Within(1e-4f), "drawn at the corrected pose at once");
        }

        [Test]
        public void TheSnapDistanceAndTheSmoothingTimeAreOverridablePerBehaviour()
        {
            _rig = new PredictedPairRig(10, snap: 0.25f);
            _rig.Advance(3);
            _rig.Shove(new Vector3(0.5f, 0f, 0f));
            AdvanceToFirstCorrection();
            Assert.AreEqual(0f, _rig.Client.CorrectionOffsetMeters, "0.5 m is over a snap distance of 0.25 m");
            _rig.Dispose();

            _rig = new PredictedPairRig(10, smoothing: 0f);
            _rig.Advance(3);
            _rig.Shove(new Vector3(0.5f, 0f, 0f));
            AdvanceToFirstCorrection();
            Assert.AreEqual(0f, _rig.Client.CorrectionOffsetMeters, "no smoothing time, no offset");
            Assert.That(_rig.Client.Identity.transform.position.x, Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void OffsetsAccumulateWhenCorrectionsComeFasterThanTheyDecay()
        {
            _rig = new PredictedPairRig(10);
            _rig.Advance(3);
            _rig.Shove(new Vector3(0.3f, 0f, 0f));
            var client = _rig.Client;
            AdvanceToFirstCorrection();
            Assert.That(client.CorrectionOffsetMeters, Is.EqualTo(0.3f).Within(1e-4f));
            // The next tick's report: the worker was shoved again before simulating it.
            _rig.Shove(new Vector3(0.3f, 0f, 0f));
            _rig.Advance();
            Assert.AreEqual(2, client.Corrections);
            Assert.That(client.CorrectionOffsetMeters, Is.EqualTo(0.6f).Within(1e-3f), "both are still owed");
            Assert.That(client.Identity.transform.position.x, Is.EqualTo(0f).Within(1e-3f), "and the entity has not moved on screen");
        }

        [Test]
        public void ARotationCorrectionIsEasedToo()
        {
            _rig = new PredictedPairRig(10);
            _rig.Advance(3);
            var worker = _rig.Worker;
            worker.Identity.SetLocalPose(worker.Identity.Container, worker.Identity.LocalPosition + new Vector3(0.2f, 0f, 0f), Quaternion.Euler(0f, 30f, 0f));
            var client = _rig.Client;
            AdvanceToFirstCorrection();
            Assert.That(client.CorrectionOffsetDegrees, Is.EqualTo(30f).Within(0.01f));
            Assert.That(Quaternion.Angle(client.Identity.transform.rotation, Quaternion.identity), Is.LessThan(0.01f), "drawn upright");
            for (int i = 0; i < 12; i++) _rig.Advance();
            Assert.That(Quaternion.Angle(client.Identity.transform.rotation, Quaternion.Euler(0f, 30f, 0f)), Is.LessThan(0.1f), "and turned by the end");
        }

        [Test]
        public void TheSimulatedStateIsTheSameWithAndWithoutSmoothingAndAgreesWithTheWorker()
        {
            var smoothed = Run(0.1f, out int smoothedCorrections, out var smoothedDrawn);
            var snapped = Run(0f, out int snappedCorrections, out _);
            Assert.AreEqual(snappedCorrections, smoothedCorrections, "the same corrections");
            Assert.That(smoothed.Count, Is.EqualTo(snapped.Count));
            for (int i = 0; i < smoothed.Count; i++) Assert.AreEqual(snapped[i], smoothed[i], $"Simulate #{i} started from the same pose");
            Assert.That(smoothedDrawn, Is.GreaterThan(0.2f), "while the smoothed entity was drawn away from the simulated one");

            List<Vector3> Run(float seconds, out int corrections, out float maxOffset)
            {
                using (var rig = new PredictedPairRig(8, seconds))
                {
                    rig.Client.Hands = rig.Worker.Hands = new Vector3(1f, 0f, 2f);
                    maxOffset = 0f;
                    for (int i = 0; i < 90; i++)
                    {
                        if (i == 20) rig.Shove(new Vector3(-0.8f, 0f, 0.3f));
                        if (i == 40) rig.Worker.Drift = new Vector3(0f, 0f, -4f); // not in the owner's prediction until a report carries it
                        rig.Advance();
                        maxOffset = Mathf.Max(maxOffset, rig.Client.CorrectionOffsetMeters);
                        if (i == 60) rig.Client.Hands = rig.Worker.Hands = Vector3.zero;
                    }
                    corrections = rig.Client.Corrections;
                    int settled = corrections;
                    rig.Advance(30);
                    Assert.AreEqual(settled, rig.Client.Corrections, "the prediction agrees with the worker: nothing left to correct");
                    var observed = new List<Vector3>(rig.Client.Observed);
                    return observed;
                }
            }
        }

        [Test]
        public void AContainerLocalOffsetFollowsTheContainerWhileItMoves()
        {
            var shipObject = new GameObject("ship");
            _objects.Add(shipObject);
            shipObject.transform.position = new Vector3(-20f, 0f, 0f);
            var shipIdentity = shipObject.AddComponent<NetworkIdentity>();
            var box = shipObject.AddComponent<Container>();
            box.ContainerId = "ship";
            box.Size = new Vector3(10f, 6f, 20f);
            box.Center = new Vector3(0f, 3f, 0f);
            shipObject.AddComponent<NetworkTransform>();
            shipIdentity.Initialize();
            shipIdentity.NetId = 42;
            shipIdentity.OwnerWorkerIndex = 7;
            shipIdentity.SetContainer(ContainerRegistry.Find(shipObject.transform.position, box));
            shipIdentity.InvokeSpawn();

            var go = new GameObject("pawn");
            _objects.Add(go);
            go.transform.position = new Vector3(-18f, 0f, 3f);
            var pawn = go.AddComponent<PairWalker>();
            go.GetComponent<NetworkIdentity>().Initialize();
            pawn.Identity.NetId = 43;
            pawn.Identity.SetContainer(box);
            Assert.AreSame(shipObject.transform, go.transform.parent);
            Assert.AreEqual(new Vector3(2f, 0f, 3f), pawn.Identity.LocalPosition);

            var input = new NetworkWriter(32);
            pawn.ClientPredictTick(1, input);
            // The worker has it half a meter further along the ship's x axis.
            var state = new NetworkWriter(64);
            state.WriteVector3(new Vector3(2.5f, 0f, 3f));
            state.WriteQuaternion(Quaternion.identity);
            state.WriteVector3(Vector3.zero);
            state.WriteVector3(Vector3.zero);
            pawn.ClientReconcile(1, new NetworkReader(state.ToSegment()));
            Assert.AreEqual(1, pawn.Corrections);
            Assert.That((pawn.CorrectionOffset - new Vector3(-0.5f, 0f, 0f)).magnitude, Is.LessThan(1e-4f), "in the ship's own axes");

            // The ship turns a quarter and sails off. The offset turns and goes with it.
            shipObject.transform.SetPositionAndRotation(new Vector3(40f, 5f, -30f), Quaternion.Euler(0f, 90f, 0f));
            ContainerRegistry.RefreshCaches();
            pawn.ApplyDrawnOffset(0f);
            var simulatedLocal = new Vector3(2.5f, 0f, 3f);
            var expected = box.ToWorld(simulatedLocal + pawn.CorrectionOffset);
            Assert.That((go.transform.position - expected).magnitude, Is.LessThan(1e-3f), "drawn where the ship's frame puts the offset pose");
            var worldOffset = go.transform.position - box.ToWorld(simulatedLocal);
            Assert.That((worldOffset - Quaternion.Euler(0f, 90f, 0f) * new Vector3(-0.5f, 0f, 0f)).magnitude, Is.LessThan(1e-3f), "the offset turned with the ship");
            Assert.That((pawn.DrawnPosition - go.transform.position).magnitude, Is.LessThan(1e-4f));

            // Decaying keeps it in the ship's axes.
            for (int i = 0; i < 4; i++) pawn.ApplyDrawnOffset(Frame);
            float k = Mathf.Exp(-4.60517f * 4f * Frame / 0.1f);
            Assert.That((pawn.CorrectionOffset - new Vector3(-0.5f * k, 0f, 0f)).magnitude, Is.LessThan(1e-3f));
            Assert.That((pawn.SimulatedPosition - simulatedLocal).magnitude, Is.LessThan(1e-4f), "and the simulated pose is never touched");
        }

        [Test]
        public void ACrossingAPortalTeleportsInsteadOfEasing()
        {
            _rig = new PredictedPairRig(10);
            _rig.Advance(3);
            _rig.Shove(new Vector3(0.5f, 0f, 0f));
            AdvanceToFirstCorrection();
            Assert.That(_rig.Client.CorrectionOffsetMeters, Is.GreaterThan(0.4f));
            _rig.Client.CarryThroughPortal(default, null);
            Assert.AreEqual(0f, _rig.Client.CorrectionOffsetMeters);
            _rig.Client.ApplyDrawnOffset(Frame);
            Assert.That(_rig.Client.Identity.transform.position.x, Is.EqualTo(0.5f).Within(1e-4f));
        }
    }
}
