using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula.Tests
{
    public class ServiceExportTests
    {
        [Test]
        public void PartitionedExportKeepsManifestOrder()
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            string folder = "Assets/NebulaServiceExportTest_" + Guid.NewGuid().ToString("N");
            string output = Path.Combine("Builds", "ServiceWorldFixture");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            try
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var world = ScriptableObject.CreateInstance<Nebula.World.WorldDefinition>();
                world.CellSize = new Vector3(100, 100, 100);
                AssetDatabase.CreateAsset(world, folder + "/World.asset");
                var manifest = ScriptableObject.CreateInstance<WorldContainerManifest>();
                manifest.World = world;
                manifest.Entries.Add(new WorldContainerManifest.Entry { Id = "z-first", Cell = new Vector3Int(2, 0, 0), IsCell = true, Size = world.CellSize });
                manifest.Entries.Add(new WorldContainerManifest.Entry { Id = "a-second", Cell = new Vector3Int(2, 0, 0), LocalPosition = new Vector3(10, 0, 0), Size = new Vector3(10, 10, 10) });
                AssetDatabase.CreateAsset(manifest, folder + "/Containers.asset");
                var config = ScriptableObject.CreateInstance<NebulaConfig>();
                config.GameScene = "";
                config.WorldManifest = manifest;
                AssetDatabase.CreateAsset(config, folder + "/Config.asset");
                new GameObject("Bootstrap").AddComponent<NebulaBootstrap>().Config = config;
                string path = folder + "/Game.unity";
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, path));
                var exporter = typeof(Nebula.Editor.NebulaBuild).Assembly.GetType("Nebula.Editor.ServiceExport", true);
                exporter.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { output, new[] { path } });
                string json = File.ReadAllText(Path.Combine(output, "nebula-services.json"));
                StringAssert.Contains("\"Partitioned\": true", json);
                Assert.Less(json.IndexOf("z-first", StringComparison.Ordinal), json.IndexOf("a-second", StringComparison.Ordinal));
                StringAssert.Contains("210.0", json);
            }
            finally
            {
                if (setup.Any(s => s.isLoaded && s.isActive)) EditorSceneManager.RestoreSceneManagerSetup(setup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(folder);
            }
        }
        [Test]
        public void SavedSceneExportMatchesRegistryOrderAndTransforms()
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            string folder = "Assets/NebulaServiceExportTest_" + Guid.NewGuid().ToString("N");
            string output = Path.Combine("Builds", "ServiceFixture");
            Directory.CreateDirectory(output);
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            try
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var config = ScriptableObject.CreateInstance<NebulaConfig>();
                config.GameScene = "";
                config.InterestFarDivisor = 17;
                AssetDatabase.CreateAsset(config, folder + "/Config.asset");
                var bootstrap = new GameObject("Bootstrap").AddComponent<NebulaBootstrap>();
                bootstrap.Config = config;
                var a = new GameObject("A").AddComponent<Container>(); a.ContainerId = "a";
                a.transform.position = new Vector3(4, 8, 12);
                a.transform.rotation = Quaternion.Euler(20, 70, 15);
                a.transform.localScale = new Vector3(2, 3, 4);
                var b = new GameObject("B").AddComponent<Container>(); b.ContainerId = "b";
                b.transform.position = new Vector3(100, 0, 0);
                string path = folder + "/Game.unity";
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, path));
                var exporter = typeof(Nebula.Editor.NebulaBuild).Assembly.GetType("Nebula.Editor.ServiceExport", true);
                exporter.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { output, new[] { path } });
                string json = File.ReadAllText(Path.Combine(output, "nebula-services.json"));
                StringAssert.Contains("\"InterestFarDivisor\": 17", json);
                Assert.Less(json.IndexOf("\"ContainerId\": \"a\"", StringComparison.Ordinal), json.IndexOf("\"ContainerId\": \"b\"", StringComparison.Ordinal));
                StringAssert.Contains("\"Matrix\"", json);
                StringAssert.Contains("\"Index\": 0", json);
                StringAssert.Contains("\"Index\": 1", json);
            }
            finally
            {
                if (setup.Any(s => s.isLoaded && s.isActive)) EditorSceneManager.RestoreSceneManagerSetup(setup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
