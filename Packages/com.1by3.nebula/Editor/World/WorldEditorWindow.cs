using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nebula.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula.Editor
{
    /// <summary>
    /// The World window (Nebula &gt; World): a map of the cell grid, one vertical layer at a time. Click an empty
    /// cell to create its scene, a defined cell to open or close it (open cells are placed next to each other in
    /// the scene view), right-click for more. Also bakes the container manifest and moves objects from a
    /// single-scene level into cells.
    /// </summary>
    public sealed class WorldEditorWindow : EditorWindow
    {
        private const string WorldKey = "Nebula.World.Window.World";
        private const float CellPx = 34f;

        private WorldDefinition _world;
        private WorldContainerManifest _manifest;
        private int _layer;
        private int _padding = 2;
        private Vector3Int _origin;
        private Vector2 _scroll;
        private bool _drawGrid = true;
        private string _lastBake = "";

        [MenuItem("Nebula/World/World Window", priority = 40)]
        public static void Open() => GetWindow<WorldEditorWindow>("World");

        private void OnEnable()
        {
            var guid = EditorPrefs.GetString(WorldKey, "");
            if (!string.IsNullOrEmpty(guid)) _world = AssetDatabase.LoadAssetAtPath<WorldDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if (_world == null) _world = WorldAssets.AllWorlds().FirstOrDefault();
            _origin = WorldEditorPlacement.EditorOriginCell;
            SceneView.duringSceneGui += OnSceneGui;
            EditorSceneManager.sceneOpened += (_, __) => Repaint();
            EditorSceneManager.sceneClosed += _ => Repaint();
        }

        private void OnDisable() => SceneView.duringSceneGui -= OnSceneGui;

        private void OnGUI()
        {
            DrawHeader();
            if (_world == null)
            {
                EditorGUILayout.HelpBox("Pick or create a World Definition. Cells are authored as their own scenes, each around its own origin; the streamer places them at runtime.", MessageType.Info);
                return;
            }
            DrawSettings();
            EditorGUILayout.Space();
            DrawGrid();
            EditorGUILayout.Space();
            DrawTools();
        }

        // ---------------------------------------------------------------------------------------- sections

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var w = (WorldDefinition)EditorGUILayout.ObjectField("World", _world, typeof(WorldDefinition), false);
                if (w != _world)
                {
                    _world = w;
                    _manifest = null;
                    EditorPrefs.SetString(WorldKey, w != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(w)) : "");
                }
                if (GUILayout.Button("New...", GUILayout.Width(60)))
                {
                    string path = EditorUtility.SaveFilePanelInProject("New World Definition", "World", "asset", "Where to save the world definition");
                    if (!string.IsNullOrEmpty(path))
                    {
                        _world = WorldAssets.CreateWorld(path, Path.GetFileNameWithoutExtension(path), new Vector3(256f, 256f, 256f));
                        EditorPrefs.SetString(WorldKey, AssetDatabase.AssetPathToGUID(path));
                        _manifest = null;
                    }
                }
            }
            if (_world == null) return;
            if (_manifest == null || _manifest.World != _world) _manifest = WorldAssets.ManifestFor(_world);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField("Container manifest", _manifest, typeof(WorldContainerManifest), false);
                if (_manifest == null)
                {
                    if (GUILayout.Button("Create", GUILayout.Width(60)))
                    {
                        string worldPath = AssetDatabase.GetAssetPath(_world);
                        string path = Path.GetDirectoryName(worldPath).Replace('\\', '/') + "/" + _world.WorldName + "Containers.asset";
                        _manifest = WorldAssets.CreateManifest(path, _world);
                    }
                }
                else if (GUILayout.Button("Bake", GUILayout.Width(60)))
                {
                    _lastBake = WorldBaker.Bake(_manifest).ToString();
                }
            }
            var cfg = NebulaConfig.Load();
            if (_manifest != null && cfg != null && cfg.WorldManifest != _manifest)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox("NebulaConfig does not use this manifest yet.", MessageType.Warning);
                    if (GUILayout.Button("Use in NebulaConfig", GUILayout.Width(140), GUILayout.Height(38)))
                    {
                        cfg.WorldManifest = _manifest;
                        EditorUtility.SetDirty(cfg);
                        AssetDatabase.SaveAssets();
                    }
                }
            }
            if (!string.IsNullOrEmpty(_lastBake)) EditorGUILayout.LabelField("Last bake", _lastBake);
        }

        private void DrawSettings()
        {
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                _world.WorldName = EditorGUILayout.TextField("World name", _world.WorldName);
                _world.CellSize = EditorGUILayout.Vector3Field("Cell size (m)", _world.CellSize);
                _world.SceneFolder = EditorGUILayout.TextField("Cell scene folder", _world.SceneFolder);
                if (check.changed)
                {
                    _world.CellSize = Vector3.Max(_world.CellSize, Vector3.one);
                    EditorUtility.SetDirty(_world);
                    WorldEditorPlacement.SetEditorOrigin(_world, WorldEditorPlacement.EditorOriginCell);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _origin = EditorGUILayout.Vector3IntField("Editor origin cell", _origin);
                if (GUILayout.Button("Apply", GUILayout.Width(60)))
                {
                    WorldEditorPlacement.SetEditorOrigin(_world, _origin);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _layer = EditorGUILayout.IntField("Layer (Y)", _layer);
                if (GUILayout.Button("-", GUILayout.Width(24))) _layer--;
                if (GUILayout.Button("+", GUILayout.Width(24))) _layer++;
                _drawGrid = GUILayout.Toggle(_drawGrid, "Scene grid", GUILayout.Width(90));
            }
        }

        private void DrawGrid()
        {
            _world.TryGetExtent(out var min, out var max);
            if (_world.Cells.Count == 0) { min = Vector3Int.zero; max = Vector3Int.zero; }
            int x0 = min.x - _padding, x1 = max.x + _padding, z0 = min.z - _padding, z1 = max.z + _padding;
            int cols = x1 - x0 + 1, rows = z1 - z0 + 1;
            EditorGUILayout.LabelField($"Layer {_layer}: {_world.Cells.Count(c => c.Coord.y == _layer)} cell(s) of {_world.Cells.Count}. Click: create / open / close. Right-click: more.", EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(false), GUILayout.MaxHeight(rows * (CellPx + 2) + 30));
            var area = GUILayoutUtility.GetRect(cols * (CellPx + 2) + 30, rows * (CellPx + 2) + 20);
            var label = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 9 };
            var head = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
            for (int ix = 0; ix < cols; ix++)
                GUI.Label(new Rect(area.x + 24 + ix * (CellPx + 2), area.y, CellPx, 14), (x0 + ix).ToString(), head);
            for (int iz = 0; iz < rows; iz++)
            {
                int z = z1 - iz; // north up
                float y = area.y + 16 + iz * (CellPx + 2);
                GUI.Label(new Rect(area.x, y, 22, CellPx), z.ToString(), head);
                for (int ix = 0; ix < cols; ix++)
                {
                    var coord = new Vector3Int(x0 + ix, _layer, z);
                    var rect = new Rect(area.x + 24 + ix * (CellPx + 2), y, CellPx, CellPx);
                    var entry = _world.GetCell(coord);
                    bool open = entry != null && WorldAssets.IsOpen(entry);
                    var color = entry == null ? new Color(0.25f, 0.25f, 0.25f) : open ? new Color(0.35f, 0.75f, 0.35f) : new Color(0.3f, 0.45f, 0.75f);
                    if (coord == WorldEditorPlacement.EditorOriginCell) color = Color.Lerp(color, Color.yellow, 0.35f);
                    EditorGUI.DrawRect(rect, color);
                    GUI.Label(rect, entry == null ? "" : $"{coord.x},{coord.z}", label);
                    var e = Event.current;
                    if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
                    {
                        if (e.button == 1) ShowContext(coord, entry);
                        else if (entry == null) WorldAuthoring.CreateCell(_world, coord);
                        else if (open) WorldAuthoring.CloseCell(_world, coord);
                        else WorldAuthoring.OpenCell(_world, coord);
                        e.Use();
                        Repaint();
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void ShowContext(Vector3Int coord, WorldDefinition.CellEntry entry)
        {
            var menu = new GenericMenu();
            if (entry == null)
            {
                menu.AddItem(new GUIContent($"Create cell {coord}"), false, () => WorldAuthoring.CreateCell(_world, coord));
            }
            else
            {
                bool open = WorldAssets.IsOpen(entry);
                menu.AddItem(new GUIContent(open ? "Close" : "Open"), false, () => { if (open) WorldAuthoring.CloseCell(_world, coord); else WorldAuthoring.OpenCell(_world, coord); });
                menu.AddItem(new GUIContent("Open alone (hub + this cell)"), false, () => OpenAlone(coord));
                menu.AddItem(new GUIContent("Ping scene asset"), false, () => EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(entry.ScenePath)));
                menu.AddItem(new GUIContent("Set as editor origin"), false, () => { _origin = coord; WorldEditorPlacement.SetEditorOrigin(_world, coord); });
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Delete cell..."), false, () => WorldAuthoring.DeleteCell(_world, coord));
            }
            menu.ShowAsContext();
        }

        private void OpenAlone(Vector3Int coord)
        {
            foreach (var c in _world.Cells.ToList())
                if (c.Coord != coord && WorldAssets.IsOpen(c)) if (!WorldAuthoring.CloseCell(_world, c.Coord)) return;
            WorldAuthoring.OpenCell(_world, coord);
        }

        private void DrawTools()
        {
            EditorGUILayout.LabelField("Partition an existing scene", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Select root objects in the Hierarchy and move them into cells. Each object goes to the cell its position falls in (given the editor origin). Objects you keep in the hub scene (lights, cameras, game mode, the Nebula bootstrap) stay put.", MessageType.None);
            var selected = Selection.gameObjects.Where(g => g.scene.IsValid() && g.transform.parent == null && g.GetComponent<WorldCell>() == null).ToList();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                {
                    if (GUILayout.Button($"Move {selected.Count} selected root(s) into cells by position"))
                    {
                        var groups = selected.GroupBy(g => WorldAuthoring.CellOfEditorPosition(_world, g.transform.position));
                        int moved = 0;
                        foreach (var g in groups) moved += WorldAuthoring.MoveIntoCell(_world, g.Key, g);
                        Debug.Log($"[world] moved {moved} object(s) into {groups.Count()} cell(s)");
                    }
                }
                if (GUILayout.Button("Everything except selection", GUILayout.Width(170)))
                {
                    var active = SceneManager.GetActiveScene();
                    if (WorldAssets.TryGetCellOf(active, out _, out _)) Debug.LogWarning("[world] the active scene is a cell scene; make the hub scene active first");
                    else if (EditorUtility.DisplayDialog("Partition scene", $"Move every root object of '{active.name}' that is not selected into cells by position?", "Move", "Cancel"))
                    {
                        int moved = WorldAuthoring.PartitionScene(_world, active, Selection.gameObjects);
                        Debug.Log($"[world] moved {moved} object(s) out of '{active.name}'");
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add cell scenes to build settings")) Debug.Log($"[world] {WorldAssets.AddCellScenesToBuildSettings(_world)} scene(s) added");
                if (GUILayout.Button("Save open cell scenes"))
                {
                    var open = _world.Cells.Select(WorldAssets.OpenSceneOf).Where(s => s.IsValid() && s.isLoaded).ToArray();
                    EditorSceneManager.SaveScenes(open);
                }
            }
        }

        // ---------------------------------------------------------------------------------------- scene view

        private void OnSceneGui(SceneView view)
        {
            if (!_drawGrid || _world == null) return;
            var origin = WorldEditorPlacement.EditorOriginCell;
            foreach (var c in _world.Cells)
            {
                bool open = WorldAssets.IsOpen(c);
                var center = _world.FrameOrigin(c.Coord, origin);
                Handles.color = open ? new Color(0.4f, 1f, 0.4f, 0.9f) : new Color(0.4f, 0.6f, 1f, 0.35f);
                Handles.DrawWireCube(center, _world.CellSize);
                Handles.Label(center + Vector3.up * (_world.CellSize.y * 0.5f), $"{c.Coord.x},{c.Coord.y},{c.Coord.z}");
            }
        }
    }
}
