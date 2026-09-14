using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula.Editor
{
    // The exported order is the worker's wire order. Services never discover or instantiate game objects.
    internal static class ServiceExport
    {
        internal static void Publish(string directory, BuildTarget target)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.1by3.nebula/package.json");
            string source = package != null ? package.resolvedPath : Path.GetFullPath("Packages/com.1by3.nebula");
            string rid = target == BuildTarget.StandaloneWindows64 ? "win-x64" : target == BuildTarget.StandaloneLinux64 ? "linux-x64"
                : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            foreach (string role in new[] { "Orchestrator", "Gateway" })
            {
                string project = Path.Combine(source, "Services~", "Nebula." + role, "Nebula." + role + ".csproj");
                string artifacts = Path.Combine(NebulaBuild.ProjectRoot, "Temp", "nebula-services");
                var info = new System.Diagnostics.ProcessStartInfo("dotnet", $"publish \"{project}\" -c Release -r {rid} --self-contained true -o \"{directory}\" --artifacts-path \"{artifacts}\" --nologo")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = System.Diagnostics.Process.Start(info))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    process.WaitForExit();
                    Debug.Log(stdout.GetAwaiter().GetResult());
                    if (process.ExitCode != 0) throw new InvalidOperationException("Service publish failed: " + stderr.GetAwaiter().GetResult());
                }
            }
        }

        [Serializable]
        private sealed class Manifest
        {
            public int Version = 1;
            public bool Partitioned;
            public List<Box> Containers = new List<Box>();
            public WorldData World;
            public List<Schema> Schemas = new List<Schema>();
        }
        [Serializable]
        private sealed class Box
        {
            public string ContainerId;
            public ushort Index;
            public Vector3 Center, Size;
            public Vector3Int Cell;
            public bool IsCell;
            public Frame transform = new Frame();
            public List<string> NeighborIds = new List<string>();
            public Bounds Bounds => new Bounds(transform.position + transform.rotation * Vector3.Scale(transform.lossyScale, Center), Vector3.Scale(Size, new Vector3(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.y), Mathf.Abs(transform.lossyScale.z))));
        }
        [Serializable]
        private sealed class Frame
        {
            public Vector3 position;
            public Quaternion rotation = Quaternion.identity;
            public Vector3 lossyScale = Vector3.one;
            public float[] Matrix;
        }
        [Serializable] private sealed class WorldData { public string WorldName; public Vector3 CellSize; public List<CellData> Cells = new List<CellData>(); }
        [Serializable] private sealed class CellData { public Vector3Int Coord; public string SceneName; }
        [Serializable] private sealed class Schema { public string Name, DisplayName; public ushort PrefabId = ushort.MaxValue; public List<Field> Fields = new List<Field>(); }
        [Serializable]
        private sealed class Field
        {
            public string Name, Type, EnumUnderlyingType;
            public string[] EnumNames, EnumValues;
        }

        internal static void Write(string directory, string[] scenes)
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            // Export only saved scenes, the same ones BuildPipeline reads. Do not discard unsaved editor changes.
            if (setup.Any(s => s.isLoaded && SceneManager.GetSceneByPath(s.path).isDirty))
                throw new InvalidOperationException("Save open scenes before building the service manifest.");
            try
            {
                var boot = EditorSceneManager.OpenScene(scenes[0], OpenSceneMode.Single);
                var bootstrap = boot.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NebulaBootstrap>(true)).FirstOrDefault();
                var config = bootstrap != null && bootstrap.Config != null ? bootstrap.Config : NebulaConfig.Load();
                string game = string.IsNullOrEmpty(config.GameScene) ? scenes[0] : scenes.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s) == config.GameScene || s == config.GameScene);
                if (game == null) throw new InvalidOperationException("NebulaConfig.GameScene must name an enabled build scene.");
                var manifest = new Manifest();
                var scene = game == scenes[0] ? boot : EditorSceneManager.OpenScene(game, OpenSceneMode.Single);
                if (config.WorldManifest != null && config.WorldManifest.World != null)
                {
                    var world = config.WorldManifest.World;
                    manifest.Partitioned = true;
                    manifest.World = new WorldData { WorldName = world.WorldName, CellSize = world.CellSize, Cells = world.Cells.Select(c => new CellData { Coord = c.Coord, SceneName = c.SceneName }).ToList() };
                    foreach (var e in config.WorldManifest.Entries)
                    {
                        var position = Vector3.Scale((Vector3)e.Cell, world.CellSize) + (e.IsCell ? Vector3.zero : e.LocalPosition);
                        var rotation = e.IsCell ? Quaternion.identity : e.LocalRotation;
                        var scale = e.IsCell ? Vector3.one : e.LocalScale;
                        manifest.Containers.Add(new Box { ContainerId = e.Id, Center = e.Center, Size = e.Size, Cell = e.Cell, IsCell = e.IsCell, transform = ExportFrame(Matrix4x4.TRS(position, rotation, scale), position, rotation, scale) });
                    }
                }
                else
                {
                    foreach (var c in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Container>(false)).Where(c => c.gameObject.activeInHierarchy && c.GetComponent<DynamicContainer>() == null && !c.IsRuntime).OrderBy(c => c.ContainerId, StringComparer.Ordinal))
                    {
                        var t = c.transform;
                        manifest.Containers.Add(new Box { ContainerId = c.ContainerId, Center = c.Center, Size = c.Size, transform = ExportFrame(t.localToWorldMatrix, t.position, t.rotation, t.lossyScale) });
                    }
                }
                var ids = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < manifest.Containers.Count; i++)
                {
                    var c = manifest.Containers[i];
                    if (i >= ContainerRef.RuntimeIndex || string.IsNullOrEmpty(c.ContainerId) || !ids.Add(c.ContainerId)) throw new InvalidOperationException("Invalid or duplicate service container ID: " + c.ContainerId);
                    c.Index = (ushort)i;
                }
                // Match the registry's touching-box adjacency, including the grid-cell filter.
                var byCell = manifest.Containers.GroupBy(c => c.Cell).ToDictionary(g => g.Key, g => g.ToList());
                var around = new List<Vector3Int>();
                foreach (var a in manifest.Containers)
                {
                    IEnumerable<Box> candidates = manifest.Containers;
                    if (manifest.Partitioned)
                    {
                        around.Clear();
                        Nebula.World.WorldGrid.Neighborhood(a.Cell, 1, around);
                        candidates = around.Where(byCell.ContainsKey).SelectMany(cell => byCell[cell]);
                    }
                    foreach (var b in candidates)
                    {
                        if (a == b) continue;
                        var bounds = a.Bounds; bounds.Expand(0.05f);
                        if (bounds.Intersects(b.Bounds)) a.NeighborIds.Add(b.ContainerId);
                    }
                }
                for (int i = 0; i < config.NetworkPrefabs.Count; i++)
                {
                    var prefab = config.NetworkPrefabs[i];
                    if (prefab != null) AddSchema(manifest, "prefab:" + i, prefab, (ushort)i);
                }
                AddSceneSchemas(manifest, scene);
                if (config.WorldManifest?.World != null)
                    foreach (var cell in config.WorldManifest.World.Cells) AddSceneSchemas(manifest, EditorSceneManager.OpenScene(cell.ScenePath, OpenSceneMode.Single));
                string data = JsonUtility.ToJson(manifest, true);
                // Config's object references are ignored by .NET. All scalar fields retain their serialized names.
                data = data.Insert(data.LastIndexOf('}'), ",\n\"Config\":" + JsonUtility.ToJson(config, true) + "\n");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "nebula-services.json"), data);
                Debug.Log($"[nebula] exported service manifest: {manifest.Containers.Count} containers, {manifest.Schemas.Count} persistence schemas");
            }
            finally
            {
                if (setup.Any(s => s.isLoaded && s.isActive)) EditorSceneManager.RestoreSceneManagerSetup(setup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
        private static Frame ExportFrame(Matrix4x4 m, Vector3 p, Quaternion q, Vector3 s)
        {
            var values = new float[16];
            for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) values[row * 4 + col] = m[row, col];
            return new Frame { position = p, rotation = q, lossyScale = s, Matrix = values };
        }
        private static void AddSceneSchemas(Manifest manifest, Scene scene)
        {
            foreach (var id in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NetworkIdentity>(true)))
                if (id.SceneId != 0) AddSchema(manifest, "scene:" + id.SceneId, id.gameObject);
        }
        private static void AddSchema(Manifest manifest, string name, GameObject template, ushort prefabId = ushort.MaxValue)
        {
            var schema = new Schema { Name = name, DisplayName = template.name, PrefabId = prefabId };
            var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
            foreach (var b in template.GetComponentsInChildren<NetworkBehaviour>(true))
            {
                if (b == null) continue;
                string stateName = b.GetType().Name + "#state";
                fields[stateName] = new Field { Name = stateName, Type = "" };
                for (var t = b.GetType(); t != null && t != typeof(NetworkBehaviour); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!typeof(NetworkVariableBase).IsAssignableFrom(f.FieldType)) continue;
                        var type = f.FieldType;
                        string fieldName = b.GetType().Name + "." + f.Name;
                        Type valueType = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(NetworkVariable<>) ? type.GetGenericArguments()[0] : null;
                        var field = new Field { Name = fieldName, Type = valueType?.FullName ?? "" };
                        if (valueType != null && valueType.IsEnum)
                        {
                            field.EnumUnderlyingType = Enum.GetUnderlyingType(valueType).FullName;
                            field.EnumNames = Enum.GetNames(valueType);
                            field.EnumValues = field.EnumNames.Select(n => Convert.ToString(Convert.ChangeType(Enum.Parse(valueType, n), Enum.GetUnderlyingType(valueType)), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        }
                        fields[fieldName] = field;
                    }
            }
            schema.Fields = fields.Values.ToList();
            if (manifest.Schemas.Any(s => s.Name == name)) throw new InvalidOperationException("Duplicate persistence template ID: " + name);
            manifest.Schemas.Add(schema);
        }
    }
}
