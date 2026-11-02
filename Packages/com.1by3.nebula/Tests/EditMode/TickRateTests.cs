using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// A configurable tick rate (NEB-381, docs/tick-rate.md): the rate is set once per process, durations held in
    /// seconds keep their meaning at 30 Hz, tick counts tuned at 60 Hz are rescaled, a client runs at the rate its
    /// gateway welcomes it with, and an older client is refused by a mesh that is not at 60 Hz.
    /// </summary>
    public sealed class TickRateTests
    {
        private float _fixedDeltaTime;

        [SetUp]
        public void SetUp() => _fixedDeltaTime = Time.fixedDeltaTime;

        [TearDown]
        public void TearDown()
        {
            NetworkTime.Configure(NetworkTime.DefaultTickRate);
            Time.fixedDeltaTime = _fixedDeltaTime;
            NetworkTime.Tick = 0;
        }

        [Test]
        public void TheRateIsClampedAndTheIntervalFollowsIt()
        {
            Assert.AreEqual(60, NetworkTime.TickRate, "the default");
            Assert.AreEqual(30, NetworkTime.Configure(30));
            Assert.AreEqual(30, NetworkTime.TickRate);
            Assert.AreEqual(1f / 30f, NetworkTime.TickInterval, 1e-7f);
            Assert.AreEqual(NetworkTime.MinTickRate, NetworkTime.ClampRate(3));
            Assert.AreEqual(NetworkTime.MaxTickRate, NetworkTime.ClampRate(1000));
            Assert.AreEqual(NetworkTime.DefaultTickRate, NetworkTime.ClampRate(0), "0 is the default rate, as an old gateway's welcome would say");
            int heard = 0;
            Action<int> handler = r => heard = r;
            NetworkTime.TickRateChanged += handler;
            try
            {
                NetworkTime.Configure(30);
                Assert.AreEqual(0, heard, "no change, no event");
                NetworkTime.Configure(45);
                Assert.AreEqual(45, heard);
            }
            finally { NetworkTime.TickRateChanged -= handler; }
        }

        [Test]
        public void DerivedTicksCountTheSameWallClockAtEitherRate()
        {
            double at60 = NetworkTime.DerivedTickExact;
            NetworkTime.Configure(30);
            double at30 = NetworkTime.DerivedTickExact;
            Assert.AreEqual(at60 / 2.0, at30, 1.0, "the same seconds since the origin, half as many ticks");
        }

        [Test]
        public void SecondsBecomeTicksAtTheRunningRate()
        {
            Assert.AreEqual(15, NetworkTime.SecondsToTicks(0.25));
            Assert.AreEqual(3, NetworkTime.SecondsToTicksCeil(0.05));
            NetworkTime.Configure(30);
            Assert.AreEqual(8, NetworkTime.SecondsToTicks(0.25), "7.5 rounds to 8");
            Assert.AreEqual(2, NetworkTime.SecondsToTicksCeil(0.05), "1.5 rounds up to 2");
            Assert.AreEqual(1, NetworkTime.SecondsToTicks(0.001), "a positive duration is at least a tick");
            Assert.AreEqual(0, NetworkTime.SecondsToTicks(0), "zero stays zero (off)");
            Assert.AreEqual(3, NetworkTime.FromDefaultRateTicks(6), "6 ticks at 60 Hz last 3 at 30");
            Assert.AreEqual(1, NetworkTime.FromDefaultRateTicks(1), "never below one tick");
            Assert.AreEqual(0, NetworkTime.FromDefaultRateTicks(0), "0 (none) is not rescaled");
        }

        [Test]
        public void NebulasOwnDurationsKeepTheirLengthInSeconds()
        {
            int lag60 = NebulaWorker.MaxLagTicks, skip60 = (int)NebulaWorker.SustainedSkipTicks, repeat60 = PredictedBehaviourBase.ServerEventRepeatTicks;
            Assert.AreEqual(15, lag60);
            Assert.AreEqual(60, skip60);
            Assert.AreEqual(30, repeat60);
            Assert.AreEqual(600u, AuthorityCallLedger.DefaultTtlTicks);
            Assert.AreEqual(60, UpdateGapDelay.MaxGapTicks);
            NetworkTime.Configure(30);
            Assert.AreEqual(8, NebulaWorker.MaxLagTicks);
            Assert.AreEqual(30u, NebulaWorker.SustainedSkipTicks);
            Assert.AreEqual(15, PredictedBehaviourBase.ServerEventRepeatTicks);
            Assert.AreEqual(300u, AuthorityCallLedger.DefaultTtlTicks);
            Assert.AreEqual(300u, new AuthorityCallLedger().TtlTicks, "a ledger made at 30 Hz remembers ten seconds");
            Assert.AreEqual(30, UpdateGapDelay.MaxGapTicks, "a second");
            Assert.AreEqual(15.0, UpdateGapDelay.SlewTicksPerSecond, 1e-9, "half the tick rate");
            Assert.AreEqual(1000f / 30f, WorkerLoadTracker.TickPeriodMs, 1e-3f, "the orchestrator weighs load against a 33 ms tick");
        }

        [Test]
        public void TheConfigsTimingIsHeldInSecondsAndReadInTicksAtTheRunningRate()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                Assert.AreEqual(3, config.InterpolationDelayTicks);
                Assert.AreEqual(12, config.MaxStreamLagTicks);
                Assert.AreEqual(30, config.SlowUpdateMaxDelayTicks);
                Assert.AreEqual(2, config.InputLeadMarginTicks);
                Assert.AreEqual(3, config.InputLeadTargetTicks);
                Assert.AreEqual(30, config.InputLeadMaxAdjustTicks);
                Assert.AreEqual(32, config.StateHistoryTicks);
                NetworkTime.Configure(30);
                Assert.AreEqual(2, config.InterpolationDelayTicks, "50 ms is two snapshots at 30 Hz");
                Assert.AreEqual(6, config.MaxStreamLagTicks);
                Assert.AreEqual(15, config.SlowUpdateMaxDelayTicks);
                Assert.AreEqual(1, config.InputLeadMarginTicks);
                Assert.AreEqual(2, config.InputLeadTargetTicks);
                Assert.AreEqual(15, config.InputLeadMaxAdjustTicks);
                Assert.AreEqual(16, config.StateHistoryTicks, "about half a second of lag-compensation history");
                config.InputLeadTargetTicks = 3;
                Assert.AreEqual(0.1f, config.InputLeadTargetSeconds, 1e-6f, "a tick count set at 30 Hz is stored as seconds at 30 Hz");
            }
            finally { Object.DestroyImmediate(config); }
        }

        [Test]
        public void AConfigSavedInTicksByAnOlderNebulaIsReadAsTicksAt60Hz()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                config.SetLegacyTimingForTest(interpolationDelay: 4, inputLeadMargin: 1, inputLeadTarget: 2, stateHistory: 64);
                config.UpgradeLegacyTiming();
                Assert.AreEqual(4f / 60f, config.InterpolationDelaySeconds, 1e-6f);
                Assert.AreEqual(1f / 60f, config.InputLeadMarginSeconds, 1e-6f);
                Assert.AreEqual(2f / 60f, config.InputLeadTargetSeconds, 1e-6f);
                Assert.AreEqual(64f / 60f, config.StateHistorySeconds, 1e-6f);
                Assert.AreEqual(4, config.InterpolationDelayTicks, "the same ticks at 60 Hz");
                config.InterpolationDelaySeconds = 0.1f;
                config.UpgradeLegacyTiming();
                Assert.AreEqual(0.1f, config.InterpolationDelaySeconds, 1e-6f, "the upgrade runs once");
                Assert.AreEqual(0.2f, config.MaxStreamLagSeconds, 1e-6f, "a field the old asset did not hold keeps its default");
            }
            finally { Object.DestroyImmediate(config); }
        }

        [Test]
        public void InterestDivisorsKeepTheirUpdatesPerSecondAt30Hz()
        {
            var settings = InterestSettings.Default;
            var normal60 = settings.TiersFor(RelevancePriority.Normal);
            Assert.AreEqual(1, normal60.NearDivisor);
            Assert.AreEqual(4, normal60.MidDivisor);
            Assert.AreEqual(12, normal60.FarDivisor);
            NetworkTime.Configure(30);
            var normal30 = settings.TiersFor(RelevancePriority.Normal);
            Assert.AreEqual(1, normal30.NearDivisor, "every update is every update");
            Assert.AreEqual(2, normal30.MidDivisor, "15 Hz");
            Assert.AreEqual(6, normal30.FarDivisor, "5 Hz");
            settings.BackgroundTiers = RelevanceTierBands.Of(30f, 80f, 30, 0, 0);
            var background = settings.TiersFor(RelevancePriority.Background);
            Assert.AreEqual(15, background.NearDivisor, "twice a second");
            Assert.AreEqual(0, background.MidDivisor, "none stays none");
            Assert.AreEqual(1, RelevanceTiers.ScaleDivisor(1, 30));
            Assert.AreEqual(1, RelevanceTiers.ScaleDivisor(2, 20), "never below every update");
        }

        [Test]
        public void AMeshAt30HzRefusesClientsThatDoNotHonourTheWelcomesRate()
        {
            Assert.IsTrue(ProtocolCompatibility.ClientProtocolAccepted(HelloMsg.MinProtocolVersion, 60), "N-1 joins a 60 Hz mesh");
            Assert.IsFalse(ProtocolCompatibility.ClientProtocolAccepted((ushort)(HelloMsg.TickRateVersion - 1), 30), "a client that counts at 60 Hz is refused at 30");
            Assert.IsTrue(ProtocolCompatibility.ClientProtocolAccepted(HelloMsg.TickRateVersion, 30));
            Assert.AreEqual(HelloMsg.TickRateVersion, ProtocolCompatibility.MinClientProtocol(30));
            Assert.AreEqual(HelloMsg.MinProtocolVersion, ProtocolCompatibility.MinClientProtocol(60));
        }

        [Test]
        public void AClientRunsAtTheRateItsGatewayWelcomesItWith()
        {
            var go = new GameObject("tick rate client");
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var client = go.AddComponent<NebulaClient>();
            try
            {
                typeof(NebulaClient).GetField("_transport", Hidden).SetValue(client, new ClientDisconnectTests.ScriptedTransport());
                typeof(NebulaClient).GetProperty("Config").SetValue(client, config);
                var writer = new NetworkWriter();
                new WelcomeMsg { ClientId = 5, TickRate = 30, ServerTick = 123456, Identity = "id", SessionToken = "s", NegotiatedVersion = HelloMsg.ProtocolVersion }.Write(writer);
                typeof(NebulaClient).GetMethod("Dispatch", Hidden).Invoke(client, new object[] { new NetworkReader(writer.ToSegment()) });
                Assert.AreEqual(30, NetworkTime.TickRate);
                Assert.AreEqual(1f / 30f, Time.fixedDeltaTime, 1e-6f, "one fixed step is one tick");
                Assert.AreEqual(30, config.TickRate);
                Assert.AreEqual(2, config.InterpolationDelayTicks, "the render delay is counted at the mesh's rate");
                double anchor = (double)typeof(NebulaClient).GetField("_serverTickAnchor", Hidden).GetValue(client);
                Assert.AreEqual(123456.0, anchor, 2.0, "the welcome's tick anchors the clock at the new rate");
            }
            finally
            {
                typeof(NebulaClient).GetField("_transport", Hidden).SetValue(client, null);
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(config);
                NebulaRuntime.LocalClientId = 0;
                NebulaRuntime.LocalIdentity = "";
            }
        }

        /// <summary>
        /// Prediction at 30 Hz: an owner and its worker step the same walker one 33 ms tick at a time, at 300 m/s (a ship's
        /// speed) with a turn, and agree on every tick, so nothing is corrected, and it covers as much ground per second as
        /// at 60 Hz. A server event posted on the worker reaches the owner's replay within the shorter repeat window.
        /// </summary>
        [TestCase(60)]
        [TestCase(30)]
        public void AFastWalkerIsPredictedWithoutCorrectionsAtEitherRate(int rate)
        {
            NetworkTime.Configure(rate);
            int lag = NetworkTime.SecondsToTicks(0.1);
            using var rig = new PredictedPairRig(lag);
            rig.Client.Hands = new Vector3(300f, 0f, 0f);
            rig.Client.TurnDegreesPerTick = rig.Worker.TurnDegreesPerTick = 90f / rate;
            rig.Client.ReportVelocity = rig.Worker.ReportVelocity = true;
            rig.Advance(rate); // one second
            Assert.AreEqual(0, rig.Client.Corrections, "owner and worker agree tick for tick");
            float travelled = rig.Worker.Identity.LocalPosition.magnitude;
            Assert.That(travelled, Is.GreaterThan(150f).And.LessThan(300f), "the worker covered most of a second's ground at 300 m/s, curving");
            uint posted = 0;
            rig.AfterWorkerTick = s => { if (posted == 0) { posted = s; rig.Worker.PostServerEvent(new ShoveEvent { Velocity = new Vector3(0f, 0f, 2f) }); } };
            rig.Advance(lag + 3);
            Assert.That(rig.Worker.ShovesSeen.Count, Is.EqualTo(1), "the worker applied it once");
            Assert.That(rig.Client.ShovesSeen.Exists(x => x.Replaying), Is.True, "the owner's replay applied it");
            Assert.That((rig.Client.SimulatedPosition - rig.Worker.Identity.LocalPosition).magnitude, Is.LessThan(300f * lag / rate + 1f),
                "the owner is no further from the worker than the link's lead");
        }
    }
}
