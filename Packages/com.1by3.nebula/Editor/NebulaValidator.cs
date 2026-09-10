using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nebula.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula.Editor
{
    /// <summary>
    /// Nebula &gt; Validate Project: checks the things that otherwise only show up as a broken build or a silent
    /// mesh. The config asset, the boot and game scenes in the build settings, the game mode, containers, the network
    /// prefab table, a partitioned world's manifest and cell scenes, static batching in cells and scene entities
    /// waiting for an id. Each issue is logged with its object as context, so clicking the log line selects it.
    /// </summary>
    public static class NebulaValidator
    {
        public enum Severity { Info, Warning, Error }

        public readonly struct Issue
        {
            public readonly Severity Severity;
            public readonly string Message;
            public readonly Object Context;

            public Issue(Severity severity, string message, Object context = null)
            {
                Severity = severity;
                Message = message;
                Context = context;
            }

            public override string ToString() => $"{Severity}: {Message}";
        }

        [MenuItem("Nebula/Validate Project", priority = -98)]
        public static void ValidateMenu()
        {
            var issues = Validate();
            foreach (var i in issues)
            {
                string line = $"[nebula] validate: {i.Message}";
                if (i.Severity == Severity.Error) Debug.LogError(line, i.Context);
                else if (i.Severity == Severity.Warning) Debug.LogWarning(line, i.Context);
                else Debug.Log(line, i.Context);
            }
            int errors = issues.Count(i => i.Severity == Severity.Error), warnings = issues.Count(i => i.Severity == Severity.Warning);
            Debug.Log($"[nebula] validate: {errors} error(s), {warnings} warning(s)");
            if (Application.isBatchMode) return;

            var shown = issues.Where(i => i.Severity != Severity.Info).ToList();
            if (shown.Count == 0)
            {
                EditorUtility.DisplayDialog("Validate Project", "No problems found.", "OK");
                return;
            }
            string body = string.Join("\n", shown.Take(12).Select(i => (i.Severity == Severity.Error ? "✖ " : "▲ ") + i.Message));
            if (shown.Count > 12) body += $"\n... and {shown.Count - 12} more in the Console";
            if (EditorUtility.DisplayDialog($"Validate Project: {errors} error(s), {warnings} warning(s)", body, "Run Set Up Active Scene", "Close"))
                NebulaSetup.SetUpActiveSceneMenu();
        }

        public static List<Issue> Validate()
        {
            var issues = new List<Issue>();
            var config = NebulaSetup.FindConfig();
            if (config == null)
            {
                issues.Add(new Issue(Severity.Error, "no Resources/NebulaConfig asset: every role runs on defaults with no game scene and no prefabs (Nebula > Set Up Active Scene creates it)"));
                return issues;
            }
            var inResources = AssetDatabase.FindAssets("t:NebulaConfig").Select(AssetDatabase.GUIDToAssetPath).Where(p => p.Contains("/Resources/")).ToList();
            if (inResources.Count > 1)
                issues.Add(new Issue(Severity.Warning, $"{inResources.Count} NebulaConfig assets in Resources folders ({string.Join(", ", inResources)}); roles may not agree on which one loads", config));

            CheckScenes(config, issues);
            CheckPrefabs(config, issues);
            if (config.WorldManifest != null) CheckWorld(config, issues);
            CheckSceneEntities(issues);
            return issues;
        }

        private static void CheckScenes(NebulaConfig config, List<Issue> issues)
        {
            var build = EditorBuildSettings.scenes.Where(s => s.enabled).ToList();
            if (build.Count == 0)
            {
                issues.Add(new Issue(Severity.Error, "no enabled scenes in the build settings"));
                return;
            }
            string boot = build[0].path;
            if (!NebulaSetup.SceneContains<NebulaBootstrap>(boot))
                issues.Add(new Issue(Severity.Error, $"the first build scene {boot} has no NebulaBootstrap, so a player boots without Nebula", AssetDatabase.LoadAssetAtPath<SceneAsset>(boot)));

            if (string.IsNullOrEmpty(config.GameScene))
            {
                issues.Add(new Issue(Severity.Error, "NebulaConfig.GameScene is empty", config));
                return;
            }
            var game = build.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s.path) == config.GameScene);
            if (game == null)
            {
                issues.Add(new Issue(Severity.Error, $"GameScene '{config.GameScene}' is not an enabled scene in the build settings", config));
                return;
            }
            if (!NebulaSetup.SceneContains<NebulaGameMode>(game.path) && !NebulaSetup.SceneContains<NebulaGameMode>(boot))
                issues.Add(new Issue(Severity.Warning, $"no NebulaGameMode in '{config.GameScene}': workers cannot spawn players", AssetDatabase.LoadAssetAtPath<SceneAsset>(game.path)));

            if (config.WorldManifest != null) return;
            var open = SceneManager.GetSceneByPath(game.path);
            if (!open.IsValid() || !open.isLoaded)
            {
                if (!NebulaSetup.SceneContains<Container>(game.path))
                    issues.Add(new Issue(Severity.Error, $"'{config.GameScene}' has no Container volumes: nothing can be leased to a worker"));
                return;
            }
            var containers = NebulaSetup.FindInScene<Container>(open);
            if (containers.Count == 0)
                issues.Add(new Issue(Severity.Error, $"'{config.GameScene}' has no Container volumes: nothing can be leased to a worker"));
            else if (containers.Count == 1)
                issues.Add(new Issue(Severity.Info, $"'{config.GameScene}' has a single container, so one worker simulates everything", containers[0]));
            foreach (var dup in containers.GroupBy(c => c.ContainerId).Where(g => g.Count() > 1))
                issues.Add(new Issue(Severity.Error, $"container id '{dup.Key}' is used by {dup.Count()} containers; ids must be unique", dup.First()));
        }

        private static void CheckPrefabs(NebulaConfig config, List<Issue> issues)
        {
            var list = config.NetworkPrefabs;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null) issues.Add(new Issue(Severity.Warning, $"NetworkPrefabs[{i}] is empty", config));
                else if (list[i].GetComponent<NetworkIdentity>() == null) issues.Add(new Issue(Severity.Error, $"NetworkPrefabs[{i}] '{list[i].name}' has no NetworkIdentity on its root", list[i]));
            }
            foreach (var dup in list.Where(p => p != null).GroupBy(p => p).Where(g => g.Count() > 1))
                issues.Add(new Issue(Severity.Warning, $"'{dup.Key.name}' is listed {dup.Count()} times in NetworkPrefabs; it spawns with the last id", dup.Key));
            var listed = new HashSet<GameObject>(list.Where(p => p != null));
            var missing = NebulaSetup.NetworkPrefabCandidates().Where(p => !listed.Contains(p)).ToList();
            if (missing.Count > 0)
                issues.Add(new Issue(Severity.Warning, $"{missing.Count} prefab(s) with a NetworkIdentity are not in NetworkPrefabs ({string.Join(", ", missing.Take(8).Select(p => p.name))}{(missing.Count > 8 ? ", ..." : "")}); Nebula > Network Prefabs > Register All Network Prefabs", missing[0]));
        }

        private static void CheckWorld(NebulaConfig config, List<Issue> issues)
        {
            var manifest = config.WorldManifest;
            var world = manifest.World;
            if (world == null)
            {
                issues.Add(new Issue(Severity.Error, "the world manifest has no WorldDefinition", manifest));
                return;
            }
            if (manifest.Entries.Count == 0)
            {
                issues.Add(new Issue(Severity.Error, $"world manifest {manifest.name} has not been baked (Nebula > World > Bake Container Manifest)", manifest));
                return;
            }
            var baked = new HashSet<Vector3Int>(manifest.Entries.Where(e => e.IsCell).Select(e => e.Cell));
            var defined = new HashSet<Vector3Int>(world.Cells.Select(c => c.Coord));
            if (!baked.SetEquals(defined))
                issues.Add(new Issue(Severity.Warning, $"the manifest's cells differ from world '{world.WorldName}' ({baked.Count} baked, {defined.Count} defined); rebake", manifest));

            string manifestPath = AssetDatabase.GetAssetPath(manifest);
            var bakedAt = File.Exists(manifestPath) ? File.GetLastWriteTimeUtc(manifestPath) : System.DateTime.MinValue;
            var enabled = new HashSet<string>(EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path));
            var stale = new List<string>();
            foreach (var cell in world.Cells)
            {
                var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(cell.ScenePath);
                if (string.IsNullOrEmpty(cell.ScenePath) || !File.Exists(cell.ScenePath))
                {
                    issues.Add(new Issue(Severity.Error, $"cell {cell.Coord}: scene '{cell.ScenePath}' is missing", world));
                    continue;
                }
                if (!enabled.Contains(cell.ScenePath))
                    issues.Add(new Issue(Severity.Error, $"cell {cell.Coord}: {cell.ScenePath} is not enabled in the build settings", asset));
                if (File.GetLastWriteTimeUtc(cell.ScenePath) > bakedAt) stale.Add(cell.SceneName);

                var scene = SceneManager.GetSceneByPath(cell.ScenePath);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                var batched = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                    .Where(t => (GameObjectUtility.GetStaticEditorFlags(t.gameObject) & StaticEditorFlags.BatchingStatic) != 0).ToList();
                if (batched.Count > 0)
                    issues.Add(new Issue(Severity.Warning, $"cell {cell.Coord}: {batched.Count} object(s) are Batching Static; static batching bakes positions and cells move at runtime", batched[0].gameObject));
            }
            if (stale.Count > 0)
                issues.Add(new Issue(Severity.Warning, $"cell scene(s) saved after the last bake ({string.Join(", ", stale.Take(6))}{(stale.Count > 6 ? ", ..." : "")}); rebake if containers or cells changed", manifest));

            var hub = SceneManager.GetSceneByName(config.GameScene);
            if (hub.IsValid() && hub.isLoaded && !WorldAssets.TryGetCellOf(hub, out _, out _))
            {
                var hubContainers = NebulaSetup.FindInScene<Container>(hub);
                if (hubContainers.Count > 0)
                    issues.Add(new Issue(Severity.Warning, $"{hubContainers.Count} Container(s) in the hub scene are ignored in a partitioned world; move them into a cell", hubContainers[0]));
            }
        }

        private static void CheckSceneEntities(List<Issue> issues)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                var unassigned = NebulaSetup.FindInScene<NetworkIdentity>(scene).Where(id => id.SceneId == 0).ToList();
                if (unassigned.Count > 0)
                    issues.Add(new Issue(Severity.Warning, $"'{scene.name}': {unassigned.Count} scene entit(ies) have no SceneId yet; save the scene to assign them", unassigned[0]));
            }
        }
    }
}
