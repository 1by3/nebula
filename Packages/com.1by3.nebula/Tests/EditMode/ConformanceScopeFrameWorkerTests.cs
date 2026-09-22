using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 14 (<c>docs/conformance-suite.md</c>, design <c>docs/scope-frames.md</c>), the worker
    /// half: <b>one</b> worker hosts two scopes whose local coordinates overlap exactly, each shifts its origin on
    /// its own, entities in each keep their precision and neither scope's registry or physics answers for the
    /// other's. Tier B — one real <see cref="NebulaWorker"/> on the <see cref="ConformanceMesh"/> with real
    /// <see cref="Container"/>s and real <see cref="NetworkIdentity"/>s, because a frame shift is about what
    /// happens to live transforms, history buffers and the spatial hash, none of which a pure fixture has. The
    /// scaler half of the scenario is pure C# and lives in <c>ConformanceScopeFrameTests.cs</c> (tier A).
    /// <para>
    /// <b>What this tier cannot do:</b> <c>InstanceScenes.Prepare</c> creates a scope's physics scene only in play
    /// mode (an EditMode test cannot create a runtime scene), so the isolation asserted here is the isolation that
    /// decides it — the containers' isolation ids, the scope-qualified registry queries and
    /// <see cref="PhysicsIslands.SameIsland"/> — and not two live <c>PhysicsScene</c>s. The play-mode half of that
    /// is already covered by <c>Tests/PlayMode/InstancePhysicsTests.cs</c>, which is the same mechanism: one physics
    /// scene per isolation id.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceScopeFrameWorkerTests
    {
        private const string Alpha = "world/alpha";
        private const string Beta = "world/beta";
        private static readonly Vector3 Cell = new Vector3(64f, 64f, 64f);
        /// <summary>Far enough out that the frame position of an unshifted origin would be ~6.4 million metres.</summary>
        private static readonly Vector3Int Far = new Vector3Int(100000, 0, 100000);

        private ConformanceMesh _mesh;
        private RuntimeGrid _alpha, _beta;
        private WorldDefinition _definition;
        private ushort _prefabId;

        [SetUp]
        public void SetUp()
        {
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = Cell;
            NebulaWorld.LoadRuntime(_definition);        // the public frame; the scoped ones do not need a streamer
            _mesh = new ConformanceMesh(1);
            _alpha = new RuntimeGrid(Cell, planar: true, scopeKey: Alpha);
            _beta = new RuntimeGrid(Cell, planar: true, scopeKey: Beta);
            NebulaChunks.Activate(_alpha, NebulaRoles.Worker, true, null);
            NebulaChunks.Activate(_beta, NebulaRoles.Worker, true, null);
            var prefab = new GameObject("rock-prefab");
            prefab.AddComponent<NetworkIdentity>();
            _prefabId = _mesh.RegisterPrefab(prefab);
            StateHistory.WindowTicks = 8; // the mesh has no Initialize(), which is what reads the config knob
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            NebulaChunks.ResetForNewSession();
            // A scoped container that still holds an entity refuses to be unregistered — deliberately, so a removed
            // lease cannot move a scope's occupants into the public world — and the mesh's teardown does not always
            // leave the entity lists empty. Destroy the boxes outright, then let the prune forget them, or a chunk
            // of this fixture would still be in ContainerRegistry.Runtime for the next fixture's assignment tests.
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_definition);
        }

        /// <summary>One chunk of a scoped grid, registered from the lease row the control plane would have written.</summary>
        private Container Chunk(RuntimeGrid grid, Vector3Int coord)
        {
            var container = ContainerRegistry.RegisterRuntime(grid.IdOf(coord), grid.BoundsOf(coord), new InstanceContainerInfo
            {
                InstanceId = grid.InstanceId,
                ScopeKey = grid.ScopeKey,
                PartId = ChunkKeys.PartId(coord),
            });
            ContainerRegistry.ApplyLease(container.ContainerId, _mesh[0].Id, _mesh[0].Index, 1);
            return container;
        }

        // ---------------------------------------------------------------------------- the frames themselves

        [Test]
        public void EachScopeOwnsItsOriginAndOneShiftLeavesTheOtherAlone()
        {
            var here = Chunk(_alpha, Far);
            var there = Chunk(_beta, Far);
            Assert.AreEqual(here.WorldBounds.center, there.WorldBounds.center,
                "both worlds start on exactly the same ground, which is what makes this a test");

            _alpha.ShiftOrigin(Far);

            Assert.AreEqual(Far, _alpha.Frame.Cell, "alpha's origin moved");
            Assert.AreEqual(Vector3Int.zero, _beta.Frame.Cell, "beta's did not");
            Assert.AreEqual(Vector3Int.zero, WorldOrigin.Cell, "and neither did the public world's");
            Assert.AreEqual(1, _alpha.Frame.ShiftCount);
            Assert.AreEqual(0, _beta.Frame.ShiftCount);

            // The chunk alpha is standing on is now at Unity's origin; beta's copy of the same ground is still
            // 6.4 million metres out, because beta has not asked for anything.
            Assert.Less(here.transform.position.magnitude, Cell.x, "alpha's chunk came back to the origin");
            Assert.Greater(there.transform.position.magnitude, 1e6f, "beta's did not move at all");
        }

        [Test]
        public void BothScopesCanSitOnUnitysOriginAtOnceOnOneWorker()
        {
            var here = Chunk(_alpha, Far);
            var there = Chunk(_beta, new Vector3Int(-Far.x, 0, Far.z));

            _alpha.ShiftOrigin(Far);
            _beta.ShiftOrigin(new Vector3Int(-Far.x, 0, Far.z));

            Assert.Less(here.transform.position.magnitude, Cell.x);
            Assert.Less(there.transform.position.magnitude, Cell.x);
            Assert.AreEqual(1, _alpha.Frame.ShiftCount);
            Assert.AreEqual(1, _beta.Frame.ShiftCount);
            // Two worlds whose absolute coordinates are millions of metres apart are both precision-safe, on one
            // machine, at the same time. That is the whole point of the item.
            Assert.AreNotEqual(_alpha.Frame.Cell, _beta.Frame.Cell);
        }

        [Test]
        public void AnEntityKeepsItsPrecisionAndItsPlaceAcrossItsScopesShift()
        {
            var here = Chunk(_alpha, Far);
            Chunk(_beta, Far);
            var rock = _mesh[0].SpawnServerDriven(_prefabId, here, here.transform.position + new Vector3(1.25f, 0f, -2.5f), Quaternion.identity);
            var local = rock.LocalPosition;

            _alpha.ShiftOrigin(Far);

            Assert.AreEqual(local.x, rock.LocalPosition.x, 1e-4f, "the pose inside its container did not change");
            Assert.AreEqual(local.z, rock.LocalPosition.z, 1e-4f);
            // Before the shift the rock was 6.4 million metres out, where a float has ~0.5 m of resolution; after it
            // it is metres from Unity's origin, where it has micrometres. That is the precision the frame buys.
            Assert.Less(rock.transform.position.magnitude, Cell.x * 2f);
        }

        [Test]
        public void TheOtherScopesEntitiesDoNotMove()
        {
            Chunk(_alpha, Far);
            var there = Chunk(_beta, Far);
            var rock = _mesh[0].SpawnServerDriven(_prefabId, there, there.transform.position, Quaternion.identity);
            var before = rock.transform.position;

            _alpha.ShiftOrigin(Far);

            Assert.AreEqual(before, rock.transform.position, "beta's entities are in beta's frame and nothing happened to it");
        }

        [Test]
        public void StateHistoryStaysCorrectAcrossAScopesShift()
        {
            var here = Chunk(_alpha, Far);
            Chunk(_beta, Far);
            var rock = _mesh[0].SpawnServerDriven(_prefabId, here, here.transform.position + new Vector3(4f, 0f, 4f), Quaternion.identity);
            _mesh[0].PublishTick(7);
            Assert.IsTrue(rock.TryGetStateAt(7, out var recorded), "the worker recorded the tick");
            var localBefore = here.ToLocal(recorded.Position);

            _alpha.ShiftOrigin(Far);

            Assert.IsTrue(rock.TryGetStateAt(7, out var after), "and still answers for it after the shift");
            // NEB-222 records a world pose; a shift moves the recorded poses of the frame that moved (StateHistory.Shift),
            // and an entry recorded under a container is left alone because its container moved with the frame. Either
            // way the pose means the same place in the world, which is what StateAt promises.
            Assert.AreEqual(localBefore.x, here.ToLocal(after.Position).x, 1e-3f);
            Assert.AreEqual(localBefore.z, here.ToLocal(after.Position).z, 1e-3f);
        }

        // ---------------------------------------------------------------------------- isolation

        [Test]
        public void TheRegistryAnswersEachScopeSeparatelyEvenWhenTheBoxesCoincide()
        {
            var here = Chunk(_alpha, Far);
            var there = Chunk(_beta, Far);
            var box = here.WorldBounds;
            var found = new List<Container>();

            ContainerRegistry.Overlapping(box, found, _alpha.InstanceId);
            CollectionAssert.AreEquivalent(new[] { here }, found, "a query in alpha never returns beta's box");
            ContainerRegistry.Overlapping(box, found, _beta.InstanceId);
            CollectionAssert.AreEquivalent(new[] { there }, found);

            Assert.AreSame(here, ContainerRegistry.Find(box.center, null, _alpha.InstanceId));
            Assert.AreSame(there, ContainerRegistry.Find(box.center, null, _beta.InstanceId));
            Assert.IsFalse(PhysicsIslands.SameIsland(here, there), "and nothing simulates the two as one island");
            Assert.AreNotEqual(here.InstanceId, there.InstanceId, "which is what gives them separate physics scenes in play mode");
        }

        [Test]
        public void AScopesShiftDoesNotDisturbTheOthersSpatialHash()
        {
            var here = Chunk(_alpha, Far);
            var there = Chunk(_beta, Far);
            var box = there.WorldBounds;

            _alpha.ShiftOrigin(Far);

            var found = new List<Container>();
            ContainerRegistry.Overlapping(box, found, _beta.InstanceId);
            CollectionAssert.AreEquivalent(new[] { there }, found, "beta is still where it was, and still findable there");
            ContainerRegistry.Overlapping(box, found, _alpha.InstanceId);
            CollectionAssert.IsEmpty(found, "alpha has left that ground entirely");
            Assert.AreSame(here, ContainerRegistry.GetRuntime(_alpha.IdOf(Far)), "and is still the same container");
        }

        // ---------------------------------------------------------------------------- the client's single frame

        [Test]
        public void AClientInOneScopeSeesExactlyOneFrameAndItIsItsOwn()
        {
            Chunk(_alpha, Far);
            var there = Chunk(_beta, Far);
            var pawn = _mesh[0].SpawnServerDriven(_prefabId, there, there.transform.position + new Vector3(3f, 0f, 3f), Quaternion.identity);

            // What NebulaChunkedWorld does for a client: one origin, chosen from the grid of the scope its pawn
            // stands in. A client in beta therefore follows beta and never hears of alpha's origin at all.
            var grid = NebulaChunks.GridOf(pawn.Container);
            Assert.AreSame(_beta, grid, "the client's frame is its own scope's");
            var local = pawn.LocalPosition;
            grid.KeepOriginNear(pawn, 1);

            Assert.AreEqual(Far, _beta.Frame.Cell, "the one frame it has followed its pawn");
            Assert.AreEqual(Vector3Int.zero, _alpha.Frame.Cell, "and the scope it is not in did not move");
            Assert.AreEqual(local.x, pawn.LocalPosition.x, 1e-4f, "poses are in beta's frame, exactly as before");
            Assert.Less(pawn.transform.position.magnitude, Cell.x * 2f);
        }
    }
}
