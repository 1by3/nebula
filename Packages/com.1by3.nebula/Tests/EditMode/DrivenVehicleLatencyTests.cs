using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// How a driven vehicle feels to its driver at 30, 80 and 150 ms of round trip (NEB-361,
    /// <c>docs/driven-vehicles.md</c>): simulated by the worker and seen through the interpolated stream, as any entity
    /// the player does not own is today, against predicted by the driver's client (<see cref="NetworkIdentity.SetDriver"/>).
    /// The harness is <see cref="DrivenVehicleHarness"/>; the numbers it prints are the table in the doc.
    /// </summary>
    public sealed class DrivenVehicleLatencyTests
    {
        private const double StepAt = 3.0;
        private static readonly int[] Latencies = { 30, 80, 150 };

        private static float JitterFor(int rtt) => 2f + 0.15f * rtt * 0.5f;

        /// <summary>Full throttle; at <see cref="StepAt"/> the wheel goes hard over and stays there.</summary>
        private static DriveInput StepSteer(double t, float seenYaw) => new DriveInput { Throttle = 1f, Steer = t >= StepAt ? 1f : 0f };

        /// <summary>Full throttle; at <see cref="StepAt"/> the driver turns to a heading of 90 degrees, steering by what the camera shows (4 per second of gain, the wheel saturating at 15 degrees off).</summary>
        private static DriveInput TurnTo90(double t, float seenYaw)
        {
            float target = t >= StepAt ? 90f : 0f;
            return new DriveInput { Throttle = 1f, Steer = Mathf.Clamp(Mathf.DeltaAngle(seenYaw, target) / 15f, -1f, 1f) };
        }

        private static DrivenVehicleHarness Run(DrivenVehicleHarness.Mode mode, int rtt, System.Func<double, float, DriveInput> driver, double bumpAt = -1)
        {
            var h = new DrivenVehicleHarness(new DrivenVehicleHarness.Settings
            {
                Mode = mode, RttMs = rtt, JitterMs = JitterFor(rtt), Seed = 361 + rtt, Driver = driver, Seconds = 7.0,
                BumpAt = bumpAt, BumpMeters = 0.5f,
            });
            h.Run();
            return h;
        }

        private struct Row
        {
            public double Response, CameraResponse, Settle;
            public float Overshoot, Jitter, Worst, BumpCorrection;
            public int Corrections, Missed;
        }

        private static Row Measure(DrivenVehicleHarness.Mode mode, int rtt)
        {
            var row = new Row();
            using (var step = Run(mode, rtt, StepSteer))
            {
                row.Response = step.ResponseAfter(StepAt, camera: false);
                row.CameraResponse = step.ResponseAfter(StepAt, camera: true);
                row.Jitter = step.Jitter(1.0, 6.5);
                row.Worst = step.WorstStep(1.0, 6.5);
                row.Corrections = step.Corrections;
                row.Missed = step.InputsMissed;
            }
            using (var turn = Run(mode, rtt, TurnTo90))
            {
                float peak = 0f;
                double settledAt = StepAt;
                foreach (var f in turn.Frames)
                {
                    if (f.Time < StepAt) continue;
                    float over = Mathf.DeltaAngle(90f, f.ShownYaw);
                    if (over > peak) peak = over;
                    if (Mathf.Abs(over) > 2f) settledAt = f.Time;
                }
                row.Overshoot = peak;
                row.Settle = (settledAt - StepAt) * 1000.0;
            }
            if (mode == DrivenVehicleHarness.Mode.DriverPredicted)
                using (var bump = Run(mode, rtt, StepSteer, bumpAt: 4.0)) row.BumpCorrection = bump.MaxCorrection;
            return row;
        }

        [Test]
        public void MeasureTheDriversViewAtThreeLatencies()
        {
            var table = new StringBuilder();
            table.AppendLine("| Path | RTT | Input to visible (vehicle) | Input to visible (camera) | Overshoot of a 90° turn | Settled within 2° after | View jitter (RMS) | Worst hitch | Corrections |");
            table.AppendLine("|---|---|---|---|---|---|---|---|---|");
            var rows = new System.Collections.Generic.List<(DrivenVehicleHarness.Mode mode, int rtt, Row row)>();
            foreach (var mode in new[] { DrivenVehicleHarness.Mode.ServerSimulated, DrivenVehicleHarness.Mode.DriverPredicted })
            {
                foreach (int rtt in Latencies)
                {
                    var r = Measure(mode, rtt);
                    rows.Add((mode, rtt, r));
                    string corrections = mode == DrivenVehicleHarness.Mode.DriverPredicted
                        ? $"{r.Corrections} (missed inputs {r.Missed}; a 0.5 m shove only the worker saw: one of {r.BumpCorrection:0.00} m)" : "n/a";
                    table.AppendLine($"| {(mode == DrivenVehicleHarness.Mode.ServerSimulated ? "Worker simulates, driver interpolates" : "Driver predicts")} | {rtt} ms | {r.Response:0} ms | {r.CameraResponse:0} ms | {r.Overshoot:0.0}° | {r.Settle:0} ms | {r.Jitter:0.00} cm | {r.Worst:0.00} cm | {corrections} |");
                }
            }
            Debug.Log("NEB-361 measurements\n" + table);
            TestContext.Out.WriteLine(table.ToString());

            foreach (var (mode, rtt, r) in rows)
            {
                if (mode == DrivenVehicleHarness.Mode.ServerSimulated)
                {
                    // The verdict: a round trip plus the input lead and the interpolation delay before the driver sees anything.
                    Assert.That(r.Response, Is.GreaterThan(rtt + 50), $"{rtt} ms: the worker's vehicle answers the wheel after a round trip, the lead and the render delay");
                }
                else
                {
                    // Seen on the next rendered frame after the input, whatever the latency.
                    Assert.That(r.Response, Is.LessThanOrEqualTo(2 * NetworkTime.TickInterval * 1000.0 + 1), $"{rtt} ms: the predicted vehicle answers within two frames");
                    Assert.That(r.Overshoot, Is.LessThan(1f), $"{rtt} ms: the driver steers onto the heading without overshooting it");
                    Assert.AreEqual(0, r.Missed, $"{rtt} ms: every input reached the worker in time");
                    Assert.AreEqual(0, r.Corrections, $"{rtt} ms: a deterministic model agrees with the worker every tick");
                    Assert.That(r.Jitter, Is.LessThan(0.2f), $"{rtt} ms: the predicted view is as smooth as the interpolated one (under 2 mm)");
                    Assert.That(r.BumpCorrection, Is.InRange(0.4f, 0.6f), $"{rtt} ms: a shove only the worker saw is corrected once, by its size");
                }
            }
        }
    }
}
