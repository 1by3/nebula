using System.Collections.Generic;
using System.IO;
using Nebula.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The Play Mode Scenario that starts the dev loop's server (NEB-349): which scenario the Editor picks, and when
    /// it makes that scenario the active one. Tested without Multiplayer Play Mode.
    /// </summary>
    public sealed class DevLoopScenarioTests
    {
        private const NebulaEditorRunMode Mppm = NebulaEditorRunMode.MultiplayerPlayMode;

        private static ScenarioEditorInstance Main(string tag = "") => new ScenarioEditorInstance("Main Editor", true, 0, tag, false);
        private static ScenarioEditorInstance Clone(int index, string tag) => new ScenarioEditorInstance($"Player {index + 1}", false, index, tag, false);

        private static KeyValuePair<string, IReadOnlyList<ScenarioEditorInstance>> Scenario(string path, params ScenarioEditorInstance[] instances) =>
            new KeyValuePair<string, IReadOnlyList<ScenarioEditorInstance>>(path, instances);

        [Test]
        public void NoScenarioHostsTheServerWithoutAServerTag()
        {
            var candidates = new[] { Scenario("Assets/Settings/PlayMode/Two Clients.asset", Main(), Clone(1, "Client"), Clone(2, "")) };
            Assert.IsNull(DevLoopScenarioRules.Choose(candidates, out var warning));
            Assert.IsNull(warning);
            Assert.IsNull(DevLoopScenarioRules.Choose(null, out _));
        }

        [Test]
        public void TheOneScenarioWithAServerInstanceIsChosen()
        {
            var candidates = new[]
            {
                Scenario("Assets/Settings/PlayMode/Clients.asset", Main(), Clone(1, "Client")),
                Scenario("Assets/Settings/PlayMode/Dev Loop.asset", Main(), Clone(1, " server ")),
            };
            Assert.AreEqual("Assets/Settings/PlayMode/Dev Loop.asset", DevLoopScenarioRules.Choose(candidates, out var warning), "the tag matches in any case, trimmed");
            Assert.IsNull(warning);
        }

        [Test]
        public void SeveralServerScenariosPickTheFirstPathAndWarn()
        {
            var candidates = new[]
            {
                Scenario("Assets/Settings/PlayMode/b.asset", Main(), Clone(1, "Server")),
                Scenario("Assets/Settings/PlayMode/B.asset", Main("Server"), Clone(1, "Client")),
                Scenario("Assets/Settings/PlayMode/a.asset", Main(), Clone(2, "Server")),
            };
            Assert.AreEqual("Assets/Settings/PlayMode/B.asset", DevLoopScenarioRules.Choose(candidates, out var warning), "ordinal order, the same in every Editor");
            StringAssert.Contains("3 Play Mode Scenarios", warning);
            StringAssert.Contains("using Assets/Settings/PlayMode/B.asset", warning);
        }

        [Test]
        public void TheServerInstanceIsTheFirstTaggedServer()
        {
            Assert.AreEqual(1, DevLoopScenarioRules.ServerInstance(new[] { Main(), Clone(1, "Server"), Clone(2, "Server") }));
            Assert.AreEqual(0, DevLoopScenarioRules.ServerInstance(new[] { Main("SERVER"), Clone(1, "Client") }), "a main Editor tagged Server hosts");
            Assert.AreEqual(-1, DevLoopScenarioRules.ServerInstance(new[] { Main(), Clone(1, "Client") }));
            Assert.AreEqual(-1, DevLoopScenarioRules.ServerInstance(null));
        }

        [Test]
        public void SwitchesOnlyFromDefaultWhenNothingRuns()
        {
            Assert.IsTrue(DevLoopScenarioRules.ShouldSwitch(Mppm, isMainEditor: true, activeIsDefault: true, scenarioIdle: true, enteringOrPlaying: false, virtualPlayerActive: false, haveScenario: true));

            Assert.IsFalse(DevLoopScenarioRules.ShouldSwitch(NebulaEditorRunMode.Mesh, true, true, true, false, false, true), "Mesh never switches");
            Assert.IsFalse(DevLoopScenarioRules.ShouldSwitch(Mppm, false, true, true, false, false, true), "a virtual player never switches");
            Assert.IsFalse(DevLoopScenarioRules.ShouldSwitch(Mppm, true, false, true, false, false, true), "a scenario someone chose stays");
            Assert.IsFalse(DevLoopScenarioRules.ShouldSwitch(Mppm, true, true, false, false, false, true), "not while a scenario starts or runs");
            Assert.IsFalse(DevLoopScenarioRules.ShouldSwitch(Mppm, true, true, true, true, false, true), "not while entering Play: the Play button has already read the scenario");
            Assert.IsFalse(DevLoopScenarioRules.ShouldSwitch(Mppm, true, true, true, false, true, true), "switching would stop the running virtual player");
            Assert.IsFalse(DevLoopScenarioRules.ShouldSwitch(Mppm, true, true, true, false, null, true), "unknown players: leave it");
            Assert.IsFalse(DevLoopScenarioRules.ShouldSwitch(Mppm, true, true, true, false, false, false), "no scenario to switch to");
        }

#if UNITY_6000_6_OR_NEWER
        /// <summary>
        /// Create Scenario reaches into Unity's internal scenario types; this fails when a Unity upgrade moves them,
        /// and checks that the Editor reads back what it wrote: an Editor instance in Player 2's slot tagged Server.
        /// </summary>
        [Test]
        public void CreateScenarioWritesAServerInstanceTheEditorFinds()
        {
            string folder = "Assets/Settings/PlayMode";
            bool hadSettings = AssetDatabase.IsValidFolder("Assets/Settings"), hadFolder = AssetDatabase.IsValidFolder(folder);
            string path = null;
            try
            {
                var scenario = DevLoopScenario.CreateScenarioAsset();
                path = AssetDatabase.GetAssetPath(scenario);
                StringAssert.StartsWith(folder + "/Nebula Dev Loop", path);

                var instances = DevLoopScenario.ReadEditorInstances(scenario);
                Assert.IsNotNull(instances);
                Assert.AreEqual(2, instances.Count, "the main Editor and the server");
                Assert.IsTrue(instances[0].IsMainEditor);
                Assert.IsFalse(instances[0].HostsServer, "the main Editor is untagged, so it is the client");
                Assert.AreEqual(1, DevLoopScenarioRules.ServerInstance(instances));
                Assert.AreEqual(1, instances[1].PlayerIndex, "Player 2");
                Assert.IsFalse(instances[1].ManualControl, "Scenario Control: Play starts it");
                Assert.AreEqual(path, DevLoopScenario.FindDevLoopScenario(out _), "found among the project's scenarios");
            }
            finally
            {
                if (path != null) AssetDatabase.DeleteAsset(path);
                if (!hadFolder && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if (!hadSettings && AssetDatabase.IsValidFolder("Assets/Settings")) AssetDatabase.DeleteAsset("Assets/Settings");
            }
        }
#endif
    }
}
