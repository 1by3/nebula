using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// A client draws a scoped grid's chunks only while its pawn is in that scope (<see cref="InstanceScenes.SetView"/>): the content of the
    /// chunks of another scope stays resident, hidden, and nothing is rebuilt when the pawn goes back. A chunk loaded while its scope is not the
    /// view arrives hidden, and the public world's chunks are never hidden. (These were part of the old <c>ObserveHost</c> tests; what a client
    /// is sent of the scope on the other side of a door is now a portal's window, <c>Services~/.../ConformancePortalWindowTests.cs</c>.)
    /// </summary>
    public sealed class ScopedChunkViewTests
    {
        private const string HostKey = "world/host";
        private static readonly Vector3 Cell = new Vector3(256f, 256f, 256f);

        private WorldDefinition _definition;
        private RuntimeGrid _host;
        private bool _wasClient;

        [SetUp]
        public void SetUp()
        {
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = Cell;
            NebulaWorld.LoadRuntime(_definition);
            _host = new RuntimeGrid(Cell, planar: false, scopeKey: HostKey);
            _wasClient = NebulaRuntime.IsClient;
        }

        [TearDown]
        public void TearDown()
        {
            NebulaRuntime.IsClient = _wasClient;
            NebulaChunks.ResetForNewSession();
            InstanceScenes.Reset();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_definition);
        }

        /// <summary>A chunk of the host grid, registered from its lease row and leased to the named worker.</summary>
        private Container HostChunk(Vector3Int coord, string owner = "w-1", ushort ownerIndex = 1)
        {
            var c = ContainerRegistry.RegisterRuntime(_host.IdOf(coord), _host.BoundsOf(coord), new InstanceContainerInfo
            {
                InstanceId = _host.InstanceId, ScopeKey = _host.ScopeKey, PartId = ChunkKeys.PartId(coord),
            });
            ContainerRegistry.ApplyLease(c.ContainerId, owner, ownerIndex, 1);
            return c;
        }

        /// <summary>A cell of the public world: a runtime container with no scope.</summary>
        private static Container PublicCell(Vector3Int coord)
        {
            var bounds = new Bounds(Vector3.Scale(coord, Cell) + Cell * 0.5f, Cell);
            var c = ContainerRegistry.RegisterRuntime(RuntimeGrid.PackId(coord), bounds);
            ContainerRegistry.ApplyLease(c.ContainerId, "w-1", 1, 1);
            return c;
        }

        [Test]
        public void AClientHidesAScopedGridsChunksWhileItsPawnIsInAnotherScope()
        {
            NebulaRuntime.IsClient = true;
            NebulaChunks.Activate(_host, NebulaRoles.Client, false, null);
            var chunk = HostChunk(Vector3Int.zero);
            var root = NebulaChunks.RootOf(chunk.RuntimeId);
            Assert.IsNotNull(root);
            var drawn = new GameObject("rock", typeof(MeshRenderer)).GetComponent<MeshRenderer>();
            var off = new GameObject("decal", typeof(MeshRenderer)).GetComponent<MeshRenderer>();
            drawn.transform.SetParent(root, false);
            off.transform.SetParent(root, false);
            off.forceRenderingOff = true;

            InstanceScenes.SetView(_host.InstanceId);
            Assert.IsFalse(drawn.forceRenderingOff, "the pawn's own scope is drawn");

            ulong vault = NebulaWorker.InstanceKey("vault/a");
            InstanceScenes.SetView(vault);
            Assert.IsTrue(drawn.forceRenderingOff, "the host stays resident but is not drawn from inside the instance");
            Assert.IsFalse(NebulaChunks.IsDrawn(new ChunkContext(Vector3Int.zero, chunk.RuntimeId, chunk, root, NebulaRoles.Client, false, _host)));

            var late = new GameObject("late", typeof(MeshRenderer)).GetComponent<MeshRenderer>();
            late.transform.SetParent(root, false);
            InstanceScenes.SetView(NebulaWorker.InstanceKey("vault/b"));
            Assert.IsTrue(late.forceRenderingOff, "content added since is hidden at the next view change");

            InstanceScenes.SetView(_host.InstanceId);
            Assert.IsFalse(drawn.forceRenderingOff, "back in the host it is drawn again");
            Assert.IsFalse(late.forceRenderingOff);
            Assert.IsTrue(off.forceRenderingOff, "and a renderer the game turned off stays off");
        }

        [Test]
        public void AChunkLoadedWhileItsScopeIsNotTheViewArrivesHidden()
        {
            NebulaRuntime.IsClient = true;
            NebulaChunks.Activate(_host, NebulaRoles.Client, false, null);
            InstanceScenes.SetView(NebulaWorker.InstanceKey("vault/a"));
            MeshRenderer rock = null;
            ChunkHandler build = (in ChunkContext chunk) =>
            {
                rock = new GameObject("rock", typeof(MeshRenderer)).GetComponent<MeshRenderer>();
                rock.transform.SetParent(chunk.Root, false);
            };
            NebulaChunks.Loaded += build;
            try
            {
                HostChunk(Vector3Int.zero);
                Assert.IsNotNull(rock);
                Assert.IsTrue(rock.forceRenderingOff);
            }
            finally { NebulaChunks.Loaded -= build; }
        }

        [Test]
        public void ThePublicWorldsChunksAreNeverHidden()
        {
            NebulaRuntime.IsClient = true;
            NebulaChunks.Activate(new RuntimeGrid(Cell, false), NebulaRoles.Client, false, null);
            var cell = PublicCell(Vector3Int.zero);
            var root = NebulaChunks.RootOf(cell.RuntimeId);
            Assert.IsNotNull(root, "the public grid reports its chunk");
            var rock = new GameObject("rock", typeof(MeshRenderer)).GetComponent<MeshRenderer>();
            rock.transform.SetParent(root, false);
            InstanceScenes.SetView(NebulaWorker.InstanceKey("vault/a"));
            Assert.IsFalse(rock.forceRenderingOff, "the public world is drawn from inside an instance, as it always was");
        }
    }
}
