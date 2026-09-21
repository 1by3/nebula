using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// Validation (design §9) exists so the relationships between these numbers are enforced instead of
    /// documented: a mesh whose streaming radius does not cover its interest radius spawns entities standing on
    /// geometry the client does not have, and nobody would guess that from the symptom.
    /// </summary>
    public class InterestSettingsTests
    {
        private static InterestSettings Validate(InterestSettings settings, out List<ConfigIssue> issues, float worldCell = 0, float ghostBand = 0)
        {
            issues = new List<ConfigIssue>();
            return settings.Validate(issues, worldCell, ghostBand);
        }

        private static bool Has(List<ConfigIssue> issues, ConfigSeverity severity, string field) =>
            issues.Exists(i => i.Severity == severity && i.Field == field);

        [Test]
        public void TheDefaultsAreConsistent()
        {
            var effective = Validate(InterestSettings.Default, out var issues);
            CollectionAssert.IsEmpty(issues, "the shipped defaults must not warn");
            Assert.AreEqual(InterestSettings.Default.Radius, effective.Radius);
            Assert.AreEqual(136f, effective.ExitRadius);
            Assert.AreEqual(168f, effective.SubscribeRadius);
        }

        [Test]
        public void ANonPositiveSizeIsAnErrorAndFallsBackToTheDefault()
        {
            var settings = InterestSettings.Default;
            settings.Radius = 0;
            settings.CellSize = -1;
            var effective = Validate(settings, out var issues);
            Assert.IsTrue(Has(issues, ConfigSeverity.Error, "InterestRadius"));
            Assert.IsTrue(Has(issues, ConfigSeverity.Error, "InterestCellSize"));
            Assert.AreEqual(InterestSettings.Default.Radius, effective.Radius);
            Assert.AreEqual(InterestSettings.Default.CellSize, effective.CellSize);
        }

        [Test]
        public void RateTiersAreKeptInsideTheSet()
        {
            var settings = InterestSettings.Default;
            settings.NearRadius = 200;
            settings.FarRadius = 50;
            var effective = Validate(settings, out var issues);
            Assert.IsTrue(Has(issues, ConfigSeverity.Warning, "InterestFarRadius"));
            Assert.LessOrEqual(effective.NearRadius, effective.FarRadius);
            Assert.LessOrEqual(effective.FarRadius, effective.Radius);
        }

        [Test]
        public void MaxRadiusIsRaisedToTheRadiusItMustContain()
        {
            var settings = InterestSettings.Default;
            settings.MaxRadius = 10;
            var effective = Validate(settings, out var issues);
            Assert.IsTrue(Has(issues, ConfigSeverity.Warning, "InterestMaxRadius"));
            Assert.AreEqual(effective.Radius, effective.MaxRadius);
        }

        [Test]
        public void MaxRadiusIsNeverUncapped()
        {
            // It is the ceiling on what a prefab's RelevanceRadius may ask a gateway to send, so "0 = off" would
            // make a security and cost boundary opt-in. A non-positive value means the mesh radius, and says so.
            foreach (float value in new[] { 0f, -1f })
            {
                var settings = InterestSettings.Default;
                settings.MaxRadius = value;
                var effective = Validate(settings, out var issues);
                Assert.IsTrue(Has(issues, ConfigSeverity.Warning, "InterestMaxRadius"), $"{value} should be reported");
                Assert.AreEqual(effective.Radius, effective.MaxRadius, $"{value} should fall back to InterestRadius");
            }
        }

        [Test]
        public void TheSubscribeMarginMustCoverOneEvaluationOfTravel()
        {
            var settings = InterestSettings.Default;
            settings.MaxFocusSpeed = 40;
            settings.EvalHz = 4;
            settings.SubscribeMargin = 1;
            var effective = Validate(settings, out var issues);
            Assert.IsTrue(Has(issues, ConfigSeverity.Warning, "InterestSubscribeMargin"));
            Assert.AreEqual(10f, effective.SubscribeMargin, 1e-4, "40 m/s for a quarter second");
        }

        [Test]
        public void ACellSizeThatDoesNotDivideTheWorldCellIsSnappedAndWarned()
        {
            var settings = InterestSettings.Default;
            settings.CellSize = 50;
            var effective = Validate(settings, out var issues, worldCell: 128);
            Assert.IsTrue(Has(issues, ConfigSeverity.Warning, "InterestCellSize"));
            Assert.AreEqual(128f / 3, effective.CellSize, 1e-3);
        }

        [Test]
        public void AGhostBandWiderThanARegionIsWarnedAbout()
        {
            var settings = InterestSettings.Default;
            settings.CellSize = 8;
            Validate(settings, out var issues, ghostBand: 16);
            Assert.IsTrue(Has(issues, ConfigSeverity.Warning, "GhostBandMargin"));
        }

        [Test]
        public void ContentStreamingIsRaisedToCoverTheInterestRadius()
        {
            var settings = InterestSettings.Default;   // 120 + 16 exit
            settings.ClientLoadRadiusCells = 1;
            var effective = Validate(settings, out var issues, worldCell: 64);
            Assert.IsTrue(Has(issues, ConfigSeverity.Warning, "ClientLoadRadiusCells"));
            Assert.AreEqual(3, effective.ClientLoadRadiusCells, "136 m needs three 64 m cells");
        }

        [Test]
        public void NearCellsIsTheLargerOfTheConfiguredWindowAndWhatInterestNeeds()
        {
            var settings = InterestSettings.Default;
            settings.ClientLoadRadiusCells = 5;
            Assert.AreEqual(5, settings.NearCells(64), "a game that wants more content than interest needs keeps it");
            settings.ClientLoadRadiusCells = 1;
            Assert.AreEqual(3, settings.NearCells(64));
            Assert.AreEqual(1, settings.NearCells(0), "no world definition, no cells to count");
        }

        [Test]
        public void DivisorsAndCountsAreClampedToUsableValues()
        {
            var settings = InterestSettings.Default;
            settings.MidDivisor = 0;
            settings.FarDivisor = -3;
            settings.MaxFoci = 0;
            settings.EvalHz = 0;
            settings.ExitMargin = 0;
            var effective = Validate(settings, out var issues);
            Assert.AreEqual(1, effective.MidDivisor);
            Assert.AreEqual(1, effective.FarDivisor);
            Assert.AreEqual(1, effective.MaxFoci);
            Assert.AreEqual(InterestSettings.Default.EvalHz, effective.EvalHz);
            Assert.AreEqual(InterestSettings.Default.ExitMargin, effective.ExitMargin);
            Assert.IsTrue(Has(issues, ConfigSeverity.Warning, "InterestExitMargin"));
        }
    }
}
