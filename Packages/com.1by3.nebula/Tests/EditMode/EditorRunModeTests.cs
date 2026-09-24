using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The Multiplayer Play Mode dev loop's decision (NEB-243): which Editor process hosts the server, which is a
    /// client, and what that does to the bootstrap's copy of the config. Tested without Multiplayer Play Mode.
    /// </summary>
    public sealed class EditorRunModeTests
    {
        private const NebulaEditorRunMode Mppm = NebulaEditorRunMode.MultiplayerPlayMode;
        private static readonly string[] NoTags = new string[0];
        private const string Root = "C:/Dev/game";

        private static EditorRunPlan Plan(bool isMainEditor, params string[] tags) =>
            EditorRunPlan.Resolve(Mppm, isEditor: true, playModeAvailable: true, isMainEditor, tags, explicitRole: false);

        [Test]
        public void MeshModeIsTodaysBehavior()
        {
            var plan = EditorRunPlan.Resolve(NebulaEditorRunMode.Mesh, true, true, false, NoTags, false);
            Assert.AreEqual(EditorPlayer.Mesh, plan.Player);
            Assert.AreEqual(NebulaRoles.None, plan.Roles);
            Assert.IsNull(plan.Warning);
        }

        [Test]
        public void MainEditorIsTheClientAndAVirtualPlayerHostsTheServer()
        {
            Assert.AreEqual(NebulaRoles.Client, Plan(isMainEditor: true).Roles);
            Assert.AreEqual(NebulaRoles.Worker | NebulaRoles.Gateway | NebulaRoles.Orchestrator, Plan(isMainEditor: false).Roles);
        }

        [Test]
        public void TagsChooseTheSide()
        {
            Assert.AreEqual(EditorPlayer.Client, Plan(false, "client").Player, "a second virtual player can be a second client");
            Assert.AreEqual(EditorPlayer.Server, Plan(true, " Server ").Player);
            Assert.AreEqual(EditorPlayer.Server, Plan(false, "Tester").Player, "an unrelated tag changes nothing");
        }

        [Test]
        public void BuildsAndExplicitRolesAreUnaffected()
        {
            Assert.AreEqual(EditorPlayer.Mesh, EditorRunPlan.Resolve(Mppm, isEditor: false, true, false, NoTags, false).Player);
            Assert.AreEqual(EditorPlayer.Mesh, EditorRunPlan.Resolve(Mppm, true, true, false, NoTags, explicitRole: true).Player);
        }

        [Test]
        public void WithoutMultiplayerPlayModeItFallsBackToMeshWithAWarning()
        {
            var plan = EditorRunPlan.Resolve(Mppm, true, playModeAvailable: false, true, null, false);
            Assert.AreEqual(EditorPlayer.Mesh, plan.Player);
            StringAssert.Contains("Multiplayer Play Mode is not available", plan.Warning);
        }

        [Test]
        public void TheServerRunsTheWholeMeshInOneProcess()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                config.GatewayAddress = "play.example.com";
                Plan(isMainEditor: false).ApplyTo(config, _ => false, Root);
                Assert.IsTrue(config.UseLocalControlPlane);
                Assert.AreEqual("memory", config.DatabaseUrl);
                Assert.AreEqual(1, config.WorkerCount);
                Assert.AreEqual(1, config.MinWorkers);
                Assert.AreEqual(1, config.MaxWorkers);
                Assert.IsFalse(config.AutoScale);
                Assert.AreEqual("127.0.0.1", config.GatewayAddress);
            }
            finally { Object.DestroyImmediate(config); }
        }

        [Test]
        public void ExplicitSwitchesWin()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                config.WorkerCount = 4;
                config.GatewayAddress = "10.0.0.5";
                var given = new HashSet<string> { "nebula-workers", "nebula-gateway" };
                Plan(isMainEditor: false).ApplyTo(config, given.Contains, Root);
                Assert.AreEqual(4, config.WorkerCount);
                Assert.AreEqual("10.0.0.5", config.GatewayAddress);
                Assert.IsTrue(config.UseLocalControlPlane);
            }
            finally { Object.DestroyImmediate(config); }
        }

        [Test]
        public void TheClientOnlyPointsAtThisMachine()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                config.GatewayAddress = "play.example.com";
                Plan(isMainEditor: true).ApplyTo(config, _ => false, Root);
                Assert.AreEqual("127.0.0.1", config.GatewayAddress);
                Assert.IsFalse(config.UseLocalControlPlane);
                Assert.AreEqual(4, config.WorkerCount);
            }
            finally { Object.DestroyImmediate(config); }
        }

        [Test]
        public void TheServerKeepsADevSaveFileOfTheProjectsOwn()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            try
            {
                Plan(isMainEditor: false).ApplyTo(config, _ => false, Root);
                Assert.AreEqual("local", config.PersistenceMode);
                Assert.AreEqual(System.IO.Path.Combine(Root, "Library", "Nebula", "DevSaves", "world.bin"), config.PersistenceLocalFile);

                config.EditorPersistence = NebulaEditorPersistence.InMemory;
                config.PersistenceLocalFile = "";
                Plan(isMainEditor: false).ApplyTo(config, _ => false, Root);
                Assert.AreEqual("memory", config.PersistenceMode);
                Assert.AreEqual("", config.PersistenceLocalFile);

                config.EditorPersistence = NebulaEditorPersistence.DevSaveFile;
                config.PersistenceMode = "auto";
                Plan(isMainEditor: false).ApplyTo(config, key => key == "nebula-persistence-mode", Root);
                Assert.AreEqual("auto", config.PersistenceMode, "-nebula-persistence-mode wins");
            }
            finally { Object.DestroyImmediate(config); }
        }

        [TestCase("C:/Dev/game/Assets", "C:/Dev/game")]
        [TestCase("C:/Dev/game/Library/VP/mppm531add0a/Assets", "C:/Dev/game")]
        [TestCase(@"C:\Dev\game\Library\VP\mppm1\Assets\", "C:/Dev/game")]
        [TestCase("/home/me/game/Assets", "/home/me/game")]
        [TestCase("/home/me/Library/game/Assets", "/home/me/Library/game")]
        public void TheProjectRootIsTheMainProjectsEvenInAVirtualPlayer(string dataPath, string root)
        {
            Assert.AreEqual(root, EditorDevPaths.ProjectRoot(dataPath));
        }

#if UNITY_6000_6_OR_NEWER
        [Test]
        public void ReadingThePlayerNeverThrows()
        {
            // Without the Multiplayer Play Mode package (this project has none) the engine's CurrentPlayer throws;
            // the bootstrap then falls back to Mesh with a warning instead of failing to boot.
            bool read = true, isMainEditor = false;
            Assert.DoesNotThrow(() => read = MultiplayerPlayModePlayer.TryRead(out isMainEditor, out _));
            if (read) Assert.IsTrue(isMainEditor, "the test runner is the main Editor");
        }
#endif
    }
}
