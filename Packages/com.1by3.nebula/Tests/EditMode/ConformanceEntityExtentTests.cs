using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 28 (<c>docs/conformance-suite.md</c>, design <c>docs/entity-extents.md</c>): a large entity
    /// is ghosted to every worker whose container its extent comes within the ghost band's margin of, not only where
    /// its root is, and a player simulated there collides with it; an entity without an extent is ghosted exactly as
    /// before.
    /// <para>
    /// Tier B: two to four <b>real</b> <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/>, whose own
    /// ghost band decides what to send and whose own <c>Dispatch</c> instantiates the ghost. Tier A cannot say this: a
    /// <c>FakeWorker</c> implements the protocol, not the ghost band. The mesh is one process, so the authoritative
    /// copy and the ghost share one physics scene: the collision is probed with the authority's collider switched
    /// off, which is what the neighbouring worker's process holds — the ghost alone.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceEntityExtentTests
    {
        private const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        private ConformanceMesh _mesh;
        private readonly List<GameObject> _objects = new List<GameObject>();

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
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            _mesh?.Dispose();
            _mesh = null;
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ fixtures

        /// <summary>What the inspector writes for an authored extent: the serialized fields, not the runtime API.</summary>
        private static void Author(NetworkIdentity identity, EntityExtentSource source, Bounds box = default)
        {
            typeof(NetworkIdentity).GetField("_extentSource", Private).SetValue(identity, source);
            if (source == EntityExtentSource.Explicit) typeof(NetworkIdentity).GetField("_extent", Private).SetValue(identity, box);
        }

        /// <summary>
        /// A static structure: one 100 x 20 x 100 m box collider standing on its root. With <paramref name="extent"/>
        /// its extent is authored as <see cref="EntityExtentSource.Colliders"/>, so the ghost band measures the box.
        /// </summary>
        private ushort StructurePrefab(bool extent)
        {
            var prefab = new GameObject(extent ? "structure-with-extent" : "structure");
            var identity = prefab.AddComponent<NetworkIdentity>();
            var wall = prefab.AddComponent<BoxCollider>();
            wall.center = new Vector3(0f, 10f, 0f);
            wall.size = new Vector3(100f, 20f, 100f);
            if (extent) Author(identity, EntityExtentSource.Colliders);
            return _mesh.RegisterPrefab(prefab);
        }

        /// <summary>
        /// Two workers; two 200 m containers side by side with their seam at x = 0, west leased to w1 and east to w2.
        /// </summary>
        private void TwoRegions()
        {
            _mesh = new ConformanceMesh(2);
            var west = _mesh.AddStaticContainer("west", new Vector3(-100f, 0f, 0f), new Vector3(200f, 60f, 200f));
            var east = _mesh.AddStaticContainer("east", new Vector3(100f, 0f, 0f), new Vector3(200f, 60f, 200f));
            _mesh.SetOwner(west, W1);
            _mesh.SetOwner(east, W2);
        }

        private Container West => ContainerRegistry.FindById("west");

        /// <summary>
        /// A player on w2's side walking 12 m towards the seam from 6 m east of it, as w2 simulates it: a
        /// <see cref="CharacterController"/> moved against whatever colliders w2 holds. Returns where it stopped.
        /// </summary>
        private float WalkTowardsTheSeam()
        {
            var player = new GameObject("player");
            _objects.Add(player);
            player.transform.position = new Vector3(6f, 0.1f, 0f);
            var controller = player.AddComponent<CharacterController>();
            controller.center = new Vector3(0f, 1f, 0f);
            controller.height = 2f;
            controller.radius = 0.5f;
            Physics.SyncTransforms();
            controller.Move(new Vector3(-12f, 0f, 0f));
            return player.transform.position.x;
        }

        /// <summary>A capsule cast along the same walk: what a hitscan or a movement query on w2 would hit.</summary>
        private static bool SomethingBlocksTheWalk(out RaycastHit hit)
        {
            Physics.SyncTransforms();
            return Physics.CapsuleCast(new Vector3(6f, 0.6f, 0f), new Vector3(6f, 1.6f, 0f), 0.5f, Vector3.left, out hit, 12f);
        }

        /// <summary>The workers <paramref name="worker"/> ghosts <paramref name="netId"/> to right now, from its own band table.</summary>
        private static Dictionary<string, float> GhostTargets(ConformanceMesh.Worker worker, ulong netId)
        {
            var table = (Dictionary<ulong, Dictionary<string, float>>)worker.GetField("_ghostTargets");
            return table.TryGetValue(netId, out var targets) ? targets : new Dictionary<string, float>();
        }

        // ------------------------------------------------------------------------------------ the issue's acceptance

        [Test]
        public void AStructureRootedFiftyMetresFromTheSeamIsGhostedByItsExtentAndBlocksAPlayer()
        {
            TwoRegions();
            var prefab = StructurePrefab(extent: true);
            // Rooted 50 m from the seam: its 100 m box spans x in [-100, 0], so its east wall is the seam.
            var structure = W1.SpawnServerDriven(prefab, West, new Vector3(-50f, 0f, 0f), Quaternion.identity);
            Assert.AreEqual(50f, NebulaWorker.SeamDistance(West, structure.transform.position, ContainerRegistry.FindById("east")), 1e-3f,
                "by its root it is far outside the 4 m band");
            W1.Tick(1);
            _mesh.Pump();

            var ghost = W2.Find(structure.NetId);
            Assert.IsNotNull(ghost, "the neighbouring worker holds it, because its body reaches the seam");
            Assert.IsFalse(ghost.HasAuthority);
            Assert.IsTrue(structure.HasAuthority, "and authority stays with the worker that owns its root's container");
            Assert.AreEqual(1, _mesh.DeliveredOf(MsgId.GhostSpawn, W2.Id).Count);

            // What w2's process holds is the ghost alone: switch the authoritative copy's collider off and walk.
            structure.GetComponent<BoxCollider>().enabled = false;
            Assert.IsTrue(SomethingBlocksTheWalk(out var hit), "a movement query on w2 hits the ghost's wall");
            Assert.AreSame(ghost.gameObject, hit.collider.gameObject);
            float stopped = WalkTowardsTheSeam();
            Assert.Greater(stopped, 0.3f, "the player simulated on w2 stops at the wall instead of walking through it");
            Assert.Less(stopped, 1f);

            // The band keeps it there, tick after tick, with no second spawn.
            W1.Tick(2);
            _mesh.Pump();
            Assert.AreEqual(1, _mesh.DeliveredOf(MsgId.GhostSpawn, W2.Id).Count);
            Assert.IsNotNull(W2.Find(structure.NetId));
        }

        [Test]
        public void WithoutAnExtentTheSameStructureIsNotGhostedAndThePlayerWalksThrough()
        {
            TwoRegions();
            var prefab = StructurePrefab(extent: false);
            var structure = W1.SpawnServerDriven(prefab, West, new Vector3(-50f, 0f, 0f), Quaternion.identity);
            W1.Tick(1);
            _mesh.Pump();

            Assert.IsNull(W2.Find(structure.NetId), "measured by its root, as every entity without an extent always was");
            Assert.AreEqual(0, _mesh.DeliveredOf(MsgId.GhostSpawn).Count);
            structure.GetComponent<BoxCollider>().enabled = false;
            Assert.IsFalse(SomethingBlocksTheWalk(out _));
            Assert.Less(WalkTowardsTheSeam(), -5f, "the gap the issue describes: nothing on w2 to collide with");
        }

        [Test]
        public void AnEntityWithoutAnExtentIsStillGhostedByItsRoot()
        {
            TwoRegions();
            var prefab = StructurePrefab(extent: false);
            var near = W1.SpawnServerDriven(prefab, West, new Vector3(-3f, 0f, 0f), Quaternion.identity);
            W1.Tick(1);
            _mesh.Pump();
            Assert.IsNotNull(W2.Find(near.NetId), "3 m from the seam, inside the band: unchanged");
        }

        [TestCase(-60f, false, TestName = "AnExtentTenMetresShortOfTheSeamIsNotGhosted")]
        [TestCase(-53f, true, TestName = "AnExtentThreeMetresShortOfTheSeamIsGhosted")]
        public void TheMarginIsMeasuredFromTheExtent(float rootX, bool ghosted)
        {
            TwoRegions();
            var prefab = StructurePrefab(extent: true);
            var structure = W1.SpawnServerDriven(prefab, West, new Vector3(rootX, 0f, 0f), Quaternion.identity);
            W1.Tick(1);
            _mesh.Pump();
            Assert.AreEqual(ghosted, W2.Find(structure.NetId) != null);
        }

        // ------------------------------------------------------------------------------------ a structure in a chunked world

        private const string Outpost = "world/outpost";
        private static readonly Vector3 Cell = new Vector3(64f, 64f, 64f);

        private static Container Chunk(RuntimeGrid grid, Vector3Int coord, ConformanceMesh.Worker owner)
        {
            var container = ContainerRegistry.RegisterRuntime(grid.IdOf(coord), grid.BoundsOf(coord), new InstanceContainerInfo
            {
                InstanceId = grid.InstanceId,
                ScopeKey = grid.ScopeKey,
                PartId = ChunkKeys.PartId(coord),
            });
            ContainerRegistry.ApplyLease(container.ContainerId, owner.Id, owner.Index, 1);
            return container;
        }

        [Test]
        public void AStructureAttachedToAChunkIsGhostedToEveryWorkerItsExtentReaches()
        {
            // Four workers over a scoped chunk grid of 64 m cells, along x: chunks -2..0 are w1's, 1 is w2's, 2 is
            // w3's, 3 is w4's, and so is the chunk north of the anchor.
            _mesh = new ConformanceMesh(4);
            var W3 = _mesh[2];
            var W4 = _mesh[3];
            var grid = new RuntimeGrid(Cell, planar: false, scopeKey: Outpost);
            Chunk(grid, new Vector3Int(-2, 0, 0), W1);
            Chunk(grid, new Vector3Int(-1, 0, 0), W1);
            var anchor = Chunk(grid, Vector3Int.zero, W1);
            Chunk(grid, new Vector3Int(1, 0, 0), W2);
            Chunk(grid, new Vector3Int(2, 0, 0), W3);
            Chunk(grid, new Vector3Int(3, 0, 0), W4);
            Chunk(grid, new Vector3Int(0, 0, 1), W4);

            // A hidden structure section: no collider on the entity itself, attached to its chunk.
            var prefab = new GameObject("structure-section");
            prefab.AddComponent<NetworkIdentity>();
            prefab.AddComponent<FrameAttachment>();
            var prefabId = _mesh.RegisterPrefab(prefab);
            var section = W1.SpawnServerDriven(prefabId, anchor, new Vector3(32f, 32f, 32f), Quaternion.identity);
            W1.Act(() => Assert.IsTrue(section.GetComponent<FrameAttachment>().Attach()));

            W1.Tick(1);
            _mesh.Pump();
            Assert.IsNull(W2.Find(section.NetId), "by its root, 32 m from every seam, it is nobody else's");

            // The section is built out to 200 m along x (x in [-68, 132]) and 40 m deep (z in [12, 52]).
            section.SetExtent(new Bounds(Vector3.zero, new Vector3(200f, 20f, 40f)));
            W1.Tick(2);
            _mesh.Pump();
            Assert.IsNotNull(W2.Find(section.NetId), "w2's chunk, next to the anchor, is under it");
            Assert.IsNotNull(W3.Find(section.NetId), "w3's chunk does not touch the anchor, and the extent still reaches it");
            Assert.IsNull(W4.Find(section.NetId), "w4's chunks are 60 m and 12 m away: none further than the margin");
            Assert.IsTrue(section.HasAuthority);
            Assert.AreSame(anchor, section.Container, "the extent never moves the entity or its authority");

            // An edit shrinks it to 100 m (x in [-18, 82]): w3's ghost lingers, then goes.
            section.SetExtent(new Bounds(Vector3.zero, new Vector3(100f, 20f, 40f)));
            W1.Tick(3);
            _mesh.Pump();
            Assert.IsNotNull(W3.Find(section.NetId), "a ghost the extent no longer reaches lingers, as for a root that left the band");
            GhostTargets(W1, section.NetId)[W3.Id] = -1000f; // the linger has run out
            W1.Tick(4);
            _mesh.Pump();
            Assert.IsNull(W3.Find(section.NetId), "then it is despawned");
            Assert.IsNotNull(W2.Find(section.NetId), "while w2, still under it, keeps its ghost");

            // The anchor is re-dealt to w2: the attached section goes with it, and so does its edited extent.
            ContainerRegistry.ApplyLease(anchor.ContainerId, W2.Id, W2.Index, 2);
            W1.Tick(5);
            _mesh.Pump();
            var theirs = W2.Find(section.NetId);
            Assert.IsTrue(theirs.HasAuthority);
            Assert.AreEqual(EntityExtentSource.Explicit, theirs.ExtentSource);
            Assert.AreEqual(new Vector3(100f, 20f, 40f), theirs.Extent.size, "the runtime extent travelled with the handover");
            Assert.IsTrue(theirs.ExtentChangedAtRuntime, "and will travel on");
            W2.Tick(6);
            _mesh.Pump();
            var targets = GhostTargets(W2, section.NetId);
            Assert.IsTrue(targets.ContainsKey(W1.Id), "w2 ghosts it back to w1, whose chunk -1 the extent covers");
            Assert.IsFalse(targets.ContainsKey(W3.Id));
            Assert.IsFalse(targets.ContainsKey(W4.Id));
        }

        [Test]
        public void TheExtentIsMeasuredInThePlanetsOwnCoordinates()
        {
            // A planet that is a carrier with its own physics frame, far out in space and turned, with two 64 m
            // chunks leased in its frame 7 km from its centre: A to w1, B (east of A) to w2.
            _mesh = new ConformanceMesh(2);
            var space = _mesh.AddStaticContainer("system", Vector3.zero, new Vector3(200000f, 200000f, 200000f));
            space.Center = Vector3.zero;
            ContainerRegistry.Rebuild();
            _mesh.SetOwner(space, W1);
            var planetPrefab = new GameObject("planet-prefab");
            planetPrefab.AddComponent<NetworkIdentity>();
            var box = planetPrefab.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = Vector3.one * 20000f;
            box.Center = Vector3.zero;
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            planetPrefab.AddComponent<NetworkTransform>();
            var planet = W1.SpawnServerDriven(_mesh.RegisterPrefab(planetPrefab), space, new Vector3(30000f, 0f, 0f), Quaternion.Euler(0f, 17f, 0f));

            var aCentre = new Vector3(7032f, -968f, 32f);
            var chunkA = ContainerRegistry.RegisterRuntime(3000, ContainerPlacement.Child(planet.Carried.ContainerId, aCentre, Cell, ContainerAuthority.Leased));
            var chunkB = ContainerRegistry.RegisterRuntime(3001, ContainerPlacement.Child(planet.Carried.ContainerId, aCentre + new Vector3(64f, 0f, 0f), Cell, ContainerAuthority.Leased));
            ContainerRegistry.ApplyLease(chunkA.ContainerId, W1.Id, W1.Index, 1);
            ContainerRegistry.ApplyLease(chunkB.ContainerId, W2.Id, W2.Index, 1);
            Assume.That(chunkA.Space, Is.SameAs(planet.Carried), "the chunks live in the planet's frame");

            var prefab = new GameObject("structure-section");
            prefab.AddComponent<NetworkIdentity>();
            prefab.AddComponent<FrameAttachment>();
            var prefabId = _mesh.RegisterPrefab(prefab);
            NetworkIdentity section = null;
            W1.Act(() =>
            {
                section = NetworkPrefabs.Instantiate(prefabId, aCentre, Quaternion.identity, chunkA.ContentRoot);
                W1.Instance.SpawnServerDriven(section, chunkA);
            });
            section.transform.position = aCentre; // frame coordinates in simulation space; the origin is still the centre
            W1.Act(() => Assert.IsTrue(section.GetComponent<FrameAttachment>().Attach()));

            W1.Tick(1);
            W1.Tick(2);
            _mesh.Pump();
            Assert.AreSame(chunkA, section.Container);
            Assert.IsNull(W2.Find(section.NetId), "by its root, 32 m from the seam, it stays w1's alone");

            // 100 m along the planet's x: [-18, 82] m past chunk A's west face, so 18 m into chunk B.
            section.SetExtent(new Bounds(Vector3.zero, new Vector3(100f, 20f, 40f)));
            W1.Tick(3);
            _mesh.Pump();
            Assert.IsNotNull(W2.Find(section.NetId), "measured in the planet's coordinates, where both chunks' boxes are");
            Assert.AreNotEqual(Vector3.zero, planet.Carried.Frame.Origin,
                "the frame's floating origin followed what w1 simulates 7 km out, and moved the boxes and the section together");

            // The planet turns: nothing on it moves in its own coordinates, so the band does not change.
            planet.transform.rotation = Quaternion.Euler(0f, 71f, 0f);
            section.SetExtent(new Bounds(Vector3.zero, new Vector3(40f, 20f, 40f))); // x in [12, 52]: 12 m short of B
            W1.Tick(4);
            _mesh.Pump();
            GhostTargets(W1, section.NetId)[W2.Id] = -1000f;
            W1.Tick(5);
            _mesh.Pump();
            Assert.IsNull(W2.Find(section.NetId), "shrunk short of chunk B, its ghost there goes");
        }
    }
}
