using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;

namespace Nebula.ServiceTests;

/// <summary>
/// The world the synthetic scale runs are driven in: a row of equal containers, one per worker, plus any number
/// of keyed scopes activated on top of them. It is deliberately the same shape for every scenario so that the
/// numbers in <c>Logs/scale/</c> can be compared across scenarios and across runs (docs/scale-suite.md, D4).
/// </summary>
public sealed class ScaleWorld : IDisposable
{
    public const float CellSize = 128f;

    private readonly string _directory;

    /// <summary>The baked container ids, in order along x.</summary>
    public readonly List<string> Cells = new();

    public ScaleWorld(int cells)
    {
        _directory = Path.Combine(Path.GetTempPath(), "nebula-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CommandLine.Override(new Dictionary<string, string>());
        var containers = new List<Container>();
        for (int i = 0; i < cells; i++)
        {
            string id = "c" + i;
            Cells.Add(id);
            containers.Add(new Container
            {
                ContainerId = id,
                Index = (ushort)i,
                Size = new Vector3(CellSize, CellSize, CellSize),
                transform = new ContainerFrame { position = new Vector3((i + 0.5f) * CellSize, 0, 0) },
            });
        }
        string path = Path.Combine(_directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest { Containers = containers }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    /// <summary>Deal the cells round-robin over the fleet's workers: the starting assignment for every scenario.</summary>
    public void Deal(Fleet fleet)
    {
        for (int i = 0; i < Cells.Count; i++) fleet.Assign(Cells[i], fleet.Workers[i % fleet.Workers.Count].WorkerId);
    }

    /// <summary>
    /// A keyed scope with one part, sitting well clear of the baked row so nothing about its isolation depends on
    /// geometry: what keeps its entities out of another scope's clients is the scope, not the distance.
    /// </summary>
    public static ScopeDefinition Room(int ordinal) => new()
    {
        Kind = ScopeKind.Parts,
        ObservePublic = false,
        Parts = { new ScopePart { PartId = "room", Center = new Vector3(100_000 + ordinal * 10_000, 0, 0), Size = new Vector3(64, 64, 64) } },
    };

    /// <summary>The runtime container a keyed scope's single part resolves to, on every process (NEB-220).</summary>
    public static ContainerRef RoomContainer(string scopeKey) => ContainerRef.Runtime(ScopeKeys.Hash(scopeKey + "/room"));

    /// <summary>Activate <paramref name="count"/> scopes, dealt round-robin over the workers. Returns their keys.</summary>
    public static List<string> ActivateScopes(Fleet fleet, int count, string prefix = "room")
    {
        var keys = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string key = prefix + "/" + i;
            fleet.Plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = key,
                Definition = Room(i),
                PreferredWorkerId = fleet.Workers[i % fleet.Workers.Count].WorkerId,
                Requester = "scale-suite",
            });
            keys.Add(key);
        }
        return keys;
    }

    public void Dispose()
    {
        ContainerRegistry.SyncRuntime(Array.Empty<LeaseInfo>());
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
