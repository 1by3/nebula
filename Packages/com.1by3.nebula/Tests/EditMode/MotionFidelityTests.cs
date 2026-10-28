using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Protocol 30 motion fidelity on the identity stream: 15-bit rotations (NEB-406), angular velocity in state entries,
    /// rotation extrapolation, and velocity-aware (Hermite) interpolation between samples.
    /// </summary>
    public sealed class MotionFidelityTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp] public void SetUp() { NebulaRuntime.Reset(); NebulaRuntime.IsServer = true; ContainerRegistry.Rebuild(); }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
            NebulaRuntime.Reset();
        }

        private RemoteInterpolator Buffer(bool velocityAware = true)
        {
            var go = new GameObject("interpolated");
            _objects.Add(go);
            var buffer = go.AddComponent<RemoteInterpolator>();
            buffer.VelocityAware = velocityAware;
            return buffer;
        }

        // ---- the wire

        [Test]
        public void APreciseRotationIsSixBytesAndWithinFiveThousandthsOfADegree()
        {
            var random = new System.Random(406);
            for (int i = 0; i < 500; i++)
            {
                var q = Quaternion.Euler((float)random.NextDouble() * 360f, (float)random.NextDouble() * 360f, (float)random.NextDouble() * 360f);
                var w = new NetworkWriter();
                w.WritePreciseQuaternion(q);
                Assert.AreEqual(6, w.Length);
                var back = new NetworkReader(w.ToSegment()).ReadPreciseQuaternion();
                Assert.That(RemoteInterpolator.AngleDegrees(q, back), Is.LessThan(0.01f), $"rotation {i}");
            }
        }

        [Test]
        public void ATurnOfFiveHundredthsOfADegreeSurvivesThePreciseEncoding()
        {
            var a = Quaternion.Euler(10f, 33f, -4f);
            var b = Quaternion.Euler(0f, 0.05f, 0f) * a;
            Quaternion RoundTrip(Quaternion q, bool precise)
            {
                var w = new NetworkWriter();
                if (precise) w.WritePreciseQuaternion(q); else w.WriteCompressedQuaternion(q);
                var r = new NetworkReader(w.ToSegment());
                return precise ? r.ReadPreciseQuaternion() : r.ReadCompressedQuaternion();
            }
            Assert.That(RemoteInterpolator.AngleDegrees(RoundTrip(a, true), RoundTrip(b, true)), Is.EqualTo(0.05f).Within(0.01f), "NEB-406: the turn arrives");
            Assert.AreEqual(0f, Quaternion.Angle(a, b), "Quaternion.Angle calls it no turn at all, which is why change detection uses AngleDegrees");
            Assert.That(RemoteInterpolator.AngleDegrees(a, b), Is.EqualTo(0.05f).Within(1e-3f));
        }

        private static EntityStateEntry Full(TransformFieldsExt ext) => new EntityStateEntry
        {
            NetId = 7, Epoch = 2, Container = ContainerRef.None,
            Fields = TransformFields.Position | TransformFields.Rotation | TransformFields.Quaternion | TransformFields.Compressed | TransformFields.Velocity,
            Ext = ext,
            LocalPosition = new Vector3(1f, 2f, 3f), LocalRotation = Quaternion.Euler(5f, 70f, 1f),
            Velocity = new Vector3(40f, 0f, -3f), AngularVelocity = new Vector3(0f, 0.6f, 0f),
        };

        [Test]
        public void AnEntryCarriesItsExtensionsToAProtocol30PeerAndDropsThemForAnOlderOne()
        {
            var entry = Full(TransformFieldsExt.AngularVelocity | TransformFieldsExt.PreciseRotation);
            var w = new NetworkWriter();
            entry.Write(w, HelloMsg.MotionFidelityVersion);
            int current = w.Length;
            var back = EntityStateEntry.Read(new NetworkReader(w.ToSegment()), HelloMsg.MotionFidelityVersion);
            Assert.AreEqual(entry.Ext, back.Ext);
            Assert.That((back.AngularVelocity - entry.AngularVelocity).magnitude, Is.LessThan(1e-3f));
            Assert.That(Quaternion.Angle(back.LocalRotation, entry.LocalRotation), Is.LessThan(0.01f));
            Assert.LessOrEqual(current, EntityStateEntry.WireSize);

            var old = new NetworkWriter();
            entry.Write(old, 29);
            Assert.AreEqual(current - 1 - 2 - 6, old.Length, "no extension byte, 4-byte rotation, no angular velocity");
            var legacy = EntityStateEntry.Read(new NetworkReader(old.ToSegment()), 29);
            Assert.AreEqual(TransformFieldsExt.None, legacy.Ext);
            Assert.AreEqual(Vector3.zero, legacy.AngularVelocity);
            Assert.That(Quaternion.Angle(legacy.LocalRotation, entry.LocalRotation), Is.LessThan(0.2f));
            Assert.That((legacy.Velocity - entry.Velocity).magnitude, Is.LessThan(0.1f));
            Assert.AreEqual(31, HelloMsg.ProtocolVersion, "30 (motion fidelity), then 31 (NEB-484, the dropped ticks)");
            Assert.AreEqual(30, HelloMsg.MinProtocolVersion, "both additive: a client of 30 still joins (the window is N and N-1)");
            Assert.AreEqual(30, HelloMsg.MotionFidelityVersion, "and a client of 30 is sent the extensions");
        }

        [Test]
        public void TheRootSendsTheAngularVelocityItTurnedAtSinceItsLastCapture()
        {
            var go = new GameObject("turning");
            _objects.Add(go);
            var id = go.AddComponent<NetworkIdentity>();
            var nt = go.AddComponent<NetworkTransform>();
            nt.UseQuaternionSynchronization = nt.UseQuaternionCompression = true;
            id.Initialize();
            id.NetId = 17; id.Epoch = 1; id.HasAuthority = true;
            id.InvokeSpawn();
            Assert.IsTrue(nt.CaptureRoot(10, out var first));
            Assert.AreEqual(TransformFieldsExt.AngularVelocity | TransformFieldsExt.PreciseRotation, first.Ext);
            Assert.AreEqual(Vector3.zero, first.AngularVelocity, "nothing to measure a turn from yet");
            go.transform.rotation = Quaternion.Euler(0f, 6f, 0f);
            Assert.IsTrue(nt.CaptureRoot(12, out var next));
            // 6 degrees in two ticks: 180 degrees a second.
            Assert.That((next.AngularVelocity - new Vector3(0f, Mathf.PI, 0f)).magnitude, Is.LessThan(1e-3f));
            // A slow turn below the old 0.16-degree blind spot is still a change worth sending (NEB-406).
            go.transform.rotation = Quaternion.Euler(0f, 6.05f, 0f);
            Assert.IsTrue(nt.CaptureRoot(14, out var slow), "0.05 degrees is over the 0.01-degree RotAngleThreshold");
            nt.SyncAngularVelocity = nt.PreciseRotation = false;
            go.transform.rotation = Quaternion.Euler(0f, 9f, 0f);
            Assert.IsTrue(nt.CaptureRoot(15, out var plain));
            Assert.AreEqual(TransformFieldsExt.None, plain.Ext, "both are settings");
        }

        // ---- the interpolator

        [Test]
        public void PastTheNewestSampleARotationKeepsTurningAtItsAngularVelocity()
        {
            var buffer = Buffer();
            var omega = new Vector3(0f, 1f, 0f);
            buffer.Push(10, null, Vector3.zero, Quaternion.identity, Vector3.zero, omega, true, true);
            Assert.IsTrue(buffer.Sample(13, out var _, out var _, out var rotation));
            Assert.That(Quaternion.Angle(rotation, Quaternion.identity), Is.EqualTo(3f / 60f * Mathf.Rad2Deg).Within(0.01f));

            var plain = Buffer();
            plain.Push(10, null, Vector3.zero, Quaternion.identity, Vector3.zero);
            plain.Sample(13, out var _, out var _, out var held);
            Assert.That(Quaternion.Angle(held, Quaternion.identity), Is.LessThan(1e-4f), "a stream without one holds its heading, as before");
        }

        [Test]
        public void BetweenSamplesOnACircleTheHermiteCurveStaysOnItAndTheChordCutsIt()
        {
            // 50 m/s round a 100 m circle, sent every 12 ticks (5 Hz).
            const float radius = 100f, speed = 50f;
            Vector3 At(float t) { float a = speed * t / radius; return new Vector3(radius * Mathf.Sin(a), 0f, radius * (1f - Mathf.Cos(a))); }
            Vector3 VelocityAt(float t) { float a = speed * t / radius; return new Vector3(speed * Mathf.Cos(a), 0f, speed * Mathf.Sin(a)); }
            float seconds = 12 * NetworkTime.TickInterval;
            var curved = Buffer();
            var straight = Buffer(false);
            foreach (var b in new[] { curved, straight })
            {
                b.Push(12, null, At(0f), Quaternion.identity, VelocityAt(0f));
                b.Push(24, null, At(seconds), Quaternion.identity, VelocityAt(seconds));
            }
            var truth = At(seconds * 0.5f);
            curved.Sample(18, out var _, out var c, out var _);
            straight.Sample(18, out var _, out var s, out var _);
            Assert.That((s - truth).magnitude, Is.GreaterThan(0.1f), "the chord's midpoint is 12 cm inside the circle");
            Assert.That((c - truth).magnitude, Is.LessThan(0.01f), "the Hermite point is on it");
        }

        [Test]
        public void VelocitiesThatDoNotExplainTheMoveFallBackToTheStraightLine()
        {
            var buffer = Buffer();
            // A teleport-sized move with velocities that say it stood still.
            buffer.Push(12, null, Vector3.zero, Quaternion.identity, Vector3.zero);
            buffer.Push(24, null, new Vector3(30f, 0f, 0f), Quaternion.identity, Vector3.zero);
            buffer.Sample(15, out var _, out var p, out var _);
            Assert.That((p - new Vector3(7.5f, 0f, 0f)).magnitude, Is.LessThan(1e-4f));
        }

        [Test]
        public void ATurnThatSpeedsUpIsFollowedThroughItsAngularVelocities()
        {
            // From rest to 2 rad/s over 12 ticks at a steady angular acceleration: it turned 0.2 rad, a quarter of it by halfway.
            float seconds = 12 * NetworkTime.TickInterval;
            var end = Quaternion.AngleAxis(seconds * Mathf.Rad2Deg, Vector3.up);
            var curved = Buffer();
            var straight = Buffer(false);
            foreach (var b in new[] { curved, straight })
            {
                b.Push(12, null, Vector3.zero, Quaternion.identity, Vector3.zero, Vector3.zero, true, true);
                b.Push(24, null, Vector3.zero, end, Vector3.zero, new Vector3(0f, 2f, 0f), true, true);
            }
            float truth = seconds * 0.25f * Mathf.Rad2Deg;
            curved.Sample(18, out var _, out var _, out var c);
            straight.Sample(18, out var _, out var _, out var s);
            Assert.That(Quaternion.Angle(Quaternion.identity, c), Is.EqualTo(truth).Within(0.05f));
            Assert.That(Quaternion.Angle(Quaternion.identity, s), Is.EqualTo(truth * 2f).Within(0.05f), "a slerp turns at the mean rate");
        }
    }
}
