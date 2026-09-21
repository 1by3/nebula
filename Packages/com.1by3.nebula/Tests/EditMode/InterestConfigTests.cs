using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The config is where a game meets interest management, so the mapping from its fields to
    /// <see cref="InterestSettings"/> and the grid must be exactly what the inspector says.
    /// </summary>
    public class InterestConfigTests
    {
        private static NebulaConfig Config()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            config.GameScene = "";
            return config;
        }

        [Test]
        public void TheShippedConfigMatchesTheShippedDefaults()
        {
            var config = Config();
            var settings = config.RawInterestSettings();
            var defaults = InterestSettings.Default;
            Assert.AreEqual(defaults.Radius, settings.Radius);
            Assert.AreEqual(defaults.ExitMargin, settings.ExitMargin);
            Assert.AreEqual(defaults.CellSize, settings.CellSize);
            Assert.AreEqual(defaults.Planar, settings.Planar);
            Assert.AreEqual(defaults.EvalHz, settings.EvalHz);
            Assert.AreEqual(defaults.MaxRadius, settings.MaxRadius);
            Assert.AreEqual(defaults.HintMaxDistance, settings.HintMaxDistance);
            Assert.AreEqual(defaults.PartitionWarnEntities, settings.PartitionWarnEntities);
            var issues = new List<ConfigIssue>();
            config.Validate(issues);
            CollectionAssert.IsEmpty(issues, "a fresh config must not warn");
            Object.DestroyImmediate(config);
        }

        [Test]
        public void ResolvedSettingsAreClampedEvenWhenNobodyReadsTheIssues()
        {
            var config = Config();
            config.InterestMaxRadius = 1f;
            config.InterestFarRadius = 5000f;
            var settings = config.ToInterestSettings();
            Assert.AreEqual(config.InterestRadius, settings.MaxRadius);
            Assert.AreEqual(config.InterestRadius, settings.FarRadius);
            Object.DestroyImmediate(config);
        }

        [Test]
        public void ValidationReportsTheFieldByItsInspectorName()
        {
            var config = Config();
            config.InterestRadius = 0f;
            var issues = new List<ConfigIssue>();
            config.Validate(issues);
            Assert.IsTrue(issues.Exists(i => i.Field == "InterestRadius" && i.Severity == ConfigSeverity.Error));
            Object.DestroyImmediate(config);
        }

        [Test]
        public void TheGridIsSnappedToABakedWorldsCells()
        {
            var config = Config();
            var world = ScriptableObject.CreateInstance<Nebula.World.WorldDefinition>();
            world.CellSize = new Vector3(256, 256, 256);
            var manifest = ScriptableObject.CreateInstance<WorldContainerManifest>();
            manifest.World = world;
            config.WorldManifest = manifest;
            config.InterestCellSize = 64f;
            var grid = config.ToInterestGrid();
            Assert.AreEqual(64, grid.EdgeX, 1e-6);
            Assert.AreEqual(-128, grid.OffsetX, 1e-6, "baked cells are centred on their coordinate");
            Assert.IsTrue(grid.Planar);
            Object.DestroyImmediate(manifest);
            Object.DestroyImmediate(world);
            Object.DestroyImmediate(config);
        }

        [Test]
        public void TheGridOfARuntimeWorldStartsAtTheCellCoordinate()
        {
            var config = Config();
            var world = ScriptableObject.CreateInstance<Nebula.World.WorldDefinition>();
            world.CellSize = new Vector3(96, 96, 96);
            config.RuntimeWorld = world;
            config.InterestCellSize = 48f;
            var grid = config.ToInterestGrid();
            Assert.AreEqual(48, grid.EdgeX, 1e-6);
            Assert.AreEqual(0, grid.OffsetX, 1e-6);
            Object.DestroyImmediate(world);
            Object.DestroyImmediate(config);
        }

        [Test]
        public void ContentStreamingIsRaisedForAWorldWhoseCellsAreSmallerThanTheRadius()
        {
            var config = Config();
            var world = ScriptableObject.CreateInstance<Nebula.World.WorldDefinition>();
            world.CellSize = new Vector3(64, 64, 64);
            config.RuntimeWorld = world;
            config.ClientLoadRadiusCells = 1;
            var issues = new List<ConfigIssue>();
            config.Validate(issues);
            Assert.IsTrue(issues.Exists(i => i.Field == nameof(NebulaConfig.ClientLoadRadiusCells)));
            Assert.AreEqual(3, config.ToInterestSettings().ClientLoadRadiusCells);
            Assert.AreEqual(3, config.ToInterestSettings().NearCells(64));
            Object.DestroyImmediate(world);
            Object.DestroyImmediate(config);
        }
    }
}
