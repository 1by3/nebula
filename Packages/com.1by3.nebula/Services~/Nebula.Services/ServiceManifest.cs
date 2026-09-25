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
        /// <summary>
        /// Environment variables every worker of a self-hosted deployment gets (non-secret; see docs "Environment
        /// variables"). Not exported by Unity: <c>nebula deploy</c> writes it from <c>deploy.env</c> in nebula.json.
        /// </summary>
        public Dictionary<string, string> Env;
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
        /// <summary>Who simulates what is inside (docs/container-tree.md D6), exported with the container.</summary>
        public ContainerAuthority Authority = ContainerAuthority.Auto;
        /// <summary>Runtime containers: the parent named on the lease row ("" for a root).</summary>
        public string ParentId = "";
        /// <summary>Runtime containers: the box centre local to the parent's frame (the row's centre for a child).</summary>
        public Vector3 LocalCenter;
        public bool OwnPhysicsFrame;
        public FrameInterestMode FrameInterest;
        /// <summary>The container this one sits in, or null for a root (docs/container-tree.md D2).</summary>
        [System.Text.Json.Serialization.JsonIgnore] public Container Parent { get; internal set; }
        [System.Text.Json.Serialization.JsonIgnore] public List<Container> Children { get; } = new List<Container>();
        /// <summary>
        /// Leased unless it was made inherited, or sits in a carrier that has no physics frame of its own: then the
        /// carrier's worker simulates it as inherited (docs/container-tree.md D7), and the services must not deal it.
        /// </summary>
        public bool IsLeased => Authority != ContainerAuthority.Inherited && !InMovingSpace;
        /// <summary>It would be leased but is simulated as inherited under a moving parent without a frame (D7).</summary>
        public bool AuthorityDemoted => Authority != ContainerAuthority.Inherited && InMovingSpace;
        /// <summary>
        /// The services never hold a carrier, but its lease row says whether it has a physics frame: a container fixed in
        /// one without a frame, with no framed container in between, is somewhere only the carrier's worker knows exactly.
        /// </summary>
        public bool InMovingSpace
        {
            get
            {
                var top = this;
                for (int hops = 0; top.Parent != null && hops < 64; hops++)
                {
                    if (top.Parent.OwnPhysicsFrame) return false;
                    top = top.Parent;
                }
                return top.ParentUnheld && ContainerRegistry.IsFramelessCarrier(top.ParentId);
            }
        }
        /// <summary>Carried containers are never registered here.</summary>
        public bool IsPinned => false;
        public ContainerSource Source => IsRuntime ? ContainerSource.Runtime : ContainerSource.Baked;
        /// <summary>A child of a container the services never hold (fixed in a ship): registered in its parent's frame, placed nowhere.</summary>
        public bool ParentUnheld => Parent == null && !string.IsNullOrEmpty(ParentId);
        /// <summary>Somewhere up its chain is a container the services never hold, so it has no absolute place here.</summary>
        public bool PlacedInUnheld { get { var top = this; for (int hops = 0; top.Parent != null && hops < 64; hops++) top = top.Parent; return top.ParentUnheld; } }
        /// <summary>How many containers enclose this one; a child of an unheld parent counts that parent.</summary>
        public int Depth { get { int d = 0; var top = this; while (top.Parent != null && d < 64) { d++; top = top.Parent; } return top.ParentUnheld ? d + 1 : d; } }
        public ContainerFrame transform = new ContainerFrame();
        public List<string> NeighborIds = new List<string>();
        public List<Container> Neighbors { get; } = new List<Container>();
        public bool IsDynamic => false;
        public ulong CarrierNetId => 0;
        public InstanceContainerInfo Instance;
        public ulong InstanceId => Instance?.InstanceId ?? 0;
        /// <summary>The opaque scope key of the container's scope (<see cref="EntityLocation.ScopeKey"/>); empty for the public world.</summary>
        public string ScopeKey => Instance?.ScopeKey ?? EntityLocation.PublicScope;
        private string _owner = "";
        private ushort _ownerIndex = ushort.MaxValue;
        private ulong _epoch;
        /// <summary>The lease's worker; an inherited container's is its parent's (docs/container-tree.md D6).</summary>
        public string OwnerWorkerId { get => !IsLeased && Parent != null ? Parent.OwnerWorkerId : _owner; set => _owner = value ?? ""; }
        public ushort OwnerWorkerIndex { get => !IsLeased && Parent != null ? Parent.OwnerWorkerIndex : _ownerIndex; set => _ownerIndex = value; }
        public ulong LeaseEpoch { get => !IsLeased && Parent != null ? Parent.LeaseEpoch : _epoch; set => _epoch = value; }
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
        /// <summary>The services register every runtime row at once (a child of an unheld parent in the parent's frame), so none waits.</summary>
        public static int PendingRuntimeCount => 0;
        /// <summary>Carried containers (<c>label#netId</c>) whose lease row says they have a physics frame of their own, from the last <see cref="SyncRuntime"/>.</summary>
        private static readonly HashSet<string> FramedCarriers = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Whether <paramref name="id"/> names a carried container without a physics frame of its own.</summary>
        public static bool IsFramelessCarrier(string id) => IsDynamicId(id) && !FramedCarriers.Contains(id);
        public static IEnumerable<ulong> PendingRuntimeIds => Array.Empty<ulong>();
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
            // The baked tree, exactly as the Unity registry builds it: the smallest enclosing box of the same scope.
            foreach (var c in All) { c.Parent = null; c.Children.Clear(); }
            foreach (var c in All)
            {
                Container best = null;
                foreach (var n in c.Neighbors)
                    if (n.InstanceId == c.InstanceId && n.Encloses(c) && (best == null || n.Volume < best.Volume)) best = n;
                c.Parent = best;
                best?.Children.Add(c);
            }
        }
        public static Container FindById(string id) => id != null && ById.TryGetValue(id, out var c) ? c : null;

        /// <summary>The placement a runtime container carries on its row (see the Unity registry's method of the same name).</summary>
        public static ContainerPlacement PlacementOf(Container c) => new ContainerPlacement
        {
            ParentId = c.ParentId ?? "",
            Center = Double3.From(string.IsNullOrEmpty(c.ParentId) ? c.transform.position : c.LocalCenter),
            Size = c.Size,
            Authority = c.Authority,
            OwnPhysicsFrame = c.OwnPhysicsFrame,
            FrameInterest = c.FrameInterest,
        };

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
        /// <summary>The services hold every box in absolute coordinates already; the scope-frame overload exists so
        /// code shared with the Unity registry (<see cref="WorkerRegistration"/>) compiles against both.</summary>
        public static Bounds ToAbsolute(Bounds b, ulong instanceId) => b;
        public static string RuntimeContainerId(ulong id) => "rt_" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool IsDynamicId(string id) => id != null && id.IndexOf('#') >= 0;
        public static ulong CarrierNetIdOf(string id) => id != null && id.LastIndexOf('#') is int i && i >= 0 && ulong.TryParse(id.Substring(i + 1), out var n) ? n : 0;
        public static void ApplyLease(string id, string owner, ushort index, ulong epoch, string state)
        {
            var c = FindById(id); if (c == null) return; c.OwnerWorkerId = owner; c.OwnerWorkerIndex = index; c.LeaseEpoch = epoch; c.LeaseState = state;
        }
        public static void SyncRuntime(IReadOnlyList<LeaseInfo> leases)
        {
            FramedCarriers.Clear();
            foreach (var l in leases) if (l.OwnPhysicsFrame && IsDynamicId(l.ContainerId)) FramedCarriers.Add(l.ContainerId);
            var keep = new HashSet<ulong>();
            // Roots before children, and a child only once its parent is here: rows may arrive in any order, so the
            // pass repeats until nothing more can be placed. A child of a container the services never hold (a
            // carrier's box) is registered in its parent's local frame, which is all a region lookup in that frame needs.
            var waiting = new List<(LeaseInfo Lease, ulong Id)>();
            foreach (var l in leases)
            {
                if (!l.HasBounds || !l.ContainerId.StartsWith("rt_", StringComparison.Ordinal) || !ulong.TryParse(l.ContainerId.Substring(3), out var id)) continue;
                keep.Add(id);
                if (RuntimeById.ContainsKey(id)) continue;
                waiting.Add((l, id));
            }
            for (bool progress = true; progress && waiting.Count > 0;)
            {
                progress = false;
                for (int i = waiting.Count - 1; i >= 0; i--)
                {
                    var (l, id) = waiting[i];
                    Container parent = null;
                    if (!l.IsRoot)
                    {
                        parent = FindById(l.ParentId);
                        if (parent == null && waiting.Any(w => w.Lease.ContainerId == l.ParentId)) continue;
                    }
                    waiting.RemoveAt(i);
                    progress = true;
                    AddRuntime(l, id, parent);
                }
            }
            foreach (var c in RuntimeList.Where(c => !keep.Contains(c.RuntimeId)).ToArray())
            {
                RuntimeUnregistering?.Invoke(c);
                foreach (var neighbor in c.Neighbors) neighbor.Neighbors.Remove(c);
                c.Parent?.Children.Remove(c);
                RuntimeList.Remove(c);
                RuntimeById.Remove(c.RuntimeId);
                ById.Remove(c.ContainerId);
            }
        }

        private static void AddRuntime(LeaseInfo l, ulong id, Container parent)
        {
            {
                var frame = new ContainerFrame { position = l.BoundsCenter };
                if (parent != null)
                {
                    frame.position = parent.ToWorld(parent.Center + l.BoundsCenter);
                    frame.rotation = parent.Rotation;
                }
                var c = new Container
                {
                    ContainerId = l.ContainerId, Index = ContainerRef.RuntimeIndex, IsRuntime = true, RuntimeId = id, Size = l.BoundsSize, Instance = l.Instance,
                    Authority = l.Authority, ParentId = l.ParentId ?? "", LocalCenter = l.BoundsCenter, OwnPhysicsFrame = l.OwnPhysicsFrame, FrameInterest = l.FrameInterest,
                    Parent = parent, transform = frame,
                };
                parent?.Children.Add(c);
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
        }
    }
}
namespace Nebula.World
{
    public static class WorldOrigin { public static ServiceWorld Definition; public static Vector3Int Cell => default; }
}
