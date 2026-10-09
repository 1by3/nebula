using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// A physics frame's velocity is the change in its carrier's scene position between ticks
    /// (<c>docs/container-tree.md</c> D14). A floating-origin shift moves the carrier in scene space without moving it:
    /// the frame's last motion sample moves with it, so the tick after a shift reads the carrier's true velocity, not the
    /// shift divided by one tick, and a body crossing into the frame on that tick keeps its velocity.
    /// </summary>
    public sealed class FrameOriginShiftMotionTests
    {
        private const float Dt = 1f / 60f;

        private ConformanceMesh _mesh;
        private WorldDefinition _definition;
        private Container _scope;
        private NetworkIdentity _planet;
        private ConformanceMesh.Worker W1 => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = Vector3.one * 1000f;
            NebulaWorld.LoadRuntime(_definition);
            _mesh = new ConformanceMesh(1);
            _scope = ContainerRegistry.RegisterRuntime(900, ContainerPlacement.Root(new Double3(0.0, 0.0, 0.0), new Vector3(200f, 200f, 200f)));
            Assert.IsNotNull(_scope);
            ContainerRegistry.ApplyLease(_scope.ContainerId, W1.Id, W1.Index, 1);

            var planet = new GameObject("planet-prefab");
            planet.AddComponent<NetworkIdentity>();
            var box = planet.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = Vector3.one * 10_000f;
            box.Center = Vector3.zero;
            box.OwnPhysicsFrame = true;
            planet.AddComponent<NetworkTransform>();
            var prefab = _mesh.RegisterPrefab(planet);
            _planet = W1.SpawnServerDriven(prefab, _scope, _scope.transform.position, Quaternion.identity);
            Assert.IsNotNull(_planet.Carried?.Frame, "the planet carries a framed container");
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_definition);
        }

        private PhysicsFrame Frame => _planet.Carried.Frame;

        private void SampleAtRest(ref uint tick, int ticks)
        {
            for (int i = 0; i < ticks; i++, tick++) Frame.Sample(tick, Dt);
            Assert.That(Frame.State.Velocity.magnitude, Is.LessThan(1e-3f), "the planet is at rest before the shift");
        }

        [Test]
        public void AFrameAtRestReadsNoVelocityAcrossAnOriginShift()
        {
            uint tick = 100;
            Frame.ResetMotion();
            SampleAtRest(ref tick, 3);
            var before = _planet.transform.position;
            NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(50_000, 0, -20_000)); // 50,000 km out
            Assert.That((_planet.transform.position - before).magnitude, Is.GreaterThan(1e6f), "the shift moved the carrier in scene space");
            Frame.Sample(tick++, Dt);
            Assert.That(Frame.State.Velocity.magnitude, Is.LessThan(1f), "the tick after the shift reads no velocity: " + Frame.State.Velocity);
            Assert.That(Frame.State.Acceleration.magnitude, Is.LessThan(60f), "nor an acceleration: " + Frame.State.Acceleration);
        }

        [Test]
        public void AMovingFrameReadsItsOwnVelocityAcrossAnOriginShift()
        {
            var v = new Vector3(120f, 0f, -45f);
            uint tick = 100;
            Frame.ResetMotion();
            for (int i = 0; i < 3; i++, tick++) { _planet.transform.position += v * Dt; Frame.Sample(tick, Dt); }
            NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(-3, 0, 4));
            _planet.transform.position += v * Dt;
            Frame.Sample(tick++, Dt);
            Assert.That((Frame.State.Velocity - v).magnitude, Is.LessThan(0.5f), "the carrier's own velocity: " + Frame.State.Velocity);
        }

        [Test]
        public void ABodyEnteringTheFrameOnTheShiftTickKeepsItsVelocity()
        {
            uint tick = 100;
            Frame.ResetMotion();
            SampleAtRest(ref tick, 3);
            NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(50_000, 0, 0));
            Frame.Sample(tick++, Dt);
            var hull = new Vector3(-285f, -8f, -958f);
            var entered = PhysicsFrames.ConvertVelocity(hull, _planet.transform.position, null, _planet.Carried);
            Assert.That((entered - hull).magnitude, Is.LessThan(1f), "a body crossing into a frame at rest keeps its velocity: " + entered);
        }

        [Test]
        public void AFramesSampleFollowsACarrierMovedInsideARebase()
        {
            // The pattern RuntimeGrid.ShiftOrigin and PhysicsFrames.ShiftOrigin use: carriers measured before and after.
            uint tick = 100;
            Frame.ResetMotion();
            SampleAtRest(ref tick, 3);
            PhysicsFrames.BeginRebase();
            _planet.transform.position += new Vector3(4.5e7f, 0f, -1.2e7f);
            PhysicsFrames.Rebase(0UL, new Vector3(1f, 1f, 1f)); // ignored inside a measured pair
            PhysicsFrames.EndRebase();
            Frame.Sample(tick++, Dt);
            Assert.That(Frame.State.Velocity.magnitude, Is.LessThan(1f), "the moved carrier reads no velocity: " + Frame.State.Velocity);
        }
    }
}
