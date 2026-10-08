using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 49, next to <see cref="ConformanceFrameRenderOriginTests"/> (<c>docs/container-tree.md</c> D25):
    /// the scene render origin on a client with no physics frames, and what is left of the render origins once Play ends.
    /// </summary>
    [Category("Conformance")]
    public sealed class FrameRenderOriginSessionTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private bool _wasServer, _wasClient, _hooked;

        [SetUp]
        public void SetUp()
        {
            _wasServer = NebulaRuntime.IsServer;
            _wasClient = NebulaRuntime.IsClient;
            _hooked = PhysicsFrames.DrawHooksInstalled;
            Assume.That(PhysicsFrames.All.Count, Is.Zero, "no physics frames left from another test");
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
        }

        [TearDown]
        public void TearDown()
        {
            PhysicsFrames.EndDrawing();
            PhysicsFrames.RenderAnchor = null;
            PhysicsFrames.ContentAnchor = null;
            SceneRenderOrigin.ResetForNewSession();
            SceneRenderOrigin.Enabled = true;
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            if (_hooked) PhysicsFrames.InstallDrawHooks();
            else PhysicsFrames.RemoveDrawHooks();
            NebulaRuntime.IsServer = _wasServer;
            NebulaRuntime.IsClient = _wasClient;
        }

        private Transform Make(string name, Vector3 position)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            go.transform.position = position;
            return go.transform;
        }

        /// <summary>A camera 20 km from Unity's origin, which never draws by itself: the hooks are called by hand.</summary>
        private List<Camera> FarCamera()
        {
            var camera = Make("far-camera", new Vector3(20_000.3f, 120.7f, -8_000.2f)).gameObject.AddComponent<Camera>();
            camera.enabled = false;
            return new List<Camera> { camera };
        }

        /// <summary>
        /// A client with no physics frames still gets the scene render origin: posing the frames installs the draw hooks
        /// before it finds there is nothing to pose, and the scene is moved near a far camera while it draws.
        /// </summary>
        [Test]
        public void TheSceneRenderOriginWorksWithNoPhysicsFrames()
        {
            PhysicsFrames.RemoveDrawHooks();
            Assume.That(PhysicsFrames.DrawHooksInstalled, Is.False);
            PhysicsFrames.PoseForRender();
            Assert.IsTrue(PhysicsFrames.DrawHooksInstalled, "posing no frames still installs the draw hooks");

            var cameras = FarCamera();
            var rock = Make("rock", new Vector3(20_010.5f, 118.25f, -8_004.75f));
            var rockAt = rock.position;
            var raised = new List<Vector3>();
            Action<Vector3> drawing = offset => raised.Add(offset);
            SceneRenderOrigin.Drawing += drawing;
            PhysicsFrames.BeginDrawing(cameras);
            var offsetWhileDrawing = SceneRenderOrigin.Offset;
            var rockWhileDrawing = rock.position;
            PhysicsFrames.EndDrawing();
            SceneRenderOrigin.Drawing -= drawing;

            Assert.That(offsetWhileDrawing.magnitude, Is.GreaterThan(15_000f), "the scene is moved near the camera");
            Assert.That(Vector3.Distance(rockWhileDrawing, rockAt - offsetWhileDrawing), Is.LessThan(0.01f), "the rock with it");
            Assert.That(raised, Is.EqualTo(new List<Vector3> { offsetWhileDrawing }), "Drawing is raised once, with the offset");
            Assert.IsTrue(rock.position.Equals(rockAt), "and put back exactly");
        }

        /// <summary>
        /// Play ending in an Editor that keeps its domain puts back what was moved for drawing and forgets the session's
        /// anchors and handlers, so EditMode tests and editor tools start clean. The settings are kept.
        /// </summary>
        [Test]
        public void EndingPlayForgetsTheRenderAnchors()
        {
            var cameras = FarCamera();
            var anchor = Make("anchor", Vector3.one);
            var rock = Make("rock", new Vector3(20_010.5f, 118.25f, -8_004.75f));
            var rockAt = rock.position;
            int raised = 0;
            SceneRenderOrigin.Shifted += _ => raised++;
            SceneRenderOrigin.Drawing += _ => raised++;
            SceneRenderOrigin.Restoring += _ => raised++;
            float step = SceneRenderOrigin.Step;
            SceneRenderOrigin.Step = 32f;
            try
            {
                PhysicsFrames.RenderAnchor = anchor;
                PhysicsFrames.ContentAnchor = anchor;
                PhysicsFrames.BeginDrawing(cameras);
                Assume.That(PhysicsFrames.Drawing);
                Assume.That(SceneRenderOrigin.Offset, Is.Not.EqualTo(Vector3.zero));

                PhysicsFrames.ResetAfterPlay();
                Assert.IsNull(PhysicsFrames.RenderAnchor, "the render anchor is forgotten");
                Assert.IsNull(PhysicsFrames.ContentAnchor, "and the content anchor");
                Assert.IsFalse(PhysicsFrames.Drawing, "drawing has ended");
                Assert.AreEqual(Vector3.zero, SceneRenderOrigin.Offset);
                Assert.IsTrue(rock.position.Equals(rockAt), "what was moved is back exactly");
                Assert.AreEqual(32f, SceneRenderOrigin.Step, "settings are kept");

                raised = 0;
                PhysicsFrames.BeginDrawing(cameras);
                PhysicsFrames.EndDrawing();
                Assert.AreEqual(0, raised, "the session's handlers are dropped");
            }
            finally
            {
                SceneRenderOrigin.Step = step;
            }
        }
    }
}
