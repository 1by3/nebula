using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Interest for fast movers: a speed floor on the gateway's update rate (<see cref="RelevanceTiers.CapForSpeed"/>,
    /// <see cref="InterestSettings.MaxTravelPerUpdate"/>) and a lead focus ahead of a moving pawn (<see cref="InterestSettings.LeadOffset"/>).
    /// </summary>
    public sealed class FastMoverInterestTests
    {
        private static float Tick => NetworkTime.TickInterval;

        [Test]
        public void AFastEntityIsSentOftenEnoughThatItNeverTravelsMoreThanTheLimitBetweenUpdates()
        {
            Assert.AreEqual(12, RelevanceTiers.CapForSpeed(12, 5f, 4f, Tick), "a walker keeps its far tier: 48 ticks of travel fit in 4 m");
            Assert.AreEqual(4, RelevanceTiers.CapForSpeed(12, 60f, 4f, Tick), "60 m/s: 1 m a tick, every 4th tick");
            Assert.AreEqual(1, RelevanceTiers.CapForSpeed(12, 400f, 4f, Tick), "400 m/s: every tick");
            Assert.AreEqual(4, RelevanceTiers.CapForSpeed(4, 30f, 4f, Tick), "never raised above the tier");
            for (float speed = 1f; speed < 2000f; speed *= 1.37f)
            {
                int d = RelevanceTiers.CapForSpeed(12, speed, 4f, Tick);
                Assert.That(d, Is.InRange(1, 12));
                if (d > 1) Assert.That(speed * Tick * d, Is.LessThanOrEqualTo(4.0001f), $"{speed} m/s every {d} ticks");
            }
        }

        [Test]
        public void QuietStaysQuietAndZeroTurnsItOff()
        {
            Assert.AreEqual(RelevanceTiers.Quiet, RelevanceTiers.CapForSpeed(RelevanceTiers.Quiet, 400f, 4f, Tick), "a band that chose no updates keeps that choice");
            Assert.AreEqual(12, RelevanceTiers.CapForSpeed(12, 400f, 0f, Tick));
            Assert.AreEqual(12, RelevanceTiers.CapForSpeed(12, 0f, 4f, Tick));
        }

        [Test]
        public void ALeadFocusGoesHalfASecondAheadOfAFastPawnOnly()
        {
            var settings = InterestSettings.Default;
            Assert.AreEqual(0.5f, settings.FocusLeadSeconds);
            Assert.AreEqual(4f, settings.MaxTravelPerUpdate);
            var walking = settings.LeadOffset(new Double3(6, 0, 0));
            Assert.AreEqual(new Double3(0, 0, 0), walking, "3 m ahead is inside the exit margin: no extra focus");
            var flying = settings.LeadOffset(new Double3(0, 0, 400));
            Assert.AreEqual(200.0, flying.Z, 1e-6, "half a second at 400 m/s");
            var planarClimb = settings.LeadOffset(new Double3(100, 300, 0));
            Assert.AreEqual(0.0, planarClimb.Y, "regions are columns in a planar world: height is no lead");
            settings.Planar = false;
            var orbital = settings.LeadOffset(new Double3(5000, 0, 0));
            Assert.AreEqual(settings.MaxRadius, orbital.X, 1e-6, "never further than the widest radius the grid matches");
            settings.FocusLeadSeconds = 0f;
            Assert.AreEqual(new Double3(0, 0, 0), settings.LeadOffset(new Double3(0, 0, 400)), "0 turns it off");
        }

        [Test]
        public void TheConfigCarriesBothSettingsAndValidationRepairsNegatives()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                Assert.AreEqual(4f, config.InterestMaxTravelPerUpdate);
                Assert.AreEqual(0.5f, config.InterestFocusLeadSeconds);
                config.InterestMaxTravelPerUpdate = 2f;
                config.InterestFocusLeadSeconds = 0.75f;
                var raw = config.RawInterestSettings();
                Assert.AreEqual(2f, raw.MaxTravelPerUpdate);
                Assert.AreEqual(0.75f, raw.FocusLeadSeconds);
                config.InterestMaxTravelPerUpdate = -1f;
                config.InterestFocusLeadSeconds = -1f;
                var issues = new List<ConfigIssue>();
                var valid = config.RawInterestSettings().Validate(issues);
                Assert.AreEqual(0f, valid.MaxTravelPerUpdate);
                Assert.AreEqual(0f, valid.FocusLeadSeconds);
                Assert.IsTrue(issues.Exists(i => i.Field == "InterestMaxTravelPerUpdate"));
                Assert.IsTrue(issues.Exists(i => i.Field == "InterestFocusLeadSeconds"));
            }
            finally { Object.DestroyImmediate(config); }
        }
    }
}
