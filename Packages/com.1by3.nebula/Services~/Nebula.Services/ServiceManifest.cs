using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nebula.ServicePrimitives;

namespace Nebula
{
    public sealed class ServiceManifest
    {
        public int Version = 1;
        public bool Partitioned;
        public NebulaConfig Config = new NebulaConfig();
        public List<Container> Containers = new List<Container>();
        public ServiceWorld World;
        public List<TemplateSchema> Schemas = new List<TemplateSchema>();
        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true, IgnoreReadOnlyProperties = true, PropertyNameCaseInsensitive = true, WriteIndented = true };
        private static ServiceManifest Current;
        private readonly Dictionary<SchemaField, Type> resolvedTypes = new Dictionary<SchemaField, Type>();
        public static string PathOnDisk { get; private set; }
        public static ServiceManifest Load(string path)
        {
            PathOnDisk = Path.GetFullPath(path);
            var manifest = JsonSerializer.Deserialize<ServiceManifest>(File.ReadAllText(PathOnDisk), Json) ?? throw new InvalidDataException("Empty service manifest");
            if (manifest.Version != 1) throw new InvalidDataException("Unsupported service manifest version");
            if (!manifest.Partitioned) manifest.World = null;
            else if (manifest.World == null || manifest.World.CellSize.x <= 0 || manifest.World.CellSize.y <= 0 || manifest.World.CellSize.z <= 0)
                throw new InvalidDataException("Partitioned service manifest requires a world with positive cell sizes");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < manifest.Containers.Count; i++)
            {
                var c = manifest.Containers[i];
                if (i >= ContainerRef.RuntimeIndex || c.Index != i || string.IsNullOrWhiteSpace(c.ContainerId) || !ids.Add(c.ContainerId))
                    throw new InvalidDataException("Service containers must have unique IDs and dense wire indices in export order");
                if (c.transform == null || (c.transform.Matrix != null && c.transform.Matrix.Length != 16))
                    throw new InvalidDataException("Service container frame must contain a 4x4 matrix");
            }
            // The services have no Unity asset references, so the world definition the interest grid is snapped
            // to reaches the config from the manifest instead (design §3). A manifest world is a baked one, and
            // baked cells are centred on their coordinate.
            manifest.Config.WorldCellSizeMeters = manifest.World != null ? MathF.Max(manifest.World.CellSize.x, manifest.World.CellSize.z) : 0f;
            manifest.Config.WorldCellsAreCentred = manifest.World != null;
            Current = manifest;
            ContainerRegistry.Load(manifest);
            return manifest;
        }
        public static Dictionary<string, Type> SchemaOf(string name)
        {
            var schema = Current?.Schemas.FirstOrDefault(s => s.Name == name);
            if (schema == null) return null;
            return schema.Fields.ToDictionary(f => f.Name, Current.ResolveField, StringComparer.Ordinal);
        }
        public static string TemplateOf(PersistedEntityRecord record)
        {
            if (record.SceneId != 0) return "scene:" + record.SceneId;
            var byId = Current?.Schemas.FirstOrDefault(s => s.PrefabId == record.PrefabId && s.PrefabId != ushort.MaxValue);
            if (byId != null && (string.IsNullOrEmpty(record.PrefabName) || byId.DisplayName == record.PrefabName)) return byId.Name;
            return Current?.Schemas.FirstOrDefault(s => s.PrefabId != ushort.MaxValue && s.DisplayName == record.PrefabName)?.Name ?? byId?.Name;
        }
        public static string TemplateDisplayName(string key) => Current?.Schemas.FirstOrDefault(s => s.Name == key)?.DisplayName ?? "";
        private Type ResolveField(SchemaField field)
        {
            if (resolvedTypes.TryGetValue(field, out var cached)) return cached;
            Type result = ResolveType(field.Type);
            if (!string.IsNullOrEmpty(field.EnumUnderlyingType) && field.EnumNames != null && field.EnumValues != null && field.EnumNames.Length == field.EnumValues.Length)
            {
                var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new System.Reflection.AssemblyName("Nebula.ExportedEnum"), System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect);
                var module = assembly.DefineDynamicModule("Schema");
                var underlying = Type.GetType(field.EnumUnderlyingType, true);
                var builder = module.DefineEnum("ExportedValue", System.Reflection.TypeAttributes.Public, underlying);
                for (int i = 0; i < field.EnumNames.Length; i++) builder.DefineLiteral(field.EnumNames[i], Convert.ChangeType(field.EnumValues[i], underlying, System.Globalization.CultureInfo.InvariantCulture));
                result = builder.CreateTypeInfo().AsType();
            }
            resolvedTypes[field] = result;
            return result;
        }
        private static Type ResolveType(string name)
        {
            return name switch
            {
                "UnityEngine.Vector2" => typeof(Vector2),
                "UnityEngine.Vector3" => typeof(Vector3),
                "UnityEngine.Quaternion" => typeof(Quaternion),
                "UnityEngine.Color" => typeof(Color),
                _ => string.IsNullOrEmpty(name) ? null : Type.GetType(name, false)
            };
        }
    }
    public sealed class TemplateSchema { public string Name, DisplayName; public ushort PrefabId = ushort.MaxValue; public List<SchemaField> Fields = new List<SchemaField>(); }
    public sealed class SchemaField { public string Name, Type, EnumUnderlyingType; public string[] EnumNames, EnumValues; }
    public sealed class ServiceWorld { public string WorldName; public Vector3 CellSize; public List<ServiceCell> Cells = new List<ServiceCell>(); }
    public sealed class ServiceCell { public Vector3Int Coord; public string SceneName; }
    public sealed class ContainerFrame { public float[] Matrix; public Vector3 position; public Quaternion rotation = Quaternion.identity; public Vector3 lossyScale = Vector3.one; }
    public sealed class Container
    {
        private float[] inverseSource;
        private System.Numerics.Matrix4x4 inverse;
        public string ContainerId;
        public ushort Index;
        public Vector3 Center, Size;
        public Vector3Int Cell;
        public bool IsCell, IsRuntime;
        public ulong RuntimeId;
        /// <summary>The baked balancing hint, exported with the container (<see cref="ContainerHint"/>).</summary>
        public ContainerHint Hint = ContainerHint.Default;
        public ContainerFrame transform = new ContainerFrame();
        public List<string> NeighborIds = new List<string>();
        public List<Container> Neighbors { get; } = new List<Container>();
        public bool IsDynamic => false;
        public ulong CarrierNetId => 0;
        public InstanceContainerInfo Instance;
        public ulong InstanceId => Instance?.InstanceId ?? 0;
        /// <summary>The opaque scope key of the container's scope (<see cref="EntityLocation.ScopeKey"/>); empty for the public world.</summary>
        public string ScopeKey => Instance?.ScopeKey ?? EntityLocation.PublicScope;
        public string OwnerWorkerId { get; set; } = "";
        public ushort OwnerWorkerIndex { get; set; } = ushort.MaxValue;
        public ulong LeaseEpoch { get; set; }
        public string LeaseState { get; set; } = "";
        public ContainerRef Ref => ContainerRef.Of(this);
        public Quaternion Rotation => transform.rotation;
        public Vector3 ToWorld(Vector3 local)
        {
            if (transform.Matrix == null) return transform.position + Rotation * Vector3.Scale(transform.lossyScale, local);
            var m = transform.Matrix;
            return new Vector3(m[0] * local.x + m[1] * local.y + m[2] * local.z + m[3], m[4] * local.x + m[5] * local.y + m[6] * local.z + m[7], m[8] * local.x + m[9] * local.y + m[10] * local.z + m[11]);
        }
        public Vector3 ToLocal(Vector3 world)
        {
            if (transform.Matrix != null)
            {
                if (!ReferenceEquals(inverseSource, transform.Matrix))
                {
                    var m = transform.Matrix;
                    var matrix = new System.Numerics.Matrix4x4(m[0], m[4], m[8], m[12], m[1], m[5], m[9], m[13], m[2], m[6], m[10], m[14], m[3], m[7], m[11], m[15]);
                    if (!System.Numerics.Matrix4x4.Invert(matrix, out inverse)) throw new InvalidDataException("Singular container matrix");
                    inverseSource = transform.Matrix;
                }
                var result = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(world.x, world.y, world.z), inverse);
                return new Vector3(result.X, result.Y, result.Z);
            }
            var p = Quaternion.Inverse(Rotation) * (world - transform.position);
            var s = transform.lossyScale;
            return new Vector3(p.x / s.x, p.y / s.y, p.z / s.z);
        }
        public Bounds WorldBounds => new Bounds(ToWorld(Center), Vector3.Scale(Size, new Vector3(MathF.Abs(transform.lossyScale.x), MathF.Abs(transform.lossyScale.y), MathF.Abs(transform.lossyScale.z))));
        public float Volume => WorldBounds.size.x * WorldBounds.size.y * WorldBounds.size.z;
        public bool Encloses(Container other) { var b = WorldBounds; b.Expand(0.5f); return other != this && Volume > other.Volume && b.Contains(other.WorldBounds.min) && b.Contains(other.WorldBounds.max); }
    }
    public static class ContainerRegistry
    {
        public static IReadOnlyList<Container> All { get; private set; } = Array.Empty<Container>();
        private static readonly List<Container> RuntimeList = new List<Container>();
        private static readonly Dictionary<string, Container> ById = new Dictionary<string, Container>(StringComparer.Ordinal);
        private static readonly Dictionary<ulong, Container> RuntimeById = new Dictionary<ulong, Container>();
        private static readonly Dictionary<Vector3Int, List<Container>> ByCell = new Dictionary<Vector3Int, List<Container>>();
        public static IReadOnlyList<Container> Runtime => RuntimeList;
        public static int Count => All.Count;
        public static bool IsGridded => World.WorldOrigin.Definition != null;
        public static event Action<Container> RuntimeRegistered, RuntimeUnregistering;
        internal static void Load(ServiceManifest manifest)
        {
            All = manifest.Containers;
            RuntimeList.Clear();
            ById.Clear();
            RuntimeById.Clear();
            ByCell.Clear();
            World.WorldOrigin.Definition = manifest.World;
            foreach (var c in All)
            {
                ById.Add(c.ContainerId, c);
                if (!ByCell.TryGetValue(c.Cell, out var cell)) ByCell.Add(c.Cell, cell = new List<Container>());
                cell.Add(c);
            }
            foreach (var c in All) { c.Neighbors.Clear(); foreach (var id in c.NeighborIds) { var n = FindById(id); if (n != null) c.Neighbors.Add(n); } }
        }
        public static Container FindById(string id) => id != null && ById.TryGetValue(id, out var c) ? c : null;

        /// <summary>
        /// Every container whose box overlaps <paramref name="box"/> (the Unity registry's query, which interest
        /// management resolves a region to its owning workers with). The standalone gateway holds no scene, so
        /// this is a scan of the manifest: the callers cache what they resolve and re-resolve only when the
        /// control plane changes, so the scan is per lease change and not per region per tick.
        /// </summary>
        public static void Overlapping(Bounds box, List<Container> result, ulong instanceId = 0)
        {
            result.Clear();
            foreach (var c in All) if (c.InstanceId == instanceId && box.Intersects(c.WorldBounds)) result.Add(c);
            foreach (var c in RuntimeList) if (c.InstanceId == instanceId && box.Intersects(c.WorldBounds)) result.Add(c);
        }

        /// <summary>The container holding the point, or — as the Unity registry does for a point in no box — the nearest one.</summary>
        public static Container Find(Vector3 world, Container exclude = null, ulong instanceId = 0)
        {
            Container inside = null, nearest = null;
            float insideVolume = float.MaxValue, nearestDistance = float.MaxValue;
            foreach (var c in All.Concat(RuntimeList))
            {
                if (c == exclude || c.InstanceId != instanceId) continue;
                var b = c.WorldBounds;
                if (b.Contains(world))
                {
                    if (c.Volume >= insideVolume) continue;
                    insideVolume = c.Volume;
                    inside = c;
                }
                else
                {
                    var min = b.min; var max = b.max;
                    var closest = new Vector3(MathF.Min(MathF.Max(world.x, min.x), max.x), MathF.Min(MathF.Max(world.y, min.y), max.y), MathF.Min(MathF.Max(world.z, min.z), max.z));
                    float d = (closest - world).sqrMagnitude;
                    if (d >= nearestDistance) continue;
                    nearestDistance = d;
                    nearest = c;
                }
            }
            return inside ?? nearest;
        }
        public static Container Resolve(ContainerRef r) => r.IsRuntime ? (RuntimeById.TryGetValue(r.RuntimeId, out var c) ? c : null) : r.IsStatic && r.Index < All.Count ? All[r.Index] : null;
        public static IReadOnlyList<Container> InCell(Vector3Int cell) => ByCell.TryGetValue(cell, out var list) ? list : Array.Empty<Container>();
        public static Bounds ToAbsolute(Bounds b) => b;
        public static string RuntimeContainerId(ulong id) => "rt_" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool IsDynamicId(string id) => id != null && id.IndexOf('#') >= 0;
        public static ulong CarrierNetIdOf(string id) => id != null && id.LastIndexOf('#') is int i && i >= 0 && ulong.TryParse(id.Substring(i + 1), out var n) ? n : 0;
        public static void ApplyLease(string id, string owner, ushort index, ulong epoch, string state)
        {
            var c = FindById(id); if (c == null) return; c.OwnerWorkerId = owner; c.OwnerWorkerIndex = index; c.LeaseEpoch = epoch; c.LeaseState = state;
        }
        public static void SyncRuntime(IReadOnlyList<LeaseInfo> leases)
        {
            var keep = new HashSet<ulong>();
            foreach (var l in leases)
            {
                if (!l.HasBounds || !l.ContainerId.StartsWith("rt_", StringComparison.Ordinal) || !ulong.TryParse(l.ContainerId.Substring(3), out var id)) continue;
                keep.Add(id);
                if (RuntimeById.ContainsKey(id)) continue;
                var c = new Container { ContainerId = l.ContainerId, Index = ContainerRef.RuntimeIndex, IsRuntime = true, RuntimeId = id, Size = l.BoundsSize, Instance = l.Instance, transform = new ContainerFrame { position = l.BoundsCenter } };
                var bounds = c.WorldBounds;
                bounds.Expand(0.05f);
                foreach (var neighbor in All.Concat(RuntimeList))
                {
                    if (neighbor.InstanceId != c.InstanceId || !bounds.Intersects(neighbor.WorldBounds)) continue;
                    c.Neighbors.Add(neighbor);
                    neighbor.Neighbors.Add(c);
                }
                RuntimeList.Add(c);
                RuntimeById.Add(id, c);
                ById.Add(c.ContainerId, c);
                RuntimeRegistered?.Invoke(c);
            }
            foreach (var c in RuntimeList.Where(c => !keep.Contains(c.RuntimeId)).ToArray())
            {
                RuntimeUnregistering?.Invoke(c);
                foreach (var neighbor in c.Neighbors) neighbor.Neighbors.Remove(c);
                RuntimeList.Remove(c);
                RuntimeById.Remove(c.RuntimeId);
                ById.Remove(c.ContainerId);
            }
        }
    }
}
namespace Nebula.World
{
    public static class WorldOrigin { public static ServiceWorld Definition; public static Vector3Int Cell => default; }
}
