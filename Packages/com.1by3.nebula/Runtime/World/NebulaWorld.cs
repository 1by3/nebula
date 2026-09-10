using System.Collections.Generic;
using Nebula.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>
    /// Nebula's side of a partitioned world (<see cref="WorldDefinition"/> + <see cref="WorldContainerManifest"/>):
    /// instantiates the manifest's container tree for the registry, owns the <see cref="WorldStreamer"/>, keeps the
    /// virtual containers and every entity's cached positions in step with origin shifts, and strips the authoring
    /// <see cref="Container"/> components out of cell scenes as they stream in. Inactive (everything behaves as a
    /// single scene) when <see cref="NebulaConfig.WorldManifest"/> is unset.
    /// </summary>
    public static class NebulaWorld
    {
        public static WorldContainerManifest Manifest { get; private set; }
        public static WorldDefinition Definition => Manifest != null ? Manifest.World : null;
        public static WorldStreamer Streamer { get; private set; }
        public static bool IsActive => Manifest != null && Streamer != null;
        public static GameObject Root { get; private set; }

        private static readonly Dictionary<Vector3Int, Container> CellContainers = new Dictionary<Vector3Int, Container>();
        private static readonly List<Container> Scratch = new List<Container>();

        /// <summary>Instantiate the manifest and populate <see cref="ContainerRegistry"/>. Call once at boot, before any role starts.</summary>
        public static void Load(WorldContainerManifest manifest)
        {
            Unload();
            if (manifest == null || manifest.World == null)
            {
                NebulaLog.Error("world manifest has no WorldDefinition; running as a single scene");
                return;
            }
            Manifest = manifest;
            WorldOrigin.Reset(manifest.World);
            Root = new GameObject("Nebula World");
            Object.DontDestroyOnLoad(Root);
            Streamer = Root.AddComponent<WorldStreamer>();
            Streamer.Definition = manifest.World;
            Streamer.AutoShiftOrigin = false; // NebulaWorldStreaming decides when to shift, per role
            Streamer.CellLoaded += OnCellLoaded;
            Streamer.CellUnloaded += coord => NebulaLog.Info($"cell {coord} unloaded; {Streamer.LoadedCount} cell(s) resident");
            WorldOrigin.Shifted += OnOriginShifted;

            var ordered = new List<Container>(manifest.Entries.Count);
            foreach (var e in manifest.Entries)
            {
                if (!e.IsCell) continue;
                var go = new GameObject(e.Id);
                go.transform.SetParent(Root.transform, false);
                go.transform.position = WorldOrigin.ToFrame(e.Cell, Vector3.zero);
                var c = go.AddComponent<Container>();
                Fill(c, e);
                CellContainers[e.Cell] = c;
            }
            foreach (var e in manifest.Entries)
            {
                Container c;
                if (e.IsCell) c = CellContainers[e.Cell];
                else
                {
                    if (!CellContainers.TryGetValue(e.Cell, out var cell))
                    {
                        NebulaLog.Error($"world manifest: container '{e.Id}' is in cell {e.Cell} which has no cell container; rebake the manifest");
                        continue;
                    }
                    var go = new GameObject(e.Id);
                    go.transform.SetParent(cell.transform, false);
                    go.transform.localPosition = e.LocalPosition;
                    go.transform.localRotation = e.LocalRotation;
                    go.transform.localScale = e.LocalScale;
                    c = go.AddComponent<Container>();
                    Fill(c, e);
                }
                ordered.Add(c);
            }
            ContainerRegistry.Load(ordered, gridded: true);
            NebulaLog.Info($"world '{manifest.World.WorldName}': {manifest.World.Cells.Count} cells, {ordered.Count} containers, cell size {manifest.World.CellSize}");
        }

        private static void Fill(Container c, WorldContainerManifest.Entry e)
        {
            c.ContainerId = e.Id;
            c.Size = e.Size;
            c.Center = e.Center;
            c.Cell = e.Cell;
            c.IsCell = e.IsCell;
        }

        public static void Unload()
        {
            if (Streamer != null) Streamer.CellLoaded -= OnCellLoaded;
            WorldOrigin.Shifted -= OnOriginShifted;
            if (Root != null) Object.Destroy(Root);
            Root = null;
            Streamer = null;
            Manifest = null;
            CellContainers.Clear();
        }

        /// <summary>The container spanning <paramref name="cell"/>, or null.</summary>
        public static Container CellContainer(Vector3Int cell) => CellContainers.TryGetValue(cell, out var c) ? c : null;

        /// <summary>Whether the content of the cell holding <paramref name="container"/> is loaded in this process.</summary>
        public static bool IsContentLoaded(Container container)
        {
            return !IsActive || (container != null && Streamer.IsLoaded(container.Cell));
        }

        private static void OnCellLoaded(Vector3Int coord, Scene scene)
        {
            NebulaLog.Info($"cell {coord} loaded ({scene.name}); {Streamer.LoadedCount} cell(s) resident");
            // The authoring copies of the containers are already represented by the manifest's virtual ones.
            Scratch.Clear();
            foreach (var root in scene.GetRootGameObjects())
                Scratch.AddRange(root.GetComponentsInChildren<Container>(true));
            foreach (var authored in Scratch)
            {
                var live = ContainerRegistry.FindById(authored.ContainerId);
                if (live == null)
                    NebulaLog.Warn($"cell {coord}: container '{authored.ContainerId}' is not in the world manifest (rebake it); ignoring the authored box");
                else if (live.Cell != coord)
                    NebulaLog.Warn($"cell {coord}: container '{authored.ContainerId}' is baked into cell {live.Cell}; ignoring the authored box");
                Object.Destroy(authored);
            }
        }

        private static void OnOriginShifted(Vector3 delta)
        {
            // Cell scenes were moved by the streamer; the virtual containers (and the entities under them) follow.
            foreach (var kv in CellContainers) kv.Value.transform.position += delta;
            ContainerRegistry.RefreshCaches();
            NetworkIdentity.ShiftFrameAll(delta);
            Physics.SyncTransforms();
            NebulaLog.Debugf($"origin shifted to cell {WorldOrigin.Cell} (delta {delta})");
        }
    }
}
