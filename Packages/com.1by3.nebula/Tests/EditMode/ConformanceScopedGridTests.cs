using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The worker-side half of conformance scenario 4 (<c>docs/conformance-suite.md</c>, design
    /// <c>docs/scoped-chunk-grids.md</c>): two chunk grids whose coordinates overlap exactly are two worlds, and
    /// nothing ghosts between them. Tier B — two <b>real</b> <see cref="NebulaWorker"/>s on the
    /// <see cref="ConformanceMesh"/>, each owning a chunk, with the sender's own ghost band deciding what to send
    /// and the receiver's own <c>Dispatch</c> applying it. Tier A cannot say this: a <c>FakeWorker</c> implements
    /// the protocol, not the ghost band. What a <i>client</i> is told is the other half of the scenario, in
    /// <c>Services~/Nebula.Services.Tests/ConformanceScopedGridTests.cs</c>.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceScopedGridTests
    {
        private const string Alpha = "world/alpha";
        private const string Beta = "world/beta";
        private static readonly Vector3 Cell = new Vector3(64f, 64f, 64f);

        private ConformanceMesh _mesh;
        private RuntimeGrid _alpha, _beta;
        private ushort _prefabId;

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            _alpha = new RuntimeGrid(Cell, planar: false, scopeKey: Alpha);
            _beta = new RuntimeGrid(Cell, planar: false, scopeKey: Beta);
            var prefab = new GameObject("rock-prefab");
            prefab.AddComponent<NetworkIdentity>();
            _prefabId = _mesh.RegisterPrefab(prefab);
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            ContainerRegistry.PruneRuntime(new System.Collections.Generic.HashSet<ulong>());
            ContainerRegistry.Rebuild();
        }

        /// <summary>One chunk of a scoped grid, registered from the lease row the control plane would have written.</summary>
        private Container Chunk(RuntimeGrid grid, Vector3Int coord, ConformanceMesh.Worker owner)
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
        public void AnEntityGhostsAcrossItsOwnScopesSeam()
        {
            var here = Chunk(_alpha, Vector3Int.zero, _mesh[0]);
            Chunk(_alpha, new Vector3Int(1, 0, 0), _mesh[1]);

            // Two metres from the seam, inside the four-metre ghost band.
            var rock = _mesh[0].SpawnServerDriven(_prefabId, here, new Vector3(62f, 32f, 32f), Quaternion.identity);
            _mesh[0].PublishTick(1);
            _mesh.Pump();

            Assert.IsNotNull(_mesh[1].Find(rock.NetId), "the neighbouring chunk's owner holds it warm, as in any chunked world");
        }

        [Test]
        public void AnEntityNeverGhostsIntoAnotherScopeStandingOnTheSameGround()
        {
            var here = Chunk(_alpha, Vector3Int.zero, _mesh[0]);
            var there = Chunk(_beta, Vector3Int.zero, _mesh[1]);

            Assert.AreEqual(here.WorldBounds, there.WorldBounds, "the two chunks occupy exactly the same box");
            Assert.AreNotEqual(here.ContainerId, there.ContainerId);
            Assert.IsFalse(here.Neighbors.Contains(there), "adjacency is scope-qualified: overlapping boxes in two scopes are not neighbours");

            var rock = _mesh[0].SpawnServerDriven(_prefabId, here, new Vector3(32f, 32f, 32f), Quaternion.identity);
            _mesh[0].PublishTick(1);
            _mesh.Pump();

            Assert.IsNull(_mesh[1].Find(rock.NetId), "nothing crosses a scope boundary, however close the two worlds are in metres");
            Assert.AreEqual(0, _mesh.DeliveredOf(MsgId.GhostSpawn).Count, "and no ghost was even offered");
        }

        [Test]
        public void ChunksOfTwoScopesAtOneCoordinateAreTwoContainersWithTwoLeaseKeys()
        {
            var here = Chunk(_alpha, new Vector3Int(2, 0, -3), _mesh[0]);
            var there = Chunk(_beta, new Vector3Int(2, 0, -3), _mesh[1]);

            Assert.AreNotEqual(here.RuntimeId, there.RuntimeId);
            Assert.AreEqual(ScopeKeys.Hash(Alpha), here.InstanceId);
            Assert.AreEqual(ScopeKeys.Hash(Beta), there.InstanceId);
            Assert.AreEqual(ScopeKeys.ContainerId(Alpha, "c/2/0/-3"), here.ContainerId, "a chunk's lease key is the scope's own derivation");
            Assert.AreSame(here, ContainerRegistry.GetRuntime(_alpha.IdOf(new Vector3Int(2, 0, -3))));
            Assert.AreSame(there, ContainerRegistry.GetRuntime(_beta.IdOf(new Vector3Int(2, 0, -3))));
        }
    }
}
