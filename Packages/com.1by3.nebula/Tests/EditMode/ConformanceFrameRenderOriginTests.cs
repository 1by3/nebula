using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 49 (<c>docs/container-tree.md</c> D11, D19, D25): how a client draws a large physics frame
    /// that turns, and how a scope's origin shift reaches what stands in a frame.
    /// <list type="bullet">
    /// <item>A scope's origin shift on a client poses the frames again at once, so an origin rule run later in the same
    /// frame reads what stands in a frame where the shift put it, and never shifts back.</item>
    /// <item>A scope's shift leaves the simulation history of what stands in a frame alone: it belongs to the frame's own
    /// origin.</item>
    /// <item>A frame whose content is drawn far from its own origin (a planet's ground, 205 km from its centre) is drawn
    /// about a render origin near the camera, so the ground holds still against the camera to well under a millimetre
    /// while the planet turns; the frame's world pose is unchanged, prediction still runs at the exact simulation pose,
    /// and a frame inside it (a ship on the planet) is drawn through it.</item>
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
            PhysicsFrames.RenderAnchor = null;
            PhysicsFrames.ContentAnchor = null;
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

        /// <summary><paramref name="t"/>'s position in <paramref name="root"/>'s coordinates, composed from the local poses in double.</summary>
        private static Double3 LocalIn(Transform t, Transform root)
        {
            var p = Double3.Zero;
            for (var x = t; x != null && x != root; x = x.parent) p = PhysicsFrames.Rotate(x.localRotation, p) + x.localPosition;
            return p;
        }

        private static float Length(Double3 v) => (float)System.Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

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
            PhysicsFrames.RenderAnchor = pawn.transform;
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

        // ------------------------------------------------------------------------------------ render origin

        /// <summary>
        /// Run <see cref="Frames"/> frames of the planet turning with a camera riding the ground (placed through the frame's
        /// render pose, as a game places it), and return how far two points of ground near it, in two chunks, move against
        /// the camera beyond the planet's own turn: the worst over the run against where the first frame drew them, and the
        /// worst from one frame to the next. (Where a child 205 km out is drawn at all is rounded once, statically, where
        /// its offset is added to its chunk's in float; that is the float spacing of frame-local coordinates, D24, and does
        /// not move.)
        /// </summary>
        private float WorstAgainstTheCamera(out float worstStep)
        {
            var frame = PlanetFrame;
            var chunkA = Under(frame.Root, "chunk-a", ChunkLocal, ChunkRotation);
            var chunkB = Under(frame.Root, "chunk-b", ChunkLocal + new Vector3(0f, 270f, -380f), ChunkRotation * Quaternion.Euler(0.4f, 0f, 0f));
            var groundA = Under(chunkA, "ground-a", new Vector3(12.5f, 0.3f, -40.25f), Quaternion.identity);
            var groundB = Under(chunkB, "ground-b", new Vector3(-80.75f, 1.1f, 230.5f), Quaternion.identity);
            var camera = new GameObject("camera").transform;
            _objects.Add(camera.gameObject);
            PhysicsFrames.RenderAnchor = camera;
            var cameraLocal = LocalIn(chunkA, frame.Root) + PhysicsFrames.Rotate(ChunkRotation, new Double3(3.25, 1.75, -6.5));
            // The planet's centre where its ground near the camera starts at Unity's origin, as the scope's origin keeps it.
            _planet.transform.SetPositionAndRotation(-(Turn(0) * cameraLocal.ToVector3()), Turn(0));
            camera.position = frame.LocalToRender(cameraLocal.ToVector3());
            var points = new[] { groundA, groundB };
            var first = new Double3[2];
            var last = new Vector3[2];
            float worst = 0f;
            worstStep = 0f;
            for (int i = 0; i < Frames; i++)
            {
                var turn = Turn(i);
                var unturn = Quaternion.Inverse(turn);
                _planet.transform.rotation = turn;
                PhysicsFrames.PoseForRender();
                camera.position = frame.LocalToRenderPrecise(cameraLocal).ToVector3();
                for (int p = 0; p < points.Length; p++)
                {
                    var shown = points[p].position - camera.position;
                    // The offset from the camera in the frame's own axes: it should never change.
                    var local = PhysicsFrames.Rotate(unturn, Double3.From(shown));
                    if (i == 0) first[p] = local;
                    worst = Mathf.Max(worst, Length(local - first[p]));
                    if (i > 0) worstStep = Mathf.Max(worstStep, Length(local - PhysicsFrames.Rotate(Quaternion.Inverse(Turn(i - 1)), Double3.From(last[p]))));
                    last[p] = shown;
                }
            }
            return worst;
        }

        /// <summary>
        /// Ground 205 km from a planet's centre, under a frame turning at about 8.5e-4 rad/s, against a camera standing on
        /// it, over 1,000 frames: drawn within a millimetre of where it belongs, every frame. Without the render origin
        /// the same ground rounds through the turn times 205 km in float, and shakes by centimetres.
        /// </summary>
        [Test]
        public void GroundFarOutOnATurningFrameHoldsStillAgainstTheCamera()
        {
            AsClient();
            float worst = WorstAgainstTheCamera(out float worstStep);
            var origin = PlanetFrame.RenderOrigin;
            PlanetFrame.UseRenderOrigin = false;
            float without = WorstAgainstTheCamera(out float withoutStep);
            TestContext.WriteLine($"{Frames} frames at {Omega} rad/s, 205 km out: worst drift against the camera {worst * 1000f:0.000} mm (frame to frame {worstStep * 1000f:0.000} mm) about render origin {origin}; without a render origin {without * 1000f:0.0} mm (frame to frame {withoutStep * 1000f:0.0} mm)");
            Assert.That(origin.magnitude, Is.GreaterThan(200_000f), "the frame was drawn about a render origin near the camera");
            Assert.That(worst, Is.LessThan(0.001f), "the ground holds within a millimetre against the camera over the run");
            Assert.That(worstStep, Is.LessThan(0.001f), "and it never moves a millimetre from one frame to the next");
            Assert.That(without, Is.GreaterThan(0.002f), "without the render origin the same ground shakes by millimetres or more");
            Assert.AreEqual(Vector3.zero, PlanetFrame.RenderOrigin, "turned off for the frame, it is not used");
        }

        /// <summary>
        /// Under a render origin the frame's world pose is the carrier's: <see cref="PhysicsFrame.RenderPosition"/> and
        /// <see cref="PhysicsFrame.RenderRotation"/> exactly, the root's own pose to float precision, and the frame-local
        /// helpers agree with the carrier's transform. A frame near its own origin (a ship's) is not split, and a worker
        /// never is.
        /// </summary>
        [Test]
        public void TheFramesWorldPoseIsUnchanged()
        {
            var frame = PlanetFrame;
            var ground = Under(frame.Root, "ground", ChunkLocal, ChunkRotation);
            Assert.AreEqual(Vector3.zero, frame.Root.position, "a worker's root at its simulation pose");

            AsClient();
            var camera = new GameObject("camera").transform;
            _objects.Add(camera.gameObject);
            PhysicsFrames.RenderAnchor = camera;
            _planet.transform.SetPositionAndRotation(new Vector3(-1250.5f, -117000f, -167500f), Turn(77));
            camera.position = _planet.transform.TransformPoint(ChunkLocal);
            PhysicsFrames.PoseForRender();

            var t = _planet.transform;
            Assert.That(frame.RenderOrigin.magnitude, Is.GreaterThan(200_000f), "a render origin is in use");
            Assert.IsTrue(frame.RenderPosition.Equals(t.position), "the render position is the carrier's, exactly");
            Assert.IsTrue(frame.RenderRotation.Equals(t.rotation), "the render rotation is the carrier's, exactly");
            Assert.AreEqual(t.lossyScale, frame.RenderScale);
            Assert.That(Quaternion.Angle(t.rotation, frame.Root.rotation), Is.LessThan(1e-3f), "the root's rotation is the carrier's");
            Assert.That(Vector3.Distance(t.position, frame.Root.position), Is.LessThan(0.05f), "the root's position is the carrier's to float precision");
            Assert.That((frame.Root.lossyScale - Vector3.one).magnitude, Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(t.TransformPoint(ChunkLocal), ground.position), Is.LessThan(0.05f), "a child is where the carrier's pose puts it");
            Assert.That(Vector3.Distance(t.TransformPoint(ChunkLocal), frame.LocalToRender(ChunkLocal)), Is.LessThan(0.05f), "LocalToRender agrees with the carrier's transform");
            var back = frame.RenderToLocalPrecise(frame.LocalToRenderPrecise(Double3.From(ChunkLocal)));
            Assert.That(Length(back - ChunkLocal), Is.LessThan(1e-6f), "RenderToLocal undoes LocalToRender");
            Assert.That(Vector3.Distance(frame.RenderToLocal(ground.position), ChunkLocal), Is.LessThan(0.05f));
            Assert.That(Quaternion.Angle(frame.LocalToRender(ChunkRotation), ground.rotation), Is.LessThan(1e-3f));
            Assert.That(Quaternion.Angle(frame.RenderToLocal(ground.rotation), ChunkRotation), Is.LessThan(1e-3f));

            // The camera back near the planet's own origin: no render origin.
            camera.position = _planet.transform.TransformPoint(new Vector3(100f, 900f, -300f));
            PhysicsFrames.PoseForRender();
            Assert.AreEqual(Vector3.zero, frame.RenderOrigin, "within the threshold of the frame's own origin, none");
            Assert.IsTrue(frame.Root.position.Equals(t.position), "and the root is at the carrier's pose exactly");
            Assert.IsTrue(frame.Root.rotation.Equals(t.rotation));
        }

        /// <summary>
        /// Prediction under a render origin: every step puts the root at its simulation pose exactly, with the pivot at
        /// the identity, so a predicted body's pose is bit for bit what it would be under a root that was never split, and
        /// the frame goes back to its turned render pose after. Over many frames of a turning planet.
        /// </summary>
        [Test]
        public void PredictionStaysExactUnderARenderOrigin()
        {
            var frame = PlanetFrame;
            var box = _planet.Carried;
            var chunk = Under(frame.Root, "chunk", ChunkLocal, ChunkRotation);
            var pawn = Under(chunk, "pawn", new Vector3(3.25f, 0.9f, -6.5f), Quaternion.Euler(0f, 31f, 0f));
            var probe = Under(chunk, "probe", new Vector3(-150.125f, 2.5f, 377.75f), Quaternion.Euler(0f, -12f, 0f));
            // The same hierarchy under a plain root, the way a worker holds it.
            var reference = new GameObject("reference-root").transform;
            _objects.Add(reference.gameObject);
            var refChunk = Under(reference, "ref-chunk", ChunkLocal, ChunkRotation);
            var refPawn = Under(refChunk, "ref-pawn", pawn.localPosition, pawn.localRotation);
            var refProbe = Under(refChunk, "ref-probe", probe.localPosition, probe.localRotation);

            AsClient();
            PhysicsFrames.RenderAnchor = pawn;
            _planet.transform.position = -(Turn(0) * ChunkLocal);
            int split = 0;
            for (int i = 0; i < 120; i++)
            {
                _planet.transform.rotation = Turn(i * 50);
                PhysicsFrames.PoseForRender();
                if (frame.RenderOrigin != Vector3.zero) split++;
                var drawn = pawn.position;

                PhysicsFrames.BeginSimulation(box, pawn);
                Assert.IsTrue(PhysicsFrames.InSimulationPose(box));
                Assert.IsTrue(frame.Root.position.Equals(-frame.Origin), "the root at the identity less the origin, exactly");
                Assert.IsTrue(frame.Root.rotation.Equals(Quaternion.identity));
                Assert.IsTrue(frame.Root.lossyScale.Equals(Vector3.one));
                reference.SetPositionAndRotation(-frame.Origin, Quaternion.identity);
                Assert.IsTrue(pawn.position.Equals(refPawn.position), $"frame {i}: the pawn simulates where an unsplit root puts it, bit for bit");
                Assert.IsTrue(pawn.rotation.Equals(refPawn.rotation));
                Assert.IsTrue(probe.position.Equals(refProbe.position));
                Assert.IsTrue(frame.LocalToSimulation(ChunkLocal).Equals(chunk.position), "frame-local to simulation agrees with the root");
                PhysicsFrames.EndSimulation();

                Assert.IsFalse(PhysicsFrames.InSimulationPose(box));
                Assert.That(Vector3.Distance(drawn, pawn.position), Is.LessThan(1e-4f), "back where it was drawn");
            }
            Assert.That(frame.Origin.magnitude, Is.GreaterThan(200_000f), "the simulation origin followed the pawn");
            Assert.AreEqual(120, split, "every frame was drawn about a render origin");
        }

        /// <summary>
        /// A ship standing on the turning planet, 205 km from its centre, with a crew member aboard in the ship's own frame.
        /// The planet is drawn about a render origin near the crew member; the ship's frame, posed from the ship as it is
        /// drawn, is near its own origin and needs none. The crew member holds still against the ground beside the ship to
        /// within a millimetre.
        /// </summary>
        [Test]
        public void AShipOnAPlanetIsDrawnThroughThePlanetsRenderOrigin()
        {
            var frame = PlanetFrame;
            var shipLocal = ChunkLocal + new Vector3(4f, -3f, 2f);
            var shipRotation = ChunkRotation * Quaternion.Euler(0f, 40f, 0f);
            var ship = SpawnInFrame(_shipPrefab, _planet.Carried, shipLocal, shipRotation);
            var shipFrame = ship.Carried != null ? ship.Carried.Frame : null;
            Assume.That(shipFrame, Is.Not.Null, "the ship has its frame");
            var crewLocal = new Vector3(1.25f, 1f, 2.5f);
            var crew = Under(shipFrame.Root, "crew", crewLocal, Quaternion.identity);
            var rock = Under(frame.Root, "rock", shipLocal + ChunkRotation * new Vector3(30f, 0f, 12f), Quaternion.identity);

            AsClient();
            PhysicsFrames.RenderAnchor = crew;
            _planet.transform.SetPositionAndRotation(-(Turn(0) * shipLocal), Turn(0));
            var crewInPlanet = Double3.From(shipLocal) + PhysicsFrames.Rotate(shipRotation, Double3.From(crewLocal));
            var rockInPlanet = LocalIn(rock, frame.Root);
            float worst = 0f;
            for (int i = 0; i < Frames; i++)
            {
                var turn = Turn(i);
                _planet.transform.rotation = turn;
                PhysicsFrames.PoseForRender();
                var shown = rock.position - crew.position;
                var truth = PhysicsFrames.Rotate(turn, rockInPlanet - crewInPlanet);
                worst = Mathf.Max(worst, Length(Double3.From(shown) - truth));
            }
            TestContext.WriteLine($"a ship 205 km out on a planet turning at {Omega} rad/s: worst error of the ground against the crew {worst * 1000f:0.000} mm over {Frames} frames; planet render origin {frame.RenderOrigin}, ship's {shipFrame.RenderOrigin}");
            Assert.That(frame.RenderOrigin.magnitude, Is.GreaterThan(200_000f), "the planet is drawn about a render origin near the crew");
            Assert.AreEqual(Vector3.zero, shipFrame.RenderOrigin, "the ship's frame needs none");
            Assert.That(Vector3.Distance(ship.transform.position, shipFrame.RenderPosition), Is.LessThan(1e-6f), "the ship's frame is drawn at the ship");
            Assert.That(worst, Is.LessThan(0.001f), "the crew member holds still against the ground beside the ship");
        }
    }
}
