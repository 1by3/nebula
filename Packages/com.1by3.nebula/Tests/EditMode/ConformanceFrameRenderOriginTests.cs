using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 49 (<c>docs/container-tree.md</c> D11, D19): how a scope's origin shift reaches what stands
    /// in a physics frame.
    /// <list type="bullet">
    /// <item>A scope's origin shift on a client poses the frames again at once, so an origin rule run later in the same
    /// frame reads what stands in a frame where the shift put it, and never shifts back.</item>
    /// <item>A scope's shift leaves the simulation history of what stands in a frame alone: it belongs to the frame's own
    /// origin.</item>
    /// </list>
    /// Tier B (<see cref="ConformanceMesh"/>, one worker); the client half switches the process to a rendering client, as
    /// the other client scenarios do.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFrameRenderOriginTests
    {
        private const string Scope = "system/render-origin";
        private const float Cell = 32768f;
        /// <summary>A slow planet's turn: about two hours a revolution.</summary>
        private const float Omega = 8.5e-4f;
        private const float Dt = 1f / 60f;
        private const int Frames = 1000;

        /// <summary>A chunk of the planet's ground 204.8 km from its centre, tilted as a sphere's are.</summary>
        private static readonly Vector3 ChunkLocal = new Vector3(0f, 117480f, 167760f);
        private static readonly Quaternion ChunkRotation = Quaternion.Euler(-55f, 0f, 0f);
        private static readonly Vector3 Axis = new Vector3(0.2f, 1f, 0.1f).normalized;

        private ConformanceMesh _mesh;
        private WorldDefinition _definition;
        private RuntimeGrid _grid;
        private Container _home;
        private ushort _planetPrefab, _shipPrefab, _thingPrefab;
        private NetworkIdentity _planet;
        private int _historyWindow;
        private readonly List<GameObject> _objects = new List<GameObject>();

        private ConformanceMesh.Worker W1 => _mesh[0];
        private PhysicsFrame PlanetFrame => _planet.Carried.Frame;

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = Vector3.one * Cell;
            NebulaWorld.LoadRuntime(_definition);
            _mesh = new ConformanceMesh(1);
            _grid = new RuntimeGrid(Vector3.one * Cell, planar: false, scopeKey: Scope);
            NebulaChunks.Activate(_grid, NebulaRoles.Worker, true, null);
            _historyWindow = StateHistory.WindowTicks;
            StateHistory.WindowTicks = 8;

            var planet = new GameObject("planet-prefab");
            planet.AddComponent<NetworkIdentity>();
            var box = planet.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = Vector3.one * 500_000f;
            box.OwnPhysicsFrame = true;
            planet.AddComponent<NetworkTransform>();
            _planetPrefab = _mesh.RegisterPrefab(planet);

            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var shipBox = ship.AddComponent<Container>();
            shipBox.ContainerId = "ship";
            shipBox.Size = new Vector3(10f, 6f, 20f);
            shipBox.Center = new Vector3(0f, 3f, 0f);
            shipBox.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>();
            var floor = new GameObject("floor");
            floor.transform.SetParent(ship.transform, false);
            floor.AddComponent<BoxCollider>().size = new Vector3(10f, 0.2f, 20f);
            _shipPrefab = _mesh.RegisterPrefab(ship);

            var thing = new GameObject("thing-prefab");
            thing.AddComponent<NetworkIdentity>();
            _thingPrefab = _mesh.RegisterPrefab(thing);

            _home = Chunk(Vector3Int.zero);
            _planet = W1.SpawnServerDriven(_planetPrefab, _home, _home.transform.position, Quaternion.identity);
            Assume.That(_planet.Carried, Is.Not.Null);
            Assume.That(PlanetFrame, Is.Not.Null, "the planet has its frame");
        }

        [TearDown]
        public void TearDown()
        {
            PhysicsFrames.EndSimulation();
            NebulaRuntime.IsClient = false;
            NebulaRuntime.IsServer = true;
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            _mesh.Dispose();
            NebulaChunks.ResetForNewSession();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_definition);
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
            StateHistory.WindowTicks = _historyWindow;
        }

        // ------------------------------------------------------------------------------------ helpers

        /// <summary>One chunk of the scoped grid, registered from the lease row the control plane would have written.</summary>
        private Container Chunk(Vector3Int coord)
        {
            var container = ContainerRegistry.RegisterRuntime(_grid.IdOf(coord), _grid.BoundsOf(coord), new InstanceContainerInfo
            {
                InstanceId = _grid.InstanceId,
                ScopeKey = _grid.ScopeKey,
                PartId = ChunkKeys.PartId(coord),
            });
            ContainerRegistry.ApplyLease(container.ContainerId, W1.Id, W1.Index, 1);
            return container;
        }

        /// <summary>An entity spawned in <paramref name="box"/>'s frame at the frame-local pose given.</summary>
        private NetworkIdentity SpawnInFrame(ushort prefab, Container box, Vector3 local, Quaternion rotation)
        {
            NetworkIdentity e = null;
            W1.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(prefab, Vector3.zero, Quaternion.identity, box.ContentRoot);
                e.transform.SetLocalPositionAndRotation(local, rotation);
                W1.Instance.SpawnServerDriven(e, box);
            });
            Assume.That(e.Container, Is.SameAs(box));
            if (e.transform.parent == box.ContentRoot) e.transform.SetLocalPositionAndRotation(local, rotation);
            return e;
        }

        /// <summary>A plain object (ground, a rock) under <paramref name="parent"/> at a local pose.</summary>
        private Transform Under(Transform parent, string name, Vector3 local, Quaternion rotation)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(local, rotation);
            return go.transform;
        }

        private Quaternion Turn(int frame) => Quaternion.AngleAxis(23.4f + Omega * Mathf.Rad2Deg * frame * Dt, Axis);

        private static void AsClient()
        {
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            Assume.That(PhysicsFrames.RendersFrames);
        }

        // ------------------------------------------------------------------------------------ origin shifts

        /// <summary>
        /// A pawn standing in a framed planet's frame, 205 km from its centre, on a client. Each frame a game's own origin
        /// rule moves the scope's origin to the pawn's chunk, then Nebula's <c>KeepOriginNear</c> runs, then the client
        /// poses its frames, as <c>NebulaClient</c> does at the end of its update. The shift poses the frames at once, so
        /// <c>KeepOriginNear</c> finds the pawn next to the origin and never moves it back: before, it read the pawn a
        /// whole shift away and the origin flipped about 1,000 km each way every frame.
        /// </summary>
        [Test]
        public void AScopeShiftMovesWhatStandsInAFrameAtOnce()
        {
            var pawn = SpawnInFrame(_thingPrefab, _planet.Carried, ChunkLocal + new Vector3(3.2f, -4.7f, -2.2f), Quaternion.identity);
            AsClient();
            _planet.transform.rotation = Turn(0);
            PhysicsFrames.PoseForRender();
            Assume.That(pawn.transform.position.magnitude, Is.GreaterThan(4f * Cell), "the pawn starts far from the origin");

            int gameShifts = 0, ruleShifts = 0;
            string firstRuleShift = null;
            for (int i = 0; i < Frames; i++)
            {
                _planet.transform.rotation = Turn(i);
                // The game's own rule, early in the frame: the origin goes to the pawn's chunk.
                var target = _grid.CoordOf(pawn);
                if (target != _grid.Frame.Cell)
                {
                    _grid.ShiftOrigin(target);
                    gameShifts++;
                }
                // Nebula's rule later in the same frame (NebulaChunkedWorld, before NebulaClient's render pose).
                int count = _grid.Frame.ShiftCount;
                var before = _grid.Frame.Cell;
                _grid.KeepOriginNear(pawn, 4);
                if (_grid.Frame.ShiftCount != count)
                {
                    ruleShifts++;
                    firstRuleShift ??= $"frame {i}: {before} to {_grid.Frame.Cell}, pawn at {pawn.transform.position}";
                    break; // a flipping origin runs away: stop at the first
                }
                PhysicsFrames.PoseForRender();
            }
            TestContext.WriteLine($"{Frames} frames: {gameShifts} shift(s) by the game's rule, {ruleShifts} by KeepOriginNear; origin {_grid.Frame.Cell}, pawn drawn at {pawn.transform.position}");
            Assert.AreEqual(0, ruleShifts, "KeepOriginNear never moved the origin back: " + firstRuleShift);
            Assert.That(gameShifts, Is.InRange(1, 3), "the game's rule moved it once, and again only as the turning planet carried the pawn on");
            Assert.That(pawn.transform.position.magnitude, Is.LessThan(2f * Cell), "the pawn is drawn next to the origin");
        }

        /// <summary>
        /// A worker: a scope's shift moves the history of what stands in the scope's own space, and leaves alone the history
        /// of what stands in a physics frame, which belongs to the frame's own origin (D19).
        /// </summary>
        [Test]
        public void AScopeShiftLeavesTheHistoryOfWhatStandsInAFrameAlone()
        {
            var pawn = SpawnInFrame(_thingPrefab, _planet.Carried, new Vector3(10f, 2f, 20f), Quaternion.identity);
            var rock = W1.SpawnServerDriven(_thingPrefab, _home, _home.transform.position + new Vector3(5f, 0f, 5f), Quaternion.identity);
            W1.Act(() =>
            {
                pawn.RecordAuthoritativeState(1);
                rock.RecordAuthoritativeState(1);
            });
            Assume.That(pawn.StateAt(1).Available && rock.StateAt(1).Available);
            var pawnRecorded = pawn.StateAt(1).Position;
            var pawnAt = pawn.transform.position;
            var rockRecorded = rock.StateAt(1).Position;
            var rockAt = rock.transform.position;

            _grid.ShiftOrigin(new Vector3Int(3, 0, -2));

            var delta = rock.transform.position - rockAt;
            Assume.That(delta.magnitude, Is.GreaterThan(Cell), "the rock moved with its chunk");
            Assert.That(Vector3.Distance(rockRecorded + delta, rock.StateAt(1).Position), Is.LessThan(0.01f), "the rock's history moved with it");
            Assert.AreEqual(pawnAt, pawn.transform.position, "the pawn's simulation pose is the frame's, which did not move");
            Assert.AreEqual(pawnRecorded, pawn.StateAt(1).Position, "and so is its history");
        }
    }
}
