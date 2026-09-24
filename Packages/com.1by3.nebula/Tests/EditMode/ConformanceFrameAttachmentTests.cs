using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 25 (<c>docs/frame-bodies.md</c> §2): attaching an entity to a container. An attached
    /// entity's container is pinned: the worker's tick never re-resolves it from its position, but the owner check still
    /// hands it to whichever worker owns the container.
    /// <para>
    /// Tier B: real <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/>, one whole tick at a time, with
    /// Editor preview scenes for physics frames. The container registry is process-wide, so of two workers' copies of
    /// one carrier only the last registered owns the box and its frame; scenarios that hand a ship over assert only on
    /// the receiving worker's copies.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFrameAttachmentTests
    {
        private ConformanceMesh _mesh;
        private Container _west, _east;
        private ushort _propPrefab;

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
        }

        [TearDown]
        public void TearDown()
        {
            _mesh?.Dispose();
            _mesh = null;
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            Assert.AreEqual(0, PhysicsFrames.All.Count, "every frame was released with its container");
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        /// <summary>Two workers; two chunks side by side at x = 0, west leased to w1 and east to w2.</summary>
        private void TwoChunks()
        {
            _mesh = new ConformanceMesh(2);
            _mesh.Config.HandoverHysteresis = 0.5f;
            _west = _mesh.AddStaticContainer("west", new Vector3(-32f, 0f, 0f), new Vector3(64f, 40f, 64f));
            _east = _mesh.AddStaticContainer("east", new Vector3(32f, 0f, 0f), new Vector3(64f, 40f, 64f));
            _mesh.SetOwner(_west, W1);
            _mesh.SetOwner(_east, W2);
            var prop = new GameObject("prop-prefab");
            prop.AddComponent<NetworkIdentity>();
            _propPrefab = _mesh.RegisterPrefab(prop);
        }

        // ------------------------------------------------------------------------------------ the pin (D8)

        [Test]
        public void APinnedEntityIsNotResolvedIntoTheNeighbouringChunkButFollowsARedeal()
        {
            TwoChunks();
            var pinned = W1.SpawnServerDriven(_propPrefab, _west, new Vector3(-1f, 1f, 0f), Quaternion.identity);
            var loose = W1.SpawnServerDriven(_propPrefab, _west, new Vector3(-1f, 1f, 4f), Quaternion.identity);
            pinned.ContainerPinned = true;

            // Both drift 2 m into the east chunk, well past the hysteresis.
            pinned.transform.position = new Vector3(2f, 1f, 0f);
            loose.transform.position = new Vector3(2f, 1f, 4f);
            W1.Tick(1);
            Assert.AreSame(_east, loose.Container, "an unpinned entity is resolved into the chunk it stands in");
            Assert.IsFalse(loose.HasAuthority, "and handed to that chunk's worker");
            Assert.AreSame(_west, pinned.Container, "a pinned one keeps its container");
            Assert.IsTrue(pinned.HasAuthority, "and its worker");

            // The west chunk is re-dealt to w2: the pinned entity goes with it.
            _mesh.SetOwner(_west, W2);
            W1.Tick(2);
            Assert.IsFalse(pinned.HasAuthority, "the owner check still runs for a pinned entity");
            _mesh.Pump();
            var theirs = W2.Find(pinned.NetId);
            Assert.IsTrue(theirs.HasAuthority);
            Assert.AreSame(_west, theirs.Container, "still in the chunk it was pinned to");
        }
    }
}
